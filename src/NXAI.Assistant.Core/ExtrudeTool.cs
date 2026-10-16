using System.Text.Json;

namespace NXAI.Assistant.Core;

/// <summary>Extrude direction along the sketch plane normal.</summary>
public enum ExtrudeDirection
{
    /// <summary>Along the sketch plane's normal (default).</summary>
    Normal,

    /// <summary>Opposite to the sketch plane's normal.</summary>
    Reverse,
}

/// <summary>
/// Accepted parameters for the create_extrude tool. SketchRef is a controlled
/// reference previously returned by create_sketch (NXHost-managed; never an
/// NX tag or object address supplied by the model). Distance is millimeters
/// after unit normalization.
/// </summary>
public sealed record CreateExtrudeParameters(
    string SketchRef,
    double Distance,
    ExtrudeDirection Direction);

/// <summary>
/// Parses and validates create_extrude arguments: a non-empty sketch
/// reference, a positive finite distance, an optional direction
/// (normal|reverse, default normal) and the unit field.
/// </summary>
public sealed class CreateExtrudeRequestParser : IToolRequestParser
{
    public const string ToolName = "create_extrude";
    private static readonly HashSet<string> KnownFields = new()
    {
        "sketch_ref", "distance", "direction", "unit",
    };

    public CreateExtrudeParameters? ParseParameters(string argumentsJson, out string? error)
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
            error = "create_extrude 参数不是合法 JSON。";
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "create_extrude 参数必须是 JSON 对象。";
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

        if (!GeometryToolJson.TryReadFiniteNumber(root, "distance", out double distance, out error))
            return null;
        if (!double.IsFinite(distance) || distance <= 0)
        {
            error = $"distance 必须为正数（当前 {distance}）。";
            return null;
        }

        string? unitError = GeometryToolJson.ReadUnit(root, out _, out double factor);
        if (unitError != null)
        {
            error = unitError;
            return null;
        }

        var direction = ExtrudeDirection.Normal;
        if (root.TryGetProperty("direction", out var directionElement))
        {
            if (directionElement.ValueKind != JsonValueKind.String)
            {
                error = "direction 必须是字符串。";
                return null;
            }
            var directionName = directionElement.GetString()!.Trim().ToLowerInvariant();
            if (directionName == "normal")
                direction = ExtrudeDirection.Normal;
            else if (directionName == "reverse")
                direction = ExtrudeDirection.Reverse;
            else
            {
                error = $"不支持的 direction 值：{directionName}。支持 normal（默认，沿草图法向）和 reverse（反向）。";
                return null;
            }
        }
        error = null;

        var parameters = new CreateExtrudeParameters(
            sketchRef, distance * factor, direction);

        error = BlockRequestValidator.ValidateWorkingPartRequest(
            0, 0, 0, parameters.Distance, parameters.Distance, parameters.Distance);
        if (error != null)
            return null;

        return parameters;
    }

    object? IToolRequestParser.Parse(string argumentsJson, out string? error) =>
        ParseParameters(argumentsJson, out error);
}

/// <summary>
/// The create_extrude tool contract: extrudes an existing sketch created by
/// create_sketch into a solid along the sketch normal.
/// </summary>
public static class CreateExtrudeTool
{
    public const string Name = CreateExtrudeRequestParser.ToolName;

    public const string ParametersJsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["sketch_ref", "distance", "unit"],
          "properties": {
            "sketch_ref": {
              "type": "string",
              "description": "要拉伸的草图引用（create_sketch 工具返回的 sketchRef），不能自己编造。"
            },
            "distance": {
              "type": "number",
              "exclusiveMinimum": 0,
              "description": "拉伸距离，按 unit 指定的单位。"
            },
            "direction": {
              "type": "string",
              "enum": ["normal", "reverse"],
              "description": "可选。拉伸方向：normal（默认）沿草图平面法向；reverse 沿法向反方向。"
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
        "将一个已创建的草图沿草图平面法向拉伸为实体。" +
        "需要 sketch_ref（create_sketch 返回的草图引用）、distance 与 unit；可选 direction（normal=默认沿法向，reverse=反向）。" +
        "仅当用户明确要求拉伸时调用；调用后需要用户在界面上确认才会执行。";
}
