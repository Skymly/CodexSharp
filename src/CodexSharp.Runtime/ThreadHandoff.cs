namespace CodexSharp.Runtime;

public sealed record HandoffResult(
    string Mode,
    string Cwd,
    string? Branch,
    string CloudHandoff = "notConfigured",
    string CrossHost = "notConfigured");

/// Local <-> managed worktree handoff (GIT-07). Cloud/cross-host stay notConfigured.
public static class ThreadHandoff
{
    public static bool IsCloudOrRemote(string? destination, string? hostId) =>
        (!string.IsNullOrWhiteSpace(destination) && destination.Equals("cloud", StringComparison.OrdinalIgnoreCase))
        || !string.IsNullOrWhiteSpace(hostId);

    public static HandoffResult Toggle(string threadId, string currentCwd)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("threadId is required", nameof(threadId));
        }

        currentCwd = Path.GetFullPath(string.IsNullOrWhiteSpace(currentCwd) ? Environment.CurrentDirectory : currentCwd);
        var binding = WorktreeBindings.Find(threadId);
        if (binding is null)
        {
            var tree = WorktreeSession.Create(currentCwd);
            WorktreeBindings.Bind(threadId, tree);
            return new HandoffResult("worktree", tree.Cwd, tree.Branch);
        }

        var work = Path.GetFullPath(binding.Cwd);
        var local = Path.GetFullPath(binding.SourceRoot);
        if (IsUnder(currentCwd, work))
        {
            return new HandoffResult("local", local, GitProbe.Collect(local)?.Branch);
        }

        return new HandoffResult("worktree", work, binding.Branch);
    }

    public static bool SameBranchCheckedOutTwice(string sourceRoot)
    {
        var trees = GitProbe.ListWorktrees(sourceRoot);
        var branches = trees
            .Select(t => t.Branch)
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .GroupBy(b => b!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1);
        return branches.Any();
    }

    public static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var baseDir = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.Equals(baseDir, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
