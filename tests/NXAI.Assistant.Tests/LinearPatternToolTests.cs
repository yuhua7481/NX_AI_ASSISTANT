using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 9.6 create_linear_pattern tests (offline): parsing/validation of
/// the two explicit modes, registry/allowlist/schema, the postcondition
/// verdict sub-conditions, and controller routing. The NX builder wiring
/// (PatternFeatureBuilder / PatternGeometryBuilder) is a separate live
/// acceptance item.
/// </summary>
public class LinearPatternToolTests
{
    private static CreateLinearPatternParameters? Parse(string json, out string? error) =>
        new CreateLinearPatternRequestParser().ParseParameters(json, out error);

    // ---------- happy paths ----------

    [Fact]
    public void FeatureMode_Parses()
    {
        var p = Parse(
            """{"mode":"feature","direction":"plus_x","count":5,"pitch":10,"unit":"mm"}""",
            out var error);
        Assert.Null(error);
        Assert.Equal(LinearPatternMode.Feature, p!.Mode);
        Assert.Equal(LinearPatternDirection.PlusX, p.Direction);
        Assert.Equal(5, p.Count);
        Assert.Equal(10, p.PitchMm);
        Assert.True(p.IncludeSeed);
    }

    [Fact]
    public void GeometryMode_Parses()
    {
        var p = Parse(
            """{"mode":"geometry","direction":"minus_z","count":3,"pitch":0.5,"unit":"cm"}""",
            out var error);
        Assert.Null(error);
        Assert.Equal(LinearPatternMode.Geometry, p!.Mode);
        Assert.Equal(LinearPatternDirection.MinusZ, p.Direction);
        Assert.Equal(5, p.PitchMm); // 0.5 cm
    }

    [Fact]
    public void IncludeSeed_False_Parses()
    {
        var p = Parse(
            """{"mode":"geometry","direction":"plus_y","count":4,"pitch":8,"include_seed":false,"unit":"mm"}""",
            out var error);
        Assert.Null(error);
        Assert.False(p!.IncludeSeed);
    }

    // ---------- rejections ----------

    [Theory]
    [InlineData("""{"direction":"plus_x","count":5,"pitch":10,"unit":"mm"}""", "mode")]
    [InlineData("""{"mode":"solid","direction":"plus_x","count":5,"pitch":10,"unit":"mm"}""", "mode")]
    [InlineData("""{"mode":"feature","count":5,"pitch":10,"unit":"mm"}""", "direction")]
    [InlineData("""{"mode":"feature","direction":"north","count":5,"pitch":10,"unit":"mm"}""", "direction")]
    [InlineData("""{"mode":"feature","direction":"plus_x","pitch":10,"unit":"mm"}""", "count")]
    [InlineData("""{"mode":"feature","direction":"plus_x","count":1,"pitch":10,"unit":"mm"}""", "count")]
    [InlineData("""{"mode":"feature","direction":"plus_x","count":5.5,"pitch":10,"unit":"mm"}""", "count")]
    [InlineData("""{"mode":"feature","direction":"plus_x","count":101,"pitch":10,"unit":"mm"}""", "count")]
    [InlineData("""{"mode":"feature","direction":"plus_x","count":5,"unit":"mm"}""", "pitch")]
    [InlineData("""{"mode":"feature","direction":"plus_x","count":5,"pitch":0,"unit":"mm"}""", "pitch")]
    [InlineData("""{"mode":"feature","direction":"plus_x","count":5,"pitch":-3,"unit":"mm"}""", "pitch")]
    [InlineData("""{"mode":"feature","direction":"plus_x","count":5,"pitch":1001,"unit":"mm"}""", "过大")]
    [InlineData("""{"mode":"feature","direction":"plus_x","count":5,"pitch":10,"unit":"mm","target_ref":"X"}""", "不支持参数字段")]
    [InlineData("not json", "JSON")]
    public void InvalidInputs_Refused(string json, string expectedFragment)
    {
        var p = Parse(json, out var error);
        Assert.Null(p);
        Assert.NotNull(error);
        Assert.Contains(expectedFragment, error);
    }

