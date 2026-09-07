namespace CodexSharp.Core

open System
open CodexSharp.Protocol

/// Local history compaction inspired by vendor/codex/codex-rs/core/src/compact.rs
module Compact =
    let ThresholdChars = 80_000
    let KeepRecent = 10

    let totalChars (history: ResizeArray<HistoryMessage>) =
        history |> Seq.sumBy (fun m -> m.Content.Length + m.ToolCallsJson.Length)

    let apply (history: ResizeArray<HistoryMessage>) =
        if totalChars history < ThresholdChars || history.Count <= KeepRecent then
            false
        else
            let keepFrom = history.Count - KeepRecent
            let dropped = history.Count - KeepRecent
            let summary =
                HistoryMessage.developer (
                    $"[compacted {dropped} earlier messages into a stub; full tool output omitted to stay inside the context window]")
            let kept = history |> Seq.skip keepFrom |> Seq.toArray
            history.Clear()
            history.Add summary
            history.AddRange kept
            true

    /// Official new_context_window: drop history without summarizing it.
    let resetWithoutSummary (history: ResizeArray<HistoryMessage>) =
        let dropped = history.Count
        history.Clear()
        history.Add(HistoryMessage.developer "[new context window; prior history dropped without summarization]")
        dropped
