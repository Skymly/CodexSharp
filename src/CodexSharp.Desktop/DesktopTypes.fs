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
      TermTabs: DesktopTerminalTabs
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
