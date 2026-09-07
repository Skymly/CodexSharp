using System.Text;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using CodexSharp.Protocol;
using CodexSharp.Runtime;

namespace CodexSharp.AppServer;

public sealed class AppServerHost
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly Dictionary<string, CodexSession> _threads = new();
    private readonly Dictionary<string, CancellationTokenSource> _turnCts = new();
    private readonly Dictionary<string, FileSystemWatcher> _watches = new();
    private readonly Dictionary<string, string[]> _fuzzySessions = new();
    private readonly Dictionary<string, List<object>> _backgroundTerminals = new();
    private readonly Dictionary<string, int> _elicitations = new();
    private readonly HashSet<string> _mcpStreams = new(StringComparer.Ordinal);
    private readonly CommandExecBroker _exec = new();
    private readonly Dictionary<string, CancellationTokenSource> _logins = new();
    private readonly Dictionary<string, PendingServerRequest> _serverRequests = new(StringComparer.Ordinal);
    private bool _initialized;
    private bool _experimentalApi;
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public AppServerHost(TextReader? input = null, TextWriter? output = null)
    {
        _input = input ?? Console.In;
        _output = output ?? Console.Out;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await _input.ReadLineAsync(ct);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

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
        var id = root.TryGetProperty("id", out var idEl) ? idEl.Clone() : default;
        var hasId = root.TryGetProperty("id", out _);
        var paramsEl = root.TryGetProperty("params", out var p) ? p : default;

        if (string.IsNullOrEmpty(method))
        {
            if (hasId) HandleServerResponse(id, root);
            return;
        }

        if (method == "initialized")
        {
            return;
        }

        if (method != "initialize" && !_initialized)
        {
            if (hasId) Error(id, -32000, "Not initialized");
            return;
        }

        switch (method)
        {
            case "initialize":
                if (_initialized)
                {
                    Error(id, -32000, "Already initialized");
                    return;
                }

                _initialized = true;
                _experimentalApi = paramsEl.ValueKind == JsonValueKind.Object
                    && paramsEl.TryGetProperty("capabilities", out var caps)
                    && caps.ValueKind == JsonValueKind.Object
                    && caps.TryGetProperty("experimentalApi", out var exp)
                    && exp.ValueKind == JsonValueKind.True;
                Result(id, new
                {
                    userAgent = $"codexsharp/0.1.0 ({Environment.OSVersion}; {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})",
                    platformFamily = "dotnet",
                    platformOs = Environment.OSVersion.Platform.ToString(),
                    experimentalApi = _experimentalApi,
                });
                _ = Task.Run(() =>
                {
                    var report = WorldWritableScan.Scan(Environment.CurrentDirectory);
                    if (report.SamplePaths.Count == 0) return;
                    Notify("windows/worldWritableWarning", new
                    {
                        samplePaths = report.SamplePaths,
                        extraCount = report.ExtraCount,
                        failedScan = report.FailedScan,
                    });
                });
                break;

            case "mock/experimentalMethod":
                if (!RequireExperimental(id, hasId, "mock/experimentalMethod")) break;
                Result(id, new { ok = true });
                break;

            case "thread/start":
            {
                if (Flag(paramsEl, "allowProviderModelFallback") && !RequireExperimental(id, hasId, "thread/start.allowProviderModelFallback"))
                {
                    break;
                }

                if (HasGranularApproval(paramsEl) && !RequireExperimental(id, hasId, "askForApproval.granular"))
                {
                    break;
                }

                var cwd = Str(paramsEl, "cwd") ?? Environment.CurrentDirectory;
                string? startProjectId = null;
                if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("projectId", out var startProject))
                {
                    if (!RequireExperimental(id, hasId, "thread/start.projectId")) break;
                    startProjectId = startProject.ValueKind == JsonValueKind.String ? startProject.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(startProjectId))
                    {
                        var projectRoot = ProjectStore.PrimaryRoot(startProjectId);
                        if (string.IsNullOrWhiteSpace(projectRoot))
                        {
                            Error(id, -32001, "Project not found: " + startProjectId);
                            break;
                        }

                        cwd = projectRoot;
                    }
                }

                var model = Str(paramsEl, "model");
                var sandbox = Str(paramsEl, "sandbox") ?? Str(paramsEl, "sandboxMode");
                var approval = Str(paramsEl, "approvalPolicy");
                var profile = Str(paramsEl, "profile");
                var config = ConfigService.Load(cwd, model, sandbox, approval, profile, Flag(paramsEl, "ignoreUserConfig"), Flag(paramsEl, "strictConfig"));
                var session = CodexSession.Start(config, Str(paramsEl, "title"));
                var source = Str(paramsEl, "source") ?? Str(paramsEl, "threadSource");
                if (!string.IsNullOrWhiteSpace(source))
                {
                    ThreadSources.Set(session.Thread.Id, source);
                }
                if (!string.IsNullOrWhiteSpace(startProjectId) && !ProjectStore.Assign(session.Thread.Id, startProjectId))
                {
                    Error(id, -32001, "Project not found: " + startProjectId);
                    break;
                }
                Wire(session);
                _threads[session.Thread.Id] = session;
                Notify("thread/started", new { thread = ThreadDto(session) });
                Result(id, new { thread = ThreadDto(session) });
                break;
            }

            case "thread/resume":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                var session = CodexSession.Resume(threadId);
                Wire(session);
                _threads[session.Thread.Id] = session;
                Result(id, new { thread = ThreadDto(session) });
                break;
            }

            case "thread/list":
            {
                if (paramsEl.ValueKind == JsonValueKind.Object
                    && paramsEl.TryGetProperty("originators", out var originators)
                    && originators.ValueKind == JsonValueKind.Array
                    && originators.GetArrayLength() > 0)
                {
                    Error(id, -32602, "originators filter is hosted-only");
                    break;
                }

                var archived = Bool(paramsEl, "archived") ?? false;
                var term = Str(paramsEl, "searchTerm") ?? "";
                var cwdFilter = Str(paramsEl, "cwd");
                var hasProject = false;
                string? projectIdFilter = null;
                if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("projectId", out var projectEl))
                {
                    if (!RequireExperimental(id, hasId, "thread/list.projectId")) break;
                    hasProject = true;
                    projectIdFilter = projectEl.ValueKind == JsonValueKind.String ? projectEl.GetString() : null;
                }
                var hasSection = false;
                string? sectionId = null;
                if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("sectionId", out var sectionEl))
                {
                    hasSection = true;
                    sectionId = sectionEl.ValueKind == JsonValueKind.String ? sectionEl.GetString() : null;
                }
                var store = new JsonlThreadStore();
                IEnumerable<ThreadInfo> threads = string.IsNullOrWhiteSpace(term)
                    ? store.List(archived)
                    : store.Search(term, archived).Select(h => h.Thread);

                if (!string.IsNullOrWhiteSpace(cwdFilter))
                {
                    threads = threads.Where(t => t.Cwd.Equals(cwdFilter, StringComparison.OrdinalIgnoreCase));
                }

                if (hasSection)
                {
                    threads = threads.Where(t =>
                    {
                        var sec = ThreadSections.SectionOf(t.Id);
                        return string.IsNullOrEmpty(sectionId) ? string.IsNullOrEmpty(sec) : sec == sectionId;
                    });
                }

                if (hasProject)
                {
                    threads = threads.Where(t =>
                    {
                        var pid = ProjectStore.ProjectOf(t.Id);
                        return string.IsNullOrEmpty(projectIdFilter) ? string.IsNullOrEmpty(pid) : pid == projectIdFilter;
                    });
                }

                var sortKey = Str(paramsEl, "sortKey") ?? "updated_at";
                var desc = !string.Equals(Str(paramsEl, "sortDirection"), "asc", StringComparison.OrdinalIgnoreCase);
                var ordered = threads.ToList();
                ordered.Sort((a, b) =>
                {
                    var pin = b.Pinned.CompareTo(a.Pinned);
                    if (pin != 0)
                    {
                        return pin;
                    }

                    var cmp = sortKey switch
                    {
                        "created_at" => a.CreatedAt.CompareTo(b.CreatedAt),
                        "section_position" => string.Compare(ThreadSections.SectionOf(a.Id), ThreadSections.SectionOf(b.Id), StringComparison.Ordinal),
                        _ => a.UpdatedAt.CompareTo(b.UpdatedAt),
                    };
                    return desc ? -cmp : cmp;
                });

                var rows = ordered.Select(t => new
                {
                    thread = new
                    {
                        id = t.Id,
                        title = t.Title,
                        cwd = t.Cwd,
                        model = t.Model,
                        createdAt = t.CreatedAt,
                        updatedAt = t.UpdatedAt,
                        archived,
                        pinned = t.Pinned,
                        sectionId = ThreadSections.SectionOf(t.Id),
                        projectId = ProjectStore.ProjectOf(t.Id),
                    },
                }).ToList();
                var (page, next, backwards) = TimelinePaging.Slice(rows, Str(paramsEl, "cursor"), Int(paramsEl, "limit", 50));
                Result(id, new { data = page, nextCursor = next, backwardsCursor = backwards });
                break;
            }

            case "thread/unsubscribe":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                _threads.Remove(threadId);
                Notify("thread/closed", new { threadId });
                Result(id, new { });
                break;
            }

            case "thread/archive":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                WorktreeBindings.UnbindAndRemove(threadId);
                new JsonlThreadStore().Archive(threadId);
                _threads.Remove(threadId);
                Notify("thread/archived", new { threadId });
                Result(id, new { });
                break;
            }

            case "turn/start":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (!_threads.TryGetValue(threadId, out var session))
                {
                    Error(id, -32001, $"Thread not loaded: {threadId}");
                    return;
                }

                var text = ReadInputText(paramsEl);
                Result(id, new { turn = new { id = "pending", status = "in_progress" } });
                StartTurn(session, text, ct);
                break;
            }

            case "turn/settings/update":
            {
                if (!RequireExperimental(id, hasId, "turn/settings/update")) break;
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (!_threads.TryGetValue(threadId, out var liveTurn) || !_turnCts.ContainsKey(threadId))
                {
                    Result(id, new { status = "targetUnavailable" });
                    break;
                }
                liveTurn.ApplySettings(Str(paramsEl, "model"), effort: Str(paramsEl, "effort"));
                Result(id, new { status = "applied" });
                break;
            }

            case "turn/interrupt":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (_turnCts.TryGetValue(threadId, out var turnCts))
                {
                    turnCts.Cancel();
                }
                Result(id, new { });
                break;
            }

            case "turn/steer":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (!_threads.TryGetValue(threadId, out var session))
                {
                    Error(id, -32001, $"Thread not loaded: {threadId}");
                    return;
                }
                var expected = Str(paramsEl, "expectedTurnId");
                if (!string.IsNullOrWhiteSpace(expected) && !string.IsNullOrWhiteSpace(session.ActiveTurnId)
                    && !string.Equals(expected, session.ActiveTurnId, StringComparison.Ordinal))
                {
                    Error(id, -32602, "expectedTurnId does not match the active turn");
                    break;
                }
                var text = ReadInputText(paramsEl);
                if (!session.TrySteer(text))
                {
                    Error(id, -32001, "no active turn to steer");
                    break;
                }
                Result(id, new { turnId = session.ActiveTurnId ?? "steered" });
                break;
            }

            case "thread/fork":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (!_threads.TryGetValue(threadId, out var parent))
                {
                    Error(id, -32001, $"Thread not loaded: {threadId}");
                    return;
                }
                var child = parent.Fork();
                var parentSource = ThreadSources.Get(parent.Thread.Id);
                if (!string.IsNullOrWhiteSpace(parentSource))
                {
                    ThreadSources.Set(child.Thread.Id, parentSource);
                }
                Wire(child);
                _threads[child.Thread.Id] = child;
                Notify("thread/started", new { thread = ThreadDto(child) });
                Result(id, new { thread = ThreadDto(child), forkedFromId = parent.Thread.Id });
                break;
            }

            case "thread/read":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (_threads.TryGetValue(threadId, out var loaded))
                {
                    Result(id, new { thread = ThreadDto(loaded) });
                    break;
                }
                var stored = new JsonlThreadStore().Find(threadId);
                if (stored is null)
                {
                    Error(id, -32001, $"Unknown thread {threadId}");
                    break;
                }
                Result(id, new { thread = new { id = stored.Id, title = stored.Title, cwd = stored.Cwd, model = stored.Model } });
                break;
            }

            case "thread/loaded/list":
                Result(id, new { threadIds = _threads.Keys.ToArray() });
                break;

            case "thread/goal/set":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (!_threads.TryGetValue(threadId, out var session))
                {
                    Error(id, -32001, $"Thread not loaded: {threadId}");
                    break;
                }
                session.SetGoal(Str(paramsEl, "objective"), Str(paramsEl, "status"));
                Notify(AppServerNotifications.ThreadGoalUpdated, new { threadId, goal = session.GoalDto() });
                Result(id, new { goal = session.GoalDto() });
                break;
            }

            case "thread/goal/get":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (!_threads.TryGetValue(threadId, out var session))
                {
                    Error(id, -32001, $"Thread not loaded: {threadId}");
                    break;
                }
                Result(id, new { goal = session.GoalDto() });
                break;
            }

            case "thread/goal/clear":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (_threads.TryGetValue(threadId, out var session))
                {
                    session.ClearGoal();
                }
                Notify(AppServerNotifications.ThreadGoalCleared, new { threadId });
                Result(id, new { cleared = true });
                break;
            }

            case "thread/increment_elicitation":
            {
                if (!RequireExperimental(id, hasId, "thread/increment_elicitation")) break;
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (string.IsNullOrWhiteSpace(threadId)) { Error(id, -32602, "threadId is required"); break; }
                _elicitations.TryGetValue(threadId, out var n);
                n++;
                _elicitations[threadId] = n;
                Result(id, new { count = n });
                break;
            }

            case "thread/decrement_elicitation":
            {
                if (!RequireExperimental(id, hasId, "thread/decrement_elicitation")) break;
                var threadId = Str(paramsEl, "threadId") ?? "";
                _elicitations.TryGetValue(threadId, out var n);
                n = Math.Max(0, n - 1);
                if (n == 0) _elicitations.Remove(threadId); else _elicitations[threadId] = n;
                Result(id, new { count = n });
                break;
            }

            case "currentTime/read":
            {
                if (!RequireExperimental(id, hasId, "currentTime/read")) break;
                Result(id, new { currentTimeAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                break;
            }

            case "mcpServer/event/stream/start":
            {
                if (!RequireExperimental(id, hasId, "mcpServer/event/stream/start")) break;
                var sub = Str(paramsEl, "subscriptionId") ?? "";
                if (string.IsNullOrWhiteSpace(sub)) { Error(id, -32602, "subscriptionId is required"); break; }
                _mcpStreams.Add(sub);
                Result(id, new { });
                break;
            }

            case "mcpServer/event/stream/stop":
            {
                if (!RequireExperimental(id, hasId, "mcpServer/event/stream/stop")) break;
                _mcpStreams.Remove(Str(paramsEl, "subscriptionId") ?? "");
                Result(id, new { });
                break;
            }

            case "environment/info":
            {
                var environmentId = Str(paramsEl, "environmentId") ?? EnvironmentStore.LocalId;
                var (st, err) = EnvironmentStore.Status(environmentId);
                if (st == "unknown")
                {
                    Error(id, -32001, err ?? "unknown environment");
                    break;
                }
                if (st != "ready")
                {
                    Error(id, -32002, err ?? "environment is not ready");
                    break;
                }
                Result(id, new
                {
                    cwd = Environment.CurrentDirectory,
                    home = CodexPaths.Home,
                    os = Environment.OSVersion.ToString(),
                    arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    sandbox = ConfigService.Load().SandboxMode,
                    shell = new { name = OperatingSystem.IsWindows() ? "powershell" : "sh", path = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh" },
                });
                break;
            }

            case "environment/add":
            {
                if (!RequireExperimental(id, hasId, "environment/add")) break;
                try
                {
                    EnvironmentStore.Add(Str(paramsEl, "environmentId") ?? "", Str(paramsEl, "execServerUrl") ?? "");
                    Result(id, new { });
                }
                catch (ArgumentException ex)
                {
                    Error(id, -32602, ex.Message);
                }
                break;
            }

            case "environment/status":
            {
                if (!RequireExperimental(id, hasId, "environment/status")) break;
                var environmentId = Str(paramsEl, "environmentId") ?? "";
                if (string.IsNullOrWhiteSpace(environmentId))
                {
                    Error(id, -32602, "environmentId is required");
                    break;
                }
                var (st, err) = EnvironmentStore.Status(environmentId);
                Result(id, new { status = st, error = err });
                break;
            }

            case "diagnostics/doctor":
            {
                var cwd = Str(paramsEl, "cwd") ?? Environment.CurrentDirectory;
                var report = DoctorProbe.Run(cwd);
                Result(id, new
                {
                    status = report.Status.ToString().ToLowerInvariant() == "ok" ? "ok" : report.Status == DoctorStatus.Warning ? "warning" : "fail",
                    checks = report.Checks.Select(c => new
                    {
                        id = c.Id,
                        group = c.Group,
                        status = c.Status == DoctorStatus.Ok ? "ok" : c.Status == DoctorStatus.Warning ? "warning" : "fail",
                        summary = c.Summary,
                        details = c.Details,
                        remediation = c.Remediation,
                    }).ToArray(),
                });
                break;
            }

            case "execPolicy/check":
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
                break;
            }

            case "execPolicy/list":
            {
                var cwd = Str(paramsEl, "cwd") ?? Environment.CurrentDirectory;
                var data = ExecPolicy.Load(cwd).Select(r => new
                {
                    pattern = r.Pattern,
                    decision = r.Decision.ToString().ToLowerInvariant(),
                    justification = r.Justification,
                }).ToArray();
                Result(id, new { data });
                break;
            }

            case "execPolicy/addUserRule":
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
                break;
            }

            case "sandbox/extraReadRoot/list":
            {
                Result(id, new { data = SandboxRoots.List() });
                break;
            }

            case "sandbox/extraReadRoot/add":
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
                break;
            }

            case "lastPatch/apply":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                var result = LastPatchApply.Run(threadId);
                Result(id, new { ok = result.Ok, output = result.Output, cwd = result.Cwd, cloudApply = "notConfigured" });
                break;
            }

            case "git/worktree/add":
            {
                var cwd = Str(paramsEl, "cwd") ?? Environment.CurrentDirectory;
                var dest = Str(paramsEl, "path") ?? "";
                var branch = Str(paramsEl, "branch") ?? ("codexsharp-" + Guid.NewGuid().ToString("N")[..8]);
                try
                {
                    var path = GitProbe.AddWorktree(cwd, dest, branch);
                    Result(id, new { path });
                }
                catch (Exception ex)
                {
                    Error(id, -32602, ex.Message);
                }
                break;
            }

            case "thread/recap/start":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (!_threads.TryGetValue(threadId, out var recapSession))
                {
                    Error(id, -32001, $"Thread not loaded: {threadId}");
                    break;
                }
                var recap = await recapSession.RunRecapAsync(ct);
                Result(id, new { status = recap.Status, recap = recap.Text, persisted = false });
                break;
            }

            case "thread/copy":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                var want = Str(paramsEl, "kind") ?? Str(paramsEl, "want") ?? "";
                if (!_threads.TryGetValue(threadId, out var copySession))
                {
                    Error(id, -32001, $"Thread not loaded: {threadId}");
                    break;
                }
                var copied = copySession.CopyLast(want);
                Result(id, new { kind = copied.Kind, text = copied.Text });
                break;
            }

            case "thread/export":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                var dest = Str(paramsEl, "path");
                string markdown;
                if (_threads.TryGetValue(threadId, out var liveExport))
                {
                    markdown = liveExport.ExportMarkdown();
                }
                else
                {
                    try
                    {
                        markdown = CodexSession.Resume(threadId).ExportMarkdown();
                    }
                    catch (Exception ex)
                    {
                        Error(id, -32001, ex.Message);
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(dest))
                {
                    var cwd = _threads.TryGetValue(threadId, out var liveCwd) ? liveCwd.Config.Cwd : Environment.CurrentDirectory;
                    dest = Path.Combine(cwd, "codexsharp-export-" + threadId + ".md");
                }

                dest = Path.GetFullPath(dest);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllText(dest, markdown);
                Result(id, new { path = dest, bytes = Encoding.UTF8.GetByteCount(markdown) });
                break;
            }

            case "thread/rollout/path":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                var path = Path.Combine(CodexPaths.SessionsDir, threadId + ".jsonl");
                Result(id, new { path, exists = File.Exists(path) });
                break;
            }

            case "thread/worktree/start":
            {
                var cwd = Str(paramsEl, "cwd") ?? Environment.CurrentDirectory;
                try
                {
                    var tree = WorktreeSession.Create(cwd);
                    var config = ConfigService.Load(tree.Cwd);
                    var session = CodexSession.Start(config, "worktree");
                    ThreadSources.Set(session.Thread.Id, "worktree");
                    WorktreeBindings.Bind(session.Thread.Id, tree);
                    Wire(session);
                    _threads[session.Thread.Id] = session;
                    Notify("thread/started", new { thread = ThreadDto(session) });
                    Result(id, new
                    {
                        thread = ThreadDto(session),
                        path = tree.Root,
                        cwd = tree.Cwd,
                        sourceRoot = tree.SourceRoot,
                        branch = tree.Branch,
                        cloudWorktree = "notConfigured",
                    });
                }
                catch (Exception ex)
                {
                    Error(id, -32602, ex.Message);
                }
                break;
            }

            case "agentsMarkdown/write":
            {
                var cwd = Str(paramsEl, "cwd") ?? Environment.CurrentDirectory;
                var created = AgentsMarkdown.WriteIfMissing(cwd);
                Result(id, new { created, path = Path.Combine(cwd, "AGENTS.md") });
                break;
            }

            case "server/diagnostics":
            {
                if (!RequireExperimental(id, hasId, "server/diagnostics")) break;
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                long? rss = null;
                try { rss = proc.WorkingSet64; } catch { }
                Result(id, new
                {
                    process = new { id = proc.Id, residentMemoryBytes = rss, physicalFootprintBytes = rss },
                    gauges = new object[]
                    {
                        new { name = "loadedThreads", value = (ulong)_threads.Count },
                        new { name = "fuzzySessions", value = (ulong)_fuzzySessions.Count },
                    },
                });
                break;
            }

            case "collaborationMode/list":
            {
                Result(id, new
                {
                    data = new object[]
                    {
                        new { name = "default", mode = "default" },
                        new { name = "plan", mode = "plan" },
                        new { name = "pair", mode = "pair" },
                    }
                });
                break;
            }

            case "experimentalFeature/list":
            {
                var specs = FeatureFlags.Specs.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
                var data = FeatureFlags.List().Select(kv =>
                {
                    specs.TryGetValue(kv.Key, out var spec);
                    return new
                    {
                        name = kv.Key,
                        enabled = kv.Value,
                        defaultEnabled = spec?.DefaultEnabled ?? false,
                        stage = spec?.Stage ?? "underDevelopment",
                        displayName = kv.Key,
                        description = spec?.Description,
                        announcement = (string?)null,
                    };
                }).ToArray();
                Result(id, new { data, nextCursor = (string?)null });
                break;
            }

            case "experimentalFeature/enablement/set":
            {
                var updated = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                if (paramsEl.ValueKind == JsonValueKind.Object
                    && paramsEl.TryGetProperty("enablement", out var enablement)
                    && enablement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in enablement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        {
                            var on = prop.Value.GetBoolean() && !HonestStubs.IsLockedFeature(prop.Name);
                            FeatureFlags.Set(prop.Name, on);
                            updated[prop.Name] = on;
                        }
                    }
                }
                Result(id, new { enablement = updated });
                break;
            }

            case "permissionProfile/list":
            {
                var data = new object[]
                {
                    new { id = "read-only", description = "Inspect only", allowed = true },
                    new { id = "workspace-write", description = "Write inside workspace", allowed = true },
                    new { id = "danger-full-access", description = "No sandbox", allowed = true },
                };
                Result(id, new { data, nextCursor = (string?)null });
                break;
            }

            case "gitDiffToRemote":
            {
                var cwd = Str(paramsEl, "cwd") ?? Environment.CurrentDirectory;
                Result(id, new { sha = GitProbe.Collect(cwd)?.Commit, diff = GitProbe.DiffToRemote(cwd), workingTreeDiff = GitProbe.WorkingTreeDiff(cwd), worktrees = GitProbe.ListWorktrees(cwd).Select(w => new { path = w.Path, head = w.Head, branch = w.Branch }).ToArray() });
                break;
            }

            case "getConversationSummary":
            {
                var threadId = Str(paramsEl, "threadId") ?? Str(paramsEl, "conversationId") ?? "";
                var info = new JsonlThreadStore().Find(threadId);
                Result(id, new {
                    summary = new {
                        conversationId = threadId,
                        title = info?.Title ?? threadId,
                        cwd = info?.Cwd,
                        model = info?.Model,
                    }
                });
                break;
            }

            case "model/list":
            {
                var data = ModelCatalog.List().Select(m => new { id = m.Id, provider = m.Provider }).ToArray();
                Result(id, new { data, nextCursor = (string?)null });
                break;
            }

            case "review/start":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (!_threads.TryGetValue(threadId, out var reviewSession))
                {
                    Error(id, -32001, $"Thread not loaded: {threadId}");
                    break;
                }
                var prompt = ReviewPrompts.FromParams(paramsEl, reviewSession.Config.Cwd);
                Result(id, new { reviewThreadId = threadId, turn = new { id = "pending", status = "in_progress" } });
                _ = Task.Run(() => reviewSession.RunTurnAsync(prompt, ct), ct);
                break;
            }

            case "thread/compact/start":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (!_threads.TryGetValue(threadId, out var session))
                {
                    Error(id, -32001, $"Thread not loaded: {threadId}");
                    break;
                }
                var compacted = session.TryCompact();
                Notify("thread/compacted", new { threadId, compacted });
                Result(id, new { });
                break;
            }

            case "thread/backgroundTerminals/list":
            {
                if (!RequireExperimental(id, hasId, "thread/backgroundTerminals/list")) break;
                var threadId = Str(paramsEl, "threadId") ?? "";
                var data = _backgroundTerminals.TryGetValue(threadId, out var list) ? list.ToArray() : [];
                Result(id, new { data, nextCursor = (string?)null });
                break;
            }

            case "thread/backgroundTerminals/clean":
            {
                if (!RequireExperimental(id, hasId, "thread/backgroundTerminals/clean")) break;
                _backgroundTerminals.Remove(Str(paramsEl, "threadId") ?? "");
                Result(id, new { });
                break;
            }

            case "thread/backgroundTerminals/terminate":
            {
                if (!RequireExperimental(id, hasId, "thread/backgroundTerminals/terminate")) break;
                var threadId = Str(paramsEl, "threadId") ?? "";
                var processId = Str(paramsEl, "processId") ?? "";
                var terminated = _exec.Terminate(processId);
                Result(id, new { terminated });
                break;
            }

            case "thread/shellCommand":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                var command = Str(paramsEl, "command") ?? "";
                if (!_threads.TryGetValue(threadId, out var session))
                {
                    Error(id, -32001, $"Thread not loaded: {threadId}");
                    break;
                }
                var exec = new ShellExecutor(new WorkspaceSandbox(session.Config), session.Config);
                var result = await exec.RunAsync(new ToolCallRequest(Ids.call (), "shell", JsonSerializer.Serialize(new { command })), ct);
                Result(id, new { output = result.Output, isError = result.IsError });
                break;
            }


            case "config/value/write":
            {
                var key = Str(paramsEl, "keyPath") ?? Str(paramsEl, "key") ?? "";
                if (string.IsNullOrWhiteSpace(key))
                {
                    Error(id, -32602, "keyPath is required");
                    break;
                }
                var value = "";
                if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("value", out var v))
                {
                    value = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
                }
                ConfigService.WriteUserKeys(new Dictionary<string, string> { [key] = value });
                ApplyLiveConfig(new Dictionary<string, string> { [key] = value });
                Result(id, new { });
                break;
            }

            case "config/batchWrite":
            {
                var edits = new Dictionary<string, string>();
                if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("edits", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in arr.EnumerateArray())
                    {
                        var key = Str(e, "keyPath") ?? Str(e, "key") ?? "";
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            continue;
                        }
                        var value = "";
                        if (e.TryGetProperty("value", out var v))
                        {
                            value = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
                        }
                        edits[key] = value;
                    }
                }
                if (edits.Count == 0)
                {
                    Error(id, -32602, "No config keys to write");
                    break;
                }
                ConfigService.WriteUserKeys(edits);
                ApplyLiveConfig(edits);
                Result(id, new { });
                break;
            }


            case "thread/turns/list":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (!_threads.TryGetValue(threadId, out var listedTurns))
                {
                    Error(id, -32001, $"Thread not loaded: {threadId}");
                    break;
                }
                var itemsView = Str(paramsEl, "itemsView");
                var data = listedTurns.Turns().Select(turn => new
                {
                    id = turn.Id,
                    status = "completed",
                    itemsView = string.IsNullOrWhiteSpace(itemsView) ? "summary" : itemsView,
                    items = turn.Messages.Select((message, i) => ItemRow(turn.Id, i, message, itemsView)).ToArray(),
                }).ToList();
                var sort = Str(paramsEl, "sortDirection");
                var desc = sort is null || string.Equals(sort, "desc", StringComparison.OrdinalIgnoreCase);
                var (page, next, backwards) = TimelinePaging.Slice(data, Str(paramsEl, "cursor"), Int(paramsEl, "limit", 50), desc);
                Result(id, new { data = page, nextCursor = next, backwardsCursor = backwards });
                break;
            }


            case "thread/search":
                if (!RequireExperimental(id, hasId, "thread/search")) break;
            {
                var term = Str(paramsEl, "searchTerm") ?? Str(paramsEl, "query") ?? "";
                var archived = Bool(paramsEl, "archived") ?? false;
                var hits = new JsonlThreadStore().Search(term, archived).Select(h => new
                {
                    thread = new { id = h.Thread.Id, title = h.Thread.Title, cwd = h.Thread.Cwd, model = h.Thread.Model },
                    snippet = h.Snippet,
                }).ToArray();
                Result(id, new { data = hits, nextCursor = (string?)null });
                break;
            }

            case "thread/queue/add":
                if (!RequireExperimental(id, hasId, "thread/queue/add")) break;
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (!_threads.ContainsKey(threadId) && new JsonlThreadStore().Find(threadId) is null)
                {
                    Error(id, -32001, $"Thread not loaded: {threadId}");
                    break;
                }
                var text = ReadInputText(paramsEl);
                var item = ThreadQueueStore.Add(threadId, text);
                Notify("thread/queue/changed", new { threadId, data = QueueDto(threadId) });
                Result(id, new { queuedSubmission = new { id = item.Id, input = new[] { new { type = "text", text } } } });
                break;
            }

            case "thread/queue/list":
                if (!RequireExperimental(id, hasId, "thread/queue/list")) break;
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                Result(id, new { data = QueueDto(threadId), nextCursor = (string?)null });
                break;
            }

            case "thread/queue/delete":
                if (!RequireExperimental(id, hasId, "thread/queue/delete")) break;
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                var itemId = Str(paramsEl, "id") ?? Str(paramsEl, "queuedSubmissionId") ?? "";
                ThreadQueueStore.Delete(threadId, itemId);
                Notify("thread/queue/changed", new { threadId, data = QueueDto(threadId) });
                Result(id, new { });
                break;
            }

            case "thread/queue/update":
                if (!RequireExperimental(id, hasId, "thread/queue/update")) break;
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                var itemId = Str(paramsEl, "queuedSubmissionId") ?? Str(paramsEl, "id") ?? "";
                var text = ReadInputText(paramsEl);
                var updated = ThreadQueueStore.Update(threadId, itemId, text);
                if (updated is null)
                {
                    Error(id, -32001, "queued submission not found: " + itemId);
                    break;
                }
                Notify("thread/queue/changed", new { threadId, data = QueueDto(threadId) });
                Result(id, new { queuedSubmission = new { id = updated.Id, input = new[] { new { type = "text", text = updated.Text } } } });
                break;
            }

            case "thread/queue/reorder":
                if (!RequireExperimental(id, hasId, "thread/queue/reorder")) break;
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                var ids = new List<string>();
                if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("queuedSubmissionIds", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array)
                {
                    ids.AddRange(idsEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0));
                }
                ThreadQueueStore.Reorder(threadId, ids);
                Notify("thread/queue/changed", new { threadId, data = QueueDto(threadId) });
                Result(id, new { });
                break;
            }

            case "thread/queue/start":
                if (!RequireExperimental(id, hasId, "thread/queue/start")) break;
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (!_threads.TryGetValue(threadId, out var startSession))
                {
                    Error(id, -32001, $"Thread not loaded: {threadId}");
                    break;
                }
                var startId = Str(paramsEl, "queuedSubmissionId") ?? Str(paramsEl, "id");
                if (!string.IsNullOrWhiteSpace(startId))
                {
                    ThreadQueueStore.Promote(threadId, startId);
                }
                DrainQueue(startSession, ct);
                Result(id, new { });
                break;
            }

            case "thread/searchOccurrences":
            {
                if (!RequireExperimental(id, hasId, "thread/searchOccurrences")) break;
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (!_threads.TryGetValue(threadId, out var occSession))
                {
                    Error(id, -32001, $"Thread not loaded: {threadId}");
                    break;
                }
                var term = Str(paramsEl, "searchTerm") ?? Str(paramsEl, "query") ?? "";
                var found = occSession.SearchOccurrences(term)
                    .Select(o => new
                    {
                        turnId = o.TurnId,
                        itemId = o.ItemId,
                        snippet = o.Snippet,
                        snippetMatchRange = new { start = o.Start, end = o.End },
                        turnCursor = o.TurnCursor,
                    })
                    .ToList();
                var limit = Int(paramsEl, "limit", 50);
                var (page, next, backwards) = TimelinePaging.Slice(found, Str(paramsEl, "cursor"), limit);
                Result(id, new { data = page, nextCursor = next, backwardsCursor = backwards });
                break;
            }

            case "thread/timeline/list":
            {
                if (!RequireExperimental(id, hasId, "thread/timeline/list")) break;
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (!_threads.TryGetValue(threadId, out var timelineSession))
                {
                    Error(id, -32001, $"Thread not loaded: {threadId}");
                    break;
                }
                var data = new List<object>();
                var position = 0L;
                foreach (var turn in timelineSession.Turns())
                {
                    data.Add(new { type = "turnStarted", position = position++, turnId = turn.Id });
                    var i = 0;
                    foreach (var message in turn.Messages)
                    {
                        data.Add(new
                        {
                            type = "item",
                            position = position++,
                            turnId = turn.Id,
                            item = new
                            {
                                id = $"{turn.Id}_{i++}",
                                type = message.Role switch
                                {
                                    "user" => "user_message",
                                    "assistant" => "agent_message",
                                    "tool" => "tool_call",
                                    _ => message.Role,
                                },
                                text = message.Content,
                            },
                        });
                    }
                    data.Add(new { type = "turnCompleted", position = position++, turnId = turn.Id, status = "completed" });
                }
                var limit = Int(paramsEl, "limit", 50);
                var desc = string.Equals(Str(paramsEl, "sortDirection"), "desc", StringComparison.OrdinalIgnoreCase);
                var (page, next, backwards) = TimelinePaging.Slice(data, Str(paramsEl, "cursor"), limit, desc);
                Result(id, new { data = page, nextCursor = next, backwardsCursor = backwards, activeRealtimeSessionAtPageStart = (string?)null });
                break;
            }

            case "thread/rollback":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                var n = Int(paramsEl, "numTurns", 1);
                if (!_threads.TryGetValue(threadId, out var rb))
                {
                    Error(id, -32001, $"Thread not loaded: {threadId}");
                    break;
                }
                if (n < 1)
                {
                    Error(id, -32602, "numTurns must be >= 1");
                    break;
                }
                rb.RollbackTurns(n);
                Result(id, new { thread = ThreadDto(rb) });
                break;
            }

            case "thread/revert":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                var before = Str(paramsEl, "beforeTurnId") ?? "";
                if (!_threads.TryGetValue(threadId, out var reverted))
                {
                    Error(id, -32001, $"Thread not loaded: {threadId}");
                    break;
                }
                if (string.IsNullOrWhiteSpace(before) || !reverted.RevertBefore(before))
                {
                    Error(id, -32602, "beforeTurnId not found");
                    break;
                }
                Result(id, new { thread = ThreadDto(reverted), turnsBackwardsCursor = (string?)null });
                break;
            }

            case "thread/items/list":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (!_threads.TryGetValue(threadId, out var listed))
                {
                    Error(id, -32001, $"Thread not loaded: {threadId}");
                    break;
                }
                var itemsView = Str(paramsEl, "itemsView");
                var filterTurn = Str(paramsEl, "turnId");
                var rows = new List<object>();
                foreach (var turn in listed.Turns())
                {
                    if (!string.IsNullOrEmpty(filterTurn) && turn.Id != filterTurn)
                    {
                        continue;
                    }

                    var i = 0;
                    foreach (var message in turn.Messages)
                    {
                        rows.Add(ItemRow(turn.Id, i++, message, itemsView));
                    }
                }
                var desc = string.Equals(Str(paramsEl, "sortDirection"), "desc", StringComparison.OrdinalIgnoreCase);
                var (page, next, backwards) = TimelinePaging.Slice(rows, Str(paramsEl, "cursor"), Int(paramsEl, "limit", 50), desc);
                Result(id, new { data = page, nextCursor = next, backwardsCursor = backwards });
                break;
            }

            case "app/list":
            case "app/installed":
                Result(id, new { data = Array.Empty<object>() });
                break;

            case "project/list":
            {
                if (!RequireExperimental(id, hasId, "project/list")) break;
                var sortKey = Str(paramsEl, "sortKey") ?? "position";
                var desc = string.Equals(Str(paramsEl, "sortDirection"), "desc", StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(sortKey, "recencyAt", StringComparison.OrdinalIgnoreCase) && !string.Equals(Str(paramsEl, "sortDirection"), "asc", StringComparison.OrdinalIgnoreCase));
                var data = ProjectStore.List(sortKey, desc).Select(ProjectDto).ToList();
                var (page, next, backwards) = TimelinePaging.Slice(data, Str(paramsEl, "cursor"), Int(paramsEl, "limit", 50));
                Result(id, new { data = page, nextCursor = next, backwardsCursor = backwards });
                break;
            }

            case "project/read":
            {
                if (!RequireExperimental(id, hasId, "project/read")) break;
                var projectId = Str(paramsEl, "projectId") ?? "";
                var project = ProjectStore.Read(projectId);
                if (project is null)
                {
                    Error(id, -32001, "Project not found: " + projectId);
                    break;
                }
                Result(id, new { project = ProjectDto(project) });
                break;
            }

            case "project/create":
            {
                if (!RequireExperimental(id, hasId, "project/create")) break;
                try
                {
                    var mutation = ProjectStore.Create(Str(paramsEl, "name") ?? "", ReadRoots(paramsEl), ReadMetadata(paramsEl), Str(paramsEl, "idempotencyKey") ?? "");
                    if (mutation.Created)
                    {
                        Notify("project/changed", new { type = "created", project = ProjectDto(mutation.Project) });
                    }
                    Result(id, new { project = ProjectDto(mutation.Project) });
                }
                catch (ArgumentException ex)
                {
                    Error(id, -32602, ex.Message);
                }
                break;
            }

            case "project/import":
            {
                if (!RequireExperimental(id, hasId, "project/import")) break;
                try
                {
                    var threadIds = new List<string>();
                    if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("threads", out var th) && th.ValueKind == JsonValueKind.Array)
                    {
                        threadIds.AddRange(th.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0));
                    }
                    var mutation = ProjectStore.Import(Str(paramsEl, "name") ?? "", ReadRoots(paramsEl), ReadMetadata(paramsEl), threadIds, Str(paramsEl, "idempotencyKey") ?? "");
                    if (mutation.Created)
                    {
                        Notify("project/changed", new { type = "created", project = ProjectDto(mutation.Project) });
                        foreach (var threadId in threadIds)
                        {
                            Notify("thread/project/updated", new { threadId, projectId = mutation.Project.Id });
                        }
                    }
                    Result(id, new { project = ProjectDto(mutation.Project) });
                }
                catch (ArgumentException ex)
                {
                    Error(id, -32602, ex.Message);
                }
                break;
            }

            case "project/update":
            {
                if (!RequireExperimental(id, hasId, "project/update")) break;
                var projectId = Str(paramsEl, "projectId") ?? "";
                IEnumerable<string>? roots = paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("roots", out _) ? ReadRoots(paramsEl) : null;
                var metadata = paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("metadata", out _) ? ReadMetadata(paramsEl) : null;
                var updated = ProjectStore.Update(projectId, Str(paramsEl, "name"), roots, metadata);
                if (updated is null)
                {
                    Error(id, -32001, "Project not found: " + projectId);
                    break;
                }
                Notify("project/changed", new { type = "updated", project = ProjectDto(updated) });
                Result(id, new { project = ProjectDto(updated) });
                break;
            }

            case "project/move":
            {
                if (!RequireExperimental(id, hasId, "project/move")) break;
                var projectId = Str(paramsEl, "projectId") ?? "";
                if (!ProjectStore.Move(projectId, Str(paramsEl, "beforeProjectId")))
                {
                    Error(id, -32001, "Project not found: " + projectId);
                    break;
                }
                Notify("project/changed", new { type = "updated", projectId });
                Result(id, new { });
                break;
            }

            case "project/delete":
            {
                if (!RequireExperimental(id, hasId, "project/delete")) break;
                var projectId = Str(paramsEl, "projectId") ?? "";
                if (!ProjectStore.Delete(projectId))
                {
                    Error(id, -32001, "Project not found: " + projectId);
                    break;
                }
                Notify("project/changed", new { type = "deleted", projectId });
                Result(id, new { });
                break;
            }

            case "project/trust/read":
            {
                var cwd = Str(paramsEl, "cwd") ?? Environment.CurrentDirectory;
                Result(id, new
                {
                    cwd,
                    trustTarget = ProjectTrust.TrustTarget(cwd),
                    trusted = ProjectTrust.IsTrusted(cwd),
                });
                break;
            }

            case "project/trust/set":
            {
                var cwd = Str(paramsEl, "cwd") ?? Environment.CurrentDirectory;
                var trusted = Flag(paramsEl, "trusted")
                    || string.Equals(Str(paramsEl, "trustLevel"), "trusted", StringComparison.OrdinalIgnoreCase);
                if (trusted)
                {
                    ProjectTrust.Trust(cwd);
                }
                Result(id, new
                {
                    cwd,
                    trustTarget = ProjectTrust.TrustTarget(cwd),
                    trusted = ProjectTrust.IsTrusted(cwd),
                });
                break;
            }

            case "config/write":
            {
                var edits = new Dictionary<string, string>();
                if (paramsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in paramsEl.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String && prop.Name is not "threadId")
                        {
                            edits[prop.Name] = prop.Value.GetString() ?? "";
                        }
                    }
                }
                if (edits.Count == 0)
                {
                    Error(id, -32602, "No config keys to write");
                    break;
                }
                ConfigService.WriteUserKeys(edits);
                ApplyLiveConfig(edits);
                Result(id, new { });
                break;
            }


            case "fs/readFile":
            {
                var path = Str(paramsEl, "path") ?? "";
                if (!TryHostPath(path, out var full, out var err))
                {
                    Error(id, -32602, err);
                    break;
                }
                if (!File.Exists(full))
                {
                    Error(id, -32004, $"file not found: {full}");
                    break;
                }
                Result(id, new { dataBase64 = Convert.ToBase64String(File.ReadAllBytes(full)) });
                break;
            }

            case "fs/writeFile":
            {
                var path = Str(paramsEl, "path") ?? "";
                if (!TryHostPath(path, out var full, out var err))
                {
                    Error(id, -32602, err);
                    break;
                }
                var b64 = Str(paramsEl, "dataBase64") ?? "";
                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(b64);
                }
                catch (FormatException)
                {
                    Error(id, -32602, "fs/writeFile requires valid base64 dataBase64");
                    break;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, bytes);
                Result(id, new { });
                break;
            }

            case "fs/createDirectory":
            {
                var path = Str(paramsEl, "path") ?? "";
                if (!TryHostPath(path, out var full, out var err))
                {
                    Error(id, -32602, err);
                    break;
                }
                var recursive = Bool(paramsEl, "recursive") ?? true;
                if (!recursive)
                {
                    var parent = Path.GetDirectoryName(full);
                    if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    {
                        Error(id, -32004, $"parent directory missing: {parent}");
                        break;
                    }
                }
                Directory.CreateDirectory(full);
                Result(id, new { });
                break;
            }

            case "fs/getMetadata":
            {
                var path = Str(paramsEl, "path") ?? "";
                if (!TryHostPath(path, out var full, out var err))
                {
                    Error(id, -32602, err);
                    break;
                }
                if (!File.Exists(full) && !Directory.Exists(full))
                {
                    Error(id, -32004, $"path not found: {full}");
                    break;
                }
                var isDir = Directory.Exists(full);
                FileSystemInfo info = isDir ? new DirectoryInfo(full) : new FileInfo(full);
                Result(id, new
                {
                    isDirectory = isDir,
                    isFile = !isDir && File.Exists(full),
                    isSymlink = info.Attributes.HasFlag(FileAttributes.ReparsePoint),
                    createdAtMs = new DateTimeOffset(info.CreationTimeUtc).ToUnixTimeMilliseconds(),
                    modifiedAtMs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                });
                break;
            }

            case "fs/readDirectory":
            {
                var path = Str(paramsEl, "path") ?? "";
                if (!TryHostPath(path, out var full, out var err))
                {
                    Error(id, -32602, err);
                    break;
                }
                if (!Directory.Exists(full))
                {
                    Error(id, -32004, $"directory not found: {full}");
                    break;
                }
                var entries = new DirectoryInfo(full).EnumerateFileSystemInfos()
                    .Select(e => new
                    {
                        fileName = e.Name,
                        isDirectory = e is DirectoryInfo,
                        isFile = e is FileInfo,
                    })
                    .ToArray();
                Result(id, new { entries });
                break;
            }

            case "fs/remove":
            {
                var path = Str(paramsEl, "path") ?? "";
                if (!TryHostPath(path, out var full, out var err))
                {
                    Error(id, -32602, err);
                    break;
                }
                var recursive = Bool(paramsEl, "recursive") ?? true;
                var force = Bool(paramsEl, "force") ?? true;
                if (Directory.Exists(full))
                {
                    Directory.Delete(full, recursive);
                }
                else if (File.Exists(full))
                {
                    File.Delete(full);
                }
                else if (!force)
                {
                    Error(id, -32004, $"path not found: {full}");
                    break;
                }
                Result(id, new { });
                break;
            }

            case "fs/copy":
            {
                var src = Str(paramsEl, "sourcePath") ?? "";
                var dst = Str(paramsEl, "destinationPath") ?? "";
                if (!TryHostPath(src, out var srcFull, out var err))
                {
                    Error(id, -32602, err);
                    break;
                }
                if (!TryHostPath(dst, out var dstFull, out err))
                {
                    Error(id, -32602, err);
                    break;
                }
                var recursive = Bool(paramsEl, "recursive") ?? false;
                if (Directory.Exists(srcFull))
                {
                    if (!recursive)
                    {
                        Error(id, -32602, "fs/copy of a directory requires recursive=true");
                        break;
                    }
                    CopyDirectory(srcFull, dstFull);
                }
                else if (File.Exists(srcFull))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dstFull)!);
                    File.Copy(srcFull, dstFull, overwrite: true);
                }
                else
                {
                    Error(id, -32004, $"path not found: {srcFull}");
                    break;
                }
                Result(id, new { });
                break;
            }

            case "fs/watch":
            {
                var path = Str(paramsEl, "path") ?? Environment.CurrentDirectory;
                if (!Directory.Exists(path) && !File.Exists(path))
                {
                    Error(id, -32602, "path not found");
                    break;
                }
                var watchId = Str(paramsEl, "watchId") ?? Ids.newId("watch");
                var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
                var watcher = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                };
                void OnFs(object _, FileSystemEventArgs e) =>
                    Notify("fs/changed", new { watchId, path = e.FullPath, changedPaths = new[] { e.FullPath }, change = e.ChangeType.ToString() });
                watcher.Changed += OnFs;
                watcher.Created += OnFs;
                watcher.Deleted += OnFs;
                _watches[watchId] = watcher;
                Result(id, new { watchId, path });
                break;
            }

            case "fs/unwatch":
            {
                var watchId = Str(paramsEl, "watchId") ?? "";
                if (_watches.Remove(watchId, out var watcher))
                {
                    watcher.Dispose();
                }
                Result(id, new { });
                break;
            }

            case "plugin/list":
            case "plugin/installed":
            {
                var cfg = ConfigService.Load();
                var data = PluginCatalog.List(cfg.Home, cfg.Cwd).Select(PluginDto).ToArray();
                var available = PluginCatalog.ListAvailable(cfg.Home, cfg.Cwd).Select(PluginDto).ToArray();
                Result(id, new { data, available });
                break;
            }

            case "plugin/search":
            {
                if (!RequireExperimental(id, hasId, "plugin/search")) break;
                var cfg = ConfigService.Load();
                var term = Str(paramsEl, "searchTerm") ?? Str(paramsEl, "query") ?? "";
                var limit = Int(paramsEl, "limit", 50);
                var data = PluginCatalog.Search(cfg.Home, cfg.Cwd, term, limit).Select(plugin => new
                {
                    plugin = PluginDto(plugin),
                    marketplaceName = "local",
                    marketplacePath = (string?)null,
                }).ToArray();
                Result(id, new { data, nextCursor = (string?)null });
                break;
            }

            case "plugin/reconcile":
            {
                var cfg = ConfigService.Load();
                var result = PluginCatalog.Reconcile(cfg.Home, cfg.Cwd, Str(paramsEl, "reason"));
                Result(id, new
                {
                    changedPlugins = result.ChangedPlugins.Select(p => new
                    {
                        id = p.Id,
                        hasMcps = p.HasMcps,
                        hasApps = p.HasApps,
                        hasHooks = p.HasHooks,
                        hasSkills = p.HasSkills,
                    }).ToArray(),
                    failedRemotePluginIds = result.FailedRemotePluginIds,
                    failedMaterializationRemotePluginIds = result.FailedMaterializationRemotePluginIds,
                });
                break;
            }

            case "plugin/skill/read":
            {
                var cfg = ConfigService.Load();
                var pluginId = Str(paramsEl, "pluginId") ?? Str(paramsEl, "pluginName") ?? Str(paramsEl, "remotePluginId") ?? "";
                var skillName = Str(paramsEl, "skillName") ?? "";
                if (string.IsNullOrWhiteSpace(pluginId))
                {
                    Error(id, -32602, "pluginId is required");
                    break;
                }
                var plugin = PluginCatalog.Read(pluginId, cfg.Home, cfg.Cwd);
                if (plugin is null)
                {
                    Error(id, -32001, "Plugin not found: " + pluginId);
                    break;
                }
                Result(id, new { contents = PluginCatalog.ReadSkill(cfg.Home, cfg.Cwd, pluginId, skillName) });
                break;
            }

            case "plugin/read":
            {
                var cfg = ConfigService.Load();
                var name = Str(paramsEl, "pluginName") ?? Str(paramsEl, "pluginId") ?? "";
                var plugin = PluginCatalog.Read(name, cfg.Home, cfg.Cwd, Str(paramsEl, "marketplacePath"));
                if (plugin is null)
                {
                    Error(id, -32001, $"Plugin not found: {name}");
                    break;
                }
                Result(id, new { plugin = PluginDto(plugin) });
                break;
            }

            case "plugin/install":
            {
                var name = Str(paramsEl, "pluginName") ?? "";
                try
                {
                    var plugin = PluginCatalog.Install(name, Str(paramsEl, "marketplacePath"));
                    Result(id, new { appsNeedingAuth = Array.Empty<object>(), authPolicy = "ON_USE", plugin = PluginDto(plugin) });
                }
                catch (Exception ex)
                {
                    Error(id, -32001, ex.Message);
                }
                break;
            }

            case "plugin/share/save":
            {
                try
                {
                    var rec = PluginShares.Save(Str(paramsEl, "pluginPath") ?? "", Str(paramsEl, "discoverability"), Str(paramsEl, "remotePluginId"));
                    Result(id, new { remotePluginId = rec.RemotePluginId, name = rec.Name, pluginPath = rec.PluginPath, discoverability = rec.Discoverability });
                }
                catch (Exception ex)
                {
                    Error(id, -32602, ex.Message);
                }
                break;
            }

            case "plugin/share/list":
                Result(id, new { data = PluginShares.List().Select(s => new { remotePluginId = s.RemotePluginId, name = s.Name, pluginPath = s.PluginPath, discoverability = s.Discoverability }).ToArray() });
                break;

            case "plugin/share/delete":
            {
                var rid = Str(paramsEl, "remotePluginId") ?? "";
                if (!PluginShares.Delete(rid))
                {
                    Error(id, -32001, "share not found: " + rid);
                    break;
                }
                Result(id, new { });
                break;
            }

            case "plugin/share/checkout":
            {
                try
                {
                    var plugin = PluginShares.Checkout(Str(paramsEl, "remotePluginId") ?? "");
                    Result(id, new { plugin = PluginDto(plugin) });
                }
                catch (Exception ex)
                {
                    Error(id, -32001, ex.Message);
                }
                break;
            }

            case "plugin/share/updateTargets":
            {
                var rec = PluginShares.UpdateTargets(Str(paramsEl, "remotePluginId") ?? "", Str(paramsEl, "discoverability"));
                if (rec is null)
                {
                    Error(id, -32001, "share not found");
                    break;
                }
                Result(id, new { remotePluginId = rec.RemotePluginId, discoverability = rec.Discoverability });
                break;
            }

            case "plugin/uninstall":
            {
                var pluginId = Str(paramsEl, "pluginId") ?? Str(paramsEl, "pluginName") ?? "";
                if (!PluginCatalog.Uninstall(pluginId))
                {
                    Error(id, -32001, $"Plugin not installed: {pluginId}");
                    break;
                }
                Result(id, new { });
                break;
            }

            case "marketplace/list":
            {
                var data = MarketplaceStore.List().Select(m => new { name = m.Name, source = m.Source, installedRoot = m.InstalledRoot }).ToArray();
                Result(id, new { data });
                break;
            }

            case "marketplace/add":
            {
                var source = Str(paramsEl, "source") ?? "";
                try
                {
                    var (info, already) = MarketplaceStore.Add(source, Str(paramsEl, "refName"));
                    Result(id, new { alreadyAdded = already, installedRoot = info.InstalledRoot, marketplaceName = info.Name });
                }
                catch (Exception ex)
                {
                    Error(id, -32602, ex.Message);
                }
                break;
            }

            case "marketplace/remove":
            {
                var name = Str(paramsEl, "marketplaceName") ?? Str(paramsEl, "name") ?? "";
                if (!MarketplaceStore.Remove(name))
                {
                    Error(id, -32001, $"Marketplace not found: {name}");
                    break;
                }
                Result(id, new { });
                break;
            }

            case "marketplace/upgrade":
            {
                var name = Str(paramsEl, "marketplaceName") ?? Str(paramsEl, "name") ?? "";
                var info = MarketplaceStore.Upgrade(name);
                if (info is null)
                {
                    Error(id, -32001, $"Marketplace not found: {name}");
                    break;
                }
                Result(id, new { installedRoot = info.InstalledRoot, marketplaceName = info.Name });
                break;
            }

            case "threadSection/list":
            {
                var data = ThreadSections.List().Select(SectionDto).ToList();
                var (page, next, _) = TimelinePaging.Slice(data, Str(paramsEl, "cursor"), Int(paramsEl, "limit", 50));
                Result(id, new { data = page, nextCursor = next });
                break;
            }

            case "threadSection/create":
            {
                var name = Str(paramsEl, "name") ?? "";
                string? color = null;
                string? icon = null;
                if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("appearance", out var appearance) && appearance.ValueKind == JsonValueKind.Object)
                {
                    color = Str(appearance, "color");
                    icon = Str(appearance, "icon");
                }
                try
                {
                    Result(id, new { section = SectionDto(ThreadSections.Create(name, color, icon)) });
                }
                catch (Exception ex)
                {
                    Error(id, -32602, ex.Message);
                }
                break;
            }

            case "threadSection/update":
            {
                var sectionId = Str(paramsEl, "sectionId") ?? "";
                var name = Str(paramsEl, "name") ?? "";
                var appearanceSpecified = false;
                string? color = null;
                string? icon = null;
                if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("appearance", out var appearanceEl))
                {
                    appearanceSpecified = true;
                    if (appearanceEl.ValueKind == JsonValueKind.Object)
                    {
                        color = Str(appearanceEl, "color");
                        icon = Str(appearanceEl, "icon");
                    }
                }
                var updated = ThreadSections.Update(sectionId, name, appearanceSpecified, color, icon);
                if (updated is null)
                {
                    Error(id, -32001, $"Section not found: {sectionId}");
                    break;
                }
                Result(id, new { section = SectionDto(updated) });
                break;
            }

            case "threadSection/delete":
            {
                var sectionId = Str(paramsEl, "sectionId") ?? "";
                if (!ThreadSections.Delete(sectionId))
                {
                    Error(id, -32001, $"Section not found: {sectionId}");
                    break;
                }
                Result(id, new { });
                break;
            }

            case "thread/section/move":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                var sectionId = Str(paramsEl, "sectionId");
                if (!ThreadSections.Move(threadId, sectionId, Str(paramsEl, "beforeThreadId")))
                {
                    Error(id, -32602, "Unable to move thread into section");
                    break;
                }
                Result(id, new { });
                break;
            }

            case "hooks/list":
            {
                var cfg = ConfigService.Load();
                var data = HookCatalog.List(cfg.Home, cfg.Cwd).Select(h => new { name = h.Name, @event = h.Event, command = h.Command, path = h.Path, source = h.Source }).ToArray();
                Result(id, new { data });
                break;
            }

            case "configRequirements/read":
                Result(id, new
                {
                    requirements = (object?)null,
                    allowBrowserAndComputerUse = false,
                    note = "No requirements.toml / MDM policy is configured.",
                });
                break;

            case "skills/extraRoots/set":
            {
                var roots = new List<string>();
                if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("extraRoots", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    roots.AddRange(arr.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0));
                }
                else if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("additionalRoots", out var arr2) && arr2.ValueKind == JsonValueKind.Array)
                {
                    roots.AddRange(arr2.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0));
                }
                SkillExtraRoots.Set(roots);
                Notify("skills/changed", new { extraRoots = SkillExtraRoots.Get() });
                Result(id, new { extraRoots = SkillExtraRoots.Get() });
                break;
            }

            case "prompts/list":
            {
                var cfg = ConfigService.Load();
                var data = PromptCatalog.List(cfg.Home, cfg.Cwd).Select(p => new { name = p.Name, path = p.Path, preview = p.Preview }).ToArray();
                Result(id, new { data });
                break;
            }

            case "prompts/read":
            {
                var cfg = ConfigService.Load();
                var name = Str(paramsEl, "name") ?? "";
                var body = PromptCatalog.Read(name, cfg.Home, cfg.Cwd);
                if (body is null)
                {
                    Error(id, -32001, "unknown prompt " + name);
                    break;
                }
                Result(id, new { name, contents = body });
                break;
            }

            case "skills/list":
            {
                var cfg = ConfigService.Load();
                var data = SkillCatalog.Load(cfg.Home, cfg.Cwd).Select(s => new
                {
                    name = s.Name,
                    path = s.Path,
                    description = s.Description,
                    enabled = SkillConfig.IsEnabled(s.Name, s.Path),
                }).ToArray();
                Result(id, new { data });
                break;
            }

            case "skills/config/write":
            {
                var enabled = Bool(paramsEl, "enabled") ?? false;
                var name = Str(paramsEl, "name");
                var path = Str(paramsEl, "path");
                var effective = SkillConfig.Set(name, path, enabled);
                Notify("skills/changed", new { name, path, enabled = effective });
                Result(id, new { effectiveEnabled = effective });
                break;
            }

            case "model/provider/capabilities/read":
            case "modelProvider/capabilities/read":
            {
                Result(id, new
                {
                    imageGeneration = false,
                    namespaceTools = true,
                    webSearch = FeatureFlags.IsEnabled("web_search"),
                });
                break;
            }

            case "app/read":
            case "apps/read":
            {
                var ids = new List<string>();
                if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("appIds", out var appIds) && appIds.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in appIds.EnumerateArray())
                    {
                        var value = item.GetString();
                        if (!string.IsNullOrWhiteSpace(value) && !ids.Contains(value))
                        {
                            ids.Add(value);
                            if (ids.Count >= 100)
                            {
                                break;
                            }
                        }
                    }
                }
                Result(id, new
                {
                    data = ids.Select(appId => new { id = appId, name = appId, installed = false, tools = Array.Empty<object>() }).ToArray(),
                });
                break;
            }

            case "mcpServer/oauth/login":
            {
                var name = Str(paramsEl, "name") ?? "";
                Notify("mcpServer/oauthLogin/completed", new { name, threadId = Str(paramsEl, "threadId"), success = false, error = "MCP OAuth is not configured in CodexSharp." });
                Error(id, -32002, "MCP OAuth is not configured in CodexSharp.");
                break;
            }

            case "mcpServer/resource/read":
            {
                var server = Str(paramsEl, "server") ?? "";
                var uri = Str(paramsEl, "uri") ?? "";
                if (string.IsNullOrWhiteSpace(uri))
                {
                    Error(id, -32602, "uri is required");
                    break;
                }
                var spec = ConfigService.LoadMcpServers().FirstOrDefault(s => s.Id == server);
                if (spec is null)
                {
                    Error(id, -32001, $"Unknown MCP server {server}");
                    break;
                }
                try
                {
                    await using var mcp = string.IsNullOrWhiteSpace(spec.Url)
                        ? (IMcpSession)McpStdioClient.Start(spec)
                        : new McpHttpClient(spec);
                    await mcp.InitializeAsync(ct);
                    var contents = await mcp.ReadResourceAsync(uri, ct);
                    object payload = contents.TryGetProperty("contents", out var arr)
                        ? new { contents = JsonSerializer.Deserialize<object>(arr.GetRawText()) }
                        : new { contents = new object[] { new { uri, text = contents.GetRawText() } } };
                    Result(id, payload);
                }
                catch (Exception ex)
                {
                    Error(id, -32000, ex.Message);
                }
                break;
            }

            case "config/mcpServer/reload":
            {
                var servers = ConfigService.LoadMcpServers().Select(s => new
                {
                    name = s.Id,
                    enabled = s.Enabled,
                    transport = string.IsNullOrWhiteSpace(s.Url) ? "stdio" : "streamableHttp",
                }).ToArray();
                Notify("mcpServer/startupStatus/updated", new { data = servers });
                Result(id, new { data = servers });
                break;
            }

            case "mcpServer/tool/call":
            {
                var server = Str(paramsEl, "server") ?? "";
                var tool = Str(paramsEl, "tool") ?? "";
                var spec = ConfigService.LoadMcpServers().FirstOrDefault(s => s.Id == server);
                if (spec is null)
                {
                    Error(id, -32001, $"Unknown MCP server {server}");
                    break;
                }
                var argsJson = "{}";
                if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("arguments", out var argsEl))
                {
                    argsJson = argsEl.GetRawText();
                }
                try
                {
                    await using var mcp = McpStdioClient.Start(spec);
                    await mcp.InitializeAsync(ct);
                    var output = await mcp.CallToolAsync(tool, argsJson, ct);
                    Result(id, new { content = new[] { new { type = "text", text = output } }, isError = false });
                }
                catch (Exception ex)
                {
                    Error(id, -32000, ex.Message);
                }
                break;
            }

            case "mcpServerStatus/list":
            {
                var servers = ConfigService.LoadMcpServers().Select(s => new
                {
                    name = s.Id,
                    enabled = s.Enabled,
                    transport = string.IsNullOrWhiteSpace(s.Url) ? "stdio" : "streamableHttp",
                    command = s.Command,
                    args = s.Args,
                    url = s.Url,
                    bearerTokenEnvVar = s.BearerTokenEnvVar,
                }).ToArray();
                Result(id, new { data = servers });
                break;
            }

            case "mcpServer/add":
            {
                var name = Str(paramsEl, "name") ?? "";
                try
                {
                    var url = Str(paramsEl, "url");
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        McpConfig.AddHttp(name, url, Str(paramsEl, "bearerTokenEnvVar"));
                    }
                    else
                    {
                        var argv = new List<string>();
                        var command = Str(paramsEl, "command");
                        if (!string.IsNullOrWhiteSpace(command)) argv.Add(command);
                        if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
                        {
                            argv.AddRange(argsEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0));
                        }
                        McpConfig.AddStdio(name, argv);
                    }
                    Result(id, new { name });
                }
                catch (Exception ex)
                {
                    Error(id, -32602, ex.Message);
                }
                break;
            }

            case "mcpServer/remove":
            {
                var name = Str(paramsEl, "name") ?? "";
                if (!McpConfig.Remove(name))
                {
                    Error(id, -32001, "MCP server not found: " + name);
                    break;
                }
                Result(id, new { });
                break;
            }

            case "fuzzyFileSearch/sessionStart":
            {
                if (!RequireExperimental(id, hasId, "fuzzyFileSearch/sessionStart")) break;
                var sessionId = Str(paramsEl, "sessionId") ?? "";
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    Error(id, -32602, "sessionId is required");
                    break;
                }
                var roots = new List<string>();
                if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("roots", out var rootEl) && rootEl.ValueKind == JsonValueKind.Array)
                {
                    roots.AddRange(rootEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0));
                }
                if (roots.Count == 0) roots.Add(ConfigService.Load().Cwd);
                _fuzzySessions[sessionId] = roots.ToArray();
                Result(id, new { });
                break;
            }

            case "fuzzyFileSearch/sessionUpdate":
            {
                if (!RequireExperimental(id, hasId, "fuzzyFileSearch/sessionUpdate")) break;
                var sessionId = Str(paramsEl, "sessionId") ?? "";
                if (!_fuzzySessions.TryGetValue(sessionId, out var roots))
                {
                    Error(id, -32001, "fuzzy search session not found: " + sessionId);
                    break;
                }
                var query = Str(paramsEl, "query") ?? "";
                var files = FuzzyFiles(roots, query);
                Notify("fuzzyFileSearch/sessionUpdated", new { sessionId, query, files });
                Notify("fuzzyFileSearch/sessionCompleted", new { sessionId });
                Result(id, new { });
                break;
            }

            case "fuzzyFileSearch/sessionStop":
            {
                if (!RequireExperimental(id, hasId, "fuzzyFileSearch/sessionStop")) break;
                _fuzzySessions.Remove(Str(paramsEl, "sessionId") ?? "");
                Result(id, new { });
                break;
            }

            case "fuzzyFileSearch":
            {
                var query = Str(paramsEl, "query") ?? "";
                var cfg = ConfigService.Load();
                var sandbox = new WorkspaceSandbox(cfg);
                var result = FileSearch.SearchNames(new ToolCallRequest("search", "file_search", JsonSerializer.Serialize(new { query, path = cfg.Cwd })), sandbox);
                var matches = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => line != "(no matches)")
                    .Select(path => new { path, fileName = Path.GetFileName(path), score = 1, matchType = "file" })
                    .ToArray();
                Result(id, new { matches });
                break;
            }

            case "thread/metadata/update":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("isPinned", out var pin) && pin.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    ThreadPins.Set(threadId, pin.GetBoolean());
                }
                if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("projectId", out var metaProject))
                {
                    if (!RequireExperimental(id, hasId, "thread/metadata/update.projectId")) break;
                    var pid = metaProject.ValueKind == JsonValueKind.String ? metaProject.GetString() : null;
                    if (!ProjectStore.Assign(threadId, pid))
                    {
                        Error(id, -32001, "Project not found: " + pid);
                        break;
                    }
                    Notify("thread/project/updated", new { threadId, projectId = pid });
                }
                Result(id, new { });
                break;
            }

            case "thread/memoryMode/set":
            {
                if (!RequireExperimental(id, hasId, "thread/memoryMode/set")) break;
                try
                {
                    MemoryStore.SetMode(Str(paramsEl, "threadId") ?? "", Str(paramsEl, "mode") ?? "");
                    Result(id, new { });
                }
                catch (ArgumentException ex)
                {
                    Error(id, -32602, ex.Message);
                }
                break;
            }

            case "memory/list":
            {
                var notes = MemoryStore.ListNotes().Select(n => new { name = n, path = Path.Combine(MemoryStore.MemoriesRoot, n.Replace('/', Path.DirectorySeparatorChar)) }).ToArray();
                Result(id, new { data = notes });
                break;
            }

            case "memory/reset":
            {
                if (!RequireExperimental(id, hasId, "memory/reset")) break;
                MemoryStore.Reset();
                Result(id, new { });
                break;
            }

            case "memory/read":
            {
                try
                {
                    var name = Str(paramsEl, "name") ?? "";
                    var body = MemoryStore.ReadNote(name);
                    Result(id, new { name = name, text = body });
                }
                catch (Exception ex)
                {
                    Error(id, -32602, ex.Message);
                }
                break;
            }

            case "memory/write":
            {
                try
                {
                    var name = Str(paramsEl, "name") ?? "";
                    var body = Str(paramsEl, "text") ?? Str(paramsEl, "content") ?? "";
                    var written = MemoryStore.WriteNote(name, body);
                    Result(id, new { name = written });
                }
                catch (Exception ex)
                {
                    Error(id, -32602, ex.Message);
                }
                break;
            }

            case "tui/keymap/list":
            {
                var data = TuiKeymap.Load().Select(b => new { context = b.Context, action = b.Action, keys = b.Keys }).ToArray();
                Result(id, new { data });
                break;
            }

            case "tui/keymap/set":
            {
                var context = Str(paramsEl, "context") ?? "";
                var action = Str(paramsEl, "action") ?? "";
                var keys = Str(paramsEl, "keys") ?? "";
                if (!TuiKeymap.TrySet(context, action, keys))
                {
                    Error(id, -32602, "unknown keymap slot");
                    break;
                }
                Result(id, new { context, action, keys });
                break;
            }

            case "tui/pets/read":
            {
                Result(id, new { id = TuiPets.Current, ascii = TuiPets.Ascii(), ids = TuiPets.Ids });
                break;
            }

            case "tui/pets/set":
            {
                var pet = Str(paramsEl, "id") ?? Str(paramsEl, "name") ?? "";
                if (!TuiPets.TrySet(pet))
                {
                    Error(id, -32602, "unknown pet");
                    break;
                }
                Result(id, new { id = TuiPets.Current, ascii = TuiPets.Ascii() });
                break;
            }

            case "thread/settings/update":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (!_threads.TryGetValue(threadId, out var settingsSession))
                {
                    Error(id, -32001, $"Thread not loaded: {threadId}");
                    break;
                }
                settingsSession.ApplySettings(
                    Str(paramsEl, "model"),
                    Str(paramsEl, "sandboxMode") ?? Str(paramsEl, "sandboxPolicy"),
                    Str(paramsEl, "approvalPolicy"),
                    Str(paramsEl, "cwd"),
                    Str(paramsEl, "effort"));
                Notify("thread/settings/updated", new { threadId, thread = ThreadDto(settingsSession) });
                Result(id, new { });
                break;
            }

            case "thread/name/set":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                var name = Str(paramsEl, "name") ?? Str(paramsEl, "title") ?? "";
                if (_threads.TryGetValue(threadId, out var named))
                {
                    named.SetName(name);
                }
                Notify("thread/name/updated", new { threadId, name });
                Result(id, new { });
                break;
            }

            case "thread/unarchive":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (!new JsonlThreadStore().Unarchive(threadId))
                {
                    Error(id, -32001, $"Thread not archived: {threadId}");
                    break;
                }
                Notify("thread/unarchived", new { threadId });
                Result(id, new { });
                break;
            }

            case "thread/delete":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                WorktreeBindings.UnbindAndRemove(threadId);
                _threads.Remove(threadId);
                if (!new JsonlThreadStore().Delete(threadId))
                {
                    Error(id, -32001, $"Unknown thread {threadId}");
                    break;
                }
                Notify("thread/deleted", new { threadId });
                Result(id, new { });
                break;
            }

            case "config/read":
            {
                var cfg = ConfigService.Load();
                Result(id, new {
                    model = cfg.Model,
                    modelProvider = cfg.Provider.Id,
                    approvalPolicy = cfg.ApprovalPolicy,
                    sandboxMode = cfg.SandboxMode,
                    cwd = cfg.Cwd,
                    home = cfg.Home,
                    reasoningEffort = cfg.ReasoningEffort,
                    personality = ConfigService.Peek("personality"),
                    collaborationMode = ConfigService.Peek("collaboration_mode"),
                    computerUse = "notConfigured",
                    browserUse = "notConfigured",
                    extraReadRoots = SandboxRoots.List(),
                    tuiRaw = ConfigService.Peek("tui_raw"),
                    tuiVim = ConfigService.Peek("tui_vim"),
                    tuiTheme = ConfigService.Peek("tui_theme"),
                    tuiStatusline = ConfigService.Peek("tui_statusline"),
                    tuiTitle = ConfigService.Peek("tui_title"),
                    tuiNotifications = ConfigService.Peek("tui_notifications"),
                });
                break;
            }

            case "account/usage/read":
            {
                object? threadUsage = null;
                var live = _threads.Values.FirstOrDefault();
                if (live is not null)
                {
                    var (total, last) = live.EstimateTokens();
                    threadUsage = new { threadId = live.Thread.Id, estimatedTokens = total, lastTokens = last };
                }
                Result(id, new
                {
                    summary = new
                    {
                        currentStreakDays = (long?)null,
                        lifetimeTokens = (long?)null,
                        longestRunningTurnSec = (long?)null,
                        longestStreakDays = (long?)null,
                        peakDailyTokens = (long?)null,
                    },
                    dailyUsageBuckets = (object?)null,
                    threadUsage,
                });
                break;
            }

            case "account/rateLimits/read":
                Result(id, new
                {
                    accountId = (string?)null,
                    ordinaryUsageAllowed = true,
                    rateLimitResetCredits = (object?)null,
                    rateLimitUpsell = (object?)null,
                    rateLimits = new
                    {
                        limitId = (string?)null,
                        limitName = (string?)null,
                        planType = (string?)null,
                        primary = (object?)null,
                        secondary = (object?)null,
                        credits = (object?)null,
                        individualLimit = (object?)null,
                        rateLimitReachedType = (string?)null,
                        spendControlReached = (bool?)null,
                        normalModelSlug = (string?)null,
                    },
                    rateLimitsByLimitId = (object?)null,
                });
                break;

            case "account/read":
            {
                var mode = AuthService.AuthMode();
                var cfg = ConfigService.Load();
                var hasKey = !string.IsNullOrWhiteSpace(cfg.Provider.ApiKey);
                Result(id, new {
                    account = hasKey ? new { type = mode ?? "apiKey" } : null,
                    requiresOpenaiAuth = !hasKey,
                });
                break;
            }

            case "getAuthStatus":
            {
                var mode = AuthService.AuthMode();
                var cfg = ConfigService.Load();
                var hasKey = !string.IsNullOrWhiteSpace(cfg.Provider.ApiKey);
                Result(id, new {
                    authMethod = hasKey ? (mode ?? "apiKey") : null,
                    requiresOpenaiAuth = !hasKey,
                });
                break;
            }

            case "account/login/start":
            {
                var kind = Str(paramsEl, "type") ?? "";
                if (kind is "apiKey")
                {
                    var key = Str(paramsEl, "apiKey") ?? "";
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        Error(id, -32602, "apiKey is required");
                        break;
                    }
                    AuthService.SaveApiKey(key);
                    Notify("account/updated", new { authMode = "apiKey" });
                    Result(id, new { type = "apiKey" });
                    break;
                }

                if (kind is "chatgptDeviceCode")
                {
                    try
                    {
                        var code = await ChatgptDeviceAuth.RequestAsync(ct);
                        var loginCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        _logins[code.LoginId] = loginCts;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await ChatgptDeviceAuth.CompleteAsync(code, loginCts.Token);
                                Notify("account/updated", new { authMode = "chatgpt" });
                                Notify("account/login/completed", new { loginId = code.LoginId, success = true, error = (string?)null });
                            }
                            catch (OperationCanceledException)
                            {
                                Notify("account/login/completed", new { loginId = code.LoginId, success = false, error = "cancelled" });
                            }
                            catch (Exception ex)
                            {
                                Notify("account/login/completed", new { loginId = code.LoginId, success = false, error = ex.Message });
                            }
                            finally
                            {
                                _logins.Remove(code.LoginId);
                                loginCts.Dispose();
                            }
                        }, loginCts.Token);
                        Result(id, new { type = "chatgptDeviceCode", loginId = code.LoginId, verificationUrl = code.VerificationUrl, userCode = code.UserCode });
                    }
                    catch (Exception ex)
                    {
                        Error(id, -32002, ex.Message);
                    }
                    break;
                }

                if (kind is "chatgpt")
                {
                    try
                    {
                        var browser = ChatgptBrowserAuth.Start();
                        var loginCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        _logins[browser.LoginId] = loginCts;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await ChatgptBrowserAuth.WaitAsync(browser, loginCts.Token);
                                Notify("account/updated", new { authMode = "chatgpt" });
                                Notify("account/login/completed", new { loginId = browser.LoginId, success = true, error = (string?)null });
                            }
                            catch (OperationCanceledException)
                            {
                                Notify("account/login/completed", new { loginId = browser.LoginId, success = false, error = "cancelled" });
                            }
                            catch (Exception ex)
                            {
                                Notify("account/login/completed", new { loginId = browser.LoginId, success = false, error = ex.Message });
                            }
                            finally
                            {
                                _logins.Remove(browser.LoginId);
                                loginCts.Dispose();
                                browser.Dispose();
                            }
                        }, loginCts.Token);
                        Result(id, new { type = "chatgpt", loginId = browser.LoginId, authUrl = browser.AuthUrl });
                    }
                    catch (Exception ex)
                    {
                        Error(id, -32002, ex.Message);
                    }
                    break;
                }

                if (kind is "chatgptAuthTokens")
                {
                    var access = Str(paramsEl, "accessToken") ?? Str(paramsEl, "access_token") ?? "";
                    if (string.IsNullOrWhiteSpace(access))
                    {
                        Error(id, -32602, "accessToken is required");
                        break;
                    }
                    var refresh = Str(paramsEl, "refreshToken") ?? Str(paramsEl, "refresh_token") ?? "";
                    var idToken = Str(paramsEl, "idToken") ?? Str(paramsEl, "id_token") ?? "";
                    AuthService.SaveChatgptTokens(access, refresh, idToken);
                    Notify("account/updated", new { authMode = "chatgpt" });
                    Result(id, new { type = "chatgptAuthTokens", authMode = "chatgpt" });
                    break;
                }

                if (kind is "amazonBedrock" or "amazonBedrockAccessKeys")
                {
                    Error(id, -32002, "Amazon Bedrock login is not implemented in CodexSharp.");
                    break;
                }

                Error(id, -32002, "ChatGPT OAuth is not configured in CodexSharp; use type=apiKey.");
                break;
            }

            case "account/bedrock/discover":
            {
                var found = BedrockDiscover.Scan();
                Result(id, new
                {
                    profiles = found.Profiles.Select(p => new { name = p.Name, region = p.Region }).ToArray(),
                    environmentCredentials = found.EnvironmentCredentials.Select(c => new { type = c.Type, region = c.Region }).ToArray(),
                });
                break;
            }

            case "account/bedrock/setup":
                Error(id, -32002, "Amazon Bedrock setup is not implemented in CodexSharp.");
                break;

            case "account/rateLimitResetCredit/consume":
                Result(id, new { outcome = "noCredit" });
                break;

            case "account/sendAddCreditsNudgeEmail":
                Error(id, -32002, "ChatGPT workspace email is not available without the ChatGPT backend.");
                break;

            case "account/login/cancel":
            {
                var loginId = Str(paramsEl, "loginId") ?? "";
                if (!string.IsNullOrWhiteSpace(loginId) && _logins.Remove(loginId, out var loginCts))
                {
                    loginCts.Cancel();
                    loginCts.Dispose();
                }
                Result(id, new { });
                break;
            }

            case "account/chatgptAuthTokens/refresh":
            {
                try
                {
                    await ChatgptDeviceAuth.RefreshAsync(ct);
                    var tokens = AuthService.ReadTokens();
                    Result(id, new { accessToken = tokens.Access ?? "", chatgptAccountId = tokens.AccountId ?? "", chatgptPlanType = (string?)null });
                    Notify("account/updated", new { authMode = "chatgpt" });
                }
                catch (Exception ex)
                {
                    Error(id, -32002, ex.Message);
                }
                break;
            }

            case "account/workspaceMessages/read":
                Result(id, new { messages = Array.Empty<object>() });
                break;

            case "item/tool/call":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (!_threads.TryGetValue(threadId, out var toolSession))
                {
                    Error(id, -32001, "Thread not loaded: " + threadId);
                    break;
                }
                var tool = Str(paramsEl, "tool") ?? "";
                var callId = Str(paramsEl, "callId") ?? Ids.call();
                var args = "{}";
                if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("arguments", out var argEl))
                    args = argEl.GetRawText();
                var exec = new BuiltinToolExecutor(toolSession.Config);
                var result = await exec.ExecuteAsync(new ToolCallRequest(callId, tool, args), ct);
                Result(id, new { success = !result.IsError, contentItems = new object[] { new { type = "inputText", text = result.Output } } });
                break;
            }

            case "thread/approveGuardianDeniedAction":
                Result(id, new { approved = false, reason = "Guardian is not implemented in CodexSharp." });
                break;

            case "attestation/generate":
                Error(id, -32002, "Attestation backend is not configured in CodexSharp.");
                break;

            case "externalAgentConfig/import/recordHistory":
                Result(id, new { data = ExternalAgentImport.ReadHistories() });
                break;

            case "account/logout":
            {
                if (File.Exists(CodexPaths.AuthFile))
                {
                    File.Delete(CodexPaths.AuthFile);
                }
                Notify("account/updated", new { authMode = (string?)null });
                Result(id, new { });
                break;
            }

            case "remoteControl/status/read":
            {
                if (!RequireExperimental(id, hasId, "remoteControl/status/read")) break;
                var snap = RemoteControlState.Read();
                Result(id, new { status = snap.Status, installationId = snap.InstallationId, serverName = snap.ServerName, environmentId = (string?)null });
                break;
            }

            case "remoteControl/enable":
            {
                if (!RequireExperimental(id, hasId, "remoteControl/enable")) break;
                var snap = RemoteControlState.SetEnabled(true);
                Notify("remoteControl/status/changed", new { status = snap.Status, installationId = snap.InstallationId, serverName = snap.ServerName, environmentId = (string?)null });
                Result(id, new { status = snap.Status, installationId = snap.InstallationId, serverName = snap.ServerName, environmentId = (string?)null });
                break;
            }

            case "remoteControl/disable":
            {
                if (!RequireExperimental(id, hasId, "remoteControl/disable")) break;
                var snap = RemoteControlState.SetEnabled(false);
                Notify("remoteControl/status/changed", new { status = snap.Status, installationId = snap.InstallationId, serverName = snap.ServerName, environmentId = (string?)null });
                Result(id, new { status = snap.Status, installationId = snap.InstallationId, serverName = snap.ServerName, environmentId = (string?)null });
                break;
            }

            case "remoteControl/pairing/start":
            {
                if (!RequireExperimental(id, hasId, "remoteControl/pairing/start")) break;
                var manual = Flag(paramsEl, "manualCode");
                var pairing = RemoteControlState.StartPairing(manual);
                Result(id, new
                {
                    pairingCode = pairing.PairingCode,
                    manualPairingCode = pairing.ManualPairingCode,
                    environmentId = pairing.EnvironmentId,
                    expiresAt = pairing.ExpiresAt,
                });
                break;
            }

            case "remoteControl/pairing/status":
            {
                if (!RequireExperimental(id, hasId, "remoteControl/pairing/status")) break;
                try
                {
                    var claimed = RemoteControlState.PairingClaimed(Str(paramsEl, "pairingCode"), Str(paramsEl, "manualPairingCode"));
                    Result(id, new { claimed });
                }
                catch (ArgumentException ex)
                {
                    Error(id, -32602, ex.Message);
                }
                break;
            }

            case "remoteControl/client/list":
            {
                if (!RequireExperimental(id, hasId, "remoteControl/client/list")) break;
                try
                {
                    var data = RemoteControlState.ListClients(Str(paramsEl, "environmentId") ?? "");
                    Result(id, new { data, nextCursor = (string?)null });
                }
                catch (ArgumentException ex)
                {
                    Error(id, -32602, ex.Message);
                }
                break;
            }

            case "remoteControl/client/revoke":
            {
                if (!RequireExperimental(id, hasId, "remoteControl/client/revoke")) break;
                try
                {
                    RemoteControlState.RevokeClient(Str(paramsEl, "environmentId") ?? "", Str(paramsEl, "clientId") ?? "");
                    Result(id, new { });
                }
                catch (ArgumentException ex)
                {
                    Error(id, -32602, ex.Message);
                }
                catch (KeyNotFoundException ex)
                {
                    Error(id, -32001, ex.Message);
                }
                break;
            }

            case "command/exec":
            {
                if (HasPermissionProfile(paramsEl) && !RequireExperimental(id, hasId, "command/exec.permissionProfile"))
                {
                    break;
                }

                var argv = ReadCommandArgv(paramsEl);
                if (argv.Count == 0)
                {
                    Error(id, -32602, "command must be a non-empty argv vector");
                    break;
                }

                var processId = Str(paramsEl, "processId");
                var tty = Flag(paramsEl, "tty");
                var streamStdin = tty || Flag(paramsEl, "streamStdin");
                var streamOut = tty || Flag(paramsEl, "streamStdoutStderr");
                if ((tty || streamStdin || streamOut) && string.IsNullOrWhiteSpace(processId))
                {
                    Error(id, -32602, "processId is required for tty/streamStdin/streamStdoutStderr");
                    break;
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
                var capturedId = id.Clone();
                var capturedHasId = hasId;
                _ = Task.Run(async () =>
                {
                    try
                    {
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
                break;
            }

            case "process/spawn":
            {
                if (!RequireExperimental(id, hasId, "process/spawn")) break;
                var argv = ReadCommandArgv(paramsEl);
                var handle = Str(paramsEl, "processHandle") ?? "";
                if (argv.Count == 0)
                {
                    Error(id, -32602, "command must be a non-empty argv vector");
                    break;
                }
                if (string.IsNullOrWhiteSpace(handle))
                {
                    Error(id, -32602, "processHandle is required");
                    break;
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
                break;
            }

            case "process/writeStdin":
            {
                if (!RequireExperimental(id, hasId, "process/writeStdin")) break;
                var handle = Str(paramsEl, "processHandle") ?? "";
                byte[]? data = null;
                var b64 = Str(paramsEl, "deltaBase64");
                if (!string.IsNullOrWhiteSpace(b64)) data = Convert.FromBase64String(b64);
                if (!_exec.Write(handle, data, Flag(paramsEl, "closeStdin")))
                {
                    Error(id, -32001, "process not found: " + handle);
                    break;
                }
                Result(id, new { });
                break;
            }

            case "process/kill":
            {
                if (!RequireExperimental(id, hasId, "process/kill")) break;
                var handle = Str(paramsEl, "processHandle") ?? "";
                if (!_exec.Terminate(handle))
                {
                    Error(id, -32001, "process not found: " + handle);
                    break;
                }
                Result(id, new { });
                break;
            }

            case "process/resizePty":
            {
                if (!RequireExperimental(id, hasId, "process/resizePty")) break;
                var handle = Str(paramsEl, "processHandle") ?? "";
                if (!_exec.Resize(handle, Int(paramsEl, "rows", 24), Int(paramsEl, "cols", 80)))
                {
                    Error(id, -32001, "process not found: " + handle);
                    break;
                }
                Result(id, new { });
                break;
            }

            case "externalAgentConfig/detect":
            {
                var cwds = new List<string>();
                if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("cwds", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.Array)
                    cwds.AddRange(cwdEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0));
                var includeHome = Flag(paramsEl, "includeHome");
                var items = ExternalAgentImport.Detect(cwds, includeHome).Select(x => new { itemType = x.ItemType, description = x.Description, cwd = x.Cwd }).ToArray();
                Result(id, new { items, connectors = Array.Empty<object>() });
                break;
            }

            case "externalAgentConfig/import":
            {
                var selected = new List<ExternalMigrationItem>();
                if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("migrationItems", out var mig) && mig.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in mig.EnumerateArray())
                    {
                        var type = el.ValueKind == JsonValueKind.Object && el.TryGetProperty("itemType", out var t) ? t.GetString() ?? "" : "";
                        var desc = el.ValueKind == JsonValueKind.Object && el.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                        var cwd = el.ValueKind == JsonValueKind.Object && el.TryGetProperty("cwd", out var c) ? c.GetString() : null;
                        if (type.Length > 0) selected.Add(new ExternalMigrationItem(type, desc, cwd));
                    }
                }
                var imported = ExternalAgentImport.Import(selected).Select(x => new { itemType = x.ItemType, description = x.Description, cwd = x.Cwd }).ToArray();
                Notify("externalAgentConfig/import/completed", new { imported = imported.Length });
                Result(id, new { imported });
                break;
            }

            case "externalAgentConfig/import/readHistories":
                Result(id, new { data = ExternalAgentImport.ReadHistories() });
                break;

            case "feedback/upload":
            {
                var classification = Str(paramsEl, "classification") ?? "";
                try
                {
                    var fbId = FeedbackStore.Save(
                        classification,
                        Str(paramsEl, "reason"),
                        Str(paramsEl, "threadId"),
                        Flag(paramsEl, "includeLogs"));
                    Result(id, new { threadId = fbId });
                }
                catch (Exception ex)
                {
                    Error(id, -32602, ex.Message);
                }
                break;
            }

            case "windowsSandbox/readiness":
            {
                var snap = WindowsSandbox.Read();
                Result(id, new { status = snap.Status, jobObject = snap.JobObject, mode = snap.Mode, implementation = snap.Status == "ready" ? "jobObject-kill-on-close" : "notConfigured" });
                break;
            }

            case "windowsSandbox/setupStart":
            {
                var mode = Str(paramsEl, "mode") ?? "";
                if (mode != "elevated" && mode != "unelevated")
                {
                    Error(id, -32602, "mode must be elevated or unelevated");
                    break;
                }

                var cwd = Str(paramsEl, "cwd");
                try
                {
                    var snap = WindowsSandbox.Setup(mode, cwd);
                    var ok = snap.Status == "ready";
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
                break;
            }

            case "mcpServer/elicitation/respond":
            {
                var threadId = Str(paramsEl, "threadId") ?? _threads.Keys.FirstOrDefault() ?? "";
                var requestId = Str(paramsEl, "requestId") ?? "";
                var answers = Str(paramsEl, "answersJson") ?? (paramsEl.ValueKind == JsonValueKind.Object ? paramsEl.GetRawText() : "{\"action\":\"decline\"}");
                if (_threads.TryGetValue(threadId, out var elicitSession))
                {
                    elicitSession.ResolveMcpElicitation(requestId, answers);
                }
                Result(id, new { });
                break;
            }

            case "item/tool/respondUserInput":
            {
                var threadId = Str(paramsEl, "threadId") ?? _threads.Keys.FirstOrDefault() ?? "";
                var requestId = Str(paramsEl, "requestId") ?? "";
                var answers = Str(paramsEl, "answersJson") ?? (paramsEl.ValueKind == JsonValueKind.Object ? paramsEl.GetRawText() : "{}");
                if (_threads.TryGetValue(threadId, out var inputSession))
                {
                    inputSession.ResolveUserInput(requestId, answers);
                }
                Result(id, new { });
                break;
            }

            case "item/permissions/respond":
            case "approval/respond":
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
                break;
            }

            case "thread/inject_items":
            case "thread/injectItems":
            {
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (!_threads.TryGetValue(threadId, out var injected))
                {
                    Error(id, -32001, $"Thread not loaded: {threadId}");
                    break;
                }

                if (paramsEl.ValueKind == JsonValueKind.Object
                    && paramsEl.TryGetProperty("items", out var itemsEl)
                    && itemsEl.ValueKind == JsonValueKind.Array)
                {
                    injected.InjectItems(itemsEl.EnumerateArray().Select(ParseInjectedItem));
                }
                Result(id, new { });
                break;
            }

            case "thread/realtime/start":
            case "thread/realtime/appendAudio":
            case "thread/realtime/appendText":
            case "thread/realtime/appendSpeech":
            {
                if (!RequireExperimental(id, hasId, method)) break;
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (!_threads.TryGetValue(threadId, out _))
                {
                    Error(id, -32001, $"Thread not loaded: {threadId}");
                    break;
                }
                Notify("thread/realtime/error", new { threadId, message = "realtime WebRTC is not implemented" });
                Notify("thread/realtime/closed", new { threadId, reason = "notImplemented" });
                Error(id, -32002, "realtime WebRTC is not implemented");
                break;
            }

            case "thread/realtime/stop":
            {
                if (!RequireExperimental(id, hasId, method)) break;
                var threadId = Str(paramsEl, "threadId") ?? "";
                if (!_threads.TryGetValue(threadId, out _))
                {
                    Error(id, -32001, $"Thread not loaded: {threadId}");
                    break;
                }
                Notify("thread/realtime/closed", new { threadId, reason = "stopped" });
                Result(id, new { });
                break;
            }

            case "thread/realtime/listVoices":
            {
                if (!RequireExperimental(id, hasId, "thread/realtime/listVoices")) break;
                Result(id, new { voices = Array.Empty<object>() });
                break;
            }

            case "command/exec/write":
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
                    break;
                }
                Result(id, new { });
                break;
            }

            case "command/exec/terminate":
            {
                var processId = Str(paramsEl, "processId") ?? "";
                if (!_exec.Terminate(processId))
                {
                    Error(id, -32001, "No active command exec session");
                    break;
                }
                Result(id, new { });
                break;
            }

            case "command/exec/resize":
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
                    break;
                }
                Result(id, new { });
                break;
            }

            default:
                if (hasId) Error(id, -32601, $"Unknown method {method}");
                break;
        }
    }


    private sealed record PendingServerRequest(string ThreadId, string ApprovalId, string Kind);

    private object[] QueueDto(string threadId) =>
        ThreadQueueStore.List(threadId).Select(x => new { id = x.Id, text = x.Text }).ToArray();

    private void StartTurn(CodexSession session, string text, CancellationToken ct)
    {
        var threadId = session.Thread.Id;
        var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _turnCts[threadId] = turnCts;
        _ = Task.Run(async () =>
        {
            try
            {
                await session.RunTurnAsync(text, turnCts.Token);
            }
            finally
            {
                _turnCts.Remove(threadId);
                DrainQueue(session, ct);
            }
        }, turnCts.Token);
    }

    private void DrainQueue(CodexSession session, CancellationToken ct)
    {
        var threadId = session.Thread.Id;
        if (_turnCts.ContainsKey(threadId))
        {
            return;
        }

        var next = ThreadQueueStore.Dequeue(threadId);

        if (next is null)
        {
            return;
        }

        Notify("thread/queue/changed", new { threadId, data = QueueDto(threadId) });
        StartTurn(session, next.Text, ct);
    }

    private static bool Flag(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v)
        && v.ValueKind is JsonValueKind.True;

    private static bool HasPermissionProfile(JsonElement el) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty("permissionProfile", out var v) && v.ValueKind == JsonValueKind.String;

    private static List<string> ReadCommandArgv(JsonElement paramsEl)
    {
        var argv = new List<string>();
        if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("command", out var cmdEl))
        {
            if (cmdEl.ValueKind == JsonValueKind.Array)
            {
                argv.AddRange(cmdEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0));
            }
            else if (cmdEl.ValueKind == JsonValueKind.String)
            {
                var text = cmdEl.GetString() ?? "";
                if (text.Length > 0)
                {
                    argv.Add(text);
                }
            }
        }

        if (argv.Count == 0)
        {
            var text = ReadInputText(paramsEl);
            if (!string.IsNullOrWhiteSpace(text))
            {
                argv.Add(text);
            }
        }

        return argv;
    }

    private static bool HasGranularApproval(JsonElement el) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty("approvalPolicy", out var ap)
        && ap.ValueKind == JsonValueKind.Object
        && ap.TryGetProperty("granular", out _);

    private bool RequireExperimental(JsonElement id, bool hasId, string method)
    {
        if (_experimentalApi)
        {
            return true;
        }

        if (hasId)
        {
            Error(id, -32601, $"{method} requires experimentalApi capability");
        }

        return false;
    }

    private void Wire(CodexSession session)
    {
        session.Event += evt =>
        {
            switch (evt)
            {
                case AgentEvent.TurnStarted turn:
                    Notify("turn/started", new { turn = new { id = turn.TurnId }, threadId = turn.ThreadId });
                    Notify("thread/status/changed", new { threadId = turn.ThreadId, status = new { type = "active", activeFlags = Array.Empty<string>() } });
                    foreach (var hook in HookCatalog.List(session.Config.Home, session.Config.Cwd).Where(h => h.Event.Equals("UserPromptSubmit", StringComparison.OrdinalIgnoreCase)))
                    {
                        Notify("hook/started", new { threadId = turn.ThreadId, turnId = turn.TurnId, hook = new { name = hook.Name, eventName = hook.Event } });
                    }
                    break;
                case AgentEvent.ItemStarted item:
                    Notify("item/started", new { item = ItemDto(item.Item) });
                    break;
                case AgentEvent.ItemDelta delta:
                    Notify("item/agentMessage/delta", new { itemId = delta.ItemId, delta = delta.Text });
                    break;
                case AgentEvent.CommandOutputDelta output:
                {
                    var payload = new
                    {
                        threadId = session.Thread.Id,
                        turnId = session.Turns().LastOrDefault().Id ?? "",
                        itemId = output.CallId,
                        delta = output.Text,
                    };
                    Notify("item/commandExecution/outputDelta", payload);
                    Notify("item/fileChange/outputDelta", payload);
                    break;
                }
                case AgentEvent.ItemCompleted item:
                    Notify("item/completed", new { item = ItemDto(item.Item) });
                    if (item.Item.Kind is "plan_update" || item.Item.ToolName is "update_plan")
                    {
                        Notify("item/plan/delta", new { itemId = item.Item.Id, delta = item.Item.Text });
                        Notify("turn/plan/updated", new { threadId = session.Thread.Id, plan = item.Item.Text });
                    }
                    if (item.Item.Kind is "file_change" || item.Item.ToolName is "apply_patch")
                    {
                        Notify("turn/diff/updated", new { threadId = session.Thread.Id, diff = item.Item.Text });
                    }
                    break;
                case AgentEvent.Log log:
                    Notify("item/reasoning/textDelta", new { delta = log.Message });
                    break;
                case AgentEvent.Compacted compacted:
                    Notify("thread/compacted", new { threadId = session.Thread.Id, compacted = true, dropped = compacted.Dropped });
                    break;
                case AgentEvent.Warning warning:
                    Notify("warning", new { message = warning.Message });
                    break;
                case AgentEvent.TokenUsage usage:
                    Notify("thread/tokenUsage/updated", new
                    {
                        threadId = session.Thread.Id,
                        turnId = session.Turns().LastOrDefault().Id ?? "",
                        tokenUsage = new
                        {
                            total = TokenBreakdown(usage.Total),
                            last = TokenBreakdown(usage.Last),
                            modelContextWindow = (long?)null,
                        },
                    });
                    break;
                case AgentEvent.ApprovalNeeded approval:
                {
                    var method = approval.Reason.Contains("apply_patch", StringComparison.Ordinal)
                                 || approval.Reason.Contains("write_file", StringComparison.Ordinal)
                        ? "item/fileChange/requestApproval"
                        : "item/commandExecution/requestApproval";
                    var turnId = session.Turns().LastOrDefault().Id ?? "";
                    var rpcId = Request(method, new
                    {
                        threadId = session.Thread.Id,
                        turnId,
                        itemId = approval.RequestId,
                        requestId = approval.RequestId,
                        command = approval.Command,
                        cwd = approval.Cwd,
                        reason = approval.Reason,
                        startedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        kind = "command",
                    });
                    _serverRequests[rpcId] = new PendingServerRequest(session.Thread.Id, approval.RequestId, "approval");
                    Notify("thread/status/changed", new { threadId = session.Thread.Id, status = new { type = "active", activeFlags = new[] { "waitingOnApproval" } } });
                    break;
                }
                case AgentEvent.McpElicitationNeeded elicit:
                {
                    var rpcId = Request("mcpServer/elicitation/request", new
                    {
                        threadId = session.Thread.Id,
                        message = elicit.Message,
                        requestedSchema = new { type = "object" },
                    });
                    _serverRequests[rpcId] = new PendingServerRequest(session.Thread.Id, elicit.RequestId, "mcpElicit");
                    Notify("thread/status/changed", new { threadId = session.Thread.Id, status = new { type = "active", activeFlags = new[] { "waitingOnUserInput" } } });
                    break;
                }
                case AgentEvent.UserInputNeeded input:
                {
                    var questions = ParseUserInputQuestions(input.QuestionsJson);
                    var rpcId = Request("item/tool/requestUserInput", new
                    {
                        threadId = session.Thread.Id,
                        turnId = session.Turns().LastOrDefault().Id ?? "",
                        itemId = input.RequestId,
                        questions,
                        isBlocking = true,
                    });
                    _serverRequests[rpcId] = new PendingServerRequest(session.Thread.Id, input.RequestId, "userInput");
                    Notify("thread/status/changed", new { threadId = session.Thread.Id, status = new { type = "active", activeFlags = new[] { "waitingOnUserInput" } } });
                    break;
                }
                case AgentEvent.TurnCompleted turn:
                    Notify("turn/completed", new { turn = new { id = turn.TurnId, status = turn.Status } });
                    Notify("thread/status/changed", new { threadId = session.Thread.Id, status = new { type = "idle" } });
                    try
                    {
                        var diff = GitProbe.WorkingTreeDiff(session.Config.Cwd);
                        Notify("turn/diff/updated", new { threadId = session.Thread.Id, turnId = turn.TurnId, diff, empty = string.IsNullOrWhiteSpace(diff) });
                    }
                    catch
                    {
                        Notify("turn/diff/updated", new { threadId = session.Thread.Id, turnId = turn.TurnId, diff = "", empty = true });
                    }
                    foreach (var hook in HookCatalog.List(session.Config.Home, session.Config.Cwd).Where(h => h.Event.Equals("Stop", StringComparison.OrdinalIgnoreCase)))
                    {
                        Notify("hook/completed", new { threadId = session.Thread.Id, turnId = turn.TurnId, hook = new { name = hook.Name, eventName = hook.Event }, status = "completed" });
                    }
                    break;
                case AgentEvent.Failed failed:
                    Notify("error", new { message = failed.Message });
                    break;
            }
        };
    }

    public bool TryGetSession(string threadId, [NotNullWhen(true)] out CodexSession? session) =>
        _threads.TryGetValue(threadId, out session);

    private void ApplyLiveConfig(Dictionary<string, string> edits)
    {
        if (edits.TryGetValue("collaboration_mode", out var collab))
        {
            foreach (var live in _threads.Values) live.SetCollaborationMode(collab, persist: false);
        }

        if (edits.TryGetValue("personality", out var personality))
        {
            foreach (var live in _threads.Values) live.SetPersonality(personality, persist: false);
        }

        edits.TryGetValue("model", out var model);
        edits.TryGetValue("sandbox_mode", out var sandbox);
        edits.TryGetValue("approval_policy", out var approval);
        edits.TryGetValue("model_reasoning_effort", out var effort);
        if (!string.IsNullOrWhiteSpace(model) || !string.IsNullOrWhiteSpace(sandbox)
            || !string.IsNullOrWhiteSpace(approval) || !string.IsNullOrWhiteSpace(effort))
        {
            foreach (var live in _threads.Values)
            {
                live.ApplySettings(model, sandbox, approval, effort: effort);
            }
        }
    }

    private static object ThreadDto(CodexSession session) => new
    {
        id = session.Thread.Id,
        title = session.Thread.Title,
        cwd = session.Thread.Cwd,
        model = session.Config.Model,
        sandbox = session.Config.SandboxMode,
        approvalPolicy = session.Config.ApprovalPolicy,
        projectId = ProjectStore.ProjectOf(session.Thread.Id),
        worktree = WorktreeBindings.Find(session.Thread.Id) is { } wt
            ? new { path = wt.Root, cwd = wt.Cwd, sourceRoot = wt.SourceRoot, branch = wt.Branch, cloudWorktree = "notConfigured" }
            : null,
        cloudWorktree = "notConfigured",
        subagents = session.ListSubagents().Select(a => new { id = a.Id, done = a.Done, result = a.Result }).ToArray(),
    };

    private static object ProjectDto(ProjectInfo project) => new
    {
        id = project.Id,
        name = project.Name,
        roots = project.Roots.Select(r => new { path = r.Path }).ToArray(),
        metadata = project.Metadata,
        position = project.Position,
        createdAt = project.CreatedAt,
        updatedAt = project.UpdatedAt,
        recencyAt = project.RecencyAt,
    };

    private static List<string> ReadRoots(JsonElement paramsEl)
    {
        var roots = new List<string>();
        if (paramsEl.ValueKind != JsonValueKind.Object || !paramsEl.TryGetProperty("roots", out var el) || el.ValueKind != JsonValueKind.Array)
        {
            return roots;
        }

        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var path = item.GetString();
                if (!string.IsNullOrWhiteSpace(path)) roots.Add(path);
            }
            else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
            {
                var path = p.GetString();
                if (!string.IsNullOrWhiteSpace(path)) roots.Add(path);
            }
        }

        return roots;
    }

    private static Dictionary<string, string>? ReadMetadata(JsonElement paramsEl)
    {
        if (paramsEl.ValueKind != JsonValueKind.Object || !paramsEl.TryGetProperty("metadata", out var el) || el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                map[prop.Name] = prop.Value.GetString() ?? "";
            }
        }

        return map;
    }

    private static object[] FuzzyFiles(IEnumerable<string> roots, string query)
    {
        var files = new List<object>();
        foreach (var root in roots)
        {
            var cfg = ConfigService.Load(root);
            var sandbox = new WorkspaceSandbox(cfg);
            var result = FileSearch.SearchNames(new ToolCallRequest("search", "file_search", JsonSerializer.Serialize(new { query, path = root })), sandbox);
            foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line == "(no matches)") continue;
                files.Add(new { root, path = line, fileName = Path.GetFileName(line), score = 1, matchType = "file", indices = (int[]?)null });
                if (files.Count >= 50) return files.ToArray();
            }
        }
        return files.ToArray();
    }

    private static object PluginDto(PluginInfo plugin) => new
    {
        id = plugin.Name,
        name = plugin.Name,
        path = plugin.Path,
        kind = plugin.Kind,
        description = plugin.Description,
    };

    private static object SectionDto(ThreadSectionInfo section) => new
    {
        id = section.Id,
        name = section.Name,
        appearance = new { color = section.Color, icon = section.Icon },
    };

    private static object ItemRow(string turnId, int index, HistoryMessage message, string? itemsView) => new
    {
        turnId,
        id = $"{turnId}_{index}",
        type = message.Role switch
        {
            "user" => "user_message",
            "assistant" => "agent_message",
            "tool" => "tool_call",
            _ => message.Role,
        },
        text = ItemsView.Text(message.Content, itemsView),
        tool = message.Name,
    };

    private static HistoryMessage ParseInjectedItem(JsonElement item)
    {
        var type = Str(item, "type") ?? "";
        var role = Str(item, "role");
        if (string.IsNullOrWhiteSpace(role))
        {
            role = type switch
            {
                "user_message" or "user" or "input_text" => "user",
                "agent_message" or "assistant" or "output_text" or "function_call" => "assistant",
                "tool_call" or "tool" or "function_call_output" => "tool",
                "message" => "user",
                _ => "user",
            };
        }

        var text = Str(item, "text") ?? "";
        if (string.IsNullOrEmpty(text) && item.ValueKind == JsonValueKind.Object && item.TryGetProperty("content", out var content))
        {
            text = content.ValueKind switch
            {
                JsonValueKind.String => content.GetString() ?? "",
                JsonValueKind.Array => string.Join("", content.EnumerateArray().Select(part => Str(part, "text") ?? "")),
                _ => content.GetRawText(),
            };
        }

        return new HistoryMessage(
            role ?? "user",
            text,
            Str(item, "callId") ?? Str(item, "call_id") ?? Str(item, "toolCallId") ?? "",
            Str(item, "name") ?? "",
            "");
    }

    private static object TokenBreakdown(long tokens) => new
    {
        totalTokens = tokens,
        inputTokens = tokens,
        cachedInputTokens = 0L,
        cacheWriteInputTokens = 0L,
        outputTokens = 0L,
        reasoningOutputTokens = 0L,
    };

    private static object ItemDto(ConversationItem item) => new
    {
        id = item.Id,
        type = item.Kind,
        text = item.Text,
        status = item.Status,
        command = item.Command,
        path = item.Path,
        tool = item.ToolName,
        toolCallId = item.ToolCallId,
    };

    private static string ReadInputText(JsonElement paramsEl)
    {
        if (paramsEl.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        if (paramsEl.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in input.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    parts.Add(item.GetString() ?? "");
                }
                else if (item.TryGetProperty("text", out var text))
                {
                    parts.Add(text.GetString() ?? "");
                }
                else if (item.TryGetProperty("type", out var ty))
                {
                    var kind = ty.GetString() ?? "";
                    var path = item.TryGetProperty("path", out var p) ? p.GetString()
                        : item.TryGetProperty("localPath", out var lp) ? lp.GetString()
                        : null;
                    if (path is not null && kind is "local_image" or "image" or "localImage")
                    {
                        parts.Add("Please inspect this image with view_image: " + path);
                    }
                }
            }

            return string.Join("\n", parts);
        }

        return Str(paramsEl, "text") ?? "";
    }


    private static int Int(JsonElement el, string name, int fallback = 0)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v))
        {
            return fallback;
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
        {
            return n;
        }

        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out n) ? n : fallback;
    }

    private static bool? Bool(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static bool TryHostPath(string path, out string full, out string error)
    {
        full = "";
        error = "";
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            error = "path must be absolute";
            return false;
        }

        full = Path.GetFullPath(path);
        return true;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

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

    private string Request(string method, object paramsObj)
    {
        var reqId = Ids.newId("rpc");
        Write(new JsonObject
        {
            ["method"] = method,
            ["id"] = reqId,
            ["params"] = JsonSerializer.SerializeToNode(paramsObj, _json),
        });
        return reqId;
    }

    private void HandleServerResponse(JsonElement id, JsonElement root)
    {
        var key = IdText(id);
        if (string.IsNullOrEmpty(key) || !_serverRequests.Remove(key, out var pending))
        {
            return;
        }

        var allow = false;
        string? reason = null;
        if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty("allow", out var allowEl))
            {
                allow = allowEl.ValueKind == JsonValueKind.True;
            }
            var decision = Str(result, "decision") ?? "";
            if (decision is "accept" or "acceptForSession") allow = true;
            if (decision is "decline" or "cancel") allow = false;
            reason = Str(result, "reason");
        }

        if (_threads.TryGetValue(pending.ThreadId, out var session))
        {
            if (pending.Kind == "userInput")
            {
                var payload = root.TryGetProperty("result", out var userResult) ? userResult.GetRawText() : "{}";
                session.ResolveUserInput(pending.ApprovalId, payload);
            }
            else if (pending.Kind == "mcpElicit")
            {
                var payload = root.TryGetProperty("result", out var elicitResult) ? elicitResult.GetRawText() : "{\"action\":\"decline\"}";
                session.ResolveMcpElicitation(pending.ApprovalId, payload);
            }
            else
            {
                session.ResolveApproval(pending.ApprovalId, allow, reason);
            }
        }

        Notify("serverRequest/resolved", new { requestId = key });
    }

    private static object[] ParseUserInputQuestions(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = doc.RootElement;
            if (root.TryGetProperty("questions", out var q) && q.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                return q.EnumerateArray().Select(item =>
                {
                    i++;
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var text = item.GetString() ?? "";
                        return (object)new { id = "q" + i, header = "Question", question = text };
                    }
                    return (object)new
                    {
                        id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : "q" + i,
                        header = item.TryGetProperty("header", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : "Question",
                        question = item.TryGetProperty("question", out var qq) && qq.ValueKind == JsonValueKind.String ? qq.GetString() : item.GetRawText(),
                    };
                }).ToArray();
            }
        }
        catch { /* fall through */ }

        return [new { id = "q1", header = "Question", question = json }];
    }

    private static string IdText(JsonElement id) =>
        id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" :
        id.ValueKind == JsonValueKind.Number ? id.GetRawText() :
        "";

    private static JsonNode CloneId(JsonElement id) =>
        id.ValueKind == JsonValueKind.Number ? id.GetInt32() :
        id.ValueKind == JsonValueKind.String ? id.GetString()! :
        0;

    private void Write(JsonNode node)
    {
        lock (_output)
        {
            _output.WriteLine(node.ToJsonString(_json));
            _output.Flush();
        }
    }
}
