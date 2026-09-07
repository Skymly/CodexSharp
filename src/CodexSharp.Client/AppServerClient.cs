using System.Diagnostics;
using System.Net.WebSockets;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexSharp.Protocol;

namespace CodexSharp.Client;

public sealed class AppServerClient : IAsyncDisposable
{
    private readonly TextReader _output;
    private readonly TextWriter _input;
    private readonly CancellationTokenSource _cts = new();
    private readonly Process? _process;
    private int _nextId = 1;
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    public event Action<JsonElement>? Notification;

    public AppServerClient(TextReader output, TextWriter input, Process? process = null)
    {
        _output = output;
        _input = input;
        _process = process;
        _ = Task.Run(ReadLoop);
    }

    public static async Task<AppServerClient> ConnectWebSocketAsync(string url, CancellationToken ct = default)
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(url), ct);
        return new AppServerClient(new WebSocketTextReader(ws), new WebSocketTextWriter(ws));
    }

    public static AppServerClient Start(string executable, params string[] args)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start app-server");
        return new AppServerClient(process.StandardOutput, process.StandardInput, process);
    }

    public async Task<JsonElement> CallAsync(string method, object? paramsObj = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending)
        {
            _pending[id] = tcs;
        }

        var node = new JsonObject { ["method"] = method, ["id"] = id };
        if (paramsObj is not null)
        {
            node["params"] = JsonSerializer.SerializeToNode(paramsObj);
        }

        await _input.WriteLineAsync(node.ToJsonString().AsMemory(), ct);
        await _input.FlushAsync(ct);
        return await tcs.Task.WaitAsync(ct);
    }

    public async Task NotifyAsync(string method, object? paramsObj = null)
    {
        var node = new JsonObject { ["method"] = method };
        if (paramsObj is not null)
        {
            node["params"] = JsonSerializer.SerializeToNode(paramsObj);
        }

        await _input.WriteLineAsync(node.ToJsonString());
        await _input.FlushAsync();
    }

    public async Task ReplyAsync(JsonElement id, object result, CancellationToken ct = default)
    {
        var node = new JsonObject
        {
            ["id"] = id.ValueKind == JsonValueKind.Number ? JsonValue.Create(id.GetInt32()) : JsonValue.Create(id.GetString()),
            ["result"] = JsonSerializer.SerializeToNode(result),
        };
        await _input.WriteLineAsync(node.ToJsonString().AsMemory(), ct);
        await _input.FlushAsync(ct);
    }

    private async Task ReadLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            var line = await _output.ReadLineAsync();
            if (line is null)
            {
                break;
            }

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(line);
                root = doc.RootElement.Clone();
            }
            catch
            {
                continue;
            }

            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
            {
                var id = idEl.GetInt32();
                lock (_pending)
                {
                    if (_pending.Remove(id, out var tcs))
                    {
                        if (root.TryGetProperty("result", out var result))
                        {
                            tcs.TrySetResult(result.Clone());
                        }
                        else if (root.TryGetProperty("error", out var err))
                        {
                            var message = err.TryGetProperty("message", out var msg) ? msg.GetString() ?? err.GetRawText() : err.GetRawText();
                            tcs.TrySetException(new InvalidOperationException(message));
                        }
                    }
                }
            }
            else
            {
                Notification?.Invoke(root);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_process is not null)
        {
            try { _process.Kill(true); } catch { /* ignore */ }
            _process.Dispose();
        }

        try { _input.Dispose(); } catch { /* pipe already closed */ }
        try { _output.Dispose(); } catch { /* pipe already closed */ }
        await Task.CompletedTask;
    }
}

public sealed record ThreadListItem(string Id, string Title, string Subtitle, bool Pinned, string SectionId, string ProjectId, string Cwd = "");
public sealed record ProjectListItem(string Id, string Name, string Root);

public sealed record SectionListItem(string Id, string Name);

public sealed record FeatureFlagRow(string Name, bool Enabled, string Stage, string? Description);

public sealed record DesktopChrome(string Status, string SideNotes, string Skills, string Model, IReadOnlyList<string> Models, bool NeedsAuth, string SandboxNote, string RemoteControlStatus, string RateLimitsNote, string ComputerUse, string BrowserUse, IReadOnlyList<FeatureFlagRow> Features, bool NeedsTrust, string TrustTarget, string TuiVim = "", string CollaborationMode = "", string Personality = "", string Effort = "", string TuiRaw = "", string TuiTitle = "");

public sealed class AppServerSession
{
    private readonly AppServerClient _client;
    public event Action<AgentEvent>? Event;
    public event Action<string, string>? ExecOutput;
    public event Action<string, string>? FuzzyHits;
    public event Action<string, string>? FsChanged;
    public event Action? QueueChanged;
    public string ThreadId { get; private set; } = "";
    public string Title { get; private set; } = "New thread";
    private JsonElement? _pendingApprovalRpc;

    public AppServerSession(AppServerClient client)
    {
        _client = client;
        _client.Notification += OnNotification;
    }

    public async Task InitializeAsync(string name = "codexsharp_desktop", string title = "CodexSharp Desktop")
    {
        await _client.CallAsync(AppServerMethods.Initialize, new
        {
            clientInfo = new { name, title, version = "0.1.0" },
            capabilities = new { experimentalApi = true },
        });
        await _client.NotifyAsync(AppServerMethods.Initialized);
    }

    public async Task<string> StartThreadAsync(
        string? cwd = null,
        string? title = null,
        string? projectId = null,
        string? model = null,
        string? sandbox = null,
        string? approvalPolicy = null,
        string? source = null,
        string? profile = null,
        bool ignoreUserConfig = false,
        bool strictConfig = false)
    {
        var payload = new JsonObject
        {
            ["cwd"] = cwd ?? Environment.CurrentDirectory,
            ["title"] = title ?? "New thread",
        };
        if (!string.IsNullOrWhiteSpace(projectId)) payload["projectId"] = projectId;
        if (!string.IsNullOrWhiteSpace(model)) payload["model"] = model;
        if (!string.IsNullOrWhiteSpace(sandbox)) payload["sandbox"] = sandbox;
        if (!string.IsNullOrWhiteSpace(approvalPolicy)) payload["approvalPolicy"] = approvalPolicy;
        if (!string.IsNullOrWhiteSpace(source)) payload["source"] = source;
        if (!string.IsNullOrWhiteSpace(profile)) payload["profile"] = profile;
        if (ignoreUserConfig) payload["ignoreUserConfig"] = true;
        if (strictConfig) payload["strictConfig"] = true;
        var result = await _client.CallAsync(AppServerMethods.ThreadStart, payload);
        ThreadId = result.GetProperty("thread").GetProperty("id").GetString() ?? "";
        Title = title ?? "New thread";
        return ThreadId;
    }

