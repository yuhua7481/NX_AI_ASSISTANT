using System.Text.Json;

namespace NXAI.Assistant.Core;

/// <summary>
/// Accepted parameters for the create_hole tool (Phase 9.1 minimum: SIMPLE
/// blind hole only — no counterbore/countersink, no thru hole, no RPO
/// positioning dimensions). The hole is placed on a user-selected planar
/// FACE at a UV offset from that face's parameter midpoint, axis along the
/// face normal (into the material). Linear values are millimeters after
/// unit normalization.
/// </summary>
public sealed record CreateHoleParameters(
    string PlacementFaceRef,
    double LocationU,
    double LocationV,
    double Diameter,
    double Depth,
    double TipAngleDeg);

/// <summary>
/// Parses and validates create_hole arguments. Rejects invalid JSON,
/// unknown fields, a missing/empty placement face reference, non-finite or
/// non-positive diameter/depth, and tip angles outside (0, 180]. unit is
/// required for linear values (same rules as the other geometry tools).
/// </summary>
public sealed class CreateHoleRequestParser : IToolRequestParser
{
    public const string ToolName = "create_hole";
    private static readonly HashSet<string> KnownFields = new()
    {
        "placement_face_ref", "location_u", "location_v",
        "diameter", "depth", "tip_angle_deg", "unit",
    };

    public CreateHoleParameters? ParseParameters(string argumentsJson, out string? error)
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
            error = "create_hole 参数不是合法 JSON。";
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "create_hole 参数必须是 JSON 对象。";
            return null;
        }

        error = GeometryToolJson.RejectUnknownFields(root, ToolName, KnownFields);
        if (error != null)
            return null;

        // Placement face reference: REQUIRED — the hole is always placed on
        // a face the host can resolve. Never guessed, never defaulted.
        if (!root.TryGetProperty("placement_face_ref", out var faceEl) ||
            faceEl.ValueKind != JsonValueKind.String)
        {
            error = "缺少必填参数：placement_face_ref（目标放置面的实体引用，来自上下文中该面的 Name 字段）。";
            return null;
        }
        string faceRef = faceEl.GetString()!.Trim();
        if (faceRef.Length == 0)
        {
            error = "placement_face_ref 不能为空。";
            return null;
        }

        string? unitError = GeometryToolJson.ReadUnit(root, out _, out double factor);
        if (unitError != null)
        {
            error = unitError;
            return null;
        }

        if (!GeometryToolJson.TryReadFiniteNumber(root, "diameter", out double diameter, out error))
            return null;
        if (diameter <= 0)
        {
            error = $"孔直径必须为正（收到 {diameter}）。";
            return null;
        }
        if (!GeometryToolJson.TryReadFiniteNumber(root, "depth", out double depth, out error))
            return null;
        if (depth <= 0)
        {
            error = $"孔深度必须为正（收到 {depth}）。盲孔需要明确深度；通孔暂不支持。";
            return null;
        }

        // Tip angle: optional, default 118 (standard drill point).
        double tipAngle = 118;
        if (root.TryGetProperty("tip_angle_deg", out var tipEl))
        {
            if (!GeometryToolJson.TryReadFiniteNumber(root, "tip_angle_deg", out tipAngle, out error))
                return null;
            if (tipAngle <= 0 || tipAngle >= 180)
            {
                error = $"刀尖角必须在 0–180 度开区间内（收到 {tipAngle}）。";
                return null;
            }
        }

        double locU = 0, locV = 0;
        if (root.TryGetProperty("location_u", out _))
        {
            if (!GeometryToolJson.TryReadFiniteNumber(root, "location_u", out locU, out error))
                return null;
        }
        if (root.TryGetProperty("location_v", out _))
        {
            if (!GeometryToolJson.TryReadFiniteNumber(root, "location_v", out locV, out error))
                return null;
        }
        error = null;

        return new CreateHoleParameters(
            faceRef,
            locU * factor, locV * factor,
            diameter * factor, depth * factor,
            tipAngle);
    }

    object? IToolRequestParser.Parse(string argumentsJson, out string? error) =>
        ParseParameters(argumentsJson, out error);
}

/// <summary>
/// The create_hole tool contract: a SIMPLE blind hole on a user-selected
/// planar face. The schema is the authoritative field list the parser
/// enforces — keep in sync.
/// </summary>
public static class CreateHoleTool
{
    public const string Name = CreateHoleRequestParser.ToolName;

    public const string ParametersJsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["placement_face_ref", "diameter", "depth", "unit"],
          "properties": {
            "placement_face_ref": {
              "type": "string",
              "description": "孔的放置面引用：必须是当前 NX 上下文中目标平面的真实引用（如 FACE 1 {(0,100,30) BLOCK(2)} 的 Name 字段）。请先让用户选中该面再调用；引用无效会被拒绝。"
            },
            "location_u": {
              "type": "number",
              "description": "可选。孔心相对放置面中心沿面 U 方向的偏移，按 unit 指定的单位，省略时为 0（面中心）。这是面局部坐标，不是世界坐标。"
            },
            "location_v": {
              "type": "number",
              "description": "可选。孔心相对放置面中心沿面 V 方向的偏移，按 unit 指定的单位，省略时为 0。"
            },
            "diameter": {
              "type": "number",
              "exclusiveMinimum": 0,
              "description": "孔直径，按 unit 指定的单位。"
            },
            "depth": {
              "type": "number",
              "exclusiveMinimum": 0,
              "description": "孔深度（从放置面沿法向向内），按 unit 指定的单位。盲孔；通孔暂不支持。"
            },
            "tip_angle_deg": {
              "type": "number",
              "description": "可选。钻尖角（度），省略时为 118（标准钻尖）。必须在 0–180 开区间内。"
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
        "在用户选中的平面上创建一个简单盲孔（Simple Hole，圆柱孔 + 标准钻尖）。" +
        "需要 placement_face_ref（上下文中目标面的真实引用——请先让用户选中该面）、diameter 与 depth；" +
        "可选 location_u/location_v 指定孔心相对面中心的偏移（面局部坐标），可选 tip_angle_deg（默认 118）。" +
        "不支持沉头/沉孔/通孔/定位尺寸。仅当用户明确要求打孔且目标面已选定时调用；调用后需要用户在界面上确认才会执行。";
}
