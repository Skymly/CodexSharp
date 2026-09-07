using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodexSharp.Protocol;
using CodexSharp.Core;

namespace CodexSharp.Runtime;

/// Minimal Windows Job Object containment, inspired by vendor/codex/codex-rs/windows-sandbox-rs.
public sealed class JobObject : IDisposable
{
    private IntPtr _handle;

    public JobObject()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = 0x2000 /* JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE */,
            },
        };
        SetInformationJobObject(_handle, 9 /* JobObjectExtendedLimitInformation */, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
    }

    public bool HasHandle => _handle != IntPtr.Zero;

    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var job = new JobObject();
        return job.HasHandle;
    }

    public void Assign(Process process)
    {
        if (_handle == IntPtr.Zero || !OperatingSystem.IsWindows())
        {
            return;
        }

        AssignProcessToJobObject(_handle, process.Handle);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public int LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public int ActiveProcessLimit;
        public UIntPtr Affinity;
        public int PriorityClass;
        public int SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
}

public static class Collaboration
{
    public static readonly AsyncLocal<int> Depth = new();
}

public sealed class SubagentRecord
{
    public required string Id { get; init; }
    public required Task<string> Work { get; set; }
    public string? Result { get; set; }
    public CancellationTokenSource Cts { get; init; } = new();
}

public sealed record SubagentSnapshot(string Id, bool Done, string? Result);

/// Collaboration tools from vendor/codex/codex-rs/core/src/tools/handlers/multi_agents.
public sealed class SubagentExecutor : IToolExecutor
{
    private readonly ConcurrentDictionary<string, SubagentRecord> _agents = new();
    private readonly Func<string, bool, CancellationToken, Task<string>> _runChild;
    private readonly IHookHost _hooks;

    public IToolExecutor Inner { get; set; }

    public IReadOnlyList<SubagentSnapshot> Snapshot() =>
        _agents.Select(kv => new SubagentSnapshot(kv.Key, kv.Value.Work.IsCompleted, kv.Value.Result)).ToArray();

    public SubagentExecutor(IToolExecutor inner, Func<string, bool, CancellationToken, Task<string>> runChild, IHookHost? hooks = null)
    {
        Inner = inner;
        _runChild = runChild;
        _hooks = hooks ?? new NoopHookHost();
    }

    public static ToolSpec[] Specs { get; } =
    [
        new("spawn_agent", "Spawn a sub-agent for a well-scoped task. Returns the spawned agent id. Omit model to inherit. Do not spawn unless the task can run in parallel.",
            """{"type":"object","properties":{"message":{"type":"string"},"fork_context":{"type":"boolean"}},"required":["message"]}"""),
        new("wait_agent", "Wait for one or more sub-agents to finish.",
            """{"type":"object","properties":{"targets":{"type":"array","items":{"type":"string"}},"timeout_ms":{"type":"integer"}},"required":["targets"]}"""),
        new("send_input", "Send a follow-up prompt to a running sub-agent.",
            """{"type":"object","properties":{"target":{"type":"string"},"message":{"type":"string"}},"required":["target","message"]}"""),
        new("close_agent", "Close a sub-agent that is no longer needed.",
            """{"type":"object","properties":{"target":{"type":"string"}},"required":["target"]}"""),
    ];

    public async Task<ToolCallResult> ExecuteAsync(ToolCallRequest call, CancellationToken ct)
    {
        return call.Name switch
        {
            "spawn_agent" => await SpawnAsync(call, ct),
            "wait_agent" => await WaitAsync(call, ct),
            "send_input" => await SendAsync(call, ct),
            "close_agent" => Close(call),
            "list_agents" => ListAgents(call),
            "resume_agent" => await ResumeAsync(call, ct),
            "interrupt_agent" => Interrupt(call),
            _ => await Inner.ExecuteAsync(call, ct),
        };
    }

