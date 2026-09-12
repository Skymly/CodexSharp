namespace CodexSharp.Runtime;

public sealed record DesktopTerminalTab(string Id, string Buffer, string Input, bool Running);

/// In-session desktop terminal tabs for TERM-02. I/O-free; no cwd per tab; no persistence.
public sealed class DesktopTerminalTabs
{
    public const int BufferCap = 8000;

    private readonly DesktopTerminalTab[] _tabs;
    private readonly int _active;
    private readonly int _next;

    private DesktopTerminalTabs(DesktopTerminalTab[] tabs, int active, int next)
    {
        _tabs = tabs;
        _active = active;
        _next = next;
    }

    public static DesktopTerminalTabs Start() =>
        new([new DesktopTerminalTab("ui-term-1", "", "", false)], 0, 2);

    public IReadOnlyList<DesktopTerminalTab> All => _tabs;

    public DesktopTerminalTab Active => _tabs[_active];

    public DesktopTerminalTabs Open()
    {
        var id = "ui-term-" + _next;
        var tabs = new DesktopTerminalTab[_tabs.Length + 1];
        Array.Copy(_tabs, tabs, _tabs.Length);
        tabs[_tabs.Length] = new DesktopTerminalTab(id, "", "", false);
        return new DesktopTerminalTabs(tabs, tabs.Length - 1, _next + 1);
    }

    public DesktopTerminalTabs Select(string id)
    {
        var i = IndexOf(id);
        return i < 0 || i == _active ? this : new DesktopTerminalTabs(_tabs, i, _next);
    }

    public DesktopTerminalTabs SetInput(string input)
    {
        return WithActive(t => t with { Input = input ?? "" });
    }

    public DesktopTerminalTabs PrepareRun(string command)
    {
        var line = "> " + command + "\n";
        return WithActive(t => t with { Buffer = Clip(t.Buffer + line), Running = true });
    }

    public DesktopTerminalTabs AppendDelta(string pid, string text)
    {
        if (string.IsNullOrEmpty(text)) return this;
        var i = IndexOf(pid);
        if (i < 0) return this;
        var tab = _tabs[i];
        var next = tab with { Buffer = Clip(tab.Buffer + text) };
        if (ReferenceEquals(next, tab) || next == tab) return this;
        return Replace(i, next);
    }

    public DesktopTerminalTabs AppendToActive(string text)
    {
        if (string.IsNullOrEmpty(text)) return this;
        return WithActive(t => t with { Buffer = Clip(t.Buffer + text) });
    }

    public DesktopTerminalTabs MarkExited(string pid)
    {
        var i = IndexOf(pid);
        if (i < 0 || !_tabs[i].Running) return this;
        return Replace(i, _tabs[i] with { Running = false });
    }

    private DesktopTerminalTabs WithActive(Func<DesktopTerminalTab, DesktopTerminalTab> map)
    {
        var next = map(_tabs[_active]);
        return next == _tabs[_active] ? this : Replace(_active, next);
    }

    private DesktopTerminalTabs Replace(int i, DesktopTerminalTab tab)
    {
        var tabs = (DesktopTerminalTab[])_tabs.Clone();
        tabs[i] = tab;
        return new DesktopTerminalTabs(tabs, _active, _next);
    }

    private int IndexOf(string id)
    {
        if (string.IsNullOrEmpty(id)) return -1;
        for (var i = 0; i < _tabs.Length; i++)
        {
            if (_tabs[i].Id == id) return i;
        }

        return -1;
    }

    private static string Clip(string buffer)
    {
        if (buffer.Length <= BufferCap) return buffer;
        return buffer[^BufferCap..];
    }
}