using System.Text.Json;

namespace NXAI.Assistant.Core;

/// <summary>How the hole center is determined. selected_face_center: the
/// user selected one planar face, the center is that face's parameter
/// midpoint (the original behavior). selected_point: the user selected one
/// resolvable POINT; the center is that point's true model position and the
/// placement face is the unique solid planar face containing it (verified
/// host-side from the LIVE selection — the model never supplies
/// coordinates).</summary>
public enum StandardHolePlacementMode
{
    SelectedFaceCenter,
    SelectedPoint,
}

/// <summary>
/// Accepted parameters for the four standard-hole tools (threaded / simple
/// screw clearance / counterbored / countersunk). The hole is placed on a
/// user-selected planar FACE (center mode, UV offset from the face's
/// parameter midpoint) or at a user-selected POINT (point mode; the host
/// re-resolves the point and its containing solid face from the live NX
/// selection). Axis along the face normal (into the material) in both
/// modes. All sizes and dimensions come from the NX standard tables — the
/// model supplies the family + size + placement, never diameters. Depths
/// (threaded + simple families) are user intent; the
/// counterbore/countersink specific diameters/depths/angles come from the
/// standard record only.
/// </summary>
public sealed record CreateStandardHoleParameters(
    StandardHoleFamily Family,
    string Size,
    string PlacementFaceRef,
    double LocationU,
    double LocationV,
    double? HoleDepthMm,
    double? ThreadDepthMm,
    StandardHolePlacementMode PlacementMode = StandardHolePlacementMode.SelectedFaceCenter);

/// <summary>
/// Shared parser for the four standard-hole tools. Accepts the size as
/// M12…M1.6 (any letter case), a required placement face reference, optional
/// face-local UV offsets, and — for threaded holes — optional hole/thread
/// depths that must relate sensibly (thread ≤ hole). Threaded depth rules:
/// omitted → the standard record's reference depths (HoleDepth/ThreadDepth)
/// are used by the host; explicit values override, both-or-none (a thread
/// depth without a hole depth is rejected — the hole must contain the
/// thread). Simple clearance holes take an optional hole depth; the
/// counterbored/countersunk families do not accept depth overrides (head
/// geometry is standard-defined; blind/thru follows the host's depth
/// option).
/// </summary>
public sealed class CreateStandardHoleRequestParser : IToolRequestParser
{
    private readonly string _toolName;
    private readonly StandardHoleFamily _family;

    public CreateStandardHoleRequestParser(string toolName, StandardHoleFamily family)
    {
        _toolName = toolName;
        _family = family;
    }

    private static readonly HashSet<string> KnownFields = new()
    {
        "size", "placement_face_ref", "location_u", "location_v",
        "hole_depth", "thread_depth", "unit", "placement_mode",
    };

