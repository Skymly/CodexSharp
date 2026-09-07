namespace CodexSharp.Runtime;

public static class AgentsMarkdown
{
    public static string PathFor(string cwd) => Path.Combine(cwd, "AGENTS.md");

    public static bool WriteIfMissing(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
            throw new ArgumentException("cwd must exist");
        cwd = GitProbe.FindRoot(cwd) ?? Path.GetFullPath(cwd);
        var path = PathFor(cwd);
        if (File.Exists(path))
            return false;
        File.WriteAllText(path, "# AGENTS.md\n\nProject instructions for CodexSharp (Codex harness recreation).\n");
        return true;
    }
}
