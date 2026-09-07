using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using CodexSharp.Protocol;
using Microsoft.Extensions.AI;
using OpenAI;

namespace CodexSharp.Runtime;

public sealed class HttpModelClient : IModelClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly CodexConfig _config;
    private readonly HttpClient? _http;
    private readonly IChatClient? _chat;
    private readonly bool _ownsChat;

    public HttpModelClient(CodexConfig config, HttpClient? http = null)
    {
        _config = config;
        _http = http;
        _chat = null;
        _ownsChat = true;
    }

    public HttpModelClient(CodexConfig config, IChatClient chat)
    {
        _config = config;
        _http = null;
        _chat = chat;
        _ownsChat = false;
    }

    public async Task StreamAsync(ModelRequest request, Action<ModelStreamEvent> onEvent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.Provider.ApiKey) && _chat is null)
        {
            onEvent(ModelStreamEvent.NewStreamFailed(
                "No API key. Set OPENAI_API_KEY, CODEXSHARP_API_KEY, or MiniMax, or write ~/.codexsharp/auth.json."));
            onEvent(ModelStreamEvent.NewStreamFinished("error"));
            return;
        }

        IChatClient? owned = null;
        var chat = _chat ?? (owned = CreateChatClient(_config, _http));

        try
        {
            var messages = new List<ChatMessage>();
            var tools = BuildTools(request, _config);
            var options = new ChatOptions
            {
                ModelId = request.Model,
                Tools = tools.Count == 0 ? null : tools,
            };

            if (IsResponses(_config))
            {
                options.Instructions = request.Instructions;
            }
            else if (!string.IsNullOrWhiteSpace(request.Instructions))
            {
                messages.Add(new ChatMessage(ChatRole.System, request.Instructions));
            }

            foreach (var message in request.Messages)
            {
                messages.Add(ToChatMessage(message));
            }

            var calls = new Dictionary<string, (string Name, string Args)>(StringComparer.Ordinal);
            await foreach (var update in chat.GetStreamingResponseAsync(messages, options, ct))
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent text when !string.IsNullOrEmpty(text.Text):
                            onEvent(ModelStreamEvent.NewOutputTextDelta(text.Text));
                            break;
                        case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                            onEvent(ModelStreamEvent.NewReasoningDelta(reasoning.Text));
                            break;
                        case FunctionCallContent call:
                            MergeCall(calls, call);
                            break;
                    }
                }
            }

            foreach (var (id, call) in calls)
            {
                onEvent(ModelStreamEvent.NewToolCallReady(new ToolCallRequest(
                    id,
                    call.Name,
                    string.IsNullOrEmpty(call.Args) ? "{}" : call.Args)));
            }

            onEvent(ModelStreamEvent.NewStreamFinished("stop"));
        }
        catch (Exception ex)
        {
            onEvent(ModelStreamEvent.NewStreamFailed(ex.Message));
            onEvent(ModelStreamEvent.NewStreamFinished("error"));
        }
        finally
        {
            if (_ownsChat)
            {
                owned?.Dispose();
            }
        }
    }

    internal static IList<AITool> BuildTools(ModelRequest request, CodexConfig config)
    {
        var tools = new List<AITool>();
        foreach (var tool in request.Tools ?? [])
        {
            JsonElement schema;
            try
            {
                schema = JsonDocument.Parse(string.IsNullOrWhiteSpace(tool.ParametersJson) ? "{}" : tool.ParametersJson).RootElement.Clone();
            }
            catch (JsonException)
            {
                schema = JsonDocument.Parse("{}").RootElement.Clone();
            }

            tools.Add(AIFunctionFactory.CreateDeclaration(tool.Name, tool.Description, schema));
        }

        if (IsResponses(config) && FeatureFlags.IsEnabled("web_search"))
        {
            tools.Add(new HostedWebSearchTool());
        }

        return tools;
    }

    internal static ChatMessage ToChatMessage(HistoryMessage message)
    {
        var role = message.Role switch
        {
            "assistant" => ChatRole.Assistant,
            "tool" => ChatRole.Tool,
            "system" or "developer" => ChatRole.System,
            _ => ChatRole.User,
        };

        if (role == ChatRole.Tool)
        {
            return new ChatMessage(role, [new FunctionResultContent(message.ToolCallId ?? "", message.Content)]);
        }

        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(message.ToolCallsJson))
        {
            if (!string.IsNullOrEmpty(message.Content))
            {
                contents.Add(new TextContent(message.Content));
            }

            try
            {
                using var doc = JsonDocument.Parse(message.ToolCallsJson);
                foreach (var call in doc.RootElement.EnumerateArray())
                {
                    var id = call.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Ids.call() : Ids.call();
                    var name = call.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                    var argsJson = call.TryGetProperty("argumentsJson", out var argsEl) ? argsEl.GetString() ?? "{}" : "{}";
                    contents.Add(new FunctionCallContent(id, name, DeserializeArgs(argsJson)));
                }
            }
            catch (JsonException)
            {
            }

            return new ChatMessage(ChatRole.Assistant, contents);
        }

        if ((role == ChatRole.User || message.Role is "developer")
            && ImageMessageContent.TryCollect(message.Content ?? "", out var rest, out var dataUrls)
            && dataUrls.Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(rest))
            {
                contents.Add(new TextContent(rest.Trim()));
            }

            foreach (var url in dataUrls)
            {
                if (TryDataContent(url, out var data))
                {
                    contents.Add(data);
                }
            }

            return new ChatMessage(role, contents);
        }

        return new ChatMessage(role, message.Content ?? "");
    }

    private static void MergeCall(Dictionary<string, (string Name, string Args)> calls, FunctionCallContent call)
    {
        var id = string.IsNullOrWhiteSpace(call.CallId) ? Ids.call() : call.CallId;
        var args = SerializeArguments(call.Arguments);
        if (calls.TryGetValue(id, out var existing))
        {
            var name = string.IsNullOrWhiteSpace(call.Name) ? existing.Name : call.Name;
            if (args == "{}")
            {
                args = existing.Args;
            }

            calls[id] = (name, args);
            return;
        }

        calls[id] = (call.Name ?? "", args);
    }

    private static string SerializeArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return "{}";
        }

        return JsonSerializer.Serialize(arguments, Json);
    }

    private static Dictionary<string, object?> DeserializeArgs(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool TryDataContent(string url, out DataContent content)
    {
        content = null!;
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var comma = url.IndexOf(',');
        if (comma < 0)
        {
            return false;
        }

        var header = url[..comma];
        var payload = url[(comma + 1)..];
        var media = "application/octet-stream";
        var colon = header.IndexOf(':');
        if (colon >= 0)
        {
            var rest = header[(colon + 1)..];
            var semi = rest.IndexOf(';');
            media = semi >= 0 ? rest[..semi] : rest;
        }

        try
        {
            content = new DataContent(Convert.FromBase64String(payload), media);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsResponses(CodexConfig config) =>
        string.Equals(config.Provider.WireApi, "responses", StringComparison.OrdinalIgnoreCase);

    private static IChatClient CreateChatClient(CodexConfig config, HttpClient? http)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = EndpointOf(config.Provider.BaseUrl),
            NetworkTimeout = TimeSpan.FromMinutes(10),
        };
        if (http is not null)
        {
            options.Transport = new HttpClientPipelineTransport(http);
        }

        var openai = new OpenAIClient(new ApiKeyCredential(config.Provider.ApiKey), options);
        var model = string.IsNullOrWhiteSpace(config.Model) ? "gpt-4.1-mini" : config.Model;
        if (IsResponses(config))
        {
#pragma warning disable OPENAI001
            return openai.GetResponsesClient().AsIChatClient(model);
#pragma warning restore OPENAI001
        }

        return openai.GetChatClient(model).AsIChatClient();
    }

    private static Uri EndpointOf(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new Uri("https://api.openai.com/v1");
        }

        return new Uri(baseUrl.Trim().TrimEnd('/'));
    }
}

