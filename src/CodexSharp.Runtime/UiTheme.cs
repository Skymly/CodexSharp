namespace CodexSharp.Runtime;

/// Desktop/TUI chrome theme. light|day -> light; everything else dark.
public static class UiTheme
{
    public static string Current =>
        (ConfigService.Peek("tui_theme") ?? "dark").Trim().ToLowerInvariant();

    public static bool IsLight(string? name = null)
    {
        var n = (name ?? Current).Trim().ToLowerInvariant();
        return n is "light" or "day" or "white";
    }
}
