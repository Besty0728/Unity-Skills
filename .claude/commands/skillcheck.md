# Skill Check — C# 代码与 SKILL.md 一致性审计 + 技能数量同步

你是 UnitySkills 项目的一致性审计助手。扫描所有 `[UnitySkill]` C# 定义与 `skills/*/*.md` + `skills/*/reference/*.md` 文档（模块入口 `SKILL.md` + 同目录兄弟文件 + `reference/` 子目录一层），报告不一致问题；同时统计实际技能数量，与文档中声称的数字对比并修正（原 `/skillcount` 已并入本命令）。

## 目标

检测以下问题（这些是 v1.6.8 修复的那类 bug 的根源——文档声称支持的参数在代码中不存在）：

1. **幽灵 Skill**：SKILL.md 中记录了但 C# 代码中不存在的 Skill
2. **完全无文档的 Skill**：C# 中存在 `[UnitySkill]` 但在整个 `skills/` 文档树中**完全无提及**的 Skill（注意：本项目为 schema-first 设计——skill 无需逐个写 `### skill_name` 定义，故"无 `###` 定义"本身**不算缺陷**，详见步骤 3a）
3. **参数不一致**：SKILL.md 文档的参数表与 C# 方法签名不匹配（多余参数、缺失参数、类型不匹配）
4. **元数据缺失**：`[UnitySkill]` 特性中缺少 `Category`、`Operation`、`Tags`、`Outputs` 等关键元数据
5. **数量失同步**：文档（AGENTS.md / README / README_CN / SKILL.md）中声称的技能总数与模块计数表和实际代码不一致

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

2. **解析纪律（历史假绿源头，必须遵守）**：
   - **括号配平切分 attribute 块**：一个 skill 的元数据只能取它自己 `[UnitySkill(...)]` 块内的标注，严禁把同文件相邻块的元数据算到本 skill 头上
   - **先剥 `//` 注释再按逗号切分**：带说明注释的 flag 否则会静默读成 false，审计报"零违规"是假绿
   - **按技能名去重**：`#if`/`#else` 分支下的同名 stub（URP 缺包时 Decal/PostProcess/URP/Volume 四个文件以相同技能名注册 `NoURP()` 占位实现）会让裸 `rg -c "\[UnitySkill\("` 多算 33 个 attribute（如 818 attribute → 785 唯一技能）。**一切后续比对和计数都以"按第一个字符串参数去重后的唯一技能名"为准**
   - `SkillRouter.cs` 等非 `*Skills.cs` 文件中出现的 `[UnitySkill(` 多为注释/文档文字，不计入

3. **Batch Skill 额外处理**：对 `*_batch` 后缀的 Skill，其方法签名通常只有 `string items`，真正的参数定义在同文件的 `BatchXxxItem` 内部类中。额外提取该类的所有 `public` 属性（属性名、类型、默认值），作为 batch skill 的"实际参数列表"。

4. 汇总为 C# Skill 清单

## 步骤 2：收集 SKILL.md 文档定义

扫描 `SkillsForUnity/unity-skills~/skills/*/*.md` 与 `skills/*/reference/*.md` 中所有记录的 Skill。**范围是每个模块目录下的所有 `.md` 兄弟文件**（入口 `SKILL.md` + `REFERENCE.md` / `*_REFERENCE.md` 等）**加上 `reference/` 子目录一层**（四个试点模块 `gameobject` / `component` / `batch` / `script` 把逐技能章节拆成 `reference/<skill_name>.md`，每文件恰好一个 `### skill_name` 且文件名 = 技能名），不再往下递归。逐技能细节已经从入口下沉到 reference，只扫 `SKILL.md` 会让这些技能静默脱离比对：

1. 对每个模块的每个 `.md` 提取：
   - **Skill 名称**（`### skill_name` 标题）
   - **参数表**（`| Parameter | Type | Required | ...` 表格中的参数名和类型）
   - **Batch Item Properties**（`**Item properties**:` 后列出的属性名列表）
   - **Returns 声明**（`**Returns**:` 后花括号内的字段名列表）
   - **所属模块**（目录名）与**所在文件名**（报告问题时必须给出 `模块/文件名`，否则同名章节在两个文件里无法区分）

2. **额外提取**（按模块级别）：
   - **DO NOT 列表**：从 `## Guardrails` → `**DO NOT**` 区块提取被声称"不存在"的 skill 名。⚠️ 条目格式恒为 `` `幻觉名` does not exist → use `真实名` ``：**只提取箭头 `→`（或 `->`）左侧、紧邻 "does not exist"/"do not exist" 的 skill 名**；箭头右侧 "use `xxx`" 是推荐替代的**真实** skill，**必须排除**，绝不能当作"被声称不存在"。例如 `` `gameobject_move` / `gameobject_rotate` do not exist → use `gameobject_set_transform` ``：只取 `gameobject_move`、`gameobject_rotate`，排除右侧的 `gameobject_set_transform`。
   - **Skills Overview**：从入口 `SKILL.md` 的 `## Skills Overview` 节（表格或列表）中提取所有列出的 skill 名

