# Changelog

All notable changes to **UnitySkills** will be documented in this file.

## [Unreleased]

### Fixed
- **修复 .meta 伪 GUID 与第三方包冲突导致编译失败** — 包内 46 个 `.meta` 文件历史上使用了顺序模式伪 GUID（如 `a1b2c3d4e5f6...`、`0123456789abcdef...`），在用户项目中与 `com.posthog.unity` 等第三方包发生 GUID 碰撞，导致 Unity 把 `ValidationSkills.cs` / `AnimatorSkills.cs` / `LightSkills.cs` / `UISkills.cs` 等文件 ownership 判给其他包并排除我们的导入，进而触发 `error CS0103: The name 'ValidationSkills' does not exist in the current context`（`BatchSkills.cs:481-482`、`PerceptionSkills.cs:593,608`）。本次修复将所有 46 个伪 GUID 全部重新生成为 uuid4 真随机 GUID，影响范围已用 grep 全库验证（0 处外部引用），不会破坏任何 prefab/asset 序列化关系。

### Added
- **`/metacheck` 自定义命令** — 新增 `.claude/commands/metacheck.md`，用启发式扫描全仓库 `.meta` 文件检测伪 GUID（连续 hex / 交错递增 / 重复字符 / abcdef 字面），支持 `--fix` 自动重生成模式，作为本类回归的长期防护。

## [1.8.2] - 2026-05-02

### Added
- **`progressGranularity` 参数** — `batch_execute` 新增 `progressGranularity`（默认 10），每处理 N 个 item 向 `progressEvents` 写入一条进度事件（含毫秒时间戳、百分比、stage、描述），解决小批次 progress 从 0 直跳 100 无法观测的问题。
- **`GET /jobs/{id}/progress` 端点** — 新增细粒度进度事件增量拉取端点，支持 `?offset=N` 参数，客户端每次传入上次 `totalCount` 即可只获取新增事件，响应含 `terminal` 标记，适合 100–200ms 高频轮询。
- **Python `get_job_progress(job_id, offset)` 辅助** — `unity_skills.py` 新增对应封装，支持增量轮询进度事件。

### Changed
- **`GET /jobs/{id}` 支持 `recentCount` 参数** — 可指定返回最近 N 条进度事件（1–200，默认 10），通过 `recentProgress` 字段返回。
- **`batch/SKILL.md` 文档补全** — 补充 `progressGranularity` 参数说明、`job_status` 的 `recentCount` 参数、`job_progress` 端点完整文档（增量轮询用法与响应字段）。
- **版本号更新** — `SkillsLogger.Version`、`package.json`、Python helper 和文档同步提升到 `1.8.2`。

## [1.8.1] - 2026-05-02

### Added
- **统一错误响应体系** — 新增 `SkillErrorCode` 枚举（21 个稳定错误码）与 `SkillErrorResponse` 统一构造器，所有 REST 错误响应统一返回 `status / errorCode / error / skill / details / suggestedFixes / relatedSkills / retryStrategy / retryAfterSeconds` 形状，AI Agent 可基于 errorCode 做确定性自愈，无需 NLP 解析自由文本；`unknownParams` 错误自动生成可重放的 SuggestedFix。
- **高风险技能二次确认** — 新增 `ConfirmationTokenService`（内存字典 + 5 分钟 TTL，token 与 `(skillName, argsHash)` 绑定，SHA256 哈希防 token-then-modify-args 攻击）。`SkillRouter.Execute` 新增 confirmation gate：当 `RiskLevel="high"` 或 `Operation` 含 `Delete` 时，首次调用返回 `CONFIRMATION_REQUIRED` + 新 token + 内嵌 dryRun 预览；二次调用带 `_confirm` 校验通过后执行；`_confirm` 加入 `_reservedBodyParameters`，不传给 skill 自身。`UnitySkillsWindow` Server 标签页 Settings 卡片新增 toggle，默认关闭——全自动场景完全无感。
- **`GET /jobs` 长轮询端点** — 新增三个绕开 skill router 的轻量端点：`GET /jobs`（最近 N 个 BatchJobRecord 列表）、`GET /jobs/{id}`（完整快照 + recentProgress + terminal 标记）、`GET /jobs/{id}/logs`（结构化日志，带 stage/level/code），专为高频进度轮询设计（200–500ms 拉一次即可获得平滑进度）。Windows HttpListener 的 chunked + flush 行为不可靠，按事先约定走轮询降级而非 SSE。
- **`unity_diagnose` 聚合诊断 Skill** — 新增 `DiagnoseSkills` 模块（1 个 Skill），单调用返回 console errors + 编译状态 + 最近 5 个 workflow tasks + 服务器统计 + 最近 10 个 jobs，AI 排错时取代 `console_get_logs / workflow_* / health` 等 4–5 个 Skill 的串联调用。
- **Python 客户端长轮询/诊断辅助** — `unity_skills.py` 新增 `get_job(id)` / `list_jobs(limit)`（走轻量 `GET /jobs` 端点）、`poll_job(id, interval, timeout, on_progress)` 长轮询便捷封装、`diagnose(...)` 聚合诊断快捷调用。

### Changed
- **`/skills/recommend` 检索增强** — `includeSchema=true` 返回完整参数定义、outputs / tags / riskLevel；score 归一化为 `confidence: high(≥10) / medium(≥5) / low`，AI 可直接按置信度分支。
- **`ResolveSkillNotFound` 智能 did-you-mean** — 用 Levenshtein 距离给出最近 5 个建议，取代原 "前 20 个名字" 的浪费输出。
- **manifest / schema 响应体瘦身** — `SkillRouter` manifest / schema 取消 `Formatting.Indented`，响应体减小约 30%。
- **HTTP 异常透出** — `SkillsHttpServer` 把 swallowed catch 改成显式异常 + 日志，便于排错。
- **类型缓存与过期清理** — `SkillsCommon` 类型查找缓存逻辑优化，新增过期运行时清理；改进测试文件夹检测策略。
- **`ConfirmationTokenService.IsHighRisk` 可见性收紧** — 改为 `internal`，因 `SkillRouter.SkillInfo` 是 `internal`，`public` 方法的参数不能引用更窄可见性的类型（CS0051）。
- **GameObject 文档扩展** — `gameobject_set_transform` 文档补充 World / Local / RectTransform 三套参数空间（`localPos*`、`anchoredPos*`、`anchor*`、`pivot*`、`sizeDelta*`、`width / height`），并明确 RectTransform 专属参数在普通 Transform 上会被忽略；`gameobject_create_batch / delete_batch / duplicate_batch / rename_batch` 补齐 `items` 参数表格行。
- **多个模块文档微调** — `asset / component / debug / history / light / material / yooasset` 的 SKILL.md 同步最新 Skill 行为说明。
- **Skill 计数文档同步** — 文档总数从 `713` 修正为 `714`（新增 `unity_diagnose` +1，与 Unity 端反射计数一致）；README / agent.md / SKILL.md 模块表加入 Diagnose 行；为 Volume / PostProcess / Decal / URP 4 个 SRP 模块加 `†` 标注，说明 URP 未安装时这 4 个模块以同名 stub 返回 `NoURP()`，rg 源码计数（747）≠ 运行时计数（714）的差额由 `#if !URP / #else` 双分支编译解释。
- **版本号更新** — `SkillsLogger.Version`、`package.json`、Python helper 和文档同步提升到 `1.8.1`。

## [1.8.0] - 2026-04-24

### Added
- **YooAsset 资源管理模块** — 新增 40 个 Skill（`yooasset_*`），覆盖资源包构建、加载、释放、更新、文件系统查询和 PlayMode 模拟，基于反射实现零编译依赖（`#if YOO_ASSET`）。
- **Netcode 网络模块** — 新增 33 个 Skill（`netcode_*`），覆盖 NetworkManager 创建/配置、NetworkObject/NetworkTransform 管理、Prefab 列表、Scene Management、Transport 配置和运行时启停（`#if NETCODE_GAMEOBJECTS`）。
- **Shader Graph 模块** — 新增 23 个 Skill（`shadergraph_*`），支持创建/查看/编辑 Shader Graph 和 Sub Graph 资产，含节点增删、连线、属性管理等操作。配套 `ShaderGraphReflectionHelper`（2697 行）和 `ShaderGraphNodeRegistry` 实现类型安全的反射编辑。
- **DOTween 动画模块** — 新增 21 个 Skill（`dotween_*`），覆盖 DOTween 状态查询、Settings 配置、Tween/Sequence 脚本生成，以及 DOTween Pro Animation 组件的完整 CRUD。配套 `DOTweenReflectionHelper` 和 `DOTweenPresenceDetector` 实现纯反射调用。
- **PostProcess 后处理模块** — 新增 20 个 Skill（`postprocess_*`），提供 SRP 后处理效果的统一管理，含 Bloom、DoF、Tonemapping、Vignette、Color Adjustments 快捷配置。
- **Volume 体积系统模块** — 新增 18 个 Skill（`volume_*`），支持 VolumeProfile 创建/管理、VolumeComponent 增删改查、批量参数设置。
- **Graphics 图形设置模块** — 新增 11 个 Skill（`graphics_*`），涵盖质量设置、渲染管线资产管理、Always Included Shaders 和 Shader Stripping 配置，从 Project 模块独立为专用模块。
- **URP 管线模块** — 新增 14 个 Skill（`urp_*`），覆盖 URP 资产配置、Renderer 数据和 Renderer Feature 的增删改查与启停。
- **Decal 贴花模块** — 新增 14 个 Skill（`decal_*`），支持 URP Decal Projector 的创建/配置/查找/批量操作，含 Renderer Feature 自动注入。
- **RenderPipelineSkillsCommon** — 新增 SRP 共享基础设施（877 行），为 URP、Volume、PostProcess、Decal、Graphics 模块提供统一的渲染管线感知和配置操作辅助方法。
- **6 个新 Advisory 模块** — `addressables-design`（8 文档）、`netcode-design`（9 文档）、`shadergraph-design`（5 文档）、`unitask-design`（8 文档）、`dotween-design`（8 文档）、`yooasset-design`（8 文档），Advisory 总数从 13 增至 19。
- **asmdef 扩展** — 新增 `Unity.Netcode.Runtime`、`YooAsset`、`Unity.RenderPipelines.*.Runtime` 程序集引用；新增 `NETCODE_GAMEOBJECTS`、`YOO_ASSET`、`SRP_CORE`、`URP`、`HDRP` 版本定义。
- **SkillCategory 扩展** — `UnitySkillAttribute` 新增 Netcode、YooAsset、DOTween、Graphics、Volume、URP、Decal、PostProcess、ShaderGraph 共 9 个分类枚举。
- **SkillRouter ShaderGraph 路由** — 新增 `shadergraph`/`subgraph`/`着色图`/`子图` 意图关键词映射。