    private async Task<ToolCallResult> SpawnAsync(ToolCallRequest call, CancellationToken ct)
    {
        if (Collaboration.Depth.Value >= 2)
        {
            return new ToolCallResult(call.Id, call.Name, "Agent depth limit reached. Solve the task yourself.", true);
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        var fork = doc.RootElement.TryGetProperty("fork_context", out var f) && f.ValueKind == JsonValueKind.True;
        var id = Ids.newId("agent");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var work = StartWork(id, message, fork, cts.Token);

        var record = new SubagentRecord { Id = id, Work = work, Cts = cts };
        _ = work.ContinueWith(t => record.Result = t.IsCompletedSuccessfully ? t.Result : t.Exception?.GetBaseException().Message, ct);
        _agents[id] = record;
        return new ToolCallResult(call.Id, call.Name, $$"""{"id":"{{id}}","nickname":"subagent"}""", false);
    }

    private async Task<ToolCallResult> WaitAsync(ToolCallRequest call, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var targets = doc.RootElement.TryGetProperty("targets", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToArray()
            : [];
        var timeout = doc.RootElement.TryGetProperty("timeout_ms", out var ms) && ms.TryGetInt32(out var n) ? n : 30_000;
        if (targets.Length == 0)
        {
            return new ToolCallResult(call.Id, call.Name, "agent ids must be non-empty", true);
        }

        var tasks = targets.Select(id => _agents.TryGetValue(id, out var rec) ? rec.Work : Task.FromResult($"unknown agent {id}")).ToArray();
        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromMilliseconds(timeout), ct);
        }
        catch (TimeoutException)
        {
            return new ToolCallResult(call.Id, call.Name, "timed out waiting for agents", false);
        }

        var results = new List<string>();
        foreach (var id in targets)
        {
            if (_agents.TryGetValue(id, out var rec) && rec.Work.IsCompletedSuccessfully)
            {
                results.Add($"{id}: {rec.Work.Result}");
            }
            else
            {
                results.Add($"{id}: pending");
            }
        }

        return new ToolCallResult(call.Id, call.Name, string.Join('\n', results), false);
    }

    private async Task<ToolCallResult> SendAsync(ToolCallRequest call, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var target = doc.RootElement.TryGetProperty("target", out var t) ? t.GetString() ?? "" : "";
        var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        if (!_agents.ContainsKey(target))
        {
            return new ToolCallResult(call.Id, call.Name, $"unknown agent {target}", true);
        }

        var output = await _runChild(message, false, ct);
        return new ToolCallResult(call.Id, call.Name, output, false);
    }

    private ToolCallResult Close(ToolCallRequest call)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var target = doc.RootElement.TryGetProperty("target", out var t) ? t.GetString() ?? "" : "";
        if (_agents.TryRemove(target, out var rec))
        {
            try { rec.Cts.Cancel(); } catch { /* ignore */ }
            rec.Cts.Dispose();
        }
        return new ToolCallResult(call.Id, call.Name, "{}", false);
    }

    private ToolCallResult ListAgents(ToolCallRequest call)
    {
        var json = JsonSerializer.Serialize(new
        {
            agents = Snapshot().Select(a => new { id = a.Id, done = a.Done, result = a.Result }).ToArray(),
        });
        return new ToolCallResult(call.Id, call.Name, json, false);
    }

    private async Task<ToolCallResult> ResumeAsync(ToolCallRequest call, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var target = doc.RootElement.TryGetProperty("target", out var t) ? t.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(target))
            target = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        if (!_agents.TryGetValue(target, out var rec))
        {
            return new ToolCallResult(call.Id, call.Name, $"unknown agent {target}", true);
        }
        if (string.IsNullOrWhiteSpace(message))
        {
            return new ToolCallResult(call.Id, call.Name, "message is required", true);
        }

        if (!rec.Work.IsCompleted)
        {
            var output = await _runChild(message, false, rec.Cts.Token);
            return new ToolCallResult(call.Id, call.Name, output, false);
        }

        rec.Result = null;
        rec.Work = StartWork(rec.Id, message, false, rec.Cts.Token);
        _ = rec.Work.ContinueWith(task => rec.Result = task.IsCompletedSuccessfully ? task.Result : task.Exception?.GetBaseException().Message, ct);
        return new ToolCallResult(call.Id, call.Name, JsonSerializer.Serialize(new { id = rec.Id, resumed = true }), false);
    }

    private ToolCallResult Interrupt(ToolCallRequest call)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var target = doc.RootElement.TryGetProperty("target", out var t) ? t.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(target))
            target = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        if (!_agents.TryGetValue(target, out var rec))
        {
            return new ToolCallResult(call.Id, call.Name, $"unknown agent {target}", true);
        }

        if (rec.Work.IsCompleted)
        {
            return new ToolCallResult(call.Id, call.Name, JsonSerializer.Serialize(new { id = target, interrupted = false, done = true }), false);
        }

        try { rec.Cts.Cancel(); } catch { /* ignore */ }
        return new ToolCallResult(call.Id, call.Name, JsonSerializer.Serialize(new { id = target, interrupted = true }), false);
    }

    private Task<string> StartWork(string id, string message, bool fork, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var previous = Collaboration.Depth.Value;
            Collaboration.Depth.Value = previous + 1;
            _hooks.Fire("SubagentStart", "spawn_agent", id);
            try
            {
                return await _runChild(message, fork, ct);
            }
            finally
            {
                _hooks.Fire("SubagentStop", "spawn_agent", id);
                Collaboration.Depth.Value = previous;
            }
        }, ct);
}
