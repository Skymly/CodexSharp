# Desktop M1 Goal

Paste the **Goal 提示** block into Codex `/goal` (or a new Goal). It is scoped to **M1 only**.
Do not treat this file as a second source of truth; capability IDs and acceptance come from `docs/DESKTOP_REQUIREMENTS.md`.

---

## Goal 提示

```
完成 CodexSharp Desktop 的 M1：在 Windows 上做出可日常编码的本地 Codex 闭环。

权威需求：docs/DESKTOP_REQUIREMENTS.md（冻结 2026-09-07 / ChatGPT 桌面 26.901）。只执行 §5.1 的 M1，对照 §4 P0 差距表。M0 已有能力不得回退。不要做 M2、M3、MX / WON'T。

目标用户路径（验收时必须能走通，且不依赖 ChatGPT 云）：
1. 打开 Desktop，把本机 Git 目录登记为项目并处理 trust。
2. 侧栏按项目/cwd 浏览、搜索、pin、重命名、归档本地 thread。
3. 在 composer 发 turn、停、steer/queue；审批 Allow/Deny；sandbox 默认 workspace-write。
4. 从项目开 Local 或 managed worktree thread；云 worktree 保持 notConfigured。
5. 设置/清除/暂停/恢复 Goal；目标文本在后续 steer 中不丢失。
6. 打开设置能看到 Computer Use / Browser Use / Voice / Remote / Cloud / credits 为未配置，且没有把 stub 标成已启用的假 Enable。

工作方式（每个切片都要遵守）：
- 先用 gh 开一个 issue，再从 origin/master 开短分支，一个 PR 只打一个切片。issue 标题：`[M1][<ID>] 短句`。PR 描述链接 issue，列出 ID、验收、测试、刻意不做。
- 小步、可编译。F# 做协议/agent 核心，C# 做宿主；Desktop 保持 Avalonia.FuncUI，不引入 Electron、不拆官方 app.asar、不像素复刻。
- 默认 sandbox 是 workspace-write；测试不得无故放宽。
- 密钥不入库；本地状态只在 ~/.codexsharp（可用 CODEXSHARP_HOME）。诚实 stub 禁止变成假成功。
- 做完一个切片就停下来开下一个 issue；不要在同一 PR 里顺手做 P1。发现范围膨胀时暂停 Goal，写清 blocker。

测试分层（必须分开，禁止把活模型塞进默认测试套件）：

A. 回归 / CI / `./build.ps1 Test`
   - Agent loop 与协议测试只用假 IModelClient，禁止活网络、禁止打 LM Studio、禁止打云 API。
   - 每个 PR 这一层必须绿。

B. 端到端（本机 LM Studio，不进 CI）
   - 凡是「发 turn / steer / 审批 / Goal 保文 / $skill 调用」的验收，用本机已部署的 LM Studio，不要用 OpenAI 云、不要用 ChatGPT 登录。
   - 默认入口：http://127.0.0.1:1234/v1 ，provider=`lmstudio`，`wire_api=chat`（与 README / 默认 config.toml 一致）。
   - 先探测 `GET http://127.0.0.1:1234/v1/models`。无进程、非 2xx、或 data 为空 → 记「E2E skipped: LM Studio not serving」，该 PR 仍可单测合并；整条 M1 Goal 不得标完成，直到探测成功并跑完下方冒烟。
   - 探测成功后：读返回的模型 id，设 `CODEXSHARP_PROVIDER=lmstudio` 和 `CODEXSHARP_MODEL=<该 id>`（必要时 `CODEXSHARP_BASE_URL=http://127.0.0.1:1234/v1`）。不要把用户正在用的 ~/.codexsharp 改坏：E2E 用隔离目录，例如 `%TEMP%\codexsharp-m1-e2e` 作为 `CODEXSHARP_HOME`。
   - 若客户端强制 env_key：只在该隔离 HOME 或进程环境里放占位（如 `CODEXSHARP_API_KEY=lm-studio`）。禁止写入仓库、禁止提交 auth.json、禁止用云 key。
   - 不要新增默认就会跑的 live HTTP 测试。若做 opt-in 脚本/测试，必须显式开关（如 `CODEXSHARP_E2E_LMSTUDIO=1`），且默认 `dotnet test` / CI 跳过。
   - LM Studio 回复质量不是验收标准；验收是 harness 行为：turn 开始/结束、item 进时间线、Stop/steer、审批框、Goal 文本仍在、sandbox 未放宽、stub 仍诚实。

