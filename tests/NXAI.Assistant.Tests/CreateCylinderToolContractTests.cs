using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Offline tests for the create_cylinder tool contract: registration beside
/// create_block, argument parsing (types, units, bounds, extra fields, JSON),
/// and structured rejections. Runs entirely outside NX.
/// </summary>
public class CreateCylinderToolContractTests
{
    private static CreateCylinderParameters? Parse(string json, out string? error) =>
        new CreateCylinderRequestParser().ParseParameters(json, out error);

    // ------------------------------------------------------------- registry

    [Fact]
    public void Registry_ContainsBothTools()
    {
        var registry = CreateBlockTool.CreateRegistry();
        Assert.Equal(8, registry.All.Count);
        Assert.True(registry.IsRegistered("create_block"));
        Assert.True(registry.IsRegistered("create_cylinder"));
        Assert.True(registry.IsRegistered("create_cone"));
        Assert.True(registry.IsRegistered("create_sphere"));
        Assert.True(registry.IsRegistered("create_sketch"));
        Assert.True(registry.IsRegistered("create_extrude"));
        Assert.True(registry.IsRegistered("create_revolve"));
        Assert.True(registry.IsRegistered("boolean_operation"));
        Assert.False(registry.IsRegistered("create_torus"));
        Assert.NotNull(registry.Find("create_cylinder"));
    }

