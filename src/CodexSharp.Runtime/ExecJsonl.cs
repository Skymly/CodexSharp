using System.Text.Json;
using CodexSharp.Protocol;

namespace CodexSharp.Runtime;

/// JSONL events for `codex exec --json`, shaped after vendor/codex/codex-rs/exec exec_events.rs.
public static class ExecJsonl
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ThreadStarted(string threadId) =>
        Line(new { type = "thread.started", threadId });

    public static string? Format(AgentEvent evt, long inputTokens = 0, long outputTokens = 0) =>
        evt switch
        {
            AgentEvent.ThreadStarted started => Line(new { type = "thread.started", threadId = started.ThreadId }),
            AgentEvent.TurnStarted => Line(new { type = "turn.started" }),
            AgentEvent.TurnCompleted completed => Line(new
            {
                type = completed.Status is "failed" or "error" ? "turn.failed" : "turn.completed",
                usage = new
                {
                    inputTokens,
                    cachedInputTokens = 0L,
                    cacheWriteInputTokens = 0L,
                    outputTokens,
                    reasoningOutputTokens = 0L,
                },
                error = completed.Status is "failed" or "error" ? new { message = completed.Status } : null,
            }),
            AgentEvent.Failed failed => Line(new { type = "error", message = failed.Message }),
            AgentEvent.ItemStarted item => Line(new { type = "item.started", item = Item(item.Item) }),
            AgentEvent.ItemCompleted item => Line(new { type = "item.completed", item = Item(item.Item) }),
            AgentEvent.ItemDelta delta => Line(new { type = "item.updated", item = new { id = delta.ItemId, type = "agent_message", text = delta.Text } }),
            AgentEvent.TokenUsage => null,
            _ => null,
        };

    public static object Item(ConversationItem item)
    {
        var kind = item.Kind switch
        {
            "agent_message" => "agent_message",
            "reasoning" => "reasoning",
            "command_execution" or "tool_call" => item.ToolName.StartsWith("mcp__", StringComparison.Ordinal) ? "mcp_tool_call" : "command_execution",
            "file_change" => "file_change",
            "web_search" => "web_search",
            "plan_update" => "todo_list",
            "error" => "error",
            _ => item.Kind.Replace('-', '_'),
        };
        return new
        {
            id = item.Id,
            type = kind,
            text = item.Text,
            command = string.IsNullOrWhiteSpace(item.Command) ? item.ToolName : item.Command,
            path = item.Path,
            status = string.IsNullOrWhiteSpace(item.Status) ? "completed" : item.Status,
        };
    }

    private static string Line(object payload) => JsonSerializer.Serialize(payload, Json);
}
