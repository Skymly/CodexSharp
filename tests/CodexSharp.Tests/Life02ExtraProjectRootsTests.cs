using CodexSharp.AppServer;
using CodexSharp.Client;
using CodexSharp.Core;
using CodexSharp.Protocol;
using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public class Life02ExtraProjectRootsTests
{
    [Fact]
    public async Task Project_update_persists_extra_roots_primary_first()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var primary = Path.Combine(Path.GetTempPath(), "codexsharp-life02-p-" + Guid.NewGuid().ToString("N"));
        var extra = Path.Combine(Path.GetTempPath(), "codexsharp-life02-e-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(primary);
            Directory.CreateDirectory(extra);
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var created = await session.CreateProjectAsync("Multi", primary);
            var id = created.GetProperty("project").GetProperty("id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(id));
            var updated = await session.UpdateProjectAsync(id!, [primary, extra]);
            var roots = updated.GetProperty("project").GetProperty("roots").EnumerateArray()
                .Select(r => Path.GetFullPath(r.GetProperty("path").GetString()!))
                .ToList();
            Assert.Equal(Path.GetFullPath(primary), roots[0]);
            Assert.Equal(Path.GetFullPath(extra), roots[1]);
            Assert.Equal(Path.GetFullPath(primary), Path.GetFullPath(ProjectStore.PrimaryRoot(id!)!));
            Assert.Equal(Path.GetFullPath(extra), Path.GetFullPath(Assert.Single(ProjectStore.ExtraRoots(id!))));

            var listed = await session.LoadProjectsAsync();
            var row = Assert.Single(listed, p => p.Id == id);
            Assert.Equal(Path.GetFullPath(primary), Path.GetFullPath(row.Root));
            Assert.Equal(Path.GetFullPath(extra), Path.GetFullPath(Assert.Single(row.ExtraRoots)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public void Extra_root_is_searchable_and_not_writable()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-life02-cwd-" + Guid.NewGuid().ToString("N"));
        var extra = Path.Combine(Path.GetTempPath(), "codexsharp-life02-x-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(cwd);
            Directory.CreateDirectory(extra);
            File.WriteAllText(Path.Combine(cwd, "primary.txt"), "in-primary");
            File.WriteAllText(Path.Combine(extra, "life02-needle.txt"), "in-extra");
            var cfg = ConfigService.Load(cwd, sandboxOverride: "workspace-write");
            cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", cwd, false, 8);
            var sandbox = new WorkspaceSandbox(cfg, [extra]);
            sandbox.EnsureReadable(Path.Combine(extra, "life02-needle.txt"));
            Assert.Throws<InvalidOperationException>(() => sandbox.EnsureWritable(Path.Combine(extra, "out.txt")));
            var names = FileSearch.SearchNames(new ToolCallRequest("c1", "file_search", "{\"query\":\"life02-needle\"}"), sandbox);
            Assert.False(names.IsError);
            Assert.Contains("life02-needle.txt", names.Output);
            var grep = FileSearch.Grep(new ToolCallRequest("c2", "grep_files", "{\"pattern\":\"in-extra\"}"), sandbox);
            Assert.Contains("in-extra", grep.Output);
            Assert.DoesNotContain(Path.GetFullPath(extra), SandboxRoots.List().Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetFullPath(extra), SessionSandbox.List().Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            SessionSandbox.Reset();
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public void Extra_agents_and_skills_are_not_auto_discovered()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var primary = Path.Combine(Path.GetTempPath(), "codexsharp-life02-ag-p-" + Guid.NewGuid().ToString("N"));
        var extra = Path.Combine(Path.GetTempPath(), "codexsharp-life02-ag-e-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(primary);
            Directory.CreateDirectory(Path.Combine(extra, "bonus"));
            File.WriteAllText(Path.Combine(primary, "AGENTS.md"), "PRIMARY_AGENTS_MARK\n");
            File.WriteAllText(Path.Combine(extra, "AGENTS.md"), "EXTRA_AGENTS_MARK\n");
            File.WriteAllText(Path.Combine(extra, "bonus", "SKILL.md"), "# extra-skill\n");
            var cfg = ConfigService.Load(primary, sandboxOverride: "workspace-write");
            cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", primary, false, 8);
            var blob = string.Join("\n", Prompt.build(cfg, [new HistoryMessage("user", "hi", "", "", "")]).Messages.Select(m => m.Content));
            Assert.Contains("PRIMARY_AGENTS_MARK", blob);
            Assert.DoesNotContain("EXTRA_AGENTS_MARK", blob);
            var skills = SkillCatalog.Load(home, primary);
            Assert.DoesNotContain(skills, s => s.Name.Equals("bonus", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Thread_start_composes_extra_read_without_host_global_roots()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var primary = Path.Combine(Path.GetTempPath(), "codexsharp-life02-t-p-" + Guid.NewGuid().ToString("N"));
        var extra = Path.Combine(Path.GetTempPath(), "codexsharp-life02-t-e-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(primary);
            Directory.CreateDirectory(extra);
            File.WriteAllText(Path.Combine(extra, "life02-thread-needle.txt"), "from-extra");
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var created = await session.CreateProjectAsync("ThreadMulti", primary);
            var id = created.GetProperty("project").GetProperty("id").GetString();
            await session.UpdateProjectAsync(id!, [primary, extra]);
            await session.StartThreadAsync(cwd: Path.GetTempPath(), title: "life02", projectId: id);
            var listed = await session.ListExtraReadRootsAsync();
            var hostRoots = listed.GetProperty("data").EnumerateArray()
                .Select(e => Path.GetFullPath(e.GetString() ?? e.GetProperty("path").GetString() ?? ""))
                .Where(p => p.Length > 0)
                .ToList();
            Assert.DoesNotContain(Path.GetFullPath(extra), hostRoots, StringComparer.OrdinalIgnoreCase);

            var cfg = ConfigService.Load(primary, sandboxOverride: "workspace-write");
            cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", primary, false, 8);
            var sandbox = new WorkspaceSandbox(cfg, ProjectStore.ExtraRoots(id!));
            var names = FileSearch.SearchNames(new ToolCallRequest("c1", "file_search", "{\"query\":\"life02-thread-needle\"}"), sandbox);
            Assert.Contains("life02-thread-needle.txt", names.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}
