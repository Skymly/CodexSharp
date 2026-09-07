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

public sealed class ScriptedModelClient : IModelClient
{
    private readonly Queue<ModelStreamEvent[]> _turns;

    public ScriptedModelClient(params ModelStreamEvent[][] turns)
    {
        _turns = new Queue<ModelStreamEvent[]>(turns);
    }

    public Task StreamAsync(ModelRequest request, Action<ModelStreamEvent> onEvent, CancellationToken ct)
    {
        var batch = _turns.Count > 0 ? _turns.Dequeue() : [ModelStreamEvent.NewOutputTextDelta("done"), ModelStreamEvent.NewStreamFinished("stop")];
        foreach (var evt in batch)
        {
            onEvent(evt);
        }

        return Task.CompletedTask;
    }
}

public sealed class RecordingSink : IEventSink
{
    public List<AgentEvent> Events { get; } = [];
    public void Emit(AgentEvent evt) => Events.Add(evt);
}

public class ApplyPatchTests
{
    [Fact]
    public void Adds_and_updates_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "hello.txt"), "hello world\n");
        var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
        cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, cfg.ApprovalPolicy, "workspace-write", cfg.DeveloperInstructions, cfg.UserInstructions, root, false, 8);
        var sandbox = new WorkspaceSandbox(cfg);

        var add = ApplyPatchEngine.Apply("""
            *** Begin Patch
            *** Add File: notes.md
            +alpha
            +beta
            *** End Patch
            """, sandbox);
        Assert.True(add.Ok, add.Output);
        Assert.Equal("alpha" + Environment.NewLine + "beta" + Environment.NewLine, File.ReadAllText(Path.Combine(root, "notes.md")));

        var update = ApplyPatchEngine.Apply("""
            *** Begin Patch
            *** Update File: hello.txt
            @@
            -hello world
            +hello CodexSharp
            *** End Patch
            """, sandbox);
        Assert.True(update.Ok, update.Output);
        Assert.Contains("hello CodexSharp", File.ReadAllText(Path.Combine(root, "hello.txt")));
        var move = ApplyPatchEngine.Apply("""
            *** Begin Patch
            *** Update File: notes.md
            *** Move to: renamed.md
            @@
            -alpha
            +alpha
            *** End Patch
            """, sandbox);
        Assert.True(move.Ok, move.Output);
        Assert.True(File.Exists(Path.Combine(root, "renamed.md")));
        Assert.False(File.Exists(Path.Combine(root, "notes.md")));

    }
}

public class AgentLoopTests
{
    [Fact]
    public async Task Runs_tool_then_final_message()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "README.md"), "hello\n");
        var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
        cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", root, false, 8);

        var model = new ScriptedModelClient(
            [
                ModelStreamEvent.NewToolCallReady(new ToolCallRequest("call_1", "read_file", """{"path":"README.md"}""")),
                ModelStreamEvent.NewStreamFinished("tool"),
            ],
            [
                ModelStreamEvent.NewOutputTextDelta("README says hello."),
                ModelStreamEvent.NewStreamFinished("stop"),
            ]);

        var sink = new RecordingSink();
        var deps = new AgentDeps(cfg, model, new BuiltinToolExecutor(cfg), new AutoApprover(true), sink, [], new NoopHookHost(), new NoopUserInputHost(), new NoopSteerHost());
        var history = new List<HistoryMessage>();
        var turnId = await AgentLoop.runTurn(deps, "thr_test", history, "Read README.md", CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(turnId));
        Assert.Contains(sink.Events, e => e is AgentEvent.TurnCompleted);
        Assert.Contains(sink.Events, e => e is AgentEvent.ItemCompleted completed && completed.Item.Kind == "agent_message" && completed.Item.Text.Contains("hello"));
        Assert.Contains(history, m => m.Role == "tool" && m.Content.Contains("hello"));
    }
}

public class PromptTests
{
    [Fact]
    public void Includes_environment_and_permissions()
    {
        var cwd = Path.GetTempPath();
        var cfg = ConfigService.Load(cwd);
        var request = Prompt.build(cfg, [new HistoryMessage("user", "hi", "", "", "")]);
        Assert.Contains("CodexSharp", request.Instructions);
        Assert.Contains(request.Messages, m => m.Content.Contains("<cwd>"));
        Assert.Contains(request.Messages, m => m.Content.Contains("permissions_instructions"));
        Assert.Contains(request.Tools, t => t.Name == "shell");
        Assert.Contains(request.Tools, t => t.Name == "apply_patch");
    }
}

public class AppServerTests
{
    [Fact]
    public async Task Initialize_then_start_thread()
    {
        var incoming = new StringReader("""
            {"method":"initialize","id":0,"params":{"clientInfo":{"name":"test","title":"Test","version":"0"}}}
            {"method":"initialized","params":{}}
            {"method":"thread/start","id":1,"params":{"cwd":"."}}

            """);
        var outgoing = new StringWriter();
        var host = new AppServerHost(incoming, outgoing);
        await host.RunAsync();
        var text = outgoing.ToString();
        Assert.Contains("userAgent", text);
        Assert.Contains("thread/started", text);
        Assert.Contains("\"id\":1", text);
    }
}

public class ConfigTests
{
    [Fact]
    public void Loads_default_config_into_home()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var cfg = ConfigService.Load(Path.GetTempPath());
            Assert.Equal(home, cfg.Home);
            Assert.True(File.Exists(Path.Combine(home, "config.toml")));
            Assert.False(string.IsNullOrWhiteSpace(cfg.Model));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class CodexSqEqTests
{
    [Fact]
    public async Task Submit_user_input_completes_turn()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
        cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", root, false, 8);
        var model = new ScriptedModelClient(
            [
                ModelStreamEvent.NewOutputTextDelta("ok"),
                ModelStreamEvent.NewStreamFinished("stop"),
            ]);
        var sink = new RecordingSink();
        var deps = new AgentDeps(cfg, model, new BuiltinToolExecutor(cfg), new AutoApprover(true), sink, [], new NoopHookHost(), new NoopUserInputHost(), new NoopSteerHost());
        var codex = CodexModule.start(deps);
        var turn = await codex.Submit(Op.NewUserInput("hello"), CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(turn));
        Assert.Contains(sink.Events, e => e is AgentEvent.TurnCompleted);
    }
}

