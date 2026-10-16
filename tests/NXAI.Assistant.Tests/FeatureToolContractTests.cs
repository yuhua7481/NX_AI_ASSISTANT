using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Offline contract tests for the Phase 7 feature tools: create_sketch,
/// create_extrude, create_revolve and boolean_operation. Parameter parsing,
/// schema-registered names, unit normalization and rejection paths only —
/// no NX, no network.
/// </summary>
public class FeatureToolContractTests
{
    // ------------------------------------------------------------ create_sketch

    [Fact]
    public void Sketch_Rectangle_ValidParameters()
    {
        var p = new CreateSketchRequestParser().ParseParameters(
            """{"plane_mode":"wcs_xy","profile_type":"rectangle","width":80,"height":40,"unit":"mm"}""",
            out var error);
        Assert.Null(error);
        Assert.NotNull(p);
        Assert.Equal(SketchPlaneMode.WcsXY, p.PlaneMode);
        Assert.Equal(SketchProfileType.Rectangle, p.ProfileType);
        Assert.Equal(80, p.RectangleWidth);
        Assert.Equal(40, p.RectangleHeight);
    }

    [Fact]
    public void Sketch_Circle_WithCenterOffset_UnitConverted()
    {
        var p = new CreateSketchRequestParser().ParseParameters(
            """{"plane_mode":"wcs_xz","profile_type":"circle","diameter":2,"center_u":1,"center_v":0.5,"unit":"cm"}""",
            out var error);
        Assert.Null(error);
        Assert.Equal(SketchProfileType.Circle, p!.ProfileType);
        Assert.Equal(20, p.CircleDiameter);
        Assert.Equal(10, p.CenterU);
        Assert.Equal(5, p.CenterV);
    }

    [Theory]
    [InlineData("""{"plane_mode":"wcs_xy","profile_type":"rectangle","width":-5,"height":40,"unit":"mm"}""")]
    [InlineData("""{"plane_mode":"wcs_xy","profile_type":"circle","diameter":0,"unit":"mm"}""")]
    [InlineData("""{"plane_mode":"wcs_xy","profile_type":"rectangle","height":40,"unit":"mm"}""")]
    [InlineData("""{"plane_mode":"wcs_xy","profile_type":"hexagon","width":10,"height":10,"unit":"mm"}""")]
    [InlineData("""{"plane_mode":"front","profile_type":"circle","diameter":10,"unit":"mm"}""")]
    [InlineData("""{"plane_mode":"wcs_xy","profile_type":"circle","diameter":10,"unit":"mm","extra":1}""")]
    [InlineData("""{"plane_mode":"wcs_xy","profile_type":"circle","diameter":10}""")]
    [InlineData("not json")]
    public void Sketch_InvalidInputs_Rejected(string args)
    {
        var p = new CreateSketchRequestParser().ParseParameters(args, out var error);
        Assert.Null(p);
        Assert.NotNull(error);
    }

    [Fact]
    public void Sketch_MissingPlaneMode_Rejected()
    {
        var p = new CreateSketchRequestParser().ParseParameters(
            """{"profile_type":"circle","diameter":10,"unit":"mm"}""", out var error);
        Assert.Null(p);
        Assert.Contains("plane_mode", error);
    }

    [Fact]
    public void Sketch_ToolRegistered_InRegistry()
    {
        var registry = CreateBlockTool.CreateRegistry();
        Assert.True(registry.IsRegistered(CreateSketchTool.Name));
        Assert.True(registry.IsRegistered(CreateExtrudeTool.Name));
        Assert.True(registry.IsRegistered(CreateRevolveTool.Name));
        Assert.True(registry.IsRegistered(BooleanOperationTool.Name));
    }

    // ------------------------------------------------------------ create_extrude

    [Fact]
    public void Extrude_ValidParameters_DistanceConverted()
    {
        var p = new CreateExtrudeRequestParser().ParseParameters(
            """{"sketch_ref":"SKETCH_1","distance":1,"direction":"reverse","unit":"cm"}""",
            out var error);
        Assert.Null(error);
        Assert.Equal("SKETCH_1", p!.SketchRef);
        Assert.Equal(10, p.Distance);
        Assert.Equal(ExtrudeDirection.Reverse, p.Direction);
    }

