using System.Runtime.CompilerServices;
using CodexSharp.Protocol;
using CodexSharp.Runtime;
using Microsoft.Extensions.AI;

namespace CodexSharp.Tests;

public class MeaiModelClientTests
{
    [Fact]
    public async Task Empty_api_key_fails_without_calling_chat()
    {
        var cfg = ConfigWithKey("");
        var client = new HttpModelClient(cfg);
        var events = new List<ModelStreamEvent>();
        await client.StreamAsync(Request(), events.Add, CancellationToken.None);
        Assert.Contains(events, e => e.IsStreamFailed);
    }

    [Fact]
    public async Task Streams_text_and_function_calls_from_IChatClient()
    {
        var chat = new ScriptedChatClient
        {
            Updates =
            [
                new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Hello ")]),
                new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("world")]),
                new ChatResponseUpdate(ChatRole.Assistant, [
                    new FunctionCallContent("call_1", "shell", new Dictionary<string, object?> { ["command"] = "ls" }),
                ]),
            ],
        };
        var client = new HttpModelClient(ConfigWithKey("sk-test"), chat);
        var events = new List<ModelStreamEvent>();
        await client.StreamAsync(Request("You are CodexSharp."), events.Add, CancellationToken.None);

        Assert.Equal(
            ["Hello ", "world"],
            events.OfType<ModelStreamEvent.OutputTextDelta>().Select(e => e.Text).ToArray());
        var call = Assert.Single(events.OfType<ModelStreamEvent.ToolCallReady>()).Call;
        Assert.Equal("call_1", call.Id);
        Assert.Equal("shell", call.Name);
        Assert.Contains("ls", call.ArgumentsJson);
        Assert.Contains(events, e => e.IsStreamFinished);
        Assert.NotNull(chat.LastMessages);
        Assert.Equal(ChatRole.System, chat.LastMessages![0].Role);
        Assert.Equal("You are CodexSharp.", chat.LastMessages[0].Text);
        var tools = chat.LastOptions?.Tools;
        Assert.NotNull(tools);
        var declared = Assert.IsAssignableFrom<AIFunctionDeclaration>(Assert.Single(tools!));
        Assert.Equal("shell", declared.Name);
        Assert.False(declared is AIFunction);
    }

    [Fact]
    public async Task Maps_tool_history_to_function_result()
    {
        var chat = new ScriptedChatClient
        {
            Updates = [new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")])],
        };
        var client = new HttpModelClient(ConfigWithKey("sk-test"), chat);
        var request = new ModelRequest(
            "gpt-4.1-mini",
            "",
            [
                new HistoryMessage("user", "run it", "", "", ""),
                new HistoryMessage("assistant", "", "", "", """[{"id":"call_9","name":"shell","argumentsJson":"{\"command\":\"pwd\"}"}]"""),
                new HistoryMessage("tool", "exit=0", "call_9", "shell", ""),
            ],
            [],
            "medium");
        await client.StreamAsync(request, _ => { }, CancellationToken.None);
        Assert.Equal(3, chat.LastMessages!.Count);
        Assert.Contains(chat.LastMessages[1].Contents, c => c is FunctionCallContent);
        var result = Assert.Single(chat.LastMessages[2].Contents.OfType<FunctionResultContent>());
        Assert.Equal("call_9", result.CallId);
        Assert.Equal("exit=0", result.Result?.ToString());
    }

    private static CodexConfig ConfigWithKey(string key)
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
        var provider = new ModelProvider(cfg.Provider.Id, cfg.Provider.Name, cfg.Provider.BaseUrl, cfg.Provider.EnvKey, "chat", key);
        return new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, provider, "never", "workspace-write", "", "", root, false, 8);
    }

    private static ModelRequest Request(string instructions = "hi") =>
        new(
            "gpt-4.1-mini",
            instructions,
            [new HistoryMessage("user", "hello", "", "", "")],
            [new ToolSpec("shell", "Run a command", """{"type":"object"}""")],
            "medium");
}

file sealed class ScriptedChatClient : IChatClient
{
    public List<ChatMessage>? LastMessages { get; private set; }
    public ChatOptions? LastOptions { get; private set; }
    public List<ChatResponseUpdate> Updates { get; init; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        LastMessages = messages.ToList();
        LastOptions = options;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastMessages = messages.ToList();
        LastOptions = options;
        foreach (var update in Updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }

        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}


