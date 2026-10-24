using System.Text.Json;

namespace NXAI.Assistant.Core;

/// <summary>Boolean operation types. Explicit — never inferred.</summary>
public enum BooleanOperationType
{
    Unite,
    Subtract,
    Intersect,
}

/// <summary>
/// Accepted parameters for the boolean_operation tool. Body refs are
/// controlled references previously returned by NXHost tool results (e.g.
/// newBodyRef) — never raw NX tags or object addresses. Target and tool must
/// be different.
/// </summary>
public sealed record BooleanOperationParameters(
    BooleanOperationType Operation,
    string TargetBodyRef,
    string ToolBodyRef);

/// <summary>
/// Parses and validates boolean_operation arguments: an explicit operation,
/// two distinct non-empty body references.
/// </summary>
public sealed class BooleanOperationRequestParser : IToolRequestParser
{
    public const string ToolName = "boolean_operation";
    private static readonly HashSet<string> KnownFields = new()
    {
        "operation", "target_body_ref", "tool_body_ref",
    };

    public BooleanOperationParameters? ParseParameters(string argumentsJson, out string? error)
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
            error = "boolean_operation 参数不是合法 JSON。";
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "boolean_operation 参数必须是 JSON 对象。";
            return null;
        }

        error = GeometryToolJson.RejectUnknownFields(root, ToolName, KnownFields);
        if (error != null)
            return null;

        if (!root.TryGetProperty("operation", out var opElement) || opElement.ValueKind != JsonValueKind.String)
        {
            error = "缺少必填参数：operation（unite、subtract 或 intersect）。";
            return null;
        }
        var opName = opElement.GetString()!.Trim().ToLowerInvariant();
        if (!TryMapOperation(opName, out var operation))
        {
            error = $"不支持的 operation 值：{opName}。支持 unite（并集）、subtract（差集）、intersect（交集）。";
            return null;
        }

        if (!ReadBodyRef(root, "target_body_ref", out string targetRef, out error))
            return null;
        if (!ReadBodyRef(root, "tool_body_ref", out string toolRef, out error))
            return null;
        error = null;

        if (string.Equals(targetRef, toolRef, StringComparison.Ordinal))
        {
            error = "target_body_ref 与 tool_body_ref 不能相同：布尔运算需要两个不同的实体。";
            return null;
        }

        return new BooleanOperationParameters(operation, targetRef, toolRef);
    }

    object? IToolRequestParser.Parse(string argumentsJson, out string? error) =>
        ParseParameters(argumentsJson, out error);

    private static bool TryMapOperation(string name, out BooleanOperationType operation)
    {
        switch (name)
        {
            case "unite": operation = BooleanOperationType.Unite; return true;
            case "subtract": operation = BooleanOperationType.Subtract; return true;
            case "intersect": operation = BooleanOperationType.Intersect; return true;
            default: operation = default; return false;
        }
    }

    private static bool ReadBodyRef(
        JsonElement root, string name, out string reference, out string? error)
    {
        reference = "";
        error = null;
        if (!root.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(element.GetString()))
        {
            error = $"缺少必填参数：{name}（必须是工具结果返回的实体引用）。";
            return false;
        }
        reference = element.GetString()!.Trim();
        return true;
    }
}

/// <summary>
/// The boolean_operation tool contract: unite / subtract / intersect of two
/// explicitly identified solid bodies via the real NX boolean feature.
/// </summary>
public static class BooleanOperationTool
{
    public const string Name = BooleanOperationRequestParser.ToolName;

    public const string ParametersJsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["operation", "target_body_ref", "tool_body_ref"],
          "properties": {
            "operation": {
              "type": "string",
              "enum": ["unite", "subtract", "intersect"],
              "description": "布尔运算类型：unite=并集，subtract=差集（从目标体中减去工具体），intersect=交集。"
            },
            "target_body_ref": {
              "type": "string",
              "description": "目标体引用：必须是先前工具结果 newBodyRef/resultBodyRef 字段返回的真实引用（如 BLOCK(3)），不能用显示名称或编造编号。unite/intersect 的结果归属目标体；subtract 从目标体减去工具体。"
            },
            "tool_body_ref": {
              "type": "string",
              "description": "工具体引用：必须是先前工具结果 newBodyRef/resultBodyRef 字段返回的真实引用（如 CYLINDER(4)），不能用显示名称或编造编号。必须与目标体不同。默认运算后工具体被消耗（合并入布尔特征）。"
            }
          }
        }
        """;

    public const string Description =
        "对两个明确指定的实体执行布尔运算（unite 并集、subtract 差集、intersect 交集）。" +
        "需要 operation、target_body_ref 与 tool_body_ref（均为先前工具结果中 newBodyRef/resultBodyRef 字段返回的真实实体引用，" +
        "不能编造显示名称或自造编号如 block_1、Block、cylinder_1——不存在的引用会被拒绝且不执行任何操作；两者必须不同）。" +
        "运算后工具体默认被消耗；结果体归属目标体。仅当用户明确要求布尔运算且两个实体都能明确识别时调用；" +
        "调用后需要用户在界面上确认才会执行。";
}
