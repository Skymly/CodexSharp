using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexSharp.Protocol;
using Tomlyn.Model;

namespace CodexSharp.Runtime;

public sealed record McpServerSpec(string Id, string Command, string[] Args, bool Enabled, string? Url = null, string? BearerTokenEnvVar = null);

public sealed class McpStdioClient : IMcpSession
{
    private readonly TextReader _output;
    private readonly TextWriter _input;
    private readonly Process? _process;
    private int _nextId;
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public Func<JsonElement, CancellationToken, Task<JsonElement>>? ElicitationHandler { get; set; }

    public McpStdioClient(TextReader output, TextWriter input, Process? process = null)
    {
        _output = output;
        _input = input;
        _process = process;
    }

    public static McpStdioClient Start(McpServerSpec spec)
    {
        var psi = new ProcessStartInfo(spec.Command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in spec.Args)
        {
            psi.ArgumentList.Add(arg);
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start MCP server {spec.Id}");
        return new McpStdioClient(process.StandardOutput, process.StandardInput, process);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await CallAsync("initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "codexsharp", version = "0.1.0" },
        }, ct);
        await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, ct);
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
            var schema = tool.TryGetProperty("inputSchema", out var s) ? s.GetRawText() : """{"type":"object","properties":{}}""";
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
            var texts = content.EnumerateArray()
                .Select(item => item.TryGetProperty("text", out var text) ? text.GetString() : item.GetRawText())
                .Where(s => !string.IsNullOrWhiteSpace(s));
            return string.Join('\n', texts);
        }

        return result.GetRawText();
    }

    private async Task<JsonElement> CallAsync(string method, object paramsObj, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var node = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = JsonSerializer.SerializeToNode(paramsObj, _json),
        };
        await WriteAsync(node, ct);

        while (!ct.IsCancellationRequested)
        {
            var line = await _output.ReadLineAsync(ct);
            if (line is null)
            {
                throw new InvalidOperationException($"MCP server closed during {method}");
            }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement.Clone();
            if (root.TryGetProperty("method", out var inboundMethod)
                && root.TryGetProperty("id", out _)
                && !root.TryGetProperty("result", out _)
                && !root.TryGetProperty("error", out _))
            {
                await ReplyServerRequestAsync(root, ct);
                continue;
            }

            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.GetInt32() == id)
            {
                if (root.TryGetProperty("error", out var error))
                {
                    throw new InvalidOperationException($"MCP {method} error: {error}");
                }

                return root.TryGetProperty("result", out var result) ? result.Clone() : root.Clone();
            }
        }

        throw new OperationCanceledException(ct);
    }

    private async Task ReplyServerRequestAsync(JsonElement request, CancellationToken ct)
    {
        var method = request.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
        JsonNode resultNode;
        if (method is "elicitation/create")
        {
            var payload = request.TryGetProperty("params", out var p) ? p : request;
            if (ElicitationHandler is not null)
            {
                var handled = await ElicitationHandler(payload, ct);
                resultNode = JsonNode.Parse(handled.GetRawText()) ?? new JsonObject { ["action"] = "decline" };
            }
            else
            {
                resultNode = new JsonObject { ["action"] = "decline" };
            }
        }
        else
        {
            await WriteAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = CloneMcpId(request.GetProperty("id")),
                ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"Unknown MCP request {method}" },
            }, ct);
            return;
        }

        await WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = CloneMcpId(request.GetProperty("id")),
            ["result"] = resultNode,
        }, ct);
    }

    private static JsonNode CloneMcpId(JsonElement id) =>
        id.ValueKind == JsonValueKind.Number ? JsonValue.Create(id.GetInt32())! :
        JsonValue.Create(id.GetString())!;

    private async Task WriteAsync(JsonNode node, CancellationToken ct)
    {
        await _input.WriteLineAsync(node.ToJsonString().AsMemory(), ct);
        await _input.FlushAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        try { _input.Dispose(); } catch { /* ignore */ }
        try { _output.Dispose(); } catch { /* ignore */ }
        if (_process is not null)
        {
            try { _process.Kill(true); } catch { /* ignore */ }
            _process.Dispose();
        }

        await Task.CompletedTask;
    }
}

public sealed class McpHub : IToolExecutor, IAsyncDisposable
{
    private readonly IToolExecutor _inner;
    private readonly Dictionary<string, (string Server, string Tool, IMcpSession Client)> _tools = new();
    private readonly Dictionary<string, IMcpSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IMcpSession> _clients = [];
    public ToolSpec[] ExtraTools { get; private set; } = [];
    public Func<JsonElement, CancellationToken, Task<JsonElement>>? ElicitationHandler { get; set; }

    public McpHub(IToolExecutor inner) => _inner = inner;

