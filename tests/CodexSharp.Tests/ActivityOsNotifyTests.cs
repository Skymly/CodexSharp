using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public class ActivityOsNotifyTests
{
    [Fact]
    public void Inbox_tracks_running_waiting_unread_and_not_scheduled()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            ActivityInbox.RecordRunning("thr_a", "Working");
            ActivityInbox.RecordWaitingApproval("thr_a", "Needs approval");
            var completed = ActivityInbox.RecordCompleted("thr_a", "Done");
            Assert.DoesNotContain(ActivityInbox.List(), i => i.Kind == ActivityKind.Running);
            Assert.DoesNotContain(ActivityInbox.List(), i => i.Kind == ActivityKind.WaitingApproval);
            var unread = Assert.Single(ActivityInbox.List(ActivityKind.Unread));
            Assert.Equal("thr_a", unread.ThreadId);
            Assert.Equal("Done", unread.Title);
            Assert.Equal(completed.Id, unread.Id);
            Assert.False(ActivityInbox.HasScheduledFilter);
            Assert.DoesNotContain(ActivityInbox.List(), i => i.Kind.ToString().Contains("Schedul", StringComparison.OrdinalIgnoreCase));

            ActivityInbox.MarkThreadRead("thr_a");
            Assert.Empty(ActivityInbox.List(ActivityKind.Unread));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
            try { Directory.Delete(home, true); } catch { }
        }
    }

    [Fact]
    public void Os_notify_respects_off_and_uses_sender_when_enabled()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        var sent = new List<(string Title, string Body)>();
        OsNotify.TestSender = (title, body) =>
        {
            sent.Add((title, body));
            return true;
        };
        try
        {
            Directory.CreateDirectory(home);
            ConfigService.WriteUserKeys(new Dictionary<string, string> { ["tui_notifications"] = "off" });
            Assert.True(OsNotify.IsOff);
            Assert.False(OsNotify.TrySend("Turn completed", "thr_a"));
            Assert.Empty(sent);

            ConfigService.WriteUserKeys(new Dictionary<string, string> { ["tui_notifications"] = "auto" });
            Assert.False(OsNotify.IsOff);
            Assert.True(OsNotify.TrySend("Turn completed", "waiting for approval"));
            var one = Assert.Single(sent);
            Assert.Equal("Turn completed", one.Title);
            Assert.Equal("waiting for approval", one.Body);
        }
        finally
        {
            OsNotify.TestSender = null;
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
            try { Directory.Delete(home, true); } catch { }
        }
    }
}
