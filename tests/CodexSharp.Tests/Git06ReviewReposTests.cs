using CodexSharp.AppServer;
using CodexSharp.Client;
using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public class Git06ReviewReposTests
{
    [Fact]
    public void Defaults_to_primary_git_root_and_lists_labeled_extras()
    {
        var primary = InitGit("git06-p-", "primary.txt", "in-primary");
        var extra = InitGit("git06-e-", "extra.txt", "in-extra");
        var skip = Path.Combine(Path.GetTempPath(), "git06-skip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(skip);
        var repos = DesktopReviewRepos.FromProject(primary, [extra, skip]);
        Assert.Equal(2, repos.Count);
        Assert.True(repos[0].Primary);
        Assert.Equal(Path.GetFullPath(primary), Path.GetFullPath(repos[0].Cwd));
        Assert.False(repos[1].Primary);
        Assert.Equal(Path.GetFullPath(extra), Path.GetFullPath(repos[1].Cwd));
        Assert.Equal(Path.GetFullPath(primary), Path.GetFullPath(DesktopReviewRepos.DefaultCwd(repos)));
        Assert.Equal(repos[1].Label, DesktopReviewRepos.LabelFor(repos, extra));
    }

    [Fact]
    public void Switched_cwd_diff_is_the_extra_repo()
    {
        var primary = InitGit("git06-dp-", "p.txt", "primary-only");
        var extra = InitGit("git06-de-", "e.txt", "extra-only-marker");
        File.WriteAllText(Path.Combine(extra, "e.txt"), "extra-only-marker-changed" + Environment.NewLine);
        var primaryDiff = GitProbe.WorkingTreeDiff(primary);
        var extraDiff = GitProbe.WorkingTreeDiff(extra);
        Assert.DoesNotContain("extra-only-marker", primaryDiff);
        Assert.Contains("extra-only-marker", extraDiff);
    }

    [Fact]
    public void Missing_gh_is_notConfigured_reason()
    {
        var missing = GhPrComments.Probe(Path.GetTempPath(), (_, _) => null);
        var note = GhPrComments.HonestNote(missing);
        Assert.Contains("notConfigured", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gh is not installed", note, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(missing.Comments);
    }

    [Fact]
    public async Task Git_diff_protocol_uses_requested_cwd()
    {
        var extra = InitGit("git06-rpc-", "x.txt", "rpc-extra-marker");
        File.WriteAllText(Path.Combine(extra, "x.txt"), "rpc-extra-marker-changed" + Environment.NewLine);
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" }, capabilities = new { experimentalApi = true } });
        await client.NotifyAsync("initialized");
        var diff = await client.CallAsync("gitDiffToRemote", new { cwd = extra });
        var text = (diff.TryGetProperty("workingTreeDiff", out var w) ? w.GetString() : null)
            ?? (diff.TryGetProperty("diff", out var d) ? d.GetString() : "")
            ?? "";
        Assert.Contains("rpc-extra-marker", text);
    }

    private static string InitGit(string prefix, string file, string content)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        void Git(string args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            Assert.NotNull(p);
            Assert.True(p!.WaitForExit(20000));
            Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd());
        }

        Git("init");
        Git("config user.email t@t.t");
        Git("config user.name t");
        File.WriteAllText(Path.Combine(root, file), content + Environment.NewLine);
        Git("add " + file);
        Git("commit --no-verify -m init");
        return root;
    }
}
