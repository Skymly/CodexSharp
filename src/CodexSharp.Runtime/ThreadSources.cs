using System.Text.Json;

namespace CodexSharp.Runtime;

/// Persists `codex exec --thread-source` classification per thread.
public static class ThreadSources
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string FilePath => Path.Combine(CodexPaths.Home, "thread-sources.json");

    public static void Set(string threadId, string source)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        lock (Gate)
        {
            CodexPaths.EnsureLayout();
            var map = Load();
            map[threadId] = source.Trim();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(map, Json));
        }
    }

    public static string? Get(string threadId)
    {
        lock (Gate)
        {
            return Load().TryGetValue(threadId, out var source) ? source : null;
        }
    }

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath), Json)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
