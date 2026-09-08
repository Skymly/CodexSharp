using CodexSharp.AppServer;
using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public class Bcu01BrowserUseTests
{
    [Fact]
    public void Browser_use_stays_notConfigured_and_locked()
    {
        Assert.Equal("notConfigured", HonestStubs.StatusOf("browser_use"));
        Assert.True(HonestStubs.ForbidsEnable("browser_use"));
        Assert.False(HonestStubs.IsTogglableFeature("browser_use"));
        Assert.Contains(HonestStubs.Capabilities, c => c.Id == "browser_use" && c.Label == "Browser Use");
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            FeatureFlags.Set("browser_use", true);
            Assert.False(FeatureFlags.IsEnabled("browser_use"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Json_rpc_cannot_enable_browser_use()
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
            var read = await client.CallAsync("config/read");
            Assert.Equal("notConfigured", read.GetProperty("browserUse").GetString());
            var req = await client.CallAsync("configRequirements/read");
            Assert.False(req.GetProperty("allowBrowserAndComputerUse").GetBoolean());
            var listed = await client.CallAsync("experimentalFeature/list");
            var browser = listed.GetProperty("data").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "browser_use");
            Assert.False(browser.GetProperty("enabled").GetBoolean());
            var set = await client.CallAsync("experimentalFeature/enablement/set", new
            {
                enablement = new Dictionary<string, bool> { ["browser_use"] = true },
            });
            Assert.False(set.GetProperty("enablement").GetProperty("browser_use").GetBoolean());
            Assert.False(FeatureFlags.IsEnabled("browser_use"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}
