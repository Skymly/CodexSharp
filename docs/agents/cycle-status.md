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

### C2 浮动窗 — accepted

- Destination: Goal Named cycle C2 — NAV-04 (`docs/DESKTOP_REQUIREMENTS.md`)
- Feature:
  - Map: [[C2] Floating always-on-top window](https://github.com/Skymly/CodexSharp/issues/42)
  - Spec: [[C2] Spec: floating always-on-top window](https://github.com/Skymly/CodexSharp/issues/44)
  - S1 [[C2][NAV-04] Pop-out always-on-top window](https://github.com/Skymly/CodexSharp/issues/43) — [PR 45](https://github.com/Skymly/CodexSharp/pull/45)
- Architecture:
  - Map: [[C2] Architecture map](https://github.com/Skymly/CodexSharp/issues/46)
  - Chart: `C:/Users/98217/AppData/Local/Temp/architecture-review-20260908-215200.html`
  - [Keep DesktopPopout adapter or fold into MainView](https://github.com/Skymly/CodexSharp/issues/47) — keep DesktopPopout + PopoutWindow; no IWindowHost; no ScreenState fold
  - Spec: [Architecture spec: no C2 implement slices](https://github.com/Skymly/CodexSharp/issues/48) — S1–Sn empty; no ready-for-agent debt
- Retro:
  - Went well: second Avalonia Window.Topmost without new JSON-RPC; same thread id (not /side fork); DesktopPopout tests lock bind/topmost; empty architecture pool legal after NAV-04.
  - Keep: DesktopPopout as host adapter; PopoutWindow as Avalonia adapter; /side stays fork; no Pets; no MainView chrome extract; fake IModelClient.
  - Change later (not this cycle): compact pop-out still lacks a live timeline; unused Forked flag; dual send paths on AppServerSession.
  - Do not: reopen #43; mint window/* RPC; fold pop-out into ScreenState; start C3 in the same hop as this retro; Electron; steal codex://.
- Verification: PR 45 green on windows-latest. No open ready-for-agent. Named feature S1–Sn were not empty.

### C3 多根项目 — accepted

- Destination: Goal Named cycle C3 — LIFE-02, GIT-06 (`docs/DESKTOP_REQUIREMENTS.md`)
- Feature:
  - Map: [[C3] Multi-root project](https://github.com/Skymly/CodexSharp/issues/50)
  - Spec: [[C3] Spec: multi-root project](https://github.com/Skymly/CodexSharp/issues/53)
  - S1 [[C3][LIFE-02] Extra project folders searchable](https://github.com/Skymly/CodexSharp/issues/51) — [PR 54](https://github.com/Skymly/CodexSharp/pull/54)
  - S2 [[C3][GIT-06] Multi-repo review with primary default](https://github.com/Skymly/CodexSharp/issues/52) — [PR 55](https://github.com/Skymly/CodexSharp/pull/55)
- Architecture:
  - Map: [[C3] Architecture map](https://github.com/Skymly/CodexSharp/issues/56)
  - Chart: `C:/Users/98217/AppData/Local/Temp/architecture-review-20260908-225430.html`
  - [Keep DesktopReviewRepos and three root modules](https://github.com/Skymly/CodexSharp/issues/57) — keep DesktopReviewRepos; GitProbe cwd-shaped; keep ProjectStore / SandboxRoots / SessionSandbox; thread-start extra-read compose; no ExtraRead type; no MainView chrome extract
  - Spec: [Architecture spec: no C3 implement slices](https://github.com/Skymly/CodexSharp/issues/58) — S1–Sn empty; no ready-for-agent debt
- Retro:
  - Went well: extra folders on existing `ProjectStore.Roots` (primary first); session extra-read searchable without host-global SandboxRoots or `--add-dir` write; DesktopReviewRepos labeled switch over `gitDiffToRemote` cwd; missing gh is `notConfigured` + reason; empty architecture pool legal after LIFE-02 and GIT-06.
  - Keep: three root modules; GitProbe cwd-shaped; AGENTS/skills/config on primary only; no `review/setRepo`; no MainView chrome extract; fake IModelClient; workspace-write.
  - Change later (not this cycle): extra-read list still a constructor argument through session/tools; MainView.fs is still wide; extra-root writes remain P2; Last-turn All repos skipped.
  - Do not: reopen #51/#52; merge ProjectStore into SandboxRoots; auto-discover extra AGENTS.md; start C4 in the same hop as this retro; GIT-08; Electron; steal codex://.
- Verification: PRs 54 and 55 green on windows-latest. No open ready-for-agent. Named feature S1–Sn were not empty.

### C4 诚实 stub 加固 — accepted

- Destination: Goal Named cycle C4 — BCU-01, BCU-06, VPM-01, EXT-08, SET-03 (`docs/DESKTOP_REQUIREMENTS.md`)
- Feature:
  - Map: [[C4] Honest stub lock](https://github.com/Skymly/CodexSharp/issues/60)
  - Spec: [[C4] Spec: honest stub lock](https://github.com/Skymly/CodexSharp/issues/66)
  - S1 [[C4][SET-03] Settings catalog without fake Enable](https://github.com/Skymly/CodexSharp/issues/61) — [PR 67](https://github.com/Skymly/CodexSharp/pull/67)
  - S2 [[C4][BCU-01] browser_use stays notConfigured](https://github.com/Skymly/CodexSharp/issues/62) — [PR 68](https://github.com/Skymly/CodexSharp/pull/68)
  - S3 [[C4][BCU-06] computer_use stays notConfigured](https://github.com/Skymly/CodexSharp/issues/63) — [PR 69](https://github.com/Skymly/CodexSharp/pull/69)
  - S4 [[C4][VPM-01] realtime Voice stays notConfigured](https://github.com/Skymly/CodexSharp/issues/64) — [PR 70](https://github.com/Skymly/CodexSharp/pull/70)
  - S5 [[C4][EXT-08] code_mode_host stays underDevelopment](https://github.com/Skymly/CodexSharp/issues/65) — [PR 71](https://github.com/Skymly/CodexSharp/pull/71)
- Architecture:
  - Map: [[C4] Architecture map](https://github.com/Skymly/CodexSharp/issues/72)
  - Chart: `C:/Users/98217/AppData/Local/Temp/architecture-review-20260908-234800.html`
  - [Keep HonestStubs; Doctor consumes StatusOf](https://github.com/Skymly/CodexSharp/issues/73) — keep honesty module; FeatureFlags.IsLockedFeature lock seam; Desktop + existing JSON-RPC adapters; Doctor consumes StatusOf; no Settings chrome extract; no honestStubs/list RPC
  - Spec: [Architecture spec: no C4 implement slices](https://github.com/Skymly/CodexSharp/issues/74) — S1–Sn empty; no ready-for-agent debt
- Retro:
  - Went well: five named IDs shipped as five vertical PRs (SET-03 first); Settings + JSON-RPC stay notConfigured or underDevelopment with tests locking no fake Enable; config/read computerUse/browserUse from HonestStubs; thread/realtime/start is notConfigured; no in-app browser, CU, Voice, or JS host; empty architecture pool legal after those IDs; Council replaced HITL.
  - Keep: HonestStubs as the honesty module; FeatureFlags.IsLockedFeature as the lock seam; Desktop settings and existing JSON-RPC as adapters; Doctor consumes StatusOf; no Enable that sticks; no MainView chrome extract; fake IModelClient; workspace-write.
  - Change later (not this cycle): MainView.fs is still wide; Settings catalog remains Desktop-local; image generation stays a later ART-03 concern, not a sixth C4 ID.
  - Do not: reopen #61–#65; implement @Browser, Computer Use, GPT-Live, or a JS host; mint honestStubs/list RPC; extract Settings chrome; start C5 in the same hop as this retro; mark image generation enabled; mark REM-01 connected; Electron; steal codex://.
- Verification: feature PRs 67–71 green on windows-latest. No open ready-for-agent. Named feature S1–Sn were not empty. Tests lock no fake Enable.

### C5 本机远程入口 — accepted

- Destination: Goal Named cycle C5 — REM-02, EXT-04 (`docs/DESKTOP_REQUIREMENTS.md`)
- Feature:
  - Map: [[C5] Local SSH remote entry](https://github.com/Skymly/CodexSharp/issues/76)
  - Spec: [[C5] Spec: local SSH remote entry](https://github.com/Skymly/CodexSharp/issues/77)
  - S1 [[C5][REM-02] SSH remote project entry](https://github.com/Skymly/CodexSharp/issues/78) — [PR 80](https://github.com/Skymly/CodexSharp/pull/80)
  - S2 [[C5][EXT-04] MCP OAuth stays notConfigured](https://github.com/Skymly/CodexSharp/issues/79) — [PR 81](https://github.com/Skymly/CodexSharp/pull/81)
- Architecture:
  - Map: [[C5] Architecture map](https://github.com/Skymly/CodexSharp/issues/82)
  - Chart: `C:/Users/98217/AppData/Local/Temp/architecture-review-20260909-004900.html`
  - [Keep SshConfig + SshRemoteProjects; RemoteControl status-only](https://github.com/Skymly/CodexSharp/issues/83) — keep parse + persist modules; ProjectStore generic metadata; no CreateSsh; RemoteControl stays REM-01 status-only; no Settings chrome extract; no unofficial ssh/* RPC
  - Spec: [Architecture spec: no C5 implement slices](https://github.com/Skymly/CodexSharp/issues/84) — S1–Sn empty; no ready-for-agent debt
- Retro:
  - Went well: two named IDs shipped as two vertical PRs (REM-02 Host picker + persist, tunnel underDevelopment; EXT-04 oauth/login lock + bearer env); Settings copy never 已配对; remoteControl stays errored/disabled; empty architecture pool legal after those IDs; Council replaced HITL.
  - Keep: SshConfig as parse module; SshRemoteProjects over generic ProjectStore metadata; HonestStubs ssh_remote distinct from remote; mcp_oauth notConfigured; HTTP MCP bearer_token_env_var; no MainView chrome extract; fake IModelClient; workspace-write.
  - Change later (not this cycle): live SSH tunnel / remote app-server; MainView.fs is still wide; OAuth refusal still a host switch plus catalog row.
  - Do not: reopen #78/#79; fold SSH into RemoteControl; implement official Remote relay or GIT-08; mark REM-01 connected; implement cloud OAuth; mint ssh/* RPC; start a sixth cycle in the same hop as this retro; Electron; steal codex://.
- Verification: feature PRs 80 and 81 green on windows-latest. No open ready-for-agent. Named feature S1–Sn were not empty.

### C6 本地图像生成 — accepted

- Destination: Goal Named cycle C6 — ART-03 (`docs/DESKTOP_REQUIREMENTS.md`)
- Feature:
  - Map: [[C6] Local image generation](https://github.com/Skymly/CodexSharp/issues/86)
  - Spec: [[C6] Spec: local image generation](https://github.com/Skymly/CodexSharp/issues/88)
  - S1 [[C6][ART-03] Local image generation](https://github.com/Skymly/CodexSharp/issues/89) — [PR 90](https://github.com/Skymly/CodexSharp/pull/90)
- Architecture:
  - Map: [[C6] Architecture map](https://github.com/Skymly/CodexSharp/issues/91)
  - Chart: `C:/Users/98217/AppData/Local/Temp/architecture-review-20260909-201422.html`
  - [Keep IImagesClient; save in tool; empty architecture pool](https://github.com/Skymly/CodexSharp/issues/92) — keep IImagesClient (HTTP + fake); tool writes workspace PNG; ART-02 preview; no chrome extract; empty architecture pool
  - Spec: [Architecture spec: no C6 implement slices](https://github.com/Skymly/CodexSharp/issues/93) — S1–Sn empty; no ready-for-agent debt
- Retro:
  - Went well: one named ID shipped as one vertical PR (ART-03 notConfigured-without-key and generate-when-configured); public Images API + user key; workspace PNG; no ChatGPT credits; empty architecture pool legal after that ID; Council replaced HITL; did not pre-write docs/DESKTOP_C6.md.
  - Keep: IImagesClient beside IModelClient; HttpImagesClient + fake adapters; image_gen in F# protocol / C# tool executor; workspace-write save; ART-02 path preview; capabilities/read follows key; fake IModelClient.
  - Change later (not this cycle): image edit; Focused/Canvas; MainView.fs is still wide.
  - Do not: reopen #89; implement Focused/Canvas or image edit; claim ChatGPT quota; mint image/* RPC; extract MainView chrome; start C7 in the same hop as this retro; Electron; steal codex://.
- Verification: feature PR 90 green on windows-latest. No open ready-for-agent. Named feature S1–Sn were not empty.

### C7 本地可视化 — accepted

- Destination: Goal Named cycle C7 — ART-04 (`docs/DESKTOP_REQUIREMENTS.md`)
- Feature:
  - Map: [[C7] Local visualizations](https://github.com/Skymly/CodexSharp/issues/95)
  - Spec: [[C7] Spec: local visualizations](https://github.com/Skymly/CodexSharp/issues/98)
  - S1 [[C7][ART-04] Local visualizations](https://github.com/Skymly/CodexSharp/issues/97) — [PR 99](https://github.com/Skymly/CodexSharp/pull/99)
- Architecture:
  - Map: [[C7] Architecture map](https://github.com/Skymly/CodexSharp/issues/100)
  - Chart: `C:/Users/98217/AppData/Local/Temp/architecture-review-20260909-223041.html`
  - [Keep SystemFilePreview; HonestStubs catalog; empty architecture pool](https://github.com/Skymly/CodexSharp/issues/101) — keep SystemFilePreview (HTML on same predicate); HonestStubs catalog + lock; no FeatureSpec; no chrome extract; empty architecture pool
  - Spec: [Architecture spec: no C7 implement slices](https://github.com/Skymly/CodexSharp/issues/102) — S1–Sn empty; no ready-for-agent debt
- Retro:
  - Went well: one named ID shipped as one vertical PR (ART-04 notConfigured and OS-open workspace HTML); no official @Visualize preview; no host; empty architecture pool legal after that ID; Council replaced HITL; did not pre-write docs/DESKTOP_C7.md.
  - Keep: SystemFilePreview as OS-open module; HonestStubs visualize catalog + lock seam; capabilities/read never true for visualize; MainView thin IsOffice/TryOpen caller; existing write + ART-03 PNG; fake IModelClient; workspace-write.
  - Change later (not this cycle): rename IsOffice; MainView.fs is still wide; vendor TUI inline-vis.
  - Do not: reopen #97; claim official @Visualize desktop preview or ChatGPT cloud viz; implement Sites hosting or Focused/Canvas; add VisualizeHost or FeatureSpec; mint visualize JSON-RPC; extract MainView chrome; start C8 in the same hop as this retro; Electron; steal codex://.
- Verification: feature PR 99 green on windows-latest. No open ready-for-agent. Named feature S1–Sn were not empty.

M0/M1/M2 **feature** slices shipped as ordinary Goal slices — not as wayfinder cycles. Do not reopen them. Do not count those feature slices or M2 toward this Goal's K.

## Current cycle

C8 多终端 — implementing. Destination: Goal Named cycle C8 — TERM-02 (`docs/DESKTOP_REQUIREMENTS.md`). Map: [[C8] Multi-tab terminals](https://github.com/Skymly/CodexSharp/issues/104). Spec: [[C8] Spec: multi-tab terminals](https://github.com/Skymly/CodexSharp/issues/107). S1 [[C8][TERM-02] Multi-tab terminals](https://github.com/Skymly/CodexSharp/issues/106) ready-for-agent. Do not pre-write docs/DESKTOP_C8.md. Do not start C9. MX / section 5.2 WON'T remain out of scope.

## Next destinations (if K > 1)

Named cycles on the active Goal (do not substitute §5.1 rows):

- MX / §5.2 WON'T: out of scope