### Changed
- **路径检查统一** — 多个模块（AssetImportSkills、AssetSkills、CleanerSkills、SkillPlanningService、GameObjectFinder）统一使用 `SkillsCommon.PathExists()` 替代重复的 `File.Exists + Directory.Exists` 组合。
- **类型查找缓存** — `SkillsCommon.FindTypeByName()` 提供跨程序集类型查找与缓存（含 null miss 缓存），`PerceptionSkills.FindTypeInAssemblies` 委托至此实现。
- **Quality 设置迁移** — `project_get_quality_settings` 和 `project_set_quality_level` 从 ProjectSkills 迁移至 GraphicsSkills，与渲染管线设置统一管理。
- **LightmapSettings API 适配** — `LightGetLightmapSettings` 改用 `Lightmapping.lightingSettings` 读取 `lightmapMaxSize`/`lightmapPadding`，兼容新版 LightingSettings 工作流。
- **spritePackingTag 兼容处理** — AssetImportSkills 对已废弃的 `spritePackingTag` 加 `#if !UNITY_2023_1_OR_NEWER` 条件编译保护。
- **CinemachineSkills 编译清理** — `FindCinemachineType` 方法增加 `#if CINEMACHINE_2 || CINEMACHINE_3` 条件编译，消除未安装 Cinemachine 时的编译警告。
- **UnitySkillsWindow 优化** — Validate 按钮文本本地化，移除未使用的 `_showSkillConfig` 和 `_autoStartServer` 字段。
- **SkillPlanningService 简化** — `ResolveGameObject` 使用默认参数合并两个重载；路径存在性检查统一委托 `SkillsCommon.PathExists`。
- **版本号更新** — `SkillsLogger.Version`、`package.json`、Python helper 和文档同步提升到 `1.8.0`。

### Docs
- **9 个新功能模块文档** — decal、dotween、graphics、netcode、postprocess、shadergraph、urp、volume、yooasset 各含完整 SKILL.md。
- **6 个新 Advisory 文档集** — 共 ~46 个 markdown 文档，覆盖 Addressables、Netcode、ShaderGraph、UniTask、DOTween、YooAsset 的设计要点与常见陷阱。
- **architecture / patterns advisory 扩展** — architecture 新增"执行顺序与入口保护"章节；patterns 新增"Decision Lab"对比决策方法。
- **SKILL.md 统计同步** — 模块文档目录从 53 更新为 68（49 功能 + 19 Advisory）。

## [1.7.3] - 2026-04-20

### Added
- **Cinemachine 能力全面提升** — 新增 11 个 Skill（`cinemachine_set_priority`、`cinemachine_set_blend`、`cinemachine_set_brain`、`cinemachine_create_sequencer`、`cinemachine_sequencer_add_instruction`、`cinemachine_create_freelook`、`cinemachine_configure_camera_manager`、`cinemachine_configure_body`、`cinemachine_configure_aim`、`cinemachine_configure_extension`、`cinemachine_configure_impulse_source`），Cinemachine 技能总数从 23 增至 34，覆盖 Priority/Blend/Brain 配置、Sequencer/FreeLook 创建、Body/Aim 阶段统一配置、Extension 与 ImpulseSource 调节。
- **纹理导入器扩展** — 新增 `texture_get_import_settings`、`texture_find_assets`、`texture_get_info`、`texture_find_by_size`、`texture_set_type`、`texture_set_platform_settings`、`texture_get_platform_settings`、`texture_set_sprite_settings`、`sprite_set_import_settings`，支持按尺寸查找纹理、逐平台覆盖设置、Sprite 专属配置等。
- **音频导入器与场景技能扩展** — 新增 `audio_get_import_settings`、`audio_find_clips`、`audio_get_clip_info`、`audio_add_source`、`audio_get_source_info`、`audio_set_source_properties`、`audio_find_sources_in_scene`、`audio_create_mixer`，覆盖音频资产查询、AudioSource 场景管理和 AudioMixer 创建。
- **模型导入器扩展** — 新增 `model_get_import_settings`、`model_find_assets`、`model_get_mesh_info`、`model_get_materials_info`、`model_get_animations_info`、`model_get_rig_info`、`model_set_animation_clips`、`model_set_rig`，覆盖网格统计、材质/动画/骨骼信息查询、动画片段分割与绑定模式切换。
- **资产标签管理** — 新增 `asset_set_labels` 和 `asset_get_labels`，支持对资产设置和读取标签。

### Changed
- **CinemachineAdapter 架构抽象** — 新增 `CinemachineAdapter.cs` 适配层（562 行），将 Cinemachine 2.x 与 3.x 的 API 差异集中到适配器，`CinemachineSkills` 的业务方法不再包含条件编译，提高可维护性和可读性。
- **GameObjectFinder 增强** — 新增 `SafePathExists` 验证方法和 `EnsureDirectoryExists` 辅助方法，统一资产路径校验逻辑。
- **SkillPlanningService 批量分析重构** — 引入 `BatchAnalyzeContext` 上下文对象，将批量操作的 item 解析、错误收集、计划构建统一封装，消除多个 `Analyze*Batch` 方法间的重复代码。
- **UIToolkitSkills / GameObjectSkills 代码质量提升** — 提取公共 helper、消除重复模式、补全日志记录。

### Docs
- **importer 模块文档大幅扩展** — `importer/SKILL.md` 从基础导入设置扩展到涵盖查询、运行时、平台覆盖、动画/骨骼等完整能力矩阵。
- **cinemachine 模块文档补全** — `cinemachine/SKILL.md` 新增全部 11 个 Skill 的参数文档。
- **asset 模块文档更新** — `asset/SKILL.md` 新增 `asset_set_labels`、`asset_get_labels` 文档。
- **版本号更新** — `SkillsLogger.Version`、`package.json`、Python helper 和文档同步提升到 `1.7.3`。

## [1.7.2] - 2026-04-18

### Changed
- **SkillRouter 热路径性能优化** — `SkillInfo` 注册时预计算并缓存 `ParameterNames` 与 `AllowedParameterSet`，`Execute`/`DryRun` 的未知参数校验不再每次分配新的数组/`HashSet`；`GetSchema()` 新增结果缓存 `_cachedSchema`，避免每次调用都重新序列化 manifest,多次访问时开销降为常量。
- **BatchExecutor 反射结果缓存** — `BatchExecutor` 新增 `ConcurrentDictionary<Type, bool>` 缓存 `HasErrorMember` 反射结果，大规模批量操作时避免对每个 item 重复执行 `GetProperty("error")` / `GetField("error")`。
- **AsyncJobService 代码去重** — 将 Test 作业 filter 构建逻辑抽取为私有 `BuildTestFilter` 方法，消除 `StartTestRun` 与 Job reconnect 分支之间的重复块，修改测试过滤规则时只需改一处。
- **BatchJobService 日志写入统一** — 取消 Job 的日志写入改用现有 `AddLog()` 辅助方法，使所有日志落盘逻辑走同一条路径，便于后续统一格式或注入遥测。
- **SkillsHttpServer 清理冗余字段** — 移除 `RequestJob.ResponseDispatched` 与所有对它的赋值。该字段自引入以来从未被读取，删除后 request pool 状态更精简。

### Fixed
- **SKILL.md 幽灵引用修复** — skillcheck 审计发现 11 个模块（cinemachine/cleaner/optimization/profiler/project/scriptableobject/smart/terrain/timeline/batch 及 Routing 区块）的 `DO NOT` 章节指向了不存在的"纠正后 Skill"，AI 被误导后仍会调用 404 路由。统一替换为真实 Skill 名（例如 `optimize_compress_textures`→`optimize_compress_texture`、`terrain_set_heights`→`terrain_set_heights_batch`、`smart_query`→`smart_scene_query` 等）。
- **SKILL.md 反向错误修复** — `shader/SKILL.md` 将 `shader_get_properties` 错误标为"不存在"，实际该 Skill 存在且返回 shader 属性定义；`navmesh/SKILL.md` 同样把实际存在的 `navmesh_set_agent`、`navmesh_add_obstacle` 标记为幻觉。改为说明它们与 `component_*` 的分工关系，避免 AI 绕开真实 Skill。

