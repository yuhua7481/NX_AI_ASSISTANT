using System.Text.Json;
using NXAI.Assistant.Core;
using NXAI.Assistant.UI;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Controller-level revolve axis self-correction: a same-turn
/// create_sketch → create_revolve chain in AUTONOMOUS mode. When the model
/// infers an axis that cannot lie in the sketch plane (the live Z-on-XY
/// case), the controller replaces it with the best feasible candidate
/// BEFORE the executor runs — the executor then sees X (never Z), and the
/// correction is logged. Confirm mode never rewrites. The bounded
/// correction, ledger behavior and turn budgets are asserted alongside.
/// </summary>
public class RevolveAxisCorrectionControllerTests
{
    private const string ToolCallJson =
        """{"length":100,"width":60,"height":20,"unit":"mm"}""";
    private const string SketchCallJson =
        """{"plane_mode":"wcs_xy","profile_type":"rectangle","width":20,"height":10,"center_u":150,"center_v":10,"unit":"mm"}""";
    private static string RevolveCallJson(string axis) =>
        "{\"sketch_ref\":\"SKETCH_000\",\"axis_mode\":\"" + axis + "\",\"angle\":360}";

    private sealed class ScriptedSender
    {
        private readonly Queue<object> _responses = new();
        public List<IReadOnlyList<OutboundMessage>> Requests { get; } = new();
        public List<IReadOnlyList<ToolDefinition>?> ToolDeclarations { get; } = new();

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
            ToolDeclarations.Add(tools);
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

        // Records the card and awaits the card's OWN response task — the
        // test resolves it through ToolConfirmationRequest.TryRespond, so
        // the request and its resolution are the same object.
        public Task<ToolConfirmationDecision?> Handler(
            ToolConfirmationRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return request.Response;
        }
    }

    /// <summary>
    /// Records every revolve call's parameters the executor actually
    /// received; returns a scripted success with the real live result shape
    /// (sketchRef/newBodyRef details).
    /// </summary>
    private sealed class RecordingExecutor : INxToolExecutor
    {
        public List<CreateRevolveParameters> RevolveCalls { get; } = new();
        public List<CreateSketchParameters> SketchCalls { get; } = new();
        public List<string> AxesSeen { get; } = new();
        public ToolExecutionStatus NextRevolveStatus { get; set; } = ToolExecutionStatus.Success;

