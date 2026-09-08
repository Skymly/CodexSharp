namespace CodexSharp.Runtime;

/// Concrete Host tokens from an OpenSSH config. Pure patterns are ignored.
public static class SshConfig
{
    public static bool IsPattern(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return true;
        }

        var t = token.Trim();
        return t.StartsWith('!') || t.Contains('*') || t.Contains('?');
    }

    public static IReadOnlyList<string> ConcreteHosts(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var hosts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.None))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[0].Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var token in parts.Skip(1))
            {
                if (IsPattern(token) || !seen.Add(token))
                {
                    continue;
                }

                hosts.Add(token);
            }
        }

        return hosts;
    }

    public static IReadOnlyList<string> ConcreteHostsFromFile(string? path = null)
    {
        path ??= DefaultPath();
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return [];
            }

            return ConcreteHosts(File.ReadAllText(path));
        }
        catch
        {
            return [];
        }
    }

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
}
