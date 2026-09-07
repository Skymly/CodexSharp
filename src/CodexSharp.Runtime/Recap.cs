using CodexSharp.Protocol;

namespace CodexSharp.Runtime;

internal sealed class NullSink : IEventSink
{
    public void Emit(AgentEvent evt) { }
}


/// Catch-up recap for a returning user. Independent wording; not a Codex prompt dump.
public static class Recap
{
    public const int MaxUserTurns = 8;
    public const int MaxChars = 900;

    public static string Prefix { get; } =
        "Write a brief catch-up for someone returning to this task. In at most two short sentences cover the goal, what already landed, and the next step or blocker. Do not invent files, tests, or work the conversation does not confirm. If the task is done, say so. Use the user language. No greeting, markdown, lists, or tool chatter."
        + "\n\nRecent conversation:\n";

    public static string? BuildPrompt(IReadOnlyList<HistoryMessage> history)
    {
        var lines = new List<(string Role, string Content)>();
        var userTurns = 0;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var message = history[i];
            if (message.Role is not ("user" or "assistant")) continue;
            var content = (message.Content ?? "").Trim();
            if (content.Length == 0) continue;
            if (content.Length > 400) content = content[..400];
            var role = message.Role == "user" ? "User" : "Assistant";
            lines.Add((role, content));
            if (message.Role == "user")
            {
                userTurns++;
                if (userTurns >= MaxUserTurns) break;
            }
        }

        if (lines.Count == 0) return null;
        lines.Reverse();
        var body = string.Join("\n", lines.Select(l => l.Role + ": " + l.Content));
        if (body.Length > MaxChars) body = body[^MaxChars..];
        return Prefix + body;
    }
}

public static class ThreadCopy
{
    public static (string Kind, string Text) Last(IReadOnlyList<HistoryMessage> history, string? want)
    {
        HistoryMessage? last = null;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var message = history[i];
            if (message.Role == "assistant" && !string.IsNullOrWhiteSpace(message.Content))
            {
                last = message;
                break;
            }
        }

        if (last is null) return ("empty", "");
        var kind = (want ?? "").Trim().ToLowerInvariant();
        if (kind is "code" or "fence" or "block")
        {
            var code = Markdown.LastCode(last.Content);
            return string.IsNullOrWhiteSpace(code) ? ("empty", "") : ("code", code!);
        }

        return ("message", last.Content);
    }
}
