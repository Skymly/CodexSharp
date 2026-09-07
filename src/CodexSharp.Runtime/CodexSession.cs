using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CodexSharp.Core;
using CodexSharp.Protocol;

namespace CodexSharp.Runtime;

public sealed record ThreadInfo(string Id, string Title, string Cwd, string Model, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool Archived, string Path, bool Pinned = false);

public sealed record ThreadSearchHit(ThreadInfo Thread, string Snippet);

public sealed class JsonlThreadStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public IReadOnlyList<ThreadInfo> List(bool archived = false)
    {
        CodexPaths.EnsureLayout();
        var dir = archived ? CodexPaths.ArchivedDir : CodexPaths.SessionsDir;
        return Directory.EnumerateFiles(dir, "*.jsonl")
            .Select(ReadInfo)
            .Where(x => x is not null)
            .Cast<ThreadInfo>()
            .Select(t => t with { Pinned = ThreadPins.IsPinned(t.Id) })
            .OrderByDescending(x => x.Pinned)
            .ThenByDescending(x => x.UpdatedAt)
            .ToList();
    }

    public ThreadInfo Create(CodexConfig config, string? title = null)
    {
        CodexPaths.EnsureLayout();
        var id = Ids.thread ();
        var now = DateTimeOffset.UtcNow;
        var info = new ThreadInfo(id, title ?? "New thread", config.Cwd, config.Model, now, now, false, FilePath(id, false));
        Append(info, new { type = "session_meta", thread = info, config.SandboxMode, config.ApprovalPolicy });
        return info;
    }

    public ThreadInfo? Find(string id)
    {
        var active = FilePath(id, false);
        if (File.Exists(active))
        {
            return ReadInfo(active);
        }

        var archived = FilePath(id, true);
        return File.Exists(archived) ? ReadInfo(archived) : null;
    }

    public void AppendEvent(string threadId, AgentEvent evt)
    {
        var info = Find(threadId);
        if (info is null)
        {
            return;
        }

        Append(info, new
        {
            type = "event",
            ts = DateTimeOffset.UtcNow,
            eventType = evt.GetType().Name,
            payload = evt.ToString(),
        });
    }

    public void Archive(string threadId)
    {
        var src = FilePath(threadId, false);
        if (!File.Exists(src))
        {
            return;
        }

        Directory.CreateDirectory(CodexPaths.ArchivedDir);
        File.Move(src, FilePath(threadId, true), overwrite: true);
    }

    public bool Unarchive(string threadId)
    {
        var src = FilePath(threadId, true);
        if (!File.Exists(src))
        {
            return false;
        }

        Directory.CreateDirectory(CodexPaths.SessionsDir);
        File.Move(src, FilePath(threadId, false), overwrite: true);
        return true;
    }

    public bool Delete(string threadId)
    {
        var any = false;
        foreach (var path in new[] { FilePath(threadId, false), FilePath(threadId, true) })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            File.Delete(path);
            any = true;
        }

        ThreadPins.Set(threadId, false);
        ThreadSections.Move(threadId, null, null);
        return any;
    }

    public void SaveHistory(string threadId, IReadOnlyList<HistoryMessage> messages)
    {
        var info = Find(threadId);
        if (info is null)
        {
            return;
        }

        Append(info, new { type = "history_snapshot", messages });
    }

    public IReadOnlyList<HistoryMessage> LoadHistory(string threadId)
    {
        var info = Find(threadId);
        if (info is null || !File.Exists(info.Path))
        {
            return [];
        }

        HistoryMessage[]? last = null;
        foreach (var line in File.ReadLines(info.Path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("type", out var type)
                    && type.GetString() == "history_snapshot"
                    && doc.RootElement.TryGetProperty("messages", out var msgs))
                {
                    last = JsonSerializer.Deserialize<HistoryMessage[]>(msgs.GetRawText(), Json);
                }
            }
            catch
            {
                // skip corrupt lines
            }
        }

        return last ?? [];
    }

    public string? FindLastPatch(string threadId)
    {
        static string? Extract(string text)
        {
            var start = text.IndexOf("*** Begin Patch", StringComparison.Ordinal);
            if (start < 0) return null;
            var end = text.IndexOf("*** End Patch", start, StringComparison.Ordinal);
            return end < 0 ? text[start..] : text[start..(end + "*** End Patch".Length)];
        }

        foreach (var message in LoadHistory(threadId).Reverse())
        {
            var patch = Extract(message.Content) ?? Extract(message.ToolCallsJson);
            if (patch is not null) return patch;
        }

        var info = Find(threadId);
        if (info is null || !File.Exists(info.Path)) return null;
        string? found = null;
        foreach (var line in File.ReadLines(info.Path))
        {
            var patch = Extract(line);
            if (patch is not null) found = patch;
        }
        return found;
    }

    public IReadOnlyList<ThreadSearchHit> Search(string term, bool archived = false, int limit = 40)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return [];
        }

        var needle = term.Trim();
        var hits = new List<ThreadSearchHit>();
        foreach (var thread in List(archived))
        {
            var snippet = thread.Title;
            var matched = thread.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || thread.Id.Contains(needle, StringComparison.OrdinalIgnoreCase);
            if (!matched)
            {
                foreach (var message in LoadHistory(thread.Id))
                {
                    if (message.Content.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    {
                        matched = true;
                        snippet = message.Content.Replace('\r', ' ').Replace('\n', ' ');
                        if (snippet.Length > 160)
                        {
                            snippet = snippet[..160] + "...";
                        }

                        break;
                    }
                }
            }

            if (matched)
            {
                hits.Add(new ThreadSearchHit(thread, snippet));
            }

            if (hits.Count >= limit)
            {
                break;
            }
        }

        return hits;
    }

    private static string FilePath(string id, bool archived) =>
        Path.Combine(archived ? CodexPaths.ArchivedDir : CodexPaths.SessionsDir, id + ".jsonl");

    private static void Append(ThreadInfo info, object payload)
    {
        File.AppendAllText(info.Path, JsonSerializer.Serialize(payload, Json) + Environment.NewLine);
    }

    private static ThreadInfo? ReadInfo(string path)
    {
        try
        {
            var first = File.ReadLines(path).FirstOrDefault();
            if (first is null)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(first);
            if (!doc.RootElement.TryGetProperty("thread", out var thread))
            {
                return new ThreadInfo(Path.GetFileNameWithoutExtension(path), Path.GetFileNameWithoutExtension(path), "", "", File.GetCreationTimeUtc(path), File.GetLastWriteTimeUtc(path), path.Contains("archived"), path);
            }

            return new ThreadInfo(
                thread.GetProperty("id").GetString() ?? Path.GetFileNameWithoutExtension(path),
                thread.GetProperty("title").GetString() ?? "thread",
                thread.TryGetProperty("cwd", out var cwd) ? cwd.GetString() ?? "" : "",
                thread.TryGetProperty("model", out var model) ? model.GetString() ?? "" : "",
                File.GetCreationTimeUtc(path),
                File.GetLastWriteTimeUtc(path),
                path.Contains("archived", StringComparison.OrdinalIgnoreCase),
                path);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class InteractiveApprover : IApprover
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ApprovalDecision>> _pending = new();
    public Func<string, string, string, Task<ApprovalDecision>>? Prompt { get; set; }

    public Task<ApprovalDecision> RequestAsync(string requestId, string command, string cwd, string reason, CancellationToken ct)
    {
        if (Prompt is not null)
        {
            return Prompt(command, cwd, reason);
        }

        var tcs = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;
        ct.Register(() => tcs.TrySetResult(ApprovalDecision.Abort));
        return tcs.Task;
    }

    public bool Complete(string requestId, bool allow, string? reason = null)
    {
        if (_pending.TryRemove(requestId, out var tcs))
        {
            tcs.TrySetResult(allow ? ApprovalDecision.Allow : ApprovalDecision.NewDeny(reason ?? "denied"));
            return true;
        }

        return false;
    }
}

public sealed class AutoApprover(bool allow) : IApprover
{
    public Task<ApprovalDecision> RequestAsync(string requestId, string command, string cwd, string reason, CancellationToken ct) =>
        Task.FromResult(allow ? ApprovalDecision.Allow : ApprovalDecision.NewDeny("auto-deny"));
}

public sealed class InteractiveUserInput : IUserInputHost
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();
    public Func<string, Task<string>>? Prompt { get; set; }

    public Task<string> RequestAsync(string requestId, string questionsJson, CancellationToken ct)
    {
        if (Prompt is not null)
        {
            return Prompt(questionsJson);
        }

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;
        ct.Register(() => tcs.TrySetResult("{\"answers\":{},\"note\":\"cancelled\"}"));
        return tcs.Task;
    }

    public bool Complete(string requestId, string answersJson)
    {
        if (_pending.TryRemove(requestId, out var tcs))
        {
            tcs.TrySetResult(string.IsNullOrWhiteSpace(answersJson) ? "{\"answers\":{}}" : answersJson);
            return true;
        }

        return false;
    }
}

