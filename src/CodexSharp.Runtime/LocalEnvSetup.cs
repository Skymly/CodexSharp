using System.Diagnostics;

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

    public static SetupScriptResult Run(string projectRoot, string worktreeCwd)
    {
        var script = FindWindowsScript(projectRoot);
        if (script is null)
        {
            return new SetupScriptResult(null, false, true, "");
        }

        worktreeCwd = Path.GetFullPath(string.IsNullOrWhiteSpace(worktreeCwd) ? projectRoot : worktreeCwd);
        Directory.CreateDirectory(worktreeCwd);
        try
        {
            var psi = new ProcessStartInfo
            {
                WorkingDirectory = worktreeCwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (script.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = "powershell.exe";
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-File");
                psi.ArgumentList.Add(script);
            }
            else
            {
                psi.FileName = "cmd.exe";
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(script);
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
}