    public Task<JsonElement> ReadProjectTrustAsync(string? cwd = null) =>
        _client.CallAsync(AppServerMethods.ProjectTrustRead, new { cwd = cwd ?? Environment.CurrentDirectory });

    public Task<JsonElement> SetProjectTrustAsync(bool trusted = true, string? cwd = null) =>
        _client.CallAsync(AppServerMethods.ProjectTrustSet, new { cwd = cwd ?? Environment.CurrentDirectory, trusted });

    public Task SetNameAsync(string name) =>
        _client.CallAsync(AppServerMethods.ThreadNameSet, new { threadId = ThreadId, name });

    public Task SetGoalAsync(string? objective = null, string? status = null) =>
        _client.CallAsync(AppServerMethods.ThreadGoalSet, new { threadId = ThreadId, objective, status });

    public Task<JsonElement> GetGoalAsync() =>
        _client.CallAsync(AppServerMethods.ThreadGoalGet, new { threadId = ThreadId });

    public Task ClearGoalAsync() =>
        _client.CallAsync(AppServerMethods.ThreadGoalClear, new { threadId = ThreadId });

    public Task<JsonElement> ReadThreadAsync() =>
        _client.CallAsync(AppServerMethods.ThreadRead, new { threadId = ThreadId });

    public Task UpdateSettingsAsync(string? model = null, string? sandboxMode = null, string? approvalPolicy = null, string? cwd = null, string? effort = null) =>
        _client.CallAsync("thread/settings/update", new { threadId = ThreadId, model, sandboxMode, approvalPolicy, cwd, effort });

    public AppServerClient Raw => _client;

    public async Task ResumeThreadAsync(string threadId)
    {
        var result = await _client.CallAsync(AppServerMethods.ThreadResume, new { threadId });
        var thread = result.GetProperty("thread");
        ThreadId = thread.GetProperty("id").GetString() ?? threadId;
        Title = thread.TryGetProperty("title", out var title) ? title.GetString() ?? threadId : threadId;
    }

    public Task<JsonElement> ListItemsAsync() =>
        _client.CallAsync(AppServerMethods.ThreadItemsList, new { threadId = ThreadId });

    public Task ArchiveAsync() => ArchiveAsync(ThreadId);

    public Task ArchiveAsync(string threadId) =>
        _client.CallAsync(AppServerMethods.ThreadArchive, new { threadId });

    public Task UnarchiveAsync(string threadId) =>
        _client.CallAsync(AppServerMethods.ThreadUnarchive, new { threadId });

    public Task DeleteThreadAsync(string threadId) =>
        _client.CallAsync(AppServerMethods.ThreadDelete, new { threadId });

    public Task<JsonElement> ListThreadsAsync(bool archived = false, string? searchTerm = null, int limit = 50, string? projectId = null, bool filterProject = false, string? cwd = null)
    {
        if (filterProject && !string.IsNullOrWhiteSpace(cwd))
            return _client.CallAsync(AppServerMethods.ThreadList, new { archived, searchTerm, limit, projectId, cwd });
        if (filterProject)
            return _client.CallAsync(AppServerMethods.ThreadList, new { archived, searchTerm, limit, projectId });
        if (!string.IsNullOrWhiteSpace(cwd))
            return _client.CallAsync(AppServerMethods.ThreadList, new { archived, searchTerm, limit, cwd });
        return _client.CallAsync(AppServerMethods.ThreadList, new { archived, searchTerm, limit });
    }

