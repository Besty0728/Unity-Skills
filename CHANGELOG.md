# Changelog

All notable changes to **UnitySkills** will be documented in this file.

## [1.5.0] - 2026-02-12

### Added
- **BatchExecutor 泛型框架**: 新增 `BatchExecutor.Execute<T>()` 通用批处理框架，支持 JSON 反序列化、逐项执行、错误隔离、setup/teardown 钩子（`BatchExecutor.cs`）
- **SkillsLogger 统一日志**: 新增 `SkillsLogger` 类，支持 Off/Error/Warning/Info/Agent/Verbose 日志级别，替代散落的 `Debug.Log` 调用（`SkillsLogger.cs`）
- **参数校验扩展**: `Validate` 类新增 `InRange()`、`RequiredJsonArray()`、`SafePath()` 方法，形成完整的参数校验工具链（`GameObjectFinder.cs`）
- **单元测试框架**: 新增 `Tests/Editor/` 目录，包含 3 个测试套件共 72 个测试用例：
  - `BatchExecutorTests.cs` — 21 个测试覆盖批处理成功/失败/setup/teardown 生命周期
  - `RegistryServiceTests.cs` — 17 个测试覆盖哈希确定性和边界条件
  - `ValidateTests.cs` — 34 个测试覆盖 Required/InRange/SafePath 校验

### Security
- **SHA256 哈希**: RegistryService 实例 ID 从 MD5 迁移到 SHA256（`RegistryService.cs`）
- **TOCTOU 文件锁**: 注册表文件读写添加文件锁防止竞态条件（`RegistryService.cs`）
- **POST Body 大小限制**: HTTP 服务器拒绝超过 10MB 的请求体，返回 413 状态码（`SkillsHttpServer.cs`）
- **ManualResetEventSlim 泄漏修复**: try/finally 模式确保信号量在 ThreadPool 入队失败时仍被释放（`SkillsHttpServer.cs`）
- **路径遍历防护**: 15 个文件操作方法补齐 `Validate.SafePath()` 校验，涵盖 Script/Shader/Material/ScriptableObject/Prefab/Scene/Asset/Cleaner/Validation 共 10 个 Skill 文件

### Changed

#### 架构重构
- **BatchExecutor 接入**: 25 个 batch 方法迁移到 `BatchExecutor.Execute<T>()` 框架，消除约 1500 行重复的反序列化/错误收集/结果汇总代码，涉及 GameObjectSkills/ComponentSkills/MaterialSkills/LightSkills/PrefabSkills/UISkills/AudioSkills/ModelSkills/TextureSkills/AssetSkills/ScriptSkills 共 11 个文件
- **WorkflowManager Undo/Redo 提取**: 重构撤销/重做逻辑为独立方法，提升可维护性（`WorkflowManager.cs`）
- **Agent 表驱动注册**: SkillRouter 的 Agent 配置改为表驱动模式，新增 Agent 类型无需修改分发逻辑（`SkillRouter.cs`）
- **SkillRouter 消除双重序列化**: 替换 `JObject.FromObject(result)` 为反射检测错误字段，避免不必要的 JSON 中间转换（`SkillRouter.cs`）

#### 代码质量
- **GameObjectFinder 迁移**: 29+ 处原始 `GameObject.Find` 调用迁移到 `GameObjectFinder.FindOrError`，提供错误提示含相似名称建议，涉及 PrefabSkills/EventSkills/TimelineSkills/CameraSkills/EditorSkills/UISkills/WorkflowSkills/ComponentSkills/SampleSkills 共 9 个文件
- **统一返回值格式**: 10 个方法补齐 `success = true/false` 字段（`SampleSkills.cs`、`OptimizationSkills.cs`、`ValidationSkills.cs`）
- **区域无关数值解析**: ComponentSkills 和 ScriptableObjectSkills 中 7 处 `float.Parse`/`double.Parse` 添加 `CultureInfo.InvariantCulture`，修复非英文区域的小数点解析问题
- **CinemachineSkills 审查修复**: 15 处代码问题修复，包括空引用保护、异常处理、API 兼容性（`CinemachineSkills.cs`）
- **静默异常修复**: 多处空 catch 块添加日志记录，便于调试定位问题
- **文件重命名**: `NextGenSkills.cs` → `PerceptionSkills.cs`，文件名与类名保持一致
- **SampleSkills 标注**: 明确标记为便捷别名，4 处 `GameObject.Find` 迁移到 `GameObjectFinder.FindOrError`

#### 基础设施
- **PhysicsSetGravity Undo 支持**: 通过 `DynamicsManager.asset` 注册 Undo，重力修改可撤销（`PhysicsSkills.cs`）
- **双重检查锁**: 单例和懒初始化改用双重检查锁模式（`SkillsHttpServer.cs`）
- **超时常量化**: 散落的超时魔数提取为命名常量（`SkillsHttpServer.cs`）
- **版本集中化**: 版本号集中管理，避免多处硬编码不一致
- **Python 客户端 retry 重写**: `unity_skills.py` 重写重试逻辑，支持 Domain Reload 场景的自动恢复

### Performance
- **GameObjectFinder 帧级缓存**: 同一帧内重复查找同名 GameObject 直接命中缓存，避免冗余遍历（`GameObjectFinder.cs`）
- **反射成员缓存**: ComponentSkills 新增 `_memberCache` 字典和 `FindMember()` 辅助方法，属性/字段查找结果被缓存，批量操作性能显著提升（`ComponentSkills.cs`）

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