public class InProcessAppServerTests
{
    [Fact]
    public async Task Diagnostics_doctor_returns_checks()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("test", "Test");
            var report = await session.RunDoctorAsync(Path.GetTempPath());
            Assert.True(report.TryGetProperty("status", out var status));
            Assert.True(report.TryGetProperty("checks", out var checks) && checks.ValueKind == JsonValueKind.Array);
            Assert.True(checks.GetArrayLength() > 0);
            Assert.Contains(checks.EnumerateArray(), c => c.GetProperty("group").GetString() == "config" || c.GetProperty("id").GetString()!.StartsWith("config"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Mcp_add_and_marketplace_list_via_app_server()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("test", "Test");
            await session.AddMcpStdioAsync("docs", ["npx", "-y", "mcp-server"]);
            var listed = await session.ListMcpAsync();
            Assert.Contains(listed.GetProperty("data").EnumerateArray(), s => s.GetProperty("name").GetString() == "docs");
            var markets = await session.ListMarketplacesAsync();
            Assert.True(markets.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Remote_control_status_via_app_server()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("test", "Test");
            var enabled = await session.EnableRemoteControlAsync();
            Assert.True(enabled.TryGetProperty("status", out var status));
            var read = await session.ReadRemoteControlStatusAsync();
            Assert.Equal(status.GetString(), read.GetProperty("status").GetString());
            var disabled = await session.DisableRemoteControlAsync();
            Assert.Equal("disabled", disabled.GetProperty("status").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Exec_policy_check_and_add_via_app_server()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("test", "Test");
            await session.AddExecPolicyRuleAsync("forbidden", ["rm"]);
            var check = await session.CheckExecPolicyAsync("rm -rf /tmp", Path.GetTempPath());
            Assert.Equal("forbidden", check.GetProperty("decision").GetString());
            var listed = await session.ListExecPolicyAsync(Path.GetTempPath());
            Assert.Contains(listed.GetProperty("data").EnumerateArray(), r => r.GetProperty("decision").GetString() == "forbidden");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Agents_markdown_write_via_app_server()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-cwd-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(cwd);
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("test", "Test");
            var first = await session.WriteAgentsMarkdownAsync(cwd);
            Assert.True(first.GetProperty("created").GetBoolean());
            Assert.True(File.Exists(Path.Combine(cwd, "AGENTS.md")));
            var second = await session.WriteAgentsMarkdownAsync(cwd);
            Assert.False(second.GetProperty("created").GetBoolean());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Last_patch_apply_without_patch_is_honest()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("test", "Test");
            var threadId = await session.StartThreadAsync(Path.GetTempPath(), "patchless");
            var result = await session.ApplyLastPatchAsync(threadId);
            Assert.False(result.GetProperty("ok").GetBoolean());
            Assert.Contains("no apply_patch", result.GetProperty("output").GetString());
            Assert.Equal("notConfigured", result.GetProperty("cloudApply").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Extra_read_root_via_app_server()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var extra = Path.Combine(Path.GetTempPath(), "codexsharp-root-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-cwd-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(extra);
            Directory.CreateDirectory(cwd);
            File.WriteAllText(Path.Combine(extra, "outside.txt"), "hello-extra");
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("test", "Test");
            await session.StartThreadAsync(cwd);
            var live = new WorkspaceSandbox(ConfigService.Load(cwd, sandboxOverride: "workspace-write"));
            Assert.Throws<InvalidOperationException>(() => live.EnsureReadable(Path.Combine(extra, "outside.txt")));
            var added = await session.AddExtraReadRootAsync(extra);
            Assert.Equal(Path.GetFullPath(extra), added.GetProperty("path").GetString());
            Assert.True(added.GetProperty("live").GetBoolean());
            var listed = await session.ListExtraReadRootsAsync();
            Assert.Contains(listed.GetProperty("data").EnumerateArray(), r => string.Equals(r.GetString(), Path.GetFullPath(extra), StringComparison.OrdinalIgnoreCase));
            var cfg = await session.ReadConfigAsync();
            Assert.True(cfg.TryGetProperty("extraReadRoots", out var roots) && roots.ValueKind == JsonValueKind.Array);
            live.EnsureReadable(Path.Combine(extra, "outside.txt"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Desktop_shaped_client_initializes_and_starts_thread()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync("test", "Test");
        var threadId = await session.StartThreadAsync(Path.GetTempPath(), "test thread");
        Assert.False(string.IsNullOrWhiteSpace(threadId));
        Assert.StartsWith("thr_", threadId);
    }

    [Fact]
    public async Task Thread_start_source_exec_hides_from_resume_picker()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-exec-cwd-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(cwd);
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("test", "Test");
            var threadId = await session.StartThreadAsync(cwd, "exec", source: "exec");
            Assert.Equal("exec", ThreadSources.Get(threadId));
            var childId = await session.ForkAsync();
            Assert.Equal("exec", ThreadSources.Get(childId));
            var here = ResumePicker.Candidates(all: false, cwd: cwd);
            Assert.DoesNotContain(here, row => row.Id == threadId);
            var every = ResumePicker.Candidates(all: true, includeNonInteractive: true, cwd: cwd);
            Assert.Contains(every, row => row.Id == threadId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
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

public class FileSearchTests
{
    [Fact]
    public void Grep_and_name_search_find_workspace_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "alpha.cs"), "class HelloWorld {}\n");
        var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
        cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", root, false, 8);
        var sandbox = new WorkspaceSandbox(cfg);
        var grep = FileSearch.Grep(new ToolCallRequest("c1", "grep_files", "{\"pattern\":\"HelloWorld\"}"), sandbox);
        Assert.False(grep.IsError);
        Assert.Contains("HelloWorld", grep.Output);
        var names = FileSearch.SearchNames(new ToolCallRequest("c2", "file_search", "{\"query\":\"alpha\"}"), sandbox);
        Assert.Contains("alpha.cs", names.Output);
    }
}

public class CompactTests
{
    [Fact]
    public void Collapses_old_history_when_over_threshold()
    {
        var history = new List<HistoryMessage>();
        for (var i = 0; i < 20; i++)
        {
            history.Add(new HistoryMessage("tool", new string('x', 10_000), $"call_{i}", "shell", ""));
        }

        var resized = new ResizeArrayAdapter(history);
        Assert.True(Compact.apply(resized.Inner));
        Assert.True(resized.Inner.Count < 20);
        Assert.Contains(resized.Inner, m => m.Content.Contains("compacted"));
    }

    private sealed class ResizeArrayAdapter
    {
        public ResizeArrayAdapter(List<HistoryMessage> seed)
        {
            Inner = [];
            Inner.AddRange(seed);
        }

        public List<HistoryMessage> Inner { get; }
    }
}

public class McpStdioTests
{
    [Fact]
    public async Task Initialize_list_and_call_roundtrip()
    {
        var toServer = new LineChannel();
        var fromServer = new LineChannel();
        var client = new McpStdioClient(fromServer.Reader, toServer.Writer);
        var server = Task.Run(async () =>
        {
            var init = await toServer.Reader.ReadLineAsync();
            Assert.Contains("initialize", init);
            await fromServer.Writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"serverInfo\":{\"name\":\"fake\",\"version\":\"1\"}}}");
            var initialized = await toServer.Reader.ReadLineAsync();
            Assert.Contains("initialized", initialized);
            var list = await toServer.Reader.ReadLineAsync();
            Assert.Contains("tools/list", list);
            await fromServer.Writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"tools\":[{\"name\":\"ping\",\"description\":\"ping\",\"inputSchema\":{\"type\":\"object\"}}]}}");
            var call = await toServer.Reader.ReadLineAsync();
            Assert.Contains("tools/call", call);
            await fromServer.Writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"pong\"}]}}");
        });

        await client.InitializeAsync();
        var tools = await client.ListToolsAsync("fake");
        Assert.Contains(tools, t => t.Name == "mcp__fake__ping");
        var output = await client.CallToolAsync("ping", "{}");
        Assert.Equal("pong", output);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

public class SubagentTests
{
    [Fact]
    public async Task Spawn_wait_and_depth_limit()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
        cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", root, false, 8);
        var inner = new BuiltinToolExecutor(cfg);
        var exec = new SubagentExecutor(inner, async (msg, fork, ct) => $"done:{msg}");
        var spawned = await exec.ExecuteAsync(new ToolCallRequest("1", "spawn_agent", """{"message":"hi"}"""), CancellationToken.None);
        Assert.False(spawned.IsError);
        Assert.Contains("agent_", spawned.Output);
        using var doc = JsonDocument.Parse(spawned.Output);
        var id = doc.RootElement.GetProperty("id").GetString();
        var waited = await exec.ExecuteAsync(new ToolCallRequest("2", "wait_agent", $"{{\"targets\":[\"{id}\"]}}"), CancellationToken.None);
        Assert.Contains("done:hi", waited.Output);

        Collaboration.Depth.Value = 2;
        var denied = await exec.ExecuteAsync(new ToolCallRequest("3", "spawn_agent", """{"message":"nope"}"""), CancellationToken.None);
        Collaboration.Depth.Value = 0;
        Assert.True(denied.IsError);
        Assert.Contains("depth limit", denied.Output);

        var listed = await exec.ExecuteAsync(new ToolCallRequest("4", "list_agents", "{}"), CancellationToken.None);
        Assert.False(listed.IsError);
        Assert.Contains("agent_", listed.Output);

        var resumed = await exec.ExecuteAsync(new ToolCallRequest("5", "resume_agent", "{\"id\":\"" + id + "\",\"message\":\"again\"}"), CancellationToken.None);
        Assert.False(resumed.IsError, resumed.Output);
        Assert.Contains("resumed", resumed.Output);

        var unknown = await exec.ExecuteAsync(new ToolCallRequest("6", "interrupt_agent", "{\"target\":\"missing\"}"), CancellationToken.None);
        Assert.True(unknown.IsError);
    }
}

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

public class AuthServiceTests
{
    [Fact]
    public void Saves_api_key_to_auth_json()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            AuthService.SaveApiKey("sk-test-123");
            var json = File.ReadAllText(Path.Combine(home, "auth.json"));
            Assert.Contains("sk-test-123", json);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class McpHttpTests
{
    [Fact]
    public async Task Streamable_http_initialize_and_list_tools()
    {
        var listener = new HttpListener();
        var prefix = $"http://127.0.0.1:{GetFreePort()}/";
        listener.Prefixes.Add(prefix);
        listener.Start();
        try
        {
            var server = Task.Run(async () =>
            {
                for (var i = 0; i < 2; i++)
                {
                    var ctx = await listener.GetContextAsync();
                    using var reader = new StreamReader(ctx.Request.InputStream);
                    var body = await reader.ReadToEndAsync();
                    string response;
                    if (body.Contains("initialize"))
                    {
                        response = """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-06-18","capabilities":{},"serverInfo":{"name":"httpfake","version":"1"}}}""";
                    }
                    else
                    {
                        response = """{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"echo","description":"echo","inputSchema":{"type":"object"}}]}}""";
                    }
                    var bytes = System.Text.Encoding.UTF8.GetBytes(response);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
            });

            var spec = new McpServerSpec("httpfake", "", [], true, prefix.TrimEnd('/'), null);
            await using var client = new McpHttpClient(spec);
            await client.InitializeAsync();
            var tools = await client.ListToolsAsync("httpfake");
            Assert.Contains(tools, x => x.Name == "mcp__httpfake__echo");
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

public class AccountProtocolTests
{
    [Fact]
    public async Task Login_api_key_then_account_read()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var client = hosted.Client;
            await client.CallAsync("initialize", new { clientInfo = new { name = "test", title = "t", version = "0" } });
            await client.NotifyAsync("initialized");
            await client.CallAsync("account/login/start", new { type = "apiKey", apiKey = "sk-test-account" });
            var account = await client.CallAsync("account/read");
            Assert.Equal("apiKey", account.GetProperty("account").GetProperty("type").GetString());
            var auth = await client.CallAsync("getAuthStatus");
            Assert.Equal("apiKey", auth.GetProperty("authMethod").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ExecPolicyTests
{
    [Fact]
    public void Prefix_rule_forbidden_beats_allow()
    {
        var rule = ExecPolicy.ParseLine("prefix_rule(pattern=[\"rm\",\"-rf\"], decision=\"forbidden\")");
        Assert.NotNull(rule);
        Assert.Equal(ExecDecision.Forbidden, rule!.Decision);
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "rules"));
            File.WriteAllText(Path.Combine(home, "rules", "default.rules"), "prefix_rule(pattern=[\"rm\",\"-rf\"], decision=\"forbidden\")");
            Assert.Equal(ExecDecision.Forbidden, ExecPolicy.Evaluate("rm -rf /tmp/x"));
            Assert.Equal(ExecDecision.Allow, ExecPolicy.Evaluate("dotnet test"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ThreadGoalTests
{
    [Fact]
    public async Task Set_and_get_goal_over_app_server()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var threadId = await session.StartThreadAsync(Path.GetTempPath(), "g");
        var set = await hosted.Client.CallAsync("thread/goal/set", new { threadId, objective = "Finish the port" });
        Assert.Equal("Finish the port", set.GetProperty("goal").GetProperty("objective").GetString());
        var get = await hosted.Client.CallAsync("thread/goal/get", new { threadId });
        Assert.Equal("Finish the port", get.GetProperty("goal").GetProperty("objective").GetString());
    }
}

public class ConfigProfileTests
{
    [Fact]
    public void Profile_overrides_model_and_sandbox()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "config.toml"), """
model = "base-model"
sandbox_mode = "read-only"

[profiles.full_auto]
model = "profile-model"
sandbox_mode = "workspace-write"
approval_policy = "never"
""");
            var cfg = ConfigService.Load(Path.GetTempPath(), profile: "full_auto");
            Assert.Equal("profile-model", cfg.Model);
            Assert.Equal("workspace-write", cfg.SandboxMode);
            Assert.Equal("never", cfg.ApprovalPolicy);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class McpStatusProtocolTests
{
    [Fact]
    public async Task Lists_configured_mcp_servers()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "config.toml"), """
[mcp_servers.docs]
command = "npx"
args = ["-y", "x"]
""");
            await using var hosted = InProcessAppServer.Start();
            var client = hosted.Client;
            await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
            await client.NotifyAsync("initialized");
            var listed = await client.CallAsync("mcpServerStatus/list");
            Assert.Equal("docs", listed.GetProperty("data")[0].GetProperty("name").GetString());
            Assert.Equal("stdio", listed.GetProperty("data")[0].GetProperty("transport").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

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

public class ConfigWriteTests
{
    [Fact]
    public void Writes_and_reloads_model_key()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "config.toml"), "model = \"old\"\n");
            ConfigService.WriteUserKeys(new Dictionary<string, string> { ["model"] = "new-model" });
            var cfg = ConfigService.Load(Path.GetTempPath());
            Assert.Equal("new-model", cfg.Model);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ThreadCompactProtocolTests
{
    [Fact]
    public async Task Compact_start_returns_empty_object()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var threadId = await session.StartThreadAsync(Path.GetTempPath(), "c");
        var result = await hosted.Client.CallAsync("thread/compact/start", new { threadId });
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
    }
}

public class FeatureFlagsTests
{
    [Fact]
    public void Enable_and_list_feature()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "config.toml"), "model = \"x\"\n");
            FeatureFlags.Set("multi_agent", true);
            Assert.True(FeatureFlags.IsEnabled("multi_agent"));
            Assert.Contains(FeatureFlags.List(), kv => kv.Key == "multi_agent" && kv.Value);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class RecapAndCopyTests
{
    [Fact]
    public void BuildPrompt_skips_empty_history_and_includes_recent_turns()
    {
        Assert.Null(Recap.BuildPrompt([]));
        var history = new List<HistoryMessage>
        {
            new("user", "Fix the login button", "", "", ""),
            new("assistant", "Updated Login.fs", "", "", ""),
        };
        var prompt = Recap.BuildPrompt(history);
        Assert.NotNull(prompt);
        Assert.Contains("Fix the login button", prompt);
        Assert.Contains("Updated Login.fs", prompt);
        Assert.Contains("Recent conversation", prompt);
    }

    [Fact]
    public async Task Recap_empty_thread_is_honest_and_copy_reads_injected_agent()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("test", "Test");
            var threadId = await session.StartThreadAsync(Path.GetTempPath(), "recap");
            var empty = await session.RecapAsync();
            Assert.Equal("empty", empty.GetProperty("status").GetString());
            Assert.False(empty.GetProperty("persisted").GetBoolean());
            var none = await session.CopyLastAsync();
            Assert.Equal("empty", none.GetProperty("kind").GetString());
            await hosted.Client.CallAsync("thread/inject_items", new
            {
                threadId,
                items = new[] { new { type = "assistant", text = "hello\n```\nvar x = 1;\n```" } },
            });
            var copied = await session.CopyLastAsync();
            Assert.Equal("message", copied.GetProperty("kind").GetString());
            Assert.Contains("hello", copied.GetProperty("text").GetString());
            var code = await session.CopyLastAsync("code");
            Assert.Equal("code", code.GetProperty("kind").GetString());
            Assert.Contains("var x = 1;", code.GetProperty("text").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ThreadExportAndRolloutTests
{
    [Fact]
    public async Task Export_and_rollout_path_go_through_app_server()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-cwd-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(cwd);
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("test", "Test");
            var threadId = await session.StartThreadAsync(cwd, "export-me");
            var dest = Path.Combine(cwd, "out.md");
            var exported = await session.ExportThreadAsync(dest);
            Assert.Equal(Path.GetFullPath(dest), exported.GetProperty("path").GetString());
            Assert.True(File.Exists(dest));
            Assert.Contains("# export-me", File.ReadAllText(dest));
            var rollout = await session.RolloutPathAsync();
            Assert.Contains(threadId, rollout.GetProperty("path").GetString());
            Assert.True(rollout.GetProperty("exists").GetBoolean());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ReviewStartTests
{
    [Fact]
    public async Task Review_start_accepts_loaded_thread()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var threadId = await session.StartThreadAsync(Path.GetTempPath(), "r");
        var result = await hosted.Client.CallAsync("review/start", new { threadId });
        Assert.Equal(threadId, result.GetProperty("reviewThreadId").GetString());
    }
}

public class CurrentTimeToolTests
{
    [Fact]
    public void Prompt_includes_current_time_tool()
    {
        var cfg = ConfigService.Load(Path.GetTempPath());
        var request = Prompt.build(cfg, [new HistoryMessage("user", "hi", "", "", "")]);
        Assert.Contains(request.Tools, x => x.Name == "current_time");
    }
}

public class ThreadPinTests
{
    [Fact]
    public void Pins_persist_and_sort_first()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var cfg = ConfigService.Load(Path.GetTempPath());
            var store = new JsonlThreadStore();
            var a = store.Create(cfg, "a");
            var b = store.Create(cfg, "b");
            ThreadPins.Set(a.Id, true);
            var listed = store.List();
            Assert.True(listed[0].Pinned);
            Assert.Equal(a.Id, listed[0].Id);
            Assert.True(ThreadPins.IsPinned(a.Id));
            Assert.False(ThreadPins.IsPinned(b.Id));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class SleepToolTests
{
    [Fact]
    public async Task Sleep_honors_short_duration()
    {
        var cfg = ConfigService.Load(Path.GetTempPath());
        var exec = new BuiltinToolExecutor(cfg);
        var started = DateTime.UtcNow;
        var result = await exec.ExecuteAsync(new ToolCallRequest("1", "sleep", "{\"duration_ms\":20}"), CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Contains("slept", result.Output);
        Assert.True((DateTime.UtcNow - started).TotalMilliseconds >= 15);
    }
}

public class ModelListProtocolTests
{
    [Fact]
    public void Catalog_includes_builtin_ids()
    {
        var ids = ModelCatalog.List().Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("gpt-4.1-mini", ids);
        Assert.Contains("gpt-4.1", ids);
    }

    [Fact]
    public void Overlay_models_json_is_listed()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "models.json"), """[{"id":"local-llama","provider":"lmstudio"}]""");
            Assert.Contains(ModelCatalog.List(), m => m.Id == "local-llama" && m.Provider == "lmstudio");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Model_list_includes_current_model()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        await client.NotifyAsync("initialized");
        var listed = await client.CallAsync("model/list");
        Assert.True(listed.GetProperty("data").GetArrayLength() >= 1);
        Assert.False(string.IsNullOrWhiteSpace(listed.GetProperty("data")[0].GetProperty("id").GetString()));
    }
}

public class ExperimentalFeatureListTests
{
    [Fact]
    public async Task Lists_feature_flags()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        await client.NotifyAsync("initialized");
        var listed = await client.CallAsync("experimentalFeature/list");
        Assert.True(listed.GetProperty("data").GetArrayLength() >= 1);
        Assert.False(string.IsNullOrWhiteSpace(listed.GetProperty("data")[0].GetProperty("name").GetString()));
        Assert.True(listed.GetProperty("data")[0].TryGetProperty("defaultEnabled", out _));
        Assert.Contains(listed.GetProperty("data").EnumerateArray(), x => x.GetProperty("name").GetString() == "web_search" && x.GetProperty("description").GetString()!.Length > 0);
    }

    [Fact]
    public async Task Enablement_set_writes_named_flags()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var client = hosted.Client;
            await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
            await client.NotifyAsync("initialized");
            var result = await client.CallAsync("experimentalFeature/enablement/set", new
            {
                enablement = new Dictionary<string, bool> { ["web_search"] = true },
            });
            Assert.True(result.GetProperty("enablement").GetProperty("web_search").GetBoolean());
            Assert.True(FeatureFlags.IsEnabled("web_search"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class PermissionProfileListTests
{
    [Fact]
    public async Task Lists_sandbox_profiles()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        await client.NotifyAsync("initialized");
        var listed = await client.CallAsync("permissionProfile/list");
        Assert.Contains(listed.GetProperty("data").EnumerateArray(), e => e.GetProperty("id").GetString() == "workspace-write");
    }
}

public class WebSearchToolTests
{
    [Fact]
    public async Task Rejects_empty_query()
    {
        var cfg = ConfigService.Load(Path.GetTempPath());
        var exec = new BuiltinToolExecutor(cfg);
        var result = await exec.ExecuteAsync(new ToolCallRequest("1", "web_search", "{\"query\":\"\"}"), CancellationToken.None);
        Assert.True(result.IsError);
    }
}

public class ThreadUnsubscribeTests
{
    [Fact]
    public async Task Unsubscribing_unloads_thread()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var threadId = await session.StartThreadAsync(Path.GetTempPath(), "u");
        await hosted.Client.CallAsync("thread/unsubscribe", new { threadId });
        var loaded = await hosted.Client.CallAsync("thread/loaded/list");
        Assert.Equal(0, loaded.GetProperty("threadIds").GetArrayLength());
    }
}

public class ConversationSummaryTests
{
    [Fact]
    public async Task Summary_returns_thread_title()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var threadId = await session.StartThreadAsync(Path.GetTempPath(), "summary-thread");
        var summary = await hosted.Client.CallAsync("getConversationSummary", new { conversationId = threadId });
        Assert.Equal("summary-thread", summary.GetProperty("summary").GetProperty("title").GetString());
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

public class TurnNotifyTests
{
    [Fact]
    public void Loads_notify_command_from_config()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "config.toml"), "notify = [\"echo\", \"done\"]\n");
            var cmd = TurnNotify.LoadCommand();
            Assert.Equal(["echo", "done"], cmd);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class RequestUserInputToolTests
{
    [Fact]
    public void Prompt_includes_request_user_input()
    {
        var cfg = ConfigService.Load(Path.GetTempPath());
        var request = Prompt.build(cfg, [new HistoryMessage("user", "hi", "", "", "")]);
        Assert.Contains(request.Tools, x => x.Name == "request_user_input");
    }
}

public class FsWatchProtocolTests
{
    [Fact]
    public async Task Watch_returns_id_and_unwatch_succeeds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codexsharp-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        await client.NotifyAsync("initialized");
        var watched = await client.CallAsync("fs/watch", new { path = dir });
        var watchId = watched.GetProperty("watchId").GetString();
        Assert.StartsWith("watch_", watchId);
        await client.CallAsync("fs/unwatch", new { watchId });
    }

    [Fact]
    public async Task Watch_notifies_on_file_change()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codexsharp-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.FsChanged += (_, path) => tcs.TrySetResult(path);
        await session.WatchFsAsync(dir, "test-watch");
        var file = Path.Combine(dir, "changed.txt");
        await File.WriteAllTextAsync(file, "hello");
        var changed = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Contains("changed.txt", changed, StringComparison.OrdinalIgnoreCase);
        await session.UnwatchFsAsync("test-watch");
    }
}


public class PersonalityConfigTests
{
    [Fact]
    public void Friendly_personality_enters_developer_instructions()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "config.toml"), "personality = \"friendly\"" + Environment.NewLine);
            var cfg = ConfigService.Load(Path.GetTempPath());
            Assert.Contains("friendly", cfg.DeveloperInstructions);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class CollaborationModeListTests
{
    [Fact]
    public async Task Lists_builtin_modes()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        await client.NotifyAsync("initialized");
        var listed = await client.CallAsync("collaborationMode/list");
        Assert.Contains(listed.GetProperty("data").EnumerateArray(), e => e.GetProperty("name").GetString() == "plan");
    }
}

public class ThreadNameProtocolTests
{
    [Fact]
    public async Task Sets_thread_name()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var threadId = await session.StartThreadAsync(Path.GetTempPath(), "old");
        await hosted.Client.CallAsync("thread/name/set", new { threadId, name = "renamed" });
        var read = await hosted.Client.CallAsync("thread/read", new { threadId });
        Assert.Equal("renamed", read.GetProperty("thread").GetProperty("title").GetString());
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


public class AppServerListenTests
{
    [Fact]
    public void Parses_stdio_off_ws_and_unix()
    {
        Assert.IsType<AppServerTransport.Stdio>(AppServerTransport.FromArgs([]));
        Assert.IsType<AppServerTransport.Stdio>(AppServerTransport.FromArgs(["--stdio"]));
        Assert.IsType<AppServerTransport.Stdio>(AppServerTransport.Parse("stdio://"));
        Assert.IsType<AppServerTransport.Off>(AppServerTransport.Parse("off"));
        var ws = Assert.IsType<AppServerTransport.WebSocket>(AppServerTransport.Parse("ws://127.0.0.1:9999"));
        Assert.Equal(9999, ws.EndPoint.Port);
        var unix = Assert.IsType<AppServerTransport.UnixSocket>(AppServerTransport.Parse("unix://"));
        Assert.Contains("app-server-control.sock", unix.Path);
    }

    [Fact]
    public void Rejects_invalid_listen_urls()
    {
        Assert.Throws<AppServerTransportException>(() => AppServerTransport.Parse("ws://localhost:9"));
        Assert.Throws<AppServerTransportException>(() => AppServerTransport.Parse("http://127.0.0.1:9"));
        Assert.Throws<AppServerTransportException>(() => AppServerTransport.FromArgs(["--stdio", "--listen", "stdio://"]));
        Assert.Throws<AppServerTransportException>(() => AppServerTransport.FromArgs(["--listen"]));
    }

    [Fact]
    public void Refuses_non_loopback_websocket_bind()
    {
        var err = Assert.Throws<InvalidOperationException>(() =>
            WebSocketAppServer.EnsureLoopback(IPEndPoint.Parse("0.0.0.0:8080")));
        Assert.Contains("non-loopback", err.Message);
    }
}

public class WindowsSandboxProtocolTests
{
    [Fact]
    public async Task Readiness_is_not_configured_until_unelevated_setup()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var client = hosted.Client;
            await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
            await client.NotifyAsync("initialized");
            var ready = await client.CallAsync("windowsSandbox/readiness");
            Assert.Equal("notConfigured", ready.GetProperty("status").GetString());
            Assert.True(ready.TryGetProperty("jobObject", out _));
            var setup = await client.CallAsync("windowsSandbox/setupStart", new { mode = "unelevated" });
            Assert.True(setup.GetProperty("started").GetBoolean());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class WebSocketAppServerTests
{
    [Fact]
    public async Task Readyz_healthz_origin_and_initialize()
    {
        await using var server = WebSocketAppServer.StartLoopback();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        HttpResponseMessage? ready = null;
        for (var i = 0; i < 40; i++)
        {
            try
            {
                ready = await http.GetAsync($"{server.HttpBase}/readyz");
                if (ready.IsSuccessStatusCode)
                {
                    break;
                }
            }
            catch
            {
                await Task.Delay(50);
            }
        }

        Assert.NotNull(ready);
        Assert.Equal(HttpStatusCode.OK, ready!.StatusCode);
        var health = await http.GetAsync($"{server.HttpBase}/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var forbidden = new HttpRequestMessage(HttpMethod.Get, $"{server.HttpBase}/healthz");
        forbidden.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
        var denied = await http.SendAsync(forbidden);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        await using var client = await AppServerClient.ConnectWebSocketAsync(server.WsUrl);
        var init = await client.CallAsync("initialize", new { clientInfo = new { name = "ws", title = "ws", version = "0" } });
        Assert.Contains("codexsharp", init.GetProperty("userAgent").GetString());
        await client.NotifyAsync("initialized");
        var listed = await client.CallAsync("model/list");
        Assert.True(listed.TryGetProperty("data", out _) || listed.ValueKind == JsonValueKind.Object);
    }
}


public class HostFsProtocolTests
{
    [Fact]
    public async Task Read_write_metadata_directory_copy_and_remove()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-fs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        await client.NotifyAsync("initialized");

        var file = Path.Combine(root, "hello.txt");
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("hi"));
        await client.CallAsync("fs/writeFile", new { path = file, dataBase64 = payload });
        var read = await client.CallAsync("fs/readFile", new { path = file });
        Assert.Equal(payload, read.GetProperty("dataBase64").GetString());

        var meta = await client.CallAsync("fs/getMetadata", new { path = file });
        Assert.True(meta.GetProperty("isFile").GetBoolean());
        Assert.False(meta.GetProperty("isDirectory").GetBoolean());

        var nested = Path.Combine(root, "sub");
        await client.CallAsync("fs/createDirectory", new { path = nested });
        var listing = await client.CallAsync("fs/readDirectory", new { path = root });
        Assert.Contains(listing.GetProperty("entries").EnumerateArray(), e => e.GetProperty("fileName").GetString() == "hello.txt");

        var copied = Path.Combine(root, "hello-copy.txt");
        await client.CallAsync("fs/copy", new { sourcePath = file, destinationPath = copied });
        Assert.True(File.Exists(copied));

        await client.CallAsync("fs/remove", new { path = copied });
        Assert.False(File.Exists(copied));
    }
}

public class AppServerHandleTests
{
    [Fact]
    public async Task Defaults_to_in_process_when_forced()
    {
        using (new EnvScope("CODEXSHARP_APP_SERVER_MODE", "in-process"))
        {
            await using var hosted = AppServerHandle.Start();
            Assert.Equal("in-process", hosted.Kind);
        }
    }

    private sealed class EnvScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;
        public EnvScope(string name, string value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}


public class ConfigValueWriteTests
{
    [Fact]
    public async Task Writes_single_key_path()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var client = hosted.Client;
            await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
            await client.NotifyAsync("initialized");
            await client.CallAsync("config/value/write", new { keyPath = "model", value = "gpt-test-value-write" });
            var read = await client.CallAsync("config/read");
            Assert.Equal("gpt-test-value-write", read.GetProperty("model").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ThreadItemsListTests
{
    [Fact]
    public async Task Lists_history_after_start()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var threadId = await session.StartThreadAsync(Path.GetTempPath(), "items");
        var listed = await hosted.Client.CallAsync("thread/items/list", new { threadId });
        Assert.True(listed.TryGetProperty("data", out var data));
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
    }

    [Fact]
    public async Task Injects_items_and_honors_itemsView_turnId_and_paging()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var threadId = await session.StartThreadAsync(Path.GetTempPath(), "items-view");
        var longText = new string('x', 250);
        await hosted.Client.CallAsync("thread/inject_items", new
        {
            threadId,
            items = new object[]
            {
                new { type = "user_message", text = "first user" },
                new { type = "agent_message", text = "first assistant" },
                new { role = "user", content = longText },
                new { role = "assistant", content = "second assistant" },
            },
        });

        var summary = await hosted.Client.CallAsync("thread/items/list", new { threadId, itemsView = "summary", limit = 10 });
        var data = summary.GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal(4, data.Length);
        Assert.Equal("trn_0", data[0].GetProperty("turnId").GetString());
        Assert.Equal("user_message", data[0].GetProperty("type").GetString());
        var longItem = data.Single(e => e.GetProperty("type").GetString() == "user_message" && (e.GetProperty("text").GetString() ?? "").Contains('x'));
        Assert.Equal(201, longItem.GetProperty("text").GetString()!.Length);
        Assert.EndsWith("…", longItem.GetProperty("text").GetString());

        var full = await hosted.Client.CallAsync("thread/items/list", new { threadId, itemsView = "full" });
        var fullLong = full.GetProperty("data").EnumerateArray()
            .Single(e => e.GetProperty("type").GetString() == "user_message" && (e.GetProperty("text").GetString() ?? "").StartsWith("xxx"));
        Assert.Equal(longText, fullLong.GetProperty("text").GetString());

        var turn0 = await hosted.Client.CallAsync("thread/items/list", new { threadId, turnId = "trn_0" });
        Assert.Equal(2, turn0.GetProperty("data").GetArrayLength());
        Assert.All(turn0.GetProperty("data").EnumerateArray(), e => Assert.Equal("trn_0", e.GetProperty("turnId").GetString()));

        var page = await hosted.Client.CallAsync("thread/items/list", new { threadId, limit = 2 });
        Assert.Equal(2, page.GetProperty("data").GetArrayLength());
        Assert.Equal("2", page.GetProperty("nextCursor").GetString());
        Assert.Equal("0", page.GetProperty("backwardsCursor").GetString());
        var page2 = await hosted.Client.CallAsync("thread/items/list", new { threadId, limit = 2, cursor = "2" });
        Assert.Equal(2, page2.GetProperty("data").GetArrayLength());
        Assert.Null(page2.GetProperty("nextCursor").GetString());
    }
}

public class ThreadInjectItemsTests
{
    [Fact]
    public async Task Unknown_thread_is_not_loaded()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            hosted.Client.CallAsync("thread/inject_items", new { threadId = "missing", items = Array.Empty<object>() }));
        Assert.Contains("not loaded", ex.Message);
    }
}

public class ThreadRealtimeStubTests
{
    [Fact]
    public async Task Start_requires_experimental_api()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CallAsync("thread/realtime/start", new { threadId = "x", outputModality = "text" }));
        Assert.Contains("experimentalApi", ex.Message);
    }

    [Fact]
    public async Task ListVoices_succeeds_but_start_is_not_implemented()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var threadId = await session.StartThreadAsync(Path.GetTempPath(), "realtime");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            hosted.Client.CallAsync("thread/realtime/start", new { threadId, outputModality = "text" }));
        Assert.Contains("WebRTC", ex.Message);
        var voices = await hosted.Client.CallAsync("thread/realtime/listVoices");
        Assert.Equal(JsonValueKind.Array, voices.GetProperty("voices").ValueKind);
        await hosted.Client.CallAsync("thread/realtime/stop", new { threadId });
    }
}


public class ThreadHistoryPersistTests
{
    [Fact]
    public async Task Resume_reloads_saved_history()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
            cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", root, false, 8);
            var session = CodexSession.Start(cfg, "hist");
            var model = new ScriptedModelClient(
            [
                ModelStreamEvent.NewOutputTextDelta("remembered"),
                ModelStreamEvent.NewStreamFinished("stop"),
            ]);
            await session.RunTurnWithAsync(model, new BuiltinToolExecutor(cfg), new AutoApprover(true), "hello persist", CancellationToken.None);
            Assert.Contains(session.History, m => m.Content.Contains("hello persist"));