    public async Task<IReadOnlyList<ThreadListItem>> LoadThreadsAsync(bool archived = false, int limit = 50, string? projectId = null, bool filterProject = false, string? cwd = null)
    {
        var listed = await ListThreadsAsync(archived, limit: limit, projectId: projectId, filterProject: filterProject, cwd: cwd);
        var rows = new List<ThreadListItem>();
        if (!listed.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        foreach (var item in data.EnumerateArray())
        {
            var thread = item.TryGetProperty("thread", out var inner) ? inner : item;
            var id = Str(thread, "id") ?? "";
            if (id.Length == 0)
            {
                continue;
            }

            var title = Str(thread, "title") ?? id;
            var pinned = thread.TryGetProperty("pinned", out var pin) && pin.ValueKind == JsonValueKind.True;
            var updated = Str(thread, "updatedAt") ?? "";
            if (DateTimeOffset.TryParse(updated, out var ts))
            {
                updated = ts.ToLocalTime().ToString("MM-dd HH:mm");
            }

            rows.Add(new ThreadListItem(
                id,
                (pinned ? "* " : "") + title,
                updated,
                pinned,
                Str(thread, "sectionId") ?? "",
                Str(thread, "projectId") ?? "",
                Str(thread, "cwd") ?? ""));
        }

        return rows;
    }

    public async Task<IReadOnlyList<SectionListItem>> LoadSectionsAsync()
    {
        var listed = await _client.CallAsync(AppServerMethods.ThreadSectionList, new { limit = 100 });
        var rows = new List<SectionListItem>();
        if (!listed.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        foreach (var item in data.EnumerateArray())
        {
            var id = Str(item, "id") ?? "";
            if (id.Length == 0)
            {
                continue;
            }

            rows.Add(new SectionListItem(id, Str(item, "name") ?? id));
        }

        return rows;
    }

    public Task SetPinnedAsync(string threadId, bool isPinned) =>
        _client.CallAsync(AppServerMethods.ThreadMetadataUpdate, new { threadId, isPinned });

    public Task<JsonElement> CreateSectionAsync(string name) =>
        _client.CallAsync(AppServerMethods.ThreadSectionCreate, new { name });

    public Task MoveToSectionAsync(string threadId, string? sectionId) =>
        _client.CallAsync(AppServerMethods.ThreadSectionMove, new { threadId, sectionId });

    public Task<JsonElement> ReadConfigAsync() =>
        _client.CallAsync(AppServerMethods.ConfigRead);

    public Task WriteConfigAsync(string key, string value)
    {
        var payload = new JsonObject { [key] = value };
        return _client.CallAsync(AppServerMethods.ConfigWrite, payload);
    }

    public Task<JsonElement> ListExtraReadRootsAsync() =>
        _client.CallAsync(AppServerMethods.SandboxExtraReadRootList);

    public Task<JsonElement> AddExtraReadRootAsync(string path) =>
        _client.CallAsync(AppServerMethods.SandboxExtraReadRootAdd, new { path });

    public Task<JsonElement> RunDoctorAsync(string? cwd = null) =>
        _client.CallAsync(AppServerMethods.DiagnosticsDoctor, new { cwd = cwd ?? Environment.CurrentDirectory });

    public Task<JsonElement> CheckExecPolicyAsync(string command, string? cwd = null) =>
        _client.CallAsync(AppServerMethods.ExecPolicyCheck, new { command, cwd = cwd ?? Environment.CurrentDirectory });

    public Task<JsonElement> ListExecPolicyAsync(string? cwd = null) =>
        _client.CallAsync(AppServerMethods.ExecPolicyList, new { cwd = cwd ?? Environment.CurrentDirectory });

    public Task<JsonElement> AddExecPolicyRuleAsync(string decision, IReadOnlyList<string> pattern) =>
        _client.CallAsync(AppServerMethods.ExecPolicyAddUserRule, new { decision, pattern });

    public Task<JsonElement> GitDiffToRemoteAsync(string? cwd = null) =>
        _client.CallAsync(AppServerMethods.GitDiffToRemote, new { cwd = cwd ?? Environment.CurrentDirectory });

    public Task<JsonElement> WriteAgentsMarkdownAsync(string? cwd = null) =>
        _client.CallAsync(AppServerMethods.AgentsMarkdownWrite, new { cwd = cwd ?? Environment.CurrentDirectory });

    public Task<JsonElement> ApplyLastPatchAsync(string threadId) =>
        _client.CallAsync(AppServerMethods.LastPatchApply, new { threadId });

    public Task<JsonElement> RecapAsync() =>
        _client.CallAsync(AppServerMethods.ThreadRecapStart, new { threadId = ThreadId });

    public Task<JsonElement> CopyLastAsync(string? kind = null) =>
        _client.CallAsync(AppServerMethods.ThreadCopy, new { threadId = ThreadId, kind });

    public Task<JsonElement> ExportThreadAsync(string? path = null) =>
        _client.CallAsync(AppServerMethods.ThreadExport, new { threadId = ThreadId, path });

    public Task<JsonElement> RolloutPathAsync(string? threadId = null) =>
        _client.CallAsync(AppServerMethods.ThreadRolloutPath, new { threadId = threadId ?? ThreadId });

    public async Task<JsonElement> StartWorktreeAsync(string? cwd = null)
    {
        var result = await _client.CallAsync(AppServerMethods.ThreadWorktreeStart, new { cwd = cwd ?? Environment.CurrentDirectory });
        if (result.TryGetProperty("thread", out var thread) && thread.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
        {
            ThreadId = idEl.GetString() ?? ThreadId;
            Title = "worktree";
        }

        return result;
    }

    public Task<JsonElement> AddGitWorktreeAsync(string cwd, string path, string? branch = null) =>
        _client.CallAsync(AppServerMethods.GitWorktreeAdd, new { cwd, path, branch });

    public Task<JsonElement> DetectExternalAgentsAsync(IEnumerable<string> cwds, bool includeHome = false) =>
        _client.CallAsync(AppServerMethods.ExternalAgentConfigDetect, new { cwds = cwds.ToArray(), includeHome });

    public Task<JsonElement> ImportExternalAgentsAsync(object migrationItems) =>
        _client.CallAsync(AppServerMethods.ExternalAgentConfigImport, new { migrationItems });

    public Task<JsonElement> ReadAccountAsync() =>
        _client.CallAsync(AppServerMethods.AccountRead);

    public Task<JsonElement> ListModelsAsync() =>
        _client.CallAsync(AppServerMethods.ModelList);

    public Task<JsonElement> ListExperimentalFeaturesAsync() =>
        _client.CallAsync(AppServerMethods.ExperimentalFeatureList);

    public Task SetExperimentalFeatureAsync(string name, bool enabled) =>
        _client.CallAsync(AppServerMethods.ExperimentalFeatureEnablementSet, new { enablement = new Dictionary<string, bool> { [name] = enabled } });

    public Task LoginApiKeyAsync(string apiKey) =>
        _client.CallAsync(AppServerMethods.AccountLoginStart, new { type = "apiKey", apiKey });

    public Task LoginAccessTokenAsync(string accessToken) =>
        _client.CallAsync(AppServerMethods.AccountLoginStart, new { type = "chatgptAuthTokens", accessToken });

    public Task<JsonElement> ReadUsageAsync() =>
        _client.CallAsync(AppServerMethods.AccountUsageRead);

    public Task<JsonElement> ReadRateLimitsAsync() =>
        _client.CallAsync(AppServerMethods.AccountRateLimitsRead);

    public Task<JsonElement> LoginChatgptAsync() =>
        _client.CallAsync(AppServerMethods.AccountLoginStart, new { type = "chatgpt" });

    public Task<JsonElement> LoginDeviceCodeAsync() =>
        _client.CallAsync(AppServerMethods.AccountLoginStart, new { type = "chatgptDeviceCode" });

    public Task CancelLoginAsync(string loginId) =>
        _client.CallAsync(AppServerMethods.AccountLoginCancel, new { loginId });

    public Task LogoutAsync() =>
        _client.CallAsync(AppServerMethods.AccountLogout);

    public async Task<DesktopChrome> LoadChromeAsync()
    {
        var cfg = await ReadConfigAsync();
        string S(string name) =>
            cfg.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        var model = S("model");
        var sandbox = S("sandboxMode");
        var approval = S("approvalPolicy");
        var cwd = S("cwd");
        var provider = S("modelProvider");
        var account = await _client.CallAsync(AppServerMethods.AccountRead);
        var auth = "no api key";
        if (account.TryGetProperty("account", out var acc) && acc.ValueKind == JsonValueKind.Object && acc.TryGetProperty("type", out var type))
        {
            var kind = type.GetString();
            auth = kind == "chatgpt" ? "chatgpt" : (string.IsNullOrWhiteSpace(provider) ? (kind ?? "apiKey") : provider);
        }

        var git = await _client.CallAsync(AppServerMethods.GitDiffToRemote, new { cwd });
        var sha = git.TryGetProperty("sha", out var shaEl) && shaEl.ValueKind == JsonValueKind.String ? shaEl.GetString() ?? "" : "";
        var diff = git.TryGetProperty("workingTreeDiff", out var wtdEl) && wtdEl.ValueKind == JsonValueKind.String ? wtdEl.GetString() ?? "" : "";
        if (diff.Length == 0)
        {
            diff = git.TryGetProperty("diff", out var diffEl) && diffEl.ValueKind == JsonValueKind.String ? diffEl.GetString() ?? "" : "";
        }
        if (string.IsNullOrWhiteSpace(diff))
        {
            diff = "No remote diff";
        }
        else if (diff.Length > 800)
        {
            diff = diff[..800] + "…";
        }

        if (git.TryGetProperty("worktrees", out var wtEl) && wtEl.ValueKind == JsonValueKind.Array && wtEl.GetArrayLength() > 0)
        {
            var wt = string.Join(Environment.NewLine, wtEl.EnumerateArray().Select(w => Str(w, "path") ?? "").Where(s => s.Length > 0).Take(6));
            if (wt.Length > 0) diff += Environment.NewLine + "Worktrees" + Environment.NewLine + wt;
        }
        var mcp = await ListMcpAsync();
        var mcpLines = "No mcp_servers";
        if (mcp.TryGetProperty("data", out var mcpData) && mcpData.ValueKind == JsonValueKind.Array)
        {
            var names = mcpData.EnumerateArray()
                .Select(s => (Str(s, "name") ?? "") + ((s.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False) ? " (off)" : ""))
                .Where(n => n.Length > 0)
                .Take(8)
                .ToArray();
            if (names.Length > 0)
            {
                mcpLines = string.Join(Environment.NewLine, names);
            }
        }

        var hooks = await _client.CallAsync(AppServerMethods.HooksList);
        var hookLines = "No hooks in ~/.codexsharp/hooks";
        if (hooks.TryGetProperty("data", out var hookData) && hookData.ValueKind == JsonValueKind.Array && hookData.GetArrayLength() > 0)
        {
            hookLines = string.Join(Environment.NewLine, hookData.EnumerateArray().Take(8).Select(h => (Str(h, "name") ?? "") + " @ " + (Str(h, "event") ?? "")));
        }

        var skills = await _client.CallAsync(AppServerMethods.SkillsList);
        var skillLines = "No SKILL.md found.";
        if (skills.TryGetProperty("data", out var skillData) && skillData.ValueKind == JsonValueKind.Array && skillData.GetArrayLength() > 0)
        {
            skillLines = string.Join(Environment.NewLine, skillData.EnumerateArray().Take(12).Select(s => Str(s, "name") ?? ""));
        }

        var status = $"{model} · {sandbox} · {approval} · {auth}" + (string.IsNullOrWhiteSpace(sha) ? "" : $" · {sha}") + $" · {cwd}";
        var plugins = await _client.CallAsync(AppServerMethods.PluginList);
        var pluginLines = "No plugins";
        if (plugins.TryGetProperty("data", out var pluginData) && pluginData.ValueKind == JsonValueKind.Array && pluginData.GetArrayLength() > 0)
        {
            pluginLines = string.Join(Environment.NewLine, pluginData.EnumerateArray().Take(8).Select(x => Str(x, "name") ?? ""));
        }

        var home = S("home");
        var memoryMode = "enabled";
        var memoryLines = "No memories";
        if (!string.IsNullOrWhiteSpace(home))
        {
            try
            {
                var memDir = Path.Combine(home, "memories");
                var listing = await _client.CallAsync(AppServerMethods.FsReadDirectory, new { path = memDir });
                if (listing.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                {
                    var names = entries.EnumerateArray()
                        .Select(e => Str(e, "fileName") ?? "")
                        .Where(n => n.Length > 0)
                        .Take(12)
                        .ToArray();
                    if (names.Length > 0) memoryLines = string.Join(Environment.NewLine, names);
                }
            }
            catch { /* memories dir may not exist yet */ }
        }

        var fileLines = "No workspace listing";
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            try
            {
                var listing = await _client.CallAsync(AppServerMethods.FsReadDirectory, new { path = cwd });
                if (listing.TryGetProperty("entries", out var files) && files.ValueKind == JsonValueKind.Array)
                {
                    var names = files.EnumerateArray().Select(e => (e.TryGetProperty("isDirectory", out var d) && d.ValueKind == JsonValueKind.True ? "[d] " : "") + (Str(e, "fileName") ?? "")).Where(n => n.Length > 0).Take(16).ToArray();
                    if (names.Length > 0) fileLines = string.Join(Environment.NewLine, names);
                }
            }
            catch { /* ignore */ }
        }
        var promptLines = "No prompts";
        if (!string.IsNullOrWhiteSpace(home))
        {
            try
            {
                var listing = await _client.CallAsync(AppServerMethods.FsReadDirectory, new { path = Path.Combine(home, "prompts") });
                if (listing.TryGetProperty("entries", out var prompts) && prompts.ValueKind == JsonValueKind.Array)
                {
                    var names = prompts.EnumerateArray().Select(e => Str(e, "fileName") ?? "").Where(n => n.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).Take(12).ToArray();
                    if (names.Length > 0) promptLines = string.Join(Environment.NewLine, names);
                }
            }
            catch { /* prompts dir may be empty */ }
        }

        var notes = "MCP" + Environment.NewLine + mcpLines + Environment.NewLine + Environment.NewLine + "Git" + Environment.NewLine + diff + Environment.NewLine + Environment.NewLine + "Hooks" + Environment.NewLine + hookLines + Environment.NewLine + Environment.NewLine + "Plugins" + Environment.NewLine + pluginLines + Environment.NewLine + Environment.NewLine + "Memory (" + memoryMode + ")" + Environment.NewLine + memoryLines + Environment.NewLine + Environment.NewLine + "Workspace" + Environment.NewLine + fileLines + Environment.NewLine + Environment.NewLine + "Prompts" + Environment.NewLine + promptLines;
        var models = await _client.CallAsync(AppServerMethods.ModelList);
        var modelIds = new List<string>();
        if (models.TryGetProperty("data", out var modelData) && modelData.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in modelData.EnumerateArray())
            {
                var id = Str(item, "id");
                if (!string.IsNullOrWhiteSpace(id))
                {
                    modelIds.Add(id);
                }
            }
        }

        var envNote = "local ready";
        try
        {
            var env = await _client.CallAsync(AppServerMethods.EnvironmentStatus, new { environmentId = "local" });
            envNote = "env " + (env.TryGetProperty("status", out var es) && es.ValueKind == JsonValueKind.String ? es.GetString() : "ready");
        }
        catch { /* experimental */ }

        var sandboxReady = await _client.CallAsync(AppServerMethods.WindowsSandboxReadiness);
        var sandboxStatus = sandboxReady.TryGetProperty("status", out var stEl) && stEl.ValueKind == JsonValueKind.String ? stEl.GetString() ?? "notConfigured" : "notConfigured";
        var job = sandboxReady.TryGetProperty("jobObject", out var jobEl) && jobEl.ValueKind == JsonValueKind.True;
        var sandboxNote = sandboxStatus == "ready"
            ? "Windows sandbox ready (Job Object kill-on-close). Official sandbox service is not installed."
            : job
                ? "Job Object available. Run windowsSandbox/setupStart mode=unelevated to mark ready."
                : "windowsSandbox status=notConfigured";
        var needsAuth = auth == "no api key";
        var remoteStatus = "disabled";
        try
        {
            var remote = await _client.CallAsync(AppServerMethods.RemoteControlStatusRead);
            remoteStatus = remote.TryGetProperty("status", out var rs) && rs.ValueKind == JsonValueKind.String ? rs.GetString() ?? "disabled" : "disabled";
        }
        catch { /* experimental method missing */ }

        var rateNote = "API key usage is not ChatGPT-metered in CodexSharp.";
        try
        {
            var limits = await _client.CallAsync(AppServerMethods.AccountRateLimitsRead);
            if (limits.TryGetProperty("ordinaryUsageAllowed", out var allowed) && allowed.ValueKind == JsonValueKind.False)
            {
                rateNote = "Ordinary included usage is not allowed.";
            }
        }
        catch { /* ignore */ }

        try
        {
            await _client.CallAsync(AppServerMethods.PluginReconcile, new { reason = "desktop-chrome" });
        }
        catch { /* local-only reconcile is best-effort */ }

        notes = notes + Environment.NewLine + Environment.NewLine + "Remote control" + Environment.NewLine + remoteStatus + Environment.NewLine + Environment.NewLine + "Rate limits" + Environment.NewLine + rateNote + Environment.NewLine + Environment.NewLine + "Environment" + Environment.NewLine + envNote;

        var computerUse = S("computerUse");
        if (string.IsNullOrWhiteSpace(computerUse)) computerUse = "notConfigured";
        var browserUse = S("browserUse");
        if (string.IsNullOrWhiteSpace(browserUse)) browserUse = "notConfigured";
        notes = notes + Environment.NewLine + Environment.NewLine + "Computer use" + Environment.NewLine + computerUse + Environment.NewLine + Environment.NewLine + "Browser use" + Environment.NewLine + browserUse;
        var featureRows = new List<FeatureFlagRow>();
        try
        {
            var listed = await _client.CallAsync(AppServerMethods.ExperimentalFeatureList);
            if (listed.TryGetProperty("data", out var featData) && featData.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in featData.EnumerateArray())
                {
                    var name = Str(item, "name") ?? "";
                    if (name.Length == 0) continue;
                    var enabled = item.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
                    var stage = Str(item, "stage") ?? "underDevelopment";
                    featureRows.Add(new FeatureFlagRow(name, enabled, stage, Str(item, "description")));
                }
            }
        }
        catch { /* experimental */ }

        var trustCwd = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : cwd;
        var needsTrust = true;
        var trustTarget = trustCwd;
        try
        {
            var trust = await ReadProjectTrustAsync(trustCwd);
            needsTrust = !(trust.TryGetProperty("trusted", out var tr) && tr.ValueKind == JsonValueKind.True);
            if (trust.TryGetProperty("trustTarget", out var tt) && tt.ValueKind == JsonValueKind.String)
            {
                trustTarget = tt.GetString() ?? trustCwd;
            }
        }
        catch { /* trust methods missing */ }
        return new DesktopChrome(status + " · rc " + remoteStatus, notes, skillLines, model, modelIds, needsAuth, sandboxNote, remoteStatus, rateNote, computerUse, browserUse, featureRows, needsTrust, trustTarget, S("tuiVim"), S("collaborationMode"), S("personality"), S("reasoningEffort"), S("tuiRaw"), S("tuiTitle"));
    }

    public async Task<IReadOnlyList<string>> MentionHitsAsync(string composer)
    {
        var at = composer.LastIndexOf('@');
        if (at < 0)
        {
            return [];
        }

        var rest = composer.Substring(at + 1);
        if (rest.Contains(' ') || rest.IndexOf('\n') >= 0)
        {
            return [];
        }

        var result = await FuzzySearchAsync(rest);
        if (!result.TryGetProperty("matches", out var matches) || matches.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return matches.EnumerateArray()
            .Select(m => Str(m, "path") ?? Str(m, "fileName") ?? "")
            .Where(s => s.Length > 0)
            .Take(8)
            .ToArray();
    }

    public Task QueueAddAsync(string text) => QueueAddAsync(ThreadId, text);

    public Task QueueAddAsync(string threadId, string text) =>
        _client.CallAsync(AppServerMethods.ThreadQueueAdd, new
        {
            threadId,
            input = new[] { new { type = "text", text } },
        });

    public Task<JsonElement> QueueListAsync() => QueueListAsync(ThreadId);

    public Task<JsonElement> QueueListAsync(string threadId) =>
        _client.CallAsync(AppServerMethods.ThreadQueueList, new { threadId });

    public Task<JsonElement> SearchThreadsAsync(string searchTerm) =>
        _client.CallAsync(AppServerMethods.ThreadSearch, new { searchTerm });

    public Task RollbackAsync(int numTurns = 1) =>
        _client.CallAsync(AppServerMethods.ThreadRollback, new { threadId = ThreadId, numTurns });

    public Task CompactAsync() =>
        _client.CallAsync(AppServerMethods.ThreadCompactStart, new { threadId = ThreadId });

    public Task ReviewAsync(object? target = null) =>
        target is null
            ? _client.CallAsync(AppServerMethods.ReviewStart, new { threadId = ThreadId, target = new { type = "uncommittedChanges" } })
            : _client.CallAsync(AppServerMethods.ReviewStart, new { threadId = ThreadId, target });

    public Task ReviewUncommittedAsync() =>
        ReviewAsync(new { type = "uncommittedChanges" });

    public Task ReviewBaseBranchAsync(string branch) =>
        ReviewAsync(new { type = "baseBranch", branch });

    public Task QueueDeleteAsync(string queuedSubmissionId) =>
        QueueDeleteAsync(ThreadId, queuedSubmissionId);

    public Task QueueDeleteAsync(string threadId, string queuedSubmissionId) =>
        _client.CallAsync(AppServerMethods.ThreadQueueDelete, new { threadId, queuedSubmissionId });

    public Task QueueUpdateAsync(string queuedSubmissionId, string text) =>
        _client.CallAsync(AppServerMethods.ThreadQueueUpdate, new
        {
            threadId = ThreadId,
            queuedSubmissionId,
            input = new[] { new { type = "text", text } },
        });

    public Task QueueReorderAsync(IReadOnlyList<string> queuedSubmissionIds) =>
        _client.CallAsync(AppServerMethods.ThreadQueueReorder, new { threadId = ThreadId, queuedSubmissionIds });

    public async Task<IReadOnlyList<ProjectListItem>> LoadProjectsAsync()
    {
        var listed = await _client.CallAsync(AppServerMethods.ProjectList, new { limit = 100 });
        var rows = new List<ProjectListItem>();
        if (!listed.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        foreach (var item in data.EnumerateArray())
        {
            var id = Str(item, "id") ?? "";
            if (id.Length == 0) continue;
            var root = "";
            if (item.TryGetProperty("roots", out var roots) && roots.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in roots.EnumerateArray())
                {
                    root = Str(r, "path") ?? "";
                    if (root.Length > 0) break;
                }
            }
            rows.Add(new ProjectListItem(id, Str(item, "name") ?? id, root));
        }

        return rows;
    }

    public Task<JsonElement> CreateProjectAsync(string name, string root)
    {
        return _client.CallAsync(AppServerMethods.ProjectCreate, new
        {
            name,
            roots = new[] { new { path = root } },
            idempotencyKey = Guid.NewGuid().ToString("N"),
        });
    }

    public Task DeleteProjectAsync(string projectId) =>
        _client.CallAsync(AppServerMethods.ProjectDelete, new { projectId });

    public Task AssignProjectAsync(string projectId) =>
        _client.CallAsync(AppServerMethods.ThreadMetadataUpdate, new { threadId = ThreadId, projectId });


    public Task<JsonElement> SetupWindowsSandboxAsync(string mode = "unelevated", string? cwd = null) =>
        _client.CallAsync(AppServerMethods.WindowsSandboxSetupStart, new { mode, cwd });

    public Task<JsonElement> WindowsSandboxReadinessAsync() =>
        _client.CallAsync(AppServerMethods.WindowsSandboxReadiness);

    public Task<JsonElement> ReadRemoteControlStatusAsync() =>
        _client.CallAsync(AppServerMethods.RemoteControlStatusRead);

    public Task<JsonElement> EnableRemoteControlAsync() =>
        _client.CallAsync(AppServerMethods.RemoteControlEnable);

    public Task<JsonElement> DisableRemoteControlAsync() =>
        _client.CallAsync(AppServerMethods.RemoteControlDisable);

    public Task<JsonElement> StartRemoteControlPairingAsync(bool manualCode = false) =>
        _client.CallAsync(AppServerMethods.RemoteControlPairingStart, new { manualCode });

    public Task QueueStartAsync() =>
        _client.CallAsync(AppServerMethods.ThreadQueueStart, new { threadId = ThreadId });

    public async Task<string> ForkAsync()
    {
        var result = await _client.CallAsync(AppServerMethods.ThreadFork, new { threadId = ThreadId });
        var thread = result.GetProperty("thread");
        ThreadId = thread.GetProperty("id").GetString() ?? ThreadId;
        Title = thread.TryGetProperty("title", out var title) ? title.GetString() ?? Title : Title;
        return ThreadId;
    }

    public Task FuzzySessionStartAsync(string sessionId, string root) =>
        _client.CallAsync(AppServerMethods.FuzzyFileSearchSessionStart, new { sessionId, roots = new[] { root } });

    public Task FuzzySessionUpdateAsync(string sessionId, string query) =>
        _client.CallAsync(AppServerMethods.FuzzyFileSearchSessionUpdate, new { sessionId, query });

    public Task FuzzySessionStopAsync(string sessionId) =>
        _client.CallAsync(AppServerMethods.FuzzyFileSearchSessionStop, new { sessionId });

    public Task<JsonElement> FuzzySearchAsync(string query) =>
        _client.CallAsync(AppServerMethods.FuzzyFileSearch, new { query });

    public Task<JsonElement> ListMcpAsync() =>
        _client.CallAsync(AppServerMethods.McpServerStatusList);

    public Task AddMcpStdioAsync(string name, IReadOnlyList<string> commandAndArgs) =>
        _client.CallAsync(AppServerMethods.McpServerAdd, new { name, command = commandAndArgs.ElementAtOrDefault(0), args = commandAndArgs.Skip(1).ToArray() });

    public Task AddMcpHttpAsync(string name, string url, string? bearerTokenEnvVar = null) =>
        _client.CallAsync(AppServerMethods.McpServerAdd, new { name, url, bearerTokenEnvVar });

    public Task RemoveMcpAsync(string name) =>
        _client.CallAsync(AppServerMethods.McpServerRemove, new { name });

    public Task<JsonElement> ListSkillsAsync() =>
        _client.CallAsync(AppServerMethods.SkillsList);

    public Task<JsonElement> ListHooksAsync() =>
        _client.CallAsync(AppServerMethods.HooksList);

    public Task WriteSkillEnabledAsync(string name, bool enabled) =>
        _client.CallAsync(AppServerMethods.SkillsConfigWrite, new { name, enabled });

    public async Task<IReadOnlyList<string>> LoadWorkspaceFilesAsync()
    {
        var cfg = await ReadConfigAsync();
        var cwd = cfg.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : Environment.CurrentDirectory;
        if (string.IsNullOrWhiteSpace(cwd)) return Array.Empty<string>();
        try
        {
            var listing = await _client.CallAsync(AppServerMethods.FsReadDirectory, new { path = cwd });
            if (!listing.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();
            return entries.EnumerateArray()
                .Select(e => (e.TryGetProperty("isDirectory", out var d) && d.ValueKind == JsonValueKind.True ? "[d] " : "") + (Str(e, "fileName") ?? ""))
                .Where(s => s.Length > 0)
                .Take(40)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public Task<JsonElement> ListPluginsAsync() =>
        _client.CallAsync(AppServerMethods.PluginList);

    public Task<JsonElement> ListPromptsAsync() =>
        _client.CallAsync(AppServerMethods.PromptsList);

    public Task<JsonElement> ReadPromptAsync(string name) =>
        _client.CallAsync(AppServerMethods.PromptsRead, new { name });

    public Task<JsonElement> InstallPluginAsync(string pluginName, string? marketplacePath = null) =>
        _client.CallAsync(AppServerMethods.PluginInstall, new { pluginName, marketplacePath });

    public Task UninstallPluginAsync(string pluginId) =>
        _client.CallAsync(AppServerMethods.PluginUninstall, new { pluginId });

    public Task<JsonElement> ListMarketplacesAsync() =>
        _client.CallAsync(AppServerMethods.MarketplaceList);

    public Task<JsonElement> AddMarketplaceAsync(string source, string? refName = null) =>
        _client.CallAsync(AppServerMethods.MarketplaceAdd, new { source, refName });

    public Task RemoveMarketplaceAsync(string name) =>
        _client.CallAsync(AppServerMethods.MarketplaceRemove, new { name });

    public Task<JsonElement> SharePluginAsync(string pluginPath, string? discoverability = null) =>
        _client.CallAsync(AppServerMethods.PluginShareSave, new { pluginPath, discoverability });

    public Task<JsonElement> ListPluginSharesAsync() =>
        _client.CallAsync(AppServerMethods.PluginShareList);

    public Task<JsonElement> ReloadMcpAsync() =>
        _client.CallAsync(AppServerMethods.McpServerReload);

    public Task<JsonElement> ReadMcpResourceAsync(string server, string uri) =>
        _client.CallAsync(AppServerMethods.McpServerResourceRead, new { server, uri });

    public Task<JsonElement> SteerAsync(string text) =>
        _client.CallAsync(AppServerMethods.TurnSteer, new
        {
            threadId = ThreadId,
            input = new[] { new { type = "text", text } },
        });

    public Task StartTurnAsync(string text, CancellationToken ct = default) =>
        StartTurnAsync(text, null, ct);

    public Task StartTurnAsync(string text, IEnumerable<string>? images, CancellationToken ct = default)
    {
        var input = new List<object> { new { type = "text", text } };
        if (images is not null)
        {
            foreach (var path in images)
            {
                input.Add(new { type = "local_image", path });
            }
        }

        return _client.CallAsync(AppServerMethods.TurnStart, new { threadId = ThreadId, input }, ct);
    }

    /// Wait until the in-flight turn finishes. `turn/start` returns as soon as the RPC is accepted.
    public async Task RunTurnToCompletionAsync(string text, IEnumerable<string>? images = null, CancellationToken ct = default)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(AgentEvent evt)
        {
            if (evt is AgentEvent.TurnCompleted or AgentEvent.Failed)
            {
                done.TrySetResult();
            }
        }

        Event += Handler;
        try
        {
            await StartTurnAsync(text, images, ct);
            await done.Task.WaitAsync(ct);
        }
        finally
        {
            Event -= Handler;
        }
    }

    public Task SetMemoryModeAsync(string mode) =>
        _client.CallAsync(AppServerMethods.ThreadMemoryModeSet, new { threadId = ThreadId, mode });

    public Task<JsonElement> ListMemoriesAsync() =>
        _client.CallAsync(AppServerMethods.MemoryList);

    public Task ResetMemoryAsync() =>
        _client.CallAsync(AppServerMethods.MemoryReset);

    public Task<JsonElement> ReadMemoryAsync(string name) =>
        _client.CallAsync(AppServerMethods.MemoryRead, new { name });

    public Task<JsonElement> WriteMemoryAsync(string name, string text) =>
        _client.CallAsync(AppServerMethods.MemoryWrite, new { name, text });

    public Task<JsonElement> ListKeymapAsync() =>
        _client.CallAsync(AppServerMethods.TuiKeymapList);

    public Task<JsonElement> SetKeymapAsync(string context, string action, string keys) =>
        _client.CallAsync(AppServerMethods.TuiKeymapSet, new { context, action, keys });

    public Task<JsonElement> ReadPetsAsync() =>
        _client.CallAsync(AppServerMethods.TuiPetsRead);

    public Task<JsonElement> SetPetsAsync(string id) =>
        _client.CallAsync(AppServerMethods.TuiPetsSet, new { id });


    public Task ReconcilePluginsNowAsync() =>
        _client.CallAsync(AppServerMethods.PluginReconcile, new { reason = "desktop" });

    public Task RespondUserInputAsync(string requestId, string answersJson) =>
        _client.CallAsync(AppServerMethods.ItemToolRespondUserInput, new
        {
            threadId = ThreadId,
            requestId,
            answersJson,
        });

    public Task RespondMcpElicitationAsync(string requestId, bool accept, string? content = null) =>
        _client.CallAsync(AppServerMethods.McpServerElicitationRespond, new
        {
            threadId = ThreadId,
            requestId,
            answersJson = accept
                ? (content ?? "{\"action\":\"accept\",\"content\":{\"text\":\"ok\"}}")
                : "{\"action\":\"decline\"}",
        });

    public async Task RespondApprovalAsync(string requestId, bool allow, string? reason = null)
    {
        if (_pendingApprovalRpc is JsonElement rpcId)
        {
            _pendingApprovalRpc = null;
            await _client.ReplyAsync(rpcId, new { decision = allow ? "accept" : "decline", reason });
        }

        await _client.CallAsync(AppServerMethods.ItemPermissionsRespond, new
        {
            threadId = ThreadId,
            requestId,
            allow,
            reason,
        });
    }

    public Task InterruptAsync() =>
        _client.CallAsync(AppServerMethods.TurnInterrupt, new { threadId = ThreadId });

    public Task<JsonElement> ListBackgroundTerminalsAsync() =>
        _client.CallAsync(AppServerMethods.ThreadBackgroundTerminalsList, new { threadId = ThreadId });

    public Task CleanBackgroundTerminalsAsync() =>
        _client.CallAsync(AppServerMethods.ThreadBackgroundTerminalsClean, new { threadId = ThreadId });

    public Task<JsonElement> ListAppsAsync() =>
        _client.CallAsync(AppServerMethods.AppList);

    public Task<JsonElement> UploadFeedbackAsync(string classification, string? reason = null) =>
        _client.CallAsync(AppServerMethods.FeedbackUpload, new { classification, reason, threadId = ThreadId, includeLogs = false });

    public Task<JsonElement> WatchFsAsync(string path, string watchId) =>
        _client.CallAsync(AppServerMethods.FsWatch, new { path, watchId });

    public Task UnwatchFsAsync(string watchId) =>
        _client.CallAsync(AppServerMethods.FsUnwatch, new { watchId });

    public Task<JsonElement> ExecCommandAsync(string command) =>
        _client.CallAsync(AppServerMethods.CommandExec, new { command = new[] { command }, cwd = Environment.CurrentDirectory });

    public Task<JsonElement> StartStreamingExecAsync(string processId, string command) =>
        _client.CallAsync(AppServerMethods.CommandExec, new
        {
            command = new[] { command },
            processId,
            streamStdin = true,
            streamStdoutStderr = true,
            tty = true,
            size = new { rows = 24, cols = 80 },
            disableTimeout = true,
            cwd = Environment.CurrentDirectory,
        });

    public Task WriteExecAsync(string processId, string text, bool closeStdin = false)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? ""));
        return _client.CallAsync(AppServerMethods.CommandExecWrite, new { processId, deltaBase64 = payload, closeStdin });
    }

    public Task TerminateExecAsync(string processId) =>
        _client.CallAsync(AppServerMethods.CommandExecTerminate, new { processId });

    private void OnNotification(JsonElement root)
    {
        if (!root.TryGetProperty("method", out var methodEl))
        {
            return;
        }

        var method = methodEl.GetString() ?? "";
        var p = root.TryGetProperty("params", out var paramsEl) ? paramsEl : default;
        switch (method)
        {
            case var _ when method == AppServerNotifications.AgentMessageDelta:
                Event?.Invoke(AgentEvent.NewItemDelta(Str(p, "itemId") ?? "", Str(p, "delta") ?? ""));
                break;
            case var _ when method == AppServerNotifications.CommandExecutionOutputDelta
                            || method == AppServerNotifications.FileChangeOutputDelta:
                Event?.Invoke(AgentEvent.NewCommandOutputDelta(Str(p, "itemId") ?? "", Str(p, "delta") ?? ""));
                break;
            case var _ when method == AppServerNotifications.ItemStarted:
                Event?.Invoke(AgentEvent.NewItemStarted(ReadItem(p)));
                break;
            case var _ when method == AppServerNotifications.ItemCompleted:
                Event?.Invoke(AgentEvent.NewItemCompleted(ReadItem(p)));
                break;
            case var _ when method == AppServerNotifications.TurnStarted:
            {
                var turnId = p.TryGetProperty("turn", out var started) ? Str(started, "id") ?? "" : "";
                Event?.Invoke(AgentEvent.NewTurnStarted(turnId, ThreadId));
                break;
            }
            case var _ when method == AppServerNotifications.TurnCompleted:
            {
                var status = p.TryGetProperty("turn", out var turn) ? Str(turn, "status") ?? "completed" : "completed";
                var turnId = p.TryGetProperty("turn", out var completed) ? Str(completed, "id") ?? "" : "";
                Event?.Invoke(AgentEvent.NewTurnCompleted(turnId, status));
                break;
            }
            case var _ when method == AppServerNotifications.McpServerElicitationRequest:
                if (root.TryGetProperty("id", out var elicitRpc))
                {
                    _pendingApprovalRpc = elicitRpc.Clone();
                }
                Event?.Invoke(AgentEvent.NewMcpElicitationNeeded(Str(p, "itemId") ?? "", Str(p, "message") ?? p.GetRawText()));
                break;
            case var _ when method == AppServerNotifications.ToolRequestUserInput:
                if (root.TryGetProperty("id", out var inputRpc))
                {
                    _pendingApprovalRpc = inputRpc.Clone();
                }
                Event?.Invoke(AgentEvent.NewUserInputNeeded(Str(p, "itemId") ?? "", p.GetRawText()));
                break;
            case var _ when method == AppServerNotifications.PermissionsRequestApproval
                            || method == AppServerNotifications.CommandExecutionRequestApproval
                            || method == AppServerNotifications.FileChangeRequestApproval:
                if (root.TryGetProperty("id", out var rpcId))
                {
                    _pendingApprovalRpc = rpcId.Clone();
                }
                Event?.Invoke(AgentEvent.NewApprovalNeeded(
                    Str(p, "requestId") ?? Str(p, "itemId") ?? "",
                    Str(p, "command") ?? "",
                    Str(p, "cwd") ?? "",
                    Str(p, "reason") ?? ""));
                break;
            case var _ when method == AppServerNotifications.Error:
                Event?.Invoke(AgentEvent.NewFailed(Str(p, "message") ?? "error"));
                break;
            case var _ when method == AppServerNotifications.ThreadQueueChanged:
                QueueChanged?.Invoke();
                break;
            case var _ when method == AppServerNotifications.ThreadCompacted:
                Event?.Invoke(AgentEvent.NewCompacted(p.TryGetProperty("dropped", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0));
                break;
            case var _ when method == AppServerNotifications.Warning
                            || method == AppServerNotifications.WindowsWorldWritableWarning:
            {
                var msg = Str(p, "message");
                if (string.IsNullOrEmpty(msg) && p.TryGetProperty("samplePaths", out var sp) && sp.ValueKind == JsonValueKind.Array)
                {
                    msg = "World-writable paths: " + string.Join(", ", sp.EnumerateArray().Select(x => x.GetString()).Where(s => !string.IsNullOrEmpty(s)).Take(8));
                }
                Event?.Invoke(AgentEvent.NewWarning(msg ?? "warning"));
                break;
            }
            case var _ when method == AppServerNotifications.ThreadTokenUsageUpdated:
            {
                long ReadNum(string name)
                {
                    if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("tokenUsage", out var tu)) return 0;
                    if (!tu.TryGetProperty("total", out var total) || !total.TryGetProperty(name, out var n)) return 0;
                    return n.ValueKind == JsonValueKind.Number ? n.GetInt64() : 0;
                }
                var last = 0L;
                if (p.TryGetProperty("tokenUsage", out var usage) && usage.TryGetProperty("last", out var lastEl) && lastEl.TryGetProperty("totalTokens", out var lt) && lt.ValueKind == JsonValueKind.Number)
                    last = lt.GetInt64();
                Event?.Invoke(AgentEvent.NewTokenUsage(ReadNum("totalTokens"), last));
                break;
            }
            case var _ when method == AppServerNotifications.FsChanged:
            {
                var watchId = Str(p, "watchId") ?? "";
                var path = Str(p, "path") ?? "";
                if (string.IsNullOrEmpty(path) && p.TryGetProperty("changedPaths", out var paths) && paths.ValueKind == JsonValueKind.Array)
                {
                    path = paths.EnumerateArray().Select(x => x.GetString()).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "";
                }
                FsChanged?.Invoke(watchId, path);
                break;
            }
            case var _ when method == AppServerNotifications.CommandExecOutputDelta:
            {
                var b64 = Str(p, "deltaBase64") ?? "";
                var textDelta = "";
                if (b64.Length > 0)
                {
                    try { textDelta = Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
                    catch { textDelta = ""; }
                }
                ExecOutput?.Invoke(Str(p, "processId") ?? "", textDelta);
                break;
            }
            case var _ when method == AppServerNotifications.FuzzyFileSearchSessionUpdated:
            {
                var files = p.TryGetProperty("files", out var fileEl) && fileEl.ValueKind == JsonValueKind.Array
                    ? string.Join(Environment.NewLine, fileEl.EnumerateArray().Select(f => Str(f, "path") ?? "").Where(s => s.Length > 0).Take(12))
                    : "";
                FuzzyHits?.Invoke(Str(p, "sessionId") ?? "", files);
                break;
            }
        }
    }

    private static ConversationItem ReadItem(JsonElement p)
    {
        var item = p.TryGetProperty("item", out var inner) ? inner : p;
        return new ConversationItem(
            Str(item, "id") ?? Ids.item (),
            Str(item, "type") ?? Str(item, "kind") ?? "agent_message",
            Str(item, "text") ?? "",
            Str(item, "status") ?? "",
            Str(item, "command") ?? "",
            Str(item, "path") ?? "",
            Str(item, "tool") ?? "",
            Str(item, "toolCallId") ?? "");
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
