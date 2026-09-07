using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexSharp.Protocol;

namespace CodexSharp.Runtime;

public sealed class HttpModelClient : IModelClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly CodexConfig _config;
    private readonly HttpClient _http;

    public HttpModelClient(CodexConfig config, HttpClient? http = null)
    {
        _config = config;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        if (!string.IsNullOrWhiteSpace(config.Provider.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Provider.ApiKey);
        }
    }

    public async Task StreamAsync(ModelRequest request, Action<ModelStreamEvent> onEvent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.Provider.ApiKey))
        {
            onEvent(ModelStreamEvent.NewStreamFailed(
                "No API key. Set OPENAI_API_KEY, CODEXSHARP_API_KEY, or MiniMax, or write ~/.codexsharp/auth.json."));
            onEvent(ModelStreamEvent.NewStreamFinished("error"));
            return;
        }

        try
        {
            if (string.Equals(_config.Provider.WireApi, "responses", StringComparison.OrdinalIgnoreCase))
            {
                await StreamResponsesAsync(request, onEvent, ct);
            }
            else
            {
                await StreamChatAsync(request, onEvent, ct);
            }
        }
        catch (Exception ex)
        {
            onEvent(ModelStreamEvent.NewStreamFailed(ex.Message));
            onEvent(ModelStreamEvent.NewStreamFinished("error"));
        }
    }

    private async Task StreamChatAsync(ModelRequest request, Action<ModelStreamEvent> onEvent, CancellationToken ct)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = request.Instructions },
        };

        foreach (var message in request.Messages)
        {
            JsonNode content = message.Role is "user" or "developer"
                ? ImageMessageContent.ChatContent(message.Content ?? "")
                : JsonValue.Create(message.Content ?? "")!;
            var node = new JsonObject { ["role"] = NormalizeRole(message.Role), ["content"] = content };
            if (!string.IsNullOrEmpty(message.ToolCallId))
            {
                node["tool_call_id"] = message.ToolCallId;
            }

            if (!string.IsNullOrEmpty(message.Name) && message.Role == "tool")
            {
                node["name"] = message.Name;
            }

            if (!string.IsNullOrEmpty(message.ToolCallsJson))
            {
                node["tool_calls"] = JsonNode.Parse(ToOpenAiToolCalls(message.ToolCallsJson));
            }

            messages.Add(node);
        }

        var tools = new JsonArray();
        foreach (var tool in request.Tools)
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonNode.Parse(tool.ParametersJson) ?? new JsonObject(),
                },
            });
        }

        var payload = new JsonObject
        {
            ["model"] = request.Model,
            ["stream"] = true,
            ["messages"] = messages,
            ["tools"] = tools,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_config.Provider.BaseUrl}/chat/completions")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            onEvent(ModelStreamEvent.NewStreamFailed($"HTTP {(int)resp.StatusCode}: {body}"));
            onEvent(ModelStreamEvent.NewStreamFinished("error"));
            return;
        }

        var raw = await resp.Content.ReadAsStreamAsync(ct);

        var toolBuf = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();
        await foreach (var data in ReadSseAsync(raw, ct))
        {
            if (data is "[DONE]")
            {
                break;
            }

            using var doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                continue;
            }

            var delta = choices[0].TryGetProperty("delta", out var d) ? d : default;
            if (delta.ValueKind == JsonValueKind.Object && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                var text = content.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    onEvent(ModelStreamEvent.NewOutputTextDelta(text));
                }
            }

            if (delta.ValueKind == JsonValueKind.Object && delta.TryGetProperty("tool_calls", out var toolCalls))
            {
                foreach (var part in toolCalls.EnumerateArray())
                {
                    var index = part.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;
                    if (!toolBuf.TryGetValue(index, out var buf))
                    {
                        buf = ("", "", new StringBuilder());
                    }

                    var id = part.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? buf.Id : buf.Id;
                    var name = buf.Name;
                    if (part.TryGetProperty("function", out var fn))
                    {
                        if (fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                        {
                            name = n.GetString() ?? name;
                        }

                        if (fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String)
                        {
                            buf.Args.Append(a.GetString());
                        }
                    }

                    toolBuf[index] = (id, name, buf.Args);
                }
            }
        }

        foreach (var call in toolBuf.OrderBy(kv => kv.Key).Select(kv => kv.Value))
        {
            onEvent(ModelStreamEvent.NewToolCallReady(new ToolCallRequest(
                string.IsNullOrEmpty(call.Id) ? Ids.call () : call.Id,
                call.Name,
                call.Args.ToString())));
        }

        onEvent(ModelStreamEvent.NewStreamFinished("stop"));
    }

    private async Task StreamResponsesAsync(ModelRequest request, Action<ModelStreamEvent> onEvent, CancellationToken ct)
    {
        var input = new JsonArray();
        foreach (var message in request.Messages)
        {
            JsonNode content = message.Role == "tool"
                ? JsonValue.Create($"[tool {message.Name} {message.ToolCallId}]\n{message.Content}")!
                : ImageMessageContent.ResponsesContent(message.Content ?? "");
            input.Add(new JsonObject
            {
                ["role"] = NormalizeRole(message.Role) == "tool" ? "user" : NormalizeRole(message.Role),
                ["content"] = content,
            });
        }

        var tools = new JsonArray();
        foreach (var tool in request.Tools)
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = JsonNode.Parse(tool.ParametersJson) ?? new JsonObject(),
            });
        }

        if (FeatureFlags.IsEnabled("web_search"))
        {
            tools.Add(new JsonObject { ["type"] = "web_search" });
        }

        var payload = new JsonObject
        {
            ["model"] = request.Model,
            ["instructions"] = request.Instructions,
            ["input"] = input,
            ["tools"] = tools,
            ["stream"] = true,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_config.Provider.BaseUrl}/responses")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            onEvent(ModelStreamEvent.NewStreamFailed($"HTTP {(int)resp.StatusCode}: {body}"));
            onEvent(ModelStreamEvent.NewStreamFinished("error"));
            return;
        }

        var raw = await resp.Content.ReadAsStreamAsync(ct);

        await foreach (var data in ReadSseAsync(raw, ct))
        {
            if (data is "[DONE]")
            {
                break;
            }

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : "";
            switch (type)
            {
                case "response.output_text.delta":
                    if (root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.String)
                    {
                        onEvent(ModelStreamEvent.NewOutputTextDelta(delta.GetString() ?? ""));
                    }
                    break;
                case "response.output_item.done":
                    if (root.TryGetProperty("item", out var item)
                        && item.TryGetProperty("type", out var itemType)
                        && itemType.GetString() is "function_call")
                    {
                        var id = item.TryGetProperty("call_id", out var cid) ? cid.GetString() : Ids.call ();
                        var name = item.TryGetProperty("name", out var n) ? n.GetString() : "";
                        var args = item.TryGetProperty("arguments", out var a) ? a.GetString() : "{}";
                        onEvent(ModelStreamEvent.NewToolCallReady(new ToolCallRequest(id ?? Ids.call (), name ?? "", args ?? "{}")));
                    }
                    break;
            }
        }

        onEvent(ModelStreamEvent.NewStreamFinished("stop"));
    }

    private static string NormalizeRole(string role) =>
        role is "developer" or "system" ? "system" : role;

    private static string ToOpenAiToolCalls(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var arr = new JsonArray();
        foreach (var call in doc.RootElement.EnumerateArray())
        {
            arr.Add(new JsonObject
            {
                ["id"] = call.GetProperty("id").GetString(),
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = call.GetProperty("name").GetString(),
                    ["arguments"] = call.GetProperty("argumentsJson").GetString() ?? "{}",
                },
            });
        }

        return arr.ToJsonString();
    }

    private static async IAsyncEnumerable<string> ReadSseAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
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
            else if (line.StartsWith('{'))
            {
                yield return line;
            }
        }
    }
}



