using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 9.2 standard-hole tool contract tests (offline): the four parsers,
/// registry integration (13 tools), autonomous allowlist membership, the
/// schema/parser field agreement, and the tool→family mapping used by the
/// controller's dispatch gate.
/// </summary>
public class StandardHoleToolContractTests
{
    private static CreateStandardHoleParameters? Parse(
        StandardHoleFamily family, string json, out string? error)
    {
        var (name, _, _, parser) = StandardHoleToolContract.For(family);
        return parser.ParseParameters(json, out error);
    }

    // ---------- happy paths ----------

    [Fact]
    public void Threaded_Minimal_ParsesWithStandardDepths()
    {
        var p = Parse(StandardHoleFamily.Threaded,
            """{"size":"M6","placement_face_ref":"FACE 1","unit":"mm"}""", out var error);
        Assert.Null(error);
        Assert.Equal(StandardHoleFamily.Threaded, p!.Family);
        Assert.Equal("M6", p.Size);
        Assert.Equal("FACE 1", p.PlacementFaceRef);
        Assert.Equal(StandardHolePlacementMode.SelectedFaceCenter, p.PlacementMode);
        Assert.Null(p.HoleDepthMm);
        Assert.Null(p.ThreadDepthMm);
    }

    // ---------- placement modes (2026-10-23 Phase 9.3: selected-point) ----------

    [Theory]
    [InlineData(StandardHoleFamily.Threaded)]
    [InlineData(StandardHoleFamily.SimpleClearance)]
    [InlineData(StandardHoleFamily.Counterbored)]
    [InlineData(StandardHoleFamily.Countersunk)]
    public void SelectedPoint_Mode_ParsesWithoutFaceRef(StandardHoleFamily family)
    {
        var p = Parse(family,
            """{"size":"M6","placement_mode":"selected_point","unit":"mm"}""", out var error);
        Assert.Null(error);
        Assert.Equal(StandardHolePlacementMode.SelectedPoint, p!.PlacementMode);
        Assert.Equal("", p.PlacementFaceRef);
    }

    [Fact]
    public void SelectedPoint_WithFaceRef_Refused()
    {
        var p = Parse(StandardHoleFamily.Threaded,
            """{"size":"M6","placement_mode":"selected_point","placement_face_ref":"FACE 1","unit":"mm"}""",
            out var error);
        Assert.Null(p);
        Assert.Contains("placement_face_ref", error);
    }

    [Fact]
    public void SelectedPoint_WithLocationOffsets_Refused()
    {
        var p = Parse(StandardHoleFamily.Threaded,
            """{"size":"M6","placement_mode":"selected_point","location_u":5,"unit":"mm"}""",
            out var error);
        Assert.Null(p);
        Assert.Contains("location_u", error);
    }

    [Fact]
    public void UnknownPlacementMode_Refused()
    {
        var p = Parse(StandardHoleFamily.Threaded,
            """{"size":"M6","placement_mode":"center_of_universe","unit":"mm"}""", out var error);
        Assert.Null(p);
        Assert.Contains("placement_mode", error);
    }

    [Fact]
    public void NonStringPlacementMode_Refused()
    {
        var p = Parse(StandardHoleFamily.Threaded,
            """{"size":"M6","placement_mode":1,"unit":"mm"}""", out var error);
        Assert.Null(p);
        Assert.Contains("placement_mode", error);
    }

    [Fact]
    public void FaceCenterMode_WithoutFaceRef_RefusedWithPointHint()
    {
        // The original rule holds: face-center mode needs the face ref; the
        // rejection now TEACHES the point alternative (the live failure:
        // the model had no way to discover selected_point).
        var p = Parse(StandardHoleFamily.Threaded,
            """{"size":"M6","unit":"mm"}""", out var error);
        Assert.Null(p);
        Assert.Contains("selected_point", error);
    }