3. 汇总为文档 Skill 清单（**保留每一次出现，不要按名字去重**——"同一技能被定义两次"本身就是要报的问题，按名字收进字典会让重复定义永远看不见）

> **注意：Advisory 模块的判定依据是 Category 归属，不是"有没有 `### skill_name`"。**
> 拆分入口/reference 之后，迁移过的 REST 入口本来就不再有 `###` 标题；而可选包模块在没装包的机器上反射不到任何技能——所以"没扫到技能"永远不能证明"这是 advisory"。正确判定：
>
> - 先按 `[UnitySkill]` 的实际归属建立 **模块目录 → SkillCategory** 映射：目录名与 `SkillCategory` 枚举名大小写不敏感相等即算命中；
> - 再叠加下面这张**例外表**（取自代码，不是猜的）：
>
>   | 模块目录 | 映射到的 Category | 声明处 | 原因 |
>   |---|---|---|---|
>   | `batch` | `Workflow` + `Validation` | `BatchSkills.cs` | 枚举里没有 `Batch`，该文件同时注册进两个 Category |
>   | `bookmark` | `Workflow` | `WorkflowSkills.cs` 的 `bookmark_*` | Workflow 类目下拆出的独立文档目录 |
>   | `history` | `Workflow` | `WorkflowSkills.cs` 的 `history_*` | 同上 |
>   | `importer` | `AssetImport` + `Audio` + `Texture` + `Model` | `AssetImportSkills.cs` / `AudioSkills.cs` / `TextureSkills.cs` / `ModelSkills.cs` | 四个导入设置类目共用一份文档 |
>
>   `Diagnose` **不需要条目**：`DiagnoseSkills.cs` 注册进 `SkillCategory.Debug`，而 `debug/` 目录已按名字命中；不存在 `diagnose/` 目录。
>
> - **命中映射 = REST 模块**；**未命中且在已知 advisory 名单内 = advisory，跳过交叉比对**；**未命中且不在名单内 = 🔴 错误**（要么是漏配 Category 的新模块，要么是目录名拼错）。
> - 反向也要查：某个 `SkillCategory` 没有任何模块目录承载文档 → 🟡 中等（AI 只能靠猜技能名才能碰到它）。
> - 这套判定与测试 `SkillDocModules_ShouldMapToCategoriesOrBeRegisteredAdvisory` 一致，例外表同步维护。

## 步骤 3：交叉比对

### 3a. Skill 名称比对

> **Schema-first 前提**：本项目文档采用 schema-first——精确的 skill 名/参数/返回由 `GET /skills/schema`（见各 SKILL.md 末尾 `## Exact Signatures` 节）提供，**模块文档无需为每个 skill 写 `### skill_name` 定义**。因此"C# 有但文档无 `###` 定义"是**预期正常态，不是缺陷**。这与项目自带测试 `SkillDocumentationConsistencyTests` 一致——它只校验幽灵 skill，从不校验"未文档化"。
>
> 拆分之后同样成立：`###` 定义在入口、同目录兄弟文件还是 `reference/<skill_name>.md` 都算"已定义"，**不得**因为入口里没有 `###` 就报缺陷，更不得要求"每个技能必须在入口完整定义"。

- 取 C# 清单和文档清单的差集：
  - `文档有 ∩ C# 无` → **幽灵 Skill** 🔴（唯一硬错误：AI 会尝试调用不存在的 Skill）。报告时给出 `模块/文件名:行号`。
  - `C# 有 ∩ 文档无 ###` → 仅作 🟢 **信息统计**（schema-first 下非问题），**不报 🟡 中等**。仅当某 skill 在整个 `skills/` 树中**完全无任何提及**（连 Route/Overview/参考文档都没有）时，才作为 🟢 建议提示补文档。

**重复定义检查（拆分后新增）**：同一个 skill 名只应被定义一次。

- **模块内重复** → 🔴 严重：同一模块的两个文件（典型是入口和 `reference/<skill>.md` 各留了一份）、或同一文件里出现两次 `### skill_name`。两份参数表会各自漂移且无人比对，这正是拆分最容易引入的缺陷。
- **跨模块重复** → 🟡 中等：不同模块目录各定义一次。当前已知且属于有意路由冗余的有 9 个，全部是 `workflow/SKILL.md` 重述了 `batch/`、`bookmark/`、`history/` 的技能（`batch_query_assets`、`batch_retry_failed`、`bookmark_set/goto/list/delete`、`history_undo/redo/get_current`）；这 9 个之外的跨模块重复要报出来。
- **试点模块全覆盖**（`gameobject`、`component`、`batch`、`script`）：这四个模块的文档技能集合应与代码技能集合**完全相等**（当前 19/14/22/12 全等）。任一方向缺项 → 🔴 严重（迁移丢技能）。注意 `batch` 的代码归属是 `BatchSkills.cs` 声明的技能，不是整个 `Workflow`+`Validation` 类目。
- **试点 reference 一一对应**：试点模块的每个 `reference/*.md` 必须恰好定义一个 `### skill_name` 且与文件名相同，模块的每个技能都要有 `reference/<skill_name>.md`（入口只写共享规则与技能列表，靠"细节在 `reference/<skill>.md`"一句寻址，不逐行放链接）→ 任一不符 🔴 严重。专题内容（示例、常用类型表等）并入相关技能的文件，不单独建 topic 文件。

