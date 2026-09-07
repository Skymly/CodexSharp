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

