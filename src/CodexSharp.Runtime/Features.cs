using Tomlyn;
using Tomlyn.Model;

namespace CodexSharp.Runtime;

/// Feature flags inspired by vendor/codex/codex-rs/features.
public sealed record FeatureSpec(string Key, bool DefaultEnabled, string Stage, string Description);

public static class FeatureFlags
{
    public static readonly FeatureSpec[] Specs =
    [
        new("multi_agent", false, "stable", "Spawn, wait, and message nested agents."),
        new("web_search", false, "stable", "Hosted web_search tool on the Responses API."),
        new("unified_exec", false, "stable", "Persistent PTY exec via process/spawn."),
        new("code_mode", false, "underDevelopment", "Code-mode collaboration overlay."),
        new("apply_patch", false, "stable", "Apply-patch tool for file edits."),
        new("view_image", false, "stable", "Attach and inspect local images."),
        new("sleep_tool", false, "stable", "clock/sleep tool for the agent loop."),
        new("collab", false, "stable", "Spawn and message nested agents in this process."),
        new("worktrees", false, "stable", "Managed git worktrees under ~/.codexsharp/worktrees."),
        new("memory_tool", false, "experimental", "File-backed memories listed by /memory."),
        new("request_permissions", false, "stable", "request_permissions tool for extra read roots."),
        new("code_mode_host", false, "underDevelopment", "Standalone JS code-mode host is notConfigured."),
        new("guardian", false, "underDevelopment", "Guardian review is notConfigured."),
        new("computer_use", false, "underDevelopment", "computer use is notConfigured."),
        new("browser_use", false, "underDevelopment", "browser use is notConfigured."),
        new("realtime", false, "underDevelopment", "WebRTC realtime is notConfigured."),
    ];

    public static readonly string[] Known = Specs.Select(s => s.Key).ToArray();

    public static IReadOnlyDictionary<string, bool> List()
    {
        var table = ReadFeaturesTable();
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Known)
        {
            map[name] = table.TryGetValue(name, out var value) && value is true;
            if (HonestStubs.IsLockedFeature(name))
            {
                map[name] = false;
            }
        }

        foreach (var (key, value) in table)
        {
            if (value is bool flag)
            {
                map[key] = HonestStubs.IsLockedFeature(key) ? false : flag;
            }
        }

        return map;
    }

    public static bool IsEnabled(string name) =>
        List().TryGetValue(name, out var on) && on;

    public static void Set(string name, bool enabled)
    {
        if (HonestStubs.IsLockedFeature(name))
        {
            enabled = false;
        }

        CodexPaths.EnsureLayout();
        var text = CodexPaths.ReadConfigText();
        if (!text.Contains("[features]", StringComparison.Ordinal))
        {
            text = text.TrimEnd() + Environment.NewLine + "[features]" + Environment.NewLine;
        }

        var line = $"{name} = {(enabled ? "true" : "false")}";
        var rx = new System.Text.RegularExpressions.Regex(
            $@"(?m)^[ \t]*{System.Text.RegularExpressions.Regex.Escape(name)}[ \t]*=.*$");
        if (rx.IsMatch(text))
        {
            text = rx.Replace(text, line, 1);
        }
        else
        {
            var idx = text.IndexOf("[features]", StringComparison.Ordinal);
            var insertAt = text.IndexOf('\n', idx);
            if (insertAt < 0)
            {
                text += Environment.NewLine + line + Environment.NewLine;
            }
            else
            {
                text = text.Insert(insertAt + 1, line + Environment.NewLine);
            }
        }

        CodexPaths.WriteConfigText(text);
    }

    private static TomlTable ReadFeaturesTable()
    {
        var raw = CodexPaths.ReadConfigText();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new TomlTable();
        }

        var model = Toml.ToModel(raw);
        return model.TryGetValue("features", out var features) && features is TomlTable table
            ? table
            : new TomlTable();
    }
}
