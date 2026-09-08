namespace CodexSharp.Runtime;

/// Desktop-local pop-out window state (NAV-04). Not protocol.
public sealed class DesktopPopout
{
    public bool IsOpen { get; private set; }
    public bool Topmost { get; private set; }
    public string ThreadId { get; private set; } = "";
    public bool Forked { get; private set; }

    public void Open(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("thread id is required", nameof(threadId));
        }

        ThreadId = threadId.Trim();
        Topmost = true;
        IsOpen = true;
        Forked = false;
    }

    public void Close()
    {
        IsOpen = false;
    }
}