            var resumed = CodexSession.Resume(session.Thread.Id, cfg);
            Assert.Contains(resumed.History, m => m.Content.Contains("hello persist"));
            Assert.Contains(resumed.History, m => m.Content.Contains("remembered"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task App_server_items_list_after_resume()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var threadId = await session.StartThreadAsync(Path.GetTempPath(), "persist-ui");
            var cfg = ConfigService.Load(Path.GetTempPath(), sandboxOverride: "workspace-write", approvalOverride: "never");
            cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", Path.GetTempPath(), false, 8);
            var live = CodexSession.Resume(threadId, cfg);
            live.RestoreHistory([new HistoryMessage("user", "from disk", "", "", "")]);
            new JsonlThreadStore().SaveHistory(threadId, live.History);

            await session.ResumeThreadAsync(threadId);
            var listed = await session.ListItemsAsync();
            Assert.Contains(listed.GetProperty("data").EnumerateArray(), e => e.GetProperty("text").GetString() == "from disk");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}


public class ThreadRollbackTests
{
    [Fact]
    public void Drops_last_user_turn()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
            cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", root, false, 8);
            var session = CodexSession.Start(cfg, "rb");
            session.RestoreHistory(
            [
                new HistoryMessage("user", "one", "", "", ""),
                new HistoryMessage("assistant", "a1", "", "", ""),
                new HistoryMessage("user", "two", "", "", ""),
                new HistoryMessage("assistant", "a2", "", "", ""),
            ]);
            Assert.Equal(2, session.Turns().Count);
            Assert.True(session.RollbackTurns(1));
            Assert.Single(session.Turns());
            Assert.Equal("one", session.History[0].Content);
            Assert.DoesNotContain(session.History, m => m.Content == "two");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Turns_list_and_rollback_over_app_server()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var threadId = await session.StartThreadAsync(Path.GetTempPath(), "turns");
        var listed = await hosted.Client.CallAsync("thread/turns/list", new { threadId, limit = 1 });
        Assert.Equal(JsonValueKind.Array, listed.GetProperty("data").ValueKind);
        Assert.True(listed.TryGetProperty("nextCursor", out _));
        Assert.True(listed.TryGetProperty("backwardsCursor", out _));
        await hosted.Client.CallAsync("thread/rollback", new { threadId, numTurns = 1 });
    }
}

public class CollaborationModeConfigTests
{
    [Fact]
    public void Plan_mode_enters_developer_instructions()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "config.toml"), "collaboration_mode = \"plan\"\n");
            var cfg = ConfigService.Load(Path.GetTempPath());
            Assert.Contains("<collaboration_mode>plan</collaboration_mode>", cfg.DeveloperInstructions);
            Assert.Contains("proposed_plan", cfg.DeveloperInstructions);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}


public class ThreadSearchAndQueueTests
{
    [Fact]
    public void Search_matches_title_and_history()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
            cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", root, false, 8);
            var session = CodexSession.Start(cfg, "unique-zebra-title");
            session.RestoreHistory([new HistoryMessage("user", "remember the pineapple token", "", "", "")]);
            new JsonlThreadStore().SaveHistory(session.Thread.Id, session.History);
            var hits = new JsonlThreadStore().Search("pineapple");
            Assert.Contains(hits, h => h.Snippet.Contains("pineapple"));
            var titled = new JsonlThreadStore().Search("zebra-title");
            Assert.Contains(titled, h => h.Thread.Id == session.Thread.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Queue_add_and_list()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        await session.StartThreadAsync(Path.GetTempPath(), "q");
        await session.QueueAddAsync("later");
        var listed = await session.QueueListAsync();
        Assert.Contains(listed.GetProperty("data").EnumerateArray(), e => e.GetProperty("text").GetString() == "later");
    }
}


public class FileChangeItemTests
{
    [Fact]
    public async Task Apply_patch_emits_file_change_item()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
        cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", root, false, 8);
        var patch = "*** Begin Patch\\n*** Add File: notes.md\\n+hi\\n*** End Patch\\n".Replace("\\n", "\n");
        var model = new ScriptedModelClient(
            [
                ModelStreamEvent.NewToolCallReady(new ToolCallRequest("call_p", "apply_patch", JsonSerializer.Serialize(new { patch }))),
                ModelStreamEvent.NewStreamFinished("tool"),
            ],
            [
                ModelStreamEvent.NewOutputTextDelta("patched"),
                ModelStreamEvent.NewStreamFinished("stop"),
            ]);
        var sink = new RecordingSink();
        var deps = new AgentDeps(cfg, model, new BuiltinToolExecutor(cfg), new AutoApprover(true), sink, [], new NoopHookHost(), new NoopUserInputHost(), new NoopSteerHost());
        await AgentLoop.runTurn(deps, "thr_patch", new List<HistoryMessage>(), "patch it", CancellationToken.None);
        Assert.Contains(sink.Events, e => e is AgentEvent.ItemCompleted c && c.Item.Kind == "file_change");
        Assert.True(File.Exists(Path.Combine(root, "notes.md")));
    }
}

public class EnvironmentInfoProtocolTests
{
    [Fact]
    public async Task Returns_cwd_and_home()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        await client.NotifyAsync("initialized");
        var info = await client.CallAsync("environment/info");
        Assert.False(string.IsNullOrWhiteSpace(info.GetProperty("cwd").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(info.GetProperty("home").GetString()));
    }
}


public class ThreadSettingsUpdateTests
{
    [Fact]
    public async Task Updates_model_on_loaded_thread()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var threadId = await session.StartThreadAsync(Path.GetTempPath(), "settings");
        await hosted.Client.CallAsync("thread/settings/update", new { threadId, model = "gpt-test-settings" });
        var read = await hosted.Client.CallAsync("thread/read", new { threadId });
        Assert.Equal("gpt-test-settings", read.GetProperty("thread").GetProperty("model").GetString());
    }
}


public class HooksListProtocolTests
{
    [Fact]
    public async Task Lists_hook_json_files()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var dir = Path.Combine(home, "hooks");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "stop.json"), """{"name":"stop-hook","event":"Stop","command":"echo done"}""");
            await using var hosted = InProcessAppServer.Start();
            var client = hosted.Client;
            await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
            await client.NotifyAsync("initialized");
            var listed = await client.CallAsync("hooks/list");
            Assert.Contains(listed.GetProperty("data").EnumerateArray(), e => e.GetProperty("name").GetString() == "stop-hook");
            var req = await client.CallAsync("configRequirements/read");
            Assert.True(req.TryGetProperty("requirements", out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}



public class HookRunnerTests
{
    [Fact]
    public void Stop_hook_runs_command()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var marker = Path.Combine(Path.GetTempPath(), "codexsharp-hook-" + Guid.NewGuid().ToString("N") + ".txt");
        Directory.CreateDirectory(Path.Combine(home, "hooks"));
        File.WriteAllText(
            Path.Combine(home, "hooks", "stop.json"),
            JsonSerializer.Serialize(new { name = "stop", @event = "Stop", command = "echo hooked>" + marker }));
        var results = HookRunner.Run(home, "Stop", new { threadId = "thr_x" });
        Assert.True(results.Count > 0);
        Assert.True(File.Exists(marker), results[0].Output);
        Assert.Contains("hooked", File.ReadAllText(marker), StringComparison.OrdinalIgnoreCase);
    }
}


public sealed class BlockingHookHost : IHookHost
{
    public HookDecision Fire(string eventName, string tool, string argumentsJson) =>
        eventName == "PreToolUse" ? HookDecision.NewHookBlock("deny from test") : HookDecision.HookContinue;
}

public class PreToolUseHookTests
{
    [Fact]
    public async Task PreToolUse_block_skips_tool()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "secret.txt"), "nope\n");
        var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
        cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", root, false, 8);
        var model = new ScriptedModelClient(
            [
                ModelStreamEvent.NewToolCallReady(new ToolCallRequest("call_1", "read_file", """{"path":"secret.txt"}""")),
                ModelStreamEvent.NewStreamFinished("tool"),
            ],
            [
                ModelStreamEvent.NewOutputTextDelta("blocked"),
                ModelStreamEvent.NewStreamFinished("stop"),
            ]);
        var sink = new RecordingSink();
        var deps = new AgentDeps(cfg, model, new BuiltinToolExecutor(cfg), new AutoApprover(true), sink, [], new BlockingHookHost(), new NoopUserInputHost(), new NoopSteerHost());
        var history = new List<HistoryMessage>();
        await AgentLoop.runTurn(deps, "thr_hook", history, "read it", CancellationToken.None);
        Assert.Contains(history, m => m.Role == "tool" && m.Content.Contains("Hook blocked"));
        Assert.DoesNotContain(history, m => m.Role == "tool" && m.Content.Contains("nope"));
    }
}


