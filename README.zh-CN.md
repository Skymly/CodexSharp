# CodexSharp

[English](README.md) | **简体中文** | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md)

[![CI](https://github.com/Skymly/CodexSharp/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/Skymly/CodexSharp/actions/workflows/ci.yml)

基于 C# + F# + .NET 10 实现的 Codex **harness**：包含交互式 CLI、JSON-RPC App Server 与桌面应用的本地编码智能体。

这是一个受公开 Codex 架构（agent loop、threads/turns/items、app-server JSON-RPC、sandbox 与 approvals）启发的独立实现，**并非**官方 OpenAI Codex CLI 或 Desktop 应用。

## 仓库结构

| 项目 | 语言 | 职责 |
|---|---|---|
| `src/CodexSharp.Protocol` | F# | Thread、Turn、Item 与工具/模型契约 |
| `src/CodexSharp.Core` | F# | 提示词组装与核心 agent loop |
| `src/CodexSharp.Runtime` | C# | 配置、持久化、沙箱、工具、HTTP 模型客户端 |
| `src/CodexSharp.AppServer` | C# | 双向 JSON-RPC 宿主（stdio / ws / unix / in-process） |
| `src/CodexSharp.Client` | C# | App Server 客户端 |
| `src/CodexSharp.Cli` | C# | `codexsharp` TUI + `exec` + `app-server` |
| `src/CodexSharp.Desktop` | F# / Avalonia.FuncUI | 桌面应用（JSON-RPC 客户端） |

采用与 Codex 相同的 Hub-and-spoke 架构：所有界面统一对接同一个核心循环。

```
CLI TUI ─┐
Desktop ─┼─► App Server / Runtime ─► Core agent loop ─► Responses / Chat Completions
AppServer┘                    │
                              ├─ shell, files, apply_patch, MCP, skills, subagents
                              └─ JSONL thread store under ~/.codexsharp
```

## 构建

NUKE 会根据 `global.json` 引导对应 SDK，并在本地与 CI 中运行相同的目标：

```bash
./build.ps1 Test      # Windows
./build.sh Test       # Linux/macOS
./build.cmd Compile
```

默认目标为 `Test`（`Restore` → `Compile` → `Test`）。GitHub Actions 工作流 `.github/workflows/ci.yml` 调用同一入口。

## 环境要求

- .NET SDK 10
- 兼容 OpenAI API 的密钥，或 LM Studio 等本地服务

## 运行

```bash
dotnet test
dotnet run --project src/CodexSharp.Cli
dotnet run --project src/CodexSharp.Cli -- exec "Summarize this repo"
dotnet run --project src/CodexSharp.Cli -- app-server
dotnet run --project src/CodexSharp.Desktop
```

## 桌面端快捷键（Windows）

NAV-05 / COMP-06 的公开默认键位，暂不支持自定义重绑。`Ctrl+K` 打开命令面板，**不会**清空终端（清空终端为 `Ctrl+L`）。

| 快捷键 | 操作 |
|---|---|
| Ctrl+K 或 Ctrl+Shift+P | 命令面板 |
| Ctrl+N | 新建线程 |
| Ctrl+, | 设置 |
| Ctrl+` | 切换终端 |
| Ctrl+B | 切换侧边栏 |

命令面板支持的操作：新建线程、打开设置、跳转到已有线程、聚焦输入框。

Deep link 使用 `codexsharp://`（而非 `codex://`；CodexSharp 不会抢占官方协议）：

- `codexsharp://threads/new`
- `codexsharp://threads/<id>`
- `codexsharp://settings`
- `codexsharp://skills`

未知路径会聚焦现有主窗口并忽略。查询参数 `path` 仅在其指向已存在的本地目录时才会接受，且绝不会被执行。

## 配置

首次启动会写入 `%USERPROFILE%\.codexsharp\config.toml`（可通过 `CODEXSHARP_HOME` 覆盖）。

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

凭据匹配优先级（取首个命中项）：

1. `CODEXSHARP_API_KEY`
2. Provider 的 `env_key`（默认 `OPENAI_API_KEY`）
3. `MINIMAX` / `MiniMax`
4. `~/.codexsharp/auth.json` 中的 `{"api_key":"..."}`

`CODEXSHARP_PROVIDER`、`CODEXSHARP_MODEL` 和 `CODEXSHARP_BASE_URL` 可覆盖当前选定的 Provider。

使用 OpenAI Responses API 时设置 `wire_api = "responses"`；使用 Chat Completions（LM Studio、MiniMax 及大多数本地服务）时设置 `chat`。

特性开关位于 `[features]` 下（`codexsharp features list|enable|disable`），默认全部关闭。

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

`exec` 参数：`--model`、`--sandbox`、`--full-auto`、`--yolo`、`--json`、`--cd`、`--image`、`--worktree`、`--add-dir`。

TUI 斜杠命令遵循官方集合（`/help`、`/status`、`/new`、`/compact`、`/fork`、`/model`、`/sandbox` 等）。其中许多命令现已通过 App Server 执行，而不再直接读写配置文件。

## App Server

基于 JSONL 的 JSON-RPC，采用 Codex 风格的方法名。传输层支持 stdio、WebSocket、Unix Socket 或进程内通信（Desktop 与 `exec`）。

核心 Thread / Turn 接口：

- `initialize` / `initialized`
- `thread/start` `thread/resume` `thread/list` `thread/archive` `thread/fork`
- `turn/start` `turn/interrupt` `turn/steer`
- 通知：`thread/started` `turn/started` `item/started` `item/agentMessage/delta` `item/completed` `turn/completed`
- 服务端请求：`item/permissions/requestApproval`

宿主同时暴露 account、config、MCP、skills、plugins、projects、fs、git、memory 与 sandbox 方法。缺失的云端或系统能力会如实返回 `notConfigured` / `underDevelopment`，而不是伪造成功响应。方法映射见 `docs/SOURCE_MAP.md`。

## 本地可用能力

- Agent 循环：prompt -> stream -> tools -> repeat -> assistant message
- 内置工具：`shell`、`read_file`、`write_file`、`apply_patch`、`list_dir`、`update_plan`、`grep_files`、`file_search`，以及受特性开关控制的 `web_search`、`exec_command`、subagents、MCP resources 与 `request_permissions`
- 支持从 `~/.codexsharp/config.toml` 加载 stdio 与 Streamable HTTP 模式的 MCP；工具名显示为 `mcp__<server>__<tool>`
- 从 git 根目录向下遍历至 cwd 读取 `AGENTS.md`；从 `skills/**/SKILL.md` 读取技能元数据
- `~/.codexsharp` 下的本地插件与插件市场
- 审批策略：`untrusted`、`on-request`、`never`
- 沙箱模式：`read-only`、`workspace-write`、`danger-full-access`（基于路径策略；Windows 使用 Job Object 实现随进程关闭终止，无 OS 级 seatbelt）
- 以 JSONL 持久化 Thread；支持 resume / fork / archive / rollback / compact
- 进程内子智能体（`spawn_agent` / `wait_agent` / ...）
- 本地历史压缩（超过字符阈值后丢弃较早消息，而非模型生成摘要）
- ChatGPT 设备码登录与本地 PKCE 登录（Token 本地存储）
- Spectre.Console TUI 与 Avalonia.FuncUI 桌面端（Desktop 作为 App Server JSON-RPC 客户端运行）

MCP stdio 配置示例：

```toml
[mcp_servers.docs]
command = "npx"
args = ["-y", "mcp-server"]
```

## 如实存根项（Stubs）

以下 API 为避免客户端崩溃而保留，但不声称支持官方云端或系统能力：

| 领域 | 行为 |
|---|---|
| `computer_use` / `browser_use` / `realtime` / `guardian` / `code_mode_host` | 返回 `notConfigured` / `underDevelopment` |
| Remote control | 仅支持状态查询/启用/禁用，无实际传输通道 |
| ChatGPT usage / credits | 返回空或 `noCredit` |
| Cloud worktree / cloud tasks | 返回 `notConfigured` |
| 提权 Windows 沙箱 | 返回 `notConfigured` |
| MCP OAuth | 未配置（not configured） |
| Token 用量通知 | 基于本地 `chars/4` 估算，并非服务商真实用量 |
| Auto-updater | 提示当前为独立构建版 |

## 未实现项

官方协议 schema 代码生成、语音/WebRTC 会话、托管多智能体，以及等同于 Linux landlock / macOS seatbelt 的操作系统级沙箱。

## 测试

`dotnet test` 运行测试套件（使用伪造的 `IModelClient`，Agent 循环不访问真实网络）。测试按领域分组在 `tests/CodexSharp.Tests/` 下。

## 参考与上游对齐

- 官方 crate 与本仓库的持续对齐清单：[`docs/SOURCE_MAP.md`](docs/SOURCE_MAP.md)
- 桌面端能力基线（ChatGPT 26.901）：[`docs/DESKTOP_REQUIREMENTS.md`](docs/DESKTOP_REQUIREMENTS.md)
- 上游参考资料（已列入 `.gitignore`，非构建依赖）：官方 Codex 克隆位于 `vendor/codex`（`https://github.com/openai/codex`）

## 许可

独立源码项目，与 OpenAI 无隶属关系。
