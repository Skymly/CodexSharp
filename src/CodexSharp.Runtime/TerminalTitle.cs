using System.Text;

namespace CodexSharp.Runtime;

/// Window-title sanitization inspired by vendor/codex/codex-rs/tui/src/terminal_title.rs.
public static class TerminalTitle
{
    public const int MaxChars = 240;

    public static string Sanitize(string? title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return "";
        }

        var sb = new StringBuilder(Math.Min(title.Length, MaxChars));
        foreach (var ch in title)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0 && sb[^1] != ' ')
                {
                    sb.Append(' ');
                }
                continue;
            }

            if (char.IsControl(ch) || ch is '​' or '‌' or '‍' or '⁦' or '⁧' or '⁨' or '⁩' or '‪' or '‫' or '‬' or '‭' or '‮')
            {
                continue;
            }

            sb.Append(ch);
            if (sb.Length >= MaxChars)
            {
                break;
            }
        }

        return sb.ToString().Trim();
    }
}
