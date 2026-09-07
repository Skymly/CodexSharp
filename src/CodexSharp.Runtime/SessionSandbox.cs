using System.Threading;

namespace CodexSharp.Runtime;

/// Session-scoped extra writable/readable roots from official `--add-dir`.
public static class SessionSandbox
{
    private static readonly AsyncLocal<List<string>?> Extra = new();

    public static void Reset() => Extra.Value = null;

    public static IReadOnlyList<string> List() => Extra.Value ?? (IReadOnlyList<string>)[];

    public static string Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            throw new ArgumentException("add-dir must be an existing directory");
        }

        var full = Path.GetFullPath(path);
        Extra.Value ??= [];
        if (!Extra.Value.Any(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase)))
        {
            Extra.Value.Add(full);
        }

        return full;
    }

    public static bool Allows(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var root in List())
        {
            var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.Equals(normalized, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
