using System.Net;

namespace CodexSharp.AppServer;

public abstract record AppServerTransport
{
    public const string DefaultListenUrl = "stdio://";

    public sealed record Stdio : AppServerTransport;
    public sealed record Off : AppServerTransport;
    public sealed record WebSocket(IPEndPoint EndPoint) : AppServerTransport;
    public sealed record UnixSocket(string Path) : AppServerTransport;

    public static AppServerTransport Parse(string listenUrl)
    {
        if (string.Equals(listenUrl, DefaultListenUrl, StringComparison.Ordinal)
            || string.Equals(listenUrl, "stdio", StringComparison.Ordinal))
        {
            return new Stdio();
        }

        if (listenUrl.StartsWith("unix://", StringComparison.Ordinal))
        {
            var raw = listenUrl["unix://".Length..];
            string path;
            if (raw.Length == 0)
            {
                path = System.IO.Path.Combine(CodexSharp.Runtime.CodexPaths.Home, "app-server-control", "app-server-control.sock");
            }
            else
            {
                path = System.IO.Path.GetFullPath(raw);
            }

            return new UnixSocket(path);
        }

        if (string.Equals(listenUrl, "off", StringComparison.Ordinal))
        {
            return new Off();
        }

        if (listenUrl.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
        {
            var socketAddr = listenUrl["ws://".Length..];
            if (!IPEndPoint.TryParse(socketAddr, out var endPoint))
            {
                throw new AppServerTransportException(
                    $"invalid websocket --listen URL `{listenUrl}`; expected `ws://IP:PORT`");
            }

            return new WebSocket(endPoint);
        }

        throw new AppServerTransportException(
            $"unsupported --listen URL `{listenUrl}`; expected `stdio://`, `unix://`, `unix://PATH`, `ws://IP:PORT`, or `off`");
    }

    public static AppServerTransport FromArgs(IReadOnlyList<string> args)
    {
        string? listen = null;
        var stdio = false;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "--stdio")
            {
                stdio = true;
                continue;
            }

            if (arg is "--listen")
            {
                if (i + 1 >= args.Count)
                {
                    throw new AppServerTransportException("--listen requires a URL");
                }

                listen = args[++i];
                continue;
            }

            if (arg.StartsWith("--listen=", StringComparison.Ordinal))
            {
                listen = arg["--listen=".Length..];
                continue;
            }

            throw new AppServerTransportException($"unknown app-server argument: {arg}");
        }

        if (stdio && listen is not null)
        {
            throw new AppServerTransportException("--stdio conflicts with --listen");
        }

        if (stdio)
        {
            return new Stdio();
        }

        return Parse(listen ?? DefaultListenUrl);
    }
}

public sealed class AppServerTransportException : Exception
{
    public AppServerTransportException(string message) : base(message)
    {
    }
}

public static class AppServerLauncher
{
    public static async Task RunAsync(AppServerTransport transport, CancellationToken ct = default)
    {
        switch (transport)
        {
            case AppServerTransport.Stdio:
                await new AppServerHost().RunAsync(ct);
                break;
            case AppServerTransport.Off:
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    // process exit
                }

                break;
            case AppServerTransport.WebSocket(var endPoint):
                await WebSocketAppServer.RunTcpAsync(endPoint, ct);
                break;
            case AppServerTransport.UnixSocket(var path):
                await WebSocketAppServer.RunUnixAsync(path, ct);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(transport), transport, "Unknown app-server transport");
        }
    }
}