using CodexSharp.Core;
using CodexSharp.Protocol;

namespace CodexSharp.Runtime;

public static class PromptDebug
{
    public static object BuildSnapshot(string? prompt)
    {
        var cfg = ConfigService.Load();
        HistoryMessage[] history = string.IsNullOrWhiteSpace(prompt)
            ? []
            : [new HistoryMessage("user", prompt, "", "", "")];
        var req = Prompt.build(cfg, history);
        return new
        {
            model = req.Model,
            reasoningEffort = req.ReasoningEffort,
            instructions = req.Instructions,
            tools = req.Tools.Select(t => t.Name).ToArray(),
            messages = req.Messages.Select(m => new { m.Role, m.Content }).ToArray(),
        };
    }
}