public sealed class BroadcastingSink : IEventSink
{
    public event Action<AgentEvent>? Received;
    public void Emit(AgentEvent evt) => Received?.Invoke(evt);
}

public sealed class CodexSession
{
    private readonly JsonlThreadStore _store = new();
    private readonly BroadcastingSink _sink = new();
    private readonly InteractiveApprover _approver = new();
    private readonly InteractiveUserInput _userInput = new();
    private readonly InteractiveUserInput _mcpElicit = new();
    private readonly List<HistoryMessage> _history = new();
    private CodexConfig _config;
    private ThreadInfo _thread;
    private SubagentExecutor? _collab;
    private readonly CommandExecBroker _unified = new();
    private readonly ConcurrentQueue<string> _steerQueue = new();
    private int _inTurn;

    public CodexSession(CodexConfig config, ThreadInfo thread)
    {
        _config = config;
        _thread = thread;
        _sink.Received += evt =>
        {
            _store.AppendEvent(thread.Id, evt);
            Event?.Invoke(evt);
            if (evt is AgentEvent.ThreadStarted)
            {
                HookRunner.Run(_config.Home, "SessionStart", new { type = "SessionStart", threadId = thread.Id }, _config.Cwd);
            }
            if (evt is AgentEvent.TurnStarted started)
            {
                ActiveTurnId = started.TurnId;
            }
            if (evt is AgentEvent.TurnCompleted done)
            {
                _store.SaveHistory(thread.Id, _history);
                HookRunner.Run(_config.Home, "Stop", new { type = "Stop", threadId = thread.Id, turnId = done.TurnId }, _config.Cwd);
                TurnNotify.Fire("agent-turn-complete", thread.Id, done.TurnId);
                var (total, last) = EstimateTokens();
                Event?.Invoke(AgentEvent.NewTokenUsage(total, last));
            }
        };
    }