public class PreCompactHookTests
{
    [Fact]
    public void Deny_output_blocks_compact()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(home, "hooks"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            File.WriteAllText(
                Path.Combine(home, "hooks", "precompact.json"),
                JsonSerializer.Serialize(new { name = "precompact", @event = "PreCompact", command = "echo deny" }));
            var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
            cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", root, false, 8);
            var session = CodexSession.Start(cfg, "compact-hook");
            var messages = new List<HistoryMessage>();
            for (var i = 0; i < 20; i++)
            {
                messages.Add(new HistoryMessage("tool", new string('x', 10_000), $"call_{i}", "shell", ""));
            }
            session.RestoreHistory(messages);
            Assert.False(session.TryCompact());
            Assert.Equal(20, session.History.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}


public sealed class RecordingSubagentHooks : IHookHost
{
    public List<string> Events { get; } = [];
    public HookDecision Fire(string eventName, string tool, string argumentsJson)
    {
        Events.Add(eventName);
        return HookDecision.HookContinue;
    }
}

public class SubagentHookTests
{
    [Fact]
    public async Task Fires_start_and_stop_hooks()
    {
        var recorder = new RecordingSubagentHooks();
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
        cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", root, false, 8);
        var exec = new SubagentExecutor(new BuiltinToolExecutor(cfg), async (msg, fork, ct) => $"done:{msg}", recorder);
        var spawned = await exec.ExecuteAsync(new ToolCallRequest("1", "spawn_agent", "{\"message\":\"hi\"}"), CancellationToken.None);
        using var doc = JsonDocument.Parse(spawned.Output);
        var id = doc.RootElement.GetProperty("id").GetString();
        await exec.ExecuteAsync(new ToolCallRequest("2", "wait_agent", "{\"targets\":[\"" + id + "\"]}"), CancellationToken.None);
        Assert.Contains("SubagentStart", recorder.Events);
        Assert.Contains("SubagentStop", recorder.Events);
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


public class McpToolCallProtocolTests
{
    [Fact]
    public async Task Unknown_server_is_error()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        await client.NotifyAsync("initialized");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.CallAsync("mcpServer/tool/call", new { server = "no-such-server", tool = "x" }));
    }
}


public class ImageMessageContentTests
{
    [Fact]
    public void Embeds_png_as_data_url()
    {
        var path = Path.Combine(Path.GetTempPath(), "codexsharp-img-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        var text = "hello\nPlease inspect this image with view_image: " + path + "\n";
        var node = ImageMessageContent.ChatContent(text);
        Assert.Equal(JsonValueKind.Array, node.GetValueKind());
        var json = node.ToJsonString();
        Assert.Contains("image_url", json);
        Assert.Contains("data:image/png;base64,", json);
        Assert.Contains("hello", json);
        var resp = ImageMessageContent.ResponsesContent(text).ToJsonString();
        Assert.Contains("input_image", resp);
    }
}


public class ClipboardImageStoreTests
{
    [Fact]
    public void Saves_png_bytes_and_roundtrips_data_url()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var path = ClipboardImageStore.SavePng(png);
        Assert.True(File.Exists(path));
        Assert.True(ImageMessageContent.TryDataUrl(path, out var url));
        Assert.StartsWith("data:image/png;base64,", url);
        Assert.Equal(png, ClipboardImageStore.AsBytes(png));
    }
}


public class ExtractLocalPathsTests
{
    [Fact]
    public void Finds_markdown_path_eq_and_marker()
    {
        var path = Path.Combine(Path.GetTempPath(), "codexsharp-img-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        var text = "see ![shot](" + path + ")\npath=" + path + "\n" + ImageMessageContent.Marker + path;
        var hits = ImageMessageContent.ExtractLocalPaths(text);
        Assert.Contains(hits, p => p.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        Assert.True(hits.Count >= 1);
    }
}


public class ImageUrlCacheTests
{
    [Fact]
    public void Extracts_markdown_and_bare_https_images()
    {
        var text = "see ![a](https://example.com/a.png) and https://cdn.example/b.jpg?x=1";
        var urls = ImageUrlCache.ExtractRemoteUrls(text);
        Assert.Contains(urls, u => u.Contains("a.png"));
        Assert.Contains(urls, u => u.Contains("b.jpg"));
    }

    [Fact]
    public async Task Download_uses_injected_http_client()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var handler = new StubImageHandler(png);
        using var http = new HttpClient(handler);
        var path = await ImageUrlCache.DownloadAsync("https://example.invalid/tiny.png", http);
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.True(File.Exists(path!));
    }

    private sealed class StubImageHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return Task.FromResult(resp);
        }
    }
}

public class BedrockAndAccountHonestyTests
{
    [Fact]
    public async Task Discover_reads_profiles_without_secrets_and_setup_errors()
    {
        var creds = Path.Combine(Path.GetTempPath(), "codexsharp-aws-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(creds, """
            [default]
            aws_access_key_id = AKIATESTNOTASECRET
            aws_secret_access_key = wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY

            [codex-bedrock]
            aws_access_key_id = AKIATEST2
            aws_secret_access_key = secret
            """);
        var prevCreds = Environment.GetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE");
        var prevCfg = Environment.GetEnvironmentVariable("AWS_CONFIG_FILE");
        var prevKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
        var prevRegion = Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION");
        Environment.SetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE", creds);
        Environment.SetEnvironmentVariable("AWS_CONFIG_FILE", creds + ".missing");
        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", "AKIATESTENV");
        Environment.SetEnvironmentVariable("AWS_DEFAULT_REGION", "us-east-1");
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var found = await hosted.Client.CallAsync("account/bedrock/discover");
            var names = found.GetProperty("profiles").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToArray();
            Assert.Contains("default", names);
            Assert.Contains("codex-bedrock", names);
            var json = found.GetRawText();
            Assert.DoesNotContain("AKIATESTNOTASECRET", json);
            Assert.DoesNotContain("wJalrXUtnFEMI", json);
            var env = found.GetProperty("environmentCredentials").EnumerateArray().Select(c => c.GetProperty("type").GetString()).ToArray();
            Assert.Contains("accessKeys", env);
            var setup = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                hosted.Client.CallAsync("account/bedrock/setup", new { type = "profile", profile = "default", region = "us-east-1" }));
            Assert.Contains("not implemented", setup.Message);
            var login = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                hosted.Client.CallAsync("account/login/start", new { type = "amazonBedrock", apiKey = "x", region = "us-east-1" }));
            Assert.Contains("Bedrock", login.Message);
            var consume = await hosted.Client.CallAsync("account/rateLimitResetCredit/consume", new { idempotencyKey = "k1" });
            Assert.Equal("noCredit", consume.GetProperty("outcome").GetString());
            var nudge = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                hosted.Client.CallAsync("account/sendAddCreditsNudgeEmail", new { creditType = "credits" }));
            Assert.Contains("ChatGPT", nudge.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE", prevCreds);
            Environment.SetEnvironmentVariable("AWS_CONFIG_FILE", prevCfg);
            Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", prevKey);
            Environment.SetEnvironmentVariable("AWS_DEFAULT_REGION", prevRegion);
            try { File.Delete(creds); } catch { /* ignore */ }
        }
    }
}


public class AccountRateLimitsTests
{
    [Fact]
    public async Task Read_returns_empty_snapshot()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        await client.NotifyAsync("initialized");
        var limits = await client.CallAsync("account/rateLimits/read");
        Assert.True(limits.GetProperty("ordinaryUsageAllowed").GetBoolean());
        Assert.True(limits.TryGetProperty("rateLimits", out _));
    }
}


public class ExperimentalApiTests
{
    [Fact]
    public async Task Queue_requires_opt_in()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        await client.NotifyAsync("initialized");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CallAsync("thread/queue/add", new { threadId = "x", input = new[] { new { type = "text", text = "hi" } } }));
        Assert.Contains("experimentalApi", ex.Message);
    }

    [Fact]
    public async Task Opt_in_is_echoed()
    {
        await using var hosted = InProcessAppServer.Start();
        var init = await hosted.Client.CallAsync("initialize", new
        {
            clientInfo = new { name = "t", title = "t", version = "0" },
            capabilities = new { experimentalApi = true },
        });
        Assert.True(init.GetProperty("experimentalApi").GetBoolean());
    }
}


public class ExperimentalFieldTests
{
    [Fact]
    public async Task Thread_start_fallback_field_is_gated()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CallAsync("thread/start", new { cwd = Path.GetTempPath(), allowProviderModelFallback = true }));
        Assert.Contains("thread/start.allowProviderModelFallback", ex.Message);
    }

    [Fact]
    public async Task Mock_experimental_method_is_gated()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CallAsync("mock/experimentalMethod"));
        Assert.Contains("mock/experimentalMethod", ex.Message);
        await using var hosted2 = InProcessAppServer.Start();
        var ok = await hosted2.Client.CallAsync("initialize", new
        {
            clientInfo = new { name = "t", title = "t", version = "0" },
            capabilities = new { experimentalApi = true },
        });
        Assert.True(ok.GetProperty("experimentalApi").GetBoolean());
        var mock = await hosted2.Client.CallAsync("mock/experimentalMethod");
        Assert.True(mock.GetProperty("ok").GetBoolean());
    }
}


public class ThreadTimelineTests
{
    [Fact]
    public async Task Requires_experimental_api()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CallAsync("thread/timeline/list", new { threadId = "x" }));
        Assert.Contains("experimentalApi", ex.Message);
    }

    [Fact]
    public async Task Returns_turn_boundaries()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var threadId = await session.StartThreadAsync(Path.GetTempPath(), "tl");
        var listed = await hosted.Client.CallAsync("thread/timeline/list", new { threadId });
        Assert.Equal(JsonValueKind.Array, listed.GetProperty("data").ValueKind);
    }
}


public class TimelinePagingTests
{
    [Fact]
    public void Slices_and_returns_next_cursor()
    {
        var items = Enumerable.Range(0, 5).ToArray();
        var (page, next, back) = TimelinePaging.Slice(items, null, 2);
        Assert.Equal([0, 1], page);
        Assert.Equal("2", next);
        Assert.Equal("0", back);
        var (page2, next2, back2) = TimelinePaging.Slice(items, next, 2);
        Assert.Equal([2, 3], page2);
        Assert.Equal("4", next2);
        Assert.Equal("2", back2);
        var (page3, next3, back3) = TimelinePaging.Slice(items, next2, 2);
        Assert.Equal([4], page3);
        Assert.Null(next3);
        Assert.Equal("4", back3);
    }

    [Fact]
    public void Descending_starts_from_end()
    {
        var items = Enumerable.Range(0, 5).ToArray();
        var (page, next, back) = TimelinePaging.Slice(items, null, 2, descending: true);
        Assert.Equal([4, 3], page);
        Assert.Equal("2", next);
        Assert.Equal("0", back);
    }
}


public class ThreadSearchOccurrencesTests
{
    [Fact]
    public void Finds_case_insensitive_snippet()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
            cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", root, false, 8);
            var session = CodexSession.Start(cfg, "occ");
            session.RestoreHistory(
            [
                new HistoryMessage("user", "please find the Pineapple token", "", "", ""),
                new HistoryMessage("assistant", "no fruit here", "", "", ""),
            ]);
            var hits = session.SearchOccurrences("pineapple");
            Assert.Single(hits);
            Assert.Contains("Pineapple", hits[0].Snippet);
            Assert.True(hits[0].End > hits[0].Start);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task App_server_requires_experimental_api()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CallAsync("thread/searchOccurrences", new { threadId = "x", searchTerm = "a" }));
        Assert.Contains("experimentalApi", ex.Message);
    }
}


public class ItemsViewTests
{
    [Fact]
    public void Summary_truncates_full_keeps_all()
    {
        var longText = new string('a', 250);
        var summary = ItemsView.Text(longText, "summary");
        Assert.Equal(201, summary.Length);
        Assert.EndsWith("…", summary);
        Assert.Equal(longText, ItemsView.Text(longText, "full"));
        Assert.Equal("short", ItemsView.Text("short", null));
    }
}


public class ThreadSectionProtocolTests
{
    [Fact]
    public async Task Create_list_move_update_and_delete()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var threadId = await session.StartThreadAsync(Path.GetTempPath(), "sectioned");
            var created = await hosted.Client.CallAsync("threadSection/create", new { name = "Work", appearance = new { color = "#f5c36c", icon = "folder" } });
            var sectionId = created.GetProperty("section").GetProperty("id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(sectionId));
            Assert.Equal("Work", created.GetProperty("section").GetProperty("name").GetString());

            await hosted.Client.CallAsync("thread/section/move", new { threadId, sectionId });
            Assert.Equal(sectionId, ThreadSections.SectionOf(threadId));

            var listed = await hosted.Client.CallAsync("threadSection/list", new { limit = 10 });
            Assert.Contains(listed.GetProperty("data").EnumerateArray(), e => e.GetProperty("id").GetString() == sectionId);

            var updated = await hosted.Client.CallAsync("threadSection/update", new { sectionId, name = "Shipped" });
            Assert.Equal("Shipped", updated.GetProperty("section").GetProperty("name").GetString());

            await hosted.Client.CallAsync("thread/section/move", new { threadId, sectionId = (string?)null });
            Assert.Null(ThreadSections.SectionOf(threadId));

            await hosted.Client.CallAsync("threadSection/delete", new { sectionId });
            var after = await hosted.Client.CallAsync("threadSection/list");
            Assert.DoesNotContain(after.GetProperty("data").EnumerateArray(), e => e.GetProperty("id").GetString() == sectionId);
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


public class CommandExecProtocolTests
{
    [Fact]
    public async Task Buffered_echo_returns_exit_code_and_stdout()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        await client.NotifyAsync("initialized");
        var result = await client.CallAsync("command/exec", new { command = new[] { "echo", "codexsharp-exec" } });
        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
        Assert.Contains("codexsharp-exec", result.GetProperty("stdout").GetString() + result.GetProperty("output").GetString());
    }

    [Fact]
    public async Task Write_without_session_errors()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CallAsync("command/exec/write", new { processId = "missing", closeStdin = true }));
        Assert.Contains("No active command exec session", ex.Message);
    }

    [Fact]
    public async Task Stream_stdin_write_and_close()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        await client.NotifyAsync("initialized");
        var exec = client.CallAsync("command/exec", new
        {
            command = new[] { "powershell.exe", "-NoLogo", "-NoProfile", "-Command", "[Console]::In.ReadToEnd()" },
            processId = "p-stdin",
            streamStdin = true,
            timeoutMs = 8000,
        });
        await Task.Delay(400);
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("hello-stdin"));
        await client.CallAsync("command/exec/write", new { processId = "p-stdin", deltaBase64 = payload, closeStdin = true });
        var result = await exec;
        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
        Assert.Contains("hello-stdin", result.GetProperty("stdout").GetString());
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

public class ModelProviderCapabilitiesTests
{
    [Fact]
    public async Task Reads_capability_flags()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        var caps = await client.CallAsync("model/provider/capabilities/read");
        Assert.True(caps.GetProperty("namespaceTools").GetBoolean());
        Assert.False(caps.GetProperty("imageGeneration").GetBoolean());
        Assert.True(caps.TryGetProperty("webSearch", out _));
    }
}

public class AppsReadTests
{
    [Fact]
    public async Task Returns_requested_ids()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        var listed = await client.CallAsync("apps/read", new { appIds = new[] { "browser", "browser" } });
        Assert.Equal(1, listed.GetProperty("data").GetArrayLength());
        Assert.Equal("browser", listed.GetProperty("data")[0].GetProperty("id").GetString());
    }
}


public class ThreadListFilterTests
{
    [Fact]
    public async Task Lists_active_then_archived_and_unarchives()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var threadId = await session.StartThreadAsync(Path.GetTempPath(), "list-me");
            var listed = await hosted.Client.CallAsync("thread/list", new { limit = 20 });
            Assert.Contains(listed.GetProperty("data").EnumerateArray(), e => e.GetProperty("thread").GetProperty("id").GetString() == threadId);
            Assert.True(listed.TryGetProperty("nextCursor", out _));

            await session.ArchiveAsync();
            var archived = await hosted.Client.CallAsync("thread/list", new { archived = true });
            Assert.Contains(archived.GetProperty("data").EnumerateArray(), e => e.GetProperty("thread").GetProperty("id").GetString() == threadId);
            var active = await hosted.Client.CallAsync("thread/list", new { archived = false });
            Assert.DoesNotContain(active.GetProperty("data").EnumerateArray(), e => e.GetProperty("thread").GetProperty("id").GetString() == threadId);

            await hosted.Client.CallAsync("thread/unarchive", new { threadId });
            var restored = await hosted.Client.CallAsync("thread/list");
            Assert.Contains(restored.GetProperty("data").EnumerateArray(), e => e.GetProperty("thread").GetProperty("id").GetString() == threadId);

            await hosted.Client.CallAsync("thread/delete", new { threadId });
            var after = await hosted.Client.CallAsync("thread/list");
            Assert.DoesNotContain(after.GetProperty("data").EnumerateArray(), e => e.GetProperty("thread").GetProperty("id").GetString() == threadId);
            var other = await session.StartThreadAsync(Path.GetTempPath(), "other");
            await session.ArchiveAsync(other);
            var archivedOther = await hosted.Client.CallAsync("thread/list", new { archived = true });
            Assert.Contains(archivedOther.GetProperty("data").EnumerateArray(), e => e.GetProperty("thread").GetProperty("id").GetString() == other);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Rejects_hosted_originators_filter()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CallAsync("thread/list", new { originators = new[] { "vscode" } }));
        Assert.Contains("hosted-only", ex.Message);
    }
}


public class AppServerSidebarClientTests
{
    [Fact]
    public async Task LoadThreadsAsync_includes_pin_and_section()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var threadId = await session.StartThreadAsync(Path.GetTempPath(), "sidebar");
            await session.SetPinnedAsync(threadId, true);
            var created = await session.CreateSectionAsync("Desk");
            var sectionId = created.GetProperty("section").GetProperty("id").GetString();
            await session.MoveToSectionAsync(threadId, sectionId);
            var rows = await session.LoadThreadsAsync();
            var row = Assert.Single(rows, r => r.Id == threadId);
            Assert.True(row.Pinned);
            Assert.Equal(sectionId, row.SectionId);
            var sections = await session.LoadSectionsAsync();
            Assert.Contains(sections, s => s.Id == sectionId && s.Name == "Desk");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}


public class ConfigWriteClientTests
{
    [Fact]
    public async Task WriteConfigAsync_then_read_sees_personality()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            await session.WriteConfigAsync("personality", "friendly");
            await session.WriteConfigAsync("collaboration_mode", "plan");
            var read = await session.ReadConfigAsync();
            Assert.Equal("friendly", read.GetProperty("personality").GetString());
            Assert.Equal("plan", read.GetProperty("collaborationMode").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}


public class DesktopChromeClientTests
{
    [Fact]
    public async Task LoadChromeAsync_returns_status_line()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var chrome = await session.LoadChromeAsync();
            Assert.False(string.IsNullOrWhiteSpace(chrome.Status));
            Assert.Contains("·", chrome.Status);
            Assert.False(string.IsNullOrWhiteSpace(chrome.SandboxNote));
            Assert.False(string.IsNullOrWhiteSpace(chrome.SideNotes));
            Assert.Contains("MCP", chrome.SideNotes);
            Assert.Equal("disabled", chrome.RemoteControlStatus);
            Assert.Contains("Remote control", chrome.SideNotes);
            Assert.False(string.IsNullOrWhiteSpace(chrome.RateLimitsNote));
            Assert.NotNull(chrome.TuiVim);
            Assert.NotNull(chrome.CollaborationMode);
            Assert.NotNull(chrome.Personality);
            var mentions = await session.MentionHitsAsync("hello");
            Assert.Empty(mentions);
            Assert.NotEmpty(chrome.Models);
            await session.LoginApiKeyAsync("sk-test-chrome");
            chrome = await session.LoadChromeAsync();
            Assert.DoesNotContain("no api key", chrome.Status);
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


public class StreamingExecNotificationTests
{
    [Fact]
    public async Task Streaming_exec_emits_output_delta()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var chunks = new List<string>();
        session.ExecOutput += (_, text) => chunks.Add(text);
        var result = await session.StartStreamingExecAsync("p-delta", "echo codexsharp-delta");
        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
        Assert.Contains(chunks, c => c.Contains("codexsharp-delta", StringComparison.OrdinalIgnoreCase));
    }
}


public class CommandExecTtyTests
{
    [Fact]
    public async Task Tty_exec_completes()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        await client.NotifyAsync("initialized");
        var result = await client.CallAsync("command/exec", new
        {
            command = new[] { "echo", "pty-hi" },
            processId = "pty1",
            tty = true,
            size = new { rows = 24, cols = 80 },
            timeoutMs = 8000,
        });
        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
    }
}


public class McpResourceReadProtocolTests
{
    [Fact]
    public async Task Unknown_server_is_error()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CallAsync("mcpServer/resource/read", new { server = "missing", uri = "file:///x" }));
    }

    [Fact]
    public async Task Reload_lists_configured_servers()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var reloaded = await session.ReloadMcpAsync();
        Assert.Equal(JsonValueKind.Array, reloaded.GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task Stdio_resources_read_roundtrip()
    {
        var toServer = new LineChannel();
        var fromServer = new LineChannel();
        var client = new McpStdioClient(fromServer.Reader, toServer.Writer);
        var server = Task.Run(async () =>
        {
            var init = await toServer.Reader.ReadLineAsync();
            Assert.Contains("initialize", init);
            await fromServer.Writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"serverInfo\":{\"name\":\"fake\",\"version\":\"1\"}}}");
            var initialized = await toServer.Reader.ReadLineAsync();
            Assert.Contains("initialized", initialized);
            var read = await toServer.Reader.ReadLineAsync();
            Assert.Contains("resources/read", read);
            await fromServer.Writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"contents\":[{\"uri\":\"memo://x\",\"text\":\"hello-resource\"}]}}");
        });

        await client.InitializeAsync();
        var result = await client.ReadResourceAsync("memo://x");
        Assert.Contains("hello-resource", result.GetRawText());
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }
}


public class FeedbackUploadTests
{
    [Fact]
    public async Task Saves_local_snapshot_without_api_key()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "config.toml"), "model = \"x\"\napi_key = \"sk-secret\"\n");
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var result = await session.UploadFeedbackAsync("bug", "repro");
            var id = result.GetProperty("threadId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(id));
            var path = Path.Combine(home, "feedback", id + ".json");
            Assert.True(File.Exists(path));
            var json = File.ReadAllText(path);
            Assert.Contains("bug", json);
            Assert.DoesNotContain("sk-secret", json);
            Assert.Contains("***", json);
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


public class RemoteControlProtocolTests
{
    [Fact]
    public async Task Status_requires_experimental_api()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CallAsync("remoteControl/status/read"));
        Assert.Contains("experimentalApi", ex.Message);
    }

    [Fact]
    public async Task Enable_reports_errored_without_remote_transport()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            await hosted.Client.CallAsync("remoteControl/enable");
            var status = await hosted.Client.CallAsync("remoteControl/status/read");
            Assert.Equal("errored", status.GetProperty("status").GetString());
            Assert.Equal("codexsharp", status.GetProperty("serverName").GetString());
            await hosted.Client.CallAsync("remoteControl/disable");
            status = await hosted.Client.CallAsync("remoteControl/status/read");
            Assert.Equal("disabled", status.GetProperty("status").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Pairing_start_never_claims_without_transport()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var started = await hosted.Client.CallAsync("remoteControl/pairing/start", new { manualCode = true });
            var code = started.GetProperty("pairingCode").GetString();
            var manual = started.GetProperty("manualPairingCode").GetString();
            Assert.False(string.IsNullOrWhiteSpace(code));
            Assert.False(string.IsNullOrWhiteSpace(manual));
            Assert.True(started.GetProperty("expiresAt").GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var status = await hosted.Client.CallAsync("remoteControl/pairing/status", new { pairingCode = code });
            Assert.False(status.GetProperty("claimed").GetBoolean());
            var both = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                hosted.Client.CallAsync("remoteControl/pairing/status", new { pairingCode = code, manualPairingCode = manual }));
            Assert.Contains("not both", both.Message);
            var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                hosted.Client.CallAsync("remoteControl/pairing/status", new { }));
            Assert.Contains("requires pairingCode", missing.Message);
            var clients = await hosted.Client.CallAsync("remoteControl/client/list", new { environmentId = started.GetProperty("environmentId").GetString() });
            Assert.Empty(clients.GetProperty("data").EnumerateArray());
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

public class QueueUpdateReorderTests
{
    [Fact]
    public async Task Update_and_reorder_queued_submissions()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        await session.StartThreadAsync(Path.GetTempPath(), "q2");
        await session.QueueAddAsync("one");
        await session.QueueAddAsync("two");
        var listed = await session.QueueListAsync();
        var ids = listed.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(2, ids.Length);
        await session.QueueUpdateAsync(ids[0], "one-edited");
        await session.QueueReorderAsync([ids[1], ids[0]]);
        listed = await session.QueueListAsync();
        var texts = listed.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("text").GetString() ?? "").ToArray();
        Assert.Equal(new[] { "two", "one-edited" }, texts);
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


public class ChatgptDeviceCodeTests
{
    [Fact]
    public async Task Device_code_persists_chatgpt_tokens_without_live_network()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        ChatgptDeviceAuth.TestHandler = new DeviceAuthStub();
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var started = await session.LoginDeviceCodeAsync();
            Assert.Equal("chatgptDeviceCode", started.GetProperty("type").GetString());
            Assert.Equal("WD12-XY34", started.GetProperty("userCode").GetString());
            Assert.Contains("/codex/device", started.GetProperty("verificationUrl").GetString());
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && AuthService.AuthMode() != "chatgpt")
            {
                await Task.Delay(20);
            }
            Assert.Equal("chatgpt", AuthService.AuthMode());
            var chrome = await session.LoadChromeAsync();
            Assert.False(chrome.NeedsAuth);
            Assert.Contains("chatgpt", chrome.Status, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ChatgptDeviceAuth.TestHandler = null;
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    private sealed class DeviceAuthStub : HttpMessageHandler
    {
        private int _polls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/deviceauth/usercode", StringComparison.Ordinal))
            {
                return Json(200, """{"device_auth_id":"dev_1","user_code":"WD12-XY34","interval":"0"}""");
            }

            if (path.EndsWith("/deviceauth/token", StringComparison.Ordinal))
            {
                var n = Interlocked.Increment(ref _polls);
                if (n < 2)
                {
                    return Json(403, "{}");
                }

                return Json(200, """{"authorization_code":"ac_1","code_challenge":"ch","code_verifier":"ver"}""");
            }

            if (path.EndsWith("/oauth/token", StringComparison.Ordinal))
            {
                return Json(200, """{"access_token":"at_test","refresh_token":"rt_test","id_token":"id_test"}""");
            }

            return Json(404, "{}");
        }

        private static Task<HttpResponseMessage> Json(int status, string body)
        {
            var resp = new HttpResponseMessage((System.Net.HttpStatusCode)status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(resp);
        }
    }
}


public class ChatgptBrowserOauthTests
{
    [Fact]
    public async Task Browser_callback_exchanges_code_without_live_network()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        var tcp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((System.Net.IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        var previous = ChatgptBrowserAuth.Ports;
        ChatgptBrowserAuth.Ports = [port];
        ChatgptDeviceAuth.TestHandler = new TokenStub();
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var started = await session.LoginChatgptAsync();
            Assert.Equal("chatgpt", started.GetProperty("type").GetString());
            var authUrl = started.GetProperty("authUrl").GetString()!;
            Assert.Contains("oauth/authorize", authUrl);
            Assert.Contains("code_challenge", authUrl);
            var query = new Uri(authUrl).Query.TrimStart('?');
            var state = query.Split('&').Select(p => p.Split('=', 2)).Where(p => p.Length == 2 && p[0] == "state").Select(p => Uri.UnescapeDataString(p[1])).FirstOrDefault();
            Assert.False(string.IsNullOrWhiteSpace(state));
            using var probe = new HttpClient();
            var callback = $"http://127.0.0.1:{port}/auth/callback?code=browser_code&state={Uri.EscapeDataString(state!)}";
            var page = await probe.GetAsync(callback);
            Assert.True(page.IsSuccessStatusCode);
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && AuthService.AuthMode() != "chatgpt")
            {
                await Task.Delay(20);
            }
            Assert.Equal("chatgpt", AuthService.AuthMode());
        }
        finally
        {
            ChatgptBrowserAuth.Ports = previous;
            ChatgptDeviceAuth.TestHandler = null;
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    private sealed class TokenStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/oauth/token", StringComparison.Ordinal))
            {
                var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"at_browser","refresh_token":"rt","id_token":"id"}""", System.Text.Encoding.UTF8, "application/json"),
                };
                return Task.FromResult(resp);
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}


public class WindowsSandboxSetupTests
{
    [Fact]
    public async Task Unelevated_setup_marks_ready_when_job_object_exists()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var before = await session.WindowsSandboxReadinessAsync();
            Assert.Equal("notConfigured", before.GetProperty("status").GetString());
            var started = await session.SetupWindowsSandboxAsync("unelevated", Path.GetTempPath());
            Assert.True(started.GetProperty("started").GetBoolean());
            var deadline = DateTime.UtcNow.AddSeconds(3);
            JsonElement after = default;
            while (DateTime.UtcNow < deadline)
            {
                after = await session.WindowsSandboxReadinessAsync();
                if (after.GetProperty("status").GetString() == "ready") break;
                await Task.Delay(20);
            }
            if (JobObject.IsAvailable())
            {
                Assert.Equal("ready", after.GetProperty("status").GetString());
                Assert.True(after.GetProperty("jobObject").GetBoolean());
            }
            else
            {
                Assert.Equal("notConfigured", after.GetProperty("status").GetString());
            }

