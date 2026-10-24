using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 9.9 create_circular_pattern offline tests: parsing/validation of
/// the explicit modes and axis kinds, strict unknown-field rejection (the
/// R2 lesson), registration/allowlist/schema, the postcondition verdict
/// sub-conditions, and the controller routing stubs. The NX builder wiring
/// (PatternFeatureBuilder/PatternGeometryBuilder with the Circular
/// definition) is a separate live acceptance item — never claimed here.
/// </summary>
public class CircularPatternToolTests
{
    private static CreateCircularPatternParameters? Parse(string json, out string? error) =>
        new CreateCircularPatternRequestParser().ParseParameters(json, out error);

    // ---------- happy paths ----------

    [Fact]
    public void FeatureMode_FullCircle_Parses()
    {
        var p = Parse(
            """{"mode":"feature","axis":"plus_z","count":8}""",
            out var error);
        Assert.Null(error);
        Assert.Equal(CircularPatternMode.Feature, p!.Mode);
        Assert.Equal(CircularPatternAxis.PlusZ, p.Axis);
        Assert.Equal(8, p.Count);
        Assert.Null(p.TotalAngleDeg);
    }

    [Fact]
    public void GeometryMode_PartialArc_Parses()
    {
        var p = Parse(
            """{"mode":"geometry","axis":"datum_axis","count":5,"total_angle_deg":120}""",
            out var error);
        Assert.Null(error);
        Assert.Equal(CircularPatternMode.Geometry, p!.Mode);
        Assert.Equal(CircularPatternAxis.DatumAxis, p.Axis);
        Assert.Equal(120, p.TotalAngleDeg);
    }

    [Fact]
    public void LineEdgeAxis_Parses()
    {
        var p = Parse(
            """{"mode":"geometry","axis":"line_edge","count":6}""",
            out var error);
        Assert.Null(error);
        Assert.Equal(CircularPatternAxis.LineEdge, p!.Axis);
    }

    // ---------- rejections ----------

    [Theory]
    [InlineData("""{"axis":"plus_z","count":6}""", "mode")]
    [InlineData("""{"mode":"circular","axis":"plus_z","count":6}""", "不支持的 mode")]
    [InlineData("""{"mode":"feature","count":6}""", "axis")]
    [InlineData("""{"mode":"feature","axis":"spin","count":6}""", "不支持的 axis")]
    [InlineData("""{"mode":"feature","axis":"plus_z"}""", "count")]
    [InlineData("""{"mode":"feature","axis":"plus_z","count":1}""", "count")]
    [InlineData("""{"mode":"feature","axis":"plus_z","count":6.5}""", "count")]
    [InlineData("""{"mode":"feature","axis":"plus_z","count":101}""", "count")]
    [InlineData("""{"mode":"feature","axis":"plus_z","count":6,"total_angle_deg":0}""", "total_angle_deg")]
    [InlineData("""{"mode":"feature","axis":"plus_z","count":6,"total_angle_deg":361}""", "total_angle_deg")]
    [InlineData("""{"mode":"feature","axis":"plus_z","count":6,"total_angle_deg":-30}""", "total_angle_deg")]
    [InlineData("""{"mode":"feature","axis":"plus_z","count":6,"target_body_ref":"BLOCK(5)"}""", "不支持参数字段")]
    [InlineData("""{"mode":"feature","axis":"plus_z","count":6,"target":"CYLINDER(5)"}""", "不支持参数字段")]
    public void Rejections(string json, string expectedFragment)
    {
        var p = Parse(json, out var error);
        Assert.Null(p);
        Assert.NotNull(error);
        Assert.Contains(expectedFragment, error!);
    }

    // ---------- contract surface ----------

    [Fact]
    public void Registry_IncludesCircularPattern()
    {
        var registry = CreateBlockTool.CreateRegistry();
        Assert.True(registry.IsRegistered(CircularPatternToolContract.Name));
        Assert.NotNull(registry.Find(CircularPatternToolContract.Name));
    }

    [Fact]
    public void Allowlist_IncludesCircularPattern()
    {
        Assert.True(AutonomousToolAllowlist.Contains(CircularPatternToolContract.Name));
    }

    [Fact]
    public void Schema_RequiredFields_AreExplicit()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(
            CircularPatternToolContract.ParametersJsonSchema);
        var root = doc.RootElement;
        var required = root.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()).ToHashSet();
        Assert.Contains("mode", required);
        Assert.Contains("axis", required);
        Assert.Contains("count", required);
        // The schema must NOT advertise any object-reference field: the
        // host resolves the seed and the axis object (R2 lesson).
        Assert.False(root.TryGetProperty("properties", out var props) &&
            props.TryGetProperty("target", out _));
        Assert.False(root.TryGetProperty("properties", out var props2) &&
            props2.TryGetProperty("axis_ref", out _));
    }

    // ---------- postcondition verdict ----------

    [Fact]
    public void Verdict_FeatureMode_InstanceEvidenceRequired()
    {
        var (pass, reason) = NxHostVerdicts.CircularPatternPostcondition(
            true, true, true, 8, 8, 1, 1, 0, isGeometryMode: false);
        Assert.True(pass, reason);
    }

    [Fact]
    public void Verdict_FeatureMode_NoInstances_Fails()
    {
        var (pass, reason) = NxHostVerdicts.CircularPatternPostcondition(
            true, true, true, 0, 8, 1, 1, 0, isGeometryMode: false);
        Assert.False(pass);
        Assert.Contains("实例证据", reason);
    }

    [Fact]
    public void Verdict_GeometryMode_BodyGrowthExact()
    {
        // count=6, seed stays → 5 new bodies.
        var (pass, reason) = NxHostVerdicts.CircularPatternPostcondition(
            true, true, true, -1, 6, 1, 6, 5, isGeometryMode: true);
        Assert.True(pass, reason);
    }

    [Fact]
    public void Verdict_GeometryMode_WrongGrowth_Fails()
    {
        var (pass, reason) = NxHostVerdicts.CircularPatternPostcondition(
            true, true, true, -1, 6, 1, 3, 5, isGeometryMode: true);
        Assert.False(pass);
        Assert.Contains("实体数量变化", reason);
    }

    [Fact]
    public void Verdict_TypeMismatch_Fails()
    {
        var (pass, reason) = NxHostVerdicts.CircularPatternPostcondition(
            true, true, false, 6, 6, 1, 1, 0, isGeometryMode: false);
        Assert.False(pass);
        Assert.Contains("不匹配", reason);
    }

    [Fact]
    public void Verdict_DeadFeature_Fails()
    {
        var (pass, _) = NxHostVerdicts.CircularPatternPostcondition(
            true, false, true, 6, 6, 1, 1, 0, isGeometryMode: false);
        Assert.False(pass);
    }
}
