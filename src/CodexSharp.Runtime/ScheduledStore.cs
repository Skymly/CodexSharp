using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexSharp.Runtime;

public enum ScheduledKind
{
    Independent,
    Thread,
}

public sealed class ScheduledTask
{
    public string Id { get; set; } = "";
    public ScheduledKind Kind { get; set; }
    public string Prompt { get; set; } = "";
    public int EveryMinutes { get; set; } = 1;
    public string? ThreadId { get; set; }
    public DateTimeOffset NextRun { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed record ScheduledRun(string TaskId, DateTimeOffset At, string Status, string? Note);

/// Local in-app Scheduled (AUTO-01 / AUTO-02). No Windows Task Scheduler, no event triggers.
public static class ScheduledStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = true,
    };

    public static string FilePath => Path.Combine(CodexPaths.Home, "scheduled.json");
    public static string RunsPath => Path.Combine(CodexPaths.Home, "scheduled-runs.json");

    public static ScheduledTask Create(string prompt, int everyMinutes, string? threadId, DateTimeOffset now)
    {
        var minutes = Math.Max(1, everyMinutes);
        var task = new ScheduledTask
        {
            Id = "sch_" + Guid.NewGuid().ToString("N")[..12],
            Kind = string.IsNullOrWhiteSpace(threadId) ? ScheduledKind.Independent : ScheduledKind.Thread,
            Prompt = prompt.Trim(),
            EveryMinutes = minutes,
            ThreadId = string.IsNullOrWhiteSpace(threadId) ? null : threadId,
            NextRun = now.AddMinutes(minutes),
            Enabled = true,
        };
        lock (Gate)
        {
            var tasks = LoadTasks();
            tasks.Add(task);
            SaveTasks(tasks);
        }
        return task;
    }

    public static IReadOnlyList<ScheduledTask> List()
    {
        lock (Gate)
        {
            return LoadTasks();
        }
    }

    public static bool Cancel(string id)
    {
        lock (Gate)
        {
            var tasks = LoadTasks();
            var n = tasks.RemoveAll(t => t.Id == id);
            SaveTasks(tasks);
            return n > 0;
        }
    }

    public static IReadOnlyList<ScheduledRun> Runs()
    {
        lock (Gate)
        {
            return LoadRuns();
        }
    }

    public static IReadOnlyList<ScheduledRun> Tick(DateTimeOffset now, bool appRunning, Action<ScheduledTask> fire)
    {
        lock (Gate)
        {
            var tasks = LoadTasks();
            var runs = LoadRuns();
            var emitted = new List<ScheduledRun>();
            foreach (var task in tasks.Where(t => t.Enabled && t.NextRun <= now))
            {
                ScheduledRun run;
                if (!appRunning)
                {
                    run = new ScheduledRun(task.Id, now, "skipped", "app not running");
                }
                else
                {
                    try
                    {
                        fire(task);
                        run = new ScheduledRun(task.Id, now, "started", null);
                    }
                    catch (Exception ex)
                    {
                        run = new ScheduledRun(task.Id, now, "failed", ex.Message);
                    }
                }

                task.NextRun = now.AddMinutes(Math.Max(1, task.EveryMinutes));
                runs.Add(run);
                emitted.Add(run);
            }

            SaveTasks(tasks);
            SaveRuns(runs);
            return emitted;
        }
    }

    private static List<ScheduledTask> LoadTasks()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            return JsonSerializer.Deserialize<List<ScheduledTask>>(File.ReadAllText(FilePath), Json) ?? [];
        }
        catch { return []; }
    }

    private static List<ScheduledRun> LoadRuns()
    {
        try
        {
            if (!File.Exists(RunsPath)) return [];
            return JsonSerializer.Deserialize<List<ScheduledRun>>(File.ReadAllText(RunsPath), Json) ?? [];
        }
        catch { return []; }
    }

    private static void SaveTasks(List<ScheduledTask> tasks)
    {
        CodexPaths.EnsureLayout();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(tasks, Json));
    }

    private static void SaveRuns(List<ScheduledRun> runs)
    {
        CodexPaths.EnsureLayout();
        File.WriteAllText(RunsPath, JsonSerializer.Serialize(runs.TakeLast(200).ToList(), Json));
    }
}
