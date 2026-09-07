namespace CodexSharp.Runtime;

public sealed record KeymapBinding(string Context, string Action, string Keys);

/// Composer/chat keymap overlay inspired by vendor/codex/codex-rs/tui keymap.
/// Spectre TUI still uses prompts; bindings are persisted and shown as hints.
public static class TuiKeymap
{
    public static readonly KeymapBinding[] Defaults =
    [
        new("composer", "submit", "enter"),
        new("composer", "queue", "alt+enter"),
        new("composer", "newline", "shift+enter"),
        new("composer", "paste", "ctrl+v"),
        new("global", "interrupt", "ctrl+c"),
        new("global", "new", "ctrl+n"),
        new("global", "quit", "ctrl+q"),
        new("global", "open_external_editor", "ctrl+g"),
        new("editor", "move_left", "left"),
        new("editor", "move_right", "right"),
        new("editor", "move_line_start", "home"),
        new("editor", "move_line_end", "end"),
        new("editor", "delete_backward", "backspace"),
        new("editor", "delete_backward_word", "ctrl+w"),
        new("editor", "kill_line_start", "ctrl+u"),
        new("editor", "kill_line_end", "ctrl+k"),
        new("editor", "yank", "ctrl+y"),
        new("chat", "scroll_up", "pageup"),
        new("chat", "scroll_down", "pagedown"),
        new("approval", "approve", "y"),
        new("approval", "deny", "n"),
    ];

    public static IReadOnlyList<KeymapBinding> Load()
    {
        var map = Defaults.ToDictionary(b => Slot(b.Context, b.Action), StringComparer.OrdinalIgnoreCase);
        foreach (var part in (ConfigService.Peek("tui_keymap") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (bits.Length != 2) continue;
            var ctxAction = bits[0].Split('.', 2, StringSplitOptions.TrimEntries);
            if (ctxAction.Length != 2 || !IsKnown(ctxAction[0], ctxAction[1]) || string.IsNullOrWhiteSpace(bits[1])) continue;
            map[Slot(ctxAction[0], ctxAction[1])] = new KeymapBinding(ctxAction[0].ToLowerInvariant(), ctxAction[1].ToLowerInvariant(), bits[1]);
        }

        return Defaults.Select(d => map[Slot(d.Context, d.Action)]).ToArray();
    }

    public static bool TrySet(string context, string action, string keys)
    {
        if (!IsKnown(context, action) || string.IsNullOrWhiteSpace(keys))
        {
            return false;
        }

        var next = Load()
            .Select(b => string.Equals(b.Context, context, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(b.Action, action, StringComparison.OrdinalIgnoreCase)
                ? b with { Keys = keys.Trim() }
                : b)
            .Select(b => $"{b.Context}.{b.Action}={b.Keys}");
        ConfigService.WriteUserKeys(new Dictionary<string, string> { ["tui_keymap"] = string.Join(';', next) });
        return true;
    }

    public static string Hint() =>
        string.Join(" · ", Load().Take(4).Select(b => $"{b.Keys} {b.Action}"));

    public static bool IsKnown(string context, string action) =>
        Defaults.Any(b =>
            string.Equals(b.Context, context, StringComparison.OrdinalIgnoreCase)
            && string.Equals(b.Action, action, StringComparison.OrdinalIgnoreCase));

    private static string Slot(string context, string action) =>
        context.Trim().ToLowerInvariant() + "." + action.Trim().ToLowerInvariant();
}

/// Terminal pets inspired by vendor/codex/codex-rs/tui pets. Original glyphs; not official assets.
public static class TuiPets
{
    public static readonly string[] Ids =
        ["none", "codex", "dewey", "fireball", "rocky", "seedy", "stacky", "bsod", "null-signal"];

    public static string Current
    {
        get
        {
            var id = (ConfigService.Peek("tui_pet") ?? "none").Trim().ToLowerInvariant();
            return Ids.Contains(id) ? id : "none";
        }
    }

    public static bool TrySet(string id)
    {
        var key = (id ?? "").Trim().ToLowerInvariant();
        if (key is "off" or "hide") key = "none";
        if (!Ids.Contains(key)) return false;
        ConfigService.WriteUserKeys(new Dictionary<string, string> { ["tui_pet"] = key });
        return true;
    }

    public static string Ascii(string? id = null) =>
        (id ?? Current) switch
        {
            "codex" => "[o_o]",
            "dewey" => "(^_^)",
            "fireball" => "(>o<)",
            "rocky" => "[#.#]",
            "seedy" => "(-_-)",
            "stacky" => "[=_=]",
            "bsod" => "[x_x]",
            "null-signal" => "(   )",
            _ => "",
        };
}
