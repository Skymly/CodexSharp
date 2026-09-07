namespace CodexSharp.Runtime;

public sealed record SharedCliOptions(
    string? Cwd,
    string? Model,
    string? Sandbox,
    string? Approval,
    string? Profile,
    bool Json,
    bool SkipGit,
    bool Ephemeral,
    bool Last,
    string? LastMessagePath,
    string? SchemaPath,
    string? Color,
    IReadOnlyList<string> Images,
    bool Worktree,
    bool IgnoreUserConfig,
    bool IgnoreRules,
    bool StrictConfig,
    IReadOnlyList<string> AddDirs,
    string[] Rest);

public static class SharedCli
{
    public static SharedCliOptions Parse(string[] args)
    {
        string? cwd = null, model = null, sandbox = null, approval = null, profile = null;
        string? lastMessagePath = null, schemaPath = null, color = null;
        var json = false;
        var skipGit = false;
        var ephemeral = false;
        var last = false;
        var worktree = false;
        var ignoreUserConfig = false;
        var ignoreRules = false;
        var strictConfig = false;
        var addDirs = new List<string>();
        var images = new List<string>();
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model": model = args[++i]; break;
                case "--sandbox": sandbox = args[++i]; break;
                case "--full-auto":
                    sandbox ??= "workspace-write";
                    approval = "never";
                    break;
                case "--yolo":
                    sandbox = "danger-full-access";
                    approval = "never";
                    break;
                case "--profile": profile = args[++i]; break;
                case "--json": json = true; break;
                case "--output-last-message" or "-o": lastMessagePath = args[++i]; break;
                case "--cd" or "-C": cwd = args[++i]; break;
                case "--image" or "-i": images.Add(args[++i]); break;
                case "--output-schema": schemaPath = args[++i]; break;
                case "--skip-git-repo-check": skipGit = true; break;
                case "--ephemeral": ephemeral = true; break;
                case "--color": color = args[++i]; break;
                case "--last" or "-l": last = true; break;
                case "--worktree": worktree = true; break;
                case "--ignore-user-config": ignoreUserConfig = true; break;
                case "--ignore-rules": ignoreRules = true; break;
                case "--strict-config": strictConfig = true; break;
                case "--add-dir": addDirs.Add(args[++i]); break;
                default:
                    rest.Add(args[i]);
                    break;
            }
        }

        return new SharedCliOptions(cwd, model, sandbox, approval, profile, json, skipGit, ephemeral, last, lastMessagePath, schemaPath, color, images, worktree, ignoreUserConfig, ignoreRules, strictConfig, addDirs, rest.ToArray());
    }
}
