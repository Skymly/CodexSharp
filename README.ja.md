# CodexSharp

[English](README.md) | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md) | **日本語**

C# + F# + .NET 10 による Codex **harness** の再実装。対話型 CLI、JSON-RPC App Server、およびデスクトップアプリを備えたローカルコーディングエージェントです。

これは公開されている Codex アーキテクチャ（agent loop、threads/turns/items、app-server JSON-RPC、sandbox と approvals）に着想を得た独立実装であり、公式の OpenAI Codex CLI や Desktop アプリでは**ありません**。

公式クレートと本リポジトリの対応一覧は `docs/SOURCE_MAP.md` を参照してください。  
デスクトップ機能のベースラインと CodexSharp（ChatGPT 26.901）の比較は `docs/DESKTOP_REQUIREMENTS.md` を参照してください。

## 上流ソース

公式 Codex は `vendor/codex`（https://github.com/openai/codex）にクローンされます。このクローンは `.gitignore` 対象の参照資料であり、ビルド依存ではありません。

## 構成

| プロジェクト | 言語 | 役割 |
|---|---|---|
| `src/CodexSharp.Protocol` | F# | Thread、Turn、Item、ツール/モデル契約 |
| `src/CodexSharp.Core` | F# | プロンプト組み立てとエージェントループ |
| `src/CodexSharp.Runtime` | C# | 設定、永続化、サンドボックス、ツール、HTTP モデルクライアント |
| `src/CodexSharp.AppServer` | C# | 双方向 JSON-RPC ホスト（stdio / ws / unix / in-process） |
| `src/CodexSharp.Client` | C# | App Server クライアント |
| `src/CodexSharp.Cli` | C# | `codexsharp` TUI + `exec` + `app-server` |
| `src/CodexSharp.Desktop` | F# / Avalonia.FuncUI | デスクトップアプリ（JSON-RPC クライアント） |

Codex と同様のハブ・アンド・スポーク構成で、すべての UI が単一のコアループと通信します。

```
CLI TUI ─┐
Desktop ─┼─► App Server / Runtime ─► Core agent loop ─► Responses / Chat Completions
AppServer┘                    │
                              ├─ shell, files, apply_patch, MCP, skills, subagents
                              └─ JSONL thread store under ~/.codexsharp
```

## ビルド

NUKE が `global.json` から SDK をブートストラップし、ローカルと CI で同じターゲットを実行します。

```bash
./build.ps1 Test      # Windows
./build.sh Test       # Linux/macOS
./build.cmd Compile
```

デフォルトターゲットは `Test`（`Restore` → `Compile` → `Test`）です。GitHub Actions ワークフロー `.github/workflows/ci.yml` も同じエントリポイントを呼び出します。

## 動作要件

- .NET SDK 10
- OpenAI 互換の API キー、または LM Studio などのローカルサーバー

## 実行

```bash
dotnet test
dotnet run --project src/CodexSharp.Cli
dotnet run --project src/CodexSharp.Cli -- exec "Summarize this repo"
dotnet run --project src/CodexSharp.Cli -- app-server
dotnet run --project src/CodexSharp.Desktop
```

## デスクトップショートカット（Windows）

NAV-05 / COMP-06 の公開デフォルトキーです。現時点ではキー割り当ての変更はできません。`Ctrl+K` はコマンドパレットを開きます（ターミナルのクリアは `Ctrl+L` です）。

| キー | 操作 |
|---|---|
| Ctrl+K または Ctrl+Shift+P | コマンドパレット |
| Ctrl+N | 新規スレッド |
| Ctrl+, | 設定 |
| Ctrl+` | ターミナル切り替え |
| Ctrl+B | サイドバー切り替え |

パレットの操作：新規スレッド、設定を開く、既存スレッドへ移動、コンポーザーへフォーカス。

ディープリンクには `codexsharp://` を使用します（`codex://` ではなく、CodexSharp は公式プロトコルを奪いません）。

- `codexsharp://threads/new`
- `codexsharp://threads/<id>`
- `codexsharp://settings`
- `codexsharp://skills`

未知のパスは既存のメインウィンドウを開いて無視されます。クエリパラメータ `path` は既存のローカルディレクトリである場合のみ受け入れられ、実行されることはありません。

## 設定

初回起動時に `%USERPROFILE%\.codexsharp\config.toml` が作成されます（`CODEXSHARP_HOME` で上書き可能）。

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

認証情報の優先順位（最初に一致したものを使用）：

1. `CODEXSHARP_API_KEY`
2. プロバイダーの `env_key`（デフォルトは `OPENAI_API_KEY`）
3. `MINIMAX` / `MiniMax`
4. `~/.codexsharp/auth.json` の `{"api_key":"..."}`

