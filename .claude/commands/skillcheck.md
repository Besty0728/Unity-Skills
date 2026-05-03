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
   - **Description 字符串**（`[UnitySkill("name", "description string")]` 第二个参数，完整保留）
   - **方法签名**（参数名、类型、是否可选、默认值）
   - **元数据**：Category、Operation、Tags、Outputs、RequiresInput、ReadOnly
   - **所在文件和行号**
   - **条件编译宏**：检查 Skill 方法是否位于 `#if XXX` 块内（如 `PROBUILDER`、`XRI`、`UNITY_NETCODE`、`CINEMACHINE_2`、`CINEMACHINE_3` 等），记录对应的宏名称；不在任何 `#if` 块内的标记为"无条件"
   - **返回值字段**：解析方法体中 `return new { ... }` 匿名对象的字段名列表（正则提取即可，不需要完美覆盖所有分支）

2. **Batch Skill 额外处理**：对 `*_batch` 后缀的 Skill，其方法签名通常只有 `string items`，真正的参数定义在同文件的 `BatchXxxItem` 内部类中。额外提取该类的所有 `public` 属性（属性名、类型、默认值），作为 batch skill 的"实际参数列表"。

3. 汇总为 C# Skill 清单

## 步骤 2：收集 SKILL.md 文档定义

扫描 `SkillsForUnity/unity-skills~/skills/*/SKILL.md` 中所有记录的 Skill：

1. 对每个 SKILL.md 提取：
   - **Skill 名称**（`### skill_name` 标题）
   - **参数表**（`| Parameter | Type | Required | ...` 表格中的参数名和类型）
   - **Batch Item Properties**（`**Item properties**:` 后列出的属性名列表）
   - **Returns 声明**（`**Returns**:` 后花括号内的字段名列表）
   - **所属模块**（目录名）

2. **额外提取**（按模块级别）：
   - **DO NOT 列表**：从 `## Guardrails` → `**DO NOT**` 区块中提取所有被声称"不存在"的 skill 名（如 `gameobject_move` / `gameobject_rotate` do not exist）
   - **Skills Overview 表格**：从 `## Skills Overview` 表格中提取所有列出的 skill 名

3. 汇总为文档 Skill 清单

> **注意**：动态识别 Advisory 模块并跳过。扫描每个 `skills/*/SKILL.md` 时，如果文档中**没有任何 `### skill_name` 格式的 Skill 端点定义**，则视为 Advisory 模块（纯架构/设计指导），自动跳过，不参与后续交叉比对。不要硬编码 Advisory 列表。

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

**Batch Skill 特殊处理**：对 `*_batch` Skill，不比对方法签名（固定为 `string items`），而是比对 `BatchXxxItem` 类的属性列表与文档中 `**Item properties**` 列出的属性名。规则同上：文档多出 → 高风险，C# 多出 → 中风险。同时检查 batch item 属性与对应单个 Skill 的参数是否一致（如 `gameobject_create` 有 `x,y,z` 但 `BatchCreateItem` 还有 `rotX,rotY,rotZ,scaleX,scaleY,scaleZ`，这种差异应标注但不算错误）。

### 3c. 元数据完整性检查

对每个 C# Skill 检查：
- `Category` 是否已设置（非默认值）
- `Operation` 是否已设置
- `Tags` 是否非空
- `Outputs` 是否非空（对有返回值的 Skill）

### 3d. Description 字符串一致性检查

`[UnitySkill]` 的 description 字符串是 AI 在 `/skills` 列表中看到的摘要，直接影响路由决策。检查：

- **Description 中提到的参数名**是否都存在于方法签名中（或 BatchItem 属性中）。例如 description 写 `{name, primitiveType, x, y, z}` 但方法实际还有 `parentName` 等 → 遗漏不算错误，但 description 提到了方法签名中不存在的参数 → 🟡 中等
- **Batch Skill 的 description** 中列出的 item 字段是否与 `BatchXxxItem` 类属性一致。例如 description 写 `{name, primitiveType, x, y, z, parentName}` 但 BatchItem 还有 `rotX, scaleX` 等 → 🟡 中等（遗漏关键参数）

### 3e. Returns / Outputs 一致性检查

三方交叉验证：

1. **Outputs 元数据 vs 文档 Returns**：`[UnitySkill]` 的 `Outputs = new[] { "field1", "field2" }` 与文档 `**Returns**: {field1, field2, ...}` 中的字段名比对
2. **C# 实际返回值 vs Outputs 元数据**：解析方法体中 `return new { ... }` 的字段名，与 `Outputs` 数组比对（正则提取，覆盖主路径即可，不要求 100% 覆盖所有分支）
3. 不一致标记为 🟡 中等（AI 依赖返回值做下一步决策，但不如参数不一致严重）

### 3f. DO NOT 列表验证（反向幽灵检查）

扫描每个 SKILL.md 的 `## Guardrails` → `**DO NOT**` 区块中声称"不存在"的 skill 名（如 `gameobject_move` do not exist），与 C# 实际 skill 名清单交叉验证：