    public CreateStandardHoleParameters? ParseParameters(string argumentsJson, out string? error)
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
            error = $"{_toolName} 参数不是合法 JSON。";
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = $"{_toolName} 参数必须是 JSON 对象。";
            return null;
        }

        error = GeometryToolJson.RejectUnknownFields(root, _toolName, KnownFields);
        if (error != null)
            return null;

        // Size: required, any letter case, must be one of the nine supported
        // metric coarse sizes (validated against the parser's own list so
        // obviously-wrong sizes are refused before the table is consulted).
        if (!root.TryGetProperty("size", out var sizeEl) || sizeEl.ValueKind != JsonValueKind.String)
        {
            error = "缺少必填参数：size（孔规格，如 M6）。";
            return null;
        }
        string size = StandardHoleTable.NormalizeSize(sizeEl.GetString() ?? "");
        if (!StandardHoleTable.SupportedSizes.Contains(size))
        {
            error = $"不支持的孔规格：{size}。当前支持：" +
                string.Join("、", StandardHoleTable.SupportedSizes) + "。";
            return null;
        }

        // Placement mode: optional; "selected_point" switches to point
        // placement (the host resolves the point + its containing solid face
        // from the LIVE selection — the model never passes coordinates).
        // Default stays selected_face_center (the original behavior).
        var placementMode = StandardHolePlacementMode.SelectedFaceCenter;
        if (root.TryGetProperty("placement_mode", out var modeEl))
        {
            if (modeEl.ValueKind != JsonValueKind.String)
            {
                error = "placement_mode 必须是字符串（selected_face_center 或 selected_point）。";
                return null;
            }
            string? modeRaw = modeEl.GetString();
            if (modeRaw == "selected_face_center")
                placementMode = StandardHolePlacementMode.SelectedFaceCenter;
            else if (modeRaw == "selected_point")
                placementMode = StandardHolePlacementMode.SelectedPoint;
            else
            {
                error = $"不支持的 placement_mode：{modeRaw}。支持 selected_face_center（面中心）与 selected_point（选定点）。";
                return null;
            }
        }
        bool pointMode = placementMode == StandardHolePlacementMode.SelectedPoint;

        // Placement face reference: required in face-center mode; in point
        // mode it is not applicable (the host finds the containing face) and
        // must be absent or empty — the model must not invent a face ref it
        // was not given.
        string faceRef = "";
        if (root.TryGetProperty("placement_face_ref", out var faceEl) &&
            faceEl.ValueKind == JsonValueKind.String)
        {
            faceRef = faceEl.GetString()!.Trim();
        }
        if (!pointMode && faceRef.Length == 0)
        {
            error = "缺少必填参数：placement_face_ref（目标放置面的实体引用，来自上下文中该面的 Name 字段）。" +
                "若用户选中的是一个点，请改用 placement_mode=\"selected_point\"。";
            return null;
        }
        if (pointMode && faceRef.Length > 0)
        {
            error = "placement_mode=selected_point 时不接受 placement_face_ref：" +
                "放置面由宿主从选定点所在的实体平面自动解析。";
            return null;
        }
        if (pointMode && (root.TryGetProperty("location_u", out _) || root.TryGetProperty("location_v", out _)))
        {
            error = "placement_mode=selected_point 时不支持 location_u/location_v：孔心就是选定点本身。";
            return null;
        }

        string? unitError = GeometryToolJson.ReadUnit(root, out _, out double factor);
        if (unitError != null)
        {
            error = unitError;
            return null;
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

        // Depth overrides: threaded family may pass hole_depth + thread_depth
        // (both or none, thread < hole); simple clearance may pass hole_depth
        // alone; counterbored/countersunk accept neither (head geometry is
        // standard-defined).
        double? holeDepth = null, threadDepth = null;
        if (_family == StandardHoleFamily.Threaded)
        {
            bool hasHole = root.TryGetProperty("hole_depth", out _);
            bool hasThread = root.TryGetProperty("thread_depth", out _);
            if (hasHole && hasThread)
            {
                if (!GeometryToolJson.TryReadFiniteNumber(root, "hole_depth", out double hd, out error))
                    return null;
                if (!GeometryToolJson.TryReadFiniteNumber(root, "thread_depth", out double td, out error))
                    return null;
                if (hd <= 0 || td <= 0)
                {
                    error = "hole_depth 与 thread_depth 必须为正数。";
                    return null;
                }
                if (td >= hd)
                {
                    error = $"thread_depth（{td}）必须小于 hole_depth（{hd}）：螺纹深度必须小于孔深。";
                    return null;
                }
                holeDepth = hd * factor;
                threadDepth = td * factor;
            }
            else if (hasHole || hasThread)
            {
                error = "threaded hole 的 hole_depth 与 thread_depth 必须同时提供（或同时省略，" +
                    "省略时使用标准表参考深度）。";
                return null;
            }
        }
        else if (_family == StandardHoleFamily.SimpleClearance)
        {
            if (root.TryGetProperty("hole_depth", out _))
            {
                if (!GeometryToolJson.TryReadFiniteNumber(root, "hole_depth", out double hd, out error))
                    return null;
                if (hd <= 0)
                {
                    error = "hole_depth 必须为正数。";
                    return null;
                }
                holeDepth = hd * factor;
            }
        }
        else
        {
            if (root.TryGetProperty("hole_depth", out var badEl) ||
                root.TryGetProperty("thread_depth", out _))
            {
                string bad = badEl.ValueKind != JsonValueKind.Undefined ? "hole_depth" : "thread_depth";
                error = $"{_toolName} 不支持 {bad}：沉头/埋头孔几何由标准表定义，不接受深度覆盖。";
                return null;
            }
        }

        if (root.TryGetProperty("thread_depth", out _) && _family == StandardHoleFamily.SimpleClearance)
        {
            error = $"{_toolName} 不支持 thread_depth：普通螺钉间隙孔不带螺纹。";
            return null;
        }

        return new CreateStandardHoleParameters(
            _family, size, faceRef, locU * factor, locV * factor, holeDepth, threadDepth,
            placementMode);
    }

    object? IToolRequestParser.Parse(string argumentsJson, out string? error) =>
        ParseParameters(argumentsJson, out error);
}

