using System.Text.Json;

namespace CodexSharp.Runtime;

public sealed record PluginInfo(string Name, string Path, string Kind, string? Description = null);

public sealed record PluginCapabilityFlags(bool HasMcps, bool HasApps, bool HasHooks, bool HasSkills);

public sealed record PluginReconcileChange(string Id, bool HasMcps, bool HasApps, bool HasHooks, bool HasSkills);

public sealed record PluginReconcileResult(
    IReadOnlyList<PluginReconcileChange> ChangedPlugins,
    IReadOnlyList<string> FailedRemotePluginIds,
    IReadOnlyList<string> FailedMaterializationRemotePluginIds);

public static class PluginCatalog
{
    public static string InstallRoot => Path.Combine(CodexPaths.Home, "plugins");

    public static IReadOnlyList<PluginInfo> List(string home, string cwd)
    {
        var roots = new[]
        {
            Path.Combine(home, "plugins"),
            Path.Combine(cwd, ".codexsharp", "plugins"),
            Path.Combine(cwd, ".codex", "plugins"),
        };

        var list = new List<PluginInfo>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                list.Add(ReadDir(dir));
            }
        }

        return list;
    }

    public static PluginInfo? Read(string pluginName, string home, string cwd, string? marketplacePath = null)
    {
        var installed = List(home, cwd).FirstOrDefault(p => string.Equals(p.Name, pluginName, StringComparison.OrdinalIgnoreCase));
        if (installed is not null)
        {
            return installed;
        }

        var found = MarketplaceStore.FindPlugin(pluginName, marketplacePath);
        return found is null ? null : ReadDir(found);
    }

    public static PluginInfo Install(string pluginName, string? marketplacePath = null)
    {
        if (string.IsNullOrWhiteSpace(pluginName) || pluginName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || pluginName.Contains(".."))
        {
            throw new ArgumentException("pluginName is invalid", nameof(pluginName));
        }

        var source = MarketplaceStore.FindPlugin(pluginName, marketplacePath);
        if (source is null)
        {
            throw new DirectoryNotFoundException($"Plugin not found: {pluginName}");
        }

        CodexPaths.EnsureLayout();
        Directory.CreateDirectory(InstallRoot);
        var dest = Path.Combine(InstallRoot, Sanitize(pluginName));
        if (Directory.Exists(dest))
        {
            Directory.Delete(dest, recursive: true);
        }

        CopyDirectory(source, dest);
        return ReadDir(dest);
    }

    public static bool Uninstall(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return false;
        }

        var dest = Path.Combine(InstallRoot, Sanitize(pluginId));
        if (!Directory.Exists(dest))
        {
            var match = List(CodexPaths.Home, Environment.CurrentDirectory)
                .FirstOrDefault(p => string.Equals(p.Name, pluginId, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return false;
            }

            dest = match.Path;
        }

        if (!IsInside(dest, InstallRoot))
        {
            return false;
        }

        Directory.Delete(dest, recursive: true);
        return true;
    }

    public static IReadOnlyList<PluginInfo> ListAvailable(string home, string cwd)
    {
        var installed = new HashSet<string>(List(home, cwd).Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var list = new List<PluginInfo>();
        foreach (var market in MarketplaceStore.List())
        {
            if (!Directory.Exists(market.InstalledRoot))
            {
                continue;
            }

            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(market.InstalledRoot); }
            catch { continue; }
            foreach (var dir in dirs)
            {
                if (!MarketplaceStore.LooksLikePlugin(dir)) continue;
                var info = ReadDir(dir);
                if (installed.Add(info.Name))
                {
                    list.Add(info);
                }
            }
        }

        return list;
    }

    public static IReadOnlyList<PluginInfo> Search(string home, string cwd, string searchTerm, int limit = 50)
    {
        var term = searchTerm ?? "";
        IEnumerable<PluginInfo> hits = List(home, cwd).Concat(ListAvailable(home, cwd));
        if (!string.IsNullOrWhiteSpace(term))
        {
            hits = hits.Where(p =>
                p.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (p.Description ?? "").Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        return hits.Take(Math.Max(1, limit)).ToList();
    }

    public static string? ReadSkill(string home, string cwd, string pluginId, string skillName)
    {
        var plugin = Read(pluginId, home, cwd);
        if (plugin is null)
        {
            return null;
        }

        var skill = string.IsNullOrWhiteSpace(skillName) ? plugin.Name : skillName;
        var candidates = new[]
        {
            Path.Combine(plugin.Path, "SKILL.md"),
            Path.Combine(plugin.Path, "skills", skill, "SKILL.md"),
            Path.Combine(plugin.Path, "skills", skill + ".md"),
            Path.Combine(plugin.Path, skill, "SKILL.md"),
        };
        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }

        return null;
    }

    public static PluginReconcileResult Reconcile(string home, string cwd, string? reason = null)
    {
        CodexPaths.EnsureLayout();
        var snapshotPath = Path.Combine(home, "plugin-reconcile.json");
        var previous = LoadSnapshot(snapshotPath);
        var current = new Dictionary<string, PluginCapabilityFlags>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in List(home, cwd))
        {
            current[plugin.Name] = FlagsOf(plugin.Path);
        }

        var changed = new List<PluginReconcileChange>();
        foreach (var (id, flags) in current)
        {
            if (!previous.TryGetValue(id, out var old) || old != flags)
            {
                changed.Add(new PluginReconcileChange(id, flags.HasMcps, flags.HasApps, flags.HasHooks, flags.HasSkills));
            }
        }

        foreach (var (id, oldFlags) in previous)
        {
            if (!current.ContainsKey(id))
            {
                changed.Add(new PluginReconcileChange(id, oldFlags.HasMcps, oldFlags.HasApps, oldFlags.HasHooks, oldFlags.HasSkills));
            }
        }

        SaveSnapshot(snapshotPath, current);
        _ = reason;
        return new PluginReconcileResult(changed, [], []);
    }

    public static PluginCapabilityFlags FlagsOf(string dir)
    {
        var hasSkills = File.Exists(Path.Combine(dir, "SKILL.md")) || Directory.Exists(Path.Combine(dir, "skills"));
        var hasMcps = Directory.Exists(Path.Combine(dir, "mcp"))
            || Directory.Exists(Path.Combine(dir, ".mcp"))
            || File.Exists(Path.Combine(dir, "mcp.json"));
        var hasHooks = Directory.Exists(Path.Combine(dir, "hooks"));
        var hasApps = Directory.Exists(Path.Combine(dir, "apps"));
        var json = Path.Combine(dir, "plugin.json");
        if (File.Exists(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                var root = doc.RootElement;
                if (root.TryGetProperty("mcpServers", out _) || root.TryGetProperty("mcp", out _))
                {
                    hasMcps = true;
                }

                if (root.TryGetProperty("hooks", out _))
                {
                    hasHooks = true;
                }

                if (root.TryGetProperty("apps", out _))
                {
                    hasApps = true;
                }

                if (root.TryGetProperty("skills", out _))
                {
                    hasSkills = true;
                }
            }
            catch
            {
                // keep filesystem flags
            }
        }

        return new PluginCapabilityFlags(hasMcps, hasApps, hasHooks, hasSkills);
    }

    private static Dictionary<string, PluginCapabilityFlags> LoadSnapshot(string path)
    {
        var map = new Dictionary<string, PluginCapabilityFlags>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return map;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Object)
            {
                return map;
            }

            foreach (var prop in plugins.EnumerateObject())
            {
                var el = prop.Value;
                map[prop.Name] = new PluginCapabilityFlags(
                    el.TryGetProperty("hasMcps", out var mcp) && mcp.ValueKind == JsonValueKind.True,
                    el.TryGetProperty("hasApps", out var apps) && apps.ValueKind == JsonValueKind.True,
                    el.TryGetProperty("hasHooks", out var hooks) && hooks.ValueKind == JsonValueKind.True,
                    el.TryGetProperty("hasSkills", out var skills) && skills.ValueKind == JsonValueKind.True);
            }
        }
        catch
        {
            return map;
        }

        return map;
    }

    private static void SaveSnapshot(string path, Dictionary<string, PluginCapabilityFlags> current)
    {
        var plugins = current.ToDictionary(
            kv => kv.Key,
            kv => new
            {
                hasMcps = kv.Value.HasMcps,
                hasApps = kv.Value.HasApps,
                hasHooks = kv.Value.HasHooks,
                hasSkills = kv.Value.HasSkills,
            });
        File.WriteAllText(path, JsonSerializer.Serialize(new { plugins }));
    }
    private static PluginInfo ReadDir(string dir)
    {
        var name = Path.GetFileName(dir);
        var json = Path.Combine(dir, "plugin.json");
        var skill = Path.Combine(dir, "SKILL.md");
        var kind = File.Exists(json) ? "plugin" : File.Exists(skill) ? "skill" : "folder";
        string? description = null;
        if (File.Exists(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                if (doc.RootElement.TryGetProperty("description", out var d))
                {
                    description = d.GetString();
                }

                if (doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                {
                    name = n.GetString() ?? name;
                }
            }
            catch
            {
                // keep folder name
            }
        }
        else if (File.Exists(skill))
        {
            description = string.Join('\n', File.ReadLines(skill).Take(8));
        }

        return new PluginInfo(name, dir, kind, description);
    }

    private static string Sanitize(string name)
    {
        var cleaned = new string(name.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.').ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "plugin" : cleaned;
    }

    private static bool IsInside(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var baseRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(baseRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }
}


