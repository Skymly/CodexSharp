namespace CodexSharp.Runtime;

public sealed record ManagedWorktree(string Root, string Cwd, string SourceRoot, string? HeadSha, string? Branch);

/// Managed git worktrees under ~/.codexsharp/worktrees, inspired by vendor/codex/codex-rs/worktree.
public static class WorktreeSession
{
    public static string RootDir => Path.Combine(CodexPaths.Home, "worktrees");

    public static ManagedWorktree Create(string sourceCwd, string? baseRev = null)
    {
        var sourceRoot = GitProbe.FindRoot(sourceCwd) ?? throw new InvalidOperationException("not a git repository");
        sourceCwd = Path.GetFullPath(sourceCwd);
        var relative = Path.GetRelativePath(sourceRoot, sourceCwd);
        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("cwd is outside the git repository");
        }

        CodexPaths.EnsureLayout();
        Directory.CreateDirectory(RootDir);
        var dest = Path.Combine(RootDir, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() + "-" + Guid.NewGuid().ToString("N")[..8]);
        var branch = "codexsharp-" + Path.GetFileName(dest);
        GitProbe.AddWorktree(sourceRoot, dest, branch);
        var nested = relative is "." or "" ? dest : Path.GetFullPath(Path.Combine(dest, relative));
        Directory.CreateDirectory(nested);
        var git = GitProbe.Collect(dest);
        return new ManagedWorktree(dest, nested, sourceRoot, git?.Commit, git?.Branch ?? branch);
    }

    public static bool Remove(ManagedWorktree tree) =>
        GitProbe.RemoveWorktree(tree.SourceRoot, tree.Root);
}
