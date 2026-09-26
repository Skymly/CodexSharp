using System.Text.Json;
using CodexSharp.Protocol;
using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public class ShellPolicyTests
{
    [Fact]
    public async Task Shell_workdir_sibling_prefix_is_denied()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-c10-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(root, "workspace");
        var sibling = Path.Combine(root, "workspace-evil");
        var child = Path.Combine(cwd, "child");
        var previousHome = Environment.GetEnvironmentVariable("CODEXSHARP_HOME");
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", Path.Combine(root, "home"));
        try
        {
            Directory.CreateDirectory(cwd);
            Directory.CreateDirectory(sibling);
            Directory.CreateDirectory(child);
            var ws = Config("workspace-write", cwd);
            var full = Config("danger-full-access", cwd);

            await AssertDenied(ws, sibling);
            await AssertAllowed(ws, child);
            await AssertAllowed(full, sibling);

            var broker = new CommandExecBroker();
            var skipped = await Exec(broker, ws, sibling, skipSandbox: true);
            Assert.DoesNotContain("Sandbox denied workdir", skipped.Stdout + skipped.Stderr);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", previousHome);
            try { Directory.Delete(root, true); } catch { /* process may still be releasing the dir */ }
        }
    }

    [Fact]
    public void Workspace_write_cannot_read_auth_json()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-c10-secret-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var cwd = Path.Combine(root, "workspace");
        var previousHome = Environment.GetEnvironmentVariable("CODEXSHARP_HOME");
        var saved = SaveEnv("CODEXSHARP_API_KEY", "OPENAI_API_KEY", "CODEX_API_KEY", "MINIMAX", "MiniMax");
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(cwd);
            var skillDir = Path.Combine(home, "skills", "demo");
            Directory.CreateDirectory(skillDir);
            var auth = Path.Combine(home, "auth.json");
            var token = Path.Combine(home, "access_token");
            var key = Path.Combine(home, "id_rsa");
            var pem = Path.Combine(home, "server.pem");
            var skill = Path.Combine(skillDir, "SKILL.md");
            var note = Path.Combine(home, "notes.txt");
            var workspaceAuth = Path.Combine(cwd, "auth.json");
            File.WriteAllText(auth, "{\"api_key\":\"host-still-reads-SECRET_AUTH_NEEDLE\"}");
            File.WriteAllText(token, "SECRET_TOKEN_NEEDLE");
            File.WriteAllText(key, "SECRET_KEY_NEEDLE");
            File.WriteAllText(pem, "SECRET_PEM_NEEDLE");
            File.WriteAllText(skill, "SKILL_OK_NEEDLE");
            File.WriteAllText(note, "HOME_NOTE_OK");
            File.WriteAllText(workspaceAuth, "WORK_AUTH_OK");

            foreach (var name in saved.Keys.ToArray())
            {
                Environment.SetEnvironmentVariable(name, null);
            }

            var cfg = ConfigService.Load(cwd, sandboxOverride: "workspace-write", approvalOverride: "never", ignoreUserConfig: true);
            Assert.Equal(home, Path.GetFullPath(cfg.Home));
            Assert.Contains("SECRET_AUTH_NEEDLE", cfg.Provider.ApiKey);
            var sandbox = new WorkspaceSandbox(cfg);
            var files = new FileToolExecutor(sandbox);

            foreach (var secret in new[] { auth, token, key, pem })
            {
                var denied = Assert.Throws<InvalidOperationException>(() => sandbox.EnsureReadable(secret));
                Assert.Contains("secret", denied.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Throws<InvalidOperationException>(() => files.ReadFile(new ToolCallRequest("r", "read_file", JsonSerializer.Serialize(new { path = secret }))));
            }

            sandbox.EnsureReadable(skill);
            sandbox.EnsureReadable(note);
            var skillRead = files.ReadFile(new ToolCallRequest("s", "read_file", JsonSerializer.Serialize(new { path = skill })));
            Assert.Contains("SKILL_OK_NEEDLE", skillRead.Output);
            Assert.False(skillRead.IsError);
            var workspaceRead = files.ReadFile(new ToolCallRequest("w", "read_file", JsonSerializer.Serialize(new { path = workspaceAuth })));
            Assert.Contains("WORK_AUTH_OK", workspaceRead.Output);

            var grepHome = FileSearch.Grep(new ToolCallRequest("g", "grep_files", JsonSerializer.Serialize(new { pattern = "SECRET_", path = home })), sandbox);
            Assert.DoesNotContain("SECRET_AUTH_NEEDLE", grepHome.Output);
            Assert.DoesNotContain("SECRET_TOKEN_NEEDLE", grepHome.Output);
            Assert.DoesNotContain("SECRET_KEY_NEEDLE", grepHome.Output);
            Assert.DoesNotContain("SECRET_PEM_NEEDLE", grepHome.Output);
            var grepSkill = FileSearch.Grep(new ToolCallRequest("gs", "grep_files", JsonSerializer.Serialize(new { pattern = "SKILL_OK_NEEDLE", path = skillDir })), sandbox);
            Assert.Contains("SKILL_OK_NEEDLE", grepSkill.Output);
        }
        finally
        {
            foreach (var pair in saved)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }

            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", previousHome);
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    private static async Task AssertDenied(CodexConfig cfg, string workdir)
    {
        var marker = Path.Combine(workdir, "ran-" + Guid.NewGuid().ToString("N") + ".txt");
        var shell = new ShellExecutor(new WorkspaceSandbox(cfg), cfg);
        var result = await shell.RunAsync(ShellCall(marker, workdir), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Contains("Sandbox denied workdir", result.Output);
        Assert.False(File.Exists(marker));

        var broker = new CommandExecBroker();
        var execDenied = await Assert.ThrowsAsync<InvalidOperationException>(() => Exec(broker, cfg, workdir, skipSandbox: false, marker));
        Assert.Contains("Sandbox denied workdir", execDenied.Message);
        var spawnDenied = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            broker.SpawnYieldAsync("Set-Content -LiteralPath '" + marker + "' -Value ran", workdir, cfg, 500, false, null, CancellationToken.None));
        Assert.Contains("Sandbox denied workdir", spawnDenied.Message);
        Assert.False(File.Exists(marker));
    }

    private static async Task AssertAllowed(CodexConfig cfg, string workdir)
    {
        var shell = new ShellExecutor(new WorkspaceSandbox(cfg), cfg);
        var result = await shell.RunAsync(
            new ToolCallRequest("ok", "shell", JsonSerializer.Serialize(new { command = "exit 0", workdir, timeout_ms = 8000 })),
            CancellationToken.None);
        Assert.DoesNotContain("Sandbox denied workdir", result.Output);

        var broker = new CommandExecBroker();
        var exec = await Exec(broker, cfg, workdir, skipSandbox: false, marker: null);
        Assert.DoesNotContain("Sandbox denied", exec.Stdout + exec.Stderr);
        var spawned = await broker.SpawnYieldAsync("exit 0", workdir, cfg, 4000, false, null, CancellationToken.None);
        Assert.DoesNotContain("Sandbox denied", spawned.Output);
        if (spawned.Running)
        {
            broker.Terminate(spawned.SessionId);
        }
    }

    private static ToolCallRequest ShellCall(string marker, string workdir) =>
        new("deny", "shell", JsonSerializer.Serialize(new
        {
            command = "Set-Content -LiteralPath '" + marker + "' -Value ran",
            workdir,
            timeout_ms = 8000,
        }));

    private static Task<CommandExecResult> Exec(CommandExecBroker broker, CodexConfig cfg, string workdir, bool skipSandbox, string? marker = null)
    {
        var command = marker is null ? "exit 0" : "Set-Content -LiteralPath '" + marker + "' -Value ran";
        return broker.RunAsync(
            ["powershell.exe", "-NoLogo", "-NoProfile", "-Command", command],
            null,
            workdir,
            cfg,
            false,
            false,
            false,
            8000,
            4000,
            null,
            CancellationToken.None,
            skipSandbox: skipSandbox);
    }

    private static CodexConfig Config(string sandbox, string cwd) =>
        new(
            CodexPaths.Home,
            "gpt-4.1-mini",
            "medium",
            new ModelProvider("openai", "openai", "https://example.invalid", "OPENAI_API_KEY", "chat", ""),
            "never",
            sandbox,
            "",
            "",
            cwd,
            false,
            8);

    private static Dictionary<string, string?> SaveEnv(params string[] names)
    {
        var saved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            saved[name] = Environment.GetEnvironmentVariable(name);
        }

        return saved;
    }
}
