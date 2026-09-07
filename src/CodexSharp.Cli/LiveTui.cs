using CodexSharp.Protocol;
using CodexSharp.Runtime;
using Spectre.Console;

namespace CodexSharp.Cli;

/// Full-screen Spectre composer inspired by vendor/codex/codex-rs/tui (history + bottom pane).
/// Not ratatui; redraws a layout each keypress when the console is interactive.
internal static class LiveTui
{
    internal sealed record Cell(string Kind, string Text);

    public const string QueuePrefix = "\u0001queue\n";

    public static bool Supported =>
        !Console.IsInputRedirected
        && !Console.IsOutputRedirected
        && AnsiConsole.Profile.Capabilities.Interactive;

    public static void Paint(TuiAgent session, IReadOnlyList<Cell> history, string composer, string footer, IReadOnlyList<string>? popup = null, int cursor = -1)
    {
        var height = Math.Max(12, Console.WindowHeight);
        var width = Math.Max(40, Console.WindowWidth);
        var popupLines = popup is { Count: > 0 } ? popup.Take(6).ToArray() : [];
        var composerView = FormatComposer(composer, cursor, width);
        var header = 3;
        var footerH = 2 + popupLines.Length + composerView.Count;
        var bodyH = Math.Max(4, height - header - footerH);

        AnsiConsole.Clear();
        var status = TuiStatus.Line(session.Config, session.Thread.Id, session.Thread.Title);
        var pet = TuiPets.Ascii();
        AnsiConsole.MarkupLine($"[bold gold1]CodexSharp[/]  [grey]{Markup.Escape(session.Thread.Id)}[/]");
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(status)}[/]" + (string.IsNullOrWhiteSpace(pet) ? "" : "  " + Markup.Escape(pet)));
        AnsiConsole.MarkupLine("[grey]" + Markup.Escape(TuiKeymap.Hint()) + "[/]");

        var lines = Flatten(history, width - 1);
        var slice = lines.Count <= bodyH ? lines : lines.Skip(lines.Count - bodyH).ToList();
        foreach (var line in slice)
        {
            AnsiConsole.MarkupLine(line);
        }

        for (var i = slice.Count; i < bodyH; i++)
        {
            AnsiConsole.WriteLine();
        }

        foreach (var hit in popupLines)
        {
            AnsiConsole.MarkupLine("[grey]" + Markup.Escape(hit) + "[/]");
        }

        for (var i = 0; i < composerView.Count; i++)
        {
            var prefix = i == 0 ? "[bold green]>[/] " : "  ";
            AnsiConsole.MarkupLine(prefix + Markup.Escape(composerView[i]));
        }

