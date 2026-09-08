namespace CodexSharp.Runtime;

public sealed record SshRemoteProjectEntry(string ProjectId, string Host, string RemoteFolder, string TunnelStatus);

/// Persist SSH remote project entries as ProjectStore metadata. No live tunnel.
public static class SshRemoteProjects
{
    public const string HostKey = "sshHost";
    public const string FolderKey = "sshRemoteFolder";
    public const string TunnelKey = "sshTunnel";
    public const string UnderDevelopment = "underDevelopment";

    public static ProjectInfo Persist(string host, string remoteFolder)
    {
        if (string.IsNullOrWhiteSpace(host) || SshConfig.IsPattern(host))
        {
            throw new ArgumentException("host must be a concrete ssh config Host", nameof(host));
        }

        if (string.IsNullOrWhiteSpace(remoteFolder))
        {
            throw new ArgumentException("remoteFolder is required", nameof(remoteFolder));
        }

        var h = host.Trim();
        var folder = remoteFolder.Trim();
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [HostKey] = h,
            [FolderKey] = folder,
            [TunnelKey] = UnderDevelopment,
        };
        return ProjectStore.Create(h, [], meta, "ssh-remote:" + h + ":" + folder).Project;
    }

    public static IReadOnlyList<SshRemoteProjectEntry> List()
    {
        var list = new List<SshRemoteProjectEntry>();
        foreach (var project in ProjectStore.List())
        {
            if (!project.Metadata.TryGetValue(HostKey, out var host) || string.IsNullOrWhiteSpace(host))
            {
                continue;
            }

            project.Metadata.TryGetValue(FolderKey, out var folder);
            project.Metadata.TryGetValue(TunnelKey, out var tunnel);
            list.Add(new SshRemoteProjectEntry(
                project.Id,
                host,
                folder ?? "",
                string.IsNullOrWhiteSpace(tunnel) ? UnderDevelopment : tunnel));
        }

        return list;
    }
}
