namespace CodexSharp.Runtime;

/// Shared `/agents` listing: this-folder sessions plus the current thread's subagents.
/// Matches vendor/codex slash Agents ("view and switch between all active agent sessions").
public static class AgentsOverview
{
    public static string Render(string? cwd, IEnumerable<SubagentSnapshot>? subagents, bool all = false)
    {
        var threads = ResumePicker.Candidates(all, includeNonInteractive: false, cwd);
        var lines = new List<string>
        {
            all ? "sessions (all folders)" : "sessions (this folder)",
        };
        if (threads.Count == 0)
        {
            lines.Add("  (none)");
        }
        else
        {
            foreach (var thread in threads.Take(20))
            {
                lines.Add("  " + ResumePicker.Format(thread, showCwd: all));
            }
        }

        lines.Add("subagents");
        var list = subagents?.ToList() ?? [];
        if (list.Count == 0)
        {
            lines.Add("  (none)");
        }
        else
        {
            foreach (var agent in list)
            {
                var result = string.IsNullOrWhiteSpace(agent.Result) ? "" : "  " + agent.Result;
                lines.Add($"  {agent.Id}  {(agent.Done ? "done" : "running")}{result}");
            }
        }

        lines.Add("usage: /agents THREAD_ID | /agents --all");
        return string.Join(Environment.NewLine, lines);
    }
}