### 3b. 参数签名比对

对两边都存在的 Skill，逐个比对参数。参数表可能位于入口、兄弟文件或 `reference/<skill_name>.md`，**都要扫**；同一技能若有多处出现，逐处比对（不要只比最后读到的那一处）：

- **文档多出的参数**（高风险）：文档声称支持但 C# 方法签名中没有 → AI 传参后被 SkillRouter 静默忽略
- **C# 多出的参数**（中风险）：C# 支持但文档未记录 → AI 不知道可以使用
- **类型不匹配**（低风险）：文档写 `string` 但 C# 是 `int` 等

> 参数比对时注意：C# 方法可能有 `= null`、`= 0`、`= false` 等默认值，这些对应文档中 `Required = No` 的参数。

**Batch Skill 特殊处理**：对 `*_batch` Skill，不比对方法签名（固定为 `string items`），而是比对 `BatchXxxItem` 类的属性列表与文档中 `**Item properties**` 列出的属性名。规则同上：文档多出 → 高风险，C# 多出 → 中风险。同时检查 batch item 属性与对应单个 Skill 的参数是否一致（如单体 `component_add` 接受路由合成的 `entityId`，而 `BatchAddComponentItem` 只有 `name/instanceId/path/componentType`，这种差异应标注但不算错误）。

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

> **背景**：曾出现 prefab 模块 9 个 skill 里 5 个 `Returns` 与代码漂移的案例——文档 `**Returns**` 声明的字段与 C# 方法体实际 `return new { ... }` 已经不一致。仅靠"以 Outputs 为中转"的两两比对，在 Outputs 元数据本身缺失、未更新、或审计时提取有误差的情况下，容易漏掉"文档 Returns 与实际返回值直接对不上"这一漂移，因此下述三条边必须**分别独立核对**，不能只做两两传递、省略第三边。

三方独立交叉验证（三条边缺一不可，不依赖 Outputs 单点中转推导出第三边）：

1. **Outputs 元数据 vs 文档 Returns**：`[UnitySkill]` 的 `Outputs = new[] { "field1", "field2" }` 与文档 `**Returns**: {field1, field2, ...}` 中的字段名比对
2. **C# 实际返回值 vs Outputs 元数据**：解析方法体中 `return new { ... }` 的字段名，与 `Outputs` 数组比对（正则提取，覆盖主路径即可，不要求 100% 覆盖所有分支）
3. **文档 Returns vs C# 实际返回值（直接比对）**：把文档 `**Returns**: {field1, field2, ...}` 与方法体 `return new { ... }` 的字段名直接对照，**不经 Outputs 中转**——这是唯一能抓出"Outputs 和 Returns 一起漂移、彼此表面仍然对齐"这类案例的手段
4. 任一边不一致标记为 🟡 中等（AI 依赖返回值做下一步决策，但不如参数不一致严重）

> **豁免 `entityId`**：`entityId` 由 `SkillRouter.GetEffectiveOutputs / GetSkillParameters / GetEffectiveDescription` 在 `/skills` manifest 层对所有含 `instanceId` 的 skill **自动注入**，因此**不需要在静态 `Outputs` 元数据中显式声明**。校验时遇到「C# `return new { ... entityId ... }` 含 `entityId`」或「文档 Returns 出现 `entityId`」而 `Outputs` 未声明的情况，**一律不算不一致**，跳过该字段（同理适用于 `parentEntityId` / `childEntityId` 等定位入参）。

### 3f. DO NOT 列表验证（反向幽灵检查）

扫描每个 SKILL.md 的 `## Guardrails` → `**DO NOT**` 区块，**只取箭头左侧声称"不存在"的 skill 名**（箭头方向规则见步骤 2.2），与 C# 实际 skill 名清单交叉验证：

- 如果某个**箭头左侧**声称"不存在"的 skill **实际已存在于 C# 中** → 🔴 严重（文档错误地否认了真实 skill）
- 否则正常，无问题

