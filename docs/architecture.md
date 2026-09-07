# Architecture

CodexSharp follows the public Codex hub-and-spoke layout.

## Surfaces

- **CLI TUI** (`codexsharp`) talks to Core in-process, same as the original TUI.
- **Desktop** (Avalonia.FuncUI.Elmish + Fluent) talks JSON-RPC to `InProcessAppServer` (same protocol as `codexsharp app-server`).
- **App Server** exposes JSON-RPC over stdio, websocket (`--listen ws://IP:PORT`), or unix socket.

## Core loop (F#)

`AgentLoop.runTurn`:

1. Emit `turn/started` and the user item
2. Assemble prompt: instructions, permissions, AGENTS.md, skills, environment, history
3. Stream the model
4. If the model returns tool calls, approve (optional) then execute, append tool output, loop
5. Stop on an assistant message, interrupt, or max turns

## Runtime (C#)

- `ConfigService` reads `~/.codexsharp/config.toml`
- `HttpModelClient` is an `IModelClient` over Microsoft.Extensions.AI (`IChatClient`). Tools are `AIFunctionDeclaration` only; the F# loop still executes them.
- `BuiltinToolExecutor` hosts shell / files / apply_patch
- `WorkspaceSandbox` enforces path policy
- `JsonlThreadStore` persists threads

## Protocol primitives

Thread → Turn → Item, with approval as a first-class event. App Server maps those events onto Codex-shaped JSON-RPC methods.
