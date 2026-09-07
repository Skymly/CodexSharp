using System.Text.Json;
using System.Text.Json.Nodes;
using CodexSharp.Protocol;

namespace CodexSharp.Runtime;

/// Local exec-server JSON-RPC (stdio), shaped after vendor/codex/codex-rs/exec-server-protocol.
/// No remote registration, Noise transport, or managed-network proxy.
public sealed class ExecServerHost
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly CommandExecBroker _exec = new();
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private bool _initialized;
    private string _sessionId = "";

    public ExecServerHost(TextReader? input = null, TextWriter? output = null)
    {
        _input = input ?? Console.In;
        _output = output ?? Console.Out;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await _input.ReadLineAsync(ct);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                await HandleLineAsync(line, ct);
            }
            catch (Exception ex)
            {
                Notify("error", new { message = ex.Message });
            }
        }
    }

    private async Task HandleLineAsync(string line, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var method = root.TryGetProperty("method", out var methodEl) ? methodEl.GetString() ?? "" : "";
        var hasId = root.TryGetProperty("id", out var idEl);
        var id = hasId ? idEl.Clone() : default;
        var paramsEl = root.TryGetProperty("params", out var p) ? p : default;

        if (string.IsNullOrEmpty(method)) return;
        if (method == "initialized") return;

        if (method != "initialize" && !_initialized)
        {
            if (hasId) Error(id, -32000, "Not initialized");
            return;
        }

        switch (method)
        {
            case "initialize":
            {
                _initialized = true;
                _sessionId = Ids.newId("execsess");
                Result(id, new
                {
                    sessionId = _sessionId,
                    environmentInfo = EnvironmentInfo(),
                });
                break;
            }
            case "environment/info":
                Result(id, EnvironmentInfo());
                break;
            case "environment/status":
                Result(id, new { status = "ready", kind = "local", remote = false });
                break;
            case "environmentConfig/read":
            {
                var cfg = ConfigService.Load();
                Result(id, new { sandboxMode = cfg.SandboxMode, approvalPolicy = cfg.ApprovalPolicy, cwd = cfg.Cwd });
                break;
            }
            case "process/start":
            {
                var argv = ReadArgv(paramsEl);
                var handle = Str(paramsEl, "processId") ?? Str(paramsEl, "processHandle") ?? "";
                if (argv.Count == 0)
                {
                    Error(id, -32602, "argv must be a non-empty vector");
                    break;
                }
                if (string.IsNullOrWhiteSpace(handle))
                {
                    Error(id, -32602, "processId is required");
                    break;
                }
                var cwd = ReadCwd(paramsEl);
                var tty = Flag(paramsEl, "tty");
                var pipeStdin = tty || Flag(paramsEl, "pipeStdin") || Flag(paramsEl, "streamStdin");
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var result = await _exec.RunAsync(
                            argv, handle, cwd, ConfigService.Load(), pipeStdin, streamOutput: true,
                            disableTimeout: true, timeoutMs: 600_000, outputCap: 256_000,
                            onDelta: (_, stream, bytes, capReached) =>
                                Notify("process/output", new
                                {
                                    processId = handle,
                                    stream,
                                    deltaBase64 = Convert.ToBase64String(bytes),
                                    capReached,
                                }),
                            ct: ct, tty: tty, skipSandbox: false, onStarted: () => started.TrySetResult());
                        Notify("process/exited", new { processId = handle, exitCode = result.ExitCode });
                        Notify("process/closed", new { processId = handle });
                    }
                    catch (Exception ex)
                    {
                        started.TrySetException(ex);
                        Notify("process/exited", new { processId = handle, exitCode = -1, error = ex.Message });
                    }
                }, ct);
                try
                {
                    await started.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
                    Result(id, new { processId = handle });
                }
                catch (Exception ex)
                {
                    Error(id, -32000, ex.Message);
                }
                break;
            }
            case "process/write":
            case "process/writeStdin":
            {
                var handle = Str(paramsEl, "processId") ?? Str(paramsEl, "processHandle") ?? "";
                byte[]? data = null;
                var b64 = Str(paramsEl, "deltaBase64") ?? Str(paramsEl, "dataBase64");
                if (!string.IsNullOrWhiteSpace(b64)) data = Convert.FromBase64String(b64);
                var text = Str(paramsEl, "data") ?? Str(paramsEl, "text");
                if (data is null && !string.IsNullOrEmpty(text)) data = System.Text.Encoding.UTF8.GetBytes(text);
                if (!_exec.Write(handle, data, Flag(paramsEl, "closeStdin") || Flag(paramsEl, "eof")))
                {
                    Error(id, -32001, "process not found: " + handle);
                    break;
                }
                Result(id, new { });
                break;
            }
            case "process/read":
            {
                var handle = Str(paramsEl, "processId") ?? Str(paramsEl, "processHandle") ?? "";
                var snap = await _exec.YieldAsync(handle, Int(paramsEl, "yieldTimeMs", 50), ct);
                Result(id, new { processId = handle, running = snap.Running, exitCode = snap.ExitCode, output = snap.Output, pid = snap.Pid });
                break;
            }
            case "process/signal":
            case "process/terminate":
            case "process/kill":
            {
                var handle = Str(paramsEl, "processId") ?? Str(paramsEl, "processHandle") ?? "";
                if (!_exec.Terminate(handle))
                {
                    Error(id, -32001, "process not found: " + handle);
                    break;
                }
                Result(id, new { });
                break;
            }
            case "fs/readFile":
            {
                var path = ReadPath(paramsEl);
                try
                {
                    var sandbox = new WorkspaceSandbox(ConfigService.Load());
                    sandbox.EnsureReadable(path);
                    var full = sandbox.Resolve(path);
                    if (!File.Exists(full))
                    {
                        Error(id, -32001, "not found: " + path);
                        break;
                    }
                    Result(id, new { path = full, contents = File.ReadAllText(full) });
                }
                catch (Exception ex)
                {
                    Error(id, -32000, ex.Message);
                }
                break;
            }
            case "fs/writeFile":
            {
                var path = ReadPath(paramsEl);
                var contents = Str(paramsEl, "contents") ?? Str(paramsEl, "content") ?? "";
                try
                {
                    var sandbox = new WorkspaceSandbox(ConfigService.Load());
                    sandbox.EnsureWritable(path);
                    var full = sandbox.Resolve(path);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllText(full, contents);
                    Result(id, new { path = full });
                }
                catch (Exception ex)
                {
                    Error(id, -32000, ex.Message);
                }
                break;
            }
            case "fs/readDirectory":
            {
                var path = ReadPath(paramsEl);
                try
                {
                    var sandbox = new WorkspaceSandbox(ConfigService.Load());
                    sandbox.EnsureReadable(path);
                    var full = sandbox.Resolve(path);
                    var entries = Directory.Exists(full)
                        ? Directory.EnumerateFileSystemEntries(full).Select(p => new
                        {
                            name = Path.GetFileName(p),
                            path = p,
                            directory = Directory.Exists(p),
                        }).Take(500).ToArray()
                        : [];
                    Result(id, new { path = full, entries });
                }
                catch (Exception ex)
                {
                    Error(id, -32000, ex.Message);
                }
                break;
            }
            case "fs/getMetadata":
            case "fs/canonicalize":
            {
                var path = ReadPath(paramsEl);
                try
                {
                    var sandbox = new WorkspaceSandbox(ConfigService.Load());
                    sandbox.EnsureReadable(path);
                    var full = Path.GetFullPath(sandbox.Resolve(path));
                    Result(id, new
                    {
                        path = full,
                        exists = File.Exists(full) || Directory.Exists(full),
                        directory = Directory.Exists(full),
                    });
                }
                catch (Exception ex)
                {
                    Error(id, -32000, ex.Message);
                }
                break;
            }
            case "capabilityRoots/discoverV1":
            {
                var roots = new List<string>();
                if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("roots", out var rootsEl) && rootsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in rootsEl.EnumerateArray())
                    {
                        var value = item.ValueKind == JsonValueKind.String ? item.GetString() : Str(item, "path") ?? Str(item, "uri");
                        if (!string.IsNullOrWhiteSpace(value)) roots.Add(StripFileUri(value));
                    }
                }
                if (roots.Count == 0) roots.Add(Environment.CurrentDirectory);
                var manifests = new[] { ".codex-plugin/plugin.json", ".codexsharp-plugin/plugin.json", ".claude-plugin/plugin.json" };
                var found = new List<object>();
                foreach (var scanRoot in roots.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!Directory.Exists(scanRoot)) continue;
                    foreach (var rel in manifests)
                    {
                        var candidate = Path.Combine(scanRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(candidate))
                        {
                            found.Add(new { root = scanRoot, path = candidate, kind = "plugin" });
                        }
                    }
                }
                Result(id, new { data = found });
                break;
            }
            case "http/request":
                Error(id, -32000, "http/request is not implemented on the local exec-server");
                break;
            case "network/policyRequest":
                Error(id, -32000, "managed network proxy is not configured");
                break;
            default:
                if (hasId) Error(id, -32601, "Unknown method: " + method);
                break;
        }
    }

    private static object EnvironmentInfo()
    {
        var cwd = Environment.CurrentDirectory;
        var shell = OperatingSystem.IsWindows()
            ? new { name = "powershell", path = "powershell.exe" }
            : new { name = "bash", path = "/bin/bash" };
        return new
        {
            shell,
            executorVersion = "0.1.0",
            cwd,
            userHomeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            platformOs = OperatingSystem.IsWindows() ? "windows" : (OperatingSystem.IsMacOS() ? "macos" : "linux"),
            tempDir = Path.GetTempPath(),
            capabilities = new
            {
                networkProxyLaunch = false,
                capabilityDiscoverySandbox = false,
                environmentConfigRead = true,
                httpHeaderEnvVars = false,
                sandboxedFileStreaming = false,
                shellSnapshotV2 = false,
            },
        };
    }

    private static IReadOnlyList<string> ReadArgv(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return [];
        if (el.TryGetProperty("argv", out var argv) && argv.ValueKind == JsonValueKind.Array)
        {
            return argv.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToArray();
        }
        if (el.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.Array)
        {
            return command.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToArray();
        }
        var text = Str(el, "command") ?? Str(el, "cmd");
        if (string.IsNullOrWhiteSpace(text)) return [];
        return OperatingSystem.IsWindows()
            ? ["powershell.exe", "-NoLogo", "-NoProfile", "-Command", text]
            : ["/bin/bash", "-lc", text];
    }

    private static string ReadCwd(JsonElement el)
    {
        var raw = Str(el, "cwd") ?? "";
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.Object)
        {
            raw = Str(cwdEl, "path") ?? Str(cwdEl, "uri") ?? raw;
        }
        raw = StripFileUri(raw);
        return string.IsNullOrWhiteSpace(raw) ? Environment.CurrentDirectory : Path.GetFullPath(raw);
    }

    private static string ReadPath(JsonElement el)
    {
        var raw = Str(el, "path") ?? Str(el, "uri") ?? "";
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.Object)
        {
            raw = Str(pathEl, "path") ?? Str(pathEl, "uri") ?? raw;
        }
        return StripFileUri(raw);
    }

    private static string StripFileUri(string raw)
    {
        if (raw.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = raw["file://".Length..];
            if (rest.StartsWith('/') && rest.Length > 2 && rest[2] == ':') rest = rest[1..];
            return Uri.UnescapeDataString(rest.Replace('/', Path.DirectorySeparatorChar));
        }
        return raw;
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool Flag(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement el, string name, int fallback)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.TryGetInt32(out var n)) return n;
        return fallback;
    }

    private void Result(JsonElement id, object result) =>
        Write(new JsonObject { ["id"] = CloneId(id), ["result"] = JsonSerializer.SerializeToNode(result, _json) });

    private void Error(JsonElement id, int code, string message) =>
        Write(new JsonObject
        {
            ["id"] = CloneId(id),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        });

    private void Notify(string method, object paramsObj) =>
        Write(new JsonObject
        {
            ["method"] = method,
            ["params"] = JsonSerializer.SerializeToNode(paramsObj, _json),
        });

    private static JsonNode? CloneId(JsonElement id) =>
        id.ValueKind switch
        {
            JsonValueKind.Number => JsonValue.Create(id.GetInt64()),
            JsonValueKind.String => JsonValue.Create(id.GetString()),
            _ => JsonValue.Create(id.GetRawText()),
        };

    private void Write(JsonNode node)
    {
        lock (_gate)
        {
            _output.WriteLine(node.ToJsonString(_json));
            _output.Flush();
        }
    }
}
