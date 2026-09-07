namespace CodexSharp.Runtime;

/// Resume-picker candidates matching vendor/codex resume --last/--all/--include-non-interactive.
/// Default hides other CWDs and exec/non-interactive threads.
public static class ResumePicker
{
    public static IReadOnlyList<ThreadInfo> Candidates(
        bool all = false,
        bool includeNonInteractive = false,
        string? cwd = null,
        string? search = null)
    {
        cwd = Path.GetFullPath(cwd ?? Environment.CurrentDirectory);
        IEnumerable<ThreadInfo> list = new JsonlThreadStore().List();
        if (!all)
        {
            list = list.Where(t => PathsEqual(t.Cwd, cwd));
        }

        if (!includeNonInteractive)
        {
            list = list.Where(t =>
            {
                var source = ThreadSources.Get(t.Id);
                return !string.Equals(source, "exec", StringComparison.OrdinalIgnoreCase)
                       && !string.Equals(source, "non-interactive", StringComparison.OrdinalIgnoreCase);
            });
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            list = list.Where(t =>
                Contains(t.Id, search) || Contains(t.Title, search) || Contains(t.Cwd, search));
        }

        return list.ToArray();
    }

    public static string Format(ThreadInfo thread, bool showCwd)
    {
        var pin = thread.Pinned ? "* " : "  ";
        var title = string.IsNullOrWhiteSpace(thread.Title) ? "(untitled)" : thread.Title;
        var line = pin + thread.Id + "  " + title;
        if (showCwd && !string.IsNullOrWhiteSpace(thread.Cwd))
        {
            line += "  " + thread.Cwd;
        }

        return line;
    }

    public static string? IdFromLabel(string label)
    {
        var text = (label ?? "").TrimStart('*', ' ');
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? null : parts[0];
    }

    private static bool Contains(string? value, string search) =>
        (value ?? "").Contains(search, StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
