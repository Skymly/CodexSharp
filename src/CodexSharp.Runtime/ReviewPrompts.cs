using System.Text;
using System.Text.Json;

namespace CodexSharp.Runtime;

/// Review-turn prompts. Rules match Codex review/start targets without copying rubric.md.
public static class ReviewPrompts
{
    public static string FromParams(JsonElement paramsEl, string cwd)
    {
        JsonElement target = default;
        var hasTarget = paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("target", out target);
        var type = hasTarget && target.ValueKind == JsonValueKind.Object && target.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? "uncommittedChanges"
            : "uncommittedChanges";

        return type switch
        {
            "baseBranch" => BaseBranch(cwd, Str(target, "branch") ?? "main"),
            "commit" => Commit(cwd, Str(target, "sha") ?? "HEAD", Str(target, "title")),
            "custom" => Custom(Str(target, "instructions") ?? ""),
            _ => Uncommitted(cwd),
        };
    }

    public static string Uncommitted(string cwd)
    {
        var root = GitProbe.FindRoot(cwd);
        var status = root is null ? "" : GitProbe.Run(root, "status --porcelain") ?? "";
        var diff = root is null ? "" : GitProbe.Run(root, "diff HEAD") ?? "";
        var sb = new StringBuilder();
        sb.AppendLine("Review the working tree (staged, unstaged, untracked). Prioritize findings and suggest concrete fixes.");
        if (status.Length > 0) sb.AppendLine("git status --porcelain:").AppendLine(Trim(status, 8000));
        if (diff.Length > 0) sb.AppendLine("git diff HEAD:").AppendLine(Trim(diff, 24_000));
        if (status.Length == 0 && diff.Length == 0) sb.AppendLine("No local git diff was available. Inspect the workspace and review recent changes.");
        return sb.ToString();
    }

    public static string BaseBranch(string cwd, string branch)
    {
        var root = GitProbe.FindRoot(cwd);
        var mergeBase = root is null ? null : GitProbe.Run(root, $"merge-base HEAD {branch}");
        var diff = mergeBase is null || root is null ? "" : GitProbe.Run(root, $"diff {mergeBase}") ?? "";
        var sb = new StringBuilder();
        sb.AppendLine($"Review changes against base branch '{branch}'.");
        if (!string.IsNullOrWhiteSpace(mergeBase)) sb.AppendLine($"merge-base: {mergeBase}. Run mentally as git diff {mergeBase}.");
        if (diff.Length > 0) sb.AppendLine(Trim(diff, 24_000));
        else sb.AppendLine("Could not compute merge-base/diff; inspect the branch yourself.");
        sb.AppendLine("Provide prioritized, actionable findings.");
        return sb.ToString();
    }

    public static string Commit(string cwd, string sha, string? title)
    {
        var root = GitProbe.FindRoot(cwd);
        var show = root is null ? "" : GitProbe.Run(root, $"show --stat --patch {sha}") ?? "";
        var label = string.IsNullOrWhiteSpace(title) ? sha : $"{sha} (\"{title}\")";
        var sb = new StringBuilder();
        sb.AppendLine($"Review the code changes introduced by commit {label}.");
        if (show.Length > 0) sb.AppendLine(Trim(show, 24_000));
        sb.AppendLine("Provide prioritized, actionable findings.");
        return sb.ToString();
    }

    public static string Custom(string instructions) =>
        string.IsNullOrWhiteSpace(instructions)
            ? "Review the current workspace. Summarize risks and suggest concrete fixes."
            : instructions.Trim();

    public sealed record ReviewComment(string File, int? Start, int? End, string Title, string Body);
    public sealed record GitPrDraft(string Title, string Body, bool Draft);

    public static IReadOnlyList<ReviewComment> ExtractComments(string? text)
    {
        var list = new List<ReviewComment>();
        foreach (var dir in AssistantDirectives.ParseAll(text))
        {
            if (!dir.Name.Equals("code-comment", StringComparison.OrdinalIgnoreCase)) continue;
            dir.Attributes.TryGetValue("file", out var file);
            file ??= dir.Attributes.GetValueOrDefault("path") ?? "";
            if (string.IsNullOrWhiteSpace(file)) continue;
            int? start = null, end = null;
            if (dir.Attributes.TryGetValue("start", out var s) && int.TryParse(s, out var sv)) start = sv;
            if (dir.Attributes.TryGetValue("end", out var e) && int.TryParse(e, out var ev)) end = ev;
            if (start is null && dir.Attributes.TryGetValue("line", out var ln) && int.TryParse(ln, out var line)) start = line;
            dir.Attributes.TryGetValue("title", out var title);
            dir.Attributes.TryGetValue("body", out var body);
            list.Add(new ReviewComment(file, start, end, title ?? "", body ?? ""));
        }
        return list;
    }

    public static GitPrDraft? ExtractGitPr(string? text)
    {
        foreach (var dir in AssistantDirectives.ParseAll(text))
        {
            if (!dir.Name.Equals("git-create-pr", StringComparison.OrdinalIgnoreCase)) continue;
            dir.Attributes.TryGetValue("title", out var title);
            dir.Attributes.TryGetValue("body", out var body);
            var draft = dir.Attributes.TryGetValue("isDraft", out var d) && d.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(title)) continue;
            return new GitPrDraft(title, body ?? "", draft);
        }
        return null;
    }

    public static string? ExtractProposedPlan(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        const string open = "<proposed_plan>";
        const string close = "</proposed_plan>";
        var start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return null;
        start += open.Length;
        var end = text.IndexOf(close, start, StringComparison.Ordinal);
        if (end < 0) return text[start..].Trim();
        return text[start..end].Trim();
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string Trim(string text, int n) =>
        text.Length <= n ? text : text[..n] + "\n...";
}
