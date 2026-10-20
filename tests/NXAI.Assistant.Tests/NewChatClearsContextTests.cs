using System.Diagnostics;
using System.Text.Json;
using NXAI.Assistant.Core;
using NXAI.Assistant.UI;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// 新建聊天 contract: clearing the chat log clears the model context. The
/// outbound request is rebuilt ONLY from the ChatLog, so after Clear the next
/// send must carry none of the old turn's user text or assistant replies —
/// the model receives an effectively fresh conversation. This mirrors the
/// form's StartNewConversation (_chatLog.Clear()) at the controller level.
/// </summary>
public class NewChatClearsContextTests
{
    private sealed class ScriptedSender
    {
        public List<IReadOnlyList<OutboundMessage>> Requests { get; } = new();

        public Task<ChatCompletionResult> SendAsync(
            IReadOnlyList<OutboundMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            CancellationToken ct)
        {
            Requests.Add(messages);
            if (Requests.Count == 1)
                return Task.FromResult(ChatCompletionResult.OkText("第一轮回复。"));
            return Task.FromResult(ChatCompletionResult.OkText("第二轮回复。"));
        }
    }

    private static async Task WaitCompletedAsync(ChatSessionController controller)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.RequestCompleted += (_, _) => tcs.TrySetResult();
        try
        {
            await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        }
        finally
        {
            controller.RequestCompleted -= (_, _) => tcs.TrySetResult();
        }
    }

    [Fact]
    public async Task NewChat_Clear_ExcludesOldConversationFromNextOutbound()
    {
        var log = new ChatLog();
        var sender = new ScriptedSender();
        var controller = new ChatSessionController(
            new ModelService(), log, () => ("https://api.test/v1", "key", "model"),
            sender.SendAsync);

        // Turn 1: a standard text turn fills the context.
        controller.StartSend("第一轮：创建长方体三次");
        await WaitCompletedAsync(controller);
        Assert.True(sender.Requests.Count >= 1);
        Assert.Contains(sender.Requests[0], m => m.Role == OutboundMessage.RoleUser && m.Content == "第一轮：创建长方体三次");
        var firstReplyIndex = -1;
        for (int i = 0; i < log.Messages.Count; i++)
        {
            if (log.Messages[i].Role == ChatRole.Assistant && log.Messages[i].Text == "第一轮回复。")
            {
                firstReplyIndex = i;
                break;
            }
        }
        Assert.True(firstReplyIndex >= 0);

        // StartNewConversation equivalent: exactly what the form does.
        log.Clear();

        // Turn 2 on the cleared log: the outbound history contains ONLY the
        // new user message — no residue of turn 1 reaches the model.
        controller.StartSend("第二轮：开始新对话");
        await WaitCompletedAsync(controller);

        var second = sender.Requests[1];
        Assert.Contains(second, m => m.Role == OutboundMessage.RoleUser && m.Content == "第二轮：开始新对话");
        // No old user text, no old assistant reply, no tool payloads.
        Assert.DoesNotContain(second, m => m.Role == OutboundMessage.RoleUser && m.Content == "第一轮：创建长方体三次");
        Assert.DoesNotContain(second, m => m.Role == OutboundMessage.RoleAssistant && m.Content == "第一轮回复。");
        foreach (var m in second)
        {
            if (m.Role == OutboundMessage.RoleAssistant && m.Content != null)
                Assert.NotEqual("第一轮回复。", m.Content);
        }
        // No canceled/left-over tool messages or tool calls survive the clear.
        Assert.DoesNotContain(second, m => m.Role == OutboundMessage.RoleTool);
        Assert.DoesNotContain(second, m => m.ToolCalls is { Count: > 0 });
    }

    [Fact]
    public async Task Without_Clear_OldTurnRemainsInNextOutbound_ControlCase()
    {
        // Control: WITHOUT a clear the old turn IS sent — proving the
        // exclusion above comes from the Clear, not from incidental behavior.
        var log = new ChatLog();
        var sender = new ScriptedSender();
        var controller = new ChatSessionController(
            new ModelService(), log, () => ("https://api.test/v1", "key", "model"),
            sender.SendAsync);

        controller.StartSend("第一轮：创建长方体三次");
        await WaitCompletedAsync(controller);
        controller.StartSend("第二轮：继续");
        await WaitCompletedAsync(controller);

        var second = sender.Requests[1];
        Assert.Contains(second, m => m.Role == OutboundMessage.RoleUser && m.Content == "第一轮：创建长方体三次");
    }
}
