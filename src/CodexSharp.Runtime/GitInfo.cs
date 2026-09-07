using System.Diagnostics;

namespace CodexSharp.Runtime;

/// Git metadata from vendor/codex/codex-rs/git-utils.
public sealed record GitInfo(string? Root, string? Branch, string? Commit, bool Dirty);

public static class GitProbe
{
    public static GitInfo? Collect(string cwd, int timeoutMs = 3000)
    {
        var root = FindRoot(cwd);
        if (root is null)
        {
            return null;
        }

        var branch = Git(root, "rev-parse --abbrev-ref HEAD", timeoutMs);
        var commit = Git(root, "rev-parse --short HEAD", timeoutMs);
        var status = Git(root, "status --porcelain", timeoutMs);
        return new GitInfo(root, branch, commit, !string.IsNullOrWhiteSpace(status));
    }

    public static string? FindRoot(string cwd)
    {
        var dir = new DirectoryInfo(cwd);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    public static string DiffToRemote(string cwd, int timeoutMs = 5000)
    {
        var root = FindRoot(cwd);
        if (root is null)
        {
            return "";
        }

        var branch = Git(root, "rev-parse --abbrev-ref HEAD", timeoutMs) ?? "HEAD";
        var against = Git(root, $"merge-base HEAD origin/{branch}", timeoutMs)
            ?? Git(root, "rev-parse origin/HEAD", timeoutMs)
            ?? "HEAD";
        var diff = Git(root, $"diff {against}", timeoutMs, allowDiffExit: true) ?? "";
        return Cap(diff);
    }

    public static string WorkingTreeDiff(string cwd, int timeoutMs = 15000)
    {
        var root = FindRoot(cwd);
        if (root is null)
        {
            return "";
        }

        var tracked = Git(root, "diff --no-ext-diff --submodule=short", timeoutMs, allowDiffExit: true) ?? "";
        var others = Git(root, "ls-files --others --exclude-standard", timeoutMs) ?? "";
        var sb = new System.Text.StringBuilder(tracked);
        var empty = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        foreach (var file in others.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var piece = Git(root, "diff --no-ext-diff --no-index -- " + Quote(empty) + " " + Quote(file), timeoutMs, allowDiffExit: true);
            if (!string.IsNullOrWhiteSpace(piece))
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(piece);
            }
        }

        return Cap(sb.ToString());
    }

    public sealed record GitWorktreeInfo(string Path, string Head, string Branch);

    public static IReadOnlyList<GitWorktreeInfo> ListWorktrees(string cwd, int timeoutMs = 5000)
    {
        var root = FindRoot(cwd);
        if (root is null) return [];
        var text = Git(root, "worktree list --porcelain", timeoutMs) ?? "";
        var list = new List<GitWorktreeInfo>();
        string? path = null; string head = ""; string branch = "";
        foreach (var line in text.Split('\n'))
        {
            var row = line.TrimEnd('\r');
            if (row.StartsWith("worktree ")) path = row[9..];
            else if (row.StartsWith("HEAD ")) head = row[5..];
            else if (row.StartsWith("branch ")) branch = row[7..];
            else if (row.Length == 0 && path is not null)
            {
                list.Add(new GitWorktreeInfo(path, head, branch));
                path = null; head = ""; branch = "";
            }
        }
        if (path is not null) list.Add(new GitWorktreeInfo(path, head, branch));
        return list;
    }

    public static string AddWorktree(string cwd, string path, string? branch = null, int timeoutMs = 15000)
    {
        var root = FindRoot(cwd) ?? throw new InvalidOperationException("not a git repository");
        var dest = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var args = string.IsNullOrWhiteSpace(branch)
            ? "worktree add " + Quote(dest)
            : "worktree add -b " + Quote(branch) + " " + Quote(dest);
        var err = GitErr(root, args, timeoutMs);
        if (err is not null) throw new InvalidOperationException(err);
        return dest;
    }

    public static bool RemoveWorktree(string cwd, string path, int timeoutMs = 15000)
    {
        var root = FindRoot(cwd);
        if (root is null) return false;
        var err = GitErr(root, "worktree remove --force " + Quote(path), timeoutMs);
        return err is null;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static string? GitErr(string cwd, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, Arguments = args };
            using var p = Process.Start(psi);
            if (p is null || !p.WaitForExit(timeoutMs)) { try { p?.Kill(true); } catch { } return "git timed out"; }
            return p.ExitCode == 0 ? null : (p.StandardError.ReadToEnd().Trim() is { Length: > 0 } e ? e : "git failed");
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static string? Run(string cwd, string args, int timeoutMs = 5000) => Git(cwd, args, timeoutMs);

    private static string Cap(string diff) =>
        diff.Length > 80_000 ? diff[..80_000] + Environment.NewLine + "..." : diff;

    private static string? Git(string cwd, string args, int timeoutMs, bool allowDiffExit = false)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = args,
            };
            using var p = Process.Start(psi);
            if (p is null || !p.WaitForExit(timeoutMs))
            {
                try { p?.Kill(true); } catch { /* ignore */ }
                return null;
            }

            return p.ExitCode == 0 || (allowDiffExit && p.ExitCode == 1)
                ? p.StandardOutput.ReadToEnd().TrimEnd()
                : null;
        }
        catch
        {
            return null;
        }
    }
}
