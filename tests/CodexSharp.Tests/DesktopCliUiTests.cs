using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using CodexSharp.AppServer;
using CodexSharp.Client;
using CodexSharp.Core;
using CodexSharp.Protocol;
using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public class AppServerSidebarClientTests
{
    [Fact]
    public async Task LoadThreadsAsync_includes_pin_and_section()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var threadId = await session.StartThreadAsync(Path.GetTempPath(), "sidebar");
            await session.SetPinnedAsync(threadId, true);
            var created = await session.CreateSectionAsync("Desk");
            var sectionId = created.GetProperty("section").GetProperty("id").GetString();
            await session.MoveToSectionAsync(threadId, sectionId);
            var rows = await session.LoadThreadsAsync();
            var row = Assert.Single(rows, r => r.Id == threadId);
            Assert.True(row.Pinned);
            Assert.Equal(sectionId, row.SectionId);
            var sections = await session.LoadSectionsAsync();
            Assert.Contains(sections, s => s.Id == sectionId && s.Name == "Desk");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ConfigWriteClientTests
{
    [Fact]
    public async Task WriteConfigAsync_then_read_sees_personality()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            await session.WriteConfigAsync("personality", "friendly");
            await session.WriteConfigAsync("collaboration_mode", "plan");
            var read = await session.ReadConfigAsync();
            Assert.Equal("friendly", read.GetProperty("personality").GetString());
            Assert.Equal("plan", read.GetProperty("collaborationMode").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class DesktopChromeClientTests
{
    [Fact]
    public async Task LoadChromeAsync_returns_status_line()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var chrome = await session.LoadChromeAsync();
            Assert.False(string.IsNullOrWhiteSpace(chrome.Status));
            Assert.Contains("·", chrome.Status);
            Assert.False(string.IsNullOrWhiteSpace(chrome.SandboxNote));
            Assert.False(string.IsNullOrWhiteSpace(chrome.SideNotes));
            Assert.Contains("MCP", chrome.SideNotes);
            Assert.Equal("disabled", chrome.RemoteControlStatus);
            Assert.Contains("Remote control", chrome.SideNotes);
            Assert.False(string.IsNullOrWhiteSpace(chrome.RateLimitsNote));
            Assert.NotNull(chrome.TuiVim);
            Assert.NotNull(chrome.CollaborationMode);
            Assert.NotNull(chrome.Personality);
            var mentions = await session.MentionHitsAsync("hello");
            Assert.Empty(mentions);
            Assert.NotEmpty(chrome.Models);
            await session.LoginApiKeyAsync("sk-test-chrome");
            chrome = await session.LoadChromeAsync();
            Assert.DoesNotContain("no api key", chrome.Status);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ImageMessageContentTests
{
    [Fact]
    public void Embeds_png_as_data_url()
    {
        var path = Path.Combine(Path.GetTempPath(), "codexsharp-img-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        var text = "hello\nPlease inspect this image with view_image: " + path + "\n";
        var node = ImageMessageContent.ChatContent(text);
        Assert.Equal(JsonValueKind.Array, node.GetValueKind());
        var json = node.ToJsonString();
        Assert.Contains("image_url", json);
        Assert.Contains("data:image/png;base64,", json);
        Assert.Contains("hello", json);
        var resp = ImageMessageContent.ResponsesContent(text).ToJsonString();
        Assert.Contains("input_image", resp);
    }
}

public class ClipboardImageStoreTests
{
    [Fact]
    public void Saves_png_bytes_and_roundtrips_data_url()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var path = ClipboardImageStore.SavePng(png);
        Assert.True(File.Exists(path));
        Assert.True(ImageMessageContent.TryDataUrl(path, out var url));
        Assert.StartsWith("data:image/png;base64,", url);
        Assert.Equal(png, ClipboardImageStore.AsBytes(png));
    }
}

public class ExtractLocalPathsTests
{
    [Fact]
    public void Finds_markdown_path_eq_and_marker()
    {
        var path = Path.Combine(Path.GetTempPath(), "codexsharp-img-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        var text = "see ![shot](" + path + ")\npath=" + path + "\n" + ImageMessageContent.Marker + path;
        var hits = ImageMessageContent.ExtractLocalPaths(text);
        Assert.Contains(hits, p => p.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        Assert.True(hits.Count >= 1);
    }
}

public class ImageUrlCacheTests
{
    [Fact]
    public void Extracts_markdown_and_bare_https_images()
    {
        var text = "see ![a](https://example.com/a.png) and https://cdn.example/b.jpg?x=1";
        var urls = ImageUrlCache.ExtractRemoteUrls(text);
        Assert.Contains(urls, u => u.Contains("a.png"));
        Assert.Contains(urls, u => u.Contains("b.jpg"));
    }

    [Fact]
    public async Task Download_uses_injected_http_client()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var handler = new StubImageHandler(png);
        using var http = new HttpClient(handler);
        var path = await ImageUrlCache.DownloadAsync("https://example.invalid/tiny.png", http);
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.True(File.Exists(path!));
    }

    private sealed class StubImageHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return Task.FromResult(resp);
        }
    }
}

public class MarkdownTests
{
    [Fact]
    public void Parses_headings_fences_and_bullets()
    {
        var blocks = Markdown.Parse("""
            # Title
            hello **world**
            ```cs
            var x = 1;
            ```
            - one
            - two
            """);
        Assert.Contains(blocks, b => b is MdHeading h && h.Level == 1 && h.Text == "Title");
        Assert.Contains(blocks, b => b is MdCode c && c.Lang == "cs" && c.Text.Contains("var x = 1;"));
        Assert.Contains(blocks, b => b is MdBullet bullet && bullet.Text == "one");
        Assert.Equal("hello world", Markdown.StripInline("hello **world**"));
        Assert.Contains("var x = 1;", Markdown.LastCode("""
            intro
            ```
            first
            ```
            ```cs
            var x = 1;
            ```
            """)!);
    }
}

public class ComposerBufferTests
{
    [Fact]
    public void Inserts_kills_and_yanks_around_the_cursor()
    {
        var buf = new ComposerBuffer();
        buf.Insert("hello world");
        buf.MoveHome();
        buf.MoveRight();
        buf.MoveRight();
        buf.Insert("X");
        Assert.Equal("heXllo world", buf.Text);
        buf.MoveEnd();
        buf.KillWordBack();
        Assert.Equal("heXllo ", buf.Text);
        Assert.Equal("world", buf.Yank);
        buf.YankInsert();
        Assert.Equal("heXllo world", buf.Text);
        buf.Set("alpha\nbeta");
        buf.MoveHome();
        buf.KillToEnd();
        Assert.Equal("alpha\n", buf.Text);
        Assert.Equal("beta", buf.Yank);
        buf.Newline();
        buf.Insert("gamma");
        Assert.Contains("\ngamma", buf.Text);
        Assert.Contains("|", buf.DisplayWithCursor());
        buf.Set("one two three");
        buf.MoveTo(7);
        buf.KillWordBack();
        Assert.Equal("one  three", buf.Text);
        Assert.Equal(4, buf.Cursor);
        buf.Insert("Z");
        Assert.Contains("Z", buf.Text);
        Assert.True(buf.Undo());
        Assert.DoesNotContain("Z", buf.Text);
        var vim = new ComposerVim(buf) { Enabled = true };
        vim.SetMode(ComposerVimMode.Normal);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('r', ConsoleKey.R, false, false, true)));
        Assert.Contains("Z", buf.Text);
        buf.Set("aaa\nbb\nc");
        buf.MoveTo(6);
        buf.MoveUp();
        Assert.Equal(2, buf.Cursor);
        buf.MoveDown();
        Assert.Equal(6, buf.Cursor);
        vim.SetMode(ComposerVimMode.Normal);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('k', ConsoleKey.K, false, false, false)));
        Assert.Equal(2, buf.Cursor);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('j', ConsoleKey.J, false, false, false)));
        Assert.Equal(6, buf.Cursor);
    }
}

