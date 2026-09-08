using System.Text.Json;
using CodexSharp.Protocol;
using Tomlyn;

namespace CodexSharp.Runtime;

public enum DoctorStatus
{
    Ok,
    Warning,
    Fail,
}

public sealed record DoctorCheck(
    string Id,
    string Group,
    DoctorStatus Status,
    string Summary,
    IReadOnlyList<string> Details,
    string? Remediation = null);

public sealed record DoctorReport(DoctorStatus Status, IReadOnlyList<DoctorCheck> Checks);

/// Read-mostly install diagnostics inspired by vendor/codex/codex-rs/cli/src/doctor.rs.
public static class DoctorProbe
{
    public static DoctorReport Run(string? cwd = null)
    {
        CodexPaths.EnsureLayout();
        CodexConfig? cfg = null;
        Exception? loadError = null;
        try
        {
            cfg = ConfigService.Load(cwd);
        }
        catch (Exception ex)
        {
            loadError = ex;
        }

        if (cfg is null)
        {
            return new DoctorReport(DoctorStatus.Fail,
            [
                SystemCheck(),
                InstallationCheck(),
                new DoctorCheck("config.load", "config", DoctorStatus.Fail, "config failed to load", [loadError?.Message ?? "unknown"], "Fix config.toml or delete it so CodexSharp can rewrite defaults."),
            ]);
        }

        var checks = new List<DoctorCheck>
        {
            SystemCheck(),
            InstallationCheck(),
            ConfigCheck(cfg),
            AuthCheck(cfg),
            SandboxCheck(cfg),
            GitCheck(cfg),
            HooksCheck(cfg),
            SkillsCheck(cfg),
            ExecPolicyCheck(),
            WritableCheck(cfg),
            FeaturesCheck(),
            McpCheck(),
            AppsCheck(),
            StateCheck(cfg),
        };

        return new DoctorReport(Overall(checks), checks);
    }

    public static string ToJson(DoctorReport report)
    {
        var payload = new
        {
            status = StatusName(report.Status),
            checks = report.Checks.Select(c => new
            {
                id = c.Id,
                group = c.Group,
                status = StatusName(c.Status),
                summary = c.Summary,
                details = c.Details,
                remediation = c.Remediation,
            }).ToArray(),
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string StatusName(DoctorStatus status) =>
        status switch
        {
            DoctorStatus.Ok => "ok",
            DoctorStatus.Warning => "warning",
            DoctorStatus.Fail => "fail",
            _ => "fail",
        };

    private static DoctorStatus Overall(IReadOnlyList<DoctorCheck> checks)
    {
        if (checks.Any(c => c.Status == DoctorStatus.Fail)) return DoctorStatus.Fail;
        if (checks.Any(c => c.Status == DoctorStatus.Warning)) return DoctorStatus.Warning;
        return DoctorStatus.Ok;
    }

    private static DoctorCheck SystemCheck()
    {
        var details = new List<string>
        {
            "os: " + Environment.OSVersion,
            "arch: " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture,
            "framework: " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            "rid: " + System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
        };
        return new DoctorCheck("system", "system", DoctorStatus.Ok, "runtime looks usable", details);
    }

    private static DoctorCheck InstallationCheck()
    {
        var details = new List<string>
        {
            "home: " + CodexPaths.Home,
            "config.toml: " + CodexPaths.ConfigFile,
            "sessions: " + CodexPaths.SessionsDir,
        };
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe)) details.Add("current executable: " + exe);
        }
        catch { /* ignore */ }

        var status = DoctorStatus.Ok;
        var summary = "installation looks consistent";
        string? remediation = null;
        if (!File.Exists(CodexPaths.ConfigFile))
        {
            status = DoctorStatus.Warning;
            summary = "config.toml is missing";
            details.Add("config.toml: missing");
            remediation = "Run any CodexSharp command so ~/.codexsharp/config.toml is created.";
        }
        else
        {
            try
            {
                Toml.ToModel(File.ReadAllText(CodexPaths.ConfigFile));
                details.Add("config.toml parse: ok");
            }
            catch (Exception ex)
            {
                status = DoctorStatus.Fail;
                summary = "config.toml does not parse";
                details.Add("config.toml parse: " + ex.Message);
                remediation = "Fix TOML syntax in config.toml.";
            }
        }

