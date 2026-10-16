using System.Text.Json;

namespace NXAI.Assistant.Core;

/// <summary>
/// A tool the model may call. Name and description go into the request's
/// "tools" declaration; ParametersJsonSchema is the JSON Schema object that
/// constrains the model's arguments.
/// </summary>
public sealed record ToolDefinition(string Name, string Description, string ParametersJsonSchema);

/// <summary>
/// Raw tool invocation requested by the model, already separated from the
/// assistant message content. ArgumentsJson is kept unparsed until a
/// registered parser validates it.
/// </summary>
public sealed record ToolCallRequest(string ToolCallId, string ToolName, string ArgumentsJson);

/// <summary>
/// Structured outcome of one tool execution, keyed to the originating
/// ToolCallId so the result can be sent back to the model as the correct
/// "tool" message.
/// </summary>
public sealed record ToolExecutionResult(
    string ToolCallId,
    string ToolName,
    ToolExecutionStatus Status,
    string Message,
    string? DetailsJson = null);

public static class ToolExecutionStatusNames
{
    public const string Success = "success";
    public const string UserCancelled = "user_cancelled";
    public const string InvalidParameters = "invalid_parameters";
    public const string UnknownTool = "unknown_tool";
    public const string PreconditionsChanged = "preconditions_changed";
    public const string VerificationFailed = "verification_failed";
    public const string ExecutionFailed = "execution_failed";
    public const string ExecutionUncertain = "execution_uncertain";
}

public enum ToolExecutionStatus
{
    Success,
    UserCancelled,
    InvalidParameters,
    UnknownTool,
    PreconditionsChanged,
    VerificationFailed,
    ExecutionFailed,
    ExecutionUncertain,
}

public static class ToolExecutionStatusExtensions
{
    public static string ToWireName(this ToolExecutionStatus status) => status switch
    {
        ToolExecutionStatus.Success => ToolExecutionStatusNames.Success,
        ToolExecutionStatus.UserCancelled => ToolExecutionStatusNames.UserCancelled,
        ToolExecutionStatus.InvalidParameters => ToolExecutionStatusNames.InvalidParameters,
        ToolExecutionStatus.UnknownTool => ToolExecutionStatusNames.UnknownTool,
        ToolExecutionStatus.PreconditionsChanged => ToolExecutionStatusNames.PreconditionsChanged,
        ToolExecutionStatus.VerificationFailed => ToolExecutionStatusNames.VerificationFailed,
        ToolExecutionStatus.ExecutionFailed => ToolExecutionStatusNames.ExecutionFailed,
        ToolExecutionStatus.ExecutionUncertain => ToolExecutionStatusNames.ExecutionUncertain,
        _ => ToolExecutionStatusNames.ExecutionFailed,
    };
}

/// <summary>
/// Parses a tool call from model output into a typed request. Returns the
/// structured error the model will see when the invocation is unusable.
/// </summary>
public interface IToolRequestParser
{
    /// <summary>Typed parameter object on success; null with <paramref name="error"/> on failure.</summary>
    object? Parse(string argumentsJson, out string? error);
}

/// <summary>Per-tool contract: schema, argument parsing and validation.</summary>
public sealed class ToolDefinitionEntry
{
    public required ToolDefinition Definition { get; init; }
    public required IToolRequestParser Parser { get; init; }
}

/// <summary>
/// Registry of tools the model is allowed to call. Only tools registered
/// here can execute; anything else is rejected before reaching any executor.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolDefinition> _tools =
        new(StringComparer.Ordinal);

    public void Register(ToolDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Name))
            throw new ArgumentException("Tool name must not be empty.", nameof(definition));
        _tools.Add(definition.Name, definition);
    }

    public bool IsRegistered(string toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && _tools.ContainsKey(toolName);

    public ToolDefinition? Find(string toolName) =>
        string.IsNullOrWhiteSpace(toolName) ? null : _tools.GetValueOrDefault(toolName);

    public IReadOnlyList<ToolDefinition> All => _tools.Values.ToList();
}

/// <summary>
/// Accepted parameters for the create_block tool. All linear values are in
/// millimeters (the tool contract converts the model's unit field itself);
/// origin defaults to (0, 0, 0) when the model omits it.
/// </summary>
public sealed record CreateBlockParameters(
    double OriginX, double OriginY, double OriginZ,
    double Length, double Width, double Height,
    string Unit);

