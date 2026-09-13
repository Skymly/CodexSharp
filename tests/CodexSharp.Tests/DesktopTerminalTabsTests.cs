using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public class DesktopTerminalTabsTests
{
    [Fact]
    public void Start_is_one_tab_ui_term_1()
    {
        var tabs = DesktopTerminalTabs.Start();
        var only = Assert.Single(tabs.All);
        Assert.Equal("ui-term-1", only.Id);
        Assert.Equal("ui-term-1", tabs.Active.Id);
        Assert.Equal("", only.Buffer);
        Assert.False(only.Running);
    }

    [Fact]
    public void Open_second_tab_does_not_clear_first_buffer_or_pid()
    {
        var first = DesktopTerminalTabs.Start()
            .PrepareRun("echo one")
            .AppendDelta("ui-term-1", "one\n");
        var two = first.Open();

        Assert.Equal(2, two.All.Count);
        Assert.Equal("ui-term-1", two.All[0].Id);
        Assert.Equal("ui-term-2", two.All[1].Id);
        Assert.Equal("ui-term-2", two.Active.Id);
        Assert.Contains("echo one", two.All[0].Buffer, StringComparison.Ordinal);
        Assert.Contains("one\n", two.All[0].Buffer, StringComparison.Ordinal);
        Assert.True(two.All[0].Running);
        Assert.Equal("", two.All[1].Buffer);
        Assert.NotEqual(two.All[0].Id, two.All[1].Id);
        Assert.All(two.All, t => Assert.StartsWith("ui-term-", t.Id, StringComparison.Ordinal));
        Assert.DoesNotContain("ui-term", two.All.Select(t => t.Id), StringComparer.Ordinal);
    }

    [Fact]
    public void Delta_to_first_while_second_selected_leaves_first()
    {
        var tabs = DesktopTerminalTabs.Start()
            .PrepareRun("echo one")
            .Open()
            .PrepareRun("echo two")
            .AppendDelta("ui-term-1", "still-one\n")
            .AppendDelta("ui-term-2", "still-two\n");

        Assert.Contains("still-one", tabs.All[0].Buffer, StringComparison.Ordinal);
        Assert.Contains("still-two", tabs.All[1].Buffer, StringComparison.Ordinal);
        Assert.DoesNotContain("still-two", tabs.All[0].Buffer, StringComparison.Ordinal);
        Assert.DoesNotContain("still-one", tabs.All[1].Buffer, StringComparison.Ordinal);
        Assert.Equal("ui-term-2", tabs.Active.Id);
    }

    [Fact]
    public void MarkExited_one_pid_does_not_stop_the_other()
    {
        var tabs = DesktopTerminalTabs.Start()
            .PrepareRun("echo one")
            .Open()
            .PrepareRun("echo two")
            .MarkExited("ui-term-1");

        Assert.False(tabs.All[0].Running);
        Assert.True(tabs.All[1].Running);
        Assert.Contains("echo one", tabs.All[0].Buffer, StringComparison.Ordinal);
        Assert.Contains("echo two", tabs.All[1].Buffer, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_switches_active_buffer_without_merging()
    {
        var tabs = DesktopTerminalTabs.Start()
            .SetInput("first")
            .Open()
            .SetInput("second")
            .Select("ui-term-1");

        Assert.Equal("ui-term-1", tabs.Active.Id);
        Assert.Equal("first", tabs.Active.Input);
        Assert.Equal("second", tabs.All[1].Input);
    }

    [Fact]
    public void Tabs_have_no_independent_cwd()
    {
        var tabs = DesktopTerminalTabs.Start().Open();
        Assert.All(tabs.All, t => Assert.Null(t.GetType().GetProperty("Cwd")));
        Assert.Null(typeof(DesktopTerminalTab).GetProperty("Cwd"));
    }

    [Fact]
    public void Unknown_pid_delta_is_ignored()
    {
        var tabs = DesktopTerminalTabs.Start().AppendDelta("other", "nope");
        Assert.Equal("", tabs.Active.Buffer);
    }

    [Fact]
    public void Start_defaults_to_powershell()
    {
        var tabs = DesktopTerminalTabs.Start();
        Assert.Equal("powershell", tabs.Active.Shell);
    }

    [Fact]
    public void Open_defaults_to_powershell_not_last_pick()
    {
        var tabs = DesktopTerminalTabs.Start().SetShell("cmd").Open();
        Assert.Equal("cmd", tabs.All[0].Shell);
        Assert.Equal("powershell", tabs.All[1].Shell);
        Assert.Equal("powershell", tabs.Active.Shell);
    }

    [Fact]
    public void SetShell_cmd_before_run()
    {
        var tabs = DesktopTerminalTabs.Start().SetShell("cmd");
        Assert.Equal("cmd", tabs.Active.Shell);
        Assert.False(tabs.Active.Running);
    }

    [Fact]
    public void SetShell_on_running_tab_is_noop()
    {
        var tabs = DesktopTerminalTabs.Start().PrepareRun("echo one").SetShell("cmd");
        Assert.Equal("powershell", tabs.Active.Shell);
        Assert.True(tabs.Active.Running);
    }

    [Fact]
    public void ExecArgv_powershell_is_powershell_exe()
    {
        var argv = DesktopTerminalTabs.Start().Active.ExecArgv("echo hi");
        Assert.Equal(new[] { "powershell.exe", "-NoLogo", "-NoProfile", "-Command", "echo hi" }, argv);
    }

    [Fact]
    public void ExecArgv_cmd_is_cmd_exe()
    {
        var argv = DesktopTerminalTabs.Start().SetShell("cmd").Active.ExecArgv("echo hi");
        Assert.Equal(new[] { "cmd.exe", "/c", "echo hi" }, argv);
    }
}
