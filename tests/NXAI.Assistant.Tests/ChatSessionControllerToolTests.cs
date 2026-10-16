using System.Diagnostics;
using System.Text.Json;
using NXAI.Assistant.Core;
using NXAI.Assistant.UI;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Offline tests for the tool-orchestration state machine in
/// ChatSessionController: confirmation gating, single-execution guarantee,
/// round cap, cancellation safety and structured result feedback. All fakes —
/// no NX, no network, no WinForms.
///
/// Note on waiting: WaitForCompletedAsync must never be awaited as a bare
/// TaskCompletionSource task. In the xUnit host, the raw-TCS completion path
/// collides with the controller's fire-and-forget request chain and kills the
/// test process (a plain console reproduction of the same flow is clean).
/// WhenAny+Delay breaks that collision and doubles as a hang guard.
/// </summary>
public class ChatSessionControllerToolTests
{
    private const string ToolCallJson =
        """{"length":100,"width":60,"height":20,"unit":"mm"}""";

    private sealed class ScriptedSender
    {
        public readonly List<IReadOnlyList<OutboundMessage>> Requests = new();
        public readonly List<IReadOnlyList<ToolDefinition>?> ToolDeclarations = new();
        private readonly Queue<object> _responses = new();
        private object? _default;

        // Items are ChatCompletionResult values or Func<ChatCompletionResult>
        // factories (needed when a scripted response must be built on demand).
        public void Enqueue(params object[] responses)
        {
            foreach (var r in responses) _responses.Enqueue(r);
        }

        public void SetDefault(object response) => _default = response;

        public int RequestCount => Requests.Count;

        public Task<ChatCompletionResult> SendAsync(
            IReadOnlyList<OutboundMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            CancellationToken ct)
        {
            Requests.Add(messages);
            ToolDeclarations.Add(tools);
            var item = _responses.Count > 0 ? _responses.Dequeue() : _default
                ?? ChatCompletionResult.OkText("最终回复");
            return Task.FromResult(item switch
            {
                Func<ChatCompletionResult> f => f(),
                ChatCompletionResult r => r,
                _ => throw new InvalidOperationException("Bad scripted response type"),
            });
        }

        public static ChatCompletionResult ToolCall(
            string id = "call_1", string name = "create_block", string args = ToolCallJson) =>
            ChatCompletionResult.OkToolCalls([new ParsedToolCall(id, name, args)]);
    }

    private sealed class CountingExecutor : INxToolExecutor
    {
        public int CallCount;
        public CreateBlockExecutionResult Result { get; set; } =
            CreateBlockExecutionResult.Success(100, 60, 20, "成功", "part.prt");

        public CreateBlockExecutionResult ExecuteCreateBlock(
            CreateBlockParameters parameters, string? expectedWorkPartFullPath)
        {
            CallCount++;
            LastParameters = parameters;
            LastExpectedPath = expectedWorkPartFullPath;
            return Result;
        }

        public CreateBlockParameters? LastParameters { get; private set; }
        public string? LastExpectedPath { get; private set; }
    }

    private sealed class ConfirmationGate
    {
        private readonly Queue<TaskCompletionSource<ToolConfirmationDecision?>> _pending = new();
        public List<ToolConfirmationRequest> Requests { get; } = new();

        public void EnqueuePending(out TaskCompletionSource<ToolConfirmationDecision?> tcs)
        {
            tcs = new TaskCompletionSource<ToolConfirmationDecision?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue(tcs);
        }

        public ToolConfirmationDelegate Handler => async (request, ct) =>
        {
            Requests.Add(request);
            if (_pending.Count > 0)
                return await _pending.Dequeue().Task.WaitAsync(ct);
            return new ToolConfirmationDecision(true, "D:\\part.prt");
        };
    }

    private static (ChatSessionController, ScriptedSender, ChatLog, CountingExecutor, ConfirmationGate)
        Create(int maxOutbound = 20)
    {
        var log = new ChatLog();
        var sender = new ScriptedSender();
        var executor = new CountingExecutor();
        var gate = new ConfirmationGate();
        var controller = new ChatSessionController(
            new ModelService(), log, () => ("https://api.test/v1", "key", "model"),
            sender.SendAsync, maxOutbound, executor, gate.Handler);
        return (controller, sender, log, executor, gate);
    }

