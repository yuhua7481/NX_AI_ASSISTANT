using System.Text.Json;

namespace NXAI.Assistant.Core;

/// <summary>Revolve axis modes. All are explicit — no silent fallback.</summary>
public enum RevolveAxisMode
{
    /// <summary>A single linear object the user selected in NX (datum axis,
    /// straight edge or sketch line). Resolved at execution time.</summary>
    SelectedAxis,

    /// <summary>Work-part absolute coordinate axes through the absolute origin.</summary>
    WcsX,
    WcsY,
    WcsZ,
}

/// <summary>
/// Accepted parameters for the create_revolve tool. SketchRef is a controlled
/// reference previously returned by create_sketch. AngleDeg is the sweep
/// angle in degrees, (0, 360].
/// </summary>
public sealed record CreateRevolveParameters(
    string SketchRef,
    RevolveAxisMode AxisMode,
    double AngleDeg);

/// <summary>
/// Parses and validates create_revolve arguments: a non-empty sketch
/// reference, an explicit axis mode and a sweep angle in degrees within
/// (0, 360]. No unit field — the angle is always degrees, never a length.
/// </summary>
public sealed class CreateRevolveRequestParser : IToolRequestParser
{
    public const string ToolName = "create_revolve";
    private static readonly HashSet<string> KnownFields = new()
    {
        "sketch_ref", "axis_mode", "angle",
    };

    public CreateRevolveParameters? ParseParameters(string argumentsJson, out string? error)
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
            error = "create_revolve 参数不是合法 JSON。";
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "create_revolve 参数必须是 JSON 对象。";
            return null;
        }

        error = GeometryToolJson.RejectUnknownFields(root, ToolName, KnownFields);
        if (error != null)
            return null;

        if (!root.TryGetProperty("sketch_ref", out var refElement) ||
            refElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(refElement.GetString()))
        {
            error = "缺少必填参数：sketch_ref（必须是 create_sketch 返回的草图引用）。";
            return null;
        }
        string sketchRef = refElement.GetString()!.Trim();

        if (!TryReadEnum(root, "axis_mode",
                new Dictionary<string, RevolveAxisMode>
                {
                    ["selected_axis"] = RevolveAxisMode.SelectedAxis,
                    ["wcs_x"] = RevolveAxisMode.WcsX,
                    ["wcs_y"] = RevolveAxisMode.WcsY,
                    ["wcs_z"] = RevolveAxisMode.WcsZ,
                }, out var axisMode, out error))
            return null;

        if (!GeometryToolJson.TryReadFiniteNumber(root, "angle", out double angle, out error))
            return null;
        error = null;

        if (!double.IsFinite(angle) || angle <= 0 || angle > 360)
        {
            error = $"angle 必须在 (0, 360] 度范围内，当前值：{angle}。";
            return null;
        }

        return new CreateRevolveParameters(sketchRef, axisMode, angle);
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
/// The create_revolve tool contract: revolves an existing sketch around an
/// explicit axis to build a solid of revolution (CAD revolve feature, not a
/// spatial transform of an existing body).
/// </summary>
public static class CreateRevolveTool
{
    public const string Name = CreateRevolveRequestParser.ToolName;

    public const string ParametersJsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["sketch_ref", "axis_mode", "angle"],
          "properties": {
            "sketch_ref": {
              "type": "string",
              "description": "要旋转的草图引用（create_sketch 工具返回的 sketchRef），不能自己编造。"
            },
            "axis_mode": {
              "type": "string",
              "enum": ["selected_axis", "wcs_x", "wcs_y", "wcs_z"],
              "description": "旋转轴。selected_axis：用户当前选中的单个直线型对象（基准轴/直边/草图直线，必须恰好一个）；wcs_x/wcs_y/wcs_z：工作部件绝对坐标轴。轴必须位于草图平面内，否则执行会被拒绝。"
            },
            "angle": {
              "type": "number",
              "exclusiveMinimum": 0,
              "maximum": 360,
              "description": "旋转角度，单位度，范围 (0, 360]。360 为完整旋转体。"
            }
          }
        }
        """;

    public const string Description =
        "将一个已创建的草图绕指定轴旋转生成旋转体（Revolve 特征）。" +
        "需要 sketch_ref（create_sketch 返回的草图引用）、axis_mode（selected_axis=用户选中的单个直线对象，或 wcs_x/wcs_y/wcs_z 绝对坐标轴）与 angle（角度，0-360 度）。" +
        "轴必须位于草图平面内；轮廓与轴相交可能产生无效几何，会被安全拒绝。" +
        "仅当用户明确要求创建旋转体时调用；调用后需要用户在界面上确认才会执行。";
}