- 如果 DO NOT 声称"不存在"的 skill **实际已存在于 C# 中** → 🔴 严重（AI 会被阻止调用一个真实存在的 Skill）
- 如果 DO NOT 声称"不存在"的 skill 确实不存在 → 正常，无问题

### 3g. Skills Overview 表格完整性

每个 SKILL.md 顶部的 `## Skills Overview` 表格应覆盖该模块所有 skill。检查：

- **Overview 中列出但模块实际没有的 skill** → 🟡 中等（误导读者）
- **模块实际有但 Overview 未列出的 skill** → 🟢 建议（不影响 AI 调用，但文档不完整）

## 步骤 4：输出审计报告

按严重程度分级输出：

```
🔍 UnitySkills 一致性审计报告
━━━━━━━━━━━━━━━━━━━━━━━━━━━━

📊 统计
- C# Skills 总数：{N}（无条件：{X}，条件编译：{Y}）
- 文档 Skills 总数：{M}
- 匹配：{X}
- Advisory 模块（自动跳过）：{列出跳过的模块名}

🔴 严重问题（AI 会被误导）

  幽灵 Skill（文档有，代码无）：
  - {module}/SKILL.md: `{skill_name}` — 文档声称存在但 C# 中未实现

  参数不一致（文档有，代码无）：
  - `{skill_name}`: 参数 `{param}` 在文档中声明但 C# 方法签名中不存在

  Batch Item 不一致（文档有，代码无）：
  - `{skill_name}`: Item 属性 `{prop}` 在文档中声明但 BatchXxxItem 类中不存在

  DO NOT 列表错误（声称不存在但实际存在）：
  - {module}/SKILL.md: DO NOT 声称 `{skill_name}` 不存在，但 C# 中已实现

🟡 中等问题（功能可用但文档不完整）

  未文档化 Skill（代码有，文档无）：
  - {file}:{line}: `{skill_name}` — C# 中存在但 SKILL.md 未记录

  未文档化参数（代码有，文档无）：
  - `{skill_name}`: 参数 `{param}` (C# 类型: {type}) 未在文档中记录

  Batch Item 未文档化属性（代码有，文档无）：
  - `{skill_name}`: BatchItem 属性 `{prop}` ({type}) 未在 Item properties 中记录

  Description 参数遗漏/错误：
  - `{skill_name}`: description 提到参数 `{param}` 但方法签名中不存在
  - `{skill_name}`: description 遗漏了 BatchItem 的 {N} 个属性

  Returns/Outputs 不一致：
  - `{skill_name}`: Outputs 声明 `{field}` 但文档 Returns 中未提及
  - `{skill_name}`: 文档 Returns 提到 `{field}` 但 Outputs 元数据中未声明

  Overview 表格多余条目：
  - {module}/SKILL.md: Overview 列出 `{skill_name}` 但该模块实际无此 Skill

🟢 建议（可改进项）

  元数据缺失：
  - {file}:{line}: `{skill_name}` — 缺少 {Category/Tags/Outputs/...}

  Overview 表格遗漏：
  - {module}/SKILL.md: Skill `{skill_name}` 未在 Overview 表格中列出

━━━━━━━━━━━━━━━━━━━━━━━━━━━━
{问题总数} 个问题，其中 {严重} 个严重、{中等} 个中等、{建议} 个建议
```

## 注意事项

- 这是**只读审计**，不修改任何文件
- 如果审计通过无问题，输出 `✅ 所有 Skill 定义与文档一致，无问题发现`
- 对于 batch 类 Skill（如 `gameobject_create_batch`），参数通常是 `string items`（JSON 数组），文档中以 `items` + Item properties 形式描述，这种情况视为一致。**真正的参数比对**应在 `BatchXxxItem` 类属性与文档 Item properties 之间进行
- `*_batch` 的 Item properties 与对应单个 Skill 的参数应保持一致，可作为额外检查项。但 batch 版本可能比单个版本多出属性（如 `gameobject_create_batch` 的 BatchItem 有 `rotX/scaleX` 而单个 `gameobject_create` 没有），这种"batch 扩展"标注但不算错误
- 大型审计可能需要读取大量文件，优先使用 Grep 批量提取而非逐文件读取
- **条件编译 Skill**：位于 `#if` 块内的 Skill 在报告中单独标注其依赖宏（如 `[需要 PROBUILDER]`），与无条件 Skill 区分展示。这些 Skill 在特定环境下可能不可用，但只要 SKILL.md 有对应文档就不算"未文档化"
- **DO NOT 列表解析**：只提取明确声称"do not exist"/"不存在"的 skill 名，忽略路由建议（如"use `component_add` instead"中的 `component_add` 不是 DO NOT 目标）
- **Returns 解析精度**：`return new { ... }` 的正则提取不要求覆盖所有代码路径（error 分支可忽略），只需覆盖主成功路径的返回字段
- **Overview 表格解析**：表格中的 skill 名可能出现在 markdown 代码标记内（如 `` `gameobject_create` ``），解析时去除反引号
