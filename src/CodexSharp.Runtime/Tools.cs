using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexSharp.Core;
using CodexSharp.Protocol;

namespace CodexSharp.Runtime;

public sealed class ShellExecutor(WorkspaceSandbox sandbox, CodexConfig config, Action<string, string>? onOutput = null)
{
    public async Task<ToolCallResult> RunAsync(ToolCallRequest call, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var root = args.RootElement;
        var command = ReadCommand(root);
        var workdir = root.TryGetProperty("workdir", out var wd) && wd.ValueKind == JsonValueKind.String
            ? sandbox.Resolve(wd.GetString()!)
            : sandbox.Cwd;
        var timeout = root.TryGetProperty("timeout_ms", out var t) && t.TryGetInt32(out var ms) ? ms : 60_000;

        if (WorkspaceSandbox.ParseSandbox(config.SandboxMode) != SandboxMode.DangerFullAccess
            && !WorkspaceSandbox.IsInside(workdir, sandbox.Cwd)
            && !SessionSandbox.Allows(workdir))
        {
            return new ToolCallResult(call.Id, call.Name, $"Sandbox denied workdir: {workdir}", true);
        }

        var policy = ExecPolicy.Evaluate(command, workdir);
        if (policy == ExecDecision.Forbidden)
        {
            return new ToolCallResult(call.Id, call.Name, $"Exec policy forbidden: {command}", true);
        }

        Directory.CreateDirectory(workdir);

        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/bash",
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (OperatingSystem.IsWindows())
        {
            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add(command);
        }

        using var process = new Process { StartInfo = psi };
        using var job = new JobObject();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            onOutput?.Invoke(call.Id, e.Data + Environment.NewLine);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            onOutput?.Invoke(call.Id, e.Data + Environment.NewLine);
        };

        process.Start();
        job.Assign(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* ignore */ }
            return new ToolCallResult(call.Id, call.Name, $"Timed out after {timeout}ms.\n{stdout}\n{stderr}", true);
        }

        var output = $"exit={process.ExitCode}\n{stdout}\n{stderr}".Trim();
        if (output.Length > 32_000)
        {
            output = output[..16_000] + "\n...\n" + output[^8_000..];
        }

        return new ToolCallResult(call.Id, call.Name, output, process.ExitCode != 0);
    }

    private static string ReadCommand(JsonElement root)
    {
        if (root.TryGetProperty("command", out var command))
        {
            if (command.ValueKind == JsonValueKind.String)
            {
                return command.GetString() ?? "";
            }

            if (command.ValueKind == JsonValueKind.Array)
            {
                return string.Join(' ', command.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)));
            }
        }

        return root.TryGetProperty("cmd", out var cmd) ? cmd.GetString() ?? "" : "";
    }
}

public sealed class FileToolExecutor(WorkspaceSandbox sandbox)
{
    public ToolCallResult ReadFile(ToolCallRequest call)
    {
        using var doc = JsonDocument.Parse(call.ArgumentsJson);
        var path = doc.RootElement.GetProperty("path").GetString() ?? "";
        sandbox.EnsureReadable(path);
        var full = sandbox.Resolve(path);
        if (!File.Exists(full))
        {
            return new ToolCallResult(call.Id, call.Name, $"File not found: {path}", true);
        }

        var offset = doc.RootElement.TryGetProperty("offset", out var o) && o.TryGetInt32(out var ov) ? ov : 1;
        var limit = doc.RootElement.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv) ? lv : 400;
        var lines = File.ReadAllLines(full);
        var start = Math.Clamp(offset, 1, Math.Max(lines.Length, 1));
        var slice = lines.Skip(start - 1).Take(limit);
        var body = string.Join('\n', slice.Select((line, i) => $"{start + i,4}| {line}"));
        return new ToolCallResult(call.Id, call.Name, body, false);
    }

    public ToolCallResult WriteFile(ToolCallRequest call)
    {
        using var doc = JsonDocument.Parse(call.ArgumentsJson);
        var path = doc.RootElement.GetProperty("path").GetString() ?? "";
        var content = doc.RootElement.GetProperty("content").GetString() ?? "";
        sandbox.EnsureWritable(path);
        var full = sandbox.Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return new ToolCallResult(call.Id, call.Name, $"Wrote {content.Length} bytes to {path}", false);
    }

    public ToolCallResult ListDir(ToolCallRequest call)
    {
        using var doc = JsonDocument.Parse(call.ArgumentsJson);
        var path = doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() ?? "." : ".";
        var depth = doc.RootElement.TryGetProperty("depth", out var d) && d.TryGetInt32(out var dv) ? dv : 1;
        sandbox.EnsureReadable(path);
        var full = sandbox.Resolve(path);
        if (!Directory.Exists(full))
        {
            return new ToolCallResult(call.Id, call.Name, $"Directory not found: {path}", true);
        }

        var rows = new List<string>();
        Enumerate(full, full, 0, Math.Clamp(depth, 1, 4), rows);
        return new ToolCallResult(call.Id, call.Name, string.Join('\n', rows), false);
    }

    private static void Enumerate(string root, string current, int depth, int max, List<string> rows)
    {
        foreach (var dir in Directory.EnumerateDirectories(current).OrderBy(x => x))
        {
            var name = Path.GetRelativePath(root, dir).Replace('\\', '/');
            if (name is ".git" or "bin" or "obj" || name.StartsWith(".git/") || name.StartsWith("bin/") || name.StartsWith("obj/"))
            {
                continue;
            }

            rows.Add($"dir  {name}");
            if (depth + 1 < max)
            {
                Enumerate(root, dir, depth + 1, max, rows);
            }
        }

        foreach (var file in Directory.EnumerateFiles(current).OrderBy(x => x))
        {
            var name = Path.GetRelativePath(root, file).Replace('\\', '/');
            rows.Add($"file {name}");
        }
    }
}

