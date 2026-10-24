using NXAI.Assistant.Core;
using NXAI.Assistant.UI;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 10 create_mirror offline tests: parsing/validation of the two
/// modes, strict unknown-field rejection, registration/allowlist/schema
/// (NO object-reference fields — the R2 lesson), the postcondition verdict
/// sub-conditions, the confirmation-description coverage (the T1 lesson:
/// every new kind needs a DescribePendingOperation case), and the tool
/// count bump. The NX builder wiring (MirrorBodyBuilder/MirrorFeatureBuilder
/// with the role-separated plane) is a separate live acceptance item.
/// </summary>
public class MirrorToolTests
{
    private static CreateMirrorParameters? Parse(string json, out string? error) =>
        new CreateMirrorRequestParser().ParseParameters(json, out error);

    private static ToolConfirmationRequest MirrorRequest(string json)
    {
        var p = Parse(json, out var error);
        Assert.Null(error);
        return ToolConfirmationRequest.ForMirror(
            "call_m", MirrorToolContract.Name, p!);
    }

    // ---------- happy paths ----------

    [Fact]
    public void GeometryMode_Parses()
    {
        var p = Parse("""{"mode":"geometry"}""", out var error);
        Assert.Null(error);
        Assert.Equal(MirrorMode.Geometry, p!.Mode);
        Assert.True(p.KeepOriginal);
    }

    [Fact]
    public void FeatureMode_KeepOriginalFalse_Parses()
    {
        var p = Parse("""{"mode":"feature","keep_original":false}""", out var error);
        Assert.Null(error);
        Assert.Equal(MirrorMode.Feature, p!.Mode);
        Assert.False(p.KeepOriginal);
    }

    // ---------- rejections ----------

    [Theory]
    [InlineData("""{}""", "mode")]
    [InlineData("""{"mode":"body"}""", "不支持的 mode")]
    [InlineData("""{"mode":"feature","keep_original":"yes"}""", "keep_original")]
    [InlineData("""{"mode":"geometry","target_body_ref":"BLOCK(5)"}""", "不支持参数字段")]
    [InlineData("""{"mode":"geometry","plane_ref":"DATUM(2)"}""", "不支持参数字段")]
    [InlineData("""{"mode":"feature","seeds":["a"]}""", "不支持参数字段")]
    public void Rejections(string json, string expectedFragment)
    {
        var p = Parse(json, out var error);
        Assert.Null(p);
        Assert.NotNull(error);
        Assert.Contains(expectedFragment, error!);
    }

    // ---------- contract surface ----------

    [Fact]
    public void Registry_IncludesMirror()
    {
        var registry = CreateBlockTool.CreateRegistry();
        Assert.True(registry.IsRegistered(MirrorToolContract.Name));
    }

    [Fact]
    public void Allowlist_IncludesMirror()
    {
        Assert.True(AutonomousToolAllowlist.Contains(MirrorToolContract.Name));
    }

    [Fact]
    public void Schema_HasNoObjectReferenceFields()
    {
        var schema = MirrorToolContract.ParametersJsonSchema;
        Assert.DoesNotContain("target", schema);
        Assert.DoesNotContain("plane_ref", schema);
        Assert.DoesNotContain("seed", schema);
        using var doc = System.Text.Json.JsonDocument.Parse(schema);
        Assert.True(doc.RootElement.TryGetProperty("required", out _));
    }

    // ---------- confirmation descriptions never NRE (the T1 lesson) ----------

    [Fact]
    public void DescribePendingOperation_MirrorRequests_DoNotThrow()
    {
        foreach (var mode in new[] { "geometry", "feature" })
        {
            var request = MirrorRequest($$"""{"mode":"{{mode}}"}""");
            var text = ChatSessionController.DescribePendingOperation(request);
            Assert.Contains("镜像", text);
            Assert.DoesNotContain("布尔", text); // never the boolean wildcard
        }
    }

    // ---------- postcondition verdict ----------

    [Fact]
    public void Verdict_GeometryMode_BodiesGrowBySeedCount()
    {
        var (pass, reason) = NxHostVerdicts.MirrorPostcondition(
            committed: true, alive: true, typeMatches: true,
            featureBodyCount: 2, seedCount: 2,
            bodiesBefore: 1, bodiesAfter: 3, expectedNewBodies: 2,
            isGeometryMode: true);
        Assert.True(pass, reason);
    }

    [Fact]
    public void Verdict_GeometryMode_WrongGrowth_Fails()
    {
        var (pass, reason) = NxHostVerdicts.MirrorPostcondition(
            committed: true, alive: true, typeMatches: true,
            featureBodyCount: 2, seedCount: 2,
            bodiesBefore: 1, bodiesAfter: 2, expectedNewBodies: 2,
            isGeometryMode: true);
        Assert.False(pass);
        Assert.Contains("实体数量变化", reason);
    }

    [Fact]
    public void Verdict_GeometryMode_NoOwnedBodies_Fails()
    {
        var (pass, reason) = NxHostVerdicts.MirrorPostcondition(
            committed: true, alive: true, typeMatches: true,
            featureBodyCount: 0, seedCount: 2,
            bodiesBefore: 1, bodiesAfter: 1, expectedNewBodies: 2,
            isGeometryMode: true);
        Assert.False(pass);
        Assert.Contains("不拥有任何实体", reason);
    }