M1 收尾冒烟（隔离 HOME + LM Studio，Desktop 或 `codexsharp exec` 均可；Desktop 切片优先点 UI）：
- 登记本仓库为项目，处理 trust。
- 新 Local thread，composer 发一句短任务，看到流式/完成 item。
- 运行中 Stop，或再发一条 steer/queue。
- `/goal` 设目标 → 再发一轮 → 目标文本仍在 → pause/clear。
- 若当前切片已做 worktree：开一条 worktree thread，确认 cwd 隔离且 `cloudWorktree=notConfigured`。
- 若当前切片已做 `$` skill：输入 `$` 能列出受信任 skill。
- 设置页仍显示 CU/Browser/Voice/Remote/Cloud 未配置。

切片顺序（一次只做一行；P0 诚实 stub 只锁展示，不实现能力）：

1) AUTO-04 Goal UX
   Issue：持久 Goal 状态机 + Desktop/TUI 可设/暂停/恢复/清除。
   要做：thread/goal/set|get|clear 支持 status=active|paused|cleared；resume thread 后仍在；composer 显示当前 objective/status（完整进度条属 P1，本切片不做）；活动 Goal 时 steer 不得丢掉目标文本。
   现状：CodexSession.Goal 仅内存，Desktop 只有 /goal 文本命令。
   测试：假客户端覆盖 set/pause/resume/clear、resume 持久化、steer 保文。有 LM Studio 时加一轮真实 steer 保文冒烟。

2) SET-03 + EXT-08 诚实设置页，顺手锁 COMP-03 云斜杠
   Issue：设置列出未配置能力；/cloud* /pet /cloud-environment 诚实失败。
   要做：Computer Use、Browser Use、Voice/realtime、Remote、Cloud thread/worktree、credits、code_mode_host、Guardian、windowsSandbox 显示 notConfigured/underDevelopment；禁止 Enable 假按钮；/pet 说明无 overlay（VPM-03 WON'T）。
   不要做：真 CU/浏览器/Voice/Remote。
   测试：不需要活模型。

3) NAV-02 + LIFE-01 侧栏项目闭环
   Issue：本地项目 + 线程侧栏可日常用。
   要做：创建/选择/删除项目；thread 写入主 cwd；This folder / All folders；list/pin/archive/rename/search 按项目或 cwd 过滤；未信任走现有 ProjectTrust。
   不要做：NAV-01 三模式壳、NAV-03 Activity、云 pin 同步、多文件夹项目（LIFE-02 P2）。
   可带：项目「在资源管理器打开」。完整「Open in 编辑器」handler 放到 P1。
   测试：假数据即可。有 LM Studio 时用隔离 HOME 开一条真实 thread，确认它出现在对应项目侧栏。

4) LIFE-05 worktree 生命周期（P0 启动 + 绑定/清理）
   Issue：Git 项目能开隔离 worktree thread，并绑到 chat、可清理。
   要做：thread/worktree/start 已有路径补：thread 元数据绑定 worktree 路径、列表可见、结束后 remove managed worktree；cloudWorktree 永远 notConfigured。
   不要做：Handoff（GIT-07 P1）、永久 worktree / .worktreeinclude（GIT-09 P1）、跨主机（GIT-08 P2）。
   测试：GitProbe/WorktreeSession 单测 + 有 LM Studio 时在 worktree cwd 发一轮短 turn。

5) COMP-04 composer `$` skill
   Issue：受信任 skill 可在 composer 用 $name 显式调用。
   要做：$ 过滤已启用 skill；插入/触发走现有 load_skill；未信任目录不出现项目 skill。
   不要做：官方插件目录、MCP OAuth。
   测试：假客户端覆盖过滤/trust。有 LM Studio 时 `$` 选一个 skill 再发 turn。

6) SAND-02 回归锁
   Issue：默认 workspace-write + 路径策略测试钉死。
   要做：确认默认仍是 workspace-write；workspace-write 拒工作区外写入；设置/UI 不得把路径策略说成 OS elevated sandbox（SAND-03 保持 stub）。
   不要做：Windows sandbox setup（P1）、放宽测试 sandbox。
   测试：路径策略必须在假客户端下失败（不放宽）。不要用 LM Studio 当越界写入的唯一断言。

