using NXAI.Assistant.UI;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Offline tests for the display-only markdown→RTF conversion used by the
/// chat message RichTextBox. Verifies text integrity (no loss, no
/// duplication), escaping of RTF metacharacters, Chinese unicode escaping,
/// and the structure of bold/list/paragraph output. Rendering itself (fonts,
/// wrapping) is a manual NX acceptance item and is not claimed here.
/// </summary>
public class ChatRtfBuilderTests
{
    [Fact]
    public void PlainChineseText_SurvivesByteIdentical_AfterUnicodeDecode()
    {
        const string raw = "普通中文回复，含标点：、。！";
        string rtf = ChatRtfBuilder.Build(raw, 9.75F);
        string visible = DecodeRtfUnicode(rtf);
        Assert.Equal(raw, visible);
    }

    [Fact]
    public void BoldMarkers_Stripped_TextPreserved()
    {
        const string raw = "**原因说明：**\n- **底面直径**：40 mm\n- **高度**：25 mm";
        string rtf = ChatRtfBuilder.Build(raw, 9.75F);
        string visible = DecodeRtfUnicode(rtf);
        Assert.DoesNotContain("**", visible);
        Assert.Contains("原因说明：", visible);
        Assert.Contains("底面直径", visible);
        Assert.Contains("40 mm", visible);
        Assert.Contains("高度", visible);
        Assert.Contains("25 mm", visible);
    }

    [Fact]
    public void BoldRuns_HaveRtfBoldToggle()
    {
        var blocks = ChatMarkdownParser.Parse("**原因说明：**");
        var sb = new System.Text.StringBuilder();
        ChatRtfBuilder.AppendBlocks(sb, blocks);
        string body = sb.ToString();
        Assert.Contains("\\b ", body);
        Assert.Contains("\\b0", body);
    }

    [Fact]
    public void ListItems_UseBulletAndLineBreaks()
    {
        var sb = new System.Text.StringBuilder();
        ChatRtfBuilder.AppendBlocks(sb, ChatMarkdownParser.Parse("- 项目一\n- 项目二"));
        string body = sb.ToString();
        Assert.Contains("\\bullet", body);
        Assert.Contains("\\line", body);
        // Both items present exactly once.
        Assert.Equal(1, CountOccurrences(body, Escape("项目一")));
        Assert.Equal(1, CountOccurrences(body, Escape("项目二")));
    }

    [Fact]
    public void RtfMetacharacters_AreEscaped()
    {
        var sb = new System.Text.StringBuilder();
        ChatRtfBuilder.AppendEscaped(sb, @"a\b{c}d");
        Assert.Equal(@"a\\b\{c\}d", sb.ToString());
    }

    [Fact]
    public void Braces_InModelOutput_CannotInjectRtf()
    {
        // A malicious/confused model string cannot close the RTF group or add
        // control words: braces are escaped and control characters dropped.
        const string raw = "}\n{\\rtf1\\ansi EVIL}";
        string rtf = ChatRtfBuilder.Build(raw, 9.75F);
        // Braces in the content appear escaped; the injected "\rtf1" must be
        // present only as escaped literal \u text, never as a control word
        // outside the document header.
        string visible = DecodeRtfUnicode(rtf);
        Assert.Contains("EVIL", visible);
        Assert.Contains("\\rtf1\\ansi EVIL", visible); // literal, decoded back
        // Content braces survive as escaped literals.
        Assert.Contains(@"\{", rtf);
        Assert.Contains(@"\}", rtf);
    }

    [Fact]
    public void ControlCharacters_AreDropped()
    {
        var sb = new System.Text.StringBuilder();
        ChatRtfBuilder.AppendEscaped(sb, "a\u0001b\u0002c");
        Assert.Equal("abc", sb.ToString());
    }

    [Fact]
    public void SurrogatePairs_Emoji_EncodedAsTwoUnicodeEscapes()
    {
        var sb = new System.Text.StringBuilder();
        ChatRtfBuilder.AppendEscaped(sb, "完成\U0001F600");
        string escaped = sb.ToString();
        Assert.Contains("\\u", escaped);
        // Both surrogate code units escaped; count >= 2 \u occurrences.
        Assert.True(escaped.Split("\\u").Length - 1 >= 2);
    }

    [Fact]
    public void LongTextWithoutSpaces_DoesNotLoseContent()
    {
        string raw = "超长无空格中文内容" + new string('长', 300);
        string rtf = ChatRtfBuilder.Build(raw, 9.75F);
        string visible = DecodeRtfUnicode(rtf);
        Assert.Equal(raw, visible);
    }

