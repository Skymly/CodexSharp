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
        var deps = new AgentDeps(cfg, model, new BuiltinToolExecutor(cfg), new AutoApprover(true), sink, [], new NoopHookHost(), new NoopUserInputHost(), new NoopSteerHost(), "", "");
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
        var deps = new AgentDeps(cfg, model, new BuiltinToolExecutor(cfg), new AutoApprover(true), sink, [], new NoopHookHost(), new NoopUserInputHost(), new NoopSteerHost(), "", "");
        var codex = CodexModule.start(deps);
        var turn = await codex.Submit(Op.NewUserInput("hello"), CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(turn));
        Assert.Contains(sink.Events, e => e is AgentEvent.TurnCompleted);
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
        var deps = new AgentDeps(cfg, model, new BuiltinToolExecutor(cfg), new AutoApprover(true), sink, [], new NoopHookHost(), new NoopUserInputHost(), new NoopSteerHost(), "", "");
        await AgentLoop.runTurn(deps, "thr_patch", new List<HistoryMessage>(), "patch it", CancellationToken.None);
        Assert.Contains(sink.Events, e => e is AgentEvent.ItemCompleted c && c.Item.Kind == "file_change");
        Assert.True(File.Exists(Path.Combine(root, "notes.md")));
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
        var deps = new AgentDeps(cfg, model, new BuiltinToolExecutor(cfg), new AutoApprover(true), sink, [], new BlockingHookHost(), new NoopUserInputHost(), new NoopSteerHost(), "", "");
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
        var deps = new AgentDeps(cfg, model, new BuiltinToolExecutor(cfg), new AutoApprover(true), sink, [], new NoopHookHost(), new NoopUserInputHost(), steer, "", "");
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