    public event Action<AgentEvent>? Event;
    public CodexConfig Config => _config;
    public ThreadInfo Thread => _thread;
    public InteractiveApprover Approver => _approver;
    public InteractiveUserInput UserInput => _userInput;
    public InteractiveUserInput McpElicitation => _mcpElicit;
    public IReadOnlyList<HistoryMessage> History => _history;
    public string? Goal { get; private set; }
    public string GoalStatus { get; private set; } = "active";
    public string? ActiveTurnId { get; private set; }

    public bool TrySteer(string text)
    {
        if (Volatile.Read(ref _inTurn) <= 0 || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        _steerQueue.Enqueue(text);
        return true;
    }

    private sealed class QueueSteerHost(ConcurrentQueue<string> queue) : ISteerHost
    {
        public string TryDequeue() => queue.TryDequeue(out var text) ? text : "";
    }

    public static CodexSession Start(CodexConfig? config = null, string? title = null)
    {
        config ??= ConfigService.Load();
        var store = new JsonlThreadStore();
        var thread = store.Create(config, title);
        var session = new CodexSession(config, thread);
        session._sink.Emit(AgentEvent.NewThreadStarted(thread.Id, thread.Title, thread.Cwd));
        return session;
    }

    public static CodexSession Resume(string threadId, CodexConfig? config = null)
    {
        config ??= ConfigService.Load();
        var store = new JsonlThreadStore();
        var thread = store.Find(threadId) ?? throw new InvalidOperationException($"Unknown thread {threadId}");
        var session = new CodexSession(config, thread);
        session.RestoreHistory(store.LoadHistory(threadId));
        return session;
    }

    public IReadOnlyList<ThreadInfo> ListThreads() => _store.List();

    public bool TryCompact()
    {
        var hooks = new ConfigHookHost(_config.Home, _config.Cwd);
        if (hooks.Fire("PreCompact", "compact", "").IsHookBlock)
        {
            return false;
        }

        var before = _history.Count;
        var ok = Compact.apply(_history);
        if (ok)
        {
            hooks.Fire("PostCompact", "compact", "");
            _store.SaveHistory(_thread.Id, _history);
            _sink.Emit(AgentEvent.NewCompacted(Math.Max(0, before - _history.Count)));
        }

        return ok;
    }

    public void SetGoal(string objective, string? status = null)
    {
        Goal = objective;
        GoalStatus = status ?? "active";
    }

    public void SetName(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            _thread = _thread with { Title = name.Trim() };
        }
    }

