using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public class DesktopPopoutTests
{
    [Fact]
    public void Open_binds_same_thread_and_is_always_on_top()
    {
        var pop = new DesktopPopout();
        pop.Open("thr_live");
        Assert.True(pop.IsOpen);
        Assert.True(pop.Topmost);
        Assert.Equal("thr_live", pop.ThreadId);
        Assert.False(pop.Forked);
        pop.Close();
        Assert.False(pop.IsOpen);
        Assert.Equal("thr_live", pop.ThreadId);
    }

    [Fact]
    public void Open_rejects_blank_thread_id()
    {
        var pop = new DesktopPopout();
        Assert.Throws<ArgumentException>(() => pop.Open(" "));
        Assert.False(pop.IsOpen);
    }
}
