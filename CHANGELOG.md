# Changelog

All notable changes to **UnitySkills** will be documented in this file.

## [1.5.1] - 2026-02-15

### ⭐ Highlight

- **全模块 10+ Skill 覆盖** — 13 个模块从不足 10 个 Skill 扩展到 10+，新增 57 个 Skill，总计约 430 个。所有模块（SampleSkills 除外）均达到 10+ Skill 覆盖。

### Added

- **服务器启动自检 (Self-Test)** — 启动后自动请求 `localhost` 和 `127.0.0.1` 的 `/health` 端点，验证可达性并在 Console 输出结果，帮助用户快速定位连接问题
- **端口占用扫描** — 自检时扫描 8090-8100 范围内其他被占用的端口，以警告形式提示用户

#### 新增 Skill（57 个）

- **ProfilerSkills** (+9): `profiler_get_memory`, `profiler_get_runtime_memory`, `profiler_get_texture_memory`, `profiler_get_mesh_memory`, `profiler_get_material_memory`, `profiler_get_audio_memory`, `profiler_get_object_count`, `profiler_get_rendering_stats`, `profiler_get_asset_bundle_stats`
- **OptimizationSkills** (+8): `optimize_analyze_scene`, `optimize_find_large_assets`, `optimize_set_static_flags`, `optimize_get_static_flags`, `optimize_audio_compression`, `optimize_find_duplicate_materials`, `optimize_analyze_overdraw`, `optimize_set_lod_group`
- **AudioSkills** (+7): `audio_find_clips`, `audio_get_clip_info`, `audio_add_source`, `audio_get_source_info`, `audio_set_source_properties`, `audio_find_sources_in_scene`, `audio_create_mixer`
- **ModelSkills** (+7): `model_find_assets`, `model_get_mesh_info`, `model_get_materials_info`, `model_get_animations_info`, `model_set_animation_clips`, `model_get_rig_info`, `model_set_rig`
- **TextureSkills** (+7): `texture_find_assets`, `texture_get_info`, `texture_set_type`, `texture_set_platform_settings`, `texture_get_platform_settings`, `texture_set_sprite_settings`, `texture_find_by_size`
- **LightSkills** (+3): `light_add_probe_group`, `light_add_reflection_probe`, `light_get_lightmap_settings`
- **PackageSkills** (+3): `package_search`, `package_get_dependencies`, `package_get_versions`
- **ValidationSkills** (+3): `validate_missing_references`, `validate_mesh_collider_convex`, `validate_shader_errors`
- **ShaderSkills** (+5): `shader_check_errors`, `shader_get_keywords`, `shader_get_variant_count`, `shader_create_urp`, `shader_set_global_keyword`
- **AnimatorSkills** (+2): `animator_add_state`, `animator_add_transition`
- **ComponentSkills** (+2): `component_copy`, `component_set_enabled`
- **PerceptionSkills** (+2): `scene_tag_layer_stats`, `scene_performance_hints`
- **PrefabSkills** (+2): `prefab_create_variant`, `prefab_find_instances`
- **SceneSkills** (+1): `scene_find_objects`

### Improved
- **`profiler_get_runtime_memory`** — 从单对象查询改为按内存占用排序的 Top N 列表，对 AI 更实用
- **`scene_tag_layer_stats`** — 新增未标记对象计数和空定义层检测
- **`scene_performance_hints`** — 增强为结构化输出（priority/category/issue/suggestion/fixSkill），新增 LOD、重复材质、粒子系统检查

### Fixed
- **IPv4 可达性修复** — `HttpListener` 同时绑定 `localhost` 和 `127.0.0.1`，修复部分 Windows 系统上 `localhost` 仅解析到 IPv6 `::1` 导致 `127.0.0.1` 无法连接的问题
- **截图文件缺少扩展名** — `SceneScreenshot` 当 `filename` 参数不含扩展名时自动补 `.png` 后缀，修复生成的截图文件无法在 Unity 中预览的问题 (`SceneSkills.cs:111`)
- **本地化补全** — 为 `Localization.cs` 的 `_chinese` 字典补充约 140 条缺失的中文翻译，英文/中文 471 个 key 完全匹配
- **SkillRouter 更新** — `_workflowTrackedSkills` 新增 17 个写操作 Skill 的追踪
- **超长任务断连修复** — 修复超过 3 分钟的任务因三层超时叠加（Python 30s / C# 60s / Skill 执行 3min+）导致必然断连的问题：
  - 请求超时改为用户可配置（默认 60 分钟），Unity 设置面板新增"请求超时"输入框
  - `/health` 端点暴露 `requestTimeoutMinutes`，Python 客户端初始化时自动同步超时配置
  - 生成的 AI 代理代码同步使用服务器超时配置，替代硬编码 30 秒
