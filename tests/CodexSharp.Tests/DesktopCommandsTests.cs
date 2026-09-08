using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public class DesktopCommandsTests
{
    [Fact]
    public void Ctrl_k_opens_palette_and_is_not_clear_terminal()
    {
        Assert.Equal(DesktopCommands.OpenPalette, DesktopCommands.Match("ctrl+k"));
        Assert.Equal(DesktopCommands.OpenPalette, DesktopCommands.Match("CTRL+K"));
        Assert.Equal(DesktopCommands.OpenPalette, DesktopCommands.Match("ctrl+shift+p"));
        Assert.False(DesktopCommands.IsClearTerminal("ctrl+k"));
        Assert.True(DesktopCommands.IsClearTerminal("ctrl+l"));
        Assert.Null(DesktopCommands.Match("ctrl+l"));
    }

    [Fact]
    public void Windows_common_chords_match_published_defaults()
    {
        Assert.Equal(DesktopCommands.NewThread, DesktopCommands.Match("ctrl+n"));
        Assert.Equal(DesktopCommands.OpenSettings, DesktopCommands.Match("ctrl+,"));
        Assert.Equal(DesktopCommands.OpenSettings, DesktopCommands.Match("ctrl+oemcomma"));
        Assert.Equal(DesktopCommands.ToggleTerminal, DesktopCommands.Match("ctrl+`"));
        Assert.Equal(DesktopCommands.ToggleTerminal, DesktopCommands.Match("ctrl+oemtilde"));
        Assert.Equal(DesktopCommands.ToggleSidebar, DesktopCommands.Match("ctrl+b"));
        Assert.Contains(DesktopCommands.DefaultBindings, b => b.Keys == "ctrl+k" && b.Action == DesktopCommands.OpenPalette);
        Assert.Contains(DesktopCommands.DefaultBindings, b => b.Keys == "ctrl+n" && b.Action == DesktopCommands.NewThread);
        Assert.Contains(DesktopCommands.DefaultBindings, b => b.Keys == "ctrl+," && b.Action == DesktopCommands.OpenSettings);
        Assert.Contains(DesktopCommands.DefaultBindings, b => b.Keys == "ctrl+`" && b.Action == DesktopCommands.ToggleTerminal);
        Assert.Contains(DesktopCommands.DefaultBindings, b => b.Keys == "ctrl+b" && b.Action == DesktopCommands.ToggleSidebar);
    }

    [Fact]
    public void Palette_lists_required_actions_and_filters_threads()
    {
        var items = DesktopCommands.CoreItems()
            .Concat([
                DesktopCommands.ThreadItem("thr_a", "Fix sandbox"),
                DesktopCommands.ThreadItem("thr_b", "Review panel"),
            ])
            .ToList();

        Assert.Contains(items, i => i.Kind == DesktopPaletteKind.NewThread);
        Assert.Contains(items, i => i.Kind == DesktopPaletteKind.OpenSettings);
        Assert.Contains(items, i => i.Kind == DesktopPaletteKind.FocusComposer);
        Assert.Contains(items, i => i.Kind == DesktopPaletteKind.JumpThread && i.ThreadId == "thr_a");

        var settings = DesktopCommands.Filter(items, "set");
        Assert.Contains(settings, i => i.Kind == DesktopPaletteKind.OpenSettings);
        Assert.DoesNotContain(settings, i => i.Kind == DesktopPaletteKind.NewThread);

        var threadHits = DesktopCommands.Filter(items, "sandbox");
        var hit = Assert.Single(threadHits);
        Assert.Equal("thr_a", hit.ThreadId);

        Assert.Equal(items.Count, DesktopCommands.Filter(items, " ").Count);
    }

    [Fact]
    public void Alt_number_switches_local_shell_mode()
    {
        Assert.Equal("codex", DesktopCommands.DefaultShellMode);
        Assert.Equal(DesktopCommands.ShellChat, DesktopCommands.Match("alt+1"));
        Assert.Equal(DesktopCommands.ShellChat, DesktopCommands.Match("alt+d1"));
        Assert.Equal(DesktopCommands.ShellWork, DesktopCommands.Match("alt+2"));
        Assert.Equal(DesktopCommands.ShellWork, DesktopCommands.Match("alt+d2"));
        Assert.Equal(DesktopCommands.ShellCodex, DesktopCommands.Match("alt+3"));
        Assert.Equal(DesktopCommands.ShellCodex, DesktopCommands.Match("alt+d3"));
        Assert.Contains(DesktopCommands.DefaultBindings, b => b.Keys == "alt+1" && b.Action == DesktopCommands.ShellChat);
        Assert.Contains(DesktopCommands.DefaultBindings, b => b.Keys == "alt+2" && b.Action == DesktopCommands.ShellWork);
        Assert.Contains(DesktopCommands.DefaultBindings, b => b.Keys == "alt+3" && b.Action == DesktopCommands.ShellCodex);
    }

    [Fact]
    public void Quick_and_temporary_chat_chords()
    {
        Assert.Equal(DesktopCommands.QuickChat, DesktopCommands.Match("ctrl+alt+n"));
        Assert.Equal(DesktopCommands.TemporaryChat, DesktopCommands.Match("ctrl+shift+n"));
        Assert.Equal(DesktopCommands.NewThread, DesktopCommands.Match("ctrl+n"));
        Assert.Contains(DesktopCommands.DefaultBindings, b => b.Keys == "ctrl+alt+n" && b.Action == DesktopCommands.QuickChat);
        Assert.Contains(DesktopCommands.DefaultBindings, b => b.Keys == "ctrl+shift+n" && b.Action == DesktopCommands.TemporaryChat);
    }
}
