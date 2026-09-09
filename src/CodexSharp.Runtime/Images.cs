using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexSharp.Protocol;

namespace CodexSharp.Runtime;

public sealed record GeneratedImage(byte[] PngBytes);

public interface IImagesClient
{
    Task<GeneratedImage> GenerateAsync(string prompt, CancellationToken ct);
}

public static class ImageGeneration
{
    public const string NotConfiguredMessage =
        "notConfigured  Image generation needs an API key in ~/.codexsharp/auth.json or OPENAI_API_KEY. This is not ChatGPT quota.";

    public static bool IsConfigured(CodexConfig cfg) =>
        !string.IsNullOrWhiteSpace(cfg.Provider.ApiKey);
}

public sealed class HttpImagesClient : IImagesClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly CodexConfig _config;
    private readonly HttpClient _http;

    public HttpImagesClient(CodexConfig config, HttpClient? http = null)
    {
        _config = config;
        _http = http ?? new HttpClient();
    }

    public async Task<GeneratedImage> GenerateAsync(string prompt, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_config.Provider.BaseUrl)
            ? "https://api.openai.com/v1"
            : _config.Provider.BaseUrl.TrimEnd('/');
        var url = baseUrl + "/images/generations";
        var payload = JsonSerializer.Serialize(new
        {
            model = "gpt-image-1",
            prompt,
            n = 1,
            size = "1024x1024",
        }, Json);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.Provider.ApiKey);
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Images API failed ({(int)resp.StatusCode}): {TrimBody(body)}");
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Images API returned no image data.");
        }

        var first = data[0];
        if (!first.TryGetProperty("b64_json", out var b64) || b64.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(b64.GetString()))
        {
            throw new InvalidOperationException("Images API did not return b64_json. This client does not follow ChatGPT credits URLs.");
        }

        return new GeneratedImage(Convert.FromBase64String(b64.GetString()!));
    }

    private static string TrimBody(string body) =>
        body.Length <= 300 ? body : body[..300];
}
