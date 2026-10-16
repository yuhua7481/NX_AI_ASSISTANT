namespace NXAI.Assistant.Core;

/// <summary>
/// Outcome of one chat-completions turn under the tool protocol. Exactly one
/// of the three outcomes is populated:
///   - Reply set: plain assistant text (no tool calls);
///   - ToolCalls set: the assistant asked to invoke registered tools;
///   - Error set: categorized failure.
/// </summary>
public sealed class ChatCompletionResult
{
    private ChatCompletionResult(
        bool success, string? reply, IReadOnlyList<ParsedToolCall> toolCalls,
        ModelChatError error, string errorMessage)
    {
        Success = success;
        Reply = reply;
        ToolCalls = toolCalls;
        Error = error;
        ErrorMessage = errorMessage;
    }

    public bool Success { get; }

    /// <summary>Assistant text when the model replied in plain text; otherwise null.</summary>
    public string? Reply { get; }

    /// <summary>Tool calls requested by the assistant; empty when the reply is plain text.</summary>
    public IReadOnlyList<ParsedToolCall> ToolCalls { get; }

    public ModelChatError Error { get; }
    public string ErrorMessage { get; }

    public static ChatCompletionResult OkText(string reply) =>
        new(true, reply, Array.Empty<ParsedToolCall>(), ModelChatError.None, "");

    public static ChatCompletionResult OkToolCalls(IReadOnlyList<ParsedToolCall> toolCalls) =>
        new(true, null, toolCalls, ModelChatError.None, "");

    public static ChatCompletionResult Fail(ModelChatError error, string message) =>
        new(false, null, Array.Empty<ParsedToolCall>(), error, message);
}
