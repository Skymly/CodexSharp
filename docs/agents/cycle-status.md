# Cycle status

K is owned by the active Goal, not this file. Hops update this file; they do not invent a destination.

## Closed cycles

### M2 — accepted

- Destination: `docs/DESKTOP_M2_GOAL.md` + `docs/DESKTOP_REQUIREMENTS.md` section 5.1 M2
- Feature slices were already merged as ordinary Goal work (`#1`–`#11`, `#13`–`#25`). This cycle is the architecture sweep + retro only. Those feature issues stay closed.
- Architecture:
  - Map: [[M2] Architecture map](https://github.com/Skymly/CodexSharp/issues/27)
  - Chart: `C:\\Users\\98217\\AppData\\Local\\Temp\\architecture-review-20260908-192637.html`
  - [Route Desktop collaboration through the App Server seam](https://github.com/Skymly/CodexSharp/issues/28) — do not mint Activity/notify/gh-preview JSON-RPC; palette and deep-link parse stay Desktop-local
  - [Shrink Desktop ScreenState or stop at the seam](https://github.com/Skymly/CodexSharp/issues/29) — do not extract a chrome module this cycle
  - Spec: [Architecture spec: no M2 implement slices](https://github.com/Skymly/CodexSharp/issues/30) — S1–Sn empty
- Retro: empty implement pool legal; Council replaced HITL; do not invent unofficial RPC.
- Verification: M2 does not count toward this Goal's K.

### C1 无云壳 — accepted

- Destination: Goal Named cycle C1 — NAV-01, LIFE-07 (`docs/DESKTOP_REQUIREMENTS.md`)
- Feature:
  - Map: [[C1] Cloudless Chat/Work shell](https://github.com/Skymly/CodexSharp/issues/31)
  - Spec: [[C1] Spec: cloudless Chat/Work shell](https://github.com/Skymly/CodexSharp/issues/34)
  - S1 [[C1][NAV-01] Local Chat/Work/Codex layout switch](https://github.com/Skymly/CodexSharp/issues/32) — [PR 35](https://github.com/Skymly/CodexSharp/pull/35)
  - S2 [[C1][LIFE-07] Quick chat and temporary chat](https://github.com/Skymly/CodexSharp/issues/33) — [PR 36](https://github.com/Skymly/CodexSharp/pull/36)
- Architecture:
  - Map: [[C1] Architecture map](https://github.com/Skymly/CodexSharp/issues/37)
  - Chart: `C:\\Users\\98217\\AppData\\Local\\Temp\\architecture-review-20260908-211407.html`
  - [Give ephemeral a store adapter or keep Path empty](https://github.com/Skymly/CodexSharp/issues/38) — keep Path empty; no IThreadStore (unanimous Council; vendor skip-persist on the same store)
  - [Keep ShellMode Desktop-local or deepen without a chrome extract](https://github.com/Skymly/CodexSharp/issues/39) — stay Desktop-local; no chrome extract; no shell/mode RPC
  - Spec: [Architecture spec: no C1 implement slices](https://github.com/Skymly/CodexSharp/issues/40) — S1–Sn empty; no ready-for-agent debt
- Retro:
  - Went well: named-cycle wayfinder did not skip to a pre-written DESKTOP_C1.md; both allowlist IDs shipped as vertical PRs; empty architecture pool is legal after those IDs; Council replaced HITL; official ephemeral skipped disk without a new method.
  - Keep: ShellMode as Desktop-local chrome; thread/list projectId JSON null for unassigned; ephemeral Path empty + tests that lock no jsonl; honest Work pane notConfigured; no Electron; fake IModelClient; workspace-write.
  - Change later (not this cycle): Path empty is a sentinel, not vendor Option skip; MainView.fs is still wide; F# ShellMode DU vs C# DefaultShellMode string drift.
  - Do not: reopen #32/#33; extract MainView chrome; mint IThreadStore or shell/mode RPC; start C2 in the same hop as this retro; steal codex://; elevated sandbox.
- Verification: feature PRs 35 and 36 green on windows-latest. No open ready-for-agent. Named feature S1–Sn were not empty.

M0/M1/M2 **feature** slices shipped as ordinary Goal slices — not as wayfinder cycles. Do not reopen them. Do not count those feature slices or M2 toward this Goal's K.

## Current cycle

None. C1 is accepted. Next named destination is C2 (hop I = hop E chart). Do not pre-write docs/DESKTOP_C2.md.

## Next destinations (if K > 1)

Named cycles on the active Goal (do not substitute §5.1 rows):

- C2 浮动窗 — NAV-04
- C3 多根项目 — LIFE-02, GIT-06
- C4 诚实 stub 加固 — BCU-01, BCU-06, VPM-01, EXT-08, SET-03
- C5 本机远程入口 — REM-02, EXT-04
- MX / §5.2 WON'T: out of scope