⚠️ **此处最易误判**：切勿把箭头右侧 "use `xxx`" 的推荐 skill 纳入校验——它们本就是真实存在的替代项。把右侧 skill 当成"被声称不存在但实际存在"会批量产出假阳性。正确预期：DO NOT 区块右侧推荐 skill 应 100% 真实存在、左侧幻觉 API 应 100% 不存在，故本项**正常结果为 0 误报**。

### 3g. Skills Overview 表格完整性

**Overview 留在入口 `SKILL.md`，逐技能 `###` 章节可以在 `REFERENCE.md` 或 `reference/<skill_name>.md`**——两者不在同一个文件是拆分后的正常形态，不要据此报错。Overview（表格或按用途分组的列表）应以完整技能名覆盖该模块所有 skill；试点入口**不逐行放 reference 链接**（round1 评测证明逐行链接会邀请整读），只用一句"细节在 `reference/<skill_name>.md`"寻址。检查：

- **Overview 中列出但模块实际没有的 skill** → 🟡 中等（误导读者）
- **模块实际有但 Overview 未列出的 skill** → 🟢 建议（不影响 AI 调用，但文档不完整）
- **寻址规则解析不了**（某技能缺 `reference/<skill_name>.md`）→ 🔴 严重，与 3a 的"试点 reference 一一对应"同口径（测试 `PilotReferenceFiles_ShouldEachDefineTheSkillTheyAreNamedAfter`）；入口里若仍有指向 reference 的链接，其断链归入 3k.8 统一报告

### 3h. Mode 元数据 ↔ 文档一致性（v1.9.0+）

针对 Skill 模式权限系统（见 `temp/skill-mode-permission-plan.md`），扫描所有 `[UnitySkill(...)]` 中的 `Mode = SkillMode.SemiAuto` 标注（默认 `FullAuto` 无需标注）。

1. 列出所有显式标注 `Mode = SkillMode.SemiAuto` 的 skill 名（含所在文件 & 行号）
2. 与下列文档来源比对：
   - `unity-skills~/skills/SKILL.md` 主索引 Mode 列的 `SA` 标注
   - 每个模块 `SKILL.md` 的 `## Guardrails` 区 `**Mode**` 字段
3. 标注差异：
   - **C# 标 SA 但文档无标注** → 🟡 中等（文档需补 SA 标注）
   - **文档标 SA 但 C# 未标 / 标了别的** → 🔴 严重（AI 看文档以为是 SemiAuto，实际走 FullAuto 流，Approval 模式下会无谓触发 grant）

### 3i. NeverInSemi 自动判定覆盖（v1.9.0+）

按 `SkillsModeManager.IsForbiddenInSemi()` 规则对所有 skill 自动判定（规则见方案第 8 节）：

```
满足以下任意一条即 NeverInSemi：
- Operation 含 Delete flag
- MayEnterPlayMode = true
- MayTriggerReload = true
- RiskLevel == "high"（大小写不敏感）
```

校验：

1. **覆盖统计**：自动判定为 NeverInSemi 的 skill 总数（当前约 75-79），按模块分组列出
2. **语义矛盾检测**：若某 skill 同时被 `Mode = SkillMode.SemiAuto` 手标 + 满足自动 NeverInSemi 判定 → 🔴 严重（必须移除其 SA 标注，或调整元数据让其不再满足 NeverInSemi 规则）

   ⚠️ **判定必须逐 `[UnitySkill(...)]` attribute 块进行**：一个 skill 的 `Mode`/`Operation`/`RiskLevel`/`MayEnterPlayMode`/`MayTriggerReload` 只能取**它自己 attribute 块内**的标注。严禁把同一 `*.cs` 文件中**其他** skill（尤其相邻块）的 `Operation=Delete` 等元数据"按文件"或"按相邻"算到本 skill 头上。典型误判：查询类 `xxx_find`/`xxx_list`/`xxx_get`（实为 `Operation=Analyze/Query` 且标了 `SemiAuto`）被同文件的 `xxx_delete` 污染成"含 Delete"。建议用括号配平精确切分每个 attribute 块。正确预期：真实矛盾通常为 0。

### 3j. /permission/* 端点存活校验（可选 — 需服务运行）

如果当前 Unity Editor + UnitySkills server 正在运行，发起以下 HTTP 检查（推荐用 `unity_skills.py` 客户端函数）：

1. `get_permission_status()` → 响应必须含字段 `mode`、`panelApprovalRequired`、`granted`、`pending`、`counts`
2. `get_server_status()`（`GET /health`）→ 必须含新字段 `currentMode`、`panelApprovalRequired`、`pendingCount`、`grantedCount`
3. `get_skills()`（`GET /skills`）→ 每条 skill entry 必须含 `mode` 字段（值为 `"semi"` 或 `"full"`）

任一字段缺失 → 🔴 严重（REST API 与文档/客户端不一致，AI 路由会出错）。

> 服务未运行时跳过本子步骤，在报告统计中标注「已跳过：服务离线」。

### 3k. SKILL.md Frontmatter 合规校验（Agent Skills 标准 + Codex/Claude 硬限）

