using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexSharp.AppServer;
using CodexSharp.Client;
using CodexSharp.Protocol;
using CodexSharp.Runtime;
using Spectre.Console;

namespace CodexSharp.Cli;

internal static class DebugCli
{
    public static int Run(string[] args) =>
        RunAsync(args).GetAwaiter().GetResult();

    public static async Task<int> RunAsync(string[] args)
    {
        var cmd = args.ElementAtOrDefault(0) ?? "help";
        return cmd switch
        {
            "models" => Models(),
            "prompt-input" => PromptInput(string.Join(' ', args.Skip(1))),
            "clear-memories" => ClearMemories(),
            "app-server" => await AppServerAsync(args.Skip(1).ToArray()),
            _ => Help(),
        };
    }

    private static int Help()
    {
        AnsiConsole.MarkupLine("debug models | prompt-input [text] | clear-memories | app-server send-message-v2 TEXT");
        return 0;
    }

    private static async Task<int> AppServerAsync(string[] args)
    {
        var sub = args.ElementAtOrDefault(0);
        if (sub is not "send-message-v2" and not "send-message")
        {
            AnsiConsole.MarkupLine("usage: codexsharp debug app-server send-message-v2 TEXT");
            return 2;
        }
        var message = string.Join(' ', args.Skip(1).Where(a => a is not "--json"));
        if (string.IsNullOrWhiteSpace(message))
        {
            AnsiConsole.MarkupLine("[red]message required[/]");
            return 2;
        }

        await using var hosted = InProcessAppServer.Start();
        var session = new AppServerSession(hosted.Client);
        await session.InitializeAsync("codexsharp_debug", "CodexSharp debug");
        await session.StartThreadAsync();
        var json = args.Any(a => a == "--json");
        var rows = new List<object>();
        session.Event += evt =>
        {
            rows.Add(new { type = evt.GetType().Name, evt });
            if (!json)
                AnsiConsole.MarkupLine("[grey]" + Markup.Escape(evt.GetType().Name) + "[/]");
        };
        try
        {
            await session.StartTurnAsync(message);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
            return 1;
        }
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(rows));
        return 0;
    }

    private static int Models()
    {
        var data = ModelCatalog.List().Select(m => new { id = m.Id, provider = m.Provider }).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static int PromptInput(string prompt)
    {
        Console.WriteLine(JsonSerializer.Serialize(PromptDebug.BuildSnapshot(prompt), new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static int ClearMemories()
    {
        MemoryStore.Reset();
        AnsiConsole.MarkupLine("[green]cleared[/] ~/.codexsharp/memories");
        return 0;
    }
}
