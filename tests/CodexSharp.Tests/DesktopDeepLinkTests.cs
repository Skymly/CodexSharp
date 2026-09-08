using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public class DesktopDeepLinkTests
{
    [Fact]
    public void Parses_known_codexsharp_routes()
    {
        Assert.Equal(DeepLinkKind.NewThread, DesktopDeepLink.Parse("codexsharp://threads/new").Kind);
        var open = DesktopDeepLink.Parse("codexsharp://threads/thr_abc-1");
        Assert.Equal(DeepLinkKind.OpenThread, open.Kind);
        Assert.Equal("thr_abc-1", open.ThreadId);
        Assert.False(open.IgnoredUnknownPath);
        Assert.Equal(DeepLinkKind.Settings, DesktopDeepLink.Parse("codexsharp://settings").Kind);
        Assert.Equal(DeepLinkKind.Skills, DesktopDeepLink.Parse("codexsharp://skills").Kind);
        Assert.Equal("codexsharp://settings", DesktopDeepLink.FirstPayload(["--wait", "codexsharp://settings"]));
    }

    [Fact]
    public void Unknown_path_opens_main_and_is_ignored()
    {
        var pets = DesktopDeepLink.Parse("codexsharp://pets/install?id=x");
        Assert.Equal(DeepLinkKind.OpenMain, pets.Kind);
        Assert.True(pets.IgnoredUnknownPath);

        var plugins = DesktopDeepLink.Parse("codexsharp://plugins/install/foo");
        Assert.Equal(DeepLinkKind.OpenMain, plugins.Kind);
        Assert.True(plugins.IgnoredUnknownPath);

        var nested = DesktopDeepLink.Parse("codexsharp://threads/foo/bar");
        Assert.Equal(DeepLinkKind.OpenMain, nested.Kind);
        Assert.True(nested.IgnoredUnknownPath);

        var empty = DesktopDeepLink.Parse("");
        Assert.Equal(DeepLinkKind.OpenMain, empty.Kind);
        Assert.False(empty.IgnoredUnknownPath);
    }

    [Fact]
    public void Query_path_must_be_an_existing_directory_and_is_never_executed()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-deeplink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ok = DesktopDeepLink.Parse("codexsharp://threads/new?path=" + Uri.EscapeDataString(root));
            Assert.Equal(DeepLinkKind.NewThread, ok.Kind);
            Assert.Equal(Path.GetFullPath(root), ok.WorkspacePath);

            var relative = DesktopDeepLink.Parse("codexsharp://threads/new?path=not-absolute");
            Assert.Null(relative.WorkspacePath);

            var url = DesktopDeepLink.Parse("codexsharp://threads/new?path=" + Uri.EscapeDataString("https://example.com/evil"));
            Assert.Null(url.WorkspacePath);

            var missing = DesktopDeepLink.Parse("codexsharp://threads/new?path=" + Uri.EscapeDataString(Path.Combine(root, "missing")));
            Assert.Null(missing.WorkspacePath);

            var file = Path.Combine(root, "notes.txt");
            File.WriteAllText(file, "nope");
            var fileLink = DesktopDeepLink.Parse("codexsharp://settings?path=" + Uri.EscapeDataString(file));
            Assert.Null(fileLink.WorkspacePath);
            Assert.Equal(DeepLinkKind.Settings, fileLink.Kind);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Does_not_register_official_codex_scheme()
    {
        Assert.False(DesktopDeepLink.ShouldRegisterOfficialScheme(officialPresent: true));
        Assert.False(DesktopDeepLink.ShouldRegisterOfficialScheme(officialPresent: false));
        Assert.Equal("\"" + @"C:\Apps\CodexSharp.Desktop.exe" + "\" \"%1\"", DesktopDeepLink.OpenCommand(@"C:\Apps\CodexSharp.Desktop.exe"));
        Assert.Null(DesktopDeepLink.ResolveDesktopExe(processPath: @"C:\dotnet.exe", baseDirectory: Path.GetTempPath()));
    }
}
