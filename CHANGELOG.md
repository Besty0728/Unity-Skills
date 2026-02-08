# Changelog

All notable changes to **UnitySkills** will be documented in this file.

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
