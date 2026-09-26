using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexSharp.AppServer;
using CodexSharp.Client;
using CodexSharp.Protocol;
using CodexSharp.Runtime;
using Spectre.Console;

namespace CodexSharp.Cli;

internal static class ExecServerCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(a => a is "--help" or "-h"))
        {
            AnsiConsole.MarkupLine("codexsharp exec-server [--listen stdio://]");
            AnsiConsole.MarkupLine("[grey]Local process/fs JSON-RPC only. --remote is not implemented.[/]");
            return 0;
        }
        if (args.Any(a => a is "--remote" || a.StartsWith("--remote=", StringComparison.Ordinal)))
        {
            AnsiConsole.MarkupLine("[yellow]notConfigured[/]  Remote exec-server registration is not implemented.");
            return 1;
        }
        var listen = "stdio://";
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--listen" && i + 1 < args.Length) listen = args[i + 1];
            else if (args[i].StartsWith("--listen=", StringComparison.Ordinal)) listen = args[i][9..];
        }
        if (listen.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) || listen.StartsWith("unix://", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[yellow]notConfigured[/]  exec-server listen is stdio only.");
            return 1;
        }
        var host = new ExecServerHost();
        await host.RunAsync();
        return 0;
    }
}
