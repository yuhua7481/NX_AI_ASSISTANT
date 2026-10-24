using System.Text.Json;

namespace NXAI.Assistant.Core;

/// <summary>
/// Rotation axis for create_circular_pattern, expressed ONLY in forms the
/// host can resolve and verify against real NX objects (2026-10-23 design):
///   - Z/X/Y: an absolute model-space axis through the origin;
///   - datum_axis: a DatumAxis the user selected (the host reads it from
///     the current selection — never a model-invented reference);
///   - line_edge: a straight solid edge the user selected (the host reads
///     the edge's two endpoints and builds an axis line).
/// The model NEVER passes a body as the axis or invents coordinates.
/// </summary>
public enum CircularPatternAxis
{
    PlusZ,
    PlusX,
    PlusY,
    DatumAxis,
    LineEdge,
}

/// <summary>
/// Accepted parameters for create_circular_pattern. Mode is REQUIRED and
/// explicit (feature vs geometry — same semantics as create_linear_pattern).
/// Axis is REQUIRED and explicit. Count is the TOTAL instance count
/// including the seed (2–100, same semantics as the linear tool). Angular
/// extent: full circle (360°) by default, or an explicit total_angle_deg
/// (exclusiveMinimum 0, ≤360). Pitch semantics:
///   - full circle: pitch = 360/count degrees between adjacent instances
///     (NX divides the circle evenly; no duplicated start/end instance);
///   - partial arc (total_angle_deg set): the host sets SpanAngle=
///     total_angle and lets NX compute the pitch from NCopies (the
///     AngularPatternSpacing.UsePitchOption=false path), so the last
///     instance lands ON the end angle.
/// Angle values are in degrees; the valid range is 0 to 360 inclusive
/// (exclusive of 0). No unit conversion applies to angles.
/// </summary>
public sealed record CreateCircularPatternParameters(
    CircularPatternMode Mode,
    CircularPatternAxis Axis,
    int Count,
    double? TotalAngleDeg = null);

public enum CircularPatternMode
{
    Feature,
    Geometry,
}

/// <summary>
/// Parses and validates create_circular_pattern arguments: mode
/// ("feature"|"geometry"), axis ("plus_z"|"plus_x"|"plus_y"|"datum_axis"|
/// "line_edge"), count (integer 2–100, includes the seed), and the optional
/// total_angle_deg (0 to 360 inclusive, 0 excluded; omitted = full
/// circle). No object
/// references are accepted — the axis object (datum axis / straight edge)
/// comes from the CURRENT SELECTION, resolved host-side, like every other
/// target in this project.
/// </summary>
public sealed class CreateCircularPatternRequestParser : IToolRequestParser
{
    public const string ToolName = "create_circular_pattern";
    private static readonly HashSet<string> KnownFields = new()
    { "mode", "axis", "count", "total_angle_deg" };

    public CreateCircularPatternParameters? ParseParameters(string argumentsJson, out string? error)
    {
        error = null;
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            error = $"{ToolName} 参数不是合法 JSON。";
            return null;
        }
        if (root.ValueKind != JsonValueKind.Object)
        {
            error = $"{ToolName} 参数必须是 JSON 对象。";
            return null;
        }
        error = GeometryToolJson.RejectUnknownFields(root, ToolName, KnownFields);
        if (error != null)
            return null;

        if (!root.TryGetProperty("mode", out var modeEl) || modeEl.ValueKind != JsonValueKind.String)
        {
            error = "缺少必填参数：mode（\"feature\"=阵列特征 或 \"geometry\"=阵列几何体）。";
            return null;
        }
        CircularPatternMode? mode = modeEl.GetString() switch
        {
            "feature" => CircularPatternMode.Feature,
            "geometry" => CircularPatternMode.Geometry,
            _ => null,
        };
        if (mode == null)
        {
            error = $"不支持的 mode：{modeEl.GetString()}。支持 \"feature\"（阵列特征）与 \"geometry\"（阵列几何体）。";
            return null;
        }

        if (!root.TryGetProperty("axis", out var axisEl) || axisEl.ValueKind != JsonValueKind.String)
        {
            error = "缺少必填参数：axis（旋转轴：plus_z/plus_x/plus_y/datum_axis/line_edge）。" +
                    "datum_axis/line_edge 要求用户已在 NX 中选中对应的基准轴/直线边；" +
                    "无法确定旋转轴时先向用户澄清，不得猜测。";
            return null;
        }
        CircularPatternAxis? axis = axisEl.GetString() switch
        {
            "plus_z" => CircularPatternAxis.PlusZ,
            "plus_x" => CircularPatternAxis.PlusX,
            "plus_y" => CircularPatternAxis.PlusY,
            "datum_axis" => CircularPatternAxis.DatumAxis,
            "line_edge" => CircularPatternAxis.LineEdge,
            _ => null,
        };
        if (axis == null)
        {
            error = $"不支持的 axis：{axisEl.GetString()}。支持 plus_z/plus_x/plus_y/datum_axis/line_edge。";
            return null;
        }