            var elevated = WindowsSandbox.Setup("elevated", null);
            Assert.Equal("notConfigured", elevated.Status);
            Assert.Contains("not shipped", elevated.Error);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}


public class ProcessSpawnProtocolTests
{
    [Fact]
    public async Task Spawn_echoes_and_exits_without_sandbox()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" }, capabilities = new { experimentalApi = true } });
        var exited = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Notification += el =>
        {
            if (el.TryGetProperty("method", out var m) && m.GetString() == "process/exited" && el.TryGetProperty("params", out var p))
            {
                exited.TrySetResult(p.Clone());
            }
        };
        var spawn = await client.CallAsync("process/spawn", new { command = new[] { "cmd", "/c", "echo spawn-hi" }, processHandle = "h-spawn", cwd = Path.GetTempPath() });
        Assert.Equal(JsonValueKind.Object, spawn.ValueKind);
        var done = await exited.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(0, done.GetProperty("exitCode").GetInt32());
        Assert.Contains("spawn-hi", done.GetProperty("stdout").GetString() + done.GetProperty("stderr").GetString(), StringComparison.OrdinalIgnoreCase);
    }
}

public class MemoryProtocolTests
{
    [Fact]
    public async Task Set_mode_and_reset_memories_dir()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "memories"));
            File.WriteAllText(Path.Combine(home, "memories", "note.md"), "keep?");
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            await hosted.Client.CallAsync("thread/memoryMode/set", new { threadId = "thr_mem", mode = "disabled" });
            Assert.Equal("disabled", MemoryStore.ModeOf("thr_mem"));
            await hosted.Client.CallAsync("memory/reset");
            Assert.True(Directory.Exists(MemoryStore.MemoriesRoot));
            Assert.Empty(Directory.GetFiles(MemoryStore.MemoriesRoot));
            Assert.Empty(MemoryStore.ListNotes());
            Assert.Equal("disabled", MemoryStore.ModeOf("thr_mem"));
            var written = await session.WriteMemoryAsync("login", "prefer the existing button");
            Assert.Equal("login.md", written.GetProperty("name").GetString());
            var read = await session.ReadMemoryAsync("login.md");
            Assert.Contains("existing button", read.GetProperty("text").GetString());
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => session.WriteMemoryAsync("../escape", "nope"));
            Assert.Contains("invalid memory name", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}


public class FuzzySearchSessionTests
{
    [Fact]
    public async Task Session_update_notifies_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-fuzzy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "alpha-needle.txt"), "x");
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" }, capabilities = new { experimentalApi = true } });
        var updated = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Notification += el =>
        {
            if (el.TryGetProperty("method", out var m) && m.GetString() == "fuzzyFileSearch/sessionUpdated" && el.TryGetProperty("params", out var p))
                updated.TrySetResult(p.Clone());
        };
        await client.CallAsync("fuzzyFileSearch/sessionStart", new { sessionId = "s1", roots = new[] { root } });
        await client.CallAsync("fuzzyFileSearch/sessionUpdate", new { sessionId = "s1", query = "needle" });
        var payload = await updated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(payload.GetProperty("files").EnumerateArray(), f => (f.GetProperty("path").GetString() ?? "").Contains("alpha-needle"));
        await client.CallAsync("fuzzyFileSearch/sessionStop", new { sessionId = "s1" });
    }
}

public class TurnSettingsUpdateTests
{
    [Fact]
    public async Task Idle_thread_is_target_unavailable()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        await session.StartThreadAsync(Path.GetTempPath(), "t");
        var result = await hosted.Client.CallAsync("turn/settings/update", new { threadId = session.ThreadId, turnId = "none", model = "gpt-5" });
        Assert.Equal("targetUnavailable", result.GetProperty("status").GetString());
    }
}


public class EnvironmentAndDiagnosticsTests
{
    [Fact]
    public async Task Local_ready_remote_pending_unknown_and_diagnostics()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var local = await hosted.Client.CallAsync("environment/status", new { environmentId = "local" });
        Assert.Equal("ready", local.GetProperty("status").GetString());
        var missing = await hosted.Client.CallAsync("environment/status", new { environmentId = "nope" });
        Assert.Equal("unknown", missing.GetProperty("status").GetString());
        await hosted.Client.CallAsync("environment/add", new { environmentId = "remote-1", execServerUrl = "ws://127.0.0.1:9" });
        var remote = await hosted.Client.CallAsync("environment/status", new { environmentId = "remote-1" });
        Assert.Equal("pending", remote.GetProperty("status").GetString());
        Assert.Contains("exec-server", remote.GetProperty("error").GetString());
        var diag = await hosted.Client.CallAsync("server/diagnostics");
        Assert.True(diag.GetProperty("process").GetProperty("id").GetInt32() > 0);
        var terms = await hosted.Client.CallAsync("thread/backgroundTerminals/list", new { threadId = "none" });
        Assert.Equal(JsonValueKind.Array, terms.GetProperty("data").ValueKind);
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


public class ElicitationAndTimeTests
{
    [Fact]
    public async Task Time_elicitation_and_mcp_stream_ids()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var time = await hosted.Client.CallAsync("currentTime/read", new { threadId = "t" });
        Assert.True(time.GetProperty("currentTimeAt").GetInt64() > 1_700_000_000);
        var up = await hosted.Client.CallAsync("thread/increment_elicitation", new { threadId = "thr_e" });
        Assert.Equal(1, up.GetProperty("count").GetInt32());
        var down = await hosted.Client.CallAsync("thread/decrement_elicitation", new { threadId = "thr_e" });
        Assert.Equal(0, down.GetProperty("count").GetInt32());
        await hosted.Client.CallAsync("mcpServer/event/stream/start", new { threadId = "t", server = "s", subscriptionId = "sub1", name = "n", arguments = new { } });
        await hosted.Client.CallAsync("mcpServer/event/stream/stop", new { subscriptionId = "sub1" });
    }
}


