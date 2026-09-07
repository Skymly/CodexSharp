using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using CodexSharp.Client;

namespace CodexSharp.AppServer;

public static class WebSocketAppServer
{
    private const string WsMagic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    public static Task RunAsync(string listen, CancellationToken ct = default) =>
        AppServerLauncher.RunAsync(AppServerTransport.Parse(listen), ct);

    public static async Task RunTcpAsync(IPEndPoint endPoint, CancellationToken ct = default, bool printBanner = true)
    {
        EnsureLoopback(endPoint);
        var listener = new TcpListener(endPoint);
        listener.Start();
        try
        {
            var bound = (IPEndPoint)listener.LocalEndpoint;
            if (printBanner)
            {
                PrintBanner(bound);
            }

            await AcceptTcpAsync(listener, ct);
        }
        finally
        {
            try { listener.Stop(); } catch { /* ignore */ }
        }
    }

    public static WebSocketAppServerSession StartLoopback()
    {
        var cts = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var bound = (IPEndPoint)listener.LocalEndpoint;
        var run = AcceptTcpAsync(listener, cts.Token);
        return new WebSocketAppServerSession(bound, listener, cts, run);
    }

    public static async Task RunUnixAsync(string path, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(128);
        using var cancelReg = ct.Register(() =>
        {
            try { listener.Dispose(); } catch { /* ignore */ }
        });

        Console.Error.WriteLine($"app-server unix socket listening on {path}");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Socket client;
                try
                {
                    client = await listener.AcceptAsync(ct);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                _ = Task.Run(() => HandleSocketAsync(client, ownsSocket: true, ct), CancellationToken.None);
            }
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
        }
    }

    public static void EnsureLoopback(IPEndPoint endPoint)
    {
        if (!IPAddress.IsLoopback(endPoint.Address))
        {
            throw new InvalidOperationException(
                $"refusing to start non-loopback websocket listener {endPoint} without auth; configure --ws-auth (not yet implemented in CodexSharp)");
        }
    }

    private static async Task AcceptTcpAsync(TcpListener listener, CancellationToken ct)
    {
        using var cancelReg = ct.Register(() =>
        {
            try { listener.Stop(); } catch { /* ignore */ }
        });

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _ = Task.Run(() => HandleTcpClientAsync(client, ct), CancellationToken.None);
        }
    }

    private static async Task HandleTcpClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            client.NoDelay = true;
            await HandleStreamAsync(client.GetStream(), ct);
        }
        catch (Exception)
        {
            // drop the connection
        }
        finally
        {
            try { client.Dispose(); } catch { /* ignore */ }
        }
    }

    private static async Task HandleSocketAsync(Socket socket, bool ownsSocket, CancellationToken ct)
    {
        try
        {
            await using var stream = new NetworkStream(socket, ownsSocket);
            await HandleStreamAsync(stream, ct);
        }
        catch (Exception)
        {
            // drop the connection
        }
        finally
        {
            if (!ownsSocket)
            {
                try { socket.Dispose(); } catch { /* ignore */ }
            }
        }
    }

    internal static async Task HandleStreamAsync(Stream stream, CancellationToken ct)
    {
        var raw = await ReadHeadersAsync(stream, ct);
        if (raw is null)
        {
            return;
        }

        var request = HttpHead.Parse(raw);
        if (request.HasOrigin)
        {
            await WriteStatusAsync(stream, 403, "Forbidden", ct);
            return;
        }

        if (request.Method == "GET" && request.Path is "/readyz" or "/healthz")
        {
            await WriteStatusAsync(stream, 200, "OK", ct);
            return;
        }

        if (request.Method != "GET" || !request.IsWebSocketUpgrade || string.IsNullOrWhiteSpace(request.WebSocketKey))
        {
            await WriteStatusAsync(stream, 400, "Bad Request", ct);
            return;
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(request.WebSocketKey + WsMagic)));
        var handshake =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n" +
            "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake), ct);
        await stream.FlushAsync(ct);

        using var socket = System.Net.WebSockets.WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
        {
            IsServer = true,
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });
        var host = new AppServerHost(new WebSocketTextReader(socket), new WebSocketTextWriter(socket));
        await host.RunAsync(ct);
        if (socket.State == WebSocketState.Open)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
        }
    }

    private static async Task<byte[]?> ReadHeadersAsync(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var one = new byte[1];
        while (ms.Length < 64 * 1024)
        {
            var n = await stream.ReadAsync(one, ct);
            if (n == 0)
            {
                return ms.Length == 0 ? null : ms.ToArray();
            }

            ms.WriteByte(one[0]);
            if (ms.Length >= 4)
            {
                var buf = ms.GetBuffer();
                var len = (int)ms.Length;
                if (buf[len - 4] == (byte)'\r' && buf[len - 3] == (byte)'\n'
                    && buf[len - 2] == (byte)'\r' && buf[len - 1] == (byte)'\n')
                {
                    return ms.ToArray();
                }
            }
        }

        throw new InvalidOperationException("HTTP headers too large");
    }

    private static async Task WriteStatusAsync(Stream stream, int code, string reason, CancellationToken ct)
    {
        var resp = $"HTTP/1.1 {code} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(resp), ct);
        await stream.FlushAsync(ct);
    }

    private static void PrintBanner(IPEndPoint bound)
    {
        Console.Error.WriteLine($"app-server websocket listening on ws://{bound}");
        Console.Error.WriteLine($"readyz:  http://{bound}/readyz");
        Console.Error.WriteLine($"healthz: http://{bound}/healthz");
    }
}

public sealed class WebSocketAppServerSession : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts;
    private readonly Task _run;

    internal WebSocketAppServerSession(IPEndPoint endPoint, TcpListener listener, CancellationTokenSource cts, Task run)
    {
        EndPoint = endPoint;
        _listener = listener;
        _cts = cts;
        _run = run;
    }

    public IPEndPoint EndPoint { get; }
    public string WsUrl => $"ws://{EndPoint}/";
    public string HttpBase => $"http://{EndPoint}";

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* ignore */ }
        try { await _run.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _cts.Dispose();
    }
}

internal readonly struct HttpHead
{
    public string Method { get; init; }
    public string Path { get; init; }
    public bool HasOrigin { get; init; }
    public bool IsWebSocketUpgrade { get; init; }
    public string? WebSocketKey { get; init; }

    public static HttpHead Parse(byte[] raw)
    {
        var text = Encoding.ASCII.GetString(raw);
        var lines = text.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines.Length > 0 ? lines[0] : "";
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var method = parts.Length > 0 ? parts[0].ToUpperInvariant() : "";
        var rawPath = parts.Length > 1 ? parts[1] : "/";
        var path = rawPath.Split('?', 2)[0];
        string? origin = null;
        string? upgrade = null;
        string? key = null;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                break;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase))
            {
                origin = value;
            }
            else if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
            {
                upgrade = value;
            }
            else if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
            {
                key = value;
            }
        }

        return new HttpHead
        {
            Method = method,
            Path = path,
            HasOrigin = origin is not null,
            IsWebSocketUpgrade = upgrade is not null && upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase),
            WebSocketKey = key,
        };
    }
}