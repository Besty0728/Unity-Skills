# 操作模式与治理层

> 本文是 [README](../README_CN.md) 中「🔐 操作模式」与「🛡️ 治理层」两节的完整版说明。English version: [Operating Modes & Governance](OPERATING_MODES.md)

## 🔐 操作模式 (v1.9.0+)

UnitySkills 引入真正的服务端权限系统，对齐 Claude Code permission modes。模式切换统一在 Unity 面板完成——打开 **Window > UnitySkills**，点 ⚙（设置）按钮进入 **Server** 区——**不再支持对话触发词**（如 `"全自动模式"` / `"semi-auto"`）。

| 模式 | 默认 | 行为 | 适用场景 |
|:-----|:----:|:-----|:---------|
| **Approval（审批）** | — | AI 想做事 → 服务端返回 `MODE_RESTRICTED` + grant token → 用户审批 → AI 重放 token 后执行 | 重控制、敏感项目 |
| **Auto（自动）** | 新安装 | AI 直接执行 FullAuto skill；服务端仅拦自动判定的高危操作（NeverInSemi） | 日常开发 |
| **Bypass（全部直接放行）** | 老安装升级保持 | 全部放行，仅保留高危 `ConfirmationToken` 二次确认 | 自动化任务、CI、快速迭代 |

### Approval 模式双轨审批

- **Dialog 渠道**（默认）—— AI 对话说明意图 + grant token，用户文字同意后 AI 调 `POST /permission/grant` 重放
- **Panel 渠道**（面板可选开启）—— grant token 必须在 Unity 面板点 **[Approve]** 才生效；AI 未经面板批准直接 grant 会返回 `GRANT_PENDING_APPROVAL`

### 老用户升级零感知

插件检测旧版 `UnitySkills_*` EditorPrefs key 自动识别老安装，默认保持 **Bypass**，行为与原 Full-Auto 完全一致，无需任何操作。新安装默认 **Auto** —— FullAuto skill 直接执行，仅 NeverInSemi（Delete / MayEnterPlayMode / MayTriggerReload / 高危）操作会被服务端拦截。若需要按 skill 手动审批，在 ⚙ 设置抽屉的 Server 区切到 **Approval**。

### 审计日志

`Library/UnitySkillsAudit.jsonl`（per-project，jsonl，1MB 滚动，保留 3 份），记录每次 grant / revoke / 被拒命中 / 调用。从 ⚙ 设置抽屉 → **查看审计日志**（或快捷键 <kbd>Alt</kbd>+<kbd>Shift</kbd>+<kbd>L</kbd>）打开浏览器，可浏览、过滤、单条删除（✕）或整体清空（🗑 Clear All）—— 删除动作本身会写 `audit_deleted` / `audit_cleared` 追踪事件，日志依然可审计。

### Skill Installer 卸载按钮

Skill Installer 卡片的"卸载"按钮按 scope 智能形变：未装为灰态；仅一处装则按钮自带 scope 标签直接卸载；两处都装则显示 `Uninstall ▾` 下拉，分别选择 Project / Global。

### 安装器落盘文件

一键安装（**AI Config** 标签页 → **Install**）会复制包内的 `unity-skills~/` 模板目录到目标位置，生成：

- `SKILL.md`
- `skills/`
- `references/`
- `scripts/unity_skills.py`
- `scripts/agent_config.json`（包含 Agent 标识）

升级插件后，已安装过的目标会自动同步到新版本（不会自动安装新目标），可在 ⚙ 设置抽屉的 **AI 工具** 区关闭。

### Advisory 模块

28 个 advisory 设计模块（架构、性能、设计模式、可测试性、包级源码规则等）在所有模式下均可用，按需自动加载。

## 🛡️ 治理层：调用生命周期的四个节点

AI 驱动编辑器，写的是真实的场景、Prefab 和 `.meta` 文件。真正的问题不是"它能不能做到"，而是"它做错时会发生什么"。UnitySkills 在调用生命周期的四个节点上回答这个问题。

- **执行前 —— `?mode=dryRun` / `?mode=plan` 预演**：`POST /skill/{name}?mode=dryRun` 不落地任何改动，只返回参数校验（`missingParams` / `unknownParams` / `typeErrors` / `semanticErrors` / `warnings`）与影响预估（`mutatesScene` / `mutatesAssets` / `mayTriggerReload` / `mayEnterPlayMode` / `riskLevel`）；有语义 planner 的 skill 还会返回 `steps` / `changes`。
- **执行时 —— 操作级风险拦截**：每个 skill 在 `[UnitySkill]` 元数据里声明 `RiskLevel` / `Operation` / `MayEnterPlayMode` / `MayTriggerReload`，服务端据此自动判定 NeverInSemi——拦不拦从不取决于 AI 是否自觉。**Allowlist** 可为单条 skill 持久放行；可选的 `ConfirmationToken` 二次确认（默认关闭，⚙ 设置 → Runtime → Require Confirmation）为高危 skill 再加一道闸。
- **执行后 —— JSONL 审计留痕**：每次调用、授权、撤销、被拦命中都追加到 `Library/UnitySkillsAudit.jsonl`（1MB 滚动，主文件 + 3 份历史），可在面板内浏览与过滤；删除审计条目这个动作本身也会以 `audit_deleted` / `audit_cleared` 入账。
- **出错后 —— 类型化持久快照回滚**：Workflow 快照分 `Modified` / `Created` / `Deleted` / `Moved` / `Setting` 五类，主文件与 `.meta` 各自独立内容寻址（`fileHash` / `metaFileHash`）落在 `Library/UnitySkills/`，跨 Domain Reload 与编辑器重启存活。`workflow_undo_task` 回退的是一个任务，不是整个项目。
- **批量即事务**：`POST /skills/batch` 支持 fail-fast 或 `continueOnError`、跨步 `$ref` 引用前序步骤输出、失败回滚，以及 `?diff=1` 返回聚合后的净变化。
