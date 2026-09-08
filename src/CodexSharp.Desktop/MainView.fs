namespace CodexSharp.Desktop

#nowarn "89"

open System
open System.IO
open System.Diagnostics
open System.Collections.Generic
open System.Text.Json
open Avalonia
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Media.Imaging
open Avalonia.Input
open Avalonia.Threading
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Platform.Storage
open Elmish
open Avalonia.FuncUI.Elmish.ElmishHook
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Styling
open CodexSharp.Protocol
open CodexSharp.Runtime
open CodexSharp.Client
open CodexSharp.AppServer

module Theme =
    let bg = SolidColorBrush(Color.Parse "#0B0E14")
    let panel = SolidColorBrush(Color.Parse "#11141C")
    let card = SolidColorBrush(Color.Parse "#151922")
    let border = SolidColorBrush(Color.Parse "#232833")
    let text = SolidColorBrush(Color.Parse "#E5E7EB")
    let muted = SolidColorBrush(Color.Parse "#6B7280")
    let accent = SolidColorBrush(Color.Parse "#F5C36C")
    let user = SolidColorBrush(Color.Parse "#7DD3FC")
    let tool = SolidColorBrush(Color.Parse "#86EFAC")
    let danger = SolidColorBrush(Color.Parse "#FCA5A5")
    let dangerBg = SolidColorBrush(Color.Parse "#3F1D1D")

    let kindBrush kind =
        match kind with
        | "user_message" | "user_image" -> user
        | "agent_message" -> accent
        | "tool_call" | "file_change" | "plan_update" -> tool
        | "command_execution" -> user
        | "error" -> danger
        | _ -> muted

type TimelineItem =
    { Id: string
      Kind: string
      ToolName: string
      Text: string
      Status: string
      ToolCallId: string }

type ThreadRow = { Id: string; Title: string; Subtitle: string; Pinned: bool; SectionId: string; ProjectId: string; Cwd: string }

type SectionRow = { Id: string; Name: string }
type ProjectRow = { Id: string; Name: string; Root: string; ExtraRoots: string list }

type ShellMode =
    | Chat
    | Work
    | Codex
type SkillRow = { Name: string; Path: string; Enabled: bool }
type HookRow = { Name: string; Event: string }
type FeatureRow = { Name: string; Enabled: bool; Stage: string }
type CommentRow = { File: string; Span: string; Body: string }
type PromptRow = { Name: string; Path: string }
type ActivityRow = { Id: string; ThreadId: string; Title: string; Kind: string; At: string }
type McpRow = { Name: string; Transport: string }

type ScreenState =
    { Session: AppServerSession
      Timeline: TimelineItem list
      Threads: ThreadRow list
      Composer: string
      Status: string
      Header: string
      Busy: bool
      ApprovalId: string
      ApprovalCommand: string
      Plan: string
      Search: string
      SearchHits: string list
      SideNotes: string
      ThreadQuery: string
      QueueCount: int
      Diff: string
      ShowSettings: bool
      Mentions: string list
      Attachments: string list
      RemoteImages: Map<string, string>
      SectionDraft: string
      ShowArchived: bool
      ShowAllThreads: bool
      Sections: SectionRow list
      Skills: string
      Models: string list
      Terminal: string
      TermIn: string
      TermPid: string
      ShowOnboarding: bool
      OnboardingDismissed: bool
      ShowTrust: bool
      TrustDismissed: bool
      TrustTarget: string
      RemoteControl: string
      RateLimits: string
      QueueItems: (string * string) list
      Projects: ProjectRow list
      ProjectDraft: string
      ExtraFolderDraft: string
      SelectedProject: string
      FsNote: string
      UserInputId: string
      UserInputPrompt: string
      UserInputDraft: string
      SkillRows: SkillRow list
      WorkspaceFiles: string list
      SlashHits: string list
      HookRows: HookRow list
      FeatureRows: FeatureRow list
      ComputerUse: string
      BrowserUse: string
      PluginAvailable: string list
      ComposerHistory: string list
      HistoryCursor: int
      ComposerDraft: string
      VimMode: string
      CommentRows: CommentRow list
      PromptRows: PromptRow list
      PrTitle: string
      PrBody: string
      MemoryNotes: string list
      McpRows: McpRow list
      McpDraft: string
      Toast: string
      TuiVim: string
      CollaborationMode: string
      Personality: string
      Effort: string
      TuiRaw: string
      TuiTitle: string
      GoalObjective: string
      GoalStatus: string
      SkillHits: string list
      ShowPalette: bool
      PaletteQuery: string
      PaletteIndex: int
      ShowSidebar: bool
      ShellMode: ShellMode
      ShowTerminal: bool
      ShowActivity: bool
      ActivityFilter: string
      ActivityRows: ActivityRow list
      ReviewBaseDraft: string
      ReviewCwd: string
      ReviewRepoLabel: string
      PrNote: string
      PrComments: GhPrComment list
      ShowScheduled: bool
      SchedDraft: string
      SchedMinutes: string
      SchedNote: string }

