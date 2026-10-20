using System.Text.Json;
using NXAI.Assistant.Core;
using NXAI.Assistant.UI;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Live failure (2026-10-20 15:27, user report): after two successful
/// sketch+extrude pairs, a legitimate boolean-difference request was
/// blocked by the unknown-outcome gate — a leftover Attempting
/// boolean_operation entry (from an earlier intersect whose turn died)
/// met the gate, and ProbeCommittedFeature had NO boolean mapping →
/// returned null ("cannot judge") → the turn stopped. Two defects:
///   1. the host probe now maps boolean_operation to UNITE/SUBTRACT/
///      INTERSECT feature kinds (host-side, covered by the probe contract);
///   2. the CONTROLLER recovery must treat a boolean unknown state exactly
///      like any other tool: probe verdict decides — committed → recover +
///      stop; no trace → terminal failure and the request proceeds;
///      unjudgeable → report and stop. These tests lock the controller
///      behavior for boolean flows after continuous modeling.
/// </summary>
public class BooleanAfterContinuousModelingTests
{
    private const string SketchCallJson =
        """{"plane_mode":"wcs_xy","profile_type":"rectangle","width":20,"height":20,"center_u":20,"center_v":0,"unit":"mm"}""";
    private const string ExtrudeCallJson =
        """{"sketch_ref":"SKETCH_000","distance":20,"unit":"mm"}""";
    private const string BooleanDiffCallJson =
        """{"operation":"subtract","target_body_ref":"BODY_001","tool_body_ref":"BODY_002"}""";

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
            string id, string name, string args) =>
            ChatCompletionResult.OkToolCalls([new ParsedToolCall(id, name, args)]);
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

    /// <summary>Scriptable executor: records boolean calls, returns
    /// successful extrudes/sketches, and can fail the boolean op.</summary>
    private sealed class ScriptableExecutor : INxToolExecutor
    {
        public int BooleanCallCount { get; private set; }
        public List<BooleanOperationParameters> BooleanCalls { get; } = new();
        public ToolExecutionStatus BooleanStatus { get; set; } = ToolExecutionStatus.Success;
        public Func<string, string, ModelFeatureProbeResult?>? ProbeOverride { get; set; }
        public int ProbeCallCount { get; private set; }

        private static CreateBlockExecutionResult Ok() =>
            CreateBlockExecutionResult.Success(1, 1, 1, "ok", "part");

        public CreateBlockExecutionResult ExecuteCreateBlock(
            CreateBlockParameters parameters, string? expectedWorkPartFullPath) => Ok();

        public CreateBlockExecutionResult ExecuteCreateCylinder(
            CreateCylinderParameters parameters, string? expectedWorkPartFullPath) => Ok();

        public CreateBlockExecutionResult ExecuteCreateCone(
            CreateConeParameters parameters, string? expectedWorkPartFullPath) => Ok();

        public CreateBlockExecutionResult ExecuteCreateSphere(
            CreateSphereParameters parameters, string? expectedWorkPartFullPath) => Ok();

        public CreateBlockExecutionResult ExecuteCreateSketch(
            CreateSketchParameters parameters, string? expectedWorkPartFullPath)
        {
            string details = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["sketchRef"] = $"SKETCH_{SketchCounter++:000}",
                ["planeMode"] = parameters.PlaneMode.ToString(),
                ["profileType"] = parameters.ProfileType.ToString(),
                ["width"] = parameters.RectangleWidth,
                ["height"] = parameters.RectangleHeight,
                ["diameter"] = parameters.CircleDiameter,
                ["centerU"] = parameters.CenterU,
                ["centerV"] = parameters.CenterV,
            });
            return CreateBlockExecutionResult.Success(
                parameters.RectangleWidth > 0 ? parameters.RectangleWidth : parameters.CircleDiameter,
                parameters.RectangleHeight > 0 ? parameters.RectangleHeight : parameters.CircleDiameter,
                0, "草图已创建。", "part", details);
        }

        private int SketchCounter;

        public CreateBlockExecutionResult ExecuteCreateExtrude(
            CreateExtrudeParameters parameters, string? expectedWorkPartFullPath)
        {
            string details = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["sketchRef"] = parameters.SketchRef,
                ["newBodyRef"] = $"BODY_{BodyCounter++:000}",
            });
            return CreateBlockExecutionResult.Success(
                parameters.Distance, parameters.Distance, parameters.Distance,
                "已拉伸。", "part", details);
        }

        private int BodyCounter = 1;

        public CreateBlockExecutionResult ExecuteCreateRevolve(
            CreateRevolveParameters parameters, string? expectedWorkPartFullPath) => Ok();

        public CreateBlockExecutionResult ExecuteBooleanOperation(
            BooleanOperationParameters parameters, string? expectedWorkPartFullPath)
        {
            BooleanCallCount++;
            BooleanCalls.Add(parameters);
            if (BooleanStatus != ToolExecutionStatus.Success)
            {
                return CreateBlockExecutionResult.Failure(
                    BooleanStatus, "布尔运算结果与预期不符（实体数量为 2，预期 1）", rolledBack: true);
            }
            string details = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["operation"] = parameters.Operation.ToString().ToLowerInvariant(),
                ["resultBodyRef"] = "BODY_100",
                ["toolConsumed"] = true,
            });
            return CreateBlockExecutionResult.Success(0, 0, 0,
                "已完成布尔差集（结果体引用 BODY_100）。", "part", details);
        }

        public ModelFeatureProbeResult? ProbeCommittedFeature(string toolName, string operationSummary)
        {
            ProbeCallCount++;
            return ProbeOverride?.Invoke(toolName, operationSummary);
        }
    }

    private static (ChatSessionController, ScriptedSender, ChatLog, ScriptableExecutor, ConfirmationGate)
        CreateAutonomous(IModelingExecutionLedger? ledger = null)
    {
        var log = new ChatLog();
        var sender = new ScriptedSender();
        var executor = new ScriptableExecutor();
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

    // ----------------------------- requirement 五.1/2: the live flow works

    [Fact]
    public async Task Sketch_Extrude_ThenBooleanDifference_ProceedsToExecution()
    {
        // The live flow with NO leftover unknown state: two sketches, two
        // extrudes (multi-call turns), then a boolean difference — the gate
        // must NOT fire (all previous attempts terminated as Recorded) and
        // the boolean must reach the executor with parsed parameters.
        var ledger = new ModelingExecutionLedger();
        var (controller, sender, log, executor, gate) = CreateAutonomous(ledger);

        // Turn 1: sketch + extrude (one multi-call message).
        sender.Enqueue(
            ChatCompletionResult.OkToolCalls(
            [
                new ParsedToolCall("c1", "create_sketch", SketchCallJson),
                new ParsedToolCall("c2", "create_extrude", ExtrudeCallJson),
            ]),
            () => ChatCompletionResult.OkText("第一个实体已创建。"));
        controller.StartSend("创建草图并拉伸");
        await WaitForCompletedAsync(controller);

        // Turn 2: the boolean difference request.
        sender.Enqueue(
            ScriptedSender.ToolCall("c3", "boolean_operation", BooleanDiffCallJson),
            () => ChatCompletionResult.OkText("差集完成。"));
        controller.StartSend("执行布尔差集，保留第一个实体");
        await WaitForCompletedAsync(controller);

        Assert.Equal(1, executor.BooleanCallCount);
        Assert.Empty(gate.Requests);
        var diff = executor.BooleanCalls[0];
        Assert.Equal(BooleanOperationType.Subtract, diff.Operation);
        // "保留第一个实体" → target = the first body, tool = the second.
        Assert.Equal("BODY_001", diff.TargetBodyRef);
        Assert.Equal("BODY_002", diff.ToolBodyRef);
        // No unknown-state messages anywhere.
        Assert.DoesNotContain(log.Messages, m => m.Text.Contains("未确认的上一轮执行结果"));
    }

    // ------------------------- requirement 五.3/4: leftover unknown states

    [Fact]
    public async Task LeftoverBooleanUnknown_ProbeFindsNoTrace_RequestProceeds()
    {
        // A leftover Attempting boolean entry (the live trigger) + probe
        // proves no committed boolean feature → the entry terminates as
        // failed AND the boolean difference proceeds on its own merit.
        var ledger = new ModelingExecutionLedger();
        ledger.BeginAttempt("boolean_operation", "布尔交集 未知结果");
        var (controller, sender, log, executor, gate) = CreateAutonomous(ledger);
        executor.ProbeOverride = (_, _) => ModelFeatureProbeResult.NotCommitted(
            "当前工作部件中没有任何已提交的 UNITE/SUBTRACT/INTERSECT 特征。");

        sender.Enqueue(
            ScriptedSender.ToolCall("c3", "boolean_operation", BooleanDiffCallJson),
            () => ChatCompletionResult.OkText("差集完成。"));
        controller.StartSend("执行布尔差集，保留第一个实体");
        await WaitForCompletedAsync(controller);

        Assert.Equal(1, executor.BooleanCallCount);       // proceeded
        Assert.Empty(gate.Requests);                      // NO dialog
        var entry = Assert.Single(ledger.Snapshot(), e => e.LedgerId == "ledger_1");
        Assert.Equal(ModelingExecutionState.Recorded, entry.State); // resolved
        Assert.False(ledger.HasUnacknowledgedAttempt("boolean_operation"));
    }

    [Fact]
    public async Task LeftoverBooleanUnknown_ProbeFindsCommitted_StopsNoDuplicate()
    {
        // The probe finds a committed boolean feature (the 13:53 intersect
        // case): recover the pending entry as a terminating fact and STOP —
        // the difference must not run on top of an unresolved state.
        var ledger = new ModelingExecutionLedger();
        ledger.BeginAttempt("boolean_operation", "布尔交集 未知结果");
        var (controller, sender, log, executor, gate) = CreateAutonomous(ledger);
        executor.ProbeOverride = (_, _) => ModelFeatureProbeResult.CommittedFound(
            "在当前工作部件中发现 1 个已提交的 INTERSECT 特征（F1）。");

        sender.Enqueue(
            ScriptedSender.ToolCall("c3", "boolean_operation", BooleanDiffCallJson),
            () => ChatCompletionResult.OkText("不应到达。"));
        controller.StartSend("执行布尔差集，保留第一个实体");
        await WaitForCompletedAsync(controller);

        Assert.Equal(0, executor.BooleanCallCount);       // no duplicate
        Assert.Empty(gate.Requests);                      // NO dialog
        var entry = Assert.Single(ledger.Snapshot(), e => e.LedgerId == "ledger_1");
        Assert.Equal(ModelingExecutionState.Recorded, entry.State);
        Assert.Contains("已提交的 INTERSECT 特征", entry.ResultMessage);
        Assert.Contains(log.Messages, m => m.Text.Contains("避免重复建模"));
    }

    [Fact]
    public async Task LeftoverBooleanUnknown_ProbeCannotJudge_SafeStopWithGuidance()
    {
        // Probe cannot judge: report + stop; the entry stays honestly
        // Attempting; no dialog, no retry, no downgrade.
        var ledger = new ModelingExecutionLedger();
        ledger.BeginAttempt("boolean_operation", "布尔交集 未知结果");
        var (controller, sender, log, executor, gate) = CreateAutonomous(ledger);
        sender.Enqueue(
            ScriptedSender.ToolCall("c3", "boolean_operation", BooleanDiffCallJson),
            () => ChatCompletionResult.OkText("不应到达。"));

        controller.StartSend("执行布尔差集，保留第一个实体");
        await WaitForCompletedAsync(controller);

        Assert.Equal(0, executor.BooleanCallCount);
        Assert.Empty(gate.Requests);
        var entry = Assert.Single(ledger.Snapshot(), e => e.LedgerId == "ledger_1");
        Assert.Equal(ModelingExecutionState.Attempting, entry.State);
        Assert.True(ledger.HasUnacknowledgedAttempt("boolean_operation"));
        Assert.Contains(log.Messages, m => m.Role == ChatRole.Error && m.Text.Contains("无法判断"));
    }

    // ----------------------------- requirement 五.5: invalid body references

    [Fact]
    public async Task BooleanDiff_WithInvalidBodyRef_ExecutorRefuses_NoMisoperation()
    {
        // A stale/unknown body reference reaches the executor (the gate is
        // open, parameters parse) — the executor refuses by contract; the
        // controller passes the failure through without retry.
        var ledger = new ModelingExecutionLedger();
        var (controller, sender, log, executor, _) = CreateAutonomous(ledger);
        executor.BooleanStatus = ToolExecutionStatus.PreconditionsChanged;
        sender.Enqueue(
            ScriptedSender.ToolCall("c3", "boolean_operation",
                """{"operation":"subtract","target_body_ref":"BODY_GONE","tool_body_ref":"BODY_002"}"""),
            () => ChatCompletionResult.OkText("未能执行。"));

        controller.StartSend("执行布尔差集，保留第一个实体");
        await WaitForCompletedAsync(controller);

        Assert.Single(executor.BooleanCalls);
        Assert.Equal("BODY_GONE", executor.BooleanCalls[0].TargetBodyRef);
        var second = sender.Requests[1];
        var toolMsg = second.Single(m => m.Role == OutboundMessage.RoleTool);
        using var doc = JsonDocument.Parse(toolMsg.Content!);
        Assert.Equal("preconditions_changed", doc.RootElement.GetProperty("status").GetString());
        _ = log;
    }

    // ---------------------------- requirement 五.6: modes/cancel no regress

    [Fact]
    public async Task ConfirmMode_BooleanDiff_StillUsesConfirmationDialog()
    {
        var ledger = new ModelingExecutionLedger();
        var log = new ChatLog();
        var sender = new ScriptedSender();
        var executor = new ScriptableExecutor();
        var gate = new ConfirmationGate();
        var controller = new ChatSessionController(
            new ModelService(), log, () => ("https://api.test/v1", "key", "model"),
            sender.SendAsync, 20, executor, gate.Handler,
            executionLedger: ledger); // default Confirm mode

        sender.Enqueue(
            ScriptedSender.ToolCall("c3", "boolean_operation", BooleanDiffCallJson),
            () => ChatCompletionResult.OkText("已执行。"));
        controller.StartSend("执行布尔差集，保留第一个实体");
        await UntilAsync(() => gate.Requests.Count >= 1);

        // Confirm mode: the card arrives (boolean is NOT on the autonomous
        // allowlist), the probe does not run, approving executes once.
        var card = Assert.Single(gate.Requests);
        Assert.Equal(ToolParametersKind.Boolean, card.ParametersKind);
        Assert.Equal(0, executor.ProbeCallCount);
        card.TryRespond(new ToolConfirmationDecision(true, null));
        await WaitForCompletedAsync(controller);
        Assert.Equal(1, executor.BooleanCallCount);
    }

    [Fact]
    public async Task UserCancel_DuringBooleanTurn_LeavesUnknownStateHandled()
    {
        // A cancel mid-turn (after BeginAttempt, before the executor
        // returns) leaves the Attempting entry — by design. The NEXT
        // request recovers via the probe (no trace → proceed), which is
        // the exact protection the live failure needed.
        var ledger = new ModelingExecutionLedger();
        var (controller, sender, log, executor, _) = CreateAutonomous(ledger);
        sender.Enqueue(
            ScriptedSender.ToolCall("c3", "boolean_operation", BooleanDiffCallJson),
            () => ChatCompletionResult.OkText("不应到达。"));

        controller.StartSend("执行布尔差集，保留第一个实体");
        // Cancel before the executor finishes — the executor here returns
        // synchronously, so cancel the turn right after it starts and let
        // the structured flow complete; the point is the ledger state and
        // the NEXT turn's recovery.
        await UntilAsync(() => executor.BooleanCallCount >= 1);
        controller.CancelActiveRequest();
        await WaitForCompletedAsync(controller);

        // The completed executor call recorded a terminating fact — no
        // leftover unknown state from a synchronous completion.
        Assert.DoesNotContain(ledger.Snapshot(),
            e => e.ToolName == "boolean_operation" && e.State == ModelingExecutionState.Attempting);

        // A follow-up request proceeds without the gate.
        sender.Enqueue(
            ScriptedSender.ToolCall("c4", "boolean_operation", BooleanDiffCallJson),
            () => ChatCompletionResult.OkText("完成。"));
        controller.StartSend("再次执行布尔差集");
        await WaitForCompletedAsync(controller);
        Assert.Equal(2, executor.BooleanCallCount);
        _ = log;
    }
}