    [Fact]
    public void Extrude_DefaultDirection_IsNormal()
    {
        var p = new CreateExtrudeRequestParser().ParseParameters(
            """{"sketch_ref":"S1","distance":20,"unit":"mm"}""", out var error);
        Assert.Null(error);
        Assert.Equal(ExtrudeDirection.Normal, p!.Direction);
    }

    [Theory]
    [InlineData("""{"distance":20,"unit":"mm"}""", "sketch_ref")]
    [InlineData("""{"sketch_ref":"","distance":20,"unit":"mm"}""", "sketch_ref")]
    [InlineData("""{"sketch_ref":"S1","distance":-1,"unit":"mm"}""", "distance")]
    [InlineData("""{"sketch_ref":"S1","distance":20,"direction":"up","unit":"mm"}""", "direction")]
    [InlineData("""{"sketch_ref":"S1","distance":20,"unit":"mm","tag":123}""", "tag")]
    public void Extrude_InvalidInputs_Rejected(string args, string expectedFragment)
    {
        var p = new CreateExtrudeRequestParser().ParseParameters(args, out var error);
        Assert.Null(p);
        Assert.Contains(expectedFragment, error);
    }

    // ------------------------------------------------------------ create_revolve

    [Fact]
    public void Revolve_ValidParameters()
    {
        var p = new CreateRevolveRequestParser().ParseParameters(
            """{"sketch_ref":"S1","axis_mode":"wcs_z","angle":360}""", out var error);
        Assert.Null(error);
        Assert.Equal(RevolveAxisMode.WcsZ, p!.AxisMode);
        Assert.Equal(360, p.AngleDeg);
    }

    [Theory]
    [InlineData("""{"sketch_ref":"S1","axis_mode":"wcs_x","angle":0}""", "angle")]
    [InlineData("""{"sketch_ref":"S1","axis_mode":"wcs_x","angle":361}""", "angle")]
    [InlineData("""{"sketch_ref":"S1","axis_mode":"wcs_x","angle":-30}""", "angle")]
    [InlineData("""{"sketch_ref":"S1","angle":180}""", "axis_mode")]
    [InlineData("""{"sketch_ref":"S1","axis_mode":"global","angle":180}""", "axis_mode")]
    [InlineData("""{"axis_mode":"wcs_x","angle":180}""", "sketch_ref")]
    [InlineData("""{"sketch_ref":"S1","axis_mode":"wcs_x","angle":180,"unit":"mm"}""", "unit")]
    public void Revolve_InvalidInputs_Rejected(string args, string expectedFragment)
    {
        var p = new CreateRevolveRequestParser().ParseParameters(args, out var error);
        Assert.Null(p);
        Assert.Contains(expectedFragment, error);
    }

    // ------------------------------------------------------------ boolean_operation

    [Theory]
    [InlineData("unite", BooleanOperationType.Unite)]
    [InlineData("subtract", BooleanOperationType.Subtract)]
    [InlineData("intersect", BooleanOperationType.Intersect)]
    public void Boolean_ValidOperations(string op, BooleanOperationType expected)
    {
        string args = "{\"operation\":\"" + op + "\",\"target_body_ref\":\"B1\",\"tool_body_ref\":\"B2\"}";
        var p = new BooleanOperationRequestParser().ParseParameters(args, out var error);
        Assert.Null(error);
        Assert.Equal(expected, p!.Operation);
        Assert.Equal("B1", p.TargetBodyRef);
        Assert.Equal("B2", p.ToolBodyRef);
    }

    [Theory]
    [InlineData("""{"operation":"merge","target_body_ref":"B1","tool_body_ref":"B2"}""", "operation")]
    [InlineData("""{"operation":"unite","tool_body_ref":"B2"}""", "target_body_ref")]
    [InlineData("""{"operation":"unite","target_body_ref":"B1"}""", "tool_body_ref")]
    [InlineData("""{"operation":"unite","target_body_ref":"B1","tool_body_ref":"B1"}""", "不能相同")]
    [InlineData("""{"operation":"unite","target_body_ref":"B1","tool_body_ref":"B2","mode":1}""", "mode")]
    public void Boolean_InvalidInputs_Rejected(string args, string expectedFragment)
    {
        var p = new BooleanOperationRequestParser().ParseParameters(args, out var error);
        Assert.Null(p);
        Assert.Contains(expectedFragment, error);
    }
}
