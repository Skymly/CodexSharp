# CodexSharp

C# + F# + .NET 10 recreation of the Codex **harness**: a local coding agent with an interactive CLI, a JSON-RPC app server, and a desktop app.

This is an independent implementation inspired by the public Codex architecture (agent loop, threads/turns/items, app-server JSON-RPC, sandbox + approvals). It is **not** the official OpenAI Codex CLI or Desktop app.

## Upstream source

Official Codex is cloned to `vendor/codex` (https://github.com/openai/codex). See `docs/SOURCE_MAP.md`.

## Layout

| Project | Language | Role |
|---|---|---|
| `src/CodexSharp.Protocol` | F# | Threads, turns, items, tool/model contracts |
| `src/CodexSharp.Core` | F# | Prompt assembly and the agent loop |
| `src/CodexSharp.Runtime` | C# | Config, persistence, sandbox, tools, HTTP model client |
| `src/CodexSharp.AppServer` | C# | Bidirectional JSON-RPC host (stdio) |
| `src/CodexSharp.Client` | C# | App-server client |
| `src/CodexSharp.Cli` | C# | `codexsharp` TUI + `exec` + `app-server` |
| `src/CodexSharp.Desktop` | F# / Avalonia.FuncUI | Desktop app |

Hub-and-spoke, same as Codex: every UI talks to one core loop.

```
CLI TUI ─┐
Desktop ─┼─► Runtime ─► Core agent loop ─► Responses / Chat Completions
AppServer┘         │
                   ├─ shell / read_file / write_file / apply_patch / update_plan
                   └─ JSONL thread store under ~/.codexsharp
```

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

## CLI

```
codexsharp                 interactive TUI
codexsharp "prompt"        opening turn, then TUI
codexsharp exec "prompt"   one shot
codexsharp app             desktop
codexsharp app-server [--listen URL]   JSON-RPC (stdio://, ws://IP:PORT, unix://, off)
codexsharp resume [id]
codexsharp doctor          local install / config diagnosis
codexsharp execpolicy check COMMAND
codexsharp exec-server     local process/fs JSON-RPC (stdio)
codexsharp migrate-rollouts
codexsharp update          independent build; no auto-updater
codexsharp mcp             list MCP servers
codexsharp skills          list | enable | disable
codexsharp plugin          list / add / remove local plugins
codexsharp marketplace     list / add / remove local marketplaces
```

`exec` flags: `--model`, `--sandbox`, `--full-auto`, `--yolo`.

TUI slash commands: `/help` `/status` `/new` `/quit`.

## App Server

JSONL JSON-RPC, Codex-shaped methods:

- `initialize` / `initialized`
- `thread/start` `thread/resume` `thread/list` `thread/archive`
- `turn/start`
- notifications: `thread/started` `turn/started` `item/started` `item/agentMessage/delta` `item/completed` `turn/completed`
- server request: `item/permissions/requestApproval`
- `windowsSandbox/readiness` (currently `notConfigured`)

## What is implemented

- Agent loop: prompt → stream → tools → repeat → assistant message
- Built-in tools: `shell`, `read_file`, `write_file`, `apply_patch`, `list_dir`, `update_plan`
- `AGENTS.md` walk from git root to cwd, skill metadata under `skills/**/SKILL.md`
- Approval policies: `untrusted`, `on-request`, `never`
- Sandbox modes: `read-only`, `workspace-write`, `danger-full-access` (path policy; Windows has no OS seatbelt)
- Thread persistence as JSONL
- Spectre.Console TUI and Avalonia.FuncUI desktop (Desktop is an App Server JSON-RPC client)
- Built-in `grep_files` / `file_search` (Codex file-search crate analogue)

MCP stdio servers are read from `~/.codexsharp/config.toml`:

```toml
[mcp_servers.docs]
command = "npx"
args = ["-y", "mcp-server"]
```

Tools appear as `mcp__<server>__<tool>` in the agent loop.

## Not in v0.1

MCP servers, ChatGPT OAuth, OS-level Windows sandbox, compaction, subagents, voice, browser, official protocol schema codegen.

## License

Independent source. Not affiliated with OpenAI.
