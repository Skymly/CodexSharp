using CodexSharp.AppServer;
using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public class Set03HonestSettingsTests
{
    [Fact]
    public void Catalog_lists_c4_capabilities_without_enable()
    {
        var labels = HonestStubs.Capabilities.Select(c => c.Label).ToArray();
        Assert.Contains("Computer Use", labels);
        Assert.Contains("Browser Use", labels);
        Assert.Contains("Voice / realtime", labels);
        Assert.Contains("code_mode_host", labels);
        Assert.All(HonestStubs.Capabilities, c =>
        {
            Assert.True(c.Status is "notConfigured" or "underDevelopment", c.Label);
            Assert.DoesNotContain("Enable", c.Label, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Enable", c.Status, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Enable", c.Detail, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal("notConfigured", HonestStubs.StatusOf("computer_use"));
        Assert.Equal("notConfigured", HonestStubs.StatusOf("browser_use"));
        Assert.Equal("notConfigured", HonestStubs.StatusOf("realtime"));
        Assert.Equal("underDevelopment", HonestStubs.StatusOf("code_mode_host"));
        Assert.True(HonestStubs.ForbidsEnable("computer_use"));
        Assert.True(HonestStubs.ForbidsEnable("browser_use"));
        Assert.True(HonestStubs.ForbidsEnable("realtime"));
        Assert.True(HonestStubs.ForbidsEnable("code_mode_host"));
        Assert.False(HonestStubs.IsTogglableFeature("computer_use"));
        Assert.True(HonestStubs.IsTogglableFeature("web_search"));
    }

    [Fact]
    public void Locked_flags_cannot_be_enabled()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            foreach (var name in new[] { "computer_use", "browser_use", "realtime", "code_mode_host" })
            {
                FeatureFlags.Set(name, true);
                Assert.False(FeatureFlags.IsEnabled(name));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Config_read_and_enablement_set_stay_honest()
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
            Assert.Equal("notConfigured", read.GetProperty("computerUse").GetString());
            Assert.Equal("notConfigured", read.GetProperty("browserUse").GetString());
            var result = await client.CallAsync("experimentalFeature/enablement/set", new
            {
                enablement = new Dictionary<string, bool>
                {
                    ["computer_use"] = true,
                    ["browser_use"] = true,
                    ["realtime"] = true,
                    ["code_mode_host"] = true,
                    ["web_search"] = true,
                },
            });
            var en = result.GetProperty("enablement");
            Assert.False(en.GetProperty("computer_use").GetBoolean());
            Assert.False(en.GetProperty("browser_use").GetBoolean());
            Assert.False(en.GetProperty("realtime").GetBoolean());
            Assert.False(en.GetProperty("code_mode_host").GetBoolean());
            Assert.True(en.GetProperty("web_search").GetBoolean());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}