/// <summary>
/// Contracts for the four standard-hole tools. Each schema is the
/// authoritative field list the parser enforces — keep in sync.
/// Chinese semantic map (PROJECT-FORMAL, fixed by the user 2026-10-23 —
/// do NOT swap based on outside usage):
///   沉头孔   → create_counterbored_hole → TypesOfHole.Counterbored
///              (cylindrical flat-bottom seat, ISO 4762)
///   埋头孔   → create_countersunk_hole  → TypesOfHole.Countersink
///              (conical, ISO 10642)
///   螺纹间隙孔 → create_clearance_hole   → TypesOfHole.Simple
///   螺纹孔   → create_threaded_hole     → TypesOfHole.ThreadedHole
/// The descriptions carry these EXCLUSIVE claims: counterbored owns
/// 沉头孔 and must not be picked for 埋头孔; countersunk owns 埋头孔 and
/// must NOT be picked when the user says only 沉头孔.
/// </summary>
public static class StandardHoleToolContract
{
    private const string FaceRefDescription =
        "孔的放置面引用（placement_mode=selected_face_center 时必填）：必须是当前 NX 上下文中目标平面的真实引用" +
        "（如 FACE 1 {(0,100,30) BLOCK(2)} 的 Name 字段）。请先让用户选中该面再调用；引用无效会被拒绝。" +
        "placement_mode=selected_point 时不要提供此字段（放置面由宿主从选定点解析）。";

    private const string LocationUDescription =
        "可选。孔心相对放置面中心沿面 U 方向的偏移，按 unit 指定的单位，省略时为 0（面中心）。这是面局部坐标，不是世界坐标。";

    private const string LocationVDescription =
        "可选。孔心相对放置面中心沿面 V 方向的偏移，按 unit 指定的单位，省略时为 0。";

    private const string UnitDescription =
        "尺寸单位（用于 location 与深度值）。执行时统一换算为毫米。";

    private static string SizeDescription(string familyText) =>
        $"孔规格：M12、M10、M8、M6、M5、M4、M3、M2、M1.6 之一。{familyText}的全部直径/深度/角度由 NX 标准表按规格自动确定。";

    private const string PlacementModeDescription =
        "放置模式：selected_face_center（默认，用户选中一个平面，孔心=面中心）或 " +
        "selected_point（用户选中一个点——独立点、点特征 PointFeature 或草图点；孔心=该点的真实坐标，" +
        "放置面由宿主从点所在的实体平面解析）。模型不传坐标；选区由 NX 宿主核实。";