    /// <summary>
    /// Waits for RequestCompleted with a hard timeout (see class remarks for
    /// why the raw TCS task must not be awaited directly).
    /// </summary>
    private static Task WaitForCompletedAsync(ChatSessionController controller)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.RequestCompleted += (_, _) => tcs.TrySetResult();
        return Task.WhenAny(tcs.Task, Task.Delay(10_000));
    }

    private static string ToolMessageContent(IReadOnlyList<OutboundMessage> request, int index) =>
        request[index].Role == OutboundMessage.RoleTool
            ? request[index].Content!
            : throw new InvalidOperationException($"消息 {index} 不是 tool 消息");

    private static JsonElement ParsePayload(IReadOnlyList<OutboundMessage> request, int index)
    {
        using var doc = JsonDocument.Parse(ToolMessageContent(request, index));
        return doc.RootElement.Clone();
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

    // ------------------------------------------------- confirmation gating

    [Fact]
    public async Task Executor_never_called_before_confirmation()
    {
        var (controller, sender, log, executor, gate) = Create();
        gate.EnqueuePending(out var pending);
        sender.Enqueue(ScriptedSender.ToolCall());

        controller.StartSend("创建长方体 100x60x20 毫米");
        await UntilAsync(() => gate.Requests.Count > 0);
        await Task.Delay(50);

        Assert.Equal(0, executor.CallCount);
        Assert.Equal(1, sender.RequestCount); // round 1 sent; round 2 waits for the decision

        // Resolve with cancel: still zero executor calls.
        pending.TrySetResult(new ToolConfirmationDecision(false, null));
        await WaitForCompletedAsync(controller);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task User_cancel_executes_nothing_and_feeds_result_back()
    {
        var (controller, sender, log, executor, gate) = Create();
        gate.EnqueuePending(out var pending);
        sender.Enqueue(ScriptedSender.ToolCall(), () => ChatCompletionResult.OkText("已取消。"));

        controller.StartSend("创建长方体");
        await UntilAsync(() => gate.Requests.Count > 0);
        pending.TrySetResult(new ToolConfirmationDecision(false, null));
        await WaitForCompletedAsync(controller);

        Assert.Equal(0, executor.CallCount);
        Assert.Equal(2, sender.RequestCount);
        var payload = ParsePayload(sender.Requests[1], 2); // user, assistant(tool_calls), tool
        Assert.Equal("user_cancelled", payload.GetProperty("status").GetString());

        // The cancellation result goes to the model; the final assistant reply
        // comes from response 2.
        Assert.Contains(log.Messages, m => m.Role == ChatRole.Assistant && m.Text == "已取消。");
        Assert.Equal(2, sender.RequestCount);
    }

    [Fact]
    public async Task Confirm_executes_exactly_once_and_feeds_structured_result()
    {
        var (controller, sender, log, executor, gate) = Create();
        sender.Enqueue(ScriptedSender.ToolCall(), () => ChatCompletionResult.OkText("已创建长方体。"));

        controller.StartSend("创建长方体");
        await WaitForCompletedAsync(controller);

        Assert.Equal(1, executor.CallCount);
        Assert.Equal(2, sender.RequestCount);

        var payload = ParsePayload(sender.Requests[1], 2);
        Assert.Equal("success", payload.GetProperty("status").GetString());
        Assert.Equal(100, payload.GetProperty("verifiedDimensionsMm").GetProperty("length").GetDouble());

        // tool_call_id 关联：assistant 声明的 id 与 tool 消息一致。
        Assert.Equal("call_1", sender.Requests[1][1].ToolCalls![0].Id);
        Assert.Equal("call_1", sender.Requests[1][2].ToolCallId);

        // Parameters reached the executor as typed values in mm.
        Assert.NotNull(executor.LastParameters);
        Assert.Equal(100, executor.LastParameters!.Length);
        Assert.Equal("mm", executor.LastParameters.Unit);

        // Confirmation carries the work part the user confirmed.
        Assert.Equal("D:\\part.prt", executor.LastExpectedPath);

        Assert.Contains(log.Messages, m => m.Role == ChatRole.Assistant && m.Text == "已创建长方体。");
    }

    [Fact]
    public async Task Work_part_changed_at_execution_is_reported_to_model()
    {
        var (controller, sender, log, executor, gate) = Create();
        executor.Result = CreateBlockExecutionResult.Failure(
            ToolExecutionStatus.PreconditionsChanged, "工作部件已变化，本次执行已中止。",
            workPartIdentity: "other.prt");
        sender.Enqueue(ScriptedSender.ToolCall(), () => ChatCompletionResult.OkText("请重新请求。"));

        controller.StartSend("创建长方体");
        await WaitForCompletedAsync(controller);

        Assert.Equal(1, executor.CallCount);
        var payload = ParsePayload(sender.Requests[1], 2);
        Assert.Equal("preconditions_changed", payload.GetProperty("status").GetString());
        Assert.Contains(log.Messages, m => m.Role == ChatRole.Assistant && m.Text == "请重新请求。");
    }

    // ------------------------------------------------- invalid invocations

    [Fact]
    public async Task Unregistered_tool_rejected_without_executor_call()
    {
        var (controller, sender, log, executor, _) = Create();
        sender.Enqueue(ScriptedSender.ToolCall(name: "delete_all_bodies"),
            () => ChatCompletionResult.OkText("该工具不可用。"));

        controller.StartSend("删除所有实体");
        await WaitForCompletedAsync(controller);

        Assert.Equal(0, executor.CallCount);
        Assert.Equal(2, sender.RequestCount);
        var payload = ParsePayload(sender.Requests[1], 2);
        Assert.Equal("unknown_tool", payload.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Invalid_json_arguments_rejected_without_executor_call()
    {
        var (controller, sender, _, executor, _) = Create();
        sender.Enqueue(ScriptedSender.ToolCall(args: "{not-json"),
            () => ChatCompletionResult.OkText("参数有误。"));

        controller.StartSend("创建长方体");
        await WaitForCompletedAsync(controller);

        Assert.Equal(0, executor.CallCount);
        var payload = ParsePayload(sender.Requests[1], 2);
        Assert.Equal("invalid_parameters", payload.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(payload.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task Missing_required_field_rejected_without_executor_call()
    {
        var (controller, sender, _, executor, _) = Create();
        sender.Enqueue(ScriptedSender.ToolCall(args: """{"length":10,"width":10}"""),
            () => ChatCompletionResult.OkText("缺少参数。"));

        controller.StartSend("创建长方体");
        await WaitForCompletedAsync(controller);

        Assert.Equal(0, executor.CallCount);
        var payload = ParsePayload(sender.Requests[1], 2);
        Assert.Equal("invalid_parameters", payload.GetProperty("status").GetString());
        // Parser checks length/width/height before unit; the request above
        // lacks both height and unit, so the first reported field is height.
        Assert.Contains("height", payload.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Nonpositive_dimensions_rejected_without_executor_call()
    {
        var (controller, sender, _, executor, _) = Create();
        sender.Enqueue(ScriptedSender.ToolCall(
                args: """{"length":-5,"width":10,"height":10,"unit":"mm"}"""),
            () => ChatCompletionResult.OkText("参数无效。"));

        controller.StartSend("创建长方体");
        await WaitForCompletedAsync(controller);

        Assert.Equal(0, executor.CallCount);
        var payload = ParsePayload(sender.Requests[1], 2);
        Assert.Equal("invalid_parameters", payload.GetProperty("status").GetString());
    }

    // ------------------------------------------------------- round limits

    [Fact]
    public async Task Final_round_tool_call_is_refused_and_not_executed()
    {
        var (controller, sender, log, executor, gate) = Create();
        // Response 1: tool call. Response 2: tool call again (model misbehaving).
        sender.Enqueue(ScriptedSender.ToolCall(id: "call_1"),
            ScriptedSender.ToolCall(id: "call_2"));
        // The user never confirms the first call either.
        gate.EnqueuePending(out var pending);

        controller.StartSend("创建长方体");
        await UntilAsync(() => gate.Requests.Count > 0);
        pending.TrySetResult(new ToolConfirmationDecision(true, "D:/part.prt"));
        await WaitForCompletedAsync(controller);

        Assert.Equal(2, sender.RequestCount);      // capped
        Assert.Equal(1, executor.CallCount);       // first call confirmed and executed
        Assert.Single(gate.Requests);              // the refused second call never reached confirmation

        // Final round does not advertise tools.
        Assert.NotNull(sender.ToolDeclarations[0]);
        Assert.Null(sender.ToolDeclarations[1]);

        var last = log.Messages[^1];
        Assert.Equal(ChatRole.Error, last.Role);
        Assert.Contains("上限", last.Text);
    }

    [Fact]
    public async Task Turn_is_capped_at_max_model_responses()
    {
        var (controller, sender, _, executor, gate) = Create();
        sender.SetDefault(() => ScriptedSender.ToolCall());
        gate.EnqueuePending(out var pending);

        controller.StartSend("创建长方体");
        await UntilAsync(() => gate.Requests.Count > 0);
        pending.TrySetResult(new ToolConfirmationDecision(true, "D:/part.prt"));
        await WaitForCompletedAsync(controller);

        Assert.Equal(ChatSessionController.MaxModelResponsesPerTurn, sender.RequestCount);
        // The user confirmed round 1's call (executed once); the round-2 tool
        // call is refused by the cap and must not reach confirmation/execution.
        Assert.Equal(1, executor.CallCount);
        Assert.Single(gate.Requests);
    }

    // ------------------------------------------------- cancel/detach safety

    [Fact]
    public async Task Detach_during_confirmation_executes_nothing()
    {
        var (controller, sender, log, executor, gate) = Create();
        gate.EnqueuePending(out var pending);
        sender.Enqueue(ScriptedSender.ToolCall());

        controller.StartSend("创建长方体");
        await UntilAsync(() => gate.Requests.Count > 0);
        controller.Detach();
        pending.TrySetResult(new ToolConfirmationDecision(true, "D:\\part.prt"));
        await UntilAsync(() => !controller.IsRequestInFlight);
        await Task.Delay(50);

        Assert.Equal(0, executor.CallCount);
        Assert.Equal(1, sender.RequestCount); // round 2 never sent
        Assert.Equal(2, log.Messages.Count);  // user message + "等待你的确认" info card
    }

    [Fact]
    public async Task Cancel_during_confirmation_executes_nothing()
    {
        var (controller, sender, log, executor, gate) = Create();
        gate.EnqueuePending(out var pending);
        sender.Enqueue(ScriptedSender.ToolCall());

        controller.StartSend("创建长方体");
        await UntilAsync(() => gate.Requests.Count > 0);
        controller.CancelActiveRequest();
        pending.TrySetResult(new ToolConfirmationDecision(true, "D:\\part.prt"));
        await WaitForCompletedAsync(controller);

        Assert.Equal(0, executor.CallCount);
        Assert.Equal(1, sender.RequestCount);
        Assert.Contains(log.Messages, m => m.Role == ChatRole.Info && m.Text.Contains("已取消"));
    }

    // ------------------------------------------------------ display hygiene

    [Fact]
    public async Task Assistant_tool_calls_never_enter_display_log_as_chat()
    {
        var (controller, sender, log, executor, _) = Create();
        sender.Enqueue(ScriptedSender.ToolCall(), () => ChatCompletionResult.OkText("完成。"));

        controller.StartSend("创建长方体");
        await WaitForCompletedAsync(controller);

        // Display log holds: user, info (request), info (result), assistant (final) —
        // never a synthetic assistant entry for the tool_calls themselves.
        Assert.DoesNotContain(log.Messages, m => m.Role == ChatRole.Assistant && m.Text.Contains("tool"));
        Assert.Contains(log.Messages, m => m.Role == ChatRole.Info && m.Text.Contains("等待你的确认"));
    }

    [Fact]
    public async Task Plain_text_turn_with_tools_enabled_has_no_regression()
    {
        var (controller, sender, log, executor, _) = Create();
        sender.Enqueue(() => ChatCompletionResult.OkText("普通回复"));

        controller.StartSend("你好");
        await WaitForCompletedAsync(controller);

        Assert.Equal(1, sender.RequestCount);
        Assert.Equal(0, executor.CallCount);
        Assert.Single(sender.ToolDeclarations[0]!);   // create_block advertised
        Assert.Equal(2, log.Messages.Count);
        Assert.Equal(ChatRole.Assistant, log.Messages[1].Role);
    }
}
