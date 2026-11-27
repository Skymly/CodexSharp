# CodexSharp

[English](README.md) | [简体中文](README.zh-CN.md) | **繁體中文** | [日本語](README.ja.md)

[![CI](https://github.com/Skymly/CodexSharp/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/Skymly/CodexSharp/actions/workflows/ci.yml)

以 C# + F# + .NET 10 重建的 Codex **harness**：包含互動式 CLI、JSON-RPC App Server 與桌面應用程式的本機編碼代理程式。

這是一個受公開 Codex 架構（agent loop、threads/turns/items、app-server JSON-RPC、sandbox 與 approvals）啟發的獨立實作，**並非**官方 OpenAI Codex CLI 或 Desktop 應用程式。

## 專案結構

| 專案 | 語言 | 職責 |
|---|---|---|
| `src/CodexSharp.Protocol` | F# | Thread、Turn、Item 與工具/模型契約 |
| `src/CodexSharp.Core` | F# | 提示詞組裝與核心 agent loop |
| `src/CodexSharp.Runtime` | C# | 組態、持續性儲存、沙箱、工具、HTTP 模型用戶端 |
| `src/CodexSharp.AppServer` | C# | 雙向 JSON-RPC 主機（stdio / ws / unix / in-process） |
| `src/CodexSharp.Client` | C# | App Server 用戶端 |
| `src/CodexSharp.Cli` | C# | `codexsharp` TUI + `exec` + `app-server` |
| `src/CodexSharp.Desktop` | F# / Avalonia.FuncUI | 桌面應用程式（JSON-RPC 用戶端） |

採用與 Codex 相同的 Hub-and-spoke 架構：每個介面皆透過同一個核心迴圈進行通訊。

```
CLI TUI ─┐
Desktop ─┼─► App Server / Runtime ─► Core agent loop ─► Responses / Chat Completions
AppServer┘                    │
                              ├─ shell, files, apply_patch, MCP, skills, subagents
                              └─ JSONL thread store under ~/.codexsharp
```

## 建置

NUKE 會依據 `global.json` 引導對應的 SDK，並在本機與 CI 執行相同的目標：

```bash
./build.ps1 Test      # Windows
./build.sh Test       # Linux/macOS
./build.cmd Compile
```

預設目標為 `Test`（`Restore` → `Compile` → `Test`）。GitHub Actions 工作流程 `.github/workflows/ci.yml` 亦呼叫同一進入點。

## 環境需求

- .NET SDK 10
- 相容於 OpenAI 的 API 金鑰，或如 LM Studio 等本機伺服器

## 執行

```bash
dotnet test
dotnet run --project src/CodexSharp.Cli
dotnet run --project src/CodexSharp.Cli -- exec "Summarize this repo"
dotnet run --project src/CodexSharp.Cli -- app-server
dotnet run --project src/CodexSharp.Desktop
```

## 桌面端快速鍵（Windows）

NAV-05 / COMP-06 的公開預設鍵位，目前尚未支援重新繫結。`Ctrl+K` 會開啟命令選擇區，**不會**清除終端機（清除終端機為 `Ctrl+L`）。

| 快速鍵 | 動作 |
|---|---|
| Ctrl+K 或 Ctrl+Shift+P | 命令選擇區 |
| Ctrl+N | 新增執行緒 |
| Ctrl+, | 設定 |
| Ctrl+` | 切換終端機 |
| Ctrl+B | 切換側邊欄 |

命令選擇區動作：新增執行緒、開啟設定、跳至現有執行緒、聚焦輸入框。

Deep link 使用 `codexsharp://`（而非 `codex://`；CodexSharp 不會搶佔官方通訊協定）：

- `codexsharp://threads/new`
- `codexsharp://threads/<id>`
- `codexsharp://settings`
- `codexsharp://skills`

未知路徑會聚焦現有主視窗並略過。查詢參數 `path` 僅在其指向現有本機目錄時才會接受，且絕不會被執行。

## 組態

首次啟動會寫入 `%USERPROFILE%\.codexsharp\config.toml`（可透過 `CODEXSHARP_HOME` 覆寫）。

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

認證比對順序（取第一個符合項）：

1. `CODEXSHARP_API_KEY`
2. 提供者的 `env_key`（預設為 `OPENAI_API_KEY`）
3. `MINIMAX` / `MiniMax`
4. `~/.codexsharp/auth.json` 中的 `{"api_key":"..."}`

`CODEXSHARP_PROVIDER`、`CODEXSHARP_MODEL` 與 `CODEXSHARP_BASE_URL` 會覆寫目前選取的提供者。

OpenAI Responses API 請設定 `wire_api = "responses"`；Chat Completions（LM Studio、MiniMax 與多數本機伺服器）請設定 `chat`。

功能旗標位於 `[features]` 下（`codexsharp features list|enable|disable`），預設皆為關閉。

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

`exec` 旗標：`--model`、`--sandbox`、`--full-auto`、`--yolo`、`--json`、`--cd`、`--image`、`--worktree`、`--add-dir`。

TUI 斜線命令遵循官方集合（`/help`、`/status`、`/new`、`/compact`、`/fork`、`/model`、`/sandbox` 等）。其中許多命令現已透過 App Server 執行，而非直接修改組態檔。

## App Server

JSONL 格式的 JSON-RPC，採用 Codex 風格的方法名稱。傳輸支援 stdio、WebSocket、Unix Socket 或同處理序通訊（Desktop 與 `exec`）。

核心 Thread / Turn 介面：

- `initialize` / `initialized`
- `thread/start` `thread/resume` `thread/list` `thread/archive` `thread/fork`
- `turn/start` `turn/interrupt` `turn/steer`
- 通知：`thread/started` `turn/started` `item/started` `item/agentMessage/delta` `item/completed` `turn/completed`
- 伺服器請求：`item/permissions/requestApproval`

主機亦提供 account、config、MCP、skills、plugins、projects、fs、git、memory 與 sandbox 方法。缺乏的雲端或作業系統能力會如實回傳 `notConfigured` / `underDevelopment`，而非偽造成功。方法對照表請參閱 `docs/SOURCE_MAP.md`。

## 本機可用功能

- Agent 迴圈：prompt -> stream -> tools -> repeat -> assistant message
- 內建工具包含 `shell`、`read_file`、`write_file`、`apply_patch`、`list_dir`、`update_plan`、`grep_files`、`file_search`，以及受功能旗標控制的 `web_search`、`exec_command`、subagents、MCP resources 與 `request_permissions`
- 支援由 `~/.codexsharp/config.toml` 載入 stdio 與 Streamable HTTP 模式的 MCP；工具名稱顯示為 `mcp__<server>__<tool>`
- 從 git 根目錄遍歷至 cwd 讀取 `AGENTS.md`；從 `skills/**/SKILL.md` 讀取技能中繼資料
- `~/.codexsharp` 下的本機外掛程式與市集
- 核准策略：`untrusted`、`on-request`、`never`
- 沙箱模式：`read-only`、`workspace-write`、`danger-full-access`（路徑策略；Windows 使用 Job Object 隨處理序關閉終止，無 OS 層級 seatbelt）
- 以 JSONL 儲存執行緒；支援 resume / fork / archive / rollback / compact
- 同處理序子代理（`spawn_agent` / `wait_agent` / ...）
- 本機歷程壓縮（超過字元閾值後丟棄較舊訊息，而非由模型撰寫摘要）
- ChatGPT 裝置碼登入與本機 PKCE 登入（Token 儲存於本機）
- Spectre.Console TUI 與 Avalonia.FuncUI 桌面應用程式（Desktop 為 App Server JSON-RPC 用戶端）

MCP stdio 設定範例：

```toml
[mcp_servers.docs]
command = "npx"
args = ["-y", "mcp-server"]
```

## 如實存根項目（Stubs）

以下 API 為避免用戶端當機而存在，但並不宣稱支援官方雲端或作業系統功能：

| 領域 | 行為 |
|---|---|
| `computer_use` / `browser_use` / `realtime` / `guardian` / `code_mode_host` | 回傳 `notConfigured` / `underDevelopment` |
| Remote control | 僅支援狀態/啟用/停用，無實際傳輸通道 |
| ChatGPT usage / credits | 回傳空白或 `noCredit` |
| Cloud worktree / cloud tasks | 回傳 `notConfigured` |
| 提權 Windows 沙箱 | 回傳 `notConfigured` |
| MCP OAuth | 未組態（not configured） |
| Token 用量通知 | 本機 `chars/4` 估算，非提供者實際用量 |
| Auto-updater | 顯示此為獨立建置版本 |

## 尚未實作

官方通訊協定 schema 程式碼產生、語音/WebRTC 工作階段、託管多代理，以及等同於 Linux landlock / macOS seatbelt 的作業系統層級沙箱。

## 測試

`dotnet test` 會執行 harness 測試套件（使用模擬的 `IModelClient`，agent 迴圈不會進行即時網路連線）。測試依領域分組於 `tests/CodexSharp.Tests/` 下。

## 參考與上游對齊

- 官方 crate 與本儲存庫的持續對齊清單：[`docs/SOURCE_MAP.md`](docs/SOURCE_MAP.md)
- 桌面端能力基線（ChatGPT 26.901）：[`docs/DESKTOP_REQUIREMENTS.md`](docs/DESKTOP_REQUIREMENTS.md)
- 上游參考資料（已列入 `.gitignore`，非建置相依性）：官方 Codex 複本位於 `vendor/codex`（`https://github.com/openai/codex`）

## 授權

獨立原始碼專案，與 OpenAI 無關聯。
