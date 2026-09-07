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

