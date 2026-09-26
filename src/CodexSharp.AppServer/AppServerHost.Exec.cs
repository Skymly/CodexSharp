using System.Text;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using CodexSharp.Core;
using CodexSharp.Protocol;
using CodexSharp.Runtime;


namespace CodexSharp.AppServer;

public sealed partial class AppServerHost
{
    private async Task DispatchExecPolicyCheckAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var command = Str(paramsEl, "command") ?? "";
            var cwd = Str(paramsEl, "cwd") ?? Environment.CurrentDirectory;
            var decision = ExecPolicy.Evaluate(command, cwd);
            Result(id, new
            {
                command,
                decision = decision.ToString().ToLowerInvariant(),
                rules = ExecPolicy.Load(cwd).Count,
            });
            return;
        }
    }

    private async Task DispatchExecPolicyListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var cwd = Str(paramsEl, "cwd") ?? Environment.CurrentDirectory;
            var data = ExecPolicy.Load(cwd).Select(r => new
            {
                pattern = r.Pattern,
                decision = r.Decision.ToString().ToLowerInvariant(),
                justification = r.Justification,
            }).ToArray();
            Result(id, new { data });
            return;
        }
    }

    private async Task DispatchExecPolicyAddUserRuleAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var decisionName = Str(paramsEl, "decision") ?? "prompt";
            var decision = decisionName.ToLowerInvariant() switch
            {
                "forbidden" => ExecDecision.Forbidden,
                "allow" => ExecDecision.Allow,
                _ => ExecDecision.Prompt,
            };
            var pattern = new List<string>();
            if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("pattern", out var pat) && pat.ValueKind == JsonValueKind.Array)
            {
                pattern.AddRange(pat.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0));
            }
            try
            {
                var ruleLine = ExecPolicy.AddUserRule(pattern, decision);
                Result(id, new { line = ruleLine, decision = decision.ToString().ToLowerInvariant() });
            }
            catch (Exception ex)
            {
                Error(id, -32602, ex.Message);
            }
            return;
        }
    }

    private async Task DispatchSandboxExtraReadRootListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            Result(id, new { data = SandboxRoots.List() });
            return;
        }
    }

    private async Task DispatchSandboxExtraReadRootAddAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var path = Str(paramsEl, "path") ?? "";
            try
            {
                var added = SandboxRoots.Add(path);
                Result(id, new { path = added, live = true });
            }
            catch (Exception ex)
            {
                Error(id, -32602, ex.Message);
            }
            return;
        }
    }

    private async Task DispatchItemToolCallAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            if (!_threads.TryGetValue(threadId, out var toolSession))
            {
                Error(id, -32001, "Thread not loaded: " + threadId);
                return;
            }
            var tool = Str(paramsEl, "tool") ?? "";
            var callId = Str(paramsEl, "callId") ?? Ids.call();
            var args = "{}";
            if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("arguments", out var argEl))
                args = argEl.GetRawText();
            var call = new ToolCallRequest(callId, tool, args);
            if (ToolApproval.needsApproval(toolSession.Config, call))
            {
                var capturedId = id.Clone();
                var capturedHasId = hasId;
                _ = Task.Run(async () =>
                {
                    var denied = await DenyReasonAsync(toolSession, call, ct);
                    if (denied is not null)
                    {
                        if (capturedHasId) Result(capturedId, new { success = false, status = "denied", contentItems = new object[] { new { type = "inputText", text = "Approval denied: " + denied } } });
                        return;
                    }

                    var exec = new BuiltinToolExecutor(toolSession.Config);
                    var result = await exec.ExecuteAsync(call, ct);
                    if (capturedHasId) Result(capturedId, new { success = !result.IsError, contentItems = new object[] { new { type = "inputText", text = result.Output } } });
                }, ct);
                return;
            }

            var execNow = new BuiltinToolExecutor(toolSession.Config);
            var resultNow = await execNow.ExecuteAsync(call, ct);
            Result(id, new { success = !resultNow.IsError, contentItems = new object[] { new { type = "inputText", text = resultNow.Output } } });
            return;
        }
    }

    private async Task DispatchCommandExecAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (HasPermissionProfile(paramsEl) && !RequireExperimental(id, hasId, "command/exec.permissionProfile"))
            {
                return;
            }

            var argv = ReadCommandArgv(paramsEl);
            if (argv.Count == 0)
            {
                Error(id, -32602, "command must be a non-empty argv vector");
                return;
            }

            var processId = Str(paramsEl, "processId");
            var tty = Flag(paramsEl, "tty");
            var streamStdin = tty || Flag(paramsEl, "streamStdin");
            var streamOut = tty || Flag(paramsEl, "streamStdoutStderr");
            if ((tty || streamStdin || streamOut) && string.IsNullOrWhiteSpace(processId))
            {
                Error(id, -32602, "processId is required for tty/streamStdin/streamStdoutStderr");
                return;
            }

            var cfg = ConfigService.Load();
            var cwd = Str(paramsEl, "cwd") ?? cfg.Cwd;
            var disableTimeout = Flag(paramsEl, "disableTimeout");
            var timeoutMs = Int(paramsEl, "timeoutMs", 60_000);
            var cap = Flag(paramsEl, "disableOutputCap") ? int.MaxValue : Int(paramsEl, "outputBytesCap", 32_000);
            var rows = 24;
            var cols = 80;
            if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("size", out var ttySize) && ttySize.ValueKind == JsonValueKind.Object)
            {
                rows = Int(ttySize, "rows", 24);
                cols = Int(ttySize, "cols", 80);
            }
            var gateThread = Str(paramsEl, "threadId");
            CodexSession? gateSession = null;
            if (!string.IsNullOrWhiteSpace(gateThread))
            {
                _threads.TryGetValue(gateThread, out gateSession);
            }

            gateSession ??= _threads.Values.FirstOrDefault();
            var gateCall = new ToolCallRequest("gate", "exec_command", JsonSerializer.Serialize(new { command = string.Join(' ', argv) }));
            var capturedId = id.Clone();
            var capturedHasId = hasId;
            if (CommandExecNeedsPrompt(cfg, argv) && gateSession is null)
            {
                Result(id, new { started = false, status = "denied", output = "Approval denied: no session approver", isError = false });
                return;
            }

            var sessionForGate = gateSession;
            _ = Task.Run(async () =>
            {
                try
                {
                    if (CommandExecNeedsPrompt(cfg, argv))
                    {
                        var denied = await DenyReasonAsync(sessionForGate!, gateCall, ct);
                        if (denied is not null)
                        {
                            if (capturedHasId) Result(capturedId, new { started = false, status = "denied", output = "Approval denied: " + denied, isError = false });
                            return;
                        }
                    }

                    var result = await _exec.RunAsync(
                        argv,
                        processId,
                        cwd,
                        cfg,
                        streamStdin,
                        streamOut,
                        disableTimeout,
                        timeoutMs,
                        cap,
                        streamOut ? (pid, stream, bytes, capReached) =>
                            Notify("command/exec/outputDelta", new
                            {
                                processId = pid,
                                stream,
                                deltaBase64 = Convert.ToBase64String(bytes),
                                capReached,
                            })
                        : null,
                        ct,
                        tty,
                        rows,
                        cols);
                    Result(capturedId, new
                    {
                        exitCode = result.ExitCode,
                        stdout = streamOut ? "" : result.Stdout,
                        stderr = streamOut ? "" : result.Stderr,
                        output = (result.Stdout + result.Stderr).Trim(),
                        isError = result.ExitCode != 0,
                    });
                }
                catch (OperationCanceledException)
                {
                    if (capturedHasId) Error(capturedId, -32000, "command/exec cancelled");
                }
                catch (Exception ex)
                {
                    if (capturedHasId) Error(capturedId, -32000, ex.Message);
                }
            }, ct);
            return;
        }
    }

    private async Task DispatchProcessSpawnAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "process/spawn")) return;
            var argv = ReadCommandArgv(paramsEl);
            var handle = Str(paramsEl, "processHandle") ?? "";
            if (argv.Count == 0)
            {
                Error(id, -32602, "command must be a non-empty argv vector");
                return;
            }
            if (string.IsNullOrWhiteSpace(handle))
            {
                Error(id, -32602, "processHandle is required");
                return;
            }
            var cwd = Str(paramsEl, "cwd") ?? Environment.CurrentDirectory;
            var tty = Flag(paramsEl, "tty");
            var streamStdin = tty || Flag(paramsEl, "streamStdin");
            var streamOut = tty || Flag(paramsEl, "streamStdoutStderr");
            var disableTimeout = Flag(paramsEl, "disableTimeout");
            var timeoutMs = Int(paramsEl, "timeoutMs", 60_000);
            var cap = Int(paramsEl, "outputBytesCap", 32_000);
            var rows = 24;
            var cols = 80;
            if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("size", out var spawnSize) && spawnSize.ValueKind == JsonValueKind.Object)
            {
                rows = Int(spawnSize, "rows", 24);
                cols = Int(spawnSize, "cols", 80);
            }
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await _exec.RunAsync(
                        argv, handle, cwd, ConfigService.Load(), streamStdin, streamOut, disableTimeout,
                        timeoutMs, cap,
                        streamOut ? (pid, stream, bytes, capReached) =>
                            Notify("process/outputDelta", new { processHandle = pid, stream, deltaBase64 = Convert.ToBase64String(bytes), capReached })
                        : null,
                        ct, tty, rows, cols, skipSandbox: true, onStarted: () => started.TrySetResult());
                    Notify("process/exited", new { processHandle = handle, exitCode = result.ExitCode, stdout = streamOut ? "" : result.Stdout, stderr = streamOut ? "" : result.Stderr, stdoutCapReached = false, stderrCapReached = false });
                }
                catch (Exception ex)
                {
                    started.TrySetException(ex);
                    Notify("process/exited", new { processHandle = handle, exitCode = -1, stdout = "", stderr = ex.Message, stdoutCapReached = false, stderrCapReached = false });
                }
            }, ct);
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
                Result(id, new { });
            }
            catch (Exception ex)
            {
                Error(id, -32000, ex.Message);
            }
            return;
        }
    }

    private async Task DispatchProcessWriteStdinAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "process/writeStdin")) return;
            var handle = Str(paramsEl, "processHandle") ?? "";
            byte[]? data = null;
            var b64 = Str(paramsEl, "deltaBase64");
            if (!string.IsNullOrWhiteSpace(b64)) data = Convert.FromBase64String(b64);
            if (!_exec.Write(handle, data, Flag(paramsEl, "closeStdin")))
            {
                Error(id, -32001, "process not found: " + handle);
                return;
            }
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchProcessKillAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "process/kill")) return;
            var handle = Str(paramsEl, "processHandle") ?? "";
            if (!_exec.Terminate(handle))
            {
                Error(id, -32001, "process not found: " + handle);
                return;
            }
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchProcessResizePtyAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "process/resizePty")) return;
            var handle = Str(paramsEl, "processHandle") ?? "";
            if (!_exec.Resize(handle, Int(paramsEl, "rows", 24), Int(paramsEl, "cols", 80)))
            {
                Error(id, -32001, "process not found: " + handle);
                return;
            }
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchWindowsSandboxReadinessAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var snap = WindowsSandbox.Read();
            Result(id, new { status = snap.Status, jobObject = snap.JobObject, mode = snap.Mode, implementation = snap.Status == WindowsSandbox.LimitedStatus ? "jobObject-kill-on-close" : "notConfigured" });
            return;
        }
    }

    private async Task DispatchWindowsSandboxSetupStartAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var mode = Str(paramsEl, "mode") ?? "";
            if (mode != "elevated" && mode != "unelevated")
            {
                Error(id, -32602, "mode must be elevated or unelevated");
                return;
            }

            var cwd = Str(paramsEl, "cwd");
            try
            {
                var snap = WindowsSandbox.Setup(mode, cwd);
                var ok = snap.Status == WindowsSandbox.LimitedStatus;
                Notify("windowsSandbox/setupCompleted", new { mode, success = ok, error = snap.Error });
                Result(id, new
                {
                    started = true,
                    status = snap.Status,
                    error = snap.Error,
                    jobObject = snap.JobObject,
                    mode = snap.Mode,
                    implementation = ok ? "jobObject-kill-on-close" : "notConfigured",
                });
            }
            catch (Exception ex)
            {
                Notify("windowsSandbox/setupCompleted", new { mode, success = false, error = ex.Message });
                Result(id, new { started = true, status = "errored", error = ex.Message, implementation = "notConfigured" });
            }
            return;
        }
    }

    private async Task DispatchItemToolRespondUserInputAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? _threads.Keys.FirstOrDefault() ?? "";
            var requestId = Str(paramsEl, "requestId") ?? "";
            var answers = Str(paramsEl, "answersJson") ?? (paramsEl.ValueKind == JsonValueKind.Object ? paramsEl.GetRawText() : "{}");
            if (_threads.TryGetValue(threadId, out var inputSession))
            {
                inputSession.ResolveUserInput(requestId, answers);
            }
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchItemPermissionsRespondAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? _threads.Keys.FirstOrDefault() ?? "";
            var requestId = Str(paramsEl, "requestId") ?? "";
            var allow = paramsEl.ValueKind == JsonValueKind.Object
                        && paramsEl.TryGetProperty("allow", out var a)
                        && a.ValueKind == JsonValueKind.True;
            if (_threads.TryGetValue(threadId, out var session))
            {
                session.ResolveApproval(requestId, allow, Str(paramsEl, "reason"));
            }

            Result(id, new { });
            return;
        }
    }

    private async Task DispatchCommandExecWriteAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var processId = Str(paramsEl, "processId") ?? "";
            byte[]? data = null;
            var b64 = Str(paramsEl, "deltaBase64");
            if (!string.IsNullOrEmpty(b64))
            {
                data = Convert.FromBase64String(b64);
            }
            if (!_exec.Write(processId, data, Flag(paramsEl, "closeStdin")))
            {
                Error(id, -32001, "No active command exec session");
                return;
            }
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchCommandExecTerminateAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var processId = Str(paramsEl, "processId") ?? "";
            if (!_exec.Terminate(processId))
            {
                Error(id, -32001, "No active command exec session");
                return;
            }
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchCommandExecResizeAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var processId = Str(paramsEl, "processId") ?? "";
            var rows = 24;
            var cols = 80;
            if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Object)
            {
                rows = Int(size, "rows", 24);
                cols = Int(size, "cols", 80);
            }
            if (!_exec.Resize(processId, rows, cols))
            {
                Error(id, -32001, "No active command exec session");
                return;
            }
            Result(id, new { });
            return;
        }
    }

}