### Docs
- **batch 模块补齐 Skill 文档** — `batch/SKILL.md` 新增 `batch_query_assets`（6 个参数含 `searchFilter`/`folder`/`typeFilter`/`namePattern`/`labelFilter`/`maxResults`）与 `batch_retry_failed`（`reportId`/`runAsync`/`chunkSize`）的完整表格，此前这两个 Skill 仅存在于 C# 但未在文档中暴露。
- **agent.md 目录结构同步** — `Editor/Skills/` 由 55 更新为 61 个 C# 文件；`unity-skills~/skills/` 由 54 更新为 53 个模块文档（40 functional + 13 advisory），匹配 v1.7.0 以来的 importer/workflow 目录合并结果。
- **版本号更新** — `SkillsLogger.Version`、`package.json`、Python helper 和文档同步提升到 `1.7.2`。

## [1.7.1] - 2026-04-17

### Added
- **机器可读 Skill Schema 端点** — 新增 `GET /skills/schema`，返回带 `schemaVersion`、参数定义、Skill 元数据和保留请求参数的结构化清单，便于 AI Agent 在低 token 场景下精确获取能力边界。
- **Python `get_skill_schema()`** — Python 客户端新增 `get_skill_schema()`，可直接读取服务端 canonical skill schema。
- **`test_smoke_skills` 回归验证 Skill** — Test 模块新增 `test_smoke_skills`，对安全只读技能直接执行、对其余技能走 dry-run，用于快速 smoke test 与发布前回归检查。

### Changed
- **Cinemachine 属性设置增强** — `cinemachine_set_vcam_property` 现支持 `fov`、`nearClip`、`farClip`、`orthoSize` 等镜头简写参数，设置常见 Lens 属性时更直接。
- **Skill 参数校验升级** — SkillRouter 现在会识别未知参数、返回允许参数列表，并对常见误传参数给出建议与提示；语义校验结果也会更完整地体现在 execute / dry-run / plan 响应中。
- **异步测试作业基础设施增强** — 异步测试与 smoke job 的作业管理能力得到扩展，提升测试型 Skill 的可观测性与复用性。
- **版本号更新** — `SkillsLogger.Version`、`package.json`、Python helper 和文档同步提升到 `1.7.1`。

### Fixed
- **Undo / Redo 事务边界修复** — 修正 REST 调用场景下的撤销分组与事务边界处理，改善 `editor_undo`、`editor_redo` 以及 workflow 撤销/重做的一致性。
- **只读组件查询副作用修复** — `ComponentSkills` 在读取 `Renderer` 材质属性时改用安全路径，避免查询型操作意外触发材质实例化。
- **场景感知与辅助行为稳定性提升** — 改进 `PerceptionSkills` 场景根对象枚举方式，并修复 `CanvasGroup` Undo 注册、书签空选择处理，以及 XR 场景报告对 ActionBased locomotion/turn provider 的识别。

### Docs
- **模块文档与参考资料增强** — 多个 `SKILL.md` 补充了更精确的签名、参数说明和调用示例，并为 importer、ProBuilder、UI、UI Toolkit、XR 等模块拆分出独立 reference 文档。
- **统计信息同步** — 文档统计已同步为 `41` 个功能模块、`543` 个 REST Skills，且 Test 模块更新为 `11` 个 Skills。

## [1.7.0] - 2026-04-15

### Added
- **统一异步 Job 基础设施 `AsyncJobService`** — 新增统一 Job 模型，接入 Test Runner、包管理和脚本变更监测。Job 状态、进度事件和日志通过 `BatchPersistence` 持久化到 `Library/UnitySkills/batch_state.json`，Domain Reload 后未完成作业会以 `reconnecting` 状态恢复。
- **批量处理服务 `BatchJobService`** — 新增批量作业执行器，负责 Batch 预览结果的排队、分块执行、取消、报告生成，以及与 `WorkflowManager` 的会话级联动。
- **Batch Skill 模块** — 新增 21 个 Batch/Job 相关 Skill，包括 `batch_query_*`、`batch_preview_*`、`batch_execute`、`batch_report_*`、`batch_retry_failed` 以及 `job_status`/`job_logs`/`job_list`/`job_wait`/`job_cancel`，形成查询、预览、执行、报告、重试与状态查询闭环。
- **作业模型与持久化** — 新增 `BatchModels.cs` 与 `BatchPersistence.cs`，定义批量作业、报告、预览、日志、进度事件等数据结构，并提供 JSON 持久化与 reload 后恢复逻辑。
- **Skill 规划服务 `SkillPlanningService`** — 新增通用规划引擎，为 Skill 提供参数校验、语义检查、变更预测、风险提示和 `serverAvailability` 预判；HTTP 层新增 `POST /skill/{name}?mode=dryRun` 与 `POST /skill/{name}?mode=plan`。
- **`workflow_plan` 聚合规划 Skill** — Workflow 模块新增 `workflow_plan`，可对多步 Skill 调用做聚合规划，输出总风险、依赖关系、分步计划与警告信息。

### Changed
- **Perception Skill 全面升级** — 新增 7 个场景感知 Skill（`scene_analyze`, `scene_health_check`, `scene_contract_validate`, `scene_component_stats`, `scene_find_hotspots`, `scene_diff`, `project_stack_detect`），现有 Skill 补充 `scene_export_report`、`scene_dependency_analyze`、`scene_context` 等能力，Perception 模块从 11 个 Skill 扩展到 18 个。
- **SkillRouter 增强** — `UnitySkillAttribute` 新增 `MutatesScene`、`MutatesAssets`、`MayTriggerReload`、`RiskLevel`、`RequiresPackages` 元数据；SkillRouter 初始化时构建输出索引并扩展中英文意图同义词表；新增 `DryRun`/`Plan` 请求模式。
- **技能文档规范化** — 14 个 SKILL.md 模块文档新增标准参数表（参数名/类型/必选/默认值/描述）和 Canonical Signature 块，所有 Skill 参数与 C# 方法签名完全一致。
- **Skill 数量提升** — 总计从 513 提升到 542（+29 个），功能模块从 40 个增加到 41 个（新增 Batch），Advisory 模块从 14 个调整为 13 个（移除 XR advisory，其内容合并到 xr/SKILL.md）。

### Fixed
- **Batch 作业恢复与状态一致性** — `BatchPersistence` 会在 Domain Reload 后将未终态 Job 规范化为 `reconnecting`，避免批量任务在重载后丢失执行状态。
- **Test Runner 回调泄漏** — `AsyncJobService` 在 Job 完成后显式 `UnregisterCallbacksFromObject`，防止回调对象泄漏导致内存占用持续增长。
- **Skill 路由中文匹配** — `SkillRouter` 新增中文同义词表，确保"创建方块"等中文自然语言能正确路由到 `gameobject_create`。

## [1.6.9] - 2026-04-03

### Added
- **依赖链查询端点 `/skills/chain`** — 新增 GET `/skills/chain?output=instanceId` 端点，基于 `Outputs` 元数据构建的反向索引，快速查找能产出指定字段的 Skill 链，支持 AI Agent 自动编排多步工作流。
- **Dry-Run 模式** — POST `/skill/{name}?dryRun=true` 仅验证参数合法性而不实际执行技能，返回参数解析结果和缺失必选参数提示，便于 Agent 预检查调用可行性。
- **意图解析增强** — `SkillRouter` 新增中英文同义词映射表（70+ 条目）、操作类型提取（Create/Delete/Query/Modify/Execute/Analyze）和分类关键词匹配，`/skills/recommend` 端点支持中文子串匹配（如"创建方块"直接匹配到 `gameobject_create`）。
- **输出索引** — `SkillRouter` 初始化时构建 `output field → producing skills` 反向索引，支撑依赖链查询和意图推荐的输出字段匹配。

### Fixed
- **Domain Reload 后服务器偶发不重启** — 新增 `ProcessJobQueue` 安全网机制：每 5 秒检测服务器是否应运行但未运行，自动触发恢复，不再完全依赖 `EditorApplication.delayCall`（该 API 在某些 Unity 版本/状态下可能不触发）。同时将端口释放等待从 500ms 增加到 2000ms，`delayCall` 恢复增加 1 秒延迟缓冲端口释放。
- **连续失败计数过于激进** — `MaxConsecutiveFailures` 从 5 提升到 10；新增 5 分钟时间衰减机制，距上次失败超过 5 分钟自动重置计数器，防止历史失败累积导致服务器永久放弃自动重启。
- **必选参数判定优化** — `SkillRouter` 新增 `IsParameterRequired()` 方法替代简单的 `!p.HasDefaultValue` 判断，正确处理值类型可空参数的必选性识别。

### Changed
- **版本号更新** — `SkillsLogger.Version`、`package.json`、Python helper 和文档同步提升到 `1.6.9`。
- **版本号更新** — `SkillsLogger.Version`、`package.json`、Python helper 和文档同步提升到 `1.7.0`。

## [1.6.8] - 2026-04-03

### Fixed
- **`gameobject_create` parentName 参数失效** — 文档声称 `gameobject_create` 支持 `parentName` 参数，但 C# 实现中缺失该参数，SkillRouter 静默忽略导致子物体被创建在场景根层级而非预期父物体下。现已在方法签名中添加 `parentName`/`parentInstanceId`/`parentPath` 三种父物体标识参数，创建后自动 `SetParent`，坐标改为 `localPosition` 语义。
- **`gameobject_create_batch` 缺少 parent 支持** — `BatchCreateItem` 同步添加 `parentName`/`parentInstanceId`/`parentPath` 字段，批量创建时支持为每个物体指定父物体。
- **`prefab_instantiate` 缺少 parent 支持** — 新增 `parentName`/`parentInstanceId`/`parentPath` 参数，实例化后自动设置父物体，返回值新增 `path` 字段。
- **`prefab_instantiate_batch` 缺少 parent 支持** — `BatchInstantiateItem` 同步添加 parent 相关字段。

