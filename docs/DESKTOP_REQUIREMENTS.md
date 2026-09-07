# Codex 桌面端需求文档（ChatGPT Desktop 26.901）

本文是 CodexSharp 的**能力级**产品需求，不是 UI 像素规格，也不是官方协议抄本。官方产品在 26.707 起为 **ChatGPT 桌面端（含 Codex 模式）**；CodexSharp 仍是独立 harness，不实现 ChatGPT 云账号体系。

---

## 1. 元信息

| 项 | 值 |
|---|---|
| 文档类型 | 单文件 PRD + 差距表 |
| 调研日期（冻结） | 2026-09-07 |
| 基线安装包 | Windows `OpenAI.Codex 26.901.6511.0`（MSIX Identity `OpenAI.Codex`） |
| 内部 AppVersion | `26.901.51231`（界面 DisplayName：**ChatGPT**） |
| 本机旁证路径 | `C:\Program Files\WindowsApps\OpenAI.Codex_26.901.6511.0_x64__2p2nqsd0c76g0` |
| 官方合并点 | changelog **26.707**（2026-07-09）：Codex 并入 ChatGPT 桌面端 |
| 实现架构（不变） | Avalonia.FuncUI 客户端 + App Server；F# 协议/agent 核心，C# 宿主 |
| 本地状态 | `~/.codexsharp`（可用 `CODEXSHARP_HOME` 覆盖） |
| 默认 sandbox | `workspace-write`；测试不得无故放宽 |
| 密钥 | 不写入仓库或本文；凭据只走环境变量 / `auth.json` |
| 非官方声明 | CodexSharp 是独立实现，**不是** OpenAI 官方客户端；不拆 `app.asar`、不像素复刻 Electron |

### 1.1 调研范围与方法

只读顺序：官方文档（`learn.chatgpt.com`；`developers.openai.com/codex/*` 现重定向到同一站点）→ changelog / what’s new → 本机安装包清单 → 本仓库 `docs/SOURCE_MAP.md` 与 Desktop 现状。

本机包只作**能力旁证**，不当作协议真相。bundled plugins：`browser`, `chrome`, `computer-use`, `deep-research`, `latex`, `sites`, `unified-computer-use`, `user-writing`, `visualize`, `codex-app-tools`。内置 skills：`hatch-pet`, `onboard-new-user`。协议 `codex://`；关联 `.csv` `.docx` `.pptx` `.tsv` `.xls` `.xlsm` `.xlsx` 与 `.skill`；入站端口 1455/1457。

### 1.2 术语

正文用中文；下列官方词保留英文：thread、worktree、handoff、composer、sandbox、skills、plugins、MCP、Goal、Scheduled、Remote、Appshots、Computer Use、Computer History、Codex Micro、Sites、Visualizations、Quick chat。

| 词 | 含义 |
|---|---|
| Chat | ChatGPT 普通对话（含 Quick chat / temporary chat） |
| Work | ChatGPT Work：交付文档/分析/浏览器工作，可本地或云 |
| Codex | 编码 agent：Local / Worktree / Cloud |
| thread | 一条可恢复的聊天/任务（官方 UI 常称 chat / task） |
| 诚实 stub | 暴露方法或 UI，但返回 `notConfigured` / `underDevelopment`，禁止假装成功 |
| 本地替代 | 不接 ChatGPT 云时，用 API key、本地市场、本地文件做到什么 |

### 1.3 现状取值

`有` = Desktop 或 App Server 已可用；`部分` = 有主路径但缺官方深度；`无` = 未做；`诚实 stub` = 有 API/UI 字样但明确未配置。

### 1.4 MoSCoW 锁定（实现者勿改优先级）

| 档 | 优先级 | 范围 |
|---|---|---|
| **MUST** | P0 | 侧栏项目+线程、composer、时间线、审批、sandbox、本地/worktree、基础 diff、单终端、本地 skills/MCP/plugins、附件/@file、trust、缺失能力诚实 stub |
| **SHOULD** | P1 | Handoff、local environments、本地 scheduled tasks、多终端、PR review、Windows sandbox setup、`codex://`、命令面板、Office/PDF 预览、通知 |
| **SHOULD** | P2 | 内置浏览器+browser use、computer use、图像生成、Voice、真实 remote pairing、多文件夹项目、side chat/浮动窗、Chat/Work 壳（无官方云） |
| **WON'T** | P3 | Pets、Codex Micro、Sites 官方托管、Computer History、ChatGPT 云同步/Cloud thread、企业 MDM、官方插件目录、像素级 Electron 克隆 |

WON'T 仍必须写官方行为并标非目标。Windows 为第一实现平台；macOS / Linux 只记录官方差异。

---

## 2. 产品地图

### 2.1 三模式

官方 ChatGPT 桌面端是一个壳，可切换：

1. **Chat**：快速问答。Quick chat（macOS `Cmd+Option+N` / Windows `Ctrl+Alt+N`）打开普通 ChatGPT 对话，不进入 Codex 侧栏。temporary chat（`Cmd+Shift+N` / `Ctrl+Shift+N`）不进历史。
2. **Work**：面向交付物（文档、表格、演示、研究）。**本地 Work**可读本机文件/应用；**Cloud Work**在 OpenAI 托管隔离环境跑，并可跨 web/mobile/desktop 同步。
3. **Codex**：编码 agent。新 chat 可选：
   - **Local**：当前项目目录；
   - **Worktree**：Git worktree 隔离；
   - **Cloud**：已配置的云环境。

Local 与 Worktree 都在用户机器上跑。Cloud 依赖 ChatGPT 账号，**不在 CodexSharp 实现范围**。模式快捷键：macOS Control+1/2/3，Windows/Linux Alt+1/2/3。

### 2.2 项目与聊天

官方 **Projects** 视图同时包含：

- **ChatGPT 项目**：上传/连接的 sources + 项目指令；不直接挂本机文件夹。
- **本地项目**：一或多个本机文件夹。主文件夹决定 Git、`AGENTS.md`、skills、`config.toml` 自动发现；次文件夹可读写/搜索，但不自动发现那些项目文件。Remote 项目目前**单文件夹**。

组织动作：pin 项目、pin 聊天、重命名、搜索（标题/正文/Git 分支名）、归档（Settings > Archived chats 恢复）。Pin 只改侧栏位置，不增加模型上下文。

Codex CLI/IDE 没有 ChatGPT Projects 视图：CLI 以启动目录为项目，IDE 以打开的 workspace 为项目。

### 2.3 worktree 与 handoff

- Codex-managed worktree 建在 `$CODEX_HOME/worktrees`，从所选分支 HEAD 出发，**detached HEAD**。
- 若起始分支有未提交改动，会应用到 worktree。
- **Handoff** 在 Local 与 Worktree（及跨主机匹配项目）之间移动 chat 与 Git 状态；同一分支不能同时在两处 checkout。
- `.worktreeinclude` 把被 ignore 的本地文件拷进 managed worktree；`AGENTS.override.md` 会自动拷贝。
- 永久 worktree 从项目菜单创建，作为独立项目，不自动删除。
- 定时任务可在 Git 仓上使用后台 worktree，以免改用户正在编辑的工作区。

### 2.4 CodexSharp 本地替代原则

| 官方依赖 | CodexSharp |
|---|---|
| ChatGPT 账号 / 云同步 / Cloud thread | 不实现。API key 或本地兼容 endpoint；云方法返回 `notConfigured` |
| 官方插件目录 / openai-curated | 本地 `~/.codexsharp/plugins` 与本地 marketplace |
| ChatGPT credits / 用量仪表 | 诚实空/本地估算；不伪造成官方额度 |
| Chat / Work 云产品 | 写入清单；P2 最多做无云壳 |
| Remote 扫码中继 | 不接官方中继；可保留 App Server `ws://` 与 `remoteControl` 状态 stub |
| 企业 MDM / `requirements.toml` 强制 | WON'T |
| Electron 壳 / `app.asar` | WON'T。保持 Avalonia.FuncUI |

---

## 3. 能力目录

每条同一张表。验收句式：「用户能… / 系统必须…」。平台列只写 macOS/Windows/Linux 差异；未写则官方三端同行为（Linux preview 可能滞后）。

### 3.1 壳与导航

#### NAV-01 三模式切换（Chat / Work / Codex）

| 字段 | 内容 |
|---|---|
| 官方行为 | 应用内切换 Chat、Work、Codex；可把 Codex 设为默认视图；macOS 可保留 Codex 图标。 |
| 平台 | macOS / Windows 完整；Linux preview 有 Codex，部分能力（Computer Use）未到。 |
| CodexSharp 现状 | 无（仅 Codex harness 壳） |
| MoSCoW | SHOULD / P2（Chat/Work 壳，无官方云） |
| 本地替代 | 不实现 ChatGPT Chat/Work 云产品；若做壳，只切换「对话 / 交付物 / 编码」本地布局。 |
| 验收 | 用户能在本地三种布局间切换且不要求 ChatGPT 登录。系统必须不把未实现的云 Chat/Work 显示为已连接。 |

#### NAV-02 侧栏：项目、线程、pin、搜索

| 字段 | 内容 |
|---|---|
| 官方行为 | 侧栏列出项目与 chats；pin 项目/聊天；搜索标题、正文、Git 分支；未读标记；「下一个需要注意的 chat」。 |
| 平台 | 桌面全平台；与 iOS 统一 pinned chats（需 ChatGPT 账号）。 |
| CodexSharp 现状 | 部分：项目列表、线程 list/pin/archive/rename/search、This folder / All folders。 |
| MoSCoW | MUST / P0 |
| 本地替代 | 项目与 thread 存在 `~/.codexsharp`；pin 本地；不与 iOS 云同步。 |
| 验收 | 用户能创建/选择项目、浏览/pin/搜索本地 thread。系统必须按项目或 cwd 过滤，且不依赖云。 |

#### NAV-03 Activity 视图

