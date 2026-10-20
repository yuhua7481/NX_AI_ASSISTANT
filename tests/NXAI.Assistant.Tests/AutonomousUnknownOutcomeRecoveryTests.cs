using System.Text.Json;
using NXAI.Assistant.Core;
using NXAI.Assistant.UI;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Autonomous-mode unknown-outcome recovery (live failure, 2026-10-19):
/// a revolve attempt died post-commit with "试图使用不活动的对象"-class
/// exceptions; a repeat request then popped the human confirmation modal
/// "存在未确认结果——请先核实部件" EVEN IN AUTONOMOUS MODE. The contract now:
///   - autonomous mode NEVER routes an unknown-outcome state to the human
///     confirmation dialog;
///   - a read-only NX probe decides: committed geometry → recover as a
///     terminating fact and STOP (no duplicate creation); no trace → the
///     attempt is terminally failed and the turn proceeds; unjudgeable →
///     report + stop;
///   - confirm mode keeps its existing authorization flow untouched.
/// </summary>
public class AutonomousUnknownOutcomeRecoveryTests
{
    private const string ToolCallJson =
        """{"length":100,"width":60,"height":20,"unit":"mm"}""";

    private sealed class ScriptedSender
    {
        private readonly Queue<object> _responses = new();
        public List<IReadOnlyList<OutboundMessage>> Requests { get; } = new();

        public void Enqueue(params object[] responses)
        {
            foreach (var r in responses) _responses.Enqueue(r);
        }

        public Task<ChatCompletionResult> SendAsync(
            IReadOnlyList<OutboundMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            CancellationToken ct)
        {
            Requests.Add(messages);
            var item = _responses.Dequeue();
            return Task.FromResult(item switch
            {
                Func<ChatCompletionResult> f => f(),
                ChatCompletionResult r => r,
                _ => throw new InvalidOperationException("Bad scripted response type"),
            });
        }

        public static ChatCompletionResult ToolCall(
            string id = "call_1", string name = "create_block", string? args = null) =>
            ChatCompletionResult.OkToolCalls(
                [new ParsedToolCall(id, name, args ?? ToolCallJson)]);
    }

    private sealed class ConfirmationGate
    {
        public List<ToolConfirmationRequest> Requests { get; } = new();

        public Task<ToolConfirmationDecision?> Handler(
            ToolConfirmationRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return request.Response;
        }
    }

    private sealed class ProbeExecutor : INxToolExecutor
    {
        public int CallCount { get; private set; }
        public int ProbeCallCount { get; private set; }

        private CreateBlockExecutionResult Result() =>
            CreateBlockExecutionResult.Success(1, 1, 1, "ok", "part");

        public CreateBlockExecutionResult ExecuteCreateBlock(
            CreateBlockParameters parameters, string? expectedWorkPartFullPath)
        {
            CallCount++;
            return Result();
        }

        public CreateBlockExecutionResult ExecuteCreateCylinder(
            CreateCylinderParameters parameters, string? expectedWorkPartFullPath) { CallCount++; return Result(); }

        public CreateBlockExecutionResult ExecuteCreateCone(
            CreateConeParameters parameters, string? expectedWorkPartFullPath) { CallCount++; return Result(); }

        public CreateBlockExecutionResult ExecuteCreateSphere(
            CreateSphereParameters parameters, string? expectedWorkPartFullPath) { CallCount++; return Result(); }

        public CreateBlockExecutionResult ExecuteCreateSketch(
            CreateSketchParameters parameters, string? expectedWorkPartFullPath) { CallCount++; return Result(); }

        public CreateBlockExecutionResult ExecuteCreateExtrude(
            CreateExtrudeParameters parameters, string? expectedWorkPartFullPath) { CallCount++; return Result(); }

        public CreateBlockExecutionResult ExecuteCreateRevolve(
            CreateRevolveParameters parameters, string? expectedWorkPartFullPath) { CallCount++; return Result(); }

        public CreateBlockExecutionResult ExecuteBooleanOperation(
            BooleanOperationParameters parameters, string? expectedWorkPartFullPath) { CallCount++; return Result(); }

        public ModelFeatureProbeResult? ProbeCommittedFeature(string toolName, string operationSummary)
        {
            ProbeCallCount++;
            return ProbeOverride?.Invoke(toolName, operationSummary);
        }
        public ModelFeatureProbeResult? ProbeCommittedFeature(
            string toolName, string operationSummary, IReadOnlySet<string>? preExistingFeatureIds) =>
            ProbeCommittedFeature(toolName, operationSummary);

        public IReadOnlySet<string>? ProbeFeatureSnapshot(string toolName) => null;