    // ---------- registry + allowlist + schema ----------

    [Fact]
    public void Tool_Registered_AndOnAllowlist()
    {
        var registry = CreateBlockTool.CreateRegistry();
        Assert.Equal(18, registry.All.Count);
        Assert.True(registry.IsRegistered(LinearPatternToolContract.Name));
        Assert.Contains(LinearPatternToolContract.Name, AutonomousToolAllowlist.Tools);
    }

    [Fact]
    public void Schema_ParsesAndRequiresExplicitModeAndDirection()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(LinearPatternToolContract.ParametersJsonSchema);
        var required = doc.RootElement.GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Contains("mode", required);
        Assert.Contains("direction", required);
        Assert.Contains("count", required);
        Assert.Contains("pitch", required);
        var modeEnum = doc.RootElement.GetProperty("properties").GetProperty("mode")
            .GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(new[] { "feature", "geometry" }, modeEnum);
    }

    [Fact]
    public void Description_ExplainsBothModes_AndAutoTarget()
    {
        // The two modes must be distinguishable in the model-facing text.
        Assert.Contains("feature", LinearPatternToolContract.Description);
        Assert.Contains("geometry", LinearPatternToolContract.Description);
        Assert.Contains("自动解析", LinearPatternToolContract.Description);
        // Direction is never guessed.
        Assert.Contains("先问用户", LinearPatternToolContract.Description);
    }

    // ---------- postcondition verdict sub-conditions ----------

    [Fact]
    public void Verdict_FeatureMode_HealthyInstanceEvidence_Passes()
    {
        var (pass, reason) = NxHostVerdicts.LinearPatternPostcondition(
            committed: true, alive: true, typeMatches: true,
            instanceCount: 5, requestedCount: 5,
            bodiesBefore: 1, bodiesAfter: 1, expectedNewBodies: 0, isGeometryMode: false);
        Assert.True(pass, reason);
    }

    [Fact]
    public void Verdict_FeatureMode_ZeroInstances_Fails()
    {
        var (pass, reason) = NxHostVerdicts.LinearPatternPostcondition(
            true, true, true, 0, 5, 1, 1, 0, false);
        Assert.False(pass);
        Assert.Contains("实例证据", reason);
    }

    [Fact]
    public void Verdict_GeometryMode_BodiesGrewByCopies_Passes()
    {
        // count=4, include seed → 3 new bodies.
        var (pass, reason) = NxHostVerdicts.LinearPatternPostcondition(
            true, true, true, -1, 4, 1, 4, 3, isGeometryMode: true);
        Assert.True(pass, reason);
    }

    [Fact]
    public void Verdict_GeometryMode_WrongBodyGrowth_Fails()
    {
        var (pass, reason) = NxHostVerdicts.LinearPatternPostcondition(
            true, true, true, -1, 4, 1, 2, 3, isGeometryMode: true);
        Assert.False(pass);
        Assert.Contains("实体数量变化", reason);
    }

    [Fact]
    public void Verdict_TypeMismatch_Fails()
    {
        var (pass, reason) = NxHostVerdicts.LinearPatternPostcondition(
            true, true, false, 5, 5, 1, 1, 0, false);
        Assert.False(pass);
        Assert.Contains("不匹配", reason);
    }

    [Fact]
    public void Verdict_DeadFeature_Fails()
    {
        var (pass, _) = NxHostVerdicts.LinearPatternPostcondition(
            true, false, true, 5, 5, 1, 1, 0, false);
        Assert.False(pass);
    }

    // ---------- Phase 9.8 bidirectional parameters ----------

    [Fact]
    public void Bidirectional_FullParameterSet_Parses()
    {
        // The user scene: "阵列特征 X+ 数量 4 间距 15，Y+ 数量 3 间距 15"
        var p = Parse(
            """{"mode":"feature","direction":"plus_x","count":4,"pitch":15,"direction2":"plus_y","count2":3,"pitch2":15,"unit":"mm"}""",
            out var error);
        Assert.Null(error);
        Assert.Equal(LinearPatternDirection.PlusY, p!.Direction2);
        Assert.Equal(3, p.Count2);
        Assert.Equal(15, p.Pitch2Mm);
    }

    [Fact]
    public void Bidirectional_UnitScalesPitch2()
    {
        var p = Parse(
            """{"mode":"feature","direction":"plus_x","count":4,"pitch":1.5,"direction2":"minus_z","count2":2,"pitch2":0.2,"unit":"cm"}""",
            out var error);
        Assert.Null(error);
        Assert.Equal(15, p!.PitchMm);   // 1.5 cm
        Assert.Equal(2, p.Pitch2Mm);    // 0.2 cm
    }

    [Fact]
    public void Bidirectional_UnitConversion()
    {
        var p = Parse(
            """{"mode":"feature","direction":"plus_x","count":4,"pitch":15,"direction2":"plus_y","count2":3,"pitch2":1.5,"unit":"cm"}""",
            out var error);
        Assert.Null(error);
        Assert.Equal(150, p!.PitchMm);  // 15 cm → 150 mm
        Assert.Equal(15, p.Pitch2Mm);   // 1.5 cm → 15 mm
    }

    [Theory]
    [InlineData("""{"mode":"feature","direction":"plus_x","count":4,"pitch":15,"direction2":"plus_y","count2":3,"unit":"mm"}""", "同时提供")]
    [InlineData("""{"mode":"feature","direction":"plus_x","count":4,"pitch":15,"direction2":"plus_y","pitch2":3,"unit":"mm"}""", "同时提供")]
    [InlineData("""{"mode":"feature","direction":"plus_x","count":4,"pitch":15,"count2":3,"pitch2":3,"unit":"mm"}""", "同时提供")]
    [InlineData("""{"mode":"feature","direction":"plus_x","count":4,"pitch":15,"direction2":"north","count2":3,"pitch2":3,"unit":"mm"}""", "不支持的 direction2")]
    [InlineData("""{"mode":"feature","direction":"plus_x","count":4,"pitch":15,"direction2":"minus_x","count2":3,"pitch2":3,"unit":"mm"}""", "平行")]
    [InlineData("""{"mode":"feature","direction":"plus_x","count":4,"pitch":15,"direction2":"plus_y","count2":1,"pitch2":3,"unit":"mm"}""", "count2")]
    [InlineData("""{"mode":"feature","direction":"plus_x","count":4,"pitch":15,"direction2":"plus_y","count2":3.5,"pitch2":3,"unit":"mm"}""", "count2")]
    [InlineData("""{"mode":"feature","direction":"plus_x","count":4,"pitch":15,"direction2":"plus_y","count2":3,"pitch2":0,"unit":"mm"}""", "pitch2")]
    [InlineData("""{"mode":"feature","direction":"plus_x","count":4,"pitch":15,"direction2":"plus_y","count2":3,"pitch2":1001,"unit":"mm"}""", "过大")]
    public void Bidirectional_Rejects(string json, string expectedFragment)
    {
        var p = Parse(json, out var error);
        Assert.Null(p);
        Assert.NotNull(error);
        Assert.Contains(expectedFragment, error!);
    }

    [Fact]
    public void Unidirectional_Params_HaveNullSecondDirection()
    {
        var p = Parse(
            """{"mode":"feature","direction":"plus_x","count":5,"pitch":10,"unit":"mm"}""",
            out var error);
        Assert.Null(error);
        Assert.Null(p!.Direction2);
        Assert.Null(p.Count2);
        Assert.Null(p.Pitch2Mm);
    }

    [Fact]
    public void Schema_IncludesBidirectionalFields()
    {
        var schema = LinearPatternToolContract.ParametersJsonSchema;
        Assert.Contains("direction2", schema);
        Assert.Contains("count2", schema);
        Assert.Contains("pitch2", schema);
    }
}
