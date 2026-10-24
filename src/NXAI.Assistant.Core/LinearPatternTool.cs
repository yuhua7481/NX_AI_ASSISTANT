using System.Text.Json;

namespace NXAI.Assistant.Core;

/// <summary>What the user wants arrayed — the two modes NX2512 actually
/// separates (verified against the real assembly by reflection):
///   Feature: the SEED is an existing modeling FEATURE (e.g. a hole
///            feature) — PatternFeatureBuilder.FeatureList;
///   Geometry: the seed is real GEOMETRY (a whole solid Body is the
///            supported case — PatternGeometryBuilder.GeometryToPattern
///            accepts bodies/faces/curves; this tool supports Body only,
///            because face/curve patterns have extra constraints the host
///            does not verify yet).
/// </summary>
public enum LinearPatternMode
{
    Feature,
    Geometry,
}

/// <summary>The axis-aligned direction of the pattern (1-D array). The
/// sign fixes the instance order along the axis; directions are absolute
/// axes of the model space — the host builds the Direction from the
/// vector, the model never passes coordinates.</summary>
public enum LinearPatternDirection
{
    PlusX,
    MinusX,
    PlusY,
    MinusY,
    PlusZ,
    MinusZ,
}

/// <summary>
/// Accepted parameters for create_linear_pattern. Mode is REQUIRED and
/// explicit (feature vs geometry). Target resolution is host-side: Feature
/// mode resolves the most recent executor-verified FEATURE reference;
/// Geometry mode resolves the most recent executor-verified BODY. The
/// model supplies direction/count/pitch — never object references.
///
/// Phase 9.8 bidirectional: Direction2/Count2/Pitch2Mm are ALL set for a
/// two-axis rectangular pattern (count includes the seed on each axis, so
/// "X+ 4 @ 15, Y+ 3 @ 15" = 4×3 = 12 instances in ONE native pattern
/// feature); all three are null for the original 1-D behavior. There is
/// no state where only some of them are set — the parser enforces it.
/// </summary>
public sealed record CreateLinearPatternParameters(
    LinearPatternMode Mode,
    LinearPatternDirection Direction,
    int Count,
    double PitchMm,
    bool IncludeSeed,
    LinearPatternDirection? Direction2 = null,
    int? Count2 = null,
    double? Pitch2Mm = null);

/// <summary>
/// Parses and validates create_linear_pattern arguments: mode
/// ("feature"|"geometry"), direction
/// ("plus_x"|"minus_x"|"plus_y"|"minus_y"|"plus_z"|"minus_z"), count
/// (integer 2–100; 1 would be a no-op), pitch (positive mm, ≤1000), and
/// include_seed (default true — NX patterns include the seed instance by
/// default; count INCLUDES the seed).
/// </summary>
public sealed class CreateLinearPatternRequestParser : IToolRequestParser
{
    public const string ToolName = "create_linear_pattern";
    private static readonly HashSet<string> KnownFields = new()
    { "mode", "direction", "count", "pitch", "include_seed", "unit",
      "direction2", "count2", "pitch2" };

    public CreateLinearPatternParameters? ParseParameters(string argumentsJson, out string? error)
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

        // mode: REQUIRED — the two modes map to different NX builders and
        // different targets; guessing is forbidden.
        if (!root.TryGetProperty("mode", out var modeEl) || modeEl.ValueKind != JsonValueKind.String)
        {
            error = "缺少必填参数：mode（阵列模式：\"feature\"=阵列特征 或 \"geometry\"=阵列几何体）。" +
                    "用户说'把这个孔特征做阵列'用 feature；'把这个实体做阵列'用 geometry。";
            return null;
        }
        LinearPatternMode? mode = modeEl.GetString() switch
        {
            "feature" => LinearPatternMode.Feature,
            "geometry" => LinearPatternMode.Geometry,
            _ => null,
        };
        if (mode == null)
        {
            error = $"不支持的 mode：{modeEl.GetString()}。支持 \"feature\"（阵列特征）与 \"geometry\"（阵列几何体）。";
            return null;
        }

        // direction: REQUIRED — a spatial direction is never guessed.
        if (!root.TryGetProperty("direction", out var dirEl) || dirEl.ValueKind != JsonValueKind.String)
        {
            error = "缺少必填参数：direction（阵列方向：plus_x/minus_x/plus_y/minus_y/plus_z/minus_z）。" +
                    "请根据用户描述的方向选择坐标轴；无法确定时先向用户澄清。";
            return null;
        }
        LinearPatternDirection? direction = dirEl.GetString() switch
        {
            "plus_x" => LinearPatternDirection.PlusX,
            "minus_x" => LinearPatternDirection.MinusX,
            "plus_y" => LinearPatternDirection.PlusY,
            "minus_y" => LinearPatternDirection.MinusY,
            "plus_z" => LinearPatternDirection.PlusZ,
            "minus_z" => LinearPatternDirection.MinusZ,
            _ => null,
        };
        if (direction == null)
        {
            error = $"不支持的 direction：{dirEl.GetString()}。支持 plus_x/minus_x/plus_y/minus_y/plus_z/minus_z。";
            return null;
        }