/// <summary>
/// Parses and validates create_block tool arguments. Rejects missing fields,
/// wrong types, non-finite numbers, non-positive or out-of-bound dimensions,
/// unsupported units, extra unsupported fields, and invalid JSON. On success
/// the parameters are normalized to millimeters.
/// </summary>
public sealed class CreateBlockRequestParser : IToolRequestParser
{
    public const string ToolName = "create_block";
    private static readonly string[] SupportedUnits = { "mm", "cm", "m", "inch", "in" };
    private static readonly HashSet<string> KnownFields = new()
    {
        "length", "width", "height", "unit", "origin_x", "origin_y", "origin_z",
    };

    public CreateBlockParameters? ParseParameters(string argumentsJson, out string? error)
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
            error = "create_block 参数不是合法 JSON。";
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "create_block 参数必须是 JSON 对象。";
            return null;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (!KnownFields.Contains(property.Name))
            {
                error = $"create_block 不支持参数字段：{property.Name}。";
                return null;
            }
        }

        if (!TryGetNumber(root, "length", out double length, out error) ||
            !TryGetNumber(root, "width", out double width, out error) ||
            !TryGetNumber(root, "height", out double height, out error))
        {
            return null;
        }

        string unit;
        if (root.TryGetProperty("unit", out var unitElement))
        {
            if (unitElement.ValueKind != JsonValueKind.String)
            {
                error = "unit 必须是字符串。";
                return null;
            }
            unit = unitElement.GetString()!.Trim().ToLowerInvariant();
        }
        else
        {
            error = "缺少必填参数：unit。";
            return null;
        }
        if (Array.IndexOf(SupportedUnits, unit) < 0)
        {
            error = $"不支持的单位：{unit}。支持 mm、cm、m、inch。";
            return null;
        }

        if (!TryGetOptionalNumber(root, "origin_x", out double originX, out error) ||
            !TryGetOptionalNumber(root, "origin_y", out double originY, out error) ||
            !TryGetOptionalNumber(root, "origin_z", out double originZ, out error))
        {
            return null;
        }

        double factor = unit switch
        {
            "mm" => 1.0,
            "cm" => 10.0,
            "m" => 1000.0,
            "inch" or "in" => 25.4,
            _ => 1.0,
        };

        var parameters = new CreateBlockParameters(
            originX * factor, originY * factor, originZ * factor,
            length * factor, width * factor, height * factor,
            "mm");

        error = BlockRequestValidator.ValidateWorkingPartRequest(
            parameters.OriginX, parameters.OriginY, parameters.OriginZ,
            parameters.Length, parameters.Width, parameters.Height);
        if (error != null)
            return null;

        return parameters;
    }

    object? IToolRequestParser.Parse(string argumentsJson, out string? error) =>
        ParseParameters(argumentsJson, out error);

    private static bool TryGetNumber(JsonElement root, string name, out double value, out string? error)
    {
        return TryReadFiniteNumber(root, name, out value, out error, required: true);
    }

    private static bool TryGetOptionalNumber(
        JsonElement root, string name, out double value, out string? error)
    {
        if (!root.TryGetProperty(name, out _))
        {
            value = 0;
            error = null;
            return true;
        }
        return TryReadFiniteNumber(root, name, out value, out error, required: false);
    }

    private static bool TryReadFiniteNumber(
        JsonElement root, string name, out double value, out string? error, bool required)
    {
        error = null;
        value = 0;
        if (!root.TryGetProperty(name, out var element))
        {
            error = $"缺少必填参数：{name}。";
            return false;
        }
        if (element.ValueKind == JsonValueKind.Number)
        {
            double number = element.GetDouble();
            if (!double.IsFinite(number))
            {
                error = $"参数 {name} 必须是有限数字。";
                return false;
            }
            value = number;
            return true;
        }
        // String-encoded numbers are tolerated (common model behaviour) but
        // must still parse to a finite double.
        if (element.ValueKind == JsonValueKind.String)
        {
            if (double.TryParse(element.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double parsed))
            {
                value = parsed;
                return true;
            }
            error = $"参数 {name} 不是可解析的数字。";
            value = 0;
            return false;
        }
        error = $"参数 {name} 必须是数字。";
        value = 0;
        return false;
    }
}
