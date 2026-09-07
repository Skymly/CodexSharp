using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace CodexSharp.Runtime;

/// Project directory trust from vendor/codex config `projects."<path>".trust_level`.
/// Untrusted trees skip project-local skills/hooks/exec policies.
public static class ProjectTrust
{
    public static string TrustTarget(string cwd)
    {
        cwd = Path.GetFullPath(string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : cwd);
        return GitProbe.FindRoot(cwd) ?? cwd;
    }

    public static string Key(string path)
    {
        var full = Path.GetFullPath(path);
        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }

    public static bool IsTrusted(string? cwd = null)
    {
        cwd = Path.GetFullPath(string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : cwd);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Key(cwd), Key(TrustTarget(cwd)) };
        for (var dir = new DirectoryInfo(cwd); dir is not null; dir = dir.Parent)
        {
            keys.Add(Key(dir.FullName));
        }

        var text = CodexPaths.ReadConfigText();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            var table = Toml.ToModel(text);
            if (!table.TryGetValue("projects", out var projectsObj) || projectsObj is not TomlTable projects)
            {
                return false;
            }

            foreach (var (name, value) in projects)
            {
                if (value is not TomlTable rec)
                {
                    continue;
                }

                var level = rec.TryGetValue("trust_level", out var raw) ? raw?.ToString() : null;
                if (!string.Equals(level, "trusted", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var named = Key(name);
                if (keys.Contains(named) || keys.Contains(name))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    public static void Trust(string? cwd = null)
    {
        cwd = Path.GetFullPath(string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : cwd);
        var target = TrustTarget(cwd);
        var key = Key(target);
        CodexPaths.EnsureLayout();
        var path = CodexPaths.ConfigFile;
        var text = CodexPaths.ReadConfigText();
        var header = "[projects.\"" + EscapeKey(key) + "\"]";
        const string assignment = "trust_level = \"trusted\"";
        var idx = IndexOfHeader(text, header);
        if (idx < 0)
        {
            text = text.TrimEnd() + Environment.NewLine + Environment.NewLine + header + Environment.NewLine + assignment + Environment.NewLine;
        }
        else
        {
            var next = NextTable(text, idx + 1);
            var sectionEnd = next < 0 ? text.Length : next;
            var section = text[idx..sectionEnd];
            if (Regex.IsMatch(section, @"^trust_level\s*=", RegexOptions.Multiline))
            {
                section = Regex.Replace(section, @"^trust_level\s*=.*$", assignment, RegexOptions.Multiline);
                text = text[..idx] + section + text[sectionEnd..];
            }
            else
            {
                var nl = text.IndexOf('\n', idx);
                var at = nl < 0 ? text.Length : nl + 1;
                text = text.Insert(at, assignment + Environment.NewLine);
            }
        }

        CodexPaths.WriteConfigText(text);
    }

    public static IReadOnlyList<string> ProjectSkillRoots(string cwd) =>
        ProjectDirs(cwd, "skills", includeBare: true);

    public static IReadOnlyList<string> ProjectHookDirs(string cwd) =>
        ProjectDirs(cwd, "hooks", includeBare: false);

    public static IReadOnlyList<string> ProjectRuleDirs(string cwd) =>
        ProjectDirs(cwd, "rules", includeBare: false);

    public static IReadOnlyList<string> ProjectConfigFiles(string cwd)
    {
        if (!IsTrusted(cwd))
        {
            return [];
        }

        cwd = Path.GetFullPath(cwd);
        var target = TrustTarget(cwd);
        var files = new List<string>
        {
            Path.Combine(target, ".codex", "config.toml"),
            Path.Combine(target, ".codexsharp", "config.toml"),
            Path.Combine(cwd, ".codex", "config.toml"),
            Path.Combine(cwd, ".codexsharp", "config.toml"),
        };
        return files.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToArray();
    }

    private static IReadOnlyList<string> ProjectDirs(string cwd, string leaf, bool includeBare)
    {
        if (!IsTrusted(cwd))
        {
            return [];
        }

        cwd = Path.GetFullPath(cwd);
        var target = TrustTarget(cwd);
        var dirs = new List<string>
        {
            Path.Combine(cwd, ".codexsharp", leaf),
            Path.Combine(cwd, ".codex", leaf),
            Path.Combine(target, ".codexsharp", leaf),
            Path.Combine(target, ".codex", leaf),
        };
        if (includeBare)
        {
            dirs.Add(Path.Combine(cwd, leaf));
            dirs.Add(Path.Combine(target, leaf));
        }

        return dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string EscapeKey(string key) =>
        key.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static int IndexOfHeader(string text, string header) =>
        text.IndexOf(header, StringComparison.OrdinalIgnoreCase);

    private static int NextTable(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] != '[')
            {
                continue;
            }

            if (i == 0 || text[i - 1] == '\n')
            {
                return i;
            }
        }

        return -1;
    }
}
