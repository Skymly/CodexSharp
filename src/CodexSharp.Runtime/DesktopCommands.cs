namespace CodexSharp.Runtime;

public enum DesktopPaletteKind
{
    NewThread,
    OpenSettings,
    FocusComposer,
    ToggleTerminal,
    ToggleSidebar,
    JumpThread,
}

public sealed record DesktopPaletteItem(
    string Id,
    string Title,
    string Keys,
    DesktopPaletteKind Kind,
    string? ThreadId = null);

/// Windows desktop command palette and published default shortcuts (NAV-05 / COMP-06).
/// Not a rebindable keymap; TUI emacs bindings stay in <see cref="TuiKeymap"/>.
public static class DesktopCommands
{
    public const string OpenPalette = "openPalette";
    public const string NewThread = "newThread";
    public const string OpenSettings = "openSettings";
    public const string ToggleTerminal = "toggleTerminal";
    public const string ToggleSidebar = "toggleSidebar";
    public const string FocusComposer = "focusComposer";
    public const string ShellChat = "shellChat";
    public const string ShellWork = "shellWork";
    public const string ShellCodex = "shellCodex";
    public const string DefaultShellMode = "codex";
    public const string QuickChat = "quickChat";
    public const string TemporaryChat = "temporaryChat";
    public const string PopOut = "popOut";

    public static readonly IReadOnlyList<(string Keys, string Action)> DefaultBindings =
    [
        ("ctrl+k", OpenPalette),
        ("ctrl+shift+p", OpenPalette),
        ("ctrl+n", NewThread),
        ("ctrl+,", OpenSettings),
        ("ctrl+`", ToggleTerminal),
        ("ctrl+b", ToggleSidebar),
        ("alt+1", ShellChat),
        ("alt+2", ShellWork),
        ("alt+3", ShellCodex),
        ("ctrl+alt+n", QuickChat),
        ("ctrl+shift+n", TemporaryChat),
        ("ctrl+alt+p", PopOut),
    ];

    public static string NormalizeChord(string? chord)
    {
        if (string.IsNullOrWhiteSpace(chord))
        {
            return "";
        }

        var mods = new HashSet<string>(StringComparer.Ordinal);
        var key = "";
        foreach (var part in chord.Trim().ToLowerInvariant().Replace(" ", "").Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part)
            {
                case "ctrl":
                case "control":
                    mods.Add("ctrl");
                    break;
                case "alt":
                    mods.Add("alt");
                    break;
                case "shift":
                    mods.Add("shift");
                    break;
                case "oemcomma":
                case "comma":
                    key = ",";
                    break;
                case "oemtilde":
                case "oem3":
                case "oem8":
                case "oem102":
                case "tilde":
                    key = "`";
                    break;
                case "d1":
                case "numpad1":
                    key = "1";
                    break;
                case "d2":
                case "numpad2":
                    key = "2";
                    break;
                case "d3":
                case "numpad3":
                    key = "3";
                    break;
                default:
                    key = part;
                    break;
            }
        }

        var ordered = new List<string>();
        if (mods.Contains("ctrl")) ordered.Add("ctrl");
        if (mods.Contains("alt")) ordered.Add("alt");
        if (mods.Contains("shift")) ordered.Add("shift");
        if (key.Length > 0) ordered.Add(key);
        return string.Join('+', ordered);
    }

    public static string? Match(string? chord)
    {
        var normalized = NormalizeChord(chord);
        if (normalized.Length == 0)
        {
            return null;
        }

        foreach (var (keys, action) in DefaultBindings)
        {
            if (NormalizeChord(keys) == normalized)
            {
                return action;
            }
        }

        return null;
    }

    public static bool IsClearTerminal(string? chord) =>
        NormalizeChord(chord) == "ctrl+l";

    public static IReadOnlyList<DesktopPaletteItem> CoreItems() =>
    [
        new("new-thread", "New thread", "Ctrl+N", DesktopPaletteKind.NewThread),
        new("open-settings", "Open settings", "Ctrl+,", DesktopPaletteKind.OpenSettings),
        new("focus-composer", "Focus composer", "", DesktopPaletteKind.FocusComposer),
        new("toggle-terminal", "Toggle terminal", "Ctrl+`", DesktopPaletteKind.ToggleTerminal),
        new("toggle-sidebar", "Toggle sidebar", "Ctrl+B", DesktopPaletteKind.ToggleSidebar),
    ];

    public static DesktopPaletteItem ThreadItem(string id, string? title) =>
        new(
            "thread:" + id,
            "Thread: " + (string.IsNullOrWhiteSpace(title) ? id : title.Trim()),
            "",
            DesktopPaletteKind.JumpThread,
            id);

    public static IReadOnlyList<DesktopPaletteItem> Filter(IEnumerable<DesktopPaletteItem> items, string? query)
    {
        var list = items as IList<DesktopPaletteItem> ?? items.ToList();
        if (string.IsNullOrWhiteSpace(query))
        {
            return list is IReadOnlyList<DesktopPaletteItem> ro ? ro : list.ToList();
        }

        var needle = query.Trim();
        return list.Where(item =>
                item.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || item.Id.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
