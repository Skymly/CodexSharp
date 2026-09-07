namespace CodexSharp.Runtime;

public sealed record PromptInfo(string Name, string Path, string Preview);

public static class PromptCatalog
{
    public static IReadOnlyList<PromptInfo> List(string home, string cwd)
    {
        var roots = new[]
        {
            Path.Combine(home, "prompts"),
            Path.Combine(cwd, ".codexsharp", "prompts"),
            Path.Combine(cwd, ".codex", "prompts"),
        };
        var list = new List<PromptInfo>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.md"))
            {
                try
                {
                    var text = File.ReadAllText(file);
                    var preview = text.Length <= 240 ? text : text[..240] + "…";
                    list.Add(new PromptInfo(Path.GetFileNameWithoutExtension(file), file, preview));
                }
                catch { /* skip unreadable */ }
            }
        }
        return list;
    }

    public static string? Read(string name, string home, string cwd) =>
        List(home, cwd).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Path is { } path
            ? File.ReadAllText(path)
            : null;
}
