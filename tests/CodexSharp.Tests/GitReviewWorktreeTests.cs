using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using CodexSharp.AppServer;
using CodexSharp.Client;
using CodexSharp.Core;
using CodexSharp.Protocol;
using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public class GitProbeTests
{
    [Fact]
    public void Finds_repo_root_when_present()
    {
        var cwd = Path.GetFullPath(".");
        var root = GitProbe.FindRoot(cwd);
        if (root is null)
        {
            return;
        }
        Assert.True(Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git")));
    }

    [Fact]
    public void WorkingTreeDiff_includes_tracked_and_untracked()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-git-" + Guid.NewGuid().ToString("N"));
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
        File.WriteAllText(Path.Combine(root, "a.txt"), "old" + Environment.NewLine);
        Git("add a.txt");
        Git("commit --no-verify -m init");
        File.WriteAllText(Path.Combine(root, "a.txt"), "new-tracked" + Environment.NewLine);
        File.WriteAllText(Path.Combine(root, "u.txt"), "untracked-marker" + Environment.NewLine);
        var diff = GitProbe.WorkingTreeDiff(root);
        Assert.Contains("new-tracked", diff);
        Assert.Contains("untracked-marker", diff);
    }
}

public class GitDiffProtocolTests
{
    [Fact]
    public async Task Git_diff_endpoint_returns_object()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        await client.NotifyAsync("initialized");
        var diff = await client.CallAsync("gitDiffToRemote", new { cwd = Path.GetTempPath() });
        Assert.Equal(JsonValueKind.Object, diff.ValueKind);
    }
}

public class ReviewPromptTests
{
    [Fact]
    public void Extracts_proposed_plan_block()
    {
        var text = "intro\n<proposed_plan>\n# Title\nDo X\n</proposed_plan>\nbye";
        Assert.Equal("# Title\nDo X", ReviewPrompts.ExtractProposedPlan(text));
        Assert.Null(ReviewPrompts.ExtractProposedPlan("no plan here"));
    }

