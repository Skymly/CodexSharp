using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexSharp.Runtime;

public enum ActivityKind
{
    Running,
    WaitingApproval,
    Unread,
}

public sealed record ActivityItem(
    string Id,
    string ThreadId,
    string Title,
    ActivityKind Kind,
    DateTimeOffset At,
    bool Read = false);

/// Local Activity inbox (NAV-03). No cloud Scheduled rows.
public static class ActivityInbox
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = true,
    };

    public static string FilePath => Path.Combine(CodexPaths.Home, "activity.json");

    public static IReadOnlyList<ActivityItem> List(ActivityKind? kind = null)
    {
        lock (Gate)
        {
            var items = Load()
                .Where(i => i.Kind != ActivityKind.Unread || !i.Read)
                .OrderByDescending(i => i.At)
                .ToList();
            if (kind is { } filter)
            {
                items = items.Where(i => i.Kind == filter).ToList();
            }

            return items;
        }
    }

    public static bool HasScheduledFilter => false;

    public static ActivityItem RecordRunning(string threadId, string? title)
    {
        lock (Gate)
        {
            var items = Load();
            items.RemoveAll(i => i.ThreadId == threadId && i.Kind == ActivityKind.Running);
            var row = NewItem(threadId, title, ActivityKind.Running);
            items.Add(row);
            Save(items);
            return row;
        }
    }

    public static ActivityItem RecordWaitingApproval(string threadId, string? title)
    {
        lock (Gate)
        {
            var items = Load();
            items.RemoveAll(i => i.ThreadId == threadId && i.Kind == ActivityKind.WaitingApproval);
            var row = NewItem(threadId, title, ActivityKind.WaitingApproval);
            items.Add(row);
            Save(items);
            return row;
        }
    }

    public static ActivityItem RecordCompleted(string threadId, string? title)
    {
        lock (Gate)
        {
            var items = Load();
            items.RemoveAll(i => i.ThreadId == threadId && i.Kind is ActivityKind.Running or ActivityKind.WaitingApproval);
            var row = NewItem(threadId, title, ActivityKind.Unread);
            items.Add(row);
            Save(items);
            return row;
        }
    }

    public static void MarkThreadRead(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }

        lock (Gate)
        {
            var items = Load();
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].ThreadId == threadId && items[i].Kind == ActivityKind.Unread)
                {
                    items[i] = items[i] with { Read = true };
                }
            }

            items.RemoveAll(i => i.ThreadId == threadId && i.Kind == ActivityKind.WaitingApproval);
            Save(items);
        }
    }

    public static void ClearRunning(string threadId)
    {
        lock (Gate)
        {
            var items = Load();
            items.RemoveAll(i => i.ThreadId == threadId && i.Kind == ActivityKind.Running);
            Save(items);
        }
    }

    private static ActivityItem NewItem(string threadId, string? title, ActivityKind kind) =>
        new(
            "act_" + Guid.NewGuid().ToString("N")[..12],
            threadId,
            string.IsNullOrWhiteSpace(title) ? threadId : title.Trim(),
            kind,
            DateTimeOffset.UtcNow);

    private static List<ActivityItem> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return [];
            }

            var parsed = JsonSerializer.Deserialize<ActivityFile>(File.ReadAllText(FilePath), JsonOptions);
            var items = parsed?.Items ?? [];
            if (items.Count > 200)
            {
                items = items.OrderByDescending(i => i.At).Take(200).ToList();
            }

            return items;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static void Save(List<ActivityItem> items)
    {
        CodexPaths.EnsureLayout();
        var trimmed = items.OrderByDescending(i => i.At).Take(200).ToList();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(new ActivityFile(trimmed), JsonOptions));
    }

    private sealed record ActivityFile(List<ActivityItem> Items);
}
