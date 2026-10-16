using System.Text;

namespace NXAI.Assistant.UI;

/// <summary>A bold or plain inline segment of text.</summary>
internal sealed record ChatMarkdownRun(string Text, bool Bold);

/// <summary>
/// One visual block of a rendered markdown message: a paragraph (flow of
/// inline runs) or an unordered list (one bullet line per item).
/// </summary>
internal sealed class ChatMarkdownBlock
{
    public bool IsList { get; init; }
    public IReadOnlyList<ChatMarkdownRun> Runs { get; init; } = Array.Empty<ChatMarkdownRun>();
    public IReadOnlyList<IReadOnlyList<ChatMarkdownRun>> ListItems { get; init; } =
        Array.Empty<IReadOnlyList<ChatMarkdownRun>>();
}

/// <summary>
/// Minimal, display-only markdown parsing for chat replies: paragraphs,
/// blank-line separation, unordered ("- " / "* ") lists, and "**bold**" runs.
/// Unknown syntax stays literal text; no HTML is parsed or executed. Pure
/// string handling — no regex on prose beyond exact "**" pair matching.
/// A "**...**" pair only counts as bold when the content is non-empty and
/// does not start or end with a space (CommonMark flanking rule), so stray
/// asterisks like "2**3" stay literal.
/// </summary>
internal static class ChatMarkdownParser
{
    public static IReadOnlyList<ChatMarkdownBlock> Parse(string text)
    {
        var blocks = new List<ChatMarkdownBlock>();
        var paragraphRuns = new List<ChatMarkdownRun>();
        var listItems = new List<List<ChatMarkdownRun>>();
        var currentListItem = new List<ChatMarkdownRun>();
        bool inList = false;

        void FlushParagraph()
        {
            if (paragraphRuns.Count > 0)
            {
                blocks.Add(new ChatMarkdownBlock { Runs = paragraphRuns.ToArray() });
                paragraphRuns = new List<ChatMarkdownRun>();
            }
        }

        void FlushListItem()
        {
            if (currentListItem.Count > 0)
            {
                listItems.Add(currentListItem);
                currentListItem = new List<ChatMarkdownRun>();
            }
        }

        void FlushList()
        {
            FlushListItem();
            if (listItems.Count > 0)
            {
                blocks.Add(new ChatMarkdownBlock
                {
                    IsList = true,
                    ListItems = listItems.Select(i => (IReadOnlyList<ChatMarkdownRun>)i.ToArray()).ToArray(),
                });
                listItems = new List<List<ChatMarkdownRun>>();
            }
        }

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                FlushList();
                inList = false;
                continue;
            }

            if (IsListItem(line, out string content))
            {
                FlushParagraph();
                FlushListItem();
                inList = true;
                currentListItem.AddRange(ParseInlineRuns(content));
                continue;
            }

            // Non-list line: close any open list, then extend the paragraph.
            if (inList)
            {
                FlushList();
                inList = false;
            }
            if (paragraphRuns.Count > 0)
                paragraphRuns.Add(new ChatMarkdownRun("\n", Bold: false));
            paragraphRuns.AddRange(ParseInlineRuns(line));
        }

        if (inList)
            FlushList();
        FlushParagraph();

        return blocks;
    }

    private static bool IsListItem(string line, out string content)
    {
        content = "";
        if (line.Length >= 2 && (line[0] == '-' || line[0] == '*') && line[1] == ' ')
        {
            content = line[2..];
            return true;
        }
        return false;
    }

    public static IReadOnlyList<ChatMarkdownRun> ParseInlineRuns(string line)
    {
        var runs = new List<ChatMarkdownRun>();
        int pos = 0;
        while (pos < line.Length)
        {
            int open = line.IndexOf("**", pos, StringComparison.Ordinal);
            if (open < 0)
            {
                AddRun(runs, line[pos..], Bold: false);
                break;
            }
            int close = line.IndexOf("**", open + 2, StringComparison.Ordinal);
            bool validBold = false;
            if (close >= 0)
            {
                string content = line[(open + 2)..close];
                validBold = content.Length > 0 &&
                            !char.IsWhiteSpace(content[0]) &&
                            !char.IsWhiteSpace(content[^1]);
            }
            if (!validBold)
            {
                // Not a bold pair: emit up to and including the opening "**"
                // as literal, then resume scanning after it.
                AddRun(runs, line[pos..(open + 2)], Bold: false);
                pos = open + 2;
                continue;
            }
            if (open > pos)
                AddRun(runs, line[pos..open], Bold: false);
            AddRun(runs, line[(open + 2)..close], Bold: true);
            pos = close + 2;
        }
        return runs;
    }

    private static void AddRun(List<ChatMarkdownRun> runs, string text, bool Bold)
    {
        if (text.Length > 0)
            runs.Add(new ChatMarkdownRun(text, Bold));
    }
}

