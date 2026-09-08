namespace CodexSharp.Runtime;

public enum DeepLinkKind
{
    OpenMain,
    NewThread,
    OpenThread,
    Settings,
    Skills,
}

public sealed record DeepLink(
    DeepLinkKind Kind,
    string? ThreadId = null,
    string? WorkspacePath = null,
    bool IgnoredUnknownPath = false,
    string Raw = "");

/// Parses <c>codexsharp://</c> (and inbound <c>codex://</c> argv) without executing query paths.
public static class DesktopDeepLink
{
    public const string Scheme = "codexsharp";
    public const string OfficialScheme = "codex";

    public static string? FirstPayload(IEnumerable<string>? argv)
    {
        if (argv is null)
        {
            return null;
        }

        foreach (var arg in argv)
        {
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }

            var trimmed = arg.Trim().Trim('"');
            if (trimmed.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith(OfficialScheme + ":", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }
        }

        return null;
    }

    public static DeepLink Parse(string? raw)
    {
        var text = (raw ?? "").Trim().Trim('"');
        if (text.Length == 0)
        {
            return new DeepLink(DeepLinkKind.OpenMain, Raw: text);
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase) is false
                && uri.Scheme.Equals(OfficialScheme, StringComparison.OrdinalIgnoreCase) is false))
        {
            return new DeepLink(DeepLinkKind.OpenMain, IgnoredUnknownPath: true, Raw: text);
        }

        var workspace = ReadValidatedWorkspace(uri);
        var route = Route(uri);
        if (route.Equals("threads/new", StringComparison.OrdinalIgnoreCase))
        {
            return new DeepLink(DeepLinkKind.NewThread, WorkspacePath: workspace, Raw: text);
        }

        if (route.Equals("settings", StringComparison.OrdinalIgnoreCase))
        {
            return new DeepLink(DeepLinkKind.Settings, WorkspacePath: workspace, Raw: text);
        }

        if (route.Equals("skills", StringComparison.OrdinalIgnoreCase))
        {
            return new DeepLink(DeepLinkKind.Skills, WorkspacePath: workspace, Raw: text);
        }

        if (route.StartsWith("threads/", StringComparison.OrdinalIgnoreCase))
        {
            var id = route["threads/".Length..].Trim();
            if (IsThreadId(id))
            {
                return new DeepLink(DeepLinkKind.OpenThread, ThreadId: id, WorkspacePath: workspace, Raw: text);
            }
        }

        return new DeepLink(DeepLinkKind.OpenMain, WorkspacePath: workspace, IgnoredUnknownPath: true, Raw: text);
    }

    public static bool TryValidateWorkspacePath(string? raw, out string? fullPath)
    {
        fullPath = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var decoded = Uri.UnescapeDataString(raw.Trim().Trim('"'));
        if (decoded.Contains("://", StringComparison.Ordinal)
            || decoded.Contains('\0')
            || decoded.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || !Path.IsPathRooted(decoded))
        {
            return false;
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(decoded);
        }
        catch (Exception)
        {
            return false;
        }

        if (!Path.IsPathRooted(normalized))
        {
            return false;
        }

        if (!Directory.Exists(normalized))
        {
            return false;
        }

        fullPath = normalized;
        return true;
    }

    public static bool ShouldRegisterOfficialScheme(bool officialPresent)
    {
        _ = officialPresent;
        return false;
    }

    public static string OpenCommand(string exePath) =>
        "\"" + exePath + "\" \"%1\"";

    public static string? ResolveDesktopExe(string? processPath = null, string? baseDirectory = null)
    {
        var dir = baseDirectory ?? AppContext.BaseDirectory;
        var bundled = Path.Combine(dir, "CodexSharp.Desktop.exe");
        if (File.Exists(bundled))
        {
            return Path.GetFullPath(bundled);
        }

        var process = processPath ?? Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(process)
            && process.EndsWith("CodexSharp.Desktop.exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(process))
        {
            return Path.GetFullPath(process);
        }

        return null;
    }

    public static bool OfficialCodexProtocolPresent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var hkcu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\codex");
            if (hkcu is not null)
            {
                return true;
            }

            using var hkcr = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("codex");
            return hkcr is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool TryRegisterCodexSharp(string? exePath = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var exe = exePath ?? ResolveDesktopExe();
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            return false;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Scheme);
            if (key is null)
            {
                return false;
            }

            key.SetValue("", "URL:CodexSharp");
            key.SetValue("URL Protocol", "");
            using (var icon = key.CreateSubKey("DefaultIcon"))
            {
                icon?.SetValue("", exe + ",0");
            }

            using var command = key.CreateSubKey(@"shell\open\command");
            command?.SetValue("", OpenCommand(exe));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Route(Uri uri)
    {
        var host = (uri.Host ?? "").Trim('/');
        var path = (uri.AbsolutePath ?? "").Trim('/');
        if (string.IsNullOrEmpty(host) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return string.IsNullOrEmpty(path) ? host : host + "/" + path;
    }

    private static string? ReadValidatedWorkspace(Uri uri)
    {
        var query = uri.Query;
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = part.Split('=', 2);
            if (bits.Length != 2 || !bits[0].Equals("path", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return TryValidateWorkspacePath(bits[1], out var full) ? full : null;
        }

        return null;
    }

    private static bool IsThreadId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
        {
            return false;
        }

        foreach (var ch in id)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
