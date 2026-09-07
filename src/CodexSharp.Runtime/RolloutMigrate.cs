namespace CodexSharp.Runtime;

public sealed record RolloutThreadReport(
    string Id,
    string Path,
    int Lines,
    int InvalidLines,
    long Bytes,
    bool Archived);

public sealed record RolloutMigrationReport(
    bool Applied,
    string Storage,
    bool SqliteThreadHistory,
    int Threads,
    int Lines,
    int InvalidLines,
    long Bytes,
    IReadOnlyList<RolloutThreadReport> Items,
    string Note);

/// Inspect / rewrite JSONL sessions. Official migrate-rollouts targets sqlite thread history;
/// CodexSharp already stores JSONL under ~/.codexsharp/sessions.
public static class RolloutMigrate
{
    public static RolloutMigrationReport Inspect(IEnumerable<string>? threadIds = null, bool apply = false)
    {
        CodexPaths.EnsureLayout();
        var filter = threadIds?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = new List<RolloutThreadReport>();
        Scan(CodexPaths.SessionsDir, archived: false, filter, apply, items);
        Scan(CodexPaths.ArchivedDir, archived: true, filter, apply, items);
        var note = apply
            ? "Rewrote JSONL and dropped unparseable lines. CodexSharp does not use a sqlite thread-history database."
            : "Dry run. Pass --apply to rewrite JSONL. CodexSharp does not use a sqlite thread-history database.";
        return new RolloutMigrationReport(
            apply,
            "jsonl",
            false,
            items.Count,
            items.Sum(i => i.Lines),
            items.Sum(i => i.InvalidLines),
            items.Sum(i => i.Bytes),
            items,
            note);
    }

    private static void Scan(string dir, bool archived, HashSet<string>? filter, bool apply, List<RolloutThreadReport> items)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var path in Directory.EnumerateFiles(dir, "*.jsonl").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (filter is { Count: > 0 } && !filter.Contains(id)) continue;
            var raw = File.ReadAllLines(path);
            var valid = new List<string>();
            var invalid = 0;
            foreach (var line in raw)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(line);
                    if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                    {
                        invalid++;
                        continue;
                    }
                    valid.Add(line);
                }
                catch
                {
                    invalid++;
                }
            }

            if (apply && invalid > 0)
            {
                File.Copy(path, path + ".bak", overwrite: true);
                File.WriteAllLines(path, valid);
                invalid = 0;
            }

            var bytes = apply ? new FileInfo(path).Length : raw.Sum(l => l.Length + 1);
            items.Add(new RolloutThreadReport(id, path, valid.Count, invalid, bytes, archived));
        }
    }
}
