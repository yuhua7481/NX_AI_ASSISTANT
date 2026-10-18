using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Offline tests for the create_block tool contract: schema presence in the
/// registry, argument parsing (types, units, bounds, extra fields, JSON),
/// and structured rejection results. Runs entirely outside NX.
/// </summary>
public class CreateBlockToolContractTests
{
    private static CreateBlockParameters? Parse(string json, out string? error) =>
        new CreateBlockRequestParser().ParseParameters(json, out error);

    // ------------------------------------------------------------- registry

    [Fact]
    public void Registry_ContainsCreateBlock()
    {
        var registry = CreateBlockTool.CreateRegistry();
        var tools = registry.All;
        Assert.Equal(8, tools.Count);
        Assert.True(registry.IsRegistered("create_block"));
        Assert.True(registry.IsRegistered("create_cylinder"));
        Assert.True(registry.IsRegistered("create_cone"));
        Assert.True(registry.IsRegistered("create_sphere"));
        Assert.True(registry.IsRegistered("create_sketch"));
        Assert.True(registry.IsRegistered("create_extrude"));
        Assert.True(registry.IsRegistered("create_revolve"));
        Assert.True(registry.IsRegistered("boolean_operation"));
        Assert.False(registry.IsRegistered("create_torus"));
        Assert.False(registry.IsRegistered(""));
        Assert.False(registry.IsRegistered(null!));
    }

