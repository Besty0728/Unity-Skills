# UnitySkills — 项目开发者速览

> **本文件面向"开发这个项目的 AI"**，非"调用该项目 REST API 的 AI"。
> 后者请读 `SkillsForUnity/unity-skills~/SKILL.md`。

通过 REST API 让 AI 直接控制 Unity 编辑器。805 个 REST Skills + 28 个 Advisory 模块。

| 项目 | 值 |
|------|----|
| 版本 | 2.8.0 |
| 技术栈 | C# (Unity Editor Plugin) + Python (Client) |
| Unity | 2022.3+（已验证 Unity 6 / 6000.x） |
| 协议 | MIT |
| 包名 | com.besty.unity-skills |

---

## 架构

```
AI Agent ──HTTP──▶ unity_skills.py ──POST localhost:8090-8100──▶ Unity Editor Plugin
                                                                    │
                                              SkillsHttpServer (Producer-Consumer)
                                                        │
                                              SkillRouter (反射发现 [UnitySkill])
                                                        │
                                              56 个 *Skills.cs → 54 个分类 (805 Skills)
                                                        │
                                         WorkflowManager (持久化撤销/回滚)
                                         RegistryService (多实例发现)
```

**线程模型（硬约束）**：HTTP 线程仅入队，Unity 主线程通过 `EditorApplication.update` 消费。**零 Unity API 跨线程调用**——任何在 HTTP 线程上直接触发 `UnityEngine.*` / `UnityEditor.*` 的改动都属违规。

---

## 项目结构

```
Unity-Skills/
├── SkillsForUnity/                       # UPM Package (com.besty.unity-skills)
│   ├── package.json
│   ├── Editor/
│   │   ├── Skills/                       # Skill 业务层 + Server 内核
│   │   │   ├── SkillsHttpServer.cs       # HTTP 服务器 (Producer-Consumer)
│   │   │   ├── SkillRouter.cs            # 反射路由 + 参数绑定
│   │   │   ├── SkillPlanningService.cs   # SkillRouter 内部预演引擎 (/plan /dryRun + 参数语义校验，非 skill，无 [UnitySkill])
│   │   │   ├── UnitySkillAttribute.cs    # [UnitySkill] 特性 (元数据契约)
│   │   │   ├── SkillErrorResponse.cs     # 统一错误响应构建器
│   │   │   ├── SkillErrorCode.cs         # 错误码枚举
│   │   │   ├── SkillsLogger.cs           # 日志 + 版本常量唯一源 (Version)
│   │   │   ├── SkillsModeManager.cs      # 操作模式 (Approval/Auto/Bypass)
│   │   │   ├── SkillsAuditLog.cs         # 审计日志 (JSONL)
│   │   │   ├── ConfirmationTokenService.cs  # 高危确认 token
│   │   │   ├── WorkflowManager.cs        # 持久化工作流
│   │   │   ├── RegistryService.cs        # 全局多实例注册表
│   │   │   ├── GameObjectFinder.cs       # 统一查找器 (name/instanceId/path)
│   │   │   ├── BatchExecutor.cs          # 批量操作框架
│   │   │   ├── SkillInstaller.cs         # AI 工具一键安装
│   │   │   ├── UnityCliService.cs        # Unity CLI 检测 + 项目绑定 (Library/UnitySkills/cli_config.json)
│   │   │   └── *Skills.cs × 56           # 54 个 SkillCategory 分类 (共 805 Skills)
│   │   ├── Locales/                      # 独立多语言 JSON 资产 (en.json, zh-CN.json, ru.json)
│   │   └── UI/                           # Editor UI (USS + UXML + EditorWindow)
│   │       ├── UnitySkillsWindow.{cs,uxml,uss}    # 主窗口
│   │       ├── AuditLogWindow.{uxml,uss}          # 审计窗口
│   │       ├── AllowlistPickerWindow.{uxml,uss}   # 白名单挑选
│   │       ├── UnityCliWindow.{cs,uxml,uss}       # Unity CLI 配置二级面板
│   │       └── Tabs/*.uxml                        # 标签页与抽屉
│   └── unity-skills~/                    # AI Skill 模板（波浪线隐藏，随包分发）
│       ├── SKILL.md                      # 调用方文档（"用"项目）
│       ├── scripts/unity_skills.py
│       ├── skills/                       # 82 个模块文档 (54 REST + 28 advisory)
│       └── references/
├── .claude/commands/                     # 自定义命令
├── docs/SETUP_GUIDE.md
├── CHANGELOG.md
└── agent.md                              # 本文件（"写"项目）
```