        if (!root.TryGetProperty("count", out var countEl) || countEl.ValueKind != JsonValueKind.Number)
        {
            error = "缺少必填参数：count（总实例数，含原始实例，2–100 的整数）。";
            return null;
        }
        int count = (int)Math.Round(countEl.GetDouble());
        if (Math.Abs(countEl.GetDouble() - count) > 1e-9 || count < 2 || count > 100)
        {
            error = $"count 必须是 2–100 的整数（收到 {countEl.GetDouble()}）。count 含原始实例。";
            return null;
        }

        double? totalAngle = null;
        if (root.TryGetProperty("total_angle_deg", out var angleEl))
        {
            if (angleEl.ValueKind != JsonValueKind.Number ||
                !TryFinite(angleEl.GetDouble(), out double a))
            {
                error = "total_angle_deg 必须是有限数字（角度，单位度，0 < 角度 ≤ 360）。";
                return null;
            }
            if (a <= 0 || a > 360)
            {
                error = $"total_angle_deg 必须在 (0, 360] 范围内（收到 {a:0.###} 度）。" +
                        "完整圆周请省略该参数。";
                return null;
            }
            // 2026-10-23 T1 live fix: a full-circle request phrased as
            // total_angle_deg=360 IS the full circle — normalize it to the
            // omitted form so the executor's 360/count pitch path runs
            // (6 instances over 360° → 60° between neighbors; the partial
            // arc formula total/(count−1) would have produced 72°).
            totalAngle = a >= 360 ? null : a;
        }

        return new CreateCircularPatternParameters(mode.Value, axis.Value, count, totalAngle);
    }

    private static bool TryFinite(double v, out double value)
    {
        value = v;
        return !double.IsNaN(v) && !double.IsInfinity(v);
    }

    object? IToolRequestParser.Parse(string argumentsJson, out string? error) =>
        ParseParameters(argumentsJson, out error);
}

/// <summary>
/// Contract for create_circular_pattern. Feature mode = NX
/// PatternFeatureBuilder with PatternDefinition.PatternType=Circular;
/// Geometry mode = NX PatternGeometryBuilder with the same circular
/// definition (both verified against NX2512's NXOpen.xml: CircularPattern
/// "specification along both the angular and radial directions", NX7.5+;
/// AngularPatternSpacing PitchAngle/SpanAngle/UsePitchOption; PatternSpacing
/// NCopies). The axis comes from the current selection or an absolute axis
/// through the origin — never a guessed center, never a body-as-axis. The
/// seed resolution reuses the linear tool's selection-first priority.
/// </summary>
public static class CircularPatternToolContract
{
    public const string Name = CreateCircularPatternRequestParser.ToolName;

    public const string Description =
        "对目标创建环形（圆形）阵列（NX 原生 Circular Pattern）。需要参数：" +
        "mode（\"feature\"=阵列已有建模特征，如孔特征；\"geometry\"=阵列实体/几何体）、" +
        "axis（旋转轴：plus_z/plus_x/plus_y=过原点的绝对坐标轴；datum_axis/line_edge=用户当前选中的基准轴/直线（拓扑边或独立直线特征），" +
        "无法确定轴时先问用户，不得猜测）、count（总实例数，含原始实例，2–100）、" +
        "total_angle_deg（可选，总角度，0<角≤360；360 或省略均表示完整圆周 360 度，" +
        "实例沿圆周均匀分布且首尾不重复，相邻角距=360/count；部分圆弧时相邻角距=总角度/(count-1)，末实例落在结束角上）。" +
        "种子目标由宿主解析：优先当前唯一选中的实体（geometry）或特征（feature），" +
        "空选区时从最近创建对象或部件清单重新识别；模型不传对象引用。" +
        "datum_axis/line_edge 模式下用户必须已选中对应的轴对象（可与种子同时选中：宿主自动区分实体/特征与轴对象）。" +
        "确认卡会显示模式、种子、旋转轴、数量与角度范围。仅当用户明确要求环形/圆形阵列时调用；调用后需要用户确认才会执行。";

    public const string ParametersJsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["mode", "axis", "count"],
          "properties": {
            "mode": {
              "type": "string",
              "enum": ["feature", "geometry"],
              "description": "阵列模式：feature=阵列已有建模特征（如孔特征）；geometry=阵列实体/几何体。"
            },
            "axis": {
              "type": "string",
              "enum": ["plus_z", "plus_x", "plus_y", "datum_axis", "line_edge"],
              "description": "旋转轴。plus_x/plus_y/plus_z=过原点的绝对坐标轴；datum_axis=当前选中的基准轴；line_edge=当前选中的直线（实体上的拓扑直线边，或独立的直线特征/直线对象）。无法确定时先向用户澄清。"
            },
            "count": {
              "type": "integer",
              "minimum": 2,
              "maximum": 100,
              "description": "总实例数（含原始实例）。完整圆周时相邻实例角距 = 360/count 度。"
            },
            "total_angle_deg": {
              "type": "number",
              "exclusiveMinimum": 0,
              "maximum": 360,
              "description": "可选。阵列总角度（度）。省略 = 完整圆周 360 度。部分圆弧时最后一个实例落在结束角上。"
            }
          }
        }
        """;

    public static readonly CreateCircularPatternRequestParser Parser = new();
}
