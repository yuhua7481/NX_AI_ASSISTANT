using NXAI.Assistant.Core;
using NXAI.Assistant.UI;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Offline tests for execution-mode-aware tool dispatch in
/// ChatSessionController: confirm-mode compatibility, autonomous-mode
/// skip-confirmation for allowlisted tools, mode-switch race safety and the
/// no-double-execution guarantee. All fakes — no NX, no network, no WinForms.
/// </summary>
public class ExecutionModeControllerTests
{
    private const string BlockArgs = """{"length":100,"width":60,"height":20,"unit":"mm"}""";
    private const string CylinderArgs = """{"diameter":40,"height":80,"unit":"mm"}""";

    private sealed class ScriptedSender
    {
        public readonly List<IReadOnlyList<OutboundMessage>> Requests = new();
        private readonly Queue<object> _responses = new();
        private object? _default;

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
            var item = _responses.Count > 0 ? _responses.Dequeue() : _default
                ?? ChatCompletionResult.OkText("最终回复");
            return item switch
            {
                Func<Task<ChatCompletionResult>> af => af(),
                Func<ChatCompletionResult> f => Task.FromResult(f()),
                ChatCompletionResult r => Task.FromResult(r),
                _ => throw new InvalidOperationException("Bad scripted response type"),
            };
        }

