using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexSharp.Runtime;

public static class ImageMessageContent
{
    public const string Marker = "Please inspect this image with view_image: ";
    private static readonly Regex MarkerLine = new(@"Please inspect this image with view_image:\s*(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private const int MaxBytes = 8 * 1024 * 1024;

    public static JsonNode ChatContent(string text)
    {
        if (!TryCollect(text, out var rest, out var dataUrls) || dataUrls.Count == 0)
        {
            return JsonValue.Create(text ?? "")!;
        }

        var arr = new JsonArray();
        if (!string.IsNullOrWhiteSpace(rest))
        {
            arr.Add(new JsonObject { ["type"] = "text", ["text"] = rest.Trim() });
        }

        foreach (var url in dataUrls)
        {
            arr.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = url },
            });
        }

        return arr;
    }

    public static JsonNode ResponsesContent(string text)
    {
        if (!TryCollect(text, out var rest, out var dataUrls) || dataUrls.Count == 0)
        {
            return JsonValue.Create(text ?? "")!;
        }

        var arr = new JsonArray();
        if (!string.IsNullOrWhiteSpace(rest))
        {
            arr.Add(new JsonObject { ["type"] = "input_text", ["text"] = rest.Trim() });
        }

        foreach (var url in dataUrls)
        {
            arr.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = url });
        }

        return arr;
    }

    public static bool TryCollect(string text, out string remainder, out List<string> dataUrls)
    {
        remainder = text;
        dataUrls = [];
        if (string.IsNullOrEmpty(text) || !text.Contains(Marker, StringComparison.Ordinal))
        {
            return false;
        }

        var paths = new List<string>();
        remainder = MarkerLine.Replace(text, match =>
        {
            paths.Add(match.Groups[1].Value.Trim());
            return "";
        });

        foreach (var path in paths)
        {
            if (TryDataUrl(path, out var url))
            {
                dataUrls.Add(url);
            }
        }

        return dataUrls.Count > 0;
    }

    private static readonly Regex MarkdownImage = new(@"!\[[^\]]*\]\(([^)]+)\)", RegexOptions.Compiled);

    public static IReadOnlyList<string> ExtractLocalPaths(string? text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return list;
        }

        void Add(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            var path = raw.Trim().Trim('"');
            if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                path = path["file:".Length..];
            }

            try
            {
                path = Path.GetFullPath(path);
            }
            catch
            {
                return;
            }

            if (!IsImagePath(path) || !File.Exists(path) || !seen.Add(path))
            {
                return;
            }

            list.Add(path);
        }

        foreach (Match match in MarkerLine.Matches(text))
        {
            Add(match.Groups[1].Value);
        }

        foreach (Match match in MarkdownImage.Matches(text))
        {
            Add(match.Groups[1].Value);
        }

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("path=", StringComparison.OrdinalIgnoreCase))
            {
                Add(trimmed["path=".Length..]);
            }
            else
            {
                Add(trimmed);
            }
        }

        return list;
    }

    public static bool IsImagePath(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";
    }

    public static bool TryDataUrl(string path, out string url)
    {
        url = "";
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaxBytes)
            {
                return false;
            }

            var mime = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "application/octet-stream",
            };
            url = $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
            return true;
        }
        catch
        {
            return false;
        }
    }
}