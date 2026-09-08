using CodexSharp.AppServer;
using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public class Bcu06ComputerUseTests
{
    [Fact]
    public void Computer_use_stays_notConfigured_and_locked()
    {
        Assert.Equal("notConfigured", HonestStubs.StatusOf("computer_use"));
        Assert.True(HonestStubs.ForbidsEnable("computer_use"));
        Assert.False(HonestStubs.IsTogglableFeature("computer_use"));
        Assert.Contains(HonestStubs.Capabilities, c => c.Id == "computer_use" && c.Label == "Computer Use");
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            FeatureFlags.Set("computer_use", true);
            Assert.False(FeatureFlags.IsEnabled("computer_use"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Json_rpc_cannot_enable_computer_use()
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
            var req = await client.CallAsync("configRequirements/read");
            Assert.False(req.GetProperty("allowBrowserAndComputerUse").GetBoolean());
            var listed = await client.CallAsync("experimentalFeature/list");
            var cu = listed.GetProperty("data").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "computer_use");
            Assert.False(cu.GetProperty("enabled").GetBoolean());
            var set = await client.CallAsync("experimentalFeature/enablement/set", new
            {
                enablement = new Dictionary<string, bool> { ["computer_use"] = true },
            });
            Assert.False(set.GetProperty("enablement").GetProperty("computer_use").GetBoolean());
            Assert.False(FeatureFlags.IsEnabled("computer_use"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}
