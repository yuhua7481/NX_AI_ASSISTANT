using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

public class ChatLogTests
{
    [Fact]
    public void Add_appends_message_and_trims_text()
    {
        var log = new ChatLog();

        var message = log.Add(ChatRole.User, "  hello  ");

        Assert.Single(log.Messages);
        Assert.Equal("hello", message.Text);
        Assert.Equal(ChatRole.User, message.Role);
    }

    [Fact]
    public void Add_raises_MessageAdded()
    {
        var log = new ChatLog();
        ChatMessage? seen = null;
        log.MessageAdded += (_, m) => seen = m;

        var message = log.Add(ChatRole.Info, "ping");

        Assert.Same(message, seen);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Add_rejects_empty_text(string text)
    {
        var log = new ChatLog();
        Assert.Throws<ArgumentException>(() => log.Add(ChatRole.User, text));
    }

    [Fact]
    public void Clear_removes_all_messages()
    {
        var log = new ChatLog();
        log.Add(ChatRole.User, "one");
        log.Add(ChatRole.Assistant, "two");

        log.Clear();

        Assert.Empty(log.Messages);
    }
}
