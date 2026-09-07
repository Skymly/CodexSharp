using CodexSharp.Client;

namespace CodexSharp.AppServer;

public interface IAppServerHandle : IAsyncDisposable
{
    AppServerClient Client { get; }
    string Kind { get; }
}

public static class AppServerHandle
{
    public static IAppServerHandle Start()
    {
        var mode = Environment.GetEnvironmentVariable("CODEXSHARP_APP_SERVER_MODE");
        if (string.Equals(mode, "in-process", StringComparison.OrdinalIgnoreCase))
        {
            return InProcessAppServer.Start();
        }

        var exe = FindCliExecutable();
        if (exe is null)
        {
            return InProcessAppServer.Start();
        }

        try
        {
            var client = AppServerClient.Start(exe, "app-server");
            return new ProcessAppServer(client, exe);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"codexsharp app-server spawn failed ({ex.Message}); falling back to in-process");
            return InProcessAppServer.Start();
        }
    }

    public static string? FindCliExecutable()
    {
        var env = Environment.GetEnvironmentVariable("CODEXSHARP_APP_SERVER");
        if (!string.IsNullOrWhiteSpace(env))
        {
            if (string.Equals(env, "in-process", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (File.Exists(env))
            {
                return env;
            }
        }

        var dir = AppContext.BaseDirectory;
        var names = OperatingSystem.IsWindows()
            ? new[] { "codexsharp.exe", "codexsharp" }
            : new[] { "codexsharp", "codexsharp.exe" };
        foreach (var name in names)
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}

internal sealed class ProcessAppServer(AppServerClient client, string executable) : IAppServerHandle
{
    public AppServerClient Client { get; } = client;
    public string Kind => "process:" + executable;

    public ValueTask DisposeAsync() => Client.DisposeAsync();
}