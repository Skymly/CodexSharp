using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexSharp.Runtime;

public static class ImageUrlCache
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Regex MarkdownImage = new(@"!\[[^\]]*\]\((https?://[^)\s]+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BareUrl = new(@"https?://[^\s)]+\.(?:png|jpe?g|gif|webp)(?:\?[^\s)]*)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private const int MaxBytes = 8 * 1024 * 1024;

    public static string CacheDir => Path.Combine(Path.GetTempPath(), "codexsharp-img-cache");

    public static IReadOnlyList<string> ExtractRemoteUrls(string? text)
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

            var url = raw.Trim().TrimEnd('.', ',', ';');
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return;
            }

            if (uri.Scheme is not ("http" or "https"))
            {
                return;
            }

            if (seen.Add(uri.AbsoluteUri))
            {
                list.Add(uri.AbsoluteUri);
            }
        }

        foreach (Match match in MarkdownImage.Matches(text))
        {
            Add(match.Groups[1].Value);
        }

        foreach (Match match in BareUrl.Matches(text))
        {
            Add(match.Value);
        }

        return list;
    }

    public static string PathFor(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24].ToLowerInvariant();
        var ext = ".img";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var candidate = Path.GetExtension(uri.AbsolutePath);
            if (candidate is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp")
            {
                ext = candidate.ToLowerInvariant();
            }
        }

        return Path.Combine(CacheDir, hash + ext);
    }

    public static string? TryGetCached(string url)
    {
        var path = PathFor(url);
        return File.Exists(path) ? path : null;
    }

    public static async Task<string?> DownloadAsync(string url, HttpClient? http = null, CancellationToken ct = default)
    {
        if (ExtractRemoteUrls(url).Count == 0 && !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return TryGetCached(url);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var path = PathFor(uri.AbsoluteUri);
        if (File.Exists(path))
        {
            return path;
        }

        try
        {
            using var resp = await (http ?? Http).GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            var media = resp.Content.Headers.ContentType?.MediaType ?? "";
            var looksImage = media.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";
            if (!looksImage)
            {
                return null;
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length is <= 0 or > MaxBytes)
            {
                return null;
            }

            Directory.CreateDirectory(CacheDir);
            await File.WriteAllBytesAsync(path, bytes, ct);
            return path;
        }
        catch
        {
            return null;
        }
    }
}