    [Fact]
    public void CylinderSchema_IsValidJson_RequiringCoreFields()
    {
        var registry = CreateBlockTool.CreateRegistry();
        var definition = registry.Find("create_cylinder")!;
        using var doc = System.Text.Json.JsonDocument.Parse(definition.ParametersJsonSchema);
        var root = doc.RootElement;

        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        var required = root.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()).ToHashSet();
        Assert.Equal(new HashSet<string?> { "diameter", "height", "unit" }, required);
        Assert.Equal(new[] { "mm", "cm", "m", "inch" },
            root.GetProperty("properties").GetProperty("unit").GetProperty("enum")
                .EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    // ---------------------------------------------------------- happy paths

    [Fact]
    public void Parse_MinimalRequest_DefaultsOriginAndUnit()
    {
        var p = Parse("""{"diameter":40,"height":25,"unit":"mm"}""", out var error);
        Assert.Null(error);
        Assert.NotNull(p);
        Assert.Equal(40, p!.Diameter);
        Assert.Equal(25, p.Height);
        Assert.Equal(0, p.OriginX);
        Assert.Equal(0, p.OriginY);
        Assert.Equal(0, p.OriginZ);
        Assert.Equal("mm", p.Unit);
    }

    [Fact]
    public void Parse_InchRequest_ConvertsToMillimeters()
    {
        var p = Parse("""{"diameter":1,"height":0.5,"unit":"inch"}""", out _);
        Assert.NotNull(p);
        Assert.Equal(25.4, p!.Diameter, 10);
        Assert.Equal(0.5 * 25.4, p.Height, 10);
        Assert.Equal("mm", p.Unit);
    }

    [Fact]
    public void Parse_MeterAndCentimeter_ConvertToMillimeters()
    {
        var p1 = Parse("""{"diameter":0.05,"height":0.02,"unit":"m"}""", out _);
        Assert.NotNull(p1);
        Assert.Equal(50, p1!.Diameter, 10);
        Assert.Equal(20, p1.Height, 10);

        var p2 = Parse("""{"diameter":5,"height":2,"unit":"cm"}""", out _);
        Assert.NotNull(p2);
        Assert.Equal(50, p2!.Diameter, 10);
    }

    [Fact]
    public void Parse_WithOrigin_PassesThrough()
    {
        var p = Parse(
            """{"diameter":10,"height":5,"unit":"mm","origin_x":1.5,"origin_y":-2,"origin_z":3}""",
            out _);
        Assert.NotNull(p);
        Assert.Equal(1.5, p!.OriginX);
        Assert.Equal(-2, p.OriginY);
        Assert.Equal(3, p.OriginZ);
    }

    [Fact]
    public void Parse_StringEncodedNumbers_Accepted()
    {
        var p = Parse("""{"diameter":"40","height":25,"unit":"mm"}""", out _);
        Assert.NotNull(p);
        Assert.Equal(40, p!.Diameter);
    }

    // ------------------------------------------------------------ rejections

    [Fact]
    public void Parse_InvalidJson_Rejected()
    {
        Assert.Null(Parse("{not json", out _));
        Assert.Null(Parse("[1,2]", out _));
        Assert.Null(Parse("42", out _));
    }

    [Fact]
    public void Parse_MissingRequiredFields_Rejected()
    {
        Assert.Null(Parse("""{"diameter":40,"height":25}""", out var e1));
        Assert.Contains("unit", e1);
        Assert.Null(Parse("""{"height":25,"unit":"mm"}""", out var e2));
        Assert.Contains("diameter", e2);
        Assert.Null(Parse("""{"diameter":40,"unit":"mm"}""", out var e3));
        Assert.Contains("height", e3);
    }

    [Fact]
    public void Parse_NonPositiveValues_Rejected()
    {
        Assert.Null(Parse("""{"diameter":0,"height":25,"unit":"mm"}""", out _));
        Assert.Null(Parse("""{"diameter":-10,"height":25,"unit":"mm"}""", out _));
        Assert.Null(Parse("""{"diameter":40,"height":0,"unit":"mm"}""", out _));
        Assert.Null(Parse("""{"diameter":40,"height":-3,"unit":"mm"}""", out _));
    }

    [Fact]
    public void Parse_NonFiniteValues_Rejected()
    {
        Assert.Null(Parse("""{"diameter":"NaN","height":25,"unit":"mm"}""", out _));
        Assert.Null(Parse("""{"diameter":40,"height":"Infinity","unit":"mm"}""", out _));
    }

    [Fact]
    public void Parse_ValuesOverSanityBound_Rejected()
    {
        var p1 = Parse(
            $$"""{"diameter":{{BlockRequestValidator.MaxDimensionMm * 10}},"height":25,"unit":"mm"}""",
            out var e1);
        Assert.Null(p1);
        Assert.Contains("sanity bound", e1);

        var p2 = Parse(
            $$"""{"diameter":40,"height":25,"unit":"mm","origin_x":{{BlockRequestValidator.MaxCoordinateMm * 2}}}""",
            out var e2);
        Assert.Null(p2);
        Assert.Contains("sanity bound", e2);
    }

    [Fact]
    public void Parse_UnsupportedUnit_Rejected()
    {
        Assert.Null(Parse("""{"diameter":40,"height":25,"unit":"furlong"}""", out var e1));
        Assert.Contains("不支持", e1);
        Assert.Null(Parse("""{"diameter":40,"height":25,"unit":5}""", out _));
    }

    [Fact]
    public void Parse_UnknownExtraField_Rejected()
    {
        Assert.Null(Parse(
            """{"diameter":40,"height":25,"unit":"mm","color":"red"}""",
            out var error));
        Assert.Contains("color", error);
    }

    [Fact]
    public void Parse_WrongFieldType_Rejected()
    {
        Assert.Null(Parse("""{"diameter":"wide","height":25,"unit":"mm"}""", out _));
        Assert.Null(Parse("""{"diameter":true,"height":25,"unit":"mm"}""", out _));
    }

    [Fact]
    public void Parse_CylinderNeverProvidesFilePath_NoPathFieldAccepted()
    {
        // A model-invented "file_path"/"save_path" field is an unknown field:
        // rejected before any executor could see it.
        Assert.Null(Parse(
            """{"diameter":40,"height":25,"unit":"mm","save_path":"D:\\x.prt"}""",
            out var error));
        Assert.Contains("save_path", error);
    }
}
