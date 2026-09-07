using System.Diagnostics;
using System.Text;
using CodexSharp.Protocol;

namespace CodexSharp.Runtime;

public sealed record CommandExecResult(int ExitCode, string Stdout, string Stderr);

public sealed record UnifiedExecSnapshot(string SessionId, bool Running, int? ExitCode, string Output, int Pid);

/// Connection-scoped `command/exec` sessions from vendor/codex app-server-protocol command_exec.
public sealed class CommandExecBroker
{
    private readonly Dictionary<string, Live> _live = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public async Task<CommandExecResult> RunAsync(
        IReadOnlyList<string> argv,
        string? processId,
        string cwd,
        CodexConfig config,
        bool streamStdin,
        bool streamOutput,
        bool disableTimeout,
        int timeoutMs,
        int outputCap,
        Action<string, string, byte[], bool>? onDelta,
        CancellationToken ct,
        bool tty = false,
        int rows = 24,
        int cols = 80,
        bool skipSandbox = false,
        Action? onStarted = null)
    {
        if (argv.Count == 0 || argv.All(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("command must be a non-empty argv vector");
        }

        var command = string.Join(' ', argv.Where(s => !string.IsNullOrWhiteSpace(s)));
        string workdir;
        if (skipSandbox)
        {
            workdir = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : Path.GetFullPath(cwd);
        }
        else
        {
            var sandbox = new WorkspaceSandbox(config);
            workdir = string.IsNullOrWhiteSpace(cwd) ? sandbox.Cwd : sandbox.Resolve(cwd);
            if (WorkspaceSandbox.ParseSandbox(config.SandboxMode) != SandboxMode.DangerFullAccess
                && !workdir.StartsWith(sandbox.Cwd, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Sandbox denied workdir: {workdir}");
            }
        }
        if (!skipSandbox)
        {
        var policy = ExecPolicy.Evaluate(command, workdir);
        if (policy == ExecDecision.Forbidden)
        {
            throw new InvalidOperationException($"Exec policy forbidden: {command}");
        }
        }

        Directory.CreateDirectory(workdir);
        var job = new JobObject();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var id = string.IsNullOrWhiteSpace(processId) ? Ids.newId("proc") : processId;
        Live live;
        if (tty && OperatingSystem.IsWindows())
        {
            var (file, args) = CommandLine(argv);
            var pty = ConPtyProcess.TryStart(file, args, workdir, (short)rows, (short)cols);
            if (pty is not null)
            {
                live = new Live(id, pty, job, streamOutput, outputCap, stdout, stderr, onDelta, rows, cols);
                try
                {
                    job.Assign(Process.GetProcessById(pty.Pid));
                }
                catch
                {
                    // process may have already exited
                }

                live.StartReaders();
                goto started;
            }
        }

        var psi = NewStartInfo(argv, workdir, streamStdin);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        live = new Live(id, process, job, streamStdin, streamOutput, outputCap, stdout, stderr, onDelta);
        process.Start();
        job.Assign(process);
        live.StartReaders();
        started:

        if (streamStdin || streamOutput || !string.IsNullOrWhiteSpace(processId))
        {
            lock (_gate)
            {
                _live[id] = live;
            }
        }

        onStarted?.Invoke();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!disableTimeout)
        {
            timeoutCts.CancelAfter(timeoutMs < 1 ? 60_000 : timeoutMs);
        }

        var exit = -1;
        try
        {
            exit = await live.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            live.Terminate();
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _live.Remove(id);
            }

            live.CloseStdin();
            try { await live.Readers; } catch { /* ignore */ }
            live.Dispose();
        }

        return new CommandExecResult(exit, stdout.ToString(), stderr.ToString());
    }

    public bool Write(string processId, byte[]? data, bool closeStdin)
    {
        lock (_gate)
        {
            if (!_live.TryGetValue(processId, out var live))
            {
                return false;
            }

            if (data is { Length: > 0 })
            {
                live.Write(data);
            }

            if (closeStdin)
            {
                live.CloseStdin();
            }

            return true;
        }
    }

    public bool Terminate(string processId)
    {
        lock (_gate)
        {
            if (!_live.TryGetValue(processId, out var live))
            {
                return false;
            }

            live.Terminate();
            return true;
        }
    }

    public bool Resize(string processId, int rows, int cols)
    {
        lock (_gate)
        {
            if (!_live.TryGetValue(processId, out var live))
            {
                return false;
            }

            live.Rows = rows;
            live.Cols = cols;
            return true;
        }
    }

    private int _sessions;