        return new DoctorCheck("installation", "install", status, summary, details, remediation);
    }

    private static DoctorCheck ConfigCheck(CodexConfig cfg)
    {
        var details = new List<string>
        {
            "model: " + cfg.Model,
            "provider: " + cfg.Provider.Id,
            "sandbox_mode: " + cfg.SandboxMode,
            "approval_policy: " + cfg.ApprovalPolicy,
            "cwd: " + cfg.Cwd,
            "personality: " + (ConfigService.Peek("personality") ?? "default"),
            "collaboration_mode: " + (ConfigService.Peek("collaboration_mode") ?? "default"),
            "network_access: " + cfg.NetworkAccess,
            "reasoning_effort: " + cfg.ReasoningEffort,
        };
        return new DoctorCheck("config.load", "config", DoctorStatus.Ok, "config loaded", details);
    }

    private static DoctorCheck AuthCheck(CodexConfig cfg)
    {
        var details = new List<string>
        {
            "auth file: " + CodexPaths.AuthFile,
            "auth file exists: " + File.Exists(CodexPaths.AuthFile),
        };
        var envVars = new[] { "CODEXSHARP_API_KEY", "OPENAI_API_KEY", "CODEX_API_KEY", cfg.Provider.EnvKey }
            .Where(n => !string.IsNullOrWhiteSpace(n) && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(n)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (envVars.Length > 0)
        {
            details.Add("auth env vars present: " + string.Join(", ", envVars));
        }

        var hasKey = !string.IsNullOrWhiteSpace(cfg.Provider.ApiKey);
        var mode = "none";
        if (File.Exists(CodexPaths.AuthFile))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(CodexPaths.AuthFile));
                if (doc.RootElement.TryGetProperty("auth_mode", out var m) && m.ValueKind == JsonValueKind.String)
                {
                    mode = m.GetString() ?? mode;
                }
                else if (doc.RootElement.TryGetProperty("api_key", out _))
                {
                    mode = "apiKey";
                }
            }
            catch
            {
                details.Add("auth file parse: invalid json");
            }
        }

        if (hasKey && mode == "none") mode = envVars.Length > 0 ? "env" : "apiKey";
        details.Add("stored auth mode: " + mode);

        if (hasKey)
        {
            var status = envVars.Length > 1 ? DoctorStatus.Warning : DoctorStatus.Ok;
            var summary = status == DoctorStatus.Warning
                ? "auth is configured, but multiple auth env vars are present"
                : "auth is configured";
            return new DoctorCheck("auth.credentials", "auth", status, summary, details);
        }

        return new DoctorCheck(
            "auth.credentials",
            "auth",
            DoctorStatus.Fail,
            "no CodexSharp credentials were found",
            details,
            "Run codexsharp login or set OPENAI_API_KEY / CODEXSHARP_API_KEY.");
    }

    private static DoctorCheck SandboxCheck(CodexConfig cfg)
    {
        var details = new List<string>
        {
            "sandbox_mode: " + cfg.SandboxMode,
            "default sandbox_mode: workspace-write",
            "approval_policy: " + cfg.ApprovalPolicy,
        };
        var win = WindowsSandbox.Read();
        details.Add("windowsSandbox: " + win.Status);
        details.Add("jobObject: " + win.JobObject);
        if (!string.IsNullOrWhiteSpace(win.Mode)) details.Add("windowsSandbox mode: " + win.Mode);
        if (!string.IsNullOrWhiteSpace(win.Error)) details.Add("windowsSandbox note: " + win.Error);
        details.Add("official windows sandbox service: not shipped");

        var status = DoctorStatus.Ok;
        var summary = "sandbox is workspace-write with on-request approvals";
        string? remediation = null;
        var mode = (cfg.SandboxMode ?? "").Trim().ToLowerInvariant().Replace("_", "-");
        if (mode is "danger-full-access" or "dangerfullaccess" or "full-access")
        {
            status = DoctorStatus.Warning;
            summary = "sandbox_mode is danger-full-access (default is workspace-write)";
            remediation = "Set sandbox_mode = \"workspace-write\" in config.toml unless you need unrestricted writes.";
        }
        else if (mode is "read-only" or "readonly")
        {
            summary = "sandbox is read-only";
        }

        if (OperatingSystem.IsWindows() && win.Status != "ready")
        {
            if (status == DoctorStatus.Ok) status = DoctorStatus.Warning;
            if (win.JobObject)
            {
                summary = "Job Object is available; windowsSandbox is notConfigured";
                remediation ??= "Run codexsharp sandbox setup unelevated to mark the Job Object sandbox ready. The official Windows sandbox service is not shipped.";
            }
            else
            {
                summary = "Windows Job Object is unavailable";
                remediation ??= "Job Object kill-on-close is unavailable on this host.";
            }
        }

        return new DoctorCheck("sandbox", "sandbox", status, summary, details, remediation);
    }

    private static DoctorCheck GitCheck(CodexConfig cfg)
    {
        var git = GitProbe.Collect(cfg.Cwd);
        if (git is null || string.IsNullOrWhiteSpace(git.Branch))
        {
            return new DoctorCheck("git", "git", DoctorStatus.Warning, "not a git repo", ["cwd: " + cfg.Cwd]);
        }

        var details = new List<string>
        {
            "root: " + (git.Root ?? cfg.Cwd),
            "branch: " + git.Branch,
            "commit: " + (git.Commit ?? ""),
            "dirty: " + git.Dirty,
        };
        var worktrees = GitProbe.ListWorktrees(cfg.Cwd);
        if (worktrees.Count > 0) details.Add("worktrees: " + worktrees.Count);
        var summary = git.Dirty ? git.Branch + "*" : git.Branch;
        return new DoctorCheck("git", "git", DoctorStatus.Ok, summary, details);
    }

    private static DoctorCheck HooksCheck(CodexConfig cfg)
    {
        var dir = Path.Combine(cfg.Home, "hooks");
        Directory.CreateDirectory(dir);
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json") : [];
        var listed = HookCatalog.List(cfg.Home);
        var details = new List<string>
        {
            "hooks dir: " + dir,
            "hook files: " + files.Length,
            "parsed hooks: " + listed.Count,
        };
        foreach (var hook in listed.Take(12))
        {
            details.Add(hook.Name + " @ " + hook.Event);
        }

        var invalid = Math.Max(0, files.Length - listed.Count);
        if (invalid > 0)
        {
            details.Add("invalid hook files: " + invalid);
            return new DoctorCheck("hooks", "hooks", DoctorStatus.Warning, listed.Count + " hooks (" + invalid + " invalid)", details, "Fix JSON in ~/.codexsharp/hooks.");
        }

        return new DoctorCheck("hooks", "hooks", DoctorStatus.Ok, listed.Count + (listed.Count == 1 ? " hook" : " hooks"), details);
    }

    private static DoctorCheck SkillsCheck(CodexConfig cfg)
    {
        var skills = SkillCatalog.Load(cfg.Home, cfg.Cwd);
        var extra = SkillExtraRoots.Get();
        var disabled = skills.Count(s => !SkillConfig.IsEnabled(s.Name, s.Path));
        var details = new List<string>
        {
            "discovered: " + skills.Count,
            "disabled: " + disabled,
            "extra roots: " + extra.Count,
        };
        foreach (var skill in skills.Take(12))
        {
            details.Add(skill.Name + (SkillConfig.IsEnabled(skill.Name, skill.Path) ? "" : " (off)"));
        }

        return new DoctorCheck("skills", "skills", DoctorStatus.Ok, skills.Count + " SKILL.md", details);
    }

    private static DoctorCheck ExecPolicyCheck()
    {
        var rules = ExecPolicy.Load();
        var userRules = Path.Combine(CodexPaths.Home, "rules", "user.rules");
        var details = new List<string>
        {
            "rules: " + rules.Count,
            "user.rules: " + (File.Exists(userRules) ? userRules : "missing"),
        };
        foreach (var rule in rules.Take(8))
        {
            details.Add(string.Join(' ', rule.Pattern) + " -> " + rule.Decision);
        }

        return new DoctorCheck("execpolicy", "execpolicy", DoctorStatus.Ok, rules.Count + " prefix_rule" + (rules.Count == 1 ? "" : "s"), details);
    }

    private static DoctorCheck WritableCheck(CodexConfig cfg)
    {
        var roots = SandboxRoots.List();
        var details = new List<string>
        {
            "cwd writable: " + (WorkspaceSandbox.ParseSandbox(cfg.SandboxMode) != SandboxMode.ReadOnly),
            "extra read roots: " + roots.Count,
        };
        foreach (var root in roots.Take(8)) details.Add("read root: " + root);

        var scan = WorldWritableScan.Scan(cfg.Cwd);
        if (scan.FailedScan)
        {
            details.Add("world-writable scan: skipped");
        }
        else if (scan.SamplePaths.Count == 0)
        {
            details.Add("world-writable: none sampled");
        }
        else
        {
            details.Add("world-writable samples: " + scan.SamplePaths.Count + (scan.ExtraCount > 0 ? " +" + scan.ExtraCount : ""));
            details.AddRange(scan.SamplePaths.Select(p => "world-writable: " + p));
            return new DoctorCheck("writable", "writable", DoctorStatus.Warning, "world-writable paths were sampled", details, "Tighten ACLs on sampled paths if they should not be writable by Everyone.");
        }

        return new DoctorCheck("writable", "writable", DoctorStatus.Ok, "writable roots look bounded", details);
    }

    private static DoctorCheck FeaturesCheck()
    {
        var listed = FeatureFlags.List();
        var enabled = listed.Where(kv => kv.Value).Select(kv => kv.Key).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
        var overrides = new List<string>();
        foreach (var spec in FeatureFlags.Specs)
        {
            var on = listed.TryGetValue(spec.Key, out var flag) && flag;
            if (on != spec.DefaultEnabled) overrides.Add(spec.Key + "=" + on.ToString().ToLowerInvariant());
        }

        var details = new List<string>
        {
            "feature flags enabled: " + enabled.Length,
            "enabled feature flags: " + (enabled.Length == 0 ? "none" : string.Join(", ", enabled)),
            "feature flag overrides: " + (overrides.Count == 0 ? "none" : string.Join(", ", overrides)),
        };
        return new DoctorCheck("features", "features", DoctorStatus.Ok, enabled.Length + " enabled", details);
    }

    private static DoctorCheck McpCheck()
    {
        var servers = ConfigService.LoadMcpServers();
        var details = new List<string> { "mcp_servers: " + servers.Count };
        foreach (var server in servers.Take(12))
        {
            details.Add(server.Id + (server.Enabled ? "" : " (off)"));
        }

        return new DoctorCheck("mcp.config", "mcp", DoctorStatus.Ok, servers.Count + " mcp_servers", details);
    }

    private static DoctorCheck AppsCheck()
    {
        var details = new List<string>
        {
            "computerUse: " + HonestStubs.StatusOf("computer_use"),
            "browserUse: " + HonestStubs.StatusOf("browser_use"),
            "realtime webrtc: " + HonestStubs.StatusOf("realtime"),
        };
        return new DoctorCheck("apps", "apps", DoctorStatus.Ok, "computer/browser use are notConfigured", details);
    }

    private static DoctorCheck StateCheck(CodexConfig cfg)
    {
        var sessions = Directory.Exists(CodexPaths.SessionsDir) ? Directory.GetFiles(CodexPaths.SessionsDir, "*.jsonl").Length : 0;
        var archived = Directory.Exists(CodexPaths.ArchivedDir) ? Directory.GetFiles(CodexPaths.ArchivedDir, "*.jsonl").Length : 0;
        var details = new List<string>
        {
            "home: " + cfg.Home,
            "sessions: " + sessions,
            "archived: " + archived,
            "logs: " + CodexPaths.LogsDir,
        };
        return new DoctorCheck("state.paths", "state", DoctorStatus.Ok, "state paths exist", details);
    }
}
