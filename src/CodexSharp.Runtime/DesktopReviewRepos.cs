namespace CodexSharp.Runtime;

/// Desktop-local review repo switch (GIT-06). GitProbe stays cwd-shaped.
public sealed record ReviewRepo(string Label, string Cwd, bool Primary);

public static class DesktopReviewRepos
{
    public static IReadOnlyList<ReviewRepo> FromProject(string? primaryRoot, IEnumerable<string>? extraRoots)
    {
        var list = new List<ReviewRepo>();
        TryAdd(list, primaryRoot, primary: true);
        if (extraRoots is not null)
        {
            foreach (var extra in extraRoots)
            {
                TryAdd(list, extra, primary: false);
            }
        }

        return list;
    }

    public static string DefaultCwd(IReadOnlyList<ReviewRepo> repos) =>
        repos.FirstOrDefault(r => r.Primary)?.Cwd
        ?? repos.FirstOrDefault()?.Cwd
        ?? "";

    public static string LabelFor(IReadOnlyList<ReviewRepo> repos, string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return repos.FirstOrDefault(r => r.Primary)?.Label ?? "(none)";
        }

        return repos.FirstOrDefault(r => r.Cwd.Equals(Path.GetFullPath(cwd), StringComparison.OrdinalIgnoreCase))?.Label
            ?? Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            ?? "(none)";
    }

    private static void TryAdd(List<ReviewRepo> list, string? path, bool primary)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            path = Path.GetFullPath(path);
        }
        catch
        {
            return;
        }

        if (!Directory.Exists(path))
        {
            return;
        }

        var root = GitProbe.FindRoot(path);
        if (root is null)
        {
            return;
        }

        if (list.Any(r => r.Cwd.Equals(root, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name))
        {
            name = root;
        }

        list.Add(new ReviewRepo(name, root, primary));
    }
}
