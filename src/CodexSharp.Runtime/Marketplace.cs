using System.Diagnostics;
using System.Text.Json;

namespace CodexSharp.Runtime;

public sealed record MarketplaceInfo(string Name, string Source, string InstalledRoot);

/// Local plugin marketplaces inspired by vendor/codex marketplace/* app-server methods.
public static class MarketplaceStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private static string FilePath => Path.Combine(CodexPaths.Home, "marketplaces.json");
    public static string Root => Path.Combine(CodexPaths.Home, "marketplaces");

    public static IReadOnlyList<MarketplaceInfo> List()
    {
        lock (Gate)
        {
            return Load().Select(ToInfo).ToList();
        }
    }

    public static (MarketplaceInfo Info, bool AlreadyAdded) Add(string source, string? refName = null)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("source is required", nameof(source));
        }

        lock (Gate)
        {
            var list = Load();
            var name = string.IsNullOrWhiteSpace(refName) ? SuggestName(source) : Sanitize(refName);
            var existing = list.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return (ToInfo(existing), true);
            }

            var dest = Path.Combine(Root, name);
            CodexPaths.EnsureLayout();
            Directory.CreateDirectory(Root);
            if (LooksLikeGitRemote(source))
            {
                if (Directory.Exists(dest))
                {
                    Directory.Delete(dest, recursive: true);
                }

                if (!GitClone(source, dest))
                {
                    throw new InvalidOperationException("git clone failed: " + source);
                }
            }
            else if (Directory.Exists(source))
            {
                CopyDirectory(Path.GetFullPath(source), dest);
            }
            else
            {
                Directory.CreateDirectory(dest);
                File.WriteAllText(Path.Combine(dest, "marketplace.json"), JsonSerializer.Serialize(new { name, source }));
            }

            var record = new Record { Name = name, Source = source, InstalledRoot = dest };
            list.Add(record);
            Save(list);
            return (ToInfo(record), false);
        }
    }

    public static bool Remove(string name)
    {
        lock (Gate)
        {
            var list = Load();
            var record = list.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            if (record is null)
            {
                return false;
            }

            list.Remove(record);
            Save(list);
            var dest = record.InstalledRoot;
            if (!string.IsNullOrWhiteSpace(dest) && Directory.Exists(dest) && IsInside(dest, Root))
            {
                Directory.Delete(dest, recursive: true);
            }

            return true;
        }
    }

    public static MarketplaceInfo? Upgrade(string name)
    {
        lock (Gate)
        {
            var list = Load();
            var record = list.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            if (record is null)
            {
                return null;
            }

            var dest = Path.Combine(Root, record.Name);
            if (Directory.Exists(Path.Combine(dest, ".git")))
            {
                GitPull(dest);
                record.InstalledRoot = dest;
                Save(list);
            }
            else if (LooksLikeGitRemote(record.Source))
            {
                if (Directory.Exists(dest))
                {
                    Directory.Delete(dest, recursive: true);
                }

                if (GitClone(record.Source, dest))
                {
                    record.InstalledRoot = dest;
                    Save(list);
                }
            }
            else if (Directory.Exists(record.Source))
            {
                if (Directory.Exists(dest))
                {
                    Directory.Delete(dest, recursive: true);
                }

                CopyDirectory(Path.GetFullPath(record.Source), dest);
                record.InstalledRoot = dest;
                Save(list);
            }

            return ToInfo(record);
        }
    }

    public static string? FindPlugin(string pluginName, string? marketplacePath = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(marketplacePath))
        {
            var named = List().FirstOrDefault(m => m.Name.Equals(marketplacePath, StringComparison.OrdinalIgnoreCase));
            var root = named is null ? marketplacePath : named.InstalledRoot;
            candidates.Add(root);
            candidates.Add(Path.Combine(root, pluginName));
        }

        foreach (var market in List())
        {
            candidates.Add(Path.Combine(market.InstalledRoot, pluginName));
        }

        foreach (var path in candidates)
        {
            if (LooksLikePlugin(path))
            {
                return Path.GetFullPath(path);
            }
        }

        return null;
    }

    private static bool LooksLikeGitRemote(string source)
    {
        source = source.Trim();
        return source.Contains("://", StringComparison.Ordinal)
            || source.StartsWith("git@", StringComparison.OrdinalIgnoreCase);
    }

    private static bool GitClone(string source, string dest)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("clone");
        psi.ArgumentList.Add("--depth");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(source);
        psi.ArgumentList.Add(dest);
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null || !proc.WaitForExit(60_000))
            {
                try { proc?.Kill(true); } catch { /* ignore */ }
                return false;
            }

            return proc.ExitCode == 0 && Directory.Exists(dest);
        }
        catch
        {
            return false;
        }
    }

    private static void GitPull(string dest)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dest,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("pull");
        psi.ArgumentList.Add("--ff-only");
        try
        {
            using var proc = Process.Start(psi);
            proc?.WaitForExit(60_000);
        }
        catch
        {
            // leave installed copy as-is
        }
    }

    public static bool LooksLikePlugin(string path) =>
        Directory.Exists(path)
        && (File.Exists(Path.Combine(path, "plugin.json"))
            || File.Exists(Path.Combine(path, "SKILL.md"))
            || Directory.EnumerateFileSystemEntries(path).Any());

    private static MarketplaceInfo ToInfo(Record r) =>
        new(r.Name, r.Source, r.InstalledRoot);

    private static List<Record> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<Record>>(File.ReadAllText(FilePath), Json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void Save(List<Record> list)
    {
        CodexPaths.EnsureLayout();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(list, Json));
    }

    private static string SuggestName(string source)
    {
        if (Directory.Exists(source) || File.Exists(source))
        {
            return Sanitize(Path.GetFileName(Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        }

        try
        {
            var uri = new Uri(source);
            var last = uri.Segments.LastOrDefault()?.Trim('/') ?? "marketplace";
            last = last.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? last[..^4] : last;
            return Sanitize(string.IsNullOrWhiteSpace(last) ? "marketplace" : last);
        }
        catch
        {
            return Sanitize(source);
        }
    }

    private static string Sanitize(string name)
    {
        var cleaned = new string(name.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.').ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "marketplace" : cleaned;
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

    private sealed class Record
    {
        public string Name { get; set; } = "";
        public string Source { get; set; } = "";
        public string InstalledRoot { get; set; } = "";
    }
}