    [Fact]
    public void MixedChineseEnglishNumbers_Preserved()
    {
        const string raw = "直径 Diameter 40.5mm，高度 25；ratio=1.618。";
        string rtf = ChatRtfBuilder.Build(raw, 9.75F);
        string visible = DecodeRtfUnicode(rtf);
        Assert.Equal(raw, visible);
    }

    [Fact]
    public void StrayAsterisks_StayLiteral_InRtf()
    {
        const string raw = "乘号 2**3 = 8 与单独 ** 标记";
        string rtf = ChatRtfBuilder.Build(raw, 9.75F);
        string visible = DecodeRtfUnicode(rtf);
        Assert.Equal(raw, visible);
    }

    [Fact]
    public void Paragraphs_ProduceParBreaks_OrderPreserved()
    {
        var sb = new System.Text.StringBuilder();
        ChatRtfBuilder.AppendBlocks(sb, ChatMarkdownParser.Parse("第一段。\n\n第二段。"));
        string body = sb.ToString();
        int first = body.IndexOf(Escape("第一段。"), StringComparison.Ordinal);
        int par = body.IndexOf("\\par", StringComparison.Ordinal);
        int second = body.IndexOf(Escape("第二段。"), StringComparison.Ordinal);
        Assert.True(first >= 0 && par > first && second > par, "段落顺序应保持且以 \\par 分隔");
    }

    [Fact]
    public void FullDocument_HasHeaderAndTerminator()
    {
        string rtf = ChatRtfBuilder.Build("你好", 9.75F);
        Assert.StartsWith(@"{\rtf1", rtf);
        Assert.EndsWith("}", rtf);
        Assert.Contains("Microsoft YaHei UI", rtf);
        Assert.Contains("\\fs20", rtf); // 9.75pt * 2 = 20 half-points
    }

    // ---------------------------------------------------------- helpers

    private static string Escape(string text)
    {
        var sb = new System.Text.StringBuilder();
        ChatRtfBuilder.AppendEscaped(sb, text);
        return sb.ToString();
    }

    /// <summary>Decodes \uN? escapes back to characters (test-only helper).</summary>
    private static string DecodeRtfUnicode(string rtf)
    {
        // Drop the font table group: its font names are metadata, not content.
        int fontStart = rtf.IndexOf(@"{\fonttbl", StringComparison.Ordinal);
        if (fontStart >= 0)
        {
            int depth = 0, i = fontStart;
            for (; i < rtf.Length; i++)
            {
                if (rtf[i] == '{') depth++;
                else if (rtf[i] == '}')
                {
                    depth--;
                    if (depth == 0) break;
                }
            }
            rtf = rtf[..fontStart] + rtf[(i + 1)..];
        }

        var sb = new System.Text.StringBuilder();
        int idx = 0;
        while (idx < rtf.Length)
        {
            if (rtf[idx] == '\\' && idx + 1 < rtf.Length && rtf[idx + 1] == 'u')
            {
                int j = idx + 2;
                int end = j;
                if (end < rtf.Length && (rtf[end] == '-' || char.IsDigit(rtf[end])))
                {
                    if (rtf[end] == '-') end++;
                    while (end < rtf.Length && char.IsDigit(rtf[end])) end++;
                    short value = short.Parse(rtf[j..end]);
                    sb.Append((char)value);
                    if (end < rtf.Length && rtf[end] == '?') end++;
                    idx = end;
                    continue;
                }
            }
            if (rtf[idx] == '\\' && idx + 1 < rtf.Length &&
                (rtf[idx + 1] == '\\' || rtf[idx + 1] == '{' || rtf[idx + 1] == '}'))
            {
                sb.Append(rtf[idx + 1]);
                idx += 2;
                continue;
            }
            // Skip RTF control words and group braces.
            if (rtf[idx] == '\\')
            {
                int j = idx + 1;
                while (j < rtf.Length && char.IsLetter(rtf[j])) j++;
                if (j < rtf.Length && rtf[j] == '-') j++;
                while (j < rtf.Length && char.IsDigit(rtf[j])) j++;
                if (j < rtf.Length && rtf[j] == ' ') j++;
                idx = Math.Max(j, idx + 1);
                continue;
            }
            if (rtf[idx] == '{' || rtf[idx] == '}')
            {
                idx++;
                continue;
            }
            sb.Append(rtf[idx]);
            idx++;
        }
        return sb.ToString();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, pos = 0;
        while ((pos = haystack.IndexOf(needle, pos, StringComparison.Ordinal)) >= 0)
        {
            count++;
            pos += needle.Length;
        }
        return count;
    }
}
