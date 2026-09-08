using System.Diagnostics;

namespace CodexSharp.Runtime;

/// Windows OS toast for Desktop (SET-02). Honors tui_notifications=off. Never pretends success.
public static class OsNotify
{
    public static Func<string, string, bool>? TestSender { get; set; }

    public static bool IsOff
    {
        get
        {
            var method = DesktopNotify.Method;
            return method is "off" or "none" or "false";
        }
    }

    public static bool TrySend(string title, string body)
    {
        if (IsOff)
        {
            return false;
        }

        var head = DesktopNotify.Sanitize(title);
        var text = DesktopNotify.Sanitize(body);
        if (head.Length == 0 && text.Length == 0)
        {
            return false;
        }

        if (head.Length == 0)
        {
            head = "CodexSharp";
        }

        if (TestSender is not null)
        {
            return TestSender(head, text);
        }

        return TryWindowsToast(head, text);
    }

    internal static bool TryWindowsToast(string title, string body)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var xml = "<toast><visual><binding template=\"ToastGeneric\"><text>"
                      + XmlEscape(title)
                      + "</text><text>"
                      + XmlEscape(body)
                      + "</text></binding></visual></toast>";
            var script =
                "$xml = New-Object Windows.Data.Xml.Dom.XmlDocument; "
                + "$xml.LoadXml(@'\n"
                + xml.Replace("'", "''")
                + "\n'@); "
                + "$toast = [Windows.UI.Notifications.ToastNotification]::new($xml); "
                + "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('CodexSharp.Desktop').Show($toast);";
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return false;
            }

            if (!proc.WaitForExit(2500))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            return proc.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string XmlEscape(string value) =>
        value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
}