- **Domain Reload 断连修复** — 修复 Unity 6 上脚本编译后服务器恢复失败的问题：
  - `OnBeforeAssemblyReload` 主动关闭 HttpListener 并等待线程退出，确保端口立即释放
  - 持久化运行端口（`PREF_LAST_PORT`），Reload 后优先恢复到同一端口，避免 Auto 模式端口漂移
  - `CheckAndRestoreServer` 增加秒级延迟重试（1s/2s/4s），替代无效的 `delayCall`（~16ms）
  - preferred port 被占用时自动降级到端口扫描，而非直接失败
  - Python 客户端重试增强：3 次重试 + 渐进式退避（2s/4s/6s），总窗口 ~12 秒
  - 注册表过期阈值从 60 秒提升到 120 秒，避免大项目 Reload 期间实例被误清理
- **Self-Test /health 返回 500 修复** — `WaitAndRespond()` 在 ThreadPool 线程上访问 `RequestTimeoutMs` 时触发 `EditorPrefs.GetInt()`（主线程限定 API），抛出 `UnityException` 被 catch 捕获返回 500。改为 `Start()` 时缓存超时值到静态字段，避免非主线程调用 Unity API
- **清理 AudioSkills.cs.bak** — 移除误提交的备份文件，消除 Unity immutable package 中缺少 .meta 文件的警告

## [1.5.0] - 2026-02-13

### ⭐ Highlight

- **`scene_export_report`** — 一键导出完整场景报告（Markdown），包含：精简层级树（内置组件仅列名称，用户脚本标 `*`）、用户脚本字段清单（含实际值和引用目标路径）、**深度 C# 代码级依赖分析**（10 种模式：`GetComponent<T>`/`FindObjectOfType<T>`/`SendMessage`/字段类型引用/单例访问/静态成员调用/`new T()`实例化/泛型类型参数/继承与接口实现/`typeof`·`is`·`as`类型检查）、合并依赖图与风险评级。覆盖项目中所有用户 C# 类（MonoBehaviour、ScriptableObject、Editor、普通类）。生成的文件可直接作为 AI 持久化上下文。调用示例：`call_skill('scene_export_report', savePath='Assets/Docs/SceneReport.md')`

### Improved
- **`scene_export_report` 依赖分析质量提升** (5 项修复):
  1. Dependency Graph 表格新增 `Source` 列，区分 `scene`（序列化引用）和 `code`（源码分析），AI 不再混淆场景对象与类名
  2. 代码扫描前剔除 `//` 单行注释和 `/* */` 块注释，消除注释中的虚假依赖
  3. `StaticAccess` 正则收紧为双侧 PascalCase（`[A-Z]\w+\.\s*[A-Z]\w*`），不再误报 `Debug.Log`、`Mathf.Clamp` 等
  4. `RxInheritance` 从 `Match` 改为 `Matches`，支持单文件多类（partial class、嵌套类）
  5. 新增方法级粒度：`From` 列显示 `ClassName.MethodName`，定位依赖发生的具体方法

### Fixed (全项目审计 — 36 项缺陷修复)

