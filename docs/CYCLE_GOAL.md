# Cycle Goal 提示

Paste **one** block into Codex `/goal`. The phrase `Follow /goal-hop` keeps shipping hops on the existing skill. `docs/agents/milestone-cycle.md` replaces hop G (empty pool → architecture / retro / next cycle) and replaces grilling HITL with Council.

Do not paste the skill files. Set **K** in the Outcome. Token budget is not completion.

## K = 1 — close the current cycle

Current live state (see `docs/agents/cycle-status.md`): M2 feature slices are already merged. K = 1 means finish M2's architecture sweep + retro, then complete.

```text
/goal Close 1 complete milestone cycle on CodexSharp. Follow /goal-hop. When hop G would block, follow docs/agents/milestone-cycle.md. Grilling uses Council in that file.

## Outcome
Accept the current cycle (M2) under docs/agents/milestone-cycle.md: architecture map, debt tickets emptied, retro recorded. Do not reopen M1/M2 feature issues. Do not start M3.

Verified by all of:
- docs/agents/cycle-status.md lists M2 as accepted, with architecture and retro recorded
- no open ready-for-agent issues for that cycle
- ./build.ps1 Test green on the default branch

Open ready-for-human tickets are not in this surface.
Reaching a token budget is not completion.
```

## K = N — close N cycles

Swap N. After M2 is accepted, the next in-scope destination is M3 (`docs/DESKTOP_REQUIREMENTS.md` section 5.1). MX is out of scope; if N overshoots the roadmap the Goal blocks.

```text
/goal Close N complete milestone cycles on CodexSharp. Follow /goal-hop. When hop G would block, follow docs/agents/milestone-cycle.md. Grilling uses Council in that file.

## Outcome
Accept N cycles under docs/agents/milestone-cycle.md, starting from the current cycle in docs/agents/cycle-status.md. One hop per continuation. Already-specified Goal files are not re-charted. MX / section 5.2 WON'T are not destinations.

Verified by all of:
- docs/agents/cycle-status.md lists N accepted cycles
- no open ready-for-agent issues for those cycles
- ./build.ps1 Test green on the default branch

Open ready-for-human tickets are not in this surface.
Reaching a token budget is not completion.
```
