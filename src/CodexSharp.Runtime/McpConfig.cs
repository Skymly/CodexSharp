using System.Text.RegularExpressions;
namespace CodexSharp.Runtime;

/// Persist `[mcp_servers.*]` tables in config.toml, matching `codex mcp add|remove`.
public static class McpConfig
{
    public static McpServerSpec? Get(string name) =>
        ConfigService.LoadMcpServers().FirstOrDefault(s => s.Id.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static void AddStdio(string name, IReadOnlyList<string> commandAndArgs)
    {
        ValidateName(name);
        if (commandAndArgs.Count == 0 || string.IsNullOrWhiteSpace(commandAndArgs[0]))
        {
            throw new ArgumentException("stdio MCP servers need a command");
        }

        Upsert(name, commandAndArgs[0], commandAndArgs.Skip(1).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray(), null, null);
    }

    public static void AddHttp(string name, string url, string? bearerTokenEnvVar)
    {
        ValidateName(name);
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("url is required");
        }

        Upsert(name, "", [], url.Trim(), string.IsNullOrWhiteSpace(bearerTokenEnvVar) ? null : bearerTokenEnvVar.Trim());
    }

    public static bool Remove(string name)
    {
        ValidateName(name);
        CodexPaths.EnsureLayout();
        var text = CodexPaths.ReadConfigText();
        var next = StripTable(text, name);
        if (next == text)
        {
            return false;
        }

        CodexPaths.WriteConfigText(next.TrimEnd() + Environment.NewLine);
        return true;
    }

    private static void Upsert(string name, string command, string[] args, string? url, string? bearer)
    {
        CodexPaths.EnsureLayout();
        var text = CodexPaths.ReadConfigText();
        text = StripTable(text, name).TrimEnd() + Environment.NewLine + Environment.NewLine;
        var block = new System.Text.StringBuilder();
        block.Append("[mcp_servers.").Append(name).Append(']').AppendLine();
        if (!string.IsNullOrWhiteSpace(url))
        {
            block.Append("url = \"").Append(url.Replace("\"", "\\\"")).Append('"').AppendLine();
            if (!string.IsNullOrWhiteSpace(bearer))
            {
                block.Append("bearer_token_env_var = \"").Append(bearer.Replace("\"", "\\\"")).Append('"').AppendLine();
            }
        }
        else
        {
            block.Append("command = \"").Append(command.Replace("\"", "\\\"")).Append('"').AppendLine();
            if (args.Length > 0)
            {
                block.Append("args = [").Append(string.Join(", ", args.Select(a => "\"" + a.Replace("\"", "\\\"") + "\""))).Append(']').AppendLine();
            }
        }

        CodexPaths.WriteConfigText(text + block);
    }

    private static string StripTable(string text, string name)
    {
        var rx = new Regex(@"^\[mcp_servers\." + Regex.Escape(name) + @"\][^\n]*\n(?:(?!^\[).*\n?)*", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return rx.Replace(text, "").TrimEnd() + Environment.NewLine;
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$"))
        {
            throw new ArgumentException("MCP server name must be alphanumeric, hyphen, or underscore");
        }
    }
}
