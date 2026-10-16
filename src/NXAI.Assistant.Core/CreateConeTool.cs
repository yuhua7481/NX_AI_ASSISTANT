using System.Text.Json;

namespace NXAI.Assistant.Core;

/// <summary>
/// Accepted parameters for the create_cone tool. All linear values are in
/// millimeters; origin (bottom-face center) defaults to (0, 0, 0); the axis
/// is the work part's +Z direction (builder default, not model-chosen).
/// TopDiameter = 0 means a right circular cone with the apex on the axis;
/// a positive TopDiameter below BaseDiameter means a truncated cone (frustum).
/// </summary>
public sealed record CreateConeParameters(
    double OriginX, double OriginY, double OriginZ,
    double BaseDiameter, double TopDiameter, double Height,
    string Unit);

/// <summary>
/// Parses and validates create_cone tool arguments. Same rules as the other
/// geometry tools; geometry bounds reuse BlockRequestValidator (base diameter
/// constrains X/Y, height constrains Z).
/// </summary>
public sealed class CreateConeRequestParser : IToolRequestParser
{
    public const string ToolName = "create_cone";
    private static readonly HashSet<string> KnownFields = new()
    {
        "base_diameter", "top_diameter", "height", "unit", "origin_x", "origin_y", "origin_z",
    };

    public CreateConeParameters? ParseParameters(string argumentsJson, out string? error)
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
            error = "create_cone 参数不是合法 JSON。";
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "create_cone 参数必须是 JSON 对象。";
            return null;
        }

        error = GeometryToolJson.RejectUnknownFields(root, ToolName, KnownFields);
        if (error != null)
            return null;

        if (!GeometryToolJson.TryReadFiniteNumber(root, "base_diameter", out double baseDiameter, out error))
            return null;
        if (!GeometryToolJson.TryReadFiniteNumber(root, "height", out double height, out error))
            return null;

        // Optional top diameter: absent or 0 → standard cone; otherwise a
        // frustum with 0 < top < base.
        double topDiameter = 0;
        if (root.TryGetProperty("top_diameter", out _))
        {
            if (!GeometryToolJson.TryReadFiniteNumber(root, "top_diameter", out double topValue, out error))
                return null;
            if (!double.IsFinite(topValue))
            {
                error = "top_diameter 必须是有限数字。";
                return null;
            }
            if (topValue < 0)
            {
                error = "top_diameter 不能为负数。";
                return null;
            }
            topDiameter = topValue;
        }

        string? unitError = GeometryToolJson.ReadUnit(root, out string unit, out double factor);
        if (unitError != null)
        {
            error = unitError;
            return null;
        }

        double originX = 0, originY = 0, originZ = 0;
        foreach (var axis in new[] { "origin_x", "origin_y", "origin_z" })
        {
            if (root.TryGetProperty(axis, out _))
            {
                if (!GeometryToolJson.TryReadFiniteNumber(root, axis, out double value, out error))
                    return null;
                switch (axis)
                {
                    case "origin_x": originX = value; break;
                    case "origin_y": originY = value; break;
                    case "origin_z": originZ = value; break;
                }
            }
        }
        error = null;

        var parameters = new CreateConeParameters(
            originX * factor, originY * factor, originZ * factor,
            baseDiameter * factor, topDiameter * factor, height * factor, "mm");

        if (parameters.TopDiameter >= parameters.BaseDiameter)
        {
            error = $"top_diameter（{parameters.TopDiameter} 毫米）必须小于 base_diameter" +
                    $"（{parameters.BaseDiameter} 毫米）；top_diameter = 0 表示标准圆锥。";
            return null;
        }

        error = BlockRequestValidator.ValidateWorkingPartRequest(
            parameters.OriginX, parameters.OriginY, parameters.OriginZ,
            parameters.BaseDiameter, parameters.BaseDiameter, parameters.Height);
        if (error != null)
            return null;

        return parameters;
    }

    object? IToolRequestParser.Parse(string argumentsJson, out string? error) =>
        ParseParameters(argumentsJson, out error);
}
