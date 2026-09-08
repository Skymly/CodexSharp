# Desktop M2 Goal

M1 is done (issues #1–#11 closed, PRs #2–#12 merged, tip `0067f6e`).
Paste the **Goal 提示** into `/goal`. Capability IDs and acceptance come from `docs/DESKTOP_REQUIREMENTS.md` §5.1 M2 and §4 P1.

Do not reopen M1. Do not start M3 / MX.

---

## Goal 提示

```
完成 CodexSharp Desktop 的 M2：Windows 上的本地协作与调度，不接 ChatGPT 云。

权威需求：docs/DESKTOP_REQUIREMENTS.md（冻结 2026-09-07 / 26.901）。只执行 §5.1 **M2**。对照 §4 P1 里本 Goal 点名的 ID。M0/M1 已有能力与诚实 stub 不得回退。不要做 M3、MX / WON'T。

仓库现状（起点，不要重做）：
- master @ 0067f6e。M1 六刀已合：Goal 持久化、诚实设置页、项目侧栏、worktree 绑定清理、composer `$skill`、workspace-write 路径策略。
- 已有但未闭环：`review/start` + Review 按钮/评论轨 +「Insert gh pr create」；toast/OSC9；`windowsSandbox/setupStart` unelevated 只写 Job Object 状态，elevated 仍 notConfigured。
- 没有：命令面板、深链、Activity 收件箱、Handoff、本地 Scheduled、local env setup 脚本、系统 Office/PDF 预览、gh 拉 PR 评论。

目标用户路径（不登录 ChatGPT，走通才算 M2 完成）：
1. Ctrl+K 命令面板：新建 thread、跳转 thread、打开设置。
2. `codexsharp://threads/<id>` 和 `codexsharp://settings` 打开已有窗；未知路径只开主窗、不执行 path。
3. turn 完成或待审批时有 Windows 通知；Activity 能列出运行中 / 待审批 / 本地未读。
4. Review 选 uncommitted 或 vs base branch；有 `gh auth` 时能拉当前分支 PR 评论，没有 gh 时说明原因。
5. 聊天头 Hand off：Local ↔ 该 thread 绑定的 worktree，中断进行中的 turn，遵守一分支一 checkout。Cloud handoff 保持 notConfigured。
6. 新 worktree 若项目有 setup 脚本则自动跑，失败日志进 thread。
7. 创建一个「每 N 分钟」绑当前 thread 的 Scheduled；app 在跑时触发 `turn/start`；app 没在跑时跳过并记录，禁止静默成功。事件触发 Scheduled 不做。

工作方式：
- 先 `gh issue create`，再从 origin/master 开短分支。一个 PR 只打一个切片。标题：`[M2][<ID>] 短句`。PR 描述链接 issue，写验收、测试、刻意不做。
- 小步可编译。F# 协议/agent 核心，C# 宿主；Desktop 保持 Avalonia.FuncUI。不引入 Electron、不拆 app.asar、不像素复刻。
- 默认 sandbox 仍是 workspace-write；测试不得无故放宽。密钥不入库。本地状态只在 ~/.codexsharp（可用 CODEXSHARP_HOME）。诚实 stub 禁止变成假成功。
- 做完一个切片再开下一个 issue。不要在同一 PR 顺手做本 Goal 未点名的 P1，更不要做 P2。范围膨胀就暂停 Goal。

测试分层：
A. `./build.ps1 Test` / CI：假 IModelClient only。禁止活网络、禁止打 LM Studio、禁止打云。每个 PR 必须绿。
B. 端到端（本机 LM Studio，不进 CI）：
   - 需要发 turn 的切片（Review、Handoff 后的继续、Scheduled 触发）用 http://127.0.0.1:1234/v1 ，`CODEXSHARP_PROVIDER=lmstudio`，`wire_api=chat`。
   - 先 GET /v1/models。失败则记「E2E skipped: LM Studio not serving」；单测 PR 可合；整条 M2 Goal 不得标完成。
   - 成功则用隔离 `CODEXSHARP_HOME`（如 %TEMP%\codexsharp-m2-e2e），`CODEXSHARP_MODEL=<id>`，占位 `CODEXSHARP_API_KEY=lm-studio`。禁止改用户日常 ~/.codexsharp，禁止提交 auth。
   - 不要把 live HTTP 测默认挂进 dotnet test。opt-in 必须 `CODEXSHARP_E2E_LMSTUDIO=1`。
   - 验收看 harness：面板打开、通知弹出、handoff 后 cwd、Scheduled 写了 run 记录；不看 LM Studio 文笔。

切片顺序（一次一行）：

1) NAV-05 命令面板 + COMP-06 Windows 常用键
   要做：Ctrl+K（或 Ctrl+Shift+P）打开命令菜单；动作至少：new thread、open settings、跳转已有 thread、focus composer。Ctrl+N 新 thread、Ctrl+, 设置、Ctrl+` 终端、Ctrl+B 侧栏。Ctrl+K 不得清终端。
   不要做：可重绑快捷键表（COMP-06 完整版留到以后）、NAV-01 三模式。
   测试：不需要活模型。

2) COMP-07 深链
   要做：注册 `codexsharp://`（threads/new、threads/<id>、settings、skills）。未知路径打开主窗并忽略。query 的 path 必须校验，不得执行未校验路径。官方包若已注册 `codex://`，默认不要抢；不要为 pets/cloud 做深链。
   不要做：插件安装深链、与官方并存时静默覆盖协议。
   测试：解析器单测 + 手工一条深链。不需要活模型。

