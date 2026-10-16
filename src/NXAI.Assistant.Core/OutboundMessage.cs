namespace NXAI.Assistant.Core;

/// <summary>
/// One message in an outbound chat-completions request. Covers the four
/// OpenAI-compatible roles used by the tool protocol:
///   - system/user/assistant with plain text content;
///   - assistant with tool_calls (content may be null or empty);
///   - tool result (Role="tool", ToolCallId + Content set).
/// Wire shape is decided by ModelService; callers only build these records.
/// </summary>
public sealed record OutboundMessage(
    string Role,
    string? Content,
    IReadOnlyList<OutboundToolCall>? ToolCalls = null,
    string? ToolCallId = null)
{
    public const string RoleSystem = "system";
    public const string RoleUser = "user";
    public const string RoleAssistant = "assistant";
    public const string RoleTool = "tool";

    public static OutboundMessage FromChatMessage(ChatMessage message) => new(
        Role: message.Role switch
        {
            ChatRole.User => RoleUser,
            ChatRole.Assistant => RoleAssistant,
            _ => RoleSystem,
        },
        Content: message.Text);
}

/// <summary>One function tool call inside an assistant message.</summary>
public sealed record OutboundToolCall(string Id, string FunctionName, string ArgumentsJson);

/// <summary>A tool call parsed out of an assistant response message.</summary>
public sealed record ParsedToolCall(string Id, string FunctionName, string ArgumentsJson);