public class AuthRefreshAndToolCallTests
{
    [Fact]
    public async Task Refresh_tool_call_workspace_guardian()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        ChatgptDeviceAuth.TestHandler = new RefreshStub();
        try
        {
            AuthService.SaveChatgptTokens("old", "rt_old", "id_old", "acct");
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var refreshed = await hosted.Client.CallAsync("account/chatgptAuthTokens/refresh", new { reason = "unauthorized" });
            Assert.Equal("at_new", refreshed.GetProperty("accessToken").GetString());
            Assert.Equal("at_new", AuthService.ReadTokens().Access);
            await session.StartThreadAsync(Path.GetTempPath(), "tools");
            var call = await hosted.Client.CallAsync("item/tool/call", new { threadId = session.ThreadId, turnId = "t", callId = "c1", tool = "current_time", arguments = new { } });
            Assert.True(call.GetProperty("success").GetBoolean());
            var msgs = await hosted.Client.CallAsync("account/workspaceMessages/read");
            Assert.Equal(JsonValueKind.Array, msgs.GetProperty("messages").ValueKind);
            var g = await hosted.Client.CallAsync("thread/approveGuardianDeniedAction", new { threadId = session.ThreadId });
            Assert.False(g.GetProperty("approved").GetBoolean());
        }
        finally
        {
            ChatgptDeviceAuth.TestHandler = null;
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    private sealed class RefreshStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"at_new","refresh_token":"rt_new","id_token":"id_new"}""", System.Text.Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(resp);
        }
    }
}

public class MemoryPromptTests
{
    [Fact]
    public void Injects_memories_until_disabled()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "memories"));
            File.WriteAllText(Path.Combine(home, "memories", "pref.md"), "prefer tabs");
            var cfg = ConfigService.Load(Path.GetTempPath());
            var withMem = Prompt.buildWithTools(cfg, [new HistoryMessage("user", "hi", "", "", "")], [], "thr_mem");
            Assert.Contains(withMem.Messages, m => m.Content.Contains("prefer tabs"));
            MemoryStore.SetMode("thr_mem", "disabled");
            var without = Prompt.buildWithTools(cfg, [new HistoryMessage("user", "hi", "", "", "")], [], "thr_mem");
            Assert.DoesNotContain(without.Messages, m => m.Content.Contains("prefer tabs"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class UserInputProtocolTests
{
    [Fact]
    public async Task Request_user_input_waits_for_answers()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
        cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", root, false, 8);
        var model = new ScriptedModelClient(
            [
                ModelStreamEvent.NewToolCallReady(new ToolCallRequest("c1", "request_user_input", "{\"questions\":[\"Ship it?\"]}")),
                ModelStreamEvent.NewStreamFinished("tool"),
            ],
            [
                ModelStreamEvent.NewOutputTextDelta("got it"),
                ModelStreamEvent.NewStreamFinished("stop"),
            ]);
        var sink = new RecordingSink();
        var input = new InteractiveUserInput();
        var deps = new AgentDeps(cfg, model, new BuiltinToolExecutor(cfg), new AutoApprover(true), sink, [], new NoopHookHost(), input, new NoopSteerHost());
        var loop = AgentLoop.runTurn(deps, "thr_ui", new List<HistoryMessage>(), "ask me", CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && !sink.Events.Any(e => e is AgentEvent.UserInputNeeded))
        {
            await Task.Delay(10);
        }
        var needed = Assert.Single(sink.Events.OfType<AgentEvent.UserInputNeeded>());
        Assert.True(input.Complete(needed.RequestId, "{\"answers\":{\"q1\":\"yes\"}}"));
        await loop;
        Assert.Contains(sink.Events, e => e is AgentEvent.TurnCompleted);
    }
}

public class SandboxReadRootTests
{
    [Fact]
    public void Extra_read_root_is_allowed()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-ws-" + Guid.NewGuid().ToString("N"));
            var extra = Path.Combine(Path.GetTempPath(), "codexsharp-extra-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(cwd);
            Directory.CreateDirectory(extra);
            File.WriteAllText(Path.Combine(extra, "outside.txt"), "hello-extra");
            var cfg = ConfigService.Load(cwd, sandboxOverride: "workspace-write");
            cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", cwd, false, 8);
            var sandbox = new WorkspaceSandbox(cfg);
            Assert.Throws<InvalidOperationException>(() => sandbox.EnsureReadable(Path.Combine(extra, "outside.txt")));
            SandboxRoots.Add(extra);
            sandbox.EnsureReadable(Path.Combine(extra, "outside.txt"));
            var prompt = Prompt.build(cfg, [new HistoryMessage("user", "hi", "", "", "")]);
            var blob = string.Join("\n", prompt.Messages.Select(m => m.Content));
            Assert.Contains("<extra_readonly_root>", blob);
            Assert.Contains(Path.GetFileName(extra), blob);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}


public class TokenUsageAndRequirementsTests
{
    [Fact]
    public async Task Turn_emits_local_token_estimate()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
            cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", root, false, 8);
            var session = CodexSession.Start(cfg, "tok");
            AgentEvent.TokenUsage? usage = null;
            session.Event += e =>
            {
                if (e is AgentEvent.TokenUsage t) usage = t;
            };
            var model = new ScriptedModelClient(
                [
                    ModelStreamEvent.NewOutputTextDelta("hello tokens"),
                    ModelStreamEvent.NewStreamFinished("stop"),
                ]);
            await session.RunTurnWithAsync(model, null, new AutoApprover(true), "count me");
            Assert.NotNull(usage);
            Assert.True(usage!.Total >= 0);
            var (total, last) = session.EstimateTokens();
            Assert.Equal(total, usage.Total);
            Assert.True(last >= 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Config_requirements_are_null_without_mdm()
    {
        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync();
        var req = await hosted.Client.CallAsync("configRequirements/read");
        Assert.Equal(JsonValueKind.Null, req.GetProperty("requirements").ValueKind);
        Assert.False(req.GetProperty("allowBrowserAndComputerUse").GetBoolean());
    }
}

public class McpElicitationTests
{
    [Fact]
    public async Task Call_handles_elicitation_then_tool_result()
    {
        var toServer = new LineChannel();
        var fromServer = new LineChannel();
        var client = new McpStdioClient(fromServer.Reader, toServer.Writer);
        client.ElicitationHandler = (el, _) =>
        {
            Assert.Equal("Need a path", el.GetProperty("message").GetString());
            using var doc = JsonDocument.Parse("{\"action\":\"accept\",\"content\":{\"text\":\"ok\"}}");
            return Task.FromResult(doc.RootElement.Clone());
        };
        var server = Task.Run(async () =>
        {
            var call = await toServer.Reader.ReadLineAsync();
            Assert.Contains("tools/call", call);
            await fromServer.Writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":99,\"method\":\"elicitation/create\",\"params\":{\"message\":\"Need a path\"}}");
            var reply = await toServer.Reader.ReadLineAsync();
            Assert.Contains("accept", reply);
            await fromServer.Writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"elicited\"}]}}");
        });
        var output = await client.CallToolAsync("ping", "{}");
        Assert.Equal("elicited", output);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
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

public class ShellOutputStreamTests
{
    [Fact]
    public async Task Echo_emits_command_output_chunks()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
        cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", root, false, 8);
        var chunks = new List<string>();
        var exec = new BuiltinToolExecutor(cfg, (_, text) => chunks.Add(text));
        var result = await exec.ExecuteAsync(new ToolCallRequest("c1", "shell", "{\"command\":[\"echo\",\"stream-hi\"]}"), CancellationToken.None);
        Assert.False(result.IsError, result.Output);
        Assert.Contains("stream-hi", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(chunks, c => c.Contains("stream-hi", StringComparison.OrdinalIgnoreCase));
    }
}

public class PlanModeToolGuardTests
{
    [Fact]
    public async Task Mutating_tools_fail_in_plan_mode()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(home, "config.toml"), "collaboration_mode = \"plan\"\n");
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
            cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", root, false, 8);
            var exec = new BuiltinToolExecutor(cfg);
            var plan = await exec.ExecuteAsync(new ToolCallRequest("1", "update_plan", "{\"plan\":[]}"), CancellationToken.None);
            Assert.True(plan.IsError);
            var write = await exec.ExecuteAsync(new ToolCallRequest("2", "write_file", "{\"path\":\"x.txt\",\"content\":\"n\"}"), CancellationToken.None);
            Assert.True(write.IsError);
            Assert.Contains("Plan Mode", write.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public void Apply_patch_reports_each_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
        cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", root, false, 8);
        var sandbox = new WorkspaceSandbox(cfg);
        var seen = new List<string>();
        var patch = """
            *** Begin Patch
            *** Add File: streamed.md
            +hello
            *** End Patch
            """;
        var result = ApplyPatchEngine.Apply(patch, sandbox, seen.Add);
        Assert.True(result.Ok, result.Output);
        Assert.Contains(seen, s => s.Contains("streamed.md"));
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

public class WorldWritableAndCompactEventTests
{
    [Fact]
    public void Missing_directory_scan_is_empty()
    {
        var report = WorldWritableScan.Scan(Path.Combine(Path.GetTempPath(), "codexsharp-missing-" + Guid.NewGuid().ToString("N")));
        Assert.Empty(report.SamplePaths);
        Assert.Equal(0, report.ExtraCount);
    }

    [Fact]
    public async Task TryCompact_emits_compacted_event()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
            cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", root, false, 8);
            var session = CodexSession.Start(cfg, "compact");
            AgentEvent.Compacted? seen = null;
            session.Event += e => { if (e is AgentEvent.Compacted c) seen = c; };
            for (var i = 0; i < 20; i++)
            {
                session.History.ToList(); // ensure history accessible
            }
            // Inject bulky history via public History if it's the live list
            var hist = (List<HistoryMessage>)session.History;
            hist.Clear();
            for (var i = 0; i < 20; i++)
                hist.Add(new HistoryMessage("tool", new string('x', 10_000), $"c{i}", "shell", ""));
            Assert.True(session.TryCompact());
            Assert.NotNull(seen);
            Assert.True(seen!.Dropped >= 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class StatuslineAndPersonalityTests
{
    [Fact]
    public void Personality_tag_and_statusline_format()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(home, "config.toml"), "personality = \"pragmatic\"\ntui_statusline = \"model,sandbox\"\n");
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var cfg = ConfigService.Load(root);
            Assert.Contains("<personality>pragmatic</personality>", cfg.DeveloperInstructions);
            var session = CodexSession.Start(cfg, "status");
            var line = TuiStatus.Line(session);
            Assert.Contains(cfg.Model, line);
            Assert.Contains(cfg.SandboxMode, line);
            Assert.StartsWith("CodexSharp — ", TuiStatus.Title(session));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public void Feedback_includeLogs_copies_session_jsonl()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            CodexPaths.EnsureLayout();
            Directory.CreateDirectory(CodexPaths.SessionsDir);
            File.WriteAllText(Path.Combine(CodexPaths.SessionsDir, "thr_fb.jsonl"), "{\"type\":\"session_meta\"}\n");
            var id = FeedbackStore.Save("bug", "n", "thr_fb", includeLogs: true);
            var json = File.ReadAllText(Path.Combine(FeedbackStore.Dir, id + ".json"));
            Assert.Contains("thr_fb.jsonl", json);
            Assert.DoesNotContain("sk-live-secret", json);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ExecPolicyWriteTests
{
    [Fact]
    public void AddUserRule_is_evaluated()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            ExecPolicy.AddUserRule(["sudo"], ExecDecision.Forbidden);
            Assert.Equal(ExecDecision.Forbidden, ExecPolicy.Evaluate("sudo apt install"));
            Assert.Equal(ExecDecision.Allow, ExecPolicy.Evaluate("dotnet build"));
            Assert.Contains("sudo", File.ReadAllText(Path.Combine(home, "rules", "user.rules")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ModelCatalogWriteTests
{
    [Fact]
    public void Add_persists_overlay_model()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var entry = ModelCatalog.Add("my-local-model", "lmstudio");
            Assert.Equal("my-local-model", entry.Id);
            Assert.Contains(ModelCatalog.List(), e => e.Id == "my-local-model" && e.Provider == "lmstudio");
            Assert.Contains("my-local-model", File.ReadAllText(Path.Combine(home, "models.json")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class DoctorProbeTests
{
    [Fact]
    public void Reports_sandbox_git_hooks_skills_execpolicy_writable_features()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-cwd-" + Guid.NewGuid().ToString("N"));
        var prevHome = Environment.GetEnvironmentVariable("CODEXSHARP_HOME");
        var prevKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var prevSharp = Environment.GetEnvironmentVariable("CODEXSHARP_API_KEY");
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        Environment.SetEnvironmentVariable("CODEXSHARP_API_KEY", null);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(cwd);
            Directory.CreateDirectory(Path.Combine(home, "hooks"));
            File.WriteAllText(Path.Combine(home, "hooks", "stop.json"), """{"name":"stop","event":"Stop","command":"echo"}""");
            var skillDir = Path.Combine(home, "skills", "demo");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# Demo" + Environment.NewLine);
            ExecPolicy.AddUserRule(["rm"], ExecDecision.Forbidden);
            FeatureFlags.Set("web_search", true);
            SandboxRoots.Add(cwd);

            var report = DoctorProbe.Run(cwd);
            Assert.Contains(report.Checks, c => c.Id == "sandbox" && c.Details.Any(d => d.Contains("workspace-write", StringComparison.OrdinalIgnoreCase)));
            Assert.Contains(report.Checks, c => c.Id == "git" && c.Summary.Contains("not a git repo", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(report.Checks, c => c.Id == "hooks" && c.Details.Any(d => d.Contains("stop")));
            Assert.Contains(report.Checks, c => c.Id == "skills" && c.Details.Any(d => d.Contains("demo")));
            Assert.Contains(report.Checks, c => c.Id == "execpolicy" && c.Details.Any(d => d.Contains("rm")));
            Assert.Contains(report.Checks, c => c.Id == "writable" && c.Details.Any(d => d.Contains(cwd, StringComparison.OrdinalIgnoreCase)));
            Assert.Contains(report.Checks, c => c.Id == "features" && c.Details.Any(d => d.Contains("web_search")));
            Assert.Contains(report.Checks, c => c.Id == "apps" && c.Details.Any(d => d.Contains("computerUse: notConfigured")) && c.Details.Any(d => d.Contains("browserUse: notConfigured")));
            var json = DoctorProbe.ToJson(report);
            Assert.Contains("\"id\": \"sandbox\"", json);
            Assert.Contains("\"id\": \"features\"", json);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", prevHome);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", prevKey);
            Environment.SetEnvironmentVariable("CODEXSHARP_API_KEY", prevSharp);
        }
    }
}

public class ConfigReadAppsTests
{
    [Fact]
    public async Task Config_read_reports_computer_and_browser_use_as_not_configured()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        await client.NotifyAsync("initialized");
        var read = await client.CallAsync("config/read");
        Assert.Equal("notConfigured", read.GetProperty("computerUse").GetString());
        Assert.Equal("notConfigured", read.GetProperty("browserUse").GetString());
    }
}

public class OfficialToolTests
{
    [Fact]
    public async Task Context_search_environment_and_plugins_are_local()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-cwd-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(cwd);
            var market = Path.Combine(Path.GetTempPath(), "codexsharp-market-" + Guid.NewGuid().ToString("N"));
            var plugin = Path.Combine(market, "demo-plugin");
            Directory.CreateDirectory(plugin);
            File.WriteAllText(Path.Combine(plugin, "plugin.json"), """{"name":"demo-plugin","description":"from marketplace"}""");
            MarketplaceStore.Add(market, "local");

            var cfg = ConfigService.Load(cwd);
            var exec = new BuiltinToolExecutor(cfg, estimateTokens: () => (120, 12));
            var context = await exec.ExecuteAsync(new ToolCallRequest("1", "get_context_remaining", "{}"), CancellationToken.None);
            Assert.False(context.IsError);
            Assert.Contains("tokens_used", context.Output);
            Assert.Contains("120", context.Output);

            var search = await exec.ExecuteAsync(new ToolCallRequest("2", "tool_search", """{"query":"plugin"}"""), CancellationToken.None);
            Assert.False(search.IsError);
            Assert.Contains("list_available_plugins_to_install", search.Output);

            var listed = await exec.ExecuteAsync(new ToolCallRequest("3", "list_available_plugins_to_install", "{}"), CancellationToken.None);
            Assert.False(listed.IsError);
            Assert.Contains("demo-plugin", listed.Output);

            var installed = await exec.ExecuteAsync(new ToolCallRequest("4", "request_plugin_install", """{"pluginName":"demo-plugin"}"""), CancellationToken.None);
            Assert.False(installed.IsError, installed.Output);
            Assert.True(Directory.Exists(Path.Combine(home, "plugins", "demo-plugin")));

            var waitLocal = await exec.ExecuteAsync(new ToolCallRequest("5", "wait_for_environment", """{"environment_id":"local"}"""), CancellationToken.None);
            Assert.False(waitLocal.IsError);
            Assert.Contains("ready", waitLocal.Output);

            var waitRemote = await exec.ExecuteAsync(new ToolCallRequest("6", "wait_for_environment", """{"environment_id":"missing"}"""), CancellationToken.None);
            Assert.True(waitRemote.IsError);
            Assert.Contains("unknown", waitRemote.Output);

            var mcp = await exec.ExecuteAsync(new ToolCallRequest("7", "list_mcp_resources", "{}"), CancellationToken.None);
            Assert.False(mcp.IsError);
            Assert.Contains("servers", mcp.Output);

            var msg = await exec.ExecuteAsync(new ToolCallRequest("8", "send_message_to_user", """{"message":"hello user"}"""), CancellationToken.None);
            Assert.False(msg.IsError);
            Assert.Equal("hello user", msg.Output);
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

public class UnifiedExecToolTests
{
    [Fact]
    public async Task Exec_command_requires_feature_flag()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-cwd-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(cwd);
            var cfg = ConfigService.Load(cwd);
            var exec = new BuiltinToolExecutor(cfg);
            var off = await exec.ExecuteAsync(new ToolCallRequest("1", "exec_command", """{"cmd":"echo hi","yield_time_ms":500}"""), CancellationToken.None);
            Assert.True(off.IsError);
            Assert.Contains("unified_exec is off", off.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Exec_command_yields_and_write_stdin_unknown_session()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-cwd-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(cwd);
            FeatureFlags.Set("unified_exec", true);
            var cfg = ConfigService.Load(cwd);
            var broker = new CommandExecBroker();
            var exec = new BuiltinToolExecutor(cfg, unified: broker);
            var cmd = OperatingSystem.IsWindows()
                ? """{"cmd":"echo unified-hello","yield_time_ms":4000}"""
                : """{"cmd":"echo unified-hello","yield_time_ms":4000}""";
            var started = await exec.ExecuteAsync(new ToolCallRequest("1", "exec_command", cmd), CancellationToken.None);
            Assert.False(started.IsError, started.Output);
            Assert.True(started.Output.Contains("unified-hello", StringComparison.OrdinalIgnoreCase), started.Output);

            var missing = await exec.ExecuteAsync(new ToolCallRequest("2", "write_stdin", """{"session_id":999,"chars":"x","yield_time_ms":50}"""), CancellationToken.None);
            Assert.True(missing.IsError);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class LocalModelDiscoverTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var json = path.Contains("tags", StringComparison.OrdinalIgnoreCase)
                ? """{"models":[{"name":"llama3.2"}]}"""
                : """{"data":[{"id":"local-qwen"}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
        }
    }

    [Fact]
    public void Discovers_ollama_and_lmstudio_models()
    {
        LocalModelDiscover.TestHandler = new StubHandler();
        try
        {
            var found = LocalModelDiscover.Probe(force: true);
            Assert.Contains(found, e => e.Id == "llama3.2" && e.Provider == "ollama");
            Assert.Contains(found, e => e.Id == "local-qwen" && e.Provider == "lmstudio");
        }
        finally
        {
            LocalModelDiscover.TestHandler = null;
        }
    }
}