        /// <summary>Set to control the probe answer; null fn = cannot judge.</summary>
        public Func<string, string, ModelFeatureProbeResult?>? ProbeOverride { get; set; }
    }

    private static (ChatSessionController, ScriptedSender, ChatLog, ProbeExecutor, ConfirmationGate)
        CreateAutonomous(IModelingExecutionLedger? ledger = null)
    {
        var log = new ChatLog();
        var sender = new ScriptedSender();
        var executor = new ProbeExecutor();
        var gate = new ConfirmationGate();
        var modeManager = new ExecutionModeManager();
        modeManager.SetMode(ExecutionMode.Autonomous);
        var controller = new ChatSessionController(
            new ModelService(), log, () => ("https://api.test/v1", "key", "model"),
            sender.SendAsync, 20, executor, gate.Handler,
            modeManager: modeManager, executionLedger: ledger);
        return (controller, sender, log, executor, gate);
    }

    private static Task WaitForCompletedAsync(ChatSessionController controller)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.RequestCompleted += (_, _) => tcs.TrySetResult();
        return Task.WhenAny(tcs.Task, Task.Delay(15_000));
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > 15_000)
                throw new TimeoutException("条件等待超时");
            await Task.Delay(10);
        }
    }

    // ------------------------------------- NO modal in autonomous recovery

    [Fact]
    public async Task Autonomous_UnknownOutcome_ProbeFindsNothing_ProceedsWithoutDialog()
    {
        // A leftover Attempting entry (the live shape) + probe proves NO
        // committed trace → the entry terminates as failed, the confirmation
        // gate is NEVER touched, and the new call executes normally.
        var ledger = new ModelingExecutionLedger();
        ledger.BeginAttempt("create_block", "长方体 100×60×20mm");
        var (controller, sender, log, executor, gate) = CreateAutonomous(ledger);
        executor.ProbeOverride = (_, _) => ModelFeatureProbeResult.NotCommitted(
            "当前工作部件中没有任何已提交的 BLOCK 特征。");
        sender.Enqueue(ScriptedSender.ToolCall(),
            () => ChatCompletionResult.OkText("已按请求执行。"));

        controller.StartSend("创建长方体 100x60x20 毫米");
        await WaitForCompletedAsync(controller);

        Assert.Equal(1, executor.ProbeCallCount);         // probe ran
        Assert.Equal(1, executor.CallCount);              // the new call executed
        Assert.Empty(gate.Requests);                      // NO human dialog
        // The pending entry is terminally resolved (not Attempting anymore);
        // the new call's own success is a second Recorded entry.
        Assert.Equal(2, ledger.Snapshot().Count);
        var entry = Assert.Single(ledger.Snapshot(), e => e.LedgerId == "ledger_1");
        Assert.Equal(ModelingExecutionState.Recorded, entry.State);
        Assert.Equal(ToolExecutionStatus.ExecutionFailed, entry.Status);
        Assert.False(ledger.HasUnacknowledgedAttempt("create_block"));
        // The continuation is visible in the chat.
        Assert.Contains(log.Messages, m => m.Text.Contains("只读核查确认"));
    }

    [Fact]
    public async Task Autonomous_UnknownOutcome_ProbeFindsCommitted_StopsWithoutDuplicate()
    {
        // The probe finds committed geometry: the turn STOPS — the new call
        // never executes (no duplicate modeling), the pending entry is
        // recovered as a terminating fact, and NO human dialog appears.
        var ledger = new ModelingExecutionLedger();
        ledger.BeginAttempt("create_block", "长方体 100×60×20mm");
        var (controller, sender, log, executor, gate) = CreateAutonomous(ledger);
        executor.ProbeOverride = (_, _) => ModelFeatureProbeResult.CommittedFound(
            "在当前工作部件中发现 1 个已提交的 BLOCK 特征（F1）。");
        sender.Enqueue(ScriptedSender.ToolCall(),
            () => ChatCompletionResult.OkText("不应到达的最终回复。"));

        controller.StartSend("创建长方体 100x60x20 毫米");
        await WaitForCompletedAsync(controller);

        Assert.Equal(1, executor.ProbeCallCount);
        Assert.Equal(0, executor.CallCount);              // NOT re-executed
        Assert.Empty(gate.Requests);                      // NO human dialog
        // The pending entry carries the probe finding as a terminating fact.
        var entry = Assert.Single(ledger.Snapshot());
        Assert.Equal(ModelingExecutionState.Recorded, entry.State);
        Assert.Contains("已提交的 BLOCK 特征", entry.ResultMessage);
        // The chat explains the stop and the recovery.
        Assert.Contains(log.Messages, m => m.Text.Contains("发现部件中已有该类已提交特征"));
        Assert.Contains(log.Messages, m => m.Text.Contains("避免重复建模"));
    }

    [Fact]
    public async Task Autonomous_UnknownOutcome_ProbeCannotJudge_ReportsAndStops()
    {
        // Probe returns null (cannot judge): the turn ends with a clear
        // report, NOTHING executes, NO dialog, and the entry stays
        // Attempting (never downgraded to "not executed").
        var ledger = new ModelingExecutionLedger();
        ledger.BeginAttempt("create_block", "长方体 100×60×20mm");
        var (controller, sender, log, executor, gate) = CreateAutonomous(ledger);
        sender.Enqueue(ScriptedSender.ToolCall(),
            () => ChatCompletionResult.OkText("不应到达。"));

        controller.StartSend("创建长方体");
        await WaitForCompletedAsync(controller);

        Assert.Equal(1, executor.ProbeCallCount);
        Assert.Equal(0, executor.CallCount);              // nothing executed
        Assert.Empty(gate.Requests);                      // NO human dialog
        var entry = Assert.Single(ledger.Snapshot());
        Assert.Equal(ModelingExecutionState.Attempting, entry.State); // honest unknown
        Assert.True(ledger.HasUnacknowledgedAttempt("create_block"));
        Assert.Contains(log.Messages, m => m.Role == ChatRole.Error && m.Text.Contains("无法判断"));
    }

    [Fact]
    public async Task Autonomous_UnknownOutcome_ProbeThrows_TreatedAsUnjudgeable()
    {
        // A throwing probe is the same as "cannot judge": report + stop.
        var ledger = new ModelingExecutionLedger();
        ledger.BeginAttempt("create_block", "长方体");
        var (controller, sender, log, executor, gate) = CreateAutonomous(ledger);
        executor.ProbeOverride = (_, _) => throw new InvalidOperationException("probe exploded");
        sender.Enqueue(ScriptedSender.ToolCall(),
            () => ChatCompletionResult.OkText("不应到达。"));

        controller.StartSend("创建长方体");
        await WaitForCompletedAsync(controller);

        Assert.Equal(0, executor.CallCount);
        Assert.Empty(gate.Requests);
        Assert.Contains(log.Messages, m => m.Role == ChatRole.Error && m.Text.Contains("无法判断"));
        var entry = Assert.Single(ledger.Snapshot());
        Assert.Equal(ModelingExecutionState.Attempting, entry.State);
    }

    // ------------------------------------- confirm mode keeps its dialog

    [Fact]
    public async Task ConfirmMode_UnknownOutcome_StillUsesConfirmationDialog()
    {
        // Confirm mode is untouched: the unknown gate routes through the
        // normal confirmation flow (the dialog doubles as the verification
        // step), and approving executes exactly once.
        var ledger = new ModelingExecutionLedger();
        ledger.BeginAttempt("create_block", "长方体");
        var log = new ChatLog();
        var sender = new ScriptedSender();
        var executor = new ProbeExecutor();
        var gate = new ConfirmationGate();
        var controller = new ChatSessionController(
            new ModelService(), log, () => ("https://api.test/v1", "key", "model"),
            sender.SendAsync, 20, executor, gate.Handler,
            executionLedger: ledger); // default Confirm mode

        sender.Enqueue(ScriptedSender.ToolCall(),
            () => ChatCompletionResult.OkText("已执行。"));
        controller.StartSend("创建长方体");
        await UntilAsync(() => gate.Requests.Count >= 1);

        // The confirmation card arrived (confirm mode keeps its dialog) and
        // the probe did NOT run (recovery is autonomous-only).
        Assert.Single(gate.Requests);
        Assert.Equal(0, executor.ProbeCallCount);
        gate.Requests[0].TryRespond(new ToolConfirmationDecision(true, null));
        await WaitForCompletedAsync(controller);
        Assert.Equal(1, executor.CallCount);
    }

    // ------------------------------------- first attempt was fine

    [Fact]
    public async Task Autonomous_FreshRequest_WithoutUnknownState_NeverProbes()
    {
        // No leftover unknown state: the probe must not run at all (zero
        // overhead on the normal path).
        var ledger = new ModelingExecutionLedger();
        var (controller, sender, log, executor, gate) = CreateAutonomous(ledger);
        sender.Enqueue(ScriptedSender.ToolCall(),
            () => ChatCompletionResult.OkText("完成。"));

        controller.StartSend("创建长方体");
        await WaitForCompletedAsync(controller);

        Assert.Equal(0, executor.ProbeCallCount);
        Assert.Equal(1, executor.CallCount);
        Assert.Empty(gate.Requests);
    }
}