    public async Task<UnifiedExecSnapshot> SpawnYieldAsync(
        string cmd,
        string cwd,
        CodexConfig config,
        int yieldMs,
        bool tty,
        Action<string, string>? onOutput,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd))
        {
            throw new ArgumentException("cmd is required");
        }

        IReadOnlyList<string> argv = OperatingSystem.IsWindows()
            ? ["powershell.exe", "-NoLogo", "-NoProfile", "-Command", cmd]
            : ["/bin/bash", "-lc", cmd];
        var sandbox = new WorkspaceSandbox(config);
        var workdir = string.IsNullOrWhiteSpace(cwd) ? sandbox.Cwd : sandbox.Resolve(cwd);
        if (WorkspaceSandbox.ParseSandbox(config.SandboxMode) != SandboxMode.DangerFullAccess
            && !workdir.StartsWith(sandbox.Cwd, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Sandbox denied workdir: {workdir}");
        }

        var policy = ExecPolicy.Evaluate(cmd, workdir);
        if (policy == ExecDecision.Forbidden)
        {
            throw new InvalidOperationException($"Exec policy forbidden: {cmd}");
        }

        Directory.CreateDirectory(workdir);
        var job = new JobObject();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var id = Interlocked.Increment(ref _sessions).ToString();
        Action<string, string, byte[], bool>? delta = onOutput is null
            ? null
            : (_, _, bytes, _) => onOutput(id, Encoding.UTF8.GetString(bytes));

        Live live;
        if (tty && OperatingSystem.IsWindows())
        {
            var (file, args) = CommandLine(argv);
            var pty = ConPtyProcess.TryStart(file, args, workdir, 24, 80);
            if (pty is not null)
            {
                live = new Live(id, pty, job, true, 32_000, stdout, stderr, delta, 24, 80);
                try { job.Assign(Process.GetProcessById(pty.Pid)); } catch { /* ignore */ }
                live.StartReaders();
                goto registered;
            }
        }

        var psi = NewStartInfo(argv, workdir, streamStdin: true);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        live = new Live(id, process, job, true, true, 32_000, stdout, stderr, delta);
        process.Start();
        job.Assign(process);
        live.StartReaders();
        registered:
        lock (_gate)
        {
            _live[id] = live;
        }

        return await YieldAsync(id, Math.Max(1, yieldMs), ct);
    }

    public async Task<UnifiedExecSnapshot> WriteStdinYieldAsync(string sessionId, string chars, int yieldMs, bool close, CancellationToken ct)
    {
        if (!Write(sessionId, string.IsNullOrEmpty(chars) ? [] : Encoding.UTF8.GetBytes(chars), close))
        {
            return new UnifiedExecSnapshot(sessionId, false, null, "unknown session_id", 0);
        }

        return await YieldAsync(sessionId, Math.Max(1, yieldMs), ct);
    }

    public async Task<UnifiedExecSnapshot> YieldAsync(string sessionId, int yieldMs, CancellationToken ct)
    {
        Live live;
        lock (_gate)
        {
            if (!_live.TryGetValue(sessionId, out live!))
            {
                return new UnifiedExecSnapshot(sessionId, false, null, "unknown session_id", 0);
            }
        }

        using var yieldCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        yieldCts.CancelAfter(yieldMs);
        int? exit = null;
        try
        {
            exit = await live.WaitAsync(yieldCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            exit = live.HasExited ? live.ExitCode : null;
        }

        if (exit is null && !live.HasExited)
        {
            return new UnifiedExecSnapshot(sessionId, true, null, live.CombinedOutput, live.Pid);
        }

        try { await live.Readers; } catch { /* ignore */ }
        var code = exit ?? live.ExitCode ?? 0;
        var output = live.CombinedOutput;
        var pid = live.Pid;
        lock (_gate)
        {
            _live.Remove(sessionId);
        }

        live.Dispose();
        return new UnifiedExecSnapshot(sessionId, false, code, output, pid);
    }

    private static ProcessStartInfo NewStartInfo(IReadOnlyList<string> argv, string workdir, bool streamStdin)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = streamStdin,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (streamStdin)
        {
            psi.StandardInputEncoding = Encoding.UTF8;
        }

        if (argv.Count == 1 || LooksLikeShell(argv))
        {
            psi.FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/bash";
            if (OperatingSystem.IsWindows())
            {
                psi.ArgumentList.Add("-NoLogo");
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(string.Join(' ', argv));
            }
            else
            {
                psi.ArgumentList.Add("-lc");
                psi.ArgumentList.Add(string.Join(' ', argv));
            }
        }
        else
        {
            psi.FileName = argv[0];
            foreach (var arg in argv.Skip(1))
            {
                psi.ArgumentList.Add(arg);
            }
        }

        return psi;
    }

    private static (string File, string Args) CommandLine(IReadOnlyList<string> argv)
    {
        if (argv.Count == 1 || LooksLikeShell(argv))
        {
            var joined = string.Join(' ', argv);
            return OperatingSystem.IsWindows()
                ? ("powershell.exe", "-NoLogo -NoProfile -Command " + joined)
                : ("/bin/bash", "-lc " + joined);
        }

        return (argv[0], string.Join(' ', argv.Skip(1)));
    }

    private static bool LooksLikeShell(IReadOnlyList<string> argv) =>
        argv.Count > 0 && (argv[0].Contains(' ') || argv[0] is "echo" or "dir" or "ls" or "cd" or "type" or "cat");

    private sealed class Live : IDisposable
    {
        private readonly Process? _process;
        private readonly ConPtyProcess? _pty;
        private readonly JobObject _job;
        private int _rows = 24;
        private int _cols = 80;
        private readonly bool _stream;
        private readonly int _cap;
        private readonly StringBuilder _stdout;
        private readonly StringBuilder _stderr;
        private readonly Action<string, string, byte[], bool>? _onDelta;
        private int _stdoutBytes;
        private int _stderrBytes;
        private bool _stdinClosed;

        public Live(
            string processId,
            Process process,
            JobObject job,
            bool streamStdin,
            bool stream,
            int cap,
            StringBuilder stdout,
            StringBuilder stderr,
            Action<string, string, byte[], bool>? onDelta)
        {
            ProcessId = processId;
            _process = process;
            _job = job;
            _stream = stream;
            _cap = cap < 1 ? 32_000 : cap;
            _stdout = stdout;
            _stderr = stderr;
            _onDelta = onDelta;
            StreamStdin = streamStdin;
        }

        public Live(
            string processId,
            ConPtyProcess pty,
            JobObject job,
            bool stream,
            int cap,
            StringBuilder stdout,
            StringBuilder stderr,
            Action<string, string, byte[], bool>? onDelta,
            int rows,
            int cols)
        {
            ProcessId = processId;
            _pty = pty;
            _process = null;
            _job = job;
            _stream = stream;
            _cap = cap < 1 ? 32_000 : cap;
            _stdout = stdout;
            _stderr = stderr;
            _onDelta = onDelta;
            StreamStdin = true;
            Rows = rows;
            Cols = cols;
        }

        public string ProcessId { get; }
        public bool StreamStdin { get; }
        public bool HasExited => _pty?.HasExited ?? _process?.HasExited == true;
        public int? ExitCode => _process is { HasExited: true } p ? p.ExitCode : HasExited ? 0 : null;
        public int Pid => _pty?.Pid ?? (_process?.Id ?? 0);
        public string CombinedOutput
        {
            get
            {
                lock (_stdout)
                {
                    var text = _stdout.ToString();
                    var err = _stderr.ToString();
                    return string.IsNullOrEmpty(err) ? text : text + err;
                }
            }
        }
        public int Rows
        {
            get => _rows;
            set { _rows = value; _pty?.Resize(value, Cols); }
        }
        public int Cols
        {
            get => _cols;
            set { _cols = value; _pty?.Resize(Rows, value); }
        }
        public Task Readers { get; private set; } = Task.CompletedTask;

        public void StartReaders()
        {
            if (_pty is not null)
            {
                Readers = Pump(_pty.Output, "stdout", _stdout, () => _stdoutBytes, v => _stdoutBytes = v);
                return;
            }

            Readers = Task.WhenAll(
                Pump(_process!.StandardOutput.BaseStream, "stdout", _stdout, () => _stdoutBytes, v => _stdoutBytes = v),
                Pump(_process.StandardError.BaseStream, "stderr", _stderr, () => _stderrBytes, v => _stderrBytes = v));
        }

        public void Write(byte[] data)
        {
            if (_stdinClosed)
            {
                return;
            }

            if (_pty is not null)
            {
                _pty.Input.Write(data);
                _pty.Input.Flush();
                return;
            }

            _process!.StandardInput.BaseStream.Write(data);
            _process.StandardInput.BaseStream.Flush();
        }

        public void CloseStdin()
        {
            if (_stdinClosed || !StreamStdin)
            {
                _stdinClosed = true;
                return;
            }

            _stdinClosed = true;
            try
            {
                if (_pty is not null)
                {
                    _pty.Input.Close();
                }
                else
                {
                    _process!.StandardInput.Close();
                }
            }
            catch { /* ignore */ }
        }

        public void Terminate()
        {
            try
            {
                if (_pty is not null)
                {
                    _pty.Terminate();
                }
                else
                {
                    _process!.Kill(true);
                }
            }
            catch { /* ignore */ }
        }

        public async Task<int> WaitAsync(CancellationToken ct)
        {
            if (_pty is not null)
            {
                await _pty.WaitForExitAsync(ct);
                return 0;
            }

            await _process!.WaitForExitAsync(ct);
            return _process.ExitCode;
        }

        public void Dispose()
        {
            CloseStdin();
            try { _process?.Dispose(); } catch { /* ignore */ }
            try { _pty?.Dispose(); } catch { /* ignore */ }
            _job.Dispose();
        }

        private async Task Pump(Stream stream, string name, StringBuilder buffer, Func<int> get, Action<int> set)
        {
            var buf = new byte[4096];
            try
            {
                while (true)
                {
                    var n = await stream.ReadAsync(buf);
                    if (n <= 0)
                    {
                        break;
                    }

                    var used = get();
                    var remaining = Math.Max(0, _cap - used);
                    var take = Math.Min(n, remaining);
                    if (take > 0)
                    {
                        set(used + take);
                        var chunk = buf.AsSpan(0, take).ToArray();
                        buffer.Append(Encoding.UTF8.GetString(chunk));
                        if (_stream)
                        {
                            _onDelta?.Invoke(ProcessId, name, chunk, take < n);
                        }
                    }
                    else if (_stream)
                    {
                        _onDelta?.Invoke(ProcessId, name, [], true);
                        break;
                    }
                }
            }
            catch
            {
                // process exited
            }
        }
    }
}
