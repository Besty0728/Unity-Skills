# Skill Check — C# 代码与 SKILL.md 文档一致性审计

你是 UnitySkills 项目的一致性审计助手。扫描所有 `[UnitySkill]` C# 定义与 `skills/*/SKILL.md` 文档，报告不一致问题。

## 目标

检测以下问题（这些是 v1.6.8 修复的那类 bug 的根源——文档声称支持的参数在代码中不存在）：

1. **幽灵 Skill**：SKILL.md 中记录了但 C# 代码中不存在的 Skill
2. **未文档化 Skill**：C# 中存在 `[UnitySkill]` 但 SKILL.md 中未记录的 Skill
3. **参数不一致**：SKILL.md 文档的参数表与 C# 方法签名不匹配（多余参数、缺失参数、类型不匹配）
4. **元数据缺失**：`[UnitySkill]` 特性中缺少 `Category`、`Operation`、`Tags`、`Outputs` 等关键元数据

## 步骤 1：收集 C# Skill 定义

扫描 `SkillsForUnity/Editor/Skills/*Skills.cs` 中所有 `[UnitySkill(...)]` 标记的方法：

1. 对每个 Skill 提取：
   - **Skill 名称**（`[UnitySkill("skill_name", ...)]` 第一个参数）
   - **方法签名**（参数名、类型、是否可选、默认值）
   - **元数据**：Category、Operation、Tags、Outputs、RequiresInput、ReadOnly
   - **所在文件和行号**

2. 汇总为 C# Skill 清单

## 步骤 2：收集 SKILL.md 文档定义

扫描 `SkillsForUnity/unity-skills~/skills/*/SKILL.md` 中所有记录的 Skill：

1. 对每个 SKILL.md 提取：
   - **Skill 名称**（`### skill_name` 标题）
   - **参数表**（`| Parameter | Type | Required | ...` 表格中的参数名和类型）
   - **所属模块**（目录名）

2. 汇总为文档 Skill 清单

> **注意**：跳过 Advisory 模块（architecture, patterns, performance, asmdef, async, inspector, blueprints, adr, project-scout, scene-contracts, script-roles, scriptdesign, testability, xr 中没有 REST Skills 的模块），它们没有对应的 C# Skill。

## 步骤 3：交叉比对

### 3a. Skill 名称比对

- 取 C# 清单和文档清单的差集：
  - `C# 有 ∩ 文档无` → **未文档化 Skill**
  - `文档有 ∩ C# 无` → **幽灵 Skill**（高风险：AI 会尝试调用这些不存在的 Skill）

### 3b. 参数签名比对

对两边都存在的 Skill，逐个比对参数：

- **文档多出的参数**（高风险）：文档声称支持但 C# 方法签名中没有 → AI 传参后被 SkillRouter 静默忽略
- **C# 多出的参数**（中风险）：C# 支持但文档未记录 → AI 不知道可以使用
- **类型不匹配**（低风险）：文档写 `string` 但 C# 是 `int` 等

> 参数比对时注意：C# 方法可能有 `= null`、`= 0`、`= false` 等默认值，这些对应文档中 `Required = No` 的参数。

### 3c. 元数据完整性检查

对每个 C# Skill 检查：
- `Category` 是否已设置（非默认值）
- `Operation` 是否已设置
- `Tags` 是否非空
- `Outputs` 是否非空（对有返回值的 Skill）

## 步骤 4：输出审计报告

按严重程度分级输出：

```
🔍 UnitySkills 一致性审计报告
━━━━━━━━━━━━━━━━━━━━━━━━━━━━

📊 统计
- C# Skills 总数：{N}
- 文档 Skills 总数：{M}
- 匹配：{X}

🔴 严重问题（AI 会被误导）

  幽灵 Skill（文档有，代码无）：
  - {module}/SKILL.md: `{skill_name}` — 文档声称存在但 C# 中未实现

  参数不一致（文档有，代码无）：
  - `{skill_name}`: 参数 `{param}` 在文档中声明但 C# 方法签名中不存在

🟡 中等问题（功能可用但文档不完整）

  未文档化 Skill（代码有，文档无）：
  - {file}:{line}: `{skill_name}` — C# 中存在但 SKILL.md 未记录

  未文档化参数（代码有，文档无）：
  - `{skill_name}`: 参数 `{param}` (C# 类型: {type}) 未在文档中记录

🟢 元数据缺失（建议补充）

  - {file}:{line}: `{skill_name}` — 缺少 {Category/Tags/Outputs/...}

━━━━━━━━━━━━━━━━━━━━━━━━━━━━
{问题总数} 个问题，其中 {严重} 个严重、{中等} 个中等、{低} 个建议
```

## 注意事项

- 这是**只读审计**，不修改任何文件
- 如果审计通过无问题，输出 `✅ 所有 Skill 定义与文档一致，无问题发现`
- 对于 batch 类 Skill（如 `gameobject_create_batch`），参数通常是 `string items`（JSON 数组），文档中以 `items` + Item properties 形式描述，这种情况视为一致
- `*_batch` 的 Item properties 与对应单个 Skill 的参数应保持一致，可作为额外检查项
- 大型审计可能需要读取大量文件，优先使用 Grep 批量提取而非逐文件读取
