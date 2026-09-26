using CodexSharp.Protocol;
using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public class LocalEnvSetupTests
{
    [Fact]
    public void Failed_setup_script_is_not_fake_success()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-setup-" + Guid.NewGuid().ToString("N"));
        var wt = Path.Combine(root, "wt");
        Directory.CreateDirectory(Path.Combine(root, ".codexsharp"));
        Directory.CreateDirectory(wt);
        File.WriteAllText(Path.Combine(root, ".codexsharp", "setup.cmd"), "echo boom>&2\r\nexit /b 7\r\n");
        var result = LocalEnvSetup.Run(root, root, wt, Config(root, "never"));
        Assert.True(result.Ran);
        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Log));
        Assert.Contains("setup.cmd", result.ScriptPath, StringComparison.OrdinalIgnoreCase);
        Directory.Delete(root, true);
    }

    [Fact]
    public void Missing_script_is_ok_without_pretending_to_run()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var result = LocalEnvSetup.Run(root, root, root, Config(root, "never"));
        Assert.False(result.Ran);
        Assert.True(result.Ok);
        Directory.Delete(root, true);
    }

    [Fact]
    public void Unapproved_setup_script_does_not_start_or_report_success()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-setup-" + Guid.NewGuid().ToString("N"));
        var wt = Path.Combine(root, "wt");
        var marker = Path.Combine(wt, "marker.txt");
        Directory.CreateDirectory(Path.Combine(root, ".codexsharp"));
        Directory.CreateDirectory(wt);
        File.WriteAllText(Path.Combine(root, ".codexsharp", "setup.cmd"), "echo ran>\"" + marker + "\"\r\nexit /b 0\r\n");
        var result = LocalEnvSetup.Run(root, root, wt, Config(root, "on-request"));
        Assert.False(result.Ran);
        Assert.False(result.Ok);
        Assert.Contains("approval required", result.Log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not started", result.Log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("denied", result.Log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isolated", result.Log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("official sandbox", result.Log, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(marker));
        Directory.Delete(root, true);
    }

    [Fact]
    public void Setup_workdir_outside_worktree_root_does_not_start()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-setup-" + Guid.NewGuid().ToString("N"));
        var tree = Path.Combine(root, "tree");
        var outside = Path.Combine(root, "outside");
        var marker = Path.Combine(outside, "marker.txt");
        Directory.CreateDirectory(Path.Combine(root, ".codexsharp"));
        Directory.CreateDirectory(tree);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(root, ".codexsharp", "setup.cmd"), "echo ran>\"" + marker + "\"\r\nexit /b 0\r\n");
        var result = LocalEnvSetup.Run(root, tree, outside, Config(root, "never"));
        Assert.False(result.Ran);
        Assert.False(result.Ok);
        Assert.Contains("path rejected", result.Log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not started", result.Log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("denied", result.Log, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(marker));
        Directory.Delete(root, true);
    }

    private static CodexConfig Config(string cwd, string approval)
    {
        var cfg = ConfigService.Load(cwd, sandboxOverride: "workspace-write", approvalOverride: approval, ignoreUserConfig: true);
        return new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, approval, "workspace-write", cfg.DeveloperInstructions, cfg.UserInstructions, cwd, false, cfg.MaxTurns);
    }
}
public class SystemFilePreviewTests
{
    [Fact]
    public void Extracts_office_paths_and_missing_file_is_path_only()
    {
        Assert.True(SystemFilePreview.IsOffice(@"C:\tmp\a.xlsx"));
        Assert.False(SystemFilePreview.IsOffice("notes.txt"));
        var hits = SystemFilePreview.Extract(@"see C:\work\out.pdf and ./deck.pptx");
        Assert.Contains(hits, h => h.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(hits, h => h.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase));
        var missing = Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".docx");
        var preview = SystemFilePreview.TryOpen(missing);
        Assert.False(preview.Opened);
        Assert.Contains(missing, preview.Path, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(preview.Error));
    }
}

public class WindowsSandboxCopyTests
{
    [Fact]
    public void Copy_never_claims_isolation_or_official_sandbox()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            var unset = WindowsSandbox.Describe(WindowsSandbox.Read());
            Assert.Contains("notConfigured", unset, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Job Object", unset, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("isolated", unset, StringComparison.OrdinalIgnoreCase);

            var elevated = WindowsSandbox.Describe(WindowsSandbox.Setup("elevated", null));
            Assert.Contains("notConfigured", elevated, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("elevated", elevated, StringComparison.OrdinalIgnoreCase);

            var unelev = WindowsSandbox.Setup("unelevated", Path.GetTempPath());
            var copy = WindowsSandbox.Describe(unelev);
            Assert.Contains("Job Object", copy, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("path policy", copy, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("isolated", copy, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("official Windows sandbox", copy, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
            try { Directory.Delete(home, true); } catch { }
        }
    }
}