---

## 编写约束

### 1. Editor UI：USS + UXML（硬约束）

- **禁止 IMGUI**：不允许 `OnGUI` / `EditorGUILayout` / `GUILayout` / `Editor.OnInspectorGUI` 等任何 IMGUI API。已有窗口全部基于 UI Toolkit，新增 UI 必须沿用。
- **文件三件套**统一放 `Editor/UI/`：`XxxWindow.cs` + `XxxWindow.uxml` + `XxxWindow.uss`。
- **加载范式**（参考 `UnitySkillsWindow.cs`）：
  1. 用 `Packages/com.besty.unity-skills/Editor/UI/Xxx.{uxml,uss}` 绝对包路径常量
  2. `CreateGUI()` 内先 `styleSheets.Add(uss)` 再 `uxml.CloneTree(rootVisualElement)`
  3. 节点用 `rootVisualElement.Q<T>("element-name")` 获取
  4. 周期刷新走 `schedule.Execute(...).Every(ms)`，**禁止** `EditorApplication.update` 轮询 UI
- **复杂窗口拆 Controller**：每个 UXML 子树由一个 `XxxController(VisualElement root, EditorWindow owner)` 接管，主窗口仅做组装与生命周期；参考 `TopbarController` / `SkillsTabController` / `SettingsDrawerController`。
- **Window 菜单单入口（用户拍板）**：`[MenuItem("Window/UnitySkills")]` 是叶子项且全项目唯一——Unity 里叶子与 `Window/UnitySkills/...` 子菜单不能并存（叶子会被吞）。**禁止**在该前缀下新增任何 `[MenuItem]`；二级面板（审计日志）只走面板内按钮 + 快捷键。
- **面板快捷键**：命令统一注册在 `Editor/UI/ShortcutActions.cs`（Unity `[Shortcut]` 特性，出厂默认不绑定）。新增可绑定面板 = 加一个 `[Shortcut]` 静态方法 + `Commands` 清单追加一条，设置抽屉 Shortcuts 节自动枚举；用户主动设置的绑定由 ShortcutManager profile 持久化，不写 EditorPrefs。
- **样式只走 USS class**：不要在 C# 里手写 `style.color = ...`，改类名（`AddToClassList` / `RemoveFromClassList`）。

### 2. Skill 编写规范

```csharp
[UnitySkill("skill_name", "描述（首句给 AI 看，建议带参数说明）",
    Category   = SkillCategory.GameObject,
    Operation  = SkillOperation.Create,        // 多操作可用 | 合并
    Tags       = new[] { "primitive", "hierarchy" },
    Outputs    = new[] { "instanceId", "path" },
    TracksWorkflow   = true,                   // 写型 skill 强烈建议
    MutatesScene     = true,                   // 风险元数据按实际填
    RiskLevel        = "medium")]              // low / medium / high
public static object SkillName(string name, float x = 0f) { ... }
```

