# Cycle Goal 提示

把 **一块** 贴进 Codex `/goal`。`Follow /goal-hop` 继续走现有 shipping hop。`docs/agents/milestone-cycle.md` 替换 hop G（空池 → architecture / retro / 下一周期），并把 grilling HITL 换成 Council。

不要把 skill 文件贴进去。在 Outcome 里写 **K**。Token budget 不算完成。

## K = 1 — 收口当前这一轮

当前实况（见 `docs/agents/cycle-status.md`）：C9 已 accepted。活动 Goal 点名 C10、C11、C12（K = 3）。C10 正在 chart。下面的 K = 1 块是上一 Goal 的示例，不是当前指令。不要开始 C13、M3、MX。不要预先写 docs/DESKTOP_C10.md。

```text
/goal Close 1 complete milestone cycle on CodexSharp. Follow /goal-hop. When hop G would block, follow docs/agents/milestone-cycle.md. Grilling uses Council in that file.

## Outcome
按 docs/agents/milestone-cycle.md 接受 Named cycle C9 — TERM-03（终端程序选择）：wayfinder map、功能切片合入、architecture map、债务票清空、retro 记入。不要重开 C1–C8 或 M1/M2 功能 issue。不要开始 C10。MX / section 5.2 WON'T 不是目的地。

权威 ID：docs/DESKTOP_REQUIREMENTS.md TERM-03。不要预先写 docs/DESKTOP_C9.md。

验收必须同时满足：
- docs/agents/cycle-status.md 将 C9 列为 accepted，并记下 architecture 与 retro
- 该周期没有开放的 ready-for-agent issue
- 默认分支上 ./build.ps1 Test 为绿

开放的 ready-for-human 票不在本 Goal 表面。
打到 token budget 不算完成。
```

## K = N — 收口 N 个周期

把 N 换掉。下一目的地必须由新 Goal 点名（不要整段替换成 §5.1 的 M3，也不要自行开 C13）。当前 Goal 已点名 C10、C11、C12。MX 不在范围；若 N 超出已点名列表则 Goal 停在 blocked。

```text
/goal Close N complete milestone cycles on CodexSharp. Follow /goal-hop. When hop G would block, follow docs/agents/milestone-cycle.md. Grilling uses Council in that file.

## Outcome
按 docs/agents/milestone-cycle.md 从 docs/agents/cycle-status.md 的当前周期起，接受 N 个周期。每次 continuation 只走一 hop。已经有 Goal 文件点名的周期不要重新 chart。MX / section 5.2 WON'T 不是目的地。

验收必须同时满足：
- docs/agents/cycle-status.md 列出 N 个 accepted 周期
- 这些周期没有开放的 ready-for-agent issue
- 默认分支上 ./build.ps1 Test 为绿

开放的 ready-for-human 票不在本 Goal 表面。
打到 token budget 不算完成。
```
