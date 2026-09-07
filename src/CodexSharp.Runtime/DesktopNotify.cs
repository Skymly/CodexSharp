namespace CodexSharp.Runtime;

/// Terminal desktop notifications inspired by vendor/codex/codex-rs/tui/notifications (OSC 9 + BEL).
public static class DesktopNotify
{
    public static string Method =>
        (ConfigService.Peek("tui_notifications") ?? "auto").Trim().ToLowerInvariant();

    public static string Sanitize(string message) =>
        (message ?? "")
            .Replace("\u001b", "", StringComparison.Ordinal)
            .Replace("\u0007", "", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

    public static string Osc9(string message) =>
        "\u001b]9;" + Sanitize(message) + "\u0007";

    public static void Send(string message)
    {
        var method = Method;
        if (method is "off" or "none" or "false")
        {
            return;
        }

        var text = Sanitize(message);
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            if (method is "bel")
            {
                Console.Write('\a');
                return;
            }

            Console.Write(Osc9(text));
            if (method is "auto")
            {
                Console.Write('\a');
            }
        }
        catch
        {
            // notification failures must not break the agent
        }
    }
}
