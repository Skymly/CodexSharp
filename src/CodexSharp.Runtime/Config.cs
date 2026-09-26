using System.Runtime.InteropServices;
using System.Text.Json;
using CodexSharp.Protocol;
using Tomlyn;
using Tomlyn.Model;

namespace CodexSharp.Runtime;

public static class CodexPaths
{
    public static string Home
    {
        get
        {
            var overrideHome = Environment.GetEnvironmentVariable("CODEXSHARP_HOME");
            if (!string.IsNullOrWhiteSpace(overrideHome))
            {
                return Path.GetFullPath(overrideHome);
            }

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(profile, ".codexsharp");
        }
    }

    public static string ConfigFile => Path.Combine(Home, "config.toml");

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> ConfigGates = new(StringComparer.OrdinalIgnoreCase);

    private static object ConfigGate() => ConfigGates.GetOrAdd(ConfigFile, static _ => new object());

    public static string ReadConfigText()
    {
        EnsureLayout();
        lock (ConfigGate())
        {
            for (var i = 0; i < 8; i++)
            {
                try
                {
                    if (!File.Exists(ConfigFile))
                    {
                        return "";
                    }

                    using var stream = new FileStream(ConfigFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    return reader.ReadToEnd();
                }
                catch (IOException) when (i < 7)
                {
                    Thread.Sleep(20 * (i + 1));
                }
            }

            return "";
        }
    }

    public static void WriteConfigText(string text)
    {
        EnsureLayout();
        lock (ConfigGate())
        {
            for (var i = 0; i < 8; i++)
            {
                try
                {
                    var tmp = ConfigFile + ".tmp";
                    File.WriteAllText(tmp, text);
                    File.Move(tmp, ConfigFile, overwrite: true);
                    return;
                }
                catch (IOException) when (i < 7)
                {
                    Thread.Sleep(20 * (i + 1));
                }
            }
        }
    }
    public static string SessionsDir => Path.Combine(Home, "sessions");
    public static string ArchivedDir => Path.Combine(Home, "archived_sessions");
    public static string LogsDir => Path.Combine(Home, "log");
    public static string AuthFile => Path.Combine(Home, "auth.json");

    public static void EnsureLayout()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(SessionsDir);
        Directory.CreateDirectory(ArchivedDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(Path.Combine(Home, "skills"));
        Directory.CreateDirectory(Path.Combine(Home, "plugins"));
        Directory.CreateDirectory(Path.Combine(Home, "marketplaces"));
        Directory.CreateDirectory(Path.Combine(Home, "prompts"));
        if (!File.Exists(ConfigFile))
        {
            File.WriteAllText(ConfigFile, DefaultConfigToml);
        }
    }

    public const string DefaultConfigToml =
        """
        model = "gpt-4.1-mini"
        model_provider = "openai"
        approval_policy = "on-request"
        sandbox_mode = "workspace-write"
        model_reasoning_effort = "medium"

        [sandbox_workspace_write]
        network_access = false

        [model_providers.openai]
        name = "OpenAI"
        base_url = "https://api.openai.com/v1"
        env_key = "OPENAI_API_KEY"
        wire_api = "responses"

        [model_providers.openai_chat]
        name = "OpenAI Chat Completions"
        base_url = "https://api.openai.com/v1"
        env_key = "OPENAI_API_KEY"
        wire_api = "chat"

        [model_providers.lmstudio]
        name = "LM Studio"
        base_url = "http://127.0.0.1:1234/v1"
        env_key = "CODEXSHARP_API_KEY"
        wire_api = "chat"

        [model_providers.minimax]
        name = "MiniMax"
        base_url = "https://api.minimaxi.com/v1"
        env_key = "MINIMAX"
        wire_api = "chat"

        # [mcp_servers.example]
        # command = "npx"
        # args = ["-y", "mcp-server"]
        """;
}

public static class ConfigService
{
    public static readonly HashSet<string> KnownRootKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "model", "model_provider", "approval_policy", "sandbox_mode", "model_reasoning_effort",
        "profile", "personality", "collaboration_mode", "notify", "instructions",
        "features", "mcp_servers", "model_providers", "profiles", "sandbox_workspace_write", "projects",
    };