    [Fact]
    public void Custom_and_uncommitted_prompts_are_honest()
    {
        Assert.Contains("only this", ReviewPrompts.Custom("only this"));
        var dir = Path.Combine(Path.GetTempPath(), "codexsharp-norepo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var prompt = ReviewPrompts.Uncommitted(dir);
        Assert.Contains("working tree", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No local git diff", prompt);
    }
}

public class FileCitationTests
{
    [Fact]
    public void Parses_codex_file_citation_and_markdown_links()
    {
        var dirs = AssistantDirectives.ParseAll("""::codex-file-citation{path="src/Foo.cs" line=12}""");
        Assert.Equal("codex-file-citation", dirs[0].Name);
        Assert.Equal("src/Foo.cs", dirs[0].Attributes["path"]);
        Assert.Equal("12", dirs[0].Attributes["line"]);

        var cites = FileCitations.Extract("see [Foo](src/Foo.cs#L12) and ::codex-file-citation{path=\"Bar.fs\" line=3}");
        Assert.Contains(cites, c => c.Path == "src/Foo.cs" && c.Line == 12);
        Assert.Contains(cites, c => c.Path == "Bar.fs" && c.Line == 3);

        var blocks = Markdown.Parse("""::codex-file-citation{path="a.cs" line=1}""");
        Assert.Contains(blocks, b => b is MdFileCite f && f.Path == "a.cs" && f.Line == 1);
    }
}

public class ReviewDirectiveTests
{
    [Fact]
    public void Extracts_code_comments_and_git_pr()
    {
        var comments = ReviewPrompts.ExtractComments("""::code-comment{file="src/A.cs" start=10 end=12 body="n+1"}""");
        Assert.Equal("src/A.cs", comments[0].File);
        Assert.Equal(10, comments[0].Start);
        Assert.Equal("n+1", comments[0].Body);

        var pr = ReviewPrompts.ExtractGitPr("""::git-create-pr{title="Fix n+1" body="details" isDraft=true}""");
        Assert.NotNull(pr);
        Assert.Equal("Fix n+1", pr!.Title);
        Assert.True(pr.Draft);
    }
}

public class GitWorktreeTests
{
    [Fact]
    public void Lists_main_worktree_of_a_repo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codexsharp-wt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        RunGit(dir, "init");
        File.WriteAllText(Path.Combine(dir, "README.md"), "wt");
        RunGit(dir, "add README.md");
        RunGit(dir, "config user.email test@example.com");
        RunGit(dir, "config user.name test");
        RunGit(dir, "commit -m init");
        var trees = GitProbe.ListWorktrees(dir);
        Assert.Contains(trees, t => t.Path.Equals(dir, StringComparison.OrdinalIgnoreCase) || t.Path.Replace('\\','/').EndsWith(Path.GetFileName(dir), StringComparison.OrdinalIgnoreCase));
    }

    private static void RunGit(string cwd, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = cwd, Arguments = args, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit(10000);
        Assert.Equal(0, p.ExitCode);
    }
}

public class WorktreeSessionTests
{
    [Fact]
    public void Creates_and_removes_managed_worktree()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(Path.GetTempPath(), "codexsharp-repo-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(repo);
            File.WriteAllText(Path.Combine(repo, "README.md"), "hi");
            RunGit(repo, "init");
            RunGit(repo, "config user.email t@t");
            RunGit(repo, "config user.name t");
            RunGit(repo, "add README.md");
            RunGit(repo, "commit -m init");
            var tree = WorktreeSession.Create(repo);
            Assert.True(Directory.Exists(tree.Root));
            Assert.True(File.Exists(Path.Combine(tree.Cwd, "README.md")));
            Assert.True(WorktreeSession.Remove(tree));
            Assert.False(Directory.Exists(tree.Root));
            Assert.True(SharedCli.Parse(["--worktree", "hi"]).Worktree);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
            try { Directory.Delete(repo, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task App_server_starts_managed_worktree_thread()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(Path.GetTempPath(), "codexsharp-repo-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(repo);
            File.WriteAllText(Path.Combine(repo, "README.md"), "hi");
            RunGit(repo, "init");
            RunGit(repo, "config user.email t@t");
            RunGit(repo, "config user.name t");
            RunGit(repo, "add README.md");
            RunGit(repo, "commit -m init");
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("test", "Test");
            var started = await session.StartWorktreeAsync(repo);
            Assert.Equal("notConfigured", started.GetProperty("cloudWorktree").GetString());
            Assert.True(Directory.Exists(started.GetProperty("cwd").GetString()));
            var threadId = started.GetProperty("thread").GetProperty("id").GetString();
            Assert.StartsWith("thr_", threadId);
            Assert.Equal(threadId, session.ThreadId);
            var bound = started.GetProperty("thread").GetProperty("worktree");
            Assert.Equal(started.GetProperty("path").GetString(), bound.GetProperty("path").GetString());
            Assert.Equal("notConfigured", bound.GetProperty("cloudWorktree").GetString());
            Assert.Equal("notConfigured", started.GetProperty("thread").GetProperty("cloudWorktree").GetString());
            var path = started.GetProperty("path").GetString();
            Assert.True(Directory.Exists(path));
            await session.DeleteThreadAsync(threadId!);
            Assert.False(Directory.Exists(path));
            Assert.Null(WorktreeBindings.Find(threadId!));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
            try { Directory.Delete(repo, true); } catch { /* ignore */ }
        }
    }

    private static void RunGit(string cwd, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit(15000);
        Assert.Equal(0, p.ExitCode);
    }
}

public class ExternalAgentImportTests
{
    [Fact]
    public async Task Detects_claude_md_and_imports_agents_md()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codexsharp-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "CLAUDE.md"), "# Claude");
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var detected = await hosted.Client.CallAsync("externalAgentConfig/detect", new { cwds = new[] { dir } });
            Assert.Contains(detected.GetProperty("items").EnumerateArray(), e => e.GetProperty("itemType").GetString() == "AGENTS_MD");
            await hosted.Client.CallAsync("externalAgentConfig/import", new { migrationItems = detected.GetProperty("items") });
            Assert.True(File.Exists(Path.Combine(dir, "AGENTS.md")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class AgentsMarkdownTests
{
    [Fact]
    public void Writes_once()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codexsharp-agents-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Assert.True(AgentsMarkdown.WriteIfMissing(dir));
        Assert.True(File.Exists(Path.Combine(dir, "AGENTS.md")));
        Assert.False(AgentsMarkdown.WriteIfMissing(dir));
    }
}

public class AgentsMarkdownInitTests
{
    [Fact]
    public void WriteIfMissing_creates_once_at_git_root()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-init-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            Assert.True(AgentsMarkdown.WriteIfMissing(cwd));
            Assert.True(File.Exists(Path.Combine(cwd, "AGENTS.md")));
            Assert.False(AgentsMarkdown.WriteIfMissing(cwd));
        }
        finally
        {
            try { Directory.Delete(cwd, true); } catch { }
        }
    }
}

public class AgentsOverviewTests
{
    [Fact]
    public void Render_lists_this_folder_sessions_and_subagents()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-cwd-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(cwd);
            var session = CodexSession.Start(ConfigService.Load(cwd), "overview");
            var text = AgentsOverview.Render(cwd, [new SubagentSnapshot("child-1", false, null)]);
            Assert.Contains("sessions (this folder)", text);
            Assert.Contains(session.Thread.Id, text);
            Assert.Contains("child-1", text);
            Assert.Contains("running", text);
            Assert.Contains("/agents THREAD_ID", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