### Changed
- **文档与实现一致性修复** — `gameobject/SKILL.md` 补充 `parentInstanceId`/`parentPath` 参数说明，`prefab/SKILL.md` 同步更新参数表和 batch item 属性列表。
- **版本号更新** — `SkillsLogger.Version`、`package.json`、Python helper 和文档同步提升到 `1.6.8`。

## [1.6.7] - 2026-04-02

### Added
- **Intent-Level Skill Metadata (v1.7 Attributes)** — 全部 513 个 Skill 的 `[UnitySkill]` 特性新增 6 个结构化元数据字段：`Category`（SkillCategory 枚举）、`Operation`（SkillOperation Flags 枚举: Query/Create/Modify/Delete/Execute/Analyze）、`Tags`（语义标签数组）、`Outputs`（返回字段声明）、`RequiresInput`（前置依赖声明）、`ReadOnly`（纯查询标记）。
- **`/skills` 过滤查询 API** — GET `/skills` 端点支持 query string 过滤：`category`、`operation`、`tags`、`readOnly`、`q`（文本搜索 name+description+tags），多条件 AND 组合。无参数调用完全向后兼容。
- **`/skills/recommend` 意图推荐端点** — 新增 GET `/skills/recommend?intent=create+cube&topN=10`，基于关键词评分排序（name=3分, tags=2分, description=1分），返回 top-N 匹配 Skill 及 relevance score。
- **Metadata Validation 工具** — 编辑器 Skills 标签页新增 **Validate** 按钮，检查 6 条元数据规则（Category/Operation/Tags/Outputs 完整性、ReadOnly 与 TracksWorkflow 矛盾检测、Delete/Modify 操作的 RequiresInput 检查），结果输出到 Console。
- **Python 客户端增强** — `get_skills()` 新增 `category`/`operation`/`tags`/`read_only`/`q` 过滤参数；新增 `find_skills(intent, top_n)` 调用服务端推荐；新增 `get_skill_chain(target_output)` 查找产出特定字段的 Skill 链。

### Changed
- **SkillsHttpServer QueryString 管道** — `RequestJob` 新增 `QueryString` 字段，HTTP 请求的查询字符串从 `ListenLoop` 完整传递到 `ProcessJob`，支撑过滤和推荐端点。
- **SkillRouter Manifest 增强** — `/skills` manifest 新增 `categories` 和 `operationTypes` 顶层字段；每个 Skill 条目新增 `category`、`operation`、`tags`、`outputs`、`requiresInput`、`readOnly` 字段。
- **版本号更新** — `SkillsLogger.Version`、`package.json`、Python helper 和文档同步提升到 `1.6.7`。

## [1.6.6] - 2026-03-26

### Fixed
- **CM3 `PrioritySettings` 编译兼容修复** — `CinemachineSkills.cs` 不再依赖 `PrioritySettings` 的隐式 `int` 转换；`cinemachine_create_vcam` 和 `cinemachine_set_active` 统一改为使用 `Priority.Value` 这一最低公共 API，修复 Unity 2022.3 项目在安装部分 `Cinemachine 3.0.x` 预览版本时出现的 `CS0029` / `CS0030` 编译错误。
- **`cinemachine_set_active` 返回值修正** — CM3 分支返回消息现在输出实际优先级数值，而不是 `PrioritySettings` 结构体对象文本。

### Changed
- **CM3 支持边界说明** — 文档明确当前 `Cinemachine 3` 兼容策略以 `3.0.0-pre.5+ / stable 3.x` 的共同 API 为基线；更早的 `3.0.0-pre.1/.2` 因核心相机 API 仍在演化，不纳入当前兼容范围。
- **Package Manager 元数据补全** — `SkillsForUnity/package.json` 新增 `changelogUrl`，Unity Package Manager 的 `Changelog` 按钮现在会跳转到仓库中的 `CHANGELOG.md`。
- **版本号更新** — `SkillsLogger.Version`、`package.json`、Python helper 和安装文档同步提升到 `1.6.6`。

## [1.6.5] - 2026-03-20

### Added
- **SKILL.md 双模式系统 (Semi-Auto / Full-Auto)**：全新操作模式机制，默认半自动模式仅激活 ~80 个高频 Skills（script/perception/scene/editor/asset/workflow/debug/console + 14 advisory），大幅降低 Token 消耗；用户说"全自动模式"/"full auto" 时激活全部 513 Skills。
- **防幻觉 Guardrails**：全部 39 个功能模块 SKILL.md 新增 `## Guardrails` 段落，包含 Mode 声明、DO NOT 幻觉列表（常见不存在的 Skill 名称和参数错误）、Routing 路由提示（引导 AI 使用正确模块）。
- **Skill 命名约定**：模块索引新增 `Skill Naming Convention` 段落，列出全部有效 module 前缀，AI 可据此判断 Skill 是否存在。
- **Advisory 模块触发词优化**：14 个 advisory 模块的 YAML description 全面升级，增加自然语言触发词（"怎么组织代码"、"太慢了"、"用什么模式好" 等）和问题描述型触发词，中英文覆盖更智能。
- **`component_set_property` 新增 `assetPath` 参数**：支持将 Project Asset（ScriptableObject、Prefab、Material、Texture、AudioClip 等）赋值给组件的 Object 引用字段。通过 `AssetDatabase.LoadAssetAtPath` 加载资产，支持精确类型匹配与降级兼容。`component_set_property_batch` 同步支持。
- **`prefab_set_property` (1 skill)**：新增 Prefab 资产属性编辑技能，通过 `SerializedObject` + `SerializedProperty` 直接编辑 Prefab Asset 文件的组件字段（无需实例化到场景）。支持：
  - 基本类型（int/float/bool/string/enum）、Vector2/3/4、Color、Rect、Bounds、LayerMask
  - Asset 引用赋值（通过 `assetReferencePath` 参数）
  - Prefab 内子对象编辑（通过 `gameObjectName` 参数）
  - 属性名自动回退（`propertyName` → `m_PropertyName` → `_propertyName` → `m_propertyName`）

### Changed
- **SKILL.md 模块索引新增 Mode 列**：38 个功能模块标注 SA (Semi-Auto) 或 FA (Full-Auto)，14 个 advisory 模块明确标注"双模式均可用"。
- **主入口 SKILL.md**：YAML description 新增 16 个中英文触发词（含模式切换词）；Core Rule #2 添加 `[Full-Auto]` 前缀；workflow/gameobject 示例标注为 Full-Auto 示例。
- **全部模块 SKILL.md 触发词标准化**：54 个 SKILL.md 文件均包含标准 `Triggers:` 格式的中英文双语触发词。
- **REST Skills 总数**：512 → 513（+1 prefab_set_property）。
- **Prefab 模块**：10 → 11 skills。

### Docs (SKILL.md 文档质量审计与修复 — 26 文件)
- **Batch Skill 返回值补全**：为 9 个模块共 25 个 batch 技能补全 `**Returns**` 结构文档：gameobject（9）、component（3）、light（2）、material（4）、asset（3）、importer（3）、prefab（1）、ui（1）、script（1）。
- **三元定位参数补全**：修复 `gameobject_set_transform`、`gameobject_set_parent`、`gameobject_set_active` 缺失的 `instanceId`/`path` 参数文档；`gameobject_set_parent` 参数名从 `name`/`parentName` 修正为 C# 实际签名 `childName`/`childInstanceId`/`childPath`/`parentName`/`parentInstanceId`/`parentPath`（6 参数完整文档）。
- **Object Targeting 统一说明**：为 gameobject、component、light、material 四个功能模块顶部新增"Object Targeting"注释段落，说明所有单对象 Skill 支持 `name`/`instanceId`/`path` 三元定位。
- **Batch 参数文档增强**：`gameobject_set_parent_batch` 补全 6 参数说明 + 三种定位方式示例；`light_set_properties_batch` 补全全部可用参数（identifier + `r`/`g`/`b`/`intensity`/`range`/`shadows`）+ 混合示例。
- **`component_set_property` 类型示例**：新增 5 种 `value` 参数类型的用法示例（float/bool、Vector3 JSON、Color JSON、Enum 字符串），消除类型模糊导致的幻觉风险。
- **Component 技能可见性**：`component_set_enabled` 和 `component_copy` 从 "Additional Skills" 部分提升到 Skills Overview 表格，提高 AI 发现率。
- **Advisory 模块 Mode 自声明**：13 个 advisory 模块（architecture/adr/performance/asmdef/blueprints/script-roles/scene-contracts/testability/patterns/async/inspector/scriptdesign/project-scout）新增 `**Mode**: Both (Semi-Auto + Full-Auto) — advisory only, no REST skills` 声明，实现 52/52 模块 SKILL.md 全覆盖 Mode 标记（8 SA + 31 FA + 13 Both）。
- **缺失 Guardrails 补建**：为 patterns、async、inspector 三个 advisory 模块新建 `## Guardrails` 段落（含 Mode 声明和反模式指导），实现全部 advisory 模块 Guardrails 100% 覆盖。
- **Cinemachine deprecated 增强**：`cinemachine_add_component` 弃用标记从行内括号改为醒目 blockquote `> **DEPRECATED**`，并补全 `instanceId`/`path`/`componentType` 参数文档。
- **`cinemachine_set_targets` 参数补全**：新增 `instanceId` 和 `path` 参数说明，支持精确定位 VCam。
- **Terrain layerIndex 说明**：`terrain_paint_texture` 的 `layerIndex` 参数补充"0-based"说明和 `terrain_get_info` 查询引导。

## [1.6.4] - 2026-03-15

