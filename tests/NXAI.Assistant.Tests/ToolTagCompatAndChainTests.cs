using System.Text.Json;
using NXAI.Assistant.Core;
using NXAI.Assistant.UI;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 7.3.2 regression tests: the live protocol shape
/// (extrude / sketch=SKETCH_000 / distance=50 / unit=mm — direction omitted),
/// the sketch→sketch_ref parameter alias, the two-step serial chain
/// (create_sketch → create_extrude fed the first tool's real result) under
/// both execution modes, and the round cap. All fakes — no NX, no network.
/// </summary>
public class ToolTagCompatAndChainTests
{
    private const string P = "an" + "tm" + "l:";
    private static string InvokeOpen => "<" + P + "inv" + "oke";
    private static string InvokeClose => "</" + P + "inv" + "oke" + ">";
    private static string ParamOpen(string key) => "<" + P + "par" + "ameter name=\"" + key + "\">";
    private static string ParamClose => "</" + P + "par" + "ameter" + ">";

    private static string Block(string name, params (string Key, string Value)[] ps)
    {
        var inner = string.Concat(ps.Select(p => ParamOpen(p.Key) + p.Value + ParamClose));
        return InvokeOpen + name + inner + InvokeClose;
    }

    private static ChatCompletionResult Parse(string responseBody) =>
        ModelService.ParseChatCompletionResponse(responseBody);

