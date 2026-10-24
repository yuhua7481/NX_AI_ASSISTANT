using System.Text.Json;

namespace NXAI.Assistant.Core;

/// <summary>
/// Accepted parameters for the create_fillet tool: a constant-radius edge
/// blend on the edges of the CURRENT selection. The model supplies only the
/// radius (and unit); the TARGET EDGES are resolved host-side from the live
/// NX selection (exactly like the standard-hole selected_point mode — the
/// model never passes edge references it was not given). Radius is in the
/// requested unit, normalized to millimeters.
/// </summary>
public sealed record CreateFilletParameters(
    double RadiusMm);

/// <summary>
/// Accepted parameters for the create_chamfer tool: a SYMMETRIC chamfer on
/// the edges of the CURRENT selection (NX ChamferOption.SymmetricOffsets —
/// one distance on both faces, the "C2" form). Asymmetric (two-offset) and
/// offset+angle modes are deliberately NOT offered: the reference journal
/// only proves the symmetric path, and an ambiguous mode would guess
/// geometry. The model supplies only the distance; edges come from the live
/// selection, resolved host-side.
/// </summary>
public sealed record CreateChamferParameters(
    double DistanceMm);

/// <summary>
/// Shared parser for create_fillet / create_chamfer. Accepts one positive
/// finite size (radius or distance) and a required unit; rejects unknown
/// fields, non-finite numbers, non-positive sizes and absurd magnitudes.
/// </summary>
public abstract class EdgeFinishRequestParser<T> : IToolRequestParser
    where T : class
{
    protected abstract string ToolNameImpl { get; }
    protected abstract string SizeFieldName { get; }
    protected abstract string SizeChineseName { get; }

    private static readonly HashSet<string> KnownFields = new() { "unit", "__SIZE__" };

    private HashSet<string> KnownFieldsWithSize() =>
        new(KnownFields.Select(f => f == "__SIZE__" ? SizeFieldName : f), StringComparer.Ordinal);

    /// <summary>Upper sanity bound in mm — a radius/distance above this is
    /// treated as invalid (almost certainly a unit or intent error).</summary>
    private const double MaxSizeMm = 1000.0;

    public T? ParseParameters(string argumentsJson, out string? error)
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
            error = $"{ToolNameImpl} 参数不是合法 JSON。";
            return null;
        }
        if (root.ValueKind != JsonValueKind.Object)
        {
            error = $"{ToolNameImpl} 参数必须是 JSON 对象。";
            return null;
        }
        error = GeometryToolJson.RejectUnknownFields(root, ToolNameImpl, KnownFieldsWithSize());
        if (error != null)
            return null;

        string? unitError = GeometryToolJson.ReadUnit(root, out _, out double factor);
        if (unitError != null)
        {
            error = unitError;
            return null;
        }

        if (!GeometryToolJson.TryReadFiniteNumber(root, SizeFieldName, out double size, out error))
            return null;
        double mm = size * factor;
        if (mm <= 0)
        {
            error = $"{SizeChineseName}必须为正数（收到 {size}）。";
            return null;
        }
        if (mm > MaxSizeMm)
        {
            error = $"{SizeChineseName}过大（{size} {unitHintFromFactor(factor)} = {mm:0.###} 毫米，" +
                    $"上限 {MaxSizeMm:0} 毫米）。请确认数值与单位。";
            return null;
        }
        return FromMillimeters(mm);
    }

    protected abstract T FromMillimeters(double mm);

    private static string unitHintFromFactor(double factor) => factor switch
    {
        1.0 => "mm",
        10.0 => "cm",
        1000.0 => "m",
        _ => "inch",
    };

    object? IToolRequestParser.Parse(string argumentsJson, out string? error) =>
        ParseParameters(argumentsJson, out error);
}