### Added
- **XRSkills (22 skills)**：新增 `XRSkills.cs` XR Interaction Toolkit 技能模块 + `XRReflectionHelper.cs` 反射辅助类，通过纯反射实现 XRI 2.x（Unity 2022）/ 3.x（Unity 6）跨版本兼容，无需编译期依赖 XRI 程序集。包含：
  - **Setup & Validation (5 skills)**：`xr_check_setup`（全面检查 XR 项目配置）、`xr_setup_rig`（创建完整 XR Origin Rig 含 Camera/Left/Right Controller 层级）、`xr_setup_interaction_manager`（添加 XRInteractionManager）、`xr_setup_event_system`（替换 StandaloneInputModule 为 XRUIInputModule）、`xr_get_scene_report`（XR 场景诊断报告）
  - **Interactor Skills (4 skills)**：`xr_add_ray_interactor`（射线交互器 + LineRenderer）、`xr_add_direct_interactor`（近距离抓取 + SphereCollider trigger）、`xr_add_socket_interactor`（插座交互器）、`xr_list_interactors`（列出所有交互器）
  - **Interactable Skills (4 skills)**：`xr_add_grab_interactable`（可抓取物体 + Rigidbody + Collider + movementType 配置）、`xr_add_simple_interactable`（简单交互）、`xr_configure_interactable`（配置交互属性）、`xr_list_interactables`（列出所有交互物体）
  - **Locomotion Skills (5 skills)**：`xr_setup_teleportation`（传送提供者）、`xr_add_teleport_area`（传送区域）、`xr_add_teleport_anchor`（传送锚点 + 可视化指示器）、`xr_setup_continuous_move`（连续移动）、`xr_setup_turn_provider`（Snap/Continuous 转向）
  - **Advanced Skills (4 skills)**：`xr_setup_ui_canvas`（Canvas XR 兼容化 + TrackedDeviceGraphicRaycaster）、`xr_configure_haptics`（触觉反馈）、`xr_add_interaction_event`（交互事件绑定）、`xr_configure_interaction_layers`（交互层配置）
- **XR Advisory 模块**：新增 `skills/xr/SKILL.md` 文档，包含 6 个 XR 开发工作流指南（Rig 搭建、抓取交互、传送系统、连续移动、XR UI、交互事件与反馈）、组件依赖关系图、movementType 选择指南、版本兼容性说明。
- **XRReflectionHelper 反射辅助**：25+ XR 类型的版本映射表（XRI 3.x 子命名空间 → 2.x 根命名空间 fallback），缓存类型解析，版本自动检测（通过命名空间探测区分 2.x/3.x）。

### Changed
- **asmdef versionDefines 扩展**：`UnitySkills.Editor.asmdef` 新增 `XRI`（`com.unity.xr.interaction.toolkit [2.0,4.0)`）和 `XR_CORE_UTILS`（`com.unity.xr.core-utils [2.0,4.0)`）条件编译符号。不添加 XRI 程序集引用（纯反射）。
- **REST Skills 总数**：490 → 512（+22 XR skills）。
- **Skills 文件数**：39 → 40 个 `*Skills.cs` 文件。
- **Advisory 模块数**：13 → 14（+1 XR advisory）。

## [1.6.3] - 2026-03-14

### Added
- **ProBuilderSkills (22 skills)**：新增 `ProBuilderSkills.cs` 模块，通过条件编译 `#if PROBUILDER` 支持可选包 `com.unity.probuilder`（5.x–7.x）。未安装时所有 skill 返回友好错误提示。包含：
  - `probuilder_create_shape`：创建参数化 ProBuilder 形状（Cube/Sphere/Cylinder/Cone/Torus/Prism/Arch/Pipe/Stairs/Door/Plane），支持位置、尺寸、旋转设置，支持 `parent` 参数指定父对象
  - `probuilder_extrude_faces`：面拉伸（IndividualFaces/FaceNormal/VertexNormal 三种模式）
  - `probuilder_subdivide`：细分整个网格或指定面
  - `probuilder_bevel_edges`：边倒角，支持顶点索引对指定
  - `probuilder_delete_faces`：按索引删除面
  - `probuilder_merge_faces`：合并多个面为单个面
  - `probuilder_set_face_material`：按面设置材质（支持 materialPath 或 submeshIndex）
  - `probuilder_flip_normals`：翻转面法线方向
  - `probuilder_get_info`：获取网格完整信息（顶点/面/边/三角形数、形状类型、材质列表、submesh 分布）
  - `probuilder_center_pivot`：居中枢轴或设置到指定世界坐标
  - `probuilder_create_batch`：批量创建多个 ProBuilder 形状
  - `probuilder_move_vertices`：按增量移动顶点（用于斜坡/坡道等造型）
  - `probuilder_set_vertices`：设置顶点绝对位置
  - `probuilder_get_vertices`：查询顶点位置信息
  - `probuilder_combine_meshes`：合并多个 ProBuilder 网格
  - `probuilder_set_material`：设置整个网格的材质（支持颜色快捷方式）

- **UISkills 新增 10 skills**（16 → 26）：基于 UGUI 源码实现完整控件层级结构：
  - `ui_create_dropdown`：创建 Dropdown（含 Template/ScrollRect/Viewport/Content/Item 完整子层级和 Toggle 选项），支持逗号分隔的选项列表
  - `ui_create_scrollview`：创建 ScrollRect 滚动视图（含 Viewport + RectMask2D + Content），支持方向和 MovementType 配置
  - `ui_create_rawimage`：创建 RawImage 元素（用于 Texture2D/RenderTexture 显示）
  - `ui_create_scrollbar`：创建独立 Scrollbar（含 Sliding Area + Handle），支持四方向和离散步数
  - `ui_set_image`：设置 Image 高级属性（Type: Simple/Sliced/Tiled/Filled、FillMethod: Radial360/Horizontal/Vertical 等、fillAmount、preserveAspect）
  - `ui_add_layout_element`：添加/配置 LayoutElement（minWidth/preferredWidth/flexibleWidth 等完整布局约束）
  - `ui_add_canvas_group`：添加/配置 CanvasGroup（alpha/interactable/blocksRaycasts/ignoreParentGroups）
  - `ui_add_mask`：添加 Mask（模板缓冲）或 RectMask2D（矩形裁剪）
  - `ui_add_outline`：添加 Shadow 或 Outline 视觉效果（颜色、距离、graphicAlpha）
  - `ui_configure_selectable`：配置 Selectable 属性（Transition 模式、ColorBlock 四态颜色、Navigation 模式）

- **UIToolkitSkills 新增 10 skills + 4 模板**（15 → 25）：基于 UI Toolkit 源码示例实现 UXML/USS 程序化操作：
  - `uitk_add_element`：向 UXML 添加元素（Label/Button/Toggle/Slider/TextField 等），支持 name/text/classes/style/binding-path
  - `uitk_remove_element`：从 UXML 按 name 删除元素
  - `uitk_modify_element`：修改 UXML 元素属性（text/classes/style/name/binding-path/自定义属性）
  - `uitk_clone_element`：复制 UXML 元素（含子元素）
  - `uitk_add_uss_rule`：向 USS 文件添加或更新样式规则（自动检测已有选择器并替换）
  - `uitk_remove_uss_rule`：从 USS 删除指定选择器的规则
  - `uitk_list_uss_variables`：提取 USS 中所有 CSS 自定义属性定义和 `var()` 引用
  - `uitk_create_editor_window`：生成 EditorWindow C# 脚本模板（CreateGUI + UXML/USS 绑定 + MenuItem）
  - `uitk_create_runtime_ui`：生成运行时 MonoBehaviour 脚本（UIDocument 查询 + 事件注册/注销模式）
  - `uitk_inspect_document`：检查场景中 UIDocument 的 VisualElement 实时层级树（类型/名称/类列表/子节点）
  - 新增 4 个 `uitk_create_from_template` 模板：`tab-view`（标签页切换）、`toolbar`（顶部工具栏）、`card`（卡片组件）、`notification`（通知/Toast）

- **`ui_create_batch` 扩展**：批量创建支持新增的 `dropdown`、`scrollview`、`rawimage`、`scrollbar` 类型。

### Changed
- **asmdef 引用扩展**：`UnitySkills.Editor.asmdef` 新增 `Unity.ProBuilder` + `Unity.ProBuilder.Editor` 引用及 `PROBUILDER` versionDefines（`[5.0,7.0)`），与 Cinemachine 条件编译模式一致。
- **REST Skills 总数**：448 → 490（+10 ProBuilder + 6 ProBuilder新增 + 6 ProBuilder高级 + 10 UGUI + 10 UIToolkit）。
- **Skills 文件数**：38 → 39 个 `*Skills.cs` 文件。

## [1.6.2] - 2026-03-13

### Added
- **13 个 advisory 设计模块**：在 `unity-skills/skills/` 下新增架构、脚本职责、异步策略、ADR、Inspector 设计、性能审视、可测试性等建议型模块，用于辅助 AI 在真正写脚本前先做设计判断。
- **Workflow 会话恢复上下文补充**：工作流状态额外暴露 `currentTaskDescription` 等信息，便于 Python 客户端在超时或短暂断连后恢复上下文。
- **脚本编写后的编译反馈提示**：脚本相关 Skill/文档补充“保存后等待 Unity 自动编译，再主动检查错误日志并继续迭代修复”的引导。

