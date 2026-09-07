using System.Text.Json;
using CodexSharp.Protocol;

namespace CodexSharp.Runtime;

public sealed record QueuedItem(string Id, string Text);

/// Persisted per-thread prompt queue (official `codex queue` / thread/queue/*).
public static class ThreadQueueStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string Dir => Path.Combine(CodexPaths.Home, "queues");
    private static string PathFor(string threadId) => Path.Combine(Dir, threadId + ".json");

    public static IReadOnlyList<QueuedItem> List(string threadId)
    {
        lock (Gate)
        {
            return Load(threadId);
        }
    }

    public static QueuedItem Add(string threadId, string text)
    {
        if (string.IsNullOrWhiteSpace(threadId)) throw new ArgumentException("threadId is required");
        lock (Gate)
        {
            var list = Load(threadId);
            var item = new QueuedItem(Ids.newId("q"), text ?? "");
            list.Add(item);
            Save(threadId, list);
            return item;
        }
    }

    public static bool Delete(string threadId, string id)
    {
        lock (Gate)
        {
            var list = Load(threadId);
            var n = list.RemoveAll(x => x.Id == id);
            if (n > 0) Save(threadId, list);
            return n > 0;
        }
    }

    public static QueuedItem? Update(string threadId, string id, string text)
    {
        lock (Gate)
        {
            var list = Load(threadId);
            var idx = list.FindIndex(x => x.Id == id);
            if (idx < 0) return null;
            var item = new QueuedItem(id, text ?? "");
            list[idx] = item;
            Save(threadId, list);
            return item;
        }
    }

    public static bool Reorder(string threadId, IReadOnlyList<string> ids)
    {
        lock (Gate)
        {
            var list = Load(threadId);
            var map = list.ToDictionary(x => x.Id);
            var next = new List<QueuedItem>();
            foreach (var qid in ids)
            {
                if (map.Remove(qid, out var item)) next.Add(item);
            }
            next.AddRange(map.Values);
            Save(threadId, next);
            return true;
        }
    }

    public static QueuedItem? Dequeue(string threadId)
    {
        lock (Gate)
        {
            var list = Load(threadId);
            if (list.Count == 0) return null;
            var item = list[0];
            list.RemoveAt(0);
            Save(threadId, list);
            return item;
        }
    }

    public static bool Promote(string threadId, string id)
    {
        lock (Gate)
        {
            var list = Load(threadId);
            var idx = list.FindIndex(x => x.Id == id);
            if (idx <= 0) return idx == 0;
            var item = list[idx];
            list.RemoveAt(idx);
            list.Insert(0, item);
            Save(threadId, list);
            return true;
        }
    }

    private static List<QueuedItem> Load(string threadId)
    {
        try
        {
            var path = PathFor(threadId);
            if (!File.Exists(path)) return [];
            return JsonSerializer.Deserialize<List<QueuedItem>>(File.ReadAllText(path), Json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void Save(string threadId, List<QueuedItem> list)
    {
        CodexPaths.EnsureLayout();
        Directory.CreateDirectory(Dir);
        var path = PathFor(threadId);
        if (list.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        File.WriteAllText(path, JsonSerializer.Serialize(list, Json));
    }
}
