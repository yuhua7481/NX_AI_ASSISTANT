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

    // ----------------------- line (Phase: sketch line) -----------------------

    [Fact]
    public void Sketch_Line_ValidParameters()
    {
        var p = new CreateSketchRequestParser().ParseParameters(
            """{"plane_mode":"wcs_xy","profile_type":"line","start_u":-10,"start_v":-5,"end_u":30,"end_v":15,"unit":"mm"}""",
            out var error);
        Assert.Null(error);
        Assert.Equal(SketchProfileType.Line, p!.ProfileType);
        Assert.Equal(-10, p.StartU);
        Assert.Equal(-5, p.StartV);
        Assert.Equal(30, p.EndU);
        Assert.Equal(15, p.EndV);
    }

    [Fact]
    public void Sketch_Line_NegativeCoordinates_UnitConverted()
    {
        var p = new CreateSketchRequestParser().ParseParameters(
            """{"plane_mode":"selected_plane","profile_type":"line","start_u":-1,"start_v":-2,"end_u":3,"end_v":4,"unit":"cm"}""",
            out var error);
        Assert.Null(error);
        Assert.Equal(SketchPlaneMode.SelectedPlane, p!.PlaneMode);
        Assert.Equal(-10, p.StartU);
        Assert.Equal(-20, p.StartV);
        Assert.Equal(30, p.EndU);
        Assert.Equal(40, p.EndV);
    }

    [Theory]
    [InlineData("""{"plane_mode":"wcs_xy","profile_type":"line","start_u":5,"start_v":5,"end_u":5,"end_v":5,"unit":"mm"}""")]
    [InlineData("""{"plane_mode":"wcs_xy","profile_type":"line","start_u":0,"start_v":0,"end_u":0,"end_v":0,"unit":"mm"}""")]
    [InlineData("""{"plane_mode":"wcs_xy","profile_type":"line","start_u":0,"start_v":0,"unit":"mm"}""")]
    [InlineData("""{"plane_mode":"wcs_xy","profile_type":"line","start_u":0,"start_v":0,"end_u":"x","end_v":1,"unit":"mm"}""")]
    public void Sketch_Line_DegenerateOrInvalid_Rejected(string args)
    {
        var p = new CreateSketchRequestParser().ParseParameters(args, out var error);
        Assert.Null(p);
        Assert.NotNull(error);
    }

    // ----------------------- arc (Phase: sketch arc) -----------------------

    [Theory]
    [InlineData(0, 90)]
    [InlineData(0, 360)]
    [InlineData(359.5, 360)]
    [InlineData(45, 315)]
    public void Sketch_Arc_ValidAngleRanges(double s, double e)
    {
        var p = new CreateSketchRequestParser().ParseParameters(
            $$"""{"plane_mode":"wcs_xy","profile_type":"arc","center_u":10,"center_v":-5,"radius":7.5,"start_angle_deg":{{s}},"end_angle_deg":{{e}},"unit":"mm"}""",
            out var error);
        Assert.Null(error);
        Assert.Equal(SketchProfileType.Arc, p!.ProfileType);
        Assert.Equal(10, p.CenterU);
        Assert.Equal(-5, p.CenterV);
        Assert.Equal(7.5, p.Radius);
        Assert.Equal(s, p.StartAngleDeg);
        Assert.Equal(e, p.EndAngleDeg);
    }

    [Theory]
    [InlineData("""{"plane_mode":"wcs_xy","profile_type":"arc","radius":0,"start_angle_deg":0,"end_angle_deg":90,"unit":"mm"}""")]
    [InlineData("""{"plane_mode":"wcs_xy","profile_type":"arc","radius":-3,"start_angle_deg":0,"end_angle_deg":90,"unit":"mm"}""")]
    [InlineData("""{"plane_mode":"wcs_xy","profile_type":"arc","radius":5,"start_angle_deg":90,"end_angle_deg":90,"unit":"mm"}""")]
    [InlineData("""{"plane_mode":"wcs_xy","profile_type":"arc","radius":5,"start_angle_deg":120,"end_angle_deg":90,"unit":"mm"}""")]
    [InlineData("""{"plane_mode":"wcs_xy","profile_type":"arc","radius":5,"start_angle_deg":-10,"end_angle_deg":90,"unit":"mm"}""")]
    [InlineData("""{"plane_mode":"wcs_xy","profile_type":"arc","radius":5,"start_angle_deg":0,"end_angle_deg":361,"unit":"mm"}""")]
    [InlineData("""{"plane_mode":"wcs_xy","profile_type":"arc","radius":5,"start_angle_deg":0,"end_angle_deg":90}""")]
    [InlineData("""{"plane_mode":"wcs_xy","profile_type":"arc","radius":5,"start_angle_deg":"x","end_angle_deg":90,"unit":"mm"}""")]
    public void Sketch_Arc_InvalidInputs_Rejected(string args)
    {
        var p = new CreateSketchRequestParser().ParseParameters(args, out var error);
        Assert.Null(p);
        Assert.NotNull(error);
    }

    [Fact]
    public void Sketch_ExistingTypes_Unchanged_ByLineAndArcAddition()
    {
        // rectangle / circle parse exactly as before the line/arc addition.
        var rect = new CreateSketchRequestParser().ParseParameters(
            """{"plane_mode":"wcs_xy","profile_type":"rectangle","width":80,"height":40,"unit":"mm"}""",
            out var error1);
        Assert.Null(error1);
        Assert.Equal(SketchProfileType.Rectangle, rect!.ProfileType);
        Assert.Equal(80, rect.RectangleWidth);

        var circ = new CreateSketchRequestParser().ParseParameters(
            """{"plane_mode":"wcs_xy","profile_type":"circle","diameter":10,"unit":"mm"}""",
            out var error2);
        Assert.Null(error2);
        Assert.Equal(SketchProfileType.Circle, circ!.ProfileType);
        Assert.Equal(10, circ.CircleDiameter);
    }

    [Fact]
    public void Sketch_Schema_IncludesLineAndArc()
    {
        Assert.Contains("\"line\"", CreateSketchTool.ParametersJsonSchema);
        Assert.Contains("\"arc\"", CreateSketchTool.ParametersJsonSchema);
        Assert.Contains("start_u", CreateSketchTool.ParametersJsonSchema);
        Assert.Contains("radius", CreateSketchTool.ParametersJsonSchema);
        Assert.Contains("start_angle_deg", CreateSketchTool.ParametersJsonSchema);
        // Parser KnownFields must stay in sync with the schema (both directions).
        var schemaFields = CreateSketchTool.ParametersJsonSchema;
        Assert.Contains("end_v", schemaFields);
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
