using System.Diagnostics;
using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;

namespace CodexSharp.Runtime;

/// User notify hook from config.toml `notify = ["cmd", "args…"]`.
public static class TurnNotify
{
    public static string[] LoadCommand()
    {
        if (!File.Exists(CodexPaths.ConfigFile))
        {
            return [];
        }

        var table = Toml.ToModel(File.ReadAllText(CodexPaths.ConfigFile));
        if (!table.TryGetValue("notify", out var notify) || notify is not TomlArray arr)
        {
            return [];
        }

        return arr.Select(x => x?.ToString() ?? "").Where(s => s.Length > 0).ToArray();
    }

    public static void Fire(string type, string threadId, string? turnId = null)
    {
        var cmd = LoadCommand();
        if (cmd.Length == 0)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new { type, threadId, turnId });
        try
        {
            var psi = new ProcessStartInfo(cmd[0])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in cmd.Skip(1))
            {
                psi.ArgumentList.Add(arg);
            }

            psi.ArgumentList.Add(payload);
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch
        {
            // notify failures must not break the agent
        }
    }
}