3) SET-02 + NAV-03 通知与本地 Activity
   要做：Windows 上 turn 完成 / 待审批可发系统通知（或明确的桌面 toast，设置里可关）。Activity 列表：运行中、等待审批、本地未读；可跳到对应 thread。无云 Scheduled 筛选项。
   现状：OSC9/BEL + FuncUI「Turn completed」toast，不是 OS 通知，也没有收件箱。
   不要做：云 Activity、Mark all as read 的云同步、Pets。
   测试：假事件即可。有 LM Studio 时跑一轮 turn 看通知。

4) GIT-02 + GIT-05 Review 面板与 gh PR
   要做：Desktop Review 面板能选 uncommittedChanges / baseBranch（已有 review/start 目标）；展示评论轨。检测 `gh`：已 auth 则拉当前分支 PR 评论到侧栏；未安装或未登录则显示原因，不接 ChatGPT GitHub 云应用。「Insert gh pr create」可保留，但不得假装已创建 PR。
   不要做：GIT-03 编辑器行内批注、GIT-04 hunk stage/revert、GIT-06 多仓、官方 GitHub app。
   测试：假 review/start + gh 缺失路径。有 LM Studio 且有 gh 时拉一条真实 PR 评论。

5) GIT-07 Handoff Local ↔ Worktree
   要做：聊天头 Hand off。把该 thread 的 Git 工作区在项目 Local cwd 与 M1 已绑定的 managed worktree 之间迁移；中断进行中的回复。一分支一 checkout。Cloud / 跨主机 = notConfigured。
   不要做：GIT-08、GIT-09 永久 worktree。
   测试：Git 夹具单测（假模型）。有 LM Studio 时 handoff 后再发一轮，确认 cwd。

6) TERM-04 local env + ART-01 系统预览 + SAND-03 setup UX
   要做：
   - 读项目 `.codex` 或 `.codexsharp` 的 Windows setup 脚本，新 worktree 自动执行；失败时日志进 thread，不要假装成功。
   - 时间线里的 xlsx/pdf/docx/pptx 用系统默认应用打开，并给出本地路径；打不开就只给路径。不上传云、不做内嵌 Office 渲染器。
   - Desktop 设置把 windowsSandbox 说清楚：elevated 永远 notConfigured；unelevated 只表示 Job Object kill-on-close + 路径策略，不得写成「已隔离 / official Windows sandbox」。
   不要做：真 elevated helper、内嵌 PDF 引擎、TERM-02 多终端 tab。
   测试：脚本失败路径单测 + sandbox 文案断言。不需要活模型。

