using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexSharp.Protocol;

namespace CodexSharp.Runtime;

public sealed record ChatgptDeviceCode(
    string LoginId,
    string VerificationUrl,
    string UserCode,
    string DeviceAuthId,
    int IntervalSeconds);

/// ChatGPT device-code login. Client id is the public Codex OAuth app from
/// vendor/codex/codex-rs/login/src/auth/manager.rs — not a fabricated id.
public static class ChatgptDeviceAuth
{
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    public const string DefaultIssuer = "https://auth.openai.com";

    /// Tests inject a handler so protocol tests never touch the live issuer.
    public static HttpMessageHandler? TestHandler { get; set; }

    public static string Issuer =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEXSHARP_CHATGPT_ISSUER"))
            ? DefaultIssuer
            : Environment.GetEnvironmentVariable("CODEXSHARP_CHATGPT_ISSUER")!.TrimEnd('/');

    public static async Task<ChatgptDeviceCode> RequestAsync(CancellationToken ct = default)
    {
        using var http = CreateClient();
        var url = Issuer.TrimEnd('/') + "/api/accounts/deviceauth/usercode";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { client_id = ClientId }), Encoding.UTF8, "application/json"),
        };
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                resp.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "device code login is not enabled for this Codex server"
                    : "device code request failed: " + (int)resp.StatusCode);
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var root = doc.RootElement;
        var userCode = Str(root, "user_code") ?? Str(root, "userCode") ?? "";
        var deviceAuthId = Str(root, "device_auth_id") ?? Str(root, "deviceAuthId") ?? "";
        if (string.IsNullOrWhiteSpace(userCode) || string.IsNullOrWhiteSpace(deviceAuthId))
        {
            throw new InvalidOperationException("device code response missing user_code");
        }

        return new ChatgptDeviceCode(
            Ids.newId("login"),
            Issuer.TrimEnd('/') + "/codex/device",
            userCode,
            deviceAuthId,
            Int(root, "interval"));
    }

    public static async Task CompleteAsync(ChatgptDeviceCode code, CancellationToken ct = default)
    {
        using var http = CreateClient();
        var tokenUrl = Issuer.TrimEnd('/') + "/api/accounts/deviceauth/token";
        var payload = JsonSerializer.Serialize(new { device_auth_id = code.DeviceAuthId, user_code = code.UserCode });
        while (!ct.IsCancellationRequested)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            using var resp = await http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var authCode = Str(doc.RootElement, "authorization_code") ?? Str(doc.RootElement, "authorizationCode") ?? "";
                var verifier = Str(doc.RootElement, "code_verifier") ?? Str(doc.RootElement, "codeVerifier") ?? "";
                if (string.IsNullOrWhiteSpace(authCode))
                {
                    throw new InvalidOperationException("device auth token response missing authorization_code");
                }

                await ExchangeCodeAsync(authCode, verifier, Issuer.TrimEnd('/' ) + "/deviceauth/callback", ct);
                return;
            }

            if ((int)resp.StatusCode is 403 or 404)
            {
                var delay = code.IntervalSeconds <= 0 ? TimeSpan.FromMilliseconds(5) : TimeSpan.FromSeconds(code.IntervalSeconds);
                await Task.Delay(delay, ct);
                continue;
            }

            throw new InvalidOperationException("device auth failed: " + (int)resp.StatusCode);
        }

        ct.ThrowIfCancellationRequested();
    }

    public static async Task ExchangeCodeAsync(string authorizationCode, string verifier, string redirectUri, CancellationToken ct = default)
    {
        using var http = CreateClient();
        var issuer = Issuer.TrimEnd('/');
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = authorizationCode,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = verifier,
        });
        using var resp = await http.PostAsync(issuer + "/oauth/token", form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("token endpoint returned " + (int)resp.StatusCode);
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var access = Str(doc.RootElement, "access_token") ?? "";
        var refresh = Str(doc.RootElement, "refresh_token") ?? "";
        var idToken = Str(doc.RootElement, "id_token") ?? "";
        if (string.IsNullOrWhiteSpace(access))
        {
            throw new InvalidOperationException("token endpoint missing access_token");
        }

        AuthService.SaveChatgptTokens(access, refresh, idToken);
    }

    public static async Task RefreshAsync(CancellationToken ct = default)
    {
        var tokens = AuthService.ReadTokens();
        if (string.IsNullOrWhiteSpace(tokens.Refresh))
            throw new InvalidOperationException("no ChatGPT refresh token");
        using var http = CreateClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.Refresh,
            ["client_id"] = ClientId,
        });
        using var resp = await http.PostAsync(Issuer.TrimEnd('/' ) + "/oauth/token", form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException("token refresh returned " + (int)resp.StatusCode);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var access = doc.RootElement.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
        var nextRefresh = doc.RootElement.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? tokens.Refresh : tokens.Refresh;
        var idToken = doc.RootElement.TryGetProperty("id_token", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(access))
            throw new InvalidOperationException("token refresh missing access_token");
        AuthService.SaveChatgptTokens(access, nextRefresh!, idToken, tokens.AccountId);
    }

    public static HttpClient CreateClient()
    {
        var http = TestHandler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(20) }
            : new HttpClient(TestHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int Int(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v))
        {
            return 0;
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
        {
            return n;
        }

        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out n) ? n : 0;
    }
}
