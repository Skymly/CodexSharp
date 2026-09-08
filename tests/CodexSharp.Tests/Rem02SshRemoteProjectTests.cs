using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public class Rem02SshRemoteProjectTests
{
    [Fact]
    public void Concrete_hosts_ignore_pure_patterns()
    {
        var text = """
            Host github.com
                HostName github.com
            Host *
            Host *.example.com
            Host !exclude
            Host box other
            Host mixed *.wild
            # Host commented
            """;
        var hosts = SshConfig.ConcreteHosts(text);
        Assert.Equal(new[] { "github.com", "box", "other", "mixed" }, hosts);
        Assert.DoesNotContain("*", hosts);
        Assert.DoesNotContain("*.example.com", hosts);
        Assert.DoesNotContain("!exclude", hosts);
        Assert.DoesNotContain("commented", hosts);
        Assert.True(SshConfig.IsPattern("*"));
        Assert.True(SshConfig.IsPattern("*.example.com"));
        Assert.True(SshConfig.IsPattern("!exclude"));
        Assert.False(SshConfig.IsPattern("github.com"));
    }

    [Fact]
    public void Missing_config_file_is_empty()
    {
        var path = Path.Combine(Path.GetTempPath(), "codexsharp-missing-ssh-" + Guid.NewGuid().ToString("N"), "config");
        Assert.Empty(SshConfig.ConcreteHostsFromFile(path));
    }

    [Fact]
    public void File_parser_reads_fixture_hosts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codexsharp-ssh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config");
        File.WriteAllText(path, "Host alpha\nHost *\nHost beta\n");
        Assert.Equal(new[] { "alpha", "beta" }, SshConfig.ConcreteHostsFromFile(path));
    }

    [Fact]
    public void Persist_host_and_remote_folder_in_metadata()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            var saved = SshRemoteProjects.Persist("lab", "/home/me/code");
            Assert.Equal("lab", saved.Metadata["sshHost"]);
            Assert.Equal("/home/me/code", saved.Metadata["sshRemoteFolder"]);
            Assert.Equal("underDevelopment", saved.Metadata["sshTunnel"]);
            Assert.Empty(saved.Roots);
            var listed = Assert.Single(SshRemoteProjects.List());
            Assert.Equal(saved.Id, listed.ProjectId);
            Assert.Equal("lab", listed.Host);
            Assert.Equal("/home/me/code", listed.RemoteFolder);
            Assert.Equal("underDevelopment", listed.TunnelStatus);
            var round = ProjectStore.Read(saved.Id);
            Assert.NotNull(round);
            Assert.Equal("lab", round!.Metadata["sshHost"]);
            Assert.Empty(round.Roots);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public void Pattern_host_cannot_persist()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexsharp-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXSHARP_HOME", home);
        try
        {
            Directory.CreateDirectory(home);
            Assert.Throws<ArgumentException>(() => SshRemoteProjects.Persist("*", "/tmp"));
            Assert.Throws<ArgumentException>(() => SshRemoteProjects.Persist("lab", "  "));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSHARP_HOME", null);
        }
    }

    [Fact]
    public void Catalog_ssh_remote_is_underDevelopment_not_paired()
    {
        var ssh = Assert.Single(HonestStubs.Capabilities, c => c.Id == "ssh_remote");
        Assert.Equal("SSH remote project", ssh.Label);
        Assert.Equal("underDevelopment", ssh.Status);
        Assert.Equal("underDevelopment", HonestStubs.StatusOf("ssh_remote"));
        Assert.DoesNotContain("Enable", ssh.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("已配对", ssh.Label);
        Assert.DoesNotContain("已配对", ssh.Status);
        Assert.DoesNotContain("已配对", ssh.Detail);
        Assert.DoesNotContain("connected", ssh.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("notConfigured", HonestStubs.StatusOf("remote"));
        var remote = Assert.Single(HonestStubs.Capabilities, c => c.Id == "remote");
        Assert.Equal("notConfigured", remote.Status);
        Assert.Contains("transport", remote.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.All(HonestStubs.Capabilities, c =>
        {
            Assert.DoesNotContain("已配对", c.Label);
            Assert.DoesNotContain("已配对", c.Status);
            Assert.DoesNotContain("已配对", c.Detail);
        });
    }
}
