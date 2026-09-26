using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexSharp.AppServer;
using CodexSharp.Client;
using CodexSharp.Protocol;
using CodexSharp.Runtime;
using Spectre.Console;

namespace CodexSharp.Cli;

internal static class SessionCli
{
    public static string? ResolveId(string[] args)
    {
        var store = new JsonlThreadStore();
        var last = args.Any(a => a is "--last" or "-l");
        var id = args.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a) && !a.StartsWith('-'));
        if (last || string.IsNullOrWhiteSpace(id))
        {
            return store.List().FirstOrDefault()?.Id;
        }
        return id;
    }

    public static string? ResolveResumeId(string[] args, bool pick = false)
    {
        var all = args.Any(a => a is "--all");
        var includeNonInteractive = all || args.Any(a => a is "--include-non-interactive");
        var last = args.Any(a => a is "--last" or "-l");
        var id = args.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a) && !a.StartsWith('-'));
        var threads = ResumePicker.Candidates(all, includeNonInteractive);
        if (last && threads.Count > 0) return threads[0].Id;
        if (!string.IsNullOrWhiteSpace(id) && id is not "all") return id;
        if (threads.Count == 0) return null;
        if (!pick || threads.Count == 1 || !AnsiConsole.Profile.Capabilities.Interactive) return threads[0].Id;
        var labels = threads.Take(30).Select(thread => ResumePicker.Format(thread, showCwd: all)).ToList();
        var picked = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Which thread?").AddChoices(labels));
        return ResumePicker.IdFromLabel(picked);
    }

    internal static async Task<T> UseAppAsync<T>(Func<AppServerSession, Task<T>> run)
    {
        await using var hosted = InProcessAppServer.Start();
        var app = new AppServerSession(hosted.Client);
        await app.InitializeAsync("codexsharp_cli", "CodexSharp CLI");
        return await run(app);
    }

    public static async Task<int> ForkAsync(string[] args)
    {
        var id = ResolveResumeId(args, pick: true);
        if (string.IsNullOrWhiteSpace(id))
        {
            AnsiConsole.MarkupLine("[yellow]No threads to fork.[/] Try [grey]codexsharp fork --all[/]");
            return 1;
        }
        var childId = await UseAppAsync(async app =>
        {
            await app.ResumeThreadAsync(id);
            return await app.ForkAsync();
        });
        AnsiConsole.MarkupLine($"[green]forked[/] {childId} from {id}");
        return await Tui.ResumeAsync(childId);
    }

    public static async Task<int> ArchiveAsync(string[] args)
    {
        var id = ResolveId(args);
        if (string.IsNullOrWhiteSpace(id)) { AnsiConsole.MarkupLine("[red]thread id required[/]"); return 1; }
        try
        {
            await UseAppAsync(async app => { await app.ArchiveAsync(id); return 0; });
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
            return 1;
        }
        AnsiConsole.MarkupLine($"[yellow]archived[/] {id}");
        return 0;
    }

    public static async Task<int> UnarchiveAsync(string[] args)
    {
        var id = ResolveId(args);
        if (string.IsNullOrWhiteSpace(id)) { AnsiConsole.MarkupLine("[red]thread id required[/]"); return 1; }
        try
        {
            await UseAppAsync(async app => { await app.UnarchiveAsync(id); return 0; });
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
            return 1;
        }
        AnsiConsole.MarkupLine($"[green]unarchived[/] {id}");
        return 0;
    }

    public static async Task<int> DeleteAsync(string[] args)
    {
        var id = ResolveId(args);
        if (string.IsNullOrWhiteSpace(id)) { AnsiConsole.MarkupLine("[red]thread id required[/]"); return 1; }
        try
        {
            await UseAppAsync(async app => { await app.DeleteThreadAsync(id); return 0; });
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
            return 1;
        }
        AnsiConsole.MarkupLine($"[yellow]deleted[/] {id}");
        return 0;
    }

    public static async Task<int> ApplyAsync(string[] args)
    {
        var id = ResolveId(args);
        if (string.IsNullOrWhiteSpace(id))
        {
            id = ResumePicker.Candidates(all: true, includeNonInteractive: true).FirstOrDefault()?.Id;
        }
        if (string.IsNullOrWhiteSpace(id)) { AnsiConsole.MarkupLine("[red]thread id required[/]"); return 1; }
        try
        {
            var result = await UseAppAsync(app => app.ApplyLastPatchAsync(id));
            var ok = result.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            var output = result.TryGetProperty("output", out var outEl) && outEl.ValueKind == JsonValueKind.String ? outEl.GetString() ?? "" : "";
            var cwd = result.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String ? cwdEl.GetString() ?? "" : "";
            if (!ok && output.StartsWith("no apply_patch", StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine("[yellow]no apply_patch in that thread[/]  (cloud apply is notConfigured)");
                return 1;
            }
            AnsiConsole.Write(new Panel(Markup.Escape(output)).Header(ok ? "applied in " + cwd : "apply failed"));
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
            return 1;
        }
    }

    public static async Task<int> ReviewAsync(string[] args)
    {
        string? branch = null;
        string? sha = null;
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--base":
                    branch = args[++i];
                    break;
                case "--commit":
                    sha = args[++i];
                    break;
                default:
                    rest.Add(args[i]);
                    break;
            }
        }
        var cwd = Environment.CurrentDirectory;
        var prompt = !string.IsNullOrWhiteSpace(sha) ? ReviewPrompts.Commit(cwd, sha, null)
            : !string.IsNullOrWhiteSpace(branch) ? ReviewPrompts.BaseBranch(cwd, branch)
            : ReviewPrompts.Uncommitted(cwd);
        if (rest.Count > 0) prompt += Environment.NewLine + string.Join(' ', rest);
        return await Exec.RunAsync(["--full-auto", prompt]);
    }

    public static int Cloud()
    {
        AnsiConsole.MarkupLine("[yellow]Codex Cloud is notConfigured in CodexSharp.[/] No remote task backend is wired.");
        return 1;
    }

    public static async Task<int> QueueAsync(string[] args)
    {
        var cmd = args.ElementAtOrDefault(0) ?? "list";
        string? thread = null;
        string? message = null;
        string? itemId = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--thread" or "-t":
                    thread = args[++i];
                    break;
                case "--message" or "-m":
                    message = args[++i];
                    break;
                case "--id":
                    itemId = args[++i];
                    break;
                case "--last":
                    thread ??= ResolveId(["--last"]);
                    break;
            }
        }
        thread ??= ResolveId(["--last"]);
        try
        {
            return await UseAppAsync(async app =>
            {
                if (cmd is "add")
                {
                    if (string.IsNullOrWhiteSpace(thread) || string.IsNullOrWhiteSpace(message))
                    {
                        AnsiConsole.MarkupLine("[red]usage: queue add --thread ID --message TEXT[/]");
                        return 1;
                    }
                    await app.QueueAddAsync(thread, message);
                    AnsiConsole.MarkupLine("[green]queued[/]");
                    return 0;
                }
                if (cmd is "delete")
                {
                    if (string.IsNullOrWhiteSpace(thread) || string.IsNullOrWhiteSpace(itemId))
                    {
                        AnsiConsole.MarkupLine("[red]usage: queue delete --thread ID --id QID[/]");
                        return 1;
                    }
                    await app.QueueDeleteAsync(thread, itemId);
                    return 0;
                }
                if (string.IsNullOrWhiteSpace(thread))
                {
                    AnsiConsole.MarkupLine("[grey]no thread[/]");
                    return 1;
                }
                var listed = await app.QueueListAsync(thread);
                var count = 0;
                if (listed.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        count++;
                        var qid = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() ?? "" : "";
                        var text = item.TryGetProperty("text", out var tEl) && tEl.ValueKind == JsonValueKind.String
                            ? tEl.GetString() ?? ""
                            : item.GetRawText();
                        AnsiConsole.MarkupLine($"{Markup.Escape(qid)}  {Markup.Escape(text)}");
                    }
                }
                if (count == 0) AnsiConsole.MarkupLine("[grey]queue empty[/]");
                return 0;
            });
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
            return 1;
        }
    }

    public static async Task<int> AgentsAsync()
    {
        return await UseAppAsync(async app =>
        {
            var listed = await app.ListThreadsAsync(limit: 40);
            var count = 0;
            if (listed.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in data.EnumerateArray())
                {
                    var thread = row.TryGetProperty("thread", out var th) ? th : row;
                    var id = thread.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() ?? "" : "";
                    var title = thread.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String ? titleEl.GetString() ?? "" : "";
                    var cwd = thread.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String ? cwdEl.GetString() ?? "" : "";
                    if (id.Length == 0) continue;
                    count++;
                    AnsiConsole.MarkupLine($"{Markup.Escape(id)}  {Markup.Escape(title)}  {Markup.Escape(cwd)}");
                }
            }
            if (count == 0) AnsiConsole.MarkupLine("[grey]no stored threads[/]");
            return 0;
        });
    }

    public static int Completion(string[] args)
    {
        var shell = (args.ElementAtOrDefault(0) ?? "powershell").ToLowerInvariant();
        var names = "exec review apply resume fork archive unarchive delete cloud queue agents completion remote-control login logout account features models mcp skills plugin marketplace project doctor sandbox app app-server help";
        if (shell is "bash")
        {
            Console.WriteLine("# bash completion for codexsharp\ncomplete -W '" + names + "' codexsharp");
            return 0;
        }
        if (shell is "cmd")
        {
            Console.WriteLine("rem cmd completion is limited; see: codexsharp --help");
            return 0;
        }
        var list = string.Join(",", names.Split(' ').Select(n => "'" + n + "'"));
        Console.WriteLine("Register-ArgumentCompleter -CommandName codexsharp -ScriptBlock { param($wordToComplete) @(" + list + ") | Where-Object { $_ -like ($wordToComplete + '*') } }");
        return 0;
    }

    public static async Task<int> RemoteControlAsync(string[] args)
    {
        var cmd = args.ElementAtOrDefault(0) ?? "status";
        try
        {
            return await UseAppAsync(async app =>
            {
                if (cmd is "enable")
                {
                    var snap = await app.EnableRemoteControlAsync();
                    var status = snap.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() ?? "disabled" : "disabled";
                    AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(status)}[/]  (no remote-control transport in CodexSharp)");
                    return status == "disabled" ? 1 : 0;
                }
                if (cmd is "disable")
                {
                    var snap = await app.DisableRemoteControlAsync();
                    var status = snap.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() ?? "disabled" : "disabled";
                    AnsiConsole.MarkupLine(Markup.Escape(status));
                    return 0;
                }
                var read = await app.ReadRemoteControlStatusAsync();
                var st2 = read.TryGetProperty("status", out var s2) && s2.ValueKind == JsonValueKind.String ? s2.GetString() ?? "" : "";
                var install = read.TryGetProperty("installationId", out var i2) && i2.ValueKind == JsonValueKind.String ? i2.GetString() ?? "" : "";
                AnsiConsole.MarkupLine($"status {Markup.Escape(st2)}  installation {Markup.Escape(install)}");
                return 0;
            });
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
            return 1;
        }
    }
}