### Changed
- **Unity 维护基线**：后续新增功能、回归验证与文档基线统一收敛到 `Unity 2022.3+`，当前重点适配目标为 `2022.3+ / Unity 6`。
- **Skill 模板目录迁移**：根目录 `unity-skills/` 已移入 UPM 包内 `SkillsForUnity/unity-skills~/`（波浪线隐藏目录），通过 `?path=SkillsForUnity` 安装时自动随包分发，无需完整克隆仓库。
- **默认请求超时统一**：Unity 服务端、Python 客户端和用户文档统一以 **15 分钟** 作为默认超时。
- **脚本生成建议增强**：创建脚本相关提示中，明确要求 AI 主动考虑耦合性、性能、可维护性与 Inspector 体验，而不是只生成可运行代码。
- **文档与安装说明同步**：更新 `README.md`、`README_EN.md`、`docs/SETUP_GUIDE.md`、`unity-skills/SKILL.md`、`.github` 文档等，补充 `447` 个 REST Skills、`13` 个 advisory 模块、完整 `unity-skills/` 模板目录与编译期短暂不可达说明。

### Fixed
- **`DebugSkills.cs` 编译错误**：修复 `debug_get_logs` 读取日志时 `LogEntryInfo` 对象初始化器使用了不合法的 `file, line` 简写，改为显式成员赋值，解决 `CS0747 Invalid initializer member declarator` 编译失败。
- **Workflow 历史可靠性**：为历史数据增加 `schemaVersion` 与迁移处理；补全 `.tmp` 崩溃恢复；恢复历史、撤销、重做时重新校验资产路径，避免信任旧磁盘记录导致的不一致或越界访问。
- **Workflow 快照去重性能**：`SnapshotObject()` 的去重由线性扫描改为 `HashSet<string>` 索引，避免批量操作时退化为 `O(n^2)`。
- **Workflow 追踪技能维护方式**：去掉 `SkillRouter` 中硬编码的工作流追踪列表，改为基于 `[UnitySkill(TracksWorkflow = true)]` 自动发现，降低后续漏配风险。
- **多场景对象查找**：`GameObjectFinder` 不再只搜索活跃场景，改为遍历所有已加载场景，修复多场景编辑下查找失败的问题。
- **REST 服务稳态与恢复**：`SkillsHttpServer` 增强监听线程准入限流、请求对象池、watchdog/keep-alive 恢复链路，并在脚本编译、Define 变更、资源重导入、包操作等导致短暂不可达时返回明确“稍后重试”提示。
- **重复限流逻辑**：移除主线程侧重复限流，统一由监听阶段执行请求准入，避免双重限流带来的行为不一致。
- **Python 客户端稳定性**：统一默认超时为 `900` 秒，复用 `requests.Session`，增强注册表损坏诊断、CLI 数值解析、可重试传输错误识别，以及 `WorkflowContext` 在超时/断连后的状态恢复。
- **文件 I/O 一致性**：统一更多文件读写为 UTF-8，补齐 `SafePath` 校验顺序，修复部分 Skill 先访问文件再校验路径的问题。
- **批处理与依赖分析性能**：清理批处理实现分裂，更多路径统一走 `BatchExecutor`；`CleanerSkills` 先建立依赖缓存再分析，移除重复 `GetDependencies()` 带来的性能瓶颈。
- **文档与注释编码问题**：修复一批中文文本与注释的编码异常，避免 AI 读取和用户查看时出现乱码。
- **Perception 模块 Skill 计数**：修正文档中 Scene/Asset/Audio/Texture/Model/Perception 六个模块的 Skill 数量偏差，补齐 `scene/SKILL.md` 遗漏的 `scene_find_objects`、`perception/SKILL.md` 遗漏的 `scene_tag_layer_stats` 和 `scene_performance_hints`，以及 `skills/SKILL.md` 缺失的 `uitoolkit` 行。
- **AnimatorSkills 值类型拷贝 Bug**：`AnimatorControllerParameter` 是值类型，`FirstOrDefault` 返回副本导致修改默认值不生效，改用 `Array.FindIndex` 直接修改原数组元素。
- **PrefabSkills 变体创建**：`prefab_create_variant` 使用 `SaveAsPrefabAsset` 创建的是普通 Prefab 而非 Variant，改用 `SaveAsPrefabAssetAndConnect` 正确创建变体。
- **MaterialSkills 内存泄漏**：`material_create` 未指定 `savePath` 时 `new Material()` 无人引用且无法被 GC 回收，现返回 `instanceId` 和警告信息供调用方后续引用或销毁。
- **ConsoleSkills 线程安全**：`_logs` 列表在 `OnLogMessage` 回调（可能来自后台线程）与 Skill 方法（主线程）之间无同步，添加 `lock` 保护所有读写操作。
- **PhysicsSkills 方向向量未归一化**：`physics_raycast`/`physics_raycast_all`/`physics_spherecast`/`physics_boxcast` 的 `direction` 参数未归一化导致 `maxDistance` 语义不正确，添加归一化和零向量检查。
- **BatchExecutor error 误计为成功**：`processor` 返回含 `error` 字段的对象不会触发 catch 而被计入 `successCount`，添加反射检测 error 字段逻辑，正确计入 `failCount`。
- **TextureSkills/ModelSkills 注释编码乱码**：修复 GBK 编码被误读为 UTF-8 导致的注释乱码（`TextureSkills.cs` L66、`ModelSkills.cs` L81）。
- **Python `call_skill_with_retry` 重试语义**：`max_retries=3` + `range(max_retries)` 实际只有 3 次尝试，与参数名"最多重试 3 次"语义不一致，改为 `range(1 + max_retries)` 确保总尝试次数为 `1 + max_retries`。
- **SKILL.md 文档补全**：补全 28 个模块约 166 个 Skill 的缺失文档条目，删除 `debug/SKILL.md` 中的幽灵条目 `debug_log`。
- **对象池无上限增长**：`SkillsHttpServer` 的 `ConcurrentBag<RequestJob>` 无上限归还，超过 `MaxPendingRequests` 时 Dispose 而非归还；超时 job 也无条件归还池中。
- **SkillRouter verbose 参数泄漏**：框架参数 `verbose` 读取后未从 `args` 移除，导致后续参数绑定可能出现类型不匹配错误。
- **SkillRouter manifest 缓存竞态**：`_cachedManifest` 读写无同步保护，并发调用可能导致重复构建或读到半初始化值，添加 double-check locking。
- **RegistryService 原子写入恢复**：`AtomicReadModifyWrite` 在崩溃后 `.tmp` 文件残留但主文件为空时无恢复机制，新增启动时 `.tmp` 备份恢复检查。
- **WorkflowManager UndoSession 去重逻辑**：`sessionTasks` 倒序收集快照导致去重保留中间状态而非原始状态，改为正序（`OrderBy`）收集以确保去重保留最旧快照。
- **FindObjectsOfType 过时 API**：全部 36 处 `Object.FindObjectsOfType<T>()` 替换为 `FindHelper.FindAll<T>()`，在 Unity 6+ 自动使用 `FindObjectsByType` 以消除性能警告。
- **NavMeshSkills TracksWorkflow 误标**：`navmesh_set_area_cost` 影响全局状态无法 SnapshotObject，移除 `TracksWorkflow` 并添加 `areaIndex`/`cost` 参数校验。
- **AnimatorSkills switch 缺 default**：`AnimatorControllerParameterType` switch 添加 `default: break;` 避免潜在的未处理枚举值。
- **代码去重 GetGameObjectPath**：`CleanerSkills` 和 `EditorSkills` 中重复的私有 `GetGameObjectPath` 方法改为调用 `GameObjectFinder.GetPath()`。
- **代码去重 ConvertValue**：`ScriptableObjectSkills` 删除私有 `ConvertValue`，改为调用 `ComponentSkills.ConvertValue`（已提升为 `internal`）。
- **ValidationSkills 优化**：删除未使用的 `rootObjects` 变量；`FindUnusedAssets` 由 O(n²) 嵌套依赖查询改为预构建依赖索引 O(n)。
- **ComponentSkills 缓存无上限**：`_typeCache`/`_memberCache` 超过 500 条时自动清空，防止长时间运行后内存增长。
- **ComponentSkills 兼容性**：`string.Contains(StringComparison)` 需要 .NET Standard 2.1+，改为 `IndexOf` 兼容旧运行时。
- **ComponentSkills 死代码**：删除未使用的 `GetTypeConversionHint` 方法。
- **LightSkills shadow switch 缺 default**：光源阴影类型 switch 添加 default 分支返回警告；批量操作中 `light.range` 添加光源类型检查。
- **ScriptSkills 参数校验**：`script_find_in_file` 添加 `pattern` 必填检查和 `Directory.Exists` 检查。
- **ShaderSkills 健壮性**：`shader_create` 添加 `shaderName` 必填检查；`File.Exists` 添加空字符串保护；提取 `FindShaderByNameOrPath` 消除重复查找逻辑。
- **EventSkills 异常保护**：`event_invoke` 的反射调用添加 try-catch 防止未处理异常。
- **SmartSkills 性能**：`GetTypeByName` 的类型字典从方法内部提升为类级 `static readonly` 字段；`fieldName` 添加空字符串检查。
- **CameraSkills 资源泄漏**：`camera_screenshot` 添加 try/finally 保证 RenderTexture/Texture2D 释放；保存/恢复原始 `targetTexture`；`camera_create` 的 AudioListener 改为可选参数。
- **PackageSkills 语义修正**：安装返回状态改为 `installing` 明确异步语义；`package_search` 描述明确为搜索已安装包。
- **ProjectSkills 结构化返回**：`project_get_packages` 返回解析后的 JSON 对象而非原始文本；`project_add_tag` 添加参数校验。
- **TestSkills 资源管理**：测试完成回调中清理过期条目（1小时）；Domain Reload 时清理 `_api` 和 `_runningTests`；`test_cancel` 返回说明限制的 note。
- **ProfilerSkills 返回值修正**：`GetStatFloat` 返回 `float?` nullable 代替 `-1f` 哨兵值；`profiler_get_stats` 添加 `success = true`。
- **OptimizationSkills 健壮性**：`optimize_textures` 添加 `limit` 参数；LOD 距离解析改用 `TryParse`；`FindDuplicateMaterials` 添加近似比较说明。
- **NavMeshSkills navmesh_clear**：返回中添加不可撤销警告。
- **ScriptableObjectSkills 类型查找**：`FindScriptableObjectType` 改用 `OrdinalIgnoreCase` 大小写不敏感匹配。
- **CleanerSkills 路径格式**：`cleaner_find_large_assets` 将绝对 OS 路径转换为 Unity 相对路径。
- **Python 线程安全**：`_auto_workflow_enabled` 和 `_current_workflow_active` 全局标志添加 `threading.Lock` 保护。