> **背景**：Codex / Claude 等原生 skill 发现器把**每个含 `SKILL.md` 的子目录当成一个独立 skill** 注册，并在 discovery 阶段读取其 frontmatter `description` 做触发匹配。`description` 超过 **1024 字符**会被直接**拒绝加载**该 skill（典型报错 `Skipped loading N skill(s) due to invalid SKILL.md files`）。本项目真实调用走顶层入口 + `GET /skills/schema`，子 description 应保持精简，避免触发该硬限。

扫描 `unity-skills~/SKILL.md` 及所有 `unity-skills~/skills/**/SKILL.md`（含 REST 与 advisory 模块），逐文件校验 frontmatter。**`REFERENCE.md` / `reference/*.md` 等兄弟文件按设计没有 frontmatter，不纳入本项校验**（发现器只把含 `SKILL.md` 的目录注册为 skill），`.github/scripts/check_skill_frontmatter.py` 只 glob `skills/*/SKILL.md` 就是这个原因，不要"顺手"把它改成 `*.md`：

1. **`description` 长度 ≤ 1024 字符** → 超限 🔴 严重（该 skill 被发现器拒载，AI 完全看不到它）。注意按**字符数**（含中文）统计，非字节数。
2. **`name` 长度 ≤ 64 字符** → 超限 🔴 严重。
3. **YAML frontmatter 结构合法**：文件以 `---` 开头并正确闭合；`name` 与 `description` 两个必填键存在且非空 → 缺失 / 不闭合 → 🔴 严重。
4. **无 UTF-8 BOM**：文件开头不得有 `EF BB BF` 字节（`SkillInstaller.cs` 明确：BOM 会让部分 agent 拒析 frontmatter）→ 有 BOM → 🟡 中等。
5. **（信息项 🟢）discovery 总量**：累加所有 `SKILL.md` 的 `description` 字符数，提示总和是否逼近发现器 ~8000 字符软预算（超出时发现器可能截断或省略部分 skill）。
6. **根 SKILL.md 字节硬红线**：`unity-skills~/SKILL.md` ≤ **8,192 字节**（用户拍板的硬红线；v2.6.0 瘦身后长期在 8,1xx 徘徊）→ 超限 🔴 严重。**口径 = LF 归一化后的 UTF-8 字节数**，与 `RootSkillDoc_ShouldStayWithinByteBudget` 测试一致：`tr -d '\r' < SKILL.md | wc -c`，不要直接 `wc -c`——Windows 检出（`core.autocrlf=true`）会把 92 个换行写成 CRLF，同一份文档裸 `wc -c` 多出 92 字节（issue #59：8185 → 8277 假红）。报告中始终给出当前字节数与剩余余量；新增内容一律下沉 `references/` 或模块文档，不进根文件。
7. **行尾锁定（issue #59 回归闸）**：
   - 仓库根 `.gitattributes` 必须存在且含 `*.md text eol=lf`（缺失 → 🔴 严重：Windows UPM git 安装会在 `PackageCache` 检出 CRLF，用户无法自行修复）
   - `unity-skills~/**/*.md` 内不得含 `\r`（`rg -l $'\r' unity-skills~` 应为空；命中 → 🟡 中等，说明有文件以 CRLF 提交，`git add --renormalize .` 后重新提交）
   - 任何按字节/哈希度量文件的测试或脚本，必须先做 `\r\n`→`\n` 归一化再计数（新增此类校验时同样适用）
8. **本地链接可达性**：扫描 `unity-skills~/SKILL.md`、`unity-skills~/references/*.md`、`unity-skills~/skills/SKILL.md` 以及所有 `unity-skills~/skills/*/*.md` 中的**相对** markdown 链接 `[..](路径#锚点)`：
   - 目标文件必须存在 → 缺失 🔴 严重（AI 跟着链接读到 404）
   - 带锚点时，锚点必须匹配目标文件里某个标题的 slug。slug 规则：转小写、空格转 `-`、除 `-` 和 `_` 外的标点全部丢弃（于是 `` ### `gameobject_create` `` → `gameobject_create`）；同名 slug 依次加 `-1`、`-2`；标题末尾的显式 `{#custom-id}` 也算有效锚点 → 不匹配 🔴 严重
   - `http(s)://` 与 `mailto:` 跳过；**不含 `/`、不以 `.md` 结尾、也不以 `#` 开头的目标不是文档链接**，跳过（例如 ASCII 示意图里的 `[Camera](Overlay)` 是 markdown 语法巧合，不是链接）
   - 本项与测试 `SkillDocLinks_ShouldResolve` 同口径；该测试用 `KnownBrokenDocLinks` 登记了尚未修复的历史断链，审计时应把登记项一并列出并提醒修掉后删除条目
