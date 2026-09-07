namespace CodexSharp.Core

open System
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open CodexSharp.Protocol

type ISteerHost =
    abstract TryDequeue: unit -> string

type AgentDeps =
    { Config: CodexConfig
      Model: IModelClient
      Tools: IToolExecutor
      Approver: IApprover
      Sink: IEventSink
      ExtraTools: ToolSpec[]
      Hooks: IHookHost
      UserInput: IUserInputHost
      Steer: ISteerHost
      GoalObjective: string
      GoalStatus: string }

type NoopHookHost() =
    interface IHookHost with
        member _.Fire(_, _, _) = HookContinue

type NoopSteerHost() =
    interface ISteerHost with
        member _.TryDequeue() = ""

type NoopUserInputHost() =
    interface IUserInputHost with
        member _.RequestAsync(_, _, _) =
            Task.FromResult("{\"answers\":{},\"note\":\"no UI attached\"}")

module AgentLoop =
    let private emit (deps: AgentDeps) evt = deps.Sink.Emit evt

    let private needsApproval (cfg: CodexConfig) (call: ToolCallRequest) =
        let approval = CodexConfig.approval cfg
        let sandbox = CodexConfig.sandbox cfg
        match approval with
        | Never -> false
        | Untrusted ->
            call.Name = "shell" || call.Name = "write_file" || call.Name = "apply_patch" || call.Name = "request_permissions" || call.Name = "exec_command"
        | OnRequest ->
            if call.Name = "request_permissions" then true
            elif call.Name <> "shell" && call.Name <> "exec_command" then false
            else
                match sandbox with
                | DangerFullAccess -> false
                | ReadOnly -> true
                | WorkspaceWrite ->
                    let args = call.ArgumentsJson.ToLowerInvariant()
                    args.Contains "curl " || args.Contains "wget " || args.Contains "invoke-webrequest"
                    || args.Contains "ssh " || args.Contains "rm -rf" || args.Contains "del /s"

    let private compactWithHooks (deps: AgentDeps) (history: ResizeArray<HistoryMessage>) =
        match deps.Hooks.Fire("PreCompact", "compact", "") with
        | HookBlock _ -> false
        | HookContinue ->
            let before = history.Count
            let ok = Compact.apply history
            if ok then
                deps.Hooks.Fire("PostCompact", "compact", "") |> ignore
                emit deps (Compacted (max 0 (before - history.Count)))
            ok

    let private toolCallsJson (calls: ToolCallRequest[]) =
        let opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
        JsonSerializer.Serialize(calls, opts)

    let runTurn (deps: AgentDeps) (threadId: string) (history: ResizeArray<HistoryMessage>) (userText: string) (ct: CancellationToken) : Task<string> =
        task {
            let turnId = Ids.turn ()
            emit deps (TurnStarted(turnId, threadId))

            let userItem = { ConversationItem.create UserMessage userText with Status = "completed" }
            emit deps (ItemStarted userItem)
            emit deps (ItemCompleted userItem)
            history.Add(HistoryMessage.user userText)
            compactWithHooks deps history |> ignore

            let mutable continueLoop = true
            let mutable iterations = 0
            let mutable finalStatus = "completed"
            let maxTurns = if deps.Config.MaxTurns <= 0 then 24 else deps.Config.MaxTurns

            let drainSteer (keepGoing: bool) =
                let rec loop got =
                    let text = deps.Steer.TryDequeue()
                    if String.IsNullOrWhiteSpace text then got
                    else
                        let steered = { ConversationItem.create UserMessage text with Status = "completed" }
                        emit deps (ItemStarted steered)
                        emit deps (ItemCompleted steered)
                        history.Add(HistoryMessage.user text)
                        loop true
                if loop false && keepGoing then continueLoop <- true

            while continueLoop && iterations < maxTurns && not ct.IsCancellationRequested do
                drainSteer true
                iterations <- iterations + 1
                let request = Prompt.buildWithGoal deps.Config (history.ToArray()) deps.ExtraTools threadId deps.GoalObjective deps.GoalStatus
                let text = StringBuilder()
                let calls = ResizeArray<ToolCallRequest>()
                let agentItem = { ConversationItem.started AgentMessage with Status = "in_progress" }
                let mutable started = false

                let onEvent (evt: ModelStreamEvent) =
                    match evt with
                    | OutputTextDelta chunk when not (String.IsNullOrEmpty chunk) ->
                        if not started then
                            started <- true
                            emit deps (ItemStarted agentItem)
                        text.Append chunk |> ignore
                        emit deps (ItemDelta(agentItem.Id, chunk))
                    | ReasoningDelta chunk when not (String.IsNullOrEmpty chunk) ->
                        emit deps (Log chunk)
                    | ToolCallReady call -> calls.Add call
                    | StreamFailed msg ->
                        finalStatus <- "failed"
                        emit deps (Failed msg)
                    | StreamFinished _ -> ()
                    | _ -> ()

                do! deps.Model.StreamAsync(request, Action<ModelStreamEvent>(onEvent), ct)

                if started then
                    let completed = { agentItem with Text = text.ToString(); Status = "completed" }
                    emit deps (ItemCompleted completed)

                if calls.Count = 0 then
                    if not started && text.Length = 0 && finalStatus <> "failed" then
                        let fallback = ConversationItem.create AgentMessage "(no model output)"
                        emit deps (ItemStarted fallback)
                        emit deps (ItemCompleted fallback)
                        history.Add(HistoryMessage.assistant fallback.Text)
                    elif started then
                        history.Add(HistoryMessage.assistant (text.ToString()))
                    continueLoop <- false
                    drainSteer true
                else
                    history.Add(HistoryMessage.assistantTools (text.ToString()) (toolCallsJson (calls.ToArray())))
                    for call in calls do
                        if continueLoop && not ct.IsCancellationRequested then
                            let kind =
                                match call.Name with
                                | "apply_patch" -> FileChange
                                | "shell" -> CommandExecution
                                | "update_plan" -> PlanUpdate
                                | _ -> ToolCall
                            let toolItem =
                                { ConversationItem.started kind with
                                    ToolName = call.Name
                                    ToolCallId = call.Id
                                    Text = call.ArgumentsJson
                                    Command = if call.Name = "shell" then call.ArgumentsJson else "" }

                            emit deps (ItemStarted toolItem)

                            let mutable denied = false
                            match deps.Hooks.Fire("PreToolUse", call.Name, call.ArgumentsJson) with
                            | HookBlock reason ->
                                denied <- true
                                let output = $"Hook blocked: {reason}"
                                history.Add(HistoryMessage.tool call.Id call.Name output)
                                emit deps (ItemCompleted { toolItem with Status = "denied"; Text = output })
                            | HookContinue -> ()

                            if not denied && needsApproval deps.Config call then
                                match deps.Hooks.Fire("PermissionRequest", call.Name, call.ArgumentsJson) with
                                | HookBlock reason ->
                                    denied <- true
                                    let output = $"Hook blocked: {reason}"
                                    history.Add(HistoryMessage.tool call.Id call.Name output)
                                    emit deps (ItemCompleted { toolItem with Status = "denied"; Text = output })
                                | HookContinue -> ()

                            if not denied && needsApproval deps.Config call then
                                let requestId = Ids.approval ()
                                emit deps (ApprovalNeeded(requestId, call.ArgumentsJson, deps.Config.Cwd, $"tool {call.Name}"))
                                let! decision = deps.Approver.RequestAsync(requestId, call.ArgumentsJson, deps.Config.Cwd, $"tool {call.Name}", ct)
                                match decision with
                                | Allow -> ()
                                | Deny reason ->
                                    denied <- true
                                    let output = $"Approval denied: {reason}"
                                    history.Add(HistoryMessage.tool call.Id call.Name output)
                                    emit deps (ItemCompleted { toolItem with Status = "denied"; Text = output })
                                | Abort ->
                                    denied <- true
                                    continueLoop <- false
                                    finalStatus <- "interrupted"

                            if not denied && continueLoop then
                                if call.Name = "request_user_input" then
                                    let inputId = Ids.approval ()
                                    emit deps (UserInputNeeded(inputId, call.ArgumentsJson))
                                    let! answers = deps.UserInput.RequestAsync(inputId, call.ArgumentsJson, ct)
                                    history.Add(HistoryMessage.tool call.Id call.Name answers)
                                    emit deps
                                        (ItemCompleted
                                            { toolItem with
                                                Status = "completed"
                                                Text = answers })
                                else
                                    let! result = deps.Tools.ExecuteAsync(call, ct)
                                    deps.Hooks.Fire("PostToolUse", call.Name, call.ArgumentsJson) |> ignore
                                    if call.Name = "new_context_window" && not result.IsError then
                                        let dropped = Compact.resetWithoutSummary history
                                        emit deps (Compacted dropped)
                                    history.Add(HistoryMessage.tool call.Id call.Name result.Output)
                                    emit deps
                                        (ItemCompleted
                                            { toolItem with
                                                Status = if result.IsError then "failed" else "completed"
                                                Text = result.Output })

            if ct.IsCancellationRequested then
                finalStatus <- "interrupted"
            elif iterations >= maxTurns && continueLoop then
                finalStatus <- "max_turns"

            if finalStatus = "interrupted" then
                deps.Hooks.Fire("Interrupt", "turn", "") |> ignore

            emit deps (TurnCompleted(turnId, finalStatus))
            return turnId
        }