### Server Resilience (Domain Reload 韧性强化)
- **Domain Reload 重启意图丢失修复**：3 次重试全部失败后 `OnBeforeAssemblyReload` 将 `PREF_SERVER_SHOULD_RUN` 覆写为 `false` 导致服务器永久死亡。修复为：仅在 `_isRunning=true` 时写入 true、`Start()` 失败时不清除重启意图，保留跨 Reload 的恢复机会。
- **跨 Reload 连续失败追踪**：新增 `PREF_CONSECUTIVE_FAILURES` 持久化计数器，每轮重试（3 次 + 指数退避）全部失败时 +1，累计达到 5 次后放弃自动重启并输出明确日志提示用户手动启动，防止无限重试循环。成功启动或用户显式停止/退出编辑器时清零。
- **Watchdog Keep-Alive 线程监控**：Watchdog 在 listener 线程健康时额外检测 Keep-Alive 线程存活状态，死亡则自动重启新线程，避免 Unity 失焦后静默失去后台唤醒能力。
- **504 超时响应增强**：新增 `diagnostics` 对象（domainReloadPending / queuedRequests / listenerAlive / keepAliveAlive）、`manualAction` 操作指引、动态 `retryAfterSeconds`（Reload 期间 5s，否则 10s），帮助 AI Agent 自主判断重试策略。
- **503 编译中响应增强**：新增 `diagnostics` 对象（isCompiling / isUpdating / domainReloadPending）、`manualAction` 操作指引、动态 `retryAfterSeconds`（Reload 期间 8s，否则 5s）。
- **`/health` 端点结构化诊断**：新增 `threads`（listenerAlive / keepAliveAlive）、`compilation`（isCompiling / isUpdating / domainReloadPending）、`queueStats`（queued / totalReceived）三组诊断字段，所有旧字段保持向后兼容。
- **SkillRouter 集中注入 `serverAvailability`**：新增 `SerializeSuccessResponse()` 辅助方法，编译进行中时自动向所有 Skill 的成功响应注入 `serverAvailability` 提示（已有该字段的响应不重复注入），无需每个 Skill 单独处理。

### Enhanced
- **新增 `script_dependency_graph` Skill**：给定入口脚本，BFS 双向扩展 N 跳依赖闭包，返回结构化 JSON（脚本列表含 fields/unityCallbacks、边列表、Kahn 拓扑排序的 suggestedReadOrder），帮助 AI 仅加载必要脚本上下文而非全量源码。（REST Skills 447 → 448）
- **`scene_context` 增强**：新增 `includeCodeDeps` 参数，开启后一次调用即可获取场景结构 + 代码级依赖 JSON，解决此前需分别调用 `scene_context` 和 `scene_export_report` 的割裂问题。
- **`RxGetComponent` 正则补强**：扩展为 `(?:Get|Add)Component<T>` 以覆盖 `AddComponent<T>()` 这一明确的依赖声明，减少代码依赖图遗漏边。

## [1.6.1] - 2026-03-11

### Fixed
- **Unity 2021.3 / 2022.3 early patch compatibility**: `PanelSettings.referenceSpritePixelsPerUnit` does not exist in Unity 2021.3 ~ 2022.3 early patches (e.g. 2022.3.17). Changed to reflection-based access to avoid CS1061 compile errors across all Unity versions.
- **Server recovery hardening**: Reduced keep-alive wake interval default to 10s, reduced watchdog interval to 5s, added proactive listener health recovery, and exposed recovery state in `/health` for easier diagnosis.
- **Safer script-domain disruption hints**: Added `serverAvailability` feedback for script edits, test script creation, forced recompilation, define changes, script-related asset reimport/import/move/delete, and package install/remove flows.
- **Path validation coverage**: Added missing file-name/path validation for controller, mixer, and physics material creation; blocked `asset_import` directory misuse from turning into a 500; tightened cleaner preview/usage checks to stay inside `Assets/` or `Packages/`.
- **Package startup stability**: Disabled automatic package auto-install on editor startup by default to avoid unexpected package-triggered recompilation and transient server drops.
- **Workflow/history safety**: Re-validated restored history asset paths and cleaned up script/timeline/test/package helper edge cases discovered during the stability audit.

### Changed
- **Default request timeout**: Changed the configurable server request timeout default from 60 minutes to 15 minutes.
- **Python helper version**: Aligned `unity_skills.py` client version metadata to `1.6.1`.

## [1.6.0] - 2026-03-06

### Added
- **UI Toolkit Module**: New `UIToolkitSkills.cs` with 15 `uitk_*` skills covering UXML/USS file operations, UIDocument scene management, PanelSettings full property create/get/set (27+ properties including Unity 6 World Space/collider support), UXML structure inspection, 6 built-in templates (menu/hud/dialog/settings/inventory/list), and batch file creation.

### Changed
- **Skills count**: Total skills increased from 431 to 446.

## [1.5.5] - 2026-03-05

### Changed
- **API Standardized**: Unified GameObject parameters to `name`, `instanceId`, and `path` across all core modules including `Prefab`, `Editor`, `Event`, `Camera`, `Timeline`, `UI`, `Component`, and `Cinemachine`. Standardized inconsistent names like `gameObjectName`, `objectName`, `directorObjectName`, and `parentName` to prevent AI hallucinations and parameter mismatch errors.
- **Enhanced Routing**: Added support for `instanceId` and `path` selection to multiple previously restricted skills (e.g., `timeline_play`, `component_copy`, `prefab_unpack`), enabling precise targeting in complex scenes.
- **Component Copy Upgrade**: `component_copy` now supports comprehensive routing via `sourceName/sourceInstanceId/sourcePath` and `targetName/targetInstanceId/targetPath`.
- **Doc Alignment**: Updated all `SKILL.md` manifest files and `SETUP_GUIDE.md` to reflect unified parameter naming conventions.

### Fixed
- **CS1737 Compiler Error**: Resolved "Optional parameters must appear after all required parameters" by ensuring all trailing parameters in modified skill signatures have appropriate default values (`null` or `0`). This makes the API more robust and AI-friendly.

## [1.5.4] - 2026-03-03

### Changed

- 版本号升级至 v1.5.4
- 合并来自silwings1986的PR-add Cursor install in AI Config

## [1.5.3] - 2026-03-01

### Security

- **`script_read` 路径遍历漏洞** — 新增 `Validate.SafePath()` 校验，阻止通过 `scriptPath` 参数读取 Assets/Packages 目录外的任意系统文件（如 `C:\Windows\System32\drivers\etc\hosts`）。（`ScriptSkills.cs`）
- **`shader_read` 路径遍历漏洞** — 同上，新增 `Validate.SafePath()` 校验。（`ShaderSkills.cs`）
- **`script_find_in_file` folder 路径遍历漏洞** — `folder` 参数新增 `Validate.SafePath()` 校验，阻止扫描 Assets 目录以外的文件系统路径。（`ScriptSkills.cs`）
- **`scene_screenshot` filename 路径穿越** — `filename` 参数改用 `Path.GetFileName()` 剥离所有路径前缀，确保截图始终保存至 `Assets/Screenshots/` 目录内，传入 `../../test.png` 等路径均安全截断为文件名。（`SceneSkills.cs`）

### Fixed

