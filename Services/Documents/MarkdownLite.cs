using System.Text.RegularExpressions;

namespace aida.Services.Documents;

internal abstract record MdBlock
{
    internal sealed record Heading(int Level, string Text) : MdBlock;
    internal sealed record Paragraph(string Text) : MdBlock;
    internal sealed record Bullet(string Text) : MdBlock;
    internal sealed record Code(string Text) : MdBlock;
}

internal static partial class MarkdownLite
{
    internal static List<MdBlock> Parse(string markdown)
    {
        var blocks = new List<MdBlock>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;

        var inFence = false;
        var code = new List<string>();
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new MdBlock.Paragraph(string.Join(" ", paragraph).Trim()));
            paragraph.Clear();
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inFence)
                {
                    blocks.Add(new MdBlock.Code(string.Join("\n", code)));
                    code.Clear();
                }
                else
                {
                    FlushParagraph();
                }
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                code.Add(raw);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            var heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                FlushParagraph();
                var level = Math.Min(heading.Groups["hashes"].Value.Length, 4);
                blocks.Add(new MdBlock.Heading(level, heading.Groups["text"].Value.Trim()));
                continue;
            }

            var bullet = BulletRegex().Match(line);
            if (bullet.Success)
            {
                FlushParagraph();
                blocks.Add(new MdBlock.Bullet(bullet.Groups["text"].Value.Trim()));
                continue;
            }

            paragraph.Add(line);
        }

        if (inFence && code.Count > 0)
            blocks.Add(new MdBlock.Code(string.Join("\n", code)));
        FlushParagraph();

        return blocks;
    }

    internal static IEnumerable<(string Text, bool Bold, bool Italic, bool Code)> Inline(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        foreach (Match m in InlineRegex().Matches(text))
        {
            if (m.Groups["bold"].Success)
                yield return (m.Groups["bold"].Value, Bold: true, Italic: false, Code: false);
            else if (m.Groups["italic"].Success)
                yield return (m.Groups["italic"].Value, Bold: false, Italic: true, Code: false);
            else if (m.Groups["code"].Success)
                yield return (m.Groups["code"].Value, Bold: false, Italic: false, Code: true);
            else
                yield return (m.Value, false, false, false);
        }
    }

    internal static string Plain(string text)
    {
        var parts = Inline(text);
        return string.Concat(parts.Select(p => p.Text));
    }

    [GeneratedRegex(@"^(?<hashes>#{1,6})\s+(?<text>.+)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*[-*+]\s+(?<text>.+)$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"\*\*(?<bold>[^*]+)\*\*|\*(?<italic>[^*]+)\*|`(?<code>[^`]+)`|[^*`]+")]
    private static partial Regex InlineRegex();
}
