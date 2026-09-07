using System.Text.Json;
using CodexSharp.Protocol;

namespace CodexSharp.Runtime;

public sealed record PluginShareInfo(string RemotePluginId, string Name, string PluginPath, string Discoverability);

/// Local plugin shares. CodexSharp does not publish to OpenAI plugin service.
public static class PluginShares
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static string FilePath => Path.Combine(CodexPaths.Home, "plugin-shares.json");

    public static PluginShareInfo Save(string pluginPath, string? discoverability = null, string? remotePluginId = null)
    {
        if (string.IsNullOrWhiteSpace(pluginPath) || !Directory.Exists(pluginPath))
        {
            throw new DirectoryNotFoundException("pluginPath must be an existing directory");
        }

        var full = Path.GetFullPath(pluginPath);
        lock (Gate)
        {
            var list = Load();
            var id = string.IsNullOrWhiteSpace(remotePluginId) ? Ids.newId("pshare") : remotePluginId.Trim();
            list.RemoveAll(s => s.RemotePluginId == id || string.Equals(s.PluginPath, full, StringComparison.OrdinalIgnoreCase));
            var rec = new PluginShareInfo(id, Path.GetFileName(full), full, string.IsNullOrWhiteSpace(discoverability) ? "PRIVATE" : discoverability);
            list.Add(rec);
            Persist(list);
            return rec;
        }
    }

    public static IReadOnlyList<PluginShareInfo> List()
    {
        lock (Gate) { return Load(); }
    }

    public static bool Delete(string remotePluginId)
    {
        lock (Gate)
        {
            var list = Load();
            var n = list.RemoveAll(s => s.RemotePluginId == remotePluginId);
            if (n > 0) Persist(list);
            return n > 0;
        }
    }

    public static PluginInfo Checkout(string remotePluginId)
    {
        PluginShareInfo? rec;
        lock (Gate)
        {
            rec = Load().FirstOrDefault(s => s.RemotePluginId == remotePluginId);
        }

        if (rec is null)
        {
            throw new InvalidOperationException("share not found: " + remotePluginId);
        }

        return PluginCatalog.Install(rec.Name, rec.PluginPath);
    }

    public static PluginShareInfo? UpdateTargets(string remotePluginId, string? discoverability)
    {
        lock (Gate)
        {
            var list = Load();
            var idx = list.FindIndex(s => s.RemotePluginId == remotePluginId);
            if (idx < 0)
            {
                return null;
            }

            var cur = list[idx];
            list[idx] = cur with { Discoverability = discoverability ?? cur.Discoverability };
            Persist(list);
            return list[idx];
        }
    }

    private static List<PluginShareInfo> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            return JsonSerializer.Deserialize<List<PluginShareInfo>>(File.ReadAllText(FilePath), Json) ?? [];
        }
        catch { return []; }
    }

    private static void Persist(List<PluginShareInfo> list)
    {
        CodexPaths.EnsureLayout();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(list, Json));
    }
}