#### 🔴 严重 (14 项)
- **P-1** `CinemachineSkills.cs` — `componentType` 为 null 时 `.Equals()` 空引用崩溃，添加 null 检查
- **P-2** `SmartSkills.cs` — 非 Component 对象强转 `(comp as Component).gameObject` 崩溃，改为安全转换并跳过
- **B-1** `ScriptSkills.cs:147` — 用户输入正则无超时限制导致 ReDoS 风险，添加 `TimeSpan.FromSeconds(1)` 超时
- **B-2** `GameObjectSkills.cs:265` — 同上 ReDoS 风险，`new Regex(name)` 添加超时参数
- **B-3** `PrefabSkills.cs:40-41,80` — `InstantiatePrefab` 返回 null 未检查导致后续空引用，添加 null 守卫
- **B-4** `SceneSkills.cs:99` — `GetComponents<Component>()` 返回含 null 元素（缺失脚本），`.Select(c => c.GetType())` 崩溃，添加 `.Where(c => c != null)` 过滤
- **B-9** `LightSkills.cs:27-30` — 无效 lightType 时返回错误但已创建的 GameObject 泄漏，添加 `DestroyImmediate(go)` 清理
- **B-10** `ComponentSkills.cs:574` — `ConvertValue` 对值类型返回 null 导致拆箱异常，改为 `Activator.CreateInstance(targetType)` 返回默认值
- **B-11** `TerrainSkills.cs:238` — `radiusPixels=0` 时除零异常，添加 `Mathf.Max(1, ...)` 下限
- **I-1** `SkillsHttpServer.cs` — `Stop()` 未 Join 后台线程导致线程泄漏，添加 `Thread.Join(2000)` 和引用清理
- **I-5** `SkillsHttpServer.cs` — skill name 未校验可注入 `/` `..` 等路径字符，添加输入验证
- **I-6** `SkillRouter.cs` — `BeginTask` 注册的 Undo hooks 在异常时未通过 `EndTask` 清理，在 catch 块中添加 `EndTask()` 调用
- **P-4** `unity_skills.py:118-127` — 端口扫描全部失败时静默回退到 8090，改为抛出 `ConnectionError` 明确报错
- **P-7** `unity_skills.py:421-425` — `WorkflowContext.__enter__` 中 `call_skill` 失败后 `_current_workflow_active` 仍为 True，重排赋值顺序并添加异常处理

#### 🟡 中等 (15 项)
- **P-3** `SmartSkills.cs:213-222` — Transform 分支是 Component 分支的子集（死代码），删除冗余分支
- **P-5** `Localization.cs:40` — `Get()` 直接读 `_current` 字段绕过 `Current` 属性的懒初始化，改为使用 `Current` 属性
- **B-5** `SceneSkills.cs:110` — `SceneScreenshot` 忽略 width/height 参数，改用 `superSize` 计算并在返回值中包含尺寸
- **B-6** `AnimatorSkills.cs:67-83` — `controller.parameters` 返回数组副本，修改后未写回，添加 `controller.parameters = parameters` 回写
- **B-7** `ComponentSkills.cs:738` — `easein` 和 `easeout` 使用相同的 `EaseInOut` 曲线，改为各自独立的加速/减速曲线
- **B-8** `MaterialSkills.cs:763` — Float 类型属性调用 `GetPropertyRangeLimits()` 返回无意义值，分离 Float 和 Range 两个 case
- **B-12** `UISkills.cs:249` — `item.type` 为 null 时 `.ToLower()` 崩溃，添加 null 合并 `(item.type ?? "")`
- **B-13** `ScriptSkills.cs:70-72` — 未提供 namespace 时 `{NAMESPACE}` 占位符残留在生成的脚本中，添加默认值替换
- **I-3** `WorkflowManager.cs` — `SaveHistory()` 直接写目标文件，崩溃时数据丢失，改为先写 `.tmp` 再原子替换
- **I-7** `SkillsHttpServer.cs` — 速率限制使用 `double` 精度时间戳存在浮点漂移，改为 `long` Ticks 整数比较
- **I-8** `WorkflowManager.cs` — 批量操作无快照上限导致内存无限增长，添加 500 条上限和日志提示
- **I-9** `RegistryService.cs` — 清理过期条目仅检查时间戳，进程已死但时间未过期的条目残留，添加 `IsProcessAlive()` 检查
- **I-10** `GameObjectFinder.cs` — 编辑器非播放模式下 `Time.frameCount` 不递增导致缓存永不失效，改为请求级 bool 标志
- **P-8** `AudioSkills.cs:145-177` — `StartAssetEditing()` 期间调用 `SaveAndReimport()` 导致导入管线冲突，移除 batch 方法的 setup/teardown
- **P-11** `unity_skills.py:520` — CLI 数值解析 `isdigit()` 预检对 `"1.2.3"` `"--5"` 等边界值误判，改为直接 try/except 转换

