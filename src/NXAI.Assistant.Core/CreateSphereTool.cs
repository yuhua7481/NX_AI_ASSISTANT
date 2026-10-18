using System.Text.Json;

namespace NXAI.Assistant.Core;

/// <summary>
/// Accepted parameters for the create_sphere tool. All linear values are in
/// millimeters; origin (sphere center) defaults to (0, 0, 0).
/// </summary>
public sealed record CreateSphereParameters(
    double CenterX, double CenterY, double CenterZ,
    double Diameter,
    string Unit);

/// <summary>
/// Parses and validates create_sphere tool arguments. Same rules as the
/// other geometry tools; geometry bounds reuse BlockRequestValidator (the
/// diameter constrains all three bounding-box extents).
/// </summary>
public sealed class CreateSphereRequestParser : IToolRequestParser
{
    public const string ToolName = "create_sphere";
    private static readonly HashSet<string> KnownFields = new()
    {
        "diameter", "unit", "origin_x", "origin_y", "origin_z",
    };

    public CreateSphereParameters? ParseParameters(string argumentsJson, out string? error)
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
            error = "create_sphere 参数不是合法 JSON。";
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "create_sphere 参数必须是 JSON 对象。";
            return null;
        }

        error = GeometryToolJson.RejectUnknownFields(root, ToolName, KnownFields);
        if (error != null)
            return null;

        if (!GeometryToolJson.TryReadFiniteNumber(root, "diameter", out double diameter, out error))
            return null;

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

        var parameters = new CreateSphereParameters(
            originX * factor, originY * factor, originZ * factor,
            diameter * factor, "mm");

        error = BlockRequestValidator.ValidateWorkingPartRequest(
            parameters.CenterX, parameters.CenterY, parameters.CenterZ,
            parameters.Diameter, parameters.Diameter, parameters.Diameter);
        if (error != null)
            return null;

        return parameters;
    }

    object? IToolRequestParser.Parse(string argumentsJson, out string? error) =>
        ParseParameters(argumentsJson, out error);
}
