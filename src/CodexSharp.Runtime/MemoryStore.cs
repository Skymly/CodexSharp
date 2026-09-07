using System.Text.Json;

namespace CodexSharp.Runtime;

public static class MemoryStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string ModesPath => Path.Combine(CodexPaths.Home, "memory-modes.json");
    public static string MemoriesRoot => Path.Combine(CodexPaths.Home, "memories");

    public static void SetMode(string threadId, string mode)
    {
        if (string.IsNullOrWhiteSpace(threadId)) throw new ArgumentException("threadId is required");
        if (mode is not "enabled" and not "disabled") throw new ArgumentException("mode must be enabled or disabled");
        lock (Gate)
        {
            var map = Load();
            map[threadId] = mode;
            CodexPaths.EnsureLayout();
            File.WriteAllText(ModesPath, JsonSerializer.Serialize(map, Json));
        }
    }

    public static string ModeOf(string threadId)
    {
        lock (Gate)
        {
            return Load().TryGetValue(threadId, out var mode) ? mode : "enabled";
        }
    }

    public static IReadOnlyList<string> ListNotes()
    {
        lock (Gate)
        {
            if (!Directory.Exists(MemoriesRoot)) return Array.Empty<string>();
            return Directory.EnumerateFiles(MemoriesRoot, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(MemoriesRoot, p).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public static string WriteNote(string name, string text)
    {
        var relative = SanitizeName(name);
        lock (Gate)
        {
            CodexPaths.EnsureLayout();
            Directory.CreateDirectory(MemoriesRoot);
            var full = Path.GetFullPath(Path.Combine(MemoriesRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            var root = Path.GetFullPath(MemoriesRoot);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !full.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("invalid memory name");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text ?? "");
            return Path.GetRelativePath(MemoriesRoot, full).Replace(Path.DirectorySeparatorChar, '/');
        }
    }

    public static string ReadNote(string name)
    {
        var relative = SanitizeName(name);
        lock (Gate)
        {
            var full = Path.GetFullPath(Path.Combine(MemoriesRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            var root = Path.GetFullPath(MemoriesRoot);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !full.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("invalid memory name");
            }
            if (!File.Exists(full)) throw new FileNotFoundException("memory not found", relative);
            return File.ReadAllText(full);
        }
    }

    public static void Reset()
    {
        lock (Gate)
        {
            CodexPaths.EnsureLayout();
            if (Directory.Exists(MemoriesRoot))
            {
                Directory.Delete(MemoriesRoot, recursive: true);
            }
            Directory.CreateDirectory(MemoriesRoot);
        }
    }

    private static string SanitizeName(string name)
    {
        var value = (name ?? "").Replace(Path.DirectorySeparatorChar, '/').Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("name is required");
        if (value.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(value))
        {
            throw new ArgumentException("invalid memory name");
        }
        if (!value.Contains('.')) value += ".md";
        return value;
    }

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(ModesPath)) return new(StringComparer.Ordinal);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ModesPath), Json)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
