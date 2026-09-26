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
    private async Task DispatchLastPatchApplyAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            var result = LastPatchApply.Run(threadId);
            Result(id, new { ok = result.Ok, output = result.Output, cwd = result.Cwd, cloudApply = "notConfigured" });
            return;
        }
    }

    private async Task DispatchGitWorktreeAddAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchAgentsMarkdownWriteAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var cwd = Str(paramsEl, "cwd") ?? Environment.CurrentDirectory;
            var created = AgentsMarkdown.WriteIfMissing(cwd);
            Result(id, new { created, path = Path.Combine(cwd, "AGENTS.md") });
            return;
        }
    }

    private async Task DispatchCollaborationModeListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchExperimentalFeatureListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchExperimentalFeatureEnablementSetAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchPermissionProfileListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var data = new object[]
            {
                new { id = "read-only", description = "Inspect only", allowed = true },
                new { id = "workspace-write", description = "Write inside workspace", allowed = true },
                new { id = "danger-full-access", description = "No sandbox", allowed = true },
            };
            Result(id, new { data, nextCursor = (string?)null });
            return;
        }
    }

    private async Task DispatchGitDiffToRemoteAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var cwd = Str(paramsEl, "cwd") ?? Environment.CurrentDirectory;
            Result(id, new { sha = GitProbe.Collect(cwd)?.Commit, diff = GitProbe.DiffToRemote(cwd), workingTreeDiff = GitProbe.WorkingTreeDiff(cwd), worktrees = GitProbe.ListWorktrees(cwd).Select(w => new { path = w.Path, head = w.Head, branch = w.Branch }).ToArray() });
            return;
        }
    }

    private async Task DispatchGetConversationSummaryAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchModelListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var data = ModelCatalog.List().Select(m => new { id = m.Id, provider = m.Provider }).ToArray();
            Result(id, new { data, nextCursor = (string?)null });
            return;
        }
    }

    private async Task DispatchReviewStartAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            if (!_threads.TryGetValue(threadId, out var reviewSession))
            {
                Error(id, -32001, $"Thread not loaded: {threadId}");
                return;
            }
            var prompt = ReviewPrompts.FromParams(paramsEl, reviewSession.Config.Cwd);
            Result(id, new { reviewThreadId = threadId, turn = new { id = "pending", status = "in_progress" } });
            _ = Task.Run(() => reviewSession.RunTurnAsync(prompt, ct), ct);
            return;
        }
    }

    private async Task DispatchConfigValueWriteAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var key = Str(paramsEl, "keyPath") ?? Str(paramsEl, "key") ?? "";
            if (string.IsNullOrWhiteSpace(key))
            {
                Error(id, -32602, "keyPath is required");
                return;
            }
            var value = "";
            if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("value", out var v))
            {
                value = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
            }
            ConfigService.WriteUserKeys(new Dictionary<string, string> { [key] = value });
            ApplyLiveConfig(new Dictionary<string, string> { [key] = value });
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchConfigBatchWriteAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
                return;
            }
            ConfigService.WriteUserKeys(edits);
            ApplyLiveConfig(edits);
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchAppListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        Result(id, new { data = Array.Empty<object>() });
        return;
    }

    private async Task DispatchProjectListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "project/list")) return;
            var sortKey = Str(paramsEl, "sortKey") ?? "position";
            var desc = string.Equals(Str(paramsEl, "sortDirection"), "desc", StringComparison.OrdinalIgnoreCase)
                || (string.Equals(sortKey, "recencyAt", StringComparison.OrdinalIgnoreCase) && !string.Equals(Str(paramsEl, "sortDirection"), "asc", StringComparison.OrdinalIgnoreCase));
            var data = ProjectStore.List(sortKey, desc).Select(ProjectDto).ToList();
            var (page, next, backwards) = TimelinePaging.Slice(data, Str(paramsEl, "cursor"), Int(paramsEl, "limit", 50));
            Result(id, new { data = page, nextCursor = next, backwardsCursor = backwards });
            return;
        }
    }

    private async Task DispatchProjectReadAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "project/read")) return;
            var projectId = Str(paramsEl, "projectId") ?? "";
            var project = ProjectStore.Read(projectId);
            if (project is null)
            {
                Error(id, -32001, "Project not found: " + projectId);
                return;
            }
            Result(id, new { project = ProjectDto(project) });
            return;
        }
    }

    private async Task DispatchProjectCreateAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "project/create")) return;
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
            return;
        }
    }

    private async Task DispatchProjectImportAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "project/import")) return;
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
            return;
        }
    }

    private async Task DispatchProjectUpdateAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "project/update")) return;
            var projectId = Str(paramsEl, "projectId") ?? "";
            IEnumerable<string>? roots = paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("roots", out _) ? ReadRoots(paramsEl) : null;
            var metadata = paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("metadata", out _) ? ReadMetadata(paramsEl) : null;
            var updated = ProjectStore.Update(projectId, Str(paramsEl, "name"), roots, metadata);
            if (updated is null)
            {
                Error(id, -32001, "Project not found: " + projectId);
                return;
            }
            Notify("project/changed", new { type = "updated", project = ProjectDto(updated) });
            Result(id, new { project = ProjectDto(updated) });
            return;
        }
    }

    private async Task DispatchProjectMoveAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "project/move")) return;
            var projectId = Str(paramsEl, "projectId") ?? "";
            if (!ProjectStore.Move(projectId, Str(paramsEl, "beforeProjectId")))
            {
                Error(id, -32001, "Project not found: " + projectId);
                return;
            }
            Notify("project/changed", new { type = "updated", projectId });
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchProjectDeleteAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "project/delete")) return;
            var projectId = Str(paramsEl, "projectId") ?? "";
            if (!ProjectStore.Delete(projectId))
            {
                Error(id, -32001, "Project not found: " + projectId);
                return;
            }
            Notify("project/changed", new { type = "deleted", projectId });
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchProjectTrustReadAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var cwd = Str(paramsEl, "cwd") ?? Environment.CurrentDirectory;
            Result(id, new
            {
                cwd,
                trustTarget = ProjectTrust.TrustTarget(cwd),
                trusted = ProjectTrust.IsTrusted(cwd),
            });
            return;
        }
    }

    private async Task DispatchProjectTrustSetAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchConfigWriteAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
                return;
            }
            ConfigService.WriteUserKeys(edits);
            ApplyLiveConfig(edits);
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchFsReadFileAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var path = Str(paramsEl, "path") ?? "";
            if (!TryHostPath(path, out var full, out var err))
            {
                Error(id, -32602, err);
                return;
            }
            if (!File.Exists(full))
            {
                Error(id, -32004, $"file not found: {full}");
                return;
            }
            Result(id, new { dataBase64 = Convert.ToBase64String(File.ReadAllBytes(full)) });
            return;
        }
    }

    private async Task DispatchFsWriteFileAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var path = Str(paramsEl, "path") ?? "";
            if (!TryHostPath(path, out var full, out var err))
            {
                Error(id, -32602, err);
                return;
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
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchFsCreateDirectoryAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var path = Str(paramsEl, "path") ?? "";
            if (!TryHostPath(path, out var full, out var err))
            {
                Error(id, -32602, err);
                return;
            }
            var recursive = Bool(paramsEl, "recursive") ?? true;
            if (!recursive)
            {
                var parent = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                {
                    Error(id, -32004, $"parent directory missing: {parent}");
                    return;
                }
            }
            Directory.CreateDirectory(full);
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchFsGetMetadataAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var path = Str(paramsEl, "path") ?? "";
            if (!TryHostPath(path, out var full, out var err))
            {
                Error(id, -32602, err);
                return;
            }
            if (!File.Exists(full) && !Directory.Exists(full))
            {
                Error(id, -32004, $"path not found: {full}");
                return;
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
            return;
        }
    }

    private async Task DispatchFsReadDirectoryAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var path = Str(paramsEl, "path") ?? "";
            if (!TryHostPath(path, out var full, out var err))
            {
                Error(id, -32602, err);
                return;
            }
            if (!Directory.Exists(full))
            {
                Error(id, -32004, $"directory not found: {full}");
                return;
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
            return;
        }
    }

    private async Task DispatchFsRemoveAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var path = Str(paramsEl, "path") ?? "";
            if (!TryHostPath(path, out var full, out var err))
            {
                Error(id, -32602, err);
                return;
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
                return;
            }
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchFsCopyAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var src = Str(paramsEl, "sourcePath") ?? "";
            var dst = Str(paramsEl, "destinationPath") ?? "";
            if (!TryHostPath(src, out var srcFull, out var err))
            {
                Error(id, -32602, err);
                return;
            }
            if (!TryHostPath(dst, out var dstFull, out err))
            {
                Error(id, -32602, err);
                return;
            }
            var recursive = Bool(paramsEl, "recursive") ?? false;
            if (Directory.Exists(srcFull))
            {
                if (!recursive)
                {
                    Error(id, -32602, "fs/copy of a directory requires recursive=true");
                    return;
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
                return;
            }
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchFsWatchAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var path = Str(paramsEl, "path") ?? Environment.CurrentDirectory;
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                Error(id, -32602, "path not found");
                return;
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
            return;
        }
    }

    private async Task DispatchFsUnwatchAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var watchId = Str(paramsEl, "watchId") ?? "";
            if (_watches.Remove(watchId, out var watcher))
            {
                watcher.Dispose();
            }
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchPluginListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var cfg = ConfigService.Load();
            var data = PluginCatalog.List(cfg.Home, cfg.Cwd).Select(PluginDto).ToArray();
            var available = PluginCatalog.ListAvailable(cfg.Home, cfg.Cwd).Select(PluginDto).ToArray();
            Result(id, new { data, available });
            return;
        }
    }

    private async Task DispatchPluginSearchAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "plugin/search")) return;
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
            return;
        }
    }

    private async Task DispatchPluginReconcileAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchPluginSkillReadAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var cfg = ConfigService.Load();
            var pluginId = Str(paramsEl, "pluginId") ?? Str(paramsEl, "pluginName") ?? Str(paramsEl, "remotePluginId") ?? "";
            var skillName = Str(paramsEl, "skillName") ?? "";
            if (string.IsNullOrWhiteSpace(pluginId))
            {
                Error(id, -32602, "pluginId is required");
                return;
            }
            var plugin = PluginCatalog.Read(pluginId, cfg.Home, cfg.Cwd);
            if (plugin is null)
            {
                Error(id, -32001, "Plugin not found: " + pluginId);
                return;
            }
            Result(id, new { contents = PluginCatalog.ReadSkill(cfg.Home, cfg.Cwd, pluginId, skillName) });
            return;
        }
    }

    private async Task DispatchPluginReadAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var cfg = ConfigService.Load();
            var name = Str(paramsEl, "pluginName") ?? Str(paramsEl, "pluginId") ?? "";
            var plugin = PluginCatalog.Read(name, cfg.Home, cfg.Cwd, Str(paramsEl, "marketplacePath"));
            if (plugin is null)
            {
                Error(id, -32001, $"Plugin not found: {name}");
                return;
            }
            Result(id, new { plugin = PluginDto(plugin) });
            return;
        }
    }

    private async Task DispatchPluginInstallAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchPluginShareSaveAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchPluginShareListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        Result(id, new { data = PluginShares.List().Select(s => new { remotePluginId = s.RemotePluginId, name = s.Name, pluginPath = s.PluginPath, discoverability = s.Discoverability }).ToArray() });
        return;
    }

    private async Task DispatchPluginShareDeleteAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var rid = Str(paramsEl, "remotePluginId") ?? "";
            if (!PluginShares.Delete(rid))
            {
                Error(id, -32001, "share not found: " + rid);
                return;
            }
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchPluginShareCheckoutAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchPluginShareUpdateTargetsAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var rec = PluginShares.UpdateTargets(Str(paramsEl, "remotePluginId") ?? "", Str(paramsEl, "discoverability"));
            if (rec is null)
            {
                Error(id, -32001, "share not found");
                return;
            }
            Result(id, new { remotePluginId = rec.RemotePluginId, discoverability = rec.Discoverability });
            return;
        }
    }

    private async Task DispatchPluginUninstallAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var pluginId = Str(paramsEl, "pluginId") ?? Str(paramsEl, "pluginName") ?? "";
            if (!PluginCatalog.Uninstall(pluginId))
            {
                Error(id, -32001, $"Plugin not installed: {pluginId}");
                return;
            }
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchMarketplaceListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var data = MarketplaceStore.List().Select(m => new { name = m.Name, source = m.Source, installedRoot = m.InstalledRoot }).ToArray();
            Result(id, new { data });
            return;
        }
    }

    private async Task DispatchMarketplaceAddAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchMarketplaceRemoveAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var name = Str(paramsEl, "marketplaceName") ?? Str(paramsEl, "name") ?? "";
            if (!MarketplaceStore.Remove(name))
            {
                Error(id, -32001, $"Marketplace not found: {name}");
                return;
            }
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchMarketplaceUpgradeAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var name = Str(paramsEl, "marketplaceName") ?? Str(paramsEl, "name") ?? "";
            var info = MarketplaceStore.Upgrade(name);
            if (info is null)
            {
                Error(id, -32001, $"Marketplace not found: {name}");
                return;
            }
            Result(id, new { installedRoot = info.InstalledRoot, marketplaceName = info.Name });
            return;
        }
    }

    private async Task DispatchModelProviderCapabilitiesReadAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var cfg = ConfigService.Load();
            Result(id, new
            {
                imageGeneration = ImageGeneration.IsConfigured(cfg),
                visualize = false,
                namespaceTools = true,
                webSearch = FeatureFlags.IsEnabled("web_search"),
            });
            return;
        }
    }

    private async Task DispatchAppReadAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchConfigMcpServerReloadAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var servers = ConfigService.LoadMcpServers().Select(s => new
            {
                name = s.Id,
                enabled = s.Enabled,
                transport = string.IsNullOrWhiteSpace(s.Url) ? "stdio" : "streamableHttp",
            }).ToArray();
            Notify("mcpServer/startupStatus/updated", new { data = servers });
            Result(id, new { data = servers });
            return;
        }
    }

    private async Task DispatchConfigReadAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
                computerUse = HonestStubs.StatusOf("computer_use"),
                browserUse = HonestStubs.StatusOf("browser_use"),
                realtimeVoice = HonestStubs.StatusOf("realtime"),
                codeModeHost = HonestStubs.StatusOf("code_mode_host"),
                extraReadRoots = SandboxRoots.List(),
                tuiRaw = ConfigService.Peek("tui_raw"),
                tuiVim = ConfigService.Peek("tui_vim"),
                tuiTheme = ConfigService.Peek("tui_theme"),
                tuiStatusline = ConfigService.Peek("tui_statusline"),
                tuiTitle = ConfigService.Peek("tui_title"),
                tuiNotifications = ConfigService.Peek("tui_notifications"),
            });
            return;
        }
    }

}