public class RequestPermissionsTests
{
    [Fact]
    public async Task Grants_readonly_roots_without_enabling_network()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var extra = Path.Combine(Path.GetTempPath(), "codexsharp-read-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(extra);
            var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-cwd-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(cwd);
            var cfg = ConfigService.Load(cwd);
            var exec = new BuiltinToolExecutor(cfg);
            var json = JsonSerializer.Serialize(new { additional_readonly_roots = new[] { extra }, network = true, justification = "tests" });
            var result = await exec.ExecuteAsync(new ToolCallRequest("1", "request_permissions", json), CancellationToken.None);
            Assert.False(result.IsError, result.Output);
            Assert.Contains("granted_readonly_roots", result.Output);
            Assert.Contains("false", result.Output);
            Assert.Contains(SandboxRoots.List(), p => string.Equals(p, Path.GetFullPath(extra), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class MarkdownTests
{
    [Fact]
    public void Parses_headings_fences_and_bullets()
    {
        var blocks = Markdown.Parse("""
            # Title
            hello **world**
            ```cs
            var x = 1;
            ```
            - one
            - two
            """);
        Assert.Contains(blocks, b => b is MdHeading h && h.Level == 1 && h.Text == "Title");
        Assert.Contains(blocks, b => b is MdCode c && c.Lang == "cs" && c.Text.Contains("var x = 1;"));
        Assert.Contains(blocks, b => b is MdBullet bullet && bullet.Text == "one");
        Assert.Equal("hello world", Markdown.StripInline("hello **world**"));
        Assert.Contains("var x = 1;", Markdown.LastCode("""
            intro
            ```
            first
            ```
            ```cs
            var x = 1;
            ```
            """)!);
    }
}

public class NewContextWindowTests
{
    [Fact]
    public async Task Drops_history_without_summarizing()
    {
        var history = new List<HistoryMessage>
        {
            new("user", "keep me out of the summary", "", "", ""),
            new("assistant", "secret details", "", "", ""),
        };
        var dropped = Compact.resetWithoutSummary(history);
        Assert.Equal(2, dropped);
        Assert.DoesNotContain(history, m => m.Content.Contains("secret details"));
        Assert.Contains(history, m => m.Content.Contains("without summarization"));

        var exec = new BuiltinToolExecutor(ConfigService.Load(Path.GetTempPath(), approvalOverride: "never"));
        var result = await exec.ExecuteAsync(new ToolCallRequest("1", "new_context_window", "{}"), CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Contains("without summarizing", result.Output);
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

public class ThreadLifecycleTests
{
    [Fact]
    public void Finds_last_patch_and_archives()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-cwd-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(cwd);
            File.WriteAllText(Path.Combine(cwd, "a.txt"), "old\n");
            var cfg = ConfigService.Load(cwd);
            var store = new JsonlThreadStore();
            var thread = store.Create(cfg, "patch");
            store.SaveHistory(thread.Id, [
                new HistoryMessage("user", "edit", "", "", ""),
                new HistoryMessage("tool", "*** Begin Patch\n*** Update File: a.txt\n@@\n-old\n+new\n*** End Patch", "c1", "apply_patch", ""),
            ]);
            var patch = store.FindLastPatch(thread.Id);
            Assert.Contains("*** Begin Patch", patch);
            var applied = ApplyPatchEngine.Apply(patch!, new WorkspaceSandbox(cfg));
            Assert.True(applied.Ok, applied.Output);
            Assert.Contains("new", File.ReadAllText(Path.Combine(cwd, "a.txt")));

            store.Archive(thread.Id);
            Assert.DoesNotContain(store.List(), t => t.Id == thread.Id);
            Assert.Contains(store.List(true), t => t.Id == thread.Id);
            Assert.True(store.Unarchive(thread.Id));
            Assert.Contains(store.List(), t => t.Id == thread.Id);
            Assert.True(store.Delete(thread.Id));
            Assert.Null(store.Find(thread.Id));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ThreadQueueStoreTests
{
    [Fact]
    public void Add_list_reorder_dequeue()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var a = ThreadQueueStore.Add("thr_test", "one");
            var b = ThreadQueueStore.Add("thr_test", "two");
            Assert.Equal(2, ThreadQueueStore.List("thr_test").Count);
            ThreadQueueStore.Update("thr_test", a.Id, "one-edited");
            ThreadQueueStore.Reorder("thr_test", [b.Id, a.Id]);
            var texts = ThreadQueueStore.List("thr_test").Select(x => x.Text).ToArray();
            Assert.Equal(new[] { "two", "one-edited" }, texts);
            Assert.Equal("two", ThreadQueueStore.Dequeue("thr_test")!.Text);
            Assert.Single(ThreadQueueStore.List("thr_test"));
            Assert.True(ThreadQueueStore.Delete("thr_test", a.Id));
            Assert.Empty(ThreadQueueStore.List("thr_test"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class MemoryListAndPromptDebugTests
{
    [Fact]
    public async Task Memory_list_and_prompt_debug_snapshot()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "memories"));
            File.WriteAllText(Path.Combine(home, "memories", "note.md"), "remember this");
            await using var hosted = InProcessAppServer.Start();
            var client = hosted.Client;
            await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
            await client.NotifyAsync("initialized");
            var listed = await client.CallAsync("memory/list");
            Assert.Contains(listed.GetProperty("data").EnumerateArray(), e => (e.GetProperty("name").GetString() ?? "").Contains("note.md"));
            var snap = JsonSerializer.Serialize(PromptDebug.BuildSnapshot("hello debug"));
            Assert.Contains("hello debug", snap);
            Assert.Contains("shell", snap);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ExecPromptTests
{
    [Fact]
    public void Compose_appends_image_markers_and_schema()
    {
        var img = Path.Combine(Path.GetTempPath(), "codexsharp-img-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(img, [1, 2, 3]);
        try
        {
            var text = ExecPrompt.Compose("look", [img], """{"type":"object"}""");
            Assert.Contains("look", text);
            Assert.Contains(ImageMessageContent.Marker, text);
            Assert.Contains(Path.GetFullPath(img), text);
            Assert.Contains("\"type\":\"object\"", text.Replace(" ", ""));
        }
        finally
        {
            try { File.Delete(img); } catch { /* ignore */ }
        }
    }
}

public class SharedCliTests
{
    [Fact]
    public void Parses_cd_model_image_and_last()
    {
        var o = SharedCli.Parse(["--cd", "C:\\repo", "--model", "gpt-4.1", "-i", "shot.png", "--last", "hello"]);
        Assert.Equal("C:\\repo", o.Cwd);
        Assert.Equal("gpt-4.1", o.Model);
        Assert.True(o.Last);
        Assert.Equal("shot.png", o.Images[0]);
        Assert.Equal(new[] { "hello" }, o.Rest);
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
            Assert.StartsWith("thr_", started.GetProperty("thread").GetProperty("id").GetString());
            Assert.Equal(started.GetProperty("thread").GetProperty("id").GetString(), session.ThreadId);
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

public class IgnoreConfigAndRulesTests
{
    [Fact]
    public void Ignore_user_config_skips_toml_model_and_rules()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "config.toml"), "model = \"from-file\"\n");
            Directory.CreateDirectory(Path.Combine(home, "rules"));
            File.WriteAllText(Path.Combine(home, "rules", "user.rules"), "prefix_rule(pattern=[\"rm\"], decision=\"forbidden\")\n");
            Assert.Equal("from-file", ConfigService.Load().Model);
            Assert.NotEmpty(ExecPolicy.Load());
            var ignored = ConfigService.Load(ignoreUserConfig: true);
            Assert.NotEqual("from-file", ignored.Model);
            ExecPolicy.SuppressRules.Value = true;
            Assert.Empty(ExecPolicy.Load());
            Assert.True(SharedCli.Parse(["--ignore-user-config", "--ignore-rules"]).IgnoreUserConfig);
            Assert.True(SharedCli.Parse(["--ignore-rules"]).IgnoreRules);
        }
        finally
        {
            ExecPolicy.SuppressRules.Value = false;
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class AddDirAndStrictConfigTests
{
    [Fact]
    public async Task Add_dir_allows_session_writes_and_strict_config_rejects_unknown_keys()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-cwd-" + Guid.NewGuid().ToString("N"));
        var extra = Path.Combine(Path.GetTempPath(), "codexsharp-extra-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(cwd);
            Directory.CreateDirectory(extra);
            SessionSandbox.Reset();
            var cfg = ConfigService.Load(cwd, sandboxOverride: "workspace-write");
            var sandbox = new WorkspaceSandbox(cfg);
            Assert.Throws<InvalidOperationException>(() => sandbox.EnsureWritable(Path.Combine(extra, "out.txt")));
            SessionSandbox.Add(extra);
            sandbox.EnsureWritable(Path.Combine(extra, "out.txt"));
            sandbox.EnsureReadable(Path.Combine(extra, "out.txt"));

            File.WriteAllText(Path.Combine(home, "config.toml"), "model = \"x\"\nnot_a_real_key = true\n");
            Assert.Throws<InvalidOperationException>(() => ConfigService.Load(cwd, strictConfig: true));
            var o = SharedCli.Parse(["--add-dir", extra, "--strict-config"]);
            Assert.True(o.StrictConfig);
            Assert.Equal(extra, o.AddDirs[0]);

            var shell = new ShellExecutor(sandbox, cfg);
            var allowed = await shell.RunAsync(
                new ToolCallRequest("1", "shell", JsonSerializer.Serialize(new { command = new[] { "echo", "hi" }, workdir = extra })),
                CancellationToken.None);
            Assert.False(allowed.IsError, allowed.Output);
            Assert.DoesNotContain("Sandbox denied workdir", allowed.Output);
        }
        finally
        {
            SessionSandbox.Reset();
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}


public class TuiKeymapAndPetsTests
{
    [Fact]
    public void Keymap_overlay_and_pet_persist()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Assert.Equal("enter", TuiKeymap.Load().First(b => b.Action == "submit").Keys);
            Assert.True(TuiKeymap.TrySet("composer", "submit", "ctrl+enter"));
            Assert.Equal("ctrl+enter", TuiKeymap.Load().First(b => b.Action == "submit").Keys);
            Assert.False(TuiKeymap.TrySet("composer", "nope", "x"));
            Assert.Equal("none", TuiPets.Current);
            Assert.True(TuiPets.TrySet("dewey"));
            Assert.Equal("dewey", TuiPets.Current);
            Assert.False(string.IsNullOrWhiteSpace(TuiPets.Ascii()));
            Assert.False(TuiPets.TrySet("not-a-pet"));
            Assert.Equal("ctrl+g", TuiKeymap.Load().First(b => b.Action == "open_external_editor").Keys);
            Assert.True(TuiKeymap.TrySet("global", "open_external_editor", "ctrl+e"));
            Assert.Equal("ctrl+e", TuiKeymap.Load().First(b => b.Action == "open_external_editor").Keys);
            Assert.Equal("shift+enter", TuiKeymap.Load().First(b => b.Action == "newline").Keys);
            Assert.Equal("alt+enter", TuiKeymap.Load().First(b => b.Action == "queue").Keys);
            Assert.Equal("ctrl+v", TuiKeymap.Load().First(b => b.Action == "paste").Keys);
            Assert.Equal("ctrl+w", TuiKeymap.Load().First(b => b.Action == "delete_backward_word").Keys);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Keymap_and_pets_go_through_app_server()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("test", "Test");
            await session.SetKeymapAsync("composer", "submit", "ctrl+enter");
            var listed = await session.ListKeymapAsync();
            Assert.Contains(listed.GetProperty("data").EnumerateArray(), b =>
                b.GetProperty("action").GetString() == "submit" && b.GetProperty("keys").GetString() == "ctrl+enter");
            var pet = await session.SetPetsAsync("dewey");
            Assert.Equal("dewey", pet.GetProperty("id").GetString());
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.SetPetsAsync("not-a-pet"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ExternalEditorTests
{
    [Fact]
    public void Missing_visual_and_editor_is_an_error()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        var visual = Environment.GetEnvironmentVariable("VISUAL");
        var editor = Environment.GetEnvironmentVariable("EDITOR");
        try
        {
            Directory.CreateDirectory(home);
            Environment.SetEnvironmentVariable("VISUAL", null);
            Environment.SetEnvironmentVariable("EDITOR", null);
            var ex = Assert.Throws<InvalidOperationException>(() => ExternalEditor.Edit("draft"));
            Assert.Contains("neither VISUAL nor EDITOR", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
            Environment.SetEnvironmentVariable("VISUAL", visual);
            Environment.SetEnvironmentVariable("EDITOR", editor);
        }
    }

    [Fact]
    public void Prefers_visual_and_round_trips_draft()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        var visual = Environment.GetEnvironmentVariable("VISUAL");
        var editor = Environment.GetEnvironmentVariable("EDITOR");
        try
        {
            Directory.CreateDirectory(home);
            var script = Path.Combine(home, "edit.py");
            File.WriteAllText(script, "import sys\np=sys.argv[1]\nopen(p,'a',encoding='utf-8').write('\\nfrom-editor')\n");
            Environment.SetEnvironmentVariable("VISUAL", "python \"" + script + "\"");
            Environment.SetEnvironmentVariable("EDITOR", "should-not-run");
            var cmd = ExternalEditor.ResolveCommand();
            Assert.Equal("python", cmd[0]);
            Assert.Equal(script, cmd[1]);
            var result = ExternalEditor.Edit("hello");
            Assert.Contains("hello", result);
            Assert.Contains("from-editor", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
            Environment.SetEnvironmentVariable("VISUAL", visual);
            Environment.SetEnvironmentVariable("EDITOR", editor);
        }
    }
}

public class ThreadReadSubagentsAndPinTests
{
    [Fact]
    public async Task Thread_read_includes_subagents_and_pin_goes_through_metadata()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("test", "Test");
            var threadId = await session.StartThreadAsync(Path.GetTempPath(), "pin-thread");
            var read = await session.ReadThreadAsync();
            Assert.True(read.GetProperty("thread").TryGetProperty("subagents", out var agents));
            Assert.Equal(System.Text.Json.JsonValueKind.Array, agents.ValueKind);
            Assert.Equal(0, agents.GetArrayLength());
            Assert.False(ThreadPins.IsPinned(threadId));
            await session.SetPinnedAsync(threadId, true);
            Assert.True(ThreadPins.IsPinned(threadId));
            await session.SetGoalAsync("ship the port");
            var goal = await session.GetGoalAsync();
            Assert.Equal("ship the port", goal.GetProperty("goal").GetProperty("objective").GetString());
            Assert.True(hosted.TryGetSession(threadId, out var live));
            Assert.Empty(live.ListSubagents());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ComposerBufferTests
{
    [Fact]
    public void Inserts_kills_and_yanks_around_the_cursor()
    {
        var buf = new ComposerBuffer();
        buf.Insert("hello world");
        buf.MoveHome();
        buf.MoveRight();
        buf.MoveRight();
        buf.Insert("X");
        Assert.Equal("heXllo world", buf.Text);
        buf.MoveEnd();
        buf.KillWordBack();
        Assert.Equal("heXllo ", buf.Text);
        Assert.Equal("world", buf.Yank);
        buf.YankInsert();
        Assert.Equal("heXllo world", buf.Text);
        buf.Set("alpha\nbeta");
        buf.MoveHome();
        buf.KillToEnd();
        Assert.Equal("alpha\n", buf.Text);
        Assert.Equal("beta", buf.Yank);
        buf.Newline();
        buf.Insert("gamma");
        Assert.Contains("\ngamma", buf.Text);
        Assert.Contains("|", buf.DisplayWithCursor());
        buf.Set("one two three");
        buf.MoveTo(7);
        buf.KillWordBack();
        Assert.Equal("one  three", buf.Text);
        Assert.Equal(4, buf.Cursor);
        buf.Insert("Z");
        Assert.Contains("Z", buf.Text);
        Assert.True(buf.Undo());
        Assert.DoesNotContain("Z", buf.Text);
        var vim = new ComposerVim(buf) { Enabled = true };
        vim.SetMode(ComposerVimMode.Normal);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('r', ConsoleKey.R, false, false, true)));
        Assert.Contains("Z", buf.Text);
        buf.Set("aaa\nbb\nc");
        buf.MoveTo(6);
        buf.MoveUp();
        Assert.Equal(2, buf.Cursor);
        buf.MoveDown();
        Assert.Equal(6, buf.Cursor);
        vim.SetMode(ComposerVimMode.Normal);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('k', ConsoleKey.K, false, false, false)));
        Assert.Equal(2, buf.Cursor);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('j', ConsoleKey.J, false, false, false)));
        Assert.Equal(6, buf.Cursor);
    }
}

public class LiveSettingsTests
{
    [Fact]
    public void ApplySettings_updates_model_sandbox_and_effort()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            var session = CodexSession.Start(ConfigService.Load(Path.GetTempPath()));
            session.ApplySettings(model: "gpt-test-live", sandbox: "read-only", effort: "xhigh");
            Assert.Equal("gpt-test-live", session.Config.Model);
            Assert.Equal("read-only", session.Config.SandboxMode);
            Assert.Equal("xhigh", session.Config.ReasoningEffort);
            ConfigService.WriteUserKeys(new Dictionary<string, string> { ["tui_statusline"] = "model,effort,sandbox" });
            var line = TuiStatus.Line(session.Config, session.Thread.Id, session.Thread.Title);
            Assert.Contains("gpt-test-live", line);
            Assert.Contains("xhigh", line);
            Assert.Contains("read-only", line);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class PersonalityLiveTests
{
    [Fact]
    public void Merge_keeps_collab_overlay_and_swaps_personality()
    {
        var withPlan = CollaborationModes.MergeDeveloper("keep-me", "plan");
        var friendly = PersonalityModes.MergeDeveloper(withPlan, "friendly");
        Assert.Contains("<collaboration_mode>plan</collaboration_mode>", friendly);
        Assert.Contains("<personality>friendly</personality>", friendly);
        Assert.Contains("keep-me", friendly);
        var pragmatic = PersonalityModes.MergeDeveloper(friendly, "pragmatic");
        Assert.Contains("<personality>pragmatic</personality>", pragmatic);
        Assert.DoesNotContain("<personality>friendly</personality>", pragmatic);
        Assert.Contains("<collaboration_mode>plan</collaboration_mode>", pragmatic);
    }
}

public class CollaborationModeLiveTests
{
    [Fact]
    public void Merge_replaces_previous_overlay()
    {
        Assert.Equal("plan", CollaborationModes.Normalize("PLAN"));
        var merged = CollaborationModes.MergeDeveloper("keep-me", "plan");
        Assert.Contains("<collaboration_mode>plan</collaboration_mode>", merged);
        Assert.Contains("keep-me", merged);
        var again = CollaborationModes.MergeDeveloper(merged, "pair");
        Assert.Contains("<collaboration_mode>pair</collaboration_mode>", again);
        Assert.DoesNotContain("<collaboration_mode>plan</collaboration_mode>", again);
        Assert.Contains("keep-me", again);
    }
}

public class ComposerVimTests
{
    [Fact]
    public void Esc_enters_normal_mode_and_dd_kills_the_line()
    {
        var buf = new ComposerBuffer();
        buf.Set("hello world");
        var vim = new ComposerVim(buf) { Enabled = true };
        Assert.Equal(ComposerVimMode.Insert, vim.Mode);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false)));
        Assert.Equal(ComposerVimMode.Normal, vim.Mode);
        buf.MoveTo(0);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('w', ConsoleKey.W, false, false, false)));
        Assert.Equal(6, buf.Cursor);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('d', ConsoleKey.D, false, false, false)));
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('d', ConsoleKey.D, false, false, false)));
        Assert.Equal("", buf.Text);
        buf.Set("abc def");
        buf.MoveTo(0);
        vim.TryHandle(new ConsoleKeyInfo('d', ConsoleKey.D, false, false, false));
        vim.TryHandle(new ConsoleKeyInfo('w', ConsoleKey.W, false, false, false));
        Assert.Equal("def", buf.Text);
        vim.TryHandle(new ConsoleKeyInfo('i', ConsoleKey.I, false, false, false));
        Assert.Equal(ComposerVimMode.Insert, vim.Mode);
        Assert.False(vim.TryHandle(new ConsoleKeyInfo('z', ConsoleKey.Z, false, false, false)));
        var again = new ComposerVim(new ComposerBuffer()) { Enabled = true };
        Assert.True(again.TryHandle(new ComposerVimInput('\0', true, false, false, false, false, false, false, false, false, false)));
        Assert.Equal(ComposerVimMode.Normal, again.Mode);
    }

    [Fact]
    public void Gg_G_and_O_open_line_above()
    {
        var buf = new ComposerBuffer();
        buf.Set("ab\ncd");
        var vim = new ComposerVim(buf) { Enabled = true };
        vim.SetMode(ComposerVimMode.Normal);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('G', ConsoleKey.G, true, false, false)));
        Assert.Equal(buf.Text.Length, buf.Cursor);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('g', ConsoleKey.G, false, false, false)));
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('g', ConsoleKey.G, false, false, false)));
        Assert.Equal(0, buf.Cursor);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('O', ConsoleKey.O, true, false, false)));
        Assert.Equal(ComposerVimMode.Insert, vim.Mode);
        Assert.StartsWith("\n", buf.Text);
    }

    [Fact]
    public void Visual_dot_yank_change_and_find_persist_across_keypresses()
    {
        var buf = new ComposerBuffer();
        buf.Set("one two three");
        var vim = new ComposerVim(buf) { Enabled = true };
        vim.SetMode(ComposerVimMode.Normal);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('d', ConsoleKey.D, false, false, false)));
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('d', ConsoleKey.D, false, false, false)));
        Assert.Equal("", buf.Text);
        buf.Set("alpha beta");
        buf.MoveTo(0);
        vim.SetMode(ComposerVimMode.Normal);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('d', ConsoleKey.D, false, false, false)));
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('w', ConsoleKey.W, false, false, false)));
        Assert.Equal("beta", buf.Text);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('.', ConsoleKey.OemPeriod, false, false, false)));
        Assert.Equal("", buf.Text);

        buf.Set("hello world");
        buf.MoveTo(0);
        vim.SetMode(ComposerVimMode.Normal);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('v', ConsoleKey.V, false, false, false)));
        Assert.Equal(ComposerVimMode.Visual, vim.Mode);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('w', ConsoleKey.W, false, false, false)));
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('d', ConsoleKey.D, false, false, false)));
        Assert.Equal(ComposerVimMode.Normal, vim.Mode);
        Assert.Equal("world", buf.Text);

        buf.Set("abc def");
        buf.MoveTo(0);
        vim.SetMode(ComposerVimMode.Normal);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('y', ConsoleKey.Y, false, false, false)));
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('w', ConsoleKey.W, false, false, false)));
        Assert.Equal("abc def", buf.Text);
        Assert.Equal("abc ", buf.Yank);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('P', ConsoleKey.P, true, false, false)));
        Assert.StartsWith("abc ", buf.Text);

        buf.Set("find x here");
        buf.MoveTo(0);
        vim.SetMode(ComposerVimMode.Normal);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('f', ConsoleKey.F, false, false, false)));
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('x', ConsoleKey.X, false, false, false)));
        Assert.Equal(5, buf.Cursor);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('r', ConsoleKey.R, false, false, false)));
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('z', ConsoleKey.Z, false, false, false)));
        Assert.Contains("z", buf.Text);

        buf.Set("keep");
        buf.MoveTo(2);
        vim.SetMode(ComposerVimMode.Insert);
        buf.Adopt("keep-me", 7);
        Assert.True(buf.Undo());
        Assert.Equal("keep", buf.Text);
        Assert.True(buf.Redo());
        Assert.Equal("keep-me", buf.Text);
    }

    [Fact]
    public void Dot_replays_last_insert_body()
    {
        var buf = new ComposerBuffer();
        buf.Set("ab");
        buf.MoveTo(0);
        var vim = new ComposerVim(buf) { Enabled = true };
        vim.SetMode(ComposerVimMode.Normal);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('i', ConsoleKey.I, false, false, false)));
        Assert.Equal(ComposerVimMode.Insert, vim.Mode);
        buf.Insert("XY");
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('', ConsoleKey.Escape, false, false, false)));
        Assert.Equal(ComposerVimMode.Normal, vim.Mode);
        Assert.Equal("XYab", buf.Text);
        buf.MoveTo(buf.Text.Length);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('.', ConsoleKey.OemPeriod, false, false, false)));
        Assert.Equal("XYabXY", buf.Text);
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

