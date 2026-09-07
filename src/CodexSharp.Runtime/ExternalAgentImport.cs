using System.Text.Json;

namespace CodexSharp.Runtime;

public sealed record ExternalMigrationItem(string ItemType, string Description, string? Cwd);

public static class ExternalAgentImport
{
    public static IReadOnlyList<ExternalMigrationItem> Detect(IEnumerable<string>? cwds, bool includeHome)
    {
        var items = new List<ExternalMigrationItem>();
        var roots = (cwds ?? []).Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d)).Select(Path.GetFullPath).ToList();
        if (roots.Count == 0)
        {
            roots.Add(Environment.CurrentDirectory);
        }

        if (includeHome)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home) && Directory.Exists(home))
            {
                roots.Add(home);
            }
        }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(Path.Combine(root, "CLAUDE.md")) || File.Exists(Path.Combine(root, "AGENTS.md")))
            {
                items.Add(new ExternalMigrationItem("AGENTS_MD", "Project instructions (AGENTS.md/CLAUDE.md)", root));
            }

            if (Directory.Exists(Path.Combine(root, ".claude", "skills")) || Directory.Exists(Path.Combine(root, "skills")))
            {
                items.Add(new ExternalMigrationItem("SKILLS", "Skill folders", root));
            }
        }

        return items;
    }

    public static IReadOnlyList<ExternalMigrationItem> Import(IEnumerable<ExternalMigrationItem> items)
    {
        var done = new List<ExternalMigrationItem>();
        foreach (var item in items)
        {
            if (item.ItemType == "AGENTS_MD" && !string.IsNullOrWhiteSpace(item.Cwd))
            {
                var dest = Path.Combine(item.Cwd, "AGENTS.md");
                var src = Path.Combine(item.Cwd, "CLAUDE.md");
                if (!File.Exists(dest) && File.Exists(src))
                {
                    File.Copy(src, dest);
                    done.Add(item);
                }
                else if (File.Exists(dest))
                {
                    done.Add(item);
                }
            }
            else if (item.ItemType == "SKILLS" && !string.IsNullOrWhiteSpace(item.Cwd))
            {
                var from = Directory.Exists(Path.Combine(item.Cwd, ".claude", "skills"))
                    ? Path.Combine(item.Cwd, ".claude", "skills")
                    : Path.Combine(item.Cwd, "skills");
                var to = Path.Combine(CodexPaths.Home, "skills");
                if (Directory.Exists(from))
                {
                    CodexPaths.EnsureLayout();
                    Directory.CreateDirectory(to);
                    foreach (var dir in Directory.GetDirectories(from))
                    {
                        var name = Path.GetFileName(dir);
                        var dest = Path.Combine(to, name);
                        if (!Directory.Exists(dest))
                        {
                            CopyDir(dir, dest);
                        }
                    }
                    done.Add(item);
                }
            }
        }

        RecordHistory(done);
        return done;
    }

    public static IReadOnlyList<object> ReadHistories()
    {
        var path = Path.Combine(CodexPaths.Home, "import-history.json");
        try
        {
            if (!File.Exists(path)) return [];
            return JsonSerializer.Deserialize<List<Dictionary<string, string>>>(File.ReadAllText(path))?.Cast<object>().ToList() ?? [];
        }
        catch { return []; }
    }

    private static void RecordHistory(IReadOnlyList<ExternalMigrationItem> items)
    {
        if (items.Count == 0) return;
        CodexPaths.EnsureLayout();
        var path = Path.Combine(CodexPaths.Home, "import-history.json");
        var rows = new List<Dictionary<string, string>>();
        try
        {
            if (File.Exists(path))
                rows = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(File.ReadAllText(path)) ?? [];
        }
        catch { /* ignore */ }

        rows.Add(new Dictionary<string, string>
        {
            ["at"] = DateTimeOffset.UtcNow.ToString("o"),
            ["count"] = items.Count.ToString(),
        });
        File.WriteAllText(path, JsonSerializer.Serialize(rows));
    }

    private static void CopyDir(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDir(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }
}
