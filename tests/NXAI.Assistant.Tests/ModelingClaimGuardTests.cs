using System.Text.Json;
using NXAI.Assistant.Core;
using NXAI.Assistant.UI;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Controller-side modeling-claim guard (2026-10-21 root cause: the second
/// subtract request produced NO tool call; the model's text reused the prior
/// SUBTRACT(3) and the UI showed it as success). Policy-level tests pin the
/// decision table; controller-level tests pin what actually lands in the
/// chat log.
/// </summary>
public class ModelingClaimGuardTests
{
    private static (string ToolName, string StatusWireName) Exec(string tool, string wire) =>
        (tool, wire);

    // ---------- policy table ----------

    [Theory]
    [InlineData("差集已完成。", "boolean_operation")]
    [InlineData("已为您完成布尔差集操作。", "boolean_operation")]
    [InlineData("长方体创建成功。", "create_block")]
    [InlineData("已在草图上完成拉伸。", "create_extrude")]
    public void ModelingSuccessText_WithZeroExecutions_IsBlocked(string text, string _)
    {
        var v = ModelingClaimGuard.Evaluate(text, Array.Empty<(string, string)>());
        Assert.True(v.IsModelingSuccessClaim);
        Assert.True(v.ShouldBlock);
        Assert.NotNull(v.ReplacementMessage);
        Assert.Contains("未执行建模工具", v.ReplacementMessage);
        Assert.Contains("不能认定操作成功", v.ReplacementMessage);
    }

    [Fact]
    public void ModelingSuccessText_WithAllFailedExecutions_IsReplacedNotShown()
    {
        var execs = new[] { Exec("boolean_operation", "execution_failed") };
        var v = ModelingClaimGuard.Evaluate("差集已完成。", execs);
        Assert.True(v.IsModelingSuccessClaim);
        Assert.True(v.ShouldBlock);
        Assert.NotNull(v.ReplacementMessage);
        // The unverified success wording must NOT survive anywhere in the
        // replacement (showing it above a correction still reads as success).
        Assert.DoesNotContain("差集已完成", v.ReplacementMessage);
        Assert.Contains("均失败或被拒绝", v.ReplacementMessage);
        Assert.Contains("未确认几何修改成功", v.ReplacementMessage);
    }

    [Fact]
    public void ModelingSuccessText_WithRealSuccess_IsAllowed()
    {
        var execs = new[] { Exec("boolean_operation", "success") };
        var v = ModelingClaimGuard.Evaluate("差集已完成。", execs);
        Assert.True(v.IsModelingSuccessClaim);
        Assert.False(v.ShouldBlock);
        Assert.Null(v.ReplacementMessage);
    }

    [Theory]
    [InlineData("差集是布尔运算的一种，先把两个实体求交得到公共部分。")]
    [InlineData("请问您想保留方体还是圆柱？确认后我再执行。")]
    [InlineData("好的。")]
    [InlineData("今天的天气不错。")]
    [InlineData("这里是一个例子：差集操作会先计算……（概念解释）")]
    public void OrdinaryText_WithoutSuccessClaim_IsNeverBlocked(string text)
    {
        var v = ModelingClaimGuard.Evaluate(text, Array.Empty<(string, string)>());
        Assert.False(v.IsModelingSuccessClaim);
        Assert.False(v.ShouldBlock);
    }

    [Fact]
    public void SuccessVerbWithoutModelingNoun_IsNotAClaim()
    {
        // "已成功" alone over a non-modeling topic must not trip the guard.
        var v = ModelingClaimGuard.Evaluate("已成功连接到服务器。", Array.Empty<(string, string)>());
        Assert.False(v.IsModelingSuccessClaim);
    }

    // ---------- controller integration ----------

    private static Task WaitForCompletedAsync(ChatSessionController controller)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.RequestCompleted += (_, _) => tcs.TrySetResult();
        return Task.WhenAny(tcs.Task, Task.Delay(15_000));
    }

    private static (ChatSessionController Controller, ScriptedSender Sender, ChatLog Log, ConfirmationGate Gate, ScriptableExecutor Executor)
        Create()
    {
        var log = new ChatLog();
        var sender = new ScriptedSender();
        var gate = new ConfirmationGate();
        var executor = new ScriptableExecutor();
        var controller = new ChatSessionController(
            new ModelService(), log, () => ("https://api.test/v1", "key", "model"),
            sender.SendAsync, 20, executor, gate.Handler);
        return (controller, sender, log, gate, executor);
    }

    [Fact]
    public async Task Controller_NoToolCall_SuccessText_IsReplaced()
    {
        var (controller, sender, log, _, _) = Create();
        sender.Enqueue(ChatCompletionResult.OkText("差集已完成。"));

        controller.StartSend("对方体和圆柱做差集，保留方体");
        await WaitForCompletedAsync(controller);

        var texts = log.Messages.Select(m => m.Text).ToList();
        Assert.DoesNotContain("差集已完成。", texts);
        Assert.Contains(texts, t => t.Contains("本轮未执行建模工具") && t.Contains("不能认定操作成功"));
    }

    [Fact]
    public async Task Controller_FailedExecution_ModelClaimsSuccess_ReplacedWithFailureVerdict()
    {
        var (controller, sender, log, _, executor) = Create();
        executor.BooleanStatus = ToolExecutionStatus.ExecutionFailed;
        sender.Enqueue(
            ChatCompletionResult.OkToolCalls([new ParsedToolCall(
                "call-1", "boolean_operation",
                """{"operation":"subtract","target_body_ref":"CYLINDER(2)","tool_body_ref":"BLOCK(1)"}""")]),
            ChatCompletionResult.OkText("差集已完成。"));

        controller.StartSend("对方体和圆柱做差集");
        await WaitForCompletedAsync(controller);

        var texts = log.Messages.Select(m => m.Text).ToList();
        // The fake success text must NOT appear in any final assistant reply;
        // the failure verdict + the structured failure summary must.
        Assert.DoesNotContain("差集已完成。", texts);
        Assert.Contains(texts, t => t.Contains("本轮建模操作均失败或被拒绝") && t.Contains("未确认几何修改成功"));
        Assert.Contains(texts, t => t.StartsWith("[执行记录]") && t.Contains("未执行/失败"));
    }

    [Fact]
    public async Task Controller_SuccessfulExecution_ResultIsShown()
    {
        var (controller, sender, log, _, _) = Create();
        sender.Enqueue(
            ChatCompletionResult.OkToolCalls([new ParsedToolCall(
                "call-1", "boolean_operation",
                """{"operation":"subtract","target_body_ref":"CYLINDER(2)","tool_body_ref":"BLOCK(1)"}""")]),
            ChatCompletionResult.OkText("差集已完成。"));

        controller.StartSend("对方体和圆柱做差集");
        await WaitForCompletedAsync(controller);

        var texts = log.Messages.Select(m => m.Text).ToList();
        // Model text stays, the structured receipt follows, no blocking hint.
        Assert.Contains("差集已完成。", texts);
        Assert.Contains(texts, t => t.StartsWith("[执行记录]") && t.Contains("已执行 boolean_operation"));
        Assert.DoesNotContain(texts, t => t.Contains("不能认定操作成功"));
    }

    [Fact]
    public async Task Controller_OrdinaryClarification_PassesThrough()
    {
        var (controller, sender, log, _, _) = Create();
        sender.Enqueue(ChatCompletionResult.OkText("请问您想保留方体还是圆柱？确认后我再执行。"));

        controller.StartSend("对方体和圆柱做差集，保留方体");
        await WaitForCompletedAsync(controller);

        Assert.Contains(log.Messages.Select(m => m.Text).ToList(),
            t => t.Contains("请问您想保留方体还是圆柱"));
        Assert.DoesNotContain(log.Messages.Select(m => m.Text).ToList(),
            t => t.Contains("不能认定操作成功"));
    }

    // ---- minimal doubles (scoped to this file) ----

    private sealed class ScriptedSender
    {
        private readonly Queue<object> _responses = new();
        public Task<ChatCompletionResult> SendAsync(
            IReadOnlyList<OutboundMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            CancellationToken ct)
        {
            var item = _responses.Dequeue();
            return Task.FromResult(item switch
            {
                ChatCompletionResult r => r,
                _ => throw new InvalidOperationException("Bad scripted response type"),
            });
        }
        public void Enqueue(params object[] responses)
        {
            foreach (var r in responses) _responses.Enqueue(r);
        }
    }

    private sealed class ConfirmationGate
    {
        public Task<ToolConfirmationDecision?> Handler(
            ToolConfirmationRequest request, CancellationToken ct)
            => Task.FromResult<ToolConfirmationDecision?>(new ToolConfirmationDecision(true, null));
    }

    private sealed class ScriptableExecutor : INxToolExecutor
    {
        public ToolExecutionStatus BooleanStatus { get; set; } = ToolExecutionStatus.Success;

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
            CreateSketchParameters parameters, string? expectedWorkPartFullPath) => Ok();
        public CreateBlockExecutionResult ExecuteCreateExtrude(
            CreateExtrudeParameters parameters, string? expectedWorkPartFullPath) => Ok();
        public CreateBlockExecutionResult ExecuteCreateRevolve(
            CreateRevolveParameters parameters, string? expectedWorkPartFullPath) => Ok();
        public CreateBlockExecutionResult ExecuteBooleanOperation(
            BooleanOperationParameters parameters, string? expectedWorkPartFullPath)
        {
            if (BooleanStatus != ToolExecutionStatus.Success)
            {
                return CreateBlockExecutionResult.Failure(
                    BooleanStatus, "布尔运算执行失败。", rolledBack: true);
            }
            string details = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["operation"] = parameters.Operation.ToString().ToLowerInvariant(),
                ["resultBodyRef"] = "CYLINDER(2)",
                ["toolConsumed"] = true,
            });
            return CreateBlockExecutionResult.Success(0, 0, 0,
                $"已完成布尔差集（结果体引用 CYLINDER(2)）。", "part", details);
        }
        public ModelFeatureProbeResult? ProbeCommittedFeature(string toolName, string operationSummary) => null;
        public ModelFeatureProbeResult? ProbeCommittedFeature(
            string toolName, string operationSummary, IReadOnlySet<string>? preExistingFeatureIds,
            BooleanOperationType? operationType = null) => null;
        public IReadOnlySet<string>? ProbeFeatureSnapshot(string toolName, string? featureKind = null) => null;
    }
}