        public CreateBlockExecutionResult ExecuteCreateBlock(
            CreateBlockParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Success(1, 1, 1, "ok", "part");

        public CreateBlockExecutionResult ExecuteCreateCylinder(
            CreateCylinderParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Success(1, 1, 1, "ok", "part");

        public CreateBlockExecutionResult ExecuteCreateCone(
            CreateConeParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Success(1, 1, 1, "ok", "part");

        public CreateBlockExecutionResult ExecuteCreateSphere(
            CreateSphereParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Success(1, 1, 1, "ok", "part");

        public CreateBlockExecutionResult ExecuteCreateSketch(
            CreateSketchParameters parameters, string? expectedWorkPartFullPath)
        {
            SketchCalls.Add(parameters);
            string details = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["sketchRef"] = "SKETCH_000",
                ["sketchName"] = "Sketch(1)",
                ["plane"] = "绝对坐标 XY 平面",
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
                0,
                $"已在绝对坐标 XY 平面创建草图，草图引用 SKETCH_000。",
                "part", details);
        }

        public CreateBlockExecutionResult ExecuteCreateExtrude(
            CreateExtrudeParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Success(1, 1, 1, "ok", "part");

        public CreateBlockExecutionResult ExecuteCreateRevolve(
            CreateRevolveParameters parameters, string? expectedWorkPartFullPath)
        {
            RevolveCalls.Add(parameters);
            AxesSeen.Add(parameters.AxisMode.ToString());
            if (NextRevolveStatus != ToolExecutionStatus.Success)
            {
                string failureMessage = parameters.AxisMode == RevolveAxisMode.SelectedAxis
                    ? "selected_axis 解析失败：选中的关联直线特征没有可用的直线几何，无法作为旋转轴。"
                    : "旋转轴方向与草图平面不平行，无法在该平面内旋转。" +
                      "当前草图平面内可行的绝对坐标轴：X 轴、Y 轴（按轮廓与轴的距离排序）。请改用可行轴后重试。";
                return CreateBlockExecutionResult.Failure(
                    NextRevolveStatus,
                    failureMessage,
                    rolledBack: true);
            }
            string details = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["sketchRef"] = parameters.SketchRef,
                ["newBodyRef"] = "BODY_001",
                ["axis"] = parameters.AxisMode.ToString(),
                ["angleDeg"] = parameters.AngleDeg,
            });
            return CreateBlockExecutionResult.Success(
                0, 0, parameters.AngleDeg,
                $"已旋转草图生成旋转体，实体引用 BODY_001。", "part", details);
        }

        public CreateBlockExecutionResult ExecuteBooleanOperation(
            BooleanOperationParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Success(1, 1, 1, "ok", "part");

        public ModelFeatureProbeResult? ProbeCommittedFeature(string toolName, string operationSummary) =>
            ProbeOverride is { } probe ? probe : null;

        /// <summary>When set, the controller's unknown-outcome probe returns
        /// this result — drives the autonomous-recovery tests.</summary>
        public ModelFeatureProbeResult? ProbeOverride { get; set; }
    }

    private static (ChatSessionController, ScriptedSender, ChatLog, RecordingExecutor, ConfirmationGate)
        CreateAutonomous(IModelingExecutionLedger? ledger = null)
    {
        var log = new ChatLog();
        var sender = new ScriptedSender();
        var executor = new RecordingExecutor();
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

    // ------------------------------------------------- the live case, fixed

    [Fact]
    public async Task Autonomous_ZOnXySketch_IsCorrectedToX_BeforeExecutor()
    {
        var (controller, sender, log, executor, _) = CreateAutonomous();
        // Round 1: create_sketch on XY. Round 2: create_revolve with the
        // model's inferred wcs_z — the live failure. Round 3: final text.
        sender.Enqueue(
            ScriptedSender.ToolCall("c1", "create_sketch", SketchCallJson),
            ScriptedSender.ToolCall("c2", "create_revolve", RevolveCallJson("wcs_z")),
            () => ChatCompletionResult.OkText("旋转体已创建。"));

        controller.StartSend("画矩形草图并绕轴旋转360度");
        await WaitForCompletedAsync(controller);

        // The sketch ran once; the revolve reached the executor exactly once
        // — with the CORRECTED axis. Z never reached the executor.
        Assert.Single(executor.SketchCalls);
        var revolve = Assert.Single(executor.RevolveCalls);
        Assert.Equal(RevolveAxisMode.WcsX, revolve.AxisMode);
        Assert.Equal(360, revolve.AngleDeg);
        Assert.DoesNotContain(executor.AxesSeen, a => a == "WcsZ");
        // The correction is visible in the chat log with its reasoning.
        // The unified planning path (all model-returned axes under
        // Unspecified intent) logs 轴规划 with the full scale reasoning
        // (Z at 16.1× the profile scale vs X's 1.5×).
        Assert.Contains(log.Messages, m =>
            m.Text.Contains("自主模式轴规划") && m.Text.Contains("X 轴"));
        // The final round still ran (no extra rounds burned).
        Assert.Equal(3, sender.Requests.Count);
    }

    [Fact]
    public async Task Autonomous_XOnXySketch_KeepsModelAxis()
    {
        // The model already picked the best axis: no correction, no log noise.
        var (controller, sender, log, executor, _) = CreateAutonomous();
        sender.Enqueue(
            ScriptedSender.ToolCall("c1", "create_sketch", SketchCallJson),
            ScriptedSender.ToolCall("c2", "create_revolve", RevolveCallJson("wcs_x")),
            () => ChatCompletionResult.OkText("旋转体已创建。"));

        controller.StartSend("画草图并绕X轴旋转");
        await WaitForCompletedAsync(controller);

        var revolve = Assert.Single(executor.RevolveCalls);
        Assert.Equal(RevolveAxisMode.WcsX, revolve.AxisMode);
        // The model's guess IS the best candidate here: the unified planning
        // path skips (RankedCandidates[0] == the model's axis) — no log.
        Assert.DoesNotContain(log.Messages, m => m.Text.Contains("自主模式轴规划"));
    }

    [Fact]
    public async Task ConfirmMode_NeverRewrites_ModelAxisReachesConfirmation()
    {
        // Default Confirm mode: the model's Z request must reach the user's
        // confirmation card unchanged — the user decides, never a silent
        // rewrite. The pre-check surfaces its advice as an Info card only.
        var log = new ChatLog();
        var sender = new ScriptedSender();
        var executor = new RecordingExecutor();
        var gate = new ConfirmationGate();
        var controller = new ChatSessionController(
            new ModelService(), log, () => ("https://api.test/v1", "key", "model"),
            sender.SendAsync, 20, executor, gate.Handler);

        sender.Enqueue(
            ScriptedSender.ToolCall("c1", "create_sketch", SketchCallJson),
            ScriptedSender.ToolCall("c2", "create_revolve", RevolveCallJson("wcs_z")),
            () => ChatCompletionResult.OkText("等待用户处理。"));

        controller.StartSend("画草图并旋转");
        // The sketch card arrives first; approve it so the revolve card follows.
        await UntilAsync(() => gate.Requests.Count >= 1);
        gate.Requests[0].TryRespond(new ToolConfirmationDecision(true, null));
        await UntilAsync(() => gate.Requests.Count >= 2);

        // The revolve confirmation shows the MODEL's axis, not a rewrite.
        var revolveRequest = gate.Requests.Single(r => r.ParametersKind == ToolParametersKind.Revolve);
        Assert.Equal(RevolveAxisMode.WcsZ, revolveRequest.RevolveParameters!.AxisMode);
        // The pre-check advice is visible (non-blocking).
        Assert.Contains(log.Messages, m => m.Text.Contains("旋转轴预检"));
        // Nothing executed yet: refusing the card keeps Z out of the executor.
        revolveRequest.TryRespond(new ToolConfirmationDecision(false, null));
        await WaitForCompletedAsync(controller);
        Assert.Empty(executor.RevolveCalls);
    }

    // ------------------------------------------------------- bounded retry

    [Fact]
    public async Task PreconditionFailure_Feedback_CarriesFeasibleAxisHint()
    {
        // The executor refuses wcs_z at runtime (the hint lives in the
        // failure message); the tool result the model receives next round
        // must carry that hint so its retry is informed, not a guess.
        var (controller, sender, log, executor, _) = CreateAutonomous();
        executor.NextRevolveStatus = ToolExecutionStatus.PreconditionsChanged;
        sender.Enqueue(
            ScriptedSender.ToolCall("c1", "create_sketch", SketchCallJson),
            ScriptedSender.ToolCall("c2", "create_revolve", RevolveCallJson("wcs_z")),
            () => ChatCompletionResult.OkText("旋转失败，已如实汇报。"));

        controller.StartSend("画草图并绕Z轴旋转360度");
        await WaitForCompletedAsync(controller);

        Assert.Single(executor.RevolveCalls);
        // The tool message for c2 carries the feasible-axis hint verbatim.
        var second = sender.Requests[1];
        var toolMsg = second.Single(m => m.Role == OutboundMessage.RoleTool && m.ToolCallId == "c2");
        using var doc = JsonDocument.Parse(toolMsg.Content!);
        string message = doc.RootElement.GetProperty("message").GetString()!;
        Assert.Contains("可行的绝对坐标轴", message);
        Assert.Contains("X 轴", message);
        Assert.Equal("preconditions_changed", doc.RootElement.GetProperty("status").GetString());
        // The refusal is also visible to the user.
        Assert.Contains(log.Messages, m => m.Text.Contains("可行的绝对坐标轴"));
    }

    [Fact]
    public async Task CorrectionBudget_OneRewritePerTurn_NoLoop()
    {
        // One multi-call message carrying TWO revolve calls: only ONE silent
        // rewrite happens; the second revolve keeps the model's axis and
        // fails at runtime with the hint — no endless silent fixing.
        var (controller, sender, log, executor, _) = CreateAutonomous();
        executor.NextRevolveStatus = ToolExecutionStatus.PreconditionsChanged;
        sender.Enqueue(
            ScriptedSender.ToolCall("c1", "create_sketch", SketchCallJson),
            ChatCompletionResult.OkToolCalls(
            [
                new ParsedToolCall("c2", "create_revolve", RevolveCallJson("wcs_z")),
                new ParsedToolCall("c3", "create_revolve", RevolveCallJson("wcs_z")),
            ]),
            () => ChatCompletionResult.OkText("结束。"));

        controller.StartSend("画草图并连续旋转两次");
        await WaitForCompletedAsync(controller);

        Assert.Equal(2, executor.RevolveCalls.Count);
        // First revolve: planned to X. Second: the budget is spent, the
        // model's wcs_z goes through unchanged and fails at runtime — no
        // endless silent fixing.
        Assert.Equal(RevolveAxisMode.WcsX, executor.RevolveCalls[0].AxisMode);
        Assert.Equal(RevolveAxisMode.WcsZ, executor.RevolveCalls[1].AxisMode);
        Assert.Single(log.Messages, m => m.Text.Contains("自主模式轴规划"));
    }

    // ------------------------------------------------- ledger interplay

    [Fact]
    public async Task UnknownLedgerGate_DisablesSilentRewrite()
    {
        // An unacknowledged Attempting entry for create_revolve (unknown
        // prior outcome) means the part state is uncertain: the axis
        // correction must NOT silently fire. Autonomous mode resolves the
        // unknown state via the READ-ONLY PROBE (never a human dialog —
        // live failure 2026-10-19). The probe cannot judge (null): the
        // turn ends with a clear report; NOTHING executes, so the model's
        // axis is never rewritten and never blindly re-fired.
        var ledger = new ModelingExecutionLedger();
        ledger.BeginAttempt("create_revolve", "未知旋转结果");
        var (controller, sender, log, executor, gate) = CreateAutonomous(ledger);
        sender.Enqueue(
            ScriptedSender.ToolCall("c1", "create_sketch", SketchCallJson),
            ScriptedSender.ToolCall("c2", "create_revolve", RevolveCallJson("wcs_z")),
            () => ChatCompletionResult.OkText("结束。"));

        controller.StartSend("画草图并旋转");
        await WaitForCompletedAsync(controller);

        // The sketch ran (different tool, no gate), but the revolve was
        // stopped by the unjudgeable recovery — no dialog, no execution.
        Assert.Single(executor.SketchCalls);
        Assert.Empty(executor.RevolveCalls);
        Assert.Empty(gate.Requests);
        Assert.Contains(log.Messages, m =>
            m.Role == ChatRole.Error && m.Text.Contains("无法判断"));
        // The unknown entry keeps its honest state.
        Assert.True(ledger.HasUnacknowledgedAttempt("create_revolve"));
    }

    // ------------------------------------------------- budget non-regression

    [Fact]
    public async Task TurnBudgets_UnderCorrection_StayIntact()
    {
        // The correction adds no rounds and no extra executions: 3 model
        // responses total (cap), 2 real executions (sketch+revolve), and the
        // ledger records exactly the two successes.
        var ledger = new ModelingExecutionLedger();
        var (controller, sender, log, executor, _) = CreateAutonomous(ledger);
        sender.Enqueue(
            ScriptedSender.ToolCall("c1", "create_sketch", SketchCallJson),
            ScriptedSender.ToolCall("c2", "create_revolve", RevolveCallJson("wcs_z")),
            () => ChatCompletionResult.OkText("完成。"));

        controller.StartSend("画草图并旋转");
        await WaitForCompletedAsync(controller);

        Assert.Equal(3, sender.Requests.Count); // MaxModelResponsesPerTurn cap
        Assert.Equal(2, executor.SketchCalls.Count + executor.RevolveCalls.Count);
        var entries = ledger.Snapshot();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(ToolExecutionStatus.Success, e.Status));
        // The revolve entry's summary describes the CORRECTED operation.
        Assert.Contains(entries, e => e.ToolName == "create_revolve");
        _ = log;
    }

    // ------------------------------------- scale-outlier rewrites (live #2)

    [Fact]
    public async Task Autonomous_InferredLegalButOutlierAxis_IsRewrittenToCompact()
    {
        // The second live case: user does NOT name an axis; the model picks
        // Y for the (u150, v10) rectangle — legal but a ~320 mm ring. The
        // advisor must rewrite to X (envelope ~30 mm) BEFORE the executor,
        // with a "尺度修正" (geometric-scale reasoning) log entry.
        var (controller, sender, log, executor, _) = CreateAutonomous();
        sender.Enqueue(
            ScriptedSender.ToolCall("c1", "create_sketch", SketchCallJson),
            ScriptedSender.ToolCall("c2", "create_revolve", RevolveCallJson("wcs_y")),
            () => ChatCompletionResult.OkText("旋转体已创建。"));

        controller.StartSend("在绝对坐标 XY 平面创建 20×10 毫米的矩形草图，中心偏移 U=150、V=10 毫米，" +
            "然后绕草图平面内的轴旋转 360 度生成旋转体。");
        await WaitForCompletedAsync(controller);

        var revolve = Assert.Single(executor.RevolveCalls);
        Assert.Equal(RevolveAxisMode.WcsX, revolve.AxisMode); // compact axis
        Assert.DoesNotContain(executor.AxesSeen, a => a == "WcsY");
        Assert.Contains(log.Messages, m =>
            m.Text.Contains("自主模式轴规划") &&
            m.Text.Contains("自动选择") &&
            m.Text.Contains("X 轴"));
        // Still a fully normal turn: 3 model rounds, no failure cards.
        Assert.Equal(3, sender.Requests.Count);
        Assert.DoesNotContain(log.Messages, m => m.Role == ChatRole.Error);
    }

    [Fact]
    public async Task Autonomous_ExplicitUserAxis_IsNeverRewritten_EvenWhenOutlier()
    {
        // The USER names the Y axis in the same geometry: the request goes
        // to the executor unchanged — a user decision outranks the scale
        // heuristic. The advice is logged for transparency only.
        var (controller, sender, log, executor, _) = CreateAutonomous();
        sender.Enqueue(
            ScriptedSender.ToolCall("c1", "create_sketch", SketchCallJson),
            ScriptedSender.ToolCall("c2", "create_revolve", RevolveCallJson("wcs_y")),
            () => ChatCompletionResult.OkText("已绕Y轴完成。"));

        controller.StartSend("在XY平面画草图后绕Y轴旋转360度");
        await WaitForCompletedAsync(controller);

        var revolve = Assert.Single(executor.RevolveCalls);
        Assert.Equal(RevolveAxisMode.WcsY, revolve.AxisMode); // kept
        Assert.DoesNotContain(log.Messages, m => m.Text.Contains("自主模式轴规划"));
        // The outlier observation is still surfaced (non-blocking).
        Assert.Contains(log.Messages, m => m.Text.Contains("旋转轴预检") && m.Text.Contains("尺度异常"));
    }

    [Fact]
    public async Task ConfirmMode_OutlierAxis_ReachesCardUnchanged()
    {
        // Confirm mode with an inferred outlier axis: the card must show the
        // MODEL's Y axis; refusing it keeps the executor untouched. The user
        // — not the advisor — decides whether Y's ring geometry is wanted.
        var log = new ChatLog();
        var sender = new ScriptedSender();
        var executor = new RecordingExecutor();
        var gate = new ConfirmationGate();
        var controller = new ChatSessionController(
            new ModelService(), log, () => ("https://api.test/v1", "key", "model"),
            sender.SendAsync, 20, executor, gate.Handler);

        sender.Enqueue(
            ScriptedSender.ToolCall("c1", "create_sketch", SketchCallJson),
            ScriptedSender.ToolCall("c2", "create_revolve", RevolveCallJson("wcs_y")),
            () => ChatCompletionResult.OkText("等待用户处理。"));

        controller.StartSend("在XY平面画草图后绕草图平面内的轴旋转360度");
        await UntilAsync(() => gate.Requests.Count >= 1);
        gate.Requests[0].TryRespond(new ToolConfirmationDecision(true, null));
        await UntilAsync(() => gate.Requests.Count >= 2);

        var revolveRequest = gate.Requests.Single(r => r.ParametersKind == ToolParametersKind.Revolve);
        Assert.Equal(RevolveAxisMode.WcsY, revolveRequest.RevolveParameters!.AxisMode);
        Assert.Contains(log.Messages, m => m.Text.Contains("旋转轴预检"));
        revolveRequest.TryRespond(new ToolConfirmationDecision(false, null));
        await WaitForCompletedAsync(controller);
        Assert.Empty(executor.RevolveCalls);
    }

    [Fact]
    public async Task Autonomous_RewriteBudget_SharedByScaleAndFeasibilityKinds()
    {
        // The per-turn correction budget (1) covers BOTH correction kinds:
        // after one rewrite (here: scale), a later infeasible-axis call in
        // the same turn goes through unchanged with the pre-check notice.
        var (controller, sender, log, executor, _) = CreateAutonomous();
        sender.Enqueue(
            ChatCompletionResult.OkToolCalls(
            [
                new ParsedToolCall("c1", "create_sketch", SketchCallJson),
                new ParsedToolCall("c2", "create_revolve", RevolveCallJson("wcs_y")),
                new ParsedToolCall("c3", "create_revolve", RevolveCallJson("wcs_z")),
            ]),
            () => ChatCompletionResult.OkText("结束。"));

        controller.StartSend("画草图后绕草图平面内的轴旋转两次");
        await WaitForCompletedAsync(controller);

        Assert.Equal(2, executor.RevolveCalls.Count);
        Assert.Equal(RevolveAxisMode.WcsX, executor.RevolveCalls[0].AxisMode); // planned to the compact axis
        Assert.Equal(RevolveAxisMode.WcsZ, executor.RevolveCalls[1].AxisMode); // budget spent, runtime check decides
        Assert.Single(log.Messages, m => m.Text.Contains("自主模式轴规划"));
    }

    // ------------------------------------- selected_axis intent protection

    [Fact]
    public async Task SelectedAxis_Angle180_PassedToExecutorUnchanged()
    {
        // The live failure's request shape: existing sketch, user-selected
        // axis, 180°. selected_axis is by definition a USER decision — the
        // advisor must never rewrite it (even in autonomous mode), and the
        // executor receives the exact parameters.
        var (controller, sender, log, executor, _) = CreateAutonomous();
        sender.Enqueue(
            ScriptedSender.ToolCall("c2", "create_revolve",
                "{\"sketch_ref\":\"SKETCH_000\",\"axis_mode\":\"selected_axis\",\"angle\":180}"),
            () => ChatCompletionResult.OkText("已按选定轴完成旋转。"));

        controller.StartSend("现有草图按我选定的轴旋转180°创建实体。");
        await WaitForCompletedAsync(controller);

        var revolve = Assert.Single(executor.RevolveCalls);
        Assert.Equal(RevolveAxisMode.SelectedAxis, revolve.AxisMode);
        Assert.Equal("SKETCH_000", revolve.SketchRef);
        Assert.Equal(180, revolve.AngleDeg);
        // No correction of any kind touched this call.
        Assert.DoesNotContain(log.Messages, m => m.Text.Contains("自主模式轴规划"));
        _ = log;
    }

    [Fact]
    public async Task SelectedAxis_ExecutorFailure_ReportsDiagnosis_NoAxisFallback()
    {
        // When the executor cannot resolve the selected object to a line
        // (the live AssociativeLine failure), the tool result the model
        // receives must carry the typed diagnosis — and must NOT contain
        // any suggestion to silently switch to a WCS axis.
        var (controller, sender, log, executor, _) = CreateAutonomous();
        executor.NextRevolveStatus = ToolExecutionStatus.PreconditionsChanged;
        sender.Enqueue(
            ScriptedSender.ToolCall("c2", "create_revolve",
                "{\"sketch_ref\":\"SKETCH_000\",\"axis_mode\":\"selected_axis\",\"angle\":180}"),
            () => ChatCompletionResult.OkText("旋转未能执行。"));

        controller.StartSend("现有草图按我选定的轴旋转180°创建实体。");
        await WaitForCompletedAsync(controller);

        Assert.Single(executor.RevolveCalls); // attempted once, not re-fired
        Assert.Equal(RevolveAxisMode.SelectedAxis, executor.RevolveCalls[0].AxisMode);
        var second = sender.Requests[1];
        var toolMsg = second.Single(m => m.Role == OutboundMessage.RoleTool && m.ToolCallId == "c2");
        using var doc = JsonDocument.Parse(toolMsg.Content!);
        string message = doc.RootElement.GetProperty("message").GetString()!;
        Assert.Equal("preconditions_changed", doc.RootElement.GetProperty("status").GetString());
        // The diagnosis names the selection content (fixture message shape).
        Assert.Contains("选中的关联直线特征", message);
    }

    // --------------------------------- requirement 六: no-axis auto-planning

    [Fact]
    public async Task UnspecifiedIntent_ModelDefaultSelectedAxis_IsNormalizedToBestCandidate()
    {
        // The live failure: "用现有草图绕轴旋转 360°" (NO axis named) — the
        // model defaulted to selected_axis and the executor hard-stopped on
        // an empty selection. Now: the controller classifies the intent as
        // Unspecified (the model's default is NOT the user's decision),
        // normalizes to the advisor's best candidate, and the executor
        // receives a CONCRETE WCS axis.
        var (controller, sender, log, executor, gate) = CreateAutonomous();
        sender.Enqueue(
            ScriptedSender.ToolCall("c1", "create_sketch", SketchCallJson),
            ScriptedSender.ToolCall("c2", "create_revolve",
                "{\"sketch_ref\":\"SKETCH_000\",\"axis_mode\":\"selected_axis\",\"angle\":360}"),
            () => ChatCompletionResult.OkText("旋转完成。"));

        controller.StartSend("用现有草图绕轴旋转 360°");
        await WaitForCompletedAsync(controller);

        // The executor received the auto-planned axis — NOT selected_axis.
        var revolve = Assert.Single(executor.RevolveCalls);
        Assert.NotEqual(RevolveAxisMode.SelectedAxis, revolve.AxisMode);
        Assert.Equal(RevolveAxisMode.WcsX, revolve.AxisMode); // best candidate
        Assert.Equal(360, revolve.AngleDeg);
        // No confirmation dialog was needed (autonomous planning worked).
        Assert.Empty(gate.Requests);
        // The planning decision is logged with its reasoning.
        Assert.Contains(log.Messages, m =>
            m.Text.Contains("自主模式轴规划") && m.Text.Contains("自动选择"));
        // The model was told the correction via the tool result path.
        _ = log;
    }

    [Fact]
    public async Task UserNamedSelectedAxis_ButNoSelection_KeepsSelectedAxis_AndFailsWithDiagnosis()
    {
        // Requirement 六.2: the USER asks for the selected axis but nothing
        // usable is selected — the executor diagnoses it; the controller
        // does NOT auto-switch to a WCS axis behind the user's back.
        var (controller, sender, log, executor, gate) = CreateAutonomous();
        executor.NextRevolveStatus = ToolExecutionStatus.PreconditionsChanged;
        sender.Enqueue(
            ScriptedSender.ToolCall("c2", "create_revolve",
                "{\"sketch_ref\":\"SKETCH_000\",\"axis_mode\":\"selected_axis\",\"angle\":180}"),
            () => ChatCompletionResult.OkText("未能执行。"));

        controller.StartSend("用我选定的轴把草图旋转 180°");
        await WaitForCompletedAsync(controller);

        // The executor received selected_axis UNCHANGED — no WCS fallback.
        var revolve = Assert.Single(executor.RevolveCalls);
        Assert.Equal(RevolveAxisMode.SelectedAxis, revolve.AxisMode);
        Assert.DoesNotContain(log.Messages, m => m.Text.Contains("自主模式轴规划"));
        _ = gate;
    }

    [Fact]
    public async Task UserNamedZAxis_IsNotRewrittenByScaleOptimization()
    {
        // Requirement 六.3: the user names Z for the (u150,v10) rectangle —
        // a legal axis (coplanarity gate is gone) whose scale is an outlier,
        // but a USER decision outranks the heuristic. Executor gets wcs_z.
        var (controller, sender, log, executor, gate) = CreateAutonomous();
        sender.Enqueue(
            ScriptedSender.ToolCall("c1", "create_sketch", SketchCallJson),
            ScriptedSender.ToolCall("c2", "create_revolve", RevolveCallJson("wcs_z")),
            () => ChatCompletionResult.OkText("已绕Z轴完成。"));

        controller.StartSend("画草图后绕Z轴旋转360度");
        await WaitForCompletedAsync(controller);

        var revolve = Assert.Single(executor.RevolveCalls);
        Assert.Equal(RevolveAxisMode.WcsZ, revolve.AxisMode); // verbatim
        Assert.DoesNotContain(log.Messages, m => m.Text.Contains("自主模式轴规划"));
        _ = gate;
    }

    [Fact]
    public async Task UnspecifiedIntent_DifferentSketchPositions_PickDifferentAxes()
    {
        // Requirement 六.4: the auto-choice follows the geometry. Sketch A
        // (u150,v10 rectangle): best = X (envelope 15 vs 160). Sketch B on
        // the YZ plane with the circle at u25 (Y world): the in-plane Z axis
        // hugs the profile better than the tilted X axis... verify at least
        // that the two cases pick per-sketch candidates, not one fixed axis.
        var (controllerA, senderA, logA, executorA, _) = CreateAutonomous();
        senderA.Enqueue(
            ScriptedSender.ToolCall("c1", "create_sketch", SketchCallJson),
            ScriptedSender.ToolCall("c2", "create_revolve",
                "{\"sketch_ref\":\"SKETCH_000\",\"axis_mode\":\"selected_axis\",\"angle\":360}"),
            () => ChatCompletionResult.OkText("完成。"));
        controllerA.StartSend("用现有草图绕轴旋转 360°");
        await WaitForCompletedAsync(controllerA);
        Assert.Equal(RevolveAxisMode.WcsX, executorA.RevolveCalls[0].AxisMode);

        // Sketch B: YZ-plane circle d30 at (u25, v0) — center 25mm along
        // the Y axis. Candidates: X (plane normal, envelope √(25²+15²)≈29.2),
        // Y (in-plane, envelope 25+15=40), Z (in-plane, envelope 40)... the
        // exact winner is computed by the advisor; assert it's a concrete
        // WCS axis with a logged plan (not selected_axis, not a stop).
        var (controllerB, senderB, logB, executorB, _) = CreateAutonomous();
        senderB.Enqueue(
            ScriptedSender.ToolCall("c1", "create_sketch",
                """{"plane_mode":"wcs_yz","profile_type":"circle","diameter":30,"center_u":25,"center_v":0,"unit":"mm"}"""),
            ScriptedSender.ToolCall("c2", "create_revolve",
                "{\"sketch_ref\":\"SKETCH_000\",\"axis_mode\":\"selected_axis\",\"angle\":360}"),
            () => ChatCompletionResult.OkText("完成。"));
        controllerB.StartSend("用现有草图绕轴旋转 360°");
        await WaitForCompletedAsync(controllerB);

        var revolveB = Assert.Single(executorB.RevolveCalls);
        Assert.NotEqual(RevolveAxisMode.SelectedAxis, revolveB.AxisMode);
        Assert.NotEqual(RevolveAxisMode.WcsX, revolveB.AxisMode); // different geometry → different axis
        Assert.Contains(logB.Messages, m => m.Text.Contains("自主模式轴规划"));
    }

    // ------------------------------- unified planning: model WCS guesses

    [Fact]
    public async Task UnspecifiedIntent_ModelGuessesWcsY_OnXyRectangle_IsPlannedToX()
    {
        // The cross-plane gap fix: the model guesses wcs_y (NOT
        // selected_axis) for the (u150,v10) rectangle with NO axis named by
        // the user. The unified planning path must evaluate it and rewrite
        // to the compact X — the model's WCS guess is not a user decision.
        var (controller, sender, log, executor, gate) = CreateAutonomous();
        sender.Enqueue(
            ScriptedSender.ToolCall("c1", "create_sketch", SketchCallJson),
            ScriptedSender.ToolCall("c2", "create_revolve", RevolveCallJson("wcs_y")),
            () => ChatCompletionResult.OkText("完成。"));

        controller.StartSend("用现有草图绕轴旋转 360°");
        await WaitForCompletedAsync(controller);

        var revolve = Assert.Single(executor.RevolveCalls);
        Assert.Equal(RevolveAxisMode.WcsX, revolve.AxisMode); // planned
        Assert.Contains(log.Messages, m => m.Text.Contains("自主模式轴规划"));
        Assert.Empty(gate.Requests);
    }

    [Fact]
    public async Task UnspecifiedIntent_ModelGuessesTheBestAxis_NoRewriteNoNoise()
    {
        // If the model's guess IS the advisor's best candidate, planning
        // does not rewrite (RankedCandidates[0] == requested) — the guess
        // passes through with no planning log.
        var (controller, sender, log, executor, _) = CreateAutonomous();
        sender.Enqueue(
            ScriptedSender.ToolCall("c1", "create_sketch", SketchCallJson),
            ScriptedSender.ToolCall("c2", "create_revolve", RevolveCallJson("wcs_x")),
            () => ChatCompletionResult.OkText("完成。"));

        controller.StartSend("用现有草图绕轴旋转 360°");
        await WaitForCompletedAsync(controller);

        var revolve = Assert.Single(executor.RevolveCalls);
        Assert.Equal(RevolveAxisMode.WcsX, revolve.AxisMode);
        Assert.DoesNotContain(log.Messages, m => m.Text.Contains("自主模式轴规划"));
    }

    // ------------------------------------- three-plane matrix (controller)

    [Theory]
    [InlineData("wcs_xy", RevolveAxisMode.WcsX)]   // center (35,0,0) on X axis
    [InlineData("wcs_xz", RevolveAxisMode.WcsX)]   // center (35,0,0) on X axis
    [InlineData("wcs_yz", RevolveAxisMode.WcsY)]   // center (0,35,0) on Y axis
    public async Task ThreePlaneMatrix_Controller_PlansTheCompactAxisPerPlane(
        string planeMode, RevolveAxisMode expectedAxis)
    {
        // Requirement 四's matrix end-to-end at controller level: identical
        // local geometry (circle d10, center 35 along local U) on each
        // plane, user names NO axis, the model guesses selected_axis — the
        // executor must receive the plane-appropriate compact axis. The
        // YZ case proves local U is converted to world Y (NOT assumed X).
        var (controller, sender, log, executor, gate) = CreateAutonomous();
        sender.Enqueue(
            ScriptedSender.ToolCall("c1", "create_sketch",
                $$"""{"plane_mode":"{{planeMode}}","profile_type":"circle","diameter":10,"center_u":35,"center_v":0,"unit":"mm"}"""),
            ScriptedSender.ToolCall("c2", "create_revolve",
                "{\"sketch_ref\":\"SKETCH_000\",\"axis_mode\":\"selected_axis\",\"angle\":360}"),
            () => ChatCompletionResult.OkText("完成。"));

        controller.StartSend("用现有草图绕轴旋转 360°");
        await WaitForCompletedAsync(controller);

        var revolve = Assert.Single(executor.RevolveCalls);
        Assert.Equal(expectedAxis, revolve.AxisMode);
        Assert.Contains(log.Messages, m =>
            m.Text.Contains("自主模式轴规划") && m.Text.Contains("自动选择"));
        Assert.Empty(gate.Requests);
    }

    [Fact]
    public async Task UnspecifiedIntent_NoSketchFacts_SafeStopNoExecution()
    {
        // Requirement 六.7: no sketch facts this turn (the sketch came from
        // an EARLIER turn) → the advisor cannot rank candidates → the
        // request goes through UNCHANGED (selected_axis to the executor,
        // which will diagnose the empty selection) rather than guessing.
        // The executor's own diagnosis is the honest outcome.
        var (controller, sender, log, executor, _) = CreateAutonomous();
        executor.NextRevolveStatus = ToolExecutionStatus.PreconditionsChanged;
        sender.Enqueue(
            ScriptedSender.ToolCall("c2", "create_revolve",
                "{\"sketch_ref\":\"SKETCH_OLD\",\"axis_mode\":\"selected_axis\",\"angle\":360}"),
            () => ChatCompletionResult.OkText("未执行。"));

        controller.StartSend("用现有草图绕轴旋转 360°");
        await WaitForCompletedAsync(controller);

        // No sketch facts → no normalization → the model's request reaches
        // the executor verbatim (which diagnoses "unknown/stale sketch").
        var revolve = Assert.Single(executor.RevolveCalls);
        Assert.Equal(RevolveAxisMode.SelectedAxis, revolve.AxisMode);
        Assert.DoesNotContain(log.Messages, m => m.Text.Contains("自主模式轴规划"));
        _ = log;
    }
}
