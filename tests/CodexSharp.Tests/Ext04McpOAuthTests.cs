using System.Net;
using CodexSharp.AppServer;
using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public class Ext04McpOAuthTests
{
    [Fact]
    public void Catalog_mcp_oauth_is_notConfigured_not_enabled()
    {
        var row = Assert.Single(HonestStubs.Capabilities, c => c.Id == "mcp_oauth");
        Assert.Equal("MCP OAuth", row.Label);
        Assert.Equal("notConfigured", row.Status);
        Assert.Equal("notConfigured", HonestStubs.StatusOf("mcp_oauth"));
        Assert.DoesNotContain("Enable", row.Label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Enable", row.Status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Enable", row.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("enabled", row.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FeatureFlags.Known, n => n.Equals("mcp_oauth", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Oauth_login_stays_not_configured()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        await client.NotifyAsync("initialized");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CallAsync("mcpServer/oauth/login", new { name = "demo" }));
        Assert.Contains("MCP OAuth", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not configured", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Http_mcp_stores_bearer_env_name_not_token()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            McpConfig.AddHttp("ext04", "https://example.test/mcp", "EXT04_MCP_TOKEN");
            var spec = McpConfig.Get("ext04");
            Assert.NotNull(spec);
            Assert.Equal("EXT04_MCP_TOKEN", spec!.BearerTokenEnvVar);
            var toml = File.ReadAllText(Path.Combine(home, "config.toml"));
            Assert.Contains("bearer_token_env_var", toml);
            Assert.Contains("EXT04_MCP_TOKEN", toml);
            Assert.DoesNotContain("sk-", toml);
            Assert.DoesNotContain("secret-token", toml);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Http_mcp_sends_bearer_from_env()
    {
        var listener = new HttpListener();
        var prefix = $"http://127.0.0.1:{FreePort()}/";
        listener.Prefixes.Add(prefix);
        listener.Start();
        string? auth = null;
        try
        {
            var server = Task.Run(async () =>
            {
                var ctx = await listener.GetContextAsync();
                auth = ctx.Request.Headers["Authorization"];
                var response = """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-06-18","capabilities":{},"serverInfo":{"name":"ext04","version":"1"}}}""";
                var bytes = System.Text.Encoding.UTF8.GetBytes(response);
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            });

            Environment.SetEnvironmentVariable("EXT04_BEARER", "env-token-value");
            var spec = new McpServerSpec("ext04", "", [], true, prefix.TrimEnd('/'), "EXT04_BEARER");
            await using var client = new McpHttpClient(spec);
            await client.InitializeAsync();
            await server.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Bearer env-token-value", auth);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EXT04_BEARER", null);
            listener.Stop();
            listener.Close();
        }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