        // count: integer 2..100 (includes the seed).
        if (!root.TryGetProperty("count", out var countEl) || countEl.ValueKind != JsonValueKind.Number)
        {
            error = "缺少必填参数：count（阵列实例总数，含原始实例，2–100 的整数）。";
            return null;
        }
        int count = (int)Math.Round(countEl.GetDouble());
        if (Math.Abs(countEl.GetDouble() - count) > 1e-9 || count < 2 || count > 100)
        {
            error = $"count 必须是 2–100 的整数（收到 {countEl.GetDouble()}）。count 含原始实例。";
            return null;
        }

        // pitch: positive mm.
        string? unitError = GeometryToolJson.ReadUnit(root, out _, out double factor);
        if (unitError != null)
        {
            error = unitError;
            return null;
        }
        if (!GeometryToolJson.TryReadFiniteNumber(root, "pitch", out double pitch, out error))
            return null;
        double pitchMm = pitch * factor;
        if (pitchMm <= 0)
        {
            error = $"pitch（实例间距）必须为正数（收到 {pitch}）。";
            return null;
        }
        if (pitchMm > 1000)
        {
            error = $"pitch 过大（{pitchMm:0.###} 毫米，上限 1000 毫米）。请确认数值与单位。";
            return null;
        }

        // include_seed: optional bool, default true.
        bool includeSeed = true;
        if (root.TryGetProperty("include_seed", out var seedEl))
        {
            if (seedEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                includeSeed = seedEl.GetBoolean();
            else
            {
                error = "include_seed 必须是布尔值（默认 true：count 含原始实例）。";
                return null;
            }
        }

        // Phase 9.8 optional second direction (bidirectional rectangular
        // pattern). direction2/count2/pitch2 must appear TOGETHER or not at
        // all; the second axis must not be parallel to the first (NX
        // RectangularPattern requires a non-parallel Y axis); count2 obeys
        // the same 2–100 rule; pitch2 the same positive ≤1000 mm rule.
        LinearPatternDirection? direction2 = null;
        int? count2 = null;
        double? pitch2Mm = null;
        bool hasDir2 = root.TryGetProperty("direction2", out var dir2El);
        bool hasCount2 = root.TryGetProperty("count2", out var count2El);
        bool hasPitch2 = root.TryGetProperty("pitch2", out var pitch2El);
        if (hasDir2 || hasCount2 || hasPitch2)
        {
            if (!(hasDir2 && hasCount2 && hasPitch2))
            {
                error = "双向阵列参数不完整：direction2、count2、pitch2 必须同时提供" +
                        "（单向阵列则三者都不提供）。";
                return null;
            }
            if (dir2El.ValueKind != JsonValueKind.String)
            {
                error = "direction2 必须是字符串（plus_x/minus_x/plus_y/minus_y/plus_z/minus_z）。";
                return null;
            }
            direction2 = dir2El.GetString() switch
            {
                "plus_x" => LinearPatternDirection.PlusX,
                "minus_x" => LinearPatternDirection.MinusX,
                "plus_y" => LinearPatternDirection.PlusY,
                "minus_y" => LinearPatternDirection.MinusY,
                "plus_z" => LinearPatternDirection.PlusZ,
                "minus_z" => LinearPatternDirection.MinusZ,
                _ => null,
            };
            if (direction2 == null)
            {
                error = $"不支持的 direction2：{dir2El.GetString()}。支持 plus_x/minus_x/plus_y/minus_y/plus_z/minus_z。";
                return null;
            }
            if (IsParallelOrOpposite(direction.Value, direction2.Value))
            {
                error = $"direction2（{dir2El.GetString()}）与 direction 平行，双向阵列要求两个方向互不平行。" +
                        "请为第二方向选择不同的坐标轴。";
                return null;
            }
            if (count2El.ValueKind != JsonValueKind.Number)
            {
                error = "count2 必须是数字（2–100 的整数，含原始实例）。";
                return null;
            }
            int c2 = (int)Math.Round(count2El.GetDouble());
            if (Math.Abs(count2El.GetDouble() - c2) > 1e-9 || c2 < 2 || c2 > 100)
            {
                error = $"count2 必须是 2–100 的整数（收到 {count2El.GetDouble()}）。count2 含原始实例。";
                return null;
            }
            count2 = c2;
            if (pitch2El.ValueKind != JsonValueKind.Number)
            {
                error = "pitch2 必须是数字（按 unit 指定的单位）。";
                return null;
            }
            double p2 = pitch2El.GetDouble() * factor;
            if (p2 <= 0)
            {
                error = $"pitch2（第二方向实例间距）必须为正数（收到 {pitch2El.GetDouble()}）。";
                return null;
            }
            if (p2 > 1000)
            {
                error = $"pitch2 过大（{p2:0.###} 毫米，上限 1000 毫米）。请确认数值与单位。";
                return null;
            }
            pitch2Mm = p2;
        }

        return new CreateLinearPatternParameters(
            mode.Value, direction.Value, count, pitchMm, includeSeed,
            direction2, count2, pitch2Mm);
    }

