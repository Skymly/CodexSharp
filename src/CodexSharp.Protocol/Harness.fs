namespace CodexSharp.Protocol

/// Prompt tags copied from vendor/codex/codex-rs/protocol/src/protocol.rs
module CodexTags =
    let UserInstructionsOpen = "<user_instructions>"
    let UserInstructionsClose = "</user_instructions>"
    let EnvironmentContextOpen = "<environment_context>"
    let EnvironmentContextClose = "</environment_context>"
    let SkillsInstructionsOpen = "<skills_instructions>"
    let SkillsInstructionsClose = "</skills_instructions>"
    let PluginsInstructionsOpen = "<plugins_instructions>"
    let PluginsInstructionsClose = "</plugins_instructions>"
    let ToolsOpen = "<tools>"
    let ToolsClose = "</tools>"
    let UserMessageBegin = "## My request for Codex:"
    let PermissionsOpen = "<permissions_instructions>"
    let PermissionsClose = "</permissions_instructions>"
    let MemoriesOpen = "<memories>"
    let MemoriesClose = "</memories>"

    let stripUserMessagePrefix (text: string) =
        let idx = text.IndexOf UserMessageBegin
        if idx < 0 then text.Trim()
        else text.Substring(idx + UserMessageBegin.Length).Trim()

/// SQ/EQ harness types from vendor/codex/codex-rs/protocol/src/protocol.rs
type Op =
    | UserInput of Text: string
    | Interrupt
    | ExecApproval of Id: string * Allow: bool
    | PatchApproval of Id: string * Allow: bool
    | Compact
    | Shutdown

type EventMsg =
    | Error of Message: string
    | AgentMessage of Text: string
    | AgentMessageContentDelta of Delta: string
    | ExecCommandBegin of CallId: string * Command: string
    | ExecCommandEnd of CallId: string * Output: string * ExitCode: int
    | PatchApplyBegin of CallId: string
    | PatchApplyEnd of CallId: string * Success: bool
    | TurnComplete of TurnId: string * Status: string
    | TokenCount of Input: int * Output: int
    | ApprovalRequested of RequestId: string * Command: string

type Submission = { Id: string; Op: Op }
type Event = { Id: string; Msg: EventMsg }

module EventMapping =
    let ofAgentEvent =
        function
        | AgentEvent.ItemDelta (_, text) -> Some(AgentMessageContentDelta text)
        | AgentEvent.ItemCompleted item when item.Kind = "agent_message" -> Some(AgentMessage item.Text)
        | AgentEvent.ItemStarted item when item.Kind = "tool_call" && item.ToolName = "shell" ->
            Some(ExecCommandBegin(item.ToolCallId, item.Command))
        | AgentEvent.ItemCompleted item when item.Kind = "tool_call" && item.ToolName = "shell" ->
            Some(ExecCommandEnd(item.ToolCallId, item.Text, if item.Status = "failed" then 1 else 0))
        | AgentEvent.ItemStarted item when item.ToolName = "apply_patch" -> Some(PatchApplyBegin item.ToolCallId)
        | AgentEvent.ItemCompleted item when item.ToolName = "apply_patch" ->
            Some(PatchApplyEnd(item.ToolCallId, item.Status <> "failed"))
        | AgentEvent.TurnCompleted (turnId, status) -> Some(TurnComplete(turnId, status))
        | AgentEvent.Failed msg -> Some(Error msg)
        | AgentEvent.ApprovalNeeded (id, cmd, _, _) -> Some(ApprovalRequested(id, cmd))
        | _ -> None