        public static ChatCompletionResult ToolCall(
            string id = "call_1", string name = "create_block", string args = BlockArgs) =>
            ChatCompletionResult.OkToolCalls([new ParsedToolCall(id, name, args)]);
    }

    private sealed class CountingExecutor : INxToolExecutor
    {
        public int CallCount;
        public string? LastToolName { get; private set; }
        public string? LastExpectedPath { get; private set; }

        public CreateBlockExecutionResult Result { get; set; } =
            CreateBlockExecutionResult.Success(100, 60, 20, "已创建。", "part.prt");

        public CreateBlockExecutionResult ExecuteCreateBlock(
            CreateBlockParameters parameters, string? expectedWorkPartFullPath)
        {
            CallCount++; LastToolName = "create_block"; LastExpectedPath = expectedWorkPartFullPath;
            return Result;
        }

        public CreateBlockExecutionResult ExecuteCreateCylinder(
            CreateCylinderParameters parameters, string? expectedWorkPartFullPath)
        {
            CallCount++; LastToolName = "create_cylinder"; LastExpectedPath = expectedWorkPartFullPath;
            return Result;
        }

        public CreateBlockExecutionResult ExecuteCreateCone(
            CreateConeParameters parameters, string? expectedWorkPartFullPath)
        {
            CallCount++; LastToolName = "create_cone"; LastExpectedPath = expectedWorkPartFullPath;
            return Result;
        }

        public CreateBlockExecutionResult ExecuteCreateSphere(
            CreateSphereParameters parameters, string? expectedWorkPartFullPath)
        {
            CallCount++; LastToolName = "create_sphere"; LastExpectedPath = expectedWorkPartFullPath;
            return Result;
        }
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

    /// <summary>In-memory store so tests flip modes without touching disk.</summary>
    private sealed class MemoryModeStore : IExecutionModeStore
    {
        public ExecutionMode? Stored { get; set; }
        public ExecutionMode? Load() => Stored;
        public void Save(ExecutionMode mode) => Stored = mode;
    }

    private static (ChatSessionController Controller, ScriptedSender Sender, ChatLog Log,
        CountingExecutor Executor, ConfirmationGate Gate, ExecutionModeManager ModeManager)
        Create(MemoryModeStore? modeStore = null)
    {
        var log = new ChatLog();
        var sender = new ScriptedSender();
        var executor = new CountingExecutor();
        var gate = new ConfirmationGate();
        var modeManager = new ExecutionModeManager(modeStore ?? new MemoryModeStore());
        var controller = new ChatSessionController(
            new ModelService(), log, () => ("https://api.test/v1", "key", "model"),
            sender.SendAsync, 20, executor, gate.Handler, modeManager: modeManager);
        return (controller, sender, log, executor, gate, modeManager);
    }

    private static Task WaitForCompletedAsync(ChatSessionController controller)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.RequestCompleted += (_, _) => tcs.TrySetResult();
        return Task.WhenAny(tcs.Task, Task.Delay(10_000));
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds > 10_000)
                throw new TimeoutException("条件等待超时");
            await Task.Delay(10);
        }
    }

    // ------------------------------------------------------------ confirm-mode compatibility

    [Fact]
    public async Task WithoutModeManager_ConfirmationPathUnchanged()
    {
        var log = new ChatLog();
        var sender = new ScriptedSender();
        var executor = new CountingExecutor();
        var gate = new ConfirmationGate();
        var controller = new ChatSessionController(
            new ModelService(), log, () => ("https://api.test/v1", "key", "model"),
            sender.SendAsync, 20, executor, gate.Handler);

        sender.Enqueue(ScriptedSender.ToolCall(), () => ChatCompletionResult.OkText("完成。"));
        controller.StartSend("创建长方体");
        await WaitForCompletedAsync(controller);

        Assert.Single(gate.Requests);           // confirmation was asked
        Assert.Equal(1, executor.CallCount);    // exactly once
        Assert.Equal("D:\\part.prt", executor.LastExpectedPath);
    }

    [Fact]
    public async Task ConfirmMode_StillShowsConfirmationCard()
    {
        var (controller, sender, log, executor, gate, modeManager) = Create();

        Assert.Equal(ExecutionMode.Confirm, modeManager.CurrentMode);
        Assert.Equal(ExecutionMode.Confirm, controller.CurrentExecutionMode);

        gate.EnqueuePending(out var pending);
        sender.Enqueue(ScriptedSender.ToolCall(), () => ChatCompletionResult.OkText("完成。"));

        controller.StartSend("创建长方体");
        await UntilAsync(() => gate.Requests.Count > 0);

        // Card is pending; the info text still asks for confirmation.
        Assert.Contains(log.Messages, m => m.Role == ChatRole.Info && m.Text.Contains("等待你的确认"));

        pending.TrySetResult(new ToolConfirmationDecision(true, "D:\\part.prt"));
        await WaitForCompletedAsync(controller);

        Assert.Equal(1, executor.CallCount);
    }

    // ------------------------------------------------------------ autonomous fast path

    [Fact]
    public async Task AutonomousMode_AllowlistedTool_SkipsConfirmationAndExecutesOnce()
    {
        var (controller, sender, log, executor, gate, modeManager) = Create();
        modeManager.SetMode(ExecutionMode.Autonomous);

        sender.Enqueue(ScriptedSender.ToolCall(), () => ChatCompletionResult.OkText("完成。"));

        controller.StartSend("创建长方体");
        await WaitForCompletedAsync(controller);

        Assert.Empty(gate.Requests);            // no confirmation card
        Assert.Equal(1, executor.CallCount);    // executed exactly once
        Assert.Contains(log.Messages, m => m.Role == ChatRole.Info && m.Text.Contains("自主模式"));
    }

    [Fact]
    public async Task AutonomousMode_CylinderTool_SkipsConfirmation()
    {
        var (controller, sender, _, executor, gate, modeManager) = Create();
        modeManager.SetMode(ExecutionMode.Autonomous);

        sender.Enqueue(ScriptedSender.ToolCall(name: "create_cylinder", args: CylinderArgs),
            () => ChatCompletionResult.OkText("完成。"));

        controller.StartSend("创建圆柱");
        await WaitForCompletedAsync(controller);

        Assert.Empty(gate.Requests);
        Assert.Equal(1, executor.CallCount);
        Assert.Equal("create_cylinder", executor.LastToolName);
    }

    [Fact]
    public async Task AutonomousMode_AutonomousStatusMessageShowsBeforeSuccess()
    {
        var (controller, sender, log, _, gate, modeManager) = Create();
        modeManager.SetMode(ExecutionMode.Autonomous);

        sender.Enqueue(ScriptedSender.ToolCall(), () => ChatCompletionResult.OkText("完成。"));

        controller.StartSend("创建长方体");
        await WaitForCompletedAsync(controller);

        // The "starting" status line exists and never claims success on its own.
        var statusLine = log.Messages.First(m => m.Role == ChatRole.Info && m.Text.Contains("自主模式"));
        Assert.Contains("正在创建", statusLine.Text);
        Assert.DoesNotContain("成功", statusLine.Text);
        Assert.DoesNotContain("已创建", statusLine.Text);
    }

    // ------------------------------------------------------------ rejections

    [Fact]
    public async Task AutonomousMode_InvalidParameters_ExecutesNothing()
    {
        var (controller, sender, log, executor, gate, modeManager) = Create();
        modeManager.SetMode(ExecutionMode.Autonomous);

        sender.Enqueue(ScriptedSender.ToolCall(args: """{"length":-5,"width":60,"height":20,"unit":"mm"}"""),
            () => ChatCompletionResult.OkText("完成。"));

        controller.StartSend("创建长方体");
        await WaitForCompletedAsync(controller);

        Assert.Empty(gate.Requests);
        Assert.Equal(0, executor.CallCount);
        Assert.Contains(log.Messages, m => m.Text.Contains("参数校验未通过"));
    }

    [Fact]
    public async Task AutonomousMode_UnknownTool_ExecutesNothing()
    {
        var (controller, sender, log, executor, gate, modeManager) = Create();
        modeManager.SetMode(ExecutionMode.Autonomous);

        sender.Enqueue(ScriptedSender.ToolCall(name: "delete_body", args: "{}"),
            () => ChatCompletionResult.OkText("完成。"));

        controller.StartSend("删除");
        await WaitForCompletedAsync(controller);

        Assert.Empty(gate.Requests);
        Assert.Equal(0, executor.CallCount);
        Assert.Contains(log.Messages, m => m.Text.Contains("未注册"));
    }

    [Fact]
    public async Task AutonomousMode_UnknownField_ExecutesNothing()
    {
        var (controller, sender, _, executor, _, modeManager) = Create();
        modeManager.SetMode(ExecutionMode.Autonomous);

        sender.Enqueue(ScriptedSender.ToolCall(
                args: """{"length":100,"width":60,"height":20,"unit":"mm","evil":"x"}"""),
            () => ChatCompletionResult.OkText("完成。"));

        controller.StartSend("创建长方体");
        await WaitForCompletedAsync(controller);

        Assert.Equal(0, executor.CallCount);
    }

    // ------------------------------------------------------------ mode-switch races

    [Fact]
    public async Task PendingConfirmationCard_SwitchToAutonomous_DoesNotAutoExecute()
    {
        var (controller, sender, log, executor, gate, modeManager) = Create();
        gate.EnqueuePending(out var pending);
        sender.Enqueue(ScriptedSender.ToolCall(), () => ChatCompletionResult.OkText("完成。"));

        controller.StartSend("创建长方体");
        await UntilAsync(() => gate.Requests.Count > 0);

        // User flips the switch while the card is still pending.
        modeManager.SetMode(ExecutionMode.Autonomous);

        // Still nothing runs without an explicit decision.
        await Task.Delay(100);
        Assert.Equal(0, executor.CallCount);

        pending.TrySetResult(new ToolConfirmationDecision(true, "D:\\part.prt"));
        await WaitForCompletedAsync(controller);

        // The pending card completes only through the user's own decision.
        Assert.Equal(1, executor.CallCount);
        Assert.Equal("D:\\part.prt", executor.LastExpectedPath);
    }

    [Fact]
    public async Task PendingConfirmationCard_CancelledByUser_ExecutesNothing()
    {
        var (controller, sender, _, executor, gate, _) = Create();
        gate.EnqueuePending(out var pending);
        sender.Enqueue(ScriptedSender.ToolCall(), () => ChatCompletionResult.OkText("完成。"));

        controller.StartSend("创建长方体");
        await UntilAsync(() => gate.Requests.Count > 0);

        pending.TrySetResult(new ToolConfirmationDecision(false, null));
        await WaitForCompletedAsync(controller);

        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task AutonomousTurn_ModeSwitchedBackDuringModelWait_TakesConfirmationPath()
    {
        var (controller, sender, _, executor, gate, modeManager) = Create();
        modeManager.SetMode(ExecutionMode.Autonomous);

        // The first response is gated: the turn is in flight while the mode
        // flips back to Confirm, so the dispatch must go through the
        // confirmation delegate — it must never execute without having asked.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.Enqueue(async () =>
        {
            await release.Task;
            return ScriptedSender.ToolCall();
        });

        controller.StartSend("创建长方体");
        modeManager.SetMode(ExecutionMode.Confirm);
        release.TrySetResult();
        await WaitForCompletedAsync(controller);

        // The switch-back forced the confirmation flow (the default gate
        // handler confirms, so the executor runs once — but only after the
        // dispatch asked).
        Assert.Single(gate.Requests);
        Assert.Equal(1, executor.CallCount);
        Assert.Equal("D:\\part.prt", executor.LastExpectedPath);
    }

    [Fact]
    public async Task AutonomousExecution_NoDoubleSubmission()
    {
        var (controller, sender, _, executor, _, modeManager) = Create();
        modeManager.SetMode(ExecutionMode.Autonomous);

        // Hold round 1 in flight, then try to re-send — StartSend must refuse.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.Enqueue(async () =>
        {
            await release.Task;
            return ScriptedSender.ToolCall();
        });

        Assert.True(controller.StartSend("创建长方体"));
        await UntilAsync(() => sender.RequestCount > 0);
        Assert.False(controller.StartSend("再来一次"));

        release.TrySetResult();
        await WaitForCompletedAsync(controller);
        Assert.False(controller.IsRequestInFlight);

        Assert.Equal(1, executor.CallCount);
        Assert.Equal(2, sender.RequestCount); // round 1 (tool call) + round 2 (final text)
    }
}
