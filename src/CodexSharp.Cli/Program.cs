using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexSharp.AppServer;
using CodexSharp.Client;
using CodexSharp.Protocol;
using CodexSharp.Runtime;
using Spectre.Console;

namespace CodexSharp.Cli;

internal static partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0)
        {
            return await Tui.RunAsync(null);
        }

        return args[0] switch
        {
            "--help" or "-h" or "help" => Help(),
            "--version" or "-V" or "version" => Version(),
            "exec" => await Exec.RunAsync(args.Skip(1).ToArray()),
            "app-server" => await RunAppServerAsync(args.Skip(1).ToArray()),
            "app" or "desktop" => LaunchDesktop(),
            "resume" => await Tui.ResumeAsync(args.Skip(1).ToArray()),
            "fork" => await SessionCli.ForkAsync(args.Skip(1).ToArray()),
            "archive" => await SessionCli.ArchiveAsync(args.Skip(1).ToArray()),
            "unarchive" => await SessionCli.UnarchiveAsync(args.Skip(1).ToArray()),
            "delete" => await SessionCli.DeleteAsync(args.Skip(1).ToArray()),
            "apply" => await SessionCli.ApplyAsync(args.Skip(1).ToArray()),
            "review" => await SessionCli.ReviewAsync(args.Skip(1).ToArray()),
            "cloud" or "cloud-tasks" => SessionCli.Cloud(),
            "queue" => await SessionCli.QueueAsync(args.Skip(1).ToArray()),
            "agents" => await SessionCli.AgentsAsync(),
            "completion" => SessionCli.Completion(args.Skip(1).ToArray()),
            "remote-control" => await SessionCli.RemoteControlAsync(args.Skip(1).ToArray()),
            "debug" => await DebugCli.RunAsync(args.Skip(1).ToArray()),
            "login" => await Login(args.Skip(1).ToArray()),
            "logout" => await LogoutAsync(),
            "account" => await AccountAsync(),
            "features" => await FeaturesAsync(args.Skip(1).ToArray()),
            "models" => await ModelsAsync(),
            "mcp" => await McpCmdAsync(args.Skip(1).ToArray()),
            "skills" => await SkillsCmdAsync(args.Skip(1).ToArray()),
            "plugins" => await PluginCliAsync(["list"]),
            "plugin" => await PluginCliAsync(args.Skip(1).ToArray()),
            "marketplace" => await MarketplaceCliAsync(args.Skip(1).ToArray()),
            "project" or "projects" => await ProjectCliAsync(args.Skip(1).ToArray()),
            "doctor" => await DoctorAsync(args.Skip(1).ToArray()),
            "sandbox" => SandboxCli(args.Skip(1).ToArray()),
            "execpolicy" => await ExecpolicyCli.RunAsync(args.Skip(1).ToArray()),
            "update" => UpdateCli.Run(),
            "exec-server" => await ExecServerCli.RunAsync(args.Skip(1).ToArray()),
            "migrate-rollouts" => MigrateRolloutsCli.Run(args.Skip(1).ToArray()),
            _ => await LaunchInteractive(args),
        };
    }
}

internal static class Tui
{
    private static string LastAgent = "";
    private static readonly List<string> PromptHistory = [];
    private static readonly List<LiveTui.Cell> LiveHistory = [];
    private static bool UseLive;
    private static TuiAgent? LiveSession;
    private static readonly string[] SlashCommands =
        ("/q /quit /exit /help /clear /stop /experimental /doctor /usage /pwd /execpolicy /plugins /prompts /subagents /memory /memories /resume /agents /app /apply /export /recap /mention /debug-config /ps /apps /ide /rollout /raw /statusline /title /theme /notifications /vim /keymap /pets /pet /cloud /cloud-environment /setup-default-sandbox /approve /side /btw /sandbox-add-read-dir /plan /default /pair /cd /init /logout /copy /import /delete /worktree /status /model /approvals /permissions /personality /sandbox /exec /new /compact /fork /pin /archive /search /rollback /skills /hooks /feedback /diff /review /effort /name /rename /goal /mcp /history /queue").Split(' ', StringSplitOptions.RemoveEmptyEntries);
    public static async Task<int> RunAsync(string? opening, CodexConfig? config = null) => await RunSessionAsync(await TuiAgent.StartAsync(config), opening);

