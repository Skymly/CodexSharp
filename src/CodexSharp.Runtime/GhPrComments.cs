using System.Diagnostics;
using System.Text.Json;

namespace CodexSharp.Runtime;

public sealed record GhPrComment(string Author, string Body, string Path, string Line);

public sealed record GhPrStatus(
    bool GhInstalled,
    bool Authenticated,
    string Reason,
    string? Url,
    IReadOnlyList<GhPrComment> Comments);

public readonly record struct CommandResult(int ExitCode, string Stdout, string Stderr);

/// Local gh PR comments (GIT-05). Never talks to ChatGPT GitHub cloud.
public static class GhPrComments
{
    public static GhPrStatus Probe(string? cwd, Func<string, string, CommandResult?>? run = null)
    {
        CommandResult? Exec(string args)
        {
            if (run is not null)
            {
                return run("gh", args);
            }

            return ExecGh(args, cwd);
        }

        var version = Exec("--version");
        if (version is null)
        {
            return Missing("gh is not installed");
        }

        var auth = Exec("auth status");
        if (auth is null || auth.Value.ExitCode != 0)
        {
            return new GhPrStatus(true, false, "gh is not logged in", null, []);
        }

        var view = Exec("pr view --json number,title,url,comments,reviews");
        if (view is null || view.Value.ExitCode != 0)
        {
            var detail = (view?.Stderr ?? view?.Stdout ?? "").Trim();
            var reason = string.IsNullOrWhiteSpace(detail)
                ? "no pull request for the current branch"
                : TrimReason(detail);
            return new GhPrStatus(true, true, reason, null, []);
        }

        return ParseView(view.Value.Stdout);
    }

    public static GhPrStatus ParseView(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = doc.RootElement;
            var url = root.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String
                ? urlEl.GetString()
                : null;
            var comments = new List<GhPrComment>();
            AddComments(root, "comments", comments);
            AddComments(root, "reviews", comments);
            return new GhPrStatus(true, true, "", url, comments);
        }
        catch (Exception)
        {
            return new GhPrStatus(true, true, "gh returned invalid PR JSON", null, []);
        }
    }

    private static void AddComments(JsonElement root, string name, List<GhPrComment> sink)
    {
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var el in arr.EnumerateArray())
        {
            var body = Str(el, "body");
            if (string.IsNullOrWhiteSpace(body))
            {
                continue;
            }

            var author = "unknown";
            if (el.TryGetProperty("author", out var authorEl))
            {
                author = authorEl.ValueKind == JsonValueKind.Object
                    ? Str(authorEl, "login")
                    : authorEl.ValueKind == JsonValueKind.String ? authorEl.GetString() ?? "unknown" : "unknown";
            }

            var path = Str(el, "path");
            var line = el.TryGetProperty("line", out var lineEl) && lineEl.ValueKind == JsonValueKind.Number
                ? lineEl.GetInt32().ToString()
                : "";
            sink.Add(new GhPrComment(author, body.Trim(), path, line));
        }
    }

    private static string Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static GhPrStatus Missing(string reason) =>
        new(false, false, reason, null, []);

    private static string TrimReason(string text)
    {
        var line = text.Replace('\r', '\n').Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? text;
        return line.Length > 180 ? line[..180] : line;
    }

    private static CommandResult? ExecGh(string args, string? cwd)
    {
        try
        {
            var psi = new ProcessStartInfo("gh")
            {
                Arguments = args,
                WorkingDirectory = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null || !proc.WaitForExit(8000))
            {
                try { proc?.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            return new CommandResult(proc.ExitCode, proc.StandardOutput.ReadToEnd(), proc.StandardError.ReadToEnd());
        }
        catch (Exception)
        {
            return null;
        }
    }
}