public class ComposerVimTests
{
    [Fact]
    public void Esc_enters_normal_mode_and_dd_kills_the_line()
    {
        var buf = new ComposerBuffer();
        buf.Set("hello world");
        var vim = new ComposerVim(buf) { Enabled = true };
        Assert.Equal(ComposerVimMode.Insert, vim.Mode);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false)));
        Assert.Equal(ComposerVimMode.Normal, vim.Mode);
        buf.MoveTo(0);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('w', ConsoleKey.W, false, false, false)));
        Assert.Equal(6, buf.Cursor);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('d', ConsoleKey.D, false, false, false)));
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('d', ConsoleKey.D, false, false, false)));
        Assert.Equal("", buf.Text);
        buf.Set("abc def");
        buf.MoveTo(0);
        vim.TryHandle(new ConsoleKeyInfo('d', ConsoleKey.D, false, false, false));
        vim.TryHandle(new ConsoleKeyInfo('w', ConsoleKey.W, false, false, false));
        Assert.Equal("def", buf.Text);
        vim.TryHandle(new ConsoleKeyInfo('i', ConsoleKey.I, false, false, false));
        Assert.Equal(ComposerVimMode.Insert, vim.Mode);
        Assert.False(vim.TryHandle(new ConsoleKeyInfo('z', ConsoleKey.Z, false, false, false)));
        var again = new ComposerVim(new ComposerBuffer()) { Enabled = true };
        Assert.True(again.TryHandle(new ComposerVimInput('\0', true, false, false, false, false, false, false, false, false, false)));
        Assert.Equal(ComposerVimMode.Normal, again.Mode);
    }

    [Fact]
    public void Gg_G_and_O_open_line_above()
    {
        var buf = new ComposerBuffer();
        buf.Set("ab\ncd");
        var vim = new ComposerVim(buf) { Enabled = true };
        vim.SetMode(ComposerVimMode.Normal);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('G', ConsoleKey.G, true, false, false)));
        Assert.Equal(buf.Text.Length, buf.Cursor);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('g', ConsoleKey.G, false, false, false)));
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('g', ConsoleKey.G, false, false, false)));
        Assert.Equal(0, buf.Cursor);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('O', ConsoleKey.O, true, false, false)));
        Assert.Equal(ComposerVimMode.Insert, vim.Mode);
        Assert.StartsWith("\n", buf.Text);
    }

    [Fact]
    public void Visual_dot_yank_change_and_find_persist_across_keypresses()
    {
        var buf = new ComposerBuffer();
        buf.Set("one two three");
        var vim = new ComposerVim(buf) { Enabled = true };
        vim.SetMode(ComposerVimMode.Normal);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('d', ConsoleKey.D, false, false, false)));
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('d', ConsoleKey.D, false, false, false)));
        Assert.Equal("", buf.Text);
        buf.Set("alpha beta");
        buf.MoveTo(0);
        vim.SetMode(ComposerVimMode.Normal);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('d', ConsoleKey.D, false, false, false)));
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('w', ConsoleKey.W, false, false, false)));
        Assert.Equal("beta", buf.Text);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('.', ConsoleKey.OemPeriod, false, false, false)));
        Assert.Equal("", buf.Text);

        buf.Set("hello world");
        buf.MoveTo(0);
        vim.SetMode(ComposerVimMode.Normal);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('v', ConsoleKey.V, false, false, false)));
        Assert.Equal(ComposerVimMode.Visual, vim.Mode);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('w', ConsoleKey.W, false, false, false)));
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('d', ConsoleKey.D, false, false, false)));
        Assert.Equal(ComposerVimMode.Normal, vim.Mode);
        Assert.Equal("world", buf.Text);

        buf.Set("abc def");
        buf.MoveTo(0);
        vim.SetMode(ComposerVimMode.Normal);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('y', ConsoleKey.Y, false, false, false)));
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('w', ConsoleKey.W, false, false, false)));
        Assert.Equal("abc def", buf.Text);
        Assert.Equal("abc ", buf.Yank);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('P', ConsoleKey.P, true, false, false)));
        Assert.StartsWith("abc ", buf.Text);

        buf.Set("find x here");
        buf.MoveTo(0);
        vim.SetMode(ComposerVimMode.Normal);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('f', ConsoleKey.F, false, false, false)));
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('x', ConsoleKey.X, false, false, false)));
        Assert.Equal(5, buf.Cursor);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('r', ConsoleKey.R, false, false, false)));
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('z', ConsoleKey.Z, false, false, false)));
        Assert.Contains("z", buf.Text);

        buf.Set("keep");
        buf.MoveTo(2);
        vim.SetMode(ComposerVimMode.Insert);
        buf.Adopt("keep-me", 7);
        Assert.True(buf.Undo());
        Assert.Equal("keep", buf.Text);
        Assert.True(buf.Redo());
        Assert.Equal("keep-me", buf.Text);
    }

    [Fact]
    public void Dot_replays_last_insert_body()
    {
        var buf = new ComposerBuffer();
        buf.Set("ab");
        buf.MoveTo(0);
        var vim = new ComposerVim(buf) { Enabled = true };
        vim.SetMode(ComposerVimMode.Normal);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('i', ConsoleKey.I, false, false, false)));
        Assert.Equal(ComposerVimMode.Insert, vim.Mode);
        buf.Insert("XY");
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('', ConsoleKey.Escape, false, false, false)));
        Assert.Equal(ComposerVimMode.Normal, vim.Mode);
        Assert.Equal("XYab", buf.Text);
        buf.MoveTo(buf.Text.Length);
        Assert.True(vim.TryHandle(new ConsoleKeyInfo('.', ConsoleKey.OemPeriod, false, false, false)));
        Assert.Equal("XYabXY", buf.Text);
    }
}

