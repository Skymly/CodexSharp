using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexSharp.AppServer;
using CodexSharp.Client;
using CodexSharp.Protocol;
using CodexSharp.Runtime;
using Spectre.Console;

namespace CodexSharp.Cli;

internal static class ExecpolicyCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var verb = args.ElementAtOrDefault(0);
        if (verb is null or "--help" or "-h" or "help")
        {
            AnsiConsole.MarkupLine("codexsharp execpolicy check [--json] [--] COMMAND...");
            return 0;
        }
        if (verb is not "check")
        {
            AnsiConsole.MarkupLine("[red]unknown execpolicy subcommand[/]");
            return 1;
        }
        var rest = args.Skip(1).ToArray();
        var json = rest.Any(a => a == "--json");
        rest = rest.Where(a => a != "--json" && a != "--").ToArray();
        var command = string.Join(' ', rest);
        if (string.IsNullOrWhiteSpace(command))
        {
            AnsiConsole.MarkupLine("[red]command is required[/]");
            return 1;
        }
        var result = await SessionCli.UseAppAsync(app => app.CheckExecPolicyAsync(command));
        var decision = result.TryGetProperty("decision", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "allow" : "allow";
        if (json)
        {
            AnsiConsole.WriteLine(result.GetRawText());
        }
        else
        {
            AnsiConsole.MarkupLine($"{Markup.Escape(decision)}  {Markup.Escape(command)}");
        }
        return decision == "forbidden" ? 2 : 0;
    }
}
