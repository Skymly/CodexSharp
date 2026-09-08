# CodexSharp

.NET 10 recreation of the Codex harness. Prefer small, compiling changes. Keep F# as the protocol/agent core and C# as the host/UI.

- Do not write secrets. Local state lives in `~/.codexsharp`.
- Default sandbox is workspace-write. Do not weaken it in tests unless the test is about that.
- Agent loop tests must use a fake `IModelClient`, never a live network.

Goal continuations, milestone cycles, wayfinder, and grilling: `docs/agents/milestone-cycle.md`.