    public void ApplySettings(string? model = null, string? sandbox = null, string? approval = null, string? cwd = null, string? effort = null)
    {
        _config = new CodexConfig(
            _config.Home,
            string.IsNullOrWhiteSpace(model) ? _config.Model : model,
            string.IsNullOrWhiteSpace(effort) ? _config.ReasoningEffort : effort,
            _config.Provider,
            string.IsNullOrWhiteSpace(approval) ? _config.ApprovalPolicy : approval,
            string.IsNullOrWhiteSpace(sandbox) ? _config.SandboxMode : sandbox,
            _config.DeveloperInstructions,
            _config.UserInstructions,
            string.IsNullOrWhiteSpace(cwd) ? _config.Cwd : cwd,
            _config.NetworkAccess,
            _config.MaxTurns);
    }

    public void SetCollaborationMode(string mode, bool persist = true)
    {
        mode = CollaborationModes.Normalize(mode);
        if (persist)
        {
            ConfigService.WriteUserKeys(new Dictionary<string, string> { ["collaboration_mode"] = mode });
        }

        _config = new CodexConfig(
            _config.Home,
            _config.Model,
            _config.ReasoningEffort,
            _config.Provider,
            _config.ApprovalPolicy,
            _config.SandboxMode,
            CollaborationModes.MergeDeveloper(_config.DeveloperInstructions, mode),
            _config.UserInstructions,
            _config.Cwd,
            _config.NetworkAccess,
            _config.MaxTurns);
    }

    public void SetPersonality(string personality, bool persist = true)
    {
        personality = PersonalityModes.Normalize(personality);
        if (persist)
        {
            ConfigService.WriteUserKeys(new Dictionary<string, string> { ["personality"] = personality });
        }

        _config = new CodexConfig(
            _config.Home,
            _config.Model,
            _config.ReasoningEffort,
            _config.Provider,
            _config.ApprovalPolicy,
            _config.SandboxMode,
            PersonalityModes.MergeDeveloper(_config.DeveloperInstructions, personality),
            _config.UserInstructions,
            _config.Cwd,
            _config.NetworkAccess,
            _config.MaxTurns);
    }

    public void ClearGoal()
    {
        Goal = null;
        GoalStatus = "cleared";
    }

    public bool ResolveApproval(string requestId, bool allow, string? reason = null) =>
        _approver.Complete(requestId, allow, reason);

    public bool ResolveUserInput(string requestId, string answersJson) =>
        _userInput.Complete(requestId, answersJson);

    public bool ResolveMcpElicitation(string requestId, string answersJson) =>
        _mcpElicit.Complete(requestId, answersJson);

    public IReadOnlyList<SubagentSnapshot> ListSubagents() =>
        _collab?.Snapshot() ?? Array.Empty<SubagentSnapshot>();


