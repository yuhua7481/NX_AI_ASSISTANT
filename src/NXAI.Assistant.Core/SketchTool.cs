using System.Text.Json;

namespace NXAI.Assistant.Core;

/// <summary>Sketch placement plane modes. All are explicit — no guessing.</summary>
public enum SketchPlaneMode
{
    /// <summary>The single planar face or datum plane the user selected in NX.
    /// Resolved from the current selection at execution time — never from
    /// model-supplied coordinates.</summary>
    SelectedPlane,

    /// <summary>Work-part absolute coordinate planes (right-hand orientation:
    /// XY normal +Z, XZ normal -Y, YZ normal +X).</summary>
    WcsXY,
    WcsXZ,
    WcsYZ,
}

/// <summary>
/// Sketch geometry types. Rectangle/Circle are closed profiles; Line and
/// Arc are OPEN curves — a sketch holding only open curves cannot feed
/// extrude/revolve as a closed section, and results must say so.
/// </summary>
public enum SketchProfileType
{
    Rectangle,
    Circle,
    Line,
    Arc,
}

/// <summary>
/// Accepted parameters for the create_sketch tool. CenterU/CenterV are the
/// profile center offset in the sketch plane's LOCAL coordinates (u/v axes
/// chosen deterministically by the executor) — never world coordinates.
/// Linear values are millimeters after unit normalization. Line uses
/// StartU/StartV/EndU/EndV (local); Arc uses CenterU/CenterV + Radius +
/// StartAngleDeg/EndAngleDeg (degrees, 0° = local +U, counterclockwise,
/// 0 ≤ start_angle_deg, end_angle_deg ≤ 360, start lower than end).
/// </summary>
public sealed record CreateSketchParameters(
    SketchPlaneMode PlaneMode,
    SketchProfileType ProfileType,
    double RectangleWidth,
    double RectangleHeight,
    double CircleDiameter,
    double CenterU,
    double CenterV,
    double StartU = 0,
    double StartV = 0,
    double EndU = 0,
    double EndV = 0,
    double Radius = 0,
    double StartAngleDeg = 0,
    double EndAngleDeg = 0);

/// <summary>
/// Parses and validates create_sketch arguments. Rejects invalid JSON,
/// unknown fields, missing plane_mode/profile_type, unsupported enum values,
/// non-finite and non-positive sizes. unit is required for linear values
/// (same rules as the other geometry tools).
/// </summary>
public sealed class CreateSketchRequestParser : IToolRequestParser
{
    public const string ToolName = "create_sketch";
    private static readonly HashSet<string> KnownFields = new()
    {
        "plane_mode", "profile_type", "width", "height", "diameter",
        "center_u", "center_v", "unit",
        "start_u", "start_v", "end_u", "end_v",
        "radius", "start_angle_deg", "end_angle_deg",
    };