    public static async Task<McpHub> StartAsync(CodexConfig config, IToolExecutor inner, CancellationToken ct = default)
    {
        var hub = new McpHub(inner);
        foreach (var spec in ConfigService.LoadMcpServers())
        {
            if (!spec.Enabled || (string.IsNullOrWhiteSpace(spec.Command) && string.IsNullOrWhiteSpace(spec.Url)))
            {
                continue;
            }

            try
            {
                IMcpSession client = !string.IsNullOrWhiteSpace(spec.Url)
                    ? new McpHttpClient(spec)
                    : McpStdioClient.Start(spec);
                client.ElicitationHandler = async (el, innerCt) =>
                {
                    if (hub.ElicitationHandler is null)
                    {
                        using var declined = JsonDocument.Parse("{\"action\":\"decline\"}");
                        return declined.RootElement.Clone();
                    }
                    return await hub.ElicitationHandler(el, innerCt);
                };
                await client.InitializeAsync(ct);
                var tools = await client.ListToolsAsync(spec.Id, ct);
                hub._clients.Add(client);
                hub._sessions[spec.Id] = client;
                foreach (var tool in tools)
                {
                    var local = tool.Name["mcp__".Length..];
                    var sep = local.IndexOf("__", StringComparison.Ordinal);
                    var toolName = sep >= 0 ? local[(sep + 2)..] : local;
                    hub._tools[tool.Name] = (spec.Id, toolName, client);
                }

                hub.ExtraTools = [.. hub.ExtraTools, .. tools];
            }
            catch
            {
                // Server failed to start; skip like Codex does for optional MCP.
            }
        }

        return hub;
    }

    public Task<ToolCallResult> ExecuteAsync(ToolCallRequest call, CancellationToken ct)
    {
        if (call.Name is "list_mcp_resources" or "list_mcp_resource_templates")
        {
            return ListResourcesAsync(call, ct, templates: call.Name.Contains("template"));
        }

        if (call.Name is "read_mcp_resource")
        {
            return ReadResourceAsync(call, ct);
        }

        if (_tools.TryGetValue(call.Name, out var mapped))
        {
            return CallMappedAsync(call, mapped.Tool, mapped.Client, ct);
        }

        return _inner.ExecuteAsync(call, ct);
    }

    private async Task<ToolCallResult> ListResourcesAsync(ToolCallRequest call, CancellationToken ct, bool templates = false)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var server = doc.RootElement.TryGetProperty("server", out var s) ? s.GetString() : null;
        var rows = new List<object>();
        foreach (var (id, client) in _sessions)
        {
            if (!string.IsNullOrWhiteSpace(server) && !string.Equals(id, server, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var listed = templates ? await client.ListResourceTemplatesAsync(ct) : await client.ListResourcesAsync(ct);
                var key = templates ? "resourceTemplates" : "resources";
                var resources = listed.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array
                    ? arr.EnumerateArray().Select(r => new
                    {
                        uri = r.TryGetProperty("uri", out var u) ? u.GetString() : "",
                        name = r.TryGetProperty("name", out var n) ? n.GetString() : "",
                    }).ToArray()
                    : [];
                rows.Add(new { server = id, resources });
            }
            catch (Exception ex)
            {
                rows.Add(new { server = id, error = ex.Message, resources = Array.Empty<object>() });
            }
        }

        return new ToolCallResult(call.Id, call.Name, JsonSerializer.Serialize(new { servers = rows }), false);
    }

    private async Task<ToolCallResult> ReadResourceAsync(ToolCallRequest call, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var uri = doc.RootElement.TryGetProperty("uri", out var u) ? u.GetString() ?? "" : "";
        var server = doc.RootElement.TryGetProperty("server", out var s) ? s.GetString() : null;
        if (string.IsNullOrWhiteSpace(uri))
        {
            return new ToolCallResult(call.Id, call.Name, "uri is required", true);
        }

        var pair = _sessions.FirstOrDefault(kv => string.IsNullOrWhiteSpace(server) || string.Equals(kv.Key, server, StringComparison.OrdinalIgnoreCase));
        if (pair.Value is null)
        {
            return new ToolCallResult(call.Id, call.Name, "no live MCP session", true);
        }

        try
        {
            var result = await pair.Value.ReadResourceAsync(uri, ct);
            return new ToolCallResult(call.Id, call.Name, result.GetRawText(), false);
        }
        catch (Exception ex)
        {
            return new ToolCallResult(call.Id, call.Name, ex.Message, true);
        }
    }

    private static async Task<ToolCallResult> CallMappedAsync(ToolCallRequest call, string tool, IMcpSession client, CancellationToken ct)
    {
        try
        {
            var output = await client.CallToolAsync(tool, call.ArgumentsJson, ct);
            return new ToolCallResult(call.Id, call.Name, output, false);
        }
        catch (Exception ex)
        {
            return new ToolCallResult(call.Id, call.Name, ex.Message, true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            await client.DisposeAsync();
        }
    }
}
