using CodexSharp.AppServer;
using CodexSharp.Core;
using CodexSharp.Protocol;
using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public sealed class FakeImagesClient : IImagesClient
{
    public static readonly byte[] TinyPng =
    [
        137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0, 31, 21, 196, 137,
        0, 0, 0, 13, 73, 68, 65, 84, 120, 156, 99, 248, 207, 192, 240, 31, 0, 5, 0, 1, 255, 137, 153, 61, 29, 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130
    ];

    public int Calls { get; private set; }
    public string? LastPrompt { get; private set; }

    public Task<GeneratedImage> GenerateAsync(string prompt, CancellationToken ct)
    {
        Calls++;
        LastPrompt = prompt;
        return Task.FromResult(new GeneratedImage(TinyPng));
    }
}

public class ImageGenerationTests
{
    [Fact]
    public void Prompt_includes_image_gen_tool()
    {
        var cfg = ConfigService.Load(Path.GetTempPath());
        var request = Prompt.build(cfg, [new HistoryMessage("user", "hi", "", "", "")]);
        Assert.Contains(request.Tools, x => x.Name == "image_gen");
    }

    [Fact]
    public async Task Without_key_generate_is_notConfigured_and_does_not_write()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cfg = ConfigWithoutKey(root);
        var fake = new FakeImagesClient();
        var exec = new BuiltinToolExecutor(cfg, images: fake);
        var result = await exec.ExecuteAsync(new ToolCallRequest("img1", "image_gen", "{\"prompt\":\"a blue whale\"}"), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Contains("notConfigured", result.Output);
        Assert.Contains("not ChatGPT quota", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fake.Calls);
        Assert.False(Directory.Exists(Path.Combine(root, "generated_images")));
    }

    [Fact]
    public async Task With_key_fake_client_saves_workspace_png()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cfg = ConfigWithKey(root, "sk-test-images");
        var fake = new FakeImagesClient();
        var exec = new BuiltinToolExecutor(cfg, images: fake);
        var result = await exec.ExecuteAsync(new ToolCallRequest("img1", "image_gen", "{\"prompt\":\"a blue whale\"}"), CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Equal(1, fake.Calls);
        Assert.Equal("a blue whale", fake.LastPrompt);
        Assert.Contains("path=", result.Output);
        var paths = ImageMessageContent.ExtractLocalPaths(result.Output);
        var saved = Assert.Single(paths);
        Assert.StartsWith(Path.GetFullPath(root), saved, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("generated_images", saved, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FakeImagesClient.TinyPng, File.ReadAllBytes(saved));
    }

    [Fact]
    public void Image_generation_is_not_a_locked_feature()
    {
        Assert.False(HonestStubs.IsLockedFeature("image_generation"));
        Assert.False(HonestStubs.IsLockedFeature("imageGeneration"));
        Assert.Contains(HonestStubs.Capabilities, c => c.Id == "credits" && c.Status == "notConfigured");
    }

    [Fact]
    public async Task Capabilities_follow_api_key_and_never_claim_credits()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        using var env = new EnvScope(new Dictionary<string, string?>
        {
            ["CODEXSHARP_HOME"] = home,
            ["OPENAI_API_KEY"] = null,
            ["CODEXSHARP_API_KEY"] = null,
            ["CODEX_API_KEY"] = null,
            ["MINIMAX"] = null,
            ["MiniMax"] = null,
        });
        Directory.CreateDirectory(home);

        await using (var hosted = InProcessAppServer.Start())
        {
            var client = hosted.Client;
            await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
            var caps = await client.CallAsync("model/provider/capabilities/read");
            Assert.False(caps.GetProperty("imageGeneration").GetBoolean());
        }

        AuthService.SaveApiKey("sk-test-images");
        await using (var hosted = InProcessAppServer.Start())
        {
            var client = hosted.Client;
            await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
            var caps = await client.CallAsync("model/provider/capabilities/read");
            Assert.True(caps.GetProperty("imageGeneration").GetBoolean());
        }
    }

    private static CodexConfig ConfigWithoutKey(string root)
    {
        var cfg = ConfigService.Load(root, sandboxOverride: "workspace-write", approvalOverride: "never");
        var provider = new ModelProvider(cfg.Provider.Id, cfg.Provider.Name, cfg.Provider.BaseUrl, cfg.Provider.EnvKey, cfg.Provider.WireApi, "");
        return new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, provider, "never", "workspace-write", "", "", root, false, 8);
    }

    private static CodexConfig ConfigWithKey(string root, string key)
    {
        var cfg = ConfigWithoutKey(root);
        var provider = new ModelProvider(cfg.Provider.Id, cfg.Provider.Name, cfg.Provider.BaseUrl, cfg.Provider.EnvKey, cfg.Provider.WireApi, key);
        return new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, provider, "never", "workspace-write", "", "", root, false, 8);
    }
}

file sealed class EnvScope : IDisposable
{
    private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);

    public EnvScope(Dictionary<string, string?> values)
    {
        foreach (var (name, value) in values)
        {
            _previous[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _previous)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