    public CreateSketchParameters? ParseParameters(string argumentsJson, out string? error)
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
            error = "create_sketch 参数不是合法 JSON。";
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "create_sketch 参数必须是 JSON 对象。";
            return null;
        }

        error = GeometryToolJson.RejectUnknownFields(root, ToolName, KnownFields);
        if (error != null)
            return null;

        if (!TryReadEnum<SketchPlaneMode>(root, "plane_mode",
                new Dictionary<string, SketchPlaneMode>
                {
                    ["selected_plane"] = SketchPlaneMode.SelectedPlane,
                    ["wcs_xy"] = SketchPlaneMode.WcsXY,
                    ["wcs_xz"] = SketchPlaneMode.WcsXZ,
                    ["wcs_yz"] = SketchPlaneMode.WcsYZ,
                }, out var planeMode, out error))
            return null;

        if (!TryReadEnum<SketchProfileType>(root, "profile_type",
                new Dictionary<string, SketchProfileType>
                {
                    ["rectangle"] = SketchProfileType.Rectangle,
                    ["circle"] = SketchProfileType.Circle,
                    ["line"] = SketchProfileType.Line,
                    ["arc"] = SketchProfileType.Arc,
                }, out var profileType, out error))
            return null;

        string? unitError = GeometryToolJson.ReadUnit(root, out _, out double factor);
        if (unitError != null)
        {
            error = unitError;
            return null;
        }

        double width = 0, height = 0, diameter = 0;
        if (profileType == SketchProfileType.Rectangle)
        {
            if (!GeometryToolJson.TryReadFiniteNumber(root, "width", out width, out error))
                return null;
            if (!GeometryToolJson.TryReadFiniteNumber(root, "height", out height, out error))
                return null;
        }
        else if (profileType == SketchProfileType.Circle)
        {
            if (!GeometryToolJson.TryReadFiniteNumber(root, "diameter", out diameter, out error))
                return null;
        }

        double centerU = 0, centerV = 0;
        if (root.TryGetProperty("center_u", out _))
        {
            if (!GeometryToolJson.TryReadFiniteNumber(root, "center_u", out centerU, out error))
                return null;
        }
        if (root.TryGetProperty("center_v", out _))
        {
            if (!GeometryToolJson.TryReadFiniteNumber(root, "center_v", out centerV, out error))
                return null;
        }

        // Line endpoints (required for line, absent otherwise).
        double startU = 0, startV = 0, endU = 0, endV = 0;
        if (profileType == SketchProfileType.Line)
        {
            if (!GeometryToolJson.TryReadFiniteNumber(root, "start_u", out startU, out error))
                return null;
            if (!GeometryToolJson.TryReadFiniteNumber(root, "start_v", out startV, out error))
                return null;
            if (!GeometryToolJson.TryReadFiniteNumber(root, "end_u", out endU, out error))
                return null;
            if (!GeometryToolJson.TryReadFiniteNumber(root, "end_v", out endV, out error))
                return null;
            if (startU == endU && startV == endV)
            {
                error = "起点与终点重合，无法创建直线段。请提供两个不同的端点。";
                return null;
            }
        }

        // Arc parameters (required for arc, absent otherwise).
        double radius = 0, startAngleDeg = 0, endAngleDeg = 0;
        if (profileType == SketchProfileType.Arc)
        {
            if (!GeometryToolJson.TryReadFiniteNumber(root, "radius", out radius, out error))
                return null;
            if (radius <= 0)
            {
                error = $"圆弧半径必须为正（收到 {radius}）。";
                return null;
            }
            if (!GeometryToolJson.TryReadFiniteNumber(root, "start_angle_deg", out startAngleDeg, out error))
                return null;
            if (!GeometryToolJson.TryReadFiniteNumber(root, "end_angle_deg", out endAngleDeg, out error))
                return null;
            if (startAngleDeg < 0 || endAngleDeg > 360 || startAngleDeg >= endAngleDeg)
            {
                error = $"圆弧角度范围无效：要求 0 ≤ start_angle_deg < end_angle_deg ≤ 360" +
                        $"（收到 start={startAngleDeg}, end={endAngleDeg}）。";
                return null;
            }
        }
        error = null;

        var parameters = new CreateSketchParameters(
            planeMode, profileType,
            width * factor, height * factor, diameter * factor,
            centerU * factor, centerV * factor,
            startU * factor, startV * factor, endU * factor, endV * factor,
            radius * factor, startAngleDeg, endAngleDeg);

        // Sanity bounds: the profile must fit the same envelope as a block of
        // the same extent, keeping every tool behind one shared sanity gate.
        double maxAbs = new[] { parameters.CenterU, parameters.CenterV, parameters.StartU, parameters.StartV, parameters.EndU, parameters.EndV }
            .Select(Math.Abs).DefaultIfEmpty(0).Max();
        double exWidth = profileType switch
        {
            SketchProfileType.Rectangle => parameters.RectangleWidth,
            SketchProfileType.Circle => parameters.CircleDiameter,
            SketchProfileType.Arc => parameters.Radius * 2,
            _ => Math.Abs(parameters.EndU - parameters.StartU),
        };
        double exHeight = profileType switch
        {
            SketchProfileType.Rectangle => parameters.RectangleHeight,
            SketchProfileType.Circle => parameters.CircleDiameter,
            SketchProfileType.Arc => parameters.Radius * 2,
            _ => Math.Abs(parameters.EndV - parameters.StartV),
        };
        error = BlockRequestValidator.ValidateWorkingPartRequest(
            parameters.ProfileType == SketchProfileType.Line ? 0 : parameters.CenterU,
            parameters.ProfileType == SketchProfileType.Line ? 0 : parameters.CenterV,
            0, Math.Max(exWidth, 2 * maxAbs) + (profileType == SketchProfileType.Arc ? 2 * parameters.Radius : 0),
            Math.Max(exHeight, 2 * maxAbs) + (profileType == SketchProfileType.Arc ? 2 * parameters.Radius : 0), 1);
        if (error != null)
            return null;

        return parameters;
    }

    object? IToolRequestParser.Parse(string argumentsJson, out string? error) =>
        ParseParameters(argumentsJson, out error);

    private static bool TryReadEnum<T>(
        JsonElement root, string name, Dictionary<string, T> values,
        out T result, out string? error)
        where T : struct
    {
        result = default;
        error = null;
        if (!root.TryGetProperty(name, out var element))
        {
            error = $"缺少必填参数：{name}。";
            return false;
        }
        if (element.ValueKind != JsonValueKind.String)
        {
            error = $"{name} 必须是字符串。";
            return false;
        }
        var text = element.GetString()!.Trim().ToLowerInvariant();
        if (!values.TryGetValue(text, out result))
        {
            error = $"不支持的 {name} 值：{text}。支持 {string.Join("、", values.Keys)}。";
            return false;
        }
        return true;
    }
}

