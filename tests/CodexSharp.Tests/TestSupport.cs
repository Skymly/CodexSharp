using CodexSharp.Protocol;

namespace CodexSharp.Tests;

public sealed class ScriptedModelClient : IModelClient
{
    private readonly Queue<ModelStreamEvent[]> _turns;

    public ScriptedModelClient(params ModelStreamEvent[][] turns)
    {
        _turns = new Queue<ModelStreamEvent[]>(turns);
    }

    public Task StreamAsync(ModelRequest request, Action<ModelStreamEvent> onEvent, CancellationToken ct)
    {
        var batch = _turns.Count > 0 ? _turns.Dequeue() : [ModelStreamEvent.NewOutputTextDelta("done"), ModelStreamEvent.NewStreamFinished("stop")];
        foreach (var evt in batch)
        {
            onEvent(evt);
        }

        return Task.CompletedTask;
    }
}

public sealed class RecordingSink : IEventSink
{
    public List<AgentEvent> Events { get; } = [];
    public void Emit(AgentEvent evt) => Events.Add(evt);
}

public sealed class BlockingHookHost : IHookHost
{
    public HookDecision Fire(string eventName, string tool, string argumentsJson) =>
        eventName == "PreToolUse" ? HookDecision.NewHookBlock("deny from test") : HookDecision.HookContinue;
}

public sealed class RecordingSubagentHooks : IHookHost
{
    public List<string> Events { get; } = [];
    public HookDecision Fire(string eventName, string tool, string argumentsJson)
    {
        Events.Add(eventName);
        return HookDecision.HookContinue;
    }
}
