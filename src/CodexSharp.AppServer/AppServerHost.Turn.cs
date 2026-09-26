using System.Text;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using CodexSharp.Core;
using CodexSharp.Protocol;
using CodexSharp.Runtime;


namespace CodexSharp.AppServer;

public sealed partial class AppServerHost
{
    private async Task DispatchTurnStartAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            if (!_threads.TryGetValue(threadId, out var session))
            {
                Error(id, -32001, $"Thread not loaded: {threadId}");
                return;
            }

            var text = ReadInputText(paramsEl);
            Result(id, new { turn = new { id = "pending", status = "in_progress" } });
            StartTurn(session, text, ct);
            return;
        }
    }

    private async Task DispatchTurnSettingsUpdateAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            if (!RequireExperimental(id, hasId, "turn/settings/update")) return;
            var threadId = Str(paramsEl, "threadId") ?? "";
            if (!_threads.TryGetValue(threadId, out var liveTurn) || !_turnCts.ContainsKey(threadId))
            {
                Result(id, new { status = "targetUnavailable" });
                return;
            }
            liveTurn.ApplySettings(Str(paramsEl, "model"), effort: Str(paramsEl, "effort"));
            Result(id, new { status = "applied" });
            return;
        }
    }

    private async Task DispatchTurnInterruptAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            if (_turnCts.TryGetValue(threadId, out var turnCts))
            {
                turnCts.Cancel();
            }
            Result(id, new { });
            return;
        }
    }

    private async Task DispatchTurnSteerAsync(JsonElement id, bool hasId, JsonElement paramsEl, string method, CancellationToken ct)
    {
        {
            var threadId = Str(paramsEl, "threadId") ?? "";
            if (!_threads.TryGetValue(threadId, out var session))
            {
                Error(id, -32001, $"Thread not loaded: {threadId}");
                return;
            }
            var expected = Str(paramsEl, "expectedTurnId");
            if (!string.IsNullOrWhiteSpace(expected) && !string.IsNullOrWhiteSpace(session.ActiveTurnId)
                && !string.Equals(expected, session.ActiveTurnId, StringComparison.Ordinal))
            {
                Error(id, -32602, "expectedTurnId does not match the active turn");
                return;
            }
            var text = ReadInputText(paramsEl);
            if (!session.TrySteer(text))
            {
                Error(id, -32001, "no active turn to steer");
                return;
            }
            Result(id, new { turnId = session.ActiveTurnId ?? "steered" });
            return;
        }
    }

}
