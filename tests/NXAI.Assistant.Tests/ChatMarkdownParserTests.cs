using NXAI.Assistant.UI;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Offline tests for the display-only chat markdown parser (bold runs,
/// unordered lists, paragraph splitting). The parser is internal, so the
/// suite accesses the internal UI parser via InternalsVisibleTo. No
/// WinForms controls are created.
/// </summary>
public class ChatMarkdownParserTests
{
    [Fact]
    public void BoldRun_IsExtracted_FromChineseHeading()
    {
        var runs = ChatMarkdownParser.ParseInlineRuns("**原因说明：**");
        var run = Assert.Single(runs);
        Assert.True(run.Bold);
        Assert.Equal("原因说明：", run.Text);
    }

    [Fact]
    public void MixedLine_SplitsBoldAndPlain()
    {
        var runs = ChatMarkdownParser.ParseInlineRuns("底面直径 **40** 毫米");
        Assert.Equal(3, runs.Count);
        Assert.False(runs[0].Bold);
        Assert.Equal("底面直径 ", runs[0].Text);
        Assert.True(runs[1].Bold);
        Assert.Equal("40", runs[1].Text);
        Assert.Equal(" 毫米", runs[2].Text);
        Assert.False(runs[2].Bold);
    }

    [Fact]
    public void UnpairedAsterisks_StayLiteral()
    {
        var runs = ChatMarkdownParser.ParseInlineRuns("乘号 2**3 = 8 与单独 ** 标记");
        Assert.All(runs, r => Assert.False(r.Bold));
        Assert.Equal("乘号 2**3 = 8 与单独 ** 标记", string.Concat(runs.Select(r => r.Text)));
    }

    [Fact]
    public void MultipleBoldSegments_Paired()
    {
        var runs = ChatMarkdownParser.ParseInlineRuns("**原因说明：** 参数错误 **详情**");
        Assert.Equal(3, runs.Count);
        Assert.True(runs[0].Bold);
        Assert.Equal("原因说明：", runs[0].Text);
        Assert.False(runs[1].Bold);
        Assert.Equal(" 参数错误 ", runs[1].Text);
        Assert.True(runs[2].Bold);
        Assert.Equal("详情", runs[2].Text);
    }

    [Fact]
    public void BoldHeading_And_List_AreSeparateBlocks()
    {
        // 截图场景：加粗标题 + 减号列表项
        var blocks = ChatMarkdownParser.Parse("**原因说明：**\n- **底面直径** 40 mm\n- **高度** 25 mm\n");
        Assert.Equal(2, blocks.Count);

        Assert.False(blocks[0].IsList);
        var heading = Assert.Single(blocks[0].Runs);
        Assert.True(heading.Bold);
        Assert.Equal("原因说明：", heading.Text);

        Assert.True(blocks[1].IsList);
        Assert.Equal(2, blocks[1].ListItems.Count);
        var item0 = blocks[1].ListItems[0];
        Assert.Equal(2, item0.Count);
        Assert.True(item0[0].Bold);
        Assert.Equal("底面直径", item0[0].Text);
        Assert.False(item0[1].Bold);
        Assert.Equal(" 40 mm", item0[1].Text);
        Assert.Equal("高度", blocks[1].ListItems[1][0].Text);
    }

    [Fact]
    public void AsteriskList_AlsoSupported()
    {
        var blocks = ChatMarkdownParser.Parse("* 项目一\n* 项目二");
        var list = Assert.Single(blocks);
        Assert.True(list.IsList);
        Assert.Equal(2, list.ListItems.Count);
        Assert.Equal("项目一", list.ListItems[0][0].Text);
        Assert.Equal("项目二", list.ListItems[1][0].Text);
    }

    [Fact]
    public void BlankLines_SplitParagraphs()
    {
        var blocks = ChatMarkdownParser.Parse("第一段。\n\n第二段。");
        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, b => Assert.False(b.IsList));
        Assert.Equal("第一段。", string.Concat(blocks[0].Runs.Select(r => r.Text)));
        Assert.Equal("第二段。", string.Concat(blocks[1].Runs.Select(r => r.Text)));
    }

    [Fact]
    public void MultiParagraph_ChineseReply_RoundTripsAllText()
    {
        const string raw =
            "**原因说明：**\n- **底面直径**：40 mm\n- **高度**：25 mm\n\n如需调整尺寸，请告诉我。";
        var blocks = ChatMarkdownParser.Parse(raw);
        Assert.True(blocks.Count >= 2);

        // Display text (markers stripped) must preserve every visible character.
        string visible = string.Concat(blocks.SelectMany(b =>
            b.IsList
                ? b.ListItems.SelectMany(i => i.Select(r => r.Text))
                : b.Runs.Select(r => r.Text)));
        Assert.DoesNotContain("**", visible);
        Assert.Contains("原因说明：", visible);
        Assert.Contains("底面直径", visible);
        Assert.Contains("40 mm", visible);
        Assert.Contains("高度", visible);
        Assert.Contains("25 mm", visible);
        Assert.Contains("如需调整尺寸，请告诉我。", visible);
    }

    [Fact]
    public void PlainTextWithoutMarkers_Unchanged()
    {
        const string raw = "普通中文回复，无任何标记。";
        var blocks = ChatMarkdownParser.Parse(raw);
        var block = Assert.Single(blocks);
        var run = Assert.Single(block.Runs);
        Assert.False(run.Bold);
        Assert.Equal(raw, run.Text);
    }

    [Fact]
    public void CRLF_Normalized()
    {
        var blocks = ChatMarkdownParser.Parse("**标题：**\r\n- 项目");
        Assert.Equal(2, blocks.Count);
        Assert.True(blocks[1].IsList);
    }
}
