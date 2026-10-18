using System.Threading;
using System.Windows.Forms;
using NXAI.Assistant.Core;
using NXAI.Assistant.UI;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Lifecycle tests for AssistantChatForm.MessageItem — the crash class fixed
/// in Phase 7.1 (ObjectDisposedException from RichTextBox.set_Rtf during
/// viewport-width updates). WinForms handles require STA, so every case runs
/// on a dedicated STA thread; no message pump is needed for creation,
/// Rtf assignment, resize or dispose.
///
/// What these tests can cover offline: normal width updates, markdown
/// reflow, and the disposed-item / disposed-child windows. What they cannot
/// cover: the real ClientSizeChanged wiring and scrollbar interaction inside
/// the live chat panel — those are explicit NX2512 manual acceptance steps
/// (see the phase report).
/// </summary>
public class ChatMessageItemLifecycleTests
{
    private static void RunOnStaThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(30_000);
        Assert.False(thread.IsAlive, "STA 测试线程超时未完成");
        if (failure != null)
            throw failure;
    }

    private static AssistantChatForm.MessageItem CreateItem(ChatRole role, string text) =>
        new(new ChatMessage(role, text, DateTimeOffset.Now), new ToolTip(), new ContextMenuStrip());

    // ------------------------------------------------------------ normal paths

    [Fact]
    public void ApplyViewportWidth_AssistantMessage_SizesRichTextBoxWithinClamp()
    {
        RunOnStaThread(() =>
        {
            using var item = CreateItem(ChatRole.Assistant, "普通**加粗**段落。");
            item.ApplyViewportWidth(800);

            // Assistant budget: 800 - 2*SideMargin(24) = 752 → clamped to 660.
            var box = Assert.IsType<RichTextBox>(item.Controls[0]);
            Assert.Equal(660, box.Width);
        });
    }

    [Fact]
    public void ApplyViewportWidth_LongMarkdown_ReflowsOnWidthChange()
    {
        RunOnStaThread(() =>
        {
            string text = "# 标题\n\n" + string.Join("\n\n",
                Enumerable.Range(1, 12).Select(i => $"第 {i} 段：较长的中文内容用于触发自动换行与高度重排。"));
            using var item = CreateItem(ChatRole.Assistant, text);

            item.ApplyViewportWidth(900);
            var box = Assert.IsType<RichTextBox>(item.Controls[0]);
            int tallHeight = box.Height;
            Assert.True(tallHeight > 60, $"多段消息重排后高度应显著大于单行（{tallHeight}）");

            item.ApplyViewportWidth(400);
            Assert.True(box.Width < 660, "窄视口下宽度应收窄");
            Assert.True(box.Height >= tallHeight, "换行宽度变窄后高度不应减少");
        });
    }

    [Fact]
    public void ApplyViewportWidth_UserBubble_AndInfoLabelPaths_Work()
    {
        RunOnStaThread(() =>
        {
            using var user = CreateItem(ChatRole.User, "用户消息");
            user.ApplyViewportWidth(700);
            Assert.NotEmpty(user.Controls);

            using var info = CreateItem(ChatRole.Info, "NX 上下文：已选择 1 个对象。");
            info.ApplyViewportWidth(700);
            var label = Assert.IsType<Label>(info.Controls[0]);
            Assert.Equal(580, label.MaximumSize.Width); // 700 - 120
        });
    }

    // ------------------------------------------------------------ disposed windows

    [Fact]
    public void ApplyViewportWidth_AfterItemDispose_DoesNotThrow()
    {
        RunOnStaThread(() =>
        {
            var item = CreateItem(ChatRole.Assistant, "将被释放的消息。");
            item.ApplyViewportWidth(600);
            item.Dispose();

            // Regression for the crash: a viewport update reaching a disposed
            // item must be a no-op, never RichTextBox.set_Rtf on a disposed
            // object.
            item.ApplyViewportWidth(300);
        });
    }

    [Fact]
    public void ApplyViewportWidth_AfterChildViewDispose_DoesNotThrow()
    {
        RunOnStaThread(() =>
        {
            using var item = CreateItem(ChatRole.Assistant, "窗体拆除窗口：子视图先释放。");
            item.ApplyViewportWidth(600);

            // Window teardown disposes the child RichTextBox before the item
            // leaves the control tree; a layout callback in that window must
            // not touch the disposed view.
            item.Controls[0].Dispose();
            item.ApplyViewportWidth(300);
        });
    }

    [Fact]
    public void ApplyViewportWidth_AfterChildLabelDispose_DoesNotThrow()
    {
        RunOnStaThread(() =>
        {
            using var item = CreateItem(ChatRole.Info, "Info 消息路径。");
            item.ApplyViewportWidth(600);
            item.Controls[0].Dispose();
            item.ApplyViewportWidth(300);
        });
    }

    [Fact]
    public void Dispose_Twice_IsSafe()
    {
        RunOnStaThread(() =>
        {
            var item = CreateItem(ChatRole.Assistant, "重复释放防护。");
            item.Dispose();
            item.Dispose();
        });
    }
}
