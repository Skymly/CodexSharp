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
    private async Task DispatchThreadStartAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (Flag(paramsEl, "allowProviderModelFallback") && !RequireExperimental(id, hasId, "thread/start.allowProviderModelFallback"))
            {
                return;
            }

            if (HasGranularApproval(paramsEl) && !RequireExperimental(id, hasId, "askForApproval.granular"))
            {
                return;
            }

            var cwd = Str(paramsEl, "cwd") ?? Environment.CurrentDirectory;
            string? startProjectId = null;
            if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("projectId", out var startProject))
            {
                if (!RequireExperimental(id, hasId, "thread/start.projectId")) return;
                startProjectId = startProject.ValueKind == JsonValueKind.String ? startProject.GetString() : null;
                if (!string.IsNullOrWhiteSpace(startProjectId))
                {
                    var projectRoot = ProjectStore.PrimaryRoot(startProjectId);
                    if (string.IsNullOrWhiteSpace(projectRoot))
                    {
                        Error(id, -32001, "Project not found: " + startProjectId);
                        return;
                    }

                    cwd = projectRoot;
                }
            }

            var model = Str(paramsEl, "model");
            var sandbox = Str(paramsEl, "sandbox") ?? Str(paramsEl, "sandboxMode");
            var approval = Str(paramsEl, "approvalPolicy");
            var profile = Str(paramsEl, "profile");
            var config = ConfigService.Load(cwd, model, sandbox, approval, profile, Flag(paramsEl, "ignoreUserConfig"), Flag(paramsEl, "strictConfig"));
            var extras = ProjectStore.ExtraRoots(startProjectId);
            var session = CodexSession.Start(config, Str(paramsEl, "title"), Flag(paramsEl, "ephemeral"), extras);
            var source = Str(paramsEl, "source") ?? Str(paramsEl, "threadSource");
            if (!string.IsNullOrWhiteSpace(source))
            {
                ThreadSources.Set(session.Thread.Id, source);
            }
            if (!string.IsNullOrWhiteSpace(startProjectId) && !ProjectStore.Assign(session.Thread.Id, startProjectId))
            {
                Error(id, -32001, "Project not found: " + startProjectId);
                return;
            }
            Wire(session);
            _threads[session.Thread.Id] = session;
            Notify("thread/started", new { thread = ThreadDto(session) });
            Result(id, new { thread = ThreadDto(session) });
            return;
        }
    }

    private async Task DispatchThreadResumeAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            CodexSession session;
            try
            {
                session = CodexSession.Resume(threadId);
            }
            catch (InvalidOperationException ex)
            {
                Error(id, -32001, ex.Message);
                return;
            }
            Wire(session);
            _threads[session.Thread.Id] = session;
            Result(id, new { thread = ThreadDto(session) });
            return;
        }
    }

    private async Task DispatchThreadListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (paramsEl.ValueKind == JsonValueKind.Object
                && paramsEl.TryGetProperty("originators", out var originators)
                && originators.ValueKind == JsonValueKind.Array
                && originators.GetArrayLength() > 0)
            {
                Error(id, -32602, "originators filter is hosted-only");
                return;
            }

            var archived = Bool(paramsEl, "archived") ?? false;
            var term = Str(paramsEl, "searchTerm") ?? "";
            var cwdFilter = Str(paramsEl, "cwd");
            var hasProject = false;
            string? projectIdFilter = null;
            if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("projectId", out var projectEl))
            {
                if (!RequireExperimental(id, hasId, "thread/list.projectId")) return;
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
            return;
        }
    }

    private async Task DispatchThreadUnsubscribeAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            _threads.Remove(threadId);
            Notify("thread/closed", new { threadId });
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchThreadArchiveAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            WorktreeBindings.UnbindAndRemove(threadId);
            new JsonlThreadStore().Archive(threadId);
            _threads.Remove(threadId);
            Notify("thread/archived", new { threadId });
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchThreadForkAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchThreadReadAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            if (_threads.TryGetValue(threadId, out var loaded))
            {
                Result(id, new { thread = ThreadDto(loaded) });
                return;
            }
            var stored = new JsonlThreadStore().Find(threadId);
            if (stored is null)
            {
                Error(id, -32001, $"Unknown thread {threadId}");
                return;
            }
            Result(id, new { thread = new { id = stored.Id, title = stored.Title, cwd = stored.Cwd, model = stored.Model } });
            return;
        }
    }

    private async Task DispatchThreadLoadedListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        Result(id, new { threadIds = _threads.Keys.ToArray() });
        return;
    }

    private async Task DispatchThreadGoalSetAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            if (!_threads.TryGetValue(threadId, out var session))
            {
                Error(id, -32001, $"Thread not loaded: {threadId}");
                return;
            }
            session.SetGoal(Str(paramsEl, "objective"), Str(paramsEl, "status"));
            Notify(AppServerNotifications.ThreadGoalUpdated, new { threadId, goal = session.GoalDto() });
            Result(id, new { goal = session.GoalDto() });
            return;
        }
    }

    private async Task DispatchThreadGoalGetAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            if (!_threads.TryGetValue(threadId, out var session))
            {
                Error(id, -32001, $"Thread not loaded: {threadId}");
                return;
            }
            Result(id, new { goal = session.GoalDto() });
            return;
        }
    }

    private async Task DispatchThreadGoalClearAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            if (_threads.TryGetValue(threadId, out var session))
            {
                session.ClearGoal();
            }
            Notify(AppServerNotifications.ThreadGoalCleared, new { threadId });
            Result(id, new { cleared = true });
            return;
        }
    }

    private async Task DispatchThreadIncrementElicitationAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "thread/increment_elicitation")) return;
            var threadId = Str(paramsEl, "threadId") ?? "";
            if (string.IsNullOrWhiteSpace(threadId)) { Error(id, -32602, "threadId is required"); return; }
            _elicitations.TryGetValue(threadId, out var n);
            n++;
            _elicitations[threadId] = n;
            Result(id, new { count = n });
            return;
        }
    }

    private async Task DispatchThreadDecrementElicitationAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "thread/decrement_elicitation")) return;
            var threadId = Str(paramsEl, "threadId") ?? "";
            _elicitations.TryGetValue(threadId, out var n);
            n = Math.Max(0, n - 1);
            if (n == 0) _elicitations.Remove(threadId); else _elicitations[threadId] = n;
            Result(id, new { count = n });
            return;
        }
    }

    private async Task DispatchThreadRecapStartAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            if (!_threads.TryGetValue(threadId, out var recapSession))
            {
                Error(id, -32001, $"Thread not loaded: {threadId}");
                return;
            }
            var recap = await recapSession.RunRecapAsync(ct);
            Result(id, new { status = recap.Status, recap = recap.Text, persisted = false });
            return;
        }
    }

    private async Task DispatchThreadCopyAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            var want = Str(paramsEl, "kind") ?? Str(paramsEl, "want") ?? "";
            if (!_threads.TryGetValue(threadId, out var copySession))
            {
                Error(id, -32001, $"Thread not loaded: {threadId}");
                return;
            }
            var copied = copySession.CopyLast(want);
            Result(id, new { kind = copied.Kind, text = copied.Text });
            return;
        }
    }

    private async Task DispatchThreadExportAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
                    return;
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
            return;
        }
    }

    private async Task DispatchThreadRolloutPathAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            var path = Path.Combine(CodexPaths.SessionsDir, threadId + ".jsonl");
            Result(id, new { path, exists = File.Exists(path) });
            return;
        }
    }

    private async Task DispatchThreadHandoffAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            var destination = Str(paramsEl, "destination");
            var hostId = Str(paramsEl, "hostId");
            if (ThreadHandoff.IsCloudOrRemote(destination, hostId))
            {
                Result(id, new { cloudHandoff = "notConfigured", crossHost = "notConfigured" });
                return;
            }

            if (!_threads.TryGetValue(threadId, out var handoffSession))
            {
                Error(id, -32001, $"Thread not loaded: {threadId}");
                return;
            }

            if (_turnCts.TryGetValue(threadId, out var handoffCts))
            {
                handoffCts.Cancel();
            }

            try
            {
                var moved = ThreadHandoff.Toggle(threadId, handoffSession.Config.Cwd);
                handoffSession.SetCwd(moved.Cwd);
                Result(id, new
                {
                    mode = moved.Mode,
                    cwd = moved.Cwd,
                    branch = moved.Branch,
                    cloudHandoff = moved.CloudHandoff,
                    crossHost = moved.CrossHost,
                });
            }
            catch (Exception ex)
            {
                Error(id, -32602, ex.Message);
            }
            return;
        }
    }

    private async Task DispatchThreadWorktreeStartAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var cwd = Str(paramsEl, "cwd") ?? Environment.CurrentDirectory;
            try
            {
                var tree = WorktreeSession.Create(cwd);
                var setup = LocalEnvSetup.Run(tree.SourceRoot, tree.Cwd);
                var config = ConfigService.Load(tree.Cwd);
                var session = CodexSession.Start(config, "worktree");
                ThreadSources.Set(session.Thread.Id, "worktree");
                WorktreeBindings.Bind(session.Thread.Id, tree);
                Wire(session);
                _threads[session.Thread.Id] = session;
                if (setup.Ran && !setup.Ok)
                {
                    var log = "setup script failed: " + setup.ScriptPath + Environment.NewLine + setup.Log;
                    new JsonlThreadStore().AppendEvent(session.Thread.Id, AgentEvent.NewWarning(log));
                }
                Notify("thread/started", new { thread = ThreadDto(session) });
                Result(id, new
                {
                    thread = ThreadDto(session),
                    path = tree.Root,
                    cwd = tree.Cwd,
                    sourceRoot = tree.SourceRoot,
                    branch = tree.Branch,
                    cloudWorktree = "notConfigured",
                    setupOk = setup.Ok,
                    setupRan = setup.Ran,
                    setupPath = setup.ScriptPath,
                    setupLog = setup.Log,
                });
            }
            catch (Exception ex)
            {
                Error(id, -32602, ex.Message);
            }
            return;
        }
    }

    private async Task DispatchThreadCompactStartAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            if (!_threads.TryGetValue(threadId, out var session))
            {
                Error(id, -32001, $"Thread not loaded: {threadId}");
                return;
            }
            var compacted = session.TryCompact();
            Notify("thread/compacted", new { threadId, compacted });
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchThreadBackgroundTerminalsListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "thread/backgroundTerminals/list")) return;
            var threadId = Str(paramsEl, "threadId") ?? "";
            var data = _backgroundTerminals.TryGetValue(threadId, out var list) ? list.ToArray() : [];
            Result(id, new { data, nextCursor = (string?)null });
            return;
        }
    }

    private async Task DispatchThreadBackgroundTerminalsCleanAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "thread/backgroundTerminals/clean")) return;
            _backgroundTerminals.Remove(Str(paramsEl, "threadId") ?? "");
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchThreadBackgroundTerminalsTerminateAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "thread/backgroundTerminals/terminate")) return;
            var threadId = Str(paramsEl, "threadId") ?? "";
            var processId = Str(paramsEl, "processId") ?? "";
            var terminated = _exec.Terminate(processId);
            Result(id, new { terminated });
            return;
        }
    }

    private async Task DispatchThreadShellCommandAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            var command = Str(paramsEl, "command") ?? "";
            if (!_threads.TryGetValue(threadId, out var session))
            {
                Error(id, -32001, $"Thread not loaded: {threadId}");
                return;
            }
            var call = new ToolCallRequest(Ids.call(), "shell", JsonSerializer.Serialize(new { command }));
            if (ToolApproval.needsApproval(session.Config, call))
            {
                var capturedId = id.Clone();
                var capturedHasId = hasId;
                _ = Task.Run(async () =>
                {
                    var denied = await DenyReasonAsync(session, call, ct);
                    if (denied is not null)
                    {
                        if (capturedHasId) Result(capturedId, new { output = "Approval denied: " + denied, isError = false, status = "denied" });
                        return;
                    }

                    var exec = new ShellExecutor(new WorkspaceSandbox(session.Config, session.ExtraReadRoots), session.Config);
                    var result = await exec.RunAsync(call, ct);
                    if (capturedHasId) Result(capturedId, new { output = result.Output, isError = result.IsError });
                }, ct);
                return;
            }

            var execNow = new ShellExecutor(new WorkspaceSandbox(session.Config, session.ExtraReadRoots), session.Config);
            var resultNow = await execNow.RunAsync(call, ct);
            Result(id, new { output = resultNow.Output, isError = resultNow.IsError });
            return;
        }
    }

    private async Task DispatchThreadTurnsListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            if (!_threads.TryGetValue(threadId, out var listedTurns))
            {
                Error(id, -32001, $"Thread not loaded: {threadId}");
                return;
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
            return;
        }
    }

    private async Task DispatchThreadSearchAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
            if (!RequireExperimental(id, hasId, "thread/search")) return;
        {
            var term = Str(paramsEl, "searchTerm") ?? Str(paramsEl, "query") ?? "";
            var archived = Bool(paramsEl, "archived") ?? false;
            var hits = new JsonlThreadStore().Search(term, archived).Select(h => new
            {
                thread = new { id = h.Thread.Id, title = h.Thread.Title, cwd = h.Thread.Cwd, model = h.Thread.Model },
                snippet = h.Snippet,
            }).ToArray();
            Result(id, new { data = hits, nextCursor = (string?)null });
            return;
        }
    }

    private async Task DispatchThreadQueueAddAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
            if (!RequireExperimental(id, hasId, "thread/queue/add")) return;
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            if (!_threads.ContainsKey(threadId) && new JsonlThreadStore().Find(threadId) is null)
            {
                Error(id, -32001, $"Thread not loaded: {threadId}");
                return;
            }
            var text = ReadInputText(paramsEl);
            var item = ThreadQueueStore.Add(threadId, text);
            Notify("thread/queue/changed", new { threadId, data = QueueDto(threadId) });
            Result(id, new { queuedSubmission = new { id = item.Id, input = new[] { new { type = "text", text } } } });
            return;
        }
    }

    private async Task DispatchThreadQueueListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
            if (!RequireExperimental(id, hasId, "thread/queue/list")) return;
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            Result(id, new { data = QueueDto(threadId), nextCursor = (string?)null });
            return;
        }
    }

    private async Task DispatchThreadQueueDeleteAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
            if (!RequireExperimental(id, hasId, "thread/queue/delete")) return;
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            var itemId = Str(paramsEl, "id") ?? Str(paramsEl, "queuedSubmissionId") ?? "";
            ThreadQueueStore.Delete(threadId, itemId);
            Notify("thread/queue/changed", new { threadId, data = QueueDto(threadId) });
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchThreadQueueUpdateAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
            if (!RequireExperimental(id, hasId, "thread/queue/update")) return;
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            var itemId = Str(paramsEl, "queuedSubmissionId") ?? Str(paramsEl, "id") ?? "";
            var text = ReadInputText(paramsEl);
            var updated = ThreadQueueStore.Update(threadId, itemId, text);
            if (updated is null)
            {
                Error(id, -32001, "queued submission not found: " + itemId);
                return;
            }
            Notify("thread/queue/changed", new { threadId, data = QueueDto(threadId) });
            Result(id, new { queuedSubmission = new { id = updated.Id, input = new[] { new { type = "text", text = updated.Text } } } });
            return;
        }
    }

    private async Task DispatchThreadQueueReorderAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
            if (!RequireExperimental(id, hasId, "thread/queue/reorder")) return;
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
            return;
        }
    }

    private async Task DispatchThreadQueueStartAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
            if (!RequireExperimental(id, hasId, "thread/queue/start")) return;
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            if (!_threads.TryGetValue(threadId, out var startSession))
            {
                Error(id, -32001, $"Thread not loaded: {threadId}");
                return;
            }
            var startId = Str(paramsEl, "queuedSubmissionId") ?? Str(paramsEl, "id");
            if (!string.IsNullOrWhiteSpace(startId))
            {
                ThreadQueueStore.Promote(threadId, startId);
            }
            DrainQueue(startSession, ct);
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchThreadSearchOccurrencesAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "thread/searchOccurrences")) return;
            var threadId = Str(paramsEl, "threadId") ?? "";
            if (!_threads.TryGetValue(threadId, out var occSession))
            {
                Error(id, -32001, $"Thread not loaded: {threadId}");
                return;
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
            return;
        }
    }

    private async Task DispatchThreadTimelineListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "thread/timeline/list")) return;
            var threadId = Str(paramsEl, "threadId") ?? "";
            if (!_threads.TryGetValue(threadId, out var timelineSession))
            {
                Error(id, -32001, $"Thread not loaded: {threadId}");
                return;
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
            return;
        }
    }

    private async Task DispatchThreadRollbackAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            var n = Int(paramsEl, "numTurns", 1);
            if (!_threads.TryGetValue(threadId, out var rb))
            {
                Error(id, -32001, $"Thread not loaded: {threadId}");
                return;
            }
            if (n < 1)
            {
                Error(id, -32602, "numTurns must be >= 1");
                return;
            }
            rb.RollbackTurns(n);
            Result(id, new { thread = ThreadDto(rb) });
            return;
        }
    }

    private async Task DispatchThreadRevertAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            var before = Str(paramsEl, "beforeTurnId") ?? "";
            if (!_threads.TryGetValue(threadId, out var reverted))
            {
                Error(id, -32001, $"Thread not loaded: {threadId}");
                return;
            }
            if (string.IsNullOrWhiteSpace(before) || !reverted.RevertBefore(before))
            {
                Error(id, -32602, "beforeTurnId not found");
                return;
            }
            Result(id, new { thread = ThreadDto(reverted), turnsBackwardsCursor = (string?)null });
            return;
        }
    }

    private async Task DispatchThreadItemsListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            if (!_threads.TryGetValue(threadId, out var listed))
            {
                Error(id, -32001, $"Thread not loaded: {threadId}");
                return;
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
            return;
        }
    }

    private async Task DispatchThreadSectionMoveAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            var sectionId = Str(paramsEl, "sectionId");
            if (!ThreadSections.Move(threadId, sectionId, Str(paramsEl, "beforeThreadId")))
            {
                Error(id, -32602, "Unable to move thread into section");
                return;
            }
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchThreadMetadataUpdateAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("isPinned", out var pin) && pin.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                ThreadPins.Set(threadId, pin.GetBoolean());
            }
            if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("projectId", out var metaProject))
            {
                if (!RequireExperimental(id, hasId, "thread/metadata/update.projectId")) return;
                var pid = metaProject.ValueKind == JsonValueKind.String ? metaProject.GetString() : null;
                if (!ProjectStore.Assign(threadId, pid))
                {
                    Error(id, -32001, "Project not found: " + pid);
                    return;
                }
                Notify("thread/project/updated", new { threadId, projectId = pid });
            }
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchThreadMemoryModeSetAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "thread/memoryMode/set")) return;
            try
            {
                MemoryStore.SetMode(Str(paramsEl, "threadId") ?? "", Str(paramsEl, "mode") ?? "");
                Result(id, new { });
            }
            catch (ArgumentException ex)
            {
                Error(id, -32602, ex.Message);
            }
            return;
        }
    }

    private async Task DispatchThreadSettingsUpdateAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            if (!_threads.TryGetValue(threadId, out var settingsSession))
            {
                Error(id, -32001, $"Thread not loaded: {threadId}");
                return;
            }
            settingsSession.ApplySettings(
                Str(paramsEl, "model"),
                Str(paramsEl, "sandboxMode") ?? Str(paramsEl, "sandboxPolicy"),
                Str(paramsEl, "approvalPolicy"),
                Str(paramsEl, "cwd"),
                Str(paramsEl, "effort"));
            Notify("thread/settings/updated", new { threadId, thread = ThreadDto(settingsSession) });
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchThreadNameSetAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            var name = Str(paramsEl, "name") ?? Str(paramsEl, "title") ?? "";
            if (_threads.TryGetValue(threadId, out var named))
            {
                named.SetName(name);
            }
            Notify("thread/name/updated", new { threadId, name });
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchThreadUnarchiveAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            if (!new JsonlThreadStore().Unarchive(threadId))
            {
                Error(id, -32001, $"Thread not archived: {threadId}");
                return;
            }
            Notify("thread/unarchived", new { threadId });
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchThreadDeleteAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            WorktreeBindings.UnbindAndRemove(threadId);
            _threads.Remove(threadId);
            if (!new JsonlThreadStore().Delete(threadId))
            {
                Error(id, -32001, $"Unknown thread {threadId}");
                return;
            }
            Notify("thread/deleted", new { threadId });
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchThreadApproveGuardianDeniedActionAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        Result(id, new { approved = false, reason = "Guardian is not implemented in CodexSharp." });
        return;
    }

    private async Task DispatchThreadInjectItemsAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            if (!_threads.TryGetValue(threadId, out var injected))
            {
                Error(id, -32001, $"Thread not loaded: {threadId}");
                return;
            }

            if (paramsEl.ValueKind == JsonValueKind.Object
                && paramsEl.TryGetProperty("items", out var itemsEl)
                && itemsEl.ValueKind == JsonValueKind.Array)
            {
                injected.InjectItems(itemsEl.EnumerateArray().Select(ParseInjectedItem));
            }
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchThreadRealtimeStartAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, method)) return;
            var threadId = Str(paramsEl, "threadId") ?? "";
            if (!_threads.TryGetValue(threadId, out _))
            {
                Error(id, -32001, $"Thread not loaded: {threadId}");
                return;
            }
            var status = HonestStubs.StatusOf("realtime");
            Notify("thread/realtime/error", new { threadId, message = "realtime Voice is " + status });
            Notify("thread/realtime/closed", new { threadId, reason = status });
            Error(id, -32002, "realtime Voice is " + status);
            return;
        }
    }

    private async Task DispatchThreadRealtimeStopAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, method)) return;
            var threadId = Str(paramsEl, "threadId") ?? "";
            if (!_threads.TryGetValue(threadId, out _))
            {
                Error(id, -32001, $"Thread not loaded: {threadId}");
                return;
            }
            Notify("thread/realtime/closed", new { threadId, reason = "stopped" });
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchThreadRealtimeListVoicesAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "thread/realtime/listVoices")) return;
            Result(id, new { voices = Array.Empty<object>() });
            return;
        }
    }

}
