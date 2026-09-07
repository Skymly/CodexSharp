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

public class SkillCatalogTests
{
    [Fact]
    public void Load_skill_returns_markdown()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(home, "skills", "demo");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# Demo\nDo the thing.\n");
        var call = new ToolCallRequest("1", "load_skill", """{"name":"demo"}""");
        var result = SkillCatalog.LoadSkill(call, home, home);
        Assert.False(result.IsError);
        Assert.Contains("Do the thing", result.Output);
    }

    [Fact]
    public void Project_local_skills_require_trust()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-cwd-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            var skillDir = Path.Combine(cwd, ".codexsharp", "skills", "secret");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# Secret\nDo not load until trusted.\n");
            var untrusted = SkillCatalog.Load(home, cwd);
            Assert.DoesNotContain(untrusted, s => s.Name == "secret");
            ProjectTrust.Trust(cwd);
            Assert.True(ProjectTrust.IsTrusted(cwd));
            var trusted = SkillCatalog.Load(home, cwd);
            Assert.Contains(trusted, s => s.Name == "secret");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public void Project_local_hooks_and_rules_require_trust()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-cwd-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            var hookDir = Path.Combine(cwd, ".codexsharp", "hooks");
            var ruleDir = Path.Combine(cwd, ".codexsharp", "rules");
            Directory.CreateDirectory(hookDir);
            Directory.CreateDirectory(ruleDir);
            File.WriteAllText(Path.Combine(hookDir, "stop.json"), """{"name":"proj-stop","event":"Stop","command":"echo project-hook"}""");
            File.WriteAllText(Path.Combine(ruleDir, "project.rules"), "prefix_rule(pattern=[\"rm\"], decision=\"forbidden\")\n");
            Assert.DoesNotContain(HookCatalog.List(home, cwd), h => h.Name == "proj-stop");
            Assert.Equal(ExecDecision.Allow, ExecPolicy.Evaluate("rm -rf /tmp/x", cwd));
            ProjectTrust.Trust(cwd);
            Assert.Contains(HookCatalog.List(home, cwd), h => h.Name == "proj-stop" && h.Source == "project");
            Assert.Equal(ExecDecision.Forbidden, ExecPolicy.Evaluate("rm -rf /tmp/x", cwd));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public void Project_local_config_requires_trust()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-cwd-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(Path.Combine(cwd, ".codexsharp"));
            File.WriteAllText(Path.Combine(home, "config.toml"), "model = \"user-model\"\n");
            File.WriteAllText(Path.Combine(cwd, ".codexsharp", "config.toml"), "model = \"project-model\"\n");
            Assert.Equal("user-model", ConfigService.Load(cwd).Model);
            ProjectTrust.Trust(cwd);
            Assert.Equal("project-model", ConfigService.Load(cwd).Model);
            Assert.Equal("gpt-4.1-mini", ConfigService.Load(cwd, ignoreUserConfig: true).Model);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class SkillsListProtocolTests
{
    [Fact]
    public async Task Lists_discovered_skills()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var skillDir = Path.Combine(home, "skills", "demo");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# Demo\nHi\n");
            await using var hosted = InProcessAppServer.Start();
            var client = hosted.Client;
            await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
            await client.NotifyAsync("initialized");
            var listed = await client.CallAsync("skills/list");
            Assert.Contains(listed.GetProperty("data").EnumerateArray(), e => e.GetProperty("name").GetString() == "demo");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class SkillExtraRootsTests
{
    [Fact]
    public async Task Extra_root_is_discovered()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var extra = Path.Combine(Path.GetTempPath(), "codexsharp-skills-" + Guid.NewGuid().ToString("N"), "bonus");
        Directory.CreateDirectory(extra);
        File.WriteAllText(Path.Combine(extra, "SKILL.md"), "# bonus\n");
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var client = hosted.Client;
            await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
            await client.NotifyAsync("initialized");
            await client.CallAsync("skills/extraRoots/set", new { extraRoots = new[] { extra } });
            var listed = await client.CallAsync("skills/list");
            Assert.Contains(listed.GetProperty("data").EnumerateArray(), e => e.GetProperty("name").GetString() == "bonus");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class SkillsConfigWriteTests
{
    [Fact]
    public async Task Disables_named_skill()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(home, "skills", "demo");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# Demo\n");
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var client = hosted.Client;
            await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
            await client.NotifyAsync("initialized");
            var listed = await client.CallAsync("skills/list");
            Assert.Contains(listed.GetProperty("data").EnumerateArray(), e => e.GetProperty("name").GetString() == "demo" && e.GetProperty("enabled").GetBoolean());
            var written = await client.CallAsync("skills/config/write", new { name = "demo", enabled = false });
            Assert.False(written.GetProperty("effectiveEnabled").GetBoolean());
            var after = await client.CallAsync("skills/list");
            Assert.Contains(after.GetProperty("data").EnumerateArray(), e => e.GetProperty("name").GetString() == "demo" && !e.GetProperty("enabled").GetBoolean());
            var cfg = ConfigService.Load(Path.GetTempPath());
            var beforeOff = File.ReadAllText(Path.Combine(home, "skills-config.json"));
            Assert.Contains("demo", beforeOff);
            var prompt = Prompt.build(cfg, [new HistoryMessage("user", "hi", "", "", "")]);
            Assert.DoesNotContain(prompt.Messages, m => m.Content.Contains("SKILL.md") && m.Content.Contains("demo"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class PluginListProtocolTests
{
    [Fact]
    public async Task Lists_plugin_directories()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var dir = Path.Combine(home, "plugins", "sample");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "plugin.json"), "{}");
            await using var hosted = InProcessAppServer.Start();
            var client = hosted.Client;
            await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
            await client.NotifyAsync("initialized");
            var listed = await client.CallAsync("plugin/list");
            Assert.Contains(listed.GetProperty("data").EnumerateArray(), e => e.GetProperty("name").GetString() == "sample");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class PluginMarketplaceProtocolTests
{
    [Fact]
    public async Task Adds_marketplace_installs_and_uninstalls_plugin()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var market = Path.Combine(Path.GetTempPath(), "codexsharp-market-" + Guid.NewGuid().ToString("N"), "demo-plugin");
        Directory.CreateDirectory(market);
        File.WriteAllText(Path.Combine(market, "plugin.json"), """{"name":"demo-plugin","description":"demo"}""");
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var client = hosted.Client;
            await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
            await client.NotifyAsync("initialized");

            var added = await client.CallAsync("marketplace/add", new { source = Path.GetDirectoryName(market), refName = "local-demo" });
            Assert.Equal("local-demo", added.GetProperty("marketplaceName").GetString());
            Assert.False(added.GetProperty("alreadyAdded").GetBoolean());

            var installed = await client.CallAsync("plugin/install", new { pluginName = "demo-plugin", marketplacePath = Path.GetDirectoryName(market) });
            Assert.Equal("ON_USE", installed.GetProperty("authPolicy").GetString());
            var listed = await client.CallAsync("plugin/list");
            Assert.Contains(listed.GetProperty("data").EnumerateArray(), e => e.GetProperty("name").GetString() == "demo-plugin");

            var read = await client.CallAsync("plugin/read", new { pluginName = "demo-plugin" });
            Assert.Equal("demo-plugin", read.GetProperty("plugin").GetProperty("name").GetString());

            await client.CallAsync("plugin/uninstall", new { pluginId = "demo-plugin" });
            var after = await client.CallAsync("plugin/list");
            Assert.DoesNotContain(after.GetProperty("data").EnumerateArray(), e => e.GetProperty("name").GetString() == "demo-plugin");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class PluginShareProtocolTests
{
    [Fact]
    public async Task Save_list_checkout_and_delete()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var plugin = Path.Combine(Path.GetTempPath(), "codexsharp-pshare-" + Guid.NewGuid().ToString("N"), "share-me");
        Directory.CreateDirectory(plugin);
        File.WriteAllText(Path.Combine(plugin, "plugin.json"), """{"name":"share-me","description":"d"}""");
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var saved = await session.SharePluginAsync(plugin, "PRIVATE");
            var rid = saved.GetProperty("remotePluginId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(rid));
            var listed = await session.ListPluginSharesAsync();
            Assert.Contains(listed.GetProperty("data").EnumerateArray(), e => e.GetProperty("remotePluginId").GetString() == rid);
            var checkedOut = await hosted.Client.CallAsync("plugin/share/checkout", new { remotePluginId = rid });
            Assert.Equal("share-me", checkedOut.GetProperty("plugin").GetProperty("name").GetString());
            await hosted.Client.CallAsync("plugin/share/delete", new { remotePluginId = rid });
            var after = await session.ListPluginSharesAsync();
            Assert.DoesNotContain(after.GetProperty("data").EnumerateArray(), e => e.GetProperty("remotePluginId").GetString() == rid);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class PluginReconcileProtocolTests
{
    [Fact]
    public async Task Reconcile_reports_local_install_then_is_quiet()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var plugin = Path.Combine(home, "plugins", "demo-skill");
        Directory.CreateDirectory(plugin);
        File.WriteAllText(Path.Combine(plugin, "SKILL.md"), "# Demo");
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var first = await hosted.Client.CallAsync("plugin/reconcile", new { reason = "test" });
            Assert.Contains(first.GetProperty("changedPlugins").EnumerateArray(), e => e.GetProperty("id").GetString() == "demo-skill" && e.GetProperty("hasSkills").GetBoolean());
            Assert.Empty(first.GetProperty("failedRemotePluginIds").EnumerateArray());
            var second = await hosted.Client.CallAsync("plugin/reconcile");
            Assert.Empty(second.GetProperty("changedPlugins").EnumerateArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Search_and_skill_read_are_local()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var plugin = Path.Combine(home, "plugins", "alpha");
        Directory.CreateDirectory(plugin);
        File.WriteAllText(Path.Combine(plugin, "plugin.json"), """{"name":"alpha","description":"needle-plugin"}""");
        File.WriteAllText(Path.Combine(plugin, "SKILL.md"), "skill body");
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var found = await hosted.Client.CallAsync("plugin/search", new { searchTerm = "needle" });
            Assert.Contains(found.GetProperty("data").EnumerateArray(), e => e.GetProperty("plugin").GetProperty("name").GetString() == "alpha");
            var skill = await hosted.Client.CallAsync("plugin/skill/read", new { pluginName = "alpha", skillName = "alpha" });
            Assert.Contains("skill body", skill.GetProperty("contents").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class PluginListAvailableTests
{
    [Fact]
    public async Task Plugin_list_includes_marketplace_available()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var market = Path.Combine(Path.GetTempPath(), "codexsharp-market-" + Guid.NewGuid().ToString("N"));
            var plugin = Path.Combine(market, "shop-plugin");
            Directory.CreateDirectory(plugin);
            File.WriteAllText(Path.Combine(plugin, "plugin.json"), """{"name":"shop-plugin"}""");
            MarketplaceStore.Add(market, "shop");

            await using var hosted = InProcessAppServer.Start();
            var client = hosted.Client;
            await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
            await client.NotifyAsync("initialized");
            var listed = await client.CallAsync("plugin/list");
            Assert.Contains(listed.GetProperty("available").EnumerateArray(), e => e.GetProperty("name").GetString() == "shop-plugin");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class MarketplaceGitCloneTests
{
    [Fact]
    public void Clones_git_remote_marketplace()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(Path.GetTempPath(), "codexsharp-git-market-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "hello.txt"), "market");
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            RunGit(src, "init");
            RunGit(src, "config user.email test@example.com");
            RunGit(src, "config user.name test");
            RunGit(src, "add hello.txt");
            RunGit(src, "commit -m init");
            var uri = new Uri(src + Path.DirectorySeparatorChar).AbsoluteUri;
            var (info, already) = MarketplaceStore.Add(uri, "cloned-market");
            Assert.False(already);
            Assert.Equal("cloned-market", info.Name);
            Assert.True(File.Exists(Path.Combine(info.InstalledRoot, "hello.txt")));
            Assert.True(Directory.Exists(Path.Combine(info.InstalledRoot, ".git")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
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
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("git missing");
        p.WaitForExit(15_000);
        Assert.Equal(0, p.ExitCode);
    }
}

public class MarketplaceFindPluginTests
{
    [Fact]
    public void FindPlugin_resolves_marketplace_name()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            CodexPaths.EnsureLayout();
            var pluginDir = Path.Combine(home, "srcplug");
            Directory.CreateDirectory(Path.Combine(pluginDir, ".codex-plugin"));
            File.WriteAllText(Path.Combine(pluginDir, ".codex-plugin", "plugin.json"), "{\"name\":\"sample\"}");
            File.WriteAllText(Path.Combine(pluginDir, "SKILL.md"), "# sample");
            var added = MarketplaceStore.Add(pluginDir, "debug");
            Assert.False(string.IsNullOrWhiteSpace(MarketplaceStore.FindPlugin("srcplug", "debug")));
            var installed = PluginCatalog.Install("srcplug", "debug");
            Assert.Equal("srcplug", installed.Name);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class PromptCatalogTests
{
    [Fact]
    public void Lists_markdown_prompts()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, "prompts"));
        File.WriteAllText(Path.Combine(home, "prompts", "review.md"), "Review this repo.");
        var listed = PromptCatalog.List(home, Path.GetTempPath());
        Assert.Contains(listed, p => p.Name == "review");
        Assert.Contains("Review this repo", PromptCatalog.Read("review", home, Path.GetTempPath()));
    }
}

public class PromptsListTests
{
    [Fact]
    public async Task Lists_and_reads_prompt_files()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var dir = Path.Combine(home, "prompts");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "review.md"), "please review this");
            await using var hosted = InProcessAppServer.Start();
            var client = hosted.Client;
            await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
            await client.NotifyAsync("initialized");
            var listed = await client.CallAsync("prompts/list");
            Assert.Contains(listed.GetProperty("data").EnumerateArray(), e => e.GetProperty("name").GetString() == "review");
            var read = await client.CallAsync("prompts/read", new { name = "review" });
            Assert.Contains("please review this", read.GetProperty("contents").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ProjectProtocolTests
{
    [Fact]
    public async Task Create_list_assign_and_idempotent_import()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var root = Path.GetTempPath();
            var key = Guid.NewGuid().ToString("N");
            var created = await hosted.Client.CallAsync("project/create", new { name = "Alpha", roots = new[] { new { path = root } }, idempotencyKey = key });
            var id = created.GetProperty("project").GetProperty("id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(id));
            var again = await hosted.Client.CallAsync("project/create", new { name = "Alpha-dup", roots = new[] { new { path = root } }, idempotencyKey = key });
            Assert.Equal(id, again.GetProperty("project").GetProperty("id").GetString());
            var listed = await hosted.Client.CallAsync("project/list");
            Assert.Contains(listed.GetProperty("data").EnumerateArray(), e => e.GetProperty("id").GetString() == id);
            await session.StartThreadAsync(root, "in-alpha", id);
            var inProject = await session.LoadThreadsAsync(projectId: id, filterProject: true);
            Assert.Contains(inProject, t => t.ProjectId == id);
            var unassigned = await session.LoadThreadsAsync(projectId: null, filterProject: true);
            Assert.DoesNotContain(unassigned, t => t.Id == session.ThreadId);
            await hosted.Client.CallAsync("project/delete", new { projectId = id });
            var after = await hosted.Client.CallAsync("project/list");
            Assert.DoesNotContain(after.GetProperty("data").EnumerateArray(), e => e.GetProperty("id").GetString() == id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Usage_and_model_provider_alias_and_mcp_oauth_stub()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var usage = await hosted.Client.CallAsync("account/usage/read");
        Assert.True(usage.TryGetProperty("summary", out _));
        var caps = await hosted.Client.CallAsync("modelProvider/capabilities/read");
        Assert.True(caps.GetProperty("namespaceTools").GetBoolean());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            hosted.Client.CallAsync("mcpServer/oauth/login", new { name = "demo" }));
        Assert.Contains("MCP OAuth", ex.Message);
    }
}

public class ProjectTrustRpcTests
{
    [Fact]
    public async Task App_server_persists_directory_trust()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-trust-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(cwd);
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("test", "Test");
            var before = await session.ReadProjectTrustAsync(cwd);
            Assert.False(before.GetProperty("trusted").GetBoolean());
            var after = await session.SetProjectTrustAsync(true, cwd);
            Assert.True(after.GetProperty("trusted").GetBoolean());
            Assert.True(ProjectTrust.IsTrusted(cwd));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

