using System.IO.Pipes;
using System.Text;

namespace CodexSharp.Runtime;

public static class DesktopActivation
{
    public const string MutexName = @"Local\CodexSharp.Desktop";
    public const string PipeName = "CodexSharp.Desktop.DeepLink";

    private static DeepLink? _startup;
    private static Mutex? _mutex;

    public static event Action<DeepLink>? Received;

    public static void SetStartup(DeepLink? link) => _startup = link;

    public static DeepLink? ConsumeStartup()
    {
        var link = _startup;
        _startup = null;
        return link;
    }

    public static void Publish(DeepLink link) => Received?.Invoke(link);

    public static bool TryBecomePrimary(string? mutexName = null)
    {
        var name = string.IsNullOrWhiteSpace(mutexName) ? MutexName : mutexName;
        var mutex = new Mutex(initiallyOwned: true, name, out var created);
        if (!created)
        {
            mutex.Dispose();
            return false;
        }

        _mutex = mutex;
        return true;
    }

    public static bool TrySendToPrimary(string? payload, string? pipeName = null)
    {
        var name = string.IsNullOrWhiteSpace(pipeName) ? PipeName : pipeName;
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
            client.Connect(400);
            var bytes = Encoding.UTF8.GetBytes((payload ?? "") + "\n");
            if (bytes.Length > 8192)
            {
                return false;
            }

            client.Write(bytes);
            client.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void Listen(Action<string> onMessage, CancellationToken cancellationToken, string? pipeName = null)
    {
        var name = string.IsNullOrWhiteSpace(pipeName) ? PipeName : pipeName;
        _ = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        name,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is not null)
                    {
                        onMessage(line);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
            }
        }, cancellationToken);
    }
}