#### 🟢 轻微 (7 项)
- **P-9** `ValidationSkills.cs:192-211` — 空文件夹删除未按深度排序，父文件夹先删导致子文件夹残留，改为按路径长度降序删除
- **P-10** `WorkflowSkills.cs:121-138` — `HistoryUndo/Redo` 未校验 steps 参数，负数导致无限循环，添加 `steps < 1` 守卫
- **P-12** `PhysicsSkills.cs:78-89` — `PhysicsSetGravity` Undo 记录使用 `RecordObject` 而非 `Undo.RecordObject`，变量命名优化避免混淆
- **B-14** `ComponentSkills.cs:167` — `SnapshotObject` 内部已有 `_currentTask == null` 守卫，确认无需额外修改
- **I-2** `SkillsHttpServer.cs` — `ManualResetEventSlim` 已通过 ownership transfer 模式正确管理，确认无泄漏
- **I-4** `RegistryService.cs` — tmp 文件删除已在文件锁保护范围内，确认无竞态条件
- **P-6** `unity_skills.py:457-462` — `get_skills()`/`health()` 使用 `requests.get` 而非 Session 对象，属设计选择非缺陷

### Added
- **依赖边扫描重构**: 提取 `CollectDependencyEdges()` 共享方法，供 `scene_export_report` 和 `scene_dependency_analyze` 复用，消除重复代码
- **场景快照 Skill**: 新增 `scene_context`，一次调用生成结构化 JSON 场景快照（层级、组件、脚本字段值、跨对象引用、UI 布局），支持 `rootPath` 子树导出、`maxObjects`/`maxDepth` 截断策略，让 AI 无需追问即可理解场景并编写代码（`PerceptionSkills.cs`）
- **依赖分析 Skill**: 新增 `scene_dependency_analyze`，分析场景对象间的引用依赖关系，生成反向依赖索引和风险评级（safe/low/medium/high），支持导出 Markdown 报告作为 AI 持久化上下文，防止 AI 操作误伤关键依赖对象（`PerceptionSkills.cs`）
- **BatchExecutor 泛型框架**: 新增 `BatchExecutor.Execute<T>()` 通用批处理框架，支持 JSON 反序列化、逐项执行、错误隔离、setup/teardown 钩子（`BatchExecutor.cs`）
- **SkillsLogger 统一日志**: 新增 `SkillsLogger` 类，支持 Off/Error/Warning/Info/Agent/Verbose 日志级别，替代散落的 `Debug.Log` 调用（`SkillsLogger.cs`）
- **参数校验扩展**: `Validate` 类新增 `InRange()`、`RequiredJsonArray()`、`SafePath()` 方法，形成完整的参数校验工具链（`GameObjectFinder.cs`）
- **单元测试框架**: 新增 `Tests/Editor/` 目录，包含 3 个测试套件共 67 个测试用例：
  - `BatchExecutorTests.cs` — 17 个测试覆盖批处理成功/失败/setup/teardown 生命周期
  - `RegistryServiceTests.cs` — 16 个测试覆盖哈希确定性和边界条件
  - `ValidateTests.cs` — 34 个测试覆盖 Required/InRange/SafePath 校验
- **场景空间查询 Skill**: 新增 `scene_spatial_query`，支持按坐标/对象名查找半径内的对象，可按组件类型过滤（`PerceptionSkills.cs`）
- **场景材质概览 Skill**: 新增 `scene_materials`，按 Shader 分组展示场景中所有材质的使用情况，可选输出 Shader 属性列表（`PerceptionSkills.cs`）

### Security
- **SHA256 哈希**: RegistryService 实例 ID 从 MD5 迁移到 SHA256（`RegistryService.cs`）
- **TOCTOU 文件锁**: 注册表文件读写添加文件锁防止竞态条件（`RegistryService.cs`）
- **POST Body 大小限制**: HTTP 服务器拒绝超过 10MB 的请求体，返回 413 状态码（`SkillsHttpServer.cs`）
- **ManualResetEventSlim 泄漏修复**: try/finally 模式确保信号量在 ThreadPool 入队失败时仍被释放，包括超大请求拒绝路径（`SkillsHttpServer.cs`）
- **路径遍历防护**: 19 个文件操作方法补齐 `Validate.SafePath()` 校验，涵盖 Script/Shader/Material/ScriptableObject/Prefab/Scene/Asset/Cleaner/Validation/Animator 共 11 个 Skill 文件

### Changed