| 字段 | 内容 |
|---|---|
| 官方行为 | 侧栏铃铛打开 Activity：未读、运行中、等待回复；可筛 Work/Chat/Pinned/Scheduled；Mark all as read。快捷键 macOS Cmd+Option+U，Windows Ctrl+Alt+U。changelog 26.727 引入。 |
| 平台 | 桌面；Linux 随 preview。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P1（通知相关） |
| 本地替代 | 本地未读/运行中/待审批列表，不接云 Scheduled。 |
| 验收 | 用户能打开本地 Activity 看到待审批与已完成 thread。系统必须在无云时仍列出本机状态。 |

#### NAV-04 弹出窗、Always on top、侧栏/底栏

| 字段 | 内容 |
|---|---|
| 官方行为 | 把当前 chat 弹出独立窗口；Always on top；切换侧栏；Codex 可切换 bottom panel。 |
| 平台 | 桌面；Linux 原生 Wayland 下浮动窗/焦点/快捷键可能不完整。 |
| CodexSharp 现状 | 无（单窗 FuncUI） |
| MoSCoW | SHOULD / P2 |
| 本地替代 | 可选第二窗盯一个 thread；不做 Electron BrowserWindow 复刻。 |
| 验收 | 用户能把一个 thread 放到独立窗并保持可见。系统必须不因此复制会话状态出错。 |

#### NAV-05 命令面板

| 字段 | 内容 |
|---|---|
| 官方行为 | Cmd/Ctrl+K 或 Cmd/Ctrl+Shift+P 打开命令菜单（不是清终端；清终端是 Ctrl+L）。 |
| 平台 | 三端官方均有；Linux Wayland 实验下快捷键可能失效。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P1 |
| 本地替代 | 调已有 App Server 方法（new/archive/settings/skills）。 |
| 验收 | 用户能用命令面板执行新建 thread、打开设置、跳转 thread。系统必须不把 Ctrl+K 绑成清终端。 |

#### NAV-06 文件树与多面板

| 字段 | 内容 |
|---|---|
| 官方行为 | Codex 可切换 file tree、review 面板、browser 面板、终端。 |
| 平台 | Codex 模式。 |
| CodexSharp 现状 | 部分：diff rail、单终端、时间线；无 file tree / 多面板布局。 |
| MoSCoW | MUST / P0（时间线+基础 diff+单终端）；其余 P1/P2 |
| 本地替代 | 现有三栏即可满足 P0。 |
| 验收 | 用户能同时看到线程列表、时间线、composer。系统必须在无 file tree 时仍能用 @file 引用。 |

#### NAV-07 像素级 Electron 克隆

| 字段 | 内容 |
|---|---|
| 官方行为 | Chromium/Electron 壳，ChatGPT.exe + 内嵌 Codex.exe。 |
| 平台 | Windows Store MSIX；macOS dmg；Linux deb/rpm preview。 |
| CodexSharp 现状 | 无（刻意） |
| MoSCoW | WON'T |
| 本地替代 | 保持 Avalonia.FuncUI。 |
| 验收 | 系统必须不引入官方 Electron 依赖。用户能在 FuncUI 完成 P0 Codex 工作流。 |

---

### 3.2 项目与聊天生命周期

#### LIFE-01 本地文件夹项目

| 字段 | 内容 |
|---|---|
| 官方行为 | 打开/添加文件夹为本地项目；新 chat 默认主文件夹；Open 用首选编辑器（可按项目覆盖）。 |
| 平台 | 桌面；Windows 可用 Open 关联 VS / VS Code 等。 |
| CodexSharp 现状 | 部分：project list/create/delete、cwd、信任提示。 |
| MoSCoW | MUST / P0 |
| 本地替代 | 项目元数据在 `~/.codexsharp`；打开系统文件夹即可。 |
| 验收 | 用户能把本机目录登记为项目并在其中开 thread。系统必须把主 cwd 写入 thread 元数据。 |

#### LIFE-02 多文件夹项目

| 字段 | 内容 |
|---|---|
| 官方行为 | Edit project → Add folder；设 primary。主仓负责 Git / AGENTS.md / skills / config；次仓可读改/搜索。changelog 26.715。 |
| 平台 | 桌面；Remote 项目仍单文件夹。 |
| CodexSharp 现状 | 部分：All folders 浏览，但不是「一个项目挂多个根」。 |
| MoSCoW | SHOULD / P2 |
| 本地替代 | 可用 extra read roots / `--add-dir` 先满足只读；多根写入列为 P2。 |
| 验收 | 用户能给项目附加第二文件夹并搜索其中文件。系统必须只在主根自动发现 AGENTS.md/skills。 |

#### LIFE-03 ChatGPT 云项目

| 字段 | 内容 |
|---|---|
| 官方行为 | 云端 sources、项目指令、跨设备 chats。 |
| 平台 | web / desktop / mobile，需账号。 |
| CodexSharp 现状 | 无 |
| MoSCoW | WON'T |
| 本地替代 | 本地项目 + `AGENTS.md` + memories 文件。 |
| 验收 | 系统必须对云项目 API 返回 `notConfigured`。用户能不登录完成本地项目工作。 |

#### LIFE-04 thread 生命周期

| 字段 | 内容 |
|---|---|
| 官方行为 | 新建、恢复、归档、取消归档、删除、fork、重命名、pin、queue 后续消息、follow-up 选择 steer 或排队。 |
| 平台 | 桌面全平台。 |
| CodexSharp 现状 | 有：list/start/resume/archive/fork/rename/search/queue/pin；steer。 |
| MoSCoW | MUST / P0 |
| 本地替代 | JSONL thread store。 |
| 验收 | 用户能新建、恢复、归档、fork、重命名、排队消息。系统必须把 transcript 持久化到 `~/.codexsharp`。 |

#### LIFE-05 本地 worktree 启动

| 字段 | 内容 |
|---|---|
| 官方行为 | 新 Codex chat 选 Worktree；`/worktree`；从 Git 仓创建 managed worktree。 |
| 平台 | 需 Git 仓库。 |
| CodexSharp 现状 | 部分：`thread/worktree/start` 与 Desktop Worktree 按钮；非完整 detached HEAD 管理/清理。 |
| MoSCoW | MUST / P0（能启动隔离 checkout）；完整生命周期 P1 |
| 本地替代 | 本地 git worktree；云 worktree 保持 `notConfigured`。 |
| 验收 | 用户能从 Git 项目开 worktree thread。系统必须不把云 worktree 显示为成功。 |

#### LIFE-06 Cloud thread / cloud worktree

| 字段 | 内容 |
|---|---|
| 官方行为 | `/cloud`、`/cloud-environment`；在托管环境跑，可离开桌面继续。 |
| 平台 | 需 ChatGPT 账号与云环境。 |
| CodexSharp 现状 | 诚实 stub：`notConfigured` |
| MoSCoW | WON'T |
| 本地替代 | 本地 thread + worktree。 |
| 验收 | 用户看到明确未配置。系统必须不创建假云任务。 |

#### LIFE-07 Quick chat / temporary chat / `/task`

| 字段 | 内容 |
|---|---|
| 官方行为 | Quick chat 进 Chat 侧栏；temporary chat 不保存；`/task` 无项目开聊。 |
| 平台 | 桌面 Chat 模式。 |
| CodexSharp 现状 | 无（有 `/new` 无项目 thread） |
| MoSCoW | SHOULD / P2 |
| 本地替代 | 无项目 thread 即 `/task` 的本地等价。 |
| 验收 | 用户能不选项目开一条本地 thread。系统必须不把它同步到 ChatGPT。 |

#### LIFE-08 目录 trust

| 字段 | 内容 |
|---|---|
| 官方行为 | 首次打开文件夹询问是否信任；未信任则限制项目级 skills/hooks/config。 |
| 平台 | 桌面 / CLI / IDE。 |
| CodexSharp 现状 | 有：`ProjectTrust`、Desktop Yes/Skip、未信任跳过项目级 skills/hooks/execpolicy/`.codexsharp/config.toml`。 |
| MoSCoW | MUST / P0 |
| 本地替代 | `projects."<path>".trust_level`。 |
| 验收 | 用户能对 cwd 选信任或跳过。系统必须在未信任时不加载项目级扩展。 |

#### LIFE-09 从其他 agent 导入

| 字段 | 内容 |
|---|---|
| 官方行为 | Settings > Import：Claude Code / Claude Cowork / Cursor；可自动同步。CLI `/import`（Claude Code / Cursor，最近 30 天最多 50 chats）。 |
| 平台 | 桌面 2026-08-11；CLI 同步提供。 |
| CodexSharp 现状 | 部分：Desktop「Import CLAUDE.md」；无完整导入向导。 |
| MoSCoW | SHOULD / P1（指令文件） / P2（完整向导） |
| 本地替代 | 读本地 `CLAUDE.md` / skills 写入 `AGENTS.md`；不上传官方。 |
| 验收 | 用户能把已有 `CLAUDE.md` 变成 `AGENTS.md`。系统必须不删除源 agent 配置。 |

#### LIFE-10 只读 thread snapshot 分享

| 字段 | 内容 |
|---|---|
| 官方行为 | macOS 可分享本地 Codex thread 只读链接；个人链任何人可看，工作区链限同 workspace；会尝试打码密钥。 |
| 平台 | **仅 macOS** + ChatGPT 账号。 |
| CodexSharp 现状 | 无 |
| MoSCoW | WON'T |
| 本地替代 | `/export` 本地 Markdown。 |
| 验收 | 用户能导出本地 Markdown。系统必须不生成官方分享链。 |

---

### 3.3 Composer、斜杠、快捷键、`codex://`

#### COMP-01 composer 发送 / 停止 / 续写

