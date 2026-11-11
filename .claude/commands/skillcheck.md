---
description: C# [UnitySkill] 与模块文档一致性审计 + 技能数量同步（脚本驱动）
allowed-tools: Bash(python3 .github/scripts/skill_inventory.py:*), Read, Grep, Glob
---

# Skill Check — 脚本跑机械比对，你只做判断

增删改技能、改模块文档后运行。**不要手读 `*Skills.cs` 和 `skills/` 文档树**：机械比对全部由 `.github/scripts/skill_inventory.py` 完成，你读它的报告，只处理脚本做不了的判断。

## 流程

1. `python3 .github/scripts/skill_inventory.py`（每个 finding code 默认列前 15 条；要全量用 `--all`，要结构化用 `--json`）。
2. 读报告，按下文分级处理 finding。
3. 计数有漂移（报告里 `count anchors: DRIFT`）：先确认实际数字合理，再 `python3 .github/scripts/skill_inventory.py --sync-counts`，重跑一次确认 `in sync`。
4. 按"报告格式"向用户汇总。

脚本在 PyYAML 缺失时跳过 frontmatter 检查并给出 `frontmatter-skipped` warning；此时提示用户 `pip install pyyaml` 后重跑，不要手写替代检查。

## 脚本已覆盖（不要重复人工核对）

- 计数：唯一技能名（`#if/#else` 同名 stub 去重）、`*Skills.cs` 文件数、`SkillCategory` 数（不含 `Uncategorized`）、模块文档数、advisory 数；`AGENTS.md` / `README.md` / `README_CN.md` 的计数锚点与分类表逐行核对。
- 元数据：缺 `Category` / `Operation`（error）、缺 `Tags` / `Outputs`（info）、手标 `SemiAuto` 却满足 NeverInSemi 规则（error）。
- 文档 ↔ 代码：幽灵技能、模块内重复定义、跨模块重复（相对测试里登记的已知冗余）、文档多出/漏记的参数与 batch item 属性、Returns ↔ Outputs ↔ 方法体 `return new {}` 三边比对、`**DO NOT**` 否认了真实技能、Skills Overview 条目、文档声称 SemiAuto 而代码不是。
- 试点模块（`gameobject` / `component` / `batch` / `script` / `material` / `editor` / `cinemachine` / `particle` / `workflow`，名单取自测试）：文档技能集与代码完全相等、`reference/<skill>.md` 一一对应、入口字节预算。
- 模块归属：目录是否映射到 `SkillCategory` 或登记为 advisory；无文档承载的 Category。
- 文档卫生：根 `SKILL.md` ≤ 8,192 字节（LF 归一化）、`.gitattributes` 行尾锁定、CRLF、相对链接与锚点、`references/` 可达性、frontmatter（复用 `check_skill_frontmatter.py`）。

advisory 名单、Category 例外表、试点名单与预算、已知冗余、已知断链都由脚本直接从 `SkillDocumentationConsistencyTests.cs` 解析，唯一来源在测试里；需要改它们就改测试，不要在本文件或脚本里另抄一份。

## 你来判断的部分

- **error**：全部如实报告；动手前先读对应 C# 或文档确认。`mode-doc-vs-code` 来自对文档散文的启发式解析，读原句确认再报。
- **warning**：逐条判断是真漂移还是预期形态。常见误报：`Outputs` 写了嵌套字段（如列表元素里的 `instanceId`）；批量接口的别名属性；文档有意只写非定位参数。真漂移才进汇总。
- **info**：只汇总数量。
- **description 质量**（脚本不查）：对本次改动涉及的技能，读 `[UnitySkill]` 描述，核对其中提到的参数确实存在（batch 技能对照 item 类属性）、首句对 AI 有路由价值。
- **服务在线时**（可选）：`unity_skills.py` 的 `get_permission_status()`、`get_server_status()`、`get_skills()`，核对 `mode` / `currentMode` / `panelApprovalRequired` 等字段存在；离线则在报告里写"已跳过：服务离线"。

## 不要做

- 本项目文档是 schema-first：C# 有而文档无 `###` 定义**不是缺陷**，脚本也不会报。不要要求每个技能都在入口里完整定义。
- advisory 的判据是测试里的注册名单，不是"有没有 `###`"。没扫到技能不能证明是 advisory。
- 计数口径：按唯一技能名；分类表按 `SkillCategory` 归组，不按文件。不要给分类表加 `Batch` / `Diagnose` 行（枚举里没有，分别并入 Workflow+Validation 与 Debug）。相邻数字各有定义，别互相"对齐"。数量以工作区为准。
- 根 `unity-skills~/SKILL.md` 不写计数，也不要加回；新增内容下沉 `references/` 或模块文档。
- 不要为让预算变绿而抬试点预算或删必要内容；预算为 `-1` 表示尚未拍板，如实报"预算待定"。
- 模块入口文件名是 `MODULE.md`，索引是 `skills/INDEX.md`。不要在 `unity-skills~/skills/` 下新增名为 `SKILL.md` 的文件（发现器会把每个 `SKILL.md` 注册成独立 skill，`check_skill_frontmatter.py` 会拦）。
- 不改 `CHANGELOG.md` 历史条目，不改 C# 与文档内容（除非用户要求修复），不 `git commit`。唯一自动写入是 `--sync-counts`，范围仅 `AGENTS.md` / `README.md` / `README_CN.md` 的计数。

## 报告格式

```
Skill Check：{N} 技能 / {F} 个 *Skills.cs / {C} 个 SkillCategory / {D} 个模块文档（advisory {A}）
根 SKILL.md：{bytes}/8192；计数：{一致 / 已同步 {文件}}
严重 {x}：每条一行，带 file:line 和一句原因
警告 {y}：只列经你判断为真问题的，其余一句"{k} 条已核对为预期形态"
信息 {z}
需要用户决定：{…}
```

全绿时一行即可：`所有 Skill 定义与文档一致，数量已同步（{N} Skills）`。
