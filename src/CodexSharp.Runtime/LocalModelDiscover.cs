using System.Net.Http;
using System.Text.Json;

namespace CodexSharp.Runtime;

/// Local OSS model discovery from vendor/codex ollama + lmstudio crates.
public static class LocalModelDiscover
{
    public static HttpMessageHandler? TestHandler { get; set; }

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(400);
    private static readonly object Gate = new();
    private static DateTimeOffset _cachedAt;
    private static IReadOnlyList<ModelEntry> _cached = [];

    public static IReadOnlyList<ModelEntry> Probe(bool force = false)
    {
        lock (Gate)
        {
            if (!force && TestHandler is null && DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromSeconds(30))
            {
                return _cached;
            }

            var list = new List<ModelEntry>();
            list.AddRange(ProbeJson("http://127.0.0.1:11434/api/tags", "ollama", "models", "name"));
            list.AddRange(ProbeJson("http://127.0.0.1:1234/v1/models", "lmstudio", "data", "id"));
            _cached = list;
            _cachedAt = DateTimeOffset.UtcNow;
            return list;
        }
    }

    private static IReadOnlyList<ModelEntry> ProbeJson(string url, string provider, string arrayName, string idName)
    {
        try
        {
            using var http = TestHandler is null ? new HttpClient { Timeout = Timeout } : new HttpClient(TestHandler, disposeHandler: false) { Timeout = Timeout };
            using var resp = http.GetAsync(url).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                return [];
            }

            using var doc = JsonDocument.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            if (!doc.RootElement.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var list = new List<ModelEntry>();
            foreach (var item in arr.EnumerateArray())
            {
                var id = item.ValueKind == JsonValueKind.Object && item.TryGetProperty(idName, out var n) ? n.GetString() : null;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    list.Add(new ModelEntry(id, provider));
                }
            }

            return list;
        }
        catch
        {
            return [];
        }
    }
}