| 字段 | 内容 |
|---|---|
| 官方行为 | 多行输入；可要求 Cmd/Ctrl+Enter 发送；运行中 Stop；follow-up 可 steer 当前 run 或排队下一 run；空 composer 上箭头恢复上次 prompt。 |
| 平台 | 桌面。 |
| CodexSharp 现状 | 有：Send/Stop、queue、steer。 |
| MoSCoW | MUST / P0 |
| 本地替代 | 现有 composer。 |
| 验收 | 用户能发送、停止、在运行中排队或 steer。系统必须把用户消息写入时间线。 |

#### COMP-02 @file、附件、粘贴图

| 字段 | 内容 |
|---|---|
| 官方行为 | @ 提文件/文件夹；附加文件与图片；长粘贴可变附件。 |
| 平台 | 桌面；图像输入全模式。 |
| CodexSharp 现状 | 有：@file、附件、粘贴图、`view_image`。 |
| MoSCoW | MUST / P0 |
| 本地替代 | 本地路径；不走 ChatGPT 文件库。 |
| 验收 | 用户能 @ 工作区内文件并附加图片。系统必须把附件纳入当轮上下文。 |

#### COMP-03 斜杠命令（桌面官方集）

| 字段 | 内容 |
|---|---|
| 官方行为 | 桌面：`/approve` `/cloud` `/cloud-environment` `/compact` `/fast` `/feedback` `/fork` `/goal` `/ide-context` `/init` `/local` `/mcp` `/memories` `/model` `/pet` `/personality` `/plan` `/project` `/reasoning` `/review` `/side` `/status` `/task` `/worktree`。启用的 skills 出现在列表；自定义 prompts 为 `/prompts:<name>`。 |
| 平台 | 因环境/权限变化。 |
| CodexSharp 现状 | 部分：大量 CLI/TUI 斜杠已接到 Desktop；缺官方云类；多出 harness 命令（`/diff` `/sandbox` 等）。 |
| MoSCoW | MUST / P0（核心集）；云命令诚实 stub |
| 本地替代 | `/cloud*` → `notConfigured`；`/pet` WON'T。 |
| 验收 | 用户键入 `/` 能过滤命令。系统必须对 `/cloud` 诚实失败，对 `/compact` `/fork` `/model` `/review` `/worktree` 真正执行。 |

#### COMP-04 `$` 调用 skill

| 字段 | 内容 |
|---|---|
| 官方行为 | Codex 用 `$skill-name` 显式调用；Chat 用 `@`。 |
| 平台 | 桌面 Codex。 |
| CodexSharp 现状 | 部分：skills 列表与加载；composer `$` 体验未对齐。 |
| MoSCoW | MUST / P0（能调用本地 skill） |
| 本地替代 | `~/.codexsharp` 与仓库 `skills/**/SKILL.md`。 |
| 验收 | 用户能显式触发已启用 skill。系统必须只加载受信任目录中的 skill。 |

#### COMP-05 composer vim

| 字段 | 内容 |
|---|---|
| 官方行为 | CLI/桌面均可 vim 编辑草稿（官方 CLI 0.153 起 undo/redo 更完整）。 |
| 平台 | 非 macOS 专属。 |
| CodexSharp 现状 | 有：Desktop 持久 `ComposerVim`（motions/visual/undo，非完整 vim）。 |
| MoSCoW | SHOULD / P1 |
| 本地替代 | 现有实现即可。 |
| 验收 | 用户能在 composer 用 hjkl 与 undo。系统必须不丢失未发送草稿。 |

#### COMP-06 快捷键

| 字段 | 内容 |
|---|---|
| 官方行为 | 完整表见官方 Commands。Windows 与 Linux 接近；macOS 用 Cmd。可在 Settings > Keyboard Shortcuts 搜索命令或按键并重置。Appshots 热键单独配置。 |
| 平台 | Linux Wayland 实验下部分快捷键失效。 |
| CodexSharp 现状 | 部分：少量按钮，无完整可重绑表。 |
| MoSCoW | SHOULD / P1（常用集）；可重绑 P2 |
| 本地替代 | 先做 Windows 默认：Ctrl+N 新 thread、Ctrl+, 设置、Ctrl+` 终端、Ctrl+B 侧栏。 |
| 验收 | 用户能用键盘新建 thread、聚焦 composer、开关终端。系统必须在文档中公布默认绑定。 |

#### COMP-07 `codex://` 深链

| 字段 | 内容 |
|---|---|
| 官方行为 | 兼容保留 `codex://`：`threads/new`、`threads/<id>`、`settings`、`settings/connections/*`、`skills`、`automations`、`plugins/install/...`、`plugins/<id>`、`pets/install?...`。query：`prompt` `path` `originUrl`。Windows 包注册协议 `codex`。 |
| 平台 | Windows/macOS 注册协议；Linux 视桌面环境。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P1（thread/settings/skills）；pets/cloud WON'T |
| 本地替代 | 注册 `codexsharp://` 或可选接管 `codex://`（勿与官方安装并存时抢协议，除非用户选择）。 |
| 验收 | 用户能用深链打开指定本地 thread 或设置页。系统必须忽略未知路径并打开主窗，不得执行未校验 path。 |

#### COMP-08 模型、effort、personality、plan/pair

| 字段 | 内容 |
|---|---|
| 官方行为 | 模型选择器；reasoning effort；personality；plan mode；`/fast` 服务档。 |
| 平台 | 视账号与目录；Bedrock 等无 Fast。 |
| CodexSharp 现状 | 有：model/effort/personality/collaborationMode；无官方 Fast 档。 |
| MoSCoW | MUST / P0（选择模型与 effort） |
| 本地替代 | `config.toml` + 兼容 API。 |
| 验收 | 用户能改模型、effort、personality。系统必须把变更写到 config 并作用于后续 turn。 |

---

### 3.4 审批与沙箱

#### SAND-01 审批策略与按钮

| 字段 | 内容 |
|---|---|
| 官方行为 | composer 下权限控件：Ask for approval、Approve for me（自动审查）、Full access、命名 profile。审批弹层：Enter 同意、Esc 拒绝。策略：`untrusted` / `on-request` / `never`。 |
| 平台 | 桌面 / CLI / IDE。 |
| CodexSharp 现状 | 有：ask/never/strict 按钮；审批 Allow/Deny。 |
| MoSCoW | MUST / P0 |
| 本地替代 | 现有 `approval_policy`。 |
| 验收 | 用户能在发送前改审批模式，并批准或拒绝命令。系统必须在未批准时不执行出界命令。 |

#### SAND-02 sandbox 模式

| 字段 | 内容 |
|---|---|
| 官方行为 | `read-only` / `workspace-write`（默认低摩擦） / `danger-full-access`。沙箱约束 spawn 的命令，不只内置写文件。macOS Seatbelt；Linux/WSL2 bubblewrap；Windows 见下。 |
| 平台 | 实现不同、语义相同。 |
| CodexSharp 现状 | 部分：三模式路径策略 + Windows Job Object kill-on-close；**无** OS seatbelt/landlock/官方 Windows sandbox。 |
| MoSCoW | MUST / P0（策略层）；OS 级 P1 |
| 本地替代 | 路径策略必须始终有效；默认 `workspace-write`。 |
| 验收 | 用户能切换 ro/ws/full。系统必须阻止 workspace-write 下对工作区外写入（测试覆盖）。 |

#### SAND-03 Windows sandbox setup

| 字段 | 内容 |
|---|---|
| 官方行为 | 原生 PowerShell 走 Windows sandbox：`elevated`（独立低权用户+防火墙+策略，需管理员批准）或 `unelevated`（受限 token+ACL，较弱）。可 `windows.sandbox_private_desktop`。WSL2 改用 Linux sandbox。Win11 推荐；Win10 尽力。 |
| 平台 | **仅 Windows**。 |
| CodexSharp 现状 | 诚实 stub：`windowsSandbox/readiness` 与 `setupStart` 为 `notConfigured`；「Setup sandbox」按钮不提升权限。 |
| MoSCoW | SHOULD / P1 |
| 本地替代 | 继续路径策略；setup 失败必须诚实。 |
| 验收 | 用户能看到 sandbox 未配置而非「已隔离」。系统必须不在未 setup 时声称 elevated 成功。 |

#### SAND-04 extra read roots 与 `/sandbox-add-read-dir`

| 字段 | 内容 |
|---|---|
| 官方行为 | 沙箱内读失败时可把绝对目录加入可读根。 |
| 平台 | Windows 文档明确；其他平台也有 writable/readable roots。 |
| CodexSharp 现状 | 有：`sandbox/extraReadRoot/add`、Desktop「Add read dir」。 |
| MoSCoW | MUST / P0 |
| 本地替代 | 现有 JSON 根列表。 |
| 验收 | 用户能添加绝对目录供后续命令读取。系统必须拒绝相对路径与越界。 |

#### SAND-05 execpolicy / rules

| 字段 | 内容 |
|---|---|
| 官方行为 | prefix rules 允许/询问/禁止沙箱外命令；比扩大 sandbox 更安全。 |
| 平台 | 全本地表面。 |
| CodexSharp 现状 | 有：`ExecPolicy` 与 App Server `execPolicy/*`。 |
| MoSCoW | MUST / P0 |
| 本地替代 | `~/.codexsharp/rules`。 |
| 验收 | 用户能列出/添加规则。系统必须在匹配 deny 时拦截。 |

#### SAND-06 Guardian / 自动审查

| 字段 | 内容 |
|---|---|
| 官方行为 | `approvals_reviewer = auto_review` 时由审查 agent 处理合格审批；不扩大 sandbox。`/approve` 可重试一次被拒。 |
| 平台 | 视账号与 feature flag。 |
| CodexSharp 现状 | 诚实 stub：`guardian` underDevelopment |
| MoSCoW | SHOULD / P2（产品想做时）；当前诚实 stub 为 P0 |
| 本地替代 | 用户审批。 |
| 验收 | 系统必须不假装 Guardian 已审查。用户仍能手动批准。 |

#### SAND-07 运行时防休眠