- **必填**：Name（构造器位置参）、Description、`Category`、`Operation`。
- **强烈建议**：`Tags`、`Outputs`；写型 skill 加 `TracksWorkflow=true`；改场景/资源/可能触发 Reload/PlayMode 的，相应风险标志要标。
- **`Mode` 字段**：默认 `FullAuto`（Approval 模式下需用户授权）。**只有真正纯读、无任何副作用**的 skill 才能声明 `SemiAuto`。
- **NeverInSemi 自动判定**：`SkillOperation.Delete` / `MayEnterPlayMode=true` / `MayTriggerReload=true` / `RiskLevel="high"` 会自动判为 Approval 模式必拦——元数据决定一切，不要靠运行时另写拦截逻辑。
- **错误返回用 `SkillErrorResponse.Build(code, msg, ...)` 而非抛异常**：业务错误必须构造结构化响应（含 `errorCode` / `suggestedFixes` / `retryStrategy`）。仅当错误真的属于"框架级未知异常"时才向上抛由 SkillRouter 包装。
- **参数校验链**：用 `Validate.Required(x, "x") is object err` 模式，提前 return err。
- **撤销 / 工作流快照**：写型操作必须 `Undo.RegisterCreatedObjectUndo` / `Undo.RegisterCompleteObjectUndo`；`TracksWorkflow=true` 的 skill 内调用 `WorkflowManager.SnapshotXxx(...)`。快照分级为 `SnapshotType`（Modified/Created/Deleted/Moved/Setting）：新建存创建态、移动保留完整操作顺序、删除完整备份文件/非空目录/场景层级、设置类经 `WorkflowSettingRestorerRegistry` 注册回退。主文件与 `.meta` 独立内容寻址（`fileHash` / `metaFileHash`），base64 不再内嵌进 `workflow_history.json`（schemaVersion 5）；自动清理不得删除仍被历史引用的 blob，限制值 `0` 表示不限制。
- **批处理范式**：成对提供 `xxx` 和 `xxx_batch`（后者用 `BatchExecutor.Execute<TItem>(items, perItem, idFn)`）。

### 3. 公共辅助层（禁止重写）

| 场景 | 用什么 | 不要做什么 |
|------|--------|------------|
| 查找 GameObject | `GameObjectFinder.FindOrError(name, instanceId, path)` | 直接 `GameObject.Find` |
| 参数校验 | `Validate.Required` / `Validate.RequiredJsonArray` | 自己手写 if-empty 链 |
| 批量执行 | `BatchExecutor.Execute<T>(...)` | 自己 for-loop 累积错误 |
| JSON 序列化 | `SkillsCommon.JsonSettings`（Newtonsoft） | 用 `JsonUtility` 或新建 settings |
| 反射可选包 | 参照 `XxxReflectionHelper.cs` + `DOTweenPresenceDetector` 模式 | 直接引用未声明依赖的类型 |
| 异步任务 | `AsyncJobService` / `BatchJobService` | 自己起 Task / Thread |
| URP/HDRP 缺包兜底 | `RenderPipelineSkillsCommon.NoURP()` 返 stub | 直接编译报错 |
| 跨工程查找 | `FindHelper.FindAll<T>()`（自动选 Unity 6 新 API） | 直接 `FindObjectsOfType` |

### 4. 常量与日志源

- **版本号唯一源**：`SkillsLogger.Version`。`SkillsHttpServer`(`/health`)、`SkillRouter`(`/skills` manifest) 等均引此常量。**禁止硬编码 "1.9.3" 等字面量**。
- **日志走 `SkillsLogger`**：`Log` / `LogWarning` / `LogError` / `LogVerbose` / `LogAgent`，禁止直调 `Debug.Log*`；前缀颜色用 `PREFIX_*` 常量。
- **EditorPrefs key 前缀**：统一 `UnitySkills_*`（用于"老安装"检测，参见 `PermissionUiHelpers.IsExistingInstall()`）。

### 5. Unity 版本兼容

- Unity 6 新 API 与老 API 用 `#if UNITY_6000_0_OR_NEWER` / `#else` 包裹（参见 `FindHelper`）。
- ListView 等控件优先用 Unity 2022.2+ 的事件（如 `selectedIndicesChanged`），避免 obsolete API。
- 命名空间：业务统一 `UnitySkills`，内部 helper 用 `UnitySkills.Internal`。

### 6. 本地化 (多语言架构)

- **JSON 解耦架构（硬约束）**：所有多语言文本彻底从 C# 业务代码解耦，统一存放于 `Editor/Locales/` 目录下（`en.json` 英文、`zh-CN.json` 中文、`ru.json` 俄文）。**严禁在 `.cs` 脚本中硬编码多语言静态字典或界面中英文字符串**。
- **调用接口与性能**：业务与 UI 代码统一调用 `SkillsLocalization.Get(key)` 或 `SkillsLocalization.Get(key, params object[] args)`。底层由 `[InitializeOnLoad]` 在编辑器加载时解析 JSON 并缓存在内存字典中，提供 O(1) 瞬时查表与零运行时 IO。
- **三语对齐契约**：新增或修改任何 UI 词条时，必须保证 `en.json`、`zh-CN.json`、`ru.json` 三者 100% 键名对齐，严禁漏填。
- **字库安全（硬约束）**：`Editor/UI/` 与 `Locales/` 涉及的中文字符必须处于 `UnitySkillsCN-UI.asset` 已有字库中，**严禁运行 font baker**。

