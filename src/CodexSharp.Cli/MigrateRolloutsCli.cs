using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexSharp.AppServer;
using CodexSharp.Client;
using CodexSharp.Protocol;
using CodexSharp.Runtime;
using Spectre.Console;

namespace CodexSharp.Cli;

internal static class MigrateRolloutsCli
{
    public static int Run(string[] args)
    {
        var apply = args.Any(a => a is "--apply");
        var json = args.Any(a => a is "--json");
        var verbose = args.Any(a => a is "--verbose" or "-v");
        var ids = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--thread" && i + 1 < args.Length) ids.Add(args[++i]);
            else if (args[i].StartsWith("--thread=", StringComparison.Ordinal)) ids.Add(args[i][9..]);
        }
        var report = RolloutMigrate.Inspect(ids, apply);
        if (json)
        {
            AnsiConsole.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
            return 0;
        }
        AnsiConsole.MarkupLine($"storage  {Markup.Escape(report.Storage)}  sqlite={report.SqliteThreadHistory}  applied={report.Applied}");
        AnsiConsole.MarkupLine($"threads  {report.Threads}  lines {report.Lines}  invalid {report.InvalidLines}  bytes {report.Bytes}");
        if (verbose)
        {
            foreach (var item in report.Items)
                AnsiConsole.MarkupLine($"{Markup.Escape(item.Id)}  lines={item.Lines} invalid={item.InvalidLines} {(item.Archived ? "archived" : "active")}");
        }
        AnsiConsole.MarkupLine("[grey]" + Markup.Escape(report.Note) + "[/]");
        return 0;
    }
}
