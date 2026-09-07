using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;

namespace CodexSharp.Runtime;

public sealed record ModelEntry(string Id, string Provider);

public static class ModelCatalog
{
    public static IReadOnlyList<ModelEntry> List()
    {
        CodexPaths.EnsureLayout();
        var list = new List<ModelEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cfg = ConfigService.Load();
        void Add(string id, string provider)
        {
            var key = provider + "/" + id;
            if (seen.Add(key))
            {
                list.Add(new ModelEntry(id, provider));
            }
        }

        Add(cfg.Model, cfg.Provider.Id);
        foreach (var id in (string[])["gpt-4.1", "gpt-4.1-mini", "gpt-4.1-nano", "gpt-4o", "gpt-4o-mini", "o3", "o4-mini"])
        {
            Add(id, cfg.Provider.Id);
        }

        foreach (var local in LocalModelDiscover.Probe())
        {
            Add(local.Id, local.Provider);
        }
        var overlay = Path.Combine(CodexPaths.Home, "models.json");
        if (File.Exists(overlay))
        {
            try
            {
                var extra = JsonSerializer.Deserialize<ModelEntry[]>(File.ReadAllText(overlay), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (extra is not null)
                {
                    foreach (var entry in extra)
                    {
                        if (!string.IsNullOrWhiteSpace(entry.Id))
                        {
                            Add(entry.Id, string.IsNullOrWhiteSpace(entry.Provider) ? cfg.Provider.Id : entry.Provider);
                        }
                    }
                }
            }
            catch
            {
                // ignore malformed overlay
            }
        }

        if (File.Exists(CodexPaths.ConfigFile))
        {
            var table = Toml.ToModel(File.ReadAllText(CodexPaths.ConfigFile));
            if (table.TryGetValue("model_providers", out var providersObj) && providersObj is TomlTable providers)
            {
                foreach (var (id, value) in providers)
                {
                    if (value is TomlTable spec && spec.TryGetValue("models", out var models) && models is TomlTable mt)
                    {
                        foreach (var (modelId, _) in mt)
                        {
                            Add(modelId, id);
                        }
                    }
                    else
                    {
                        Add(cfg.Model, id);
                    }
                }
            }
        }

        return list;
    }

    public static ModelEntry Add(string id, string? provider = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id is required");
        CodexPaths.EnsureLayout();
        var path = Path.Combine(CodexPaths.Home, "models.json");
        var list = new List<ModelEntry>();
        if (File.Exists(path))
        {
            try
            {
                list = JsonSerializer.Deserialize<List<ModelEntry>>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            }
            catch { list = []; }
        }
        var entry = new ModelEntry(id.Trim(), string.IsNullOrWhiteSpace(provider) ? ConfigService.Load().Provider.Id : provider.Trim());
        list.RemoveAll(e => e.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase) && e.Provider.Equals(entry.Provider, StringComparison.OrdinalIgnoreCase));
        list.Add(entry);
        File.WriteAllText(path, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        return entry;
    }
}
