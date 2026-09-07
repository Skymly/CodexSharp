using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexSharp.Protocol;

namespace CodexSharp.Runtime;

public interface IMcpSession : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<ToolSpec[]> ListToolsAsync(string serverId, CancellationToken ct = default);
    Task<string> CallToolAsync(string name, string argumentsJson, CancellationToken ct = default);
    Task<JsonElement> ReadResourceAsync(string uri, CancellationToken ct = default);
    Task<JsonElement> ListResourcesAsync(CancellationToken ct = default);
    Task<JsonElement> ListResourceTemplatesAsync(CancellationToken ct = default);
    Func<JsonElement, CancellationToken, Task<JsonElement>>? ElicitationHandler { get; set; }
}

/// Streamable HTTP MCP transport from vendor/codex/codex-rs/config mcp StreamableHttp.
public sealed class McpHttpClient : IMcpSession
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private int _nextId;
    private string? _sessionId;
    public Func<JsonElement, CancellationToken, Task<JsonElement>>? ElicitationHandler { get; set; }

    public McpHttpClient(McpServerSpec spec, HttpClient? http = null)
    {
        _endpoint = new Uri(spec.Url ?? throw new ArgumentException("MCP HTTP server requires url"));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var envName = spec.BearerTokenEnvVar;
        if (!string.IsNullOrWhiteSpace(envName))
        {
            var token = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrWhiteSpace(token))
            {
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await CallAsync("initialize", new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { },
            clientInfo = new { name = "codexsharp", version = "0.1.0" },
        }, ct);
    }

    public async Task<ToolSpec[]> ListToolsAsync(string serverId, CancellationToken ct = default)
    {
        var result = await CallAsync("tools/list", new { }, ct);
        if (!result.TryGetProperty("tools", out var tools))
        {
            return [];
        }

        return tools.EnumerateArray().Select(tool =>
        {
            var name = tool.GetProperty("name").GetString() ?? "tool";
            var description = tool.TryGetProperty("description", out var d) ? d.GetString() ?? name : name;
            var schema = tool.TryGetProperty("inputSchema", out var s) ? s.GetRawText() : """{"type":"object"}""";
            return new ToolSpec($"mcp__{serverId}__{name}", description, schema);
        }).ToArray();
    }

    public Task<JsonElement> ReadResourceAsync(string uri, CancellationToken ct = default) =>
        CallAsync("resources/read", new { uri }, ct);

    public Task<JsonElement> ListResourcesAsync(CancellationToken ct = default) =>
        CallAsync("resources/list", new { }, ct);

    public Task<JsonElement> ListResourceTemplatesAsync(CancellationToken ct = default) =>
        CallAsync("resources/templates/list", new { }, ct);

    public async Task<string> CallToolAsync(string name, string argumentsJson, CancellationToken ct = default)
    {
        JsonNode args;
        try { args = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) ?? new JsonObject(); }
        catch { args = new JsonObject(); }

        var result = await CallAsync("tools/call", new { name, arguments = args }, ct);
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            return string.Join('\n', content.EnumerateArray().Select(item =>
                item.TryGetProperty("text", out var text) ? text.GetString() : item.GetRawText()));
        }

        return result.GetRawText();
    }

    public async Task<JsonElement> CallAsync(string method, object paramsObj, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = JsonSerializer.SerializeToNode(paramsObj),
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrWhiteSpace(_sessionId))
        {
            req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        }

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.Headers.TryGetValues("Mcp-Session-Id", out var ids))
        {
            _sessionId = ids.FirstOrDefault();
        }

        var media = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (media.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var data in ReadSseAsync(await resp.Content.ReadAsStreamAsync(ct), ct))
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.GetInt32() == id)
                {
                    if (doc.RootElement.TryGetProperty("error", out var error))
                    {
                        throw new InvalidOperationException(error.GetRawText());
                    }

                    return doc.RootElement.TryGetProperty("result", out var result) ? result.Clone() : doc.RootElement.Clone();
                }
            }

            throw new InvalidOperationException($"MCP HTTP SSE ended before {method} result");
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        resp.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(body);
        if (json.RootElement.TryGetProperty("error", out var err))
        {
            throw new InvalidOperationException(err.GetRawText());
        }

        return json.RootElement.TryGetProperty("result", out var r) ? r.Clone() : json.RootElement.Clone();
    }

    private static async IAsyncEnumerable<string> ReadSseAsync(Stream stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                yield break;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                yield return line["data:".Length..].Trim();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
