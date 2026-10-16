using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Offline tests for the create_cone tool contract, including the optional
/// top_diameter (frustum) support added in Phase 4.1. Runs outside NX.
/// </summary>
public class CreateConeToolContractTests
{
    private static CreateConeParameters? Parse(string json, out string? error) =>
        new CreateConeRequestParser().ParseParameters(json, out error);

    // ------------------------------------------------------------- registry

    [Fact]
    public void Schema_IsValidJson_OptionalTopDiameter()
    {
        var registry = CreateBlockTool.CreateRegistry();
        var definition = registry.Find("create_cone")!;
        using var doc = System.Text.Json.JsonDocument.Parse(definition.ParametersJsonSchema);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        var required = root.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()).ToHashSet();
        Assert.Equal(new HashSet<string?> { "base_diameter", "height", "unit" }, required);
        // top_diameter must be declared but optional.
        Assert.True(root.GetProperty("properties").TryGetProperty("top_diameter", out var top));
        Assert.DoesNotContain("top_diameter", required);
        Assert.Equal(0, top.GetProperty("minimum").GetDouble());
    }

    // ---------------------------------------------------------- cone (top=0)

    [Fact]
    public void Parse_WithoutTopDiameter_DefaultsToZero_StandardCone()
    {
        var p = Parse("""{"base_diameter":50,"height":30,"unit":"mm"}""", out var error);
        Assert.Null(error);
        Assert.NotNull(p);
        Assert.Equal(50, p!.BaseDiameter);
        Assert.Equal(0, p.TopDiameter);
        Assert.Equal(30, p.Height);
        Assert.Equal("mm", p.Unit);
    }

    [Fact]
    public void Parse_ExplicitTopDiameterZero_StandardCone()
    {
        var p = Parse("""{"base_diameter":50,"top_diameter":0,"height":30,"unit":"mm"}""", out _);
        Assert.NotNull(p);
        Assert.Equal(0, p!.TopDiameter);
    }

    [Fact]
    public void Parse_PositiveTopDiameter_Frustum()
    {
        var p = Parse("""{"base_diameter":40,"top_diameter":20,"height":20,"unit":"mm"}""", out var error);
        Assert.Null(error);
        Assert.NotNull(p);
        Assert.Equal(40, p!.BaseDiameter);
        Assert.Equal(20, p.TopDiameter);
        Assert.Equal(20, p.Height);
    }

    // ------------------------------------------------------ frustum rejects

    [Fact]
    public void Parse_NegativeTopDiameter_Rejected()
    {
        Assert.Null(Parse("""{"base_diameter":40,"top_diameter":-1,"height":20,"unit":"mm"}""", out var e));
        Assert.Contains("负数", e);
    }

    [Fact]
    public void Parse_TopDiameterEqualToBase_Rejected()
    {
        Assert.Null(Parse("""{"base_diameter":40,"top_diameter":40,"height":20,"unit":"mm"}""", out var e));
        Assert.Contains("小于 base_diameter", e);
    }

    [Fact]
    public void Parse_TopDiameterOverBase_Rejected()
    {
        Assert.Null(Parse("""{"base_diameter":40,"top_diameter":80,"height":20,"unit":"mm"}""", out var e));
        Assert.Contains("小于 base_diameter", e);
    }

    [Fact]
    public void Parse_NonFiniteTopDiameter_Rejected()
    {
        Assert.Null(Parse("""{"base_diameter":40,"top_diameter":"NaN","height":20,"unit":"mm"}""", out _));
        Assert.Null(Parse("""{"base_diameter":40,"top_diameter":"Infinity","height":20,"unit":"mm"}""", out _));
    }

    [Fact]
    public void Parse_TopDiameterOverSanityBound_Rejected()
    {
        var p = Parse(
            $$"""{"base_diameter":40,"top_diameter":20,"height":{{BlockRequestValidator.MaxDimensionMm * 10}},"unit":"mm"}""",
            out var error);
        Assert.Null(p);
        Assert.Contains("sanity bound", error);
    }

    // ---------------------------------------------------------- common rules

    [Fact]
    public void Parse_UnknownField_Rejected()
    {
        Assert.Null(Parse(
            """{"base_diameter":40,"top_diameter":20,"height":20,"unit":"mm","apex":"sharp"}""",
            out var e));
        Assert.Contains("apex", e);
    }

    [Fact]
    public void Parse_InvalidJson_AndNonObject_Rejected()
    {
        Assert.Null(Parse("{bad", out _));
        Assert.Null(Parse("[]", out _));
    }

    [Fact]
    public void Parse_MissingRequired_Rejected()
    {
        Assert.Null(Parse("""{"base_diameter":40,"height":20}""", out var e1));
        Assert.Contains("unit", e1);
        Assert.Null(Parse("""{"height":20,"unit":"mm"}""", out var e2));
        Assert.Contains("base_diameter", e2);
    }

    [Fact]
    public void Parse_NonPositiveBaseOrHeight_Rejected()
    {
        Assert.Null(Parse("""{"base_diameter":0,"height":20,"unit":"mm"}""", out _));
        Assert.Null(Parse("""{"base_diameter":-40,"height":20,"unit":"mm"}""", out _));
        Assert.Null(Parse("""{"base_diameter":40,"height":0,"unit":"mm"}""", out _));
    }

    [Fact]
    public void Parse_InchUnits_ConvertedToMillimeters()
    {
        var p = Parse("""{"base_diameter":2,"top_diameter":1,"height":1,"unit":"inch"}""", out _);
        Assert.NotNull(p);
        Assert.Equal(2 * 25.4, p!.BaseDiameter, 10);
        Assert.Equal(25.4, p.TopDiameter, 10);
        Assert.Equal(25.4, p.Height, 10);
        Assert.Equal("mm", p.Unit);
    }

    [Fact]
    public void Parse_OriginOptional_DefaultsZero()
    {
        var p = Parse("""{"base_diameter":40,"height":20,"unit":"mm","origin_x":5}""", out _);
        Assert.NotNull(p);
        Assert.Equal(5, p!.OriginX);
        Assert.Equal(0, p.OriginY);
        Assert.Equal(0, p.OriginZ);
    }
}