public sealed class CreateFilletRequestParser : EdgeFinishRequestParser<CreateFilletParameters>
{
    public const string ToolName = "create_fillet";
    protected override string ToolNameImpl => "create_fillet";
    protected override string SizeFieldName => "radius";
    protected override string SizeChineseName => "圆角半径";
    protected override CreateFilletParameters FromMillimeters(double mm) => new(mm);
}

public sealed class CreateChamferRequestParser : EdgeFinishRequestParser<CreateChamferParameters>
{
    public const string ToolName = "create_chamfer";
    protected override string ToolNameImpl => "create_chamfer";
    protected override string SizeFieldName => "distance";
    protected override string SizeChineseName => "倒角距离";
    protected override CreateChamferParameters FromMillimeters(double mm) => new(mm);
}

/// <summary>
/// Contracts for create_fillet / create_chamfer. Both tools act on the
/// CURRENT selection's edges (resolved and re-verified host-side after the
/// user confirms — a stale selection never executes). Fillet = NX
/// EdgeBlendBuilder (constant radius, one chainset per selection);
/// chamfer = NX ChamferBuilder in SymmetricOffsets mode (the "C2" form).
/// The edge→feature mapping stays exclusive: no schema field, description
/// or executor mixes the two builders.
/// </summary>
public static class EdgeFinishToolContract
{
    public const string FilletName = CreateFilletRequestParser.ToolName;
    public const string ChamferName = CreateChamferRequestParser.ToolName;

    public const string FilletDescription =
        "对目标实体边创建等半径圆角（Edge Blend，NX 边倒圆）。" +
        "需要 radius（圆角半径，按 unit 单位）与 unit；目标边由宿主解析，模型不传边引用：" +
        "①用户已选中实体边时处理选中的边（可多条）；②用户未选中但本对话刚创建过实体（如长方体）时，" +
        "对'周边/所有棱边'类请求自动解析目标——标准长方体解析为其全部 12 条外棱边；" +
        "③目标不明确（多实体、非长方体、目标已删除）时宿主会拒绝并说明原因，届时请转告用户。" +
        "确认卡会显示实际目标与边数。仅当用户明确要求倒圆角/圆角时调用；调用后需要用户在界面上确认才会执行。";

    public const string ChamferDescription =
        "对目标实体边创建对称倒斜角（Chamfer，NX 倒斜角，SymmetricOffsets 对称距离模式——" +
        "即'C2'这类等距倒角）。需要 distance（倒角距离，按 unit 单位）与 unit；目标边解析规则与 create_fillet 相同" +
        "（选中边优先；空选区时对刚创建的标准长方体自动解析周边 12 条棱边；目标不明确则拒绝）。模型不传边引用。" +
        "注意：本工具只做对称等距倒角；用户要求'距离1×距离2'或'距离×角度'的非对称倒角时，明确告知暂不支持。" +
        "仅当用户明确要求倒斜角/倒角/C 斜角时调用；调用后需要用户在界面上确认才会执行。";

    public const string FilletSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["radius", "unit"],
          "properties": {
            "radius": {
              "type": "number",
              "exclusiveMinimum": 0,
              "description": "圆角半径，按 unit 指定的单位。例如 R2 = radius 2, unit mm。"
            },
            "unit": {
              "type": "string",
              "enum": ["mm", "cm", "m", "inch"],
              "description": "尺寸单位。执行时统一换算为毫米。"
            }
          }
        }
        """;

    public const string ChamferSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["distance", "unit"],
          "properties": {
            "distance": {
              "type": "number",
              "exclusiveMinimum": 0,
              "description": "对称倒角距离（两个面各退该距离），按 unit 指定的单位。例如 C2 = distance 2, unit mm。"
            },
            "unit": {
              "type": "string",
              "enum": ["mm", "cm", "m", "inch"],
              "description": "尺寸单位。执行时统一换算为毫米。"
            }
          }
        }
        """;

    public static readonly CreateFilletRequestParser FilletParser = new();
    public static readonly CreateChamferRequestParser ChamferParser = new();
}