### 7. 注释语言

- **源码注释一律英文**（`.cs` 的 `//` `///` `/* */`、`.py` 的 `#`）。仅在确有必要处保留中文：逐字引用中文界面文案、上游第三方的中文提示串、或中文本地化键值——保留的中文放在引号内，周围说明文字仍用英文。
- 这条只约束**注释**。字符串字面量里的中文（Localization 词条值、生成代码模板、面向用户的错误文案）属于产品输出，不在此列，不要改。
- `Editor/UI/` 与 `Locales/` 另有约束：字体图集扫描器会扫这些文件的字符（含注释），其中文只能减不能增。

---

## 操作模式 (v1.9+)

服务端权限系统，对齐 Claude Code permission modes。三档（新安装默认 Auto；老安装升级默认 Bypass；从不默认 Approval——由 `SkillsModeManager.CurrentMode` 决策，见 `:143`）：

| 模式 | 行为（针对 FullAuto skill） |
|------|------|
| Approval | 返 `MODE_RESTRICTED` + grant token；用户同意后 `POST /permission/grant` **一步执行原 skill 并在 grant 响应里返 `result`**。grant 单次执行；持久放行需用户在面板加入 Allowlist |
| Auto | 直接执行（写审计）；仅拦自动判定的 NeverInSemi |
| Bypass | 全部放行，仅 `ConfirmationToken` 影响高危 |

**双轨审批**：Dialog（默认，对话同意）/ Panel（面板 Approve）。**Allowlist** 命中绕过 ModeGate（含 NeverInSemi），但**不绕过** `ConfirmationToken` 二次确认。

**REST 端点**：`/permission/{status,grant,approve,deny,revoke,allowlist,allowlist/{add,remove},audit}`；`/health` 含 `currentMode` / `panelApprovalRequired` / `pendingCount`；`/skills` 每条含 `mode` 与 `approvalBehavior`。`POST /skills/batch` 支持 fail-fast、`continueOnError`、`?mode=dryRun`、跨步 `$ref`、事务回滚与 `?diff=1` 聚合净变化；单技能写操作同样支持 `?diff=1`。观测端点包括 `GET /compile/status`、`GET /events` 与 `GET /analytics?window=1h|24h|7d|all`；最近 7 天有效遥测会以有限降权和 warning 反哺 `/skills/recommend`。

> 编写 Skill 时**只需正确填风险元数据**，权限拦截/审计/灰度均由框架处理，不要在 Skill 内部写授权判断。

详见 `SkillsForUnity/unity-skills~/SKILL.md` 的 Operating Mode 协议节。

---

## Skills 模块 (56 个 *Skills.cs / 54 个 SkillCategory 分类，805 Skills)

> **两个数不一样，不是笔误**：`Editor/Skills/` 下有 **56** 个 `*Skills.cs` 源文件，但 `SkillCategory` 枚举只有 **54** 个分类——`BatchSkills.cs` 不占独立分类（它的 skills 分别登记到 Workflow 与 Validation），`DiagnoseSkills.cs` 归入 Debug。下表按**分类**列，因此是 54 项。

