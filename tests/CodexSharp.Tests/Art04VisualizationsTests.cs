using CodexSharp.AppServer;
using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public class Art04VisualizationsTests
{
    [Fact]
    public void Catalog_lists_visualize_notConfigured_without_enable_or_supported_claim()
    {
        var row = Assert.Single(HonestStubs.Capabilities, c => c.Id == "visualize");
        Assert.Equal("Visualizations", row.Label);
        Assert.Equal("notConfigured", row.Status);
        Assert.Equal("notConfigured", HonestStubs.StatusOf("visualize"));
        Assert.DoesNotContain("Enable", row.Label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Enable", row.Status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Enable", row.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("已支持 Visualize", row.Label, StringComparison.Ordinal);
        Assert.DoesNotContain("已支持 Visualize", row.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("@Visualize", row.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.True(HonestStubs.ForbidsEnable("visualize"));
        Assert.False(HonestStubs.IsTogglableFeature("visualize"));
        Assert.DoesNotContain(FeatureFlags.Specs, s => s.Key.Equals("visualize", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Visualize_enable_cannot_stick()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            FeatureFlags.Set("visualize", true);
            Assert.False(FeatureFlags.IsEnabled("visualize"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public void Preview_extracts_html_and_missing_file_is_path_only()
    {
        Assert.True(SystemFilePreview.IsOffice(@"C:\tmp\chart.html"));
        Assert.True(SystemFilePreview.IsOffice("plot.htm"));
        Assert.False(SystemFilePreview.IsOffice("notes.txt"));
        var hits = SystemFilePreview.Extract(@"see C:\work\chart.html and ./plot.htm");
        Assert.Contains(hits, h => h.EndsWith(".html", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(hits, h => h.EndsWith(".htm", StringComparison.OrdinalIgnoreCase));
        var missing = Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".html");
        var preview = SystemFilePreview.TryOpen(missing);
        Assert.False(preview.Opened);
        Assert.Contains(missing, preview.Path, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(preview.Error));
        Assert.DoesNotContain("http://", preview.Path, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", preview.Error ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Capabilities_read_visualize_is_never_true()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        using var env = new Art04EnvScope(new Dictionary<string, string?>
        {
            ["CODEXSHARP_HOME"] = home,
            ["OPENAI_API_KEY"] = null,
            ["CODEXSHARP_API_KEY"] = null,
            ["CODEX_API_KEY"] = null,
        });
        Directory.CreateDirectory(home);

        await using (var hosted = InProcessAppServer.Start())
        {
            var client = hosted.Client;
            await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
            var caps = await client.CallAsync("model/provider/capabilities/read");
            Assert.False(caps.GetProperty("visualize").GetBoolean());
        }

        AuthService.SaveApiKey("sk-test-visualize");
        await using (var hosted = InProcessAppServer.Start())
        {
            var client = hosted.Client;
            await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
            var caps = await client.CallAsync("model/provider/capabilities/read");
            Assert.False(caps.GetProperty("visualize").GetBoolean());
        }
    }

    [Fact]
    public async Task Enablement_set_cannot_enable_visualize()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            await using var hosted = InProcessAppServer.Start();
            var client = hosted.Client;
            await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
            await client.NotifyAsync("initialized");
            var result = await client.CallAsync("experimentalFeature/enablement/set", new
            {
                enablement = new Dictionary<string, bool> { ["visualize"] = true },
            });
            Assert.False(result.GetProperty("enablement").GetProperty("visualize").GetBoolean());
            Assert.False(FeatureFlags.IsEnabled("visualize"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

file sealed class Art04EnvScope : IDisposable
{
    private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);

    public Art04EnvScope(Dictionary<string, string?> values)
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
