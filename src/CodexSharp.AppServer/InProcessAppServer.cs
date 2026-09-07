using System.Diagnostics.CodeAnalysis;
using System.Collections.Concurrent;
using System.Text;
using CodexSharp.Client;
using CodexSharp.Runtime;

namespace CodexSharp.AppServer;

public sealed class LineChannel
{
    private readonly BlockingCollection<string> _lines = new(new ConcurrentQueue<string>());

    public TextReader Reader { get; }
    public TextWriter Writer { get; }

    public LineChannel()
    {
        Reader = new ReaderImpl(_lines);
        Writer = new WriterImpl(_lines);
    }

    public void Complete()
    {
        _lines.CompleteAdding();
    }

    private sealed class WriterImpl(BlockingCollection<string> lines) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { }
        public override void WriteLine(string? value) => lines.Add(value ?? "");
        public override Task WriteLineAsync(string? value)
        {
            WriteLine(value);
            return Task.CompletedTask;
        }
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            lines.Add(buffer.ToString());
            return Task.CompletedTask;
        }
        public override Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override void Flush() { }
    }

    private sealed class ReaderImpl(BlockingCollection<string> lines) : TextReader
    {
        public override string? ReadLine()
        {
            try { return lines.Take(); }
            catch (InvalidOperationException) { return null; }
        }

        public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await Task.Run(() => lines.Take(cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}

public sealed class InProcessAppServer : IAppServerHandle
{
    private readonly CancellationTokenSource _cts;
    private readonly Task _run;
    private readonly LineChannel _toServer;
    private readonly LineChannel _fromServer;
    private readonly AppServerHost _host;

    private InProcessAppServer(AppServerClient client, Task run, CancellationTokenSource cts, LineChannel toServer, LineChannel fromServer, AppServerHost host)
    {
        Client = client;
        _run = run;
        _cts = cts;
        _toServer = toServer;
        _fromServer = fromServer;
        _host = host;
    }

    public AppServerClient Client { get; }

    public string Kind => "in-process";

    public bool TryGetSession(string threadId, [NotNullWhen(true)] out CodexSession? session) =>
        _host.TryGetSession(threadId, out session);

    public static InProcessAppServer Start()
    {
        var toServer = new LineChannel();
        var fromServer = new LineChannel();
        var cts = new CancellationTokenSource();
        var host = new AppServerHost(toServer.Reader, fromServer.Writer);
        var client = new AppServerClient(fromServer.Reader, toServer.Writer);
        var run = host.RunAsync(cts.Token);
        return new InProcessAppServer(client, run, cts, toServer, fromServer, host);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _toServer.Complete();
        _fromServer.Complete();
        try { await Client.DisposeAsync(); } catch { /* ignore */ }
        try { await _run.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
