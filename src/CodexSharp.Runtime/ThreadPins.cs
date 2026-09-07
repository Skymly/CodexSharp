using System.Text.Json;

namespace CodexSharp.Runtime;

public static class ThreadPins
{
    private static string FilePath => Path.Combine(CodexPaths.Home, "pinned.json");

    public static bool IsPinned(string id) => Load().Contains(id);

    public static void Set(string id, bool pinned)
    {
        var set = Load();
        if (pinned) set.Add(id); else set.Remove(id);
        CodexPaths.EnsureLayout();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(set.ToArray()));
    }

    private static HashSet<string> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return [];
            }

            var ids = JsonSerializer.Deserialize<string[]>(File.ReadAllText(FilePath)) ?? [];
            return new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return [];
        }
    }
}
