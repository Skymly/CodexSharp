using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexSharp.AppServer;
using CodexSharp.Client;
using CodexSharp.Protocol;
using CodexSharp.Runtime;
using Spectre.Console;

namespace CodexSharp.Cli;

internal static class UpdateCli
{
    public static int Run()
    {
        AnsiConsole.MarkupLine("[yellow]notConfigured[/]  CodexSharp is an independent .NET build and has no auto-updater.");
        AnsiConsole.MarkupLine("Rebuild from source: [grey]dotnet build CodexSharp.slnx[/]");
        return 1;
    }
}
