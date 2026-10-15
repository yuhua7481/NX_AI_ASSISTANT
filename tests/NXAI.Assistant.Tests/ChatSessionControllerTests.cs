using System.Diagnostics;
using System.Net;
using System.Text;
using NXAI.Assistant.Core;
using NXAI.Assistant.UI;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Offline tests for the send coordination between the chat window and
/// ModelService: the request sender is a fake delegate and the config loader
/// is injected — no real network, no WinForms controls, no real config file.
/// </summary>
public class ChatSessionControllerTests
{
    private sealed class ScriptedSender
    {
        public readonly List<IReadOnlyList<ChatMessage>> Requests = new();
        public readonly List<CancellationToken> Tokens = new();
        public Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<ModelChatResult>> Behavior =
            (_, _) => Task.FromResult(ModelChatResult.Ok("默认回复"));

        public Task<ModelChatResult> SendAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
        {
            Requests.Add(messages);
            Tokens.Add(ct);
            return Behavior(messages, ct);
        }
    }

    private sealed class FakeJsonHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _responder;
        public string? LastRequestBody;

        public FakeJsonHandler(Func<HttpResponseMessage> responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content != null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return _responder();
        }

        public string? ReadLastModelName()
        {
            if (LastRequestBody == null)
                return null;
            using var doc = System.Text.Json.JsonDocument.Parse(LastRequestBody);
            return doc.RootElement.GetProperty("model").GetString();
        }
    }

    private static (ChatSessionController Controller, ScriptedSender Sender, ChatLog Log) Create(
        Func<(string apiBaseUrl, string apiKey, string modelName)?>? loader = null)
    {
        var log = new ChatLog();
        var scripted = new ScriptedSender();
        var controller = new ChatSessionController(new ModelService(), log,
            loader ?? (() => null), scripted.SendAsync);
        return (controller, scripted, log);
    }

    private static Task WaitForCompletedAsync(ChatSessionController controller)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.RequestCompleted += (_, _) => tcs.TrySetResult();
        return tcs.Task;
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.ElapsedMilliseconds < 5000, "条件在超时时间内未满足。");
            await Task.Delay(10);
        }
    }

    // --------------------------------------------------------- input validation

    [Fact]
    public void StartSend_empty_text_returns_false_and_starts_nothing()
    {
        var (controller, sender, log) = Create();

        Assert.False(controller.StartSend(""));
        Assert.False(controller.StartSend("   "));
        Assert.Empty(sender.Requests);
        Assert.Empty(log.Messages);
    }

    // ------------------------------------------------------------ send pipeline

    [Fact]
    public async Task StartSend_adds_user_message_and_starts_request()
    {
        var (controller, sender, log) = Create();
        var completion = WaitForCompletedAsync(controller);
        var started = 0;
        controller.RequestStarted += (_, _) => started++;

        Assert.True(controller.StartSend(" 你好 "));
        await completion;

        Assert.Equal(1, started);
        Assert.Single(sender.Requests);
        Assert.Contains(sender.Requests[0], m => m.Role == ChatRole.User && m.Text == "你好");
        Assert.Equal(2, log.Messages.Count); // user message + the scripted default reply
        Assert.Equal(ChatRole.User, log.Messages[0].Role);
        Assert.Equal("你好", log.Messages[0].Text);
        Assert.False(controller.IsRequestInFlight);
    }

    [Fact]
    public async Task Success_reply_added_as_assistant_message()
    {
        var (controller, sender, log) = Create();
        sender.Behavior = (_, _) => Task.FromResult(ModelChatResult.Ok("你好，我是助手。"));
        var completion = WaitForCompletedAsync(controller);

        Assert.True(controller.StartSend("你好"));
        await completion;

        Assert.Equal(2, log.Messages.Count);
        Assert.Equal(ChatRole.User, log.Messages[0].Role);
        Assert.Equal(ChatRole.Assistant, log.Messages[1].Role);
        Assert.Equal("你好，我是助手。", log.Messages[1].Text);
    }

    [Fact]
    public async Task Failure_result_shown_as_error_message()
    {
        var (controller, sender, log) = Create();
        sender.Behavior = (_, _) => Task.FromResult(
            ModelChatResult.Fail(ModelChatError.AuthFailed, "鉴权失败（HTTP 401）：API Key 无效或没有访问权限，请检查密钥。"));
        var completion = WaitForCompletedAsync(controller);

        Assert.True(controller.StartSend("hi"));
        await completion;

        var last = log.Messages[^1];
        Assert.Equal(ChatRole.Error, last.Role);
        Assert.Contains("鉴权失败", last.Text);
    }

    [Fact]
    public async Task Blank_reply_shows_error_without_throwing()
    {
        // ChatLog.Add throws on blank text; a defensive path must prevent that.
        var (controller, sender, log) = Create();
        sender.Behavior = (_, _) => Task.FromResult(ModelChatResult.Ok("   "));
        var completion = WaitForCompletedAsync(controller);

        Assert.True(controller.StartSend("hi"));
        await completion;

        var last = log.Messages[^1];
        Assert.Equal(ChatRole.Error, last.Role);
        Assert.Contains("空白", last.Text);
    }

    // ------------------------------------------------------------- busy/cancel

    [Fact]
    public async Task Duplicate_send_while_in_flight_is_rejected_then_recovers()
    {
        var (controller, sender, _) = Create();
        var gate = new TaskCompletionSource<ModelChatResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.Behavior = (_, _) => gate.Task;
        var completion = WaitForCompletedAsync(controller);

        Assert.True(controller.StartSend("第一条"));
        Assert.True(controller.IsRequestInFlight);
        Assert.False(controller.StartSend("第二条"));
        Assert.Single(sender.Requests);

        gate.SetResult(ModelChatResult.Ok("回复"));
        await completion;
        Assert.False(controller.IsRequestInFlight);

        Assert.True(controller.StartSend("第三条"));
        Assert.Equal(2, sender.Requests.Count);
    }

    [Fact]
    public async Task Cancel_aborts_request_token_and_restores_state()
    {
        var (controller, sender, log) = Create();
        sender.Behavior = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return ModelChatResult.Ok("不应到达");
        };
        var started = 0;
        var completed = 0;
        controller.RequestStarted += (_, _) => started++;
        controller.RequestCompleted += (_, _) => completed++;
        var completion = WaitForCompletedAsync(controller);

        Assert.True(controller.StartSend("你好"));
        Assert.True(controller.IsRequestInFlight);

        controller.CancelActiveRequest();
        await completion;

        Assert.Equal(1, started);
        Assert.Equal(1, completed);
        Assert.False(controller.IsRequestInFlight);
        Assert.True(sender.Tokens[0].IsCancellationRequested);
        Assert.Equal(ChatRole.Info, log.Messages[^1].Role);
        Assert.Contains("已取消", log.Messages[^1].Text);
    }

    // ---------------------------------------------------------- context/history

    [Fact]
    public async Task Info_and_error_entries_are_never_sent_to_model()
    {
        var (controller, sender, log) = Create();
        log.Add(ChatRole.Info, "本地提示，不应发送");
        log.Add(ChatRole.Error, "本地错误，不应发送");
        var completion = WaitForCompletedAsync(controller);

        Assert.True(controller.StartSend("你好"));
        await completion;

        Assert.Single(sender.Requests[0]);
        Assert.Equal(ChatRole.User, sender.Requests[0][0].Role);
        Assert.Equal("你好", sender.Requests[0][0].Text);
    }

    [Fact]
    public async Task Two_rounds_send_correct_history_roles_and_order()
    {
        var (controller, sender, _) = Create();
        sender.Behavior = (_, _) => Task.FromResult(ModelChatResult.Ok("好的"));
        var completion1 = WaitForCompletedAsync(controller);
        Assert.True(controller.StartSend("第一问"));
        await completion1;
        var completion2 = WaitForCompletedAsync(controller);
        Assert.True(controller.StartSend("第二问"));
        await completion2;

        var second = sender.Requests[1];
        Assert.Equal(3, second.Count);
        Assert.Equal((ChatRole.User, "第一问"), (second[0].Role, second[0].Text));
        Assert.Equal((ChatRole.Assistant, "好的"), (second[1].Role, second[1].Text));
        Assert.Equal((ChatRole.User, "第二问"), (second[2].Role, second[2].Text));
    }

    [Fact]
    public async Task History_is_capped_to_last_max_outbound_messages()
    {
        var (controller, sender, log) = Create();
        sender.Behavior = (_, _) => Task.FromResult(ModelChatResult.Ok("ok"));
        for (var i = 1; i <= 25; i++)
            log.Add(ChatRole.User, $"历史{i}");
        var completion = WaitForCompletedAsync(controller);

        Assert.True(controller.StartSend("新消息"));
        await completion;

        var sent = sender.Requests[0];
        Assert.Equal(ChatSessionController.DefaultMaxOutboundMessages, sent.Count);
        Assert.Equal("历史7", sent[0].Text); // 26 sendable -> keep the last 20
        Assert.Equal("新消息", sent[^1].Text);
    }

    [Fact]
    public async Task Cleared_log_sends_only_new_conversation_context()
    {
        // 对应主窗口“新对话”按钮的行为：ChatLog.Clear 之后，下一个请求
        // 只携带新对话的内容，旧的用户消息和助手回复都不会再发送。
        var (controller, sender, log) = Create();
        sender.Behavior = (_, _) => Task.FromResult(ModelChatResult.Ok("好的"));
        var completion1 = WaitForCompletedAsync(controller);
        Assert.True(controller.StartSend("旧问题"));
        await completion1;

        log.Clear();
        log.Add(ChatRole.Info, "已开始新对话，之前的消息不会发送给模型。");

        var completion2 = WaitForCompletedAsync(controller);
        Assert.True(controller.StartSend("新问题"));
        await completion2;

        var sent = sender.Requests[1];
        Assert.Single(sent);
        Assert.Equal((ChatRole.User, "新问题"), (sent[0].Role, sent[0].Text));
        Assert.DoesNotContain(sent, m => m.Text.Contains("旧问题"));
        Assert.DoesNotContain(sent, m => m.Text.Contains("好的"));
    }

    // ---------------------------------------------------------- window lifetime

    [Fact]
    public async Task Detach_drops_late_result_without_touching_log()
    {
        var (controller, sender, log) = Create();
        var gate = new TaskCompletionSource<ModelChatResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.Behavior = (_, _) => gate.Task;
        var completedAfterDetach = false;
        controller.RequestCompleted += (_, _) => completedAfterDetach = true;

        Assert.True(controller.StartSend("你好"));
        controller.Detach();
        Assert.True(sender.Tokens[0].IsCancellationRequested);
        Assert.False(controller.StartSend("关闭后的输入"));

        gate.SetResult(ModelChatResult.Ok("迟到回复"));
        await UntilAsync(() => !controller.IsRequestInFlight);

        Assert.False(completedAfterDetach);
        Assert.Single(log.Messages); // user message only — no late writes, no assistant entry
        Assert.Equal(ChatRole.User, log.Messages[0].Role);
    }

    // ------------------------------------------------------------ configuration

    [Fact]
    public void ReloadConfiguration_applies_loader_values()
    {
        var (controller, _, _) = Create(loader: () => ("https://api.example.com", "key-1", "model-x"));

        Assert.True(controller.ReloadConfiguration());
        Assert.True(controller.IsConfigurationReady);
        Assert.Equal("model-x", controller.CurrentModelName);
    }

    [Fact]
    public async Task Not_configured_send_fails_safely_with_user_hint()
    {
        // Real ModelService path (no fake sender, no HTTP: NotConfigured returns before any call).
        var log = new ChatLog();
        var controller = new ChatSessionController(new ModelService(), log, () => null);
        Assert.False(controller.ReloadConfiguration());
        var completion = WaitForCompletedAsync(controller);

        Assert.True(controller.StartSend("你好"));
        await completion;

        var last = log.Messages[^1];
        Assert.Equal(ChatRole.Error, last.Role);
        Assert.Contains("未配置", last.Text);
    }

    [Fact]
    public async Task Configuration_update_is_used_by_the_next_request()
    {
        // Real ModelService + fake HTTP handler: proves the reloaded config reaches
        // the actual outbound request payload without touching real network or files.
        var handler = new FakeJsonHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}", Encoding.UTF8, "application/json"),
        });
        var service = new ModelService(new HttpClient(handler));
        (string apiBaseUrl, string apiKey, string modelName) current =
            ("https://api.example.com", "key-1", "model-a");
        var log = new ChatLog();
        var controller = new ChatSessionController(service, log, () => current);

        Assert.True(controller.ReloadConfiguration());
        var completion1 = WaitForCompletedAsync(controller);
        Assert.True(controller.StartSend("第一问"));
        await completion1;
        Assert.Equal("model-a", handler.ReadLastModelName());

        current = ("https://api.example.com", "key-1", "model-b");
        Assert.True(controller.ReloadConfiguration());
        var completion2 = WaitForCompletedAsync(controller);
        Assert.True(controller.StartSend("第二问"));
        await completion2;
        Assert.Equal("model-b", handler.ReadLastModelName());
    }
}
