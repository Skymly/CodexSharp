using System.Text.Json;
using CodexSharp.Protocol;

namespace CodexSharp.Runtime;

/// `codex sandbox` host backends. Windows runs the command under Job Object + workspace-write.
/// macOS seatbelt / Linux landlock stay notConfigured.
public static class SandboxCommand
{
    public sealed record Result(int ExitCode, string Output, string Status);

    public static Result Run(string[] args, string? cwd = null)
    {
        cwd ??= Environment.CurrentDirectory;
        var verb = args.ElementAtOrDefault(0) ?? "status";
        if (verb is "status" || args.Length == 0)
        {
            var snap = WindowsSandbox.Read();
            return new Result(snap.Status == "ready" ? 0 : 1,
                $"status     {snap.Status}\njobObject  {snap.JobObject}\nmode       {snap.Mode}\n",
                snap.Status);
        }

        if (verb is "setup")
        {
            var mode = args.ElementAtOrDefault(1) ?? "unelevated";
            var snap = WindowsSandbox.Setup(mode, cwd);
            return new Result(snap.Status == "ready" ? 0 : 1,
                snap.Status + (string.IsNullOrWhiteSpace(snap.Error) ? "" : "\n" + snap.Error),
                snap.Status);
        }

        if (verb is "macos" or "seatbelt" or "linux" or "landlock")
        {
            return new Result(1, "notConfigured  that host sandbox is not implemented on this OS.", "notConfigured");
        }

        var commandArgs = args.AsEnumerable();
        if (verb is "windows") commandArgs = args.Skip(1);
        var parts = commandArgs.ToList();
        if (parts.Count > 0 && parts[0] == "--") parts.RemoveAt(0);
        if (parts.Count == 0)
        {
            return Run(["status"], cwd);
        }

        if (!OperatingSystem.IsWindows())
        {
            return new Result(1, "notConfigured  windows sandbox command runner is Windows-only.", "notConfigured");
        }

        var command = string.Join(' ', parts);
        var cfg = ConfigService.Load(cwd, sandboxOverride: "workspace-write", approvalOverride: "never");
        var sandbox = new WorkspaceSandbox(cfg);
        var exec = new ShellExecutor(sandbox, cfg);
        var call = new ToolCallRequest("sandbox", "shell", JsonSerializer.Serialize(new { command, workdir = cwd }));
        var ran = exec.RunAsync(call, CancellationToken.None).GetAwaiter().GetResult();
        return new Result(ran.IsError ? 1 : 0, ran.Output, ran.IsError ? "error" : "ok");
    }
}
