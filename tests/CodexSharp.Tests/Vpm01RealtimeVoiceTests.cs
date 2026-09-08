using CodexSharp.AppServer;
using CodexSharp.Client;
using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public class Vpm01RealtimeVoiceTests
{
    [Fact]
    public void Realtime_stays_notConfigured_and_locked()
    {
        Assert.Equal("notConfigured", HonestStubs.StatusOf("realtime"));
        Assert.True(HonestStubs.ForbidsEnable("realtime"));
        Assert.False(HonestStubs.IsTogglableFeature("realtime"));
        Assert.Contains(HonestStubs.Capabilities, c => c.Id == "realtime" && c.Label == "Voice / realtime");
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            FeatureFlags.Set("realtime", true);
            Assert.False(FeatureFlags.IsEnabled("realtime"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Json_rpc_cannot_enable_or_start_voice()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var read = await hosted.Client.CallAsync("config/read");
            Assert.Equal("notConfigured", read.GetProperty("realtimeVoice").GetString());
            var listed = await hosted.Client.CallAsync("experimentalFeature/list");
            var rt = listed.GetProperty("data").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "realtime");
            Assert.False(rt.GetProperty("enabled").GetBoolean());
            var set = await hosted.Client.CallAsync("experimentalFeature/enablement/set", new
            {
                enablement = new Dictionary<string, bool> { ["realtime"] = true },
            });
            Assert.False(set.GetProperty("enablement").GetProperty("realtime").GetBoolean());
            Assert.False(FeatureFlags.IsEnabled("realtime"));

            var threadId = await session.StartThreadAsync(Path.GetTempPath(), "voice-stub");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                hosted.Client.CallAsync("thread/realtime/start", new { threadId }));
            Assert.Contains("notConfigured", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}
