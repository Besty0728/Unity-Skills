# Changelog

All notable changes to **UnitySkills** will be documented in this file.

## [1.3.0] - 2026-01-27

### 🐛 Bug Fixes / 问题修复

- **Windows Console Encoding / Windows 控制台编码**:
    - Fixed Chinese character encoding issue in Python client output. / 修复 Python 客户端输出中文字符乱码问题。
    - Moved encoding fix to module top level before any imports. / 将编码修复移至模块顶部，在任何导入之前执行。
    - Changed from `io.TextIOWrapper` to `codecs.getwriter` for more reliable UTF-8 output. / 使用 `codecs.getwriter` 替代 `io.TextIOWrapper` 以获得更可靠的 UTF-8 输出。
- **Chinese Character Support / 中文字符支持**:
    - Fixed JSON serialization escaping Unicode characters, causing Chinese names to display as garbled text in AI terminals. / 修复 JSON 序列化转义 Unicode 字符导致中文名称在 AI 终端显示乱码。
    - Added `charset=utf-8` to HTTP Content-Type header. / HTTP 响应头添加 charset=utf-8 声明。
    - Python client now forces UTF-8 decoding. / Python 客户端强制 UTF-8 解码。

### 🌟 New Skills / 新增技能

- **Cinemachine Support**:
    - Full control over Virtual Cameras: Create, inspect, and modify properties via reflection. / 支持 Cinemachine 虚拟相机创建、属性深度修改。
    - `cinemachine_set_vcam_property` allows adjusting almost any value. / 支持任意数值调整。
- **Timeline Support**:
    - Create Timelines, add Audio/Animation tracks and bind objects. / 支持创建 Timeline 及添加音轨/动画轨。
- **Console & Debugging**:
    - Capture and retrieve Unity console logs for self-correction. / 支持捕获 Unity 控制台日志以进行自我纠错。
    - `console_get_logs`, `console_clear` allow AI to see errors. / AI 可读取报错信息。
- **Profiler & Performance**:
    - Get real-time stats including FPS, memory, draw calls. / 获取实时 FPS、内存、DrawCall 等性能数据。
- **Physics Perception**:
    - Spatial awareness via Raycast and OverlapSphere. / 通过射线和重叠球进行空间感知。
    - `physics_check_overlap` to find nearby objects. / 查找附近物体。
- **Event System**:
    - Inspect and modify UnityEvents (e.g. Button.onClick) at runtime/editor. / 运行时/编辑器内省和修改 UnityEvent。
    - Support for adding listeners with int, float, string, bool, void arguments. / 支持添加各种类型的事件监听。
- **NavMesh**:
    - Bake NavMesh, clear data, and calculate paths. / 烘焙导航网格、清除数据、计算路径。

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