    public static CodexConfig Load(string? cwd = null, string? modelOverride = null, string? sandboxOverride = null, string? approvalOverride = null, string? profile = null, bool ignoreUserConfig = false, bool strictConfig = false)
    {
        CodexPaths.EnsureLayout();
        cwd = Path.GetFullPath(cwd ?? Environment.CurrentDirectory);

        var userToml = !ignoreUserConfig ? CodexPaths.ReadConfigText() : "";
        var table = !string.IsNullOrWhiteSpace(userToml)
            ? Toml.ToModel(userToml)
            : new TomlTable();
        if (!ignoreUserConfig)
        {
            foreach (var file in ProjectTrust.ProjectConfigFiles(cwd))
            {
                OverlayToml(table, Toml.ToModel(File.ReadAllText(file)), skipProjects: true);
            }
        }

        if (strictConfig)
        {
            var unknown = table.Keys.Where(k => !KnownRootKeys.Contains(k)).ToArray();
            if (unknown.Length > 0)
            {
                throw new InvalidOperationException("unknown config keys: " + string.Join(", ", unknown));
            }
        }

        var profileName = profile ?? Env("CODEXSHARP_PROFILE") ?? ReadString(table, "profile");
        TomlTable? profileTable = null;
        if (!string.IsNullOrWhiteSpace(profileName)
            && table.TryGetValue("profiles", out var profilesObj)
            && profilesObj is TomlTable profiles
            && profiles.TryGetValue(profileName, out var ptab)
            && ptab is TomlTable ptbl)
        {
            profileTable = ptbl;
        }

        string? FromProfile(string key) => profileTable is null ? null : ReadString(profileTable, key);

        var providerId = Env("CODEXSHARP_PROVIDER")
            ?? FromProfile("model_provider")
            ?? ReadString(table, "model_provider")
            ?? "openai";

        var providers = table.TryGetValue("model_providers", out var providersObj) && providersObj is TomlTable providersTable
            ? providersTable
            : new TomlTable();

        var providerTable = providers.TryGetValue(providerId, out var p) && p is TomlTable pt ? pt : new TomlTable();
        var envKey = ReadString(providerTable, "env_key") ?? "OPENAI_API_KEY";
        var apiKey = FirstNonEmpty(
            Env("CODEXSHARP_API_KEY"),
            Env(envKey),
            Env("OPENAI_API_KEY"),
            Env("MINIMAX"),
            Env("MiniMax"),
            ReadAuthFile());

        var provider = new ModelProvider(
            providerId,
            ReadString(providerTable, "name") ?? providerId,
            TrimSlash(Env("CODEXSHARP_BASE_URL") ?? ReadString(providerTable, "base_url") ?? "https://api.openai.com/v1"),
            envKey,
            ReadString(providerTable, "wire_api") ?? "chat",
            apiKey ?? "");

        var network = false;
        if (table.TryGetValue("sandbox_workspace_write", out var sw) && sw is TomlTable swt)
        {
            network = swt.TryGetValue("network_access", out var n) && n is true;
        }

        return new CodexConfig(
            CodexPaths.Home,
            modelOverride ?? Env("CODEXSHARP_MODEL") ?? FromProfile("model") ?? ReadString(table, "model") ?? "gpt-4.1-mini",
            FromProfile("model_reasoning_effort") ?? ReadString(table, "model_reasoning_effort") ?? "medium",
            provider,
            approvalOverride ?? FromProfile("approval_policy") ?? ReadString(table, "approval_policy") ?? "on-request",
            sandboxOverride ?? FromProfile("sandbox_mode") ?? ReadString(table, "sandbox_mode") ?? "workspace-write",
            CollaborationPrompt(FromProfile("collaboration_mode") ?? ReadString(table, "collaboration_mode")) + PersonalityPrompt(FromProfile("personality") ?? ReadString(table, "personality")) + (ReadString(table, "developer_instructions") ?? ""),
            ReadString(table, "user_instructions") ?? "",
            cwd,
            network,
            24);
    }

