using CodexSharp.AppServer;
using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public class Ext08CodeModeHostTests
{
    [Fact]
    public void Code_mode_host_stays_underDevelopment_and_locked()
    {
        Assert.Equal("underDevelopment", HonestStubs.StatusOf("code_mode_host"));
        Assert.True(HonestStubs.ForbidsEnable("code_mode_host"));
        Assert.False(HonestStubs.IsTogglableFeature("code_mode_host"));
        Assert.Contains(HonestStubs.Capabilities, c => c.Id == "code_mode_host");
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            FeatureFlags.Set("code_mode_host", true);
            Assert.False(FeatureFlags.IsEnabled("code_mode_host"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Json_rpc_cannot_enable_code_mode_host()
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
            Assert.Equal("underDevelopment", read.GetProperty("codeModeHost").GetString());
            var listed = await client.CallAsync("experimentalFeature/list");
            var host = listed.GetProperty("data").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "code_mode_host");
            Assert.False(host.GetProperty("enabled").GetBoolean());
            var set = await client.CallAsync("experimentalFeature/enablement/set", new
            {
                enablement = new Dictionary<string, bool> { ["code_mode_host"] = true },
            });
            Assert.False(set.GetProperty("enablement").GetProperty("code_mode_host").GetBoolean());
            Assert.False(FeatureFlags.IsEnabled("code_mode_host"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}