    public string ExportMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {_thread.Title}");
        sb.AppendLine();
        foreach (var message in _history)
        {
            sb.AppendLine($"## {message.Role}");
            sb.AppendLine(message.Content);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public (long Total, long Last) EstimateTokens()
    {
        static long Tokens(string? text) => Math.Max(0, (text?.Length ?? 0) / 4);
        var total = _history.Sum(m => Tokens(m.Content));
        var last = _history.TakeLast(Math.Min(2, _history.Count)).Sum(m => Tokens(m.Content));
        return (total, last);
    }

    public async Task<string> RunTurnAsync(string userText, CancellationToken ct = default)
    {
        IModelClient model = new HttpModelClient(_config);
        IToolExecutor inner = new BuiltinToolExecutor(_config, (id, chunk) => _sink.Emit(AgentEvent.NewCommandOutputDelta(id, chunk)), EstimateTokens, _unified);
        await using var hub = await McpHub.StartAsync(_config, inner, ct);
        hub.ElicitationHandler = async (el, innerCt) =>
        {
            var requestId = Ids.approval();
            var message = el.ValueKind == JsonValueKind.Object && el.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String
                ? msg.GetString() ?? el.GetRawText()
                : el.GetRawText();
            Event?.Invoke(AgentEvent.NewMcpElicitationNeeded(requestId, message));
            var json = await _mcpElicit.RequestAsync(requestId, message, innerCt);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{\"action\":\"decline\"}" : json);
            return doc.RootElement.Clone();
        };
        _collab ??= new SubagentExecutor(hub, async (message, fork, childCt) =>
        {
            var child = fork ? Fork() : Start(_config);
            return await child.RunTurnAsync(message, childCt);
        }, new ConfigHookHost(_config.Home, _config.Cwd));
        _collab.Inner = hub;
        var deps = new AgentDeps(_config, model, _collab, _approver, _sink, hub.ExtraTools, new ConfigHookHost(_config.Home, _config.Cwd), _userInput, new QueueSteerHost(_steerQueue));
        HookRunner.Run(_config.Home, "UserPromptSubmit", new { type = "UserPromptSubmit", threadId = _thread.Id, text = userText }, _config.Cwd);
        Interlocked.Increment(ref _inTurn);
        try
        {
            return await AgentLoop.runTurn(deps, _thread.Id, _history, userText, ct);
        }
        finally
        {
            if (Interlocked.Decrement(ref _inTurn) <= 0)
            {
                ActiveTurnId = null;
            }
        }
    }

    public (string Kind, string Text) CopyLast(string? want = null) =>
        ThreadCopy.Last(_history, want);

    public async Task<(string Status, string Text)> RunRecapAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _inTurn) > 0)
        {
            return ("busy", "A recap is already being generated, or a turn is in progress.");
        }

        var prompt = Recap.BuildPrompt(_history);
        if (prompt is null)
        {
            return ("empty", "There is no conversation history to recap.");
        }

        IModelClient model = new HttpModelClient(_config);
        IToolExecutor inner = new BuiltinToolExecutor(_config);
        await using var hub = await McpHub.StartAsync(_config, inner, ct);
        var snapshot = new List<HistoryMessage>(_history);
        var deps = new AgentDeps(_config, model, inner, _approver, new NullSink(), hub.ExtraTools, new ConfigHookHost(_config.Home, _config.Cwd), _userInput, new NoopSteerHost());
        Interlocked.Increment(ref _inTurn);
        try
        {
            await AgentLoop.runTurn(deps, _thread.Id, snapshot, prompt, ct);
            for (var i = snapshot.Count - 1; i >= 0; i--)
            {
                if (snapshot[i].Role == "assistant" && !string.IsNullOrWhiteSpace(snapshot[i].Content))
                {
                    return ("completed", snapshot[i].Content);
                }
            }

            return ("errored", "Could not generate a recap.");
        }
        catch (Exception ex)
        {
            return ("errored", ex.Message);
        }
        finally
        {
            Interlocked.Decrement(ref _inTurn);
        }
    }

    public CodexSession Fork()
    {
        var child = Start(_config, $"fork of {_thread.Title}");
        foreach (var message in _history)
        {
            child._history.Add(message);
        }
        child._store.SaveHistory(child._thread.Id, child._history);
        return child;
    }

    public IReadOnlyList<HistoryMessage> SnapshotHistory() => _history.ToArray();

    public void RestoreHistory(IEnumerable<HistoryMessage> messages)
    {
        _history.Clear();
        _history.AddRange(messages);
    }

    public int InjectItems(IEnumerable<HistoryMessage> messages)
    {
        var added = 0;
        foreach (var message in messages)
        {
            if (string.IsNullOrWhiteSpace(message.Role) && string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            _history.Add(message);
            added++;
        }

        _store.SaveHistory(_thread.Id, _history);
        return added;
    }

    public readonly record struct TurnSlice(string Id, IReadOnlyList<HistoryMessage> Messages);

    public IReadOnlyList<TurnSlice> Turns()
    {
        var turns = new List<TurnSlice>();
        List<HistoryMessage>? current = null;
        var index = 0;
        foreach (var message in _history)
        {
            if (message.Role == "user")
            {
                if (current is not null)
                {
                    turns.Add(new TurnSlice($"trn_{index++}", current));
                }

                current = [message];
            }
            else
            {
                current ??= [];
                current.Add(message);
            }
        }

        if (current is not null)
        {
            turns.Add(new TurnSlice($"trn_{index}", current));
        }

        return turns;
    }

    public readonly record struct SearchOccurrence(string TurnId, string ItemId, string Snippet, int Start, int End, string TurnCursor);

    public IReadOnlyList<SearchOccurrence> SearchOccurrences(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return [];
        }

        var list = new List<SearchOccurrence>();
        foreach (var turn in Turns())
        {
            var i = 0;
            foreach (var message in turn.Messages)
            {
                var itemId = $"{turn.Id}_{i++}";
                if (message.Role is not ("user" or "assistant"))
                {
                    continue;
                }

                var idx = message.Content.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    continue;
                }

                var windowStart = Math.Max(0, idx - 40);
                var length = Math.Min(120, message.Content.Length - windowStart);
                var snippet = message.Content.Substring(windowStart, length);
                var start = idx - windowStart;
                var end = Math.Min(snippet.Length, start + term.Length);
                list.Add(new SearchOccurrence(turn.Id, itemId, snippet, start, end, turn.Id));
            }
        }

        return list;
    }

    public bool RollbackTurns(int count)
    {
        if (count < 1)
        {
            return false;
        }

        var turns = Turns();
        if (turns.Count == 0)
        {
            return false;
        }

        var keep = Math.Max(0, turns.Count - count);
        _history.Clear();
        foreach (var turn in turns.Take(keep))
        {
            _history.AddRange(turn.Messages);
        }

        _store.SaveHistory(_thread.Id, _history);
        return true;
    }

    public bool RevertBefore(string turnId)
    {
        var turns = Turns();
        var idx = -1;
        for (var i = 0; i < turns.Count; i++)
        {
            if (turns[i].Id == turnId)
            {
                idx = i;
                break;
            }
        }

        if (idx < 0)
        {
            return false;
        }

        _history.Clear();
        foreach (var turn in turns.Take(idx))
        {
            _history.AddRange(turn.Messages);
        }

        _store.SaveHistory(_thread.Id, _history);
        return true;
    }

    public Task<string> RunTurnWithAsync(IModelClient model, IToolExecutor? tools, IApprover? approver, string userText, CancellationToken ct = default)
    {
        var deps = new AgentDeps(_config, model, tools ?? new BuiltinToolExecutor(_config, (id, chunk) => _sink.Emit(AgentEvent.NewCommandOutputDelta(id, chunk)), EstimateTokens, _unified), approver ?? _approver, _sink, [], new ConfigHookHost(_config.Home, _config.Cwd), _userInput, new QueueSteerHost(_steerQueue));
        HookRunner.Run(_config.Home, "UserPromptSubmit", new { type = "UserPromptSubmit", threadId = _thread.Id, text = userText }, _config.Cwd);
        Interlocked.Increment(ref _inTurn);
        try
        {
            return AgentLoop.runTurn(deps, _thread.Id, _history, userText, ct);
        }
        finally
        {
            if (Interlocked.Decrement(ref _inTurn) <= 0)
            {
                ActiveTurnId = null;
            }
        }
    }
}

