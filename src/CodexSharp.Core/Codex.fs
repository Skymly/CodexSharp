namespace CodexSharp.Core

open System.Threading
open System.Threading.Tasks
open CodexSharp.Protocol

/// SQ/EQ handle modeled on vendor/codex/codex-rs/core + protocol.rs
type Codex(deps: AgentDeps, threadId: string, history: ResizeArray<HistoryMessage>) =
    member _.ThreadId = threadId
    member _.History = history

    member _.Submit(op: Op, ct: CancellationToken) : Task<string> =
        match op with
        | UserInput text -> AgentLoop.runTurn deps threadId history text ct
        | Interrupt ->
            deps.Sink.Emit(AgentEvent.TurnCompleted(Ids.turn (), "interrupted"))
            Task.FromResult "interrupted"
        | ExecApproval (id, allow) ->
            Task.FromResult id
        | PatchApproval (id, allow) ->
            Task.FromResult id
        | Compact -> Task.FromResult "compact"
        | Shutdown ->
            deps.Sink.Emit(AgentEvent.Log "shutdown")
            Task.FromResult "shutdown"

module Codex =
    let start deps =
        let threadId = Ids.thread ()
        deps.Sink.Emit(AgentEvent.ThreadStarted(threadId, "New thread", deps.Config.Cwd))
        Codex(deps, threadId, ResizeArray())
