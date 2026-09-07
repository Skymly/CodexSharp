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

