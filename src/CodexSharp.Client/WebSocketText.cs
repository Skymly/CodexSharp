using System.Net.WebSockets;
using System.Text;

namespace CodexSharp.Client;

public sealed class WebSocketTextReader(WebSocket socket) : TextReader
{
    public override Task<string?> ReadLineAsync() =>
        ReadLineAsync(CancellationToken.None).AsTask();

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        return null;
    }
}

public sealed class WebSocketTextWriter(WebSocket socket) : TextWriter
{
    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value) { }

    public override void WriteLine(string? value) =>
        WriteLineAsync(value).GetAwaiter().GetResult();

    public override Task WriteLineAsync(string? value) =>
        WriteLineAsync((value ?? "").AsMemory(), CancellationToken.None);

    public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(buffer.ToString());
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    public override void Flush() { }

    public override Task FlushAsync() => Task.CompletedTask;
}