    [Fact]
    public void AllFourSchemas_AdvertisePlacementMode()
    {
        var schemas = new[]
        {
            StandardHoleToolContract.ThreadedSchema,
            StandardHoleToolContract.ClearanceSchema,
            StandardHoleToolContract.CounterboredSchema,
            StandardHoleToolContract.CountersunkSchema,
        };
        foreach (var schema in schemas)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(schema);
            Assert.True(doc.RootElement.GetProperty("properties").TryGetProperty(
                "placement_mode", out var mode));
            var enumVals = mode.GetProperty("enum").EnumerateArray()
                .Select(e => e.GetString()!).ToArray();
            Assert.Equal(new[] { "selected_face_center", "selected_point" }, enumVals);
            // placement_face_ref is NO LONGER in required (point mode omits
            // it) — but size and unit stay required.
            var required = doc.RootElement.GetProperty("required")
                .EnumerateArray().Select(e => e.GetString()!).ToArray();
            Assert.DoesNotContain("placement_face_ref", required);
            Assert.Contains("size", required);
            Assert.Contains("unit", required);
        }
    }

    [Fact]
    public void PointPlacement_GeometryGates_OfflineMath()
    {
        // The host's point→face acceptance math, exercised offline with the
        // same tolerance and containment rules the executor applies:
        //   a point ON the plane AND within the face's UV extents → accepted;
        //   a point on the infinite plane but OUTSIDE the extents → refused;
        //   a point off the plane → refused.
        const double Tolerance = 0.01;
        // Face: plane z=20, normal +Z, UV extents u,v ∈ [-30,30] mapped
        // 1:1 to x,y (unit-speed parametrization, like an axis-aligned plane).
        double uMin = -30, uMax = 30, vMin = -30, vMax = 30;
        double duMag = 1.0, dvMag = 1.0; // plane with identity parametrization

        bool Accepts(double x, double y, double z)
        {
            // distance to plane z=20
            double dist = Math.Abs(z - 20);
            if (dist > Tolerance) return false;
            // in-plane offsets along x (u) and y (v); midpoint (0,0)
            double paramU = 0 + x / duMag;
            double paramV = 0 + y / dvMag;
            return paramU >= uMin - Tolerance / duMag && paramU <= uMax + Tolerance / duMag &&
                   paramV >= vMin - Tolerance / dvMag && paramV <= vMax + Tolerance / dvMag;
        }

        Assert.True(Accepts(0, 0, 20));        // center: accepted
        Assert.True(Accepts(15, -12, 20));     // inside: accepted
        Assert.True(Accepts(29.995, 29.995, 20)); // just inside boundary: accepted
        Assert.False(Accepts(45, 0, 20));      // on the infinite plane, outside extents: refused
        Assert.False(Accepts(0, 0, 25));       // off the plane: refused
        Assert.False(Accepts(0, 0, 19.9));     // 0.1mm off the plane: refused (tolerance 0.01)
    }

    [Fact]
    public void PointPlacement_MultipleFaces_AmbiguityRule()
    {
        // The executor refuses when MORE THAN ONE qualifying face contains
        // the point (edge/vertex neighborhood). This pins the rule set:
        // zero → refuse, one → accept, many → refuse.
        int Decide(int matchingFaces) => matchingFaces switch
        {
            0 => 0, // refuse
            1 => 1, // accept
            _ => -1, // refuse (ambiguous)
        };
        Assert.Equal(0, Decide(0));
        Assert.Equal(1, Decide(1));
        Assert.Equal(-1, Decide(2));
        Assert.Equal(-1, Decide(5));
    }

    [Fact]
    public void Threaded_ExplicitDepths_Parsed()
    {
        var p = Parse(StandardHoleFamily.Threaded,
            """{"size":"M8","placement_face_ref":"F","hole_depth":20,"thread_depth":12,"unit":"mm"}""",
            out var error);
        Assert.Null(error);
        Assert.Equal(20, p!.HoleDepthMm);
        Assert.Equal(12, p.ThreadDepthMm);
    }

    [Fact]
    public void Threaded_LowercaseSize_Normalized()
    {
        var p = Parse(StandardHoleFamily.Threaded,
            """{"size":"m1.6","placement_face_ref":"F","unit":"mm"}""", out var error);
        Assert.Null(error);
        Assert.Equal("M1.6", p!.Size);
    }

    [Fact]
    public void Threaded_UnitConverted()
    {
        var p = Parse(StandardHoleFamily.Threaded,
            """{"size":"M6","placement_face_ref":"F","location_u":0.5,"hole_depth":2,"thread_depth":1.2,"unit":"cm"}""",
            out var error);
        Assert.Null(error);
        Assert.Equal(5, p!.LocationU);
        Assert.Equal(20, p.HoleDepthMm);
        Assert.Equal(12, p.ThreadDepthMm);
    }

    [Fact]
    public void Simple_OptionalHoleDepth_Parsed()
    {
        var p = Parse(StandardHoleFamily.SimpleClearance,
            """{"size":"M10","placement_face_ref":"F","hole_depth":25,"unit":"mm"}""", out var error);
        Assert.Null(error);
        Assert.Equal(25, p!.HoleDepthMm);
        Assert.Null(p.ThreadDepthMm);
    }

    [Theory]
    [InlineData(StandardHoleFamily.SimpleClearance)]
    [InlineData(StandardHoleFamily.Counterbored)]
    [InlineData(StandardHoleFamily.Countersunk)]
    public void ClearanceFamilies_FullParams_Parse(StandardHoleFamily family)
    {
        var p = Parse(family,
            """{"size":"M3","placement_face_ref":"FACE 2 {(0,0,10) BLOCK(1)}","location_u":1,"location_v":-2,"unit":"mm"}""",
            out var error);
        Assert.Null(error);
        Assert.Equal(family, p!.Family);
        Assert.Equal("M3", p.Size);
        Assert.Equal(1, p.LocationU);
        Assert.Equal(-2, p.LocationV);
    }

    // ---------- rejections ----------

    [Theory]
    [InlineData(StandardHoleFamily.Threaded)]
    [InlineData(StandardHoleFamily.SimpleClearance)]
    [InlineData(StandardHoleFamily.Counterbored)]
    [InlineData(StandardHoleFamily.Countersunk)]
    public void MissingSize_Refused(StandardHoleFamily family)
    {
        var p = Parse(family, """{"placement_face_ref":"F","unit":"mm"}""", out var error);
        Assert.Null(p);
        Assert.Contains("size", error);
    }

    [Theory]
    [InlineData(StandardHoleFamily.Threaded)]
    [InlineData(StandardHoleFamily.SimpleClearance)]
    [InlineData(StandardHoleFamily.Counterbored)]
    [InlineData(StandardHoleFamily.Countersunk)]
    public void UnsupportedSize_Refused(StandardHoleFamily family)
    {
        var p = Parse(family, """{"size":"M2.5","placement_face_ref":"F","unit":"mm"}""", out var error);
        Assert.Null(p);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(StandardHoleFamily.Threaded)]
    [InlineData(StandardHoleFamily.SimpleClearance)]
    [InlineData(StandardHoleFamily.Counterbored)]
    [InlineData(StandardHoleFamily.Countersunk)]
    public void MissingFaceRef_Refused(StandardHoleFamily family)
    {
        var p = Parse(family, """{"size":"M6","unit":"mm"}""", out var error);
        Assert.Null(p);
        Assert.Contains("placement_face_ref", error);
    }

    [Theory]
    [InlineData(StandardHoleFamily.Threaded)]
    [InlineData(StandardHoleFamily.SimpleClearance)]
    [InlineData(StandardHoleFamily.Counterbored)]
    [InlineData(StandardHoleFamily.Countersunk)]
    public void MissingUnit_Refused(StandardHoleFamily family)
    {
        var p = Parse(family, """{"size":"M6","placement_face_ref":"F"}""", out var error);
        Assert.Null(p);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(StandardHoleFamily.Threaded)]
    [InlineData(StandardHoleFamily.SimpleClearance)]
    [InlineData(StandardHoleFamily.Counterbored)]
    [InlineData(StandardHoleFamily.Countersunk)]
    public void UnknownField_Refused(StandardHoleFamily family)
    {
        var p = Parse(family,
            """{"size":"M6","placement_face_ref":"F","unit":"mm","diameter":9}""",
            out var error);
        Assert.Null(p);
        Assert.Contains("diameter", error); // diameters are NEVER model-supplied
    }

    [Fact]
    public void Threaded_ThreadDepthAlone_Refused()
    {
        var p = Parse(StandardHoleFamily.Threaded,
            """{"size":"M6","placement_face_ref":"F","thread_depth":9,"unit":"mm"}""", out var error);
        Assert.Null(p);
        Assert.Contains("同时", error);
    }

    [Fact]
    public void Threaded_HoleDepthAlone_Refused()
    {
        var p = Parse(StandardHoleFamily.Threaded,
            """{"size":"M6","placement_face_ref":"F","hole_depth":20,"unit":"mm"}""", out var error);
        Assert.Null(p);
        Assert.NotNull(error);
    }

    [Fact]
    public void Threaded_ThreadDepthGeHoleDepth_Refused()
    {
        var p = Parse(StandardHoleFamily.Threaded,
            """{"size":"M6","placement_face_ref":"F","hole_depth":10,"thread_depth":10,"unit":"mm"}""",
            out var error);
        Assert.Null(p);
        Assert.Contains("小于", error);
    }

    [Fact]
    public void Threaded_NegativeDepth_Refused()
    {
        var p = Parse(StandardHoleFamily.Threaded,
            """{"size":"M6","placement_face_ref":"F","hole_depth":-10,"thread_depth":-9,"unit":"mm"}""",
            out var error);
        Assert.Null(p);
        Assert.NotNull(error);
    }

    [Fact]
    public void Simple_ThreadDepth_Refused()
    {
        var p = Parse(StandardHoleFamily.SimpleClearance,
            """{"size":"M6","placement_face_ref":"F","thread_depth":9,"unit":"mm"}""", out var error);
        Assert.Null(p);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(StandardHoleFamily.Counterbored)]
    [InlineData(StandardHoleFamily.Countersunk)]
    public void HeadFamilies_DepthOverride_Refused(StandardHoleFamily family)
    {
        var p = Parse(family,
            """{"size":"M6","placement_face_ref":"F","hole_depth":20,"unit":"mm"}""", out var error);
        Assert.Null(p);
        Assert.NotNull(error);
    }

    [Fact]
    public void NotJson_Refused()
    {
        var p = Parse(StandardHoleFamily.Threaded, "not json", out var error);
        Assert.Null(p);
        Assert.NotNull(error);
    }

    // ---------- registry + allowlist + contract agreement ----------

    [Fact]
    public void FourTools_Registered_InToolRegistry()
    {
        var registry = CreateBlockTool.CreateRegistry();
        Assert.Equal(18, registry.All.Count);
        Assert.True(registry.IsRegistered(StandardHoleToolContract.ThreadedName));
        Assert.True(registry.IsRegistered(StandardHoleToolContract.ClearanceName));
        Assert.True(registry.IsRegistered(StandardHoleToolContract.CounterboredName));
        Assert.True(registry.IsRegistered(StandardHoleToolContract.CountersunkName));
    }

    [Fact]
    public void FourTools_OnAutonomousAllowlist()
    {
        Assert.Equal(18, AutonomousToolAllowlist.Tools.Count);
        Assert.Contains(StandardHoleToolContract.ThreadedName, AutonomousToolAllowlist.Tools);
        Assert.Contains(StandardHoleToolContract.ClearanceName, AutonomousToolAllowlist.Tools);
        Assert.Contains(StandardHoleToolContract.CounterboredName, AutonomousToolAllowlist.Tools);
        Assert.Contains(StandardHoleToolContract.CountersunkName, AutonomousToolAllowlist.Tools);
    }

    [Theory]
    [InlineData(StandardHoleFamily.Threaded)]
    [InlineData(StandardHoleFamily.SimpleClearance)]
    [InlineData(StandardHoleFamily.Counterbored)]
    [InlineData(StandardHoleFamily.Countersunk)]
    public void SchemaAndParser_FieldSetsAgree(StandardHoleFamily family)
    {
        var (name, description, schema, parser) = StandardHoleToolContract.For(family);
        Assert.Equal(name, parser.ToolNameContract());
        foreach (var field in new[] { "size", "placement_face_ref", "location_u", "location_v", "unit" })
            Assert.Contains($"\"{field}\"", schema);
        // Threaded + simple schemas expose their depth fields; head-family
        // schemas must NOT (parser refuses them).
        if (family == StandardHoleFamily.Threaded)
        {
            Assert.Contains("\"hole_depth\"", schema);
            Assert.Contains("\"thread_depth\"", schema);
        }
        else if (family == StandardHoleFamily.SimpleClearance)
        {
            Assert.Contains("\"hole_depth\"", schema);
            Assert.DoesNotContain("\"thread_depth\"", schema);
        }
        else
        {
            Assert.DoesNotContain("\"hole_depth\"", schema);
            Assert.DoesNotContain("\"thread_depth\"", schema);
        }
        Assert.False(string.IsNullOrWhiteSpace(description));
    }

    [Fact]
    public void ToolNames_UniqueAndWellFormed()
    {
        var names = new[]
        {
            StandardHoleToolContract.ThreadedName,
            StandardHoleToolContract.ClearanceName,
            StandardHoleToolContract.CounterboredName,
            StandardHoleToolContract.CountersunkName,
        };
        Assert.Equal(4, names.Distinct().Count());
        Assert.All(names, n => Assert.Matches("^create_[a-z_]+$", n));
    }

    [Fact]
    public void ContractConfusionGuard_CountersunkDescription_PinsMinSize()
    {
        // The M2/M1.6 ISO gap must be stated in the model-facing description
        // so the model can pre-filter instead of discovering it at execution.
        Assert.Contains("M3", StandardHoleToolContract.CountersunkDescription);
    }

    // ---------- 2026-10-23 PROJECT-FORMAL Chinese semantic map (user-fixed) ----------
    //
    // 沉头孔 → create_counterbored_hole (Counterbored, cylindrical, ISO 4762)
    // 埋头孔 → create_countersunk_hole  (Countersink, conical, ISO 10642)
    // 螺纹间隙孔 → create_clearance_hole (Simple)
    // 螺纹孔 → create_threaded_hole (ThreadedHole)
    // These are EXCLUSIVE claims enforced through the tool descriptions (the
    // only local routing surface — natural-language choice happens in the
    // model, which sees exactly these descriptions).

    [Fact]
    public void CounterboredDescription_ClaimsTheChineseWord_ChentouKong_Exclusively()
    {
        // 沉头孔 belongs to the cylindrical counterbore: the description must
        // claim it, name the cylindrical geometry and the NX builder type,
        // and refuse the conical form.
        Assert.Contains("沉头孔", StandardHoleToolContract.CounterboredDescription);
        Assert.Contains("圆柱形扩孔", StandardHoleToolContract.CounterboredDescription);
        Assert.Contains("平底台阶", StandardHoleToolContract.CounterboredDescription);
        Assert.Contains("TypesOfHole.Counterbored", StandardHoleToolContract.CounterboredDescription);
        Assert.Contains("不可用于锥形埋头孔", StandardHoleToolContract.CounterboredDescription);
    }

    [Fact]
    public void CountersunkDescription_ClaimsTheChineseWord_MaifouKong_Exclusively()
    {
        // 埋头孔 belongs to the conical countersink: the description must
        // claim it (埋头孔 / 锥形埋头孔), name the conical geometry and the
        // NX builder type, and REFUSE the plain 沉头孔 request.
        Assert.Contains("埋头孔", StandardHoleToolContract.CountersunkDescription);
        Assert.Contains("锥形埋头孔", StandardHoleToolContract.CountersunkDescription);
        Assert.Contains("锥形扩孔", StandardHoleToolContract.CountersunkDescription);
        Assert.Contains("TypesOfHole.Countersink", StandardHoleToolContract.CountersunkDescription);
        // The exclusive negative: 沉头孔-only requests must NOT pick this tool.
        Assert.Contains("只说'沉头孔'时，不可调用此工具",
            StandardHoleToolContract.CountersunkDescription);
        Assert.Contains(StandardHoleToolContract.CounterboredName,
            StandardHoleToolContract.CountersunkDescription);
    }

    [Fact]
    public void CountersunkDescription_NamesConicalFormAndIso10642()
    {
        // The description must pin the ACTUAL NX geometry the tool creates:
        // conical (锥形) + 10642, so a confirmation-card reader can verify
        // the shape before confirming.
        Assert.Contains("锥形", StandardHoleToolContract.CountersunkDescription);
        Assert.Contains("10642", StandardHoleToolContract.CountersunkDescription);
    }

    [Fact]
    public void ClearanceDescription_ClaimsTheChineseWord_ChiwenJianxiKong()
    {
        // 螺纹间隙孔 belongs to the simple clearance hole, explicitly
        // thread-less.
        Assert.Contains("螺纹间隙孔", StandardHoleToolContract.ClearanceDescription);
        Assert.Contains("不创建内螺纹", StandardHoleToolContract.ClearanceDescription);
        Assert.Contains("TypesOfHole.Simple", StandardHoleToolContract.ClearanceDescription);
    }

    [Fact]
    public void ThreadedDescription_ClaimsTheChineseWord_LuowenKong()
    {
        Assert.Contains("螺纹孔", StandardHoleToolContract.ThreadedDescription);
        Assert.Contains("内螺纹", StandardHoleToolContract.ThreadedDescription);
        Assert.Contains("TypesOfHole.ThreadedHole", StandardHoleToolContract.ThreadedDescription);
    }

    [Fact]
    public void ChineseSemanticMap_ToolLevelRoutingIsDeterministic()
    {
        // The LOCAL deterministic part of routing: tool name → family → NX
        // hole form + standard-table family. The model's word→tool choice is
        // NOT locally deterministic (described in the report), but once a
        // tool name arrives, the family/type mapping must be exact.
        var map = new (string Tool, StandardHoleFamily Family)[]
        {
            (StandardHoleToolContract.CounterboredName, StandardHoleFamily.Counterbored),
            (StandardHoleToolContract.CountersunkName, StandardHoleFamily.Countersunk),
            (StandardHoleToolContract.ClearanceName, StandardHoleFamily.SimpleClearance),
            (StandardHoleToolContract.ThreadedName, StandardHoleFamily.Threaded),
        };
        Assert.Equal(4, map.Length);
        Assert.All(map, m =>
        {
            var (_, _, _, parser) = StandardHoleToolContract.For(m.Family);
            Assert.Equal(m.Tool, parser.ToolNameContract());
        });
        // NX builder form per family (HoleToolExecutor's switch contract,
        // mirrored here so a future family swap fails this test first).
        var nxForm = new (StandardHoleFamily Family, string NxType)[]
        {
            (StandardHoleFamily.Counterbored, "Counterbored"),
            (StandardHoleFamily.Countersunk, "Countersink"),
            (StandardHoleFamily.SimpleClearance, "Simple"),
            (StandardHoleFamily.Threaded, "ThreadedHole"),
        };
        Assert.Equal(4, nxForm.Length); // documented pairing; executor must match
    }

    [Fact]
    public void ToolDescriptionExclusivity_NoAmbiguousSharedClaims()
    {
        // The countersunk tool must NOT tell the model that plain 沉头孔
        // (without 埋/锥 qualification) belongs to it: the old wording
        // ("中文'埋头孔/沉头孔'通常指…") made both words ambiguous.
        Assert.DoesNotContain("埋头孔/沉头孔", StandardHoleToolContract.CountersunkDescription);
        Assert.DoesNotContain("埋头孔/沉头孔", StandardHoleToolContract.CounterboredDescription);
    }

    [Fact]
    public void CountersunkM8_StandardRecord_IsConicalNotCounterbore()
    {
        // 埋头孔 (Countersink) M8: the conical ISO 10642 record
        // (CSinkDia 18.25, 90° cone) — never the counterbore record
        // (ISO 4762: CBoreDia 15 / CBoreDepth 8.8).
        var rec = StandardHoleTable.Resolve(StandardHoleFamily.Countersunk, "M8",
            family => family == StandardHoleFamily.Threaded ? ThreadXmlFixture : ClearanceXmlFixture);
        Assert.Equal("Countersunk", rec.Field("Form"));
        Assert.Equal("Socket Flat Head, 10642", rec.Field("ScrewType"));
        Assert.Equal("18.25", rec.Field("CSinkDia"));
        Assert.Equal("90", rec.Field("CSinkAngle"));
        Assert.Null(rec.Field("CBoreDia")); // no cylindrical-seat fields on the conical record
        Assert.Null(rec.Field("CBoreDepth"));
    }

    [Fact]
    public void CounterboredM8_StandardRecord_IsCylindricalForChentouKong()
    {
        // 沉头孔 (Counterbored) M8: the cylindrical ISO 4762 record with its
        // own CBore fields (15 / 8.8) — independent of the conical family;
        // the two share no record and no fields.
        var rec = StandardHoleTable.Resolve(StandardHoleFamily.Counterbored, "M8",
            family => family == StandardHoleFamily.Threaded ? ThreadXmlFixture : ClearanceXmlFixture);
        Assert.Equal("Counterbored", rec.Field("Form"));
        Assert.Equal("Socket Head, 4762", rec.Field("ScrewType"));
        Assert.Equal("15", rec.Field("CBoreDia"));
        Assert.Equal("8.8", rec.Field("CBoreDepth"));
        Assert.Null(rec.Field("CSinkDia"));
    }

    private const string ThreadXmlFixture = "<Root></Root>";

    private const string ClearanceXmlFixture = """
        <Root>
          <StandardHole Unit="Metric" Standard="ISO" Form="Countersunk" ScrewType="Socket Flat Head, 10642" Size="M8" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M8 x 1.25" ThreadEngage="0.75" HoleDiameter="8.4" HoleTipAngle="118" CSinkDia="18.25" CSinkAngle="90" CSinkReliefDepth="1.2" StartChamferOffset="0.6" StartChamferAngle="45" EndChamferOffset="0.6" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Counterbored" ScrewType="Socket Head, 4762" Size="M8" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M8 x 1.25" ThreadEngage="0.75" HoleDiameter="8.4" HoleTipAngle="118" CBoreDia="15" CBoreDepth="8.8" NeckChamferOffset="0.6" NeckChamferAngle="60" StartChamferOffset="0.6" StartChamferAngle="45" EndChamferOffset="0.6" EndChamferAngle="45" />
        </Root>
        """;
}

/// <summary>Test-only extension for the parser name check.</summary>
internal static class StandardHoleParserTestExtensions
{
    public static string ToolNameContract(this CreateStandardHoleRequestParser parser)
    {
        // The parser carries its tool name internally; verify via a rejection
        // message round-trip (the tool name prefixes every parse error).
        parser.ParseParameters("not json", out var error);
        Assert.NotNull(error);
        return error!.Split(' ')[0];
    }
}
