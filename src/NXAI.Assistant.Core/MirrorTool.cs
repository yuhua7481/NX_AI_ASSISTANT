using System.Text.Json;

namespace NXAI.Assistant.Core;

/// <summary>
/// Mirror mode — the two modes NX2512 natively separates:
///   Geometry: MirrorBodyBuilder — real solid bodies mirrored as
///             MirrorBody feature(s); the source body survives by default
///             (DeleteSourceBody stays false).
///   Feature:  MirrorFeatureBuilder — existing modeling features mirrored
///             with the model history intact (MirrorFeature feature).
/// Both accept ONE OR MORE objects of their own kind and one mirror plane.
/// </summary>
public enum MirrorMode
{
    Geometry,
    Feature,
}

/// <summary>
/// Accepted parameters for create_mirror. The SEEDS and the MIRROR PLANE
/// are resolved host-side from the CURRENT selection (role-separated: the
/// plane object never counts as a seed; see the executor) — the model
/// passes only the mode and the safety defaults. This keeps object
/// references out of the schema entirely (the R2 lesson from the linear
/// pattern's stray "target" field).
///   KeepOriginal: geometry mode keeps the source bodies (DeleteSourceBody
///   = false, the NX default and this tool's fixed default — the mirror is
///   additive, never destructive).
/// </summary>
public sealed record CreateMirrorParameters(
    MirrorMode Mode,
    bool KeepOriginal = true);

/// <summary>
/// Parses and validates create_mirror arguments: mode
/// ("geometry"|"geometry_body" not accepted — exactly "geometry" or
/// "feature"), optional keep_original (default true). NO object-reference
/// fields exist: seeds and mirror plane are resolved from the NX selection.
/// </summary>
public sealed class CreateMirrorRequestParser : IToolRequestParser
{
    public const string ToolName = "create_mirror";
    private static readonly HashSet<string> KnownFields = new()
    { "mode", "keep_original" };

    public CreateMirrorParameters? ParseParameters(string argumentsJson, out string? error)
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

        if (!root.TryGetProperty("mode", out var modeEl) || modeEl.ValueKind != JsonValueKind.String)
        {
            error = "缺少必填参数：mode（\"geometry\"=镜像实体/几何体 或 \"feature\"=镜像建模特征）。" +
                    "待镜像对象与镜像平面（基准面或实体上的平面）由宿主从当前选区解析，模型不传对象引用。";
            return null;
        }
        MirrorMode? mode = modeEl.GetString() switch
        {
            "geometry" => MirrorMode.Geometry,
            "feature" => MirrorMode.Feature,
            _ => null,
        };
        if (mode == null)
        {
            error = $"不支持的 mode：{modeEl.GetString()}。支持 \"geometry\"（镜像实体）与 \"feature\"（镜像特征）。";
            return null;
        }

        bool keepOriginal = true;
        if (root.TryGetProperty("keep_original", out var keepEl))
        {
            if (keepEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                keepOriginal = keepEl.GetBoolean();
            else
            {
                error = "keep_original 必须是布尔值（默认 true：保留原对象，镜像为新增结果）。";
                return null;
            }
        }

        return new CreateMirrorParameters(mode.Value, keepOriginal);
    }

    object? IToolRequestParser.Parse(string argumentsJson, out string? error) =>
        ParseParameters(argumentsJson, out error);
}

/// <summary>
/// Contract for create_mirror. Geometry mode = NX MirrorBodyBuilder
/// (MirrorBodyCollector + Plane + DeleteSourceBody=false; FeatureOption
/// OneFeatureForAllBodies so N selected bodies mirror in ONE feature);
/// feature mode = NX MirrorFeatureBuilder (FeatureSet + Plane). The mirror
/// plane is a selected Datum Plane OR a selected planar solid face (its
/// real plane definition — origin + unit normal — read from the geometry;
/// NXOpen.xml-verified builders). The model never passes object references.
/// </summary>
public static class MirrorToolContract
{
    public const string Name = CreateMirrorRequestParser.ToolName;

    public const string Description =
        "对选中的对象创建镜像（NX 原生 Mirror，保留历史与参数化）。需要参数：" +
        "mode（\"geometry\"=镜像实体/几何体，可多个实体一次镜像；\"feature\"=镜像已有建模特征，可多个特征）。" +
        "待镜像对象与镜像平面（基准面，或实体上的普通平面）由宿主从当前选区解析：" +
        "选中 1 个或多个同类型对象（实体/特征）加恰好 1 个平面（基准面或平面面）；" +
        "平面与对象按角色分别解析，不会把镜像面当成待镜像对象。" +
        "默认保留原对象，镜像结果为新增特征（不删除、不移动原对象）。" +
        "模型不传对象引用。未选对象、平面歧义（多个平面）或混入不兼容对象时宿主会拒绝并说明原因。" +
        "确认卡会显示模式、种子对象、镜像面与保留策略。仅当用户明确要求镜像时调用；调用后需要用户确认才会执行。";

    public const string ParametersJsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["mode"],
          "properties": {
            "mode": {
              "type": "string",
              "enum": ["geometry", "feature"],
              "description": "镜像模式：geometry=镜像实体/几何体（可多个实体）；feature=镜像已有建模特征（可多个特征）。"
            },
            "keep_original": {
              "type": "boolean",
              "description": "可选。是否保留原对象，默认 true（镜像为新增结果，不删除原对象）。"
            }
          }
        }
        """;

    public static readonly CreateMirrorRequestParser Parser = new();
}
