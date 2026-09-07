using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using CodexSharp.AppServer;
using CodexSharp.Client;
using CodexSharp.Core;
using CodexSharp.Protocol;
using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public class ConfigTests
{
    [Fact]
    public void Loads_default_config_into_home()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var cfg = ConfigService.Load(Path.GetTempPath());
            Assert.Equal(home, cfg.Home);
            Assert.True(File.Exists(Path.Combine(home, "config.toml")));
            Assert.False(string.IsNullOrWhiteSpace(cfg.Model));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ConfigProfileTests
{
    [Fact]
    public void Profile_overrides_model_and_sandbox()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "config.toml"), """
model = "base-model"
sandbox_mode = "read-only"

[profiles.full_auto]
model = "profile-model"
sandbox_mode = "workspace-write"
approval_policy = "never"
""");
            var cfg = ConfigService.Load(Path.GetTempPath(), profile: "full_auto");
            Assert.Equal("profile-model", cfg.Model);
            Assert.Equal("workspace-write", cfg.SandboxMode);
            Assert.Equal("never", cfg.ApprovalPolicy);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ConfigWriteTests
{
    [Fact]
    public void Writes_and_reloads_model_key()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "config.toml"), "model = \"old\"\n");
            ConfigService.WriteUserKeys(new Dictionary<string, string> { ["model"] = "new-model" });
            var cfg = ConfigService.Load(Path.GetTempPath());
            Assert.Equal("new-model", cfg.Model);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ConfigValueWriteTests
{
    [Fact]
    public async Task Writes_single_key_path()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var client = hosted.Client;
            await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
            await client.NotifyAsync("initialized");
            await client.CallAsync("config/value/write", new { keyPath = "model", value = "gpt-test-value-write" });
            var read = await client.CallAsync("config/read");
            Assert.Equal("gpt-test-value-write", read.GetProperty("model").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class FeatureFlagsTests
{
    [Fact]
    public void Enable_and_list_feature()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "config.toml"), "model = \"x\"\n");
            FeatureFlags.Set("multi_agent", true);
            Assert.True(FeatureFlags.IsEnabled("multi_agent"));
            Assert.Contains(FeatureFlags.List(), kv => kv.Key == "multi_agent" && kv.Value);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class AuthServiceTests
{
    [Fact]
    public void Saves_api_key_to_auth_json()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            AuthService.SaveApiKey("sk-test-123");
            var json = File.ReadAllText(Path.Combine(home, "auth.json"));
            Assert.Contains("sk-test-123", json);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class AccountProtocolTests
{
    [Fact]
    public async Task Login_api_key_then_account_read()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var client = hosted.Client;
            await client.CallAsync("initialize", new { clientInfo = new { name = "test", title = "t", version = "0" } });
            await client.NotifyAsync("initialized");
            await client.CallAsync("account/login/start", new { type = "apiKey", apiKey = "sk-test-account" });
            var account = await client.CallAsync("account/read");
            Assert.Equal("apiKey", account.GetProperty("account").GetProperty("type").GetString());
            var auth = await client.CallAsync("getAuthStatus");
            Assert.Equal("apiKey", auth.GetProperty("authMethod").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class BedrockAndAccountHonestyTests
{
    [Fact]
    public async Task Discover_reads_profiles_without_secrets_and_setup_errors()
    {
        var creds = Path.Combine(Path.GetTempPath(), "codexsharp-aws-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(creds, """
            [default]
            aws_access_key_id = AKIATESTNOTASECRET
            aws_secret_access_key = wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY

            [codex-bedrock]
            aws_access_key_id = AKIATEST2
            aws_secret_access_key = secret
            """);
        var prevCreds = Environment.GetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE");
        var prevCfg = Environment.GetEnvironmentVariable("AWS_CONFIG_FILE");
        var prevKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
        var prevRegion = Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION");
        Environment.SetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE", creds);
        Environment.SetEnvironmentVariable("AWS_CONFIG_FILE", creds + ".missing");
        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", "AKIATESTENV");
        Environment.SetEnvironmentVariable("AWS_DEFAULT_REGION", "us-east-1");
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var found = await hosted.Client.CallAsync("account/bedrock/discover");
            var names = found.GetProperty("profiles").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToArray();
            Assert.Contains("default", names);
            Assert.Contains("codex-bedrock", names);
            var json = found.GetRawText();
            Assert.DoesNotContain("AKIATESTNOTASECRET", json);
            Assert.DoesNotContain("wJalrXUtnFEMI", json);
            var env = found.GetProperty("environmentCredentials").EnumerateArray().Select(c => c.GetProperty("type").GetString()).ToArray();
            Assert.Contains("accessKeys", env);
            var setup = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                hosted.Client.CallAsync("account/bedrock/setup", new { type = "profile", profile = "default", region = "us-east-1" }));
            Assert.Contains("not implemented", setup.Message);
            var login = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                hosted.Client.CallAsync("account/login/start", new { type = "amazonBedrock", apiKey = "x", region = "us-east-1" }));
            Assert.Contains("Bedrock", login.Message);
            var consume = await hosted.Client.CallAsync("account/rateLimitResetCredit/consume", new { idempotencyKey = "k1" });
            Assert.Equal("noCredit", consume.GetProperty("outcome").GetString());
            var nudge = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                hosted.Client.CallAsync("account/sendAddCreditsNudgeEmail", new { creditType = "credits" }));
            Assert.Contains("ChatGPT", nudge.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE", prevCreds);
            Environment.SetEnvironmentVariable("AWS_CONFIG_FILE", prevCfg);
            Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", prevKey);
            Environment.SetEnvironmentVariable("AWS_DEFAULT_REGION", prevRegion);
            try { File.Delete(creds); } catch { /* ignore */ }
        }
    }
}

public class AccountRateLimitsTests
{
    [Fact]
    public async Task Read_returns_empty_snapshot()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        await client.NotifyAsync("initialized");
        var limits = await client.CallAsync("account/rateLimits/read");
        Assert.True(limits.GetProperty("ordinaryUsageAllowed").GetBoolean());
        Assert.True(limits.TryGetProperty("rateLimits", out _));
    }
}

public class PersonalityConfigTests
{
    [Fact]
    public void Friendly_personality_enters_developer_instructions()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "config.toml"), "personality = \"friendly\"" + Environment.NewLine);
            var cfg = ConfigService.Load(Path.GetTempPath());
            Assert.Contains("friendly", cfg.DeveloperInstructions);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class CollaborationModeListTests
{
    [Fact]
    public async Task Lists_builtin_modes()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        await client.NotifyAsync("initialized");
        var listed = await client.CallAsync("collaborationMode/list");
        Assert.Contains(listed.GetProperty("data").EnumerateArray(), e => e.GetProperty("name").GetString() == "plan");
    }
}

public class CollaborationModeConfigTests
{
    [Fact]
    public void Plan_mode_enters_developer_instructions()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "config.toml"), "collaboration_mode = \"plan\"\n");
            var cfg = ConfigService.Load(Path.GetTempPath());
            Assert.Contains("<collaboration_mode>plan</collaboration_mode>", cfg.DeveloperInstructions);
            Assert.Contains("proposed_plan", cfg.DeveloperInstructions);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ChatgptDeviceCodeTests
{
    [Fact]
    public async Task Device_code_persists_chatgpt_tokens_without_live_network()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        ChatgptDeviceAuth.TestHandler = new DeviceAuthStub();
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var started = await session.LoginDeviceCodeAsync();
            Assert.Equal("chatgptDeviceCode", started.GetProperty("type").GetString());
            Assert.Equal("WD12-XY34", started.GetProperty("userCode").GetString());
            Assert.Contains("/codex/device", started.GetProperty("verificationUrl").GetString());
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && AuthService.AuthMode() != "chatgpt")
            {
                await Task.Delay(20);
            }
            Assert.Equal("chatgpt", AuthService.AuthMode());
            var chrome = await session.LoadChromeAsync();
            Assert.False(chrome.NeedsAuth);
            Assert.Contains("chatgpt", chrome.Status, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ChatgptDeviceAuth.TestHandler = null;
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    private sealed class DeviceAuthStub : HttpMessageHandler
    {
        private int _polls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/deviceauth/usercode", StringComparison.Ordinal))
            {
                return Json(200, """{"device_auth_id":"dev_1","user_code":"WD12-XY34","interval":"0"}""");
            }

            if (path.EndsWith("/deviceauth/token", StringComparison.Ordinal))
            {
                var n = Interlocked.Increment(ref _polls);
                if (n < 2)
                {
                    return Json(403, "{}");
                }

                return Json(200, """{"authorization_code":"ac_1","code_challenge":"ch","code_verifier":"ver"}""");
            }

            if (path.EndsWith("/oauth/token", StringComparison.Ordinal))
            {
                return Json(200, """{"access_token":"at_test","refresh_token":"rt_test","id_token":"id_test"}""");
            }

            return Json(404, "{}");
        }

        private static Task<HttpResponseMessage> Json(int status, string body)
        {
            var resp = new HttpResponseMessage((System.Net.HttpStatusCode)status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(resp);
        }
    }
}

public class ChatgptBrowserOauthTests
{
    [Fact]
    public async Task Browser_callback_exchanges_code_without_live_network()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        var tcp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((System.Net.IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        var previous = ChatgptBrowserAuth.Ports;
        ChatgptBrowserAuth.Ports = [port];
        ChatgptDeviceAuth.TestHandler = new TokenStub();
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var started = await session.LoginChatgptAsync();
            Assert.Equal("chatgpt", started.GetProperty("type").GetString());
            var authUrl = started.GetProperty("authUrl").GetString()!;
            Assert.Contains("oauth/authorize", authUrl);
            Assert.Contains("code_challenge", authUrl);
            var query = new Uri(authUrl).Query.TrimStart('?');
            var state = query.Split('&').Select(p => p.Split('=', 2)).Where(p => p.Length == 2 && p[0] == "state").Select(p => Uri.UnescapeDataString(p[1])).FirstOrDefault();
            Assert.False(string.IsNullOrWhiteSpace(state));
            using var probe = new HttpClient();
            var callback = $"http://127.0.0.1:{port}/auth/callback?code=browser_code&state={Uri.EscapeDataString(state!)}";
            var page = await probe.GetAsync(callback);
            Assert.True(page.IsSuccessStatusCode);
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && AuthService.AuthMode() != "chatgpt")
            {
                await Task.Delay(20);
            }
            Assert.Equal("chatgpt", AuthService.AuthMode());
        }
        finally
        {
            ChatgptBrowserAuth.Ports = previous;
            ChatgptDeviceAuth.TestHandler = null;
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    private sealed class TokenStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/oauth/token", StringComparison.Ordinal))
            {
                var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"at_browser","refresh_token":"rt","id_token":"id"}""", System.Text.Encoding.UTF8, "application/json"),
                };
                return Task.FromResult(resp);
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}

public class AuthRefreshAndToolCallTests
{
    [Fact]
    public async Task Refresh_tool_call_workspace_guardian()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        ChatgptDeviceAuth.TestHandler = new RefreshStub();
        try
        {
            AuthService.SaveChatgptTokens("old", "rt_old", "id_old", "acct");
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var refreshed = await hosted.Client.CallAsync("account/chatgptAuthTokens/refresh", new { reason = "unauthorized" });
            Assert.Equal("at_new", refreshed.GetProperty("accessToken").GetString());
            Assert.Equal("at_new", AuthService.ReadTokens().Access);
            await session.StartThreadAsync(Path.GetTempPath(), "tools");
            var call = await hosted.Client.CallAsync("item/tool/call", new { threadId = session.ThreadId, turnId = "t", callId = "c1", tool = "current_time", arguments = new { } });
            Assert.True(call.GetProperty("success").GetBoolean());
            var msgs = await hosted.Client.CallAsync("account/workspaceMessages/read");
            Assert.Equal(JsonValueKind.Array, msgs.GetProperty("messages").ValueKind);
            var g = await hosted.Client.CallAsync("thread/approveGuardianDeniedAction", new { threadId = session.ThreadId });
            Assert.False(g.GetProperty("approved").GetBoolean());
        }
        finally
        {
            ChatgptDeviceAuth.TestHandler = null;
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    private sealed class RefreshStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"at_new","refresh_token":"rt_new","id_token":"id_new"}""", System.Text.Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(resp);
        }
    }
}

public class StatuslineAndPersonalityTests
{
    [Fact]
    public void Personality_tag_and_statusline_format()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(home, "config.toml"), "personality = \"pragmatic\"\ntui_statusline = \"model,sandbox\"\n");
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var cfg = ConfigService.Load(root);
            Assert.Contains("<personality>pragmatic</personality>", cfg.DeveloperInstructions);
            var session = CodexSession.Start(cfg, "status");
            var line = TuiStatus.Line(session);
            Assert.Contains(cfg.Model, line);
            Assert.Contains(cfg.SandboxMode, line);
            Assert.StartsWith("CodexSharp — ", TuiStatus.Title(session));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public void Feedback_includeLogs_copies_session_jsonl()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            CodexPaths.EnsureLayout();
            Directory.CreateDirectory(CodexPaths.SessionsDir);
            File.WriteAllText(Path.Combine(CodexPaths.SessionsDir, "thr_fb.jsonl"), "{\"type\":\"session_meta\"}\n");
            var id = FeedbackStore.Save("bug", "n", "thr_fb", includeLogs: true);
            var json = File.ReadAllText(Path.Combine(FeedbackStore.Dir, id + ".json"));
            Assert.Contains("thr_fb.jsonl", json);
            Assert.DoesNotContain("sk-live-secret", json);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ModelCatalogWriteTests
{
    [Fact]
    public void Add_persists_overlay_model()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var entry = ModelCatalog.Add("my-local-model", "lmstudio");
            Assert.Equal("my-local-model", entry.Id);
            Assert.Contains(ModelCatalog.List(), e => e.Id == "my-local-model" && e.Provider == "lmstudio");
            Assert.Contains("my-local-model", File.ReadAllText(Path.Combine(home, "models.json")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ConfigReadAppsTests
{
    [Fact]
    public async Task Config_read_reports_computer_and_browser_use_as_not_configured()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        await client.NotifyAsync("initialized");
        var read = await client.CallAsync("config/read");
        Assert.Equal("notConfigured", read.GetProperty("computerUse").GetString());
        Assert.Equal("notConfigured", read.GetProperty("browserUse").GetString());
    }
}

public class LiveSettingsTests
{
    [Fact]
    public void ApplySettings_updates_model_sandbox_and_effort()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            var session = CodexSession.Start(ConfigService.Load(Path.GetTempPath()));
            session.ApplySettings(model: "gpt-test-live", sandbox: "read-only", effort: "xhigh");
            Assert.Equal("gpt-test-live", session.Config.Model);
            Assert.Equal("read-only", session.Config.SandboxMode);
            Assert.Equal("xhigh", session.Config.ReasoningEffort);
            ConfigService.WriteUserKeys(new Dictionary<string, string> { ["tui_statusline"] = "model,effort,sandbox" });
            var line = TuiStatus.Line(session.Config, session.Thread.Id, session.Thread.Title);
            Assert.Contains("gpt-test-live", line);
            Assert.Contains("xhigh", line);
            Assert.Contains("read-only", line);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class PersonalityLiveTests
{
    [Fact]
    public void Merge_keeps_collab_overlay_and_swaps_personality()
    {
        var withPlan = CollaborationModes.MergeDeveloper("keep-me", "plan");
        var friendly = PersonalityModes.MergeDeveloper(withPlan, "friendly");
        Assert.Contains("<collaboration_mode>plan</collaboration_mode>", friendly);
        Assert.Contains("<personality>friendly</personality>", friendly);
        Assert.Contains("keep-me", friendly);
        var pragmatic = PersonalityModes.MergeDeveloper(friendly, "pragmatic");
        Assert.Contains("<personality>pragmatic</personality>", pragmatic);
        Assert.DoesNotContain("<personality>friendly</personality>", pragmatic);
        Assert.Contains("<collaboration_mode>plan</collaboration_mode>", pragmatic);
    }
}

public class CollaborationModeLiveTests
{
    [Fact]
    public void Merge_replaces_previous_overlay()
    {
        Assert.Equal("plan", CollaborationModes.Normalize("PLAN"));
        var merged = CollaborationModes.MergeDeveloper("keep-me", "plan");
        Assert.Contains("<collaboration_mode>plan</collaboration_mode>", merged);
        Assert.Contains("keep-me", merged);
        var again = CollaborationModes.MergeDeveloper(merged, "pair");
        Assert.Contains("<collaboration_mode>pair</collaboration_mode>", again);
        Assert.DoesNotContain("<collaboration_mode>plan</collaboration_mode>", again);
        Assert.Contains("keep-me", again);
    }
}

public class ChatgptAccessTokenLoginTests
{
    [Fact]
    public async Task Login_start_chatgptAuthTokens_persists_without_network()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            CodexPaths.EnsureLayout();
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("tests", "tests");
            await session.LoginAccessTokenAsync("tok_test_access");
            Assert.Equal("chatgpt", AuthService.AuthMode());
            var tokens = AuthService.ReadTokens();
            Assert.Equal("tok_test_access", tokens.Access);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class AccountUsageReadTests
{
    [Fact]
    public async Task Usage_read_includes_local_thread_estimate()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            CodexPaths.EnsureLayout();
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("tests", "tests");
            await session.StartThreadAsync();
            var usage = await session.ReadUsageAsync();
            Assert.True(usage.TryGetProperty("threadUsage", out var tu));
            Assert.Equal(JsonValueKind.Object, tu.ValueKind);
            Assert.True(tu.TryGetProperty("estimatedTokens", out _));
            Assert.True(usage.TryGetProperty("summary", out var summary));
            Assert.Equal(JsonValueKind.Null, summary.GetProperty("lifetimeTokens").ValueKind);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ModelListProtocolTests
{
    [Fact]
    public void Catalog_includes_builtin_ids()
    {
        var ids = ModelCatalog.List().Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("gpt-4.1-mini", ids);
        Assert.Contains("gpt-4.1", ids);
    }

    [Fact]
    public void Overlay_models_json_is_listed()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "models.json"), """[{"id":"local-llama","provider":"lmstudio"}]""");
            Assert.Contains(ModelCatalog.List(), m => m.Id == "local-llama" && m.Provider == "lmstudio");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Model_list_includes_current_model()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        await client.NotifyAsync("initialized");
        var listed = await client.CallAsync("model/list");
        Assert.True(listed.GetProperty("data").GetArrayLength() >= 1);
        Assert.False(string.IsNullOrWhiteSpace(listed.GetProperty("data")[0].GetProperty("id").GetString()));
    }
}

public class ExperimentalFeatureListTests
{
    [Fact]
    public async Task Lists_feature_flags()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        await client.NotifyAsync("initialized");
        var listed = await client.CallAsync("experimentalFeature/list");
        Assert.True(listed.GetProperty("data").GetArrayLength() >= 1);
        Assert.False(string.IsNullOrWhiteSpace(listed.GetProperty("data")[0].GetProperty("name").GetString()));
        Assert.True(listed.GetProperty("data")[0].TryGetProperty("defaultEnabled", out _));
        Assert.Contains(listed.GetProperty("data").EnumerateArray(), x => x.GetProperty("name").GetString() == "web_search" && x.GetProperty("description").GetString()!.Length > 0);
    }

    [Fact]
    public async Task Enablement_set_writes_named_flags()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var client = hosted.Client;
            await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
            await client.NotifyAsync("initialized");
            var result = await client.CallAsync("experimentalFeature/enablement/set", new
            {
                enablement = new Dictionary<string, bool> { ["web_search"] = true },
            });
            Assert.True(result.GetProperty("enablement").GetProperty("web_search").GetBoolean());
            Assert.True(FeatureFlags.IsEnabled("web_search"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class PermissionProfileListTests
{
    [Fact]
    public async Task Lists_sandbox_profiles()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        await client.NotifyAsync("initialized");
        var listed = await client.CallAsync("permissionProfile/list");
        Assert.Contains(listed.GetProperty("data").EnumerateArray(), e => e.GetProperty("id").GetString() == "workspace-write");
    }
}

public class ModelProviderCapabilitiesTests
{
    [Fact]
    public async Task Reads_capability_flags()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        var caps = await client.CallAsync("model/provider/capabilities/read");
        Assert.True(caps.GetProperty("namespaceTools").GetBoolean());
        Assert.False(caps.GetProperty("imageGeneration").GetBoolean());
        Assert.True(caps.TryGetProperty("webSearch", out _));
    }
}

