using System.Net;
using System.Security.Cryptography;
using System.Text;
using CodexSharp.Protocol;

namespace CodexSharp.Runtime;

public sealed class ChatgptBrowserLogin : IDisposable
{
    public required string LoginId { get; init; }
    public required string AuthUrl { get; init; }
    public required int Port { get; init; }
    public required string RedirectUri { get; init; }
    public required string State { get; init; }
    public required string Verifier { get; init; }
    public required HttpListener Listener { get; init; }

    public void Dispose()
    {
        try { Listener.Stop(); } catch { }
        try { Listener.Close(); } catch { }
    }
}

public static class ChatgptBrowserAuth
{
    public static int[] Ports { get; set; } = [1455, 1457];

    public static ChatgptBrowserLogin Start()
    {
        HttpListener? listener = null;
        var port = 0;
        foreach (var candidate in Ports)
        {
            var http = new HttpListener();
            http.Prefixes.Add("http://127.0.0.1:" + candidate + "/");
            try
            {
                http.Start();
                listener = http;
                port = candidate;
                break;
            }
            catch
            {
                http.Close();
            }
        }

        if (listener is null)
        {
            throw new InvalidOperationException("Could not bind ChatGPT login callback on ports " + string.Join("/", Ports));
        }

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var redirect = "http://127.0.0.1:" + port + "/auth/callback";
        var issuer = ChatgptDeviceAuth.Issuer.TrimEnd('/');
        var query = string.Join("&", new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = ChatgptDeviceAuth.ClientId,
            ["redirect_uri"] = redirect,
            ["scope"] = "openid profile email offline_access api.connectors.read api.connectors.invoke",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["state"] = state,
            ["originator"] = "codexsharp",
        }.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));

        return new ChatgptBrowserLogin
        {
            LoginId = Ids.newId("login"),
            AuthUrl = issuer + "/oauth/authorize?" + query,
            Port = port,
            RedirectUri = redirect,
            State = state,
            Verifier = verifier,
            Listener = listener,
        };
    }

    public static async Task WaitAsync(ChatgptBrowserLogin login, CancellationToken ct = default)
    {
        using var reg = ct.Register(() =>
        {
            try { login.Listener.Stop(); } catch { }
        });

        HttpListenerContext ctx;
        try
        {
            ctx = await login.Listener.GetContextAsync();
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        var code = ctx.Request.QueryString["code"];
        var state = ctx.Request.QueryString["state"];
        var error = ctx.Request.QueryString["error"];
        var ok = string.Equals(state, login.State, StringComparison.Ordinal) && string.IsNullOrEmpty(error) && !string.IsNullOrWhiteSpace(code);
        var html = ok
            ? "<html><body><h1>CodexSharp signed in</h1><p>You can close this tab.</p></body></html>"
            : "<html><body><h1>Sign-in failed</h1></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.StatusCode = ok ? 200 : 400;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, ct);
        ctx.Response.OutputStream.Close();
        login.Listener.Stop();

        if (!ok)
        {
            throw new InvalidOperationException(string.IsNullOrEmpty(error) ? "OAuth callback missing code or state" : error);
        }

        await ChatgptDeviceAuth.ExchangeCodeAsync(code!, login.Verifier, login.RedirectUri, ct);
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
