using System.Diagnostics;
using System.Text.Json;

namespace CodexSharp.Runtime;

public sealed record WindowsSandboxSnapshot(string Status, bool JobObject, string? Mode, string? Error);

public static class WindowsSandbox
{
    public const int SetupVersion = 1;
    public const string LimitedStatus = "limited";

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private static string StatePath => Path.Combine(CodexPaths.Home, "windows-sandbox.json");

    public static WindowsSandboxSnapshot Read()
    {
        lock (Gate)
        {
            var job = JobObject.IsAvailable();
            try
            {
                if (File.Exists(StatePath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
                    var root = doc.RootElement;
                    var completed = root.TryGetProperty("completed", out var c) && c.ValueKind == JsonValueKind.True;
                    var mode = root.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                    if (completed && job)
                    {
                        return new WindowsSandboxSnapshot(LimitedStatus, true, mode, null);
                    }
                }
            }
            catch { /* ignore */ }

            return new WindowsSandboxSnapshot("notConfigured", job, null, job ? null : "Job Object is unavailable");
        }
    }

    public static WindowsSandboxSnapshot Setup(string mode, string? cwd)
    {
        if (string.Equals(mode, "elevated", StringComparison.OrdinalIgnoreCase))
        {
            return new WindowsSandboxSnapshot("notConfigured", JobObject.IsAvailable(), "elevated",
                "Official Windows sandbox service / elevated helper is not shipped in CodexSharp.");
        }

        if (!string.Equals(mode, "unelevated", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("mode must be elevated or unelevated", nameof(mode));
        }

        if (!JobObject.IsAvailable())
        {
            return new WindowsSandboxSnapshot("notConfigured", false, "unelevated", "Job Object is unavailable on this host.");
        }

        lock (Gate)
        {
            CodexPaths.EnsureLayout();
            var root = Path.Combine(CodexPaths.Home, "sandbox");
            Directory.CreateDirectory(root);
            if (!string.IsNullOrWhiteSpace(cwd))
            {
                Directory.CreateDirectory(Path.Combine(root, "workspaces"));
            }

            var state = new
            {
                version = SetupVersion,
                completed = true,
                mode = "unelevated",
                cwd,
                jobObject = true,
                completedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                implementation = "jobObject-kill-on-close",
            };
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state, Json));
            return Read();
        }
    }

    public static string Describe(WindowsSandboxSnapshot? snap = null)
    {
        snap ??= Read();
        if (string.Equals(snap.Mode, "elevated", StringComparison.OrdinalIgnoreCase))
        {
            return "elevated windowsSandbox is notConfigured. CodexSharp does not ship an official Windows sandbox helper.";
        }

        if (snap.Status == LimitedStatus)
        {
            return NoteForStatus(snap.Status);
        }

        return NoteForStatus(snap.Status);
    }

    public static string NoteForStatus(string? status)
    {
        if (string.Equals(status, LimitedStatus, StringComparison.Ordinal))
        {
            return "unelevated: Job Object kill-on-close + workspace-write path policy. This is not an OS sandbox.";
        }

        return "windowsSandbox notConfigured. unelevated only records Job Object kill-on-close + workspace-write path policy; elevated stays notConfigured. This is not an OS sandbox.";
    }
}

public sealed record WorldWritableReport(IReadOnlyList<string> SamplePaths, int ExtraCount, bool FailedScan);

public static class WorldWritableScan
{
    public static WorldWritableReport Scan(string root, int limit = 40)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return new WorldWritableReport([], 0, false);
        }

        var samples = new List<string>();
        var extra = 0;
        var failed = false;
        try
        {
            var paths = new List<string> { Path.GetFullPath(root) };
            try
            {
                paths.AddRange(Directory.EnumerateDirectories(root).Take(limit));
            }
            catch { failed = true; }

            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase).Take(limit))
            {
                if (!LooksWorldWritable(path)) continue;
                if (samples.Count < 8) samples.Add(path);
                else extra++;
            }
        }
        catch
        {
            failed = true;
        }

        return new WorldWritableReport(samples, extra, failed);
    }

    private static bool LooksWorldWritable(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("icacls")
            {
                Arguments = "\"" + path.Replace("\"", "\\\"") + "\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null || !p.WaitForExit(1500))
            {
                try { p?.Kill(true); } catch { /* ignore */ }
                return false;
            }
            var text = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).ToUpperInvariant();
            if (!text.Contains("EVERYONE") && !text.Contains("BUILTIN\\USERS") && !text.Contains("S-1-1-0"))
            {
                return false;
            }
            return text.Contains("(F)") || text.Contains("(M)") || text.Contains("(W)") || text.Contains("(WD)");
        }
        catch
        {
            return false;
        }
    }
}