        AnsiConsole.MarkupLine("[grey]" + Markup.Escape(string.IsNullOrWhiteSpace(footer) ? "enter send · shift+enter newline · alt+enter queue · ctrl+g editor · /help" : footer) + "[/]");
    }

    public static async Task<string?> ReadAsync(
        TuiAgent session,
        IReadOnlyList<Cell> history,
        List<string> promptHistory,
        string[] slashCommands,
        string footer,
        CancellationToken ct)
    {
        var composer = new ComposerBuffer();
        var vim = new ComposerVim(composer);
        var histIndex = -1;
        var draft = "";
        Paint(session, history, "", footer, null, 0);
        while (!ct.IsCancellationRequested)
        {
            while (!Console.KeyAvailable)
            {
                if (ct.IsCancellationRequested) return null;
                await Task.Delay(30, ct);
            }

            var key = Console.ReadKey(true);
            if (MatchesChord(key, "interrupt") || (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)))
            {
                return "\u0003";
            }

            vim.Enabled = string.Equals(ConfigService.Peek("tui_vim"), "true", StringComparison.OrdinalIgnoreCase);
            if (vim.TryHandle(key))
            {
            }
            else if (MatchesChord(key, "open_external_editor"))
            {
                try
                {
                    composer.Set(ExternalEditor.Edit(composer.Text));
                }
                catch (Exception ex)
                {
                    footer = ex.Message;
                }
            }
            else if (MatchesChord(key, "queue"))
            {
                var text = composer.Text;
                if (string.IsNullOrWhiteSpace(text)) continue;
                return QueuePrefix + text;
            }
            else if (MatchesChord(key, "newline"))
            {
                composer.Newline();
            }
            else if (MatchesChord(key, "submit") || (key.Key == ConsoleKey.Enter && key.Modifiers == 0))
            {
                var text = composer.Text;
                if (string.IsNullOrWhiteSpace(text)) continue;
                return text;
            }
            else if (MatchesChord(key, "paste") || (key.Key == ConsoleKey.V && key.Modifiers.HasFlag(ConsoleModifiers.Control)))
            {
                Paste(session, composer, ref footer);
            }
            else if (MatchesChord(key, "delete_backward_word") || (key.Key == ConsoleKey.W && key.Modifiers.HasFlag(ConsoleModifiers.Control)))
            {
                composer.KillWordBack();
            }
            else if (MatchesChord(key, "kill_line_start") || (key.Key == ConsoleKey.U && key.Modifiers.HasFlag(ConsoleModifiers.Control)))
            {
                composer.KillToStart();
            }
            else if (MatchesChord(key, "kill_line_end") || (key.Key == ConsoleKey.K && key.Modifiers.HasFlag(ConsoleModifiers.Control)))
            {
                composer.KillToEnd();
            }
            else if (MatchesChord(key, "yank") || (key.Key == ConsoleKey.Y && key.Modifiers.HasFlag(ConsoleModifiers.Control)))
            {
                composer.YankInsert();
            }
            else if (MatchesChord(key, "move_left") || key.Key == ConsoleKey.LeftArrow)
            {
                composer.MoveLeft();
            }
            else if (MatchesChord(key, "move_right") || key.Key == ConsoleKey.RightArrow)
            {
                composer.MoveRight();
            }
            else if (MatchesChord(key, "move_line_start") || key.Key == ConsoleKey.Home)
            {
                composer.MoveHome();
            }
            else if (MatchesChord(key, "move_line_end") || key.Key == ConsoleKey.End)
            {
                composer.MoveEnd();
            }
            else if (key.Key == ConsoleKey.Delete)
            {
                composer.DeleteForward();
            }
            else if (MatchesChord(key, "delete_backward") || key.Key == ConsoleKey.Backspace)
            {
                composer.Backspace();
            }
            else if (key.Key == ConsoleKey.Escape)
            {
                composer.Clear();
                histIndex = -1;
            }
            else if (key.Key == ConsoleKey.Tab)
            {
                Complete(session, composer, slashCommands);
            }
            else if (key.Key == ConsoleKey.UpArrow)
            {
                if (promptHistory.Count == 0) continue;
                if (histIndex < 0) draft = composer.Text;
                histIndex = Math.Min(histIndex + 1, promptHistory.Count - 1);
                composer.Set(promptHistory[histIndex]);
            }
            else if (key.Key == ConsoleKey.DownArrow)
            {
                if (histIndex < 0) continue;
                histIndex--;
                composer.Set(histIndex < 0 ? draft : promptHistory[histIndex]);
            }
            else if (!char.IsControl(key.KeyChar))
            {
                composer.Insert(key.KeyChar);
            }

            var now = composer.Text;
            IReadOnlyList<string>? popup = null;
            if (now.StartsWith('/') && !now.Contains(' ') && !now.Contains('\n'))
            {
                popup = slashCommands.Where(c => c.StartsWith(now, StringComparison.OrdinalIgnoreCase)).Take(6).ToArray();
            }
            else
            {
                var at = now.LastIndexOf('@');
                if (at >= 0 && (at == now.Length - 1 || !now[(at + 1)..].Contains(' ')))
                {
                    var q = at >= 0 && at < now.Length - 1 ? now[(at + 1)..] : "";
                    popup = FileSearch.SuggestNames(session.Config.Cwd, q, 6);
                }
            }

            var vimFooter = vim.Enabled ? ("vim " + vim.Mode.ToString().ToLowerInvariant() + "  " + footer) : footer;
            Paint(session, history, now, vimFooter, popup, composer.Cursor);
        }

        return null;
    }

    public static void Append(List<Cell> history, AgentEvent evt)
    {
        switch (evt)
        {
            case AgentEvent.ItemDelta delta:
                if (history.Count > 0 && history[^1].Kind == "agent")
                    history[^1] = history[^1] with { Text = history[^1].Text + delta.Text };
                else
                    history.Add(new Cell("agent", delta.Text));
                break;
            case AgentEvent.CommandOutputDelta output:
                if (history.Count > 0 && history[^1].Kind == "command")
                    history[^1] = history[^1] with { Text = history[^1].Text + output.Text };
                else
                    history.Add(new Cell("command", output.Text));
                break;
            case AgentEvent.ItemCompleted item when item.Item.Kind == "user_message":
                history.Add(new Cell("you", item.Item.Text));
                break;
            case AgentEvent.ItemCompleted item when item.Item.Kind == "agent_message":
                if (history.Count == 0 || history[^1].Kind != "agent")
                    history.Add(new Cell("agent", item.Item.Text));
                else
                    history[^1] = history[^1] with { Text = string.IsNullOrWhiteSpace(item.Item.Text) ? history[^1].Text : item.Item.Text };
                break;
            case AgentEvent.ItemCompleted item when item.Item.Kind is "tool_call" or "command_execution" or "file_change" or "reasoning":
                history.Add(new Cell(item.Item.Kind == "file_change" ? "patch" : item.Item.Kind, item.Item.Text));
                break;
            case AgentEvent.Failed failed:
                history.Add(new Cell("error", failed.Message));
                break;
            case AgentEvent.TurnCompleted:
                history.Add(new Cell("status", "turn completed"));
                break;
        }

        if (history.Count > 400)
        {
            history.RemoveRange(0, history.Count - 300);
        }
    }

    internal static bool MatchesChord(ConsoleKeyInfo key, string action)
    {
        var binding = TuiKeymap.Load().FirstOrDefault(b => b.Action == action);
        if (binding is null) return false;
        var parts = binding.Keys.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        var needCtrl = parts.Any(p => p.Equals("ctrl", StringComparison.OrdinalIgnoreCase));
        var needAlt = parts.Any(p => p.Equals("alt", StringComparison.OrdinalIgnoreCase));
        var needShift = parts.Any(p => p.Equals("shift", StringComparison.OrdinalIgnoreCase));
        if (needCtrl != key.Modifiers.HasFlag(ConsoleModifiers.Control)) return false;
        if (needAlt != key.Modifiers.HasFlag(ConsoleModifiers.Alt)) return false;
        if (needShift != key.Modifiers.HasFlag(ConsoleModifiers.Shift)) return false;
        var letter = parts[^1];
        return TryMapKey(letter, out var ck) && key.Key == ck;
    }

    internal static bool TryMapKey(string letter, out ConsoleKey key)
    {
        switch (letter.Trim().ToLowerInvariant())
        {
            case "left": key = ConsoleKey.LeftArrow; return true;
            case "right": key = ConsoleKey.RightArrow; return true;
            case "up": key = ConsoleKey.UpArrow; return true;
            case "down": key = ConsoleKey.DownArrow; return true;
            case "esc": key = ConsoleKey.Escape; return true;
            case "pageup": key = ConsoleKey.PageUp; return true;
            case "pagedown": key = ConsoleKey.PageDown; return true;
            default:
                return Enum.TryParse(letter, true, out key);
        }
    }

    private static void Paste(TuiAgent session, ComposerBuffer composer, ref string footer)
    {
        try
        {
            var png = ClipboardPaste.GetPng();
            if (png is { Length: > 0 })
            {
                var path = ClipboardImageStore.SavePng(png);
                session.PendingImages.Add(path);
                composer.Insert(" @" + path + " ");
                footer = "pasted image " + Path.GetFileName(path);
                return;
            }
        }
        catch (Exception ex)
        {
            footer = ex.Message;
        }

        var text = ClipboardPaste.GetText();
        if (!string.IsNullOrEmpty(text))
        {
            composer.Insert(text);
        }
        else
        {
            footer = "clipboard empty";
        }
    }

    private static void Complete(TuiAgent session, ComposerBuffer composer, string[] slashCommands)
    {
        var cur = composer.Text;
        if (cur.StartsWith('/') && !cur.Contains(' ') && !cur.Contains('\n'))
        {
            var hit = slashCommands.FirstOrDefault(c => c.StartsWith(cur, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) composer.Set(hit + " ");
            return;
        }

        var at = cur.LastIndexOf('@');
        if (at >= 0)
        {
            var mention = session.App.MentionHitsAsync(cur).GetAwaiter().GetResult();
            if (mention.Count > 0) composer.Set(cur[..at] + mention[0]);
        }
    }

    private static List<string> FormatComposer(string composer, int cursor, int width)
    {
        var text = (composer ?? "").Replace('\r', '\n');
        if (cursor >= 0)
        {
            cursor = Math.Clamp(cursor, 0, text.Length);
            text = text[..cursor] + "|" + text[cursor..];
        }

        var raw = text.Length == 0 ? "|" : text;
        var lines = raw.Split('\n');
        if (lines.Length > 4) lines = lines[^4..];
        return lines.Select(line =>
        {
            var max = Math.Max(8, width - 4);
            return line.Length > max ? line[^max..] : line;
        }).ToList();
    }

    private static List<string> Flatten(IReadOnlyList<Cell> history, int width)
    {
        var lines = new List<string>();
        foreach (var cell in history)
        {
            var color = cell.Kind switch
            {
                "you" => "cyan",
                "agent" => "white",
                "error" => "red",
                "patch" => "green",
                "reasoning" => "grey",
                _ => "blue",
            };
            var prefix = cell.Kind switch
            {
                "you" => "you ",
                "agent" => "codex ",
                "error" => "err ",
                "patch" => "patch ",
                _ => cell.Kind + " ",
            };
            var text = cell.Text.Replace('\r', ' ').Replace('\t', ' ');
            foreach (var raw in text.Split('\n'))
            {
                var line = raw;
                if (line.Length > width - prefix.Length - 1)
                    line = line[..Math.Max(8, width - prefix.Length - 4)] + "...";
                lines.Add($"[{color}]{Markup.Escape(prefix + line)}[/]");
                prefix = new string(' ', prefix.Length);
            }
        }

        return lines;
    }
}
