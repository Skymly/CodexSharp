using System.Text.RegularExpressions;

namespace CodexSharp.Runtime;

public static class SkillSlash
{
    private static readonly Regex Token = new(@"\$([A-Za-z0-9][A-Za-z0-9._-]*)", RegexOptions.Compiled);

    public static string? PartialToken(string composer)
    {
        if (string.IsNullOrEmpty(composer))
        {
            return null;
        }

        var i = composer.LastIndexOf('$');
        if (i < 0)
        {
            return null;
        }

        if (i > 0 && !char.IsWhiteSpace(composer[i - 1]))
        {
            return null;
        }

        var rest = composer[(i + 1)..];
        if (rest.IndexOfAny([' ', '\t', '\r', '\n']) >= 0)
        {
            return null;
        }

        return rest;
    }

    public static IReadOnlyList<SkillInfo> Hits(string composer, string home, string cwd)
    {
        var prefix = PartialToken(composer);
        if (prefix is null)
        {
            return [];
        }

        return SkillCatalog.Load(home, cwd)
            .Where(s => SkillConfig.IsEnabled(s.Name, s.Path))
            .Where(s => s.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static string Expand(string text, string home, string cwd)
    {
        if (string.IsNullOrWhiteSpace(text) || text.IndexOf('$') < 0)
        {
            return text;
        }

        var skills = SkillCatalog.Load(home, cwd)
            .Where(s => SkillConfig.IsEnabled(s.Name, s.Path))
            .ToList();
        return Token.Replace(text, match =>
        {
            var name = match.Groups[1].Value;
            var skill = skills.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (skill is null)
            {
                return match.Value;
            }

            var loaded = SkillCatalog.LoadSkill(
                new CodexSharp.Protocol.ToolCallRequest("skill", "load_skill", JsonName(name, skill.Path)),
                home,
                cwd);
            if (loaded.IsError)
            {
                return match.Value;
            }

            return loaded.Output;
        });
    }

    private static string JsonName(string name, string path) =>
        "{\"name\":\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\",\"path\":\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}";
}
