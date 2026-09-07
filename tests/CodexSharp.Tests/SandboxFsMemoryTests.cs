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


public class WorkspaceWritePathPolicyTests
{
    [Fact]
    public void Default_config_sandbox_is_workspace_write()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-cwd-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(cwd);
            var cfg = ConfigService.Load(cwd);
            Assert.Equal("workspace-write", cfg.SandboxMode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Fake_client_write_file_outside_workspace_is_denied()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-cwd-" + Guid.NewGuid().ToString("N"));
        var outsideDir = Path.Combine(Path.GetTempPath(), "codexsharp-out-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(outsideDir, "leak.txt");
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(cwd);
            Directory.CreateDirectory(outsideDir);
            var cfg = ConfigService.Load(cwd, sandboxOverride: "workspace-write", approvalOverride: "never");
            cfg = new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, "never", "workspace-write", "", "", cwd, false, 8);
            var args = JsonSerializer.Serialize(new { path = outside, content = "nope" });
            var model = new ScriptedModelClient(
                [ModelStreamEvent.NewToolCallReady(new ToolCallRequest("w1", "write_file", args)), ModelStreamEvent.NewStreamFinished("tool")],
                [ModelStreamEvent.NewOutputTextDelta("ok"), ModelStreamEvent.NewStreamFinished("stop")]);
            var sink = new RecordingSink();
            var deps = new AgentDeps(cfg, model, new BuiltinToolExecutor(cfg), new AutoApprover(true), sink, [], new NoopHookHost(), new NoopUserInputHost(), new NoopSteerHost(), "", "");
            var history = new List<HistoryMessage>();
            await AgentLoop.runTurn(deps, "thr_sandbox", history, "write outside", CancellationToken.None);
            Assert.Contains(history, m => m.Role == "tool" && m.Content.Contains("outside workspace", StringComparison.OrdinalIgnoreCase));
            Assert.False(File.Exists(outside));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Chrome_sandbox_note_is_not_elevated()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var chrome = await session.LoadChromeAsync();
            Assert.Contains("workspace-write", chrome.SandboxNote, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not an OS elevated sandbox", chrome.SandboxNote, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("elevated sandbox enabled", chrome.SandboxNote, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}