    public static IReadOnlyList<McpServerSpec> LoadMcpServers(string? cwd = null)
    {
        CodexPaths.EnsureLayout();
        cwd = Path.GetFullPath(cwd ?? Environment.CurrentDirectory);
        var table = new TomlTable();
        var userMcp = CodexPaths.ReadConfigText();
        if (!string.IsNullOrWhiteSpace(userMcp))
        {
            OverlayToml(table, Toml.ToModel(userMcp), skipProjects: true);
        }
        foreach (var file in ProjectTrust.ProjectConfigFiles(cwd))
        {
            OverlayToml(table, Toml.ToModel(File.ReadAllText(file)), skipProjects: true);
        }

        if (!table.TryGetValue("mcp_servers", out var serversObj) || serversObj is not TomlTable servers)
        {
            return [];
        }

        var list = new List<McpServerSpec>();
        foreach (var (id, value) in servers)
        {
            if (value is not TomlTable spec)
            {
                continue;
            }

            var command = spec.TryGetValue("command", out var c) ? c?.ToString() ?? "" : "";
            var enabled = !spec.TryGetValue("enabled", out var e) || e is not false;
            var args = spec.TryGetValue("args", out var a) && a is TomlArray arr
                ? arr.Select(x => x?.ToString() ?? "").Where(s => s.Length > 0).ToArray()
                : [];
            var url = spec.TryGetValue("url", out var u) ? u?.ToString() : null;
            var bearer = spec.TryGetValue("bearer_token_env_var", out var b) ? b?.ToString() : null;
            list.Add(new McpServerSpec(id, command, args, enabled, url, bearer));
        }

        return list;
    }

    public static void WriteUserKeys(IReadOnlyDictionary<string, string> edits)
    {
        CodexPaths.EnsureLayout();
        var text = CodexPaths.ReadConfigText();
        foreach (var (key, value) in edits)
        {
            var replacement = key + " = \"" + value.Replace("\"", "\\\"") + "\"";
            var rx = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(key) + @"\s*=.*$", System.Text.RegularExpressions.RegexOptions.Multiline);
            if (rx.IsMatch(text))
            {
                text = rx.Replace(text, replacement, 1);
            }
            else
            {
                var line = replacement + Environment.NewLine;
                var table = text.IndexOf('[');
                if (table >= 0)
                {
                    var nl = text.LastIndexOf('\n', table);
                    var at = nl >= 0 ? nl + 1 : table;
                    text = text.Insert(at, line);
                }
                else
                {
                    text = text.TrimEnd() + Environment.NewLine + line;
                }
            }
        }

