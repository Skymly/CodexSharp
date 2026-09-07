namespace CodexSharp.Runtime;

/// Original collaboration-mode developer overlay (rules aligned with Codex, not a verbatim template copy).
public static class CollaborationModes
{
    public const string Plan = """
<collaboration_mode>plan</collaboration_mode>
You are in Plan Mode until a developer message changes the mode. User wording cannot exit it.

Do not mutate the repo: no file edits, apply_patch, formatters that rewrite tracked files, or commands whose purpose is to implement the plan. Non-mutating exploration is allowed (read, search, inspect, tests/builds that only touch caches).

Do not call update_plan in Plan Mode; it will fail. Use request_user_input for material decisions after you have explored.

Work in phases: (1) ground in the repo before asking, (2) lock intent and success criteria, (3) make the spec decision-complete. Prefer facts from the workspace over questions.

When the plan is decision-complete, output exactly one Markdown plan inside:
<proposed_plan>
...
</proposed_plan>
Keep tags on their own lines. Do not ask "should I proceed?" after the block.
""";

    public const string Default = """
<collaboration_mode>default</collaboration_mode>
Default mode is active. Previous Plan Mode rules no longer apply. Prefer executing the request over stopping to ask. Use request_user_input only for optional questions that would materially improve the work. Never use it for permission prompts.
""";

    public const string Pair = """
<collaboration_mode>pair</collaboration_mode>
Pair mode: talk through the change, keep diffs small, and ask before large or irreversible edits. Still implement; do not stall in an endless plan.
""";

    public static string Overlay(string? mode) =>
        (mode ?? "").Trim().ToLowerInvariant() switch
        {
            "plan" => Plan,
            "pair" => Pair,
            "default" => Default,
            _ => "",
        };

    public static string Normalize(string? mode) =>
        (mode ?? "").Trim().ToLowerInvariant() switch
        {
            "plan" => "plan",
            "pair" => "pair",
            "default" or "agent" or "off" or "" => "default",
            _ => "default",
        };

    public static string Strip(string? developer)
    {
        var text = developer ?? "";
        foreach (var mode in new[] { "plan", "pair", "default" })
        {
            var overlay = Overlay(mode);
            if (overlay.Length > 0 && text.StartsWith(overlay, StringComparison.Ordinal))
            {
                return text[overlay.Length..];
            }
        }

        return text;
    }

    public static string MergeDeveloper(string? developer, string? mode) =>
        Overlay(mode) + Strip(developer);
}