    [Fact]
    public void Verdict_FeatureMode_DoesNotAssertBodyGrowth()
    {
        // A mirrored hole feature subtracts inside an existing body —
        // body count may legitimately stay the same.
        var (pass, reason) = NxHostVerdicts.MirrorPostcondition(
            committed: true, alive: true, typeMatches: true,
            featureBodyCount: -1, seedCount: 1,
            bodiesBefore: 1, bodiesAfter: 1, expectedNewBodies: 0,
            isGeometryMode: false);
        Assert.True(pass, reason);
    }

    [Fact]
    public void Verdict_TypeMismatch_Fails()
    {
        var (pass, reason) = NxHostVerdicts.MirrorPostcondition(
            committed: true, alive: true, typeMatches: false,
            featureBodyCount: 2, seedCount: 2,
            bodiesBefore: 1, bodiesAfter: 3, expectedNewBodies: 2,
            isGeometryMode: true);
        Assert.False(pass);
        Assert.Contains("不匹配", reason);
    }

    [Fact]
    public void Verdict_DeadFeature_Fails()
    {
        var (pass, _) = NxHostVerdicts.MirrorPostcondition(
            committed: true, alive: false, typeMatches: true,
            featureBodyCount: 2, seedCount: 2,
            bodiesBefore: 1, bodiesAfter: 3, expectedNewBodies: 2,
            isGeometryMode: true);
        Assert.False(pass);
    }

    [Fact]
    public void Verdict_NotCommitted_Fails()
    {
        var (pass, reason) = NxHostVerdicts.MirrorPostcondition(
            committed: false, alive: false, typeMatches: false,
            featureBodyCount: -1, seedCount: 1,
            bodiesBefore: 1, bodiesAfter: 1, expectedNewBodies: 1,
            isGeometryMode: true);
        Assert.False(pass);
        Assert.Contains("CommitFeature", reason);
    }
}

/// <summary>
/// Phase 10.4 P3: the mirror plane wiring uses the REFLECTION-VERIFIED
/// single-selection holders (SelectDatumPlane.Value / SelectISurface.Value)
/// — the live failure was a borrowed list-Add that these types do not have.
/// Pinned on the implementation source (the real wiring needs NX).
/// </summary>
public class MirrorPlaneWiringTests
{
    private static string ExecutorSource =>
        File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "NXAI.Assistant.NxHost.NX2512", "MirrorToolExecutor.cs"));

    [Fact]
    public void WireMirrorPlane_UsesTypedValueAssignment_NoDynamicAdd()
    {
        Assert.Contains("SelectDatumPlane builderPlane", ExecutorSource);
        Assert.Contains("SelectISurface builderPlane", ExecutorSource);
        Assert.Contains("builderPlane.Value = dp;", ExecutorSource);
        Assert.Contains("builderPlane.Value = face;", ExecutorSource);
        // The failed dynamic-Add path must be gone.
        Assert.DoesNotContain("builderPlaneSelect.Add(", ExecutorSource);
        Assert.DoesNotContain("dynamic builderPlaneSelect", ExecutorSource);
    }

    [Fact]
    public void WireMirrorPlane_GeometryOverload_AcceptsPlanarFaceViaTempDatum()
    {
        // Phase 10.4 follow-up: the geometry-mode mirror accepts a selected
        // PLANAR FACE (the tool contract's second mirror-plane kind) by
        // converting its real plane definition into a fixed datum plane
        // (CreateFixedDatumPlane with the face's normal as the Z axis),
        // assigning it to the SelectDatumPlane.Value.
        Assert.Contains("geometry 模式镜像面必须恰好一个（基准面或平面面）", ExecutorSource);
        Assert.Contains("CreateFixedDatumPlane(origin, matrix)", ExecutorSource);
    }

    [Fact]
    public void TempDatum_IsKeptAsMirrorDefinition_NeverDeleted()
    {
        // Phase 10.4 closure v2 (live evidence, phase.log 3205-3208): the
        // committed MirrorBody DEPENDS on the temp datum — DeleteFeature
        // removed the datum AND the mirror body with it (bodies 39→40→39,
        // both before and after DoUpdate). The datum must be KEPT as the
        // mirror's supporting definition and MANAGED instead: layer 250,
        // blanked. The DeleteFeature path must be gone.
        Assert.DoesNotContain("DeleteFeature(", ExecutorSource);
        Assert.Contains("tempDatumToDestroy.Layer = 250;", ExecutorSource);
        Assert.Contains("tempDatumToDestroy.Blank();", ExecutorSource);
        Assert.Contains("kept as mirror definition", ExecutorSource);
    }

    [Fact]
    public void WireMirrorPlane_ReadsFaceDefinitionViaVerifiedPaths()
    {
        // The conversion reads the face's REAL geometry — origin + unit
        // normal through the verified UF pair; UFEvalsf is forbidden.
        Assert.Contains("ReadPlanarFaceDefinition", ExecutorSource);
        Assert.Contains("AskFaceUvMinmax", ExecutorSource);
        Assert.Contains("AskFaceProps", ExecutorSource);
        Assert.DoesNotContain("GetEvalsf", ExecutorSource);
    }
}
