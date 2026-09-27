using System.Text.Json;
using CodexSharp.Core;
using CodexSharp.Protocol;
using Tomlyn;
using Tomlyn.Model;

namespace CodexSharp.Runtime;

public sealed record ProjectAction(string Label, string Command);

public sealed record ProjectActionList(IReadOnlyList<ProjectAction> Buttons, bool SuccessToast)
{
    public static ProjectActionList Hidden { get; } = new([], false);
}

public sealed record ProjectActionClick(
    bool Started,
    bool Success,
    string TerminalText,
    string ProcessId,
    IReadOnlyList<string> Argv);

/// Project `.codexsharp/actions.toml` buttons for the existing terminal. Not a terminal host.
public static class ProjectActions
{
    public static ProjectActionList Load(string? projectRoot)
    {
        if (!TryActionsFile(projectRoot, out var path))
        {
            return ProjectActionList.Hidden;
        }

        try
        {
            var table = Toml.ToModel(File.ReadAllText(path));
            if (!table.TryGetValue("action", out var raw) || raw is not TomlTableArray rows)
            {
                return ProjectActionList.Hidden;
            }

            var buttons = new List<ProjectAction>();
            foreach (var item in rows)
            {

                var label = Read(item, "label");
                var command = Read(item, "command");
                if (label.Length == 0 || command.Length == 0)
                {
                    continue;
                }

                buttons.Add(new ProjectAction(label, command));
            }

            return new ProjectActionList(buttons, false);
        }
        catch
        {
            return ProjectActionList.Hidden;
        }
    }

    public static ProjectActionClick Click(
        DesktopTerminalTabs tabs,
        string? projectRoot,
        string? workdir,
        CodexConfig? config,
        string? command,
        Action<string, IReadOnlyList<string>>? start)
    {
        var pid = tabs.Active.Id;
        command = (command ?? "").Trim();
        if (command.Length == 0 || !Listed(projectRoot, command))
        {
            return NotStarted(pid, "action was not started", command);
        }

        if (!TryResolve(projectRoot, out var projectFull)
            || !TryResolve(workdir, out var workFull)
            || !WorkspaceSandbox.IsInside(workFull, projectFull)
            || IsSecret(workFull, config)
            || IsSecret(ActionsPath(projectFull), config))
        {
            return NotStarted(pid, "path rejected; action was not started", command);
        }

        var call = new ToolCallRequest("action", "shell", JsonSerializer.Serialize(new { command }));
        if (config is null || ToolApproval.needsApproval(config, call))
        {
            return NotStarted(pid, "approval required; action was not started", command);
        }

        var argv = tabs.Active.ExecArgv(command);
        if (start is null)
        {
            return NotStarted(pid, "action was not started", command);
        }

        start(pid, argv);
        return new ProjectActionClick(true, false, "", pid, argv);
    }

    private static ProjectActionClick NotStarted(string pid, string text, string? command)
    {
        var body = text;
        if (!string.IsNullOrWhiteSpace(command))
        {
            body += Environment.NewLine + command.Trim();
        }

        return new ProjectActionClick(false, false, body + Environment.NewLine, pid, []);
    }

    private static bool Listed(string? projectRoot, string command)
    {
        foreach (var action in Load(projectRoot).Buttons)
        {
            if (string.Equals(action.Command, command, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string ActionsPath(string projectFull) =>
        Path.GetFullPath(Path.Combine(projectFull, ".codexsharp", "actions.toml"));

    private static bool TryActionsFile(string? projectRoot, out string path)
    {
        path = "";
        if (!TryResolve(projectRoot, out var projectFull) || !Directory.Exists(projectFull))
        {
            return false;
        }

        var candidate = ActionsPath(projectFull);
        if (!File.Exists(candidate)
            || !WorkspaceSandbox.IsInside(candidate, projectFull)
            || IsSecret(candidate, null))
        {
            return false;
        }

        path = candidate;
        return true;
    }

    private static bool IsSecret(string path, CodexConfig? config)
    {
        var home = config?.Home;
        if (string.IsNullOrWhiteSpace(home))
        {
            home = CodexPaths.Home;
        }

        return WorkspaceSandbox.IsHomeSecret(path, home);
    }

    private static bool TryResolve(string? path, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        full = Path.GetFullPath(path);
        return true;
    }

    private static string Read(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out var value) || value is not string text)
        {
            return "";
        }

        return text.Trim();
    }
}
