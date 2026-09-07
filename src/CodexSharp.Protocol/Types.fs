namespace CodexSharp.Protocol

open System

module Ids =
    let newId (prefix: string) =
        sprintf "%s_%s" prefix (Guid.NewGuid().ToString("N").Substring(0, 12))

    let thread () = newId "thr"
    let turn () = newId "turn"
    let item () = newId "item"
    let call () = newId "call"
    let approval () = newId "appr"

type SandboxMode =
    | ReadOnly
    | WorkspaceWrite
    | DangerFullAccess

module SandboxMode =
    let parse (s: string) =
        match s.Trim().ToLowerInvariant().Replace("_", "-") with
        | "read-only" | "readonly" -> ReadOnly
        | "danger-full-access" | "dangerfullaccess" | "danger-full" | "full-access" -> DangerFullAccess
        | _ -> WorkspaceWrite

    let toWire =
        function
        | ReadOnly -> "read-only"
        | WorkspaceWrite -> "workspace-write"
        | DangerFullAccess -> "danger-full-access"

type ApprovalPolicy =
    | Untrusted
    | OnRequest
    | Never

module ApprovalPolicy =
    let parse (s: string) =
        match s.Trim().ToLowerInvariant().Replace("_", "-") with
        | "untrusted" | "always" -> Untrusted
        | "never" | "auto" -> Never
        | _ -> OnRequest

    let toWire =
        function
        | Untrusted -> "untrusted"
        | OnRequest -> "on-request"
        | Never -> "never"

type WireApi =
    | Responses
    | ChatCompletions

module WireApi =
    let parse (s: string) =
        match s.Trim().ToLowerInvariant() with
        | "responses" | "response" -> Responses
        | _ -> ChatCompletions

    let toWire =
        function
        | Responses -> "responses"
        | ChatCompletions -> "chat"

[<CLIMutable>]
type ModelProvider =
    { Id: string
      Name: string
      BaseUrl: string
      EnvKey: string
      WireApi: string
      ApiKey: string }

[<CLIMutable>]
type CodexConfig =
    { Home: string
      Model: string
      ReasoningEffort: string
      Provider: ModelProvider
      ApprovalPolicy: string
      SandboxMode: string
      DeveloperInstructions: string
      UserInstructions: string
      Cwd: string
      NetworkAccess: bool
      MaxTurns: int }

module CodexConfig =
    let sandbox (cfg: CodexConfig) = SandboxMode.parse cfg.SandboxMode
    let approval (cfg: CodexConfig) = ApprovalPolicy.parse cfg.ApprovalPolicy
    let wireApi (cfg: CodexConfig) = WireApi.parse cfg.Provider.WireApi

type ItemKind =
    | UserMessage
    | AgentMessage
    | Reasoning
    | CommandExecution
    | FileChange
    | PlanUpdate
    | ToolCall
    | ApprovalRequest
    | ErrorMessage

module ItemKind =
    let toWire =
        function
        | UserMessage -> "user_message"
        | AgentMessage -> "agent_message"
        | Reasoning -> "reasoning"
        | CommandExecution -> "command_execution"
        | FileChange -> "file_change"
        | PlanUpdate -> "plan_update"
        | ToolCall -> "tool_call"
        | ApprovalRequest -> "approval"
        | ErrorMessage -> "error"

[<CLIMutable>]
type ConversationItem =
    { Id: string
      Kind: string
      Text: string
      Status: string
      Command: string
      Path: string
      ToolName: string
      ToolCallId: string }

module ConversationItem =
    let create kind text =
        { Id = Ids.item ()
          Kind = ItemKind.toWire kind
          Text = text
          Status = "completed"
          Command = ""
          Path = ""
          ToolName = ""
          ToolCallId = "" }

    let started kind =
        { create kind "" with Status = "in_progress" }

type AgentEvent =
    | ThreadStarted of ThreadId: string * Title: string * Cwd: string
    | TurnStarted of TurnId: string * ThreadId: string
    | ItemStarted of Item: ConversationItem
    | ItemDelta of ItemId: string * Text: string
    | CommandOutputDelta of CallId: string * Text: string
    | ItemCompleted of Item: ConversationItem
    | ApprovalNeeded of RequestId: string * Command: string * Cwd: string * Reason: string
    | UserInputNeeded of RequestId: string * QuestionsJson: string
    | McpElicitationNeeded of RequestId: string * Message: string
    | TokenUsage of Total: int64 * Last: int64
    | Compacted of Dropped: int
    | Warning of Message: string
    | TurnCompleted of TurnId: string * Status: string
    | Log of Message: string
    | Failed of Message: string

type ApprovalDecision =
    | Allow
    | Deny of Reason: string
    | Abort

[<CLIMutable>]
type ToolSpec =
    { Name: string
      Description: string
      ParametersJson: string }

[<CLIMutable>]
type ToolCallRequest =
    { Id: string
      Name: string
      ArgumentsJson: string }

[<CLIMutable>]
type ToolCallResult =
    { Id: string
      Name: string
      Output: string
      IsError: bool }

type ModelStreamEvent =
    | OutputTextDelta of Text: string
    | ReasoningDelta of Text: string
    | ToolCallReady of Call: ToolCallRequest
    | StreamFinished of Reason: string
    | StreamFailed of Message: string

[<CLIMutable>]
type HistoryMessage =
    { Role: string
      Content: string
      ToolCallId: string
      Name: string
      ToolCallsJson: string }

module HistoryMessage =
    let user text =
        { Role = "user"; Content = text; ToolCallId = ""; Name = ""; ToolCallsJson = "" }

    let assistant text =
        { Role = "assistant"; Content = text; ToolCallId = ""; Name = ""; ToolCallsJson = "" }

    let system text =
        { Role = "system"; Content = text; ToolCallId = ""; Name = ""; ToolCallsJson = "" }

    let developer text =
        { Role = "developer"; Content = text; ToolCallId = ""; Name = ""; ToolCallsJson = "" }

    let tool id name output =
        { Role = "tool"; Content = output; ToolCallId = id; Name = name; ToolCallsJson = "" }

    let assistantTools text toolCallsJson =
        { Role = "assistant"
          Content = text
          ToolCallId = ""
          Name = ""
          ToolCallsJson = toolCallsJson }

[<CLIMutable>]
type ModelRequest =
    { Model: string
      Instructions: string
      Messages: HistoryMessage[]
      Tools: ToolSpec[]
      ReasoningEffort: string }

type IModelClient =
    abstract StreamAsync:
        request: ModelRequest *
        onEvent: Action<ModelStreamEvent> *
        ct: Threading.CancellationToken -> Threading.Tasks.Task

type IToolExecutor =
    abstract ExecuteAsync:
        call: ToolCallRequest *
        ct: Threading.CancellationToken -> Threading.Tasks.Task<ToolCallResult>

type IApprover =
    abstract RequestAsync:
        requestId: string *
        command: string *
        cwd: string *
        reason: string *
        ct: Threading.CancellationToken -> Threading.Tasks.Task<ApprovalDecision>

type IEventSink =
    abstract Emit: evt: AgentEvent -> unit

type HookDecision =
    | HookContinue
    | HookBlock of Reason: string

type IHookHost =
    abstract Fire: eventName: string * tool: string * argumentsJson: string -> HookDecision

type IUserInputHost =
    abstract RequestAsync:
        requestId: string *
        questionsJson: string *
        ct: Threading.CancellationToken -> Threading.Tasks.Task<string>