    private static string Schema(string extraProperties, string[] required)
    {
        string sizeEnum = "\"M12\", \"M10\", \"M8\", \"M6\", \"M5\", \"M4\", \"M3\", \"M2\", \"M1.6\"";
        return """
        {
          "type": "object",
          "additionalProperties": false,
          "required": [__REQ__],
          "properties": {
            "size": { "type": "string", "enum": [__SIZES__], "description": "孔规格。" },
            "placement_mode": { "type": "string", "enum": ["selected_face_center", "selected_point"], "description": "__MODE__" },
            "placement_face_ref": { "type": "string", "description": "__FACEREF__" },
            "location_u": { "type": "number", "description": "__LOCU__" },
            "location_v": { "type": "number", "description": "__LOCV__" }__EXTRA__,
            "unit": { "type": "string", "enum": ["mm", "cm", "m", "inch"], "description": "__UNIT__" }
          }
        }
        """
            .Replace("__REQ__", string.Join(", ", required.Select(r => $"\"{r}\"")))
            .Replace("__SIZES__", sizeEnum)
            .Replace("__MODE__", PlacementModeDescription)
            .Replace("__FACEREF__", FaceRefDescription)
            .Replace("__LOCU__", LocationUDescription)
            .Replace("__LOCV__", LocationVDescription)
            .Replace("__EXTRA__", extraProperties)
            .Replace("__UNIT__", UnitDescription);
    }

    // ---------- create_threaded_hole ----------
    // 中文语义（项目正式映射，2026-10-23 用户定案）：螺纹孔 = 本工具 =
    // Threaded Hole（内螺纹孔，Metric Coarse）。
    public const string ThreadedName = "create_threaded_hole";
    public const string ThreadedDescription =
        "创建标准螺纹孔（Threaded Hole，NX TypesOfHole.ThreadedHole，内螺纹孔，Metric Coarse 粗牙）。" +
        "当用户明确说'螺纹孔'时调用此工具。" +
        "参数来自 NX 螺纹标准表：丝锥底孔直径、螺距、倒角、退刀槽等全部按规格自动写入。" +
        "需要 size；放置方式二选一：用户选中目标平面（默认 selected_face_center，需提供 placement_face_ref）或用户选中一个点（placement_mode=selected_point，孔心=选定点，不要提供 placement_face_ref）；可选 location_u/location_v 指定面中心模式下的孔心偏移（点模式下不支持）；" +
        "可选 hole_depth + thread_depth 同时指定孔深与螺纹深（螺纹深必须小于孔深），省略时用标准表参考深度。" +
        "调用后需要用户在界面上确认才会执行。";

    public static readonly string ThreadedSchema = Schema(
        ",\n            \"hole_depth\": { \"type\": \"number\", \"exclusiveMinimum\": 0, \"description\": \"可选。孔总深度（与 thread_depth 必须同时提供）。\" },\n            \"thread_depth\": { \"type\": \"number\", \"exclusiveMinimum\": 0, \"description\": \"可选。螺纹深度（必须小于 hole_depth，与 hole_depth 同时提供）。\" }",
        new[] { "size", "unit" });

    public static readonly CreateStandardHoleRequestParser ThreadedParser =
        new(ThreadedName, StandardHoleFamily.Threaded);

    // ---------- create_clearance_hole ----------
    // 中文语义（项目正式映射，2026-10-23 用户定案）：螺纹间隙孔 = 本工具 =
    // Simple（普通螺钉孔，不创建内螺纹）。
    public const string ClearanceName = "create_clearance_hole";
    public const string ClearanceDescription =
        "创建标准螺纹间隙孔（Simple Screw Clearance Hole，NX TypesOfHole.Simple，" +
        "ISO General Screw Clearance，Close H12 配合）——普通螺钉过孔，不创建内螺纹。" +
        "当用户明确说'螺纹间隙孔'或'螺钉间隙孔/过孔'时调用此工具。" +
        "孔径、倒角、钻尖角全部按规格从标准表写入。" +
        "需要 size；放置方式二选一：用户选中目标平面（默认 selected_face_center，需提供 placement_face_ref）或用户选中一个点（placement_mode=selected_point，孔心=选定点，不要提供 placement_face_ref）；可选 location_u/location_v 指定面中心模式下的孔心偏移（点模式下不支持）；" +
        "可选 hole_depth 指定孔深（省略时用标准表参考深度）。" +
        "调用后需要用户在界面上确认才会执行。";

    public static readonly string ClearanceSchema = Schema(
        ",\n            \"hole_depth\": { \"type\": \"number\", \"exclusiveMinimum\": 0, \"description\": \"可选。孔总深度，省略时使用标准表参考深度。\" }",
        new[] { "size", "unit" });