public class TuiKeymapAndPetsTests
{
    [Fact]
    public void Keymap_overlay_and_pet_persist()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Assert.Equal("enter", TuiKeymap.Load().First(b => b.Action == "submit").Keys);
            Assert.True(TuiKeymap.TrySet("composer", "submit", "ctrl+enter"));
            Assert.Equal("ctrl+enter", TuiKeymap.Load().First(b => b.Action == "submit").Keys);
            Assert.False(TuiKeymap.TrySet("composer", "nope", "x"));
            Assert.Equal("none", TuiPets.Current);
            Assert.True(TuiPets.TrySet("dewey"));
            Assert.Equal("dewey", TuiPets.Current);
            Assert.False(string.IsNullOrWhiteSpace(TuiPets.Ascii()));
            Assert.False(TuiPets.TrySet("not-a-pet"));
            Assert.Equal("ctrl+g", TuiKeymap.Load().First(b => b.Action == "open_external_editor").Keys);
            Assert.True(TuiKeymap.TrySet("global", "open_external_editor", "ctrl+e"));
            Assert.Equal("ctrl+e", TuiKeymap.Load().First(b => b.Action == "open_external_editor").Keys);
            Assert.Equal("shift+enter", TuiKeymap.Load().First(b => b.Action == "newline").Keys);
            Assert.Equal("alt+enter", TuiKeymap.Load().First(b => b.Action == "queue").Keys);
            Assert.Equal("ctrl+v", TuiKeymap.Load().First(b => b.Action == "paste").Keys);
            Assert.Equal("ctrl+w", TuiKeymap.Load().First(b => b.Action == "delete_backward_word").Keys);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Keymap_and_pets_go_through_app_server()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync("test", "Test");
            await session.SetKeymapAsync("composer", "submit", "ctrl+enter");
            var listed = await session.ListKeymapAsync();
            Assert.Contains(listed.GetProperty("data").EnumerateArray(), b =>
                b.GetProperty("action").GetString() == "submit" && b.GetProperty("keys").GetString() == "ctrl+enter");
            var pet = await session.SetPetsAsync("dewey");
            Assert.Equal("dewey", pet.GetProperty("id").GetString());
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.SetPetsAsync("not-a-pet"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ExternalEditorTests
{
    [Fact]
    public void Missing_visual_and_editor_is_an_error()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        var visual = Environment.GetEnvironmentVariable("VISUAL");
        var editor = Environment.GetEnvironmentVariable("EDITOR");
        try
        {
            Directory.CreateDirectory(home);
            Environment.SetEnvironmentVariable("VISUAL", null);
            Environment.SetEnvironmentVariable("EDITOR", null);
            var ex = Assert.Throws<InvalidOperationException>(() => ExternalEditor.Edit("draft"));
            Assert.Contains("neither VISUAL nor EDITOR", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
            Environment.SetEnvironmentVariable("VISUAL", visual);
            Environment.SetEnvironmentVariable("EDITOR", editor);
        }
    }

    [Fact]
    public void Prefers_visual_and_round_trips_draft()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        var visual = Environment.GetEnvironmentVariable("VISUAL");
        var editor = Environment.GetEnvironmentVariable("EDITOR");
        try
        {
            Directory.CreateDirectory(home);
            var script = Path.Combine(home, "edit.py");
            File.WriteAllText(script, "import sys\np=sys.argv[1]\nopen(p,'a',encoding='utf-8').write('\\nfrom-editor')\n");
            Environment.SetEnvironmentVariable("VISUAL", "python \"" + script + "\"");
            Environment.SetEnvironmentVariable("EDITOR", "should-not-run");
            var cmd = ExternalEditor.ResolveCommand();
            Assert.Equal("python", cmd[0]);
            Assert.Equal(script, cmd[1]);
            var result = ExternalEditor.Edit("hello");
            Assert.Contains("hello", result);
            Assert.Contains("from-editor", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
            Environment.SetEnvironmentVariable("VISUAL", visual);
            Environment.SetEnvironmentVariable("EDITOR", editor);
        }
    }
}

public class UiThemeTests
{
    [Fact]
    public void Light_aliases_are_light_everything_else_is_dark()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            CodexPaths.EnsureLayout();
            Assert.True(UiTheme.IsLight("light"));
            Assert.True(UiTheme.IsLight("day"));
            Assert.False(UiTheme.IsLight("dark"));
            Assert.False(UiTheme.IsLight("dim"));
            ConfigService.WriteUserKeys(new Dictionary<string, string> { ["tui_theme"] = "light" });
            Assert.True(UiTheme.IsLight());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class DesktopNotifyTests
{
    [Fact]
    public void Osc9_payload_strips_controls_and_off_is_silent()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            CodexPaths.EnsureLayout();
            var osc = DesktopNotify.Osc9("hi\nthere\u0007");
            Assert.StartsWith("\u001b]9;", osc);
            Assert.EndsWith("\u0007", osc);
            Assert.DoesNotContain("\n", DesktopNotify.Sanitize("a\nb"));
            ConfigService.WriteUserKeys(new Dictionary<string, string> { ["tui_notifications"] = "off" });
            DesktopNotify.Send("should not throw");
            Assert.Equal("off", DesktopNotify.Method);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class ResumePickerTests
{
    [Fact]
    public void Filters_cwd_and_exec_threads_unless_all()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var cwdA = Path.Combine(Path.GetTempPath(), "codexsharp-cwd-a-" + Guid.NewGuid().ToString("N"));
        var cwdB = Path.Combine(Path.GetTempPath(), "codexsharp-cwd-b-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(cwdA);
            Directory.CreateDirectory(cwdB);
            var a = CodexSession.Start(ConfigService.Load(cwdA), "alpha");
            var b = CodexSession.Start(ConfigService.Load(cwdB), "beta");
            var exec = CodexSession.Start(ConfigService.Load(cwdA), "exec-job");
            ThreadSources.Set(exec.Thread.Id, "exec");
            var here = ResumePicker.Candidates(all: false, cwd: cwdA);
            Assert.Contains(here, t => t.Id == a.Thread.Id);
            Assert.DoesNotContain(here, t => t.Id == b.Thread.Id);
            Assert.DoesNotContain(here, t => t.Id == exec.Thread.Id);
            var every = ResumePicker.Candidates(all: true, includeNonInteractive: true, cwd: cwdA);
            Assert.Contains(every, t => t.Id == b.Thread.Id);
            Assert.Contains(every, t => t.Id == exec.Thread.Id);
            var search = ResumePicker.Candidates(all: true, includeNonInteractive: true, cwd: cwdA, search: "beta");
            Assert.Contains(search, t => t.Id == b.Thread.Id);
            Assert.DoesNotContain(search, t => t.Id == a.Thread.Id);
            var label = ResumePicker.Format(a.Thread with { Pinned = true }, showCwd: true);
            Assert.StartsWith("* ", label);
            Assert.Equal(a.Thread.Id, ResumePicker.IdFromLabel(label));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class TurnNotifyTests
{
    [Fact]
    public void Loads_notify_command_from_config()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "config.toml"), "notify = [\"echo\", \"done\"]\n");
            var cmd = TurnNotify.LoadCommand();
            Assert.Equal(["echo", "done"], cmd);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class SharedCliTests
{
    [Fact]
    public void Parses_cd_model_image_and_last()
    {
        var o = SharedCli.Parse(["--cd", "C:\\repo", "--model", "gpt-4.1", "-i", "shot.png", "--last", "hello"]);
        Assert.Equal("C:\\repo", o.Cwd);
        Assert.Equal("gpt-4.1", o.Model);
        Assert.True(o.Last);
        Assert.Equal("shot.png", o.Images[0]);
        Assert.Equal(new[] { "hello" }, o.Rest);
    }
}

public class TerminalTitleTests
{
    [Fact]
    public void Sanitize_strips_controls_and_caps_length()
    {
        Assert.Equal("hello world", TerminalTitle.Sanitize("hello\n\tworld\u202e"));
        Assert.Equal("", TerminalTitle.Sanitize("\u0007\u0008"));
        var longTitle = new string('a', 400);
        Assert.Equal(TerminalTitle.MaxChars, TerminalTitle.Sanitize(longTitle).Length);
    }
}

public class DoctorProbeTests
{
    [Fact]
    public void Reports_sandbox_git_hooks_skills_execpolicy_writable_features()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(Path.GetTempPath(), "codexsharp-cwd-" + Guid.NewGuid().ToString("N"));
        var prevHome = Environment.GetEnvironmentVariable("CODEXSHARP_HOME");
        var prevKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var prevSharp = Environment.GetEnvironmentVariable("CODEXSHARP_API_KEY");
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        Environment.SetEnvironmentVariable("CODEXSHARP_API_KEY", null);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(cwd);
            Directory.CreateDirectory(Path.Combine(home, "hooks"));
            File.WriteAllText(Path.Combine(home, "hooks", "stop.json"), """{"name":"stop","event":"Stop","command":"echo"}""");
            var skillDir = Path.Combine(home, "skills", "demo");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# Demo" + Environment.NewLine);
            ExecPolicy.AddUserRule(["rm"], ExecDecision.Forbidden);
            FeatureFlags.Set("web_search", true);
            SandboxRoots.Add(cwd);

            var report = DoctorProbe.Run(cwd);
            Assert.Contains(report.Checks, c => c.Id == "sandbox" && c.Details.Any(d => d.Contains("workspace-write", StringComparison.OrdinalIgnoreCase)));
            Assert.Contains(report.Checks, c => c.Id == "git" && c.Summary.Contains("not a git repo", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(report.Checks, c => c.Id == "hooks" && c.Details.Any(d => d.Contains("stop")));
            Assert.Contains(report.Checks, c => c.Id == "skills" && c.Details.Any(d => d.Contains("demo")));
            Assert.Contains(report.Checks, c => c.Id == "execpolicy" && c.Details.Any(d => d.Contains("rm")));
            Assert.Contains(report.Checks, c => c.Id == "writable" && c.Details.Any(d => d.Contains(cwd, StringComparison.OrdinalIgnoreCase)));
            Assert.Contains(report.Checks, c => c.Id == "features" && c.Details.Any(d => d.Contains("web_search")));
            Assert.Contains(report.Checks, c => c.Id == "apps" && c.Details.Any(d => d.Contains("computerUse: notConfigured")) && c.Details.Any(d => d.Contains("browserUse: notConfigured")));
            var json = DoctorProbe.ToJson(report);
            Assert.Contains("\"id\": \"sandbox\"", json);
            Assert.Contains("\"id\": \"features\"", json);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", prevHome);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", prevKey);
            Environment.SetEnvironmentVariable("CODEXSHARP_API_KEY", prevSharp);
        }
    }
}

public class FeedbackUploadTests
{
    [Fact]
    public async Task Saves_local_snapshot_without_api_key()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "config.toml"), "model = \"x\"\napi_key = \"sk-secret\"\n");
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var result = await session.UploadFeedbackAsync("bug", "repro");
            var id = result.GetProperty("threadId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(id));
            var path = Path.Combine(home, "feedback", id + ".json");
            Assert.True(File.Exists(path));
            var json = File.ReadAllText(path);
            Assert.Contains("bug", json);
            Assert.DoesNotContain("sk-secret", json);
            Assert.Contains("***", json);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class RemoteControlProtocolTests
{
    [Fact]
    public async Task Status_requires_experimental_api()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CallAsync("remoteControl/status/read"));
        Assert.Contains("experimentalApi", ex.Message);
    }

    [Fact]
    public async Task Enable_reports_errored_without_remote_transport()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            await hosted.Client.CallAsync("remoteControl/enable");
            var status = await hosted.Client.CallAsync("remoteControl/status/read");
            Assert.Equal("errored", status.GetProperty("status").GetString());
            Assert.Equal("codexsharp", status.GetProperty("serverName").GetString());
            await hosted.Client.CallAsync("remoteControl/disable");
            status = await hosted.Client.CallAsync("remoteControl/status/read");
            Assert.Equal("disabled", status.GetProperty("status").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public async Task Pairing_start_never_claims_without_transport()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            await using var hosted = InProcessAppServer.Start();
            var session = new AppServerSession(hosted.Client);
            await session.InitializeAsync();
            var started = await hosted.Client.CallAsync("remoteControl/pairing/start", new { manualCode = true });
            var code = started.GetProperty("pairingCode").GetString();
            var manual = started.GetProperty("manualPairingCode").GetString();
            Assert.False(string.IsNullOrWhiteSpace(code));
            Assert.False(string.IsNullOrWhiteSpace(manual));
            Assert.True(started.GetProperty("expiresAt").GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var status = await hosted.Client.CallAsync("remoteControl/pairing/status", new { pairingCode = code });
            Assert.False(status.GetProperty("claimed").GetBoolean());
            var both = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                hosted.Client.CallAsync("remoteControl/pairing/status", new { pairingCode = code, manualPairingCode = manual }));
            Assert.Contains("not both", both.Message);
            var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                hosted.Client.CallAsync("remoteControl/pairing/status", new { }));
            Assert.Contains("requires pairingCode", missing.Message);
            var clients = await hosted.Client.CallAsync("remoteControl/client/list", new { environmentId = started.GetProperty("environmentId").GetString() });
            Assert.Empty(clients.GetProperty("data").EnumerateArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

}

public class HooksListProtocolTests
{
    [Fact]
    public async Task Lists_hook_json_files()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            var dir = Path.Combine(home, "hooks");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "stop.json"), """{"name":"stop-hook","event":"Stop","command":"echo done"}""");
            await using var hosted = InProcessAppServer.Start();
            var client = hosted.Client;
            await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
            await client.NotifyAsync("initialized");
            var listed = await client.CallAsync("hooks/list");
            Assert.Contains(listed.GetProperty("data").EnumerateArray(), e => e.GetProperty("name").GetString() == "stop-hook");
            var req = await client.CallAsync("configRequirements/read");
            Assert.True(req.TryGetProperty("requirements", out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

public class HookRunnerTests
{
    [Fact]
    public void Stop_hook_runs_command()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        var marker = Path.Combine(Path.GetTempPath(), "codexsharp-hook-" + Guid.NewGuid().ToString("N") + ".txt");
        Directory.CreateDirectory(Path.Combine(home, "hooks"));
        File.WriteAllText(
            Path.Combine(home, "hooks", "stop.json"),
            JsonSerializer.Serialize(new { name = "stop", @event = "Stop", command = "echo hooked>" + marker }));
        var results = HookRunner.Run(home, "Stop", new { threadId = "thr_x" });
        Assert.True(results.Count > 0);
        Assert.True(File.Exists(marker), results[0].Output);
        Assert.Contains("hooked", File.ReadAllText(marker), StringComparison.OrdinalIgnoreCase);
    }
}

public class AppsReadTests
{
    [Fact]
    public async Task Returns_requested_ids()
    {
        await using var hosted = InProcessAppServer.Start();
        var client = hosted.Client;
        await client.CallAsync("initialize", new { clientInfo = new { name = "t", title = "t", version = "0" } });
        var listed = await client.CallAsync("apps/read", new { appIds = new[] { "browser", "browser" } });
        Assert.Equal(1, listed.GetProperty("data").GetArrayLength());
        Assert.Equal("browser", listed.GetProperty("data")[0].GetProperty("id").GetString());
    }
}

public class RolloutMigrateTests
{
    [Fact]
    public void Inspect_and_apply_drop_invalid_jsonl_lines()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            CodexPaths.EnsureLayout();
            var path = Path.Combine(CodexPaths.SessionsDir, "thr_migrate.jsonl");
            File.WriteAllText(path, "{\"thread\":{\"id\":\"thr_migrate\",\"title\":\"t\"}}\nnot-json\n{\"ok\":true}\n");
            var dry = RolloutMigrate.Inspect(["thr_migrate"], apply: false);
            Assert.False(dry.Applied);
            Assert.False(dry.SqliteThreadHistory);
            Assert.Equal("jsonl", dry.Storage);
            Assert.Equal(1, dry.Threads);
            Assert.Equal(1, dry.InvalidLines);
            var applied = RolloutMigrate.Inspect(["thr_migrate"], apply: true);
            Assert.True(applied.Applied);
            Assert.Equal(0, applied.InvalidLines);
            Assert.Equal(2, applied.Lines);
            Assert.True(File.Exists(path + ".bak"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }
}