module MainView =

    let applyWindowTheme () =
        match Application.Current with
        | null -> ()
        | app ->
            app.RequestedThemeVariant <- if UiTheme.IsLight() then ThemeVariant.Light else ThemeVariant.Dark

    let private copyToClipboard (text: string) =
        try
            let psi = ProcessStartInfo("clip")
            psi.RedirectStandardInput <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            use proc = Process.Start(psi)
            if not (isNull proc) then
                proc.StandardInput.Write(text)
                proc.StandardInput.Close()
                proc.WaitForExit(1000) |> ignore
        with _ ->
            ()

    let private setWindowTitle (header: string) =
        try
            match Application.Current.ApplicationLifetime with
            | :? IClassicDesktopStyleApplicationLifetime as desk when not (isNull desk.MainWindow) ->
                let title = TerminalTitle.Sanitize("CodexSharp — " + header)
                desk.MainWindow.Title <- if title.Length = 0 then "CodexSharp" else title
            | _ -> ()
        with _ ->
            ()

    let private jsBool (el: JsonElement) (name: string) =
        let mutable v = Unchecked.defaultof<JsonElement>
        el.ValueKind = JsonValueKind.Object && el.TryGetProperty(name, &v) && v.ValueKind = JsonValueKind.True

    let private slashCommands =
        [ "/help"; "/new"; "/task"; "/compact"; "/fork"; "/side"; "/btw"; "/archive"; "/rollback"; "/exec"; "/review"; "/queue"; "/name"; "/rename"; "/effort"; "/ide"; "/rollout"; "/plan"; "/default"; "/pair"; "/stop"; "/status"; "/skills"; "/hooks"; "/mcp"; "/diff"; "/worktree"; "/memory"; "/memories"; "/model"; "/sandbox"; "/sandbox-add-read-dir"; "/logout"; "/init"; "/apply"; "/export"; "/recap"; "/clear"; "/delete"; "/pwd"; "/cd"; "/plugins"; "/experimental"; "/prompts"; "/permissions"; "/approvals"; "/personality"; "/usage"; "/copy"; "/keymap"; "/pets"; "/pet"; "/cloud"; "/cloud-environment"; "/setup-default-sandbox"; "/doctor"; "/debug-config"; "/apps"; "/ps"; "/mention"; "/resume"; "/agents"; "/vim"; "/theme"; "/statusline"; "/title"; "/notifications"; "/pin"; "/search"; "/goal"; "/feedback"; "/approve"; "/import"; "/raw"; "/subagents"; "/execpolicy"; "/history" ]

    let private exportMarkdown (threadId: string) (timeline: TimelineItem list) =
        let md =
            timeline
            |> List.map (fun item -> "## " + item.Kind + Environment.NewLine + item.Text)
            |> fun rows -> String.Join(Environment.NewLine + Environment.NewLine, rows)
        let dest = Path.Combine(Environment.CurrentDirectory, "codexsharp-export-" + threadId + ".md")
        File.WriteAllText(dest, if String.IsNullOrWhiteSpace md then "# (empty thread)" + Environment.NewLine else md)
        dest

    let private slashHits (composer: string) =
        if composer.StartsWith("/") && not (composer.Contains(" ")) then
            slashCommands |> List.filter (fun c -> c.StartsWith(composer, StringComparison.OrdinalIgnoreCase))
        else
            []

    let private markdownViews (text: string) (onOpen: string -> unit) : IView list =
        Markdown.Parse text
        |> Seq.map (fun block ->
            match block with
            | :? MdHeading as h ->
                TextBlock.create [
                    TextBlock.text (Markdown.StripInline h.Text)
                    TextBlock.fontWeight FontWeight.SemiBold
                    TextBlock.fontSize (if h.Level <= 1 then 18. elif h.Level = 2 then 15. else 13.)
                    TextBlock.foreground Theme.accent
                    TextBlock.textWrapping TextWrapping.Wrap
                ] :> IView
            | :? MdCode as c ->
                Border.create [
                    Border.background Theme.bg
                    Border.cornerRadius 6.
                    Border.padding 8.
                    Border.child (
                        TextBlock.create [
                            TextBlock.text (if String.IsNullOrWhiteSpace c.Lang then c.Text else c.Lang + Environment.NewLine + c.Text)
                            TextBlock.fontFamily (FontFamily "Cascadia Code, Consolas, monospace")
                            TextBlock.fontSize 12.
                            TextBlock.foreground Theme.text
                            TextBlock.textWrapping TextWrapping.Wrap
                        ])
                ] :> IView
            | :? MdBullet as b ->
                TextBlock.create [
                    TextBlock.text ("• " + Markdown.StripInline b.Text)
                    TextBlock.foreground Theme.text
                    TextBlock.textWrapping TextWrapping.Wrap
                ] :> IView
            | :? MdQuote as q ->
                TextBlock.create [
                    TextBlock.text ("│ " + Markdown.StripInline q.Text)
                    TextBlock.foreground Theme.muted
                    TextBlock.fontStyle FontStyle.Italic
                    TextBlock.textWrapping TextWrapping.Wrap
                ] :> IView
            | :? MdRule ->
                Border.create [ Border.height 1.; Border.background Theme.border ] :> IView
            | :? MdPara as para ->
                TextBlock.create [
                    TextBlock.text (Markdown.StripInline para.Text)
                    TextBlock.foreground Theme.text
                    TextBlock.textWrapping TextWrapping.Wrap
                ] :> IView
            | :? MdFileCite as cite ->
                let label =
                    if cite.Line.HasValue then cite.Path + ":" + string cite.Line.Value
                    else cite.Label
                Button.create [
                    Button.content label
                    Button.horizontalAlignment HorizontalAlignment.Left
                    Button.onClick (fun _ -> onOpen cite.Path)
                ] :> IView
            | _ ->
                TextBlock.create [ TextBlock.text text; TextBlock.textWrapping TextWrapping.Wrap ] :> IView)
        |> Seq.toList

    let private diffViews (diff: string) : IView list =
        if String.IsNullOrWhiteSpace diff then
            [ TextBlock.create [
                TextBlock.text "No file_change yet. apply_patch shows here."
                TextBlock.foreground Theme.muted
                TextBlock.fontSize 11.
              ] :> IView ]
        else
            diff.Split([| char 10 |])
            |> Array.map (fun line -> line.TrimEnd(char 13))
            |> Array.truncate 120
            |> Array.map (fun line ->
                let color =
                    if line.StartsWith("+++") || line.StartsWith("---") || line.StartsWith("diff") then Theme.muted
                    elif line.StartsWith("+") then Theme.tool
                    elif line.StartsWith("-") then Theme.danger
                    else Theme.text
                TextBlock.create [
                    TextBlock.text line
                    TextBlock.foreground color
                    TextBlock.fontFamily (FontFamily "Cascadia Code, Consolas, monospace")
                    TextBlock.fontSize 11.
                    TextBlock.textWrapping TextWrapping.Wrap
                ] :> IView)
            |> Array.toList

    let private jsStr (el: JsonElement) (name: string) =

        let mutable v = Unchecked.defaultof<JsonElement>
        if el.ValueKind = JsonValueKind.Object && el.TryGetProperty(name, &v) && v.ValueKind = JsonValueKind.String then
            v.GetString()
        else
            ""

    let private itemsToTimeline (el: JsonElement) =
        let mutable data = Unchecked.defaultof<JsonElement>
        if el.ValueKind <> JsonValueKind.Object || not (el.TryGetProperty("data", &data)) || data.ValueKind <> JsonValueKind.Array then
            []
        else
            data.EnumerateArray()
            |> Seq.map (fun x ->
                { TimelineItem.Id = jsStr x "id"
                  Kind = jsStr x "type"
                  ToolName = jsStr x "tool"
                  Text = jsStr x "text"
                  Status = ""
                  ToolCallId = jsStr x "toolCallId" })
            |> Seq.toList

    let private toThreadRows (items: IReadOnlyList<ThreadListItem>) =
        items
        |> Seq.map (fun t ->
            { ThreadRow.Id = t.Id
              Title = t.Title
              Subtitle = t.Subtitle
              Pinned = t.Pinned
              SectionId = t.SectionId
              ProjectId = t.ProjectId
              Cwd = t.Cwd })
        |> Seq.toList

    let private toProjectRows (items: IReadOnlyList<ProjectListItem>) =
        items
        |> Seq.map (fun p -> { ProjectRow.Id = p.Id; Name = p.Name; Root = p.Root; ExtraRoots = p.ExtraRoots |> Seq.toList })
        |> Seq.toList

    let private reviewRepos (projects: ProjectRow list) (selectedId: string) =
        match projects |> List.tryFind (fun p -> p.Id = selectedId) with
        | None -> []
        | Some p -> DesktopReviewRepos.FromProject(p.Root, p.ExtraRoots) |> List.ofSeq

    let private toSectionRows (items: IReadOnlyList<SectionListItem>) =
        items
        |> Seq.map (fun s -> { SectionRow.Id = s.Id; Name = s.Name })
        |> Seq.toList

    let private toActivityRows () =
        ActivityInbox.List()
        |> Seq.map (fun i ->
            { ActivityRow.Id = i.Id
              ThreadId = i.ThreadId
              Title = i.Title
              Kind =
                match i.Kind with
                | ActivityKind.Running -> "running"
                | ActivityKind.WaitingApproval -> "waiting"
                | _ -> "unread"
              At = i.At.ToLocalTime().ToString("HH:mm") })
        |> Seq.toList

    let private applyEvent (state: IWritable<ScreenState>) (evt: AgentEvent) =
        let current = state.Current
        let upsert (item: TimelineItem) append =
            let exists = current.Timeline |> List.exists (fun x -> x.Id = item.Id)
            let updated =
                current.Timeline
                |> List.map (fun x -> if x.Id = item.Id then { x with Text = (if String.IsNullOrEmpty item.Text then x.Text else item.Text); Status = item.Status } else x)
            let timeline =
                if exists then updated
                elif append then current.Timeline @ [item]
                else current.Timeline @ [item]
            state.Set { current with Timeline = timeline }

        match evt with
        | AgentEvent.ItemStarted item ->
            upsert
                { TimelineItem.Id = item.Id; Kind = item.Kind; ToolName = item.ToolName; Text = item.Text; Status = item.Status; ToolCallId = item.ToolCallId }
                true
        | AgentEvent.ItemDelta (itemId, text) ->
            let timeline =
                current.Timeline
                |> List.map (fun x -> if x.Id = itemId then { x with Text = x.Text + text } else x)
            state.Set { current with Timeline = timeline }
        | AgentEvent.CommandOutputDelta (callId, text) ->
            let timeline =
                current.Timeline
                |> List.map (fun x -> if x.Id = callId || x.ToolCallId = callId then { x with Text = x.Text + text } else x)
            state.Set { current with Timeline = timeline; Terminal = current.Terminal + text }
        | AgentEvent.ItemCompleted item ->
            let header =
                if item.Kind = "user_message" && current.Header = "New thread" then
                    let one = item.Text.Replace('\n', ' ').Trim()
                    if one.Length <= 48 then one else one.Substring(0, 48) + "…"
                else current.Header
            setWindowTitle header
            upsert
                { TimelineItem.Id = item.Id; Kind = item.Kind; ToolName = item.ToolName; Text = item.Text; Status = item.Status; ToolCallId = item.ToolCallId }
                false
            let extracted = ReviewPrompts.ExtractProposedPlan(item.Text)
            let plan =
                if item.Kind = "plan_update" || item.ToolName = "update_plan" then item.Text
                elif not (isNull extracted) then extracted
                else state.Current.Plan
            let diff = if item.Kind = "file_change" || item.ToolName = "apply_patch" then item.Text else state.Current.Diff
            let urls = ImageUrlCache.ExtractRemoteUrls(item.Text) |> Seq.toList
            let pending =
                urls
                |> List.fold (fun acc url -> Map.add url "pending" acc) state.Current.RemoteImages
            let comments =
                ReviewPrompts.ExtractComments(item.Text)
                |> Seq.map (fun c ->
                    let span =
                        if c.Start.HasValue && c.End.HasValue then string c.Start.Value + "-" + string c.End.Value
                        elif c.Start.HasValue then string c.Start.Value
                        else ""
                    { CommentRow.File = c.File; Span = span; Body = if String.IsNullOrWhiteSpace c.Body then c.Title else c.Body })
                |> Seq.toList
            let pr = ReviewPrompts.ExtractGitPr(item.Text)
            state.Set
                { state.Current with
                    Header = header
                    Status = state.Current.Status
                    Plan = plan
                    Diff = diff
                    RemoteImages = pending
                    CommentRows = if comments.IsEmpty then state.Current.CommentRows else state.Current.CommentRows @ comments
                    PrTitle = if isNull pr then state.Current.PrTitle else pr.Title
                    PrBody = if isNull pr then state.Current.PrBody else pr.Body }
            async {
                for url in urls do
                    let! cached = ImageUrlCache.DownloadAsync(url) |> Async.AwaitTask
                    let status = if isNull cached then "fail" else "ok"
                    Dispatcher.UIThread.Post(fun () ->
                        state.Set
                            { state.Current with
                                RemoteImages = Map.add url status state.Current.RemoteImages
                                Timeline = state.Current.Timeline |> List.map id })
            }
            |> Async.Start
        | AgentEvent.ApprovalNeeded (requestId, command, _, _) ->
            if not (String.IsNullOrWhiteSpace current.Session.ThreadId) then
                ActivityInbox.RecordWaitingApproval(current.Session.ThreadId, current.Header) |> ignore
            OsNotify.TrySend("CodexSharp", "Waiting for approval") |> ignore
            state.Set { current with ApprovalId = requestId; ApprovalCommand = command; ActivityRows = toActivityRows () }
        | AgentEvent.UserInputNeeded (requestId, questionsJson) ->
            state.Set { current with UserInputId = requestId; UserInputPrompt = questionsJson; UserInputDraft = "" }
        | AgentEvent.McpElicitationNeeded (requestId, message) ->
            state.Set { current with UserInputId = requestId; UserInputPrompt = "MCP: " + message; UserInputDraft = "" }
        | AgentEvent.TokenUsage (total, last) ->
            state.Set { current with Status = sprintf "est %d tok (last %d)" total last }
        | AgentEvent.Failed msg ->
            state.Set
                { current with
                    Timeline =
                        current.Timeline
                        @ [ { TimelineItem.Id = Ids.item (); Kind = "error"; ToolName = ""; Text = msg; Status = "failed"; ToolCallId = "" } ] }
        | AgentEvent.TurnStarted _ ->
            if not (String.IsNullOrWhiteSpace current.Session.ThreadId) then
                ActivityInbox.RecordRunning(current.Session.ThreadId, current.Header) |> ignore
            state.Set { current with Busy = true; ActivityRows = toActivityRows () }
        | AgentEvent.TurnCompleted _ ->
            if not (String.IsNullOrWhiteSpace current.Session.ThreadId) then
                ActivityInbox.RecordCompleted(current.Session.ThreadId, current.Header) |> ignore
            OsNotify.TrySend("CodexSharp", "Turn completed") |> ignore
            DesktopNotify.Send "CodexSharp turn completed"
            TurnNotify.Fire("agent-turn-complete", current.Session.ThreadId, null)
            state.Set { current with Busy = false; Toast = "Turn completed"; ActivityRows = toActivityRows () }
        | AgentEvent.Compacted dropped ->
            state.Set { current with Status = sprintf "compacted %d messages" dropped }
        | AgentEvent.Warning msg ->
            state.Set
                { current with
                    Timeline =
                        current.Timeline
                        @ [ { TimelineItem.Id = Ids.item (); Kind = "error"; ToolName = ""; Text = msg; Status = "warning"; ToolCallId = "" } ] }
        | _ -> ()

    let private itemCard (remote: Map<string, string>) (retry: string -> unit) (onOpen: string -> unit) (item: TimelineItem) : IView =
        let title =
            match item.Kind with
            | "user_message" | "user_image" -> "You"
            | "agent_message" -> if item.Text.Contains("<proposed_plan>") then "Proposed plan" else "CodexSharp"
            | "file_change" -> "Patch"
            | "command_execution" -> "Command"
            | "plan_update" -> "Plan"
            | "tool_call" -> if String.IsNullOrEmpty item.ToolName then "tool" else item.ToolName
            | _ -> item.Kind

        Border.create [
            Border.margin (Thickness(0, 0, 0, 14))
            Border.padding 14.
            Border.cornerRadius 10.
            Border.background Theme.card
            Border.borderBrush Theme.border
            Border.borderThickness 1.
            Border.child (
                StackPanel.create [
                    StackPanel.spacing 6.
                    StackPanel.children (
                        let pics =
                            let extracted = ImageMessageContent.ExtractLocalPaths(item.Text) |> Seq.toList
                            let remote =
                                ImageUrlCache.ExtractRemoteUrls(item.Text)
                                |> Seq.choose (fun u ->
                                    let cached = ImageUrlCache.TryGetCached(u)
                                    if isNull cached then None else Some cached)
                                |> Seq.toList
                            let combined = extracted @ remote
                            if item.Kind = "user_image" && System.IO.File.Exists item.Text then
                                item.Text :: combined |> List.distinct
                            else
                                combined
                        [
                            DockPanel.create [
                                DockPanel.children (
                                    [
                                        Border.create [
                                            Border.width 8.
                                            Border.height 8.
                                            Border.cornerRadius 4.
                                            Border.background (Theme.kindBrush item.Kind)
                                            Border.margin (Thickness(0, 0, 8, 0))
                                        ] :> IView
                                        TextBlock.create [
                                            TextBlock.text title
                                            TextBlock.fontWeight FontWeight.SemiBold
                                            TextBlock.foreground Theme.text
                                        ] :> IView
                                        TextBlock.create [
                                            TextBlock.dock Dock.Right
                                            TextBlock.text item.Status
                                            TextBlock.foreground Theme.muted
                                            TextBlock.fontSize 11.
                                            TextBlock.horizontalAlignment HorizontalAlignment.Right
                                        ] :> IView
                                    ]
                                    @ (
                                        if item.Kind = "agent_message" && not (String.IsNullOrWhiteSpace item.Text) then
                                            let copyBtn =
                                                Button.create [
                                                    Button.dock Dock.Right
                                                    Button.content "Copy"
                                                    Button.margin (Thickness(0, 0, 8, 0))
                                                    Button.onClick (fun _ -> copyToClipboard item.Text)
                                                ] :> IView
                                            match Markdown.LastCode(item.Text) with
                                            | null | "" -> [ copyBtn ]
                                            | code ->
                                                let codeBtn =
                                                    Button.create [
                                                        Button.dock Dock.Right
                                                        Button.content "Code"
                                                        Button.margin (Thickness(0, 0, 8, 0))
                                                        Button.onClick (fun _ -> copyToClipboard code)
                                                    ] :> IView
                                                [ copyBtn; codeBtn ]
                                        else
                                            [])
                                )
                            ] :> IView
                            if item.Kind = "user_image" then
                                TextBlock.create [
                                    TextBlock.text (System.IO.Path.GetFileName item.Text)
                                    TextBlock.textWrapping TextWrapping.Wrap
                                    TextBlock.foreground Theme.text
                                ] :> IView
                            elif item.Kind = "agent_message" then
                                StackPanel.create [
                                    StackPanel.spacing 6.
                                    StackPanel.children (markdownViews item.Text onOpen)
                                ] :> IView
                            else
                                TextBlock.create [
                                    TextBlock.text item.Text
                                    TextBlock.textWrapping TextWrapping.Wrap
                                    TextBlock.foreground Theme.text
                                    TextBlock.fontFamily (FontFamily "Cascadia Code, Consolas, monospace")
                                ] :> IView
                        ]
                        @ (pics
                           |> List.map (fun pth ->
                               Image.create [
                                   Image.maxHeight 220.
                                   Image.stretch Stretch.Uniform
                                   Image.horizontalAlignment HorizontalAlignment.Left
                                   Image.source (new Bitmap(pth))
                               ] :> IView))
                        @ (ImageUrlCache.ExtractRemoteUrls(item.Text)
                           |> Seq.toList
                           |> List.choose (fun url ->
                               if not (isNull (ImageUrlCache.TryGetCached(url))) then
                                   None
                               else
                                   match Map.tryFind url remote with
                                   | Some "fail" ->
                                       Some (
                                           Button.create [
                                               Button.content "Couldn't load image — retry"
                                               Button.foreground Theme.danger
                                               Button.onClick (fun _ -> retry url)
                                           ] :> IView)
                                   | _ ->
                                       Some (
                                           StackPanel.create [
                                               StackPanel.spacing 6.
                                               StackPanel.children [
                                                   Border.create [
                                                       Border.width 220.
                                                       Border.height 72.
                                                       Border.cornerRadius 8.
                                                       Border.background Theme.border
                                                   ]
                                                   ProgressBar.create [
                                                       ProgressBar.isIndeterminate true
                                                       ProgressBar.height 4.
                                                       ProgressBar.width 220.
                                                   ]
                                               ]
                                           ] :> IView)))
                    )
                ]
            )
        ] :> IView

    let private parseQueue (el: JsonElement) =
        let mutable data = Unchecked.defaultof<JsonElement>
        if el.ValueKind = JsonValueKind.Object && el.TryGetProperty("data", &data) && data.ValueKind = JsonValueKind.Array then
            data.EnumerateArray()
            |> Seq.map (fun x -> jsStr x "id", jsStr x "text")
            |> Seq.toList
        else
            []

    type private ShellModel =
        { Screen: ScreenState
          ApiKey: string
          ModelDraft: string
          SandboxRootDraft: string }

    type private ShellMsg =
        | SetScreen of ScreenState
        | SetApiKey of string
        | SetModelDraft of string
        | SetSandboxRoot of string

    type private Bridge<'t>(getValue: unit -> 't, setValue: 't -> unit) =
        let instanceId = Guid.NewGuid()
        member _.Current = getValue()
        member _.Set(value: 't) = setValue value
        interface IWritable<'t> with
            member _.InstanceId = instanceId
            member _.InstanceType = InstanceType.Source
            member _.ValueType = typeof<'t>
            member _.Current = getValue()
            member _.Set value = setValue value
            member _.Subscribe _ = { new IDisposable with member _.Dispose() = () }
            member _.SubscribeAny _ = { new IDisposable with member _.Dispose() = () }
            member _.Dispose() = ()

    let private shellUpdate (msg: ShellMsg) (model: ShellModel) : ShellModel * Cmd<ShellMsg> =
        match msg with
        | SetScreen screen -> { model with Screen = screen }, Cmd.none
        | SetApiKey value -> { model with ApiKey = value }, Cmd.none
        | SetModelDraft value -> { model with ModelDraft = value }, Cmd.none
        | SetSandboxRoot value -> { model with SandboxRootDraft = value }, Cmd.none

    let create () =
        Component(fun ctx ->
            let session =
                ctx.useState(lazy (AppServerSession(AppServerHandle.Start().Client)), false).Current.Value
            let holder: ShellModel option ref = ctx.useState(ref None, false).Current
            let init () =
                match holder.Value with
                | Some model -> model, Cmd.none
                | None ->
                    let screen =
                        { Session = session
                          Timeline = []
                          Threads = []
                          Composer = ""
                          Status = "connecting…"
                          Header = "New thread"
                          Busy = true
                          ApprovalId = ""
                          ApprovalCommand = ""
                          Plan = ""
                          Search = ""
                          SearchHits = []
                          SideNotes = "Loading…"
                          ThreadQuery = ""
                          QueueCount = 0
                          Diff = ""
                          ShowSettings = false
                          Mentions = []
                          Attachments = []
                          RemoteImages = Map.empty
                          SectionDraft = ""
                          ShowArchived = false
                          ShowAllThreads = false
                          Sections = []
                          Skills = "Loading skills…"
                          Models = []
                          Terminal = ""
                          TermIn = ""
                          TermPid = ""
                          ShowOnboarding = false
                          OnboardingDismissed = false
                          ShowTrust = false
                          TrustDismissed = false
                          TrustTarget = ""
                          RemoteControl = "disabled"
                          RateLimits = ""
                          QueueItems = []
                          Projects = []
                          ProjectDraft = ""
                          ExtraFolderDraft = ""
                          SelectedProject = ""
                          FsNote = ""
                          UserInputId = ""
                          UserInputPrompt = ""
                          UserInputDraft = ""
                          SkillRows = []
                          WorkspaceFiles = []
                          SlashHits = []
                          HookRows = []
                          FeatureRows = []
                          ComputerUse = "notConfigured"
                          BrowserUse = "notConfigured"
                          PluginAvailable = []
                          ComposerHistory = []
                          HistoryCursor = -1
                          ComposerDraft = ""
                          VimMode = "insert"
                          CommentRows = []
                          PromptRows = []
                          PrTitle = ""
                          PrBody = ""
                          MemoryNotes = []
                          McpRows = []
                          McpDraft = ""
                          Toast = ""
                          TuiVim = ""
                          CollaborationMode = ""
                          Personality = ""
                          Effort = ""
                          TuiRaw = ""
                          TuiTitle = ""
                          GoalObjective = ""
                          GoalStatus = "cleared"
                          SkillHits = []
                          ShowPalette = false
                          PaletteQuery = ""
                          PaletteIndex = 0
                          ShowSidebar = true
                          ShellMode = Codex
                          ShowTerminal = true
                          ShowActivity = false
                          ActivityFilter = ""
                          ActivityRows = []
                          ReviewBaseDraft = "main"
                          ReviewCwd = ""
                          ReviewRepoLabel = ""
                          PrNote = ""
                          PrComments = []
                          ShowScheduled = false
                          SchedDraft = ""
                          SchedMinutes = "1"
                          SchedNote = "" }
                    let model =
                        { Screen = screen
                          ApiKey = ""
                          ModelDraft = ""
                          SandboxRootDraft = "" }
                    holder.Value <- Some model
                    model, Cmd.none

            let model, dispatch = ctx.useElmish(init, shellUpdate)
            match holder.Value with
            | Some _ -> ()
            | None -> holder.Value <- Some model
            let current () = holder.Value |> Option.defaultValue model
            let state: IWritable<ScreenState> =
                new Bridge<ScreenState>(
                    (fun () -> current().Screen),
                    fun screen ->
                        holder.Value <- Some { current() with Screen = screen }
                        dispatch (SetScreen screen))
            let apiKey: IWritable<string> =
                new Bridge<string>(
                    (fun () -> current().ApiKey),
                    fun value ->
                        holder.Value <- Some { current() with ApiKey = value }
                        dispatch (SetApiKey value))
            let modelDraft: IWritable<string> =
                new Bridge<string>(
                    (fun () -> current().ModelDraft),
                    fun value ->
                        holder.Value <- Some { current() with ModelDraft = value }
                        dispatch (SetModelDraft value))
            let sandboxRootDraft: IWritable<string> =
                new Bridge<string>(
                    (fun () -> current().SandboxRootDraft),
                    fun value ->
                        holder.Value <- Some { current() with SandboxRootDraft = value }
                        dispatch (SetSandboxRoot value))

            let startupLink = DesktopActivation.ConsumeStartup() |> Option.ofObj
            let composerVim = ctx.useState (ComposerVim(ComposerBuffer()))
            let sshHostDraft = ctx.useState ""
            let sshFolderDraft = ctx.useState ""

            let wipeComposerBuffer () =
                composerVim.Current.Buffer.Clear()
                composerVim.Current.SetMode ComposerVimMode.Insert

            applyWindowTheme ()

            let refreshThreads () =
                async {
                    try
                        let archived = state.Current.ShowArchived
                        let chatUnassigned = state.Current.ShellMode = Chat
                        let projectId = state.Current.SelectedProject
                        let filterProject = chatUnassigned || not (String.IsNullOrWhiteSpace projectId)
                        let listProjectId = if chatUnassigned then null else if filterProject then projectId else null
                        let cwd = if state.Current.ShowAllThreads then null else Environment.CurrentDirectory
                        let! threads = session.LoadThreadsAsync(archived, 50, listProjectId, filterProject, cwd) |> Async.AwaitTask
                        let! sections = session.LoadSectionsAsync() |> Async.AwaitTask
                        let! projects = session.LoadProjectsAsync() |> Async.AwaitTask
                        let rows =
                            toThreadRows threads
                            |> List.filter (fun row ->
                                if state.Current.ShowAllThreads then true
                                else
                                    let src = ThreadSources.Get row.Id
                                    src <> "exec" && src <> "non-interactive")
                        Dispatcher.UIThread.Post(fun () ->
                            state.Set
                                { state.Current with
                                    Threads = rows
                                    Sections = toSectionRows sections
                                    Projects = toProjectRows projects })
                    with _ ->
                        ()
                }
                |> Async.Start

            let startQuickChat (ephemeral: bool) =
                async {
                    let! _ = session.StartThreadAsync(ephemeral = ephemeral) |> Async.AwaitTask
                    Dispatcher.UIThread.Post(fun () ->
                        state.Set
                            { state.Current with
                                ShellMode = Chat
                                Timeline = []
                                Composer = ""
                                Header = (if ephemeral then "Temporary chat" else "Quick chat")
                                Busy = false
                                ApprovalId = ""
                                ApprovalCommand = ""
                                Plan = ""
                                SearchHits = []
                                Diff = ""
                                GoalObjective = ""
                                GoalStatus = ThreadGoal.Cleared
                                ShowPalette = false })
                    refreshThreads ()
                }
                |> Async.Start

            let popout = DesktopPopout()
            let mutable popWin : Window option = None
            let openPopout () =
                let id = session.ThreadId
                if String.IsNullOrWhiteSpace id then ()
                else
                    popout.Open(id)
                    match popWin with
                    | Some w ->
                        w.Show()
                        w.Activate()
                    | None ->
                        let w = PopoutWindow(id, state.Current.Header, fun text ->
                            async {
                                try
                                    do! session.StartTurnOnThreadAsync(id, text) |> Async.AwaitTask |> Async.Ignore
                                with ex ->
                                    Dispatcher.UIThread.Post(fun () -> state.Set { state.Current with Status = ex.Message })
                            }
                            |> Async.Start)
                        w.Closed.Add(fun _ ->
                            popout.Close()
                            popWin <- None)
                        popWin <- Some w
                        w.Show()

            let refreshChrome () =
                async {
                    try
                        let! chrome = session.LoadChromeAsync() |> Async.AwaitTask
                        let! skillsEl = session.ListSkillsAsync() |> Async.AwaitTask
                        let! files = session.LoadWorkspaceFilesAsync() |> Async.AwaitTask
                        let! hooksEl = session.ListHooksAsync() |> Async.AwaitTask
                        let! pluginsEl = session.ListPluginsAsync() |> Async.AwaitTask
                        let! promptsEl = session.ListPromptsAsync() |> Async.AwaitTask
                        let! memEl = session.ListMemoriesAsync() |> Async.AwaitTask
                        let! mcpEl = session.ListMcpAsync() |> Async.AwaitTask
                        let hookRows =
                            let mutable hdata = Unchecked.defaultof<JsonElement>
                            if hooksEl.ValueKind = JsonValueKind.Object && hooksEl.TryGetProperty("data", &hdata) && hdata.ValueKind = JsonValueKind.Array then
                                hdata.EnumerateArray()
                                |> Seq.map (fun x -> { HookRow.Name = jsStr x "name"; Event = jsStr x "event" })
                                |> Seq.toList
                            else
                                []
                        let skillRows =
                            let mutable data = Unchecked.defaultof<JsonElement>
                            if skillsEl.ValueKind = JsonValueKind.Object && skillsEl.TryGetProperty("data", &data) && data.ValueKind = JsonValueKind.Array then
                                data.EnumerateArray()
                                |> Seq.map (fun x -> { SkillRow.Name = jsStr x "name"; Path = jsStr x "path"; Enabled = jsBool x "enabled" })
                                |> Seq.toList
                            else
                                []
                        let pluginAvailable =
                            let mutable adata = Unchecked.defaultof<JsonElement>
                            if pluginsEl.ValueKind = JsonValueKind.Object && pluginsEl.TryGetProperty("available", &adata) && adata.ValueKind = JsonValueKind.Array then
                                adata.EnumerateArray()
                                |> Seq.map (fun x -> jsStr x "name")
                                |> Seq.filter (fun n -> n.Length > 0)
                                |> Seq.toList
                            else
                                []
                        let memoryNotes =
                            let mutable mdata = Unchecked.defaultof<JsonElement>
                            if memEl.ValueKind = JsonValueKind.Object && memEl.TryGetProperty("data", &mdata) && mdata.ValueKind = JsonValueKind.Array then
                                mdata.EnumerateArray()
                                |> Seq.map (fun x -> jsStr x "name")
                                |> Seq.filter (fun n -> n.Length > 0)
                                |> Seq.toList
                            else
                                []
                        let promptRows =
                            let mutable pdata = Unchecked.defaultof<JsonElement>
                            if promptsEl.ValueKind = JsonValueKind.Object && promptsEl.TryGetProperty("data", &pdata) && pdata.ValueKind = JsonValueKind.Array then
                                pdata.EnumerateArray()
                                |> Seq.map (fun x -> { PromptRow.Name = jsStr x "name"; Path = jsStr x "path" })
                                |> Seq.filter (fun r -> r.Name.Length > 0)
                                |> Seq.toList
                            else
                                []
                        let mcpRows =
                            let mutable mcpData = Unchecked.defaultof<JsonElement>
                            if mcpEl.ValueKind = JsonValueKind.Object && mcpEl.TryGetProperty("data", &mcpData) && mcpData.ValueKind = JsonValueKind.Array then
                                mcpData.EnumerateArray()
                                |> Seq.map (fun x ->
                                    let n = jsStr x "name"
                                    let url = jsStr x "url"
                                    let cmd = jsStr x "command"
                                    { McpRow.Name = n; Transport = if url.Length > 0 then url else cmd })
                                |> Seq.filter (fun r -> r.Name.Length > 0)
                                |> Seq.toList
                            else
                                []
                        Dispatcher.UIThread.Post(fun () ->
                            if not (String.IsNullOrWhiteSpace chrome.Model) then
                                modelDraft.Set chrome.Model
                            state.Set
                                { state.Current with
                                    Status = chrome.Status
                                    McpRows = mcpRows
                                    SideNotes = chrome.SideNotes + Environment.NewLine + Environment.NewLine + "Sandbox" + Environment.NewLine + chrome.SandboxNote
                                    Skills = chrome.Skills
                                    SkillRows = skillRows
                                    HookRows = hookRows
                                    WorkspaceFiles = files |> Seq.toList
                                    Models = chrome.Models |> Seq.toList
                                    ShowOnboarding = chrome.NeedsAuth && not state.Current.OnboardingDismissed
                                    ShowTrust = chrome.NeedsTrust && not state.Current.TrustDismissed && not (chrome.NeedsAuth && not state.Current.OnboardingDismissed)
                                    TrustTarget = chrome.TrustTarget
                                    RemoteControl = chrome.RemoteControlStatus
                                    RateLimits = chrome.RateLimitsNote
                                    ComputerUse = chrome.ComputerUse
                                    BrowserUse = chrome.BrowserUse
                                    FeatureRows =
                                        chrome.Features
                                        |> Seq.map (fun f -> { FeatureRow.Name = f.Name; Enabled = f.Enabled; Stage = f.Stage })
                                        |> Seq.toList
                                    PluginAvailable = pluginAvailable
                                    PromptRows = promptRows
                                    MemoryNotes = memoryNotes
                                    TuiVim = chrome.TuiVim
                                    CollaborationMode = chrome.CollaborationMode
                                    Personality = chrome.Personality
                                    Effort = chrome.Effort
                                    TuiRaw = chrome.TuiRaw
                                    TuiTitle = chrome.TuiTitle })
                    with _ ->
                        ()
                }
                |> Async.Start

            let applyGoalPayload (el: JsonElement) =
                let mutable goal = Unchecked.defaultof<JsonElement>
                let objective, status =
                    if el.TryGetProperty("goal", &goal) && goal.ValueKind = JsonValueKind.Object then
                        let mutable obj = Unchecked.defaultof<JsonElement>
                        let mutable st = Unchecked.defaultof<JsonElement>
                        let o =
                            if goal.TryGetProperty("objective", &obj) && obj.ValueKind = JsonValueKind.String then obj.GetString() else ""
                        let s =
                            if goal.TryGetProperty("status", &st) && st.ValueKind = JsonValueKind.String then ThreadGoal.normalizeStatus (st.GetString()) else ThreadGoal.Cleared
                        (if String.IsNullOrWhiteSpace o then "" else o), (if String.IsNullOrWhiteSpace o then ThreadGoal.Cleared else s)
                    else
                        "", ThreadGoal.Cleared
                state.Set { state.Current with GoalObjective = objective; GoalStatus = status }

            let refreshGoal () =
                async {
                    try
                        let! g = session.GetGoalAsync() |> Async.AwaitTask
                        Dispatcher.UIThread.Post(fun () -> applyGoalPayload g)
                    with _ ->
                        Dispatcher.UIThread.Post(fun () ->
                            state.Set { state.Current with GoalObjective = ""; GoalStatus = ThreadGoal.Cleared })
                }
                |> Async.Start

            let writeConfig (key: string) (value: string) =
                async {
                    do! session.WriteConfigAsync(key, value) |> Async.AwaitTask |> Async.Ignore
                    if key = "tui_theme" then
                        Dispatcher.UIThread.Post(fun () -> applyWindowTheme ())
                    refreshChrome ()
                }
                |> Async.Start

            let toggleFeature (name: string) (enabled: bool) =
                if HonestStubs.IsLockedFeature name then
                    ()
                else
                    async {
                        do! session.SetExperimentalFeatureAsync(name, not enabled) |> Async.AwaitTask |> Async.Ignore
                        refreshChrome ()
                    }
                    |> Async.Start

            let insertMention (path: string) =
                let cleaned = path.Replace("\\", "/").Replace("[d] ", "")
                let next = (state.Current.Composer + " @" + cleaned).Trim()
                state.Set { state.Current with Composer = next }

            ctx.useEffect (
                handler =
                    (fun _ ->
                        session.add_Event (
                            Action<AgentEvent>(fun evt ->
                                Dispatcher.UIThread.Post(fun () ->
                                    applyEvent state evt
                                    match evt with
                                    | AgentEvent.TurnCompleted _ ->
                                        async {
                                            try
                                                do! session.QueueStartAsync() |> Async.AwaitTask |> Async.Ignore
                                                let! listed = session.QueueListAsync() |> Async.AwaitTask
                                                let items = parseQueue listed
                                                Dispatcher.UIThread.Post(fun () ->
                                                    state.Set { state.Current with QueueItems = items; QueueCount = items.Length })
                                            with _ ->
                                                ()
                                        }
                                        |> Async.Start
                                    | _ -> ())))
                        session.add_FsChanged (
                            Action<string, string>(fun _ path ->
                                Dispatcher.UIThread.Post(fun () ->
                                    if path <> "" then
                                        state.Set { state.Current with FsNote = path })))
                        session.add_ExecOutput (
                            Action<string, string>(fun pid text ->
                                Dispatcher.UIThread.Post(fun () ->
                                    if pid = state.Current.TermPid && text <> "" then
                                        let clipped =
                                            let next = state.Current.Terminal + text
                                            if next.Length > 8000 then next.Substring(next.Length - 8000) else next
                                        state.Set { state.Current with Terminal = clipped })))
                        session.add_FuzzyHits (
                            Action<string, string>(fun sid text ->
                                Dispatcher.UIThread.Post(fun () ->
                                    if sid = "desktop-search" then
                                        let hits =
                                            text.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                                            |> Array.map (fun s -> s.Trim())
                                            |> Array.filter (fun s -> s.Length > 0)
                                            |> Array.toList
                                        state.Set { state.Current with SearchHits = hits })))
                        async {
                            do! session.InitializeAsync() |> Async.AwaitTask
                            match startupLink with
                            | Some link when link.Kind = DeepLinkKind.OpenThread && not (String.IsNullOrWhiteSpace link.ThreadId) ->
                                do! session.ResumeThreadAsync link.ThreadId |> Async.AwaitTask
                                let! items = session.ListItemsAsync() |> Async.AwaitTask
                                Dispatcher.UIThread.Post(fun () ->
                                    state.Set
                                        { state.Current with
                                            Timeline = itemsToTimeline items
                                            Header = session.Title
                                            Busy = false })
                            | Some link when link.Kind = DeepLinkKind.NewThread ->
                                let! _ = session.StartThreadAsync(cwd = link.WorkspacePath) |> Async.AwaitTask
                                Dispatcher.UIThread.Post(fun () ->
                                    state.Set { state.Current with Busy = false; Header = "New thread" })
                            | Some link when link.Kind = DeepLinkKind.Settings ->
                                let! _ = session.StartThreadAsync() |> Async.AwaitTask
                                Dispatcher.UIThread.Post(fun () ->
                                    state.Set { state.Current with Busy = false; ShowSettings = true })
                            | Some link when link.Kind = DeepLinkKind.Skills ->
                                let! _ = session.StartThreadAsync() |> Async.AwaitTask
                                Dispatcher.UIThread.Post(fun () ->
                                    state.Set { state.Current with Busy = false; Status = "Skills" })
                            | _ ->
                                let! _ = session.StartThreadAsync() |> Async.AwaitTask
                                Dispatcher.UIThread.Post(fun () ->
                                    state.Set { state.Current with Busy = false; Status = state.Current.Status })
                            refreshThreads ()
                            refreshChrome ()
                            async {
                                try
                                    let! cfg = session.ReadConfigAsync() |> Async.AwaitTask
                                    let cwd = jsStr cfg "cwd"
                                    if cwd <> "" then
                                        do! session.WatchFsAsync(cwd, "desktop-cwd") |> Async.AwaitTask |> Async.Ignore
                                        do! session.FuzzySessionStartAsync("desktop-search", cwd) |> Async.AwaitTask |> Async.Ignore
                                with _ ->
                                    ()
                            }
                            |> Async.Start
                        }
                        |> Async.Start),
                triggers = [ EffectTrigger.AfterInit ]
            )

            let applyEditor (e: Avalonia.Input.KeyEventArgs) (fn: ComposerBuffer -> unit) =
                e.Handled <- true
                let vim = composerVim.Current
                let buf = vim.Buffer
                match e.Source with
                | :? TextBox as tb -> buf.Adopt(tb.Text, tb.CaretIndex)
                | _ -> buf.Adopt(state.Current.Composer, buf.Cursor)
                fn buf
                state.Set { state.Current with Composer = buf.Text; VimMode = vim.ModeLabel }
                match e.Source with
                | :? TextBox as tb ->
                    tb.CaretIndex <- max 0 (min buf.Cursor buf.Text.Length)
                | _ -> ()

            let vimChar (e: Avalonia.Input.KeyEventArgs) =
                let shift = e.KeyModifiers.HasFlag Avalonia.Input.KeyModifiers.Shift
                if e.Key >= Avalonia.Input.Key.A && e.Key <= Avalonia.Input.Key.Z then
                    let n = int e.Key - int Avalonia.Input.Key.A
                    if shift then char (int 'A' + n) else char (int 'a' + n)
                elif e.Key = Avalonia.Input.Key.D0 then '0'
                elif e.Key = Avalonia.Input.Key.D4 && shift then '$'
                elif e.Key = Avalonia.Input.Key.OemPeriod then '.'
                elif e.Key = Avalonia.Input.Key.OemComma then ','
                elif e.Key = Avalonia.Input.Key.OemSemicolon then ';'
                else char 0

            let applyVim (e: Avalonia.Input.KeyEventArgs) =
                if state.Current.TuiVim <> "true" then false
                else
                    let vim = composerVim.Current
                    vim.Enabled <- true
                    let buf = vim.Buffer
                    match e.Source with
                    | :? TextBox as tb ->
                        if vim.Mode = ComposerVimMode.Insert || buf.Text <> tb.Text then
                            buf.Adopt(tb.Text, tb.CaretIndex)
                        else
                            buf.MoveTo tb.CaretIndex
                    | _ -> ()
                    let input =
                        ComposerVimInput(
                            vimChar e,
                            e.Key = Avalonia.Input.Key.Escape,
                            e.Key = Avalonia.Input.Key.Enter,
                            e.Key = Avalonia.Input.Key.Left,
                            e.Key = Avalonia.Input.Key.Right,
                            e.Key = Avalonia.Input.Key.Home,
                            e.Key = Avalonia.Input.Key.End,
                            e.Key = Avalonia.Input.Key.Delete,
                            e.Key = Avalonia.Input.Key.Back,
                            e.KeyModifiers.HasFlag Avalonia.Input.KeyModifiers.Control,
                            e.KeyModifiers.HasFlag Avalonia.Input.KeyModifiers.Alt)
                    if not (vim.TryHandle input) then false
                    else
                        e.Handled <- true
                        state.Set { state.Current with Composer = buf.Text; VimMode = vim.ModeLabel }
                        match e.Source with
                        | :? TextBox as tb -> tb.CaretIndex <- max 0 (min buf.Cursor buf.Text.Length)
                        | _ -> ()
                        true

            let send () =
                let text = state.Current.Composer.Trim()
                if text.StartsWith("/") then
                    wipeComposerBuffer ()
                    state.Set { state.Current with Composer = ""; VimMode = "insert" }
                    let addNote kind body =
                        let item =
                            { TimelineItem.Id = Ids.item ()
                              Kind = kind
                              ToolName = ""
                              Text = body
                              Status = ""
                              ToolCallId = "" }
                        state.Set { state.Current with Timeline = state.Current.Timeline @ [item] }
                    let jsList (el: JsonElement) =
                        let mutable data = Unchecked.defaultof<JsonElement>
                        if el.ValueKind = JsonValueKind.Object && el.TryGetProperty("data", &data) && data.ValueKind = JsonValueKind.Array then
                            data.EnumerateArray()
                            |> Seq.map (fun x ->
                                let mutable n = Unchecked.defaultof<JsonElement>
                                if x.ValueKind = JsonValueKind.Object && x.TryGetProperty("name", &n) && n.ValueKind = JsonValueKind.String then n.GetString()
                                elif x.ValueKind = JsonValueKind.String then x.GetString()
                                else x.ToString())
                            |> Seq.filter (fun s -> not (String.IsNullOrWhiteSpace s))
                            |> Seq.toList
                        else
                            []
                    if text = "/help" then
                        addNote "agent_message" "/new /compact /fork /side /archive /rollback /exec CMD /review /queue /name TITLE /rename TITLE /effort low|medium|high /ide /rollout /plan /stop /resume ID /agents ID /status /skills /hooks /mcp /diff /worktree /memory /model NAME /sandbox MODE /logout /init /export /recap /clear /pwd /cd DIR /plugins /experimental /prompts /permissions /personality NAME /usage /copy /keymap /pets /pet /cloud /cloud-environment /setup-default-sandbox /vim /theme /statusline /pin /search TERM /goal [TEXT|pause|resume|clear] /feedback /approve /import /raw /subagents /execpolicy /history /help"
                    elif text = "/task" then
                        startQuickChat false
                    elif text = "/new" then
                        async {
                            let! _ = session.StartThreadAsync() |> Async.AwaitTask
                            Dispatcher.UIThread.Post(fun () ->
                                state.Set
                                    { state.Current with
                                        Timeline = []
                                        Threads = state.Current.Threads
                                        Header = "New thread"
                                        Busy = false
                                        Plan = ""
                                        Diff = ""
                                        Status = state.Current.Status })
                        }
                        |> Async.Start
                    elif text = "/compact" then
                        async {
                            do! session.CompactAsync() |> Async.AwaitTask |> Async.Ignore
                            let! items = session.ListItemsAsync() |> Async.AwaitTask
                            Dispatcher.UIThread.Post(fun () ->
                                state.Set { state.Current with Timeline = itemsToTimeline items; Status = state.Current.Status })
                        }
                        |> Async.Start
                    elif text = "/fork" || text = "/side" || text.StartsWith("/btw") then
                        async {
                            let! _ = session.ForkAsync() |> Async.AwaitTask
                            let! items = session.ListItemsAsync() |> Async.AwaitTask
                            Dispatcher.UIThread.Post(fun () ->
                                state.Set
                                    { state.Current with
                                        Timeline = itemsToTimeline items
                                        Threads = state.Current.Threads
                                        Header = session.Title
                                        Status = state.Current.Status })
                        }
                        |> Async.Start
                    elif text = "/archive" then
                        async {
                            do! session.ArchiveAsync() |> Async.AwaitTask |> Async.Ignore
                            let! _ = session.StartThreadAsync() |> Async.AwaitTask
                            Dispatcher.UIThread.Post(fun () ->
                                state.Set
                                    { state.Current with
                                        Timeline = []
                                        Threads = state.Current.Threads
                                        Header = "New thread"
                                        Status = state.Current.Status })
                        }
                        |> Async.Start
                    elif text = "/ide" then
                        let bits =
                            [ "TERM_PROGRAM"; "VSCODE_PID"; "VSCODE_INJECTION"; "CURSOR_SESSION_ID" ]
                            |> List.choose (fun n ->
                                match Environment.GetEnvironmentVariable n with
                                | null | "" -> None
                                | v -> Some (n + "=" + v))
                        let msg =
                            if bits.IsEmpty then "No IDE env. CodexSharp does not speak the VS Code/Cursor Codex extension protocol."
                            else String.Join(Environment.NewLine, bits) + Environment.NewLine + "no selection/open-file bridge"
                        addNote "agent_message" msg
                    elif text = "/rollout" then
                        async {
                            try
                                let! rollout = session.RolloutPathAsync() |> Async.AwaitTask
                                let path = jsStr rollout "path"
                                copyToClipboard path
                                Dispatcher.UIThread.Post(fun () -> addNote "agent_message" path)
                            with ex ->
                                Dispatcher.UIThread.Post(fun () -> addNote "error" ex.Message)
                        }
                        |> Async.Start
                    elif text = "/review" then
                        async {
                            do! session.ReviewAsync() |> Async.AwaitTask |> Async.Ignore
                        }
                        |> Async.Start
                    elif text = "/queue" then
                        async {
                            do! session.QueueStartAsync() |> Async.AwaitTask |> Async.Ignore
                        }
                        |> Async.Start
                    elif text = "/rollback" then
                        async {
                            do! session.RollbackAsync(1) |> Async.AwaitTask |> Async.Ignore
                            let! items = session.ListItemsAsync() |> Async.AwaitTask
                            Dispatcher.UIThread.Post(fun () ->
                                state.Set { state.Current with Timeline = itemsToTimeline items; Status = state.Current.Status })
                        }
                        |> Async.Start
                    elif text.StartsWith("/exec ") then
                        let command = text.Substring(6).Trim()
                        async {
                            try
                                let! result = session.ExecCommandAsync(command) |> Async.AwaitTask
                                let stdout =
                                    let mutable v = Unchecked.defaultof<System.Text.Json.JsonElement>
                                    if result.TryGetProperty("stdout", &v) && v.ValueKind = System.Text.Json.JsonValueKind.String then v.GetString()
                                    else result.ToString()
                                Dispatcher.UIThread.Post(fun () ->
                                    let item =
                                        { TimelineItem.Id = Ids.item ()
                                          Kind = "command_execution"
                                          ToolName = "command/exec"
                                          Text = stdout
                                          Status = "completed"
                                          ToolCallId = "" }
                                    state.Set { state.Current with Timeline = state.Current.Timeline @ [item]; Status = state.Current.Status })
                            with ex ->
                                Dispatcher.UIThread.Post(fun () ->
                                    state.Set
                                        { state.Current with
                                            Timeline =
                                                state.Current.Timeline
                                                @ [ { TimelineItem.Id = Ids.item (); Kind = "error"; ToolName = ""; Text = ex.Message; Status = "failed"; ToolCallId = "" } ] })
                        }
                        |> Async.Start
                    elif text.StartsWith("/name ") then
                        let name = text.Substring(6).Trim()
                        session.SetNameAsync name |> ignore
                        state.Set { state.Current with Header = name }
                    elif text.StartsWith("/effort ") then
                        let rest = text.Substring(8).Trim()
                        writeConfig "model_reasoning_effort" rest
                    elif text = "/stop" then
                        session.InterruptAsync() |> ignore
                        addNote "agent_message" "Interrupted the in-flight turn."
                    elif text = "/agents" || text = "/agents --all" || text = "/agents all" then
                        let all = text.EndsWith("--all") || text.EndsWith(" all")
                        let cwd = if all then null else Environment.CurrentDirectory
                        async {
                            try
                                let! listed = session.ReadThreadAsync() |> Async.AwaitTask
                                let mutable thread = Unchecked.defaultof<JsonElement>
                                let mutable agents = Unchecked.defaultof<JsonElement>
                                let snaps =
                                    if listed.TryGetProperty("thread", &thread) && thread.TryGetProperty("subagents", &agents) && agents.ValueKind = JsonValueKind.Array then
                                        agents.EnumerateArray()
                                        |> Seq.map (fun a ->
                                            let mutable idEl = Unchecked.defaultof<JsonElement>
                                            let mutable doneEl = Unchecked.defaultof<JsonElement>
                                            let mutable resultEl = Unchecked.defaultof<JsonElement>
                                            let id = if a.TryGetProperty("id", &idEl) && idEl.ValueKind = JsonValueKind.String then idEl.GetString() else "?"
                                            let doneFlag = a.TryGetProperty("done", &doneEl) && doneEl.ValueKind = JsonValueKind.True
                                            let result = if a.TryGetProperty("result", &resultEl) && resultEl.ValueKind = JsonValueKind.String then resultEl.GetString() else null
                                            SubagentSnapshot(id, doneFlag, result))
                                        |> Seq.toList
                                    else
                                        []
                                Dispatcher.UIThread.Post(fun () -> addNote "agent_message" (AgentsOverview.Render(cwd, snaps, all)))
                            with _ ->
                                Dispatcher.UIThread.Post(fun () -> addNote "agent_message" (AgentsOverview.Render(cwd, [], all)))
                        }
                        |> Async.Start
                    elif text.StartsWith("/agents ") || text.StartsWith("/resume ") then
                        let id = text.Substring(text.IndexOf(' ') + 1).Trim()
                        async {
                            do! session.ResumeThreadAsync id |> Async.AwaitTask
                            let! items = session.ListItemsAsync() |> Async.AwaitTask
                            let! goal = session.GetGoalAsync() |> Async.AwaitTask
                            Dispatcher.UIThread.Post(fun () ->
                                applyGoalPayload goal
                                state.Set
                                    { state.Current with
                                        Timeline = itemsToTimeline items
                                        Header = session.Title
                                        Busy = false
                                        Status = state.Current.Status
                                        GoalObjective = state.Current.GoalObjective
                                        GoalStatus = state.Current.GoalStatus })
                        }
                        |> Async.Start
                    elif text = "/resume" then
                        addNote "agent_message" "usage: /resume THREAD_ID"
                    elif text = "/vim" then
                        let next = if state.Current.TuiVim = "true" then "false" else "true"
                        writeConfig "tui_vim" next
                        state.Set { state.Current with VimMode = "insert" }
                        addNote "agent_message" ("vim " + next + "  esc normal · i/a/I/A/o/O insert · v/V visual · hjkl G gg 0$ w/b e x dd dw c y p P u . f t r")
                    elif text.StartsWith("/theme ") then
                        writeConfig "tui_theme" (text.Substring(7).Trim())
                        addNote "agent_message" ("theme " + text.Substring(7).Trim() + "  (FuncUI chrome; not ratatui)")
                    elif text.StartsWith("/statusline ") then
                        writeConfig "tui_statusline" (text.Substring(12).Trim())
                        addNote "agent_message" ("statusline " + text.Substring(12).Trim())
                    elif text = "/title" then
                        addNote "agent_message" ("title " + (if String.IsNullOrWhiteSpace state.Current.TuiTitle then "thread,model" else state.Current.TuiTitle))
                    elif text.StartsWith("/title ") then
                        let rest = text.Substring(7).Trim()
                        writeConfig "tui_title" rest
                        addNote "agent_message" ("title " + rest)
                    elif text.StartsWith("/notifications") then
                        let rest = if text.Length > 14 then text.Substring(14).Trim() else ""
                        if rest <> "" then writeConfig "tui_notifications" rest
                        addNote "agent_message" ("notifications " + (if rest = "" then DesktopNotify.Method else rest))
                    elif text = "/plan" || text = "/default" || text = "/pair" then
                        let mode = text.Substring(1)
                        writeConfig "collaboration_mode" mode
                        addNote "agent_message" ("collaboration " + mode + (if mode = "plan" then ". Mutating tools are blocked." else "."))
                    elif text = "/status" then
                        async {
                            let! chrome = session.LoadChromeAsync() |> Async.AwaitTask
                            Dispatcher.UIThread.Post(fun () -> addNote "agent_message" chrome.Status)
                        }
                        |> Async.Start
                    elif text = "/skills" then
                        async {
                            let! listed = session.ListSkillsAsync() |> Async.AwaitTask
                            Dispatcher.UIThread.Post(fun () ->
                                let names = jsList listed
                                addNote "agent_message" (if names.IsEmpty then "No SKILL.md found." else String.Join(Environment.NewLine, names)))
                        }
                        |> Async.Start
                    elif text = "/hooks" then
                        async {
                            let! listed = session.ListHooksAsync() |> Async.AwaitTask
                            Dispatcher.UIThread.Post(fun () ->
                                let names = jsList listed
                                addNote "agent_message" (if names.IsEmpty then "No hooks in ~/.codexsharp/hooks" else String.Join(Environment.NewLine, names)))
                        }
                        |> Async.Start
                    elif text = "/mcp" then
                        async {
                            let! listed = session.ListMcpAsync() |> Async.AwaitTask
                            Dispatcher.UIThread.Post(fun () ->
                                let names = jsList listed
                                addNote "agent_message" (if names.IsEmpty then "No mcp_servers in config.toml" else String.Join(Environment.NewLine, names)))
                        }
                        |> Async.Start
                    elif text = "/plugins" then
                        async {
                            let! listed = session.ListPluginsAsync() |> Async.AwaitTask
                            Dispatcher.UIThread.Post(fun () ->
                                let names = jsList listed
                                addNote "agent_message" (if names.IsEmpty then "No plugins" else String.Join(Environment.NewLine, names)))
                        }
                        |> Async.Start
                    elif text = "/prompts" then
                        async {
                            let! listed = session.ListPromptsAsync() |> Async.AwaitTask
                            Dispatcher.UIThread.Post(fun () ->
                                let names = jsList listed
                                addNote "agent_message" (if names.IsEmpty then "No prompts" else String.Join(Environment.NewLine, names)))
                        }
                        |> Async.Start
                    elif text = "/diff" then
                        addNote "agent_message" (if String.IsNullOrWhiteSpace state.Current.Diff then "No git diff loaded." else state.Current.Diff)
                    elif text = "/worktree" || text.StartsWith("/worktree ") then
                        async {
                            try
                                let! result = session.StartWorktreeAsync(Environment.CurrentDirectory) |> Async.AwaitTask
                                let cwd = jsStr result "cwd"
                                Dispatcher.UIThread.Post(fun () ->
                                    state.Set
                                        { state.Current with
                                            Timeline = []
                                            Header = "Worktree"
                                            Status = if String.IsNullOrWhiteSpace cwd then "worktree" else cwd })
                            with ex ->
                                Dispatcher.UIThread.Post(fun () -> addNote "error" ex.Message)
                        }
                        |> Async.Start
                    elif text = "/memory" || text = "/memories" || text.StartsWith("/memory ") || text.StartsWith("/memories ") then
                        let rest =
                            if text.StartsWith("/memories") then (if text.Length > 9 then text.Substring(9).Trim() else "")
                            else (if text.Length > 7 then text.Substring(7).Trim() else "")
                        async {
                            try
                                if rest.StartsWith("add ") || rest.StartsWith("write ") then
                                    let payload = rest.Substring(rest.IndexOf(' ') + 1).Trim()
                                    let parts = payload.Split([| ' ' |], 2, StringSplitOptions.RemoveEmptyEntries)
                                    if parts.Length < 2 then
                                        Dispatcher.UIThread.Post(fun () -> addNote "agent_message" "usage: /memory add NAME TEXT")
                                    else
                                        let! written = session.WriteMemoryAsync(parts.[0], parts.[1]) |> Async.AwaitTask
                                        Dispatcher.UIThread.Post(fun () -> addNote "agent_message" ("wrote " + jsStr written "name"))
                                elif rest.StartsWith("read ") then
                                    let! note = session.ReadMemoryAsync(rest.Substring(5).Trim()) |> Async.AwaitTask
                                    Dispatcher.UIThread.Post(fun () -> addNote "agent_message" (jsStr note "text"))
                                else
                                    let! listed = session.ListMemoriesAsync() |> Async.AwaitTask
                                    Dispatcher.UIThread.Post(fun () ->
                                        let names = jsList listed
                                        addNote "agent_message" (if names.IsEmpty then "No memories." else String.Join(Environment.NewLine, names)))
                            with ex ->
                                Dispatcher.UIThread.Post(fun () -> addNote "error" ex.Message)
                        }
                        |> Async.Start
                    elif text.StartsWith("/model ") then
                        let rest = text.Substring(7).Trim()
                        session.UpdateSettingsAsync(model = rest) |> ignore
                        writeConfig "model" rest
                        addNote "agent_message" ("model " + rest)
                    elif text.StartsWith("/sandbox ") then
                        let rest = text.Substring(9).Trim()
                        session.UpdateSettingsAsync(sandboxMode = rest) |> ignore
                        writeConfig "sandbox_mode" rest
                        addNote "agent_message" ("sandbox " + rest)
                    elif text = "/sandbox-add-read-dir" || text.StartsWith("/sandbox-add-read-dir ") then
                        let rest = if text.Length > 21 then text.Substring(21).Trim() else ""
                        if rest = "" then
                            async {
                                try
                                    let! listed = session.ListExtraReadRootsAsync() |> Async.AwaitTask
                                    let mutable data = Unchecked.defaultof<JsonElement>
                                    let lines =
                                        if listed.TryGetProperty("data", &data) && data.ValueKind = JsonValueKind.Array then
                                            data.EnumerateArray()
                                            |> Seq.map (fun r -> if r.ValueKind = JsonValueKind.String then r.GetString() else "")
                                            |> Seq.filter (fun s -> not (String.IsNullOrWhiteSpace s))
                                            |> fun xs -> String.Join(Environment.NewLine, xs)
                                        else ""
                                    Dispatcher.UIThread.Post(fun () -> addNote "agent_message" (if String.IsNullOrWhiteSpace lines then "usage: /sandbox-add-read-dir PATH" else lines))
                                with ex ->
                                    Dispatcher.UIThread.Post(fun () -> addNote "error" ex.Message)
                            }
                            |> Async.Start
                        else
                            async {
                                try
                                    let! added = session.AddExtraReadRootAsync(Path.GetFullPath(rest, Environment.CurrentDirectory)) |> Async.AwaitTask
                                    Dispatcher.UIThread.Post(fun () -> addNote "agent_message" ("read-root " + jsStr added "path"))
                                with ex ->
                                    Dispatcher.UIThread.Post(fun () -> addNote "error" ex.Message)
                            }
                            |> Async.Start
                    elif text.StartsWith("/cd ") then
                        let rest = text.Substring(4).Trim()
                        session.UpdateSettingsAsync(cwd = rest) |> ignore
                        addNote "agent_message" ("cwd " + rest)
                    elif text = "/pwd" then
                        async {
                            let! cfg = session.ReadConfigAsync() |> Async.AwaitTask
                            Dispatcher.UIThread.Post(fun () -> addNote "agent_message" (jsStr cfg "cwd"))
                        }
                        |> Async.Start
                    elif text = "/logout" then
                        session.LogoutAsync() |> ignore
                        addNote "agent_message" "Logged out. auth.json removed if it existed."
                    elif text = "/init" then
                        async {
                            try
                                let! written = session.WriteAgentsMarkdownAsync() |> Async.AwaitTask
                                let created = jsBool written "created"
                                Dispatcher.UIThread.Post(fun () -> addNote "agent_message" (if created then "wrote AGENTS.md" else "AGENTS.md already exists"))
                            with ex ->
                                Dispatcher.UIThread.Post(fun () -> addNote "error" ex.Message)
                        }
                        |> Async.Start
                    elif text = "/apply" then
                        async {
                            try
                                let! result = session.ApplyLastPatchAsync(session.ThreadId) |> Async.AwaitTask
                                let ok = jsBool result "ok"
                                let output = jsStr result "output"
                                let cwd = jsStr result "cwd"
                                Dispatcher.UIThread.Post(fun () ->
                                    addNote (if ok then "agent_message" else "error") (if ok then "applied last patch in " + cwd else output + " (cloud apply is notConfigured)"))
                            with ex ->
                                Dispatcher.UIThread.Post(fun () -> addNote "error" ex.Message)
                        }
                        |> Async.Start
                    elif text = "/export" || text.StartsWith("/export ") then
                        async {
                            try
                                let dest = if text.Length > 7 then text.Substring(7).Trim() else ""
                                let! result = session.ExportThreadAsync(if dest = "" then null else dest) |> Async.AwaitTask
                                let path = jsStr result "path"
                                Dispatcher.UIThread.Post(fun () -> addNote "agent_message" ("wrote " + path))
                            with ex ->
                                Dispatcher.UIThread.Post(fun () -> addNote "error" ex.Message)
                        }
                        |> Async.Start
                    elif text = "/recap" then
                        async {
                            try
                                let! recap = session.RecapAsync() |> Async.AwaitTask
                                let status = jsStr recap "status"
                                let body = jsStr recap "recap"
                                let msg =
                                    if status = "empty" || String.IsNullOrWhiteSpace body then "There is no conversation history to recap."
                                    elif status = "errored" || status = "busy" then body
                                    else body
                                Dispatcher.UIThread.Post(fun () -> addNote (if status = "errored" || status = "busy" then "error" else "agent_message") msg)
                            with ex ->
                                Dispatcher.UIThread.Post(fun () -> addNote "error" ex.Message)
                        }
                        |> Async.Start
                    elif text = "/clear" then
                        async {
                            let! _ = session.StartThreadAsync() |> Async.AwaitTask
                            Dispatcher.UIThread.Post(fun () ->
                                state.Set
                                    { state.Current with
                                        Timeline = []
                                        Plan = ""
                                        Diff = ""
                                        Header = "New thread"
                                        Busy = false
                                        Toast = "" })
                        }
                        |> Async.Start
                    elif text = "/delete" then
                        async {
                            try
                                do! session.DeleteThreadAsync(session.ThreadId) |> Async.AwaitTask |> Async.Ignore
                            with _ ->
                                ()
                            let! _ = session.StartThreadAsync() |> Async.AwaitTask
                            Dispatcher.UIThread.Post(fun () ->
                                state.Set
                                    { state.Current with
                                        Timeline = []
                                        Plan = ""
                                        Diff = ""
                                        Header = "New thread"
                                        Busy = false
                                        Status = "deleted previous thread" })
                        }
                        |> Async.Start
                    elif text = "/experimental" then
                        async {
                            let! listed = session.ListExperimentalFeaturesAsync() |> Async.AwaitTask
                            Dispatcher.UIThread.Post(fun () ->
                                let names = jsList listed
                                addNote "agent_message" (if names.IsEmpty then "No experimental features." else String.Join(Environment.NewLine, names)))
                        }
                        |> Async.Start
                    elif text = "/permissions" || text.StartsWith("/permissions ") then
                        let rest = if text.Length > 12 then text.Substring(12).Trim() else ""
                        if rest <> "" then
                            session.UpdateSettingsAsync(approvalPolicy = rest) |> ignore
                            writeConfig "approval_policy" rest
                        addNote "agent_message" (if rest = "" then "usage: /permissions on-request|never|untrusted" else "approval " + rest)
                    elif text.StartsWith("/personality ") then
                        let rest = text.Substring(13).Trim()
                        writeConfig "personality" rest
                        addNote "agent_message" ("personality " + rest)
                    elif text = "/usage" then
                        async {
                            try
                                let! u = session.ReadUsageAsync() |> Async.AwaitTask
                                let mutable tu = Unchecked.defaultof<JsonElement>
                                let msg =
                                    if u.TryGetProperty("threadUsage", &tu) && tu.ValueKind = JsonValueKind.Object then
                                        let mutable estEl = Unchecked.defaultof<JsonElement>
                                        let mutable lastEl = Unchecked.defaultof<JsonElement>
                                        let mutable est = 0L
                                        let mutable last = 0L
                                        if tu.TryGetProperty("estimatedTokens", &estEl) then estEl.TryGetInt64(&est) |> ignore
                                        if tu.TryGetProperty("lastTokens", &lastEl) then lastEl.TryGetInt64(&last) |> ignore
                                        sprintf "local estimate %d tok (last %d). ChatGPT billed usage is not available for local API-key sessions." est last
                                    else
                                        "no live thread token estimate. ChatGPT billed usage is not available for local API-key sessions."
                                Dispatcher.UIThread.Post(fun () -> addNote "agent_message" msg)
                            with ex ->
                                Dispatcher.UIThread.Post(fun () -> addNote "error" ex.Message)
                        }
                        |> Async.Start
                    elif text = "/copy" || text.StartsWith("/copy ") then
                        let rest = if text.Length > 5 then text.Substring(5).Trim() else ""
                        async {
                            try
                                let! copied = session.CopyLastAsync(if rest = "" then null else rest) |> Async.AwaitTask
                                let kind = jsStr copied "kind"
                                let body = jsStr copied "text"
                                Dispatcher.UIThread.Post(fun () ->
                                    if kind = "empty" || String.IsNullOrWhiteSpace body then
                                        addNote "error" "No agent message to copy."
                                    else
                                        copyToClipboard body
                                        addNote "agent_message" (if kind = "code" then "copied last code fence" else "copied last agent message"))
                            with ex ->
                                Dispatcher.UIThread.Post(fun () -> addNote "error" ex.Message)
                        }
                        |> Async.Start
                    elif text = "/keymap" || text.StartsWith("/keymap ") then
                        let rest = if text.Length > 7 then text.Substring(7).Trim() else ""
                        async {
                            try
                                if rest <> "" then
                                    let parts = rest.Split([| ' ' |], 3, StringSplitOptions.RemoveEmptyEntries)
                                    if parts.Length = 3 then
                                        let! _ = session.SetKeymapAsync(parts.[0], parts.[1], parts.[2]) |> Async.AwaitTask
                                        ()
                                let! listed = session.ListKeymapAsync() |> Async.AwaitTask
                                Dispatcher.UIThread.Post(fun () ->
                                    let lines = jsList listed
                                    addNote "agent_message" ((if lines.IsEmpty then "No keymap." else String.Join(Environment.NewLine, lines)) + Environment.NewLine + "Desktop: Enter send · Tab complete · Up/Down history · ctrl+g external editor"))
                            with ex ->
                                Dispatcher.UIThread.Post(fun () -> addNote "error" ex.Message)
                        }
                        |> Async.Start
                    elif text = "/cloud" || text.StartsWith("/cloud ") || text.StartsWith("/cloud-") then
                        addNote "agent_message" (HonestStubs.CloudSlashMessage())
                    elif text = "/pet" || text.StartsWith("/pet ") then
                        addNote "agent_message" (HonestStubs.PetOverlayMessage())
                    elif text = "/pets" || text.StartsWith("/pets ") then
                        let rest = if text.Length > 5 then text.Substring(5).Trim() else ""
                        async {
                            try
                                let! pet =
                                    if rest <> "" then session.SetPetsAsync rest |> Async.AwaitTask
                                    else session.ReadPetsAsync() |> Async.AwaitTask
                                Dispatcher.UIThread.Post(fun () ->
                                    addNote "agent_message" ("pet " + jsStr pet "id" + " " + jsStr pet "ascii" + Environment.NewLine + HonestStubs.PetOverlayMessage()))
                            with ex ->
                                Dispatcher.UIThread.Post(fun () -> addNote "error" ex.Message)
                        }
                        |> Async.Start
                    elif text = "/setup-default-sandbox" then
                        async {
                            let! snap = session.SetupWindowsSandboxAsync("unelevated") |> Async.AwaitTask
                            Dispatcher.UIThread.Post(fun () -> addNote "agent_message" (snap.ToString()))
                        }
                        |> Async.Start
                    elif text = "/doctor" then
                        async {
                            try
                                let! report = session.RunDoctorAsync() |> Async.AwaitTask
                                let mutable checks = Unchecked.defaultof<JsonElement>
                                let lines =
                                    if report.TryGetProperty("checks", &checks) && checks.ValueKind = JsonValueKind.Array then
                                        checks.EnumerateArray()
                                        |> Seq.map (fun c -> sprintf "%s  %s  %s" (jsStr c "status") (jsStr c "group") (jsStr c "summary"))
                                        |> fun rows -> String.Join(Environment.NewLine, rows)
                                    else
                                        report.ToString()
                                Dispatcher.UIThread.Post(fun () -> addNote "agent_message" lines)
                            with ex ->
                                Dispatcher.UIThread.Post(fun () -> addNote "error" ex.Message)
                        }
                        |> Async.Start
                    elif text = "/debug-config" then
                        async {
                            try
                                let! cfg = session.ReadConfigAsync() |> Async.AwaitTask
                                let msg = sprintf "cwd %s\nmodel %s\nsandbox %s\napproval %s" (jsStr cfg "cwd") (jsStr cfg "model") (jsStr cfg "sandboxMode") (jsStr cfg "approvalPolicy")
                                Dispatcher.UIThread.Post(fun () -> addNote "agent_message" (msg.Replace("\\n", Environment.NewLine)))
                            with ex ->
                                Dispatcher.UIThread.Post(fun () -> addNote "error" ex.Message)
                        }
                        |> Async.Start
                    elif text = "/mention" || text.StartsWith("/mention ") then
                        let q = if text.Length > 8 then text.Substring(8).Trim() else ""
                        if q = "" then
                            addNote "agent_message" "usage: /mention QUERY   (or type @ in the composer)"
                        else
                            async {
                                try
                                    let! hits = session.MentionHitsAsync("@" + q) |> Async.AwaitTask
                                    Dispatcher.UIThread.Post(fun () ->
                                        let list = hits |> Seq.toList
                                        match list with
                                        | [] -> addNote "agent_message" "no file matches"
                                        | [one] ->
                                            addNote "agent_message" ("mentioned " + one)
                                            state.Set { state.Current with Composer = "@" + one.Replace(char 92, '/') }
                                        | many -> addNote "agent_message" (String.Join(Environment.NewLine, many)))
                                with ex ->
                                    Dispatcher.UIThread.Post(fun () -> addNote "error" ex.Message)
                            }
                            |> Async.Start
                    elif text = "/ps" then
                        async {
                            try
                                let! listed = session.ListBackgroundTerminalsAsync() |> Async.AwaitTask
                                let mutable data = Unchecked.defaultof<JsonElement>
                                let lines =
                                    if listed.TryGetProperty("data", &data) && data.ValueKind = JsonValueKind.Array then
                                        data.EnumerateArray() |> Seq.map (fun x -> x.GetRawText()) |> Seq.toList
                                    else []
                                let extra = if String.IsNullOrWhiteSpace state.Current.TermPid then [] else [ "desktop-term " + state.Current.TermPid ]
                                let msg = if lines.IsEmpty && extra.IsEmpty then "no background terminals" else String.Join(Environment.NewLine, extra @ lines)
                                Dispatcher.UIThread.Post(fun () -> addNote "agent_message" msg)
                            with ex ->
                                Dispatcher.UIThread.Post(fun () -> addNote "error" ex.Message)
                        }
                        |> Async.Start
                    elif text = "/apps" then
                        async {
                            try
                                let! listed = session.ListAppsAsync() |> Async.AwaitTask
                                let mutable data = Unchecked.defaultof<JsonElement>
                                let n = if listed.TryGetProperty("data", &data) && data.ValueKind = JsonValueKind.Array then data.GetArrayLength() else 0
                                Dispatcher.UIThread.Post(fun () ->
                                    addNote "agent_message" (if n = 0 then "no hosted apps; remote OpenAI plugin-service is not configured" else sprintf "%d apps" n))
                            with ex ->
                                Dispatcher.UIThread.Post(fun () -> addNote "error" ex.Message)
                        }
                        |> Async.Start
                    elif text = "/pin" then
                        let id = session.ThreadId
                        let next = not (ThreadPins.IsPinned id)
                        async {
                            do! session.SetPinnedAsync(id, next) |> Async.AwaitTask |> Async.Ignore
                            Dispatcher.UIThread.Post(fun () ->
                                addNote "agent_message" (if next then "pinned" else "unpinned")
                                refreshThreads ())
                        }
                        |> Async.Start
                    elif text.StartsWith("/search") then
                        let term = if text.Length > 7 then text.Substring(7).Trim() else ""
                        if term = "" then addNote "error" "usage: /search term"
                        else
                            async {
                                try
                                    let! result = session.SearchThreadsAsync(term) |> Async.AwaitTask
                                    let mutable data = Unchecked.defaultof<JsonElement>
                                    let lines =
                                        if result.TryGetProperty("data", &data) && data.ValueKind = JsonValueKind.Array then
                                            data.EnumerateArray()
                                            |> Seq.map (fun hit ->
                                                let mutable th = Unchecked.defaultof<JsonElement>
                                                let mutable sn = Unchecked.defaultof<JsonElement>
                                                let title =
                                                    if hit.TryGetProperty("thread", &th) && th.ValueKind = JsonValueKind.Object then
                                                        let mutable titleEl = Unchecked.defaultof<JsonElement>
                                                        if th.TryGetProperty("title", &titleEl) && titleEl.ValueKind = JsonValueKind.String then titleEl.GetString() else ""
                                                    else ""
                                                let snippet = if hit.TryGetProperty("snippet", &sn) && sn.ValueKind = JsonValueKind.String then sn.GetString() else ""
                                                title + "  " + snippet)
                                            |> fun xs -> String.Join(Environment.NewLine, xs)
                                        else
                                            ""
                                    Dispatcher.UIThread.Post(fun () -> addNote "agent_message" (if String.IsNullOrWhiteSpace lines then "no matches" else lines))
                                with ex ->
                                    Dispatcher.UIThread.Post(fun () -> addNote "error" ex.Message)
                            }
                            |> Async.Start
                    elif text = "/goal" || text.StartsWith("/goal ") then
                        let rest = if text.Length > 5 then text.Substring(5).Trim() else ""
                        async {
                            try
                                match GoalSlash.parse rest with
                                | GoalSlash.Command.Show ->
                                    let! g = session.GetGoalAsync() |> Async.AwaitTask
                                    Dispatcher.UIThread.Post(fun () ->
                                        applyGoalPayload g
                                        let s = state.Current
                                        let msg =
                                            if String.IsNullOrWhiteSpace s.GoalObjective then "no goal"
                                            else s.GoalStatus + ": " + s.GoalObjective
                                        addNote "agent_message" msg)
                                | GoalSlash.Command.Clear ->
                                    do! session.ClearGoalAsync() |> Async.AwaitTask |> Async.Ignore
                                    Dispatcher.UIThread.Post(fun () ->
                                        state.Set { state.Current with GoalObjective = ""; GoalStatus = ThreadGoal.Cleared }
                                        addNote "agent_message" "goal cleared")
                                | GoalSlash.Command.Pause ->
                                    do! session.SetGoalAsync(null, ThreadGoal.Paused) |> Async.AwaitTask |> Async.Ignore
                                    let! g = session.GetGoalAsync() |> Async.AwaitTask
                                    Dispatcher.UIThread.Post(fun () ->
                                        applyGoalPayload g
                                        addNote "agent_message" (if String.IsNullOrWhiteSpace state.Current.GoalObjective then "no goal" else "goal paused: " + state.Current.GoalObjective))
                                | GoalSlash.Command.Resume ->
                                    do! session.SetGoalAsync(null, ThreadGoal.Active) |> Async.AwaitTask |> Async.Ignore
                                    let! g = session.GetGoalAsync() |> Async.AwaitTask
                                    Dispatcher.UIThread.Post(fun () ->
                                        applyGoalPayload g
                                        addNote "agent_message" (if String.IsNullOrWhiteSpace state.Current.GoalObjective then "no goal" else "goal: " + state.Current.GoalObjective))
                                | GoalSlash.Command.Set obj ->
                                    do! session.SetGoalAsync(obj, ThreadGoal.Active) |> Async.AwaitTask |> Async.Ignore
                                    Dispatcher.UIThread.Post(fun () ->
                                        state.Set { state.Current with GoalObjective = obj; GoalStatus = ThreadGoal.Active }
                                        addNote "agent_message" ("goal: " + obj))
                            with ex ->
                                Dispatcher.UIThread.Post(fun () -> addNote "error" ex.Message)
                        }
                        |> Async.Start
                    elif text = "/feedback" then
                        async {
                            let! _ = session.UploadFeedbackAsync("other", "desktop") |> Async.AwaitTask
                            Dispatcher.UIThread.Post(fun () -> addNote "agent_message" "saved local feedback snapshot (not uploaded)")
                        }
                        |> Async.Start
                    elif text = "/approve" then
                        addNote "agent_message" "No auto-review denial to retry. Guardian is not configured."
                    elif text = "/import" then
                        async {
                            try
                                let! detected = session.DetectExternalAgentsAsync([| Environment.CurrentDirectory |]) |> Async.AwaitTask
                                let mutable items = Unchecked.defaultof<JsonElement>
                                if not (detected.TryGetProperty("items", &items)) || items.ValueKind <> JsonValueKind.Array || items.GetArrayLength() = 0 then
                                    Dispatcher.UIThread.Post(fun () -> addNote "agent_message" "nothing to import")
                                else
                                    let! imported = session.ImportExternalAgentsAsync(items) |> Async.AwaitTask
                                    let mutable imp = Unchecked.defaultof<JsonElement>
                                    let n = if imported.TryGetProperty("imported", &imp) && imp.ValueKind = JsonValueKind.Array then imp.GetArrayLength() else 0
                                    Dispatcher.UIThread.Post(fun () -> addNote "agent_message" (sprintf "imported %d" n))
                            with ex ->
                                Dispatcher.UIThread.Post(fun () -> addNote "error" ex.Message)
                        }
                        |> Async.Start
                    elif text = "/raw" then
                        let next = if state.Current.TuiRaw = "true" then "false" else "true"
                        writeConfig "tui_raw" next
                        addNote "agent_message" ("raw " + next)
                    elif text = "/subagents" then
                        async {
                            try
                                let! listed = session.ReadThreadAsync() |> Async.AwaitTask
                                let mutable thread = Unchecked.defaultof<JsonElement>
                                let mutable agents = Unchecked.defaultof<JsonElement>
                                let lines =
                                    if listed.TryGetProperty("thread", &thread) && thread.TryGetProperty("subagents", &agents) && agents.ValueKind = JsonValueKind.Array then
                                        agents.EnumerateArray()
                                        |> Seq.map (fun a ->
                                            let mutable idEl = Unchecked.defaultof<JsonElement>
                                            let mutable doneEl = Unchecked.defaultof<JsonElement>
                                            let id = if a.TryGetProperty("id", &idEl) && idEl.ValueKind = JsonValueKind.String then idEl.GetString() else "?"
                                            let doneFlag = a.TryGetProperty("done", &doneEl) && doneEl.ValueKind = JsonValueKind.True
                                            id + "  " + (if doneFlag then "done" else "running"))
                                        |> fun xs -> String.Join(Environment.NewLine, xs)
                                    else ""
                                Dispatcher.UIThread.Post(fun () -> addNote "agent_message" (if String.IsNullOrWhiteSpace lines then "no subagents" else lines))
                            with ex ->
                                Dispatcher.UIThread.Post(fun () -> addNote "error" ex.Message)
                        }
                        |> Async.Start
                    elif text = "/execpolicy" || text.StartsWith("/execpolicy ") then
                        let rest = if text.Length > 11 then text.Substring(11).Trim() else ""
                        if rest = "" then
                            async {
                                try
                                    let! listed = session.ListExecPolicyAsync() |> Async.AwaitTask
                                    let mutable data = Unchecked.defaultof<JsonElement>
                                    let lines =
                                        if listed.TryGetProperty("data", &data) && data.ValueKind = JsonValueKind.Array then
                                            data.EnumerateArray()
                                            |> Seq.map (fun r ->
                                                let mutable pat = Unchecked.defaultof<JsonElement>
                                                let pattern =
                                                    if r.TryGetProperty("pattern", &pat) && pat.ValueKind = JsonValueKind.Array then
                                                        String.Join(" ", pat.EnumerateArray() |> Seq.map (fun x -> if x.ValueKind = JsonValueKind.String then x.GetString() else ""))
                                                    else ""
                                                pattern + "  " + jsStr r "decision")
                                            |> fun xs -> String.Join(Environment.NewLine, xs)
                                        else ""
                                    Dispatcher.UIThread.Post(fun () -> addNote "agent_message" (if String.IsNullOrWhiteSpace lines then "no prefix_rule files in ~/.codexsharp/rules" else lines))
                                with ex ->
                                    Dispatcher.UIThread.Post(fun () -> addNote "error" ex.Message)
                            }
                            |> Async.Start
                        else
                            let parts = rest.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)
                            if parts.Length < 2 || (parts.[0] <> "forbidden" && parts.[0] <> "prompt" && parts.[0] <> "allow") then
                                addNote "error" "usage: /execpolicy forbidden|prompt TOKEN [TOKEN...]"
                            else
                                async {
                                    try
                                        let pattern = parts |> Seq.skip 1 |> Seq.toArray
                                        let! added = session.AddExecPolicyRuleAsync(parts.[0], pattern) |> Async.AwaitTask
                                        Dispatcher.UIThread.Post(fun () -> addNote "agent_message" ("rule " + jsStr added "line"))
                                    with ex ->
                                        Dispatcher.UIThread.Post(fun () -> addNote "error" ex.Message)
                                }
                                |> Async.Start
                    elif text = "/rename" || text.StartsWith("/rename ") then
                        let rest = if text.Length > 7 then text.Substring(7).Trim() else ""
                        if rest = "" then addNote "agent_message" session.Title
                        else
                            session.SetNameAsync rest |> ignore
                            addNote "agent_message" ("named " + rest)
                    elif text = "/approvals" || text.StartsWith("/approvals ") then
                        let rest = if text.Length > 10 then text.Substring(10).Trim() else ""
                        if rest <> "" then
                            session.UpdateSettingsAsync(approvalPolicy = rest) |> ignore
                            writeConfig "approval_policy" rest
                        addNote "agent_message" (if rest = "" then "usage: /approvals on-request|never|untrusted" else "approval " + rest)
                    elif text = "/history" then
                        let lines = String.Join(Environment.NewLine, state.Current.ComposerHistory)
                        addNote "agent_message" (if String.IsNullOrWhiteSpace lines then "no composer history" else lines)
                    else
                        addNote "error" "Unknown command. Type /help"
                elif text <> "" then
                    let text = SkillSlash.Expand(text, CodexPaths.Home, Environment.CurrentDirectory)
                    if state.Current.Busy then
                        wipeComposerBuffer ()
                        state.Set { state.Current with Composer = ""; VimMode = "insert" }
                        async {
                            try
                                let! _ = session.SteerAsync text |> Async.AwaitTask
                                Dispatcher.UIThread.Post(fun () ->
                                    let item =
                                        { TimelineItem.Id = Ids.item ()
                                          Kind = "agent_message"
                                          ToolName = ""
                                          Text = "steered: " + text
                                          Status = ""
                                          ToolCallId = "" }
                                    state.Set { state.Current with Timeline = state.Current.Timeline @ [item]; Status = "steered" })
                            with ex ->
                                Dispatcher.UIThread.Post(fun () ->
                                    let item =
                                        { TimelineItem.Id = Ids.item ()
                                          Kind = "error"
                                          ToolName = ""
                                          Text = ex.Message
                                          Status = ""
                                          ToolCallId = "" }
                                    state.Set { state.Current with Timeline = state.Current.Timeline @ [item] })
                        }
                        |> Async.Start
                    else
                        let pics = state.Current.Attachments
                        let bubbles =
                            pics
                            |> List.map (fun pth ->
                                { TimelineItem.Id = Guid.NewGuid().ToString("N")
                                  Kind = "user_image"
                                  ToolName = ""
                                  Text = pth
                                  Status = ""
                                  ToolCallId = "" })
                        wipeComposerBuffer ()
                        state.Set
                            { state.Current with
                                Composer = ""
                                VimMode = "insert"
                                Busy = true
                                Attachments = []
                                ComposerHistory = text :: (state.Current.ComposerHistory |> List.truncate 49)
                                HistoryCursor = -1
                                ComposerDraft = ""
                                Timeline = state.Current.Timeline @ bubbles }
                        async {
                            try
                                do! session.StartTurnAsync(text, pics) |> Async.AwaitTask |> Async.Ignore
                                ()
                            finally
                                Dispatcher.UIThread.Post(fun () ->
                                    state.Set { state.Current with Busy = false; Status = state.Current.Status })
                        }
                        |> Async.Start

            let newThread () =
                async {
                    let! _ = session.StartThreadAsync(projectId = (if String.IsNullOrWhiteSpace state.Current.SelectedProject then null else state.Current.SelectedProject)) |> Async.AwaitTask
                    Dispatcher.UIThread.Post(fun () ->
                        state.Set
                            { state.Current with
                                Timeline = []
                                Composer = ""
                                Status = state.Current.Status
                                Header = "New thread"
                                Busy = false
                                ApprovalId = ""
                                ApprovalCommand = ""
                                Plan = ""
                                SearchHits = []
                                Diff = ""
                                GoalObjective = ""
                                GoalStatus = ThreadGoal.Cleared })
                    refreshThreads ()
                }
                |> Async.Start

            let resumeThread (id: string) =
                async {
                    do! session.ResumeThreadAsync id |> Async.AwaitTask
                    let! items = session.ListItemsAsync() |> Async.AwaitTask
                    let! goal = session.GetGoalAsync() |> Async.AwaitTask
                    Dispatcher.UIThread.Post(fun () ->
                        applyGoalPayload goal
                        state.Set
                            { state.Current with
                                Timeline = itemsToTimeline items
                                Header = session.Title
                                Busy = false
                                Status = state.Current.Status
                                ApprovalId = ""
                                ApprovalCommand = ""
                                Plan = ""
                                GoalObjective = state.Current.GoalObjective
                                GoalStatus = state.Current.GoalStatus })
                }
                |> Async.Start

            let listedPalette () =
                let core = DesktopCommands.CoreItems() |> List.ofSeq
                let threads =
                    state.Current.Threads
                    |> List.map (fun t -> DesktopCommands.ThreadItem(t.Id, t.Title))
                DesktopCommands.Filter(core @ threads, state.Current.PaletteQuery)
                |> List.ofSeq

            let focusNamed (name: string) =
                Dispatcher.UIThread.Post(fun () ->
                    match Application.Current with
                    | null -> ()
                    | app ->
                        match app.ApplicationLifetime with
                        | :? IClassicDesktopStyleApplicationLifetime as desk when not (isNull desk.MainWindow) ->
                            match desk.MainWindow.FindControl<TextBox>(name) with
                            | null -> ()
                            | tb -> tb.Focus() |> ignore
                        | _ -> ())

            let openPalette () =
                state.Set { state.Current with ShowPalette = true; PaletteQuery = ""; PaletteIndex = 0 }
                focusNamed "commandPaletteQuery"

            let runPaletteItem (item: DesktopPaletteItem) =
                let closed =
                    { state.Current with
                        ShowPalette = false
                        PaletteQuery = ""
                        PaletteIndex = 0 }
                match item.Kind with
                | DesktopPaletteKind.NewThread ->
                    state.Set closed
                    newThread ()
                | DesktopPaletteKind.OpenSettings ->
                    state.Set { closed with ShowSettings = true }
                | DesktopPaletteKind.FocusComposer ->
                    state.Set closed
                    focusNamed "composer"
                | DesktopPaletteKind.ToggleTerminal ->
                    state.Set { closed with ShowTerminal = not closed.ShowTerminal }
                | DesktopPaletteKind.ToggleSidebar ->
                    state.Set { closed with ShowSidebar = not closed.ShowSidebar }
                | DesktopPaletteKind.JumpThread ->
                    state.Set closed
                    if String.IsNullOrWhiteSpace item.ThreadId then () else resumeThread item.ThreadId
                | _ ->
                    state.Set closed

            let runSelectedPalette () =
                let items = listedPalette ()
                if items.IsEmpty then
                    ()
                else
                    let idx = min (max 0 state.Current.PaletteIndex) (items.Length - 1)
                    runPaletteItem items.[idx]

            let desktopChord (e: Avalonia.Input.KeyEventArgs) =
                let parts = System.Collections.Generic.List<string>()
                if e.KeyModifiers.HasFlag Avalonia.Input.KeyModifiers.Control then parts.Add "ctrl"
                if e.KeyModifiers.HasFlag Avalonia.Input.KeyModifiers.Alt then parts.Add "alt"
                if e.KeyModifiers.HasFlag Avalonia.Input.KeyModifiers.Shift then parts.Add "shift"
                let keyName =
                    match e.Key with
                    | Avalonia.Input.Key.OemComma -> ","
                    | Avalonia.Input.Key.OemTilde | Avalonia.Input.Key.Oem3 | Avalonia.Input.Key.Oem8 -> "`"
                    | Avalonia.Input.Key.K -> "k"
                    | Avalonia.Input.Key.N -> "n"
                    | Avalonia.Input.Key.B -> "b"
                    | Avalonia.Input.Key.P -> "p"
                    | Avalonia.Input.Key.L -> "l"
                    | _ -> e.Key.ToString().ToLowerInvariant()
                String.Join("+", Array.append (parts.ToArray()) [| keyName |])

            let handleDesktopChord (e: Avalonia.Input.KeyEventArgs) =
                if e.Handled then
                    false
                else
                    match DesktopCommands.Match(desktopChord e) with
                    | null -> false
                    | action when action = DesktopCommands.OpenPalette ->
                        e.Handled <- true
                        if state.Current.ShowPalette then
                            state.Set { state.Current with ShowPalette = false; PaletteQuery = ""; PaletteIndex = 0 }
                        else
                            openPalette ()
                        true
                    | action when action = DesktopCommands.NewThread ->
                        e.Handled <- true
                        state.Set { state.Current with ShowPalette = false }
                        newThread ()
                        true
                    | action when action = DesktopCommands.OpenSettings ->
                        e.Handled <- true
                        state.Set { state.Current with ShowPalette = false; ShowSettings = true }
                        true
                    | action when action = DesktopCommands.ToggleTerminal ->
                        e.Handled <- true
                        state.Set { state.Current with ShowTerminal = not state.Current.ShowTerminal; ShowPalette = false }
                        true
                    | action when action = DesktopCommands.ToggleSidebar ->
                        e.Handled <- true
                        state.Set { state.Current with ShowSidebar = not state.Current.ShowSidebar; ShowPalette = false }
                        true
                    | action when action = DesktopCommands.ShellChat ->
                        e.Handled <- true
                        state.Set { state.Current with ShellMode = Chat; ShowPalette = false }
                        refreshThreads ()
                        true
                    | action when action = DesktopCommands.ShellWork ->
                        e.Handled <- true
                        state.Set { state.Current with ShellMode = Work; ShowPalette = false }
                        true
                    | action when action = DesktopCommands.ShellCodex ->
                        e.Handled <- true
                        state.Set { state.Current with ShellMode = Codex; ShowPalette = false }
                        refreshThreads ()
                        true
                    | action when action = DesktopCommands.QuickChat ->
                        e.Handled <- true
                        startQuickChat false
                        true
                    | action when action = DesktopCommands.TemporaryChat ->
                        e.Handled <- true
                        startQuickChat true
                        true
                    | action when action = DesktopCommands.PopOut ->
                        e.Handled <- true
                        openPopout ()
                        true
                    | _ -> false

            let applyRuntimeLink (link: DeepLink) =
                match link.Kind with
                | DeepLinkKind.OpenThread when not (String.IsNullOrWhiteSpace link.ThreadId) ->
                    resumeThread link.ThreadId
                | DeepLinkKind.NewThread ->
                    async {
                        let! _ = session.StartThreadAsync(cwd = link.WorkspacePath, projectId = (if String.IsNullOrWhiteSpace state.Current.SelectedProject then null else state.Current.SelectedProject)) |> Async.AwaitTask
                        Dispatcher.UIThread.Post(fun () ->
                            state.Set
                                { state.Current with
                                    Timeline = []
                                    Composer = ""
                                    Header = "New thread"
                                    Busy = false
                                    ShowSettings = false
                                    ShowPalette = false })
                        refreshThreads ()
                    }
                    |> Async.Start
                | DeepLinkKind.Settings ->
                    state.Set { state.Current with ShowSettings = true; ShowPalette = false }
                | DeepLinkKind.Skills ->
                    state.Set { state.Current with Status = "Skills"; ShowPalette = false }
                | _ ->
                    ()

            ctx.useEffect (
                handler =
                    (fun _ ->
                        DesktopActivation.add_Received (
                            Action<DeepLink>(fun link ->
                                Dispatcher.UIThread.Post(fun () -> applyRuntimeLink link)))),
                triggers = [ EffectTrigger.AfterInit ]
            )

            let archiveThread () =
                async {
                    do! session.ArchiveAsync() |> Async.AwaitTask |> Async.Ignore
                    let! _ = session.StartThreadAsync() |> Async.AwaitTask
                    Dispatcher.UIThread.Post(fun () ->
                        state.Set
                            { state.Current with
                                Timeline = []
                                Header = "New thread"
                                Busy = false
                                Status = state.Current.Status
                                Plan = "" })
                    refreshThreads ()
                }
                |> Async.Start

            let forkThread () =
                async {
                    let! _ = session.ForkAsync() |> Async.AwaitTask
                    let! items = session.ListItemsAsync() |> Async.AwaitTask
                    Dispatcher.UIThread.Post(fun () ->
                        state.Set
                            { state.Current with
                                Timeline = itemsToTimeline items
                                Header = session.Title
                                Status = state.Current.Status })
                    refreshThreads ()
                }
                |> Async.Start

            let applyLastPatch () =
                try
                    let result = LastPatchApply.Run(session.ThreadId)
                    state.Set { state.Current with Status = if result.Ok then "applied last patch in " + result.Cwd else result.Output }
                    refreshChrome ()
                with ex ->
                    state.Set { state.Current with Status = ex.Message }

            let compactThread () =
                async {
                    do! session.CompactAsync() |> Async.AwaitTask |> Async.Ignore
                    let! items = session.ListItemsAsync() |> Async.AwaitTask
                    Dispatcher.UIThread.Post(fun () ->
                        state.Set
                            { state.Current with
                                Timeline = itemsToTimeline items
                                Status = state.Current.Status })
                }
                |> Async.Start

            let rollbackThread () =
                async {
                    do! session.RollbackAsync(1) |> Async.AwaitTask |> Async.Ignore
                    let! items = session.ListItemsAsync() |> Async.AwaitTask
                    Dispatcher.UIThread.Post(fun () ->
                        state.Set
                            { state.Current with
                                Timeline = itemsToTimeline items
                                Status = state.Current.Status })
                }
                |> Async.Start

            let reviewThread (baseBranch: string) =
                async {
                    if String.IsNullOrWhiteSpace baseBranch then
                        do! session.ReviewUncommittedAsync() |> Async.AwaitTask |> Async.Ignore
                    else
                        do! session.ReviewBaseBranchAsync(baseBranch) |> Async.AwaitTask |> Async.Ignore
                    refreshChrome ()
                }
                |> Async.Start

            let refreshPrComments () =
                async {
                    try
                        let primary =
                            state.Current.Projects
                            |> List.tryFind (fun p -> p.Id = state.Current.SelectedProject)
                            |> Option.map (fun p -> p.Root)
                            |> Option.defaultValue state.Current.ReviewCwd
                        let cwd = if String.IsNullOrWhiteSpace primary then Environment.CurrentDirectory else primary
                        let status = GhPrComments.Probe(cwd)
                        Dispatcher.UIThread.Post(fun () ->
                            state.Set
                                { state.Current with
                                    PrNote = GhPrComments.HonestNote(status)
                                    PrComments = status.Comments |> List.ofSeq })
                    with ex ->
                        Dispatcher.UIThread.Post(fun () ->
                            state.Set { state.Current with PrNote = ex.Message; PrComments = [] })
                }
                |> Async.Start

            ctx.useEffect (
                handler =
                    (fun _ ->
                        let timer = DispatcherTimer()
                        timer.Interval <- TimeSpan.FromSeconds 30.
                        timer.Tick.Add(fun _ ->
                            async {
                                try
                                    let! _ = session.ScheduledTickAsync() |> Async.AwaitTask
                                    ()
                                with _ ->
                                    ()
                            }
                            |> Async.Start)
                        timer.Start()),
                triggers = [ EffectTrigger.AfterInit ]
            )

            let applyQueue listed =
                let items = parseQueue listed
                Dispatcher.UIThread.Post(fun () ->
                    state.Set { state.Current with QueueItems = items; QueueCount = items.Length })

            let refreshQueue () =
                async {
                    try
                        let! listed = session.QueueListAsync() |> Async.AwaitTask
                        applyQueue listed
                    with _ ->
                        ()
                }
                |> Async.Start

            ctx.useEffect (
                handler =
                    (fun _ ->
                        session.add_QueueChanged (
                            Action(fun () ->
                                Dispatcher.UIThread.Post(fun () -> refreshQueue ())))),
                triggers = [ EffectTrigger.AfterInit ]
            )

            let runQueue () =
                async {
                    do! session.QueueStartAsync() |> Async.AwaitTask |> Async.Ignore
                    let! listed = session.QueueListAsync() |> Async.AwaitTask
                    applyQueue listed
                }
                |> Async.Start

            let deleteQueued id =
                async {
                    do! session.QueueDeleteAsync(id) |> Async.AwaitTask |> Async.Ignore
                    refreshQueue ()
                }
                |> Async.Start

            let reorderQueued (id: string) (delta: int) =
                let items = state.Current.QueueItems
                let idx = items |> List.tryFindIndex (fun (qid, _) -> qid = id)
                match idx with
                | None -> ()
                | Some i ->
                    let j = i + delta
                    if j >= 0 && j < items.Length then
                        let arr = items |> List.toArray
                        let tmp = arr.[i]
                        arr.[i] <- arr.[j]
                        arr.[j] <- tmp
                        let next = arr |> Array.toList
                        state.Set { state.Current with QueueItems = next }
                        async {
                            do! session.QueueReorderAsync(next |> List.map fst) |> Async.AwaitTask |> Async.Ignore
                            refreshQueue ()
                        }
                        |> Async.Start


            let runSearch () =
                let q = state.Current.Search.Trim()
                if q <> "" then
                    async {
                        do! session.FuzzySessionUpdateAsync("desktop-search", q) |> Async.AwaitTask |> Async.Ignore
                    }
                    |> Async.Start

            let attachImage () =
                async {
                    match Application.Current.ApplicationLifetime with
                    | :? IClassicDesktopStyleApplicationLifetime as desk when not (isNull desk.MainWindow) ->
                        let opts = FilePickerOpenOptions()
                        opts.Title <- "Attach image"
                        opts.AllowMultiple <- true
                        opts.FileTypeFilter <- [| FilePickerFileTypes.ImageAll |]
                        let! files = desk.MainWindow.StorageProvider.OpenFilePickerAsync(opts) |> Async.AwaitTask
                        let paths =
                            files
                            |> Seq.choose (fun f ->
                                let local = f.TryGetLocalPath()
                                if String.IsNullOrWhiteSpace local then None else Some local)
                            |> Seq.toList
                        Dispatcher.UIThread.Post(fun () ->
                            state.Set { state.Current with Attachments = state.Current.Attachments @ paths })
                    | _ -> ()
                }
                |> Async.Start

            let pasteClipboardImage () =
                async {
                    match Application.Current.ApplicationLifetime with
                    | :? IClassicDesktopStyleApplicationLifetime as desk when not (isNull desk.MainWindow) && not (isNull desk.MainWindow.Clipboard) ->
                        let clipboard = desk.MainWindow.Clipboard
                        let! transfer = clipboard.TryGetDataAsync() |> Async.AwaitTask
                        if not (isNull transfer) then
                            let! bitmap = transfer.TryGetValueAsync(DataFormat.Bitmap) |> Async.AwaitTask
                            match box bitmap with
                            | null -> ()
                            | :? Bitmap as bmp ->
                                use ms = new System.IO.MemoryStream()
                                bmp.Save(ms, PngBitmapEncoderOptions())
                                let path = ClipboardImageStore.SavePng(ms.ToArray())
                                Dispatcher.UIThread.Post(fun () ->
                                    state.Set { state.Current with Attachments = state.Current.Attachments @ [path] })
                            | _ -> ()
                    | _ -> ()
                }
                |> Async.Start


            DockPanel.create [
                DockPanel.background Theme.bg
                InputElement.focusable true
                InputElement.onKeyDown (fun e -> handleDesktopChord e |> ignore)
                DockPanel.children [
                    Border.create [
                            Border.dock Dock.Top
                            Border.isVisible (not (String.IsNullOrWhiteSpace state.Current.Toast))
                            Border.background Theme.card
                            Border.borderBrush Theme.accent
                            Border.borderThickness (Thickness(0, 0, 0, 1))
                            Border.padding 8.
                            Border.child (
                                DockPanel.create [
                                    DockPanel.children [
                                        Button.create [
                                            Button.dock Dock.Right
                                            Button.content "Dismiss"
                                            Button.onClick (fun _ -> state.Set { state.Current with Toast = "" })
                                        ]
                                        TextBlock.create [
                                            TextBlock.text state.Current.Toast
                                            TextBlock.foreground Theme.accent
                                            TextBlock.verticalAlignment VerticalAlignment.Center
                                        ]
                                    ]
                                ]
                            )
                    ]
                    if state.Current.ShowPalette then
                        Border.create [
                            Border.dock Dock.Top
                            Border.background Theme.card
                            Border.borderBrush Theme.accent
                            Border.borderThickness (Thickness(0, 0, 0, 1))
                            Border.padding 12.
                            Border.child (
                                StackPanel.create [
                                    StackPanel.spacing 6.
                                    StackPanel.children (
                                        let items = listedPalette ()
                                        let queryBox =
                                            TextBox.create [
                                                StyledElement.name "commandPaletteQuery"
                                                TextBox.placeHolderText "Run a command…"
                                                TextBox.text state.Current.PaletteQuery
                                                TextBox.onTextChanged (fun t ->
                                                    state.Set { state.Current with PaletteQuery = t; PaletteIndex = 0 })
                                                TextBox.onKeyDown (fun e ->
                                                    if handleDesktopChord e then
                                                        ()
                                                    elif e.Key = Avalonia.Input.Key.Escape then
                                                        e.Handled <- true
                                                        state.Set { state.Current with ShowPalette = false; PaletteQuery = ""; PaletteIndex = 0 }
                                                    elif e.Key = Avalonia.Input.Key.Enter then
                                                        e.Handled <- true
                                                        runSelectedPalette ()
                                                    elif e.Key = Avalonia.Input.Key.Down then
                                                        e.Handled <- true
                                                        let maxIdx = max 0 (listedPalette().Length - 1)
                                                        state.Set { state.Current with PaletteIndex = min (state.Current.PaletteIndex + 1) maxIdx }
                                                    elif e.Key = Avalonia.Input.Key.Up then
                                                        e.Handled <- true
                                                        state.Set { state.Current with PaletteIndex = max 0 (state.Current.PaletteIndex - 1) })
                                            ] :> IView
                                        let rows =
                                            items
                                            |> List.truncate 8
                                            |> List.mapi (fun i item ->
                                                Button.create [
                                                    Button.content (
                                                        let keys = if String.IsNullOrWhiteSpace item.Keys then "" else "  " + item.Keys
                                                        item.Title + keys)
                                                    Button.horizontalAlignment HorizontalAlignment.Stretch
                                                    Button.horizontalContentAlignment HorizontalAlignment.Left
                                                    Button.background (if i = state.Current.PaletteIndex then Theme.accent else Theme.bg)
                                                    Button.foreground (if i = state.Current.PaletteIndex then SolidColorBrush(Color.Parse "#111827") else Theme.text)
                                                    Button.onClick (fun _ -> runPaletteItem item)
                                                ] :> IView)
                                        queryBox :: rows)
                                ]
                            )
                        ]
                    else
                        Border.create [
                            Border.dock Dock.Top
                            Border.height 0.
                        ]
                    Border.create [
                            Border.dock Dock.Top
                            Border.isVisible (state.Current.ShowOnboarding || state.Current.ShowTrust)
                            Border.background Theme.card
                            Border.borderBrush Theme.accent
                            Border.borderThickness (Thickness(0, 0, 0, 1))
                            Border.padding 16.
                            Border.child (
                                StackPanel.create [
                                    StackPanel.spacing 8.
                                    StackPanel.children [
                                        TextBlock.create [
                                            TextBlock.text (if state.Current.ShowTrust then "Trust this directory?" else "Welcome to CodexSharp")
                                            TextBlock.fontWeight FontWeight.SemiBold
                                            TextBlock.foreground Theme.accent
                                        ]
                                        TextBlock.create [
                                            TextBlock.text (
                                                if state.Current.ShowTrust then
                                                    let extra = if state.Current.TrustTarget <> "" && not (state.Current.TrustTarget.Equals(Environment.CurrentDirectory, StringComparison.OrdinalIgnoreCase)) then " Trusting applies to the git root: " + state.Current.TrustTarget else ""
                                                    "Do you trust the contents of this directory? Untrusted trees skip project-local skills, hooks, and exec policies." + extra
                                                else
                                                    "Paste an API key, or sign in with ChatGPT (device code or browser OAuth).")
                                            TextBlock.foreground Theme.muted
                                            TextBlock.textWrapping TextWrapping.Wrap
                                        ]
                                        DockPanel.create [
                                            DockPanel.children [
                                                Button.create [
                                                    Button.dock Dock.Right
                                                    Button.content "Skip"
                                                    Button.onClick (fun _ ->
                                                        if state.Current.ShowTrust then
                                                            state.Set { state.Current with ShowTrust = false; TrustDismissed = true }
                                                        else
                                                            state.Set { state.Current with ShowOnboarding = false; OnboardingDismissed = true })
                                                ]
                                                Button.create [
                                                    Button.dock Dock.Right
                                                    Button.content "Yes, continue"
                                                    Button.isVisible state.Current.ShowTrust
                                                    Button.onClick (fun _ ->
                                                        async {
                                                            do! session.SetProjectTrustAsync(true) |> Async.AwaitTask |> Async.Ignore
                                                            Dispatcher.UIThread.Post(fun () ->
                                                                state.Set { state.Current with ShowTrust = false; TrustDismissed = true })
                                                            refreshChrome ()
                                                        }
                                                        |> Async.Start)
                                                ]
                                                Button.create [
                                                    Button.dock Dock.Right
                                                    Button.content "Save key"
                                                    Button.isVisible (not state.Current.ShowTrust)
                                                    Button.onClick (fun _ ->
                                                        if apiKey.Current.Trim().Length > 0 then
                                                            let key = apiKey.Current.Trim()
                                                            apiKey.Set ""
                                                            async {
                                                                do! session.LoginApiKeyAsync(key) |> Async.AwaitTask |> Async.Ignore
                                                                refreshChrome ()
                                                            }
                                                            |> Async.Start)
                                                ]
                                                Button.create [
                                                    Button.dock Dock.Right
                                                    Button.content "Save access token"
                                                    Button.isVisible (not state.Current.ShowTrust)
                                                    Button.onClick (fun _ ->
                                                        if apiKey.Current.Trim().Length > 0 then
                                                            let key = apiKey.Current.Trim()
                                                            apiKey.Set ""
                                                            async {
                                                                do! session.LoginAccessTokenAsync(key) |> Async.AwaitTask |> Async.Ignore
                                                                refreshChrome ()
                                                            }
                                                            |> Async.Start)
                                                ]
                                                Button.create [
                                                    Button.dock Dock.Right
                                                    Button.content "ChatGPT device code"
                                                    Button.isVisible (not state.Current.ShowTrust)
                                                    Button.onClick (fun _ ->
                                                        async {
                                                            try
                                                                let! started = session.LoginDeviceCodeAsync() |> Async.AwaitTask
                                                                let url = jsStr started "verificationUrl"
                                                                let code = jsStr started "userCode"
                                                                Dispatcher.UIThread.Post(fun () ->
                                                                    state.Set { state.Current with Status = "ChatGPT " + code + "  " + url })
                                                            with ex ->
                                                                Dispatcher.UIThread.Post(fun () ->
                                                                    state.Set { state.Current with Status = ex.Message })
                                                        }
                                                        |> Async.Start)
                                                ]
                                                Button.create [
                                                    Button.dock Dock.Right
                                                    Button.content "ChatGPT browser"
                                                    Button.isVisible (not state.Current.ShowTrust)
                                                    Button.onClick (fun _ ->
                                                        async {
                                                            try
                                                                let! started = session.LoginChatgptAsync() |> Async.AwaitTask
                                                                let url = jsStr started "authUrl"
                                                                Dispatcher.UIThread.Post(fun () ->
                                                                    state.Set { state.Current with Status = url })
                                                                if url.Length > 0 then
                                                                    try
                                                                        Process.Start(ProcessStartInfo(FileName = url, UseShellExecute = true)) |> ignore
                                                                    with _ -> ()
                                                            with ex ->
                                                                Dispatcher.UIThread.Post(fun () ->
                                                                    state.Set { state.Current with Status = ex.Message })
                                                        }
                                                        |> Async.Start)
                                                ]
                                                TextBox.create [
                                                    TextBox.passwordChar '*'
                                                    TextBox.placeHolderText "API key"
                                                    TextBox.isVisible (not state.Current.ShowTrust)
                                                    TextBox.text apiKey.Current
                                                    TextBox.onTextChanged apiKey.Set
                                                ]
                                            ]
                                        ]
                                    ]
                                ]
                            )
                        ]
                    Border.create [
                        Border.dock Dock.Top
                        Border.height 48.
                        Border.background Theme.panel
                        Border.borderBrush Theme.border
                        Border.borderThickness (Thickness(0, 0, 0, 1))
                        Border.child (
                            Grid.create [
                                Grid.columnDefinitions "Auto,*,Auto"
                                Grid.margin (Thickness(16, 0))
                                Grid.children [
                                    StackPanel.create [
                                        StackPanel.orientation Orientation.Horizontal
                                        StackPanel.spacing 10.
                                        StackPanel.verticalAlignment VerticalAlignment.Center
                                        StackPanel.children [
                                            Border.create [ Border.width 10.; Border.height 10.; Border.cornerRadius 5.; Border.background Theme.accent ]
                                            TextBlock.create [
                                                TextBlock.text "CodexSharp"
                                                TextBlock.fontWeight FontWeight.SemiBold
                                                TextBlock.fontSize 16.
                                                TextBlock.foreground Theme.text
                                            ]
                                            TextBlock.create [
                                                TextBlock.text "Avalonia.FuncUI"
                                                TextBlock.foreground Theme.muted
                                                TextBlock.verticalAlignment VerticalAlignment.Center
                                            ]
                                            Button.create [
                                                Button.content "Chat"
                                                Button.onClick (fun _ ->
                                                    state.Set { state.Current with ShellMode = Chat }
                                                    refreshThreads ())
                                            ]
                                            Button.create [
                                                Button.content "Work"
                                                Button.onClick (fun _ ->
                                                    state.Set { state.Current with ShellMode = Work })
                                            ]
                                            Button.create [
                                                Button.content "Codex"
                                                Button.onClick (fun _ ->
                                                    state.Set { state.Current with ShellMode = Codex }
                                                    refreshThreads ())
                                            ]
                                            Button.create [
                                                Button.content "Pop out"
                                                Button.onClick (fun _ -> openPopout ())
                                            ]
                                            TextBlock.create [
                                                TextBlock.text (
                                                    match state.Current.ShellMode with
                                                    | Chat -> "layout Chat (local)"
                                                    | Work -> HonestStubs.WorkLayoutMessage()
                                                    | Codex -> "layout Codex")
                                                TextBlock.foreground Theme.muted
                                                TextBlock.verticalAlignment VerticalAlignment.Center
                                                TextBlock.fontSize 11.
                                                TextBlock.textWrapping TextWrapping.Wrap
                                            ]
                                            TextBlock.create [
                                                TextBlock.text (
                                                    let baseStatus = if state.Current.Busy then "▸ working" else "▸ idle"
                                                    let vim =
                                                        if state.Current.TuiVim = "true" then
                                                            " · vim " + state.Current.VimMode
                                                        else ""
                                                    let collab =
                                                        let mode = state.Current.CollaborationMode
                                                        if System.String.IsNullOrWhiteSpace mode || mode = "default" then ""
                                                        else " · " + mode
                                                    let personality =
                                                        let p = state.Current.Personality
                                                        if System.String.IsNullOrWhiteSpace p then ""
                                                        else " · " + p
                                                    let effort =
                                                        let e = state.Current.Effort
                                                        if System.String.IsNullOrWhiteSpace e || e = "medium" then ""
                                                        else " · " + e
                                                    baseStatus + vim + collab + personality + effort)
                                                TextBlock.foreground (if state.Current.Busy then Theme.accent else Theme.muted)
                                                TextBlock.verticalAlignment VerticalAlignment.Center
                                                TextBlock.fontSize 12.
                                            ]
                                            Button.create [
                                                Button.content (if state.Current.ShowSettings then "Close settings" else "Settings")
                                                Button.onClick (fun _ -> state.Set { state.Current with ShowSettings = not state.Current.ShowSettings })
                                            ]
                                            Button.create [
                                                Button.content (if state.Current.ShowActivity then "Close activity" else "Activity")
                                                Button.onClick (fun _ ->
                                                    state.Set
                                                        { state.Current with
                                                            ShowActivity = not state.Current.ShowActivity
                                                            ActivityRows = toActivityRows () })
                                            ]
                                            Button.create [
                                                Button.content (if state.Current.ShowScheduled then "Close scheduled" else "Scheduled")
                                                Button.onClick (fun _ -> state.Set { state.Current with ShowScheduled = not state.Current.ShowScheduled })
                                            ]
                                            TextBlock.create [
                                                TextBlock.text ("rc " + state.Current.RemoteControl)
                                                TextBlock.foreground Theme.muted
                                                TextBlock.fontSize 11.
                                                TextBlock.verticalAlignment VerticalAlignment.Center
                                            ]
                                        ]
                                    ]
                                    TextBox.create [
                                        Grid.column 1
                                        TextBox.text state.Current.Header
                                        TextBox.placeHolderText "Thread name"
                                        TextBox.horizontalAlignment HorizontalAlignment.Center
                                        TextBox.minWidth 220.
                                        TextBox.onTextChanged (fun v -> state.Set { state.Current with Header = v })
                                        TextBox.onKeyDown (fun e ->
                                            if e.Key = Avalonia.Input.Key.Enter then
                                                session.SetNameAsync(state.Current.Header) |> ignore)
                                    ]
                                    TextBlock.create [
                                        Grid.column 2
                                        TextBlock.text (
                                            if String.IsNullOrWhiteSpace state.Current.FsNote then state.Current.Status
                                            else state.Current.Status + " · " + System.IO.Path.GetFileName state.Current.FsNote)
                                        TextBlock.foreground Theme.muted
                                        TextBlock.fontSize 11.
                                        TextBlock.verticalAlignment VerticalAlignment.Center
                                        TextBlock.textTrimming TextTrimming.CharacterEllipsis
                                    ]
                                ]
                            ]
                        )
                    ]
                    if state.Current.ShowSettings then
                        Border.create [
                            Border.dock Dock.Top
                            Border.background Theme.card
                            Border.borderBrush Theme.border
                            Border.borderThickness (Thickness(0, 0, 0, 1))
                            Border.padding 12.
                            Border.child (
                                StackPanel.create [
                                    StackPanel.spacing 8.
                                    StackPanel.children [
                                        TextBlock.create [
                                            TextBlock.text "Settings"
                                            TextBlock.fontWeight FontWeight.SemiBold
                                            TextBlock.foreground Theme.accent
                                        ]
                                        StackPanel.create [
                                            StackPanel.orientation Orientation.Horizontal
                                            StackPanel.spacing 4.
                                            StackPanel.children [
                                                Button.create [
                                                    Button.content "friendly"
                                                    Button.onClick (fun _ ->
                                                        writeConfig "personality" "friendly")
                                                ]
                                                Button.create [
                                                    Button.content "pragmatic"
                                                    Button.onClick (fun _ ->
                                                        writeConfig "personality" "pragmatic")
                                                ]
                                                Button.create [
                                                    Button.content "professional"
                                                    Button.onClick (fun _ ->
                                                        writeConfig "personality" "professional")
                                                ]
                                            ]
                                        ]
                                        StackPanel.create [
                                            StackPanel.orientation Orientation.Horizontal
                                            StackPanel.spacing 4.
                                            StackPanel.children [
                                                Button.create [
                                                    Button.content "Dark"
                                                    Button.onClick (fun _ -> writeConfig "tui_theme" "dark")
                                                ]
                                                Button.create [
                                                    Button.content "Light"
                                                    Button.onClick (fun _ -> writeConfig "tui_theme" "light")
                                                ]
                                            ]
                                        ]
                                        StackPanel.create [
                                            StackPanel.orientation Orientation.Horizontal
                                            StackPanel.spacing 4.
                                            StackPanel.children [
                                                Button.create [
                                                    Button.content "Notify auto"
                                                    Button.onClick (fun _ -> writeConfig "tui_notifications" "auto")
                                                ]
                                                Button.create [
                                                    Button.content "OSC9"
                                                    Button.onClick (fun _ -> writeConfig "tui_notifications" "osc9")
                                                ]
                                                Button.create [
                                                    Button.content "BEL"
                                                    Button.onClick (fun _ -> writeConfig "tui_notifications" "bel")
                                                ]
                                                Button.create [
                                                    Button.content "Windows"
                                                    Button.onClick (fun _ -> writeConfig "tui_notifications" "os")
                                                ]
                                                Button.create [
                                                    Button.content "Off"
                                                    Button.onClick (fun _ -> writeConfig "tui_notifications" "off")
                                                ]
                                            ]
                                        ]
                                        TextBlock.create [
                                            TextBlock.text (WindowsSandbox.Describe())
                                            TextBlock.textWrapping TextWrapping.Wrap
                                            TextBlock.foreground Theme.muted
                                            TextBlock.fontSize 11.
                                        ]
                                        TextBlock.create [
                                            TextBlock.text "Unconfigured capabilities"
                                            TextBlock.fontWeight FontWeight.SemiBold
                                            TextBlock.foreground Theme.accent
                                        ]
                                        StackPanel.create [
                                            StackPanel.spacing 2.
                                            StackPanel.children (
                                                HonestStubs.Capabilities
                                                |> Seq.map (fun cap ->
                                                    TextBlock.create [
                                                        TextBlock.text (cap.Label + "  " + cap.Status + "  —  " + cap.Detail)
                                                        TextBlock.foreground Theme.muted
                                                        TextBlock.fontSize 11.
                                                        TextBlock.textWrapping TextWrapping.Wrap
                                                    ] :> IView)
                                                |> Seq.toList
                                            )
                                        ]
                                        StackPanel.create [
                                            StackPanel.spacing 2.
                                            StackPanel.children (
                                                let rows =
                                                    state.Current.FeatureRows
                                                    |> List.filter (fun row -> HonestStubs.IsTogglableFeature row.Name)
                                                if rows.IsEmpty then
                                                    [ TextBlock.create [
                                                        TextBlock.text "No togglable feature flags"
                                                        TextBlock.foreground Theme.muted
                                                        TextBlock.fontSize 11.
                                                      ] :> IView ]
                                                else
                                                    rows
                                                    |> List.map (fun row ->
                                                        Button.create [
                                                            Button.content (row.Name + "  " + (if row.Enabled then "on" else "off"))
                                                            Button.horizontalAlignment HorizontalAlignment.Stretch
                                                            Button.onClick (fun _ -> toggleFeature row.Name row.Enabled)
                                                        ] :> IView)
                                            )
                                        ]
                                        TextBlock.create [
                                            TextBlock.text "MCP servers"
                                            TextBlock.fontWeight FontWeight.SemiBold
                                            TextBlock.foreground Theme.accent
                                        ]
                                        StackPanel.create [
                                            StackPanel.spacing 2.
                                            StackPanel.children (
                                                if state.Current.McpRows.IsEmpty then
                                                    [ TextBlock.create [
                                                        TextBlock.text "No mcp_servers in config.toml"
                                                        TextBlock.foreground Theme.muted
                                                        TextBlock.fontSize 11.
                                                      ] :> IView ]
                                                else
                                                    state.Current.McpRows
                                                    |> List.map (fun row ->
                                                        DockPanel.create [
                                                            DockPanel.children [
                                                                Button.create [
                                                                    Button.dock Dock.Right
                                                                    Button.content "remove"
                                                                    Button.onClick (fun _ ->
                                                                        McpConfig.Remove row.Name |> ignore
                                                                        async {
                                                                            do! session.ReloadMcpAsync() |> Async.AwaitTask |> Async.Ignore
                                                                            Dispatcher.UIThread.Post(fun () -> refreshChrome ())
                                                                        }
                                                                        |> Async.Start)
                                                                ]
                                                                TextBlock.create [
                                                                    TextBlock.text (row.Name + "  " + row.Transport)
                                                                    TextBlock.foreground Theme.text
                                                                    TextBlock.verticalAlignment VerticalAlignment.Center
                                                                    TextBlock.fontSize 11.
                                                                ]
                                                            ]
                                                        ] :> IView)
                                            )
                                        ]
                                        DockPanel.create [
                                            DockPanel.children [
                                                Button.create [
                                                    Button.dock Dock.Right
                                                    Button.content "Add MCP"
                                                    Button.onClick (fun _ ->
                                                        let draft = state.Current.McpDraft.Trim()
                                                        if draft.Length > 0 then
                                                            let parts = draft.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)
                                                            if parts.Length >= 2 then
                                                                McpConfig.AddStdio(parts[0], parts |> Array.skip 1)
                                                                state.Set { state.Current with McpDraft = "" }
                                                                async {
                                                                    do! session.ReloadMcpAsync() |> Async.AwaitTask |> Async.Ignore
                                                                    Dispatcher.UIThread.Post(fun () -> refreshChrome ())
                                                                }
                                                                |> Async.Start)
                                                ]
                                                TextBox.create [
                                                    TextBox.placeHolderText "name command [args…]"
                                                    TextBox.text state.Current.McpDraft
                                                    TextBox.onTextChanged (fun v -> state.Set { state.Current with McpDraft = v })
                                                ]
                                            ]
                                        ]
                                        TextBlock.create [
                                            TextBlock.text state.Current.SideNotes
                                            TextBlock.textWrapping TextWrapping.Wrap
                                            TextBlock.foreground Theme.muted
                                            TextBlock.fontSize 11.
                                        ]
                                    ]
                                ]
                            )
                        ]
                    else
                        Border.create [
                            Border.dock Dock.Top
                            Border.height 0.
                        ]
                    if state.Current.ShowActivity then
                        Border.create [
                            Border.dock Dock.Top
                            Border.background Theme.card
                            Border.borderBrush Theme.border
                            Border.borderThickness (Thickness(0, 0, 0, 1))
                            Border.padding 12.
                            Border.maxHeight 220.
                            Border.child (
                                StackPanel.create [
                                    StackPanel.spacing 6.
                                    StackPanel.children [
                                        TextBlock.create [
                                            TextBlock.text "Activity  ·  local only"
                                            TextBlock.fontWeight FontWeight.SemiBold
                                            TextBlock.foreground Theme.accent
                                        ]
                                        StackPanel.create [
                                            StackPanel.orientation Orientation.Horizontal
                                            StackPanel.spacing 4.
                                            StackPanel.children [
                                                Button.create [
                                                    Button.content "All"
                                                    Button.onClick (fun _ -> state.Set { state.Current with ActivityFilter = "" })
                                                ]
                                                Button.create [
                                                    Button.content "Running"
                                                    Button.onClick (fun _ -> state.Set { state.Current with ActivityFilter = "running" })
                                                ]
                                                Button.create [
                                                    Button.content "Waiting"
                                                    Button.onClick (fun _ -> state.Set { state.Current with ActivityFilter = "waiting" })
                                                ]
                                                Button.create [
                                                    Button.content "Unread"
                                                    Button.onClick (fun _ -> state.Set { state.Current with ActivityFilter = "unread" })
                                                ]
                                            ]
                                        ]
                                        StackPanel.create [
                                            StackPanel.spacing 2.
                                            StackPanel.children (
                                                let rows =
                                                    state.Current.ActivityRows
                                                    |> List.filter (fun r ->
                                                        state.Current.ActivityFilter = "" || r.Kind = state.Current.ActivityFilter)
                                                if rows.IsEmpty then
                                                    [ TextBlock.create [
                                                        TextBlock.text "No local activity."
                                                        TextBlock.foreground Theme.muted
                                                      ] :> IView ]
                                                else
                                                    rows
                                                    |> List.truncate 12
                                                    |> List.map (fun row ->
                                                        Button.create [
                                                            Button.content (row.Kind + "  " + row.At + "  " + row.Title)
                                                            Button.horizontalAlignment HorizontalAlignment.Stretch
                                                            Button.horizontalContentAlignment HorizontalAlignment.Left
                                                            Button.onClick (fun _ ->
                                                                ActivityInbox.MarkThreadRead row.ThreadId
                                                                resumeThread row.ThreadId
                                                                state.Set { state.Current with ActivityRows = toActivityRows (); ShowActivity = true })
                                                        ] :> IView)
                                            )
                                        ]
                                    ]
                                ]
                            )
                        ]
                    else
                        Border.create [
                            Border.dock Dock.Top
                            Border.height 0.
                        ]
                    if state.Current.ShowScheduled then
                        Border.create [
                            Border.dock Dock.Top
                            Border.background Theme.card
                            Border.padding 12.
                            Border.child (
                                StackPanel.create [
                                    StackPanel.spacing 6.
                                    StackPanel.children [
                                        TextBlock.create [
                                            TextBlock.text "Scheduled  ·  app heartbeat"
                                            TextBlock.fontWeight FontWeight.SemiBold
                                            TextBlock.foreground Theme.accent
                                        ]
                                        TextBox.create [
                                            TextBox.placeHolderText "prompt"
                                            TextBox.text state.Current.SchedDraft
                                            TextBox.onTextChanged (fun v -> state.Set { state.Current with SchedDraft = v })
                                        ]
                                        TextBox.create [
                                            TextBox.placeHolderText "every N minutes"
                                            TextBox.text state.Current.SchedMinutes
                                            TextBox.onTextChanged (fun v -> state.Set { state.Current with SchedMinutes = v })
                                        ]
                                        StackPanel.create [
                                            StackPanel.orientation Orientation.Horizontal
                                            StackPanel.spacing 4.
                                            StackPanel.children [
                                                Button.create [
                                                    Button.content "This thread"
                                                    Button.onClick (fun _ ->
                                                        let n = match Int32.TryParse(state.Current.SchedMinutes) with | true, v -> v | _ -> 1
                                                        async {
                                                            let! _ = session.ScheduledCreateAsync(state.Current.SchedDraft, n, session.ThreadId) |> Async.AwaitTask
                                                            Dispatcher.UIThread.Post(fun () ->
                                                                state.Set { state.Current with SchedNote = "created in-chat task" })
                                                        } |> Async.Start)
                                                ]
                                                Button.create [
                                                    Button.content "New thread each run"
                                                    Button.onClick (fun _ ->
                                                        let n = match Int32.TryParse(state.Current.SchedMinutes) with | true, v -> v | _ -> 1
                                                        async {
                                                            let! _ = session.ScheduledCreateAsync(state.Current.SchedDraft, n, null) |> Async.AwaitTask
                                                            Dispatcher.UIThread.Post(fun () ->
                                                                state.Set { state.Current with SchedNote = "created independent task" })
                                                        } |> Async.Start)
                                                ]
                                            ]
                                        ]
                                        TextBlock.create [
                                            TextBlock.text state.Current.SchedNote
                                            TextBlock.foreground Theme.muted
                                            TextBlock.fontSize 11.
                                        ]
                                    ]
                                ]
                            )
                        ]
                    else
                        Border.create [
                            Border.dock Dock.Top
                            Border.height 0.
                        ]
                    Grid.create [
                        Grid.columnDefinitions (if state.Current.ShowSidebar then "260,*,220" else "0,*,220")
                        Grid.children [
                            Border.create [
                                Grid.column 0
                                Grid.columnSpan 3
                                Border.zIndex 8
                                Border.isVisible (state.Current.ShellMode = Work)
                                Border.background Theme.panel
                                Border.child (
                                    StackPanel.create [
                                        StackPanel.verticalAlignment VerticalAlignment.Center
                                        StackPanel.horizontalAlignment HorizontalAlignment.Center
                                        StackPanel.spacing 8.
                                        StackPanel.margin 24.
                                        StackPanel.children [
                                            TextBlock.create [
                                                TextBlock.text "Work"
                                                TextBlock.fontWeight FontWeight.SemiBold
                                                TextBlock.fontSize 18.
                                                TextBlock.horizontalAlignment HorizontalAlignment.Center
                                                TextBlock.foreground Theme.text
                                            ]
                                            TextBlock.create [
                                                TextBlock.text (HonestStubs.WorkLayoutMessage())
                                                TextBlock.textWrapping TextWrapping.Wrap
                                                TextBlock.fontSize 13.
                                                TextBlock.horizontalAlignment HorizontalAlignment.Center
                                                TextBlock.foreground Theme.muted
                                            ]
                                        ]
                                    ]
                                )
                            ]
                            Border.create [
                                Border.background Theme.panel
                                Border.borderBrush Theme.border
                                Border.borderThickness (Thickness(0, 0, 1, 0))
                                Border.isVisible state.Current.ShowSidebar
                                Border.child (
                                    DockPanel.create [
                                        DockPanel.children [
                                            StackPanel.create [
                                                StackPanel.dock Dock.Top
                                                StackPanel.margin 12.
                                                StackPanel.spacing 8.
                                                StackPanel.children [
                                                    Button.create [
                                                        Button.content "New thread"
                                                        Button.height 36.
                                                        Button.horizontalAlignment HorizontalAlignment.Stretch
                                                        Button.background (SolidColorBrush(Color.Parse "#1C2330"))
                                                        Button.foreground Theme.accent
                                                        Button.onClick (fun _ -> newThread ())
                                                    ]
                                                    StackPanel.create [
                                                        StackPanel.orientation Orientation.Horizontal
                                                        StackPanel.spacing 4.
                                                        StackPanel.children [
                                                            Button.create [ Button.content "Archive"; Button.onClick (fun _ -> archiveThread ()) ]
                                                            Button.create [
                                                                Button.content "Delete"
                                                                Button.onClick (fun _ ->
                                                                    async {
                                                                        try
                                                                            do! session.DeleteThreadAsync(session.ThreadId) |> Async.AwaitTask |> Async.Ignore
                                                                        with _ -> ()
                                                                        let! _ = session.StartThreadAsync() |> Async.AwaitTask
                                                                        Dispatcher.UIThread.Post(fun () ->
                                                                            state.Set
                                                                                { state.Current with
                                                                                    Timeline = []
                                                                                    Header = "New thread"
                                                                                    Plan = ""
                                                                                    Diff = ""
                                                                                    Busy = false
                                                                                    Status = "deleted thread" })
                                                                    }
                                                                    |> Async.Start)
                                                            ]
                                                            Button.create [
                                                                Button.content "Worktree"
                                                                Button.onClick (fun _ ->
                                                                    async {
                                                                        try
                                                                            let! result = session.StartWorktreeAsync(Environment.CurrentDirectory) |> Async.AwaitTask
                                                                            let cwd = jsStr result "cwd"
                                                                            let setupLog = jsStr result "setupLog"
                                                                            let setupOk = jsBool result "setupOk"
                                                                            let status =
                                                                                if setupLog <> "" && not setupOk then "setup failed: " + setupLog
                                                                                else "worktree " + cwd
                                                                            Dispatcher.UIThread.Post(fun () ->
                                                                                state.Set
                                                                                    { state.Current with
                                                                                        Timeline = []
                                                                                        Header = "Worktree"
                                                                                        Status = status })
                                                                            refreshChrome ()
                                                                        with ex ->
                                                                            Dispatcher.UIThread.Post(fun () ->
                                                                                state.Set { state.Current with Status = ex.Message })
                                                                    }
                                                                    |> Async.Start)
                                                            ]
                                                            Button.create [ Button.content "Fork"; Button.onClick (fun _ -> forkThread ()) ]
                                                            Button.create [
                                                                Button.content "Hand off"
                                                                Button.onClick (fun _ ->
                                                                    async {
                                                                        try
                                                                            do! session.InterruptAsync() |> Async.AwaitTask |> Async.Ignore
                                                                            let! result = session.HandoffAsync() |> Async.AwaitTask
                                                                            let cwd = jsStr result "cwd"
                                                                            let mode = jsStr result "mode"
                                                                            Dispatcher.UIThread.Post(fun () ->
                                                                                state.Set { state.Current with Status = "handoff " + mode + " " + cwd })
                                                                            refreshChrome ()
                                                                        with ex ->
                                                                            Dispatcher.UIThread.Post(fun () ->
                                                                                state.Set { state.Current with Status = ex.Message })
                                                                    }
                                                                    |> Async.Start)
                                                            ]
                                                            Button.create [ Button.content "Compact"; Button.onClick (fun _ -> compactThread ()) ]
                                                            Button.create [ Button.content "Apply"; Button.onClick (fun _ -> applyLastPatch ()) ]
                                                            Button.create [ Button.content "Rollback"; Button.onClick (fun _ -> rollbackThread ()) ]
                                                            TextBlock.create [
                                                                TextBlock.text (
                                                                    let repos = reviewRepos state.Current.Projects state.Current.SelectedProject
                                                                    let cwd = if String.IsNullOrWhiteSpace state.Current.ReviewCwd then DesktopReviewRepos.DefaultCwd(repos) else state.Current.ReviewCwd
                                                                    let label = if String.IsNullOrWhiteSpace state.Current.ReviewRepoLabel then DesktopReviewRepos.LabelFor(repos, cwd) else state.Current.ReviewRepoLabel
                                                                    "repo: " + label)
                                                                TextBlock.foreground Theme.muted
                                                                TextBlock.fontSize 11.
                                                            ]
                                                            StackPanel.create [
                                                                StackPanel.orientation Orientation.Horizontal
                                                                StackPanel.spacing 4.
                                                                StackPanel.children (
                                                                    (reviewRepos state.Current.Projects state.Current.SelectedProject)
                                                                    |> List.map (fun repo ->
                                                                        Button.create [
                                                                            Button.content (if repo.Primary then repo.Label + "*" else repo.Label)
                                                                            Button.onClick (fun _ ->
                                                                                state.Set
                                                                                    { state.Current with
                                                                                        ReviewCwd = repo.Cwd
                                                                                        ReviewRepoLabel = repo.Label }
                                                                                async {
                                                                                    try
                                                                                        let! git = session.GitDiffToRemoteAsync(repo.Cwd) |> Async.AwaitTask
                                                                                        let diff =
                                                                                            let wtd = jsStr git "workingTreeDiff"
                                                                                            if wtd.Length > 0 then wtd else jsStr git "diff"
                                                                                        Dispatcher.UIThread.Post(fun () ->
                                                                                            state.Set { state.Current with Diff = diff; Status = "review " + repo.Label })
                                                                                    with ex ->
                                                                                        Dispatcher.UIThread.Post(fun () ->
                                                                                            state.Set { state.Current with Status = ex.Message })
                                                                                }
                                                                                |> Async.Start)
                                                                        ] :> IView)
                                                                )
                                                            ]
                                                            Button.create [ Button.content "Uncommitted"; Button.onClick (fun _ -> reviewThread "") ]
                                                            Button.create [
                                                                Button.content "Export"
                                                                Button.onClick (fun _ ->
                                                                    async {
                                                                        try
                                                                            let! result = session.ExportThreadAsync() |> Async.AwaitTask
                                                                            let path = jsStr result "path"
                                                                            Dispatcher.UIThread.Post(fun () ->
                                                                                state.Set { state.Current with Status = "wrote " + path })
                                                                        with ex ->
                                                                            Dispatcher.UIThread.Post(fun () ->
                                                                                state.Set { state.Current with Status = ex.Message })
                                                                    }
                                                                    |> Async.Start)
                                                            ]
                                                            Button.create [
                                                                Button.content "vs branch"
                                                                Button.onClick (fun _ ->
                                                                    let branch = if String.IsNullOrWhiteSpace state.Current.ReviewBaseDraft then "main" else state.Current.ReviewBaseDraft.Trim()
                                                                    reviewThread branch)
                                                            ]
                                                            TextBox.create [
                                                                TextBox.width 88.
                                                                TextBox.text state.Current.ReviewBaseDraft
                                                                TextBox.placeHolderText "base branch"
                                                                TextBox.onTextChanged (fun v -> state.Set { state.Current with ReviewBaseDraft = v })
                                                            ]
                                                            Button.create [
                                                                Button.content "Feedback"
                                                                Button.onClick (fun _ ->
                                                                    async {
                                                                        try
                                                                            let! _ = session.UploadFeedbackAsync("other", "desktop") |> Async.AwaitTask
                                                                            Dispatcher.UIThread.Post(fun () ->
                                                                                state.Set { state.Current with Status = state.Current.Status + " · feedback saved" })
                                                                        with ex ->
                                                                            Dispatcher.UIThread.Post(fun () ->
                                                                                state.Set { state.Current with Status = ex.Message })
                                                                    }
                                                                    |> Async.Start)
                                                            ]
                                                            Button.create [ Button.content "Queue"; Button.onClick (fun _ -> runQueue ()) ]
                                                            Button.create [
                                                                Button.content (if state.Current.ShowArchived then "Inbox" else "Archived")
                                                                Button.onClick (fun _ ->
                                                                    let archived = not state.Current.ShowArchived
                                                                    state.Set { state.Current with ShowArchived = archived }
                                                                    refreshThreads ())
                                                            ]
                                                            Button.create [
                                                                Button.content (if state.Current.ShowAllThreads then "This folder" else "All folders")
                                                                Button.onClick (fun _ ->
                                                                    state.Set { state.Current with ShowAllThreads = not state.Current.ShowAllThreads }
                                                                    refreshThreads ())
                                                            ]
                                                        ]
                                                    ]
                                                    TextBox.create [
                                                        TextBox.placeHolderText "file search"
                                                        TextBox.text state.Current.Search
                                                        TextBox.onTextChanged (fun v -> state.Set { state.Current with Search = v })
                                                        TextBox.onKeyDown (fun e ->
                                                            if e.Key = Avalonia.Input.Key.Enter then runSearch ())
                                                    ]
                                                    StackPanel.create [
                                                        StackPanel.spacing 2.
                                                        StackPanel.children (
                                                            if state.Current.SearchHits.IsEmpty then
                                                                [ TextBlock.create [
                                                                    TextBlock.text ""
                                                                    TextBlock.fontSize 11.
                                                                  ] :> IView ]
                                                            else
                                                                state.Current.SearchHits
                                                                |> List.map (fun path ->
                                                                    Button.create [
                                                                        Button.content path
                                                                        Button.horizontalAlignment HorizontalAlignment.Stretch
                                                                        Button.onClick (fun _ -> insertMention path)
                                                                    ] :> IView)
                                                        )
                                                    ]
                                                    TextBox.create [
                                                        TextBox.placeHolderText "search threads"
                                                        TextBox.text state.Current.ThreadQuery
                                                        TextBox.onTextChanged (fun v -> state.Set { state.Current with ThreadQuery = v })
                                                    ]
                                                    TextBlock.create [
                                                        TextBlock.text (
                                                            match state.Current.ShellMode with
                                                            | Chat -> "Chat — unassigned threads"
                                                            | Work -> HonestStubs.WorkLayoutMessage()
                                                            | Codex ->
                                                                if String.IsNullOrWhiteSpace state.Current.SelectedProject then "all projects"
                                                                else "project " + (state.Current.Projects |> List.tryFind (fun p -> p.Id = state.Current.SelectedProject) |> Option.map (fun p -> p.Name) |> Option.defaultValue state.Current.SelectedProject))
                                                        TextBlock.foreground Theme.muted
                                                        TextBlock.fontSize 11.
                                                        TextBlock.textWrapping TextWrapping.Wrap
                                                    ]
                                                    StackPanel.create [
                                                        StackPanel.spacing 2.
                                                        StackPanel.isVisible (state.Current.ShellMode = Codex)
                                                        StackPanel.children (
                                                            ({ Id = ""; Name = "All projects"; Root = ""; ExtraRoots = [] } :: state.Current.Projects)
                                                            |> List.map (fun proj ->
                                                                Button.create [
                                                                    Button.content proj.Name
                                                                    Button.horizontalAlignment HorizontalAlignment.Stretch
                                                                    Button.onClick (fun _ ->
                                                                        let repos = reviewRepos state.Current.Projects proj.Id
                                                                        let cwd = DesktopReviewRepos.DefaultCwd(repos)
                                                                        state.Set
                                                                            { state.Current with
                                                                                SelectedProject = proj.Id
                                                                                ReviewCwd = cwd
                                                                                ReviewRepoLabel = DesktopReviewRepos.LabelFor(repos, cwd) }
                                                                        refreshThreads ())
                                                                ] :> IView)
                                                        )
                                                    ]
                                                    DockPanel.create [
                                                        DockPanel.children [
                                                            Button.create [
                                                                Button.dock Dock.Right
                                                                Button.content "Project"
                                                                Button.onClick (fun _ ->
                                                                    let name = state.Current.ProjectDraft.Trim()
                                                                    if name.Length > 0 then
                                                                        async {
                                                                            do! session.CreateProjectAsync(name, Environment.CurrentDirectory) |> Async.AwaitTask |> Async.Ignore
                                                                            Dispatcher.UIThread.Post(fun () ->
                                                                                state.Set { state.Current with ProjectDraft = "" })
                                                                            refreshThreads ()
                                                                        }
                                                                        |> Async.Start)
                                                            ]
                                                            TextBox.create [
                                                                TextBox.placeHolderText "new project"
                                                                TextBox.text state.Current.ProjectDraft
                                                                TextBox.onTextChanged (fun v -> state.Set { state.Current with ProjectDraft = v })
                                                            ]
                                                        ]
                                                    ]
                                                    TextBlock.create [
                                                        TextBlock.text (
                                                            let pid = state.Current.SelectedProject
                                                            let extras =
                                                                state.Current.Projects
                                                                |> List.tryFind (fun p -> p.Id = pid)
                                                                |> Option.map (fun p -> p.ExtraRoots)
                                                                |> Option.defaultValue []
                                                            if extras.IsEmpty then "no extra folders"
                                                            else "extra: " + String.concat "; " extras)
                                                        TextBlock.foreground Theme.muted
                                                        TextBlock.fontSize 11.
                                                    ]
                                                    DockPanel.create [
                                                        DockPanel.children [
                                                            Button.create [
                                                                Button.dock Dock.Right
                                                                Button.content "Add folder"
                                                                Button.onClick (fun _ ->
                                                                    let pid = state.Current.SelectedProject
                                                                    let extra = state.Current.ExtraFolderDraft.Trim()
                                                                    let proj = state.Current.Projects |> List.tryFind (fun p -> p.Id = pid)
                                                                    if pid.Length > 0 && extra.Length > 0 && proj.IsSome then
                                                                        let roots = proj.Value.Root :: proj.Value.ExtraRoots @ [ System.IO.Path.GetFullPath extra ]
                                                                        async {
                                                                            do! session.UpdateProjectAsync(pid, roots) |> Async.AwaitTask |> Async.Ignore
                                                                            Dispatcher.UIThread.Post(fun () ->
                                                                                state.Set { state.Current with ExtraFolderDraft = "" })
                                                                            refreshThreads ()
                                                                        }
                                                                        |> Async.Start)
                                                            ]
                                                            TextBox.create [
                                                                TextBox.placeHolderText "extra folder"
                                                                TextBox.text state.Current.ExtraFolderDraft
                                                                TextBox.onTextChanged (fun v -> state.Set { state.Current with ExtraFolderDraft = v })
                                                            ]
                                                        ]
                                                    ]
                                                    StackPanel.create [
                                                        StackPanel.orientation Orientation.Horizontal
                                                        StackPanel.spacing 4.
                                                        StackPanel.children [
                                                            Button.create [
                                                                Button.content "Delete project"
                                                                Button.onClick (fun _ ->
                                                                    let pid = state.Current.SelectedProject
                                                                    if pid.Length > 0 then
                                                                        async {
                                                                            do! session.DeleteProjectAsync pid |> Async.AwaitTask |> Async.Ignore
                                                                            Dispatcher.UIThread.Post(fun () ->
                                                                                state.Set { state.Current with SelectedProject = "" })
                                                                            refreshThreads ()
                                                                        }
                                                                        |> Async.Start)
                                                            ]
                                                            Button.create [
                                                                Button.content "Explorer"
                                                                Button.onClick (fun _ ->
                                                                    let pid = state.Current.SelectedProject
                                                                    let root =
                                                                        state.Current.Projects
                                                                        |> List.tryFind (fun p -> p.Id = pid)
                                                                        |> Option.map (fun p -> p.Root)
                                                                        |> Option.defaultValue ""
                                                                    if root.Length > 0 then
                                                                        try
                                                                            Process.Start(FolderLaunch.ExplorerStartInfo root) |> ignore
                                                                        with ex ->
                                                                            state.Set { state.Current with Status = ex.Message })
                                                            ]
                                                        ]
                                                    ]
                                                    DockPanel.create [
                                                        DockPanel.children [
                                                            Button.create [
                                                                Button.dock Dock.Right
                                                                Button.content "Section"
                                                                Button.onClick (fun _ ->
                                                                    let name = state.Current.SectionDraft.Trim()
                                                                    if name.Length > 0 then
                                                                        async {
                                                                            do! session.CreateSectionAsync(name) |> Async.AwaitTask |> Async.Ignore
                                                                            Dispatcher.UIThread.Post(fun () ->
                                                                                state.Set { state.Current with SectionDraft = "" })
                                                                            refreshThreads ()
                                                                        }
                                                                        |> Async.Start)
                                                            ]
                                                            TextBox.create [
                                                                TextBox.placeHolderText "new section"
                                                                TextBox.text state.Current.SectionDraft
                                                                TextBox.onTextChanged (fun v -> state.Set { state.Current with SectionDraft = v })
                                                            ]
                                                        ]
                                                    ]
                                                    TextBlock.create [
                                                        TextBlock.text (if state.Current.QueueCount > 0 then sprintf "queued %d" state.Current.QueueCount else "queue empty")
                                                        TextBlock.foreground Theme.muted
                                                        TextBlock.fontSize 11.
                                                    ]
                                                    StackPanel.create [
                                                        StackPanel.spacing 2.
                                                        StackPanel.children (
                                                            state.Current.QueueItems
                                                            |> List.map (fun (qid, qtext) ->
                                                                DockPanel.create [
                                                                    DockPanel.children [
                                                                        Button.create [
                                                                            Button.dock Dock.Right
                                                                            Button.content "x"
                                                                            Button.onClick (fun _ -> deleteQueued qid)
                                                                        ]
                                                                        Button.create [
                                                                            Button.dock Dock.Right
                                                                            Button.content "v"
                                                                            Button.onClick (fun _ -> reorderQueued qid 1)
                                                                        ]
                                                                        Button.create [
                                                                            Button.dock Dock.Right
                                                                            Button.content "^"
                                                                            Button.onClick (fun _ -> reorderQueued qid -1)
                                                                        ]
                                                                        TextBlock.create [
                                                                            TextBlock.text qtext
                                                                            TextBlock.foreground Theme.muted
                                                                            TextBlock.fontSize 11.
                                                                            TextBlock.textTrimming TextTrimming.CharacterEllipsis
                                                                        ]
                                                                    ]
                                                                ] :> IView)
                                                        )
                                                    ]
                                                    TextBox.create [
                                                        TextBox.placeHolderText "API key"
                                                        TextBox.passwordChar '*'
                                                        TextBox.text apiKey.Current
                                                        TextBox.onTextChanged apiKey.Set
                                                    ]
                                                    Button.create [
                                                        Button.content "Save key"
                                                        Button.onClick (fun _ ->
                                                            if apiKey.Current.Trim().Length > 0 then
                                                                let key = apiKey.Current.Trim()
                                                                apiKey.Set ""
                                                                async {
                                                                    do! session.LoginApiKeyAsync(key) |> Async.AwaitTask |> Async.Ignore
                                                                    refreshChrome ()
                                                                }
                                                                |> Async.Start)
                                                    ]
                                                    Button.create [
                                                        Button.content "Logout"
                                                        Button.onClick (fun _ ->
                                                            async {
                                                                do! session.LogoutAsync() |> Async.AwaitTask |> Async.Ignore
                                                                refreshChrome ()
                                                            }
                                                            |> Async.Start)
                                                    ]
                                                    TextBlock.create [
                                                        TextBlock.text ("Remote control: " + state.Current.RemoteControl)
                                                        TextBlock.foreground Theme.muted
                                                        TextBlock.fontSize 11.
                                                    ]
                                                    TextBlock.create [
                                                        TextBlock.text state.Current.RateLimits
                                                        TextBlock.foreground Theme.muted
                                                        TextBlock.fontSize 11.
                                                        TextBlock.textWrapping TextWrapping.Wrap
                                                    ]
                                                    TextBlock.create [
                                                        TextBlock.text "Remote enable/disable is not offered here; there is no transport."
                                                        TextBlock.foreground Theme.muted
                                                        TextBlock.fontSize 11.
                                                        TextBlock.textWrapping TextWrapping.Wrap
                                                    ]
                                                    TextBlock.create [
                                                        TextBlock.text "SSH remote project"
                                                        TextBlock.fontWeight FontWeight.SemiBold
                                                        TextBlock.foreground Theme.accent
                                                    ]
                                                    TextBlock.create [
                                                        TextBlock.text "Tunnel underDevelopment. Not paired."
                                                        TextBlock.foreground Theme.muted
                                                        TextBlock.fontSize 11.
                                                        TextBlock.textWrapping TextWrapping.Wrap
                                                    ]
                                                    ComboBox.create [
                                                        ComboBox.dataItems (SshConfig.ConcreteHostsFromFile() |> Seq.toList)
                                                        ComboBox.selectedItem sshHostDraft.Current
                                                        ComboBox.onSelectedItemChanged (fun item ->
                                                            match item with
                                                            | :? string as host when host.Trim().Length > 0 -> sshHostDraft.Set host
                                                            | _ -> ())
                                                    ]
                                                    TextBox.create [
                                                        TextBox.placeHolderText "Remote folder"
                                                        TextBox.text sshFolderDraft.Current
                                                        TextBox.onTextChanged sshFolderDraft.Set
                                                    ]
                                                    Button.create [
                                                        Button.content "Save SSH remote project"
                                                        Button.onClick (fun _ ->
                                                            try
                                                                let _ = SshRemoteProjects.Persist(sshHostDraft.Current, sshFolderDraft.Current)
                                                                refreshThreads ()
                                                                state.Set { state.Current with Status = "SSH remote project saved (tunnel underDevelopment)" }
                                                            with ex ->
                                                                state.Set { state.Current with Status = ex.Message })
                                                    ]
                                                    ComboBox.create [
                                                        ComboBox.dataItems state.Current.Models
                                                        ComboBox.selectedItem modelDraft.Current
                                                        ComboBox.onSelectedItemChanged (fun item ->
                                                            match item with
                                                            | :? string as id when id.Trim().Length > 0 ->
                                                                modelDraft.Set id
                                                                writeConfig "model" (id)
                                                            | _ -> ())
                                                    ]
                                                    TextBox.create [
                                                        TextBox.placeHolderText "Custom model"
                                                        TextBox.text modelDraft.Current
                                                        TextBox.onTextChanged modelDraft.Set
                                                    ]
                                                    Button.create [
                                                        Button.content "Use model"
                                                        Button.onClick (fun _ ->
                                                            if modelDraft.Current.Trim().Length > 0 then
                                                                writeConfig "model" (modelDraft.Current.Trim()))
                                                    ]
                                                    StackPanel.create [
                                                        StackPanel.orientation Orientation.Horizontal
                                                        StackPanel.spacing 4.
                                                        StackPanel.children [
                                                            Button.create [
                                                                Button.content "ro"
                                                                Button.onClick (fun _ ->
                                                                    writeConfig "sandbox_mode" "read-only")
                                                            ]
                                                            Button.create [
                                                                Button.content "ws"
                                                                Button.onClick (fun _ ->
                                                                    writeConfig "sandbox_mode" "workspace-write")
                                                            ]
                                                            Button.create [
                                                                Button.content "full"
                                                                Button.onClick (fun _ ->
                                                                    writeConfig "sandbox_mode" "danger-full-access")
                                                            ]
                                                        ]
                                                    ]
                                                    StackPanel.create [
                                                        StackPanel.orientation Orientation.Horizontal
                                                        StackPanel.spacing 4.
                                                        StackPanel.children [
                                                            Button.create [
                                                                Button.content "default"
                                                                Button.onClick (fun _ -> writeConfig "collaboration_mode" "default")
                                                            ]
                                                            Button.create [
                                                                Button.content "plan"
                                                                Button.onClick (fun _ -> writeConfig "collaboration_mode" "plan")
                                                            ]
                                                            Button.create [
                                                                Button.content "pair"
                                                                Button.onClick (fun _ -> writeConfig "collaboration_mode" "pair")
                                                            ]
                                                        ]
                                                    ]
                                                    TextBlock.create [
                                                        TextBlock.text "Extra read roots"
                                                        TextBlock.foreground Theme.muted
                                                        TextBlock.fontSize 11.
                                                    ]
                                                    StackPanel.create [
                                                        StackPanel.spacing 2.
                                                        StackPanel.children (
                                                            let roots = SandboxRoots.List()
                                                            if roots.Count = 0 then
                                                                [ TextBlock.create [
                                                                    TextBlock.text "none"
                                                                    TextBlock.foreground Theme.muted
                                                                    TextBlock.fontSize 11.
                                                                  ] :> IView ]
                                                            else
                                                                roots
                                                                |> Seq.map (fun r ->
                                                                    TextBlock.create [
                                                                        TextBlock.text r
                                                                        TextBlock.fontSize 11.
                                                                        TextBlock.foreground Theme.text
                                                                        TextBlock.textTrimming TextTrimming.CharacterEllipsis
                                                                    ] :> IView)
                                                                |> Seq.toList
                                                        )
                                                    ]
                                                    DockPanel.create [
                                                        DockPanel.children [
                                                            Button.create [
                                                                Button.dock Dock.Right
                                                                Button.content "Add read dir"
                                                                Button.onClick (fun _ ->
                                                                    let raw = sandboxRootDraft.Current.Trim()
                                                                    if raw.Length > 0 then
                                                                        try
                                                                            SandboxRoots.Add(Path.GetFullPath(raw, Environment.CurrentDirectory)) |> ignore
                                                                            sandboxRootDraft.Set ""
                                                                            refreshChrome ()
                                                                        with ex ->
                                                                            state.Set { state.Current with Status = ex.Message })
                                                            ]
                                                            TextBox.create [
                                                                TextBox.placeHolderText "absolute or relative dir"
                                                                TextBox.text sandboxRootDraft.Current
                                                                TextBox.onTextChanged sandboxRootDraft.Set
                                                            ]
                                                        ]
                                                    ]
                                                    Button.create [
                                                        Button.content "Import CLAUDE.md"
                                                        Button.onClick (fun _ ->
                                                            let found = ExternalAgentImport.Detect([Environment.CurrentDirectory], false)
                                                            let n = ExternalAgentImport.Import(found).Count
                                                            state.Set { state.Current with Status = sprintf "imported %d" n })
                                                    ]
                                                    Button.create [
                                                        Button.content "Write AGENTS.md"
                                                        Button.onClick (fun _ ->
                                                            async {
                                                                try
                                                                    let! written = session.WriteAgentsMarkdownAsync() |> Async.AwaitTask
                                                                    Dispatcher.UIThread.Post(fun () ->
                                                                        state.Set { state.Current with Status = if jsBool written "created" then "wrote AGENTS.md" else "AGENTS.md exists" })
                                                                with ex ->
                                                                    Dispatcher.UIThread.Post(fun () -> state.Set { state.Current with Status = ex.Message })
                                                            }
                                                            |> Async.Start)
                                                    ]
                                                    Button.create [
                                                        Button.content "Doctor"
                                                        Button.onClick (fun _ ->
                                                            async {
                                                                try
                                                                    let! report = session.RunDoctorAsync() |> Async.AwaitTask
                                                                    let mutable checks = Unchecked.defaultof<JsonElement>
                                                                    let n = if report.TryGetProperty("checks", &checks) && checks.ValueKind = JsonValueKind.Array then checks.GetArrayLength() else 0
                                                                    Dispatcher.UIThread.Post(fun () ->
                                                                        state.Set { state.Current with Status = sprintf "doctor %s (%d checks)" (jsStr report "status") n })
                                                                with ex ->
                                                                    Dispatcher.UIThread.Post(fun () -> state.Set { state.Current with Status = ex.Message })
                                                            }
                                                            |> Async.Start)
                                                    ]
                                                    Button.create [
                                                        Button.content "Setup sandbox"
                                                        Button.onClick (fun _ ->
                                                            async {
                                                                try
                                                                    do! session.SetupWindowsSandboxAsync("unelevated") |> Async.AwaitTask |> Async.Ignore
                                                                    refreshChrome ()
                                                                with ex ->
                                                                    Dispatcher.UIThread.Post(fun () ->
                                                                        state.Set { state.Current with Status = ex.Message })
                                                            }
                                                            |> Async.Start)
                                                    ]
                                                    StackPanel.create [
                                                        StackPanel.orientation Orientation.Horizontal
                                                        StackPanel.spacing 4.
                                                        StackPanel.children [
                                                            Button.create [
                                                                Button.content "ask"
                                                                Button.onClick (fun _ ->
                                                                    writeConfig "approval_policy" "on-request")
                                                            ]
                                                            Button.create [
                                                                Button.content "never"
                                                                Button.onClick (fun _ ->
                                                                    writeConfig "approval_policy" "never")
                                                            ]
                                                            Button.create [
                                                                Button.content "strict"
                                                                Button.onClick (fun _ ->
                                                                    writeConfig "approval_policy" "untrusted")
                                                            ]
                                                        ]
                                                    ]
                                                    StackPanel.create [
                                                        StackPanel.orientation Orientation.Horizontal
                                                        StackPanel.spacing 4.
                                                        StackPanel.children [
                                                            Button.create [
                                                                Button.content "low"
                                                                Button.onClick (fun _ ->
                                                                    writeConfig "model_reasoning_effort" "low")
                                                            ]
                                                            Button.create [
                                                                Button.content "med"
                                                                Button.onClick (fun _ ->
                                                                    writeConfig "model_reasoning_effort" "medium")
                                                            ]
                                                            Button.create [
                                                                Button.content "high"
                                                                Button.onClick (fun _ ->
                                                                    writeConfig "model_reasoning_effort" "high")
                                                            ]
                                                            Button.create [
                                                                Button.content "xhigh"
                                                                Button.onClick (fun _ ->
                                                                    writeConfig "model_reasoning_effort" "xhigh")
                                                            ]
                                                            Button.create [
                                                                Button.content "ultra"
                                                                Button.onClick (fun _ ->
                                                                    writeConfig "model_reasoning_effort" "ultra")
                                                            ]
                                                        ]
                                                    ]
                                                    StackPanel.create [
                                                        StackPanel.orientation Orientation.Horizontal
                                                        StackPanel.spacing 4.
                                                        StackPanel.children [
                                                            Button.create [
                                                                Button.content "default"
                                                                Button.onClick (fun _ ->
                                                                    writeConfig "collaboration_mode" "default")
                                                            ]
                                                            Button.create [
                                                                Button.content "plan"
                                                                Button.onClick (fun _ ->
                                                                    writeConfig "collaboration_mode" "plan")
                                                            ]
                                                            Button.create [
                                                                Button.content "pair"
                                                                Button.onClick (fun _ ->
                                                                    writeConfig "collaboration_mode" "pair")
                                                            ]
                                                        ]
                                                    ]
                                                ]
                                            ]
                                            StackPanel.create [
                                                StackPanel.margin 8.
                                                StackPanel.children (
                                                    let q = state.Current.ThreadQuery
                                                    let filtered =
                                                        state.Current.Threads
                                                        |> List.filter (fun row ->
                                                            String.IsNullOrWhiteSpace q
                                                            || row.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                                                            || row.Id.Contains(q, StringComparison.OrdinalIgnoreCase))
                                                    let sections = state.Current.Sections
                                                    let header (title: string) : IView =
                                                        TextBlock.create [
                                                            TextBlock.text title
                                                            TextBlock.foreground Theme.accent
                                                            TextBlock.fontSize 11.
                                                            TextBlock.fontWeight FontWeight.SemiBold
                                                            TextBlock.margin (Thickness(8, 10, 8, 4))
                                                        ] :> IView
                                                    let cycleSection threadId =
                                                        if sections.IsEmpty then ()
                                                        else
                                                            let current =
                                                                state.Current.Threads
                                                                |> List.tryFind (fun t -> t.Id = threadId)
                                                                |> Option.map (fun t -> t.SectionId)
                                                                |> Option.defaultValue ""
                                                            let idx = sections |> List.tryFindIndex (fun s -> s.Id = current)
                                                            let nextId =
                                                                match idx with
                                                                | None -> sections.Head.Id
                                                                | Some i when i = sections.Length - 1 -> null
                                                                | Some i -> sections.[i + 1].Id
                                                            async {
                                                                do! session.MoveToSectionAsync(threadId, nextId) |> Async.AwaitTask |> Async.Ignore
                                                                refreshThreads ()
                                                            }
                                                            |> Async.Start
                                                    let rowView (row: ThreadRow) : IView =
                                                        DockPanel.create [
                                                            DockPanel.children [
                                                                Button.create [
                                                                    Button.dock Dock.Right
                                                                    Button.content (
                                                                        if state.Current.ShowArchived then "open"
                                                                        elif row.Pinned then "*"
                                                                        else "pin")
                                                                    Button.onClick (fun _ ->
                                                                        if state.Current.ShowArchived then
                                                                            async {
                                                                                do! session.UnarchiveAsync row.Id |> Async.AwaitTask |> Async.Ignore
                                                                                Dispatcher.UIThread.Post(fun () ->
                                                                                    state.Set { state.Current with ShowArchived = false })
                                                                                resumeThread row.Id
                                                                            }
                                                                            |> Async.Start
                                                                        else
                                                                            async {
                                                                                do! session.SetPinnedAsync(row.Id, not row.Pinned) |> Async.AwaitTask |> Async.Ignore
                                                                                refreshThreads ()
                                                                            }
                                                                            |> Async.Start)
                                                                ]
                                                                Button.create [
                                                                    Button.dock Dock.Right
                                                                    Button.content (if state.Current.ShowArchived then "del" else "sec")
                                                                    Button.onClick (fun _ ->
                                                                        if state.Current.ShowArchived then
                                                                            async {
                                                                                do! session.DeleteThreadAsync row.Id |> Async.AwaitTask |> Async.Ignore
                                                                                refreshThreads ()
                                                                            }
                                                                            |> Async.Start
                                                                        else
                                                                            cycleSection row.Id)
                                                                ]
                                                                Button.create [
                                                                    Button.horizontalAlignment HorizontalAlignment.Stretch
                                                                    Button.background Theme.panel
                                                                    Button.onClick (fun _ -> resumeThread row.Id)
                                                                    Button.content (
                                                                        StackPanel.create [
                                                                            StackPanel.margin (Thickness(8, 6))
                                                                            StackPanel.children [
                                                                                TextBlock.create [
                                                                                    TextBlock.text row.Title
                                                                                    TextBlock.foreground Theme.text
                                                                                    TextBlock.textTrimming TextTrimming.CharacterEllipsis
                                                                                ]
                                                                                TextBlock.create [
                                                                                    TextBlock.text (
                                                                                        if state.Current.ShowAllThreads && not (String.IsNullOrWhiteSpace row.Cwd) then
                                                                                            row.Subtitle + "  " + row.Cwd
                                                                                        else row.Subtitle)
                                                                                    TextBlock.foreground Theme.muted
                                                                                    TextBlock.fontSize 11.
                                                                                    TextBlock.textTrimming TextTrimming.CharacterEllipsis
                                                                                ]
                                                                            ]
                                                                        ]
                                                                    )
                                                                ]
                                                            ]
                                                        ] :> IView
                                                    let grouped =
                                                        sections
                                                        |> List.collect (fun sec ->
                                                            let rows = filtered |> List.filter (fun r -> r.SectionId = sec.Id)
                                                            if rows.IsEmpty then []
                                                            else header sec.Name :: (rows |> List.map rowView))
                                                    let inbox = filtered |> List.filter (fun r -> String.IsNullOrWhiteSpace r.SectionId)
                                                    let inboxViews =
                                                        if inbox.IsEmpty then []
                                                        elif sections.IsEmpty then inbox |> List.map rowView
                                                        else header "Inbox" :: (inbox |> List.map rowView)
                                                    inboxViews @ grouped
                                                )
                                            ]
                                        ]
                                    ]
                                )
                            ]
                            DockPanel.create [
                                Grid.column 1
                                DockPanel.children [
                                    Border.create [
                                        Border.dock Dock.Bottom
                                        Border.background Theme.panel
                                        Border.borderBrush Theme.border
                                        Border.borderThickness (Thickness(0, 1, 0, 0))
                                        Border.padding 16.
                                        Border.child (
                                            StackPanel.create [
                                                StackPanel.spacing 10.
                                                StackPanel.children [
                                                    if not (String.IsNullOrWhiteSpace state.Current.GoalObjective) then
                                                        TextBlock.create [
                                                            TextBlock.text ("Goal · " + state.Current.GoalStatus + "  " + state.Current.GoalObjective)
                                                            TextBlock.foreground Theme.accent
                                                            TextBlock.fontSize 11.
                                                            TextBlock.textWrapping TextWrapping.Wrap
                                                            TextBlock.textTrimming TextTrimming.CharacterEllipsis
                                                        ]
                                                    TextBlock.create [
                                                        TextBlock.text (
                                                            (if state.Current.Busy then "▸ working  " else "▸ idle  ")
                                                            + state.Current.Status
                                                            + "  ·  "
                                                            + TuiKeymap.Hint())
                                                        TextBlock.foreground Theme.muted
                                                        TextBlock.fontSize 11.
                                                        TextBlock.textTrimming TextTrimming.CharacterEllipsis
                                                    ]
                                                    if state.Current.UserInputId <> "" then
                                                        Border.create [
                                                            Border.background Theme.card
                                                            Border.borderBrush Theme.accent
                                                            Border.borderThickness 1.
                                                            Border.cornerRadius 8.
                                                            Border.padding 12.
                                                            Border.child (
                                                                StackPanel.create [
                                                                    StackPanel.spacing 8.
                                                                    StackPanel.children [
                                                                        TextBlock.create [
                                                                            TextBlock.text "Agent needs input"
                                                                            TextBlock.foreground Theme.accent
                                                                            TextBlock.fontWeight FontWeight.SemiBold
                                                                        ]
                                                                        TextBlock.create [
                                                                            TextBlock.text state.Current.UserInputPrompt
                                                                            TextBlock.foreground Theme.text
                                                                            TextBlock.textWrapping TextWrapping.Wrap
                                                                            TextBlock.fontSize 12.
                                                                        ]
                                                                        TextBox.create [
                                                                            TextBox.text state.Current.UserInputDraft
                                                                            TextBox.placeHolderText "Your answer"
                                                                            TextBox.onTextChanged (fun v -> state.Set { state.Current with UserInputDraft = v })
                                                                        ]
                                                                        Button.create [
                                                                            Button.content "Submit answer"
                                                                            Button.onClick (fun _ ->
                                                                                let rid = state.Current.UserInputId
                                                                                let draft = state.Current.UserInputDraft
                                                                                if state.Current.UserInputPrompt.StartsWith("MCP:") then
                                                                                    let payload = sprintf "{\"action\":\"accept\",\"content\":{\"text\":\"%s\"}}" (draft.Replace("\"", "\\\""))
                                                                                    session.RespondMcpElicitationAsync(rid, true, payload) |> ignore
                                                                                else
                                                                                    session.RespondUserInputAsync(rid, draft) |> ignore
                                                                                state.Set { state.Current with UserInputId = ""; UserInputPrompt = ""; UserInputDraft = "" })
                                                                        ]
                                                                    ]
                                                                ]
                                                            )
                                                        ]
                                                    if state.Current.ApprovalCommand <> "" then
                                                        Border.create [
                                                            Border.background Theme.dangerBg
                                                            Border.borderBrush (SolidColorBrush(Color.Parse "#7F1D1D"))
                                                            Border.borderThickness 1.
                                                            Border.cornerRadius 8.
                                                            Border.padding 12.
                                                            Border.child (
                                                                DockPanel.create [
                                                                    DockPanel.children [
                                                                        StackPanel.create [
                                                                            StackPanel.dock Dock.Right
                                                                            StackPanel.orientation Orientation.Horizontal
                                                                            StackPanel.spacing 8.
                                                                            StackPanel.children [
                                                                                Button.create [
                                                                                    Button.content "Deny"
                                                                                    Button.onClick (fun _ ->
                                                                                        session.RespondApprovalAsync(state.Current.ApprovalId, false, "user denied") |> ignore
                                                                                        state.Set { state.Current with ApprovalId = ""; ApprovalCommand = "" })
                                                                                ]
                                                                                Button.create [
                                                                                    Button.content "Allow"
                                                                                    Button.background (SolidColorBrush(Color.Parse "#166534"))
                                                                                    Button.foreground Brushes.White
                                                                                    Button.onClick (fun _ ->
                                                                                        session.RespondApprovalAsync(state.Current.ApprovalId, true, null) |> ignore
                                                                                        state.Set { state.Current with ApprovalId = ""; ApprovalCommand = "" })
                                                                                ]
                                                                            ]
                                                                        ]
                                                                        StackPanel.create [
                                                                            StackPanel.children [
                                                                                TextBlock.create [
                                                                                    TextBlock.text "Approval required"
                                                                                    TextBlock.foreground Theme.danger
                                                                                    TextBlock.fontWeight FontWeight.SemiBold
                                                                                ]
                                                                                TextBlock.create [
                                                                                    TextBlock.text state.Current.ApprovalCommand
                                                                                    TextBlock.foreground Theme.danger
                                                                                    TextBlock.textWrapping TextWrapping.Wrap
                                                                                ]
                                                                            ]
                                                                        ]
                                                                    ]
                                                                ]
                                                            )
                                                        ]
                                                    StackPanel.create [
                                                        StackPanel.spacing 4.
                                                        StackPanel.children (
                                                            state.Current.SlashHits
                                                            |> List.map (fun cmd ->
                                                                Button.create [
                                                                    Button.content cmd
                                                                    Button.onClick (fun _ ->
                                                                        state.Set { state.Current with Composer = cmd + " "; SlashHits = [] })
                                                                ] :> IView)
                                                        )
                                                    ]
                                                    StackPanel.create [
                                                        StackPanel.spacing 4.
                                                        StackPanel.children (
                                                            state.Current.SkillHits
                                                            |> List.map (fun name ->
                                                                Button.create [
                                                                    Button.content ("$" + name)
                                                                    Button.onClick (fun _ ->
                                                                        let src = state.Current.Composer
                                                                        let dollar = src.LastIndexOf('$')
                                                                        let next = if dollar >= 0 then src.Substring(0, dollar) + "$" + name + " " else src + "$" + name + " "
                                                                        state.Set { state.Current with Composer = next; SkillHits = [] })
                                                                ] :> IView)
                                                        )
                                                    ]
                                                    StackPanel.create [
                                                        StackPanel.spacing 4.
                                                        StackPanel.children (
                                                            state.Current.Mentions
                                                            |> List.map (fun path ->
                                                                Button.create [
                                                                    Button.content path
                                                                    Button.onClick (fun _ ->
                                                                        let src = state.Current.Composer
                                                                        let at = src.LastIndexOf('@')
                                                                        let next = if at >= 0 then src.Substring(0, at) + path else src + path
                                                                        state.Set { state.Current with Composer = next; Mentions = [] })
                                                                ] :> IView)
                                                        )
                                                    ]
                                                    StackPanel.create [
                                                        StackPanel.orientation Orientation.Horizontal
                                                        StackPanel.spacing 4.
                                                        StackPanel.children (
                                                            state.Current.Attachments
                                                            |> List.map (fun path ->
                                                                Button.create [
                                                                    Button.padding (Thickness(4))
                                                                    Button.content (
                                                                        DockPanel.create [
                                                                            DockPanel.children [
                                                                                Image.create [
                                                                                    Image.width 36.
                                                                                    Image.height 36.
                                                                                    Image.stretch Stretch.UniformToFill
                                                                                    Image.source (new Bitmap(path))
                                                                                    Image.margin (Thickness(0, 0, 6, 0))
                                                                                ]
                                                                                TextBlock.create [
                                                                                    TextBlock.text (System.IO.Path.GetFileName path)
                                                                                    TextBlock.verticalAlignment VerticalAlignment.Center
                                                                                    TextBlock.foreground Theme.text
                                                                                ]
                                                                            ]
                                                                        ]
                                                                    )
                                                                    Button.onClick (fun _ ->
                                                                        state.Set { state.Current with Attachments = state.Current.Attachments |> List.filter (fun p -> p <> path) })
                                                                ] :> IView)
                                                        )
                                                    ]
                                                    TextBlock.create [
                                                        TextBlock.text (TuiKeymap.Hint() + "  ·  ctrl+k commands  ·  /help")
                                                        TextBlock.foreground Theme.muted
                                                        TextBlock.fontSize 11.
                                                    ]
                                                    Grid.create [
                                                        Grid.columnDefinitions "*,Auto,Auto,Auto"
                                                        Grid.children [
                                                            TextBox.create [
                                                                StyledElement.name "composer"
                                                                TextBox.text state.Current.Composer
                                                                TextBox.placeHolderText "Ask CodexSharp to work in this repo…"
                                                                TextBox.acceptsReturn true
                                                                TextBox.minHeight 56.
                                                                TextBox.maxHeight 140.
                                                                TextBox.textWrapping TextWrapping.Wrap
                                                                TextBox.background Theme.bg
                                                                TextBox.foreground Theme.text
                                                                TextBox.onTextChanged (fun t ->
                                                                    let skills =
                                                                        SkillSlash.Hits(t, CodexPaths.Home, Environment.CurrentDirectory)
                                                                        |> Seq.map (fun s -> s.Name)
                                                                        |> Seq.toList
                                                                    state.Set { state.Current with Composer = t; SlashHits = slashHits t; SkillHits = skills }
                                                                    async {
                                                                        try
                                                                            let! hits = session.MentionHitsAsync(t) |> Async.AwaitTask
                                                                            Dispatcher.UIThread.Post(fun () ->
                                                                                if state.Current.Composer = t then
                                                                                    state.Set { state.Current with Mentions = List.ofSeq hits })
                                                                        with _ ->
                                                                            ()
                                                                    }
                                                                    |> Async.Start)
                                                                TextBox.onKeyDown (fun e ->
                                                                    if handleDesktopChord e then () else
                                                                    let chord =
                                                                        let parts = System.Collections.Generic.List<string>()
                                                                        if e.KeyModifiers.HasFlag Avalonia.Input.KeyModifiers.Control then parts.Add "ctrl"
                                                                        if e.KeyModifiers.HasFlag Avalonia.Input.KeyModifiers.Alt then parts.Add "alt"
                                                                        if e.KeyModifiers.HasFlag Avalonia.Input.KeyModifiers.Shift && e.Key <> Avalonia.Input.Key.Enter then parts.Add "shift"
                                                                        let keyName =
                                                                            match e.Key with
                                                                            | Avalonia.Input.Key.Enter -> "enter"
                                                                            | Avalonia.Input.Key.Escape -> "esc"
                                                                            | Avalonia.Input.Key.C -> "c"
                                                                            | Avalonia.Input.Key.N -> "n"
                                                                            | Avalonia.Input.Key.Q -> "q"
                                                                            | _ -> e.Key.ToString().ToLowerInvariant()
                                                                        String.Join("+", Array.append (parts.ToArray()) [| keyName |])
                                                                    let action =
                                                                        TuiKeymap.Load()
                                                                        |> Seq.tryFind (fun b -> b.Keys.Equals(chord, StringComparison.OrdinalIgnoreCase))
                                                                        |> Option.map (fun b -> b.Action)
                                                                    if applyVim e then
                                                                        ()
                                                                    elif e.Key = Avalonia.Input.Key.Enter && not (e.KeyModifiers.HasFlag Avalonia.Input.KeyModifiers.Shift) && not (e.KeyModifiers.HasFlag Avalonia.Input.KeyModifiers.Alt) then
                                                                        e.Handled <- true
                                                                        send ()
                                                                    elif action = Some "queue" then
                                                                        e.Handled <- true
                                                                        let queued = state.Current.Composer.Trim()
                                                                        if queued <> "" then
                                                                            state.Set { state.Current with Composer = "" }
                                                                            async {
                                                                                do! session.QueueAddAsync queued |> Async.AwaitTask |> Async.Ignore
                                                                                let! listed = session.QueueListAsync() |> Async.AwaitTask
                                                                                let items = parseQueue listed
                                                                                Dispatcher.UIThread.Post(fun () ->
                                                                                    state.Set { state.Current with QueueItems = items; QueueCount = items.Length; Status = "queued" })
                                                                            }
                                                                            |> Async.Start
                                                                    elif action = Some "interrupt" && state.Current.Busy then
                                                                        e.Handled <- true
                                                                        session.InterruptAsync() |> ignore
                                                                    elif action = Some "new" then
                                                                        e.Handled <- true
                                                                        newThread ()
                                                                    elif action = Some "open_external_editor" then
                                                                        e.Handled <- true
                                                                        try
                                                                            let next = ExternalEditor.Edit(state.Current.Composer)
                                                                            state.Set { state.Current with Composer = next; Status = "edited in VISUAL/EDITOR" }
                                                                        with ex ->
                                                                            state.Set { state.Current with Status = ex.Message }
                                                                    elif action = Some "delete_backward_word" then
                                                                        applyEditor e (fun buf -> buf.KillWordBack())
                                                                    elif action = Some "kill_line_start" then
                                                                        applyEditor e (fun buf -> buf.KillToStart())
                                                                    elif action = Some "kill_line_end" then
                                                                        applyEditor e (fun buf -> buf.KillToEnd())
                                                                    elif action = Some "yank" then
                                                                        applyEditor e (fun buf -> buf.YankInsert())
                                                                    elif e.Key = Avalonia.Input.Key.Up && (String.IsNullOrWhiteSpace state.Current.Composer || e.KeyModifiers.HasFlag Avalonia.Input.KeyModifiers.Control) && not state.Current.ComposerHistory.IsEmpty then
                                                                        e.Handled <- true
                                                                        let draft = if state.Current.HistoryCursor < 0 then state.Current.Composer else state.Current.ComposerDraft
                                                                        let idx = if state.Current.HistoryCursor < 0 then 0 else min (state.Current.HistoryCursor + 1) (state.Current.ComposerHistory.Length - 1)
                                                                        state.Set { state.Current with Composer = state.Current.ComposerHistory.[idx]; HistoryCursor = idx; ComposerDraft = draft; SlashHits = [] }
                                                                    elif e.Key = Avalonia.Input.Key.Down && state.Current.HistoryCursor >= 0 then
                                                                        e.Handled <- true
                                                                        let idx = state.Current.HistoryCursor - 1
                                                                        if idx < 0 then
                                                                            state.Set { state.Current with Composer = state.Current.ComposerDraft; HistoryCursor = -1; SlashHits = [] }
                                                                        else
                                                                            state.Set { state.Current with Composer = state.Current.ComposerHistory.[idx]; HistoryCursor = idx; SlashHits = [] }
                                                                    elif e.Key = Avalonia.Input.Key.Tab && not state.Current.SlashHits.IsEmpty then
                                                                        e.Handled <- true
                                                                        state.Set { state.Current with Composer = state.Current.SlashHits.Head + " "; SlashHits = [] }
                                                                    elif e.Key = Avalonia.Input.Key.Tab && not state.Current.Mentions.IsEmpty then
                                                                        e.Handled <- true
                                                                        let hit = state.Current.Mentions.Head
                                                                        let src = state.Current.Composer
                                                                        let at = src.LastIndexOf('@')
                                                                        let next = if at >= 0 then src.Substring(0, at) + hit else src + hit
                                                                        state.Set { state.Current with Composer = next; Mentions = [] }
                                                                    elif e.Key = Avalonia.Input.Key.V && e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) then
                                                                        pasteClipboardImage ())
                                                            ]
                                                            Button.create [
                                                                Grid.column 1
                                                                Button.content "Attach"
                                                                Button.margin (Thickness(10, 0, 0, 0))
                                                                Button.width 72.
                                                                Button.height 36.
                                                                Button.onClick (fun _ -> attachImage ())
                                                            ]
                                                            Button.create [
                                                                Grid.column 2
                                                                Button.content "Stop"
                                                                Button.margin (Thickness(10, 0, 0, 0))
                                                                Button.width 72.
                                                                Button.height 36.
                                                                Button.isEnabled state.Current.Busy
                                                                Button.onClick (fun _ -> session.InterruptAsync() |> ignore)
                                                            ]
                                                            Button.create [
                                                                Grid.column 3
                                                                Button.content "Send"
                                                                Button.margin (Thickness(8, 0, 0, 0))
                                                                Button.width 88.
                                                                Button.height 36.
                                                                Button.background Theme.accent
                                                                Button.foreground (SolidColorBrush(Color.Parse "#111827"))
                                                                Button.fontWeight FontWeight.SemiBold
                                                                Button.onClick (fun _ -> send ())
                                                            ]
                                                        ]
                                                    ]
                                                ]
                                            ]
                                        )
                                    ]
                                    ScrollViewer.create [
                                        ScrollViewer.content (
                                            StackPanel.create [
                                                StackPanel.margin (Thickness(24, 16))
                                                StackPanel.children (
                                                    let retry url =
                                                        state.Set { state.Current with RemoteImages = Map.add url "pending" state.Current.RemoteImages }
                                                        async {
                                                            let! cached = ImageUrlCache.DownloadAsync(url) |> Async.AwaitTask
                                                            let status = if isNull cached then "fail" else "ok"
                                                            Dispatcher.UIThread.Post(fun () ->
                                                                state.Set
                                                                    { state.Current with
                                                                        RemoteImages = Map.add url status state.Current.RemoteImages
                                                                        Timeline = state.Current.Timeline |> List.map id })
                                                        }
                                                        |> Async.Start
                                                    state.Current.Timeline |> List.map (itemCard state.Current.RemoteImages retry (fun path ->
                                                                        if SystemFilePreview.IsOffice path then
                                                                            let preview = SystemFilePreview.TryOpen path
                                                                            let note = preview.Path + (if preview.Opened then "" else "  " + (if preview.Error = null then "unavailable" else preview.Error))
                                                                            state.Set { state.Current with Status = note }
                                                                        else
                                                                            insertMention path))
                                                )
                                            ]
                                        )
                                    ]
                                ]
                            ]
                            Border.create [
                                Grid.column 2
                                Border.background Theme.panel
                                Border.borderBrush Theme.border
                                Border.borderThickness (Thickness(1, 0, 0, 0))
                                Border.padding 12.
                                Border.child (
                                    StackPanel.create [
                                        StackPanel.spacing 8.
                                        StackPanel.children [
                                            TextBlock.create [
                                                TextBlock.text "Plan"
                                                TextBlock.fontWeight FontWeight.SemiBold
                                                TextBlock.foreground Theme.accent
                                            ]
                                            TextBlock.create [
                                                TextBlock.text (if String.IsNullOrWhiteSpace state.Current.Plan then "No plan yet. The agent can call update_plan." else state.Current.Plan)
                                                TextBlock.textWrapping TextWrapping.Wrap
                                                TextBlock.foreground Theme.text
                                                TextBlock.fontFamily (FontFamily "Cascadia Code, Consolas, monospace")
                                            ]
                                            TextBlock.create [
                                                TextBlock.text "Diff"
                                                TextBlock.fontWeight FontWeight.SemiBold
                                                TextBlock.foreground Theme.accent
                                                TextBlock.margin (Thickness(0, 12, 0, 0))
                                            ]
                                            StackPanel.create [
                                                StackPanel.spacing 0.
                                                StackPanel.children (diffViews state.Current.Diff)
                                            ]
                                            DockPanel.create [
                                                DockPanel.margin (Thickness(0, 12, 0, 0))
                                                DockPanel.children [
                                                    Button.create [
                                                        Button.dock Dock.Right
                                                        Button.content "Load PR"
                                                        Button.onClick (fun _ -> refreshPrComments ())
                                                    ]
                                                    TextBlock.create [
                                                        TextBlock.text "Review comments"
                                                        TextBlock.fontWeight FontWeight.SemiBold
                                                        TextBlock.foreground Theme.accent
                                                        TextBlock.verticalAlignment VerticalAlignment.Center
                                                    ]
                                                ]
                                            ]
                                            TextBlock.create [
                                                TextBlock.text (if String.IsNullOrWhiteSpace state.Current.PrNote then "" else state.Current.PrNote)
                                                TextBlock.isVisible (not (String.IsNullOrWhiteSpace state.Current.PrNote))
                                                TextBlock.foreground Theme.muted
                                                TextBlock.fontSize 11.
                                                TextBlock.textWrapping TextWrapping.Wrap
                                            ]
                                            StackPanel.create [
                                                StackPanel.spacing 2.
                                                StackPanel.children (
                                                    state.Current.PrComments
                                                    |> List.truncate 8
                                                    |> List.map (fun row ->
                                                        TextBlock.create [
                                                            TextBlock.text (
                                                                row.Author + ": " + row.Body
                                                                + (if String.IsNullOrWhiteSpace row.Path then "" else "  (" + row.Path + (if row.Line = "" then "" else ":" + row.Line) + ")"))
                                                            TextBlock.foreground Theme.text
                                                            TextBlock.fontSize 11.
                                                            TextBlock.textWrapping TextWrapping.Wrap
                                                        ] :> IView)
                                                )
                                            ]
                                            StackPanel.create [
                                                StackPanel.spacing 4.
                                                StackPanel.children (
                                                    if state.Current.CommentRows.IsEmpty then
                                                        [ TextBlock.create [
                                                            TextBlock.text "No ::code-comment yet"
                                                            TextBlock.foreground Theme.muted
                                                            TextBlock.fontSize 11.
                                                          ] :> IView ]
                                                    else
                                                        state.Current.CommentRows
                                                        |> List.truncate 12
                                                        |> List.map (fun row ->
                                                            Button.create [
                                                                Button.content (row.File + (if row.Span = "" then "" else ":" + row.Span))
                                                                Button.horizontalAlignment HorizontalAlignment.Stretch
                                                                Button.onClick (fun _ -> insertMention row.File)
                                                            ] :> IView)
                                                )
                                            ]
                                            TextBlock.create [
                                                TextBlock.text "Prompts"
                                                TextBlock.fontWeight FontWeight.SemiBold
                                                TextBlock.foreground Theme.accent
                                                TextBlock.margin (Thickness(0, 12, 0, 0))
                                            ]
                                            StackPanel.create [
                                                StackPanel.spacing 2.
                                                StackPanel.children (
                                                    if state.Current.PromptRows.IsEmpty then
                                                        [ TextBlock.create [
                                                            TextBlock.text "No ~/.codexsharp/prompts"
                                                            TextBlock.foreground Theme.muted
                                                            TextBlock.fontSize 11.
                                                          ] :> IView ]
                                                    else
                                                        state.Current.PromptRows
                                                        |> List.map (fun row ->
                                                            Button.create [
                                                                Button.content row.Name
                                                                Button.horizontalAlignment HorizontalAlignment.Stretch
                                                                Button.onClick (fun _ ->
                                                                    async {
                                                                        try
                                                                            let! body = session.ReadPromptAsync(row.Name) |> Async.AwaitTask
                                                                            let text = jsStr body "contents"
                                                                            Dispatcher.UIThread.Post(fun () ->
                                                                                state.Set { state.Current with Composer = text })
                                                                        with _ -> ()
                                                                    }
                                                                    |> Async.Start)
                                                            ] :> IView)
                                                )
                                            ]
                                            StackPanel.create [
                                                StackPanel.spacing 4.
                                                StackPanel.children (
                                                    if String.IsNullOrWhiteSpace state.Current.PrTitle then
                                                        []
                                                    else
                                                        [
                                                            TextBlock.create [
                                                                TextBlock.text ("PR  " + state.Current.PrTitle)
                                                                TextBlock.fontWeight FontWeight.SemiBold
                                                                TextBlock.foreground Theme.accent
                                                                TextBlock.margin (Thickness(0, 12, 0, 0))
                                                            ] :> IView
                                                            Button.create [
                                                                Button.content "Insert gh pr create"
                                                                Button.onClick (fun _ ->
                                                                    let title = state.Current.PrTitle.Replace("\"", "'")
                                                                    let body = state.Current.PrBody.Replace("\"", "'")
                                                                    state.Set { state.Current with Composer = "gh pr create --title \"" + title + "\" --body \"" + body + "\"" ; Status = "inserts gh pr create; does not create a PR" })
                                                            ] :> IView
                                                            TextBlock.create [
                                                                TextBlock.text "Inserts a command only. Does not create a PR."
                                                                TextBlock.foreground Theme.muted
                                                                TextBlock.fontSize 11.
                                                            ] :> IView
                                                        ]
                                                )
                                            ]
                                            TextBlock.create [
                                                TextBlock.text "Terminal"
                                                TextBlock.fontWeight FontWeight.SemiBold
                                                TextBlock.foreground Theme.accent
                                                TextBlock.margin (Thickness(0, 12, 0, 0))
                                                TextBlock.isVisible state.Current.ShowTerminal
                                            ]
                                            TextBlock.create [
                                                TextBlock.isVisible state.Current.ShowTerminal
                                                TextBlock.text (if String.IsNullOrWhiteSpace state.Current.Terminal then "command/exec stream. Type a command and Run." else state.Current.Terminal)
                                                TextBlock.textWrapping TextWrapping.Wrap
                                                TextBlock.foreground Theme.text
                                                TextBlock.fontFamily (FontFamily "Cascadia Code, Consolas, monospace")
                                                TextBlock.fontSize 11.
                                                TextBlock.maxHeight 160.
                                            ]
                                            DockPanel.create [
                                                DockPanel.isVisible state.Current.ShowTerminal
                                                DockPanel.children [
                                                    Button.create [
                                                        Button.dock Dock.Right
                                                        Button.content "Stop"
                                                        Button.onClick (fun _ ->
                                                            let pid = state.Current.TermPid
                                                            if pid <> "" then
                                                                async {
                                                                    try
                                                                        do! session.TerminateExecAsync(pid) |> Async.AwaitTask |> Async.Ignore
                                                                    with _ ->
                                                                        ()
                                                                    Dispatcher.UIThread.Post(fun () ->
                                                                        state.Set { state.Current with TermPid = "" })
                                                                }
                                                                |> Async.Start)
                                                    ]
                                                    Button.create [
                                                        Button.dock Dock.Right
                                                        Button.content "Run"
                                                        Button.onClick (fun _ ->
                                                            let cmd = state.Current.TermIn.Trim()
                                                            if cmd <> "" then
                                                                let pid = "ui-term"
                                                                state.Set { state.Current with TermPid = pid; Terminal = "> " + cmd + "\n" }
                                                                async {
                                                                    try
                                                                        let! _ = session.StartStreamingExecAsync(pid, cmd) |> Async.AwaitTask
                                                                        Dispatcher.UIThread.Post(fun () ->
                                                                            state.Set { state.Current with TermPid = "" })
                                                                    with ex ->
                                                                        Dispatcher.UIThread.Post(fun () ->
                                                                            state.Set { state.Current with Terminal = state.Current.Terminal + ex.Message; TermPid = "" })
                                                                }
                                                                |> Async.Start)
                                                    ]
                                                    TextBox.create [
                                                        TextBox.placeHolderText "shell command"
                                                        TextBox.text state.Current.TermIn
                                                        TextBox.onTextChanged (fun v -> state.Set { state.Current with TermIn = v })
                                                        TextBox.onKeyDown (fun e ->
                                                            if handleDesktopChord e then ()
                                                            elif e.Key = Avalonia.Input.Key.Enter && state.Current.TermPid <> "" then
                                                                e.Handled <- true
                                                                let line = state.Current.TermIn + "\n"
                                                                let pid = state.Current.TermPid
                                                                state.Set { state.Current with TermIn = "" }
                                                                async {
                                                                    do! session.WriteExecAsync(pid, line, false) |> Async.AwaitTask |> Async.Ignore
                                                                }
                                                                |> Async.Start)
                                                    ]
                                                ]
                                            ]
                                            TextBlock.create [
                                                TextBlock.text "Memory"
                                                TextBlock.fontWeight FontWeight.SemiBold
                                                TextBlock.foreground Theme.accent
                                                TextBlock.margin (Thickness(0, 12, 0, 0))
                                            ]
                                            StackPanel.create [
                                                StackPanel.orientation Orientation.Horizontal
                                                StackPanel.spacing 4.
                                                StackPanel.children [
                                                    Button.create [
                                                        Button.content "on"
                                                        Button.onClick (fun _ ->
                                                            async {
                                                                try
                                                                    do! session.SetMemoryModeAsync("enabled") |> Async.AwaitTask |> Async.Ignore
                                                                    refreshChrome ()
                                                                with _ ->
                                                                    ()
                                                            }
                                                            |> Async.Start)
                                                    ]
                                                    Button.create [
                                                        Button.content "off"
                                                        Button.onClick (fun _ ->
                                                            async {
                                                                try
                                                                    do! session.SetMemoryModeAsync("disabled") |> Async.AwaitTask |> Async.Ignore
                                                                    refreshChrome ()
                                                                with _ ->
                                                                    ()
                                                            }
                                                            |> Async.Start)
                                                    ]
                                                    Button.create [
                                                        Button.content "reset"
                                                        Button.onClick (fun _ ->
                                                            async {
                                                                try
                                                                    do! session.ResetMemoryAsync() |> Async.AwaitTask |> Async.Ignore
                                                                    refreshChrome ()
                                                                with _ ->
                                                                    ()
                                                            }
                                                            |> Async.Start)
                                                    ]
                                                ]
                                            ]
                                            StackPanel.create [
                                                StackPanel.spacing 2.
                                                StackPanel.children (
                                                    if state.Current.MemoryNotes.IsEmpty then
                                                        [ TextBlock.create [
                                                            TextBlock.text "No memory notes"
                                                            TextBlock.foreground Theme.muted
                                                            TextBlock.fontSize 11.
                                                          ] :> IView ]
                                                    else
                                                        state.Current.MemoryNotes
                                                        |> List.truncate 12
                                                        |> List.map (fun name ->
                                                            Button.create [
                                                                Button.content name
                                                                Button.horizontalAlignment HorizontalAlignment.Stretch
                                                                Button.onClick (fun _ -> insertMention ("memories/" + name))
                                                            ] :> IView)
                                                )
                                            ]
                                            TextBlock.create [
                                                TextBlock.text "Plugins"
                                                TextBlock.fontWeight FontWeight.SemiBold
                                                TextBlock.foreground Theme.accent
                                                TextBlock.margin (Thickness(0, 12, 0, 0))
                                            ]
                                            Button.create [
                                                Button.content "Reconcile plugins"
                                                Button.onClick (fun _ ->
                                                    async {
                                                        try
                                                            do! session.ReconcilePluginsNowAsync() |> Async.AwaitTask |> Async.Ignore
                                                            refreshChrome ()
                                                        with _ ->
                                                            ()
                                                    }
                                                    |> Async.Start)
                                            ]
                                            StackPanel.create [
                                                StackPanel.spacing 2.
                                                StackPanel.children (
                                                    if state.Current.PluginAvailable.IsEmpty then
                                                        [ TextBlock.create [
                                                            TextBlock.text "No marketplace plugins to install"
                                                            TextBlock.foreground Theme.muted
                                                            TextBlock.fontSize 11.
                                                          ] :> IView ]
                                                    else
                                                        state.Current.PluginAvailable
                                                        |> List.map (fun name ->
                                                            Button.create [
                                                                Button.content ("Install " + name)
                                                                Button.horizontalAlignment HorizontalAlignment.Stretch
                                                                Button.onClick (fun _ ->
                                                                    async {
                                                                        try
                                                                            do! session.InstallPluginAsync(name, null) |> Async.AwaitTask |> Async.Ignore
                                                                            Dispatcher.UIThread.Post(fun () -> refreshChrome ())
                                                                        with _ ->
                                                                            ()
                                                                    }
                                                                    |> Async.Start)
                                                            ] :> IView)
                                                )
                                            ]
                                            TextBlock.create [
                                                TextBlock.text "Hooks"
                                                TextBlock.fontWeight FontWeight.SemiBold
                                                TextBlock.foreground Theme.accent
                                                TextBlock.margin (Thickness(0, 12, 0, 0))
                                            ]
                                            StackPanel.create [
                                                StackPanel.spacing 2.
                                                StackPanel.children (
                                                    if state.Current.HookRows.IsEmpty then
                                                        [ TextBlock.create [
                                                            TextBlock.text "No hooks"
                                                            TextBlock.foreground Theme.muted
                                                            TextBlock.fontSize 11.
                                                          ] :> IView ]
                                                    else
                                                        state.Current.HookRows
                                                        |> List.map (fun row ->
                                                            TextBlock.create [
                                                                TextBlock.text (row.Name + " @ " + row.Event)
                                                                TextBlock.foreground Theme.text
                                                                TextBlock.fontSize 11.
                                                            ] :> IView)
                                                )
                                            ]
                                            TextBlock.create [
                                                TextBlock.text "Workspace"
                                                TextBlock.fontWeight FontWeight.SemiBold
                                                TextBlock.foreground Theme.accent
                                                TextBlock.margin (Thickness(0, 12, 0, 0))
                                            ]
                                            StackPanel.create [
                                                StackPanel.spacing 2.
                                                StackPanel.children (
                                                    if state.Current.WorkspaceFiles.IsEmpty then
                                                        [ TextBlock.create [
                                                            TextBlock.text "No files"
                                                            TextBlock.foreground Theme.muted
                                                            TextBlock.fontSize 11.
                                                          ] :> IView ]
                                                    else
                                                        state.Current.WorkspaceFiles
                                                        |> List.map (fun name ->
                                                            Button.create [
                                                                Button.content name
                                                                Button.horizontalAlignment HorizontalAlignment.Stretch
                                                                Button.onClick (fun _ -> insertMention name)
                                                            ] :> IView)
                                                )
                                            ]
                                            TextBlock.create [
                                                TextBlock.text "Skills"
                                                TextBlock.fontWeight FontWeight.SemiBold
                                                TextBlock.foreground Theme.accent
                                                TextBlock.margin (Thickness(0, 12, 0, 0))
                                            ]
                                            StackPanel.create [
                                                StackPanel.spacing 2.
                                                StackPanel.children (
                                                    if state.Current.SkillRows.IsEmpty then
                                                        [ TextBlock.create [
                                                            TextBlock.text state.Current.Skills
                                                            TextBlock.foreground Theme.muted
                                                          ] :> IView ]
                                                    else
                                                        state.Current.SkillRows
                                                        |> List.map (fun row ->
                                                            DockPanel.create [
                                                                DockPanel.children [
                                                                    Button.create [
                                                                        Button.dock Dock.Right
                                                                        Button.content (if row.Enabled then "on" else "off")
                                                                        Button.onClick (fun _ ->
                                                                            async {
                                                                                try
                                                                                    do! session.WriteSkillEnabledAsync(row.Name, not row.Enabled) |> Async.AwaitTask |> Async.Ignore
                                                                                    refreshChrome ()
                                                                                with _ ->
                                                                                    ()
                                                                            }
                                                                            |> Async.Start)
                                                                    ]
                                                                    TextBlock.create [
                                                                        TextBlock.text row.Name
                                                                        TextBlock.foreground Theme.text
                                                                        TextBlock.verticalAlignment VerticalAlignment.Center
                                                                    ]
                                                                ]
                                                            ] :> IView)
                                                )
                                            ]
                                            TextBlock.create [
                                                TextBlock.text "MCP / Git"
                                                TextBlock.fontWeight FontWeight.SemiBold
                                                TextBlock.foreground Theme.accent
                                                TextBlock.margin (Thickness(0, 12, 0, 0))
                                            ]
                                            TextBlock.create [
                                                TextBlock.text state.Current.SideNotes
                                                TextBlock.textWrapping TextWrapping.Wrap
                                                TextBlock.foreground Theme.muted
                                                TextBlock.fontSize 11.
                                                TextBlock.fontFamily (FontFamily "Cascadia Code, Consolas, monospace")
                                            ]
                                        ]
                                    ]
                                )
                            ]
                        ]
                    ]
                ]
            ]
        )