- **`debug_get_errors` / `debug_get_logs` 始终返回空列表** — 修复 `LogEntry.mode` bitmask 值与 Unity 内部枚举（UnityCsReference）不一致导致所有日志被过滤的根本问题。原 `errorMask = 1|2|16|32|64|128 = 243` 不含 `ScriptingError = 256`（bit 8），导致 `Debug.LogError()` 产生的日志 `256 & 243 = 0` 被全部跳过；`logMask = 4` 不含 `ScriptingLog = 1024`，`Debug.Log()` 同理。修正后：`ErrorModeMask = 1|2|16|64|256|2048|131072`，`LogModeMask = 4|1024`，`WarningModeMask = 128|512|4096`，完整覆盖所有 Unity 日志类型。感谢 **@RubingHan** 发现并报告此问题。（`DebugSkills.cs`）
- **`debug_*` 反射性能与稳定性** — 反射成员改为静态字段缓存（首次调用后复用），新增 `BindingFlags.NonPublic` 确保跨 Unity 版本兼容，提取 `ReadLogEntries()` 共享方法消除代码重复，用 `try/finally` 包裹 `EndGettingEntries()` 修复原资源泄漏风险，反射失败时清空缓存字段允许下次重试。（`DebugSkills.cs`）
- **`PhysicMaterial` Unity 6 向后兼容** — `physics_create_material` / `physics_set_material` 用 `#if UNITY_6000_0_OR_NEWER` 区分 `PhysicsMaterial`（Unity 6+）和 `PhysicMaterial`（Unity 2021.3–2022），确保双版本编译通过。（`PhysicsSkills.cs`）
- **`Assembly` 命名空间歧义编译错误** — 修复同时 `using UnityEditor.Compilation` 和 `using System.Reflection` 导致的 `CS0104` 歧义错误，改为 `System.Reflection.Assembly.GetAssembly()` 完整限定调用。（`DebugSkills.cs`）
- **`shader_delete` 无法通过 Workflow 撤销** — 删除前新增 `WorkflowManager.SnapshotObject()` 调用，使 `workflow_undo_task` 能正确追踪并恢复被删除的 Shader 文件，与 `script_delete` 行为对齐。（`ShaderSkills.cs`）
- **`WorkflowManager.LoadHistory` 崩溃时数据丢失** — 主文件不存在但 `.tmp` 文件存在时（进程崩溃典型场景），自动将 `.tmp` 提升为主文件再读取，防止历史数据因原子写入未完成而丢失。（`WorkflowManager.cs`）
- **`DebugSkills.ReadLogEntries` 空消息 NullReferenceException** — `LogEntry.message` / `file` 字段反射取值改为 `?? ""`，避免 Console 中存在空消息条目时抛出 NRE。`DebugGetStackTrace` 同步修复。（`DebugSkills.cs`）
- **`console_export` 必须先 `console_start_capture`** — 当 capture 缓冲区为空且未在捕获模式时，改为直接从 Unity Console 历史（`LogEntries` 反射）读取并导出，不再返回空文件。（`ConsoleSkills.cs`）
- **`console_get_stats` 始终返回全零** — 同上，非 capture 模式下改为从 Unity Console 实时统计各类型日志数量，结果附带 `source` 字段区分来源（`"capture"` / `"console"`）。（`ConsoleSkills.cs`）

### Improved（Skill 描述优化 — AI 自动触发质量提升）

- **日志读取 Skill 交叉引用** — `debug_get_errors` 和 `debug_get_logs` 描述中注明与 `console_get_logs` 的关系，避免 AI 在全自动模式下选错工具
- **`scene_find_objects` vs `gameobject_find`** — 描述中注明简单查询 vs 高级查询（regex/layer/path）的区别
- **`hierarchy_describe` vs `scene_get_hierarchy`** — 描述中注明文本树 vs JSON 结构的区别
- **`prefab_apply` / `prefab_apply_overrides`** — 互相注明两者等价，消除 AI 的选择困惑
- **`prefab_unpack`** — 明确说明 `completely` 参数含义（仅解包最外层 vs 完全递归解包）
- **`editor_undo` / `editor_redo`** — 注明单步限制，引导多步操作使用 `history_undo(steps=N)` / `history_redo(steps=N)`
- **`editor_play` / `editor_stop`** — 新增 Play 模式数据丢失警告，防止 AI 在 Play 模式下修改场景后直接 Stop 导致数据丢失
- **`workflow_task_end` / `workflow_snapshot_object` / `workflow_snapshot_created`** — 明确前置条件（需先调用 `workflow_task_start`），防止 AI 跳过必要初始化步骤
- **`smart_scene_layout` / `smart_align_to_ground` / `smart_distribute` / `smart_replace_objects`** — 注明需要先在 Hierarchy 中选中对象，防止 AI 直接调用返回"无选中对象"错误
- **`test_run`** — 明确异步行为（立即返回 jobId），引导 AI 随后调用 `test_get_result(jobId)` 轮询结果
- **`test_get_result`** — 注明需要 `test_run` 返回的 `jobId`，防止 AI 无参数调用
- **`scene_context`** — 补充适用场景说明（编码或复杂场景作业前的初始上下文收集）
- **`scene_screenshot`** — 描述从"scene view"更正为"game view"，与实际行为一致

### Notes

- `console_get_logs` 返回空列表属于**设计行为**，不是 bug：该 skill 基于 `Application.logMessageReceived` 事件回调，只捕获订阅后产生的新日志。**使用前必须先调用 `console_start_capture`**，之后触发的日志才会被记录。如需读取 Console 中已有的历史日志，请使用 `debug_get_errors`、`debug_get_logs` 或 `console_get_stats`（均可直接读取 Unity Editor LogEntries，无需预先启动）。

## [1.5.2] - 2026-02-25

### Fixed
- **JetBrains.Annotations 反射崩溃** — 修复在含有 JetBrains Rider 注解（`[NotNull]`/`[CanBeNull]` 等）的项目中，插件扫描技能方法时 CLR 尝试加载 `JetBrains.Annotations.dll`（Version=4242.42.42.42）失败导致的 `FileNotFoundException`。现在遇到程序集加载异常时会跳过该方法继续扫描，不影响正常功能。（`SkillRouter.cs`、`UnitySkillsWindow.cs`）
- **反射 GetCustomAttribute 崩溃风险** — 修复与 JetBrains 崩溃同类的三处反射调用：`AllowMultiple()`（`ComponentSkills.cs:551`）、`GetRequiredByComponents()` 内的 LINQ 查询（`ComponentSkills.cs:556`）、`GetCustomAttribute<ObsoleteAttribute>()`（`CinemachineSkills.cs:166`）。CLR 解析特性时若触发程序集加载失败，现在均以 try-catch 安全降级，不影响正常功能。
- **路径遍历安全漏洞** — 两处文件操作路径参数缺少校验：`scriptableobject_import_json` 的 `jsonFilePath` 参数新增 `Validate.SafePath()` 校验，阻止 `../../etc/passwd` 等路径逃逸（`ScriptableObjectSkills.cs:209`）；`script_create` 的 `scriptName` 参数新增路径分隔符检查（`/`、`\`、`..`），防止经由 `Path.Combine` 逃逸出 Assets 目录（`ScriptSkills.cs:24`）。

### Security
- **`scriptableobject_import_json` jsonFilePath 路径遍历** — `jsonFilePath` 参数现在通过 `Validate.SafePath()` 限制在 Assets/Packages 目录内（`ScriptableObjectSkills.cs`）
- **`script_create` scriptName 目录逃逸** — `scriptName` 含路径分隔符时立即返回错误，不再经由 `Path.Combine` 拼接到文件系统（`ScriptSkills.cs`）

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
- **`script_create` 参数名兼容** — 同时支持 `scriptName` 和 `name` 参数，当两者都为空时返回明确错误而非生成 `.cs` 空文件名。`script_create_batch` 同步支持
- **`light_add_probe_group` 增强** — 新增 `gridX/gridY/gridZ`（每轴探针数）和 `spacingX/spacingY/spacingZ`（间距）参数，支持一步创建网格布局的光照探针组；已有组件时支持重新设置探针位置

#### Unity 6 兼容性修复（6 项）
- **`console_set_collapse` / `console_set_clear_on_play` 修复** — Unity 6 移除了 `ConsoleWindow.s_ConsoleFlags` 静态字段，改为多级回退策略：`SetConsoleFlag` 方法 → `s_ConsoleFlags` 字段 → `LogEntries` API → `EditorPrefs` 兜底（`ConsoleSkills.cs`）
- **`cinemachine_set_active` IComparable 修复** — CM3 的 `Priority` 属性不支持 LINQ `Max()` 泛型比较，改用 `foreach` 手动迭代并显式 `(int)` 转换（`CinemachineSkills.cs:538`）
- **`audio_create_mixer` 创建失败修复** — Unity 6 中 `ScriptableObject.CreateInstance(AudioMixerController)` 触发 `ExtensionOfNativeClass` 异常导致返回失败，重构为优先使用 `CreateMixerControllerAtPath` 内部工厂方法 + `ScriptableObject.CreateInstance` 回退。注："Mixer is not initialized" 日志为 Unity 6 内部已知问题，Unity 自身菜单创建 AudioMixer 也会产生，不影响功能（`AudioSkills.cs:280`）
- **`event_add_listener` 目标组件查找修复** — `GetComponent("GameObject")` 返回 null（GameObject 不是 Component），新增特殊处理：当 `targetComponentName` 为 `"GameObject"` 时直接使用 GO 作为目标 Object；同时增加 `set_XXX` 属性 setter 方法查找支持（`EventSkills.cs:90`）
- **`smart_reference_bind` 字段查找修复** — 增加 Unity 序列化命名约定回退查找（`m_XXX`、`_xxx`）和 `PropertyInfo` 回退，修复 Unity 6 中部分组件字段名不匹配的问题（`SmartSkills.cs:159`）
- **Splines 版本适配** — 新增 `SplinesVersionUnity6 = "2.8.3"` 常量和 `GetRecommendedSplinesVersion()` 方法，Unity 6 自动使用 2.8.3、Unity 2022 使用 2.8.0；CM3 安装依赖同步更新（`PackageManagerHelper.cs`）
- **`component_set_enabled` Renderer/Collider 支持** — 原代码仅检查 `Behaviour` 类型，导致 `MeshRenderer`（继承 `Renderer`）和 `Collider` 等组件无法启用/禁用，新增 `Renderer` 和 `Collider` 类型分支（`ComponentSkills.cs:911`）
- **`optimize_find_duplicate_materials` _Color 属性异常修复** — `mat.color` 直接访问 `_Color` 属性，TextMeshPro 等 shader 无此属性时抛出异常，改为 `HasProperty` 检查并回退到 `_BaseColor`（`OptimizationSkills.cs:237`）

### Added
- **`package_install_splines` 技能** — 新增 Splines 包版本化安装技能，自动检测 Unity 版本选择正确的 Splines 版本（Unity 6: 2.8.3, Unity 2022: 2.8.0），支持升级已安装的旧版本（`PackageSkills.cs`）

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