    private static string ResponseJson(string content) => JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { content } } },
    });

    // ------------------------------------------------------------ live shape

    [Fact]
    public void LiveShape_SketchAlias_NormalizesAndPassesRealParser()
    {
        // Exact live shape: extrude / sketch=SKETCH_000 / distance=50 /
        // unit=mm — no direction (the product contract defaults to the
        // sketch normal; schema lists only sketch_ref/distance/unit as
        // required).
        string block = Block("extrude",
            ("sketch", "SKETCH_000"), ("distance", "50"), ("unit", "mm"));
        var result = Parse(ResponseJson(block));

        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("create_extrude", call.FunctionName);
        using var doc = JsonDocument.Parse(call.ArgumentsJson);
        Assert.Equal("SKETCH_000", doc.RootElement.GetProperty("sketch_ref").GetString());

        var p = new CreateExtrudeRequestParser().ParseParameters(call.ArgumentsJson, out var error);
        Assert.Null(error);
        Assert.Equal("SKETCH_000", p!.SketchRef);
        Assert.Equal(50, p.Distance);
        Assert.Equal(ExtrudeDirection.Normal, p.Direction); // contract default
    }

    [Fact]
    public void DirectionOmitted_DefaultsToNormal_ProducesNoError()
    {
        // Product contract: extruding along the sketch normal by default is
        // the documented behavior (schema: direction optional; parser:
        // default Normal). Missing direction is NOT a missing required param.
        string block = Block("extrude",
            ("sketch", "S1"), ("distance", "10"), ("unit", "mm"));
        var result = Parse(ResponseJson(block));
        var call = Assert.Single(result.ToolCalls);
        using var doc = JsonDocument.Parse(call.ArgumentsJson);
        Assert.False(doc.RootElement.TryGetProperty("direction", out _));

        var p = new CreateExtrudeRequestParser().ParseParameters(call.ArgumentsJson, out var error);
        Assert.Null(error);
        Assert.Equal(ExtrudeDirection.Normal, p!.Direction);
    }

    [Fact]
    public void SketchAlias_ConflictingCanonicalName_IsRejected()
    {
        string block = Block("extrude",
            ("sketch", "A"), ("sketch_ref", "B"), ("distance", "1"), ("unit", "mm"));
        var result = Parse(ResponseJson(block));
        Assert.False(result.Success);
        Assert.Contains("冲突", result.ErrorMessage);
        // No raw markers in the user-facing error.
        Assert.DoesNotContain("invoke", result.ErrorMessage);
    }

    // ------------------------------------------------------------ serial chain

    private sealed class ScriptedSender
    {
        public readonly List<IReadOnlyList<OutboundMessage>> Requests = new();
        public readonly List<IReadOnlyList<ToolDefinition>?> ToolDeclarations = new();
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
            ToolDeclarations.Add(tools);
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
            string id, string name, string args) =>
            ChatCompletionResult.OkToolCalls([new ParsedToolCall(id, name, args)]);
    }

    private sealed class CountingExecutor : INxToolExecutor
    {
        public List<string> ExecutedTools { get; } = new();
        public List<string?> LastExpectedPaths { get; } = new();
        public string? NextResultJson { get; set; }
        public int CallCount => ExecutedTools.Count;

        public CreateBlockExecutionResult ExecuteCreateBlock(
            CreateBlockParameters p, string? path) => Record("create_block", path);
        public CreateBlockExecutionResult ExecuteCreateCylinder(
            CreateCylinderParameters p, string? path) => Record("create_cylinder", path);
        public CreateBlockExecutionResult ExecuteCreateCone(
            CreateConeParameters p, string? path) => Record("create_cone", path);
        public CreateBlockExecutionResult ExecuteCreateSphere(
            CreateSphereParameters p, string? path) => Record("create_sphere", path);

        public CreateBlockExecutionResult ExecuteCreateSketch(
            CreateSketchParameters p, string? path)
        {
            Record("create_sketch", path);
            // Return the controlled reference the chain feeds forward.
            return CreateBlockExecutionResult.Success(30, 30, 0,
                "已创建草图，草图引用 SKETCH_000。", "part.prt",
                """{"sketchRef":"SKETCH_000","sketchName":"SKETCH_000"}""");
        }

        public CreateBlockExecutionResult ExecuteCreateExtrude(
            CreateExtrudeParameters p, string? path)
        {
            Record("create_extrude", path);
            LastExtrudeRef = p.SketchRef;
            return CreateBlockExecutionResult.Success(50, 50, 50,
                $"已拉伸草图（{p.Distance} 毫米），实体引用 BODY_1。", "part.prt",
                """{"newBodyRef":"BODY_1"}""");
        }

        public CreateBlockExecutionResult ExecuteCreateRevolve(
            CreateRevolveParameters p, string? path) => Record("create_revolve", path);
        public CreateBlockExecutionResult ExecuteBooleanOperation(
            BooleanOperationParameters p, string? path) => Record("boolean_operation", path);

        public string? LastExtrudeRef { get; private set; }

        private CreateBlockExecutionResult Record(string name, string? path)
        {
            ExecutedTools.Add(name);
            LastExpectedPaths.Add(path);
            return CreateBlockExecutionResult.Success(1, 1, 1, "ok", "part.prt");
        }

        public ModelFeatureProbeResult? ProbeCommittedFeature(string toolName, string operationSummary) => null;
    }

    private sealed class ConfirmationGate
    {
        public List<ToolConfirmationRequest> Requests { get; } = new();

        public ToolConfirmationDelegate Handler => (request, ct) =>
        {
            Requests.Add(request);
            return Task.FromResult<ToolConfirmationDecision?>(new ToolConfirmationDecision(true, "D:\\part.prt"));
        };
    }

    private static (ChatSessionController, ScriptedSender, ChatLog, CountingExecutor, ConfirmationGate)
        Create(ExecutionModeManager? modeManager = null)
    {
        var log = new ChatLog();
        var sender = new ScriptedSender();
        var executor = new CountingExecutor();
        var gate = new ConfirmationGate();
        var controller = new ChatSessionController(
            new ModelService(), log, () => ("https://api.test/v1", "key", "model"),
            sender.SendAsync, 20, executor, gate.Handler, modeManager: modeManager);
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

    [Fact]
    public async Task SerialChain_SketchThenExtrude_SecondCallReceivesRealSketchRef()
    {
        var (controller, sender, log, executor, gate) = Create();

        // Round 1: create_sketch. Round 2: create_extrude (scripted to use
        // the controlled ref — in production the model reads it from the
        // fed-back tool result). Round 3: final text.
        sender.Enqueue(
            ScriptedSender.ToolCall("call_1", "create_sketch",
                """{"plane_mode":"wcs_xy","profile_type":"circle","diameter":30,"unit":"mm"}"""),
            ScriptedSender.ToolCall("call_2", "create_extrude",
                """{"sketch_ref":"SKETCH_000","distance":50,"unit":"mm"}"""),
            () => ChatCompletionResult.OkText("圆柱已创建。"));

        controller.StartSend("用拉伸命令绘制直径30高50的圆柱");
        await WaitForCompletedAsync(controller);

        Assert.Equal(["create_sketch", "create_extrude"], executor.ExecutedTools);
        Assert.Equal("SKETCH_000", executor.LastExtrudeRef);
        Assert.Equal(3, sender.RequestCount);

        // Tools advertised in rounds 1-2, not in the final round.
        Assert.NotNull(sender.ToolDeclarations[0]);
        Assert.NotNull(sender.ToolDeclarations[1]);
        Assert.Null(sender.ToolDeclarations[2]);

        // Real results in the chat log; no raw protocol anywhere.
        Assert.DoesNotContain(log.Messages, m => m.Text.Contains("invoke"));
        Assert.Equal("圆柱已创建。", log.Messages[^2].Text);
        GC.KeepAlive(gate);
    }

    [Fact]
    public async Task SerialChain_ConfirmMode_EachStepAsksForConfirmation()
    {
        var (controller, sender, _, executor, gate) = Create(); // default Confirm mode

        sender.Enqueue(
            ScriptedSender.ToolCall("call_1", "create_sketch",
                """{"plane_mode":"wcs_xy","profile_type":"circle","diameter":30,"unit":"mm"}"""),
            ScriptedSender.ToolCall("call_2", "create_extrude",
                """{"sketch_ref":"SKETCH_000","distance":50,"unit":"mm"}"""),
            () => ChatCompletionResult.OkText("完成。"));

        controller.StartSend("画圆草图并拉伸");
        await WaitForCompletedAsync(controller);

        // Confirm mode: EVERY modifying step asked for its own confirmation.
        Assert.Equal(2, gate.Requests.Count);
        Assert.Equal("create_sketch", gate.Requests[0].ToolName);
        Assert.Equal("create_extrude", gate.Requests[1].ToolName);
        Assert.Equal(2, executor.CallCount);
    }

    [Fact]
    public async Task SerialChain_AutonomousMode_BothStepsExecuteWithoutCards()
    {
        var modeManager = new ExecutionModeManager();
        modeManager.SetMode(ExecutionMode.Autonomous);
        var (controller, sender, log, executor, gate) = Create(modeManager);

        sender.Enqueue(
            ScriptedSender.ToolCall("call_1", "create_sketch",
                """{"plane_mode":"wcs_xy","profile_type":"circle","diameter":30,"unit":"mm"}"""),
            ScriptedSender.ToolCall("call_2", "create_extrude",
                """{"sketch_ref":"SKETCH_000","distance":50,"unit":"mm"}"""),
            () => ChatCompletionResult.OkText("完成。"));

        controller.StartSend("用拉伸命令绘制直径30高50的圆柱");
        await WaitForCompletedAsync(controller);

        Assert.Empty(gate.Requests);            // no confirmation cards at all
        Assert.Equal(2, executor.CallCount);    // both steps executed serially
        Assert.Contains(log.Messages, m => m.Text.Contains("自主模式：正在创建草图"));
    }

    [Fact]
    public async Task SerialChain_FirstStepFails_SecondStepStillGatedByCap()
    {
        // If round 1 fails (parse/authorization/etc.), round 2 still runs
        // only if the model sends another call — but a FAILURE result is fed
        // back, and the chain cannot exceed the cap. Here: the sketch fails
        // (invalid params), the model then asks for the extrude anyway; the
        // extrude must still go through its own full checks.
        var (controller, sender, log, executor, gate) = Create();

        sender.Enqueue(
            ScriptedSender.ToolCall("call_1", "create_sketch",
                """{"plane_mode":"front","profile_type":"circle","diameter":30,"unit":"mm"}"""), // invalid plane
            ScriptedSender.ToolCall("call_2", "create_extrude",
                """{"sketch_ref":"SKETCH_000","distance":50,"unit":"mm"}"""),
            () => ChatCompletionResult.OkText("完成。"));

        controller.StartSend("画草图并拉伸");
        await WaitForCompletedAsync(controller);

        // Sketch failed (InvalidParameters, executor never called for it);
        // the extrude still went through its own gate and executed.
        Assert.Equal(["create_extrude"], executor.ExecutedTools);
        Assert.Contains(log.Messages, m => m.Text.Contains("参数校验未通过"));
    }

    [Fact]
    public async Task SerialChain_CapPreventsUnlimitedLooping()
    {
        var (controller, sender, log, executor, gate) = Create();
        sender.SetDefault(() => ScriptedSender.ToolCall("loop", "create_block",
            """{"length":1,"width":1,"height":1,"unit":"mm"}"""));

        controller.StartSend("循环创建");
        await UntilAsync(() => executor.CallCount >= 2);
        await WaitForCompletedAsync(controller);

        // Rounds 1-2 each execute their single call; the final round refuses
        // the looping request with a real message and the turn ends at the
        // response cap — in a single-call-per-message turn the budget cap
        // (MaxToolExecutionsPerTurn) is never the binding limit, the
        // response-cap final-round refusal is.
        Assert.Equal(2, executor.CallCount);
        Assert.Equal(ChatSessionController.MaxModelResponsesPerTurn, sender.RequestCount);
        Assert.Contains(log.Messages, m => m.Role == ChatRole.Error && m.Text.Contains("上限"));
        GC.KeepAlive(gate);
    }
}
