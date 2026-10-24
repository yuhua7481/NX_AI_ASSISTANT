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

    internal sealed class ScriptedSender
    {
        public readonly List<IReadOnlyList<OutboundMessage>> Requests = new();
        public readonly List<IReadOnlyList<ToolDefinition>?> ToolDeclarations = new();
        private readonly Queue<object> _responses = new();
        private object? _default;

        // Items are ChatCompletionResult values, Func<ChatCompletionResult>
        // factories (built on demand), or token-aware async factories
        // Func<CancellationToken, Task<ChatCompletionResult>> — the last one
        // can hang until the turn's token is cancelled, giving
        // CancelActiveRequest a real in-flight window to race against.
        public void Enqueue(params object[] responses)
        {
            foreach (var r in responses) _responses.Enqueue(r);
        }

        public void SetDefault(object response) => _default = response;

        public int RequestCount => Requests.Count;

        public async Task<ChatCompletionResult> SendAsync(
            IReadOnlyList<OutboundMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            CancellationToken ct)
        {
            Requests.Add(messages);
            ToolDeclarations.Add(tools);
            var item = _responses.Count > 0 ? _responses.Dequeue() : _default
                ?? ChatCompletionResult.OkText("最终回复");
            if (item is Func<CancellationToken, Task<ChatCompletionResult>> tokenFactory)
                return await tokenFactory(ct);
            return item switch
            {
                Func<ChatCompletionResult> f => f(),
                ChatCompletionResult r => r,
                _ => throw new InvalidOperationException("Bad scripted response type"),
            };
        }

        public static ChatCompletionResult ToolCall(
            string id = "call_1", string name = "create_block", string args = ToolCallJson) =>
            ChatCompletionResult.OkToolCalls([new ParsedToolCall(id, name, args)]);
    }

    internal sealed class CountingExecutor : INxToolExecutor
    {
        public CreateBlockExecutionResult ExecuteCreateHole(
            CreateHoleParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Success(1, 1, 1, "ok", "part");
        public CreateBlockExecutionResult ExecuteCreateStandardHole(
            CreateStandardHoleParameters parameters, string? expectedWorkPartFullPath)
        {
            CallCount++;
            Calls.Add(StandardHoleToolName(parameters));
            LastToolName = StandardHoleToolName(parameters);
            LastExpectedPath = expectedWorkPartFullPath;
            LastStandardHoleParameters = parameters;
            return Outcome(StandardHoleToolName(parameters));
        }

        private static string StandardHoleToolName(CreateStandardHoleParameters p) => p.Family switch
        {
            StandardHoleFamily.Threaded => StandardHoleToolContract.ThreadedName,
            StandardHoleFamily.SimpleClearance => StandardHoleToolContract.ClearanceName,
            StandardHoleFamily.Counterbored => StandardHoleToolContract.CounterboredName,
            _ => StandardHoleToolContract.CountersunkName,
        };

        public CreateBlockExecutionResult ExecuteCreateFillet(
            CreateFilletParameters parameters, string? expectedWorkPartFullPath)
        {
            CallCount++;
            Calls.Add(CreateFilletRequestParser.ToolName);
            LastToolName = CreateFilletRequestParser.ToolName;
            LastExpectedPath = expectedWorkPartFullPath;
            return Outcome(CreateFilletRequestParser.ToolName);
        }

        public CreateBlockExecutionResult ExecuteCreateChamfer(
            CreateChamferParameters parameters, string? expectedWorkPartFullPath)
        {
            CallCount++;
            Calls.Add(CreateChamferRequestParser.ToolName);
            LastToolName = CreateChamferRequestParser.ToolName;
            LastExpectedPath = expectedWorkPartFullPath;
            return Outcome(CreateChamferRequestParser.ToolName);
        }

        public CreateBlockExecutionResult ExecuteCreateLinearPattern(
            CreateLinearPatternParameters parameters, string? expectedWorkPartFullPath)
        {
            CallCount++;
            Calls.Add(LinearPatternToolContract.Name);
            LastToolName = LinearPatternToolContract.Name;
            LastExpectedPath = expectedWorkPartFullPath;
            return Outcome(LinearPatternToolContract.Name);
        }
        public CreateBlockExecutionResult ExecuteCreateCircularPattern(
            CreateCircularPatternParameters parameters, string? expectedWorkPartFullPath)
        {
            CallCount++;
            Calls.Add(CircularPatternToolContract.Name);
            LastToolName = CircularPatternToolContract.Name;
            LastExpectedPath = expectedWorkPartFullPath;
            return Outcome(CircularPatternToolContract.Name);
        }

        public CreateBlockExecutionResult ExecuteCreateMirror(
            CreateMirrorParameters parameters, string? expectedWorkPartFullPath)
        {
            CallCount++;
            Calls.Add(MirrorToolContract.Name);
            LastToolName = MirrorToolContract.Name;
            LastExpectedPath = expectedWorkPartFullPath;
            return Outcome(MirrorToolContract.Name);
        }

        public int CallCount;
        public CreateBlockExecutionResult Result { get; set; } =
            CreateBlockExecutionResult.Success(100, 60, 20, "成功", "part.prt");

        // Per-tool overrides for multi-tool tests: keyed by tool name, takes
        // precedence over Result when present. Execution order stays
        // observable via the Calls list.
        public Dictionary<string, CreateBlockExecutionResult> PerToolResults { get; } = new();
        public Dictionary<string, ModelFeatureProbeResult> ProbeResultOverride { get; } = new();
        public List<string> Calls { get; } = new();

        private CreateBlockExecutionResult Outcome(string toolName) =>
            PerToolResults.TryGetValue(toolName, out var r) ? r : Result;

        public CreateBlockExecutionResult ExecuteCreateBlock(
            CreateBlockParameters parameters, string? expectedWorkPartFullPath)
        {
            CallCount++;
            Calls.Add("create_block");
            LastParameters = parameters;
            LastExpectedPath = expectedWorkPartFullPath;
            LastToolName = "create_block";
            return Outcome("create_block");
        }

        public CreateBlockExecutionResult ExecuteCreateCylinder(
            CreateCylinderParameters parameters, string? expectedWorkPartFullPath)
        {
            CallCount++;
            Calls.Add("create_cylinder");
            LastCylinderParameters = parameters;
            LastExpectedPath = expectedWorkPartFullPath;
            LastToolName = "create_cylinder";
            return Outcome("create_cylinder");
        }

        public CreateBlockExecutionResult ExecuteCreateCone(
            CreateConeParameters parameters, string? expectedWorkPartFullPath)
        {
            CallCount++;
            Calls.Add("create_cone");
            LastConeParameters = parameters;
            LastExpectedPath = expectedWorkPartFullPath;
            LastToolName = "create_cone";
            return Outcome("create_cone");
        }

        public CreateBlockExecutionResult ExecuteCreateSphere(
            CreateSphereParameters parameters, string? expectedWorkPartFullPath)
        {
            CallCount++;
            Calls.Add("create_sphere");
            LastSphereParameters = parameters;
            LastExpectedPath = expectedWorkPartFullPath;
            LastToolName = "create_sphere";
            return Outcome("create_sphere");
        }

        public CreateBlockExecutionResult ExecuteCreateSketch(
            CreateSketchParameters parameters, string? expectedWorkPartFullPath)
        {
            CallCount++; Calls.Add("create_sketch"); LastToolName = "create_sketch"; LastExpectedPath = expectedWorkPartFullPath;
            return Outcome("create_sketch");
        }

        public CreateBlockExecutionResult ExecuteCreateExtrude(
            CreateExtrudeParameters parameters, string? expectedWorkPartFullPath)
        {
            CallCount++; Calls.Add("create_extrude"); LastToolName = "create_extrude"; LastExpectedPath = expectedWorkPartFullPath;
            return Outcome("create_extrude");
        }

        public CreateBlockExecutionResult ExecuteCreateRevolve(
            CreateRevolveParameters parameters, string? expectedWorkPartFullPath)
        {
            CallCount++; Calls.Add("create_revolve"); LastToolName = "create_revolve"; LastExpectedPath = expectedWorkPartFullPath;
            return Outcome("create_revolve");
        }

        public CreateBlockExecutionResult ExecuteBooleanOperation(
            BooleanOperationParameters parameters, string? expectedWorkPartFullPath)
        {
            CallCount++; Calls.Add("boolean_operation"); LastToolName = "boolean_operation"; LastExpectedPath = expectedWorkPartFullPath;
            return Outcome("boolean_operation");
        }

        public ModelFeatureProbeResult? ProbeCommittedFeature(string toolName, string operationSummary)
            => ProbeResultOverride.TryGetValue(toolName, out var result) ? result : null;

        public CreateConeParameters? LastConeParameters { get; private set; }
        public CreateStandardHoleParameters? LastStandardHoleParameters { get; private set; }
        public ModelFeatureProbeResult? ProbeCommittedFeature(
            string toolName, string operationSummary, IReadOnlySet<string>? preExistingFeatureIds,
            BooleanOperationType? operationType = null) =>
            ProbeCommittedFeature(toolName, operationSummary);

        public IReadOnlySet<string>? ProbeFeatureSnapshot(string toolName, string? featureKind = null) => null;
        public CreateSphereParameters? LastSphereParameters { get; private set; }
        public string? LastToolName { get; private set; }

        public CreateCylinderParameters? LastCylinderParameters { get; private set; }

        public CreateBlockParameters? LastParameters { get; private set; }
        public string? LastExpectedPath { get; private set; }
    }

    /// <summary>
    /// Executor that enters the modeling call and never returns a terminal
    /// result — it throws, simulating a process crash mid-execution (in a
    /// real NX crash the throw would be the teardown itself). Exactly the
    /// shape the ledger's Attempting state exists to record.
    /// </summary>
    private sealed class ThrowingExecutor : INxToolExecutor
    {
        public CreateBlockExecutionResult ExecuteCreateBlock(
            CreateBlockParameters parameters, string? expectedWorkPartFullPath) =>
            throw new InProcessCrashException("模拟进程崩溃：执行器未返回");

        public CreateBlockExecutionResult ExecuteCreateCylinder(
            CreateCylinderParameters parameters, string? expectedWorkPartFullPath) =>
            throw new InProcessCrashException("模拟进程崩溃：执行器未返回");

        public CreateBlockExecutionResult ExecuteCreateCone(
            CreateConeParameters parameters, string? expectedWorkPartFullPath) =>
            throw new InProcessCrashException("模拟进程崩溃：执行器未返回");

        public CreateBlockExecutionResult ExecuteCreateSphere(
            CreateSphereParameters parameters, string? expectedWorkPartFullPath) =>
            throw new InProcessCrashException("模拟进程崩溃：执行器未返回");

        public CreateBlockExecutionResult ExecuteCreateHole(
            CreateHoleParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Success(1, 1, 1, "ok", "part");
        public CreateBlockExecutionResult ExecuteCreateStandardHole(
            CreateStandardHoleParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Success(1, 1, 1, "ok", "part");
        public CreateBlockExecutionResult ExecuteCreateFillet(
            CreateFilletParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Success(1, 1, 1, "ok", "part");
        public CreateBlockExecutionResult ExecuteCreateChamfer(
            CreateChamferParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Success(1, 1, 1, "ok", "part");
        public CreateBlockExecutionResult ExecuteCreateLinearPattern(
            CreateLinearPatternParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Success(1, 1, 1, "ok", "part");
        public CreateBlockExecutionResult ExecuteCreateCircularPattern(
            CreateCircularPatternParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Success(1, 1, 1, "ok", "part");
        public CreateBlockExecutionResult ExecuteCreateMirror(
            CreateMirrorParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Success(1, 1, 1, "ok", "part");
        public CreateBlockExecutionResult ExecuteCreateSketch(
            CreateSketchParameters parameters, string? expectedWorkPartFullPath) =>
            throw new InProcessCrashException("模拟进程崩溃：执行器未返回");

        public CreateBlockExecutionResult ExecuteCreateExtrude(
            CreateExtrudeParameters parameters, string? expectedWorkPartFullPath) =>
            throw new InProcessCrashException("模拟进程崩溃：执行器未返回");

        public CreateBlockExecutionResult ExecuteCreateRevolve(
            CreateRevolveParameters parameters, string? expectedWorkPartFullPath) =>
            throw new InProcessCrashException("模拟进程崩溃：执行器未返回");

        public CreateBlockExecutionResult ExecuteBooleanOperation(
            BooleanOperationParameters parameters, string? expectedWorkPartFullPath) =>
            throw new InProcessCrashException("模拟进程崩溃：执行器未返回");

        public ModelFeatureProbeResult? ProbeCommittedFeature(string toolName, string operationSummary) => null;
        public ModelFeatureProbeResult? ProbeCommittedFeature(
            string toolName, string operationSummary, IReadOnlySet<string>? preExistingFeatureIds,
            BooleanOperationType? operationType = null) =>
            ProbeCommittedFeature(toolName, operationSummary);

        public IReadOnlySet<string>? ProbeFeatureSnapshot(string toolName, string? featureKind = null) => null;

        public sealed class InProcessCrashException : InvalidOperationException
        {
            public InProcessCrashException(string message) : base(message) { }
        }
    }

    internal sealed class ConfirmationGate
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
        // Rounds 1-2 may each execute their calls; the final round refuses a
        // tool call with a real result — never confirmed/executed.
        sender.Enqueue(ScriptedSender.ToolCall(id: "call_1"),
            ScriptedSender.ToolCall(id: "call_2"),
            ScriptedSender.ToolCall(id: "call_3"));
        gate.EnqueuePending(out var pending);

        controller.StartSend("创建长方体");
        await UntilAsync(() => gate.Requests.Count > 0);
        pending.TrySetResult(new ToolConfirmationDecision(true, "D:/part.prt"));
        // Rounds 2 and 3: the default gate handler auto-confirms; count cards.
        await WaitForCompletedAsync(controller);

        Assert.Equal(ChatSessionController.MaxModelResponsesPerTurn, sender.RequestCount); // capped at 3
        Assert.Equal(2, executor.CallCount);   // the two pre-final executions
        Assert.Equal(2, gate.Requests.Count);  // round-3 call refused before confirmation

        // Final round does not advertise tools.
        Assert.NotNull(sender.ToolDeclarations[0]);
        Assert.NotNull(sender.ToolDeclarations[1]);
        Assert.Null(sender.ToolDeclarations[^1]);

        // The cap's refusal card survives as an Error entry; the turn-level
        // interrupted-state record may follow it, so assert by content.
        Assert.Contains(log.Messages, m => m.Role == ChatRole.Error && m.Text.Contains("上限"));
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
        // Rounds 1-2 confirmed+executed; the round-3 tool call is refused by
        // the final-round rule and must not reach confirmation/execution.
        Assert.Equal(2, executor.CallCount);
        Assert.Equal(2, gate.Requests.Count);
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
        Assert.Equal(18, sender.ToolDeclarations[0]!.Count); // 13 tools advertised (9 geometry + 4 standard-hole)
        Assert.Equal(2, log.Messages.Count);
        Assert.Equal(ChatRole.Assistant, log.Messages[1].Role);
    }

    // ------------------------------------------------------ create_cylinder

    private const string CylinderCallJson =
        """{"diameter":40,"height":25,"unit":"mm"}""";

    [Fact]
    public async Task Cylinder_confirm_executes_exactly_once_and_feeds_structured_result()
    {
        var (controller, sender, log, executor, gate) = Create();
        sender.Enqueue(ScriptedSender.ToolCall(name: "create_cylinder", args: CylinderCallJson),
            () => ChatCompletionResult.OkText("已创建圆柱体。"));

        controller.StartSend("创建直径 40 高 25 毫米的圆柱");
        await WaitForCompletedAsync(controller);

        Assert.Equal(1, executor.CallCount);
        Assert.Equal("create_cylinder", executor.LastToolName);
        Assert.Equal(2, sender.RequestCount);

        var payload = ParsePayload(sender.Requests[1], 2);
        Assert.Equal("success", payload.GetProperty("status").GetString());
        // The counting executor returns its canned verification (100/60/20);
        // the point is that the verified values, not the requested ones,
        // reach the model.
        Assert.Equal(20, payload.GetProperty("verifiedDimensionsMm").GetProperty("height").GetDouble());

        // tool_call_id 关联保持一致。
        Assert.Equal("call_1", sender.Requests[1][2].ToolCallId);

        // Typed parameters reached the executor in mm.
        Assert.NotNull(executor.LastCylinderParameters);
        Assert.Equal(40, executor.LastCylinderParameters!.Diameter);
        Assert.Equal("mm", executor.LastCylinderParameters.Unit);
        Assert.Equal("D:\\part.prt", executor.LastExpectedPath);

        Assert.Contains(log.Messages, m => m.Role == ChatRole.Assistant && m.Text == "已创建圆柱体。");
    }

    [Fact]
    public async Task Cylinder_user_cancel_executes_nothing()
    {
        var (controller, sender, log, executor, gate) = Create();
        gate.EnqueuePending(out var pending);
        sender.Enqueue(ScriptedSender.ToolCall(name: "create_cylinder", args: CylinderCallJson),
            () => ChatCompletionResult.OkText("已取消。"));

        controller.StartSend("创建圆柱体");
        await UntilAsync(() => gate.Requests.Count > 0);
        Assert.Equal(ToolParametersKind.Cylinder, gate.Requests[0].ParametersKind);
        pending.TrySetResult(new ToolConfirmationDecision(false, null));
        await WaitForCompletedAsync(controller);

        Assert.Equal(0, executor.CallCount);
        var payload = ParsePayload(sender.Requests[1], 2);
        Assert.Equal("user_cancelled", payload.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Cylinder_invalid_arguments_rejected_without_executor_call()
    {
        var (controller, sender, _, executor, _) = Create();
        sender.Enqueue(
            ScriptedSender.ToolCall(name: "create_cylinder",
                args: """{"diameter":-5,"height":25,"unit":"mm"}"""),
            () => ChatCompletionResult.OkText("参数无效。"));

        controller.StartSend("创建圆柱体");
        await WaitForCompletedAsync(controller);

        Assert.Equal(0, executor.CallCount);
        var payload = ParsePayload(sender.Requests[1], 2);
        Assert.Equal("invalid_parameters", payload.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Cylinder_extra_field_rejected_without_executor_call()
    {
        var (controller, sender, _, executor, _) = Create();
        sender.Enqueue(
            ScriptedSender.ToolCall(name: "create_cylinder",
                args: """{"diameter":40,"height":25,"unit":"mm","file_path":"D:\\\\x.prt"}"""),
            () => ChatCompletionResult.OkText("不支持的字段。"));

        controller.StartSend("创建圆柱体");
        await WaitForCompletedAsync(controller);

        Assert.Equal(0, executor.CallCount);
        var payload = ParsePayload(sender.Requests[1], 2);
        Assert.Equal("invalid_parameters", payload.GetProperty("status").GetString());
        Assert.Contains("file_path", payload.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Mixed_tool_calls_never_execute_in_the_final_round()
    {
        // Multi-tool contract: responses 1..N-1 may each carry tool calls and
        // execute them; the FINAL round exists to phrase the accumulated
        // results, so a tool call there is refused with a real result — never
        // confirmed/executed.
        var (controller, sender, log, executor, gate) = Create();
        sender.Enqueue(
            ScriptedSender.ToolCall(name: "create_cylinder", args: CylinderCallJson),
            ScriptedSender.ToolCall(id: "call_2", name: "create_block"),
            ScriptedSender.ToolCall(id: "call_3", name: "create_sphere",
                args: """{"diameter":10,"unit":"mm"}"""));
        gate.EnqueuePending(out var pending);

        controller.StartSend("先圆柱后长方体");
        await UntilAsync(() => gate.Requests.Count > 0);
        pending.TrySetResult(new ToolConfirmationDecision(true, "D:/part.prt"));
        await WaitForCompletedAsync(controller);

        // Rounds 1-2 executed exactly the two earlier calls.
        Assert.Equal(2, executor.CallCount);
        Assert.Equal(["create_cylinder", "create_block"], executor.Calls);
        Assert.Equal(ChatSessionController.MaxModelResponsesPerTurn, sender.RequestCount);
        // Only rounds 1-2 reached confirmation; the round-3 call never did.
        Assert.Equal(2, gate.Requests.Count);
        Assert.Contains(log.Messages, m => m.Role == ChatRole.Error && m.Text.Contains("上限"));
    }

    [Fact]
    public async Task Frustum_confirm_shows_typed_parameters_and_executes_once()
    {
        var (controller, sender, log, executor, gate) = Create();
        sender.Enqueue(
            ScriptedSender.ToolCall(name: "create_cone",
                args: """{"base_diameter":40,"top_diameter":20,"height":20,"unit":"mm"}"""),
            () => ChatCompletionResult.OkText("已创建圆台。"));

        controller.StartSend("创建底 40 顶 20 高 20 的圆台");
        await WaitForCompletedAsync(controller);

        Assert.Equal(1, executor.CallCount);
        Assert.Equal("create_cone", executor.LastToolName);
        Assert.NotNull(executor.LastConeParameters);
        Assert.Equal(40, executor.LastConeParameters!.BaseDiameter);
        Assert.Equal(20, executor.LastConeParameters.TopDiameter);
        Assert.Equal(20, executor.LastConeParameters.Height);
        Assert.Equal(2, sender.RequestCount);

        // Info card and confirmation request call it a frustum (圆台), not a cone.
        Assert.Contains(log.Messages, m => m.Role == ChatRole.Info && m.Text.Contains("圆台"));
        Assert.Contains(log.Messages, m => m.Role == ChatRole.Assistant && m.Text == "已创建圆台。");
    }

    [Fact]
    public async Task Frustum_top_over_base_rejected_without_executor_call()
    {
        var (controller, sender, _, executor, _) = Create();
        sender.Enqueue(
            ScriptedSender.ToolCall(name: "create_cone",
                args: """{"base_diameter":40,"top_diameter":50,"height":20,"unit":"mm"}"""),
            () => ChatCompletionResult.OkText("参数无效。"));

        controller.StartSend("创建圆台");
        await WaitForCompletedAsync(controller);

        Assert.Equal(0, executor.CallCount);
        var payload = ParsePayload(sender.Requests[1], 2);
        Assert.Equal("invalid_parameters", payload.GetProperty("status").GetString());
        Assert.Contains("小于 base_diameter", payload.GetProperty("message").GetString());
    }

    // --------------------------------------------- create_cone / create_sphere

    private const string ConeCallJson =
        """{"base_diameter":50,"height":30,"unit":"mm"}""";
    private const string SphereCallJson =
        """{"diameter":30,"unit":"mm"}""";

    [Fact]
    public async Task Cone_confirm_executes_once_with_typed_parameters()
    {
        var (controller, sender, log, executor, gate) = Create();
        sender.Enqueue(ScriptedSender.ToolCall(name: "create_cone", args: ConeCallJson),
            () => ChatCompletionResult.OkText("已创建圆锥体。"));

        controller.StartSend("创建底面直径 50 高 30 毫米的圆锥");
        await WaitForCompletedAsync(controller);

        Assert.Equal(1, executor.CallCount);
        Assert.Equal("create_cone", executor.LastToolName);
        Assert.NotNull(executor.LastConeParameters);
        Assert.Equal(50, executor.LastConeParameters!.BaseDiameter);
        Assert.Equal(30, executor.LastConeParameters.Height);
        Assert.Equal(2, sender.RequestCount);
        Assert.Equal("call_1", sender.Requests[1][2].ToolCallId);
        Assert.Contains(log.Messages, m => m.Role == ChatRole.Info && m.Text.Contains("圆锥"));
        Assert.Contains(log.Messages, m => m.Role == ChatRole.Assistant && m.Text == "已创建圆锥体。");
    }

    [Fact]
    public async Task Cone_invalid_arguments_rejected_without_executor_call()
    {
        var (controller, sender, _, executor, _) = Create();
        sender.Enqueue(
            ScriptedSender.ToolCall(name: "create_cone",
                args: """{"base_diameter":0,"height":25,"unit":"mm"}"""),
            () => ChatCompletionResult.OkText("参数无效。"));

        controller.StartSend("创建圆锥体");
        await WaitForCompletedAsync(controller);

        Assert.Equal(0, executor.CallCount);
        var payload = ParsePayload(sender.Requests[1], 2);
        Assert.Equal("invalid_parameters", payload.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Sphere_confirm_executes_once_and_feeds_verified_result()
    {
        var (controller, sender, log, executor, gate) = Create();
        sender.Enqueue(ScriptedSender.ToolCall(name: "create_sphere",
                args: """{"diameter":50,"unit":"mm"}"""),
            () => ChatCompletionResult.OkText("已创建球体。"));

        controller.StartSend("创建直径 50 毫米的球");
        await WaitForCompletedAsync(controller);

        Assert.Equal(1, executor.CallCount);
        Assert.Equal("create_sphere", executor.LastToolName);
        Assert.NotNull(executor.LastSphereParameters);
        Assert.Equal(50, executor.LastSphereParameters!.Diameter);

        var payload = ParsePayload(sender.Requests[1], 2);
        Assert.Equal("success", payload.GetProperty("status").GetString());
        Assert.Equal(20, payload.GetProperty("verifiedDimensionsMm").GetProperty("height").GetDouble());
        Assert.Equal("call_1", sender.Requests[1][2].ToolCallId);
    }

    [Fact]
    public async Task Sphere_cancel_executes_nothing()
    {
        var (controller, sender, _, executor, gate) = Create();
        gate.EnqueuePending(out var pending);
        sender.Enqueue(ScriptedSender.ToolCall(name: "create_sphere",
                args: """{"diameter":50,"unit":"mm"}"""));

        controller.StartSend("创建球体");
        await UntilAsync(() => gate.Requests.Count > 0);
        Assert.Equal(ToolParametersKind.Sphere, gate.Requests[0].ParametersKind);
        pending.TrySetResult(new ToolConfirmationDecision(false, null));
        await WaitForCompletedAsync(controller);

        Assert.Equal(0, executor.CallCount);
    }

    // -------------------------------------- Phase 9.5: remembered auto-target

    [Fact]
    public async Task Successful_block_records_LastCreatedBodyRef()
    {
        var (controller, sender, _, executor, gate) = Create();
        executor.PerToolResults["create_block"] = CreateBlockExecutionResult.Success(
            60, 60, 60, "成功", "part.prt",
            System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, object?> { ["newBodyRef"] = "BLOCK(5)" }));
        sender.Enqueue(ScriptedSender.ToolCall(name: "create_block",
            args: """{"length":60,"width":60,"height":60,"unit":"mm"}"""));

        controller.StartSend("创建60*60*60");
        await WaitForCompletedAsync(controller);

        Assert.Equal(1, executor.CallCount);
        Assert.Equal("BLOCK(5)", controller.LastCreatedBodyRef);
    }

    [Fact]
    public async Task Failed_result_does_not_record_LastCreatedBodyRef()
    {
        var (controller, sender, _, executor, gate) = Create();
        executor.PerToolResults["create_block"] = CreateBlockExecutionResult.Failure(
            ToolExecutionStatus.VerificationFailed, "验证失败");
        sender.Enqueue(ScriptedSender.ToolCall(name: "create_block",
            args: """{"length":60,"width":60,"height":60,"unit":"mm"}"""));

        controller.StartSend("创建60*60*60");
        await WaitForCompletedAsync(controller);

        Assert.Null(controller.LastCreatedBodyRef);
    }

    [Fact]
    public void ResetTargetResolution_clears_remembered_ref()
    {
        var (controller, _, _, _, _) = Create();
        controller.ResetTargetResolution();
        Assert.Null(controller.LastCreatedBodyRef);
    }

    [Fact]
    public void NxExecutor_implements_target_sink_and_defaults_to_null_provider()
    {
        // The real executor exposes the sink; without a controller wiring
        // the provider the auto-target candidate is null (fail-safe).
        var executor = new NXAI.Assistant.NxHost.NX2512.NxToolExecutor();
        Assert.Null(executor.LastCreatedBodyRefProvider);
        Assert.Null(executor.LastCreatedBodyRefProvider?.Invoke());
    }

    [Fact]
    public async Task Four_tools_all_registered_and_advertised()
    {
        var (controller, sender, log, executor, _) = Create();
        sender.Enqueue(() => ChatCompletionResult.OkText("普通回复"));

        controller.StartSend("你好");
        await WaitForCompletedAsync(controller);

        var tools = sender.ToolDeclarations[0]!;
        Assert.Equal(18, tools.Count);
        Assert.Equal(
            new HashSet<string>
            {
                "create_block", "create_cylinder", "create_cone", "create_sphere",
                "create_sketch", "create_extrude", "create_revolve", "boolean_operation",
                "create_hole",
                "create_threaded_hole", "create_clearance_hole",
                "create_counterbored_hole", "create_countersunk_hole",
                "create_fillet", "create_chamfer",
                "create_linear_pattern", "create_circular_pattern", "create_mirror",
            },
            tools.Select(t => t.Name).ToHashSet());
    }

    [Fact]
    public async Task Standard_hole_four_families_route_to_executor_and_confirm()
    {
        var executor = new CountingExecutor();
        var log = new ChatLog();
        var sender = new ScriptedSender();
        var gate = new ConfirmationGate();
        var controller = new ChatSessionController(
            new ModelService(), log, () => ("https://api.test/v1", "key", "model"),
            sender.SendAsync, 20, executor, gate.Handler);

        string[] names =
        {
            StandardHoleToolContract.ThreadedName,
            StandardHoleToolContract.ClearanceName,
            StandardHoleToolContract.CounterboredName,
            StandardHoleToolContract.CountersunkName,
        };
        string[] args =
        {
            """{"size":"M12","placement_face_ref":"FACE 1","hole_depth":25,"thread_depth":18,"unit":"mm"}""",
            """{"size":"M10","placement_face_ref":"FACE 1","unit":"mm"}""",
            """{"size":"M8","placement_face_ref":"FACE 1","unit":"mm"}""",
            """{"size":"M6","placement_face_ref":"FACE 1","unit":"mm"}""",
        };
        for (int i = 0; i < 4; i++)
        {
            gate.EnqueuePending(out var pending);
            sender.Enqueue(ScriptedSender.ToolCall(id: $"call_{i}", name: names[i], args: args[i]));

            controller.StartSend("打孔");
            await UntilAsync(() => gate.Requests.Count > 0);
            Assert.Equal(ToolParametersKind.StandardHole, gate.Requests[^1].ParametersKind);
            var std = gate.Requests[^1].StandardHoleParameters!;
            Assert.Equal(names[i] == StandardHoleToolContract.ThreadedName ? StandardHoleFamily.Threaded
                : names[i] == StandardHoleToolContract.ClearanceName ? StandardHoleFamily.SimpleClearance
                : names[i] == StandardHoleToolContract.CounterboredName ? StandardHoleFamily.Counterbored
                : StandardHoleFamily.Countersunk, std.Family);
            pending.TrySetResult(new ToolConfirmationDecision(true, "D:\\part.prt"));
            await WaitForCompletedAsync(controller);

            // Reset per-round state: a fresh tool call arrives each loop and
            // the controller's own round bookkeeping is per-turn.
            sender.Requests.Clear();
            gate.Requests.Clear();
        }

        Assert.Equal(4, executor.CallCount);
        Assert.Equal(4, executor.Calls.Count(name => names.Contains(name)));
    }

    [Fact]
    public async Task Standard_hole_unsupported_size_M16_rejected_before_confirmation()
    {
        // M16 is outside the parser's nine-size list — refused at parse time,
        // no confirmation card, no executor call.
        var (controller, sender, _, executor, gate) = Create();
        sender.Enqueue(ScriptedSender.ToolCall(name: "create_threaded_hole",
            args: """{"size":"M16","placement_face_ref":"FACE 1","unit":"mm"}"""));

        controller.StartSend("创建 M16 螺纹孔");
        await WaitForCompletedAsync(controller);

        Assert.Equal(0, executor.CallCount);
        Assert.Empty(gate.Requests);
        var payload = ParsePayload(sender.Requests[1], 2);
        Assert.Equal("invalid_parameters", payload.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Standard_hole_countersunk_M2_passes_parser_executor_refuses_at_table()
    {
        // M2 IS a supported size token — it reaches the executor, which
        // resolves against the standard table and refuses (the ISO 10642
        // countersunk series has no M2 record). The fake executor here
        // reports success for any table content; the table-level refusal is
        // covered by StandardHoleTableTests + the real NxHost executor's
        // own path. What this test pins: M2 does NOT get blocked before the
        // executor, and the structured result round-trips.
        var (controller, sender, _, executor, gate) = Create();
        sender.Enqueue(ScriptedSender.ToolCall(name: "create_countersunk_hole",
            args: """{"size":"M2","placement_face_ref":"FACE 1","unit":"mm"}"""));

        controller.StartSend("创建 M2 沉头孔");
        await WaitForCompletedAsync(controller);

        Assert.Equal(1, executor.CallCount);
        var payload = ParsePayload(sender.Requests[1], 2);
        Assert.Equal("success", payload.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Standard_hole_depth_rule_thread_ge_hole_rejected()
    {
        var (controller, sender, _, executor, gate) = Create();
        sender.Enqueue(ScriptedSender.ToolCall(name: "create_threaded_hole",
            args: """{"size":"M6","placement_face_ref":"FACE 1","hole_depth":10,"thread_depth":10,"unit":"mm"}"""));

        controller.StartSend("创建螺纹孔");
        await WaitForCompletedAsync(controller);

        Assert.Equal(0, executor.CallCount);
        Assert.Empty(gate.Requests);
        var payload = ParsePayload(sender.Requests[1], 2);
        Assert.Equal("invalid_parameters", payload.GetProperty("status").GetString());
    }

    // -------------------------------------- turn-state consistency receipts
    //
    // The controller must carry a trusted record of what its executors
    // actually did into the visible chat state and into the next turn's
    // outbound history. Every assertion below targets executor call counts
    // and structured statuses — display text alone is never sufficient.

    private static async Task<ChatCompletionResult> HangUntilCancelled(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return ChatCompletionResult.OkText("不应到达");
    }

    [Fact]
    public async Task T1_Successful_execution_then_cancelled_final_request_is_reported_as_completed_but_interrupted()
    {
        var (controller, sender, log, executor, gate) = Create();
        sender.Enqueue(ScriptedSender.ToolCall(), (Func<CancellationToken, Task<ChatCompletionResult>>)HangUntilCancelled);
        gate.EnqueuePending(out var pending);

        controller.StartSend("创建长方体 100x60x20 毫米");
        await UntilAsync(() => gate.Requests.Count > 0);
        pending.TrySetResult(new ToolConfirmationDecision(true, "D:\\part.prt"));
        await UntilAsync(() => executor.CallCount == 1); // execution committed
        await UntilAsync(() => sender.RequestCount == 2); // final round in flight, hanging
        controller.CancelActiveRequest();               // final round aborted
        await WaitForCompletedAsync(controller);

        // The model did its work: exactly one real executor call.
        Assert.Equal(1, executor.CallCount);
        // Cancel must NOT erase what happened: an interrupted-turn status card
        // says the modeling completed, never a bare "已取消当前请求" as last
        // word and no error card pretending failure.
        Assert.Contains(log.Messages,
            m => m.Role == ChatRole.Info && m.Text.Contains("已成功执行") && m.Text.Contains("create_block"));
        // A controller-issued assistant record carries the receipt across turns.
        Assert.Contains(log.Messages, m =>
            m.Role == ChatRole.Assistant && m.Text.Contains("[执行记录]") &&
            m.Text.Contains("create_block"));
        // The receipt is built from the structured result, not model text.
        Assert.Contains(log.Messages, m =>
            m.Role == ChatRole.Info && m.Text.Contains("最终回复被用户取消"));
    }

    [Fact]
    public async Task T2_Successful_execution_then_failed_final_request_is_not_misreported()
    {
        var (controller, sender, log, executor, gate) = Create();
        sender.Enqueue(ScriptedSender.ToolCall(),
            () => ChatCompletionResult.Fail(ModelChatError.ServerError, "服务器错误（HTTP 500）。"));

        controller.StartSend("创建长方体");
        await WaitForCompletedAsync(controller);

        Assert.Equal(1, executor.CallCount);
        // Error card states the HTTP error…
        Assert.Contains(log.Messages, m => m.Role == ChatRole.Error && m.Text.Contains("服务器错误"));
        // …but the controller record next to it states the executor DID
        // succeed; error text alone must be the full story.
        Assert.Contains(log.Messages, m =>
            m.Role == ChatRole.Assistant && m.Text.Contains("[执行记录]") &&
            m.Text.Contains("create_block") && m.Text.Contains("已成功执行"));
        Assert.DoesNotContain(log.Messages, m => m.Text == "模型返回了空白回复。");
    }

    [Fact]
    public async Task T3_Model_contradicting_final_text_cannot_overwrite_structured_receipt()
    {
        // The model succeeded but its reply claims it created nothing.
        // The visible state must keep the structured receipt so neither the
        // user nor the next turn reads this as "nothing was created".
        var (controller, sender, log, executor, _) = Create();
        sender.Enqueue(ScriptedSender.ToolCall(),
            () => ChatCompletionResult.OkText("抱歉，我还没有创建这个长方体，请问需要确认吗？"));

        controller.StartSend("创建长方体");
        await WaitForCompletedAsync(controller);

        // The executor really ran once.
        Assert.Equal(1, executor.CallCount);
        // The final assistant message keeps showing the contradictory text…
        Assert.Contains(log.Messages, m =>
            m.Role == ChatRole.Assistant && m.Text.Contains("还没有创建"));
        // …but the controller's execution record follows as its own assistant
        // entry (marked as a record, not model speech), so the visible and
        // outbound state still carries the structured truth.
        Assert.Contains(log.Messages, m =>
            m.Role == ChatRole.Assistant && m.Text.Contains("[执行记录]") &&
            m.Text.Contains("create_block"));
        // Order guarantee: the receipt is directly AFTER the contradicting
        // reply — the last visible word on the turn is the structured truth,
        // not the model's claim. Also proves the record is not model speech:
        // text "已执行 create_block" can only come from the controller.
        var compareToReply = log.Messages.Last(m =>
            m.Role == ChatRole.Assistant && m.Text.Contains("还没有创建"));
        var compareToRecord = log.Messages.Last(m =>
            m.Role == ChatRole.Assistant && m.Text.Contains("[执行记录]"));
        Assert.True(compareToRecord.TimestampUtc > compareToReply.TimestampUtc,
            "执行记录必须出现在矛盾回复之后（最终有效状态以回执为准）");
        // And the receipt is the LAST message of the turn — nothing after it
        // can dilute the structured outcome.
        Assert.Equal(compareToRecord, log.Messages[^1]);
    }

    [Fact]
    public async Task T4_Multi_step_turn_interruption_preserves_every_step_receipt()
    {
        // Round 1: cylinder confirmed+executed; round 2: block confirmed+
        // executed; then the final request fails. The interrupted-turn
        // record must name BOTH executed tools, not a count only.
        var (controller, sender, log, executor, gate) = Create();
        sender.Enqueue(
            ScriptedSender.ToolCall(name: "create_cylinder", args: CylinderCallJson)
            , ScriptedSender.ToolCall(id: "call_2", name: "create_block"),
            (Func<CancellationToken, Task<ChatCompletionResult>>)HangUntilCancelled);
        gate.EnqueuePending(out var pending1);
        gate.EnqueuePending(out var pending2);

        controller.StartSend("先圆柱后长方体");
        // Confirm mode resolves each gate request in arrival order; this keeps
        // the strict serial chain round1→exec1→round2→exec2→final-request.
        await UntilAsync(() => gate.Requests.Count == 1);
        pending1.TrySetResult(new ToolConfirmationDecision(true, "D:\\part.prt"));
        await UntilAsync(() => executor.CallCount == 1);
        await UntilAsync(() => gate.Requests.Count == 2);
        pending2.TrySetResult(new ToolConfirmationDecision(true, "D:\\part.prt"));
        await UntilAsync(() => executor.CallCount == 2);
        await UntilAsync(() => sender.RequestCount == 3); // final round in flight, hanging
        controller.CancelActiveRequest(); // the final round never answers
        await WaitForCompletedAsync(controller);

        Assert.Equal(2, executor.CallCount);
        // The receipt keeps every step (both names), not just the last one.
        Assert.Contains(log.Messages, m =>
            m.Role == ChatRole.Assistant && m.Text.Contains("[执行记录]") &&
            m.Text.Contains("create_cylinder") && m.Text.Contains("create_block"));
        Assert.Contains(log.Messages, m =>
            m.Role == ChatRole.Info && m.Text.Contains("2 步已成功执行"));
        // Both per-execution Info cards exist too.
        Assert.Equal(2, log.Messages.Count(m =>
            m.Role == ChatRole.Info && m.Text.StartsWith("建模执行结果：")));
    }

    [Fact]
    public async Task T5_Next_turn_outbound_carries_controller_execution_record()
    {
        // Turn 1 succeeds fully. Turn 2's request must carry the controller
        // receipt (assistant role), so the model can ground on what happened.
        var (controller, sender, log, executor, _) = Create();
        sender.Enqueue(ScriptedSender.ToolCall(),
            () => ChatCompletionResult.OkText("已创建长方体。"));

        controller.StartSend("创建长方体");
        await WaitForCompletedAsync(controller);
        Assert.Equal(1, executor.CallCount);

        controller.StartSend("很好，请继续整体计划");
        await WaitForCompletedAsync(controller);

        var outbound = sender.Requests[^1];
        // The real model reply of turn 1…
        Assert.Contains(outbound, m =>
            m.Role == OutboundMessage.RoleAssistant && m.Content?.Contains("已创建长方体。") == true);
        // …plus the controller's [执行记录] assistant entry.
        Assert.Contains(outbound, m =>
            m.Role == OutboundMessage.RoleAssistant && m.Content?.Contains("[执行记录]") == true &&
            m.Content?.Contains("create_block") == true);
        // Protocol shape stays coherent: NO dangling tool role survives the
        // rebuild, and no assistant entry carries tool_calls across turns.
        Assert.DoesNotContain(outbound, m => m.Role == OutboundMessage.RoleTool);
        Assert.DoesNotContain(outbound, m => m.ToolCalls is { Count: > 0 });
        // Order: user → model-text → controller-record → new user request.
        Assert.Equal(OutboundMessage.RoleUser, outbound[^1].Role);
        Assert.Equal(OutboundMessage.RoleAssistant, outbound[^2].Role);
        Assert.StartsWith("[执行记录]", outbound[^2].Content);
    }

    [Fact]
    public async Task T6_Unconfirmed_outcome_never_lets_controller_say_success()
    {
        // User rejected the modeling action. Nothing executed; the turn must
        // not produce an execution record, and "已取消" is the visible truth.
        var (controller, sender, log, executor, gate) = Create();
        sender.Enqueue(ScriptedSender.ToolCall());
        gate.EnqueuePending(out var pending);

        controller.StartSend("创建长方体");
        await UntilAsync(() => gate.Requests.Count > 0);
        pending.TrySetResult(new ToolConfirmationDecision(false, null));
        await WaitForCompletedAsync(controller);

        Assert.Equal(0, executor.CallCount);
        // The receipt states the plain fact (create_block was NOT executed,
        // status user_cancelled) — it exists so later turns cannot assume
        // success — but never claims the action succeeded.
        Assert.Contains(log.Messages, m =>
            m.Role == ChatRole.Assistant && m.Text.Contains("[执行记录]") &&
            m.Text.Contains("user_cancelled"));
        Assert.DoesNotContain(log.Messages, m =>
            m.Text.Contains("[执行记录]") && m.Text.Contains("已执行"));
        Assert.DoesNotContain(log.Messages, m => m.Text.Contains("已成功执行"));
        // both the model-facing and local views agree: user cancelled.
        var payload = ParsePayload(sender.Requests[1], 2);
        Assert.Equal("user_cancelled", payload.GetProperty("status").GetString());
    }

    [Fact]
    public async Task T7_Normal_success_turn_produces_exactly_one_assistant_record_without_programmed_text()
    {
        // Regression guard: the standard happy path must keep the exact same
        // visible state as before the receipts feature — one user, one
        // request-info, one result-info, execution info cards and exactly one
        // assistant reply, which contains both the model text and the record.
        var (controller, sender, log, executor, _) = Create();
        sender.Enqueue(ScriptedSender.ToolCall(),
            () => ChatCompletionResult.OkText("已创建长方体 100×60×20 毫米。"));

        controller.StartSend("创建长方体");
        await WaitForCompletedAsync(controller);

        Assert.Equal(1, executor.CallCount);
        var assistantEntries = log.Messages.Where(m => m.Role == ChatRole.Assistant).ToList();
        Assert.Equal(2, assistantEntries.Count);       // model text + receipt
        Assert.Equal("已创建长方体 100×60×20 毫米。", assistantEntries[0].Text);
        Assert.Contains("[执行记录]", assistantEntries[1].Text);
        Assert.Contains("create_block", assistantEntries[1].Text);
        Assert.DoesNotContain(log.Messages, m => m.Text.Contains("请基于真实部件状态继续"));
        // Full display hygiene: user + confirmation-request info + result info
        // + model text + receipt = 5 entries.
        Assert.Equal(5, log.Messages.Count);
    }

    [Fact]
    public async Task T8_Interrupted_turn_record_and_info_card_are_structurally_distinct()
    {
        // Cancel + success: same visible pairs as T1's, but asserting the
        // exact roles present — Info without error role for the outcome and
        // no new-style error confusion — guards the state transition table.
        var (controller, sender, log, executor, gate) = Create();
        sender.Enqueue(ScriptedSender.ToolCall(name: "create_cylinder", args: CylinderCallJson),
            (Func<CancellationToken, Task<ChatCompletionResult>>)HangUntilCancelled);
        gate.EnqueuePending(out var pending);

        controller.StartSend("创建圆柱体");
        await UntilAsync(() => gate.Requests.Count > 0);
        pending.TrySetResult(new ToolConfirmationDecision(true, "D:\\part.prt"));
        // The final request hangs on the token, so the cancel is observed
        // mid-flight — not racing the synchronous completion.
        await UntilAsync(() => executor.CallCount == 1);
        await UntilAsync(() => sender.RequestCount == 2);
        controller.CancelActiveRequest();
        await WaitForCompletedAsync(controller);

        Assert.Equal(1, executor.CallCount);
        Assert.Single(log.Messages, m =>
            m.Role == ChatRole.Info && m.Text.Contains("最终回复被用户取消"));
        // The receipt goes to the model, visibly marked, and names the tool.
        Assert.Contains(log.Messages, m =>
            m.Role == ChatRole.Assistant && m.Text.Contains("[执行记录]") &&
            m.Text.Contains("create_cylinder"));
        // No error card pretends otherwise.
        Assert.DoesNotContain(log.Messages, m => m.Role == ChatRole.Error);
    }

    // ----------------------------------------------------- T6 acceptance gap
    //
    // Original T6 scope: "user reports the last turn created nothing while
    // the controller's record of that execution is unknown/unconfirmed — no
    // blind autonomous re-execution, and a clear NEW request must not be
    // blocked either".

    [Fact]
    public async Task T9_Repeated_request_after_recorded_cancel_runs_full_validation_not_automatic_trust()
    {
        // Turn 1: user rejected create_block (recorded as user_cancelled in
        // the chat history). Turn 2: the model asks for create_block again.
        // Guard: the second turn must go through the FULL authorization
        // pipeline (confirmation gate, parser validation, precondition check)
        // — blind trust of either the receipt or the model is impossible.
        var (controller, sender, log, executor, gate) = Create();
        sender.Enqueue(ScriptedSender.ToolCall());
        gate.EnqueuePending(out var pending1);

        controller.StartSend("创建长方体");
        await UntilAsync(() => gate.Requests.Count > 0);
        pending1.TrySetResult(new ToolConfirmationDecision(false, null)); // user says no
        await WaitForCompletedAsync(controller);
        var callsAfterTurn1 = executor.CallCount;
        Assert.Equal(0, callsAfterTurn1);

        // Turn 2: the model requests the same tool again.
        gate.EnqueuePending(out var pending2);
        sender.Enqueue(ScriptedSender.ToolCall(id: "call_2"));
        controller.StartSend("再建一次");
        await UntilAsync(() => gate.Requests.Count > 1);
        // The confirmation gate fired again — no autonomous bypass.
        Assert.Equal(2, gate.Requests.Count);
        Assert.Equal(ToolParametersKind.Block, gate.Requests[1].ParametersKind);
        // Resolve as confirm: pipeline completes normally.
        pending2.TrySetResult(new ToolConfirmationDecision(true, "D:\\part.prt"));
        await WaitForCompletedAsync(controller);

        Assert.Equal(1, executor.CallCount);
        // The second turn's tool message reports the executed result — the
        // pipeline ran on its own merit, not on any assumption from turn 1.
        var lastIndex = sender.Requests[^1]
            .Select((m, i) => (m, i)).Last(t => t.m.Role == OutboundMessage.RoleTool).i;
        var payload = ParsePayload(sender.Requests[^1], lastIndex);
        Assert.Equal("success", payload.GetProperty("status").GetString());
    }

    [Fact]
    public async Task T10_Unknown_prior_state_clear_new_request_is_not_blocked()
    {
        // A fresh, explicit user request (no prior tool turn at all) must
        // reach the executor — no over-blocking by the receipts feature.
        var (controller, sender, log, executor, _) = Create();
        sender.Enqueue(ScriptedSender.ToolCall(),
            () => ChatCompletionResult.OkText("已创建长方体。"));

        controller.StartSend("创建一个 20x20x20 长方体");
        await WaitForCompletedAsync(controller);

        Assert.Equal(1, executor.CallCount);
        Assert.Equal(100, executor.LastParameters!.Length); // scripted executor verifies
        // Receipt anchors the executed step; no interruption machinery fired.
        Assert.Contains(log.Messages, m =>
            m.Role == ChatRole.Assistant && m.Text.Contains("[执行记录]") &&
            m.Text.Contains("create_block"));
        Assert.DoesNotContain(log.Messages, m => m.Text.Contains("最终回复未完成"));
    }

    // ------------------------------------------- shared execution ledger (L1+)
    //
    // Approved design: NxHostBoot holds one IModelingExecutionLedger across
    // chat-window rebuilds. These tests drive controller-level behavior with
    // a shared ledger instance simulating exactly that wiring: controller A
    // executes (or is torn down mid-execution), controller B is constructed
    // on the same ledger and must behave accordingly.

    private static (ChatSessionController, ScriptedSender, ChatLog, CountingExecutor, ConfirmationGate)
        CreateWithLedger(IModelingExecutionLedger ledger, int maxOutbound = 20)
    {
        var log = new ChatLog();
        var sender = new ScriptedSender();
        var executor = new CountingExecutor();
        var gate = new ConfirmationGate();
        var controller = new ChatSessionController(
            new ModelService(), log, () => ("https://api.test/v1", "key", "model"),
            sender.SendAsync, maxOutbound, executor, gate.Handler,
            executionLedger: ledger);
        return (controller, sender, log, executor, gate);
    }

    [Fact]
    public async Task T11_Successful_execution_recorded_immediately_survives_form_teardown()
    {
        // Controller A executes successfully. The result reaches the ledger
        // BEFORE the turn completes, so even an immediate Detach (tearing the
        // turn down with the window) leaves the durable success fact intact.
        var ledger = new ModelingExecutionLedger();
        var (controller, sender, _, executor, gate) = CreateWithLedger(ledger);
        sender.Enqueue(ScriptedSender.ToolCall());
        gate.EnqueuePending(out var pending);

        controller.StartSend("创建长方体");
        await UntilAsync(() => gate.Requests.Count > 0);
        pending.TrySetResult(new ToolConfirmationDecision(true, "D:\\part.prt"));
        await UntilAsync(() => executor.CallCount == 1);
        // The ledger fact already exists while the turn is still running
        // (final model request has not answered yet).
        var midTurn = Assert.Single(ledger.Snapshot());
        Assert.Equal(ToolExecutionStatus.Success, midTurn.Status);
        Assert.Equal(ModelingExecutionState.Recorded, midTurn.State);
        Assert.Equal("create_block", midTurn.ToolName);

        // Now tear the window down mid-turn: the final request would never
        // complete, but the ledger entry must stay recorded.
        controller.Detach();
        await WaitForCompletedAsync(controller);
        var afterDetach = Assert.Single(ledger.Snapshot());
        Assert.Equal(ToolExecutionStatus.Success, afterDetach.Status);
        Assert.Equal(ModelingExecutionState.Recorded, afterDetach.State);
        Assert.False(ledger.HasUnresolved);
        Assert.False(ledger.HasUnacknowledgedAttempt("create_block"));
    }

    [Fact]
    public async Task T12_New_controller_on_same_ledger_sees_old_success_receipt()
    {
        // Form A ran a successful create_block and was destroyed (controller's
        // log is gone). Form B is constructed on the same shared ledger: the
        // unseen success entry from form A must surface into B's chat log.
        var ledger = new ModelingExecutionLedger();
        var (controllerA, senderA, logA, executorA, gateA) = CreateWithLedger(ledger);
        senderA.Enqueue(ScriptedSender.ToolCall(),
            () => ChatCompletionResult.OkText("已创建长方体。"));
        controllerA.StartSend("创建长方体");
        await WaitForCompletedAsync(controllerA);
        Assert.Equal(1, executorA.CallCount);
        _ = (logA, gateA);

        // Form B: fresh controller on the same ledger (all-new ChatLog).
        var (controllerB, senderB, logB, executorB, _) = CreateWithLedger(ledger);
        _ = logB;
        // The window constructor surfaces unseen entries; at the harness
        // level (pure controller) the ledger itself is the carrier — the data
        // for B's first Info card is exactly the recorded A-fact.
        var surfaced = ledger.TakeUnseen();
        var aEntry = Assert.Single(surfaced, e => e.ToolName == "create_block");
        Assert.Equal(ToolExecutionStatus.Success, aEntry.Status);
        Assert.Equal(ModelingExecutionState.Recorded, aEntry.State);
        // And B can still model freely — a recorded success does not block.
        senderB.Enqueue(ScriptedSender.ToolCall(name: "create_cylinder", args: CylinderCallJson),
            () => ChatCompletionResult.OkText("B 已创建圆柱体。"));
        controllerB.StartSend("创建圆柱体");
        await WaitForCompletedAsync(controllerB);
        Assert.Equal(1, executorB.CallCount);   // B's own execution went through
        Assert.Equal("create_cylinder", executorB.LastToolName);
        // B's own execution was also recorded — the ledger accumulates facts.
        var bEntry = ledger.Snapshot().Single(e => e.ToolName == "create_cylinder");
        Assert.Equal(ToolExecutionStatus.Success, bEntry.Status);
    }

    [Fact]
    public async Task T13_Unknown_result_blocks_autonomous_rerun_until_acknowledged()
    {
        // Form A: the executor is entered, then the process "dies" mid-call —
        // the executor never returns, so RecordResult never happens and the
        // ledger entry stays Attempting: the honest "result unknown" fact.
        var ledger = new ModelingExecutionLedger();
        var gateA = new ConfirmationGate();
        var senderA = new ScriptedSender();
        var executorA = new ThrowingExecutor();
        var controllerA = new ChatSessionController(
            new ModelService(), new ChatLog(), () => ("https://api.test/v1", "key", "model"),
            senderA.SendAsync, 20, executorA, gateA.Handler,
            executionLedger: ledger);
        senderA.Enqueue(ScriptedSender.ToolCall());
        gateA.EnqueuePending(out var pendingA);

        controllerA.StartSend("创建长方体");
        await UntilAsync(() => pendingA.Task.IsCompleted == false);
        pendingA.TrySetResult(new ToolConfirmationDecision(true, "D:\\part.prt"));
        // ThrowingExecutor throws inside the executor call → the attempt is
        // registered, the throw is caught by RunRequestAsync's error path →
        // no RecordResult. Wait for the whole turn to end, then verify.
        await WaitForCompletedAsync(controllerA);
        var entryA = Assert.Single(ledger.Snapshot());
        Assert.Equal(ModelingExecutionState.Attempting, entryA.State);
        Assert.Null(entryA.Status); // no claimed outcome anywhere
        Assert.True(ledger.HasUnresolved);
        Assert.True(ledger.HasUnacknowledgedAttempt("create_block"));

        // Form B: autonomous mode manager + same ledger + same tool request.
        var mmB = new ExecutionModeManager();
        mmB.SetMode(ExecutionMode.Autonomous);
        var logB = new ChatLog();
        var senderB = new ScriptedSender();
        var executorB = new CountingExecutor();
        var gateB = new ConfirmationGate();
        // Exactly two model responses, then the turn ends: one tool call, and
        // a final text reply. No default looping — every executor call after
        // this must be accountable to exactly one scripted call.
        senderB.Enqueue(ScriptedSender.ToolCall(),
            () => ChatCompletionResult.OkText("已按请求执行。"));
        var controllerB = new ChatSessionController(
            new ModelService(), logB, () => ("https://api.test/v1", "key", "model"),
            senderB.SendAsync, 20, executorB, gateB.Handler,
            modeManager: mmB, executionLedger: ledger);

        // The model immediately asks again for create_block. The unknown
        // gate must NOT silently re-fire (old contract: forced confirm
        // dialog — replaced 2026-10-19: autonomous mode now resolves the
        // unknown state via the READ-ONLY PROBE, never a human dialog).
        // CountingExecutor cannot probe (returns null = cannot judge):
        // contract = report + STOP. Nothing executes, no dialog appears.
        controllerB.StartSend("再建一次长方体");
        await WaitForCompletedAsync(controllerB);

        // Strict: NO human dialog, NO blind re-execution, the turn ends
        // with the unjudgeable report, and the form-A entry keeps its
        // honest unknown state (never downgraded, never deleted).
        Assert.Empty(gateB.Requests);                   // NO confirm path
        Assert.Equal(0, executorB.CallCount);           // no execution
        Assert.True(ledger.HasUnacknowledgedAttempt("create_block"));
        Assert.True(ledger.HasUnresolved);
        Assert.Contains(logB.Messages,
            m => m.Role == ChatRole.Error && m.Text.Contains("无法判断"));
        Assert.Contains(ledger.Snapshot(),
            e => e.State == ModelingExecutionState.Attempting && e.Status == null);
    }

    [Fact]
    public async Task T14_Unknown_result_does_not_block_new_tool_or_confirmed_retry()
    {
        // Unknown for create_block; a DIFFERENT tool (create_cylinder) in
        // autonomous mode must not be blocked — the gate is per-tool, not a
        // global freeze.
        var ledger = new ModelingExecutionLedger();
        ledger.BeginAttempt("create_block", "未知长方体"); // stays unknown

        var mm = new ExecutionModeManager();
        mm.SetMode(ExecutionMode.Autonomous);
        var log = new ChatLog();
        var sender = new ScriptedSender();
        var executor = new CountingExecutor();
        var gate = new ConfirmationGate();
        var controller = new ChatSessionController(
            new ModelService(), log, () => ("https://api.test/v1", "key", "model"),
            sender.SendAsync, 20, executor, gate.Handler,
            modeManager: mm, executionLedger: ledger);

        sender.Enqueue(ScriptedSender.ToolCall(name: "create_cylinder", args: CylinderCallJson),
            () => ChatCompletionResult.OkText("已创建圆柱体。"));
        controller.StartSend("创建圆柱体");
        await WaitForCompletedAsync(controller);

        Assert.Empty(gate.Requests);   // autonomous allowlist still works
        Assert.Equal(1, executor.CallCount);
        Assert.Equal("create_cylinder", executor.LastToolName);
        // The block-unknown stays untouched and still unacknowledged.
        Assert.True(ledger.HasUnacknowledgedAttempt("create_block"));
        // The cylinder execution itself was recorded as a terminating fact.
        var cylEntry = ledger.Snapshot().Single(e => e.ToolName == "create_cylinder");
        Assert.Equal(ToolExecutionStatus.Success, cylEntry.Status);
    }

    [Fact]
    public async Task T15_Never_started_request_is_not_marked_in_ledger()
    {
        // Parameter rejection / unknown tool / user cancel must leave the
        // ledger untouched — only actual executor entry gets an Attempting
        // entry, so "not executed" is distinguishable from "result unknown".
        var ledger = new ModelingExecutionLedger();
        var (controller, sender, _, executor, gate) = CreateWithLedger(ledger);

        // (1) Invalid parameters: parser rejects before the executor.
        sender.Enqueue(
            ScriptedSender.ToolCall(args: """{"length":-5,"width":10,"height":10,"unit":"mm"}"""));
        controller.StartSend("非法尺寸长方体");
        await WaitForCompletedAsync(controller);

        Assert.Equal(0, executor.CallCount);
        Assert.Empty(ledger.Snapshot());
        Assert.False(ledger.HasUnresolved);

        // (2) A user cancel at the confirmation stage: likewise no entry.
        gate.EnqueuePending(out var pending);
        sender.Enqueue(ScriptedSender.ToolCall());
        controller.StartSend("创建长方体");
        await UntilAsync(() => gate.Requests.Count > 0);
        pending.TrySetResult(new ToolConfirmationDecision(false, null));
        await WaitForCompletedAsync(controller);

        Assert.Equal(0, executor.CallCount);
        Assert.Empty(ledger.Snapshot());
        Assert.False(ledger.HasUnresolved);
    }

    /// <summary>
    /// Executor contract check: the structured ExecutionUncertain failure
    /// path — an executor that ran but cannot vouch for committed geometry —
    /// must leave the entry in the unknown-gate state, so a fresh controller
    /// in autonomous mode on the same ledger cannot silently re-fire it.
    /// </summary>
    private sealed class UncertainExecutor : INxToolExecutor
    {
        public CreateBlockExecutionResult ExecuteCreateHole(
            CreateHoleParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Success(1, 1, 1, "ok", "part");
        public CreateBlockExecutionResult ExecuteCreateStandardHole(
            CreateStandardHoleParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Success(1, 1, 1, "ok", "part");
        public CreateBlockExecutionResult ExecuteCreateFillet(
            CreateFilletParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Success(1, 1, 1, "ok", "part");
        public CreateBlockExecutionResult ExecuteCreateChamfer(
            CreateChamferParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Success(1, 1, 1, "ok", "part");
        public CreateBlockExecutionResult ExecuteCreateLinearPattern(
            CreateLinearPatternParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Success(1, 1, 1, "ok", "part");
        public CreateBlockExecutionResult ExecuteCreateCircularPattern(
            CreateCircularPatternParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Success(1, 1, 1, "ok", "part");
        public CreateBlockExecutionResult ExecuteCreateMirror(
            CreateMirrorParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Success(1, 1, 1, "ok", "part");

        public CreateBlockExecutionResult ExecuteCreateBlock(
            CreateBlockParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionUncertain,
                "无法确认几何是否已提交（模拟部分修改后失败）", rolledBack: false);

        public CreateBlockExecutionResult ExecuteCreateCylinder(
            CreateCylinderParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionUncertain, "无法确认", rolledBack: false);

        public CreateBlockExecutionResult ExecuteCreateCone(
            CreateConeParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionUncertain, "无法确认", rolledBack: false);

        public CreateBlockExecutionResult ExecuteCreateSphere(
            CreateSphereParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionUncertain, "无法确认", rolledBack: false);

        public CreateBlockExecutionResult ExecuteCreateSketch(
            CreateSketchParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionUncertain, "无法确认", rolledBack: false);

        public CreateBlockExecutionResult ExecuteCreateExtrude(
            CreateExtrudeParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionUncertain, "无法确认", rolledBack: false);

        public CreateBlockExecutionResult ExecuteCreateRevolve(
            CreateRevolveParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionUncertain, "无法确认", rolledBack: false);

        public CreateBlockExecutionResult ExecuteBooleanOperation(
            BooleanOperationParameters parameters, string? expectedWorkPartFullPath) =>
            CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionUncertain, "无法确认", rolledBack: false);

        public ModelFeatureProbeResult? ProbeCommittedFeature(string toolName, string operationSummary) => null;
        public ModelFeatureProbeResult? ProbeCommittedFeature(
            string toolName, string operationSummary, IReadOnlySet<string>? preExistingFeatureIds,
            BooleanOperationType? operationType = null) =>
            ProbeCommittedFeature(toolName, operationSummary);

        public IReadOnlySet<string>? ProbeFeatureSnapshot(string toolName, string? featureKind = null) => null;
    }

    [Fact]
    public async Task T16_ExecutionUncertain_keeps_unknown_gate_across_rebuild()
    {
        // Form A: the executor ran and returned a structured uncertain
        // outcome — the turn completed normally, but the ledger entry stays
        // Attempting because the executor cannot vouch for committed geometry.
        var ledger = new ModelingExecutionLedger();
        var senderA = new ScriptedSender();
        var executorA = new UncertainExecutor();
        var gateA = new ConfirmationGate();
        var controllerA = new ChatSessionController(
            new ModelService(), new ChatLog(), () => ("https://api.test/v1", "key", "model"),
            senderA.SendAsync, 20, executorA, gateA.Handler,
            executionLedger: ledger);
        senderA.Enqueue(ScriptedSender.ToolCall(),
            () => ChatCompletionResult.OkText("执行完成。" ));
        controllerA.StartSend("创建长方体");
        await WaitForCompletedAsync(controllerA);

        // The tool call reached the executor (its contract decided the
        // outcome is uncertain) and the ledger keeps the unknown gate.
        var entry = Assert.Single(ledger.Snapshot());
        Assert.Equal(ModelingExecutionState.Attempting, entry.State);
        Assert.Null(entry.Status);
        Assert.True(ledger.HasUnresolved);
        Assert.True(ledger.HasUnacknowledgedAttempt("create_block"));

        // Form B: autonomous mode, same ledger, same tool again. The unknown
        // state now triggers the READ-ONLY PROBE (not a human dialog — the
        // 2026-10-19 live failure). The UncertainExecutor cannot probe
        // (returns null = cannot judge): the contract is report + STOP, no
        // execution, the attempt stays honestly Attempting.
        var mmB = new ExecutionModeManager();
        mmB.SetMode(ExecutionMode.Autonomous);
        var senderB = new ScriptedSender();
        var executorB = new CountingExecutor();
        var gateB = new ConfirmationGate();
        var logB = new ChatLog();
        senderB.Enqueue(ScriptedSender.ToolCall(),
            () => ChatCompletionResult.OkText("已按请求执行。"));
        var controllerB = new ChatSessionController(
            new ModelService(), logB, () => ("https://api.test/v1", "key", "model"),
            senderB.SendAsync, 20, executorB, gateB.Handler,
            modeManager: mmB, executionLedger: ledger);

        // Before any user review the unknown gate is exactly what forms
        // B's dispatch decision — assert it now.
        Assert.True(ledger.HasUnacknowledgedAttempt("create_block"));

        controllerB.StartSend("再建一次长方体");
        await WaitForCompletedAsync(controllerB);

        // Strict, new contract: NO human dialog, NO blind re-execution, the
        // turn ends with the unjudgeable report, and the entry keeps its
        // honest unknown state (never downgraded to "not executed").
        Assert.Empty(gateB.Requests);
        Assert.Equal(0, executorB.CallCount);
        Assert.True(ledger.HasUnacknowledgedAttempt("create_block"));
        Assert.True(ledger.HasUnresolved);
        var aById = ledger.Snapshot().Single(e => e.LedgerId == entry.LedgerId);
        Assert.Equal(ModelingExecutionState.Attempting, aById.State); // still an attempt fact
        Assert.Null(aById.Status);                                    // never claimed terminated
        Assert.False(aById.Acknowledged);                             // nothing released it
        Assert.Contains(logB.Messages, m =>
            m.Role == ChatRole.Error && m.Text.Contains("无法判断"));
        _ = logB;
    }

    // ------------------------------------------------------- multi-tool turns
    //
    // One user request may produce FOUR modeling objects (block/cylinder/
    // cone/sphere). One assistant message may carry multiple tool calls; the
    // controller executes them strictly in order, feeds each structured
    // result back under its own tool_call_id, and lets the model's next
    // response decide the final text. Failures are reported as failures.
    private const string CylinderJson = CylinderCallJson;
    private const string ConeJson = ConeCallJson;
    private const string SphereJson = SphereCallJson;

    [Fact]
    public async Task MT1_FourToolCalls_execute_in_order_and_feed_each_result()
    {
        var (controller, sender, log, executor, gate) = Create();
        // One model response requesting all four base solids in order, then
        // the final reply. The counting executor records call order.
        sender.Enqueue(ChatCompletionResult.OkToolCalls([
            new ParsedToolCall("c1", "create_block", ToolCallJson),
            new ParsedToolCall("c2", "create_cylinder", CylinderJson),
            new ParsedToolCall("c3", "create_cone", ConeJson),
            new ParsedToolCall("c4", "create_sphere", SphereJson),
        ]), () => ChatCompletionResult.OkText("四个对象已创建。"));

        controller.StartSend("创建长方体、圆柱体、圆台、球体");
        await WaitForCompletedAsync(controller);

        // All four executed, strictly in the order the model requested them.
        Assert.Equal(4, executor.CallCount);
        Assert.Equal(
            ["create_block", "create_cylinder", "create_cone", "create_sphere"],
            executor.Calls);
        // Two model rounds: the multi-tool response and the final text.
        Assert.Equal(2, sender.RequestCount);
        Assert.Contains(log.Messages, m => m.Role == ChatRole.Assistant && m.Text == "四个对象已创建。");

        // Protocol integrity: request 2 declares ONE assistant message with
        // all four tool_calls and FOUR tool messages, each matched by id.
        var second = sender.Requests[1];
        var assistantMsg = second.Single(m =>
            m.Role == OutboundMessage.RoleAssistant && m.ToolCalls is { Count: 4 });
        Assert.Equal(["c1", "c2", "c3", "c4"], assistantMsg.ToolCalls!.Select(t => t.Id).ToList());
        var toolMsgs = second.Where(m => m.Role == OutboundMessage.RoleTool).ToList();
        Assert.Equal(4, toolMsgs.Count);
        Assert.Equal(["c1", "c2", "c3", "c4"],
            toolMsgs.Select(t => t.ToolCallId).ToList());
        foreach (var msg in toolMsgs)
        {
            using var doc = JsonDocument.Parse(msg.Content!);
            Assert.Equal("success", doc.RootElement.GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task MT2_FirstSucceedsSecondFails_reality_reported_not_success()
    {
        // Failure is a terminating fact for THAT call; the turn continues so
        // the model can see the true outcome — it is never reported as a
        // success, and later results still feed back under their own ids.
        var (controller, sender, log, executor, _) = Create();
        executor.PerToolResults["create_cylinder"] =
            CreateBlockExecutionResult.Failure(ToolExecutionStatus.ExecutionFailed,
                "NX 执行失败", rolledBack: true);
        sender.Enqueue(ChatCompletionResult.OkToolCalls([
            new ParsedToolCall("c1", "create_block", ToolCallJson),
            new ParsedToolCall("c2", "create_cylinder", CylinderJson),
        ]), () => ChatCompletionResult.OkText("长方体成功，圆柱体失败。"));

        controller.StartSend("块和圆柱");
        await WaitForCompletedAsync(controller);

        Assert.Equal(2, executor.CallCount); // both were attempted, no silent drop
        var second = sender.Requests[1];
        var toolMsgs = second.Where(m => m.Role == OutboundMessage.RoleTool).ToList();
        Assert.Equal(2, toolMsgs.Count);
        using (var doc1 = JsonDocument.Parse(toolMsgs[0].Content!))
            Assert.Equal("success", doc1.RootElement.GetProperty("status").GetString());
        using (var doc2 = JsonDocument.Parse(toolMsgs[1].Content!))
        {
            Assert.Equal("execution_failed", doc2.RootElement.GetProperty("status").GetString());
            Assert.True(doc2.RootElement.GetProperty("rolledBack").GetBoolean());
        }
        // The model's final text reaches the user; the receipts also state
        // the mixed reality in the controller record.
        Assert.Contains(log.Messages,
            m => m.Role == ChatRole.Assistant && m.Text.Contains("圆柱体失败"));
        Assert.Contains(log.Messages, m =>
            m.Role == ChatRole.Info && m.Text.Contains("建模执行结果") && m.Text.Contains("失败"));
    }

    [Fact]
    public async Task MT3_InvalidParam_and_unknown_tools_rejected_per_call()
    {
        // Mixed bag: one valid block, one invalid-args cylinder, one unknown
        // tool. Only the valid block reaches the executor; each rejected
        // call still gets its OWN structured tool result (per tool_call_id).
        var (controller, sender, log, executor, _) = Create();
        sender.Enqueue(ChatCompletionResult.OkToolCalls([
            new ParsedToolCall("c1", "create_block", ToolCallJson),
            new ParsedToolCall("c2", "create_cylinder",
                """{"diameter":-5,"height":25,"unit":"mm"}"""),
            new ParsedToolCall("c3", "teleport_to_moon", "{}"),
        ]), () => ChatCompletionResult.OkText("处理完成。"));

        controller.StartSend("三项操作");
        await WaitForCompletedAsync(controller);

        Assert.Equal(1, executor.CallCount);       // only the valid block ran
        Assert.Equal(["create_block"], executor.Calls);
        var second = sender.Requests[1];
        var toolMsgs = second.Where(m => m.Role == OutboundMessage.RoleTool).ToList();
        Assert.Equal(3, toolMsgs.Count);            // every call answered
        Assert.Equal(["c1", "c2", "c3"], toolMsgs.Select(t => t.ToolCallId).ToList());
        using var p2 = JsonDocument.Parse(toolMsgs[1].Content!);
        Assert.Equal("invalid_parameters", p2.RootElement.GetProperty("status").GetString());
        using var p3 = JsonDocument.Parse(toolMsgs[2].Content!);
        Assert.Equal("unknown_tool", p3.RootElement.GetProperty("status").GetString());
        // The two rejections are visible as local Info cards too.
        Assert.Contains(log.Messages, m => m.Text.Contains("参数校验未通过"));
        Assert.Contains(log.Messages, m => m.Text.Contains("teleport_to_moon") && m.Text.Contains("未注册"));
    }

    [Fact]
    public async Task MT4_ExecutionUncertain_in_multi_tools_keeps_reality()
    {
        var ledger = new ModelingExecutionLedger();
        var (controller, sender, log, executor, _) = CreateWithLedger(ledger);
        executor.PerToolResults["create_cone"] = CreateBlockExecutionResult.Failure(
            ToolExecutionStatus.ExecutionUncertain, "无法确认几何是否提交");
        sender.Enqueue(ChatCompletionResult.OkToolCalls([
            new ParsedToolCall("c1", "create_block", ToolCallJson),
            new ParsedToolCall("c2", "create_cone", ConeJson),
        ]), () => ChatCompletionResult.OkText("长方体确定，圆台待核实。"));

        controller.StartSend("块和圆台");
        await WaitForCompletedAsync(controller);

        Assert.Equal(2, executor.CallCount);
        var second = sender.Requests[1];
        var c2Tool = second.Single(m => m.Role == OutboundMessage.RoleTool && m.ToolCallId == "c2");
        using var doc = JsonDocument.Parse(c2Tool.Content!);
        Assert.Equal("execution_uncertain", doc.RootElement.GetProperty("status").GetString());
        // The ledger keeps the uncertain cone as an unacknowledged attempt;
        // the block is a clean Recorded success.
        var coneEntry = Assert.Single(ledger.Snapshot(), e => e.ToolName == "create_cone");
        Assert.Equal(ModelingExecutionState.Attempting, coneEntry.State);
        Assert.True(ledger.HasUnacknowledgedAttempt("create_cone"));
        var blockEntry = ledger.Snapshot().Single(e => e.ToolName == "create_block");
        Assert.Equal(ToolExecutionStatus.Success, blockEntry.Status);
    }

    [Fact]
    public async Task MT5_Over_turn_budget_calls_get_explicit_refusal_tool_results()
    {
        // Five valid calls in ONE model response: the first four execute (the
        // per-turn budget), the fifth is refused with a real, id-matched
        // tool result — never silently dropped. No loop, no extra requests.
        var (controller, sender, log, executor, gate) = Create();
        var five = ChatCompletionResult.OkToolCalls([
            new ParsedToolCall("c1", "create_block", ToolCallJson),
            new ParsedToolCall("c2", "create_cylinder", CylinderJson),
            new ParsedToolCall("c3", "create_cone", ConeJson),
            new ParsedToolCall("c4", "create_sphere", SphereJson),
            new ParsedToolCall("c5", "create_block", ToolCallJson),
        ]);
        sender.Enqueue(five, () => ChatCompletionResult.OkText("按预算执行完成。"));

        controller.StartSend("五个对象");
        await WaitForCompletedAsync(controller);

        Assert.Equal(4, executor.CallCount);   // per-turn execution budget
        Assert.Equal(
            ["create_block", "create_cylinder", "create_cone", "create_sphere"],
            executor.Calls);                        // c5 did NOT hijack the next step
        Assert.Equal(2, sender.RequestCount);       // no loop
        // The fifth call's result explicitly reports the refusal, keyed c5.
        var second = sender.Requests[1];
        var c5Tool = second.Single(m => m.Role == OutboundMessage.RoleTool && m.ToolCallId == "c5");
        using var doc = JsonDocument.Parse(c5Tool.Content!);
        Assert.Equal("execution_failed", doc.RootElement.GetProperty("status").GetString());
        Assert.Contains("上限", doc.RootElement.GetProperty("message").GetString());
        // The visible chat also names the refusal rather than hiding it.
        Assert.Contains(log.Messages, m => m.Text.Contains("上限") && m.Text.Contains("未执行"));
        _ = gate;
    }
}

/// <summary>Public entry for cross-test-class routing tests (EdgeFinishToolContractTests).</summary>
public static class ChatSessionControllerToolTestsPublicHelpers
{
    public static Task WaitForCompletedAsyncPublic(ChatSessionController controller)
    {
        // Reuse the private implementation through reflection-free rebind:
        // the same WhenAny+Delay pattern (see ChatSessionControllerToolTests
        // remarks for why the raw TCS await must not be used).
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.RequestCompleted += (_, _) => tcs.TrySetResult();
        return Task.WhenAny(tcs.Task, Task.Delay(10_000));
    }

    public static async Task UntilAsyncPublic(Func<bool> condition)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("condition not met within 10s");
            await Task.Delay(50);
        }
    }
}