#### 架构重构
- **BatchExecutor 接入**: 25 个 batch 方法迁移到 `BatchExecutor.Execute<T>()` 框架，消除约 1500 行重复的反序列化/错误收集/结果汇总代码，涉及 GameObjectSkills/ComponentSkills/MaterialSkills/LightSkills/PrefabSkills/UISkills/AudioSkills/ModelSkills/TextureSkills/AssetSkills/ScriptSkills 共 11 个文件
- **WorkflowManager Undo/Redo 提取**: 重构撤销/重做逻辑为独立方法，提升可维护性（`WorkflowManager.cs`）
- **Agent 表驱动注册**: SkillRouter 的 Agent 配置改为表驱动模式，新增 Agent 类型无需修改分发逻辑（`SkillRouter.cs`）
- **SkillRouter 消除双重序列化**: 替换 `JObject.FromObject(result)` 为反射检测错误字段，避免不必要的 JSON 中间转换（`SkillRouter.cs`）

#### 代码质量
- **GameObjectFinder 全面迁移**: 50+ 处原始 `GameObject.Find` 调用迁移到 `GameObjectFinder.FindOrError`，提供错误提示含相似名称建议，涉及 PrefabSkills/EventSkills/TimelineSkills/CameraSkills/EditorSkills/UISkills/WorkflowSkills/ComponentSkills/SampleSkills/CinemachineSkills 共 10 个文件
- **CinemachineSkills 全面升级**: 所有 Skill 方法支持 name/instanceId/path 三种查找方式，与其他 Skills 保持一致（`CinemachineSkills.cs`）
- **统一返回值格式**: 10 个方法补齐 `success = true/false` 字段（`SampleSkills.cs`、`OptimizationSkills.cs`、`ValidationSkills.cs`）
- **区域无关数值解析**: ComponentSkills 和 ScriptableObjectSkills 中 7 处 `float.Parse`/`double.Parse` 添加 `CultureInfo.InvariantCulture`，修复非英文区域的小数点解析问题
- **静默异常修复**: 多处空 catch 块添加日志记录，便于调试定位问题
- **文件重命名**: `NextGenSkills.cs` → `PerceptionSkills.cs`，文件名与类名保持一致
- **SampleSkills 标注**: 明确标记为便捷别名，4 处 `GameObject.Find` 迁移到 `GameObjectFinder.FindOrError`
- **PerceptionSkills 全面改进**: `script_analyze` 扩展支持 ScriptableObject 和用户自定义类，返回新增 `kind` 字段；`hierarchy_describe` 组件 emoji 提示从 5 种扩展到 13 种（新增 Animator/AudioSource/ParticleSystem/Collider/Rigidbody/SkinnedMeshRenderer/SpriteRenderer/UI）；`IsUnityCallback` HashSet 提升为 `static readonly` 并扩充回调列表（`PerceptionSkills.cs`）

#### 基础设施
- **PhysicsSetGravity Undo 支持**: 通过 `DynamicsManager.asset` 注册 Undo，重力修改可撤销（`PhysicsSkills.cs`）
- **双重检查锁**: 单例和懒初始化改用双重检查锁模式（`SkillsHttpServer.cs`）
- **超时常量化**: 散落的超时魔数提取为命名常量（`SkillsHttpServer.cs`）
- **版本集中化**: 版本号集中管理，避免多处硬编码不一致
- **Python 客户端异常安全**: `unity_skills.py` workflow 相关代码使用 try/finally 确保 `_current_workflow_active` 状态正确重置

### Performance
- **GameObjectFinder 帧级缓存**: 同一帧内重复查找同名 GameObject 直接命中缓存，避免冗余遍历（`GameObjectFinder.cs`）
- **反射成员缓存**: ComponentSkills 新增 `_memberCache` 字典和 `FindMember()` 辅助方法，属性/字段查找结果被缓存，批量操作性能显著提升（`ComponentSkills.cs`）
- **scene_summarize 单次遍历**: 消除 3 次额外 `FindObjectsOfType`（Light/Camera/Canvas），改为在组件遍历中内联统计，大场景性能提升显著（`PerceptionSkills.cs`）

### Docs
- README.md 技能数量修正
- agent.md 添加 Git 分支同步规则和 agent_config.json 手动安装说明

---

## [1.4.4] - 2026-02-11

