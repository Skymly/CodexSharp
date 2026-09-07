using System.Text.Json;

namespace CodexSharp.Runtime;

public sealed record WorktreeBinding(string ThreadId, string Root, string Cwd, string SourceRoot, string? Branch);

public static class WorktreeBindings
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static string FilePath => Path.Combine(CodexPaths.Home, "worktree-bindings.json");

    public static void Bind(string threadId, ManagedWorktree tree)
    {
        if (string.IsNullOrWhiteSpace(threadId) || tree is null)
        {
            return;
        }

        lock (Gate)
        {
            var store = Load();
            store.RemoveAll(b => b.ThreadId == threadId);
            store.Add(new WorktreeBinding(threadId, tree.Root, tree.Cwd, tree.SourceRoot, tree.Branch));
            Save(store);
        }
    }

    public static WorktreeBinding? Find(string threadId)
    {
        lock (Gate)
        {
            return Load().FirstOrDefault(b => b.ThreadId == threadId);
        }
    }

    public static bool UnbindAndRemove(string threadId)
    {
        lock (Gate)
        {
            var store = Load();
            var idx = store.FindIndex(b => b.ThreadId == threadId);
            if (idx < 0)
            {
                return false;
            }

            var binding = store[idx];
            store.RemoveAt(idx);
            Save(store);
            return WorktreeSession.Remove(new ManagedWorktree(binding.Root, binding.Cwd, binding.SourceRoot, null, binding.Branch));
        }
    }

    private static List<WorktreeBinding> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<WorktreeBinding>>(File.ReadAllText(FilePath), Json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void Save(List<WorktreeBinding> store)
    {
        CodexPaths.EnsureLayout();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(store, Json));
    }
}
