using System.Text.RegularExpressions;

namespace CodexSharp.Runtime;

public sealed record AssistantDirective(string Name, IReadOnlyDictionary<string, string> Attributes, string Raw);

/// Parses `::name{k="v"}` markers from vendor/codex tui assistant_directives.rs.
public static class AssistantDirectives
{
    public static IReadOnlyList<AssistantDirective> ParseAll(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("::", StringComparison.Ordinal))
        {
            return [];
        }

        var list = new List<AssistantDirective>();
        var i = 0;
        while (i < text.Length)
        {
            var start = text.IndexOf("::", i, StringComparison.Ordinal);
            if (start < 0) break;
            if (TryParse(text.AsSpan(start), out var dir, out var consumed) && dir is not null)
            {
                list.Add(dir);
                i = start + consumed;
            }
            else
            {
                i = start + 2;
            }
        }

        return list;
    }

    public static bool TryParse(ReadOnlySpan<char> source, out AssistantDirective? directive, out int consumed)
    {
        directive = null;
        consumed = 0;
        var colons = 0;
        while (colons < source.Length && source[colons] == ':') colons++;
        if (colons is < 1 or > 3) return false;
        var rest = source[colons..];
        var nameLen = 0;
        while (nameLen < rest.Length && (char.IsAsciiLetterOrDigit(rest[nameLen]) || rest[nameLen] is '-' or '_')) nameLen++;
        if (nameLen == 0 || nameLen >= rest.Length || rest[nameLen] != '{') return false;
        var name = rest[..nameLen].ToString();
        var body = rest[(nameLen + 1)..];
        var close = FindClose(body);
        if (close < 0) return false;
        var attrs = ParseAttrs(body[..close].ToString());
        consumed = colons + nameLen + 1 + close + 1;
        directive = new AssistantDirective(name, attrs, source[..consumed].ToString());
        return true;
    }

    private static int FindClose(ReadOnlySpan<char> body)
    {
        var quote = '\0';
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (quote != '\0')
            {
                if (c == '\\' && i + 1 < body.Length) { i++; continue; }
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '}') return i;
        }
        return -1;
    }

    private static Dictionary<string, string> ParseAttrs(string body)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < body.Length)
        {
            while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
            var keyStart = i;
            while (i < body.Length && (char.IsAsciiLetterOrDigit(body[i]) || body[i] is '_' or '-')) i++;
            if (i == keyStart) break;
            var key = body[keyStart..i];
            while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
            if (i >= body.Length || body[i] != '=') { map[key] = "true"; continue; }
            i++;
            while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
            if (i < body.Length && body[i] is '"' or '\'')
            {
                var q = body[i++];
                var val = new System.Text.StringBuilder();
                while (i < body.Length && body[i] != q)
                {
                    if (body[i] == '\\' && i + 1 < body.Length) { val.Append(body[++i]); i++; continue; }
                    val.Append(body[i++]);
                }
                if (i < body.Length) i++;
                map[key] = val.ToString();
            }
            else
            {
                var valStart = i;
                while (i < body.Length && !char.IsWhiteSpace(body[i]) && body[i] != ',') i++;
                map[key] = body[valStart..i];
            }
            while (i < body.Length && (char.IsWhiteSpace(body[i]) || body[i] == ',')) i++;
        }
        return map;
    }
}

public static class FileCitations
{
    private static readonly Regex MdLink = new(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);

    public static IReadOnlyList<MdFileCite> Extract(string? text)
    {
        var list = new List<MdFileCite>();
        if (string.IsNullOrEmpty(text)) return list;
        foreach (var dir in AssistantDirectives.ParseAll(text))
        {
            if (!dir.Name.Equals("codex-file-citation", StringComparison.OrdinalIgnoreCase)
                && !dir.Name.Equals("code-comment", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            dir.Attributes.TryGetValue("path", out var path);
            path ??= dir.Attributes.GetValueOrDefault("file");
            if (string.IsNullOrWhiteSpace(path)) continue;
            int? line = null;
            if (dir.Attributes.TryGetValue("line", out var ls) && int.TryParse(ls, out var ln)) line = ln;
            dir.Attributes.TryGetValue("body", out var body);
            list.Add(new MdFileCite(path, line, body ?? path));
        }

        foreach (Match m in MdLink.Matches(text))
        {
            var dest = m.Groups[2].Value.Trim();
            if (!LooksLocal(dest)) continue;
            var (path, line) = SplitLine(dest);
            list.Add(new MdFileCite(path, line, m.Groups[1].Value));
        }

        return list;
    }

    public static bool LooksLocal(string dest)
    {
        if (string.IsNullOrWhiteSpace(dest)) return false;
        if (dest.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || dest.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        if (dest.StartsWith("file://", StringComparison.OrdinalIgnoreCase) || dest.StartsWith("./") || dest.StartsWith("../") || dest.StartsWith("/")) return true;
        return dest.Contains('/') || dest.Contains('\\') || dest.Contains('.');
    }

    public static (string Path, int? Line) SplitLine(string dest)
    {
        dest = dest.Replace("file://", "", StringComparison.OrdinalIgnoreCase);
        var hash = dest.LastIndexOf("#L", StringComparison.Ordinal);
        if (hash >= 0 && int.TryParse(dest[(hash + 2)..].TrimEnd('C', 'c'), out var hl))
        {
            return (dest[..hash], hl);
        }
        var colon = dest.LastIndexOf(':');
        if (colon > 1 && int.TryParse(dest[(colon + 1)..], out var line))
        {
            return (dest[..colon], line);
        }
        return (dest, null);
    }
}
