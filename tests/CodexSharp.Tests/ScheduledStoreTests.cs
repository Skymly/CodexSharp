using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public class ScheduledStoreTests
{
    [Fact]
    public void Skips_when_app_not_running_and_does_not_silently_succeed()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        var fired = 0;
        try
        {
            Directory.CreateDirectory(home);
            var now = DateTimeOffset.Parse("2026-09-08T00:00:00Z");
            var independent = ScheduledStore.Create("ping independent", 1, null, now.AddMinutes(-1));
            var threadTask = ScheduledStore.Create("ping thread", 1, "thr_x", now.AddMinutes(-1));
            Assert.Equal(ScheduledKind.Independent, independent.Kind);
            Assert.Equal(ScheduledKind.Thread, threadTask.Kind);
            Assert.Equal("thr_x", threadTask.ThreadId);

            var skipped = ScheduledStore.Tick(now, appRunning: false, _ => fired++);
            Assert.Equal(0, fired);
            Assert.All(skipped, r => Assert.Equal("skipped", r.Status));
            Assert.Contains(skipped, r => r.Note == "app not running");
            Assert.DoesNotContain(ScheduledStore.Runs(), r => r.Status == "started");

            var started = ScheduledStore.Tick(now.AddMinutes(1), appRunning: true, _ => fired++);
            Assert.Equal(2, fired);
            Assert.All(started, r => Assert.Equal("started", r.Status));
            Assert.Equal(2, started.Count);

            Assert.True(ScheduledStore.Cancel(independent.Id));
            Assert.DoesNotContain(ScheduledStore.List(), t => t.Id == independent.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
            try { Directory.Delete(home, true); } catch { }
        }
    }
}
