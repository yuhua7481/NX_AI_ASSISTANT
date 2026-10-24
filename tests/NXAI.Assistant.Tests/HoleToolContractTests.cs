using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 9.1 create_hole contract tests (offline): parser validation,
/// registry integration, allowlist membership, and the probe attribution
/// mapping. Live NXOpen behavior (Face-as-ISurface, FeatureType string,
/// GetBodies semantics) is a separate acceptance item.
/// </summary>
public class HoleToolContractTests
{
    private static CreateHoleParameters? Parse(string json, out string? error) =>
        new CreateHoleRequestParser().ParseParameters(json, out error);

    // ---------- happy paths ----------

    [Fact]
    public void Hole_MinimalParameters_ParsesWithDefaults()
    {
        var p = Parse(
            """{"placement_face_ref":"FACE 1","diameter":8,"depth":15,"unit":"mm"}""",
            out var error);
        Assert.Null(error);
        Assert.NotNull(p);
        Assert.Equal("FACE 1", p.PlacementFaceRef);
        Assert.Equal(0, p.LocationU);
        Assert.Equal(0, p.LocationV);
        Assert.Equal(8, p.Diameter);
        Assert.Equal(15, p.Depth);
        Assert.Equal(118, p.TipAngleDeg);
    }

    [Fact]
    public void Hole_FullParameters_UnitConverted()
    {
        var p = Parse(
            """{"placement_face_ref":"FACE 2 {(0,0,10) BLOCK(1)}","location_u":1,"location_v":-0.5,"diameter":2,"depth":3,"tip_angle_deg":140,"unit":"cm"}""",
            out var error);
        Assert.Null(error);
        Assert.Equal(10, p!.LocationU);
        Assert.Equal(-5, p.LocationV);
        Assert.Equal(20, p.Diameter);
        Assert.Equal(30, p.Depth);
        Assert.Equal(140, p.TipAngleDeg);
    }

    [Fact]
    public void Hole_NegativeLocation_Allowed()
    {
        var p = Parse(
            """{"placement_face_ref":"F(1)","location_u":-25,"location_v":-10,"diameter":5,"depth":10,"unit":"mm"}""",
            out var error);
        Assert.Null(error);
        Assert.Equal(-25, p!.LocationU);
        Assert.Equal(-10, p.LocationV);
    }

    // ---------- rejections ----------

    [Theory]
    [InlineData("""{"diameter":8,"depth":15,"unit":"mm"}""")]                       // missing face ref
    [InlineData("""{"placement_face_ref":"","diameter":8,"depth":15,"unit":"mm"}""")] // empty ref
    [InlineData("""{"placement_face_ref":"FACE 1","diameter":0,"depth":15,"unit":"mm"}""")]  // zero diameter
    [InlineData("""{"placement_face_ref":"FACE 1","diameter":-3,"depth":15,"unit":"mm"}""")] // negative diameter
    [InlineData("""{"placement_face_ref":"FACE 1","diameter":8,"depth":0,"unit":"mm"}""")]   // zero depth
    [InlineData("""{"placement_face_ref":"FACE 1","diameter":8,"depth":-5,"unit":"mm"}""")]  // negative depth
    [InlineData("""{"placement_face_ref":"FACE 1","diameter":8,"unit":"mm"}""")]             // missing depth
    [InlineData("""{"placement_face_ref":"FACE 1","diameter":8,"depth":15}""")]              // missing unit
    [InlineData("""{"placement_face_ref":"FACE 1","diameter":8,"depth":15,"unit":"mm","extra":1}""")] // unknown field
    [InlineData("""{"placement_face_ref":"FACE 1","diameter":8,"depth":15,"tip_angle_deg":0,"unit":"mm"}""")]   // tip ≤ 0
    [InlineData("""{"placement_face_ref":"FACE 1","diameter":8,"depth":15,"tip_angle_deg":180,"unit":"mm"}""")] // tip ≥ 180
    [InlineData("""{"placement_face_ref":"FACE 1","diameter":8,"depth":15,"tip_angle_deg":"x","unit":"mm"}""")] // non-finite
    [InlineData("not json")]
    public void Hole_InvalidInputs_Rejected(string args)
    {
        var p = Parse(args, out var error);
        Assert.Null(p);
        Assert.NotNull(error);
    }

    // ---------- registry + allowlist integration ----------

    [Fact]
    public void Hole_IsRegistered_InToolRegistry()
    {
        var registry = CreateBlockTool.CreateRegistry();
        Assert.Equal(18, registry.All.Count);
        Assert.True(registry.IsRegistered("create_hole"));
    }

    [Fact]
    public void Hole_IsOnAutonomousAllowlist()
    {
        Assert.Equal(18, AutonomousToolAllowlist.Tools.Count);
        Assert.Contains("create_hole", AutonomousToolAllowlist.Tools);
        Assert.True(AutonomousToolAllowlist.Contains("create_hole"));
    }

    [Fact]
    public void Hole_SchemaAndParser_FieldSetsAgree()
    {
        var schema = CreateHoleTool.ParametersJsonSchema;
        foreach (var field in new[] { "placement_face_ref", "location_u", "location_v", "diameter", "depth", "tip_angle_deg", "unit" })
            Assert.Contains($"\"{field}\"", schema);
    }
}