7) AUTO-01 + AUTO-02 本地 Scheduled
   要做：状态在 ~/.codexsharp（隔离 HOME 可测）。两种：独立任务每次新 thread；聊天内任务对已有 threadId 做定时 `turn/start`。实现优先 **app 内 heartbeat**（不要先装 Windows 计划任务）。app 未运行则跳过并写失败/skipped 记录，禁止静默成功。Desktop 能创建、列出、取消。分钟级跟进即可，不必上完整 RRULE。
   不要做：AUTO-03 事件触发（Gmail/Slack/GitHub；官方桌面也没有）、开机无 app 仍碰文件、云 Scheduled 收件箱。
   测试：假时钟/heartbeat 单测。有 LM Studio 时建一条 1 分钟聊天内任务，等到一次 turn。

本 Goal 明确不做（P1 但不是 §5.1 M2 这一刀）：
SAND-07 防休眠，GIT-03 行内评论，GIT-04 hunk，GIT-09 `.worktreeinclude`，TERM-02/03/05/06，EXT-07 memories UX，LIFE-09 完整导入向导，REM-03，SET-01 设置信息架构，COMP-06 可重绑。

完成标准：
- 7 个 issue 都有已合并 PR。
- `./build.ps1 Test` 绿。
- LM Studio 探测成功，隔离 HOME 走完：命令面板、一条深链、一次通知、一次 Review、一次 Handoff、一条 Scheduled 触发。
- 无 ChatGPT 登录、无假成功 stub、未实现 M3/WON'T。

停止条件：
- M2 完成后停止，等用户开下一 Goal。
- 要做 elevated Windows sandbox、抢官方 `codex://`、或事件触发 Scheduled 时暂停并问用户。
- LM Studio 未监听：单测可继续，禁止改打云 API 凑 E2E。
```

---

## 切片 → issue / PR

| # | IDs | Issue 标题 | 合理 PR 范围 | 刻意不做 |
|---|---|---|---|---|
| 1 | NAV-05, COMP-06 | `[M2][NAV-05] Command palette and Windows shortcuts` | Desktop 快捷键 + 面板调 App Server | 可重绑、三模式 |
| 2 | COMP-07 | `[M2][COMP-07] Register codexsharp:// deep links` | 协议解析 + Windows 注册 + 打开 thread/settings | 抢官方 `codex://`、pets |
| 3 | SET-02, NAV-03 | `[M2][NAV-03] Local activity inbox and OS notify` | 通知 + 本地收件箱 | 云 Activity、Pets |
| 4 | GIT-02, GIT-05 | `[M2][GIT-02] Review panel and gh PR comments` | Review 面板 + `gh` 探测/评论 | 行内批注、多仓、GitHub 云应用 |
| 5 | GIT-07 | `[M2][GIT-07] Handoff Local ↔ worktree` | git 迁移 + 中断 turn + Desktop 按钮 | 跨主机、Cloud |
| 6 | TERM-04, ART-01, SAND-03 | `[M2][TERM-04] Worktree setup, file open, sandbox copy` | setup 脚本 + ShellOpen + 诚实 sandbox 文案 | elevated sandbox、内嵌预览 |
| 7 | AUTO-01, AUTO-02 | `[M2][AUTO-01] Local heartbeat scheduled tasks` | 本地状态机 + heartbeat + Desktop CRUD | 事件触发、系统计划任务 |

建议分支：`m2/nav-05-palette`、`m2/comp-07-deep-links`、`m2/nav-03-activity`、`m2/git-02-review-gh`、`m2/git-07-handoff`、`m2/term-04-env-preview`、`m2/auto-01-scheduled`。

Epic：`[M2] Local collaboration and scheduling on Windows`

---

## 相对 M1 的变化

- M1 是「能在本机编码」。M2 是「能协作、能被通知、能定时接着干」。
- Scheduled 用 app 内 heartbeat，承认「桌面没开就跳过」，比装系统计划任务更诚实，也更适合一个 PR。
- `codexsharp://` 不抢官方 `codex://`。
- 其余 P1（多终端、行内评论、防休眠、导入向导…）不进本 Goal。

---

## 后续（现在不要贴）

**M2 补刀 / M2.5**（其余 P1）：GIT-03/04/09、TERM-02/03/05/06、SAND-07、EXT-07、LIFE-09、SET-01。

**M3**（P2）：内置浏览器、Computer Use、Voice、图像生成、多文件夹、side chat、无云 Chat/Work 壳、SSH。

**MX**：Pets、Micro、Sites、Computer History、ChatGPT 云、MDM、官方插件目录、Electron 克隆、事件触发 Scheduled。