9. **试点模块入口字节预算**：`gameobject`、`component`、`batch`、`script` 四个模块的**入口** `SKILL.md` 各有独立上限，数值以测试 `SkillDocumentationConsistencyTests.PilotEntryByteBudgets` 为准（不要在本文件里另抄一份数字）：
   - 口径与根 SKILL.md 一致：**LF 归一化后的 UTF-8 字节数，含 frontmatter**（`tr -d '\r' < SKILL.md | wc -c`），不要裸 `wc -c`
   - 超限 🔴 严重。报告给出每个试点的当前字节数与余量
   - 登记值为 `-1` 表示预算尚未拍板，测试会**故意失败**；此时报告如实写"预算待定"，不要把它当成通过，也不要为了让测试变绿去填一个数字
   - 提高预算必须有回归证据（说明新增了哪些必要语义）；没有证据就把内容下沉到 `reference/`，不删必要内容换绿灯
   - `REFERENCE.md` / `reference/*.md` 不设预算（按需读取，不是每次调用的固定成本）

> 正常预期：0 项超限、0 BOM、0 CRLF、0 断链、`.gitattributes` 在位，试点预算在限内（或如实标注"预算待定"）。本项是防止"超 1024 拒载"与"CRLF 假红"两类 bug 复发的核心闸门。

## 步骤 4：技能数量统计与文档同步（原 /skillcount，唯一允许写文件的步骤）

基于步骤 1 **去重后**的唯一技能清单做数量统计，与文档声称的数字对比并修正。

### 4a. 统计口径（历史踩坑，必须遵守）

- **总数 = 唯一技能名数**，不是 attribute 出现次数。裸 `rg -c "\[UnitySkill\("` 会把 `#if/#else` 同名 stub 双算（Decal 7 + PostProcess 10 + URP 7 + Volume 9 = 33 个重复），**禁止**直接拿裸 grep 结果去改文档。
- **模块计数表按 `SkillCategory` 归组，不按文件**。枚举里没有 `Batch` 和 `Diagnose`：`BatchSkills.cs` 的技能拆进 Workflow + Validation，`DiagnoseSkills.cs` 的并入 Debug。按文件口径会误报 Workflow/Validation/Debug 三处"不一致"并"顺手"加出 Batch/Diagnose 两行——那是把正确的表改坏。
- **相邻数字各有定义，别互相"对齐"**：`*Skills.cs` 文件数（55）≠ 功能模块数（54，Diagnose 无独立模块文档）≠ SkillCategory 数（53）。三者不同是自洽的。
- **以工作区为基线**：若存在未提交变更，数量以工作区为准，不要拿 HEAD 复核后误改。

### 4b. 读取文档中的数字

| 文件 | 搜索内容 |
|------|---------|
| `AGENTS.md` | 架构行的 `56 *Skills.cs / 54 SkillCategory / 805 skills`（无模块计数表） |
| `README.md` | badge 数字、正文中的总数 |
| `README_CN.md` | 同上中文版 |
| `SkillsForUnity/unity-skills~/SKILL.md` | 总数引用 |

用 Grep 搜索文档中与实际总数不同的旧数字（如 `\d+ 个 REST`、`\d+ Skills`、`Skills-\d+` 等上下文）。

> ⚠️ **不修改** `CHANGELOG.md` 中的历史条目 — 那些是版本发布时的快照。

### 4c. 对比并修正

如果实际总数与文档不一致：

1. **替换总数**：将文档中所有旧总数替换为实际统计总数
2. **更新模块计数表**：同步 `README.md` 和 `README_CN.md` 中的分类概要表（SkillCategory 口径；`AGENTS.md` 不再维护该表）

替换时注意上下文匹配，避免误替换（如版本号中的数字）。修正后用 `rg -n "{旧数字}"` 验证旧数字不再出现（非技能计数上下文除外）。

如果一致：不改任何文件，报告中输出 `✅ 所有文档中的技能数量已是最新（{N} Skills）`。

## 步骤 5：输出审计报告

按严重程度分级输出：

