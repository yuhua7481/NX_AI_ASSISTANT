using System.Text.Json;

namespace NXAI.Assistant.Core;

/// <summary>Cylinder placement modes. GlobalOrigin is the legacy behavior.</summary>
public enum CylinderPlacement
{
    /// <summary>Legacy: origin from parameters (default 0,0,0), axis +Z.</summary>
    GlobalOrigin,

    /// <summary>Face-relative: bottom circle centered on the selected planar
    /// face, axis along the face's outward normal. Resolved from the captured
    /// NX context — never from model-supplied coordinates.</summary>
    SelectedFaceCenter,
}

/// <summary>
/// Accepted parameters for the create_cylinder tool. All linear values are in
/// millimeters (the tool contract converts the model's unit field itself);
/// origin (bottom-face center) defaults to (0, 0, 0) when the model omits it;
/// the axis is the work part's +Z direction (builder default, not model-chosen).
/// Placement defaults to GlobalOrigin (legacy behavior, unchanged).
/// </summary>
public sealed record CreateCylinderParameters(
    double OriginX, double OriginY, double OriginZ,
    double Diameter, double Height,
    string Unit,
    CylinderPlacement Placement = CylinderPlacement.GlobalOrigin);

/// <summary>
/// Parses and validates create_cylinder tool arguments. Same field rules as
/// create_block: rejects invalid JSON, unknown fields, wrong types, missing
/// fields, non-finite numbers, non-positive values, and out-of-bounds values
/// (reusing BlockRequestValidator's geometry bounds).
/// </summary>
public sealed class CreateCylinderRequestParser : IToolRequestParser
{
    public const string ToolName = "create_cylinder";
    private static readonly HashSet<string> KnownFields = new()
    {
        "diameter", "height", "unit", "origin_x", "origin_y", "origin_z", "placement",
    };

    public CreateCylinderParameters? ParseParameters(string argumentsJson, out string? error)
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
            error = "create_cylinder 参数不是合法 JSON。";
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "create_cylinder 参数必须是 JSON 对象。";
            return null;
        }

        error = GeometryToolJson.RejectUnknownFields(root, ToolName, KnownFields);
        if (error != null)
            return null;

        if (!GeometryToolJson.TryReadFiniteNumber(root, "diameter", out double diameter, out error))
            return null;
        if (!GeometryToolJson.TryReadFiniteNumber(root, "height", out double height, out error))
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

        // Optional placement mode. Anything other than the two known values
        // is rejected — the model cannot invent modes.
        var placement = CylinderPlacement.GlobalOrigin;
        if (root.TryGetProperty("placement", out var placementElement))
        {
            if (placementElement.ValueKind != JsonValueKind.String)
            {
                error = "placement 必须是字符串。";
                return null;
            }
            var placementName = placementElement.GetString();
            if (placementName == "global_origin")
            {
                placement = CylinderPlacement.GlobalOrigin;
            }
            else if (placementName == "selected_face_center")
            {
                placement = CylinderPlacement.SelectedFaceCenter;
            }
            else
            {
                error = $"不支持的放置模式：{placementName}。支持 global_origin（默认）和 selected_face_center。";
                return null;
            }
        }

        var parameters = new CreateCylinderParameters(
            originX * factor, originY * factor, originZ * factor,
            diameter * factor, height * factor, "mm", placement);

        // Diameter constrains both X and Y extents; reuse the block bounds so
        // the cylinder can never exceed the same sanity envelope. In face mode
        // the origin values are ignored downstream (the face decides), but the
        // request itself must still pass the same sanity gate.
        error = BlockRequestValidator.ValidateWorkingPartRequest(
            parameters.OriginX, parameters.OriginY, parameters.OriginZ,
            parameters.Diameter, parameters.Diameter, parameters.Height);
        if (error != null)
            return null;

        return parameters;
    }

    object? IToolRequestParser.Parse(string argumentsJson, out string? error) =>
        ParseParameters(argumentsJson, out error);
}