| 模块 | 数量 | 模块 | 数量 | 模块 | 数量 |
|------|:----:|------|:----:|------|:----:|
| YooAsset* | 40 | Cinemachine | 34 | Netcode* | 39 |
| UI | 29 | UIToolkit | 31 | ShaderGraph | 23 |
| Workflow | 40 | ProBuilder* | 22 | XR* | 22 |
| DOTween* | 21 | Material | 21 | PrimeTween* | 5 |
| PostProcess† | 10 | GameObject | 19 | Perception | 18 |
| Volume† | 9 | URP† | 7 | Decal† | 7 |
| Test | 13 | Editor | 16 | Script | 12 |
| Timeline | 12 | Physics | 12 | Asset | 12 |
| AssetImport | 11 | Camera | 12 | Package | 11 |
| Prefab | 11 | Shader | 11 | Graphics | 11 |
| Animator | 10 | Audio | 10 | Cleaner | 10 |
| Component | 14 | Console | 10 | Debug | 11 |
| Event | 11 | Light | 11 | Model | 10 |
| NavMesh | 10 | Optimization | 10 | Profiler | 10 |
| Scene | 10 | ScriptableObject | 13 | Smart | 10 |
| Terrain | 10 | Texture | 10 | Validation | 16 |
| Project | 10 | Sample | 8 | Behavior* | 10 |
| HybridCLR* | 12 | Addressables* | 8 | QFramework* | 20 |

\*ProBuilder 需 `com.unity.probuilder`，XR 需 `com.unity.xr.interaction.toolkit`，Netcode 需 `com.unity.netcode.gameobjects`，YooAsset 需 `com.tuyoogame.yooasset (≥2.3.15)`，DOTween 需 `DG.Tweening`，PrimeTween 需 `com.kyrylokuzyk.primetween`，Behavior 需 `com.unity.behavior`，HybridCLR 需 `com.code-philosophy.hybridclr`，Addressables 需 `com.unity.addressables`，QFramework 需 [QFramework](https://github.com/liangxiegame/QFramework)（**无 UPM 包**——Toolkits unitypackage 装进 `Assets/QFramework/` 或单文件 `QFramework.cs`，故检测走反射锚类型而非包 id，且不声明 `RequiresPackages`）
†Volume / PostProcess / Decal / URP 需 `com.unity.render-pipelines.universal`（URP 未安装时这 4 个模块以同名 stub 返回 `NoURP()` 提示）。

**Advisory 模块 (28)**：architecture, patterns, performance, asmdef, async, inspector, blueprints, adr, project-scout, scene-contracts, script-roles, scriptdesign, testability, netcode-design, yooasset-design, addressables-design, unitask-design, dotween-design, primetween-design, shadergraph-design, pico-design, qframework-design, yaml-editing, unity-cli, manual-gameobject, manual-component, manual-material, manual-scene — **纯架构/设计指导文档，无 REST Skills，无 C# 实现**；新增 advisory 时只动 `unity-skills~/skills/` 下文档，不要在 Editor/Skills/ 加 stub。

---

## 开发流程

- **Git 分支**：开发在 `beta`，通过 `/release` 同步到 `main`（线性历史，无 merge commit）。
- **版本更新**：`/updateversion <版本号>` 只更新明确的项目版本锚点并生成 CHANGELOG，随后用 `.github/scripts/check_project_version.py` 验证一致性；禁止按数字全局替换，以免误改第三方 SDK 版本说明。
- **正式发布**：`/release [版本号]` 必须依次通过 beta 候选矩阵、main/beta 同步、tag 自身矩阵，最后才创建稳定 GitHub Release 并核对 `releases/latest`；Unity 更新横幅以该稳定 Release 为准。
- **扩展 Skill**：在 `Editor/Skills/` 已有模块文件内加 `[UnitySkill]` 静态方法（或新增 `XxxSkills.cs`），SkillRouter 启动反射自动发现。新增模块需在 `SkillCategory` 枚举里登记。
- **新增 UI 窗口**：在 `Editor/UI/` 加 `.cs + .uxml + .uss` 三件套（参考 `UnitySkillsWindow` / `UnitySkillsAuditWindow`）。
- **自定义命令**：`/skillcheck`（C# 与 SKILL.md 一致性审计 + 技能数量同步，原 `/skillcount` 已并入）、`/metacheck`（.meta GUID）、`/updateversion`、`/release`。
- **Domain Reload 期间**：脚本保存、包安装等会让 REST 服务短暂不可达（503/504 带诊断），客户端等待重试是预期行为——**不要为绕过此现象修改服务端**。`SkillsHttpServer` 已有 EditorPrefs 持久化端口、连续失败 10 次上限、Watchdog 自动重启死亡线程等容错机制。
