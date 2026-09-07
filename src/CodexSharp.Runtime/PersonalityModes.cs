using CodexSharp.Protocol;
namespace CodexSharp.Runtime;

/// Personality overlay. Original wording; not a Codex template dump.
public static class PersonalityModes
{
    public static string Overlay(string? personality) =>
        (personality ?? "").Trim().ToLowerInvariant() switch
        {
            "friendly" => """
<personality>friendly</personality>
Be warm and collaborative. Prefer plain language. Celebrate small wins without being sycophantic. Still be precise about code and risk.
""",
            "pragmatic" => """
<personality>pragmatic</personality>
Be terse and direct. Skip preamble. Prefer diffs and commands over essays. Call out blockers immediately.
""",
            "professional" => """
<personality>professional</personality>
Be calm, precise, and formal. Use complete sentences. Avoid slang. Document assumptions.
""",
            _ => "",
        };

    public static string Normalize(string? personality) =>
        (personality ?? "").Trim().ToLowerInvariant() switch
        {
            "friendly" => "friendly",
            "pragmatic" => "pragmatic",
            "professional" => "professional",
            "off" or "default" or "none" or "" => "",
            _ => (personality ?? "").Trim().ToLowerInvariant(),
        };

    public static string Strip(string? developer)
    {
        var text = developer ?? "";
        foreach (var name in new[] { "friendly", "pragmatic", "professional" })
        {
            var overlay = Overlay(name);
            if (overlay.Length > 0)
            {
                text = text.Replace(overlay, "", StringComparison.Ordinal);
            }
        }

        return text;
    }

    public static string MergeDeveloper(string? developer, string? personality)
    {
        var text = Strip(developer);
        var overlay = Overlay(personality);
        if (overlay.Length == 0) return text;
        foreach (var mode in new[] { "plan", "pair", "default" })
        {
            var collab = CollaborationModes.Overlay(mode);
            if (collab.Length > 0 && text.StartsWith(collab, StringComparison.Ordinal))
            {
                return collab + overlay + text[collab.Length..];
            }
        }

        return overlay + text;
    }
}

public static class TuiStatus
{
    public static string Line(CodexSession session) =>
        Line(session.Config, session.Thread.Id, session.Thread.Title);

    public static string Line(CodexConfig config, string threadId, string? title = null)
    {
        var keys = (ConfigService.Peek("tui_statusline") ?? "model,sandbox,cwd").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return string.Join("  ", keys.Select(k => Format(config, threadId, title, k)).Where(s => s.Length > 0));
    }

    public static string Title(CodexSession session) =>
        Title(session.Config, session.Thread.Id, session.Thread.Title);

    public static string Title(CodexConfig config, string threadId, string? title = null)
    {
        var keys = (ConfigService.Peek("tui_title") ?? "thread,model").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var parts = keys.Select(k => Format(config, threadId, title, k)).Where(s => s.Length > 0);
        return "CodexSharp — " + string.Join(" — ", parts);
    }

    private static string Format(CodexConfig config, string threadId, string? title, string key) =>
        key.ToLowerInvariant() switch
        {
            "model" => config.Model,
            "sandbox" => config.SandboxMode,
            "cwd" => config.Cwd,
            "thread" => string.IsNullOrWhiteSpace(title) ? threadId : title,
            "approval" => config.ApprovalPolicy,
            "provider" => config.Provider.Id,
            "effort" or "reasoning" => config.ReasoningEffort,
            _ => "",
        };
}