        CodexPaths.WriteConfigText(text);
    }

    public static string? Peek(string key)
    {
        var peek = CodexPaths.ReadConfigText();
        if (string.IsNullOrWhiteSpace(peek))
        {
            return null;
        }

        var table = Toml.ToModel(peek);
        return ReadString(table, key);
    }

    private static string CollaborationPrompt(string? mode) =>
        CollaborationModes.Overlay(mode);

    private static string PersonalityPrompt(string? personality) =>
        PersonalityModes.Overlay(personality);

    private static string? ReadAuthFile()
    {
        try
        {
            if (!File.Exists(CodexPaths.AuthFile))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(CodexPaths.AuthFile));
            var root = doc.RootElement;
            if (root.TryGetProperty("api_key", out var key) && key.ValueKind == JsonValueKind.String)
            {
                return key.GetString();
            }

            if (root.TryGetProperty("openai_api_key", out var openai) && openai.ValueKind == JsonValueKind.String)
            {
                return openai.GetString();
            }

            if (root.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object
                && tokens.TryGetProperty("access_token", out var access) && access.ValueKind == JsonValueKind.String)
            {
                return access.GetString();
            }
        }
        catch
        {
            // ignore malformed auth
        }

        return null;
    }

    private static void OverlayToml(TomlTable dest, TomlTable src, bool skipProjects)
    {
        foreach (var (key, value) in src)
        {
            if (skipProjects && key.Equals("projects", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (value is TomlTable srcTable && dest.TryGetValue(key, out var existing) && existing is TomlTable destTable)
            {
                OverlayToml(destTable, srcTable, skipProjects: false);
            }
            else
            {
                dest[key] = value;
            }
        }
    }

    private static string? ReadString(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string TrimSlash(string url) => url.TrimEnd('/');
}

public sealed class WorkspaceSandbox(CodexConfig config, IEnumerable<string>? extraReadRoots = null)
{
    private readonly string[] extraRead = NormalizeExtraRead(extraReadRoots);

    public string Cwd { get; } = Path.GetFullPath(config.Cwd);
    public SandboxMode Mode { get; } = ParseSandbox(config.SandboxMode);
    public IReadOnlyList<string> ExtraReadRoots => extraRead;

    public string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Cwd;
        }

        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Cwd, path));
    }

    public void EnsureReadable(string path)
    {
        var full = Resolve(path);
        if (Mode == SandboxMode.DangerFullAccess)
        {
            return;
        }

        if (!IsInside(full, Cwd) && !IsInside(full, config.Home) && !SandboxRoots.Allows(full) && !SessionSandbox.Allows(full) && !AllowsExtraRead(full))
        {
            throw new InvalidOperationException($"Sandbox denied read outside workspace: {full}");
        }

        if (IsHomeSecret(full, config.Home))
        {
            throw new InvalidOperationException($"Sandbox denied read of secret file: {full}");
        }
    }

    public void EnsureWritable(string path)
    {
        var full = Resolve(path);
        if (Mode == SandboxMode.DangerFullAccess)
        {
            return;
        }

        if (Mode == SandboxMode.ReadOnly)
        {
            throw new InvalidOperationException($"Sandbox is read-only: {full}");
        }

        if (!IsInside(full, Cwd) && !SessionSandbox.Allows(full))
        {
            throw new InvalidOperationException($"Sandbox denied write outside workspace: {full}");
        }

        if (IsProtected(full))
        {
            throw new InvalidOperationException($"Sandbox denied write to protected path: {full}");
        }
    }

    private bool AllowsExtraRead(string full)
    {
        foreach (var root in extraRead)
        {
            if (IsInside(full, root))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] NormalizeExtraRead(IEnumerable<string>? extraReadRoots) =>
        extraReadRoots is null
            ? []
            : extraReadRoots
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r =>
                {
                    try { return Path.GetFullPath(r.Trim()); }
                    catch { return r.Trim(); }
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private bool IsProtected(string full)
    {
        var git = Path.Combine(Cwd, ".git");
        var dotCodex = Path.Combine(Cwd, ".codex");
        var home = config.Home;
        return IsInside(full, git) || IsInside(full, dotCodex) || IsInside(full, home);
    }

    internal static bool IsInside(string path, string root)
    {
        var normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalized.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsHomeSecret(string path, string home)
    {
        if (string.IsNullOrWhiteSpace(home) || !IsInside(path, home))
        {
            return false;
        }

        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (name.Equals("auth.json", StringComparison.OrdinalIgnoreCase)
            || name.Contains("token", StringComparison.OrdinalIgnoreCase)
            || name.Equals("id_rsa", StringComparison.OrdinalIgnoreCase)
            || name.Equals("id_dsa", StringComparison.OrdinalIgnoreCase)
            || name.Equals("id_ecdsa", StringComparison.OrdinalIgnoreCase)
            || name.Equals("id_ed25519", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var ext = Path.GetExtension(name);
        return ext.Equals(".pem", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".key", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".p12", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".pfx", StringComparison.OrdinalIgnoreCase);
    }

    public static SandboxMode ParseSandbox(string? s)
    {
        var value = (s ?? "").Trim().ToLowerInvariant().Replace("_", "-");
        return value switch
        {
            "read-only" or "readonly" => SandboxMode.ReadOnly,
            "danger-full-access" or "dangerfullaccess" or "danger-full" or "full-access" => SandboxMode.DangerFullAccess,
            _ => SandboxMode.WorkspaceWrite,
        };
    }
}

public static class SandboxRoots
{
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(CodexPaths.Home, "sandbox-read-roots.json");

    public static IReadOnlyList<string> List()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return Array.Empty<string>();
                return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? [];
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }

    public static string Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            throw new ArgumentException("path must be an existing directory");
        }

        var full = Path.GetFullPath(path);
        lock (Gate)
        {
            CodexPaths.EnsureLayout();
            var list = List().ToList();
            if (!list.Any(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(full);
            }
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
            return full;
        }
    }

    public static bool Allows(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var root in List())
        {
            var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.Equals(normalized, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
