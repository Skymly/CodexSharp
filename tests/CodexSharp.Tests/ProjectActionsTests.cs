using CodexSharp.Protocol;
using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public class ProjectActionsTests
{
    [Fact]
    public void Actions_hidden_when_config_missing()
    {
        var root = NewRoot();
        var home = IsolateHome();
        try
        {
            var missing = ProjectActions.Load(root);
            Assert.Empty(missing.Buttons);
            Assert.False(missing.SuccessToast);

            Directory.CreateDirectory(Path.Combine(root, ".codexsharp"));
            File.WriteAllText(Path.Combine(root, ".codexsharp", "actions.toml"), "[[action]]\nlabel = \"Nope\"\nicon = \"play\"\n");
            var incomplete = ProjectActions.Load(root);
            Assert.Empty(incomplete.Buttons);
            Assert.False(incomplete.SuccessToast);

            File.WriteAllText(Path.Combine(root, ".codexsharp", "actions.toml"), "not toml [[[");
            var broken = ProjectActions.Load(root);
            Assert.Empty(broken.Buttons);
            Assert.False(broken.SuccessToast);
        }
        finally
        {
            Restore(home);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Action_click_runs_command_in_current_terminal()
    {
        var root = NewRoot();
        var home = IsolateHome();
        try
        {
            WriteAction(root, "Test", "echo hi");
            var loaded = ProjectActions.Load(root);
            Assert.Equal("Test", Assert.Single(loaded.Buttons).Label);
            Assert.False(loaded.SuccessToast);

            var tabs = DesktopTerminalTabs.Start().Open();
            Assert.Equal("ui-term-2", tabs.Active.Id);
            string? pid = null;
            IReadOnlyList<string>? argv = null;
            var click = ProjectActions.Click(
                tabs,
                root,
                root,
                Config(root, "never"),
                "echo hi",
                (processId, command) =>
                {
                    pid = processId;
                    argv = command;
                });

            Assert.True(click.Started);
            Assert.False(click.Success);
            Assert.Equal("ui-term-2", click.ProcessId);
            Assert.Equal("ui-term-2", pid);
            Assert.NotEqual("ui-term-1", pid);
            Assert.Equal(tabs.Active.ExecArgv("echo hi"), argv);
            Assert.Equal(tabs.Active.ExecArgv("echo hi"), click.Argv);
            Assert.Equal(2, tabs.All.Count);
            Assert.False(ToolApprovalNeeded(root, "echo hi", "never"));
        }
        finally
        {
            Restore(home);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Action_command_does_not_bypass_approval()
    {
        var root = NewRoot();
        var home = IsolateHome();
        var marker = Path.Combine(root, "ran.txt");
        var starterMarker = Path.Combine(root, "starter.txt");
        try
        {
            var command = "Set-Content -LiteralPath '" + marker.Replace('\\', '/') + "' -Value ran";
            WriteAction(root, "Test", command);
            Assert.True(ToolApprovalNeeded(root, command, "on-request"));
            var started = false;
            var click = ProjectActions.Click(
                DesktopTerminalTabs.Start(),
                root,
                root,
                Config(root, "on-request"),
                command,
                (_, _) =>
                {
                    started = true;
                    File.WriteAllText(starterMarker, "started");
                });

            Assert.False(click.Started);
            Assert.False(click.Success);
            Assert.False(started);
            Assert.Contains("approval required", click.TerminalText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not started", click.TerminalText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("success", click.TerminalText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("isolated", click.TerminalText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("official sandbox", click.TerminalText, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(marker));
            Assert.False(File.Exists(starterMarker));
        }
        finally
        {
            Restore(home);
            Directory.Delete(root, true);
        }
    }

    private static bool ToolApprovalNeeded(string cwd, string command, string approval)
    {
        var call = new ToolCallRequest("action", "shell", System.Text.Json.JsonSerializer.Serialize(new { command }));
        return CodexSharp.Core.ToolApproval.needsApproval(Config(cwd, approval), call);
    }

    private static void WriteAction(string root, string label, string command)
    {
        Directory.CreateDirectory(Path.Combine(root, ".codexsharp"));
        File.WriteAllText(
            Path.Combine(root, ".codexsharp", "actions.toml"),
            "[[action]]\nlabel = \"" + label + "\"\ncommand = \"" + command + "\"\nicon = \"play\"\n");
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-actions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string? IsolateHome()
    {
        var previous = Environment.GetEnvironmentVariable("CODEXSHARP_HOME");
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        return previous;
    }

    private static void Restore(string? previous)
    {
        var home = Environment.GetEnvironmentVariable("CODEXSHARP_HOME");
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", previous);
        if (!string.IsNullOrWhiteSpace(home) && Directory.Exists(home))
        {
            Directory.Delete(home, true);
        }
    }

    private static CodexConfig Config(string cwd, string approval)
    {
        var cfg = ConfigService.Load(cwd, sandboxOverride: "workspace-write", approvalOverride: approval, ignoreUserConfig: true);
        return new CodexConfig(cfg.Home, cfg.Model, cfg.ReasoningEffort, cfg.Provider, approval, "workspace-write", cfg.DeveloperInstructions, cfg.UserInstructions, cwd, false, cfg.MaxTurns);
    }
}