`CODEXSHARP_PROVIDER`、`CODEXSHARP_MODEL`、`CODEXSHARP_BASE_URL` で選択中のプロバイダーを上書きできます。

OpenAI Responses API の場合は `wire_api = "responses"`、Chat Completions（LM Studio、MiniMax、ほとんどのローカルサーバー）の場合は `chat` を設定します。

機能フラグは `[features]` 配下にあります（`codexsharp features list|enable|disable`）。デフォルトはすべてオフです。

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

`exec` フラグ：`--model`、`--sandbox`、`--full-auto`、`--yolo`、`--json`、`--cd`、`--image`、`--worktree`、`--add-dir`。

TUI のスラッシュコマンドは公式セットに従います（`/help`、`/status`、`/new`、`/compact`、`/fork`、`/model`、`/sandbox` など）。これらの多くは設定ファイルを直接触るのではなく App Server 経由で動作します。

## App Server

Codex 形式のメソッドを持つ JSONL JSON-RPC です。トランスポートは stdio、WebSocket、Unix ソケット、またはインプロセス（Desktop と `exec`）に対応しています。

主要なスレッド／ターン API：

- `initialize` / `initialized`
- `thread/start` `thread/resume` `thread/list` `thread/archive` `thread/fork`
- `turn/start` `turn/interrupt` `turn/steer`
- 通知：`thread/started` `turn/started` `item/started` `item/agentMessage/delta` `item/completed` `turn/completed`
- サーバーリクエスト：`item/permissions/requestApproval`

ホストは account、config、MCP、skills、plugins、projects、fs、git、memory、sandbox の各メソッドも公開します。未対応のクラウドや OS 機能は成功を偽装せず、率直に `notConfigured` / `underDevelopment` を返します。メソッド対応表は `docs/SOURCE_MAP.md` を参照してください。

## ローカルで動作する機能

- エージェントループ：prompt -> stream -> tools -> repeat -> assistant message
- 組み込みツール：`shell`、`read_file`、`write_file`、`apply_patch`、`list_dir`、`update_plan`、`grep_files`、`file_search`、および機能フラグ制御下の `web_search`、`exec_command`、subagents、MCP リソース、`request_permissions`
- `~/.codexsharp/config.toml` からの MCP stdio および Streamable HTTP 読み込み（ツール名は `mcp__<server>__<tool>` として公開）
- git ルートから cwd までの `AGENTS.md` 探索、`skills/**/SKILL.md` 配下のスキルメタデータ読み込み
- `~/.codexsharp` 配下のローカルプラグインとマーケットプレイス
- 承認ポリシー：`untrusted`、`on-request`、`never`
- サンドボックスモード：`read-only`、`workspace-write`、`danger-full-access`（パスポリシーベース、Windows では終了時に子プロセスを落とす Job Object を使用、OS レベルの seatbelt はなし）
- JSONL によるスレッド永続化：resume / fork / archive / rollback / compact
- インプロセスサブエージェント（`spawn_agent` / `wait_agent` / ...）
- ローカル履歴コンパクション（文字数しきい値を超えた古いメッセージを削除。モデルによる要約は行いません）
- ChatGPT デバイスコードおよびローカル PKCE ログイン（トークンはローカル保存）
- Spectre.Console TUI および Avalonia.FuncUI デスクトップアプリ（Desktop は App Server JSON-RPC クライアント）

MCP stdio 設定例：

```toml
[mcp_servers.docs]
command = "npx"
args = ["-y", "mcp-server"]
```

## 正直なスタブ（Stubs）

クライアントのクラッシュを防ぐために存在する API であり、公式のクラウドや OS 機能を提供するものではありません。

| 領域 | 挙動 |
|---|---|
| `computer_use` / `browser_use` / `realtime` / `guardian` / `code_mode_host` | `notConfigured` / `underDevelopment` を返却 |
| Remote control | status/enable/disable のみ、トランスポートなし |
| ChatGPT usage / credits | 空または `noCredit` |
| Cloud worktree / cloud tasks | `notConfigured` |
| 昇格版 Windows サンドボックス | `notConfigured` |
| MCP OAuth | 未設定（not configured） |
| トークン使用量通知 | プロバイダーの使用量ではなくローカルの `chars/4` 推定値 |
| Auto-updater | 独立ビルドである旨を表示 |

## 未実装機能

公式プロトコルスキーマのコード生成、音声/WebRTC セッション、ホスト型マルチエージェント、および Linux landlock / macOS seatbelt 相当の OS レベルサンドボックス。

## テスト

`dotnet test` でハーネススイートを実行します（フェイクの `IModelClient` を使用し、エージェントループ内で実ネットワーク通信は行いません）。テストは領域別に `tests/CodexSharp.Tests/` 配下に分類されています。

## ライセンス

独立したソースコードであり、OpenAI とは関係ありません。
