using Nuke.Common;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

[GitHubActions(
    "ci",
    GitHubActionsImage.WindowsLatest,
    OnPushBranches = new[] { "master" },
    OnPullRequestBranches = new[] { "master" },
    InvokedTargets = new[] { nameof(Test) })]
class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Test);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    AbsolutePath SolutionFile => RootDirectory / "CodexSharp.slnx";

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            foreach (var dir in RootDirectory.GlobDirectories("**/bin", "**/obj"))
            {
                var path = dir.ToString().Replace('\\', '/');
                if (path.Contains("/build/"))
                {
                    continue;
                }

                dir.DeleteDirectory();
            }
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(s => s.SetProjectFile(SolutionFile));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(SolutionFile)
                .SetConfiguration(Configuration)
                .EnableNoRestore());
        });

    Target Test => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTest(s => s
                .SetProjectFile(SolutionFile)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore());
        });
}
