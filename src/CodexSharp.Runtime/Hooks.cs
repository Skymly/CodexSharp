using System.Diagnostics;
using System.Text.Json;
using CodexSharp.Protocol;

namespace CodexSharp.Runtime;

public sealed record HookInfo(string Name, string Event, string Command, string Path, string Source);

public sealed record HookRunResult(string Name, string Event, bool Ok, string Output);

public static class HookCatalog
{
    public static IReadOnlyList<HookInfo> List(string home, string? cwd = null)
    {
        var list = new List<HookInfo>();
        var userDir = Path.Combine(home, "hooks");
        Directory.CreateDirectory(userDir);
        list.AddRange(ReadDir(userDir, "user"));
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            foreach (var dir in ProjectTrust.ProjectHookDirs(cwd))
            {
                list.AddRange(ReadDir(dir, "project"));
            }
        }

        return list;
    }

    private static IEnumerable<HookInfo> ReadDir(string dir, string source)
    {
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            HookInfo? parsed = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? Path.GetFileNameWithoutExtension(file) : Path.GetFileNameWithoutExtension(file);
                var ev = root.TryGetProperty("event", out var e) ? e.GetString() ?? "Stop" : "Stop";
                var cmd = root.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
                parsed = new HookInfo(name, ev, cmd, file, source);
            }
            catch
            {
                // skip invalid hook files
            }

            if (parsed is not null)
            {
                yield return parsed;
            }
        }
    }
}

public static class HookRunner
{
    public static IReadOnlyList<HookRunResult> Run(string home, string eventName, object payload, string? cwd = null)
    {
        var json = JsonSerializer.Serialize(payload);
        var results = new List<HookRunResult>();
        foreach (var hook in HookCatalog.List(home, cwd))
        {
            if (!hook.Event.Equals(eventName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            results.Add(Execute(hook, json));
        }

        return results;
    }

    private static HookRunResult Execute(HookInfo hook, string json)
    {
        if (string.IsNullOrWhiteSpace(hook.Command))
        {
            return new HookRunResult(hook.Name, hook.Event, false, "empty command");
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
            psi.ArgumentList.Add(hook.Command);
            using var process = Process.Start(psi);
            if (process is null)
            {
                return new HookRunResult(hook.Name, hook.Event, false, "failed to start");
            }

            process.StandardInput.WriteLine(json);
            process.StandardInput.Close();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(true); } catch { /* ignore */ }
                return new HookRunResult(hook.Name, hook.Event, false, "timeout");
            }

            var output = (process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd()).Trim();
            return new HookRunResult(hook.Name, hook.Event, process.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            return new HookRunResult(hook.Name, hook.Event, false, ex.Message);
        }
    }
}

public sealed class ConfigHookHost(string home, string? cwd = null) : IHookHost
{
    public HookDecision Fire(string eventName, string tool, string argumentsJson)
    {
        var results = HookRunner.Run(home, eventName, new { type = eventName, tool, arguments = argumentsJson }, cwd);
        foreach (var result in results)
        {
            var output = result.Output ?? "";
            if (output.Contains("deny", StringComparison.OrdinalIgnoreCase)
                || output.Contains("block", StringComparison.OrdinalIgnoreCase))
            {
                return HookDecision.NewHookBlock(output);
            }
        }

        return HookDecision.HookContinue;
    }
}