    [Fact]
    public void Schema_IsValidJson_RequiringAllCoreFields()
    {
        var registry = CreateBlockTool.CreateRegistry();
        var definition = registry.Find("create_block")!;
        using var doc = System.Text.Json.JsonDocument.Parse(definition.ParametersJsonSchema);
        var root = doc.RootElement;

        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        var required = root.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()).ToHashSet();
        Assert.Equal(new HashSet<string?> { "length", "width", "height", "unit" }, required);
        Assert.Equal(7, root.GetProperty("properties").EnumerateObject().Count());
        Assert.Equal(new[] { "mm", "cm", "m", "inch" },
            root.GetProperty("properties").GetProperty("unit").GetProperty("enum")
                .EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    // ---------------------------------------------------------- happy paths

    [Fact]
    public void Parse_MinimalRequest_DefaultsOriginAndUnit()
    {
        var p = Parse("""{"length":100,"width":60,"height":20,"unit":"mm"}""", out var error);
        Assert.Null(error);
        Assert.NotNull(p);
        Assert.Equal(100, p!.Length);
        Assert.Equal(60, p.Width);
        Assert.Equal(20, p.Height);
        Assert.Equal(0, p.OriginX);
        Assert.Equal(0, p.OriginY);
        Assert.Equal(0, p.OriginZ);
        Assert.Equal("mm", p.Unit);
    }

    [Fact]
    public void Parse_InchRequest_ConvertsToMillimeters()
    {
        var p = Parse("""{"length":2,"width":1,"height":0.5,"unit":"inch"}""", out _);
        Assert.NotNull(p);
        Assert.Equal(2 * 25.4, p!.Length, 10);
        Assert.Equal(25.4, p.Width, 10);
        Assert.Equal(0.5 * 25.4, p.Height, 10);
        Assert.Equal("mm", p.Unit);
    }

    [Fact]
    public void Parse_MeterRequest_ConvertsToMillimeters()
    {
        var p = Parse("""{"length":0.1,"width":0.06,"height":0.02,"unit":"m"}""", out _);
        Assert.NotNull(p);
        Assert.Equal(100, p!.Length, 10);
        Assert.Equal(60, p.Width, 10);
        Assert.Equal(20, p.Height, 10);
    }

    [Fact]
    public void Parse_WithOrigin_PassesThrough()
    {
        var p = Parse("""{"length":10,"width":10,"height":10,"unit":"mm","origin_x":5,"origin_y":-2,"origin_z":3}""", out _);
        Assert.NotNull(p);
        Assert.Equal(5, p!.OriginX);
        Assert.Equal(3, p.OriginZ);
    }

    [Fact]
    public void Parse_StringEncodedNumbers_Accepted()
    {
        var p = Parse("""{"length":"100","width":60,"height":20,"unit":"mm"}""", out _);
        Assert.NotNull(p);
        Assert.Equal(100, p!.Length);
    }

    // ------------------------------------------------------------ rejections

    [Fact]
    public void Parse_InvalidJson_Rejected()
    {
        var p = Parse("{not json", out var error);
        Assert.Null(p);
        Assert.NotNull(error);
    }

    [Fact]
    public void Parse_NonObjectPayload_Rejected()
    {
        Assert.Null(Parse("[1,2,3]", out _));
        Assert.Null(Parse("42", out _));
    }

    [Fact]
    public void Parse_MissingRequiredFields_Rejected()
    {
        Assert.Null(Parse("""{"length":100,"width":60,"height":20}""", out var e1));
        Assert.Contains("unit", e1);
        Assert.Null(Parse("""{"width":60,"height":20,"unit":"mm"}""", out var e2));
        Assert.Contains("length", e2);
        Assert.Null(Parse("""{"length":100,"height":20,"unit":"mm"}""", out var e3));
        Assert.Contains("width", e3);
    }

    [Fact]
    public void Parse_NonPositiveDimensions_Rejected()
    {
        Assert.Null(Parse("""{"length":0,"width":60,"height":20,"unit":"mm"}""", out _));
        Assert.Null(Parse("""{"length":-5,"width":60,"height":20,"unit":"mm"}""", out _));
        Assert.Null(Parse("""{"length":10,"width":0,"height":20,"unit":"mm"}""", out _));
        Assert.Null(Parse("""{"length":10,"width":10,"height":-1,"unit":"mm"}""", out _));
    }

    [Fact]
    public void Parse_NonFiniteDimensions_Rejected()
    {
        // JSON itself cannot carry NaN/Infinity; string-encoded values must be refused.
        Assert.Null(Parse("""{"length":"NaN","width":10,"height":10,"unit":"mm"}""", out _));
        Assert.Null(Parse("""{"length":"Infinity","width":10,"height":10,"unit":"mm"}""", out _));
    }

    [Fact]
    public void Parse_DimensionOverSanityBound_Rejected()
    {
        var p = Parse(
            $$"""{"length":{{BlockRequestValidator.MaxDimensionMm * 10}},"width":60,"height":20,"unit":"mm"}""",
            out var error);
        Assert.Null(p);
        Assert.Contains("sanity bound", error);
    }

    [Fact]
    public void Parse_OriginOverSanityBound_Rejected()
    {
        var p = Parse(
            $$"""{"length":100,"width":60,"height":20,"unit":"mm","origin_x":{{BlockRequestValidator.MaxCoordinateMm * 2}}}""",
            out var error);
        Assert.Null(p);
        Assert.Contains("sanity bound", error);
    }

    [Fact]
    public void Parse_UnsupportedUnit_Rejected()
    {
        Assert.Null(Parse("""{"length":10,"width":10,"height":10,"unit":"furlong"}""", out var e1));
        Assert.Contains("不支持", e1);
        Assert.Null(Parse("""{"length":10,"width":10,"height":10,"unit":123}""", out _));
    }

    [Fact]
    public void Parse_UnknownExtraField_Rejected()
    {
        Assert.Null(Parse(
            """{"length":10,"width":10,"height":10,"unit":"mm","color":"red"}""",
            out var error));
        Assert.Contains("color", error);
    }

    [Fact]
    public void Parse_WrongFieldType_Rejected()
    {
        Assert.Null(Parse("""{"length":"wide","width":10,"height":10,"unit":"mm"}""", out var e1));
        Assert.Contains("length", e1);
        Assert.Null(Parse("""{"length":true,"width":10,"height":10,"unit":"mm"}""", out _));
    }

    [Fact]
    public void Parse_NonFiniteLiterals_Rejected()
    {
        // NaN/Infinity are not valid JSON literals — rejected at parse level.
        Assert.Null(Parse("""{"length":NaN,"width":60,"height":20,"unit":"mm"}""", out _));
    }

    [Fact]
    public void Parse_ExecutorContract_RejectsUnregisteredTool()
    {
        var registry = CreateBlockTool.CreateRegistry();
        Assert.False(registry.IsRegistered("delete_all_bodies"));
    }
}
