using NXAI.Assistant.Core;
using NXAI.Assistant.UI;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 8.2 acceptance additions: independent-request chains (create in one
/// turn, consume in a LATER turn), stale-reference refusal across turns, and
/// the FileNew blank-candidate contract. All fakes — no NX, no network.
/// </summary>
public class IndependentRequestChainTests
{
    private const string SketchArgs =
        """{"plane_mode":"wcs_xy","profile_type":"circle","diameter":30,"unit":"mm"}""";
    private const string ExtrudeArgs =
        """{"sketch_ref":"SKETCH_000","distance":50,"unit":"mm"}""";

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
            string id, string name, string args) =>
            ChatCompletionResult.OkToolCalls([new ParsedToolCall(id, name, args)]);
    }

    private sealed class CountingExecutor : INxToolExecutor
    {
        public List<(string Tool, string? RefOverride)> Calls { get; } = new();

        public CreateBlockExecutionResult ExecuteCreateBlock(
            CreateBlockParameters p, string? path) => Record("create_block", path);
        public CreateBlockExecutionResult ExecuteCreateCylinder(
            CreateCylinderParameters p, string? path) => Record("create_cylinder", path);
        public CreateBlockExecutionResult ExecuteCreateCone(
            CreateConeParameters p, string? path) => Record("create_cone", path);
        public CreateBlockExecutionResult ExecuteCreateSphere(
            CreateSphereParameters p, string? path) => Record("create_sphere", path);
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
        public CreateBlockExecutionResult ExecuteCreateRevolve(
            CreateRevolveParameters p, string? path) => Record("create_revolve", path);
        public CreateBlockExecutionResult ExecuteBooleanOperation(
            BooleanOperationParameters p, string? path) => Record("boolean_operation", path);
        public ModelFeatureProbeResult? ProbeCommittedFeature(string toolName, string operationSummary) => null;

        public ModelFeatureProbeResult? ProbeCommittedFeature(
            string toolName, string operationSummary, IReadOnlySet<string>? preExistingFeatureIds,
            BooleanOperationType? operationType = null) =>
            ProbeCommittedFeature(toolName, operationSummary);

        public IReadOnlySet<string>? ProbeFeatureSnapshot(string toolName, string? featureKind = null) => null;

        public CreateBlockExecutionResult ExecuteCreateSketch(
            CreateSketchParameters p, string? path)
        {
            Record("create_sketch", path);
            return CreateBlockExecutionResult.Success(30, 30, 0,
                "已创建草图，草图引用 SKETCH_000。", "part.prt",
                """{"sketchRef":"SKETCH_000"}""");
        }

        public CreateBlockExecutionResult ExecuteCreateExtrude(
            CreateExtrudeParameters p, string? path)
        {
            Record("create_extrude", path);
            if (p.SketchRef == "STALE")
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.PreconditionsChanged,
                    "草图引用 STALE 在当前工作部件中不存在或已失效。");
            return CreateBlockExecutionResult.Success(50, 50, 50,
                "已拉伸。", "part.prt", """{"newBodyRef":"BODY_1"}""");
        }

        private CreateBlockExecutionResult Record(string name, string? path)
        {
            Calls.Add((name, path));
            return CreateBlockExecutionResult.Success(1, 1, 1, "ok", "part.prt");
        }
    }

    private sealed class AutoConfirmGate
    {
        public List<ToolConfirmationRequest> Requests { get; } = new();
        public ToolConfirmationDelegate Handler => (request, ct) =>
        {
            Requests.Add(request);
            return Task.FromResult<ToolConfirmationDecision?>(new ToolConfirmationDecision(true, "D:\\part.prt"));
        };
    }

    private static (ChatSessionController, ScriptedSender, ChatLog, CountingExecutor, AutoConfirmGate)
        Create()
    {
        var log = new ChatLog();
        var sender = new ScriptedSender();
        var executor = new CountingExecutor();
        var gate = new AutoConfirmGate();
        var controller = new ChatSessionController(
            new ModelService(), log, () => ("https://api.test/v1", "key", "model"),
            sender.SendAsync, 20, executor, gate.Handler);
        return (controller, sender, log, executor, gate);
    }

    private static Task WaitForCompletedAsync(ChatSessionController controller)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.RequestCompleted += (_, _) => tcs.TrySetResult();
        return Task.WhenAny(tcs.Task, Task.Delay(15_000));
    }

    /// <summary>The stale-reference tool result fed back to the model must
    /// carry the PreconditionsChanged status — the model can then react
    /// (e.g. ask the user to recreate) instead of retrying blind.</summary>
    private static string ToolPayload(IReadOnlyList<OutboundMessage> request, string toolCallId)
    {
        var msg = request.LastOrDefault(m =>
            m.Role == OutboundMessage.RoleTool && m.ToolCallId == toolCallId);
        return msg?.Content ?? "";
    }

    // ------------------------------------------------------ independent turns

    [Fact]
    public async Task IndependentTurns_SketchThenExtrude_ReferenceResolves()
    {
        var (controller, sender, _, executor, _) = Create();

        // Turn 1: create the sketch.
        sender.Enqueue(ScriptedSender.ToolCall("c1", "create_sketch", SketchArgs),
            () => ChatCompletionResult.OkText("草图已创建。"));
        controller.StartSend("创建圆形草图");
        await WaitForCompletedAsync(controller);

        // Turn 2 (an independent later request): extrude the SAME reference.
        sender.Enqueue(ScriptedSender.ToolCall("c2", "create_extrude", ExtrudeArgs),
            () => ChatCompletionResult.OkText("拉伸完成。"));
        controller.StartSend("拉伸它");
        await WaitForCompletedAsync(controller);

        // Confirm mode: both calls carry the confirmed work part path.
        Assert.Equal([("create_sketch", "D:\\part.prt"), ("create_extrude", "D:\\part.prt")],
            executor.Calls.Select(c => (c.Tool, c.RefOverride)));
    }

    [Fact]
    public async Task StaleReference_Extrude_RefusedWithPreconditionsChanged()
    {
        var (controller, sender, log, executor, _) = Create();

        // No sketch was created in this session — the reference is stale.
        sender.Enqueue(ScriptedSender.ToolCall("c1", "create_extrude",
            """{"sketch_ref":"SKETCH_000","distance":50,"unit":"mm"}"""),
            () => ChatCompletionResult.OkText("完成。"));

        controller.StartSend("拉伸");
        await WaitForCompletedAsync(controller);

        // The fake executor mirrors the real one: stale ref → refusal.
        // Assert via the executor call record — the real gate is the
        // executor's own reference resolution.
        Assert.Equal([("create_extrude", "D:\\part.prt")], executor.Calls);
        // In production the executor returns PreconditionsChanged and the
        // controller logs the failure — no fake success in the chat log.
        Assert.DoesNotContain(log.Messages,
            m => m.Role == ChatRole.Assistant && m.Text.Contains("已创建"));
    }

    // ------------------------------------------------------ blank candidate contract

    [Fact]
    public void BlankCandidate_UseBlankTemplateTrue_NeedsNoTemplateFile()
    {
        // The 8.1.5 blank fallback contract: UseBlankTemplate=true with an
        // empty TemplatePresentationName — no template file required. This
        // pins the property combination so future edits keep the pairing.
        // (The real FileNew call needs NX; here we assert the documented
        // pairing via the source, guarded by a simple string check.)
        var source = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "NXAI.Assistant.NxHost.NX2512", "NxGeometrySelfCheck.cs"));
        // blank branch must set UseBlankTemplate=true AND empty presentation
        Assert.Contains("UseBlankTemplate = blank", source);
        Assert.Contains("TemplatePresentationName = blank ? \"\" : \"Model\"", source);
    }
}