    public static async Task<int> ResumeAsync(params string[] args)
    {
        var all = args.Any(a => a is "--all");
        var includeNonInteractive = args.Any(a => a is "--include-non-interactive");
        var last = args.Any(a => a is "--last" or "-l");
        string? search = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--search" or "-s") search = args[i + 1];
        }

        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--search", "-s" };
        if (!string.IsNullOrWhiteSpace(search)) skip.Add(search);
        var id = args.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a) && !a.StartsWith('-') && !skip.Contains(a));
        var threads = ResumePicker.Candidates(all, includeNonInteractive, Environment.CurrentDirectory, search);
        if (last && threads.Count > 0)
        {
            id = threads[0].Id;
        }
        if (string.IsNullOrWhiteSpace(id))
        {
            if (threads.Count == 0)
            {
                AnsiConsole.MarkupLine(all
                    ? "[yellow]No threads to resume.[/]"
                    : "[yellow]No threads in this directory.[/] Try [grey]codexsharp resume --all[/]");
                return 1;
            }

            if (threads.Count == 1 || !AnsiConsole.Profile.Capabilities.Interactive)
            {
                id = threads[0].Id;
            }
            else
            {
                if (threads.Count > 8 && string.IsNullOrWhiteSpace(search))
                {
                    var filter = AnsiConsole.Prompt(new TextPrompt<string>("Filter (empty = all):").AllowEmpty());
                    if (!string.IsNullOrWhiteSpace(filter))
                    {
                        threads = ResumePicker.Candidates(all, includeNonInteractive, Environment.CurrentDirectory, filter);
                        if (threads.Count == 0)
                        {
                            AnsiConsole.MarkupLine("[yellow]No matching threads.[/]");
                            return 1;
                        }
                    }
                }

                var labels = threads.Take(30).Select(thread => ResumePicker.Format(thread, showCwd: all)).ToList();
                var picked = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Resume which thread?").AddChoices(labels));
                id = ResumePicker.IdFromLabel(picked);
            }
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            AnsiConsole.MarkupLine("[red]thread id required[/]");
            return 1;
        }

        return await RunSessionAsync(await TuiAgent.ResumeAsync(id), null);
    }

    private static async Task<int> RunSessionAsync(TuiAgent session, string? opening)
    {
        await using var _owned = session;
        if (string.IsNullOrWhiteSpace(session.Config.Provider.ApiKey))
        {
            AnsiConsole.MarkupLine("[yellow]No API key configured.[/] Set CODEXSHARP_API_KEY or paste one now.");
            var key = AnsiConsole.Prompt(new TextPrompt<string>("API key:").Secret().AllowEmpty());
            if (!string.IsNullOrWhiteSpace(key))
            {
                AuthService.SaveApiKey(key.Trim());
                AnsiConsole.MarkupLine("[green]Saved[/] auth.json  (use /new to load it into this session)");
            }
        }

        if (AnsiConsole.Profile.Capabilities.Interactive && !ProjectTrust.IsTrusted(session.Config.Cwd))
        {
            var target = ProjectTrust.TrustTarget(session.Config.Cwd);
            AnsiConsole.MarkupLine($"You are in [bold]{Markup.Escape(session.Config.Cwd)}[/]");
            if (!string.Equals(target, session.Config.Cwd, StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine("[yellow]Trusting applies to the git root:[/] " + Markup.Escape(target));
            }
            AnsiConsole.MarkupLine("Do you trust the contents of this directory? Untrusted trees skip project-local skills, hooks, and exec policies.");
            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title("Trust this directory?")
                .AddChoices("Yes, continue", "No, quit"));
            if (choice.StartsWith("No", StringComparison.Ordinal))
            {
                return 1;
            }
            await session.App.SetProjectTrustAsync(true, session.Config.Cwd);
        }

        RenderBanner(session);
        session.Approver.Prompt = async (command, cwd, reason) =>
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[yellow]Approval required[/] ({Markup.Escape(reason)})");
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(cwd)}[/]");
            AnsiConsole.Write(new Panel(Markup.Escape(command)).Header("command"));
            var choice = await AnsiConsole.PromptAsync(new SelectionPrompt<string>()
                .Title("Decision")
                .AddChoices("allow", "deny", "abort"));
            return choice switch
            {
                "allow" => ApprovalDecision.Allow,
                "abort" => ApprovalDecision.Abort,
                _ => ApprovalDecision.NewDeny("user denied"),
            };
        };
        session.UserInput.Prompt = async questionsJson =>
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Agent needs input[/]");
            AnsiConsole.Write(new Panel(Markup.Escape(questionsJson ?? "")).Header("questions"));
            var answer = await AnsiConsole.AskAsync<string>("Answer:");
            return "{\"answers\":{\"q1\":\"" + (answer ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}}";
        };

        session.McpElicitation.Prompt = async message =>
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]MCP elicitation[/]");
            AnsiConsole.Write(new Panel(Markup.Escape(message ?? "")).Header("mcp"));
            var answer = await AnsiConsole.AskAsync<string>("Answer (empty declines):");
            if (string.IsNullOrWhiteSpace(answer)) return "{\"action\":\"decline\"}";
            return "{\"action\":\"accept\",\"content\":{\"text\":\"" + answer.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}}";
        };

        session.Event += RenderEvent;
        CancellationTokenSource? liveTurn = null;
        UseLive = LiveTui.Supported;
        LiveHistory.Clear();
        LiveSession = session;

        if (!string.IsNullOrWhiteSpace(opening))
        {
            await session.RunTurnAsync(opening);
            if (UseLive) LiveTui.Paint(session, LiveHistory, "", liveTurn is null ? "idle" : "working");
        }

        var quitArmed = DateTimeOffset.MinValue;
        while (true)
        {
            string input;
            if (UseLive)
            {
                var footer = liveTurn is not null ? "working"
                    : (DateTimeOffset.UtcNow - quitArmed < TimeSpan.FromSeconds(2)
                        ? "press ctrl+c again to quit"
                        : "idle  " + TuiStatus.Line(session.Config, session.Thread.Id, session.Thread.Title));
                var line = await LiveTui.ReadAsync(session, LiveHistory, PromptHistory, SlashCommands, footer, CancellationToken.None);
                if (line == "")
                {
                    if (liveTurn is not null)
                    {
                        liveTurn.Cancel();
                        quitArmed = DateTimeOffset.MinValue;
                        continue;
                    }
                    if (DateTimeOffset.UtcNow - quitArmed < TimeSpan.FromSeconds(2))
                        return 0;
                    quitArmed = DateTimeOffset.UtcNow;
                    continue;
                }
                quitArmed = DateTimeOffset.MinValue;
                input = line ?? "";
                if (input.StartsWith(LiveTui.QueuePrefix, StringComparison.Ordinal))
                {
                    var queued = input[LiveTui.QueuePrefix.Length..];
                    if (!string.IsNullOrWhiteSpace(queued))
                    {
                        session.App.QueueAddAsync(queued).GetAwaiter().GetResult();
                    }
                    continue;
                }
            }
            else
            {
                AnsiConsole.WriteLine();
                input = AnsiConsole.Prompt(new TextPrompt<string>("[bold green]>[/]").AllowEmpty());
            }
            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            if (!TryResolveSlash(ref input))
            {
                continue;
            }

            if (input is "/q" or "/quit" or "/exit")
            {
                return 0;
            }

            if (input is "/help")
            {
                AnsiConsole.MarkupLine("/quit  /new  /status  /model  /sandbox  /exec  /compact  /fork  /side  /btw  /mcp  /history  /goal  /name  /rename  /effort  /pin  /archive  /rollback  /skills  /hooks  /diff  /review  /feedback  /search  /approvals  /personality  /clear  /stop  /pwd  /usage  /plugins  /plan  /cd  /init  /logout  /copy  /worktree  /import  /delete  /experimental  /doctor  /memory  /resume  /agents  /app  /apply  /export  /recap  /mention  /debug-config  /ps  /apps  /ide  /rollout  /raw  /theme  /vim  /keymap  /pets  /setup-default-sandbox  /approve  /prompts  /subagents  /permissions  /execpolicy  /statusline  /title  /sandbox-add-read-dir  /help");
                continue;
            }

            if (input is "/clear")
            {
                LiveHistory.Clear();
                session.Restart();
                AnsiConsole.Clear();
                if (UseLive) LiveTui.Paint(session, LiveHistory, "", "idle");
                else RenderBanner(session);
                continue;
            }

            if (input is "/stop")
            {
                if (liveTurn is not null)
                {
                    liveTurn.Cancel();
                    AnsiConsole.MarkupLine("[yellow]interrupted turn[/]");
                }
                try
                {
                    session.App.CleanBackgroundTerminalsAsync().GetAwaiter().GetResult();
                    AnsiConsole.MarkupLine("[grey]background terminals cleaned[/]");
                }
                catch (Exception ex)
                {
                    if (liveTurn is null)
                        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(ex.Message)}[/]");
                }
                continue;
            }

            if (input is "/experimental")
            {
                try
                {
                    var listed = session.App.ListExperimentalFeaturesAsync().GetAwaiter().GetResult();
                    if (listed.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in data.EnumerateArray())
                        {
                            var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                            var on = item.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
                            AnsiConsole.MarkupLine($"{Markup.Escape(name)}  {(on ? "[green]on[/]" : "[grey]off[/]")}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                }
                continue;
            }

            if (input is "/doctor")
            {
                try
                {
                    var report = session.App.RunDoctorAsync(session.Config.Cwd).GetAwaiter().GetResult();
                    if (report.TryGetProperty("checks", out var checks) && checks.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var check in checks.EnumerateArray())
                        {
                            var cs = check.TryGetProperty("status", out var csEl) && csEl.ValueKind == JsonValueKind.String ? csEl.GetString() ?? "fail" : "fail";
                            var color = cs == "ok" ? "green" : cs == "warning" ? "yellow" : "red";
                            var group = check.TryGetProperty("group", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() ?? "" : "";
                            var summary = check.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
                            AnsiConsole.MarkupLine($"[{color}]{Markup.Escape(cs)}[/]  {Markup.Escape(group)}  {Markup.Escape(summary)}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                }
                continue;
            }

            if (input is "/usage")
            {
                try
                {
                    var usage = session.App.ReadUsageAsync().GetAwaiter().GetResult();
                    if (usage.TryGetProperty("threadUsage", out var tu) && tu.ValueKind == JsonValueKind.Object)
                    {
                        var est = tu.TryGetProperty("estimatedTokens", out var e) && e.TryGetInt64(out var ev) ? ev : 0;
                        var last = tu.TryGetProperty("lastTokens", out var l) && l.TryGetInt64(out var lv) ? lv : 0;
                        AnsiConsole.MarkupLine($"[grey]local estimate[/] {est} tok  last {last}");
                    }
                    else
                        AnsiConsole.MarkupLine("[grey]no live thread token estimate[/]");
                    AnsiConsole.MarkupLine("[grey]ChatGPT billed usage is not available for local API-key sessions.[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                }
                continue;
            }

            if (input is "/pwd")
            {
                AnsiConsole.MarkupLine(Markup.Escape(session.Config.Cwd));
                continue;
            }

            if (input.StartsWith("/execpolicy", StringComparison.Ordinal))
            {
                var rest = input.Length > 11 ? input[11..].Trim() : "";
                try
                {
                    if (rest.Length == 0)
                    {
                        var listed = session.App.ListExecPolicyAsync(session.Config.Cwd).GetAwaiter().GetResult();
                        var count = 0;
                        if (listed.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var rule in data.EnumerateArray())
                            {
                                count++;
                                var pattern = rule.TryGetProperty("pattern", out var pat) && pat.ValueKind == JsonValueKind.Array
                                    ? string.Join(' ', pat.EnumerateArray().Select(x => x.GetString() ?? ""))
                                    : "";
                                var decision = rule.TryGetProperty("decision", out var dec) && dec.ValueKind == JsonValueKind.String ? dec.GetString() ?? "" : "";
                                AnsiConsole.MarkupLine($"{Markup.Escape(pattern)}  {Markup.Escape(decision)}");
                            }
                        }
                        if (count == 0) AnsiConsole.MarkupLine("[grey]no prefix_rule files in ~/.codexsharp/rules[/]");
                        AnsiConsole.MarkupLine("[grey]usage: /execpolicy forbidden|prompt TOKEN [TOKEN...][/]");
                    }
                    else
                    {
                        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (parts.Length < 2 || parts[0] is not ("forbidden" or "prompt" or "allow"))
                        {
                            AnsiConsole.MarkupLine("usage: /execpolicy forbidden|prompt TOKEN [TOKEN...]");
                        }
                        else
                        {
                            var added = session.App.AddExecPolicyRuleAsync(parts[0], parts.Skip(1).ToArray()).GetAwaiter().GetResult();
                            var line = added.TryGetProperty("line", out var lineEl) && lineEl.ValueKind == JsonValueKind.String ? lineEl.GetString() ?? "" : rest;
                            AnsiConsole.MarkupLine($"[yellow]rule[/] {Markup.Escape(line)}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                }
                continue;
            }

            if (input is "/plugins")
            {
                try
                {
                    var listed = session.App.ListPluginsAsync().GetAwaiter().GetResult();
                    var count = 0;
                    if (listed.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var plugin in data.EnumerateArray())
                        {
                            count++;
                            var name = plugin.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            var kind = plugin.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                            AnsiConsole.MarkupLine($"{Markup.Escape(name)}  [grey]{Markup.Escape(kind)}[/]");
                        }
                    }
                    if (count == 0) AnsiConsole.MarkupLine("[grey]No plugins[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                }
                continue;
            }

            if (input.StartsWith("/prompts", StringComparison.Ordinal))
            {
                var rest = input.Length > 8 ? input[8..].Trim() : "";
                try
                {
                    if (rest.Length == 0)
                    {
                        var listed = await session.App.ListPromptsAsync();
                        var count = 0;
                        if (listed.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var prompt in data.EnumerateArray())
                            {
                                count++;
                                var name = prompt.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                                var path = prompt.TryGetProperty("path", out var pth) ? pth.GetString() ?? "" : "";
                                AnsiConsole.MarkupLine($"{Markup.Escape(name)}  [grey]{Markup.Escape(path)}[/]");
                            }
                        }
                        if (count == 0) AnsiConsole.MarkupLine("[grey]no prompts in ~/.codexsharp/prompts[/]");
                        AnsiConsole.MarkupLine("[grey]usage: /prompts NAME[/]");
                    }
                    else
                    {
                        var read = await session.App.ReadPromptAsync(rest);
                        var body = read.TryGetProperty("contents", out var contents) && contents.ValueKind == JsonValueKind.String ? contents.GetString() : null;
                        if (string.IsNullOrWhiteSpace(body)) AnsiConsole.MarkupLine("[red]prompt not found[/]");
                        else await session.RunTurnAsync(body);
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                }
                continue;
            }

            if (input is "/subagents")
            {
                var agents = session.ListSubagents();
                if (agents.Count == 0) AnsiConsole.MarkupLine("[grey]no subagents[/]");
                foreach (var agent in agents)
                    AnsiConsole.MarkupLine($"{Markup.Escape(agent.Id)}  {(agent.Done ? "[green]done[/]" : "[yellow]running[/]")}  {Markup.Escape(agent.Result ?? "")}");
                continue;
            }

            if (input.StartsWith("/memory", StringComparison.Ordinal) || input.StartsWith("/memories", StringComparison.Ordinal))
            {
                var rest = input.StartsWith("/memories", StringComparison.Ordinal)
                    ? (input.Length > 9 ? input[9..].Trim() : "")
                    : (input.Length > 7 ? input[7..].Trim() : "");
                try
                {
                    if (rest is "off" or "disabled")
                    {
                        await session.App.SetMemoryModeAsync("disabled");
                        AnsiConsole.MarkupLine("[yellow]memory[/] disabled");
                    }
                    else if (rest is "on" or "enabled")
                    {
                        await session.App.SetMemoryModeAsync("enabled");
                        AnsiConsole.MarkupLine("[yellow]memory[/] enabled");
                    }
                    else if (rest is "reset")
                    {
                        await session.App.ResetMemoryAsync();
                        AnsiConsole.MarkupLine("[yellow]memories cleared[/]");
                    }
                    else if (rest.StartsWith("add ", StringComparison.Ordinal) || rest.StartsWith("write ", StringComparison.Ordinal))
                    {
                        var payload = rest[(rest.IndexOf(' ') + 1)..].Trim();
                        var split = payload.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (split.Length < 2)
                        {
                            AnsiConsole.MarkupLine("usage: /memory add NAME TEXT");
                        }
                        else
                        {
                            var written = await session.App.WriteMemoryAsync(split[0], split[1]);
                            var name = written.TryGetProperty("name", out var n) ? n.GetString() ?? split[0] : split[0];
                            AnsiConsole.MarkupLine("[green]wrote[/] " + Markup.Escape(name));
                        }
                    }
                    else if (rest.StartsWith("read ", StringComparison.Ordinal))
                    {
                        var name = rest[5..].Trim();
                        var note = await session.App.ReadMemoryAsync(name);
                        var body = note.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                        AnsiConsole.WriteLine(body);
                    }
                    else
                    {
                        var listed = await session.App.ListMemoriesAsync();
                        if (!listed.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                        {
                            AnsiConsole.MarkupLine("[grey]no memories[/]");
                        }
                        else
                        {
                            foreach (var note in data.EnumerateArray().Take(20))
                            {
                                var name = note.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? note.ToString() : note.ToString();
                                AnsiConsole.MarkupLine(Markup.Escape(name));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                }
                continue;
            }

            if (input.StartsWith("/resume", StringComparison.Ordinal))
            {
                var rest = input.Length > 7 ? input[7..].Trim() : "";
                var all = rest is "--all" or "all";
                if (all) rest = "";
                if (rest.Length == 0)
                {
                    var threads = ResumePicker.Candidates(all, includeNonInteractive: all);
                    if (threads.Count == 0)
                    {
                        AnsiConsole.MarkupLine(all ? "[grey]no threads[/]" : "[grey]no threads in this directory[/]  /resume --all");
                    }
                    else if (AnsiConsole.Profile.Capabilities.Interactive && threads.Count > 1)
                    {
                        var labels = threads.Take(30).Select(thread => ResumePicker.Format(thread, showCwd: all)).ToList();
                        var picked = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Resume which thread?").AddChoices(labels));
                        var id = ResumePicker.IdFromLabel(picked);
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            session.Resume(id, session.Config);
                            RenderBanner(session);
                        }
                    }
                    else
                    {
                        foreach (var thread in threads.Take(20))
                            AnsiConsole.MarkupLine(Markup.Escape(ResumePicker.Format(thread, showCwd: all)));
                        AnsiConsole.MarkupLine("[grey]usage: /resume THREAD_ID | /resume --all[/]");
                    }
                }
                else
                {
                    try
                    {
                        session.Resume(rest, session.Config);
                        RenderBanner(session);
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                    }
                }
                continue;
            }

            if (input.StartsWith("/agents", StringComparison.Ordinal))
            {
                var rest = input.Length > 7 ? input[7..].Trim() : "";
                var all = rest is "--all" or "all";
                if (all) rest = "";
                var cwd = all ? null : Environment.CurrentDirectory;
                if (rest.Length == 0)
                {
                    AnsiConsole.MarkupLine(Markup.Escape(AgentsOverview.Render(cwd, session.ListSubagents(), all)));
                    var threads = ResumePicker.Candidates(all, includeNonInteractive: false, cwd);
                    if (AnsiConsole.Profile.Capabilities.Interactive && threads.Count > 1)
                    {
                        var labels = threads.Take(30).Select(thread => ResumePicker.Format(thread, showCwd: all)).ToList();
                        var picked = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Switch to which agent session?").AddChoices(labels));
                        var id = ResumePicker.IdFromLabel(picked);
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            session.Resume(id, session.Config);
                            RenderBanner(session);
                        }
                    }
                }
                else
                {
                    try
                    {
                        session.Resume(rest, session.Config);
                        RenderBanner(session);
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                    }
                }
                continue;
            }

            if (input is "/app")
            {
                Program.LaunchDesktop();
                continue;
            }

            if (input is "/apply")
            {
                await SessionCli.ApplyAsync([session.Thread.Id]);
                continue;
            }

            if (input is "/export" || input.StartsWith("/export ", StringComparison.Ordinal))
            {
                var dest = input.Length > 7 ? input[7..].Trim() : "";
                try
                {
                    var exported = await session.App.ExportThreadAsync(string.IsNullOrWhiteSpace(dest) ? null : dest);
                    var path = exported.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? dest : dest;
                    AnsiConsole.MarkupLine($"[green]wrote[/] {Markup.Escape(path)}");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                }
                continue;
            }

            if (input is "/recap")
            {
                try
                {
                    var recap = await session.App.RecapAsync();
                    var status = recap.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() ?? "" : "";
                    var body = recap.TryGetProperty("recap", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
                    if (status == "empty" || string.IsNullOrWhiteSpace(body))
                        AnsiConsole.MarkupLine("[grey]" + Markup.Escape(string.IsNullOrWhiteSpace(body) ? "There is no conversation history to recap." : body) + "[/]");
                    else if (status == "errored" || status == "busy")
                        AnsiConsole.MarkupLine("[red]" + Markup.Escape(body) + "[/]");
                    else
                        AnsiConsole.MarkupLine("[green]recap[/] " + Markup.Escape(body));
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                }
                continue;
            }

            if (input.StartsWith("/mention", StringComparison.Ordinal))
            {
                var q = input.Length > 8 ? input[8..].Trim() : "";
                if (q.Length == 0)
                {
                    AnsiConsole.MarkupLine("usage: /mention QUERY");
                }
                else
                {
                    try
                    {
                        var found = await session.App.FuzzySearchAsync(q);
                        if (found.TryGetProperty("matches", out var matches) && matches.ValueKind == JsonValueKind.Array)
                        {
                            var any = false;
                            foreach (var match in matches.EnumerateArray())
                            {
                                var path = match.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
                                if (path.Length == 0) continue;
                                any = true;
                                AnsiConsole.MarkupLine(Markup.Escape(path));
                            }
                            if (!any) AnsiConsole.MarkupLine("[grey]no file matches[/]");
                        }
                        else AnsiConsole.MarkupLine("[grey]no file matches[/]");
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                    }
                }
                continue;
            }

            if (input is "/debug-config")
            {
                try
                {
                    var cfg = await session.App.ReadConfigAsync();
                    static string S(JsonElement el, string name) =>
                        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    AnsiConsole.MarkupLine($"home {Markup.Escape(S(cfg, "home"))}");
                    AnsiConsole.MarkupLine($"cwd {Markup.Escape(S(cfg, "cwd"))}");
                    AnsiConsole.MarkupLine($"model {Markup.Escape(S(cfg, "model"))}");
                    AnsiConsole.MarkupLine($"provider {Markup.Escape(S(cfg, "modelProvider"))}");
                    AnsiConsole.MarkupLine($"sandbox {Markup.Escape(S(cfg, "sandboxMode"))}");
                    AnsiConsole.MarkupLine($"approval {Markup.Escape(S(cfg, "approvalPolicy"))}");
                    if (cfg.TryGetProperty("extraReadRoots", out var roots) && roots.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var root in roots.EnumerateArray())
                        {
                            if (root.ValueKind == JsonValueKind.String)
                                AnsiConsole.MarkupLine($"read-root {Markup.Escape(root.GetString() ?? "")}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                }
                continue;
            }

            if (input is "/ps")
            {
                try
                {
                    var listed = session.App.ListBackgroundTerminalsAsync().GetAwaiter().GetResult();
                    var n = 0;
                    if (listed.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var row in data.EnumerateArray())
                        {
                            n++;
                            AnsiConsole.MarkupLine(Markup.Escape(row.GetRawText()));
                        }
                    }
                    if (n == 0) AnsiConsole.MarkupLine("[grey]no background terminals[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                }
                continue;
            }

            if (input is "/apps")
            {
                try
                {
                    var listed = session.App.ListAppsAsync().GetAwaiter().GetResult();
                    var n = 0;
                    if (listed.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var row in data.EnumerateArray())
                        {
                            n++;
                            AnsiConsole.MarkupLine(Markup.Escape(row.GetRawText()));
                        }
                    }
                    if (n == 0)
                        AnsiConsole.MarkupLine("[grey]no hosted apps[/]  remote OpenAI plugin-service is not configured");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                }
                continue;
            }

            if (input is "/ide")
            {
                var bits = new[] { "TERM_PROGRAM", "TERM_PROGRAM_VERSION", "VSCODE_PID", "VSCODE_INJECTION", "CURSOR_SESSION_ID" }
                    .Select(n => n + "=" + (Environment.GetEnvironmentVariable(n) ?? ""))
                    .Where(s => !s.EndsWith('='))
                    .ToArray();
                if (bits.Length == 0)
                    AnsiConsole.MarkupLine("[grey]No IDE env. CodexSharp does not speak the VS Code/Cursor Codex extension protocol.[/]");
                else
                {
                    foreach (var bit in bits)
                        AnsiConsole.MarkupLine("[yellow]IDE env[/] " + Markup.Escape(bit));
                    AnsiConsole.MarkupLine("[grey]no selection/open-file bridge[/]");
                }
                continue;
            }

            if (input is "/rollout")
            {
                try
                {
                    var rollout = await session.App.RolloutPathAsync();
                    var path = rollout.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
                    var exists = rollout.TryGetProperty("exists", out var e) && e.ValueKind == JsonValueKind.True;
                    AnsiConsole.MarkupLine(Markup.Escape(path) + (exists ? "" : " [grey](missing)[/]"));
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                }
                continue;
            }

            if (input is "/raw")
            {
                var cfg = await session.App.ReadConfigAsync();
                var cur = cfg.TryGetProperty("tuiRaw", out var rawEl) && rawEl.ValueKind == JsonValueKind.String ? rawEl.GetString() : null;
                var next = cur == "true" ? "false" : "true";
                await session.App.WriteConfigAsync("tui_raw", next);
                AnsiConsole.MarkupLine($"[yellow]raw[/] {next}");
                continue;
            }

            if (input.StartsWith("/statusline", StringComparison.Ordinal))
            {
                var rest = input.Length > 11 ? input[11..].Trim() : "";
                if (rest.Length == 0)
                {
                    var cfg = await session.App.ReadConfigAsync();
                    var cur = cfg.TryGetProperty("tuiStatusline", out var sl) && sl.ValueKind == JsonValueKind.String ? sl.GetString() : null;
                    AnsiConsole.MarkupLine($"statusline {Markup.Escape(cur ?? "model,sandbox,cwd")}");
                }
                else
                {
                    await session.App.WriteConfigAsync("tui_statusline", rest);
                    AnsiConsole.MarkupLine($"[yellow]statusline[/] {Markup.Escape(rest)}");
                    RenderBanner(session);
                }
                continue;
            }

            if (input.StartsWith("/title", StringComparison.Ordinal))
            {
                var rest = input.Length > 6 ? input[6..].Trim() : "";
                if (rest.Length == 0)
                {
                    var cfg = await session.App.ReadConfigAsync();
                    var cur = cfg.TryGetProperty("tuiTitle", out var tt) && tt.ValueKind == JsonValueKind.String ? tt.GetString() : null;
                    AnsiConsole.MarkupLine($"title {Markup.Escape(cur ?? "thread,model")}");
                }
                else
                {
                    await session.App.WriteConfigAsync("tui_title", rest);
                    try { Console.Title = TerminalTitle.Sanitize(TuiStatus.Title(session.Config, session.Thread.Id, session.Thread.Title)); } catch { }
                    AnsiConsole.MarkupLine($"[yellow]title[/] {Markup.Escape(rest)}");
                }
                continue;
            }

            if (input.StartsWith("/theme", StringComparison.Ordinal))
            {
                var rest = input.Length > 6 ? input[6..].Trim() : "";
                if (rest.Length == 0)
                {
                    var cfg = await session.App.ReadConfigAsync();
                    var cur = cfg.TryGetProperty("tuiTheme", out var th) && th.ValueKind == JsonValueKind.String ? th.GetString() : null;
                    AnsiConsole.MarkupLine($"theme {Markup.Escape(cur ?? "default")}  usage: /theme NAME");
                }
                else
                {
                    await session.App.WriteConfigAsync("tui_theme", rest);
                    AnsiConsole.MarkupLine($"[yellow]theme[/] {Markup.Escape(rest)}  [grey]Spectre TUI; not ratatui[/]");
                }
                continue;
            }

            if (input.StartsWith("/notifications", StringComparison.Ordinal) || input.StartsWith("/notification", StringComparison.Ordinal))
            {
                var rest = input.StartsWith("/notifications", StringComparison.Ordinal)
                    ? (input.Length > 14 ? input[14..].Trim() : "")
                    : (input.Length > 13 ? input[13..].Trim() : "");
                if (rest.Length == 0)
                    AnsiConsole.MarkupLine("notifications " + Markup.Escape(DesktopNotify.Method) + "  usage: /notifications auto|osc9|bel|off");
                else if (rest is not "auto" and not "osc9" and not "bel" and not "off")
                    AnsiConsole.MarkupLine("[red]unknown method[/]");
                else
                {
                    await session.App.WriteConfigAsync("tui_notifications", rest);
                    AnsiConsole.MarkupLine("[yellow]notifications[/] " + Markup.Escape(rest));
                }
                continue;
            }

            if (input is "/vim")
            {
                var cfg = await session.App.ReadConfigAsync();
                var cur = cfg.TryGetProperty("tuiVim", out var vimEl) && vimEl.ValueKind == JsonValueKind.String ? vimEl.GetString() : null;
                var next = cur == "true" ? "false" : "true";
                await session.App.WriteConfigAsync("tui_vim", next);
                AnsiConsole.MarkupLine(next == "true"
                    ? "[yellow]vim[/] on  [grey]esc normal · i/a/I/A/o/O insert · v/V visual · hjkl G gg 0$ w/b e x dd dw c y p P u . f t r ctrl+r[/]"
                    : "[yellow]vim[/] off");
                continue;
            }

            if (input.StartsWith("/keymap", StringComparison.Ordinal))
            {
                var rest = input.Length > 7 ? input[7..].Trim() : "";
                if (rest.Length == 0)
                {
                    var listed = await session.App.ListKeymapAsync();
                    if (listed.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var bind in data.EnumerateArray())
                        {
                            var context = bind.TryGetProperty("context", out var c) ? c.GetString() ?? "" : "";
                            var action = bind.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
                            var keys = bind.TryGetProperty("keys", out var k) ? k.GetString() ?? "" : "";
                            AnsiConsole.MarkupLine($"{Markup.Escape(context)}.{Markup.Escape(action)}  [grey]{Markup.Escape(keys)}[/]");
                        }
                    }
                    AnsiConsole.MarkupLine("[grey]usage: /keymap CONTEXT ACTION KEYS   Spectre TUI still uses prompts[/]");
                }
                else
                {
                    var parts = rest.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length < 3)
                        AnsiConsole.MarkupLine("[red]unknown keymap slot[/]  usage: /keymap composer submit enter");
                    else
                    {
                        try
                        {
                            await session.App.SetKeymapAsync(parts[0], parts[1], parts[2]);
                            AnsiConsole.MarkupLine($"[yellow]keymap[/] {Markup.Escape(parts[0])}.{Markup.Escape(parts[1])}={Markup.Escape(parts[2])}");
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                        }
                    }
                }
                continue;
            }

            if (input is "/cloud" || input.StartsWith("/cloud ", StringComparison.Ordinal) || input.StartsWith("/cloud-", StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine("[yellow]" + Markup.Escape(HonestStubs.CloudSlashMessage()) + "[/]");
                continue;
            }

            if (input is "/pet" || input.StartsWith("/pet ", StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine("[yellow]" + Markup.Escape(HonestStubs.PetOverlayMessage()) + "[/]");
                continue;
            }

            if (input.StartsWith("/pets", StringComparison.Ordinal))
            {
                var rest = input.Length > 5 ? input[5..].Trim() : "";
                try
                {
                    if (rest.Length == 0)
                    {
                        var pet = await session.App.ReadPetsAsync();
                        var id = pet.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                        var ascii = pet.TryGetProperty("ascii", out var aEl) ? aEl.GetString() ?? "" : "";
                        AnsiConsole.MarkupLine("pet " + Markup.Escape(id) + "  " + Markup.Escape(ascii));
                        if (pet.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
                            AnsiConsole.MarkupLine("[grey]" + Markup.Escape(string.Join(' ', ids.EnumerateArray().Select(x => x.GetString() ?? ""))) + "[/]");
                    }
                    else
                    {
                        var pet = await session.App.SetPetsAsync(rest);
                        var id = pet.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? rest : rest;
                        var ascii = pet.TryGetProperty("ascii", out var aEl) ? aEl.GetString() ?? "" : "";
                        AnsiConsole.MarkupLine($"[yellow]pet[/] {Markup.Escape(id)}  {Markup.Escape(ascii)}");
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                }
                continue;
            }

            if (input is "/setup-default-sandbox")
            {
                try
                {
                    var snap = await session.App.SetupWindowsSandboxAsync("unelevated", session.Config.Cwd);
                    var status = snap.TryGetProperty("status", out var st) ? st.GetString() ?? snap.ToString() : snap.ToString();
                    AnsiConsole.MarkupLine("[yellow]" + Markup.Escape(status) + "[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                }
                continue;
            }

            if (input is "/approve")
            {
                AnsiConsole.MarkupLine("[grey]No auto-review denial to retry. Guardian is not configured.[/]");
                continue;
            }

            if (input is "/side" || input.StartsWith("/btw", StringComparison.Ordinal) || input.StartsWith("/side ", StringComparison.Ordinal))
            {
                var rest = input.StartsWith("/btw", StringComparison.Ordinal)
                    ? (input.Length > 4 ? input[4..].Trim() : "")
                    : (input.Length > 5 ? input[5..].Trim() : "");
                session = session.Fork();
                session.Approver.Prompt = (command, cwd, reason) => Task.FromResult(ApprovalDecision.Allow);
                session.Event += RenderEvent;
                RenderBanner(session);
                AnsiConsole.MarkupLine("[yellow]side conversation[/] (fork)");
                if (rest.Length > 0)
                    await session.RunTurnAsync(rest);
                continue;
            }

            if (input.StartsWith("/sandbox-add-read-dir", StringComparison.Ordinal))
            {
                var rest = input.Length > 21 ? input[21..].Trim() : "";
                if (rest.Length == 0)
                {
                    AnsiConsole.MarkupLine("usage: /sandbox-add-read-dir PATH");
                }
                else
                {
                    try
                    {
                        var added = await session.App.AddExtraReadRootAsync(Path.GetFullPath(rest, session.Config.Cwd));
                        var path = added.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? rest : rest;
                        AnsiConsole.MarkupLine($"[yellow]read root[/] {Markup.Escape(path)}");
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                    }
                }
                continue;
            }

            if (input is "/plan" or "/default" or "/pair")
            {
                var mode = input[1..];
                session.App.WriteConfigAsync("collaboration_mode", mode).GetAwaiter().GetResult();
                AnsiConsole.MarkupLine($"[yellow]collaboration[/] {mode}");
                continue;
            }

            if (input.StartsWith("/cd", StringComparison.Ordinal))
            {
                var rest = input.Length > 3 ? input[3..].Trim() : "";
                if (rest.Length == 0)
                {
                    AnsiConsole.MarkupLine(Markup.Escape(session.Config.Cwd));
                }
                else
                {
                    var next = Path.GetFullPath(rest, session.Config.Cwd);
                    if (!Directory.Exists(next))
                    {
                        AnsiConsole.MarkupLine("[red]not a directory[/]");
                    }
                    else
                    {
                        session.ApplySettings(cwd: next);
                        AnsiConsole.MarkupLine($"[yellow]cwd[/] {Markup.Escape(next)}");
                    }
                }
                continue;
            }

            if (input is "/init")
            {
                try
                {
                    var written = await session.App.WriteAgentsMarkdownAsync(session.Config.Cwd);
                    var created = written.TryGetProperty("created", out var c) && c.ValueKind == JsonValueKind.True;
                    AnsiConsole.MarkupLine(created ? "[green]wrote AGENTS.md[/]" : "[grey]AGENTS.md already exists[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                }
                continue;
            }

            if (input is "/logout")
            {
                try
                {
                    await session.App.LogoutAsync();
                    AnsiConsole.MarkupLine("[grey]logged out[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                }
                continue;
            }

            if (input is "/copy" || input.StartsWith("/copy ", StringComparison.Ordinal))
            {
                var rest = input.Length > 5 ? input[5..].Trim() : "";
                try
                {
                    var copied = await session.App.CopyLastAsync(rest);
                    var kind = copied.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? "" : "";
                    var body = copied.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String ? tx.GetString() ?? "" : "";
                    if (kind == "empty" || string.IsNullOrWhiteSpace(body))
                    {
                        AnsiConsole.MarkupLine("[grey]no agent text[/]");
                    }
                    else
                    {
                        CopyText(body);
                        AnsiConsole.MarkupLine(kind == "code" ? "[green]copied last code fence[/]" : "[green]copied last agent message[/]");
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                }
                continue;
            }

            if (input is "/import")
            {
                try
                {
                    var detected = await session.App.DetectExternalAgentsAsync([session.Config.Cwd]);
                    if (!detected.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
                    {
                        AnsiConsole.MarkupLine("[grey]nothing to import[/]");
                    }
                    else
                    {
                        foreach (var item in items.EnumerateArray())
                        {
                            var type = item.TryGetProperty("itemType", out var tEl) ? tEl.GetString() ?? "" : "";
                            var desc = item.TryGetProperty("description", out var dEl) ? dEl.GetString() ?? "" : "";
                            AnsiConsole.MarkupLine($"{Markup.Escape(type)}  {Markup.Escape(desc)}");
                        }
                        var imported = await session.App.ImportExternalAgentsAsync(items);
                        var n = imported.TryGetProperty("imported", out var imp) && imp.ValueKind == JsonValueKind.Array ? imp.GetArrayLength() : 0;
                        AnsiConsole.MarkupLine($"[green]imported {n}[/]");
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                }
                continue;
            }

            if (input is "/delete")
            {
                try
                {
                    await session.App.DeleteThreadAsync(session.Thread.Id);
                    AnsiConsole.MarkupLine("[grey]deleted thread[/]");
                    session.Restart();
                    RenderBanner(session);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                }
                continue;
            }

            if (input.StartsWith("/worktree", StringComparison.Ordinal))
            {
                var rest = input.Length > 9 ? input[9..].Trim() : "";
                if (rest.Length == 0 || rest is "list")
                {
                    try
                    {
                        var git = await session.App.GitDiffToRemoteAsync(session.Config.Cwd);
                        var count = 0;
                        if (git.TryGetProperty("worktrees", out var trees) && trees.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var tree in trees.EnumerateArray())
                            {
                                count++;
                                var path = tree.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                                var branch = tree.TryGetProperty("branch", out var b) ? b.GetString() ?? "" : "";
                                AnsiConsole.MarkupLine($"{Markup.Escape(path)}  [grey]{Markup.Escape(branch)}[/]");
                            }
                        }
                        if (count == 0) AnsiConsole.MarkupLine("[grey]no worktrees (not a git repo?)[/]");
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                    }
                }
                else if (rest.StartsWith("add", StringComparison.Ordinal))
                {
                    var dest = rest.Length > 3 ? rest[3..].Trim() : "";
                    if (dest.Length == 0)
                    {
                        dest = Path.Combine(CodexPaths.Home, "worktrees", session.Thread.Id);
                    }
                    try
                    {
                        var added = await session.App.AddGitWorktreeAsync(session.Config.Cwd, dest, "codexsharp-" + session.Thread.Id[..8]);
                        var path = added.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? dest : dest;
                        session.ApplySettings(cwd: path);
                        AnsiConsole.MarkupLine($"[green]worktree[/] {Markup.Escape(path)}");
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                    }
                }
                else if (rest is "new" or "start")
                {
                    try
                    {
                        var started = await session.App.StartWorktreeAsync(session.Config.Cwd);
                        var path = started.TryGetProperty("cwd", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
                        var cloud = started.TryGetProperty("cloudWorktree", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                        if (path.Length > 0) session.ApplySettings(cwd: path);
                        AnsiConsole.MarkupLine($"[green]worktree[/] {Markup.Escape(path)}  [grey]{Markup.Escape(cloud)}[/]");
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine("usage: /worktree [list]|add [PATH]|new");
                }
                continue;
            }



            if (input is "/status")
            {
                RenderBanner(session);
                continue;
            }

            if (input.StartsWith("/model", StringComparison.Ordinal))
            {
                var rest = input.Length > 6 ? input[6..].Trim() : "";
                if (rest.Length == 0)
                {
                    try
                    {
                        var listed = await session.App.ListModelsAsync();
                        if (listed.TryGetProperty("data", out var models) && models.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var entry in models.EnumerateArray())
                            {
                                var id = entry.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                                var provider = entry.TryGetProperty("provider", out var pEl) ? pEl.GetString() ?? "" : "";
                                if (id.Length == 0) continue;
                                AnsiConsole.MarkupLine($"{Markup.Escape(id)}  [grey]{Markup.Escape(provider)}[/]");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                    }
                    AnsiConsole.MarkupLine($"[grey]current[/] {Markup.Escape(session.Config.Model)}");
                    AnsiConsole.MarkupLine("[grey]usage: /model ID | /model add ID[/]");
                }
                else if (rest.StartsWith("add ", StringComparison.Ordinal))
                {
                    var id = rest[4..].Trim();
                    var entry = ModelCatalog.Add(id);
                    AnsiConsole.MarkupLine($"[green]catalog[/] {Markup.Escape(entry.Id)} ({Markup.Escape(entry.Provider)})");
                }
                else
                {
                    await session.App.WriteConfigAsync("model", rest);
                    session.ApplySettings(model: rest);
                    AnsiConsole.MarkupLine($"[yellow]model[/] {Markup.Escape(rest)}");
                }
                continue;
            }

            if (input.StartsWith("/approvals", StringComparison.Ordinal) || input.StartsWith("/permissions", StringComparison.Ordinal))
            {
                var rest = input.StartsWith("/permissions", StringComparison.Ordinal)
                    ? (input.Length > 12 ? input[12..].Trim() : "")
                    : (input.Length > 10 ? input[10..].Trim() : "");
                if (rest is "on-request" or "never" or "untrusted")
                {
                    await session.App.WriteConfigAsync("approval_policy", rest);
                    session.ApplySettings(approval: rest);
                    AnsiConsole.MarkupLine($"[yellow]approval[/] {Markup.Escape(rest)}");
                }
                else
                {
                    AnsiConsole.MarkupLine("usage: /approvals on-request|never|untrusted");
                }
                continue;
            }

            if (input.StartsWith("/personality", StringComparison.Ordinal))
            {
                var rest = input.Length > 12 ? input[12..].Trim() : "";
                if (rest.Length == 0)
                {
                    try
                    {
                        var cfg = await session.App.ReadConfigAsync();
                        var current = cfg.TryGetProperty("personality", out var pEl) && pEl.ValueKind == JsonValueKind.String
                            ? pEl.GetString()
                            : null;
                        AnsiConsole.MarkupLine(Markup.Escape(string.IsNullOrWhiteSpace(current) ? "default" : current));
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                    }
                }
                else
                {
                    await session.App.WriteConfigAsync("personality", rest);
                    AnsiConsole.MarkupLine($"[yellow]personality[/] {Markup.Escape(rest)}");
                }
                continue;
            }

            if (input.StartsWith("/sandbox", StringComparison.Ordinal))
            {
                var rest = input.Length > 8 ? input[8..].Trim() : "";
                if (rest is "read-only" or "workspace-write" or "danger-full-access")
                {
                    await session.App.WriteConfigAsync("sandbox_mode", rest);
                    session.ApplySettings(sandbox: rest);
                    AnsiConsole.MarkupLine($"[yellow]sandbox[/] {Markup.Escape(rest)}");
                }
                else
                {
                    AnsiConsole.MarkupLine("usage: /sandbox read-only|workspace-write|danger-full-access");
                }
                continue;
            }

            if (input.StartsWith("/exec ", StringComparison.Ordinal))
            {
                var cmd = input[6..].Trim();
                if (cmd.Length == 0)
                {
                    AnsiConsole.MarkupLine("usage: /exec command");
                    continue;
                }

                var broker = new CommandExecBroker();
                var result = await broker.RunAsync(
                    [cmd],
                    null,
                    session.Config.Cwd,
                    session.Config,
                    false,
                    false,
                    false,
                    60_000,
                    32_000,
                    null,
                    CancellationToken.None);
                AnsiConsole.Write(new Panel(Markup.Escape(Trim(result.Stdout + result.Stderr, 4000))).Header($"exit {result.ExitCode}"));
                continue;
            }

            if (input is "/new")
            {
                session.Restart();
                RenderBanner(session);
                continue;
            }

            if (input is "/compact")
            {
                AnsiConsole.MarkupLine(session.TryCompact() ? "[yellow]Compacted earlier history.[/]" : "[grey]History already small.[/]");
                continue;
            }

            if (input is "/fork")
            {
                session.Fork();
                RenderBanner(session);
                continue;
            }

            if (input is "/pin")
            {
                AnsiConsole.MarkupLine(session.TogglePin() ? "[yellow]pinned[/]" : "[grey]unpinned[/]");
                continue;
            }

            if (input is "/archive")
            {
                try
                {
                    await session.App.ArchiveAsync(session.Thread.Id);
                    AnsiConsole.MarkupLine("[grey]archived[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                }
                continue;
            }

            if (input.StartsWith("/search", StringComparison.Ordinal))
            {
                var term = input.Length > 7 ? input[7..].Trim() : "";
                if (term.Length == 0)
                {
                    AnsiConsole.MarkupLine("usage: /search term");
                }
                else
                {
                    try
                    {
                        var listed = await session.App.SearchThreadsAsync(term);
                        if (listed.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var hit in data.EnumerateArray())
                            {
                                var title = "";
                                if (hit.TryGetProperty("thread", out var th) && th.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String)
                                    title = tEl.GetString() ?? "";
                                var snippet = hit.TryGetProperty("snippet", out var sEl) && sEl.ValueKind == JsonValueKind.String ? sEl.GetString() ?? "" : "";
                                AnsiConsole.MarkupLine($"{Markup.Escape(title)}  [grey]{Markup.Escape(snippet)}[/]");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                    }
                }
                continue;
            }

            if (input is "/rollback")
            {
                AnsiConsole.MarkupLine(session.RollbackTurns(1) ? "[yellow]rolled back last turn[/]" : "[grey]nothing to rollback[/]");
                continue;
            }

            if (input.StartsWith("/skills", StringComparison.Ordinal))
            {
                var rest = input.Length > 7 ? input[7..].Trim() : "";
                try
                {
                    if (rest.Length == 0)
                    {
                        var listed = await session.App.ListSkillsAsync();
                        var count = 0;
                        if (listed.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var skill in data.EnumerateArray())
                            {
                                count++;
                                var name = skill.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                                var path = skill.TryGetProperty("path", out var pth) ? pth.GetString() ?? "" : "";
                                var on = !(skill.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False);
                                AnsiConsole.MarkupLine($"{Markup.Escape(name)}  {(on ? "[green]on[/]" : "[grey]off[/]")}  [grey]{Markup.Escape(path)}[/]");
                            }
                        }
                        if (count == 0) AnsiConsole.MarkupLine("[grey]No SKILL.md found[/]");
                        AnsiConsole.MarkupLine("[grey]usage: /skills NAME on|off[/]");
                    }
                    else
                    {
                        var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (parts.Length == 2 && parts[1] is "on" or "off")
                        {
                            await session.App.WriteSkillEnabledAsync(parts[0], parts[1] == "on");
                            AnsiConsole.MarkupLine($"[yellow]skill[/] {Markup.Escape(parts[0])} {parts[1]}");
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("usage: /skills NAME on|off");
                        }
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                }
                continue;
            }

            if (input is "/hooks")
            {
                try
                {
                    var listed = await session.App.ListHooksAsync();
                    var count = 0;
                    if (listed.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var hook in data.EnumerateArray())
                        {
                            count++;
                            var name = hook.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            var ev = hook.TryGetProperty("event", out var e) ? e.GetString() ?? "" : "";
                            var command = hook.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
                            AnsiConsole.MarkupLine($"{Markup.Escape(name)}  {Markup.Escape(ev)}  [grey]{Markup.Escape(command)}[/]");
                        }
                    }
                    if (count == 0) AnsiConsole.MarkupLine("[grey]No hooks in ~/.codexsharp/hooks[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                }
                continue;
            }

            if (input is "/feedback")
            {
                try
                {
                    var saved = await session.App.UploadFeedbackAsync("other", "cli");
                    var fbId = saved.TryGetProperty("threadId", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() ?? "" : "";
                    AnsiConsole.MarkupLine($"[green]saved[/] {Markup.Escape(fbId)}");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                }
                continue;
            }

            if (input is "/diff")
            {
                try
                {
                    var git = await session.App.GitDiffToRemoteAsync(session.Config.Cwd);
                    var diff = git.TryGetProperty("workingTreeDiff", out var wtd) && wtd.ValueKind == JsonValueKind.String ? wtd.GetString() ?? "" : "";
                    DiffPager.Show("git diff", string.IsNullOrWhiteSpace(diff) ? "(no local diff)" : diff);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                }
                continue;
            }

            if (input.StartsWith("/review", StringComparison.Ordinal))
            {
                var rest = input.Length > 7 ? input[7..].Trim() : "";
                try
                {
                    if (rest.Length == 0)
                        await session.App.ReviewUncommittedAsync();
                    else if (rest is "main" or "master" or "develop")
                        await session.App.ReviewBaseBranchAsync(rest);
                    else
                        await session.App.ReviewAsync(new { type = "custom", instructions = rest });
                    AnsiConsole.MarkupLine("[grey]review started[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                }
                continue;
            }

            if (input.StartsWith("/effort", StringComparison.Ordinal))
            {
                var rest = input.Length > 7 ? input[7..].Trim() : "";
                if (rest is "low" or "medium" or "high" or "minimal" or "xhigh" or "ultra")
                {
                    session.App.WriteConfigAsync("model_reasoning_effort", rest).GetAwaiter().GetResult();
                    session.ApplySettings(effort: rest);
                    AnsiConsole.MarkupLine($"[yellow]effort[/] {rest}");
                }
                else
                {
                    AnsiConsole.MarkupLine("usage: /effort low|medium|high|minimal|xhigh|ultra");
                }
                continue;
            }

            if (input.StartsWith("/name", StringComparison.Ordinal) || input.StartsWith("/rename", StringComparison.Ordinal))
            {
                var rest = input.StartsWith("/rename", StringComparison.Ordinal)
                    ? (input.Length > 7 ? input[7..].Trim() : "")
                    : (input.Length > 5 ? input[5..].Trim() : "");
                if (rest.Length == 0)
                {
                    AnsiConsole.MarkupLine(Markup.Escape(session.Thread.Title));
                }
                else
                {
                    session.SetName(rest);
                    AnsiConsole.MarkupLine($"[yellow]named[/] {Markup.Escape(rest)}");
                }
                continue;
            }

            if (input.StartsWith("/goal", StringComparison.Ordinal))
            {
                var rest = input.Length > 5 ? input[5..].Trim() : "";
                var cmd = GoalSlash.parse(rest);
                if (cmd.IsShow)
                {
                    session.SyncGoal();
                    AnsiConsole.MarkupLine(FormatGoalLine(session));
                }
                else if (cmd.IsClear)
                {
                    session.ClearGoal();
                    AnsiConsole.MarkupLine("[grey]goal cleared[/]");
                }
                else if (cmd.IsPause)
                {
                    if (string.IsNullOrWhiteSpace(session.Goal))
                    {
                        AnsiConsole.MarkupLine("[grey]no goal[/]");
                    }
                    else
                    {
                        session.SetGoal(null, ThreadGoal.Paused);
                        AnsiConsole.MarkupLine($"[yellow]goal paused:[/] {Markup.Escape(session.Goal ?? "")}");
                    }
                }
                else if (cmd.IsResume)
                {
                    if (string.IsNullOrWhiteSpace(session.Goal))
                    {
                        AnsiConsole.MarkupLine("[grey]no goal[/]");
                    }
                    else
                    {
                        session.SetGoal(null, ThreadGoal.Active);
                        AnsiConsole.MarkupLine($"[yellow]goal:[/] {Markup.Escape(session.Goal ?? "")}");
                    }
                }
                else if (cmd is GoalSlash.Command.Set set)
                {
                    session.SetGoal(set.Item, ThreadGoal.Active);
                    AnsiConsole.MarkupLine($"[yellow]goal:[/] {Markup.Escape(set.Item)}");
                }
                continue;
            }

            if (input.StartsWith("/queue", StringComparison.Ordinal))
            {
                var rest = input.Length > 6 ? input[6..].Trim() : "";
                try
                {
                    if (rest.Length == 0 || rest is "list")
                    {
                        var listed = session.App.QueueListAsync().GetAwaiter().GetResult();
                        var n = 0;
                        if (listed.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var q in data.EnumerateArray())
                            {
                                n++;
                                var qid = q.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                                var text = q.TryGetProperty("text", out var tEl) ? tEl.GetString() ?? "" : q.GetRawText();
                                AnsiConsole.MarkupLine($"{Markup.Escape(qid)}  {Markup.Escape(Trim(text, 80))}");
                            }
                        }
                        if (n == 0) AnsiConsole.MarkupLine("[grey]queue empty[/]  usage: /queue TEXT");
                    }
                    else if (rest is "start")
                    {
                        session.App.QueueStartAsync().GetAwaiter().GetResult();
                        AnsiConsole.MarkupLine("[yellow]queue started[/]");
                    }
                    else
                    {
                        session.App.QueueAddAsync(rest).GetAwaiter().GetResult();
                        AnsiConsole.MarkupLine("[green]queued[/] " + Markup.Escape(Trim(rest, 80)));
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                }
                continue;
            }

            if (input is "/history")
            {
                if (PromptHistory.Count == 0)
                {
                    AnsiConsole.MarkupLine("[grey]no prompt history[/]");
                    continue;
                }

                input = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("prompt history").PageSize(12).AddChoices(PromptHistory.Take(20)));
            }

            if (input is "/mcp")
            {
                try
                {
                    var listed = await session.App.ListMcpAsync();
                    var count = 0;
                    if (listed.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var spec in data.EnumerateArray())
                        {
                            count++;
                            var name = spec.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            var command = spec.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
                            var enabled = !(spec.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False);
                            AnsiConsole.MarkupLine($"{Markup.Escape(name)}  {Markup.Escape(command)} enabled={enabled}");
                        }
                    }
                    if (count == 0) AnsiConsole.MarkupLine("[grey]No mcp_servers in config.toml[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                }
                continue;
            }

            if (!input.StartsWith('/'))
            {
                PromptHistory.Remove(input);
                PromptHistory.Insert(0, input);
                if (PromptHistory.Count > 50) PromptHistory.RemoveAt(PromptHistory.Count - 1);
            }

            if (liveTurn is not null && !input.StartsWith('/'))
            {
                try
                {
                    session.App.SteerAsync(input).GetAwaiter().GetResult();
                    AnsiConsole.MarkupLine("[yellow]steered into the active turn[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                }
                continue;
            }

            liveTurn = new CancellationTokenSource();
            input = SkillSlash.Expand(input, session.Config.Home, session.Config.Cwd);
            AnsiConsole.MarkupLine("[grey]▸ working[/]  " + Markup.Escape(TuiStatus.Line(session.Config, session.Thread.Id, session.Thread.Title)));
            try
            {
                await session.RunTurnAsync(input, liveTurn.Token);
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("[yellow]turn cancelled[/]");
            }
            finally
            {
                liveTurn.Dispose();
                liveTurn = null;
                if (UseLive) LiveTui.Paint(session, LiveHistory, "", "idle  " + TuiStatus.Line(session.Config, session.Thread.Id, session.Thread.Title));
                else AnsiConsole.MarkupLine("[grey]▸ idle[/]  " + Markup.Escape(TuiStatus.Line(session.Config, session.Thread.Id, session.Thread.Title)));
            }
        }
    }

    private static bool TryResolveSlash(ref string input)
    {
        if (!input.StartsWith('/'))
        {
            return true;
        }

        var token = input.Split(' ', 2)[0];
        if (SlashCommands.Any(c => string.Equals(c, token, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var matches = token is "/"
            ? SlashCommands
            : SlashCommands.Where(c => c.StartsWith(token, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0)
        {
            AnsiConsole.MarkupLine("[grey]unknown slash command[/]");
            return false;
        }

        if (matches.Length == 1)
        {
            var rest = input.Length > token.Length ? input[token.Length..] : "";
            input = matches[0] + rest;
            return true;
        }

        var picked = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("slash commands")
                .PageSize(16)
                .AddChoices(matches));
        input = picked;
        return true;
    }

    private static string FormatGoalLine(TuiAgent session)
    {
        try
        {
            session.SyncGoal();
        }
        catch
        {
            // keep last cached goal
        }

        return string.IsNullOrWhiteSpace(session.Goal)
            ? "[grey]goal[/] none"
            : $"[grey]goal[/] {Markup.Escape(session.GoalStatus)}  {Markup.Escape(session.Goal)}";
    }

    private static void RenderBanner(TuiAgent session)
    {
        var cfg = session.Config;
        var key = string.IsNullOrWhiteSpace(cfg.Provider.ApiKey) ? "missing key" : "key set";
        AnsiConsole.Write(new FigletText("CodexSharp").Color(Color.Gold1));
        AnsiConsole.MarkupLine(
            $"[grey]thread[/] {session.Thread.Id}   [grey]model[/] {Markup.Escape(cfg.Model)}   [grey]provider[/] {Markup.Escape(cfg.Provider.Id)} ({key})");
        AnsiConsole.MarkupLine(
            $"[grey]sandbox[/] {Markup.Escape(cfg.SandboxMode)}   [grey]approval[/] {Markup.Escape(cfg.ApprovalPolicy)}   [grey]cwd[/] {Markup.Escape(cfg.Cwd)}");
        AnsiConsole.MarkupLine(FormatGoalLine(session));
        var status = TuiStatus.Line(session.Config, session.Thread.Id, session.Thread.Title);
        if (!string.IsNullOrWhiteSpace(status))
            AnsiConsole.MarkupLine($"[grey]status[/] {Markup.Escape(status)}");
        var pet = TuiPets.Ascii();
        if (!string.IsNullOrWhiteSpace(pet))
            AnsiConsole.MarkupLine($"[grey]pet[/] {Markup.Escape(TuiPets.Current)} {Markup.Escape(pet)}");
        AnsiConsole.MarkupLine($"[grey]keys[/] {Markup.Escape(TuiKeymap.Hint())}");
        try { Console.Title = TerminalTitle.Sanitize(TuiStatus.Title(session.Config, session.Thread.Id, session.Thread.Title)); } catch { /* ignore */ }
        AnsiConsole.MarkupLine("[grey]Type /help for commands. This is a .NET recreation of the Codex harness, not the official OpenAI app.[/]");
    }

    private static void RenderEvent(AgentEvent evt)
    {
        if (evt is AgentEvent.ApprovalNeeded approval)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[yellow]Approval required[/] ({Markup.Escape(approval.Reason)})");
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(approval.Cwd)}[/]");
            AnsiConsole.Write(new Panel(Markup.Escape(approval.Command)).Header("command"));
            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Decision").AddChoices("allow", "deny", "abort"));
            LiveSession?.App.RespondApprovalAsync(approval.RequestId, choice == "allow", choice == "allow" ? null : "user denied").GetAwaiter().GetResult();
            return;
        }
        if (evt is AgentEvent.UserInputNeeded userInput)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Agent needs input[/]");
            var answer = AnsiConsole.Ask<string>("Answer:");
            LiveSession?.App.RespondUserInputAsync(userInput.RequestId, "{\"answers\":{\"q1\":\"" + (answer ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}}").GetAwaiter().GetResult();
            return;
        }
        if (evt is AgentEvent.McpElicitationNeeded elicit)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]MCP elicitation[/]");
            var answer = AnsiConsole.Ask<string>("Answer (empty declines):");
            var accept = !string.IsNullOrWhiteSpace(answer);
            LiveSession?.App.RespondMcpElicitationAsync(elicit.RequestId, accept, accept ? answer : null).GetAwaiter().GetResult();
            return;
        }

        if (evt is AgentEvent.TurnCompleted)
        {
            DesktopNotify.Send("CodexSharp turn completed");
            TurnNotify.Fire("agent-turn-complete", LiveSession?.Thread.Id ?? "", null);
            var agent = LiveSession;
            if (agent is not null)
            {
                _ = Task.Run(async () =>
                {
                    try { await agent.App.QueueStartAsync(); }
                    catch { /* empty queue or not experimental */ }
                });
            }
        }

        if (UseLive)
        {
            LiveTui.Append(LiveHistory, evt);
            if (evt is AgentEvent.ItemCompleted item && item.Item.Kind == "agent_message" && !string.IsNullOrWhiteSpace(item.Item.Text))
                LastAgent = item.Item.Text;
            if (LiveSession is not null)
            {
                try { LiveTui.Paint(LiveSession, LiveHistory, "", "working"); }
                catch { /* ignore resize races */ }
            }
            return;
        }

        switch (evt)
        {
            case AgentEvent.ItemDelta delta:
                Console.Write(delta.Text);
                LastAgent += delta.Text;
                break;
            case AgentEvent.CommandOutputDelta output:
                Console.Write(output.Text);
                break;
            case AgentEvent.ItemCompleted item when item.Item.Kind == "user_message":
                Console.WriteLine();
                AnsiConsole.Write(new Panel(Markup.Escape(Trim(item.Item.Text, 800)))
                    .Header("you")
                    .BorderColor(Color.Aqua));
                break;
            case AgentEvent.ItemCompleted item when item.Item.Kind == "agent_message":
                Console.WriteLine();
                if (!string.IsNullOrWhiteSpace(item.Item.Text)) LastAgent = item.Item.Text;
                AnsiConsole.MarkupLine("[grey]────────────────────────────────────────[/]");
                break;
            case AgentEvent.ItemCompleted item when item.Item.Kind == "reasoning":
                Console.WriteLine();
                AnsiConsole.MarkupLine("[grey italic]" + Markup.Escape(Trim(item.Item.Text, 240)) + "[/]");
                break;
            case AgentEvent.ItemCompleted item when item.Item.Kind is "tool_call" or "command_execution" or "file_change":
                Console.WriteLine();
                AnsiConsole.Write(new Panel(Markup.Escape(Trim(item.Item.Text, 400)))
                    .Header(Markup.Escape(string.IsNullOrWhiteSpace(item.Item.ToolName) ? item.Item.Kind : item.Item.ToolName))
                    .BorderColor(item.Item.Kind == "file_change" ? Color.Green : Color.Blue));
                break;
            case AgentEvent.Failed failed:
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(failed.Message)}[/]");
                break;
        }
    }

    private static void CopyText(string text)
    {
        if (!OperatingSystem.IsWindows()) { AnsiConsole.WriteLine(text); return; }
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("clip") { RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return;
            proc.StandardInput.Write(text);
            proc.StandardInput.Close();
            proc.WaitForExit(1000);
        }
        catch { }
    }

    private static string Trim(string text, int n)
    {
        var oneLine = text.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= n ? oneLine : oneLine[..n] + "...";
    }
}