    public static readonly CreateStandardHoleRequestParser ClearanceParser =
        new(ClearanceName, StandardHoleFamily.SimpleClearance);

    // ---------- create_counterbored_hole ----------
    // 中文语义（项目正式映射，2026-10-23 用户定案）：沉头孔 = 本工具 =
    // Counterbored（圆柱形扩孔、平底台阶，ISO 4762）。
    public const string CounterboredName = "create_counterbored_hole";
    public const string CounterboredDescription =
        "创建标准沉头孔：圆柱形扩孔、平底台阶（Counterbored Hole，NX TypesOfHole.Counterbored，" +
        "用于 ISO 4762 内六角圆柱头螺钉让位）。" +
        "当用户明确说'沉头孔'（或'圆柱形沉头孔/台阶孔/让位孔'）时必须调用此工具；不可用于锥形埋头孔。" +
        "孔径、圆柱沉头直径、沉头深度、颈部倒角全部按规格从标准表写入。" +
        "需要 size；放置方式二选一：用户选中目标平面（默认 selected_face_center，需提供 placement_face_ref）或用户选中一个点（placement_mode=selected_point，孔心=选定点，不要提供 placement_face_ref）；可选 location_u/location_v 指定面中心模式下的孔心偏移（点模式下不支持）。" +
        "调用后需要用户在界面上确认才会执行。";

    public static readonly string CounterboredSchema = Schema("", new[] { "size", "unit" });

    public static readonly CreateStandardHoleRequestParser CounterboredParser =
        new(CounterboredName, StandardHoleFamily.Counterbored);

    // ---------- create_countersunk_hole ----------
    // 中文语义（项目正式映射，2026-10-23 用户定案）：埋头孔 = 本工具 =
    // Countersink（锥形扩孔、斜面，ISO 10642）。用户只说"沉头孔"时不得
    // 调用本工具——沉头孔属于 create_counterbored_hole。
    public const string CountersunkName = "create_countersunk_hole";
    public const string CountersunkDescription =
        "创建标准埋头孔：锥形扩孔、斜面（Countersink Hole，NX TypesOfHole.Countersink，90° 锥角，" +
        "用于 ISO 10642 沉头螺钉）。" +
        "当用户明确说'埋头孔'或'锥形埋头孔'时调用此工具；用户只说'沉头孔'时，不可调用此工具" +
        "（沉头孔=圆柱形平底台阶，用 create_counterbored_hole）。" +
        "孔径、锥孔直径、锥角、卸荷深度全部按规格从标准表写入。" +
        "需要 size；放置方式二选一：用户选中目标平面（默认 selected_face_center，需提供 placement_face_ref）或用户选中一个点（placement_mode=selected_point，孔心=选定点，不要提供 placement_face_ref）；可选 location_u/location_v 指定面中心模式下的孔心偏移（点模式下不支持）。" +
        "注意：ISO 10642 锥形沉孔系列最小规格为 M3——M2/M1.6 在该标准中不存在，调用会被拒绝。" +
        "调用后需要用户在界面上确认才会执行。";

    public static readonly string CountersunkSchema = Schema("", new[] { "size", "unit" });

    public static readonly CreateStandardHoleRequestParser CountersunkParser =
        new(CountersunkName, StandardHoleFamily.Countersunk);

    // ---------- lookup helpers ----------

    public static (string Name, string Description, string Schema, CreateStandardHoleRequestParser Parser)
        For(StandardHoleFamily family) => family switch
    {
        StandardHoleFamily.Threaded => (ThreadedName, ThreadedDescription, ThreadedSchema, ThreadedParser),
        StandardHoleFamily.SimpleClearance => (ClearanceName, ClearanceDescription, ClearanceSchema, ClearanceParser),
        StandardHoleFamily.Counterbored => (CounterboredName, CounterboredDescription, CounterboredSchema, CounterboredParser),
        _ => (CountersunkName, CountersunkDescription, CountersunkSchema, CountersunkParser),
    };
}