/// <summary>
/// Display-only markdown-to-RTF conversion for chat messages. Produces the
/// RTF body (no header) for a read-only RichTextBox: bold runs, "- "/"* "
/// bullet lists, paragraph and line breaks. All text is escaped to RTF
/// unicode (\uN?) so Chinese, symbols and braces survive byte-identical;
/// no HTML is produced and model prose can never inject RTF control words.
/// Pure string functions — unit-testable outside WinForms.
/// </summary>
internal static class ChatRtfBuilder
{
    /// <summary>Builds a full RTF document for the message text.</summary>
    public static string Build(string markdownText, float fontSizePt, bool boldFontAvailable = true)
    {
        var blocks = ChatMarkdownParser.Parse(markdownText);
        var sb = new StringBuilder(capacity: 256 + markdownText.Length * 8);
        sb.Append(@"{\rtf1\ansi\ansicpg0\deff0");
        sb.Append(@"{\fonttbl{\f0\fnil\fcharset134 Microsoft YaHei UI;}}");
        sb.Append(@"\fs").Append((int)MathF.Round(fontSizePt * 2));
        sb.Append("\\viewkind4\\uc1 ");
        AppendBlocks(sb, blocks);
        sb.Append('}');
        return sb.ToString();
    }

    internal static void AppendBlocks(StringBuilder sb, IReadOnlyList<ChatMarkdownBlock> blocks)
    {
        bool first = true;
        foreach (var block in blocks)
        {
            if (!first)
                sb.Append("\\par ");
            first = false;

            if (block.IsList)
            {
                bool firstItem = true;
                foreach (var item in block.ListItems)
                {
                    if (!firstItem)
                        sb.Append("\\line ");
                    firstItem = false;
                    sb.Append(@"\bullet  ");
                    AppendRuns(sb, item);
                }
                continue;
            }

            AppendRuns(sb, block.Runs);
        }
    }

    private static void AppendRuns(StringBuilder sb, IReadOnlyList<ChatMarkdownRun> runs)
    {
        bool bold = false;
        foreach (var run in runs)
        {
            // A "\n" inside a paragraph run is an explicit line break the
            // parser inserted between consecutive non-list lines.
            var segments = run.Text.Split('\n');
            for (int i = 0; i < segments.Length; i++)
            {
                if (i > 0)
                    sb.Append("\\line ");
                if (segments[i].Length == 0)
                    continue;
                if (run.Bold && !bold)
                {
                    sb.Append(@"\b ");
                    bold = true;
                }
                else if (!run.Bold && bold)
                {
                    sb.Append(@"\b0 ");
                    bold = false;
                }
                AppendEscaped(sb, segments[i]);
            }
        }
        if (bold)
            sb.Append(@"\b0 ");
    }

    /// <summary>
    /// Appends text as RTF: braces and backslash escaped, every non-ASCII
    /// char as signed \uN? (surrogate pairs become two \u escapes, which
    /// RichEdit recombines), control chars 0x00-0x1F skipped.
    /// </summary>
    internal static void AppendEscaped(StringBuilder sb, string text)
    {
        foreach (char c in text)
        {
            switch (c)
            {
                case '\\':
                    sb.Append(@"\\");
                    break;
                case '{':
                    sb.Append(@"\{");
                    break;
                case '}':
                    sb.Append(@"\}");
                    break;
                default:
                    if (c < ' ')
                        continue; // control characters never reach the display
                    if (c <= 0x7F)
                    {
                        sb.Append(c);
                    }
                    else
                    {
                        sb.Append(@"\u").Append(unchecked((short)c)).Append('?');
                    }
                    break;
            }
        }
    }
}
