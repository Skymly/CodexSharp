using System.Text.Json;
using System.Text.RegularExpressions;
using CodexSharp.Protocol;

namespace CodexSharp.Runtime;

/// Local feedback snapshots. CodexSharp does not upload reports to OpenAI.
public static class FeedbackStore
{
    public static string Dir => Path.Combine(CodexPaths.Home, "feedback");

    public static string Save(string classification, string? reason = null, string? threadId = null, bool includeLogs = false)
    {
        if (string.IsNullOrWhiteSpace(classification))
        {
            throw new ArgumentException("classification is required", nameof(classification));
        }

        CodexPaths.EnsureLayout();
        Directory.CreateDirectory(Dir);
        var id = string.IsNullOrWhiteSpace(threadId) ? Ids.newId("fb") : threadId.Trim();
        var config = "";
        if (File.Exists(CodexPaths.ConfigFile))
        {
            config = File.ReadAllText(CodexPaths.ConfigFile);
            config = Regex.Replace(config, @"(?im)(api[_-]?key)\s*=\s*""[^""]*""", "$1 = \"***\"");
        }

        var logs = new List<string>();
        if (includeLogs)
        {
            if (Directory.Exists(CodexPaths.LogsDir))
            {
                foreach (var file in Directory.GetFiles(CodexPaths.LogsDir).Take(8))
                {
                    try { logs.Add(Path.GetFileName(file) + "\n" + File.ReadAllText(file)); }
                    catch { /* skip unreadable */ }
                }
            }
            if (Directory.Exists(CodexPaths.SessionsDir))
            {
                foreach (var file in Directory.GetFiles(CodexPaths.SessionsDir, "*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc).Take(3))
                {
                    try
                    {
                        var text = File.ReadAllText(file);
                        if (text.Length > 16_000) text = text[..16_000] + "\n...";
                        logs.Add(Path.GetFileName(file) + "\n" + text);
                    }
                    catch { /* skip */ }
                }
            }
        }

        var payload = new
        {
            classification,
            reason,
            threadId = id,
            createdAt = DateTimeOffset.UtcNow,
            config,
            logs,
        };
        File.WriteAllText(Path.Combine(Dir, id + ".json"), JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return id;
    }
}