| 字段 | 内容 |
|---|---|
| 官方行为 | Settings：Prevent sleep while running，便于本地长任务。 |
| 平台 | macOS 文档强调；Windows 主机 Remote 需保持唤醒。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P1 |
| 本地替代 | Windows 用执行状态 API 申请持续。 |
| 验收 | 用户能在长任务期间阻止自动睡眠。系统必须在任务结束后释放。 |

---

### 3.5 Git / Review / PR / Handoff

#### GIT-01 基础 diff

| 字段 | 内容 |
|---|---|
| 官方行为 | 工作区 diff 出现在 review 面板与时间线 file change。 |
| 平台 | 需 Git 项目。 |
| CodexSharp 现状 | 有：diff rail、`/diff`、turn 完成后 `turn/diff/updated`。 |
| MoSCoW | MUST / P0 |
| 本地替代 | `git diff`（含未跟踪 `--no-index`）。 |
| 验收 | 用户能看到当前工作区 diff。系统必须在无 Git 时明确提示而非崩溃。 |

#### GIT-02 Review 视图（Unstaged / Staged / Commit / Branch / Last turn）

| 字段 | 内容 |
|---|---|
| 官方行为 | `/review`：相对 base branch 或未提交改动；面板可切 Unstaged、Staged、Commit、Branch、Last turn。默认当前 chat，Settings 可改为 Detached 新 chat。 |
| 平台 | 桌面 Codex。 |
| CodexSharp 现状 | 部分：Review 按钮、`review/start`、vs main；无完整五档面板。 |
| MoSCoW | SHOULD / P1 |
| 本地替代 | 只读审查 turn，不改工作树。 |
| 验收 | 用户能对未提交改动或相对 main 启动审查。系统必须不在审查模式改文件，除非用户另发修复指令。 |

#### GIT-03 行内评论

| 字段 | 内容 |
|---|---|
| 官方行为 | 在 diff 行上 + 评论，随后在 chat 里让 Codex 按评论改。 |
| 平台 | 桌面 review 面板。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P1 |
| 本地替代 | 把文件:行 评论当后续 user item。 |
| 验收 | 用户能对某行留下评论并发送。系统必须把评论绑定到路径与行号。 |

#### GIT-04 stage / revert hunk 与提交推送

| 字段 | 内容 |
|---|---|
| 官方行为 | 整 diff / 单文件 / 单 hunk 的 stage、unstage、revert；提交、推送、开 PR。 |
| 平台 | 桌面；需 Git。 |
| CodexSharp 现状 | 无（依赖 agent 跑 git 或终端） |
| MoSCoW | SHOULD / P1 |
| 本地替代 | 集成终端 + agent；UI 操作走 git 命令。 |
| 验收 | 用户能暂存或丢弃一个 hunk。系统必须调用 git，不自己重写对象库。 |

#### GIT-05 PR Chat（`gh`）

| 字段 | 内容 |
|---|---|
| 官方行为 | 26.707：在 PR 分支上显示 GitHub 评论与改动，可让 Codex 改并推送。需要已登录 `gh`。 |
| 平台 | 桌面；无 `gh` 则侧栏无 PR 详情。 |
| CodexSharp 现状 | 部分：「Insert gh pr create」；无 PR 评论侧栏。 |
| MoSCoW | SHOULD / P1 |
| 本地替代 | 检测 `gh`；未登录则提示，不接 ChatGPT GitHub 云应用。 |
| 验收 | 用户在已 `gh auth` 时能拉取当前分支 PR 评论。系统必须在缺少 `gh` 时说明原因。 |

#### GIT-06 多仓库 review

| 字段 | 内容 |
|---|---|
| 官方行为 | 26.727：多文件夹项目可在同一 review 头切换仓库；Last turn 可看 All repos。PR/worktree 动作仍针对主仓。 |
| 平台 | 桌面多文件夹项目。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P2（依赖 LIFE-02） |
| 本地替代 | 主仓 review 优先。 |
| 验收 | 用户能在多根项目中切换仓库看 diff。系统必须标明当前仓库。 |

#### GIT-07 Handoff（Local ↔ Worktree）

| 字段 | 内容 |
|---|---|
| 官方行为 | 聊天头 Hand off：在 Local 与该 chat 绑定的 worktree 间迁移 Git 状态；中断进行中的回复。 |
| 平台 | 桌面 Codex。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P1 |
| 本地替代 | 本地 git 操作；不支持 handoff 到 Cloud。 |
| 验收 | 用户能把 thread 从 worktree 迁到 Local 或反向。系统必须遵守「一分支一 checkout」。 |

#### GIT-08 跨主机 Handoff

| 字段 | 内容 |
|---|---|
| 官方行为 | 把 chat+Git 迁到另一已保存相同仓库的主机；官方用 ChatGPT 账号中继。不支持 Cloud。 |
| 平台 | macOS / Windows 主机。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P2 |
| 本地替代 | 用户可访问的 App Server + 相同 Git remote；无官方中继。 |
| 验收 | 用户能在已配对的本机 App Server 之间迁移 thread。系统必须在无配对时 `notConfigured`。 |

#### GIT-09 永久 worktree 与 `.worktreeinclude`

| 字段 | 内容 |
|---|---|
| 官方行为 | 项目菜单创建永久 worktree 项目；`.worktreeinclude` 复制 ignore 文件。 |
| 平台 | 本地 managed worktree；remote/手工 git worktree 不自动拷。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P1 |
| 本地替代 | 读仓库根 `.worktreeinclude`。 |
| 验收 | 用户能在有 `.worktreeinclude` 的仓开 worktree 并看到列出的 ignore 文件。系统必须不覆盖已存在文件、不跟随 symlink。 |

---

### 3.6 终端与 Local environments

#### TERM-01 每 chat 一个集成终端

