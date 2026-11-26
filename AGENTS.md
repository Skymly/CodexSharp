# CodexSharp

.NET 10 implementation of a coding-agent harness with CLI, app-server, and desktop hosts (`CodexSharp.slnx`).

## Build & Verify

```powershell
# Authoritative local & CI gate (Restore -> Compile -> Test):
./build.ps1 Test        # Linux/macOS: ./build.sh Test
```

- SDK version is pinned in [`global.json`](global.json); CI runs the same Nuke `Test` target.

## Architecture & Invariants

- **Language split**: Keep **F#** as the protocol and agent core and **C#** as the host and UI layer (see [`docs/architecture.md`](docs/architecture.md) and [`docs/SOURCE_MAP.md`](docs/SOURCE_MAP.md)).
- **Sandbox & test isolation**: Default sandbox is `workspace-write` — never weaken it in tests unless the test specifically targets sandbox behavior. Agent loop tests must use a fake `IModelClient`, never live network calls.
- **Secrets & local state**: Never commit secrets; local runtime state lives under `~/.codexsharp`.

## Docs & Agent Config

- Architecture & layout: [`docs/architecture.md`](docs/architecture.md), [`docs/SOURCE_MAP.md`](docs/SOURCE_MAP.md), [`docs/DESKTOP_REQUIREMENTS.md`](docs/DESKTOP_REQUIREMENTS.md)
- Goal continuations & milestone cycles: [`docs/agents/milestone-cycle.md`](docs/agents/milestone-cycle.md)

## Repo-Specific Conventions

- Default branch is **`master`**.
- Prefer small, compiling changes; English issues, PRs, and commit messages.
- Do not commit or push unless explicitly requested.
