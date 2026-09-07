using System.Text;

namespace CodexSharp.Runtime;

public abstract record MdBlock;
public sealed record MdHeading(int Level, string Text) : MdBlock;
public sealed record MdCode(string Lang, string Text) : MdBlock;
public sealed record MdBullet(string Text) : MdBlock;
public sealed record MdQuote(string Text) : MdBlock;
public sealed record MdPara(string Text) : MdBlock;
public sealed record MdRule : MdBlock;
public sealed record MdFileCite(string Path, int? Line, string Label) : MdBlock;

/// Small markdown subset for Desktop/TUI, inspired by vendor/codex tui markdown_render.
public static class Markdown
{
    public static IReadOnlyList<MdBlock> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var blocks = new List<MdBlock>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.StartsWith("```"))
            {
                var lang = line[3..].Trim();
                var body = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].StartsWith("```"))
                {
                    if (body.Length > 0) body.Append('\n');
                    body.Append(lines[i]);
                    i++;
                }
                blocks.Add(new MdCode(lang, body.ToString()));
                if (i < lines.Length) i++;
                continue;
            }

            if (line.StartsWith("### ")) { blocks.Add(new MdHeading(3, line[4..].Trim())); i++; continue; }
            if (line.StartsWith("## ")) { blocks.Add(new MdHeading(2, line[3..].Trim())); i++; continue; }
            if (line.StartsWith("# ")) { blocks.Add(new MdHeading(1, line[2..].Trim())); i++; continue; }
            if (line.Trim() is "---" or "***") { blocks.Add(new MdRule()); i++; continue; }
            if (line.StartsWith("> ")) { blocks.Add(new MdQuote(line[2..])); i++; continue; }
            if (line.StartsWith("- ") || line.StartsWith("* ")) { blocks.Add(new MdBullet(line[2..])); i++; continue; }
            if (line.Length >= 2 && char.IsDigit(line[0]) && line[1] == '.') { blocks.Add(new MdBullet(line[2..].TrimStart())); i++; continue; }
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            var cites = FileCitations.Extract(line);
            if (cites.Count > 0)
            {
                foreach (var cite in cites) blocks.Add(cite);
                i++;
                continue;
            }

            var para = new StringBuilder(line);
            i++;
            while (i < lines.Length
                   && !string.IsNullOrWhiteSpace(lines[i])
                   && !lines[i].StartsWith("#")
                   && !lines[i].StartsWith("```")
                   && !lines[i].StartsWith("- ")
                   && !lines[i].StartsWith("* ")
                   && !lines[i].StartsWith("> "))
            {
                para.Append('\n').Append(lines[i]);
                i++;
            }
            blocks.Add(new MdPara(para.ToString()));
        }

        return blocks;
    }

    public static string StripInline(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Replace("**", "").Replace("__", "");
    }

    public static string? LastCode(string? text) =>
        Parse(text).OfType<MdCode>().LastOrDefault()?.Text;
}