| 字段 | 内容 |
|---|---|
| 官方行为 | 每个 chat 一个终端，作用域为当前项目或 worktree。图标或 Ctrl+` 打开。agent 可读当前输出。 |
| 平台 | Codex。 |
| CodexSharp 现状 | 有：单终端面板。 |
| MoSCoW | MUST / P0 |
| 本地替代 | 现有 PTY。 |
| 验收 | 用户能在当前 thread 的 cwd 跑命令。系统必须把输出留给模型当上下文（若会话启用）。 |

#### TERM-02 多 tab 终端

| 字段 | 内容 |
|---|---|
| 官方行为 | changelog：同一 chat 可多终端 tab。 |
| 平台 | 桌面 Codex。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P1 |
| 本地替代 | 每 tab 独立 PTY，仍绑定同一 cwd/worktree。 |
| 验收 | 用户能开第二个终端 tab 而不丢第一个会话。系统必须按 tab 隔离进程。 |

#### TERM-03 终端程序选择

| 字段 | 内容 |
|---|---|
| 官方行为 | Windows：PowerShell、Command Prompt、Git Bash、WSL。仅影响新会话。agent 默认仍是 Windows-native PowerShell，除非走 WSL 模式。 |
| 平台 | Windows 选项最多；macOS/Linux 用用户 shell。 |
| CodexSharp 现状 | 部分：跟进程默认 shell。 |
| MoSCoW | SHOULD / P1 |
| 本地替代 | 检测已安装的 pwsh/cmd/bash/wsl。 |
| 验收 | 用户能为新终端选择 PowerShell 或 cmd。系统必须不把 agent sandbox 实现与终端程序混为一谈。 |

#### TERM-04 Local environment setup scripts

| 字段 | 内容 |
|---|---|
| 官方行为 | 仅 Codex 桌面。项目根 `.codex` 保存 setup scripts（可按 macOS/Windows/Linux 覆盖），在新建 worktree 时自动跑。 |
| 平台 | 桌面 Codex。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P1 |
| 本地替代 | 读项目 `.codex` 或 `.codexsharp` 脚本；不上传云。 |
| 验收 | 用户能定义 setup 脚本并在新 worktree 自动执行。系统必须在脚本失败时把日志放进 thread。 |

#### TERM-05 顶栏 Actions

| 字段 | 内容 |
|---|---|
| 官方行为 | 常用命令（测/跑）出现在顶栏，在集成终端执行；可设图标与平台脚本。Win+Shift+D 跑环境主 action。 |
| 平台 | 桌面 Codex。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P1 |
| 本地替代 | 配置文件定义的按钮。 |
| 验收 | 用户能点一个 Action 在当前终端跑测试。系统必须在无配置时隐藏按钮。 |

#### TERM-06 Open in 编辑器

| 字段 | 内容 |
|---|---|
| 官方行为 | Open 用默认或项目级编辑器；`desktop.custom_file_handlers` 可加自定义。 |
| 平台 | 桌面。 |
| CodexSharp 现状 | 部分：时间线可打开本地路径。 |
| MoSCoW | SHOULD / P1 |
| 本地替代 | 系统默认应用 + 可选 `config.toml` handler。 |
| 验收 | 用户能用已配置编辑器打开文件。系统必须不把路径发到云。 |

---

### 3.7 Skills / Plugins / MCP / Hooks

#### EXT-01 本地 skills

| 字段 | 内容 |
|---|---|
| 官方行为 | `SKILL.md` 包；Codex `$` 调用；可来自用户/仓库/插件。未信任目录不加载项目 skills。 |
| 平台 | 全本地表面。Windows 关联 `.skill`。 |
| CodexSharp 现状 | 有：SkillCatalog、`load_skill`、Desktop 列表开关。 |
| MoSCoW | MUST / P0 |
| 本地替代 | `~/.codexsharp` 与仓库 skills。 |
| 验收 | 用户能列出、启用、禁用 skill。系统必须在未信任项目跳过项目 skills。 |

#### EXT-02 本地 plugins 与 marketplace

| 字段 | 内容 |
|---|---|
| 官方行为 | 插件可含 skills、connectors/MCP、hooks、browser 扩展、scheduled 模板。官方有通用目录。 |
| 平台 | Chat/Work/Codex 桌面；CLI 有 plugin browser；IDE 无插件。 |
| CodexSharp 现状 | 有：本地 plugin/marketplace CLI 与 Desktop 安装/调和。 |
| MoSCoW | MUST / P0（本地）；官方目录 WON'T |
| 本地替代 | `~/.codexsharp/plugins` 与本地 `marketplace.json`。 |
| 验收 | 用户能从本地 marketplace 安装/卸载插件。系统必须不请求官方 curated 目录。 |

#### EXT-03 MCP stdio 与 Streamable HTTP

| 字段 | 内容 |
|---|---|
| 官方行为 | config 声明 MCP；工具以服务器名为前缀。 |
| 平台 | 全本地。 |
| CodexSharp 现状 | 有：stdio 与 Streamable HTTP（url + bearer env）。 |
| MoSCoW | MUST / P0 |
| 本地替代 | `config.toml` `[mcp_servers]`。 |
| 验收 | 用户能添加 stdio MCP 并在对话中调用其工具。系统必须用 `mcp__server__tool` 命名。 |

#### EXT-04 MCP OAuth

| 字段 | 内容 |
|---|---|
| 官方行为 | 部分连接器需 OAuth/登录。 |
| 平台 | 官方连接器。 |
| CodexSharp 现状 | 诚实 stub：未配置 |
| MoSCoW | SHOULD / P2；诚实 stub 为 P0 |
| 本地替代 | bearer env / 用户自己的 token；不实现官方连接器云登录。 |
| 验收 | 系统必须对 OAuth 连接器返回未配置。用户仍能用 env 令牌的 HTTP MCP。 |

#### EXT-05 Hooks

| 字段 | 内容 |
|---|---|
| 官方行为 | 生命周期命令；插件 hooks 需先审查再启用。 |
| 平台 | 本地。 |
| CodexSharp 现状 | 有：hooks 列表与执行路径（受 trust 约束）。 |
| MoSCoW | MUST / P0 |
| 本地替代 | 项目/用户 hooks 文件。 |
| 验收 | 用户能列出 hooks。系统必须在未信任目录不跑项目 hooks。 |

#### EXT-06 官方插件目录与连接器（Gmail/Slack/Drive/Messages…）

| 字段 | 内容 |
|---|---|
| 官方行为 | 通用目录；Apple Messages 仅 macOS Work/Codex；教育插件等随 workspace。 |
| 平台 | 视插件；Messages 仅 macOS。 |
| CodexSharp 现状 | 无 |
| MoSCoW | WON'T（官方目录）；用户可本地装等价 MCP |
| 本地替代 | 用户自备 MCP。 |
| 验收 | 系统必须不内置官方连接器密钥。用户能装自己的 MCP 代替。 |

#### EXT-07 Memories

| 字段 | 内容 |
|---|---|
| 官方行为 | 可选跨 chat 记忆；EEA/UK/CH 默认关；API key / Bedrock 不可用官方 Memories。 |
| 平台 | 账号功能。 |
| CodexSharp 现状 | 部分：`~/.codexsharp/memories` 文件记忆。 |
| MoSCoW | SHOULD / P1 |
| 本地替代 | 本地文件，不进 ChatGPT 云记忆。 |
| 验收 | 用户能开关本地 memories。系统必须拒绝路径穿越。 |

#### EXT-08 官方 bundled 插件旁证

| 字段 | 内容 |
|---|---|
| 官方行为 | 安装包内置 browser/chrome/computer-use/sites/visualize 等。 |
| 平台 | 桌面包。 |
| CodexSharp 现状 | 诚实 stub：computer_use / browser_use 等 feature flags |
| MoSCoW | 各插件见 3.8–3.9；此处要求诚实 |
| 本地替代 | 不复制官方 bundle 二进制。 |
| 验收 | 设置页列出这些能力为 notConfigured，直到真正实现。 |

---

### 3.8 Browser / Computer Use / Appshots / 扩展

#### BCU-01 内置浏览器

| 字段 | 内容 |
|---|---|
| 官方行为 | 应用内浏览器；`@Browser`；地址栏搜历史或 Google；Settings 管理历史。26.727。 |
| 平台 | 桌面；管理员可限 origin/上传下载。 |
| CodexSharp 现状 | 诚实 stub：`browser_use` |
| MoSCoW | SHOULD / P2 |
| 本地替代 | 可选 WebView；无云浏览器登录。 |
| 验收 | 未实现时系统必须 `notConfigured`。实现后用户能用 `@Browser` 打开 URL。 |

#### BCU-02 页面注释与样式

| 字段 | 内容 |
|---|---|
| 官方行为 | 对页面区域注释；Adjust 可改字体间距颜色并预览。 |
| 平台 | 内置浏览器。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P2 |
| 本地替代 | 注释变成带选择器的 chat 附件。 |
| 验收 | 用户能圈选页面元素并发送修改意图。 |

#### BCU-03 Site tools（WebMCP）

| 字段 | 内容 |
|---|---|
| 官方行为 | 2026-08-25：内置浏览器可调用网站提供的工具。需较新模型；Luna 与 Enterprise/Edu 不可用。 |
| 平台 | 桌面 Work/Codex。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P2 |
| 本地替代 | 仅当内置浏览器存在。 |
| 验收 | 未实现则诚实 stub。系统必须把网页工具当不可信输入。 |

#### BCU-04 CDP Developer mode

| 字段 | 内容 |
|---|---|
| 官方行为 | Settings > Browser 开启完整 CDP；使用前需明确批准。组织可禁用。 |
| 平台 | Chrome 扩展 + 内置浏览器。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P2 |
| 本地替代 | 默认关。 |
| 验收 | 用户默认不能静默 CDP。系统必须每次完整 CDP 都请求批准。 |

#### BCU-05 浏览器扩展（Chrome / Edge / Brave / Opera / Vivaldi）

| 字段 | 内容 |
|---|---|
| 官方行为 | 提 tab、控制已登录站点、Ask ChatGPT、YouTube 问答。Opera **无** side chat。设置在 Settings > Computer Use。 |
| 平台 | 桌面；需用户浏览器。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P2 |
| 本地替代 | 可选装扩展；不强制官方扩展 ID。 |
| 验收 | 未实现时 notConfigured。实现后用户能 @ 已连接浏览器的当前 tab。 |

#### BCU-06 Computer Use

| 字段 | 内容 |
|---|---|
| 官方行为 | Work/Codex 安装 Computer Use 插件；`@Computer` 或 `@AppName`。看屏幕、点、打字。macOS 需 Screen Recording + Accessibility；可后台。Windows **前台占用**，目标窗须在当前桌面。Linux preview **尚无**。不能自动化终端或 ChatGPT 自身，不能点系统权限框。 |
| 平台 | macOS / Windows；Linux 无。 |
| CodexSharp 现状 | 诚实 stub：`computer_use` |
| MoSCoW | SHOULD / P2 |
| 本地替代 | 不复制官方 CUA 二进制；若做，走独立插件且默认关。 |
| 验收 | 未实现必须 `notConfigured`。实现后用户能授权单个应用。系统必须保留 sandbox 对文件/shell 的约束。 |

#### BCU-07 Windows 前台 / macOS locked use

| 字段 | 内容 |
|---|---|
| 官方行为 | Windows 不能在同一会话后台 CU。macOS locked use 可在锁屏后为受信任 CU turn 短暂解锁，有插入即重新锁定。 |
| 平台 | 见上。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P2（随 CU）；locked use 可 WON'T |
| 本地替代 | Windows 实现必须文档化前台占用。 |
| 验收 | 用户在 Windows 上看到「将占用指针/键盘」提示。系统必须不实现未声明的解锁插件。 |

#### BCU-08 Appshots

| 字段 | 内容 |
|---|---|
| 官方行为 | **仅 macOS**：双 Command（可改）捕获最前窗口图像+辅助功能文本，当附件。60 秒内有过交互则附加到最近 chat。 |
| 平台 | 仅 macOS。Windows/Linux 官方无。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P2（仅若做 macOS 客户端） |
| 本地替代 | Windows 用户用截图附件。 |
| 验收 | Windows 构建不得显示 Appshots 为可用。 |

#### BCU-09 Computer History

| 字段 | 内容 |
|---|---|
| 官方行为 | macOS 选择加入的时间线/记忆，默认关；需 Memories；不可用于 API key。取代 Chronicle。 |
| 平台 | **仅 macOS** + ChatGPT 计划。 |
| CodexSharp 现状 | 无 |
| MoSCoW | WON'T |
| 本地替代 | 无。 |
| 验收 | 系统必须不采集全机活动时间线。 |

#### BCU-10 Record & Replay

| 字段 | 内容 |
|---|---|
| 官方行为 | macOS 演示工作流生成 skill；需 Computer Use。已扩到 EU/UK/CH。 |
| 平台 | macOS。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P2（依赖 CU）；否则 WON'T |
| 本地替代 | 用户手写 skill。 |
| 验收 | 未实现 CU 时不展示 Record & Replay 为可用。 |

---

### 3.9 Artifacts / 图像 / Sites / Visualizations

#### ART-01 Office / PDF / HTML 预览与批注

| 字段 | 内容 |
|---|---|
| 官方行为 | 桌面预览 docx/pptx/xlsx/pdf；可选自动打开；html 可交互预览；对区域批注请求修改。Windows 包关联表格/文档扩展名。 |
| 平台 | 桌面。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P1 |
| 本地替代 | 用系统查看器或后续内嵌预览；不上传 ChatGPT。 |
| 验收 | 用户能预览生成的 xlsx/pdf/docx。系统必须在无预览器时给出本地路径。 |

#### ART-02 图像输入

| 字段 | 内容 |
|---|---|
| 官方行为 | 附加参考图；agent `view_image`。 |
| 平台 | 全表面。 |
| CodexSharp 现状 | 有 |
| MoSCoW | MUST / P0 |
| 本地替代 | 本地文件。 |
| 验收 | 用户能粘贴或附加图片。系统必须把图像交给模型（若提供商支持），否则诚实失败。 |

#### ART-03 图像生成（Focused / Canvas）

| 字段 | 内容 |
|---|---|
| 官方行为 | 26.727：展开查看、Focused/Canvas、跨图 Comment、多选再改。官方用量走 Codex 额度。 |
| 平台 | 桌面；Bedrock 无。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P2 |
| 本地替代 | 可选 Images API + 用户 API key；不声称 ChatGPT 额度。 |
| 验收 | 未配置时 notConfigured。配置后用户能生成并保存到工作区。 |

#### ART-04 Visualizations

| 字段 | 内容 |
|---|---|
| 官方行为 | `@Visualize` 交互图；桌面 preview 滚动中。CLI/IDE 不渲染。 |
| 平台 | web 较完整；桌面 preview。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P2 |
| 本地替代 | 打开生成的 HTML；不托管。 |
| 验收 | 未实现则不当作已支持。用户仍能得到静态图或 HTML 文件。 |

#### ART-05 Sites 官方托管

| 字段 | 内容 |
|---|---|
| 官方行为 | 托管站点/应用；自定义域（26.707）、协作编辑与改 URL（2026-08-20）、分析、ChatGPT 登录。Plus+ beta。 |
| 平台 | ChatGPT 云。 |
| CodexSharp 现状 | 无 |
| MoSCoW | WON'T |
| 本地替代 | 本地生成静态文件；用户自己部署。 |
| 验收 | 系统必须不创建 openai 托管站点。用户能得到本地产物路径。 |

---

### 3.10 Automations / Goals / 长任务

#### AUTO-01 独立 Scheduled 任务

| 字段 | 内容 |
|---|---|
| 官方行为 | 每次 run 新 chat；可跨项目；自定义 RRULE；Git 项目可选本地目录或后台 worktree；侧栏 **Scheduled** 收件箱。桌面需开机且 app 在跑才能碰本地文件。 |
| 平台 | 桌面可本地；web 不能碰文件夹。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P1 |
| 本地替代 | 本机调度（登录即运行的计划任务或 app 内 heartbeat），状态在 `~/.codexsharp`；不实现 Gmail/Slack/GitHub 事件触发（官方桌面也没有事件触发）。 |
| 验收 | 用户能创建每日本地任务并在下次触发看到新 thread。系统必须在 app 未运行时跳过并记录，而不是静默成功。 |

#### AUTO-02 聊天内 Scheduled

| 字段 | 内容 |
|---|---|
| 官方行为 | 回到同一 chat 上下文；可分钟级跟进。 |
| 平台 | 桌面 / web。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P1 |
| 本地替代 | 对已有 thread id 做定时 `turn/start`。 |
| 验收 | 用户能把「每 10 分钟检查构建」绑到当前 thread。系统必须复用同一 thread 而非每次新建。 |

#### AUTO-03 事件触发 Scheduled（Gmail/Slack/GitHub）

| 字段 | 内容 |
|---|---|
| 官方行为 | 2026-08-25：web/mobile 合格计划。**桌面 / CLI / IDE 没有。** 不能与时间表组合。 |
| 平台 | 仅 web/mobile。 |
| CodexSharp 现状 | 无 |
| MoSCoW | WON'T（官方桌面也无） |
| 本地替代 | 无。 |
| 验收 | 文档标明非桌面能力。系统必须不伪造事件触发器。 |

#### AUTO-04 Goal mode（`/goal`）

| 字段 | 内容 |
|---|---|
| 官方行为 | 持久目标直到完成/暂停/需输入；composer 上进度条可暂停、恢复、编辑、清除。建议先 `/plan`。 |
| 平台 | 桌面 / CLI / IDE。 |
| CodexSharp 现状 | 部分：`thread/goal/*`、Desktop `/goal`。 |
| MoSCoW | MUST / P0（能设目标并显示状态）；完整进度条 P1 |
| 本地替代 | 本地 goal 状态机。 |
| 验收 | 用户能设置、暂停、清除 goal。系统必须在 goal 活动时继续 steer 而不丢掉目标文本。 |

#### AUTO-05 长任务并行与「下一个需要注意」

| 字段 | 内容 |
|---|---|
| 官方行为 | 多 chat 并行；worktree 隔离写冲突；快捷键跳到下一个需要注意的 Codex chat；Pets/通知辅助。 |
| 平台 | 桌面。 |
| CodexSharp 现状 | 部分：多 thread 可开，缺 inbox 快捷键。 |
| MoSCoW | MUST / P0（并行 thread）；导航 P1 |
| 本地替代 | 多 JSON-RPC 会话。 |
| 验收 | 用户能同时跑两条本地 thread。系统必须按 thread 隔离工作区，除非用户显式共享 cwd。 |

---

### 3.11 Voice / Pets / Codex Micro

#### VPM-01 ChatGPT Voice（GPT-Live）

| 字段 | 内容 |
|---|---|
| 官方行为 | 26.715：在 Chat/Work/Codex 语音协调任务；可开新任务、查进度、切换任务。同时仅一路。Plus+（Enterprise/Edu 有提前接入）。macOS Screen context 用 Appshot。也可经 Remote iOS。 |
| 平台 | 桌面；Linux 视 preview。 |
| CodexSharp 现状 | 诚实 stub：`realtime` / `thread/realtime/*` |
| MoSCoW | SHOULD / P2 |
| 本地替代 | 可选 Realtime API + 用户 key；无 ChatGPT Voice 套餐。 |
| 验收 | 未配置返回 `notConfigured`。实现后同时只一路语音。 |

#### VPM-02 语音听写

| 字段 | 内容 |
|---|---|
| 官方行为 | 把说话变成 prompt 文本，不同于 Voice 对话。 |
| 平台 | 桌面快捷键 Ctrl+Shift+D。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P2 |
| 本地替代 | OS 听写或本地 STT。 |
| 验收 | 未实现则不显示已连接麦克风会话。 |

#### VPM-03 Pets

| 字段 | 内容 |
|---|---|
| 官方行为 | 可选浮动宠物显示 Running / Needs input / Ready / Blocked；`/pet`；自定义宠物走 `hatch-pet`；尊重减少动态效果。web 宠物无浮动层。 |
| 平台 | 桌面浮动层；CLI 终端图。 |
| CodexSharp 现状 | 部分：`/pets` 经 App Server，无浮动 overlay。 |
| MoSCoW | WON'T |
| 本地替代 | 用系统通知 + Activity。 |
| 验收 | `/pet` 可诚实提示未实现 overlay，不得假装有桌面宠物。 |

#### VPM-04 Codex Micro

| 字段 | 内容 |
|---|---|
| 官方行为 | 与 Work Louder 合作的硬件：USB-C/蓝牙，快捷键、语音、灯。macOS 需 Input Monitoring。Settings > Codex Micro。 |
| 平台 | 桌面 + 硬件。 |
| CodexSharp 现状 | 无 |
| MoSCoW | WON'T |
| 本地替代 | 无。 |
| 验收 | 系统必须忽略 Micro 设备协议，不声明支持。 |

---

### 3.12 Remote / SSH

#### REM-01 官方 Remote 扫码中继

| 字段 | 内容 |
|---|---|
| 官方行为 | Settings > Connections > Control this Mac or PC；手机扫码，同一 ChatGPT 账号。手机可开任务、steer、审批、看 diff/终端。安全中继，不把机器暴露到公网。登出会关 Remote Control 但保留配对。 |
| 平台 | 主机 macOS/Windows（Linux 非 Remote 主机）。控制端 iOS/Android 或其他桌面。 |
| CodexSharp 现状 | 诚实 stub：`remoteControl/status/enable/disable`，**无 transport** |
| MoSCoW | SHOULD / P2（真实配对）；stub 诚实为 P0 |
| 本地替代 | 不接 ChatGPT 中继。可用局域网 App Server `ws://`（Origin 检查）。 |
| 验收 | 用户打开 Remote 时看到无传输层。系统必须不显示「已连接到 ChatGPT Remote」。 |

#### REM-02 SSH 远程项目

| 字段 | 内容 |
|---|---|
| 官方行为 | 读 `~/.ssh/config` 具体 Host（忽略纯 pattern）；远程需 PATH 上有 `codex`；远程跑 app-server。Remote 项目单文件夹。 |
| 平台 | 桌面。 |
| CodexSharp 现状 | 无 |
| MoSCoW | SHOULD / P2 |
| 本地替代 | SSH 隧道到 `codexsharp app-server`；远程装 CodexSharp 而非官方 `codex`。 |
| 验收 | 未实现 SSH UI 时诚实。实现后用户能选 ssh config Host 并打开远程文件夹。 |

#### REM-03 防火墙端口旁证

| 字段 | 内容 |
|---|---|
| 官方行为 | 本机包允许入站 1455/1457 等（本地服务/浏览器调试，非协议规范）。 |
| 平台 | Windows MSIX。 |
| CodexSharp 现状 | 有：可选 `app-server --listen ws://IP:PORT` + `/readyz` `/healthz` |
| MoSCoW | SHOULD / P1（本机 listen）；公开暴露 WON'T |
| 本地替代 | 默认不监听公网。 |
| 验收 | 系统必须默认不在 0.0.0.0 开放未认证端口。 |

---

### 3.13 账号、云端、企业

#### ACC-01 ChatGPT 登录与云同步

| 字段 | 内容 |
|---|---|
| 官方行为 | 桌面登录 ChatGPT；项目、pins、credits、Remote、Cloud 都绑账号。 |
| 平台 | 官方桌面。 |
| CodexSharp 现状 | 部分：本地 API key、device-code/PKCE 登录存本地；无云同步。 |
| MoSCoW | MUST / P0（本地凭据）；云同步 WON'T |
| 本地替代 | `CODEXSHARP_API_KEY` / `auth.json`。 |
| 验收 | 用户能保存 API key 并开 thread。系统必须不把 thread 同步到 chatgpt.com。 |

#### ACC-02 ChatGPT credits / 用量

| 字段 | 内容 |
|---|---|
| 官方行为 | Profile 显示 lifetime/peak tokens、streaks；Work/Codex 共享额度。 |
| 平台 | 官方账号。 |
| CodexSharp 现状 | 诚实 stub：空或 `noCredit`；本地 `chars/4` 估算 |
| MoSCoW | WON'T（官方额度）；诚实 stub P0 |
| 本地替代 | 显示本地估算并标明非官方。 |
| 验收 | 系统必须不显示伪造的 ChatGPT 剩余额度。 |

#### ACC-03 企业 MDM / 托管配置 / 自动更新策略

| 字段 | 内容 |
|---|---|
| 官方行为 | `requirements.toml`、Intune/MDM 部署 MSIX、可关应用内更新、浏览器/CU 管理控制。 |
| 平台 | 企业 Windows/macOS。 |
| CodexSharp 现状 | 无 |
| MoSCoW | WON'T |
| 本地替代 | 用户自己的 config。 |
| 验收 | 文档标明不提供 MDM。 |

#### ACC-04 独立构建无官方自动更新

| 字段 | 内容 |
|---|---|
| 官方行为 | 应用内更新；企业可关。 |
| 平台 | 官方桌面。 |
| CodexSharp 现状 | 诚实 stub：`codexsharp update` 声明独立构建 |
| MoSCoW | WON'T（官方更新通道） |
| 本地替代 | 用户自行发版。 |
| 验收 | 系统必须不检查 openai 更新 CDN 并自称已更新到 26.901。 |

#### ACC-05 `code_mode_host`

| 字段 | 内容 |
|---|---|
| 官方行为 | 官方桌面/宿主的 code mode 托管面。 |
| 平台 | 官方。 |
| CodexSharp 现状 | 诚实 stub：`code_mode_host` |
| MoSCoW | WON'T / 诚实 stub P0 |
| 本地替代 | 普通 agent loop。 |
| 验收 | feature flag 保持 `notConfigured` / `underDevelopment`。 |

---

### 3.14 Settings

#### SET-01 设置面板结构

| 字段 | 内容 |
|---|---|
| 官方行为 | Ctrl+, 打开。分组包括 General（多行发送、防休眠、follow-up steer/queue、Code review detached）、Profile、Keyboard Shortcuts、Notifications、Appearance（主题/强调色/字体，可分享主题）、Pets、Voice、Computer use、Browser、Connections、Import、Personalization（personality、自定义指令写入个人 AGENTS.md）、Archived chats、Suggested prompts。 |
| 平台 | 桌面；部分页 macOS 独有。 |
| CodexSharp 现状 | 部分：精简 Settings（personality、主题、notify、skills、MCP、模型、sandbox、审批、effort、RC 开关、doctor）。 |
| MoSCoW | MUST / P0（能改模型/sandbox/审批/主题）；完整分组 P1/P2 |
| 本地替代 | 写 `config.toml`。 |
| 验收 | 用户能不改手文件完成 P0 设置。系统必须把变更落到 `~/.codexsharp/config.toml`。 |

#### SET-02 通知

| 字段 | 内容 |
|---|---|
| 官方行为 | 回合完成：从不 / 仅后台 / 总是；权限与提问通知开关；OS 授权。 |
| 平台 | 桌面。 |
| CodexSharp 现状 | 部分：turn complete `DesktopNotify`（auto/OSC9/BEL/Off）。 |
| MoSCoW | SHOULD / P1 |
| 本地替代 | Windows toast / 终端 BEL。 |
| 验收 | 用户能在后台完成时收到通知。系统必须尊重 Off。 |

#### SET-03 缺失能力在设置中诚实展示

| 字段 | 内容 |
|---|---|
| 官方行为 | 官方只显示账号可用的设置页。 |
| 平台 | — |
| CodexSharp 现状 | 有：computerUse/browserUse 字符串 |
| MoSCoW | MUST / P0 |
| 本地替代 | 设置页列出 stub 与 `notConfigured`。 |
| 验收 | 用户打开设置能看到 Computer Use / Browser Use / Remote 未配置。系统必须不提供「Enable」假按钮把 stub 标成已启用。 |

---

## 4. 差距总表

只列 **部分 / 无 / 诚实 stub**。`有` 且无缺口的 P0（composer 发送停止、时间线主路径、审批 Allow/Deny、@file/附件、trust、本地 skills/MCP/plugins/hooks、基础 diff、单终端、本地凭据）不重复。优先级 P0–P3 与 MoSCoW 对应。

### P0

| ID | 能力 | 现状 | 缺口 | 本地替代 / stub |
|---|---|---|---|---|
| NAV-02 | 侧栏项目+线程 | 部分 | 无官方三模式信息架构；无 Activity；无云 pin 同步 | 本地项目/thread 即可交付 |
| LIFE-01 | 本地项目 | 部分 | 缺项目级 Open 编辑器、项目菜单完整编辑 | 登记 cwd |
| LIFE-05 | worktree 启动 | 部分 | 非完整 managed detached HEAD、清理、绑定 chat | 云 worktree = `notConfigured` |
| COMP-03 | 斜杠 | 部分 | 官方云命令未对齐；需对 `/cloud*` `/pet` 诚实 | 云命令 stub |
| COMP-04 | `$` skill | 部分 | composer `$` 体验 | 已能 load_skill |
| SAND-02 | sandbox 模式 | 部分 | 无 OS 级隔离 | 路径策略必须保留 |
| SAND-03 | Windows sandbox | 诚实 stub | `windowsSandbox/*` notConfigured | 不得声称 elevated |
| SAND-06 | Guardian | 诚实 stub | `guardian` underDevelopment | 用户审批 |
| AUTO-04 | Goal | 部分 | 进度条/暂停 UX | `thread/goal/*` |
| AUTO-05 | 并行 thread | 部分 | 缺「下一待办」导航 | 多 thread 已可跑 |
| EXT-08 | bundled 能力 | 诚实 stub | computer/browser/realtime/code_mode_host | 设置页诚实 |
| BCU-01 | browser_use | 诚实 stub | 无内置浏览器 | `notConfigured` |
| BCU-06 | computer_use | 诚实 stub | 无 CU | `notConfigured` |
| VPM-01 | realtime Voice | 诚实 stub | 无 GPT-Live | `notConfigured` |
| REM-01 | remoteControl | 诚实 stub | 无 transport | enable/disable 仅状态 |
| ACC-02 | credits | 诚实 stub | 无官方额度 | 空/`noCredit`/chars/4 |
| LIFE-06 | Cloud thread | 诚实 stub | 不实现 | `notConfigured` |
| ACC-05 | code_mode_host | 诚实 stub | 不实现 | underDevelopment |
| SET-03 | stub 展示 | 部分 | 须保证无假 Enable | 已有字符串展示 |

### P1

| ID | 能力 | 现状 | 缺口 | 本地替代 |
|---|---|---|---|---|
| NAV-03 | Activity / 通知收件箱 | 无 | 未读/运行中/待审批聚合 | 本地状态 |
| NAV-05 | 命令面板 | 无 | Ctrl+K | 调 App Server |
| COMP-06 | 快捷键表 | 部分 | 无完整/可重绑 | Windows 常用集 |
| COMP-07 | `codex://` | 无 | 未注册协议 | 谨慎与官方包并存 |
| SET-02 | OS 通知 | 部分 | 非完整三态 | toast |
| SAND-07 | 防休眠 | 无 | 长任务会睡 | Windows 执行状态 |
| GIT-02 | Review 五档 | 部分 | 无完整面板 | `/review` |
| GIT-03 | 行内评论 | 无 | | 文件:行 item |
| GIT-04 | stage/revert hunk | 无 | | git |
| GIT-05 | PR Chat + `gh` | 部分 | 无评论侧栏 | 检测 `gh` |
| GIT-07 | Handoff Local/Worktree | 无 | | 本地 git |
| GIT-09 | `.worktreeinclude` / 永久 worktree | 无 | | 读文件 |
| TERM-02 | 多终端 tab | 无 | | 多 PTY |
| TERM-03 | PS/cmd/Git Bash/WSL | 部分 | 无选择器 | 检测已安装 |
| TERM-04 | local env setup | 无 | `.codex` 脚本 | 项目文件 |
| TERM-05 | 顶栏 Actions | 无 | | 配置按钮 |
| TERM-06 | Open in 编辑器 | 部分 | 无 custom handlers | 系统默认 |
| ART-01 | Office/PDF 预览 | 无 | | 系统查看器 + 路径 |
| AUTO-01 | 独立 Scheduled | 无 | | 本机调度 |
| AUTO-02 | 聊天内 Scheduled | 无 | | 定时 turn |
| EXT-07 | 本地 memories UX | 部分 | 设置页弱 | 文件 memories |
| LIFE-09 | 导入其他 agent | 部分 | 无向导 | CLAUDE.md → AGENTS.md |
| REM-03 | 本机 listen | 部分 | 需默认安全 | 已有 Origin 403 |
| SET-01 | 设置分组 | 部分 | 非官方信息架构 | config.toml |

### P2

| ID | 能力 | 现状 | 缺口 | 本地替代 |
|---|---|---|---|---|
| NAV-01 | Chat/Work 壳 | 无 | 无云产品 | 无云布局 |
| NAV-04 | 弹出窗 / always on top | 无 | | 第二窗 |
| LIFE-02 | 多文件夹项目 | 部分 | 非多根项目模型 | extra read roots |
| LIFE-07 | Quick/temporary chat | 无 | | 无项目 thread |
| BCU-02–05 | 浏览器注释/WebMCP/CDP/扩展 | 无 | 依赖内置浏览器 | stub |
| BCU-07 | locked use / Windows 前台语义 | 无 | 依赖 CU | |
| BCU-08 | Appshots | 无 | 官方仅 macOS | 截图附件 |
| BCU-10 | Record & Replay | 无 | 依赖 macOS CU | 手写 skill |
| ART-03 | 图像生成 Canvas | 无 | | Images API + 用户 key |
| ART-04 | Visualizations | 无 | 官方桌面 preview | 本地 HTML |
| VPM-02 | 听写 | 无 | | OS STT |
| GIT-06 | 多仓 review | 无 | 依赖多文件夹 | 主仓 |
| GIT-08 | 跨主机 Handoff | 无 | 无官方中继 | 自建 App Server |
| REM-02 | SSH 远程项目 | 无 | | SSH 隧道 |
| EXT-04 | MCP OAuth | 诚实 stub | | bearer env |
| SAND-06 | 真 Guardian | 诚实 stub | | 用户审批 |

### P3（WON'T，仍须诚实）

| ID | 能力 | 现状 | 官方差异 | 处理 |
|---|---|---|---|---|
| NAV-07 | Electron 像素克隆 | 无 | Chromium 壳 | 保持 FuncUI |
| LIFE-03 | ChatGPT 云项目 | 无 | 需账号 | `notConfigured` |
| LIFE-06 | Cloud thread/worktree | 诚实 stub | 需云环境 | 保持 stub |
| LIFE-10 | 只读分享链 | 无 | 仅 macOS+账号 | `/export` |
| EXT-06 | 官方插件目录/连接器 | 无 | 通用目录 | 本地市场 |
| BCU-09 | Computer History | 无 | 仅 macOS+账号 | 不采集 |
| ART-05 | Sites 托管 | 无 | ChatGPT 云 | 本地产物 |
| AUTO-03 | 事件触发 Scheduled | 无 | **官方桌面也无** | 不实现 |
| VPM-03 | Pets overlay | 部分命令 | 浮动宠物 | WON'T overlay |
| VPM-04 | Codex Micro | 无 | 硬件 | 忽略 |
| ACC-01 云同步 | ChatGPT 同步 | 无 | 账号 | API key |
| ACC-03 | MDM / requirements.toml | 无 | 企业 | WON'T |
| ACC-04 | 官方自动更新 | 诚实 stub | CDN | 独立构建声明 |

---

## 5. 分期与非目标

### 5.1 分期（Codex 工作流先 MUST）

| 阶段 | 目标 | 包含 |
|---|---|---|
| **M0 诚实基线** | 现在即可验收 | P0 已有项 + 全部诚实 stub（不得回退成假成功） |
| **M1 Codex 桌面闭环** | Windows 上可日常编码 | 补齐 NAV-02 侧栏、worktree 生命周期、Goal UX、stub 设置页、默认 `workspace-write` |
| **M2 协作与调度** | P1 | Handoff、Review 面板、PR+`gh`、local env、Scheduled 本地、`codex://`、命令面板、通知、Office/PDF 预览、Windows sandbox setup |
| **M3 外围表面** | P2 可选 | 内置浏览器、Computer Use、Voice、图像生成、多文件夹、side chat/浮动窗、无云 Chat/Work 壳、SSH |
| **MX 不做** | WON'T | 见 5.2 |

Windows 第一。macOS 专有（Appshots、Computer History、locked use、Messages、分享链）不阻塞 Windows M1/M2。Linux 只在官方差异表中跟踪。

### 5.2 非目标

- Pets 浮动层、Codex Micro 硬件。
- Sites 官方托管、自定义域、协作编辑。
- Computer History / 全盘活动时间线。
- ChatGPT 云同步、Cloud thread、Cloud Work、官方 credits。
- 企业 MDM、Intune、`requirements.toml` 强制、官方更新通道。
- 官方插件目录与 Gmail/Slack/Drive 等连接器云 OAuth。
- 像素级 Electron 克隆、拆 `app.asar` 当协议。
- 事件触发 Scheduled（官方桌面本身没有）。
- 把 sandbox 默认改成 `danger-full-access` 或在测试里无故放宽。
- 在仓库或配置样例里写入密钥。

### 5.3 changelog 26.707 起桌面能力覆盖

| 版本/日期 | 桌面能力 | 本文位置 | 处理 |
|---|---|---|---|
| 26.707 2026-07-09 | Codex 并入 ChatGPT 桌面；Markdown/代码编辑与批注；PR Chat；Sites 自定义域 | NAV-01、ART-01、GIT-05、ART-05 | 壳 P2；预览 P1；PR P1；Sites WON'T |
| 26.715 2026-07-23 | Voice；多文件夹项目 | VPM-01、LIFE-02 | P2 |
| 26.727 2026-07-30 | 浏览器历史/搜索；扩展 Ask ChatGPT/YouTube；多仓 review；图像 Focused/Canvas；Activity | BCU-*、GIT-06、ART-03、NAV-03 | P1/P2 |
| 2026-07-31 | Record & Replay 扩区域 | BCU-10 | P2/WON'T 至有 CU |
| 2026-08-11 | Linux preview；Import Claude/Cursor | 元信息、LIFE-09 | Linux 只记录；导入 P1 |
| 2026-08-13 | Computer History | BCU-09 | WON'T |
| 2026-08-20 | Apple Messages；Sites 协作/改 URL；Computer History 欧洲；分享 snapshot；统一 pin | EXT-06、ART-05、LIFE-10、NAV-02 | WON'T 或云 pin 不同步 |
| 2026-08-25 | Edge/Brave/Opera/Vivaldi；WebMCP；云浏览器登录 | BCU-05/03、ACC-01 | P2 / WON'T 云登录 |
| 2026-08-25 | 事件触发 Scheduled | AUTO-03 | WON'T（非桌面） |
| Features 目录其余 | Remote、Sites、Visualizations、Scheduled、Goals、Notifications、Pets、Micro、Appshots、Plugins、图像输入 | 第 3 节对应条 | 均已入目录或 WON'T |

---

## 6. 来源附录

官方 URL，调研日 2026-09-07。不摘录大段原文。`developers.openai.com/codex/app` 等现重定向到 `learn.chatgpt.com`。

### 6.1 ChatGPT Learn（桌面）

- https://learn.chatgpt.com/docs/app
- https://learn.chatgpt.com/docs/features
- https://learn.chatgpt.com/docs/projects
- https://learn.chatgpt.com/docs/automations
- https://learn.chatgpt.com/docs/browser
- https://learn.chatgpt.com/docs/computer-use
- https://learn.chatgpt.com/docs/artifacts-viewer
- https://learn.chatgpt.com/docs/plugins
- https://learn.chatgpt.com/docs/skills-and-plugins
- https://learn.chatgpt.com/docs/remote
- https://learn.chatgpt.com/docs/remote-connections
- https://learn.chatgpt.com/docs/environments/git-worktrees
- https://learn.chatgpt.com/docs/environments/local-environment
- https://learn.chatgpt.com/docs/environments/modes
- https://learn.chatgpt.com/docs/features/voice
- https://learn.chatgpt.com/docs/features/codex-micro
- https://learn.chatgpt.com/docs/appshots
- https://learn.chatgpt.com/docs/chrome-extension
- https://learn.chatgpt.com/docs/sites
- https://learn.chatgpt.com/docs/pets
- https://learn.chatgpt.com/docs/visualizations
- https://learn.chatgpt.com/docs/image-generation
- https://learn.chatgpt.com/docs/image-inputs
- https://learn.chatgpt.com/docs/reference/commands
- https://learn.chatgpt.com/docs/reference/slash-commands
- https://learn.chatgpt.com/docs/reference/settings
- https://learn.chatgpt.com/docs/windows/windows-app
- https://learn.chatgpt.com/docs/windows/windows-sandbox
- https://learn.chatgpt.com/docs/windows/wsl
- https://learn.chatgpt.com/docs/linux/linux-app
- https://learn.chatgpt.com/docs/code-review
- https://learn.chatgpt.com/docs/long-running-work
- https://learn.chatgpt.com/docs/notifications
- https://learn.chatgpt.com/docs/changelog
- https://learn.chatgpt.com/docs/whats-new
- https://learn.chatgpt.com/docs/customization/computer-history
- https://learn.chatgpt.com/docs/extend/record-and-replay
- https://learn.chatgpt.com/docs/integrated-terminal
- https://learn.chatgpt.com/docs/sandboxing
- https://learn.chatgpt.com/docs/import
- https://learn.chatgpt.com/docs/personalize
- https://learn.chatgpt.com/docs/config-file/config-advanced
- https://learn.chatgpt.com/docs/enterprise/chatgpt-work-overview
- https://learn.chatgpt.com/docs/enterprise/manage-app-updates
- https://learn.chatgpt.com/docs/enterprise/windows-deployment

### 6.2 Codex 开发者入口（重定向到上表）

- https://developers.openai.com/codex/app
- https://developers.openai.com/codex/features
- https://developers.openai.com/codex/browser
- https://developers.openai.com/codex/computer-use
- https://developers.openai.com/codex/automations
- https://developers.openai.com/codex/worktrees
- https://developers.openai.com/codex/windows

### 6.3 本仓库与本机旁证

- `docs/SOURCE_MAP.md` — 官方 crate / App Server 方法对照与诚实 stub
- `README.md` — Honest stubs 表
- `src/CodexSharp.Desktop/MainView.fs` — 当前 FuncUI 能力
- 本机 `AppxManifest.xml`：DisplayName ChatGPT，协议 `codex`，Office/`.skill` 关联，端口 1455/1457
- 本机 `app/resources/plugins/openai-bundled/plugins/` — bundled plugin 名
- 本机 `app/resources/skills/skills/.curated/` — `hatch-pet`, `onboard-new-user`

文档冻结于 2026-09-07 / 26.901。日更 changelog 不自动跟踪。