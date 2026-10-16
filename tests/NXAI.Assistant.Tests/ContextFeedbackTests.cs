using NXAI.Assistant.Core;
using NXAI.Assistant.UI;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Offline tests for the user-visible NX context feedback: what the send path
/// captured must be stated in the chat (count and types), failures must be
/// reported as failures, and the captured-context note must never be sent to
/// the model as chat content.
/// </summary>
public class ContextFeedbackTests
{
    private sealed class ScriptedSender
    {
        public int RequestCount;
        public readonly List<IReadOnlyList<OutboundMessage>> Requests = new();
        public Task<ChatCompletionResult> SendAsync(
            IReadOnlyList<OutboundMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            CancellationToken ct)
        {
            RequestCount++;
            Requests.Add(messages);
            return Task.FromResult(ChatCompletionResult.OkText("默认回复"));
        }
    }

    private static Task WaitForCompletedAsync(ChatSessionController controller)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.RequestCompleted += (_, _) => tcs.TrySetResult();
        return tcs.Task;
    }

    private static (ChatSessionController Controller, ScriptedSender Sender, ChatLog Log) Create(
        Func<NxContextSnapshot?>? contextProvider)
    {
        var log = new ChatLog();
        var scripted = new ScriptedSender();
        var controller = new ChatSessionController(new ModelService(), log,
            () => null, scripted.SendAsync, contextProvider: contextProvider);
        return (controller, scripted, log);
    }

    private static NxSelectionItem Face(double cx = 10, double cy = 20, double cz = 30) => new()
    {
        Kind = "face",
        Subtype = "planar",
        Name = "FACE(0)",
        Face = new NxFaceInfo("planar", cx, cy, cz, 0, 0, 1, null),
    };

    private static NxContextSnapshot Snapshot(params NxSelectionItem[] selection) => new()
    {
        CapturedAtUtc = "2026-10-16T12:00:00.0000000Z",
        Status = "available",
        WorkPartName = "test_part",
        WorkPartUnits = "Millimeters",
        WorkPartUnsaved = false,
        Selection = selection,
    };

    // ------------------------------------------------ DescribeCapturedContext

    [Fact]
    public void Summary_EmptySelection_StatesZero()
    {
        var text = ChatSessionController.DescribeCapturedContext(Snapshot());
        Assert.Contains("没有选中对象", text);
    }

    [Fact]
    public void Summary_SinglePlanarFace_ShowsCountTypeCenterAndNormal()
    {
        var text = ChatSessionController.DescribeCapturedContext(Snapshot(Face()));
        Assert.Contains("已选择 1 个对象", text);
        Assert.Contains("1 个面（planar）", text);
        Assert.Contains("(10, 20, 30)", text);
        Assert.Contains("(0, 0, 1)", text);
    }

    [Fact]
    public void Summary_MixedSelection_GroupsByType()
    {
        var text = ChatSessionController.DescribeCapturedContext(Snapshot(
            Face(), Face(11, 21, 31),
            new NxSelectionItem { Kind = "body", Subtype = "solid_body", Name = "BODY(0)" },
            new NxSelectionItem { Kind = "other", Name = "X" }));
        Assert.Contains("已选择 4 个对象", text);
        Assert.Contains("2 个面（planar）", text);
        Assert.Contains("1 个实体", text);
        Assert.Contains("1 个other", text);
    }

    [Fact]
    public void Summary_NoActivePart_StatesIt()
    {
        var text = ChatSessionController.DescribeCapturedContext(
            new NxContextSnapshot { Status = "no_active_part" });
        Assert.Contains("没有活动工作部件", text);
    }

    [Fact]
    public void Summary_ErrorState_ReportsReason()
    {
        var text = ChatSessionController.DescribeCapturedContext(new NxContextSnapshot
        {
            Status = "error",
            Unavailable = new[] { "无法访问 NX 会话：boom" },
        });
        Assert.Contains("读取失败", text);
        Assert.Contains("无法访问 NX 会话：boom", text);
    }

    [Fact]
    public void Summary_AppendsUnavailableReasons()
    {
        var snapshot = Snapshot(Face()) with
        {
            Unavailable = new[] { "绝对坐标系未获取（未实现）。" },
        };
        var text = ChatSessionController.DescribeCapturedContext(snapshot);
        Assert.Contains("未获取", text);
        Assert.Contains("绝对坐标系未获取", text);
    }

    // ------------------------------------------------------- send-time display

    [Fact]
    public async Task StartSend_AddsInfoEntryWithCapturedSelectionSummary()
    {
        var (controller, sender, log) = Create(() => Snapshot(Face()));
        var completed = WaitForCompletedAsync(controller);

        Assert.True(controller.StartSend("在我选定的面上创建一个圆柱"));
        await completed;

        Assert.Equal(1, sender.RequestCount);
        var info = log.Messages.Single(m => m.Role == ChatRole.Info && m.Text.Contains("NX 上下文"));
        Assert.Contains("已选择 1 个对象", info.Text);
    }

    [Fact]
    public async Task StartSend_CapturedContextNote_IsNotSentToModel()
    {
        var (controller, sender, log) = Create(() => Snapshot(Face()));
        var completed = WaitForCompletedAsync(controller);

        Assert.True(controller.StartSend("你好"));
        await completed;

        Assert.Equal(1, sender.RequestCount);
        // The user-visible capture note must not appear among outbound messages.
        Assert.DoesNotContain(sender.Requests[0], m => m.Content?.Contains("NX 上下文") == true);
    }

    [Fact]
    public async Task StartSend_WithoutContextProvider_StillSendsAndAddsNoSummary()
    {
        var (controller, sender, log) = Create(null);
        var completed = WaitForCompletedAsync(controller);

        Assert.True(controller.StartSend("你好"));
        await completed;

        Assert.Equal(1, sender.RequestCount);
        Assert.DoesNotContain(log.Messages, m => m.Role == ChatRole.Info && m.Text.Contains("NX 上下文"));
    }

    [Fact]
    public void CaptureContextNow_RefreshesCurrentTurnContext()
    {
        var (controller, _, _) = Create(() => Snapshot(Face()));
        Assert.Null(controller.CurrentTurnContext);

        var snapshot = controller.CaptureContextNow();

        Assert.NotNull(snapshot);
        Assert.Same(snapshot, controller.CurrentTurnContext);
    }

    [Fact]
    public async Task StartSend_WhenProviderReturnsNull_NoSummaryAndRequestProceeds()
    {
        var (controller, sender, log) = Create(() => null);
        var completed = WaitForCompletedAsync(controller);

        Assert.True(controller.StartSend("你好"));
        await completed;

        Assert.Equal(1, sender.RequestCount);
        Assert.DoesNotContain(log.Messages, m => m.Role == ChatRole.Info && m.Text.Contains("NX 上下文"));
    }
}
