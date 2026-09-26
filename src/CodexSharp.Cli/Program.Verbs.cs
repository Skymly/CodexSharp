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
    private static int Help()
    {
        AnsiConsole.MarkupLine("[bold]CodexSharp[/] — C# + F# + .NET 10 Codex harness");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [cyan]codexsharp[/]                  Interactive TUI");
        AnsiConsole.MarkupLine("  [cyan]codexsharp[/] \"prompt\"         Interactive TUI with an opening turn");
        AnsiConsole.MarkupLine("  [cyan]codexsharp exec[/] \"prompt\"    One-shot non-interactive turn ([grey]--json[/])");
        AnsiConsole.MarkupLine("  [cyan]codexsharp exec resume|fork|review[/]  Non-interactive session ops");
        AnsiConsole.MarkupLine("  [cyan]codexsharp review[/]            Non-interactive review turn");
        AnsiConsole.MarkupLine("  [cyan]codexsharp apply [[id|--last]][/]  Apply last apply_patch in that thread (local; cloud apply is notConfigured)");
        AnsiConsole.MarkupLine("  [cyan]codexsharp app[/]              Launch the desktop app");
        AnsiConsole.MarkupLine("  [cyan]codexsharp app-server[/]       JSON-RPC app server (stdio:// default)");
        AnsiConsole.MarkupLine("  [cyan]codexsharp resume [[id|--last|--all|--include-non-interactive]][/] Resume a stored thread");
        AnsiConsole.MarkupLine("  [cyan]codexsharp fork [[id|--last|--all]][/]  Fork a stored thread");
        AnsiConsole.MarkupLine("  [cyan]codexsharp archive|unarchive|delete[/]  Thread lifecycle");
        AnsiConsole.MarkupLine("  [cyan]codexsharp cloud[/]            Codex Cloud (notConfigured)");
        AnsiConsole.MarkupLine("  [cyan]codexsharp queue[/]            add --thread ID --message TEXT | list | delete");
        AnsiConsole.MarkupLine("  [cyan]codexsharp agents[/]           List stored threads");
        AnsiConsole.MarkupLine("  [cyan]codexsharp completion[/]       powershell | bash | cmd");
        AnsiConsole.MarkupLine("  [cyan]codexsharp remote-control[/]   status | enable | disable (no transport)");
        AnsiConsole.MarkupLine("  [cyan]codexsharp debug[/]            models | prompt-input | clear-memories | app-server send-message-v2");
        AnsiConsole.MarkupLine("  [cyan]codexsharp login[/]            API key, [grey]--device[/], [grey]--chatgpt[/], [grey]--with-api-key[/], [grey]--with-access-token[/]");
        AnsiConsole.MarkupLine("  [cyan]codexsharp logout[/]           Remove auth.json");
        AnsiConsole.MarkupLine("  [cyan]codexsharp account[/]          Show current auth status");
        AnsiConsole.MarkupLine("  [cyan]codexsharp features[/]         list [--json] | enable NAME | disable NAME");
        AnsiConsole.MarkupLine("  [cyan]codexsharp models[/]           List configured models");
        AnsiConsole.MarkupLine("  [cyan]codexsharp mcp[/]              list | get | add | remove | login | logout");
        AnsiConsole.MarkupLine("  [cyan]codexsharp skills[/]           list | enable NAME | disable NAME");
        AnsiConsole.MarkupLine("  [cyan]codexsharp plugin[/]           list [--available] [--json] | add PLUGIN[@MARKET] | remove ID");
        AnsiConsole.MarkupLine("  [cyan]codexsharp marketplace[/]      list | add SOURCE | remove NAME");
        AnsiConsole.MarkupLine("  [cyan]codexsharp project[/]         list | create NAME | delete ID");
        AnsiConsole.MarkupLine("  [cyan]codexsharp doctor[/]           Diagnose local install and config ([grey]--json[/])");
        AnsiConsole.MarkupLine("  [cyan]codexsharp sandbox[/]          status | setup [unelevated|elevated] | windows -- CMD");
        AnsiConsole.MarkupLine("  [cyan]codexsharp execpolicy check[/] COMMAND   Evaluate prefix_rule files");
        AnsiConsole.MarkupLine("  [cyan]codexsharp update[/]           Independent build; no auto-updater");
        AnsiConsole.MarkupLine("  [cyan]codexsharp exec-server[/]      Local process/fs JSON-RPC (stdio)");
        AnsiConsole.MarkupLine("  [cyan]codexsharp migrate-rollouts[/] Inspect JSONL sessions ([grey]--apply[/] [grey]--json[/])");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Flags for exec: [grey]--full-auto[/]  [grey]--yolo[/]  [grey]--model NAME[/]  [grey]--sandbox MODE[/]  [grey]--json[/]  [grey]--output-last-message FILE[/]  [grey]--cd DIR[/]  [grey]--image FILE[/]  [grey]--output-schema FILE[/]  [grey]--skip-git-repo-check[/]  [grey]--ephemeral[/]  [grey]--worktree[/]  [grey]--ignore-user-config[/]  [grey]--ignore-rules[/]  [grey]--add-dir DIR[/]  [grey]--strict-config[/]  [grey]--thread-source SOURCE[/]");
        AnsiConsole.MarkupLine("Flags for app-server: [grey]--listen stdio://|ws://IP:PORT|unix://[PATH]|off[/]  [grey]--stdio[/]");
        return 0;
    }

    private static int Version()
    {
        AnsiConsole.WriteLine("codexsharp 0.1.0 (.NET 10)");
        return 0;
    }

    private static async Task<int> Login(string[] args)
    {
        CodexPaths.EnsureLayout();
        if (args.Length > 0 && args[0] is "status")
        {
            return await AccountAsync();
        }
        if (args.Any(a => a is "--with-access-token" or "--access-token"))
        {
            if (!Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine("[red]--with-access-token expects the token on stdin.[/] Try `printenv CODEX_ACCESS_TOKEN | codexsharp login --with-access-token`.");
                return 1;
            }
            var token = (await Console.In.ReadToEndAsync()).Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                AnsiConsole.MarkupLine("[red]empty access token on stdin[/]");
                return 1;
            }
            AuthService.SaveChatgptTokens(token, "", "");
            AnsiConsole.MarkupLine($"[green]Saved ChatGPT access token[/]  {Markup.Escape(CodexPaths.AuthFile)}");
            AnsiConsole.MarkupLine("[grey]Token was stored locally; CodexSharp did not validate it against ChatGPT.[/]");
            return 0;
        }
        if (args.Any(a => a is "--with-api-key" or "--api-key"))
        {
            string piped;
            if (Console.IsInputRedirected)
            {
                piped = (await Console.In.ReadToEndAsync()).Trim();
            }
            else
            {
                AnsiConsole.MarkupLine("[red]--with-api-key expects the API key on stdin.[/] Try `printenv OPENAI_API_KEY | codexsharp login --with-api-key`.");
                return 1;
            }
            if (string.IsNullOrWhiteSpace(piped))
            {
                AnsiConsole.MarkupLine("[red]empty API key on stdin[/]");
                return 1;
            }
            await SessionCli.UseAppAsync(async app => { await app.LoginApiKeyAsync(piped); return 0; });
            AnsiConsole.MarkupLine($"[green]Saved[/] {Markup.Escape(CodexPaths.AuthFile)}");
            return 0;
        }
        if (args.Any(a => a is "--chatgpt" or "chatgpt" or "--browser"))
        {
            try
            {
                using var login = ChatgptBrowserAuth.Start();
                AnsiConsole.MarkupLine($"Open {Markup.Escape(login.AuthUrl)}");
                AnsiConsole.MarkupLine("[grey]Waiting for browser callback on localhost…[/]");
                await ChatgptBrowserAuth.WaitAsync(login);
                AnsiConsole.MarkupLine($"[green]Signed in with ChatGPT[/]  {Markup.Escape(CodexPaths.AuthFile)}");
                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                return 1;
            }
        }

        if (args.Any(a => a is "--device" or "device" or "--device-auth" or "--device-code"))
        {
            try
            {
                var code = await ChatgptDeviceAuth.RequestAsync();
                AnsiConsole.MarkupLine($"Open {Markup.Escape(code.VerificationUrl)} and enter [bold]{Markup.Escape(code.UserCode)}[/]");
                AnsiConsole.MarkupLine("[grey]Waiting for ChatGPT…[/]");
                await ChatgptDeviceAuth.CompleteAsync(code);
                AnsiConsole.MarkupLine($"[green]Signed in with ChatGPT[/]  {Markup.Escape(CodexPaths.AuthFile)}");
                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                return 1;
            }
        }

        var key = AnsiConsole.Prompt(
            new TextPrompt<string>("API key:").Secret().AllowEmpty());
        if (!string.IsNullOrWhiteSpace(key))
        {
            await SessionCli.UseAppAsync(async app => { await app.LoginApiKeyAsync(key.Trim()); return 0; });
            AnsiConsole.MarkupLine($"[green]Saved[/] {Markup.Escape(CodexPaths.AuthFile)}");
            return 0;
        }

        AnsiConsole.MarkupLine($"Home: [grey]{CodexPaths.Home}[/]");
        AnsiConsole.MarkupLine("Set OPENAI_API_KEY or run [cyan]codexsharp login --device[/] / [cyan]--chatgpt[/]");
        return 0;
    }

    private static async Task<int> LogoutAsync()
    {
        try
        {
            await SessionCli.UseAppAsync(async app =>
            {
                await app.LogoutAsync();
                return 0;
            });
            AnsiConsole.MarkupLine("[green]Logged out[/]");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
            return 1;
        }
    }

    private static async Task<int> McpCmdAsync(string[] args)
    {
        var sub = args.ElementAtOrDefault(0) ?? "list";
        var json = args.Any(a => a == "--json");
        try
        {
            switch (sub)
            {
                case "list":
                {
                    var listed = await SessionCli.UseAppAsync(app => app.ListMcpAsync());
                    var servers = listed.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToArray() : [];
                    if (json)
                    {
                        AnsiConsole.WriteLine(listed.TryGetProperty("data", out var payload) ? payload.GetRawText() : listed.GetRawText());
                        return 0;
                    }
                    if (servers.Length == 0)
                    {
                        AnsiConsole.MarkupLine("[grey]No mcp_servers in config.toml[/]");
                        return 0;
                    }
                    foreach (var spec in servers)
                    {
                        var name = spec.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                        var url = spec.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "";
                        var command = spec.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                        var enabled = !(spec.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False);
                        var transport = string.IsNullOrWhiteSpace(url) ? command : url;
                        AnsiConsole.MarkupLine($"{Markup.Escape(name)}  {Markup.Escape(transport)}  {(enabled ? "[green]on[/]" : "[grey]off[/]")}");
                    }
                    return 0;
                }
                case "get":
                {
                    var name = args.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        AnsiConsole.MarkupLine("usage: codexsharp mcp get NAME");
                        return 2;
                    }
                    var listed = await SessionCli.UseAppAsync(app => app.ListMcpAsync());
                    JsonElement spec = default;
                    var found = listed.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                        && data.EnumerateArray().Any(s =>
                        {
                            if (s.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase))
                            {
                                spec = s;
                                return true;
                            }
                            return false;
                        });
                    if (!found)
                    {
                        AnsiConsole.MarkupLine("[red]not found[/]");
                        return 1;
                    }
                    if (json)
                    {
                        AnsiConsole.WriteLine(spec.GetRawText());
                        return 0;
                    }
                    var id = spec.TryGetProperty("name", out var idEl) ? idEl.GetString() ?? name : name;
                    AnsiConsole.MarkupLine($"name     {Markup.Escape(id)}");
                    var url = spec.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        AnsiConsole.MarkupLine($"url      {Markup.Escape(url)}");
                        if (spec.TryGetProperty("bearerTokenEnvVar", out var b) && b.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(b.GetString()))
                            AnsiConsole.MarkupLine($"bearer   {Markup.Escape(b.GetString() ?? "")}");
                    }
                    else
                    {
                        var command = spec.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                        AnsiConsole.MarkupLine($"command  {Markup.Escape(command)}");
                    }
                    return 0;
                }
                case "add":
                {
                    var name = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        AnsiConsole.MarkupLine("usage: codexsharp mcp add NAME --url URL | mcp add NAME -- COMMAND [ARGS]");
                        return 2;
                    }
                    var urlIdx = Array.IndexOf(args, "--url");
                    if (urlIdx >= 0 && urlIdx + 1 < args.Length)
                    {
                        var bearerIdx = Array.IndexOf(args, "--bearer-token-env-var");
                        var bearer = bearerIdx >= 0 && bearerIdx + 1 < args.Length ? args[bearerIdx + 1] : null;
                        await SessionCli.UseAppAsync(async app => { await app.AddMcpHttpAsync(name, args[urlIdx + 1], bearer); return 0; });
                        AnsiConsole.MarkupLine($"[green]added[/] {Markup.Escape(name)}  http");
                        return 0;
                    }
                    var dd = Array.IndexOf(args, "--");
                    var command = dd >= 0 ? args.Skip(dd + 1).ToArray() : args.Skip(1).SkipWhile(a => a == name || a.StartsWith('-')).ToArray();
                    await SessionCli.UseAppAsync(async app => { await app.AddMcpStdioAsync(name, command); return 0; });
                    AnsiConsole.MarkupLine($"[green]added[/] {Markup.Escape(name)}  stdio");
                    return 0;
                }
                case "remove":
                {
                    var name = args.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        AnsiConsole.MarkupLine("usage: codexsharp mcp remove NAME");
                        return 2;
                    }
                    var removed = McpConfig.Remove(name);
                    AnsiConsole.MarkupLine(removed ? "[green]removed[/]" : "[red]not found[/]");
                    return removed ? 0 : 1;
                }
                case "login":
                case "logout":
                    AnsiConsole.MarkupLine("[yellow]notConfigured[/]  MCP OAuth login/logout is not implemented.");
                    return 1;
                default:
                    AnsiConsole.MarkupLine("usage: codexsharp mcp list|get|add|remove|login|logout");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }

    private static async Task<int> SkillsCmdAsync(string[] args)
    {
        var verb = args.ElementAtOrDefault(0) ?? "list";
        if (verb is "enable" or "disable")
        {
            var name = args.ElementAtOrDefault(1);
            if (string.IsNullOrWhiteSpace(name))
            {
                AnsiConsole.MarkupLine("usage: codexsharp skills enable|disable NAME");
                return 2;
            }
            var enabled = verb == "enable";
            await SessionCli.UseAppAsync(async app => { await app.WriteSkillEnabledAsync(name, enabled); return 0; });
            AnsiConsole.MarkupLine($"{Markup.Escape(name)}  {(enabled ? "[green]on[/]" : "[grey]off[/]")}");
            return 0;
        }

        var listed = await SessionCli.UseAppAsync(app => app.ListSkillsAsync());
        if (!listed.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
        {
            AnsiConsole.MarkupLine("[grey]No SKILL.md found[/]");
            return 0;
        }
        foreach (var skill in data.EnumerateArray())
        {
            var name = skill.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            var path = skill.TryGetProperty("path", out var pth) && pth.ValueKind == JsonValueKind.String ? pth.GetString() ?? "" : "";
            var on = !(skill.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False);
            AnsiConsole.MarkupLine($"{Markup.Escape(name)}  {(on ? "[green]on[/]" : "[grey]off[/]")}  [grey]{Markup.Escape(path)}[/]");
        }
        return 0;
    }

    private static async Task<int> ProjectCliAsync(string[] args)
    {
        var verb = args.ElementAtOrDefault(0) ?? "list";
        try
        {
            switch (verb)
            {
                case "list":
                {
                    var projects = await SessionCli.UseAppAsync(app => app.LoadProjectsAsync());
                    foreach (var project in projects)
                    {
                        AnsiConsole.MarkupLine($"{Markup.Escape(project.Id)}  {Markup.Escape(project.Name)}  [grey]{Markup.Escape(project.Root)}[/]");
                    }
                    if (projects.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[grey]No projects[/]");
                    }
                    return 0;
                }
                case "create":
                {
                    var name = args.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        AnsiConsole.MarkupLine("usage: codexsharp project create NAME");
                        return 2;
                    }
                    var created = await SessionCli.UseAppAsync(app => app.CreateProjectAsync(name, Environment.CurrentDirectory));
                    var project = created.TryGetProperty("project", out var proj) ? proj : created;
                    var pid = project.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() ?? "" : "";
                    var pname = project.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String ? nameEl.GetString() ?? name : name;
                    AnsiConsole.MarkupLine($"[green]created[/] {Markup.Escape(pid)}  {Markup.Escape(pname)}");
                    return 0;
                }
                case "delete":
                {
                    var id = args.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        AnsiConsole.MarkupLine("usage: codexsharp project delete ID");
                        return 2;
                    }
                    try
                    {
                        await SessionCli.UseAppAsync(async app => { await app.DeleteProjectAsync(id); return 0; });
                        AnsiConsole.MarkupLine("[green]deleted[/]");
                    }
                    catch
                    {
                        AnsiConsole.MarkupLine("[red]not found[/]");
                    }
                    return 0;
                }
                default:
                    AnsiConsole.MarkupLine("usage: codexsharp project list|create|delete");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }

    private static async Task<int> PluginCliAsync(string[] args)
    {
        var sub = args.ElementAtOrDefault(0) ?? "list";
        switch (sub)
        {
            case "list":
            {
                var json = args.Any(a => a == "--json");
                var available = args.Any(a => a == "--available");
                var market = "";
                for (var i = 1; i < args.Length; i++)
                {
                    if (args[i] is "--marketplace" or "-m" && i + 1 < args.Length) market = args[++i];
                }
                var listed = await SessionCli.UseAppAsync(app => app.ListPluginsAsync());
                var key = available ? "available" : "data";
                var plugins = listed.TryGetProperty(key, out var data) && data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToArray() : [];
                if (!string.IsNullOrWhiteSpace(market))
                    plugins = plugins.Where(p => (p.TryGetProperty("path", out var pathEl) && pathEl.GetString()?.Contains(market, StringComparison.OrdinalIgnoreCase) == true)).ToArray();
                if (json)
                {
                    AnsiConsole.WriteLine(listed.TryGetProperty(key, out var payload) ? payload.GetRawText() : listed.GetRawText());
                    return 0;
                }
                if (plugins.Length == 0)
                {
                    AnsiConsole.MarkupLine("[grey]No plugins found[/]");
                    return 0;
                }
                foreach (var plugin in plugins)
                {
                    var pname = plugin.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var kind = plugin.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                    var path = plugin.TryGetProperty("path", out var pth) ? pth.GetString() ?? "" : "";
                    AnsiConsole.MarkupLine($"{Markup.Escape(pname)}  [grey]{Markup.Escape(kind)} {Markup.Escape(path)}[/]");
                }
                return 0;
            }
            case "add":
            {
                string? name = null;
                string? market = null;
                string? path = null;
                var json = args.Any(a => a == "--json");
                for (var i = 1; i < args.Length; i++)
                {
                    if (args[i] is "--path" or "--marketplacePath" && i + 1 < args.Length)
                        path = args[++i];
                    else if (args[i] is "--marketplace" or "-m" && i + 1 < args.Length)
                        market = args[++i];
                    else if (args[i] == "--json") { }
                    else if (!args[i].StartsWith('-'))
                        name = args[i];
                }
                if (!string.IsNullOrWhiteSpace(name) && name.Contains('@'))
                {
                    var parts = name.Split('@', 2);
                    name = parts[0];
                    market ??= parts[1];
                }
                if (string.IsNullOrWhiteSpace(name))
                {
                    AnsiConsole.MarkupLine("usage: codexsharp plugin add PLUGIN[@MARKETPLACE] [--marketplace NAME] [--path DIR]");
                    return 2;
                }
                var installed = await SessionCli.UseAppAsync(app => app.InstallPluginAsync(name, path ?? market));
                var plugin = installed.TryGetProperty("plugin", out var plug) ? plug : installed;
                var pname = plugin.TryGetProperty("name", out var n) ? n.GetString() ?? name : name;
                var ppath = plugin.TryGetProperty("path", out var pth) ? pth.GetString() ?? "" : "";
                var kind = plugin.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                if (json)
                {
                    AnsiConsole.WriteLine(plugin.GetRawText());
                    return 0;
                }
                AnsiConsole.MarkupLine($"[green]installed[/] {Markup.Escape(pname)}  [grey]{Markup.Escape(ppath)}[/]");
                return 0;
            }
            case "remove":
            {
                var id = args.ElementAtOrDefault(1);
                if (string.IsNullOrWhiteSpace(id))
                {
                    AnsiConsole.MarkupLine("usage: codexsharp plugin remove ID");
                    return 2;
                }

                try
                {
                    await SessionCli.UseAppAsync(async app => { await app.UninstallPluginAsync(id); return 0; });
                    AnsiConsole.MarkupLine("[green]removed[/]");
                }
                catch
                {
                    AnsiConsole.MarkupLine("[red]not installed[/]");
                }
                return 0;
            }
            default:
                AnsiConsole.MarkupLine("usage: codexsharp plugin list|add|remove");
                return 2;
        }
    }

    private static async Task<int> MarketplaceCliAsync(string[] args)
    {
        var sub = args.ElementAtOrDefault(0) ?? "list";
        switch (sub)
        {
            case "list":
            {
                var listed = await SessionCli.UseAppAsync(app => app.ListMarketplacesAsync());
                if (!listed.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                {
                    AnsiConsole.MarkupLine("[grey]No marketplaces[/]");
                    return 0;
                }

                foreach (var market in data.EnumerateArray())
                {
                    var mname = market.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var source = market.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "";
                    AnsiConsole.MarkupLine($"{Markup.Escape(mname)}  [grey]{Markup.Escape(source)}[/]");
                }

                return 0;
            }
            case "add":
            {
                var source = args.ElementAtOrDefault(1);
                var name = args.ElementAtOrDefault(2);
                if (string.IsNullOrWhiteSpace(source))
                {
                    AnsiConsole.MarkupLine("usage: codexsharp marketplace add SOURCE [NAME]");
                    return 2;
                }

                var added = await SessionCli.UseAppAsync(app => app.AddMarketplaceAsync(source, name));
                var already = added.TryGetProperty("alreadyAdded", out var al) && al.ValueKind == JsonValueKind.True;
                var mname = added.TryGetProperty("marketplaceName", out var mn) ? mn.GetString() ?? source : source;
                AnsiConsole.MarkupLine($"{(already ? "[yellow]already added[/]" : "[green]added[/]")} {Markup.Escape(mname)}");
                return 0;
            }
            case "remove":
            {
                var name = args.ElementAtOrDefault(1);
                if (string.IsNullOrWhiteSpace(name))
                {
                    AnsiConsole.MarkupLine("usage: codexsharp marketplace remove NAME");
                    return 2;
                }

                AnsiConsole.MarkupLine(MarketplaceStore.Remove(name) ? "[green]removed[/]" : "[red]not found[/]");
                return 0;
            }
            default:
                AnsiConsole.MarkupLine("usage: codexsharp marketplace list|add|remove");
                return 2;
        }
    }

    private static int SandboxCli(string[] args)
    {
        var result = SandboxCommand.Run(args);
        foreach (var line in result.Output.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0) continue;
            var color = result.Status is "ok" ? "green" : result.Status is "limited" or "notConfigured" ? "yellow" : "grey";
            if (result.ExitCode != 0 && result.Status != "limited" && result.Status != "notConfigured" && result.Status != "ok")
            {
                color = "red";
            }
            AnsiConsole.MarkupLine($"[{color}]{Markup.Escape(line)}[/]");
        }
        return result.ExitCode;
    }

    private static async Task<int> DoctorAsync(string[] args)
    {
        var report = await SessionCli.UseAppAsync(app => app.RunDoctorAsync());
        var status = report.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() ?? "fail" : "fail";
        if (args.Any(a => a is "--json" or "-j"))
        {
            Console.WriteLine(report.GetRawText());
            return status == "fail" ? 1 : 0;
        }

        AnsiConsole.MarkupLine("[bold]CodexSharp doctor[/]");
        if (report.TryGetProperty("checks", out var checks) && checks.ValueKind == JsonValueKind.Array)
        {
            foreach (var check in checks.EnumerateArray())
            {
                var cs = check.TryGetProperty("status", out var csEl) && csEl.ValueKind == JsonValueKind.String ? csEl.GetString() ?? "fail" : "fail";
                var color = cs == "ok" ? "green" : cs == "warning" ? "yellow" : "red";
                var group = check.TryGetProperty("group", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() ?? "" : "";
                var summary = check.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
                AnsiConsole.MarkupLine($"  [{color}]{Markup.Escape(cs)}[/]  {Markup.Escape(group)}  {Markup.Escape(summary)}");
                if (check.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                {
                    foreach (var detail in details.EnumerateArray().Take(8))
                    {
                        if (detail.ValueKind == JsonValueKind.String)
                            AnsiConsole.MarkupLine("    [grey]" + Markup.Escape(detail.GetString() ?? "") + "[/]");
                    }
                }
                if (check.TryGetProperty("remediation", out var rem) && rem.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(rem.GetString()))
                {
                    AnsiConsole.MarkupLine("    [grey]" + Markup.Escape(rem.GetString() ?? "") + "[/]");
                }
            }
        }
        return status == "fail" ? 1 : 0;
    }

    private static async Task<int> ModelsAsync()
    {
        var listed = await SessionCli.UseAppAsync(app => app.ListModelsAsync());
        if (listed.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in data.EnumerateArray())
            {
                var provider = model.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
                var id = model.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() ?? "" : "";
                AnsiConsole.MarkupLine($"{Markup.Escape(provider)}/{Markup.Escape(id)}");
            }
        }
        return 0;
    }

    private static async Task<int> FeaturesAsync(string[] args)
    {
        var json = args.Any(a => a == "--json");
        var rest = args.Where(a => a != "--json").ToArray();
        if (rest.Length >= 2 && rest[0] is "enable" or "disable")
        {
            var enabled = rest[0] == "enable";
            await SessionCli.UseAppAsync(async app =>
            {
                await app.SetExperimentalFeatureAsync(rest[1], enabled);
                return 0;
            });
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { name = rest[1], enabled }));
                return 0;
            }
            AnsiConsole.MarkupLine($"{Markup.Escape(rest[1])} = {(enabled ? "true" : "false")}");
            return 0;
        }

        var listed = await SessionCli.UseAppAsync(app => app.ListExperimentalFeaturesAsync());
        if (json)
        {
            Console.WriteLine(listed.TryGetProperty("data", out var payload) ? payload.GetRawText() : listed.GetRawText());
            return 0;
        }
        if (listed.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                var on = item.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
                AnsiConsole.MarkupLine($"{Markup.Escape(name)}  {(on ? "[green]on[/]" : "[grey]off[/]")}");
            }
        }
        return 0;
    }

    private static async Task<int> AccountAsync()
    {
        var account = await SessionCli.UseAppAsync(app => app.ReadAccountAsync());
        var auth = "none";
        if (account.TryGetProperty("account", out var acc) && acc.ValueKind == JsonValueKind.Object
            && acc.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
        {
            auth = type.GetString() ?? "apiKey";
        }
        var cfg = await SessionCli.UseAppAsync(app => app.ReadConfigAsync());
        var provider = cfg.TryGetProperty("modelProvider", out var mp) && mp.ValueKind == JsonValueKind.String ? mp.GetString() ?? "" : "";
        var model = cfg.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
        AnsiConsole.MarkupLine($"auth: {Markup.Escape(auth)}");
        AnsiConsole.MarkupLine($"provider: {Markup.Escape(provider)}");
        AnsiConsole.MarkupLine($"model: {Markup.Escape(model)}");
        return 0;
    }

    private static async Task<int> RunAppServerAsync(string[] args)
    {
        Console.Title = "codexsharp app-server";
        AppServerTransport transport;
        try
        {
            transport = AppServerTransport.FromArgs(args);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        try
        {
            await AppServerLauncher.RunAsync(transport, cts.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }

    private static async Task<int> LaunchInteractive(string[] args)
    {
        var o = SharedCli.Parse(args);
        var workdir = string.IsNullOrWhiteSpace(o.Cwd) ? Environment.CurrentDirectory : Path.GetFullPath(o.Cwd);
        if (!o.SkipGit && GitProbe.FindRoot(workdir) is null)
        {
            AnsiConsole.MarkupLine("[yellow]Not a git repository.[/] Use [grey]--skip-git-repo-check[/] to hide this warning.");
        }
        if (o.Worktree)
        {
            var wt = WorktreeSession.Create(workdir);
            workdir = wt.Cwd;
            AnsiConsole.MarkupLine("[grey]worktree[/] " + Markup.Escape(wt.Root));
        }
        ExecPolicy.SuppressRules.Value = o.IgnoreRules;
        SessionSandbox.Reset();
        foreach (var dir in o.AddDirs)
        {
            SessionSandbox.Add(dir);
        }
        var cfg = ConfigService.Load(cwd: workdir, modelOverride: o.Model, sandboxOverride: o.Sandbox, approvalOverride: o.Approval, profile: o.Profile, ignoreUserConfig: o.IgnoreUserConfig, strictConfig: o.StrictConfig);
        var opening = string.Join(' ', o.Rest).Trim('"');
        if (o.Images.Count > 0)
        {
            opening = ExecPrompt.Compose(opening, o.Images, null);
        }
        return await Tui.RunAsync(string.IsNullOrWhiteSpace(opening) ? null : opening, cfg);
    }

    internal static int LaunchDesktop()
    {
        var dir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(dir, "CodexSharp.Desktop.exe"),
            Path.Combine(dir, "CodexSharp.Desktop.dll"),
            Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "CodexSharp.Desktop", "bin", "Debug", "net10.0", "CodexSharp.Desktop.exe")),
        };

        var exe = candidates.FirstOrDefault(File.Exists);
        if (exe is null)
        {
            AnsiConsole.MarkupLine("[red]Desktop app not found. Build CodexSharp.Desktop first.[/]");
            return 1;
        }

        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        return 0;
    }
}
