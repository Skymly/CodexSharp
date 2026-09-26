using System.Diagnostics;
using System.Text.Json;
using CodexSharp.Core;
using CodexSharp.Protocol;

namespace CodexSharp.Runtime;

public sealed record SetupScriptResult(string? ScriptPath, bool Ran, bool Ok, string Log);

/// Windows setup scripts from .codex / .codexsharp (TERM-04). Failures are not fake success.
public static class LocalEnvSetup
{
    public static readonly string[] Names =
    [
        Path.Combine(".codexsharp", "windows", "setup.ps1"),
        Path.Combine(".codexsharp", "setup.ps1"),
        Path.Combine(".codexsharp", "setup.cmd"),
        Path.Combine(".codexsharp", "setup.bat"),
        Path.Combine(".codex", "windows", "setup.ps1"),
        Path.Combine(".codex", "setup.ps1"),
        Path.Combine(".codex", "setup.cmd"),
        Path.Combine(".codex", "setup.bat"),
    ];

    public static string? FindWindowsScript(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
        {
            return null;
        }

        foreach (var rel in Names)
        {
            var path = Path.Combine(projectRoot, rel);
            if (File.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }

        return null;
    }

    public static SetupScriptResult Run(string projectRoot, string worktreeRoot, string worktreeCwd, CodexConfig config)
    {
        var script = FindWindowsScript(projectRoot);
        if (script is null)
        {
            return new SetupScriptResult(null, false, true, "");
        }

        if (!TryResolve(projectRoot, out var projectFull)
            || !WorkspaceSandbox.IsInside(script, projectFull)
            || IsSecret(script, config))
        {
            return NotStarted(script, "path rejected; setup script was not started");
        }

        if (!TryResolve(worktreeRoot, out var treeFull)
            || !TryResolve(worktreeCwd, out var workdir)
            || !WorkspaceSandbox.IsInside(workdir, treeFull)
            || IsSecret(workdir, config))
        {
            return NotStarted(script, "path rejected; setup script was not started");
        }

        var argv = ScriptArgv(script);
        var command = string.Join(' ', argv);
        var call = new ToolCallRequest("setup", "shell", JsonSerializer.Serialize(new { command }));
        if (config is null || ToolApproval.needsApproval(config, call))
        {
            return NotStarted(script, "approval required; setup script was not started");
        }

        Directory.CreateDirectory(workdir);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = argv[0],
                WorkingDirectory = workdir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            for (var i = 1; i < argv.Count; i++)
            {
                psi.ArgumentList.Add(argv[i]);
            }

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return new SetupScriptResult(script, true, false, "failed to start setup script");
            }

            if (!proc.WaitForExit(30_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return new SetupScriptResult(script, true, false, "setup script timed out");
            }

            var log = (proc.StandardOutput.ReadToEnd() + Environment.NewLine + proc.StandardError.ReadToEnd()).Trim();
            var ok = proc.ExitCode == 0;
            if (!ok && log.Length == 0)
            {
                log = "setup script exited " + proc.ExitCode;
            }

            return new SetupScriptResult(script, true, ok, log);
        }
        catch (Exception ex)
        {
            return new SetupScriptResult(script, true, false, ex.Message);
        }
    }

    private static SetupScriptResult NotStarted(string script, string log) =>
        new(script, false, false, log);

    private static bool TryResolve(string? path, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        full = Path.GetFullPath(path);
        return true;
    }

    private static bool IsSecret(string path, CodexConfig? config) =>
        config is not null && WorkspaceSandbox.IsHomeSecret(path, config.Home);

    private static IReadOnlyList<string> ScriptArgv(string script)
    {
        if (script.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            return ["powershell.exe", "-NoProfile", "-NonInteractive", "-File", script];
        }

        return ["cmd.exe", "/c", script];
    }
}
