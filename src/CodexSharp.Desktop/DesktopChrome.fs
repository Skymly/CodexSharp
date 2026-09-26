namespace CodexSharp.Desktop

#nowarn "89"

open System
open System.IO
open System.Collections.Generic
open System.Text.Json
open Avalonia
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Input
open Avalonia.Threading
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Styling
open CodexSharp.Protocol
open CodexSharp.Runtime
open CodexSharp.Client
open CodexSharp.AppServer

module DesktopChrome =

    let applyWindowTheme () =
        match Application.Current with
        | null -> ()
        | app ->
            app.RequestedThemeVariant <- if UiTheme.IsLight() then ThemeVariant.Light else ThemeVariant.Dark

    let jsBool (el: JsonElement) (name: string) =
        let mutable v = Unchecked.defaultof<JsonElement>
        el.ValueKind = JsonValueKind.Object && el.TryGetProperty(name, &v) && v.ValueKind = JsonValueKind.True

    let diffViews (diff: string) : IView list =
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

    let jsStr (el: JsonElement) (name: string) =

        let mutable v = Unchecked.defaultof<JsonElement>
        if el.ValueKind = JsonValueKind.Object && el.TryGetProperty(name, &v) && v.ValueKind = JsonValueKind.String then
            v.GetString()
        else
            ""

    let toActivityRows () =
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

    let refreshChrome (session: AppServerSession) (state: IWritable<ScreenState>) (modelDraft: IWritable<string>) =

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

    let writeConfig (session: AppServerSession) (state: IWritable<ScreenState>) (modelDraft: IWritable<string>) (key: string) (value: string) =

        async {
            do! session.WriteConfigAsync(key, value) |> Async.AwaitTask |> Async.Ignore
            if key = "tui_theme" then
                Dispatcher.UIThread.Post(fun () -> applyWindowTheme ())
            refreshChrome session state modelDraft
        }
        |> Async.Start

    let toggleFeature (session: AppServerSession) (state: IWritable<ScreenState>) (modelDraft: IWritable<string>) (name: string) (enabled: bool) =

        if HonestStubs.IsLockedFeature name then
            ()
        else
            async {
                do! session.SetExperimentalFeatureAsync(name, not enabled) |> Async.AwaitTask |> Async.Ignore
                refreshChrome session state modelDraft
            }
            |> Async.Start

    let settingsPanel (state: IWritable<ScreenState>) (session: AppServerSession) (writeConfig: string -> string -> unit) (toggleFeature: string -> bool -> unit) (refreshChrome: unit -> unit) (resumeThread: string -> unit) =

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

    let activityPanel (state: IWritable<ScreenState>) (session: AppServerSession) (writeConfig: string -> string -> unit) (toggleFeature: string -> bool -> unit) (refreshChrome: unit -> unit) (resumeThread: string -> unit) =

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

    let scheduledPanel (state: IWritable<ScreenState>) (session: AppServerSession) (writeConfig: string -> string -> unit) (toggleFeature: string -> bool -> unit) (refreshChrome: unit -> unit) (resumeThread: string -> unit) =

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

    let rail (state: IWritable<ScreenState>) (session: AppServerSession) (refreshChrome: unit -> unit) (insertMention: string -> unit) (refreshPrComments: unit -> unit) (handleDesktopChord: Avalonia.Input.KeyEventArgs -> bool) =

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
                        StackPanel.create [
                            StackPanel.isVisible state.Current.ShowTerminal
                            StackPanel.orientation Orientation.Horizontal
                            StackPanel.spacing 4.
                            StackPanel.children (
                                (state.Current.TermTabs.All
                                 |> Seq.map (fun tab ->
                                     Button.create [
                                         Button.content (tab.Id.Replace("ui-term-", ""))
                                         Button.onClick (fun _ ->
                                             state.Set { state.Current with TermTabs = state.Current.TermTabs.Select(tab.Id) })
                                     ] :> IView)
                                 |> Seq.toList)
                                @ [ Button.create [
                                        Button.content "+"
                                        Button.onClick (fun _ ->
                                            state.Set { state.Current with TermTabs = state.Current.TermTabs.Open() })
                                    ] :> IView ]
                            )
                        ]
                        StackPanel.create [
                            StackPanel.isVisible state.Current.ShowTerminal
                            StackPanel.orientation Orientation.Horizontal
                            StackPanel.spacing 4.
                            StackPanel.children [
                                Button.create [
                                    Button.content (if state.Current.TermTabs.Active.Shell = "cmd" then "PS" else "PS*")
                                    Button.onClick (fun _ ->
                                        state.Set { state.Current with TermTabs = state.Current.TermTabs.SetShell("powershell") })
                                ]
                                Button.create [
                                    Button.content (if state.Current.TermTabs.Active.Shell = "cmd" then "cmd*" else "cmd")
                                    Button.onClick (fun _ ->
                                        state.Set { state.Current with TermTabs = state.Current.TermTabs.SetShell("cmd") })
                                ]
                            ]
                        ]
                        TextBlock.create [
                            TextBlock.isVisible state.Current.ShowTerminal
                            TextBlock.text (if String.IsNullOrWhiteSpace state.Current.TermTabs.Active.Buffer then "command/exec stream. Type a command and Run." else state.Current.TermTabs.Active.Buffer)
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
                                        let pid = state.Current.TermTabs.Active.Id
                                        if state.Current.TermTabs.Active.Running then
                                            async {
                                                try
                                                    do! session.TerminateExecAsync(pid) |> Async.AwaitTask |> Async.Ignore
                                                with _ ->
                                                    ()
                                                Dispatcher.UIThread.Post(fun () ->
                                                    state.Set { state.Current with TermTabs = state.Current.TermTabs.MarkExited(pid) })
                                            }
                                            |> Async.Start)
                                ]
                                Button.create [
                                    Button.dock Dock.Right
                                    Button.content "Run"
                                    Button.onClick (fun _ ->
                                        let cmd = state.Current.TermTabs.Active.Input.Trim()
                                        if cmd <> "" && not state.Current.TermTabs.Active.Running then
                                            let next = state.Current.TermTabs.PrepareRun(cmd)
                                            let pid = next.Active.Id
                                            state.Set { state.Current with TermTabs = next }
                                            async {
                                                try
                                                    let! _ = session.StartStreamingExecAsync(pid, next.Active.ExecArgv(cmd)) |> Async.AwaitTask
                                                    Dispatcher.UIThread.Post(fun () ->
                                                        state.Set { state.Current with TermTabs = state.Current.TermTabs.MarkExited(pid) })
                                                with ex ->
                                                    Dispatcher.UIThread.Post(fun () ->
                                                        state.Set { state.Current with TermTabs = state.Current.TermTabs.AppendDelta(pid, ex.Message).MarkExited(pid) })
                                            }
                                            |> Async.Start)
                                ]
                                TextBox.create [
                                    TextBox.placeHolderText "shell command"
                                    TextBox.text state.Current.TermTabs.Active.Input
                                    TextBox.onTextChanged (fun v -> state.Set { state.Current with TermTabs = state.Current.TermTabs.SetInput(v) })
                                    TextBox.onKeyDown (fun e ->
                                        if handleDesktopChord e then ()
                                        elif e.Key = Avalonia.Input.Key.Enter && state.Current.TermTabs.Active.Running then
                                            e.Handled <- true
                                            let line = state.Current.TermTabs.Active.Input + "\n"
                                            let pid = state.Current.TermTabs.Active.Id
                                            state.Set { state.Current with TermTabs = state.Current.TermTabs.SetInput("") }
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
