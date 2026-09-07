using Spectre.Console;

namespace CodexSharp.Cli;

/// Scrollable diff/text pager inspired by vendor/codex/codex-rs/tui pager_overlay.
internal static class DiffPager
{
    public static void Show(string title, string text)
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive || Console.IsOutputRedirected)
        {
            AnsiConsole.WriteLine(string.IsNullOrWhiteSpace(text) ? "(empty)" : text);
            return;
        }

        var lines = (text ?? "").Replace("\r", "").Split('\n');
        if (lines.Length == 1 && lines[0].Length == 0)
        {
            AnsiConsole.MarkupLine("[grey](no diff)[/]");
            return;
        }

        var offset = 0;
        while (true)
        {
            var height = Math.Max(6, Console.WindowHeight - 2);
            var width = Math.Max(40, Console.WindowWidth);
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(title)}[/]  [grey]j/k ↑↓  q quit  {offset + 1}/{lines.Length}[/]");
            var end = Math.Min(lines.Length, offset + height);
            for (var i = offset; i < end; i++)
            {
                var raw = lines[i];
                if (raw.Length > width - 1) raw = raw[..(width - 4)] + "...";
                var color = raw.StartsWith('+') && !raw.StartsWith("+++") ? "green"
                    : raw.StartsWith('-') && !raw.StartsWith("---") ? "red"
                    : raw.StartsWith("@@") ? "aqua"
                    : "grey";
                AnsiConsole.MarkupLine($"[{color}]{Markup.Escape(raw)}[/]");
            }

            var key = Console.ReadKey(true);
            if (key.Key is ConsoleKey.Q or ConsoleKey.Escape) break;
            if (key.Key is ConsoleKey.DownArrow or ConsoleKey.J) offset = Math.Min(Math.Max(0, lines.Length - 1), offset + 1);
            else if (key.Key is ConsoleKey.UpArrow or ConsoleKey.K) offset = Math.Max(0, offset - 1);
            else if (key.Key is ConsoleKey.PageDown or ConsoleKey.Spacebar) offset = Math.Min(Math.Max(0, lines.Length - 1), offset + height);
            else if (key.Key is ConsoleKey.PageUp) offset = Math.Max(0, offset - height);
            else if (key.Key is ConsoleKey.Home or ConsoleKey.G) offset = 0;
            else if (key.Key is ConsoleKey.End) offset = Math.Max(0, lines.Length - height);
        }
    }
}
