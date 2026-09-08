using System.Text.Json;
using CodexSharp.Runtime;

namespace CodexSharp.Tests;

public class GhPrCommentsTests
{
    [Fact]
    public void Missing_or_logged_out_gh_explains_why()
    {
        var missing = GhPrComments.Probe(Path.GetTempPath(), (_, _) => null);
        Assert.False(missing.GhInstalled);
        Assert.Equal("gh is not installed", missing.Reason);
        Assert.Empty(missing.Comments);

        var loggedOut = GhPrComments.Probe(Path.GetTempPath(), (_, args) =>
            args.Contains("auth", StringComparison.Ordinal)
                ? new CommandResult(1, "", "You are not logged into any GitHub hosts")
                : new CommandResult(0, "gh version 2", ""));
        Assert.True(loggedOut.GhInstalled);
        Assert.False(loggedOut.Authenticated);
        Assert.Equal("gh is not logged in", loggedOut.Reason);
        Assert.Empty(loggedOut.Comments);

        var noPr = GhPrComments.Probe(Path.GetTempPath(), (_, args) =>
            args.Contains("pr view", StringComparison.Ordinal)
                ? new CommandResult(1, "", "no pull requests found for branch \"feat\"")
                : new CommandResult(0, "ok", ""));
        Assert.True(noPr.Authenticated);
        Assert.Contains("no pull requests found", noPr.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(noPr.Comments);
    }

    [Fact]
    public void Parses_pr_issue_and_review_comments()
    {
        var json = """
            {
              "number": 6,
              "title": "Sidebar",
              "url": "https://example.invalid/pr/6",
              "comments": [{"author":{"login":"alice"},"body":"please ship"}],
              "reviews": [{"author":{"login":"bob"},"body":"lgtm","path":"src/A.cs","line":4}]
            }
            """;
        var status = GhPrComments.ParseView(json);
        Assert.Equal("https://example.invalid/pr/6", status.Url);
        Assert.Equal("", status.Reason);
        Assert.Contains(status.Comments, c => c.Author == "alice" && c.Body == "please ship");
        Assert.Contains(status.Comments, c => c.Author == "bob" && c.Path == "src/A.cs" && c.Line == "4");
    }

    [Fact]
    public void Review_start_target_selects_uncommitted_or_base_branch()
    {
        var uncommitted = ReviewPrompts.FromParams(JsonDocument.Parse("""{"target":{"type":"uncommittedChanges"}}""").RootElement, Path.GetTempPath());
        Assert.Contains("working tree", uncommitted, StringComparison.OrdinalIgnoreCase);

        var dir = Path.Combine(Path.GetTempPath(), "codexsharp-norepo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var vsMain = ReviewPrompts.FromParams(JsonDocument.Parse("""{"target":{"type":"baseBranch","branch":"develop"}}""").RootElement, dir);
            Assert.Contains("develop", vsMain, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("base branch", vsMain, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