public sealed class BuiltinToolExecutor : IToolExecutor
{
    private readonly CodexConfig _config;
    private readonly WorkspaceSandbox _sandbox;
    private readonly ShellExecutor _shell;
    private readonly FileToolExecutor _files;
    private readonly Action<string, string>? _onOutput;
    private readonly Func<(long Total, long Last)>? _estimateTokens;
    private readonly CommandExecBroker _unified;
    private readonly IImagesClient? _images;

    public BuiltinToolExecutor(CodexConfig config, Action<string, string>? onOutput = null, Func<(long Total, long Last)>? estimateTokens = null, CommandExecBroker? unified = null, IReadOnlyList<string>? extraReadRoots = null, IImagesClient? images = null)
    {
        _config = config;
        _sandbox = new WorkspaceSandbox(config, extraReadRoots);
        _shell = new ShellExecutor(new WorkspaceSandbox(config, extraReadRoots), config, onOutput);
        _files = new FileToolExecutor(new WorkspaceSandbox(config, extraReadRoots));
        _onOutput = onOutput;
        _estimateTokens = estimateTokens;
        _unified = unified ?? new CommandExecBroker();
        _images = images;
    }

    public Task<ToolCallResult> ExecuteAsync(ToolCallRequest call, CancellationToken ct)
    {
        try
        {
            return call.Name switch
            {
                "shell" => _shell.RunAsync(call, ct),
                "read_file" => Task.FromResult(_files.ReadFile(call)),
                "write_file" => Task.FromResult(InPlanMode ? DenyPlan(call) : _files.WriteFile(call)),
                "list_dir" => Task.FromResult(_files.ListDir(call)),
                "grep_files" => Task.FromResult(FileSearch.Grep(call, _sandbox)),
                "load_skill" => Task.FromResult(SkillCatalog.LoadSkill(call, _config.Home, _config.Cwd)),
                "request_user_input" => Task.FromResult(new ToolCallResult(call.Id, call.Name, "{\"answers\":{},\"note\":\"no UI attached\"}", false)),
                "view_image" => Task.FromResult(ViewImage(call)),
                "image_gen" => ImageGenAsync(call, ct),
                "current_time" => Task.FromResult(new ToolCallResult(call.Id, call.Name, DateTimeOffset.Now.ToString("O") + " local\n" + DateTimeOffset.UtcNow.ToString("O") + " utc", false)),
                "sleep" => SleepAsync(call, ct),
                "web_search" => WebSearchAsync(call, ct),
                "file_search" => Task.FromResult(FileSearch.SearchNames(call, _sandbox)),
                "apply_patch" => Task.FromResult(InPlanMode ? DenyPlan(call) : Apply(call)),
                "update_plan" => Task.FromResult(UpdatePlan(call)),
                "get_context_remaining" => Task.FromResult(GetContextRemaining(call)),
                "tool_search" => Task.FromResult(ToolSearch(call)),
                "list_available_plugins_to_install" => Task.FromResult(ListAvailablePlugins(call)),
                "request_plugin_install" => Task.FromResult(RequestPluginInstall(call)),
                "list_mcp_resources" => Task.FromResult(ListMcpResources(call)),
                "read_mcp_resource" => Task.FromResult(new ToolCallResult(call.Id, call.Name, "read_mcp_resource requires a live MCP session", true)),
                "wait_for_environment" => Task.FromResult(WaitForEnvironment(call)),
                "send_message_to_user" => Task.FromResult(SendMessageToUser(call)),
                "exec_command" => ExecCommandAsync(call, ct),
                "write_stdin" => WriteStdinAsync(call, ct),
                "request_permissions" => Task.FromResult(RequestPermissions(call)),
                "new_context_window" => Task.FromResult(new ToolCallResult(call.Id, call.Name, "A new context window will start without summarizing conversation history.", false)),
                "list_mcp_resource_templates" => Task.FromResult(ListMcpResources(call)),
                _ => Task.FromResult(new ToolCallResult(call.Id, call.Name, $"Unknown tool: {call.Name}", true)),
            };
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolCallResult(call.Id, call.Name, ex.Message, true));
        }
    }

    private static async Task<ToolCallResult> WebSearchAsync(ToolCallRequest call, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var query = doc.RootElement.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(query))
        {
            return new ToolCallResult(call.Id, call.Name, "query is required", true);
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var url = "https://api.duckduckgo.com/?q=" + Uri.EscapeDataString(query) + "&format=json&no_html=1&skip_disambig=1";
            var json = await http.GetStringAsync(url, ct);
            using var parsed = JsonDocument.Parse(json);
            var heading = parsed.RootElement.TryGetProperty("Heading", out var h) ? h.GetString() : "";
            var abs = parsed.RootElement.TryGetProperty("AbstractText", out var a) ? a.GetString() : "";
            var related = parsed.RootElement.TryGetProperty("RelatedTopics", out var r) && r.ValueKind == JsonValueKind.Array
                ? string.Join("\n", r.EnumerateArray().Take(5).Select(t => t.TryGetProperty("Text", out var tx) ? tx.GetString() : null).Where(s => !string.IsNullOrWhiteSpace(s)))
                : "";
            var body = string.Join("\n", new[] { heading, abs, related }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return new ToolCallResult(call.Id, call.Name, string.IsNullOrWhiteSpace(body) ? json[..Math.Min(json.Length, 2000)] : body!, false);
        }
        catch (Exception ex)
        {
            return new ToolCallResult(call.Id, call.Name, "web_search failed: " + ex.Message, true);
        }
    }

    private static async Task<ToolCallResult> SleepAsync(ToolCallRequest call, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var ms = doc.RootElement.TryGetProperty("duration_ms", out var d) && d.TryGetInt64(out var v) ? v : 0;
        ms = Math.Clamp(ms, 1, 12L * 60 * 60 * 1000);
        var started = DateTimeOffset.UtcNow;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(ms), ct);
        }
        catch (OperationCanceledException)
        {
            var elapsed = (DateTimeOffset.UtcNow - started).TotalMilliseconds;
            return new ToolCallResult(call.Id, call.Name, $"sleep interrupted after {elapsed:0}ms", false);
        }

        return new ToolCallResult(call.Id, call.Name, $"slept {ms}ms", false);
    }

    private async Task<ToolCallResult> ImageGenAsync(ToolCallRequest call, CancellationToken ct)
    {
        if (InPlanMode)
        {
            return DenyPlan(call);
        }

        if (!ImageGeneration.IsConfigured(_config))
        {
            return new ToolCallResult(call.Id, call.Name, ImageGeneration.NotConfiguredMessage, true);
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var prompt = doc.RootElement.TryGetProperty("prompt", out var p) ? p.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new ToolCallResult(call.Id, call.Name, "prompt is required", true);
        }

        var client = _images ?? new HttpImagesClient(_config);
        GeneratedImage image;
        try
        {
            image = await client.GenerateAsync(prompt.Trim(), ct);
        }
        catch (Exception ex)
        {
            return new ToolCallResult(call.Id, call.Name, ex.Message, true);
        }

        if (image.PngBytes is null || image.PngBytes.Length == 0)
        {
            return new ToolCallResult(call.Id, call.Name, "Images API returned an empty image", true);
        }

        var relative = Path.Combine("generated_images", SanitizeFileToken(call.Id) + ".png");
        _sandbox.EnsureWritable(relative);
        var full = _sandbox.Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, image.PngBytes);
        return new ToolCallResult(call.Id, call.Name, "path=" + full + "\nbytes=" + image.PngBytes.Length + "\ntype=.png", false);
    }

    private static string SanitizeFileToken(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray();
        var token = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(token) ? "image" : token;
    }

    private ToolCallResult ViewImage(ToolCallRequest call)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var path = doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
        _sandbox.EnsureReadable(path);
        var full = _sandbox.Resolve(path);
        if (!File.Exists(full))
        {
            return new ToolCallResult(call.Id, call.Name, $"Image not found: {path}", true);
        }
        var info = new FileInfo(full);
        return new ToolCallResult(call.Id, call.Name, $"path={full}\nbytes={info.Length}\ntype={info.Extension}", false);
    }

    private static bool InPlanMode =>
        string.Equals(ConfigService.Peek("collaboration_mode"), "plan", StringComparison.OrdinalIgnoreCase);

    private static ToolCallResult DenyPlan(ToolCallRequest call) =>
        new(call.Id, call.Name, "Mutating tools are not available in Plan Mode.", true);

    private ToolCallResult UpdatePlan(ToolCallRequest call)
    {
        if (string.Equals(ConfigService.Peek("collaboration_mode"), "plan", StringComparison.OrdinalIgnoreCase))
        {
            return new ToolCallResult(call.Id, call.Name, "update_plan is not available in Plan Mode.", true);
        }
        return new ToolCallResult(call.Id, call.Name, call.ArgumentsJson, false);
    }

    private ToolCallResult Apply(ToolCallRequest call)
    {
        using var doc = JsonDocument.Parse(call.ArgumentsJson);
        var patch = doc.RootElement.TryGetProperty("patch", out var p) ? p.GetString() ?? "" : call.ArgumentsJson;
        var result = ApplyPatchEngine.Apply(patch, _sandbox, line => _onOutput?.Invoke(call.Id, line));
        return new ToolCallResult(call.Id, call.Name, result.Output, !result.Ok);
    }

    private ToolCallResult GetContextRemaining(ToolCallRequest call)
    {
        long used = 0;
        if (_estimateTokens is not null)
        {
            used = _estimateTokens().Total;
        }
        var json = JsonSerializer.Serialize(new
        {
            tokens_used = used,
            tokens_left = (long?)null,
            note = "local char/4 estimate; provider context window is unknown",
        });
        return new ToolCallResult(call.Id, call.Name, json, false);
    }

    private static ToolCallResult ToolSearch(ToolCallRequest call)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var query = doc.RootElement.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        var limit = doc.RootElement.TryGetProperty("limit", out var l) && l.TryGetInt32(out var n) ? Math.Clamp(n, 1, 40) : 12;
        var hits = BuiltinTools.all
            .Where(t => t.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || t.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .Select(t => new { name = t.Name, description = t.Description })
            .ToArray();
        return new ToolCallResult(call.Id, call.Name, JsonSerializer.Serialize(new { tools = hits }), false);
    }

    private ToolCallResult ListAvailablePlugins(ToolCallRequest call)
    {
        var plugins = PluginCatalog.ListAvailable(_config.Home, _config.Cwd)
            .Select(p => new { id = p.Name, name = p.Name, description = p.Description, path = p.Path })
            .ToArray();
        return new ToolCallResult(call.Id, call.Name, JsonSerializer.Serialize(new { tools = plugins }), false);
    }

    private static ToolCallResult RequestPluginInstall(ToolCallRequest call)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var name = doc.RootElement.TryGetProperty("pluginName", out var pn) ? pn.GetString() : null;
        name ??= doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return new ToolCallResult(call.Id, call.Name, "pluginName is required", true);
        }

        try
        {
            var plugin = PluginCatalog.Install(name);
            return new ToolCallResult(call.Id, call.Name, JsonSerializer.Serialize(new { id = plugin.Name, path = plugin.Path }), false);
        }
        catch (Exception ex)
        {
            return new ToolCallResult(call.Id, call.Name, ex.Message, true);
        }
    }

    private ToolCallResult ListMcpResources(ToolCallRequest call)
    {
        var servers = ConfigService.LoadMcpServers()
            .Select(s => new { name = s.Id, command = s.Command, url = s.Url, enabled = s.Enabled, resources = Array.Empty<object>() })
            .ToArray();
        return new ToolCallResult(call.Id, call.Name, JsonSerializer.Serialize(new
        {
            servers,
            note = "live resources/list requires an attached MCP session",
        }), false);
    }

    private static ToolCallResult WaitForEnvironment(ToolCallRequest call)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var id = doc.RootElement.TryGetProperty("environment_id", out var e) ? e.GetString() ?? "local" : "local";
        var (status, error) = EnvironmentStore.Status(id);
        return new ToolCallResult(call.Id, call.Name, JsonSerializer.Serialize(new { environment_id = id, status, error }), status == "unknown");
    }

    private async Task<ToolCallResult> ExecCommandAsync(ToolCallRequest call, CancellationToken ct)
    {
        if (!FeatureFlags.IsEnabled("unified_exec"))
        {
            return new ToolCallResult(call.Id, call.Name, "unified_exec is off; run `codexsharp features enable unified_exec`", true);
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var cmd = doc.RootElement.TryGetProperty("cmd", out var c) ? c.GetString() : null;
        cmd ??= doc.RootElement.TryGetProperty("command", out var cmdEl) ? cmdEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(cmd))
        {
            return new ToolCallResult(call.Id, call.Name, "cmd is required", true);
        }

        var workdir = doc.RootElement.TryGetProperty("workdir", out var w) ? w.GetString() : _config.Cwd;
        var yieldMs = doc.RootElement.TryGetProperty("yield_time_ms", out var y) && y.TryGetInt32(out var ym) ? Math.Clamp(ym, 1, 60_000) : 10_000;
        var tty = doc.RootElement.TryGetProperty("tty", out var t) && t.ValueKind == JsonValueKind.True;
        try
        {
            var snap = await _unified.SpawnYieldAsync(cmd, workdir ?? _config.Cwd, _config, yieldMs, tty, _onOutput, ct);
            return SnapshotResult(call, snap);
        }
        catch (Exception ex)
        {
            return new ToolCallResult(call.Id, call.Name, ex.Message, true);
        }
    }

    private async Task<ToolCallResult> WriteStdinAsync(ToolCallRequest call, CancellationToken ct)
    {
        if (!FeatureFlags.IsEnabled("unified_exec"))
        {
            return new ToolCallResult(call.Id, call.Name, "unified_exec is off; run `codexsharp features enable unified_exec`", true);
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        string? sessionId = null;
        if (doc.RootElement.TryGetProperty("session_id", out var sid))
        {
            sessionId = sid.ValueKind == JsonValueKind.Number ? sid.GetRawText() : sid.GetString();
        }

        sessionId ??= doc.RootElement.TryGetProperty("processId", out var pid) ? pid.GetString() : null;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new ToolCallResult(call.Id, call.Name, "session_id is required", true);
        }

        var chars = doc.RootElement.TryGetProperty("chars", out var ch) ? ch.GetString() ?? "" : "";
        var yieldMs = doc.RootElement.TryGetProperty("yield_time_ms", out var y) && y.TryGetInt32(out var ym) ? Math.Clamp(ym, 1, 60_000) : 250;
        var snap = await _unified.WriteStdinYieldAsync(sessionId, chars, yieldMs, close: false, ct);
        var unknown = snap.Output == "unknown session_id" && !snap.Running;
        return SnapshotResult(call, snap, unknown);
    }

    private static ToolCallResult SnapshotResult(ToolCallRequest call, UnifiedExecSnapshot snap, bool error = false)
    {
        var json = JsonSerializer.Serialize(new
        {
            session_id = int.TryParse(snap.SessionId, out var n) ? n : 0,
            process_id = snap.SessionId,
            running = snap.Running,
            exit_code = snap.ExitCode,
            pid = snap.Pid,
            output = snap.Output,
        });
        return new ToolCallResult(call.Id, call.Name, json, error);
    }

    private ToolCallResult RequestPermissions(ToolCallRequest call)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var justification = doc.RootElement.TryGetProperty("justification", out var j) ? j.GetString() ?? "" : "";
        var network = doc.RootElement.TryGetProperty("network", out var net) && net.ValueKind == JsonValueKind.True;
        var granted = new List<string>();
        var skipped = new List<string>();
        if (doc.RootElement.TryGetProperty("additional_readonly_roots", out var roots) && roots.ValueKind == JsonValueKind.Array)
        {
            foreach (var root in roots.EnumerateArray())
            {
                var path = root.GetString();
                if (string.IsNullOrWhiteSpace(path)) continue;
                try
                {
                    granted.Add(SandboxRoots.Add(path));
                }
                catch (Exception ex)
                {
                    skipped.Add(path + ": " + ex.Message);
                }
            }
        }

        var json = JsonSerializer.Serialize(new
        {
            justification,
            granted_readonly_roots = granted,
            skipped,
            network_granted = false,
            network_requested = network,
            note = network ? "network was requested but not auto-enabled; set sandbox_workspace_write.network_access in config.toml" : "writes remain workspace-write",
        });
        return new ToolCallResult(call.Id, call.Name, json, false);
    }

    private static ToolCallResult SendMessageToUser(ToolCallRequest call)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
        message ??= doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(message))
        {
            return new ToolCallResult(call.Id, call.Name, "message is required", true);
        }
        return new ToolCallResult(call.Id, call.Name, message, false);
    }
}