/// <summary>
/// The create_sketch tool contract: creates a real parametric NX sketch with
/// one closed profile (rectangle or circle) on an explicit plane. The schema
/// is the authoritative field list the parser enforces — keep in sync.
/// </summary>
public static class CreateSketchTool
{
    public const string Name = CreateSketchRequestParser.ToolName;

    public const string ParametersJsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["plane_mode", "profile_type", "unit"],
          "properties": {
            "plane_mode": {
              "type": "string",
              "enum": ["selected_plane", "wcs_xy", "wcs_xz", "wcs_yz"],
              "description": "草图放置平面。selected_plane：用户当前选中的单个平面或基准面（必须恰好一个）；wcs_xy/wcs_xz/wcs_yz：工作部件绝对坐标基本平面。"
            },
            "profile_type": {
              "type": "string",
              "enum": ["rectangle", "circle", "line", "arc"],
              "description": "轮廓类型：rectangle=封闭矩形，circle=完整圆，line=单条直线段（开放），arc=圆弧段（开放）。单条 line/arc 不是封闭轮廓，不能直接用于拉伸/旋转。"
            },
            "width": {
              "type": "number",
              "exclusiveMinimum": 0,
              "description": "矩形沿草图局部 u 轴的宽度，按 unit 指定的单位。profile_type=rectangle 时必填。"
            },
            "height": {
              "type": "number",
              "exclusiveMinimum": 0,
              "description": "矩形沿草图局部 v 轴的高度，按 unit 指定的单位。profile_type=rectangle 时必填。"
            },
            "diameter": {
              "type": "number",
              "exclusiveMinimum": 0,
              "description": "圆形直径，按 unit 指定的单位。profile_type=circle 时必填。"
            },
            "start_u": {
              "type": "number",
              "description": "直线起点在草图局部坐标 u 方向的坐标，按 unit 指定的单位。profile_type=line 时必填。"
            },
            "start_v": {
              "type": "number",
              "description": "直线起点在草图局部坐标 v 方向的坐标，按 unit 指定的单位。profile_type=line 时必填。"
            },
            "end_u": {
              "type": "number",
              "description": "直线终点在草图局部坐标 u 方向的坐标，按 unit 指定的单位。profile_type=line 时必填，且不得与起点重合。"
            },
            "end_v": {
              "type": "number",
              "description": "直线终点在草图局部坐标 v 方向的坐标，按 unit 指定的单位。profile_type=line 时必填，且不得与起点重合。"
            },
            "radius": {
              "type": "number",
              "exclusiveMinimum": 0,
              "description": "圆弧半径，按 unit 指定的单位。profile_type=arc 时必填。"
            },
            "start_angle_deg": {
              "type": "number",
              "minimum": 0,
              "description": "圆弧起始角（度）：以草图局部 +u 轴为 0°，在 u/v 平面内逆时针为正。profile_type=arc 时必填，要求 0 ≤ start_angle_deg < end_angle_deg ≤ 360。"
            },
            "end_angle_deg": {
              "type": "number",
              "maximum": 360,
              "description": "圆弧结束角（度）：以草图局部 +u 轴为 0°，逆时针为正。profile_type=arc 时必填，必须大于 start_angle_deg。"
            },
            "center_u": {
              "type": "number",
              "description": "可选。轮廓中心在草图平面局部坐标 u 方向的偏移，按 unit 指定的单位，省略时为 0。这是草图局部坐标，不是世界坐标。"
            },
            "center_v": {
              "type": "number",
              "description": "可选。轮廓中心在草图平面局部坐标 v 方向的偏移，按 unit 指定的单位，省略时为 0。这是草图局部坐标，不是世界坐标。"
            },
            "unit": {
              "type": "string",
              "enum": ["mm", "cm", "m", "inch"],
              "description": "尺寸单位。执行时会统一换算为毫米。"
            }
          }
        }
        """;

    public const string Description =
        "在指定平面上创建一个真实的 NX 参数化草图，包含一个几何元素：封闭轮廓（矩形或圆形）或开放曲线（单条直线、单条圆弧）。" +
        "需要 plane_mode（selected_plane=用户选中的单个平面/基准面，或 wcs_xy/wcs_xz/wcs_yz 基本平面）、profile_type 与 unit；" +
        "矩形需要 width/height，圆形需要 diameter；直线需要 start_u/start_v/end_u/end_v（草图局部坐标，起终点不得重合）；" +
        "圆弧需要 radius 与 start_angle_deg/end_angle_deg（0°=局部 +u 方向，逆时针为正，0 ≤ start < end ≤ 360），可选 center_u/center_v 指定圆心。" +
        "创建成功后返回草图引用（sketchRef）。注意：单条直线或圆弧不是封闭轮廓，不能直接拉伸/旋转成实体；需要实体时请创建封闭轮廓。" +
        "仅当用户明确要求创建草图时调用；调用后需要用户在界面上确认才会执行。";
}