### Added
- 统一错误响应格式：自动检测并转换 Skill 返回的错误对象
- 参数验证工具类：`Validate.Required()` 和 `Validate.SafePath()`
- 请求追踪 ID：每个请求分配唯一 X-Request-Id
- Agent 标识：支持 X-Agent-Id header 识别调用的 AI 工具
- 日志级别控制：支持 Off/Error/Warning/Info/Agent/Verbose
- SkillsLogger 类：统一日志管理
- 服务端自动工作流记录：修改类 Skill 自动记录历史

### Changed
- Python 客户端：使用 UTF-8 编码发送 JSON，内置重试逻辑
- Skill Manifest：添加缓存机制减少开销
- GameObjectFinder：使用场景根遍历优化性能

### Security
- 文件路径安全校验：防止路径遍历攻击，限制在 Assets/Packages 目录

---

## [1.4.3] - 2026-02-09

### 📝 文档规范化
- **Skill 文档全面优化**: 所有 36 个模块的 SKILL.md 文件现已符合统一规范
  - 添加完整的 YAML frontmatter（name + description）
  - description 格式统一为：`"{功能描述}. Use when {使用场景}. Triggers: {关键词}."`
  - 拆分合并的 `### skill_a / skill_b` 条目为独立条目
- **Skill 数量修正**: README.md 中的数字从 279 修正为实际的 277
- **清理测试文件**: 删除验证过程中产生的临时脚本文件

---

## [1.4.2] - 2026-02-09

### 🆕 Package Manager Skills
- **新增 `PackageManagerHelper.cs`**: 封装 Unity Package Manager API，支持包的安装、移除、刷新等操作。
- **新增 `PackageSkills.cs`**: AI 可调用的包管理技能：
  - `package_list` - 列出已安装包
  - `package_check` - 检查包是否已安装
  - `package_install` - 安装指定包
  - `package_remove` - 移除包
  - `package_refresh` - 刷新包列表缓存
  - `package_install_cinemachine` - 安装 Cinemachine（支持版本 2 或 3）
  - `package_get_cinemachine_status` - 获取 Cinemachine 安装状态

### 🎬 Cinemachine 自动安装
- **全自动安装**: 移除手动安装 UI，改为编辑器启动时自动安装
  - Unity 6+: 自动安装 CM 3.1.3 + Splines 2.8.0
  - Unity 2022 及以下: 自动安装 CM 2.10.5
- **重试机制**: Package Manager 繁忙时自动重试（最多 5 次，间隔 3 秒）

### 🔧 CM2/CM3 兼容性
- **条件编译**: 通过 `CINEMACHINE_2` / `CINEMACHINE_3` 宏区分版本
- **API 适配**: 修复 `CinemachineBrain.UpdateMethod` vs `m_UpdateMethod` 等 API 差异
- **双版本测试**: 在 Unity 2022 (CM2) 和 Unity 6 (CM3) 上验证所有 Cinemachine Skills

### 📝 Workflow 支持完善
- **SmartSkills**: `smart_scene_layout`, `smart_reference_bind` 添加 Workflow 支持
- **EventSkills**: `event_add_listener`, `event_remove_listener` 添加 Workflow 支持
- **ValidationSkills**: `validate_fix_missing_scripts` 添加 Workflow 支持
- 所有使用 Undo 的模块现已完整支持 Workflow 撤销/重做

---

## [1.4.1] - 2026-02-05

