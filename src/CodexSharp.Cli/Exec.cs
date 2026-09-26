using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexSharp.AppServer;
using CodexSharp.Client;
using CodexSharp.Protocol;
using CodexSharp.Runtime;
using Spectre.Console;

namespace CodexSharp.Cli;

internal static class Exec
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.ElementAtOrDefault(0) is "resume")
        {
            return await ResumeOneShotAsync(args.Skip(1).ToArray());
        }
        if (args.ElementAtOrDefault(0) is "fork")
        {
            return await ForkOneShotAsync(args.Skip(1).ToArray());
        }
        if (args.ElementAtOrDefault(0) is "review")
        {
            return await SessionCli.ReviewAsync(args.Skip(1).ToArray());
        }

        string? model = null;
        string? sandbox = null;
        string? approval = "never";
        string? profile = null;
        var json = false;
        string? lastMessagePath = null;
        string? cwd = null;
        string? schemaPath = null;
        var skipGit = false;
        var ephemeral = false;
        var worktree = false;
        var ignoreUserConfig = false;
        var ignoreRules = false;
        var strictConfig = false;
        var addDirs = new List<string>();
        string? color = null;
        var images = new List<string>();
        var promptParts = new List<string>();
        string? threadSource = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model":
                    model = args[++i];
                    break;
                case "--sandbox":
                    sandbox = args[++i];
                    break;
                case "--full-auto":
                    sandbox ??= "workspace-write";
                    approval = "never";
                    break;
                case "--yolo":
                    sandbox = "danger-full-access";
                    approval = "never";
                    break;
                case "--profile":
                    profile = args[++i];
                    break;
                case "--json":
                    json = true;
                    break;
                case "--output-last-message" or "-o":
                    lastMessagePath = args[++i];
                    break;
                case "--cd" or "-C":
                    cwd = args[++i];
                    break;
                case "--image":
                    images.Add(args[++i]);
                    break;
                case "--output-schema":
                    schemaPath = args[++i];
                    break;
                case "--skip-git-repo-check":
                    skipGit = true;
                    break;
                case "--ephemeral":
                    ephemeral = true;
                    break;
                case "--worktree":
                    worktree = true;
                    break;
                case "--ignore-user-config":
                    ignoreUserConfig = true;
                    break;
                case "--ignore-rules":
                    ignoreRules = true;
                    break;
                case "--strict-config":
                    strictConfig = true;
                    break;
                case "--add-dir":
                    addDirs.Add(args[++i]);
                    break;
                case "--color":
                    color = args[++i];
                    break;
                case "--thread-source":
                    threadSource = args[++i];
                    break;
                default:
                    promptParts.Add(args[i]);
                    break;
            }
        }

        var prompt = string.Join(' ', promptParts).Trim('"');
        if (prompt is "-" || (string.IsNullOrWhiteSpace(prompt) && Console.IsInputRedirected))
        {
            prompt = Console.In.ReadToEnd();
        }
        else if (!string.IsNullOrWhiteSpace(prompt) && Console.IsInputRedirected)
        {
            prompt += Environment.NewLine + "<stdin>" + Environment.NewLine + Console.In.ReadToEnd() + Environment.NewLine + "</stdin>";
        }
        if (string.IsNullOrWhiteSpace(prompt) && images.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]exec requires a prompt[/]");
            return 1;
        }

        if (string.Equals(color, "never", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;
        }

        var workdir = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : Path.GetFullPath(cwd);
        if (!skipGit && GitProbe.FindRoot(workdir) is null)
        {
            AnsiConsole.MarkupLine("[red]Not a git repository.[/] Re-run with [grey]--skip-git-repo-check[/] to override.");
            return 1;
        }

        ManagedWorktree? managed = null;
        if (worktree)
        {
            managed = WorktreeSession.Create(workdir);
            workdir = managed.Cwd;
            AnsiConsole.MarkupLine("[grey]worktree[/] " + Markup.Escape(managed.Root));
        }

        string? schemaJson = null;
        if (!string.IsNullOrWhiteSpace(schemaPath) && File.Exists(schemaPath))
        {
            schemaJson = File.ReadAllText(schemaPath);
        }
        prompt = ExecPrompt.Compose(prompt, images, schemaJson);
        ExecPolicy.SuppressRules.Value = ignoreRules;
        SessionSandbox.Reset();
        foreach (var dir in addDirs)
        {
            SessionSandbox.Add(dir);
        }
        await using var hosted = InProcessAppServer.Start();
        var app = new AppServerSession(hosted.Client);
        await app.InitializeAsync("codexsharp_exec", "CodexSharp exec");
        var source = string.IsNullOrWhiteSpace(threadSource) ? "exec" : threadSource;
        await app.StartThreadAsync(
            workdir,
            "exec",
            model: model,
            sandbox: sandbox,
            approvalPolicy: approval,
            source: source,
            profile: profile,
            ignoreUserConfig: ignoreUserConfig,
            strictConfig: strictConfig);
        if (hosted.TryGetSession(app.ThreadId, out var live))
        {
            live.Approver.Prompt = (_, _, _) => Task.FromResult(ApprovalDecision.Allow);
        }

        var lastAgent = await PumpExecTurnAsync(app, prompt, json);
        if (!string.IsNullOrWhiteSpace(lastMessagePath))
        {
            File.WriteAllText(lastMessagePath, lastAgent);
        }
        if (ephemeral)
        {
            await app.DeleteThreadAsync(app.ThreadId);
            if (managed is not null) WorktreeSession.Remove(managed);
        }
        return 0;
    }

    private static async Task<int> ResumeOneShotAsync(string[] args)
    {
        var o = SharedCli.Parse(args);
        var id = o.Last ? new JsonlThreadStore().List().FirstOrDefault()?.Id : o.Rest.ElementAtOrDefault(0);
        var prompt = o.Last ? string.Join(' ', o.Rest) : string.Join(' ', o.Rest.Skip(1));
        if (string.IsNullOrWhiteSpace(id))
        {
            AnsiConsole.MarkupLine("[yellow]No threads to resume.[/]");
            return 1;
        }
        if (string.IsNullOrWhiteSpace(prompt) && o.Images.Count == 0)
        {
            AnsiConsole.MarkupLine($"[grey]resumed[/] {id}");
            return 0;
        }
        return await OneShotAsync(id, prompt, o, fork: false);
    }

    private static async Task<int> ForkOneShotAsync(string[] args)
    {
        var o = SharedCli.Parse(args);
        var id = o.Rest.ElementAtOrDefault(0);
        var prompt = string.Join(' ', o.Rest.Skip(1));
        if (string.IsNullOrWhiteSpace(id))
        {
            AnsiConsole.MarkupLine("[red]exec fork requires a session id[/]");
            return 1;
        }
        await using var hosted = InProcessAppServer.Start();
        var app = new AppServerSession(hosted.Client);
        await app.InitializeAsync("codexsharp_exec", "CodexSharp exec");
        await app.ResumeThreadAsync(id);
        var childId = await app.ForkAsync();
        AnsiConsole.MarkupLine($"[green]forked[/] {childId} from {id}");
        if (string.IsNullOrWhiteSpace(prompt) && o.Images.Count == 0)
        {
            return 0;
        }
        return await OneShotAsync(childId, prompt, o, fork: false, existing: (hosted, app));
    }

    private static async Task<int> OneShotAsync(string threadId, string prompt, SharedCliOptions o, bool fork, (InProcessAppServer Hosted, AppServerSession App)? existing = null)
    {
        string? schemaJson = null;
        if (!string.IsNullOrWhiteSpace(o.SchemaPath) && File.Exists(o.SchemaPath))
        {
            schemaJson = File.ReadAllText(o.SchemaPath);
        }
        prompt = ExecPrompt.Compose(prompt, o.Images, schemaJson);
        if (existing is { } pair)
        {
            if (pair.Hosted.TryGetSession(pair.App.ThreadId, out var liveExisting))
            {
                liveExisting.Approver.Prompt = (_, _, _) => Task.FromResult(ApprovalDecision.Allow);
            }
            var lastExisting = await PumpExecTurnAsync(pair.App, prompt, o.Json);
            if (!string.IsNullOrWhiteSpace(o.LastMessagePath)) File.WriteAllText(o.LastMessagePath, lastExisting);
            if (o.Ephemeral) await pair.App.DeleteThreadAsync(pair.App.ThreadId);
            return 0;
        }

        await using var hosted = InProcessAppServer.Start();
        var app = new AppServerSession(hosted.Client);
        await app.InitializeAsync("codexsharp_exec", "CodexSharp exec");
        await app.ResumeThreadAsync(threadId);
        if (fork)
        {
            await app.ForkAsync();
        }
        if (hosted.TryGetSession(app.ThreadId, out var live))
        {
            live.Approver.Prompt = (_, _, _) => Task.FromResult(ApprovalDecision.Allow);
        }
        var lastAgent = await PumpExecTurnAsync(app, prompt, o.Json);
        if (!string.IsNullOrWhiteSpace(o.LastMessagePath)) File.WriteAllText(o.LastMessagePath, lastAgent);
        if (o.Ephemeral) await app.DeleteThreadAsync(app.ThreadId);
        return 0;
    }

    private static async Task<string> PumpExecTurnAsync(AppServerSession app, string prompt, bool json)
    {
        var lastAgent = "";
        long inputTokens = 0, outputTokens = 0;
        if (json)
        {
            Console.WriteLine(ExecJsonl.ThreadStarted(app.ThreadId));
        }
        void OnEvent(AgentEvent evt)
        {
            if (evt is AgentEvent.ItemCompleted done && done.Item.Kind == "agent_message" && !string.IsNullOrWhiteSpace(done.Item.Text))
            {
                lastAgent = done.Item.Text;
            }
            if (evt is AgentEvent.TokenUsage usage)
            {
                inputTokens = usage.Total;
                outputTokens = usage.Last;
            }
            if (json)
            {
                var line = ExecJsonl.Format(evt, inputTokens, outputTokens);
                if (!string.IsNullOrWhiteSpace(line))
                    Console.WriteLine(line);
                return;
            }
            switch (evt)
            {
                case AgentEvent.ItemDelta delta:
                    Console.Write(delta.Text);
                    break;
                case AgentEvent.ItemCompleted item when item.Item.Kind is "tool_call" or "command_execution":
                    Console.WriteLine();
                    AnsiConsole.MarkupLine($"[grey]{Markup.Escape(item.Item.ToolName)}[/] {Markup.Escape(Trim(item.Item.Text, 240))}");
                    break;
                case AgentEvent.Failed failed:
                    AnsiConsole.MarkupLine($"[red]{Markup.Escape(failed.Message)}[/]");
                    break;
                case AgentEvent.TurnCompleted:
                    Console.WriteLine();
                    break;
            }
        }

        app.Event += OnEvent;
        try
        {
            await app.RunTurnToCompletionAsync(prompt);
        }
        finally
        {
            app.Event -= OnEvent;
        }
        return lastAgent;
    }

    private static string Trim(string text, int n) =>
        string.IsNullOrEmpty(text) ? "" : text.Length <= n ? text : text[..n] + "...";
}