完成标准（全部满足才可把 Goal 标完成）：
- 上列 6 个 issue 都有已合并 PR。
- `./build.ps1 Test` 绿（假客户端，无活网络）。
- LM Studio 探测成功，并在隔离 `CODEXSHARP_HOME` 下走完「M1 收尾冒烟」。
- README Honest stubs 与设置页一致；无假成功。
- 未实现 M2/M3/WON'T 项。

停止条件：
- M1 完成后停止，等用户另开 M2 Goal。
- 需要改 MoSCoW、做云能力、或把 stub 变成真实现时，暂停并问用户。
- LM Studio 未在 1234 监听时：单测 PR 可继续；不要改去打云 API 凑 E2E；Goal 保持未完成并写明探测结果。
```

---

## 本机 LM Studio 怎么接（Goal 外备忘）

默认 config 已有 provider，不必为 E2E 改仓库样例密钥：

```toml
[model_providers.lmstudio]
name = "LM Studio"
base_url = "http://127.0.0.1:1234/v1"
env_key = "CODEXSHARP_API_KEY"
wire_api = "chat"
```

隔离跑一次（PowerShell）：

```powershell
$env:CODEXSHARP_HOME = Join-Path $env:TEMP "codexsharp-m1-e2e"
$env:CODEXSHARP_PROVIDER = "lmstudio"
$env:CODEXSHARP_BASE_URL = "http://127.0.0.1:1234/v1"
$env:CODEXSHARP_API_KEY = "lm-studio"   # 占位，不是云密钥
# 先 GET /v1/models，再：
$env:CODEXSHARP_MODEL = "<lmstudio-model-id>"
dotnet run --project src/CodexSharp.Desktop
```

`GET http://127.0.0.1:1234/v1/models` 失败 = 本机没在 serve，跳过 E2E，不要改 `model_provider = openai`。

---

## 切片 → issue / PR

| # | IDs | Issue 标题 | 合理 PR 范围 | 刻意不做 |
|---|---|---|---|---|
| 1 | AUTO-04 | `[M1][AUTO-04] Persist and pause thread goals` | Protocol + Runtime + App Server + Desktop/TUI + 假客户端测试 | 进度条像素、Scheduled、CI 打 LM Studio |
| 2 | SET-03, EXT-08, COMP-03 | `[M1][SET-03] Honest stub settings and cloud slashes` | 设置页 + `/cloud*` `/pet` 诚实失败 + 测试 | 真 CU/浏览器/Voice |
| 3 | NAV-02, LIFE-01 | `[M1][NAV-02] Project-scoped thread sidebar` | Desktop 侧栏 + project APIs 若缺再补 + 测试 | Activity、三模式、多根项目 |
| 4 | LIFE-05 | `[M1][LIFE-05] Bind and clean managed worktrees` | WorktreeSession + thread 元数据 + Desktop + 测试 | Handoff、`.worktreeinclude` |
| 5 | COMP-04 | `[M1][COMP-04] Composer $skill picker` | Desktop composer + trust 过滤 + 测试 | 官方 marketplace |
| 6 | SAND-02 | `[M1][SAND-02] Lock workspace-write defaults` | 默认值 + 越界写入测试 + UI 文案 | Windows sandbox setup |

建议分支名：`m1/auto-04-goal-ux`、`m1/set-03-honest-stubs`、`m1/nav-02-sidebar`、`m1/life-05-worktree`、`m1/comp-04-skill-dollar`、`m1/sand-02-workspace-write`。

Epic（可选，先开再挂子 issue）：

```
[M1] Codex desktop closed loop on Windows
```

正文只需链到本文件和 `docs/DESKTOP_REQUIREMENTS.md` §5.1。

---

## 后续 Goal（现在不要贴）

M1 合并后再各开一条，仍然一条 Goal 只打一个阶段：

**M2** — 协作与调度（P1）：Handoff、Review 面板、PR+`gh`、local env、Scheduled 本地、`codex://`、命令面板、通知、Office/PDF 预览、Windows sandbox setup。

**M3** — 外围表面（P2，可选）：内置浏览器、Computer Use、Voice、图像生成、多文件夹、side chat、无云 Chat/Work 壳、SSH。

**MX** — 永不做：Pets overlay、Codex Micro、Sites 托管、Computer History、ChatGPT 云同步/Cloud thread、企业 MDM、官方插件目录、Electron 像素克隆、事件触发 Scheduled。
