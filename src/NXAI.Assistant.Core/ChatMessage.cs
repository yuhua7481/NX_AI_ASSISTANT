namespace NXAI.Assistant.Core;

public enum ChatRole
{
    System,
    User,
    Assistant,
    Error,
    Info,
}

/// <summary>
/// One entry in the chat shell's message list. Phase 0 keeps messages local
/// (no provider yet); the same model will carry provider replies in later phases.
/// </summary>
public sealed record ChatMessage(ChatRole Role, string Text, DateTimeOffset TimestampUtc);

/// <summary>
/// Ordered chat log. Not thread-safe; the chat shell owns it on one thread.
/// </summary>
public sealed class ChatLog
{
    private readonly List<ChatMessage> _messages = new();

    public event EventHandler<ChatMessage>? MessageAdded;

    public IReadOnlyList<ChatMessage> Messages => _messages;

    public ChatMessage Add(ChatRole role, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("消息内容不能为空。", nameof(text));
        }

        var message = new ChatMessage(role, text.Trim(), DateTimeOffset.UtcNow);
        _messages.Add(message);
        MessageAdded?.Invoke(this, message);
        return message;
    }

    public void Clear() => _messages.Clear();
}
