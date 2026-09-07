using System.Text.Json;
using CodexSharp.Protocol;

namespace CodexSharp.Runtime;

public sealed record SkillInfo(string Name, string Path, string Description);

/// Skill discovery from vendor/codex/codex-rs/skills.
public static class SkillCatalog
{
    public static IReadOnlyList<SkillInfo> Load(string home, string cwd)
    {
        var roots = new List<string>
        {
            Path.Combine(home, "skills"),
        };
        roots.AddRange(ProjectTrust.ProjectSkillRoots(cwd));
        roots.AddRange(SkillExtraRoots.Get());

        var list = new List<SkillInfo>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(Path.GetDirectoryName(file)) ?? "skill";
                var head = string.Join('\n', File.ReadLines(file).Take(8));
                list.Add(new SkillInfo(name, file, head));
            }
        }

        return list;
    }

    public static ToolCallResult LoadSkill(ToolCallRequest call, string home, string cwd)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var path = doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() : null;
        var skills = Load(home, cwd);
        var match = skills.FirstOrDefault(s =>
            (!string.IsNullOrEmpty(path) && s.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            || s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return new ToolCallResult(call.Id, call.Name, $"Unknown skill '{name}'. Available: {string.Join(", ", skills.Select(s => s.Name))}", true);
        }

        if (!SkillConfig.IsEnabled(match.Name, match.Path))
        {
            return new ToolCallResult(call.Id, call.Name, $"Skill '{match.Name}' is disabled.", true);
        }

        return new ToolCallResult(call.Id, call.Name, File.ReadAllText(match.Path), false);
    }
}

public static class AuthService
{
    public static void SaveApiKey(string apiKey)
    {
        CodexPaths.EnsureLayout();
        File.WriteAllText(CodexPaths.AuthFile, JsonSerializer.Serialize(new { api_key = apiKey }));
    }

    public static void SaveChatgptTokens(string accessToken, string refreshToken, string idToken, string? accountId = null)
    {
        CodexPaths.EnsureLayout();
        var payload = new Dictionary<string, object?>
        {
            ["auth_mode"] = "chatgpt",
            ["tokens"] = new Dictionary<string, object?>
            {
                ["access_token"] = accessToken,
                ["refresh_token"] = refreshToken,
                ["id_token"] = idToken,
                ["account_id"] = accountId,
            },
            ["last_refresh"] = DateTimeOffset.UtcNow.ToString("o"),
        };
        File.WriteAllText(CodexPaths.AuthFile, JsonSerializer.Serialize(payload));
    }

    public static (string? Access, string? Refresh, string? IdToken, string? AccountId) ReadTokens()
    {
        try
        {
            if (!File.Exists(CodexPaths.AuthFile)) return default;
            using var doc = JsonDocument.Parse(File.ReadAllText(CodexPaths.AuthFile));
            if (!doc.RootElement.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
                return default;
            string? S(string n) => tokens.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return (S("access_token"), S("refresh_token"), S("id_token"), S("account_id"));
        }
        catch { return default; }
    }

    public static string? AuthMode()
    {
        try
        {
            if (!File.Exists(CodexPaths.AuthFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(CodexPaths.AuthFile));
            if (doc.RootElement.TryGetProperty("auth_mode", out var mode) && mode.ValueKind == JsonValueKind.String)
            {
                return mode.GetString();
            }
            if (doc.RootElement.TryGetProperty("api_key", out var key) && key.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(key.GetString()))
            {
                return "apiKey";
            }
        }
        catch { /* ignore */ }
        return null;
    }


}

public static class SkillExtraRoots
{
    public static string FilePath => Path.Combine(CodexPaths.Home, "skills-extra-roots.json");

    public static IReadOnlyList<string> Get()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<string[]>(File.ReadAllText(FilePath)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void Set(IEnumerable<string> roots)
    {
        CodexPaths.EnsureLayout();
        var clean = roots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(clean));
    }
}


public static class SkillConfig
{
    private static string FilePath => Path.Combine(CodexPaths.Home, "skills-config.json");

    public static bool IsEnabled(string name, string path)
    {
        var disabled = Load();
        return !disabled.Contains(name) && !disabled.Contains(path);
    }

    public static bool Set(string? name, string? path, bool enabled)
    {
        var disabled = Load();
        var keys = new[] { name, path }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToArray();
        if (keys.Length == 0)
        {
            return false;
        }

        foreach (var key in keys)
        {
            if (enabled)
            {
                disabled.Remove(key);
            }
            else
            {
                disabled.Add(key);
            }
        }

        CodexPaths.EnsureLayout();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(disabled.ToArray()));
        return IsEnabled(name ?? "", path ?? "");
    }

    private static HashSet<string> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var items = JsonSerializer.Deserialize<string[]>(File.ReadAllText(FilePath)) ?? [];
            return new HashSet<string>(items, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