```
🔍 UnitySkills 一致性审计报告
━━━━━━━━━━━━━━━━━━━━━━━━━━━━

📊 统计
- C# Skills 总数：{N} 唯一（attribute {M} 个，#if/#else 同名 stub 去重 {M-N}）；无条件：{X}，条件编译：{Y}
- 文档 Skills 总数：{M}（`###` 出现 {occ} 次 / 去重 {M}；入口/兄弟文件 {a} + reference/ {b}）
- 匹配：{X}
- Advisory 模块（未映射到任何 Category，自动跳过）：{列出跳过的模块名}
- 模块归属：{✅ 全部目录可判定（REST {R} / advisory {A}） / 🔴 N 个目录既无 Category 也未登记}
- 重复定义：{✅ 0 模块内重复，跨模块 {K} 个均属已知冗余 / 🔴 明细}
- 试点全覆盖（gameobject/component/batch/script）：{✅ 19/19、14/14、22/22、12/12 / 🔴 明细}
- 试点 reference 一一对应：{✅ 67 个文件各定义同名技能、无缺失 / 🔴 明细}
- Mode = SemiAuto 标注：{N}（C# 显式手标）
- NeverInSemi 自动判定：{N}（纯元数据规则，无兜底名单）
- /permission API 校验：{已通过 / 已跳过：服务离线 / N 项失败}
- Frontmatter 合规：{通过（0 超限）/ N 项超限}（最长 description：{module} {len} 字符；discovery 总量：{sum} / ~8000 软预算）
- 根 SKILL.md 预算：{N} / 8192 字节（LF 归一化口径，余量 {8192-N}）
- 试点入口预算：gameobject {N}/{budget}、component {N}/{budget}、batch {N}/{budget}、script {N}/{budget}（LF 归一化含 frontmatter；`-1` = 预算待定，测试故意失败）
- 本地链接：{✅ {L} 条相对链接全部可达 / 🔴 N 条断链}（含 KnownBrokenDocLinks 登记 {K} 条）
- 行尾锁定：{✅ .gitattributes 在位、0 个 CRLF 文档 / 🔴 .gitattributes 缺失 / 🟡 N 个 CRLF 文件}

📊 数量同步（原 /skillcount）
- 实际总数：{N}（SkillCategory 口径 {K} 个分类）
- 文档核对：{✅ 全部一致（未改文件） / 已修正 {列出文件}，旧值 {old} → {new}}
- 模块计数表：{✅ 逐行匹配 / 修正明细}

🔴 严重问题（AI 会被误导）

  幽灵 Skill（文档有，代码无）：
  - {module}/{file}:{line}: `{skill_name}` — 文档声称存在但 C# 中未实现

  模块内重复定义（同一 skill 定义了两次）：
  - `{skill_name}`: {module}/SKILL.md:{line} 与 {module}/reference/{skill_name}.md:{line} 各定义一次 — 两份参数表会各自漂移

  模块归属未知（既无 Category 也未登记 advisory）：
  - {module}/ — 补 SkillCategory / 登记例外表映射 / 登记为 advisory，三选一

  静默零比对（REST 模块一个 `###` 都没有且未登记 schema-first）：
  - {module}/ — 迁移是否丢了逐技能章节？

  试点模块丢技能：
  - {module}: 代码有 `{skill_name}`，模块所有 *.md 中均无对应 `###` 章节

  试点 reference 不对应：
  - {module}/reference/{file}: 定义了 {N} 个 `###` 段 / 定义的是 `{other}` 而非同名技能
  - {module}: `{skill_name}` 缺 reference/{skill_name}.md

  链接断裂（AI 跟着读到 404）：
  - {file} -> {target} — 目标文件不存在 / 目标中无此锚点

  试点入口超预算：
  - {module}/SKILL.md: {N} 字节 > {budget} 字节 — 把内容下沉 reference/<skill_name>.md，不要无证据抬预算

  参数不一致（文档有，代码无）：
  - `{skill_name}` ({module}/{file}): 参数 `{param}` 在文档中声明但 C# 方法签名中不存在

  Batch Item 不一致（文档有，代码无）：
  - `{skill_name}`: Item 属性 `{prop}` 在文档中声明但 BatchXxxItem 类中不存在

  DO NOT 列表错误（声称不存在但实际存在）：
  - {module}/SKILL.md: DO NOT 声称 `{skill_name}` 不存在，但 C# 中已实现

  Mode 文档失同步（文档标 SA, 代码未标）：
  - {module}/SKILL.md: `{skill_name}` 文档标 Mode=SemiAuto 但 C# 中实际为 FullAuto

  Mode 语义矛盾（手标 SA + 自动 NeverInSemi）：
  - `{skill_name}` 被 `Mode = SkillMode.SemiAuto` 手标，但同时满足 IsForbiddenInSemi 自动判定（Operation 含 Delete / MayEnterPlayMode / ...）

  /permission API 字段缺失（仅服务运行时校验）：
  - `GET /permission/status` 响应缺少字段 `{counts/pending/...}`
  - `GET /health` 响应缺少字段 `{currentMode/panelApprovalRequired/...}`
  - `GET /skills` 部分 entry 缺少字段 `mode`

  Frontmatter 超限（发现器会拒载该 skill）：
  - {module}/SKILL.md: `description` 长度 {len} 字符 > 1024 — Codex/Claude 跳过加载，AI 看不到此 skill
  - {module}/SKILL.md: `name` 长度 {len} 字符 > 64
  - {module}/SKILL.md: frontmatter 缺少必填键 `{name/description}` 或 `---` 未闭合

  行尾锁定缺失：
  - 仓库根 `.gitattributes` 不存在或未含 `*.md text eol=lf` — Windows 检出会让根 SKILL.md 字节预算测试假红（issue #59）

