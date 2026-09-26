using System.Text.Json;
using CodexSharp.Core;
using CodexSharp.Protocol;
using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public class ExecApprovalTests
{
    [Fact]
    public async Task Exec_policy_prompt_requests_approval_and_does_not_run()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-c10-prompt-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var cwd = Path.Combine(root, "workspace");
        var previousHome = Environment.GetEnvironmentVariable("CODEXSHARP_HOME");
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(cwd);
            ExecPolicy.AddUserRule(["Set-Content"], ExecDecision.Prompt);
            var marker = Path.Combine(cwd, "ran.txt");
            var command = "Set-Content -LiteralPath '" + marker.Replace("'", "''") + "' -Value ran";
            var denied = await RunShell(cwd, "on-request", command, allow: false);
            Assert.Contains(denied.Events, e => e is AgentEvent.ApprovalNeeded);
            var completed = denied.Events.OfType<AgentEvent.ItemCompleted>().Single(e => e.Item.Text.Contains("Approval denied"));
            Assert.Equal("denied", completed.Item.Status);
            Assert.Contains("Approval denied:", completed.Item.Text);
            Assert.DoesNotContain("Exec policy requires approval", completed.Item.Text);
            Assert.False(File.Exists(marker));

            var allowed = await RunShell(cwd, "never", command, allow: false);
            Assert.DoesNotContain(allowed.Events, e => e is AgentEvent.ApprovalNeeded);
            Assert.DoesNotContain(allowed.Events.OfType<AgentEvent.ItemCompleted>(), e => e.Item.Text.Contains("Approval denied"));
            Assert.True(File.Exists(marker));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", previousHome);
            try { Directory.Delete(root, true); } catch { /* process may still release the marker */ }
        }
    }

    [Fact]
    public async Task Network_claim_matches_enforcement()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexsharp-c10-net-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var cwd = Path.Combine(root, "workspace");
        var previousHome = Environment.GetEnvironmentVariable("CODEXSHARP_HOME");
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(cwd);
            var ask = Config(cwd, "on-request", network: false);
            Assert.True(ToolApproval.needsApproval(ask, Call("shell", "echo hi")));
            Assert.True(ToolApproval.needsApproval(ask, Call("exec_command", "echo hi")));
            Assert.False(ToolApproval.needsApproval(ask, Call("write_file", "echo hi")));

            var marker = Path.Combine(cwd, "net-ran.txt");
            var command = "Set-Content -LiteralPath '" + marker.Replace("'", "''") + "' -Value ran";
            var denied = await RunShell(cwd, "on-request", command, allow: false);
            Assert.Contains(denied.Events, e => e is AgentEvent.ApprovalNeeded);
            Assert.False(File.Exists(marker));
            var execDenied = await RunTool(cwd, "on-request", "exec_command", JsonSerializer.Serialize(new { cmd = command }), allow: false);
            Assert.Contains(execDenied.Events, e => e is AgentEvent.ApprovalNeeded);
            Assert.Contains(execDenied.Events.OfType<AgentEvent.ItemCompleted>(), e => e.Item.Status == "denied" && e.Item.Text.Contains("Approval denied:"));

            var never = Config(cwd, "never", network: false);
            Assert.False(ToolApproval.needsApproval(never, Call("shell", command)));
            Assert.False(ToolApproval.needsApproval(never, Call("exec_command", command)));
            var ran = await RunShell(cwd, "never", command, allow: false);
            Assert.DoesNotContain(ran.Events, e => e is AgentEvent.ApprovalNeeded);
            Assert.True(File.Exists(marker));

            var prompt = Prompt.permissions(ask);
            var neverPrompt = Prompt.permissions(never);
            Assert.Contains("does not intercept network", prompt);
            Assert.Contains("does not intercept network", neverPrompt);
            Assert.DoesNotContain("denied", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("isolated", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("denied", neverPrompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("isolated", neverPrompt, StringComparison.OrdinalIgnoreCase);

            var report = DoctorProbe.Run(cwd);
            var sandbox = report.Checks.Single(c => c.Id == "sandbox");
            var joined = string.Join("\n", sandbox.Details) + "\n" + sandbox.Summary;
            Assert.Contains("does not intercept network", joined);
            Assert.DoesNotContain("denied", joined, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("isolated", joined, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", previousHome);
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    private static ToolCallRequest Call(string name, string command) =>
        new("c", name, JsonSerializer.Serialize(new { command, cmd = command }));

    private static CodexConfig Config(string cwd, string approval, bool network) =>
        new(
            CodexPaths.Home,
            "gpt-4.1-mini",
            "medium",
            new ModelProvider("openai", "openai", "https://example.invalid", "OPENAI_API_KEY", "chat", ""),
            approval,
            "workspace-write",
            "",
            "",
            cwd,
            network,
            8);

    private static Task<RecordingSink> RunShell(string cwd, string approval, string command, bool allow) =>
        RunTool(cwd, approval, "shell", JsonSerializer.Serialize(new { command }), allow);

    private static async Task<RecordingSink> RunTool(string cwd, string approval, string name, string args, bool allow)
    {
        var cfg = Config(cwd, approval, network: false);
        var model = new ScriptedModelClient(
            [
                ModelStreamEvent.NewToolCallReady(new ToolCallRequest("c1", name, args)),
                ModelStreamEvent.NewStreamFinished("tool"),
            ],
            [
                ModelStreamEvent.NewOutputTextDelta("done"),
                ModelStreamEvent.NewStreamFinished("stop"),
            ]);
        var sink = new RecordingSink();
        var deps = new AgentDeps(cfg, model, new BuiltinToolExecutor(cfg), new AutoApprover(allow), sink, [], new NoopHookHost(), new NoopUserInputHost(), new NoopSteerHost(), "", "");
        await AgentLoop.runTurn(deps, "thr", [], "run", CancellationToken.None);
        return sink;
    }
}