*> This PR upgrades the project to support Cinemachine 3.x (Unity.Cinemachine namespace), which is standard in Unity 6.*
*> Credit: [PieAIStudio](https://github.com/PieAIStudio)*

### 🚀 Cinemachine 3.x Upgrade
- **Namespace Migration**: Refactored `CinemachineSkills.cs` to use the new `Unity.Cinemachine` namespace and API (replacing `CinemachineCamera`, etc.).
- **Dependency Update**:
    - Updated `com.unity.cinemachine` to **3.1.3**.
    - Added `com.unity.splines` **2.8.0** as a hard dependency (required for CM 3.x).
    - Updated `UnitySkills.Editor.asmdef` to reference `Unity.Cinemachine` and `Unity.Splines`.
- **Advanced Features**:
    - Full support for **Manager Cameras**: `MixingCamera`, `ClearShot`, `StateDrivenCamera`.
    - Support for **Spline Dolly** (`cinemachine_set_spline`) and **Target Group** (`cinemachine_create_target_group`).
    - Fixed infinite recursion issues in JSON serialization for deep inspection.

---

## [1.4.0] - 2026-02-04

### 🌟 New Features / 新特性 (Major Update since v1.3.0)

- **Persistent Workflow History / 持久化工作流历史**:
    - Introduced "Time Machine" persistent operation history. / 引入了持久化的 AI 操作历史记录。
    - Support for tagging tasks (`workflow_task_start`), snapshots (`workflow_snapshot_object`), and full rollback (`workflow_revert_task`). / 支持任务标签、对象快照及可视化回滚。
    - History persists across Editor restarts and Domain Reloads. / 历史记录在编辑器重启和重载后仍然保留。
    - Added **History Tab** in UnitySkills Window. / 在插件窗口新增“历史”标签页。

- **High-Level Scene Perception / 高级场景感知**:
    - `scene_summarize`, `hierarchy_describe`, `script_analyze`: Deeply perceive scene structure and API. / 深度感知场景结构与 API。

- **Consolidated Skill Modules / 模块功能补完**:
    - **Cinemachine / Timeline / NavMesh / Physics / Event / Profiler**: Full documentation and exposure of these critical modules. / 补全并正式开放这些核心模块的功能与文档。

- **Operations & System**:
    - Customizable Skill Installation path. / 支持自定义安装路径。
    - Terrain editing and Asset redundancy detection (Cleaner). / 新增地形编辑与资源清理。

### 🐞 Bug Fixes / 问题修复
- **Unicode & Encoding**: Fully fixed Chinese character support in both Python client and Unity server. / 彻底修复中文字符支持及乱码问题。
- **Dependencies**: Added `com.unity.splines` (2.8.0) as a hard dependency to support advanced Cinemachine features. / 新增 Splines (2.8.0) 为硬依赖以支持 Cinemachine 高级功能。

---

## [1.3.0] - 2026-01-27

### 🌟 New Features / 新特性
- **Multi-Instance Support**: Auto-port discovery (8090-8100) and Global Registry.
- **Transactional Safety**: Atomic Undo/Redo for skill operations.
- **Batching**: Broad implementation of `*_batch` variants for improved performance.
- **Documentation**: Standardized SKILL.md format and token optimization.

### 📝 Documentation Improvements / 文档优化

- **SKILL.md Token Optimization / SKILL.md Token 优化**:
    - Restructured main SKILL.md for AI consumption with batch-first approach. / 重构主 SKILL.md，采用批量优先方式便于 AI 使用。
    - Unified table format across all skill modules. / 统一所有技能模块的表格格式。
    - Added complete parameter lists and enum values. / 添加完整的参数列表和枚举值。
    - Removed redundant content and duplicate entries. / 移除冗余内容和重复条目。
    - All sub-module SKILL.md files optimized with batch-first rule. / 所有子模块 SKILL.md 文件按批量优先规则优化。

---

## [1.2.0] - 2026-01-24

### 🌟 New Features / 新特性

- **Editor Context Skill (`editor_get_context`) / 编辑器上下文获取**:
    - Get currently selected GameObjects from Hierarchy with instanceId, path, components. / 获取 Hierarchy 选中物体。
    - Get currently selected assets from Project window with GUID, path, type. / 获取 Project 窗口选中资源。
    - Get active scene info, focused window, editor state in one call. / 一次调用获取完整编辑器状态。
    - **AI can now operate directly on selection without searching!** / AI 可直接操作选中对象无需搜索！

- **Texture Import Settings (3 skills) / 纹理导入设置**:
    - `texture_get_settings`: Get current texture import settings. / 获取纹理导入设置。
    - `texture_set_settings`: Set texture type, size, filter mode, compression, etc. / 设置纹理类型、尺寸、过滤模式等。
    - `texture_set_settings_batch`: Batch process multiple textures. / 批量处理多张纹理。

- **Audio Import Settings (3 skills) / 音频导入设置**:
    - `audio_get_settings`: Get current audio import settings. / 获取音频导入设置。
    - `audio_set_settings`: Set load type, compression format, quality, etc. / 设置加载类型、压缩格式、质量等。
    - `audio_set_settings_batch`: Batch process multiple audio files. / 批量处理多个音频。

- **Model Import Settings (3 skills) / 模型导入设置**:
    - `model_get_settings`: Get current model import settings. / 获取模型导入设置。
    - `model_set_settings`: Set mesh compression, animation type, materials, etc. / 设置网格压缩、动画类型、材质等。
    - `model_set_settings_batch`: Batch process multiple 3D models. / 批量处理多个模型。

### 📦 New Skill Modules / 新增模块

| Module | Skills | Files |
|--------|--------|-------|
| **Editor** | +1 | `EditorSkills.cs` |
| **Texture** | 3 | `TextureSkills.cs` (NEW) |
| **Audio** | 3 | `AudioSkills.cs` (NEW) |
| **Model** | 3 | `ModelSkills.cs` (NEW) |
| **GameObject** | +3 | `gameobject_duplicate_batch`, `gameobject_rename`, `gameobject_rename_batch` |
| **Light** | +2 | `light_set_enabled_batch`, `light_set_properties_batch` |

### 📝 Documentation Improvements / 文档优化

- All SKILL.md now include **Returns** structure for each skill / 所有技能文档现在包含返回结构说明
- Added ⚠️ batch operation warnings to prevent N-calls loops / 添加批量操作警告避免循环调用
- Added `instanceId` support documentation / 添加 instanceId 支持说明
- Fixed duplicate content in prefab SKILL.md / 修复 prefab 文档重复内容

---

## [1.1.0] - 2026-01-23


### 🚀 Major Update: Production Readiness / 生产级就绪
This release transforms UnitySkills from a basic toolset into a production-grade orchestration platform.
本次更新将 UnitySkills 从基础工具集升级为生产级编排平台。

### 🌟 New Features / 新特性
- **Multi-Instance Support (多实例支持)**:
    - Auto-discovery of available ports (8090-8100). / 自动发现可用端口。
    - Global Registry service for finding instances by ID. / 全局注册表服务。
    - `python unity_skills.py --list-instances` CLI support.
- **Transactional Safety (Atomic Undo) / 原子化撤销**:
    - All operations now run within isolated Undo Groups. / 所有操作在隔离的 Undo 组中运行。
    - **Auto-Revert**: If any part of a skill fails, the *entire* operation is rolled back. / 失败自动全量回滚。
- **Batch Operations (批处理)**:
    - Added `*_batch` variants for all major skills (GameObject, Component, Asset, UI). / 全技能支持批处理。
    - 100x performance improvement for large scene generation. / 大规模生成性能提升 100 倍。
- **One-Click Installer for Codex (Codex 一键安装)**:
    - Added direct support for OpenAI Codex in the Skill Installer. / 安装器新增 Codex 支持。
- **Token Optimization (Token 优化)**:
    - **Summary Mode**: Large result sets are automatically truncated (`verbose=false`) to save tokens. / 结果自动截断。
    - **Context Compression**: `SKILL.md` rewritten for 40% reduction in System Prompt size. / 上下文压缩。

### 🛠 Improvements / 改进
- **UI Update**: UnitySkills Window now displays Instance ID and dynamic Port. / 面板显示实例 ID 和端口。
- **Client Library**: `UnitySkills` python class refactored for object-oriented connection management. / Python 客户端重构。

---

## [1.0.0] - 2025-01-22

### 🚀 Initial Product Release
This version represents the first stable release of UnitySkills, consolidating all experimental features into a robust automation suite.

### ✨ Key Features
- **100+ Professional Skills**: Modular automation tools across 14+ categories.
- **Antigravity Native Support**: Direct integration with Antigravity via `/unity-skills` slash command workflows.
- **One-Click Installer**: Integrated C# installer for Claude, Antigravity, and Gemini CLI.
- **REST API Core**: Producer-consumer architecture for thread-safe Unity Editor control.

### 🤖 Supported IDEs & Agents
- **Antigravity**: Full slash command and workflow support.
- **Claude Code**: Direct skill invocation and intent recognition.
- **Gemini CLI**: experimental.skills compatibility.

### 📦 Skill Modules Overview
- **GameObject (7)**: Hierarchy and primitive manipulation.
- **Component (5)**: Property劫持 and dynamic configuration.
- **Scene (6)**: High-level management and HD screenshots.
- **Material (17)**: Advanced shaders and HDR control.
- **UI (10)**: Canvas and element automation.
- **Animator (8)**: Controller and state management.
- **Asset/Prefab (12)**: Management and instantiation.
- **System (35+)**: Console, Script, Shader, Editor, Validation, etc.