🟡 中等问题（功能可用但文档不完整）

  完全无文档的 Skill（代码有，整个 skills/ 树无任何提及）：
  - {file}:{line}: `{skill_name}` — C# 存在但文档树完全未提及（schema-first 下仅此种才报；"无 ### 定义"不报）

  跨模块重复定义（超出 9 条已知冗余）：
  - `{skill_name}`: 同时定义在 {moduleA}/{file} 与 {moduleB}/{file}

  Category 无文档承载：
  - SkillCategory `{Category}` 没有任何模块目录承载文档 — AI 只能靠猜技能名才碰得到

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

  Mode 文档遗漏（代码标 SA, 文档未标）：
  - {module}/SKILL.md: `{skill_name}` C# 中标了 Mode=SemiAuto 但文档 Guardrails 中未注明

  Frontmatter 编码问题：
  - {module}/SKILL.md: 文件含 UTF-8 BOM（EF BB BF）— 部分 agent 会拒析 frontmatter，应存为 UTF-8 无 BOM
  - {path}: 文档含 CRLF 行尾 — 以 CRLF 提交，`git add --renormalize .` 后重新提交

🟢 建议（可改进项）

  元数据缺失：
  - {file}:{line}: `{skill_name}` — 缺少 {Category/Tags/Outputs/...}

  Overview 表格遗漏：
  - {module}/SKILL.md: Skill `{skill_name}` 未在 Overview 表格中列出

  NeverInSemi 自动覆盖统计：
  - 自动判定 {N} 个 skill 为 NeverInSemi（按模块分组：{module1: K1, module2: K2, ...}）

━━━━━━━━━━━━━━━━━━━━━━━━━━━━
{问题总数} 个问题，其中 {严重} 个严重、{中等} 个中等、{建议} 个建议
数量同步：{✅ 一致 / 已修正 N 处}
```

## 注意事项

- **审计部分（步骤 1–3）是只读的**；唯一允许修改文件的是步骤 4 的数量同步（且仅限 AGENTS.md / README.md / README_CN.md / unity-skills~/SKILL.md 四个文件中的数量引用），不修改 C# 代码，不自动 `git commit`，只提示用户审阅后提交
- 如果审计通过且数量一致，输出 `✅ 所有 Skill 定义与文档一致，数量已同步（{N} Skills），无问题发现`
- 对于 batch 类 Skill（如 `gameobject_create_batch`），参数通常是 `string items`（JSON 数组），文档中以 `items` + Item properties 形式描述，这种情况视为一致。**真正的参数比对**应在 `BatchXxxItem` 类属性与文档 Item properties 之间进行
- `*_batch` 的 Item properties 与对应单个 Skill 的参数应保持一致，可作为额外检查项。但 batch 版本可能与单个版本字段不同（如 `component_*_batch` 的 item 没有单体可用的合成 `entityId`，`script_create_batch` 的 item 多一个别名 `namespace`），这种差异标注但不算错误
- 大型审计可能需要读取大量文件，优先使用 Grep 批量提取而非逐文件读取
- **入口/reference 拆分**：试点模块文档是 `SKILL.md`（入口：跨技能共享事实与守则、技能列表、`## Exact Signatures`，不链接相邻模块文档）+ `reference/<skill_name>.md`（每技能一个文件：参数表在前，然后 Item properties、Returns、陷阱与示例）；其他模块仍可用单文件或 `REFERENCE.md`。扫描一律覆盖模块目录下所有 `.md` 与 `reference/` 一层；**不得**要求技能必须定义在入口，也不得因为入口没有 `###` 就判定为 advisory。相关测试：`SkillDocumentationConsistencyTests` 的 `SkillDocModules_ShouldMapToCategoriesOrBeRegisteredAdvisory`、`PilotSkillDocs_ShouldDocumentEverySkillOfTheirModule`、`PilotReferenceFiles_ShouldEachDefineTheSkillTheyAreNamedAfter`、`SkillDocLinks_ShouldResolve`、`PilotSkillDoc_ShouldStayWithinByteBudget`
- **条件编译 Skill**：位于 `#if` 块内的 Skill 在报告中单独标注其依赖宏（如 `[需要 PROBUILDER]`），与无条件 Skill 区分展示。这些 Skill 在特定环境下可能不可用，但只要 SKILL.md 有对应文档就不算"未文档化"
- **DO NOT 列表解析**：只提取明确声称"do not exist"/"不存在"的 skill 名，忽略路由建议（如"use `component_add` instead"中的 `component_add` 不是 DO NOT 目标）
- **Returns 解析精度**：`return new { ... }` 的正则提取不要求覆盖所有代码路径（error 分支可忽略），只需覆盖主成功路径的返回字段
- **Overview 表格解析**：表格中的 skill 名可能出现在 markdown 代码标记内（如 `` `gameobject_create` ``），解析时去除反引号
