using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexSharp.Core;
using CodexSharp.Protocol;

namespace CodexSharp.Runtime;

/// Prefix exec policy inspired by vendor/codex/codex-rs/execpolicy.
public enum ExecDecision
{
    Allow,
    Prompt,
    Forbidden,
}

public sealed record ExecRule(string[] Pattern, ExecDecision Decision, string? Justification);

public static class ExecPolicy
{
    public static readonly AsyncLocal<bool> SuppressRules = new();

    public static IReadOnlyList<ExecRule> Load(string? cwd = null)
    {
        if (SuppressRules.Value)
        {
            return [];
        }

        var rules = new List<ExecRule>();
        LoadDir(Path.Combine(CodexPaths.Home, "rules"), rules);
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            foreach (var dir in ProjectTrust.ProjectRuleDirs(cwd))
            {
                LoadDir(dir, rules);
            }
        }

        return rules;
    }

    private static void LoadDir(string dir, List<ExecRule> rules)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*.rules"))
        {
            foreach (var line in File.ReadLines(file))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith("//"))
                {
                    continue;
                }

                var parsed = ParseLine(trimmed);
                if (parsed is not null)
                {
                    rules.Add(parsed);
                }
            }
        }
    }

    public static string AddUserRule(IReadOnlyList<string> pattern, ExecDecision decision)
    {
        if (pattern.Count == 0) throw new ArgumentException("pattern is required");
        CodexPaths.EnsureLayout();
        var dir = Path.Combine(CodexPaths.Home, "rules");
        Directory.CreateDirectory(dir);
        var tokens = string.Join(", ", pattern.Select(p => "\"" + p.Replace("\"", "") + "\""));
        var line = "prefix_rule(pattern=[" + tokens + "], decision=\"" + decision.ToString().ToLowerInvariant() + "\")";
        File.AppendAllText(Path.Combine(dir, "user.rules"), line + Environment.NewLine);
        return line;
    }

    public static ExecDecision Evaluate(string command, string? cwd = null)
    {
        var tokens = Tokenize(command);
        var decision = ExecDecision.Allow;
        foreach (var rule in Load(cwd))
        {
            if (!MatchesPrefix(tokens, rule.Pattern))
            {
                continue;
            }

            if (rule.Decision == ExecDecision.Forbidden)
            {
                return ExecDecision.Forbidden;
            }

            if (rule.Decision == ExecDecision.Prompt)
            {
                decision = ExecDecision.Prompt;
            }
        }

        return decision;
    }

    public static ExecRule? ParseLine(string line)
    {
        // prefix_rule(pattern=["gh","pr"], decision="prompt")
        var match = Regex.Match(line, """prefix_rule\s*\(\s*pattern\s*=\s*\[([^\]]+)\]\s*,\s*decision\s*=\s*"?(allow|prompt|forbidden)"?""", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var pattern = match.Groups[1].Value
            .Split(',')
            .Select(p => p.Trim().Trim('"', '\''))
            .Where(p => p.Length > 0)
            .ToArray();
        var decision = match.Groups[2].Value.ToLowerInvariant() switch
        {
            "forbidden" => ExecDecision.Forbidden,
            "prompt" => ExecDecision.Prompt,
            _ => ExecDecision.Allow,
        };
        return new ExecRule(pattern, decision, null);
    }

    private static bool MatchesPrefix(string[] tokens, string[] pattern)
    {
        if (tokens.Length < pattern.Length)
        {
            return false;
        }

        for (var i = 0; i < pattern.Length; i++)
        {
            if (!tokens[i].Equals(pattern[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string[] Tokenize(string command) =>
        command.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
}

internal sealed class ExecPolicyPromptProbe : IExecPolicyPrompt
{
    public bool IsPrompt(CodexConfig cfg, ToolCallRequest call)
    {
        if (call.Name is not ("shell" or "exec_command"))
        {
            return false;
        }

        return ExecPolicy.Evaluate(ReadCommand(call.ArgumentsJson), cfg.Cwd) == ExecDecision.Prompt;
    }

    private static string ReadCommand(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = doc.RootElement;
            foreach (var name in new[] { "command", "cmd" })
            {
                if (!root.TryGetProperty(name, out var value))
                {
                    continue;
                }

                if (value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString() ?? "";
                }

                if (value.ValueKind == JsonValueKind.Array)
                {
                    return string.Join(' ', value.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)));
                }
            }
        }
        catch
        {
            return "";
        }

        return "";
    }
}

internal static class ExecPolicyPromptRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register()
    {
        ToolApproval.setProbe(new ExecPolicyPromptProbe());
    }
}
