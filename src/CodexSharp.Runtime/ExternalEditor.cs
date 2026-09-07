using System.Diagnostics;
using System.Text;
using CodexSharp.Protocol;

namespace CodexSharp.Runtime;

/// Open VISUAL/EDITOR against a draft, matching vendor/codex/codex-rs/tui/src/external_editor.rs.
/// CodexSharp does not fall back to notepad; missing VISUAL/EDITOR is an error.
public static class ExternalEditor
{
    public static string DirectoryPath => Path.Combine(CodexPaths.Home, "editor");

    public static IReadOnlyList<string> ResolveCommand()
    {
        var raw = Environment.GetEnvironmentVariable("VISUAL");
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = Environment.GetEnvironmentVariable("EDITOR");
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("neither VISUAL nor EDITOR is set");
        }

        var parts = SplitCommand(raw);
        if (parts.Count == 0)
        {
            throw new InvalidOperationException("editor command is empty");
        }

        return parts;
    }

    public static string Edit(string draft)
    {
        CodexPaths.EnsureLayout();
        Directory.CreateDirectory(DirectoryPath);
        var file = Path.Combine(DirectoryPath, "draft-" + Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(file, draft ?? "");

        var cmd = ResolveCommand();
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
        };
        var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        psi.CreateNoWindow = !interactive;
        if (!interactive)
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }

        var exe = cmd[0];
        var start = 1;
        if (OperatingSystem.IsWindows() &&
            (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(exe);
        }
        else
        {
            psi.FileName = exe;
        }

        for (var i = start; i < cmd.Count; i++)
        {
            psi.ArgumentList.Add(cmd[i]);
        }

        psi.ArgumentList.Add(file);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start editor");
        process.WaitForExit();
        return File.ReadAllText(file);
    }

    internal static List<string> SplitCommand(string raw)
    {
        var parts = new List<string>();
        var cur = new StringBuilder();
        var quote = '\0';
        foreach (var ch in raw)
        {
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                else cur.Append(ch);
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (cur.Length > 0)
                {
                    parts.Add(cur.ToString());
                    cur.Clear();
                }

                continue;
            }

            cur.Append(ch);
        }

        if (cur.Length > 0) parts.Add(cur.ToString());
        return parts;
    }
}
