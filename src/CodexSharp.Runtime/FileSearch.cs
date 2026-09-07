using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexSharp.Protocol;

namespace CodexSharp.Runtime;

/// Approximate port of vendor/codex/codex-rs/file-search: workspace file name + content search.
public static class FileSearch
{
    private static readonly string[] SkipDirs = [".git", "bin", "obj", "node_modules", ".codexsharp", "vendor"];

    public static ToolCallResult SearchNames(ToolCallRequest call, WorkspaceSandbox sandbox)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var query = doc.RootElement.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        var path = doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() ?? "." : ".";
        sandbox.EnsureReadable(path);
        var root = sandbox.Resolve(path);
        if (!Directory.Exists(root))
        {
            return new ToolCallResult(call.Id, call.Name, $"Directory not found: {path}", true);
        }

        var hits = new List<string>();
        foreach (var file in EnumerateFiles(root))
        {
            var rel = Path.GetRelativePath(sandbox.Cwd, file).Replace('\\', '/');
            if (rel.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(rel);
                if (hits.Count >= 50)
                {
                    break;
                }
            }
        }

        return new ToolCallResult(call.Id, call.Name, hits.Count == 0 ? "(no matches)" : string.Join('\n', hits), false);
    }

    public static IReadOnlyList<string> SuggestNames(string cwd, string query, int limit = 8)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
        {
            return [];
        }

        var hits = new List<string>();
        foreach (var file in EnumerateFiles(cwd))
        {
            var rel = Path.GetRelativePath(cwd, file).Replace('\\', '/');
            if (query.Length == 0 || rel.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(rel);
                if (hits.Count >= Math.Clamp(limit, 1, 40))
                {
                    break;
                }
            }
        }

        return hits;
    }

    public static ToolCallResult Grep(ToolCallRequest call, WorkspaceSandbox sandbox)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var pattern = doc.RootElement.TryGetProperty("pattern", out var pat) ? pat.GetString() ?? "" : "";
        var path = doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() ?? "." : ".";
        var glob = doc.RootElement.TryGetProperty("glob", out var g) ? g.GetString() : null;
        var max = doc.RootElement.TryGetProperty("max_matches", out var m) && m.TryGetInt32(out var mv) ? mv : 80;
        sandbox.EnsureReadable(path);
        var root = sandbox.Resolve(path);
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            return new ToolCallResult(call.Id, call.Name, $"Invalid pattern: {ex.Message}", true);
        }

        var output = new StringBuilder();
        var count = 0;
        foreach (var file in EnumerateFiles(root))
        {
            if (!string.IsNullOrEmpty(glob) && !MatchesGlob(Path.GetFileName(file), glob))
            {
                continue;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch
            {
                continue;
            }

            var rel = Path.GetRelativePath(sandbox.Cwd, file).Replace('\\', '/');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!regex.IsMatch(lines[i]))
                {
                    continue;
                }

                output.Append(rel).Append(':').Append(i + 1).Append(':').AppendLine(lines[i]);
                count++;
                if (count >= max)
                {
                    output.AppendLine($"… truncated at {max} matches");
                    return new ToolCallResult(call.Id, call.Name, output.ToString(), false);
                }
            }
        }

        return new ToolCallResult(call.Id, call.Name, count == 0 ? "(no matches)" : output.ToString(), false);
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (SkipDirs.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                stack.Push(child);
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }

    private static bool MatchesGlob(string name, string glob)
    {
        var regex = "^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
    }
}
