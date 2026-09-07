# Source map

Official Codex lives at `vendor/codex` (shallow clone of https://github.com/openai/codex).
This is an independent C#/F# recreation, not a line-for-line port.

| Official crate | CodexSharp |
|---|---|
| `codex-rs/protocol` | `src/CodexSharp.Protocol` (F#) |
| `codex-rs/core` | `src/CodexSharp.Core` (F#) + `src/CodexSharp.Runtime` (C#) |
| `codex-rs/apply-patch` | `src/CodexSharp.Runtime/ApplyPatchEngine.cs` |
| `codex-rs/app-server` + `app-server-protocol` | `src/CodexSharp.AppServer` |
| `codex-rs/app-server-client` | `src/CodexSharp.Client` |
| `codex-rs/cli` + `codex-rs/tui` | `src/CodexSharp.Cli` |
| Codex Desktop (installed by `cli/src/desktop_app`) | `src/CodexSharp.Desktop` Avalonia.FuncUI |

Harness primitives taken from `vendor/codex/codex-rs/protocol/src/protocol.rs` and `items.rs`:

- Submission Queue / Event Queue (`Op`, `EventMsg`)
- Prompt tags (`<environment_context>`, `<skills_instructions>`, …)
- `TurnItem` kinds: user / agent / command / file change / plan / reasoning
- apply-patch markers: Begin/End Patch, Add/Update/Delete File, Move to


Desktop now talks JSON-RPC to `InProcessAppServer` (same stdio protocol as `codexsharp app-server`), matching the official CLI/desktop split.
File name/content search ports the `codex-rs/file-search` idea as `file_search` / `grep_files`.

| `codex-rs/rmcp-client` | `src/CodexSharp.Runtime/Mcp.cs` stdio JSON-RPC |
| `codex-rs/core/compact.rs` | `src/CodexSharp.Core/Compact.fs` |
| `thread/fork` | `CodexSession.Fork` + app-server `thread/fork` |

| `codex-rs/core/tools/handlers/multi_agents` | `src/CodexSharp.Runtime/Subagents.cs` (`spawn_agent` / `wait_agent` / `send_input` / `close_agent`) |
| `codex-rs/windows-sandbox-rs` | `JobObject` kill-on-close around shell processes |

| MCP Streamable HTTP | `McpHttpClient` (`url` + `bearer_token_env_var`) |
| `codex-rs/skills` | `SkillCatalog` + `load_skill` |
| `codex-rs/login` auth.json | `AuthService` / `codexsharp login` |

| `account/read` `account/login/start` `getAuthStatus` | App Server account APIs (apiKey login) |
| `view_image` | `codex-rs/core/tools/handlers/view_image` |
| `command/exec` | one-off shell via app-server |

| `codex-rs/execpolicy` | `ExecPolicy` prefix_rule files under ~/.codexsharp/rules |
| `thread/goal/*` | CodexSession goal + app-server |
| `fuzzyFileSearch` | FileSearch over app-server |

| `config.toml` profiles | `ConfigService.Load(..., profile)` |
| `mcpServerStatus/list` | App Server MCP registry listing |
| Plan sidebar | FuncUI panel from `update_plan` items |

| `codex-rs/git-utils` | `GitProbe` + `<git_branch>` in environment_context |
| `thread/compact/start` | `CodexSession.TryCompact` |
| `thread/inject_items` | `CodexSession.InjectItems` + app-server |
| `thread/realtime/*` | App Server experimental stubs |
| `experimentalFeature/enablement/set` | `FeatureFlags.Set` |
| `codex doctor` / `mcp` / `skills` | `DoctorProbe` + `codexsharp doctor [--json]` `mcp` `skills` `plugins` |
| `thread/shellCommand` `config/write` | App Server |

| `codex-rs/features` | `FeatureFlags` + `codexsharp features` |
| `review/start` | App Server review turn |

| `clock/curr_time` | `current_time` tool |
| thread pin | `ThreadPins` + `thread/metadata/update` |
| turn interrupt UI | FuncUI Stop button |

| `clock/sleep` | `sleep` tool |
| `model/list` | `ModelCatalog` |

| `experimentalFeature/list` `permissionProfile/list` | App Server |
| FuncUI model box | `config/write` via WriteUserKeys |

| hosted/function `web_search` | Builtin + Responses `type=web_search` when feature on |
| `thread/unsubscribe` | Unload live thread |
| FuncUI sandbox buttons | `sandbox_mode` write |

| `gitDiffToRemote` | `GitProbe.DiffToRemote` |
| `getConversationSummary` | thread title/cwd/model |
| FuncUI approval buttons | ask/never/strict |

| `notify = [...]` | `TurnNotify` on turn complete |
| `skills/list` | SkillCatalog over app-server |
| FuncUI dirty git | `GitInfo.Dirty` asterisk |

| `request_user_input` | Agent approval pause + tool |
| `fs/watch` `fs/unwatch` | FileSystemWatcher + `fs/changed` |
| FuncUI skills list | SkillCatalog in plan rail |

| `personality` | Config developer prompt overlay |
| `collaborationMode/list` | default/plan/pair |
| `thread/name/set` | CodexSession.SetName |

| `plugin/list` | PluginCatalog under ~/.codexsharp/plugins |
| reasoning effort UI | `model_reasoning_effort` + `/effort` |
| FuncUI thread rename | `thread/name/set` on Enter |

| `app-server --listen ws://IP:PORT` | `WebSocketAppServer` TCP + `/readyz` `/healthz` Origin 403 |
| `windowsSandbox/readiness` | App Server reports `notConfigured` (no Windows sandbox service yet) |

| `fs/readFile` `fs/writeFile` `fs/readDirectory` | App Server host filesystem APIs |
| Desktop spawn `codexsharp app-server` | `AppServerHandle` (falls back to in-process) |

| JSONL `history_snapshot` | `JsonlThreadStore.SaveHistory` / Resume reload |

| `thread/turns/list` `thread/rollback` `thread/revert` | CodexSession turn slices |
| `collaboration_mode` | developer prompt overlay (plan/pair) |

| `thread/search` | JsonlThreadStore.Search titles+history |
| `thread/queue/*` | in-memory queue, drain after turn |

| `file_change` items | apply_patch emits FileChange; FuncUI Diff rail |
| `environment/info` | App Server host env |

| `thread/settings/update` | CodexSession.ApplySettings |
| FuncUI composer `/` | /new /compact /fork /archive /rollback /name /effort /help |

| `hooks/list` | `HookCatalog` under ~/.codexsharp/hooks |
| FuncUI Settings | personality + hook list overlay |

| SessionStart / Stop / UserPromptSubmit hooks | `HookRunner` executes ~/.codexsharp/hooks |

| PreToolUse / PermissionRequest / PostToolUse | `IHookHost` in AgentLoop |

| PreCompact / PostCompact / Interrupt | compact + cancelled turns |

| SubagentStart / SubagentStop | `SubagentExecutor` spawn lifecycle |

| `skills/extraRoots/set` | SkillExtraRoots json under ~/.codexsharp |
| FuncUI `@file` | composer mention via FileSearch |

| `mcpServer/tool/call` | one-shot McpStdioClient |
| FuncUI Attach | local_image input items -> view_image prompt |

| local_image -> multimodal | `ImageMessageContent` data URL in chat/responses |

| FuncUI Ctrl+V image | ClipboardImageStore temp PNG attachments |

| `threadSection/*` `thread/section/move` | `ThreadSections` + app-server |
| `plugin/install` `plugin/read` `plugin/uninstall` | `PluginCatalog` |
| `marketplace/add|remove|upgrade` | `MarketplaceStore` |

| `command/exec` PTY-style sessions | `CommandExecBroker` + write/terminate/resize |
| `skills/config/write` | `SkillConfig` |

| `plugin/reconcile` `plugin/search` `plugin/skill/read` | local PluginCatalog snapshot + search |
| `remoteControl/pairing/*` `remoteControl/client/*` | honest stubs: codes never claimed, no clients |
| `thread/queue/update` `thread/queue/reorder` | in-memory queue |
| FuncUI remote-control chrome | `LoadChromeAsync` + Enable/Disable RC |
| FuncUI queue list | delete queued submissions from sidebar |

| `project/create` `project/read` `project/import` `project/move` `project/delete` | `ProjectStore` JSON under ~/.codexsharp/projects.json |
| FuncUI project picker | `project/list` + `thread/list.projectId` |
| `account/usage/read` `modelProvider/capabilities/read` | honest empty usage + alias |
| `mcpServer/oauth/login` | honest not-configured stub |

| ChatGPT device code `account/login/start` | `ChatgptDeviceAuth` using official Codex `CLIENT_ID` |

| ChatGPT browser OAuth `account/login/start` type=chatgpt | localhost 1455/1457 PKCE callback |

| `windowsSandbox/setupStart` unelevated | Job Object setup state in ~/.codexsharp/windows-sandbox.json |

| `process/spawn` family | CommandExecBroker skipSandbox + process/exited |
| `thread/memoryMode/set` `memory/reset` | MemoryStore JSON + ~/.codexsharp/memories |

| `fuzzyFileSearch/session*` | incremental FileSearch + sessionUpdated |
| `turn/settings/update` | applied vs targetUnavailable |
| TUI `/clear` `/stop` `/pwd` | Spectre session controls |

| `environment/add` `environment/status` | local ready; remote pending without exec-server |
| `server/diagnostics` | process RSS + gauges |
| `thread/backgroundTerminals/*` | experimental list/clean/terminate |

| TUI `/plugins` `/plan` `/cd` `/init` `/logout` `/copy` | official slash set |

| TUI `/worktree` + `git worktree` | GitProbe.ListWorktrees/AddWorktree |

| `externalAgentConfig/detect|import` | CLAUDE.md/skills -> AGENTS.md |

| `currentTime/read` `thread/*_elicitation` `mcpServer/event/stream/*` | experimental clocks, pause counters, stream ids |

| `account/chatgptAuthTokens/refresh` `item/tool/call` | refresh_token grant + BuiltinToolExecutor |

| `account/bedrock/discover|setup` | BedrockDiscover (~/.aws + env, no secrets); setup/login honest error |
| `account/rateLimitResetCredit/consume` | outcome=noCredit without ChatGPT credits |
| `item/commandExecution/requestApproval` | server→client JSON-RPC + item/permissions/respond compat |
| realtime start | experimentalApi then WebRTC notImplemented (no fake session) |
| TUI `/memory` `/resume` `/agents` | MemoryStore, JsonlThreadStore, AGENTS.md |
| FuncUI Memory/Plugins | memoryMode/reset + plugin/reconcile |

| memories injection | Prompt.loadMemories from ~/.codexsharp/memories; thread/memoryMode/set disables |
| `item/tool/requestUserInput` | AgentEvent.UserInputNeeded + JSON-RPC / item/tool/respondUserInput |
| `/sandbox-add-read-dir` | SandboxRoots extra read paths (writes still workspace-only) |
| TUI `/export` `/recap` `/mention` `/app` `/debug-config` `/ps` `/apps` `/rename` | official slash set |

| `thread/tokenUsage/updated` | local char/4 estimate, not provider usage |
| `item/plan/delta` `turn/plan/updated` `turn/diff/updated` | from update_plan / apply_patch items |
| `configRequirements/read` | requirements=null without MDM; computer/browser use not allowed |
| TUI `/side` `/btw` `/ide` `/rollout` `/raw` `/theme` `/vim` `/approve` | official slash set; vim/theme persist, no ratatui |

| MCP `elicitation/create` | McpStdioClient reverse RPC -> mcpServer/elicitation/request |
| `~/.codexsharp/prompts/*.md` | PromptCatalog + TUI `/prompts` |
| workspace listing | Desktop chrome fs/readDirectory |
| `/subagents` `/permissions` | SubagentExecutor.Snapshot; approvals alias |

| skills-config.json | Prompt.loadSkills skips disabled; Desktop on/off; `/skills NAME on|off` |
| workspace file buttons | Desktop fs/readDirectory click inserts @file |
| config/read computerUse/browserUse | notConfigured |

| `item/commandExecution/outputDelta` | ShellExecutor streams stdout/stderr chunks during tool shell |

| collaboration_mode plan/default/pair | CollaborationModes overlay; mutating tools denied in plan |
| `item/fileChange/outputDelta` | ApplyPatchEngine per-file callback |
| Desktop slash popup | composer `/` prefix matches command list |

| `review/start` targets | uncommittedChanges / baseBranch / commit / custom via ReviewPrompts + git |
| `<proposed_plan>` | Desktop Plan rail + Proposed plan card title |

| `thread/compacted` | AgentLoop/TryCompact emit Compacted dropped count |
| `windows/worldWritableWarning` | WorldWritableScan via icacls Everyone/(W|M|F) on cwd |

| personality overlays | PersonalityModes friendly/pragmatic/professional tags |
| `/statusline` `/title` | TuiStatus from config keys; Console.Title + Desktop window title |
| `thread/status/changed` | active on turn/started, idle on turn/completed |
| feedback includeLogs | redacted config + recent session jsonl snapshots (no upload) |

| waitingOnApproval / waitingOnUserInput | thread/status/changed activeFlags |
| `/execpolicy` | ExecPolicy.AddUserRule -> ~/.codexsharp/rules/user.rules |
| Desktop queue ^/v | thread/queue/reorder |

| `thread/queue/changed` | Desktop QueueChanged live refresh |
| ModelCatalog.Add | ~/.codexsharp/models.json overlay; `/model add ID` |
| Desktop Hooks rail | hooks/list name @ event |

| `get_context_remaining` `tool_search` `wait_for_environment` | BuiltinToolExecutor local estimates / catalog / EnvironmentStore |
| `list_available_plugins_to_install` `request_plugin_install` | PluginCatalog.ListAvailable + Install from local marketplaces |
| `list_mcp_resources` `read_mcp_resource` | MCP resources/list + resources/read; config-only without a live session |
| TUI slash popup | Spectre SelectionPrompt for `/` prefixes (not ratatui) |
| FuncUI marketplace install | plugin/list.available + plugin/install |

| `exec_command` `write_stdin` (unified_exec) | CommandExecBroker.SpawnYieldAsync; feature-gated; session-scoped PTY |
| TUI history cells + working/idle | Spectre Panel tool cells; `▸ working` / `▸ idle` (not ratatui) |
| FuncUI status indicator | header `▸ working` while Busy |

| ollama / lmstudio local models | `LocalModelDiscover` probes `/api/tags` and `/v1/models` (400ms, cached) |
| `request_permissions` | extra readonly roots via SandboxRoots; network not auto-enabled |
| `list_mcp_resource_templates` | MCP `resources/templates/list` |
| TUI approval overlay | allow / deny / abort SelectionPrompt |
| TUI `/history` + Desktop Up/Down | composer prompt history |

| `new_context_window` | Compact.resetWithoutSummary — drop history without a summary |
| Desktop markdown + diff colors | Markdown.Parse headings/fences/lists; + green / - red |

| `::codex-file-citation` + markdown file links | AssistantDirectives + FileCitations; Desktop buttons insert @path |

| `::code-comment` `::git-create-pr` | ReviewPrompts extractors; Desktop review rail + gh pr create insert (does not push) |
| `prompts/list` `prompts/read` | PromptCatalog over app-server; Desktop prompt buttons |

| `codex apply` `review` `resume --last` `fork` `archive` | `SessionCli` + JsonlThreadStore.FindLastPatch |
| `codex cloud` | honest notConfigured |
| `codex exec --json` | JSONL event dump |

| `codex queue` `agents` `completion` `remote-control` | ThreadQueueStore persisted queues; agents lists threads; completion scripts; RC honest errored |

| `codex debug models|prompt-input|clear-memories` | DebugCli + PromptDebug.BuildSnapshot |
| `exec --output-last-message` | write last agent_message to a file |
| `memory/list` | MemoryStore notes; Desktop memory rail |

| `codex exec --cd --image --output-schema --skip-git-repo-check --ephemeral` | ExecPrompt.Compose + GitProbe.FindRoot + session delete |

| `codex exec resume|fork|review` | Exec one-shot against JsonlThreadStore |
| Interactive `--cd --model --image` | SharedCli.Parse + Tui.RunAsync(config) |

| `codex --worktree` | WorktreeSession under ~/.codexsharp/worktrees (git worktree -b) |

| `--ignore-user-config` `--ignore-rules` | ConfigService.Load(ignoreUserConfig) + ExecPolicy.SuppressRules |
| Desktop Worktree button | WorktreeSession.Create |

| `--add-dir` `--strict-config` | SessionSandbox extra writable roots; unknown config.toml keys fail when strict |

| `codex execpolicy check` | ExecPolicy.Evaluate + `codexsharp execpolicy check` |
| `codex update` | honest notConfigured (independent .NET build) |
| `codex exec-server` | ExecServerHost local JSON-RPC: process/start|write|read|terminate, fs/*, environment/info. No remote/Noise |
| `codex migrate-rollouts` | RolloutMigrate inspect/rewrite JSONL; no sqlite thread-history db |
| TUI `/keymap` `/pets` `/setup-default-sandbox` `/memories` | TuiKeymap + TuiPets + WindowsSandbox unelevated |
| Desktop slash parity | FuncUI composer: status/skills/hooks/mcp/diff/worktree/memory/model/sandbox/plan/stop/keymap/pets/... |

| `codex mcp list|get|add|remove` | McpConfig writes `[mcp_servers.*]` in config.toml; login/logout honest notConfigured |
| Spectre live composer | LiveTui full-screen history + bottom pane (not ratatui) |
| `codex skills enable|disable` | SkillConfig.Set |

| TUI via App Server | TuiAgent + InProcessAppServer (official tui/app_server_session split) |
| `codex plugin add PLUGIN@MARKET` `--available` `--json` | PluginCatalog.Install + MarketplaceStore name resolve |
| `codex login --with-api-key` / `login status` | stdin API key; status via Account() |

| TUI `/agents` session switch | JsonlThreadStore list + TuiAgent.Resume (AGENTS.md remains `/init`) |
| Desktop MCP settings | McpConfig add/remove in FuncUI settings; `/resume` `/agents` slash |
| `login --device-auth` | alias for `--device` |

| Desktop Light/Dark | UiTheme + Avalonia ThemeVariant from tui_theme |
| `debug app-server send-message-v2` | InProcessAppServer turn; prints event names |
| TUI Ctrl+C twice to quit | LiveTui footer hint, 2s window |
| `features --json` | FeatureFlags.List JSON |

| TUI/Desktop notifications | DesktopNotify OSC 9 + BEL (`tui_notifications`); FuncUI toast on turn complete |

| Desktop composer keymap | Enter send, Shift+Enter newline, TuiKeymap interrupt/new (ctrl+c/ctrl+n) |

| `codex exec --json` | ExecJsonl thread.started / turn.* / item.* snake_case JSONL |

| Desktop status strip | idle/working + chrome.Status + keymap hint above composer |
| Alt+Enter queue | TuiKeymap composer.queue |

| TUI `/queue` | list/add/start via thread/queue/*; drain on turn.completed |
| Desktop auto-queue | QueueStart after TurnCompleted |

| Desktop `/export` + Export button | writes cwd/codexsharp-export-<thread>.md |

| Desktop `/copy` + Copy button | last agent_message to clipboard (clip.exe) |

| Desktop `/init` | AgentsMarkdown.WriteIfMissing at git root |

| Desktop `/delete` + Delete button | thread/delete then new thread |
| `/clear` starts a new chat | TUI Restart + Desktop StartThreadAsync |

| Terminal title OSC sanitization | TerminalTitle.Sanitize; Desktop window title + TUI Console.Title |
| Desktop `/title` | writes tui_title like TUI |

| `/usage` | account/usage/read local token estimate; billed ChatGPT usage stays null |

| Desktop `/doctor` + Doctor button | DoctorProbe.Run in-app |
| Desktop `/debug-config` | config/read snapshot |

| TUI/Desktop `/ps` `/apps` | thread/backgroundTerminals/list + app/list (empty hosted apps) |
| TUI `/stop` | interrupt turn + clean background terminals |

| `exec --thread-source` | ThreadSources persist per thread id |
| Desktop `/rollout` | sessions JSONL path + clipboard |

| Desktop `/mention QUERY` | FileSearch.SuggestNames; unique hit inserts @path |

| Desktop `/sandbox-add-read-dir` | SandboxRoots.Add + settings extra read roots |

| TUI/Desktop Ctrl+G | `ExternalEditor` VISUAL/EDITOR draft under ~/.codexsharp/editor; no notepad fallback |
| TUI `/pin` | `thread/metadata/update` via TuiAgent.TogglePin |
| Desktop slash parity | `/pin` `/search` `/goal` `/feedback` `/approve` `/import` `/raw` `/subagents` `/execpolicy` `/history` `/rename` `/approvals` |
| `thread/read` subagents | ThreadDto lists spawn_agent snapshots; TuiAgent.ListSubagents via InProcessAppServer |

| TUI composer editor | `ComposerBuffer` cursor/yank; shift+enter newline; alt+enter queue; ctrl+w/u/k/y; not ratatui |
| TUI Ctrl+V paste | `ClipboardPaste` image PNG via WinForms clipboard or text; attaches local_image on next turn |
| `codexsharp sandbox windows -- CMD` | Job Object + workspace-write `SandboxCommand`; macos/linux stay notConfigured |

| `turn/steer` | Injects into the active AgentLoop; errors `no active turn to steer` instead of starting a parallel turn |
| Desktop/TUI busy Enter | steers the live turn; Alt+Enter still queues |

| `codex resume --all --include-non-interactive` | `ResumePicker` cwd filter by default; exec threads hidden unless included |
| Desktop composer ctrl+w/u/k/y | FuncUI TextBox applies `ComposerBuffer` at CaretIndex |

| TUI `/resume` `/agents` | ResumePicker cwd/exec filter; `--all` lists every folder |
| Desktop thread list | default this folder, hide exec; **All folders** toggle |
| `codexsharp fork --all` | same picker rules as resume |

| TUI `/vim` | `ComposerVim` insert/normal: i/a/I/A/o hjkl 0$ w/b x dd dw p; not ratatui, not full vim |

| Desktop `/vim` | FuncUI composer uses `ComposerVim` when tui_vim=true; status strip shows insert/normal |

| `/plan` `/default` `/pair` | Writes collaboration_mode and updates live session developer overlay; Desktop mode buttons |

| `/personality NAME` | Live developer overlay via config/write; Desktop friendly/pragmatic/professional |

| config/write model/sandbox/approval/effort | ApplyLiveConfig updates loaded threads; `/effort` includes xhigh/ultra |

| ComposerVim j/k | `ComposerBuffer.MoveUp/MoveDown` keep column across wrapped composer lines |

| ComposerVim G/gg/O | buffer end/start; open line above into insert |
| Desktop All folders | thread subtitle includes cwd |

| TUI/Desktop `/apply` | Last `apply_patch` in the thread via ApplyPatchEngine at thread cwd; cloud task apply stays notConfigured |

| `/copy code` | Last fenced block via `Markdown.LastCode`; Desktop **Code** button |
| `LastPatchApply` | Shared local apply at thread cwd for CLI/TUI/Desktop |

| TUI/Desktop `/diff` | `GitProbe.WorkingTreeDiff` tracked + untracked (`git diff --no-index`); `git diff` exit 1 is success |

| ComposerVim `u` | `ComposerBuffer` undo stack (80) for insert/kill; Desktop apply refreshes Diff chrome |

| ComposerVim ctrl+r | `ComposerBuffer.Redo` after `u`; new edits clear the redo stack |

| Desktop composer vim | Persistent `ComposerVim` in the FuncUI view; `Adopt` keeps undo across keypresses (`u` / ctrl+r / `dd` / `gg`) |

| ComposerVim visual `.` | `v`/`V` visual, `d/c/y` operators, `p/P`, `f/t/r`, `.` replays last operator; not ratatui, not full vim |

| TUI/Desktop `/agents` | `AgentsOverview` this-folder sessions + subagents; `--all` every folder; TUI picker when interactive |

| `codexsharp exec` | In-process App Server `thread/start` + `turn/start` (official exec uses InProcessAppServerClient); `--thread-source` via `thread/start.source`; resume/fork one-shots too |
| CLI archive/unarchive/delete/fork/queue/agents | In-process App Server JSON-RPC (same daemon split as official `codex agents` / session cmds) |
| CLI doctor/features/account/models/logout | App Server `diagnostics/doctor`, `experimentalFeature/*`, `account/read`, `model/list`, `account/logout` |
| CLI mcp/skills/plugin/marketplace/project | App Server `mcpServer/*`, `skills/*`, `plugin/*`, `marketplace/*`, `project/*` |
| TUI/Desktop `/doctor` | `diagnostics/doctor` over App Server |
| TUI `/experimental` | `experimentalFeature/list` over App Server |
| config.toml IO | `CodexPaths.ReadConfigText/WriteConfigText` path-keyed lock + retry (xunit parallel config.toml) |
| CLI/TUI/Desktop execpolicy | App Server `execPolicy/check|list|addUserRule` |
| TUI `/plugins` | `plugin/list` over App Server |
| TUI `/prompts` `/skills` `/hooks` `/mcp` | App Server `prompts/*`, `skills/*`, `hooks/list`, `mcpServerStatus/list` |
| TUI `/memory` `/diff` `/init` `/logout` `/import` `/delete` | App Server memory/*, gitDiffToRemote, agentsMarkdown/write, account/logout, externalAgentConfig/*, thread/delete |
| TUI `/archive` `/search` `/feedback` `/apply` `/worktree` | App Server thread/archive, thread/search, feedback/upload, lastPatch/apply, gitDiffToRemote + git/worktree/add |
| TUI `/sandbox-add-read-dir` `/debug-config` tui_* | `sandbox/extraReadRoot/*` + `config/read|write` |
| CLI `remote-control` | `remoteControl/status/read|enable|disable` (transport still absent; status is honest) |

| Onboarding trust-directory | `ProjectTrust` writes `projects."<path>".trust_level`; TUI Yes/No, Desktop Yes/Skip; untrusted cwd skips project-local skills, hooks, execpolicy, and `.codexsharp/config.toml` |
| `project/trust/read` `project/trust/set` | App Server; Desktop chrome `NeedsTrust` |

| `thread/worktree/start` | Managed worktree + new thread; cloud worktree stays `notConfigured` |
| TUI `/mention` `/model` `/approvals` `/sandbox` `/personality` `/review` | App Server `fuzzyFileSearch` `model/list` `config/write` `config/read` `review/start` |
| ComposerVim `.` insert replay | Last insert body between `i/a/I/A/o/O` and Esc |
| `sandbox/extraReadRoot/add` | Persists + live `WorkspaceSandbox.EnsureReadable` |

| `thread/export` `thread/rollout/path` | TUI/Desktop `/export` `/rollout` over App Server |
| `list_agents` `resume_agent` `interrupt_agent` | Subagent v2-shaped tools; not hosted multi-agent |
| `<extra_readonly_root>` | environment_context from sandbox-read-roots.json |

| `thread/recap/start` | Ephemeral recap turn; does not persist the recap prompt into the thread |
| `thread/copy` | Last assistant message or fenced code for TUI/Desktop `/copy` |
| Feature flags `guardian` `computer_use` `browser_use` `realtime` `code_mode_host` | Honest underDevelopment / notConfigured |

| `memory/read` `memory/write` | File-backed notes under ~/.codexsharp/memories; path traversal rejected |
| `tui/keymap/*` `tui/pets/*` | TUI/Desktop `/keymap` `/pets` over App Server |
| `turn/diff/updated` | Working-tree diff after `turn/completed` |
| `windowsSandbox/setupStart` | Returns snapshot; elevated stays notConfigured |

| Desktop chrome vim/collab/personality/effort | From `config/read` via App Server; no ConfigService.Peek in FuncUI |
