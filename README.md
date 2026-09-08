# CodexSharp

C# + F# + .NET 10 recreation of the Codex **harness**: a local coding agent with an interactive CLI, a JSON-RPC app server, and a desktop app.

This is an independent implementation inspired by the public Codex architecture (agent loop, threads/turns/items, app-server JSON-RPC, sandbox + approvals). It is **not** the official OpenAI Codex CLI or Desktop app.

Living inventory of official crates vs this repo: `docs/SOURCE_MAP.md`.
Desktop capability baseline vs CodexSharp (ChatGPT 26.901): `docs/DESKTOP_REQUIREMENTS.md`.

## Upstream source

Official Codex is cloned to `vendor/codex` (https://github.com/openai/codex). The clone is gitignored reference material, not a build dependency.

## Layout

| Project | Language | Role |
|---|---|---|
| `src/CodexSharp.Protocol` | F# | Threads, turns, items, tool/model contracts |
| `src/CodexSharp.Core` | F# | Prompt assembly and the agent loop |
| `src/CodexSharp.Runtime` | C# | Config, persistence, sandbox, tools, HTTP model client |
| `src/CodexSharp.AppServer` | C# | Bidirectional JSON-RPC host (stdio / ws / unix / in-process) |
| `src/CodexSharp.Client` | C# | App-server client |
| `src/CodexSharp.Cli` | C# | `codexsharp` TUI + `exec` + `app-server` |
| `src/CodexSharp.Desktop` | F# / Avalonia.FuncUI | Desktop app (JSON-RPC client) |

Hub-and-spoke, same as Codex: every UI talks to one core loop.

```
CLI TUI ─┐
Desktop ─┼─► App Server / Runtime ─► Core agent loop ─► Responses / Chat Completions
AppServer┘                    │
                              ├─ shell, files, apply_patch, MCP, skills, subagents
                              └─ JSONL thread store under ~/.codexsharp
```

## Build

NUKE bootstraps the SDK from `global.json` and runs the same targets locally and in CI:

```bash
./build.ps1 Test      # Windows
./build.sh Test       # Linux/macOS
./build.cmd Compile
```

Default target is `Test` (`Restore` → `Compile` → `Test`). GitHub Actions workflow `.github/workflows/ci.yml` invokes the same entry point.

## Requirements

- .NET SDK 10
- An OpenAI-compatible API key, or a local server such as LM Studio

## Run

```bash
dotnet test
dotnet run --project src/CodexSharp.Cli
dotnet run --project src/CodexSharp.Cli -- exec "Summarize this repo"
dotnet run --project src/CodexSharp.Cli -- app-server
dotnet run --project src/CodexSharp.Desktop
```

## Desktop shortcuts (Windows)

Published defaults for NAV-05 / COMP-06. These are not rebindable yet. Ctrl+K opens the command palette; it does **not** clear the terminal (that would be Ctrl+L).

| Keys | Action |
|---|---|
| Ctrl+K or Ctrl+Shift+P | Command palette |
| Ctrl+N | New thread |
| Ctrl+, | Settings |
| Ctrl+` | Toggle terminal |
| Ctrl+B | Toggle sidebar |

Palette actions: new thread, open settings, jump to an existing thread, focus composer.

Deep links use `codexsharp://` (not `codex://`; CodexSharp does not steal the official protocol):

- `codexsharp://threads/new`
- `codexsharp://threads/<id>`
- `codexsharp://settings`
- `codexsharp://skills`

Unknown paths open the existing main window and are ignored. Query `path` is accepted only when it is an existing local directory; it is never executed.

## Config

First launch writes `%USERPROFILE%\.codexsharp\config.toml` (override with `CODEXSHARP_HOME`).

```toml
model = "gpt-4.1-mini"
model_provider = "openai"
approval_policy = "on-request"
sandbox_mode = "workspace-write"

[model_providers.lmstudio]
name = "LM Studio"
base_url = "http://127.0.0.1:1234/v1"
env_key = "CODEXSHARP_API_KEY"
wire_api = "chat"
```

Credentials, first match:

1. `CODEXSHARP_API_KEY`
2. Provider `env_key` (default `OPENAI_API_KEY`)
3. `MINIMAX` / `MiniMax`
4. `~/.codexsharp/auth.json` `{"api_key":"..."}`

`CODEXSHARP_PROVIDER`, `CODEXSHARP_MODEL`, and `CODEXSHARP_BASE_URL` override the selected provider.

Set `wire_api = "responses"` for the OpenAI Responses API, or `chat` for Chat Completions (LM Studio, MiniMax, most local servers).

Feature flags live under `[features]` (`codexsharp features list|enable|disable`). Defaults are off.

## CLI

```
codexsharp                 interactive TUI
codexsharp "prompt"        opening turn, then TUI
codexsharp exec "prompt"   one shot
codexsharp app             desktop
codexsharp app-server [--listen URL]   JSON-RPC (stdio://, ws://IP:PORT, unix://, off)
codexsharp resume [id]
codexsharp login|logout|account
codexsharp doctor          local install / config diagnosis
codexsharp execpolicy check COMMAND
codexsharp exec-server     local process/fs JSON-RPC (stdio)
codexsharp mcp             list | get | add | remove
codexsharp skills          list | enable | disable
codexsharp plugin          list / add / remove local plugins
codexsharp marketplace     list / add / remove local marketplaces
codexsharp project         list | create | delete
codexsharp features        list | enable | disable
codexsharp migrate-rollouts
codexsharp update          independent build; no auto-updater
```

`exec` flags: `--model`, `--sandbox`, `--full-auto`, `--yolo`, `--json`, `--cd`, `--image`, `--worktree`, `--add-dir`.

TUI slash commands follow the official set (`/help`, `/status`, `/new`, `/compact`, `/fork`, `/model`, `/sandbox`, ...). Many of them now go through the App Server rather than touching config files directly.

## App Server

JSONL JSON-RPC, Codex-shaped methods. Transport: stdio, websocket, unix socket, or in-process (Desktop and `exec`).

Core thread/turn surface:

- `initialize` / `initialized`
- `thread/start` `thread/resume` `thread/list` `thread/archive` `thread/fork`
- `turn/start` `turn/interrupt` `turn/steer`
- notifications: `thread/started` `turn/started` `item/started` `item/agentMessage/delta` `item/completed` `turn/completed`
- server request: `item/permissions/requestApproval`

The host also exposes account, config, MCP, skills, plugins, projects, fs, git, memory, and sandbox methods. Missing cloud/OS capabilities return honest `notConfigured` / `underDevelopment` rather than fake success. See `docs/SOURCE_MAP.md` for the method map.

## What works locally

- Agent loop: prompt -> stream -> tools -> repeat -> assistant message
- Built-in tools including `shell`, `read_file`, `write_file`, `apply_patch`, `list_dir`, `update_plan`, `grep_files`, `file_search`, plus feature-gated `web_search`, `exec_command`, subagents, MCP resources, and `request_permissions`
- MCP stdio and Streamable HTTP from `~/.codexsharp/config.toml`; tools appear as `mcp__<server>__<tool>`
- `AGENTS.md` walk from git root to cwd; skill metadata under `skills/**/SKILL.md`
- Local plugins and marketplaces under `~/.codexsharp`
- Approval policies: `untrusted`, `on-request`, `never`
- Sandbox modes: `read-only`, `workspace-write`, `danger-full-access` (path policy; Windows Job Object kill-on-close, no OS seatbelt)
- Thread persistence as JSONL; resume / fork / archive / rollback / compact
- In-process subagents (`spawn_agent` / `wait_agent` / ...)
- Local history compaction (drop old messages past a char threshold; not a model-written summary)
- ChatGPT device-code and localhost PKCE login (tokens stored locally)
- Spectre.Console TUI and Avalonia.FuncUI desktop (Desktop is an App Server JSON-RPC client)

MCP stdio example:

```toml
[mcp_servers.docs]
command = "npx"
args = ["-y", "mcp-server"]
```

## Honest stubs

These APIs exist so clients do not crash, but they do not claim the official cloud/OS feature:

| Area | Behavior |
|---|---|
| `computer_use` / `browser_use` / `realtime` / `guardian` / `code_mode_host` | `notConfigured` / `underDevelopment` |
| Remote control | status/enable/disable only; no transport |
| ChatGPT usage / credits | empty or `noCredit` |
| Cloud worktree / cloud tasks | `notConfigured` |
| Elevated Windows sandbox | `notConfigured` |
| MCP OAuth | not configured |
| Token usage notifications | local `chars/4` estimate, not provider usage |
| Auto-updater | prints that this is an independent build |

## Not implemented

Official protocol schema codegen, voice/WebRTC sessions, hosted multi-agent, and OS-level sandbox equivalent to Linux landlock / macOS seatbelt.

## Tests

`dotnet test` runs the harness suite (fake `IModelClient`, no live network in the agent loop). Tests are grouped by area under `tests/CodexSharp.Tests/`.

## License

Independent source. Not affiliated with OpenAI.