public class ResumePickerTests
{
    [Fact]
    public void Filters_cwd_and_exec_threads_unless_all()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var cwdA = Path.Combine(Path.GetTempPath(), "codexsharp-cwd-a-" + Guid.NewGuid().ToString("N"));
        var cwdB = Path.Combine(Path.GetTempPath(), "codexsharp-cwd-b-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(cwdA);
            Directory.CreateDirectory(cwdB);
            var a = CodexSession.Start(ConfigService.Load(cwdA), "alpha");
            var b = CodexSession.Start(ConfigService.Load(cwdB), "beta");
            var exec = CodexSession.Start(ConfigService.Load(cwdA), "exec-job");
            ThreadSources.Set(exec.Thread.Id, "exec");
            var here = ResumePicker.Candidates(all: false, cwd: cwdA);
            Assert.Contains(here, t => t.Id == a.Thread.Id);
            Assert.DoesNotContain(here, t => t.Id == b.Thread.Id);
            Assert.DoesNotContain(here, t => t.Id == exec.Thread.Id);
            var every = ResumePicker.Candidates(all: true, includeNonInteractive: true, cwd: cwdA);
            Assert.Contains(every, t => t.Id == b.Thread.Id);
            Assert.Contains(every, t => t.Id == exec.Thread.Id);
            var search = ResumePicker.Candidates(all: true, includeNonInteractive: true, cwd: cwdA, search: "beta");
            Assert.Contains(search, t => t.Id == b.Thread.Id);
            Assert.DoesNotContain(search, t => t.Id == a.Thread.Id);
            var label = ResumePicker.Format(a.Thread with { Pinned = true }, showCwd: true);
            Assert.StartsWith("* ", label);
            Assert.Equal(a.Thread.Id, ResumePicker.IdFromLabel(label));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class SandboxCommandTests
{
    [Fact]
    public void Host_backends_and_windows_echo()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-sandbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(cwd);
        try
        {
            var mac = SandboxCommand.Run(["macos", "--", "echo", "nope"], cwd);
            Assert.Equal(1, mac.ExitCode);
            Assert.Contains("notConfigured", mac.Output);
            var linux = SandboxCommand.Run(["landlock"], cwd);
            Assert.Contains("notConfigured", linux.Output);
            var echo = SandboxCommand.Run(["windows", "--", "echo", "sandbox-hi"], cwd);
            Assert.Equal(0, echo.ExitCode);
            Assert.Contains("sandbox-hi", echo.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
            SessionSandbox.Reset();
        }
    }
}

public class TurnSteerTests
{
    private sealed class CountingSteerHost : ISteerHost
    {
        public int Calls;
        public string Payload = "keep going";
        public string TryDequeue()
        {
            Calls++;
            return Calls == 2 ? Payload : "";
        }
    }

    [Fact]
    public void Idle_session_rejects_steer()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            var session = CodexSession.Start(ConfigService.Load(Path.GetTempPath()));
            Assert.False(session.TrySteer("later"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Steer_host_injects_follow_up_user_turn()
    {
        var cfg = ConfigService.Load(Path.GetTempPath());
        var model = new ScriptedModelClient(
            [ModelStreamEvent.NewOutputTextDelta("first"), ModelStreamEvent.NewStreamFinished("stop")],
            [ModelStreamEvent.NewOutputTextDelta("second"), ModelStreamEvent.NewStreamFinished("stop")]);
        var sink = new RecordingSink();
        var steer = new CountingSteerHost();
        var deps = new AgentDeps(cfg, model, new BuiltinToolExecutor(cfg), new AutoApprover(true), sink, [], new NoopHookHost(), new NoopUserInputHost(), steer);
        var history = new List<HistoryMessage>();
        await AgentLoop.runTurn(deps, "thr_steer", history, "hello", CancellationToken.None);
        Assert.Contains(history, m => m.Role == "user" && m.Content.Contains("keep going"));
        Assert.Contains(history, m => m.Role == "assistant" && m.Content.Contains("second"));
    }

    [Fact]
    public async Task App_server_steer_without_active_turn_errors()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("test", "Test");
            await session.StartThreadAsync(Path.GetTempPath(), "steer-thread");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => session.SteerAsync("mid-turn"));
            Assert.Contains("no active turn", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ExecPolicyCheckTests
{
    [Fact]
    public void Forbidden_prefix_is_rejected()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "rules"));
            ExecPolicy.AddUserRule(["rm"], ExecDecision.Forbidden);
            Assert.Equal(ExecDecision.Forbidden, ExecPolicy.Evaluate("rm -rf /"));
            Assert.Equal(ExecDecision.Allow, ExecPolicy.Evaluate("echo hi"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class RolloutMigrateTests
{
    [Fact]
    public void Inspect_and_apply_drop_invalid_jsonl_lines()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            CodexPaths.EnsureLayout();
            var path = Path.Combine(CodexPaths.SessionsDir, "thr_migrate.jsonl");
            File.WriteAllText(path, "{\"thread\":{\"id\":\"thr_migrate\",\"title\":\"t\"}}\nnot-json\n{\"ok\":true}\n");
            var dry = RolloutMigrate.Inspect(["thr_migrate"], apply: false);
            Assert.False(dry.Applied);
            Assert.False(dry.SqliteThreadHistory);
            Assert.Equal("jsonl", dry.Storage);
            Assert.Equal(1, dry.Threads);
            Assert.Equal(1, dry.InvalidLines);
            var applied = RolloutMigrate.Inspect(["thr_migrate"], apply: true);
            Assert.True(applied.Applied);
            Assert.Equal(0, applied.InvalidLines);
            Assert.Equal(2, applied.Lines);
            Assert.True(File.Exists(path + ".bak"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ExecServerHostTests
{
    [Fact]
    public async Task Initialize_environment_fs_and_process_start()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-cwd-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        var previous = Environment.CurrentDirectory;
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(cwd);
            File.WriteAllText(Path.Combine(home, "config.toml"), "sandbox_mode = \"danger-full-access\"\napproval_policy = \"never\"\n");
            File.WriteAllText(Path.Combine(cwd, "note.txt"), "hello-exec");
            var toServer = new CodexSharp.AppServer.LineChannel();
            var fromServer = new CodexSharp.AppServer.LineChannel();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var host = new ExecServerHost(toServer.Reader, fromServer.Writer);
            var run = host.RunAsync(cts.Token);
            await using var client = new AppServerClient(fromServer.Reader, toServer.Writer);
            var init = await client.CallAsync("initialize", new { clientName = "tests" });
            Assert.False(string.IsNullOrWhiteSpace(init.GetProperty("sessionId").GetString()));
            var info = await client.CallAsync("environment/info");
            Assert.Equal("windows", info.GetProperty("platformOs").GetString());
            var status = await client.CallAsync("environment/status");
            Assert.Equal("ready", status.GetProperty("status").GetString());
            Assert.False(status.GetProperty("remote").GetBoolean());
            var read = await client.CallAsync("fs/readFile", new { path = Path.Combine(cwd, "note.txt") });
            Assert.Contains("hello-exec", read.GetProperty("contents").GetString());
            var output = new System.Text.StringBuilder();
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Notification += el =>
            {
                if (!el.TryGetProperty("method", out var methodEl)) return;
                var method = methodEl.GetString();
                if (method == "process/output" && el.TryGetProperty("params", out var p) && p.TryGetProperty("deltaBase64", out var b64) && b64.ValueKind == JsonValueKind.String)
                {
                    output.Append(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64.GetString() ?? "")));
                }
                if (method == "process/exited" && el.TryGetProperty("params", out var ex) && ex.TryGetProperty("exitCode", out var code) && code.TryGetInt32(out var n))
                    exited.TrySetResult(n);
            };
            var argv = new[] { "powershell.exe", "-NoLogo", "-NoProfile", "-Command", "echo exec-server-ok" };
            var started = await client.CallAsync("process/start", new { processId = "p1", argv, cwd, tty = false, pipeStdin = false });
            Assert.Equal("p1", started.GetProperty("processId").GetString());
            var code = await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, code);
            Assert.Contains("exec-server-ok", output.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}


public class McpConfigCliTests
{
    [Fact]
    public void Add_get_and_remove_stdio_and_http_servers()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            CodexPaths.EnsureLayout();
            McpConfig.AddStdio("docs", ["npx", "-y", "mcp-server"]);
            var stdio = McpConfig.Get("docs");
            Assert.NotNull(stdio);
            Assert.Equal("npx", stdio!.Command);
            Assert.Equal(["-y", "mcp-server"], stdio.Args);
            McpConfig.AddHttp("remote", "https://example.test/mcp", "MCP_TOKEN");
            var http = McpConfig.Get("remote");
            Assert.NotNull(http);
            Assert.Equal("https://example.test/mcp", http!.Url);
            Assert.Equal("MCP_TOKEN", http.BearerTokenEnvVar);
            Assert.True(McpConfig.Remove("docs"));
            Assert.Null(McpConfig.Get("docs"));
            Assert.NotNull(McpConfig.Get("remote"));
            Assert.False(McpConfig.Remove("missing"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class FileSearchSuggestTests
{
    [Fact]
    public void SuggestNames_matches_relative_paths()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-cwd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(cwd, "src"));
        File.WriteAllText(Path.Combine(cwd, "src", "LiveTui.cs"), "class LiveTui {}");
        File.WriteAllText(Path.Combine(cwd, "README.md"), "hi");
        var hits = FileSearch.SuggestNames(cwd, "Live", 8);
        Assert.Contains(hits, h => h.Replace('\\', '/').Contains("LiveTui.cs"));
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


public class UiThemeTests
{
    [Fact]
    public void Light_aliases_are_light_everything_else_is_dark()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            CodexPaths.EnsureLayout();
            Assert.True(UiTheme.IsLight("light"));
            Assert.True(UiTheme.IsLight("day"));
            Assert.False(UiTheme.IsLight("dark"));
            Assert.False(UiTheme.IsLight("dim"));
            ConfigService.WriteUserKeys(new Dictionary<string, string> { ["tui_theme"] = "light" });
            Assert.True(UiTheme.IsLight());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}


public class DesktopNotifyTests
{
    [Fact]
    public void Osc9_payload_strips_controls_and_off_is_silent()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            CodexPaths.EnsureLayout();
            var osc = DesktopNotify.Osc9("hi\nthere\u0007");
            Assert.StartsWith("\u001b]9;", osc);
            Assert.EndsWith("\u0007", osc);
            Assert.DoesNotContain("\n", DesktopNotify.Sanitize("a\nb"));
            ConfigService.WriteUserKeys(new Dictionary<string, string> { ["tui_notifications"] = "off" });
            DesktopNotify.Send("should not throw");
            Assert.Equal("off", DesktopNotify.Method);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}


public class ChatgptAccessTokenLoginTests
{
    [Fact]
    public async Task Login_start_chatgptAuthTokens_persists_without_network()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            CodexPaths.EnsureLayout();
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("tests", "tests");
            await session.LoginAccessTokenAsync("tok_test_access");
            Assert.Equal("chatgpt", AuthService.AuthMode());
            var tokens = AuthService.ReadTokens();
            Assert.Equal("tok_test_access", tokens.Access);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}


public class ExecJsonlTests
{
    [Fact]
    public void Emits_official_thread_and_item_types()
    {
        var started = ExecJsonl.ThreadStarted("thr_1");
        Assert.Contains("\"type\":\"thread.started\"", started.Replace(" ", ""));
        Assert.Contains("\"thread_id\":\"thr_1\"", started.Replace(" ", ""));
        var turn = ExecJsonl.Format(AgentEvent.NewTurnStarted("turn_1", "thr_1"));
        Assert.NotNull(turn);
        Assert.Contains("turn.started", turn);
        var item = new ConversationItem("item_1", "agent_message", "hello", "completed", "", "", "", "");
        var completed = ExecJsonl.Format(AgentEvent.NewItemCompleted(item));
        Assert.NotNull(completed);
        Assert.Contains("item.completed", completed);
        Assert.Contains("agent_message", completed);
        var err = ExecJsonl.Format(AgentEvent.NewFailed("boom"));
        Assert.Contains("\"type\":\"error\"", err!.Replace(" ", ""));
        Assert.Null(ExecJsonl.Format(AgentEvent.NewTokenUsage(1, 1)));
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


public class TerminalTitleTests
{
    [Fact]
    public void Sanitize_strips_controls_and_caps_length()
    {
        Assert.Equal("hello world", TerminalTitle.Sanitize("hello\n\tworld\u202e"));
        Assert.Equal("", TerminalTitle.Sanitize("\u0007\u0008"));
        var longTitle = new string('a', 400);
        Assert.Equal(TerminalTitle.MaxChars, TerminalTitle.Sanitize(longTitle).Length);
    }
}


public class AccountUsageReadTests
{
    [Fact]
    public async Task Usage_read_includes_local_thread_estimate()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            CodexPaths.EnsureLayout();
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("tests", "tests");
            await session.StartThreadAsync();
            var usage = await session.ReadUsageAsync();
            Assert.True(usage.TryGetProperty("threadUsage", out var tu));
            Assert.Equal(JsonValueKind.Object, tu.ValueKind);
            Assert.True(tu.TryGetProperty("estimatedTokens", out _));
            Assert.True(usage.TryGetProperty("summary", out var summary));
            Assert.Equal(JsonValueKind.Null, summary.GetProperty("lifetimeTokens").ValueKind);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}


public class AppsAndBackgroundTerminalsTests
{
    [Fact]
    public async Task App_list_is_empty_and_background_terminals_list()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            CodexPaths.EnsureLayout();
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("tests", "tests");
            await session.StartThreadAsync();
            var apps = await session.ListAppsAsync();
            Assert.True(apps.TryGetProperty("data", out var data));
            Assert.Equal(JsonValueKind.Array, data.ValueKind);
            Assert.Equal(0, data.GetArrayLength());
            var terms = await session.ListBackgroundTerminalsAsync();
            Assert.True(terms.TryGetProperty("data", out var tdata));
            Assert.Equal(JsonValueKind.Array, tdata.ValueKind);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}


public class ThreadSourcesTests
{
    [Fact]
    public void Set_and_get_thread_source()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            CodexPaths.EnsureLayout();
            ThreadSources.Set("thr_a", "cli");
            Assert.Equal("cli", ThreadSources.Get("thr_a"));
            Assert.Null(ThreadSources.Get("missing"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}