    /// <summary>True when the two axis enums sit on the SAME model axis
    /// (either sign) — the one parallel case NX's rectangular definition
    /// refuses.</summary>
    private static bool IsParallelOrOpposite(LinearPatternDirection a, LinearPatternDirection b) =>
        (a, b) switch
        {
            (LinearPatternDirection.PlusX or LinearPatternDirection.MinusX,
             LinearPatternDirection.PlusX or LinearPatternDirection.MinusX) => true,
            (LinearPatternDirection.PlusY or LinearPatternDirection.MinusY,
             LinearPatternDirection.PlusY or LinearPatternDirection.MinusY) => true,
            (LinearPatternDirection.PlusZ or LinearPatternDirection.MinusZ,
             LinearPatternDirection.PlusZ or LinearPatternDirection.MinusZ) => true,
            _ => false,
        };

    object? IToolRequestParser.Parse(string argumentsJson, out string? error) =>
        ParseParameters(argumentsJson, out error);
}

/// <summary>
/// Contract for create_linear_pattern. Feature mode = NX
/// PatternFeatureBuilder (journal "阵列特征"); Geometry mode = NX
/// PatternGeometryBuilder (journal "阵列几何特征"), Body seeds only for
/// now. Direction/count/pitch come from the model; the TARGET comes from
/// the host's auto-target resolution (the controller's last verified
/// feature/body ref, re-resolved live). The two modes never share a
/// builder or a target type.
/// </summary>
public static class LinearPatternToolContract
{
    public const string Name = CreateLinearPatternRequestParser.ToolName;

    public const string Description =
        "对目标创建线性阵列（NX 线性阵列，支持 1 个或 2 个方向）。需要参数：" +
        "mode（\"feature\"=阵列已有建模特征，如孔特征；\"geometry\"=阵列实体/几何体）、" +
        "direction（阵列方向 plus_x/minus_x/plus_y/minus_y/plus_z/minus_z，无法确定时先问用户）、" +
        "count（实例总数，含原始实例，2–100）、pitch（相邻实例间距）与 unit。" +
        "双向阵列（如\"X+ 4 个间距 15，Y+ 3 个间距 15\"）：再提供 direction2/count2/pitch2 " +
        "（三者必须同时提供），一次原生矩形阵列生成 count×count2 个位置；" +
        "两方向不得平行；两个方向的 count 均含原始实例，种子只算一次。" +
        "目标由宿主自动解析：本对话最近成功创建的特征（feature 模式）或实体（geometry 模式），" +
        "记忆不存在时从当前部件的特征树/实体清单重新识别；模型不传对象引用。" +
        "确认卡会显示模式、目标、方向、数量与间距。仅当用户明确要求线性阵列时调用；调用后需要用户确认才会执行。";

    public const string ParametersJsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["mode", "direction", "count", "pitch", "unit"],
          "properties": {
            "mode": {
              "type": "string",
              "enum": ["feature", "geometry"],
              "description": "阵列模式：feature=阵列已有建模特征（如孔特征）；geometry=阵列实体/几何体。两者对应不同的 NX Builder，必须区分。"
            },
            "direction": {
              "type": "string",
              "enum": ["plus_x", "minus_x", "plus_y", "minus_y", "plus_z", "minus_z"],
              "description": "阵列方向（模型空间坐标轴）。无法从用户描述唯一确定时先向用户澄清，不得猜测。"
            },
            "count": {
              "type": "integer",
              "minimum": 2,
              "maximum": 100,
              "description": "第一方向实例总数（含原始实例）。"
            },
            "pitch": {
              "type": "number",
              "exclusiveMinimum": 0,
              "description": "第一方向相邻实例间距（中心到中心），按 unit 指定的单位。"
            },
            "direction2": {
              "type": "string",
              "enum": ["plus_x", "minus_x", "plus_y", "minus_y", "plus_z", "minus_z"],
              "description": "可选，第二方向（双向矩形阵列）。必须与 direction 不平行，且与 count2/pitch2 同时提供。"
            },
            "count2": {
              "type": "integer",
              "minimum": 2,
              "maximum": 100,
              "description": "第二方向实例总数（含原始实例）。总实例数 = count × count2。"
            },
            "pitch2": {
              "type": "number",
              "exclusiveMinimum": 0,
              "description": "第二方向相邻实例间距，按 unit 指定的单位。"
            },
            "include_seed": {
              "type": "boolean",
              "description": "可选。count 是否含原始实例，默认 true。"
            },
            "unit": {
              "type": "string",
              "enum": ["mm", "cm", "m", "inch"],
              "description": "间距单位。执行时统一换算为毫米。"
            }
          }
        }
        """;

    public static readonly CreateLinearPatternRequestParser Parser = new();
}
