# Milestone cycle

Read this on **Goal** continuations, a **cycle**, **wayfinder**, or **grilling**.
The Goal names Outcome, verification, and **K** (how many cycles to close). This file owns every continuation hop.

`Follow /goal-hop` still applies for shipping a claimed ticket. This file **replaces hop G**: an empty implement pool is not blocked — it is the door into architecture, retro, or the next cycle. **Council** replaces goal-hop Proxy-user and grilling HITL.

## 1. Detect

Ignore chat memory. Read live `git status` / `git log`, `gh issue list`, `gh pr list`, and [cycle-status.md](cycle-status.md).

Create missing tracker labels on first need: `wayfinder:map`, `ready-for-agent`, `ready-for-human`.

**Complete** when [cycle-status.md](cycle-status.md) lists **K** accepted cycles **and** the tracker has no open `ready-for-agent` children for those cycles **and** `./build.ps1 Test` is green on the default branch. Then `update_goal` complete. Stop.

Token budget is not completion.

Done when verification is decided or the live frontier hop is known.

## 2. Pick one hop

First match wins. Then do that hop and **stop** (Goal continuation takes the next). One ticket per hop. Plan-mode wait is forbidden — auto-accept the Plan and execute in this same turn.

| Id | When | Hop |
|---|---|---|
| A | Open PR, or a claimed ticket with unpushed/unmerged commits | `/ship-pr-reviewed` |
| B | Unblocked unclaimed `ready-for-agent` implement ticket | `/next-work-auto` implement path |
| C | Open `wayfinder:map` with a frontier decision ticket | `/next-work-auto` wayfinder path, **Council** for grilling |
| D | Map exists, frontier empty, way clear, no implement children yet | `/to-spec` then `/to-tickets` against that parent (one PR: create children, write numbers into the plan table). List existing children first; never mint a second S1-Sn set |
| E | Fog remains toward the destination (`Not yet specified`, or no map) | `/wayfinder` only far enough to graduate fog into tickets. Resolve nothing else |
| F | Feature side of this cycle is empty, no architecture map | `/improve-codebase-architecture` — **chart only** |
| G | Architecture map in play (fog, frontier, spec, or `ready-for-agent` debt tickets) | Same as C/D/E/B against **that** map |
| H | Feature + architecture tickets empty, retro not recorded | Write the retro into [cycle-status.md](cycle-status.md), mark the cycle accepted or file gap tickets and return to B |
| I | Accepted cycles `< K` and the next destination is named | Start that cycle (section 4). Chart a map, or skip to tickets when a Goal file already names the slices |
| J | Accepted cycles `>= K` | `update_goal` complete |
| X | None of the above | Blocked stop (section 6) |

`ready-for-human` is never hop B. Credentials, dashboards, `/wizard` -> blocked stop.

Done when exactly one hop has finished, or the run has stopped blocked/complete.

## 3. Already-specified destinations

A cycle whose destination is already a Goal file (`docs/DESKTOP_M2_GOAL.md`, later `docs/DESKTOP_Mn_GOAL.md`) **does not re-chart**.

- Missing tracker children for named slices -> this hop creates those `ready-for-agent` issues (title `[Mn][<ID>] ...`) and stops.
- Children exist -> hop B.
- Feature children all closed -> hop F (architecture), not a new wayfinder of the same destination.

Authoritative IDs stay in `docs/DESKTOP_REQUIREMENTS.md`. MX / section 5.2 WON'T never become a cycle destination.

## 4. Next cycle (K > 1)

Next destination is the next row in `docs/DESKTOP_REQUIREMENTS.md` section 5.1 that is not yet accepted in [cycle-status.md](cycle-status.md).

- **M3** if M2 is accepted and K allows it. No `DESKTOP_M3_GOAL.md` yet -> hop E (chart).
- **MX** is out of scope. If K still remains after the last in-scope milestone, blocked stop: K overshoots the roadmap.

A new cycle starts only after the previous cycle's retro is recorded.

## 5. Council

When a hop would `/grilling`, run **Council**. Do not wait for the user. Do not ask batch vs sequential — every round is the whole **frontier**.

Each round:

1. Number every frontier question with a recommended answer.
2. Spawn **three** sub-agents in parallel (`fork_context: false`). Same question list, same constitution, different role:
   - **Upstream** — `vendor/codex` is the default unless this repo already forked it.
   - **Ship-small** — one compiling slice; no extra P1/P2.
   - **Deep-module** — seam, testability, F# protocol/core vs C# host/UI.
3. Each agent answers every question: choice, one-paragraph reason, dissent.
4. Parent synthesizes. Agreement stands. On a split, **Ship-small** wins unless constitution or Upstream cites a concrete upstream path.
5. Close all three agents. Post the vote on the ticket (or the map). Recompute the frontier from those settlements and loop until the tree is empty **or** this hop's ticket is resolved — still one ticket per hop.

**Facts are not voted.** Dispatch research against `vendor/codex`, tests, and the tracker. Only **decisions** go to Council.

Constitution (Council cannot override):

- This file, `AGENTS.md`, the active Goal, `docs/DESKTOP_REQUIREMENTS.md` WON'T / honest stubs
- Default sandbox `workspace-write`; agent-loop tests use a fake `IModelClient`
- No secrets in-repo; local state under `~/.codexsharp`

## 6. Blocked stop

Do not mark the Goal complete. Report attempted paths, evidence, the blocker, and what would unlock it. `update_goal` blocked only when the same blocker has no defensible path under constitution.

Blocked (do not grill, do not invent a cycle):

- Redrawing the destination or taking MX / section 5.2 WON'T
- Cloud success, Electron, live network in agent-loop tests, weakening default sandbox
- Human-only steps (credentials, dashboard clicks, `/wizard`)
- K remaining but no in-scope next destination

## 7. Hop close-out

Update [cycle-status.md](cycle-status.md). Comment the parent issue if one exists. Write four lines, then **stop**:

- current cycle / phase / hop just done (name + link)
- evidence (tests, PR, closed ticket, `ci: none|watching|green|red`)
- remaining toward K
- next hop the continuation should take

Do not start the next ticket.
