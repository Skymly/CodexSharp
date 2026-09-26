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
    private async Task DispatchInitializeAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
        return;
    }

    private async Task DispatchMockExperimentalMethodAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        if (!RequireExperimental(id, hasId, "mock/experimentalMethod")) return;
        Result(id, new { ok = true });
        return;
    }

    private async Task DispatchScheduledListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            Result(id, new { data = ScheduledStore.List() });
            return;
        }
    }

    private async Task DispatchScheduledCreateAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var prompt = Str(paramsEl, "prompt") ?? "";
            var minutes = paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("everyMinutes", out var mEl) && mEl.TryGetInt32(out var mins) ? mins : 1;
            var threadId = Str(paramsEl, "threadId");
            var task = ScheduledStore.Create(prompt, minutes, threadId, DateTimeOffset.UtcNow);
            Result(id, new { task });
            return;
        }
    }

    private async Task DispatchScheduledCancelAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var sid = Str(paramsEl, "id") ?? "";
            Result(id, new { cancelled = ScheduledStore.Cancel(sid) });
            return;
        }
    }

    private async Task DispatchScheduledRunsAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            Result(id, new { data = ScheduledStore.Runs() });
            return;
        }
    }

    private async Task DispatchScheduledTickAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var emitted = ScheduledStore.Tick(DateTimeOffset.UtcNow, true, task =>
            {
                if (task.Kind == ScheduledKind.Independent || string.IsNullOrWhiteSpace(task.ThreadId))
                {
                    var config = ConfigService.Load();
                    var created = CodexSession.Start(config, "scheduled");
                    Wire(created);
                    _threads[created.Thread.Id] = created;
                    _ = created.RunTurnAsync(task.Prompt, CancellationToken.None);
                }
                else if (_threads.TryGetValue(task.ThreadId, out var live))
                {
                    _ = live.RunTurnAsync(task.Prompt, CancellationToken.None);
                }
                else
                {
                    throw new InvalidOperationException("thread not loaded");
                }
            });
            Result(id, new { data = emitted });
            return;
        }
    }

    private async Task DispatchCurrentTimeReadAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "currentTime/read")) return;
            Result(id, new { currentTimeAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
            return;
        }
    }

    private async Task DispatchMcpServerEventStreamStartAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "mcpServer/event/stream/start")) return;
            var sub = Str(paramsEl, "subscriptionId") ?? "";
            if (string.IsNullOrWhiteSpace(sub)) { Error(id, -32602, "subscriptionId is required"); return; }
            _mcpStreams.Add(sub);
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchMcpServerEventStreamStopAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "mcpServer/event/stream/stop")) return;
            _mcpStreams.Remove(Str(paramsEl, "subscriptionId") ?? "");
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchEnvironmentInfoAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var environmentId = Str(paramsEl, "environmentId") ?? EnvironmentStore.LocalId;
            var (st, err) = EnvironmentStore.Status(environmentId);
            if (st == "unknown")
            {
                Error(id, -32001, err ?? "unknown environment");
                return;
            }
            if (st != "ready")
            {
                Error(id, -32002, err ?? "environment is not ready");
                return;
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
            return;
        }
    }

    private async Task DispatchEnvironmentAddAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "environment/add")) return;
            try
            {
                EnvironmentStore.Add(Str(paramsEl, "environmentId") ?? "", Str(paramsEl, "execServerUrl") ?? "");
                Result(id, new { });
            }
            catch (ArgumentException ex)
            {
                Error(id, -32602, ex.Message);
            }
            return;
        }
    }

    private async Task DispatchEnvironmentStatusAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "environment/status")) return;
            var environmentId = Str(paramsEl, "environmentId") ?? "";
            if (string.IsNullOrWhiteSpace(environmentId))
            {
                Error(id, -32602, "environmentId is required");
                return;
            }
            var (st, err) = EnvironmentStore.Status(environmentId);
            Result(id, new { status = st, error = err });
            return;
        }
    }

    private async Task DispatchDiagnosticsDoctorAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchServerDiagnosticsAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "server/diagnostics")) return;
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
            return;
        }
    }

    private async Task DispatchThreadSectionListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var data = ThreadSections.List().Select(SectionDto).ToList();
            var (page, next, _) = TimelinePaging.Slice(data, Str(paramsEl, "cursor"), Int(paramsEl, "limit", 50));
            Result(id, new { data = page, nextCursor = next });
            return;
        }
    }

    private async Task DispatchThreadSectionCreateAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchThreadSectionUpdateAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
                return;
            }
            Result(id, new { section = SectionDto(updated) });
            return;
        }
    }

    private async Task DispatchThreadSectionDeleteAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var sectionId = Str(paramsEl, "sectionId") ?? "";
            if (!ThreadSections.Delete(sectionId))
            {
                Error(id, -32001, $"Section not found: {sectionId}");
                return;
            }
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchHooksListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var cfg = ConfigService.Load();
            var data = HookCatalog.List(cfg.Home, cfg.Cwd).Select(h => new { name = h.Name, @event = h.Event, command = h.Command, path = h.Path, source = h.Source }).ToArray();
            Result(id, new { data });
            return;
        }
    }

    private async Task DispatchConfigRequirementsReadAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        Result(id, new
        {
            requirements = (object?)null,
            allowBrowserAndComputerUse = !HonestStubs.ForbidsEnable("browser_use") && !HonestStubs.ForbidsEnable("computer_use"),
            note = "No requirements.toml / MDM policy is configured.",
        });
        return;
    }

    private async Task DispatchSkillsExtraRootsSetAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchPromptsListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var cfg = ConfigService.Load();
            var data = PromptCatalog.List(cfg.Home, cfg.Cwd).Select(p => new { name = p.Name, path = p.Path, preview = p.Preview }).ToArray();
            Result(id, new { data });
            return;
        }
    }

    private async Task DispatchPromptsReadAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var cfg = ConfigService.Load();
            var name = Str(paramsEl, "name") ?? "";
            var body = PromptCatalog.Read(name, cfg.Home, cfg.Cwd);
            if (body is null)
            {
                Error(id, -32001, "unknown prompt " + name);
                return;
            }
            Result(id, new { name, contents = body });
            return;
        }
    }

    private async Task DispatchSkillsListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchSkillsConfigWriteAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var enabled = Bool(paramsEl, "enabled") ?? false;
            var name = Str(paramsEl, "name");
            var path = Str(paramsEl, "path");
            var effective = SkillConfig.Set(name, path, enabled);
            Notify("skills/changed", new { name, path, enabled = effective });
            Result(id, new { effectiveEnabled = effective });
            return;
        }
    }

    private async Task DispatchMcpServerOauthLoginAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var name = Str(paramsEl, "name") ?? "";
            Notify("mcpServer/oauthLogin/completed", new { name, threadId = Str(paramsEl, "threadId"), success = false, error = "MCP OAuth is not configured in CodexSharp." });
            Error(id, -32002, "MCP OAuth is not configured in CodexSharp.");
            return;
        }
    }

    private async Task DispatchMcpServerResourceReadAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var server = Str(paramsEl, "server") ?? "";
            var uri = Str(paramsEl, "uri") ?? "";
            if (string.IsNullOrWhiteSpace(uri))
            {
                Error(id, -32602, "uri is required");
                return;
            }
            var spec = ConfigService.LoadMcpServers().FirstOrDefault(s => s.Id == server);
            if (spec is null)
            {
                Error(id, -32001, $"Unknown MCP server {server}");
                return;
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
            return;
        }
    }

    private async Task DispatchMcpServerToolCallAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var server = Str(paramsEl, "server") ?? "";
            var tool = Str(paramsEl, "tool") ?? "";
            var spec = ConfigService.LoadMcpServers().FirstOrDefault(s => s.Id == server);
            if (spec is null)
            {
                Error(id, -32001, $"Unknown MCP server {server}");
                return;
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
            return;
        }
    }

    private async Task DispatchMcpServerStatusListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchMcpServerAddAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchMcpServerRemoveAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var name = Str(paramsEl, "name") ?? "";
            if (!McpConfig.Remove(name))
            {
                Error(id, -32001, "MCP server not found: " + name);
                return;
            }
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchFuzzyFileSearchSessionStartAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "fuzzyFileSearch/sessionStart")) return;
            var sessionId = Str(paramsEl, "sessionId") ?? "";
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                Error(id, -32602, "sessionId is required");
                return;
            }
            var roots = new List<string>();
            if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("roots", out var rootEl) && rootEl.ValueKind == JsonValueKind.Array)
            {
                roots.AddRange(rootEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0));
            }
            if (roots.Count == 0) roots.Add(ConfigService.Load().Cwd);
            _fuzzySessions[sessionId] = roots.ToArray();
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchFuzzyFileSearchSessionUpdateAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "fuzzyFileSearch/sessionUpdate")) return;
            var sessionId = Str(paramsEl, "sessionId") ?? "";
            if (!_fuzzySessions.TryGetValue(sessionId, out var roots))
            {
                Error(id, -32001, "fuzzy search session not found: " + sessionId);
                return;
            }
            var query = Str(paramsEl, "query") ?? "";
            var files = FuzzyFiles(roots, query);
            Notify("fuzzyFileSearch/sessionUpdated", new { sessionId, query, files });
            Notify("fuzzyFileSearch/sessionCompleted", new { sessionId });
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchFuzzyFileSearchSessionStopAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "fuzzyFileSearch/sessionStop")) return;
            _fuzzySessions.Remove(Str(paramsEl, "sessionId") ?? "");
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchFuzzyFileSearchAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchMemoryListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var notes = MemoryStore.ListNotes().Select(n => new { name = n, path = Path.Combine(MemoryStore.MemoriesRoot, n.Replace('/', Path.DirectorySeparatorChar)) }).ToArray();
            Result(id, new { data = notes });
            return;
        }
    }

    private async Task DispatchMemoryResetAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "memory/reset")) return;
            MemoryStore.Reset();
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchMemoryReadAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchMemoryWriteAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchTuiKeymapListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var data = TuiKeymap.Load().Select(b => new { context = b.Context, action = b.Action, keys = b.Keys }).ToArray();
            Result(id, new { data });
            return;
        }
    }

    private async Task DispatchTuiKeymapSetAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var context = Str(paramsEl, "context") ?? "";
            var action = Str(paramsEl, "action") ?? "";
            var keys = Str(paramsEl, "keys") ?? "";
            if (!TuiKeymap.TrySet(context, action, keys))
            {
                Error(id, -32602, "unknown keymap slot");
                return;
            }
            Result(id, new { context, action, keys });
            return;
        }
    }

    private async Task DispatchTuiPetsReadAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            Result(id, new { id = TuiPets.Current, ascii = TuiPets.Ascii(), ids = TuiPets.Ids });
            return;
        }
    }

    private async Task DispatchTuiPetsSetAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var pet = Str(paramsEl, "id") ?? Str(paramsEl, "name") ?? "";
            if (!TuiPets.TrySet(pet))
            {
                Error(id, -32602, "unknown pet");
                return;
            }
            Result(id, new { id = TuiPets.Current, ascii = TuiPets.Ascii() });
            return;
        }
    }

    private async Task DispatchAccountUsageReadAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchAccountRateLimitsReadAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
        return;
    }

    private async Task DispatchAccountReadAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var mode = AuthService.AuthMode();
            var cfg = ConfigService.Load();
            var hasKey = !string.IsNullOrWhiteSpace(cfg.Provider.ApiKey);
            Result(id, new {
                account = hasKey ? new { type = mode ?? "apiKey" } : null,
                requiresOpenaiAuth = !hasKey,
            });
            return;
        }
    }

    private async Task DispatchGetAuthStatusAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var mode = AuthService.AuthMode();
            var cfg = ConfigService.Load();
            var hasKey = !string.IsNullOrWhiteSpace(cfg.Provider.ApiKey);
            Result(id, new {
                authMethod = hasKey ? (mode ?? "apiKey") : null,
                requiresOpenaiAuth = !hasKey,
            });
            return;
        }
    }

    private async Task DispatchAccountLoginStartAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var kind = Str(paramsEl, "type") ?? "";
            if (kind is "apiKey")
            {
                var key = Str(paramsEl, "apiKey") ?? "";
                if (string.IsNullOrWhiteSpace(key))
                {
                    Error(id, -32602, "apiKey is required");
                    return;
                }
                AuthService.SaveApiKey(key);
                Notify("account/updated", new { authMode = "apiKey" });
                Result(id, new { type = "apiKey" });
                return;
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
                return;
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
                return;
            }

            if (kind is "chatgptAuthTokens")
            {
                var access = Str(paramsEl, "accessToken") ?? Str(paramsEl, "access_token") ?? "";
                if (string.IsNullOrWhiteSpace(access))
                {
                    Error(id, -32602, "accessToken is required");
                    return;
                }
                var refresh = Str(paramsEl, "refreshToken") ?? Str(paramsEl, "refresh_token") ?? "";
                var idToken = Str(paramsEl, "idToken") ?? Str(paramsEl, "id_token") ?? "";
                AuthService.SaveChatgptTokens(access, refresh, idToken);
                Notify("account/updated", new { authMode = "chatgpt" });
                Result(id, new { type = "chatgptAuthTokens", authMode = "chatgpt" });
                return;
            }

            if (kind is "amazonBedrock" or "amazonBedrockAccessKeys")
            {
                Error(id, -32002, "Amazon Bedrock login is not implemented in CodexSharp.");
                return;
            }

            Error(id, -32002, "ChatGPT OAuth is not configured in CodexSharp; use type=apiKey.");
            return;
        }
    }

    private async Task DispatchAccountBedrockDiscoverAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var found = BedrockDiscover.Scan();
            Result(id, new
            {
                profiles = found.Profiles.Select(p => new { name = p.Name, region = p.Region }).ToArray(),
                environmentCredentials = found.EnvironmentCredentials.Select(c => new { type = c.Type, region = c.Region }).ToArray(),
            });
            return;
        }
    }

    private async Task DispatchAccountBedrockSetupAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        Error(id, -32002, "Amazon Bedrock setup is not implemented in CodexSharp.");
        return;
    }

    private async Task DispatchAccountRateLimitResetCreditConsumeAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        Result(id, new { outcome = "noCredit" });
        return;
    }

    private async Task DispatchAccountSendAddCreditsNudgeEmailAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        Error(id, -32002, "ChatGPT workspace email is not available without the ChatGPT backend.");
        return;
    }

    private async Task DispatchAccountLoginCancelAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var loginId = Str(paramsEl, "loginId") ?? "";
            if (!string.IsNullOrWhiteSpace(loginId) && _logins.Remove(loginId, out var loginCts))
            {
                loginCts.Cancel();
                loginCts.Dispose();
            }
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchAccountChatgptAuthTokensRefreshAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchAccountWorkspaceMessagesReadAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        Result(id, new { messages = Array.Empty<object>() });
        return;
    }

    private async Task DispatchAttestationGenerateAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        Error(id, -32002, "Attestation backend is not configured in CodexSharp.");
        return;
    }

    private async Task DispatchExternalAgentConfigImportRecordHistoryAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        Result(id, new { data = ExternalAgentImport.ReadHistories() });
        return;
    }

    private async Task DispatchAccountLogoutAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (File.Exists(CodexPaths.AuthFile))
            {
                File.Delete(CodexPaths.AuthFile);
            }
            Notify("account/updated", new { authMode = (string?)null });
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchRemoteControlStatusReadAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "remoteControl/status/read")) return;
            var snap = RemoteControlState.Read();
            Result(id, new { status = snap.Status, installationId = snap.InstallationId, serverName = snap.ServerName, environmentId = (string?)null });
            return;
        }
    }

    private async Task DispatchRemoteControlEnableAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "remoteControl/enable")) return;
            var snap = RemoteControlState.SetEnabled(true);
            Notify("remoteControl/status/changed", new { status = snap.Status, installationId = snap.InstallationId, serverName = snap.ServerName, environmentId = (string?)null });
            Result(id, new { status = snap.Status, installationId = snap.InstallationId, serverName = snap.ServerName, environmentId = (string?)null });
            return;
        }
    }

    private async Task DispatchRemoteControlDisableAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "remoteControl/disable")) return;
            var snap = RemoteControlState.SetEnabled(false);
            Notify("remoteControl/status/changed", new { status = snap.Status, installationId = snap.InstallationId, serverName = snap.ServerName, environmentId = (string?)null });
            Result(id, new { status = snap.Status, installationId = snap.InstallationId, serverName = snap.ServerName, environmentId = (string?)null });
            return;
        }
    }

    private async Task DispatchRemoteControlPairingStartAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "remoteControl/pairing/start")) return;
            var manual = Flag(paramsEl, "manualCode");
            var pairing = RemoteControlState.StartPairing(manual);
            Result(id, new
            {
                pairingCode = pairing.PairingCode,
                manualPairingCode = pairing.ManualPairingCode,
                environmentId = pairing.EnvironmentId,
                expiresAt = pairing.ExpiresAt,
            });
            return;
        }
    }

    private async Task DispatchRemoteControlPairingStatusAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "remoteControl/pairing/status")) return;
            try
            {
                var claimed = RemoteControlState.PairingClaimed(Str(paramsEl, "pairingCode"), Str(paramsEl, "manualPairingCode"));
                Result(id, new { claimed });
            }
            catch (ArgumentException ex)
            {
                Error(id, -32602, ex.Message);
            }
            return;
        }
    }

    private async Task DispatchRemoteControlClientListAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "remoteControl/client/list")) return;
            try
            {
                var data = RemoteControlState.ListClients(Str(paramsEl, "environmentId") ?? "");
                Result(id, new { data, nextCursor = (string?)null });
            }
            catch (ArgumentException ex)
            {
                Error(id, -32602, ex.Message);
            }
            return;
        }
    }

    private async Task DispatchRemoteControlClientRevokeAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "remoteControl/client/revoke")) return;
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
            return;
        }
    }

    private async Task DispatchExternalAgentConfigDetectAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var cwds = new List<string>();
            if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("cwds", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.Array)
                cwds.AddRange(cwdEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0));
            var includeHome = Flag(paramsEl, "includeHome");
            var items = ExternalAgentImport.Detect(cwds, includeHome).Select(x => new { itemType = x.ItemType, description = x.Description, cwd = x.Cwd }).ToArray();
            Result(id, new { items, connectors = Array.Empty<object>() });
            return;
        }
    }

    private async Task DispatchExternalAgentConfigImportAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchExternalAgentConfigImportReadHistoriesAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        Result(id, new { data = ExternalAgentImport.ReadHistories() });
        return;
    }

    private async Task DispatchFeedbackUploadAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
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
            return;
        }
    }

    private async Task DispatchMcpServerElicitationRespondAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? _threads.Keys.FirstOrDefault() ?? "";
            var requestId = Str(paramsEl, "requestId") ?? "";
            var answers = Str(paramsEl, "answersJson") ?? (paramsEl.ValueKind == JsonValueKind.Object ? paramsEl.GetRawText() : "{\"action\":\"decline\"}");
            if (_threads.TryGetValue(threadId, out var elicitSession))
            {
                elicitSession.ResolveMcpElicitation(requestId, answers);
            }
            Result(id, new { });
            return;
        }
    }

}
