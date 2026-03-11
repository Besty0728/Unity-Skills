# UnitySkills 完整使用指南 / Complete Setup Guide

REST API 直接控制 Unity Editor，让 AI 生成的脚本完成场景操作！
Control Unity Editor directly via REST API — let AI-generated scripts handle scene operations!

---

## 🔧 前置要求 / Prerequisites

- **Unity 版本 / Version**: 2022.3 或更高（推荐 Unity 6）/ 2022.3+ (Unity 6 recommended)
- **依赖包 / Dependencies**:
    - `com.unity.cinemachine`: 3.1.3+ (核心依赖 / Core dependency)
    - `com.unity.splines`: 2.8.0+ (v1.4.1 新增硬依赖 / Hard dependency since v1.4.1)
- **注意 / Note**: 安装插件时会自动拉取上述依赖，无需手动安装。Dependencies are auto-installed with the plugin.
- **维护说明 / Maintenance Note**: 当前官方适配与测试重点为 Unity 2022.3+ 与 Unity 6。仓库中可能仍保留部分旧版本兼容逻辑，但不再作为后续适配主目标。Official adaptation and testing now focus on Unity 2022.3+ and Unity 6. Some legacy compatibility logic may remain, but it is no longer the primary forward-compatibility target.

---

## 一、安装 Unity 插件 / Install Unity Plugin

### 方式 A：Git URL（推荐 / Recommended）

通过 Unity Package Manager 直接添加 Git URL：
Add via Unity Package Manager with Git URL:

```
Unity 菜单 / Menu → Window → Package Manager → + → Add package from git URL
```

**稳定版安装 / Stable (main)**:
```
https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity
```

**开发测试版安装 / Beta (beta)**:
```
https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity#beta
```

**指定版本安装 / Specific Version** (如 / e.g. v1.6.2):
```
https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity#v1.6.2
```

> 📦 所有版本均可在 [Releases](https://github.com/Besty0728/Unity-Skills/releases) 页面下载。
> All versions available on the [Releases](https://github.com/Besty0728/Unity-Skills/releases) page.

### 方式 B：本地安装 / Local Install

将 `SkillsForUnity` 文件夹复制到 Unity 项目的 `Packages/` 目录。
Copy the `SkillsForUnity` folder to your Unity project's `Packages/` directory.

---

## 二、启动服务器 / Start Server

1. Unity 菜单 / Menu：**Window → UnitySkills → Start REST Server**
2. Console 显示 / Shows：`[UnitySkills] REST Server started at http://localhost:8090/`

---

## 三、验证 / Verify

### 浏览器 / Browser
打开 / Open http://localhost:8090/skills 查看所有可用 Skills / to view all available Skills

### 命令行 / Command Line
```bash
curl http://localhost:8090/skills
```

---

## 四、AI 工具配置 / AI Tool Configuration

UnitySkills 支持多种 AI 工具，可以通过 Unity 编辑器一键安装。
UnitySkills supports multiple AI tools with one-click installation from the Unity Editor.

### 打开配置窗口 / Open Config Window
Unity 菜单 / Menu：**Window → UnitySkills**，切换到 / switch to **AI Config** 标签页 / tab

### 支持的 AI 工具 / Supported AI Tools

| AI 工具 / Tool | 项目安装路径 / Project Path | 全局安装路径 / Global Path |
|---------|------------|------------|
| Claude Code | `.claude/skills/unity-skills/` | `~/.claude/skills/unity-skills/` |
| Antigravity | `.agent/skills/unity-skills/` | `~/.gemini/antigravity/skills/unity-skills/` |
| Gemini CLI | `.gemini/skills/unity-skills/` | `~/.gemini/skills/unity-skills/` |
| Codex | `.codex/skills/unity-skills/` | `~/.codex/skills/unity-skills/` |
| Cursor | `.cursor/skills/unity-skills/` | `~/.cursor/skills/unity-skills/` |

### 一键安装 / One-Click Install
1. 在 UnitySkills 窗口的 AI Config 标签页 / In the AI Config tab
2. 选择要安装的 AI 工具 / Select the AI tool to install
3. 点击 "安装到项目" 或 "全局安装" / Click "Install to Project" or "Global Install"
4. 安装成功后会显示 "✅ 已安装" / Shows "✅ Installed" on success

> 安装功能生成的文件说明（生成于目标目录）：
> Generated files (created in target directory):
> - `SKILL.md`
> - `scripts/unity_skills.py`
> - `scripts/agent_config.json`（含 Agent 标识 / with Agent identifier）
> - Antigravity 额外生成 / additionally generates `workflows/unity-skills.md`

### Gemini CLI 特别说明 / Gemini CLI Special Notes
Gemini CLI 的 Skills 功能是实验性的，需要手动启用：
Gemini CLI Skills feature is experimental and requires manual activation:
```bash
gemini
# 进入交互模式后输入 / In interactive mode, type:
/settings
# 搜索 "Skills" 并启用 / Search "Skills" and enable experimental.skills
```

### OpenAI Codex 特别说明 / OpenAI Codex Special Notes

**推荐使用全局安装** / **Global install recommended**：Codex 不会自动扫描项目级 `.codex/skills/` 目录，需要在 `AGENTS.md` 中明确声明技能识别。
Codex won't auto-scan project-level `.codex/skills/`. You need to declare skills in `AGENTS.md`.

全局安装路径 / Global install path（`~/.codex/skills/`）会被自动识别，安装后重启 Codex 即可：
The global path is auto-recognized. Restart Codex after installation:
```bash
codex
```

如果必须使用项目级安装，需要手动在项目根目录的 `AGENTS.md` 中添加：
For project-level install, manually add to `AGENTS.md` in project root:
```markdown
## Available Skills
- unity-skills: Unity Editor automation via REST API
```

---

## 五、调用 Skills / Calling Skills

### 基本格式 / Basic Format
```bash
POST http://localhost:8090/skill/{skill_name}
Content-Type: application/json

{参数JSON / Parameter JSON}
```

### 示例 / Examples

#### 创建物体 / Create GameObject
```bash
curl -X POST http://localhost:8090/skill/gameobject_create \
  -H "Content-Type: application/json" \
  -d '{"name":"MyCube","primitiveType":"Cube","x":0,"y":1,"z":0}'
```

#### 设置颜色 / Set Color
```bash
curl -X POST http://localhost:8090/skill/material_set_color \
  -d '{"name":"MyCube","r":1,"g":0,"b":0}'
```

#### 保存场景 / Save Scene
```bash
curl -X POST http://localhost:8090/skill/scene_save \
  -d '{"scenePath":"Assets/Scenes/MyScene.unity"}'
```

---

## 六、Python 客户端 / Python Client

```python
import requests

UNITY_URL = "http://localhost:8090"

def call_skill(name, **kwargs):
    return requests.post(f"{UNITY_URL}/skill/{name}", json=kwargs).json()

# 使用 / Usage
call_skill("gameobject_create", name="Cube", primitiveType="Cube", x=0, y=1, z=0)
call_skill("material_set_color", name="Cube", r=1, g=0, b=0)
call_skill("editor_play")
```

---

## 七、完整 Skills 列表 / Complete Skills Reference

> ⚠️ **提示 / Tip**：大部分模块支持 `*_batch` 批量操作，操作多个物体时应优先使用批量 Skills。
> Most modules support `*_batch` batch operations. Use batch Skills when working with multiple objects.

> 📊 **总计 / Total**: **446 Skills** across **37 modules**

---

### Scene (场景) — 18 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| scene_create | 创建新场景 / Create new scene | scenePath |
| scene_load | 加载场景 / Load scene | scenePath, additive |
| scene_save | 保存场景 / Save scene | scenePath |
| scene_get_info | 获取场景信息 / Get scene info | - |
| scene_get_hierarchy | 获取层级树 / Get hierarchy tree | maxDepth |
| scene_screenshot | 截图 / Screenshot (Game View) | filename, width, height |
| scene_find_objects | 查找场景物体 / Find scene objects | name, tag, component, limit |
| scene_context | 获取完整上下文 / Get full scene context | - |
| scene_export_report | 导出场景报告 / Export scene report (Markdown) | savePath |
| scene_unload | 卸载场景 / Unload scene | scenePath |
| scene_set_active | 设置活动场景 / Set active scene | scenePath |
| scene_get_dependencies | 获取场景依赖 / Get scene dependencies | scenePath |
| scene_list_loaded | 列出已加载场景 / List loaded scenes | - |
| scene_create_from_template | 从模板创建 / Create from template | templateName, scenePath |
| scene_get_build_settings | 获取构建设置 / Get build settings | - |
| scene_add_to_build | 添加到构建 / Add to build | scenePath |
| scene_remove_from_build | 从构建移除 / Remove from build | scenePath |
| scene_get_dirty | 获取未保存状态 / Check if scene is dirty | - |

### GameObject (物体) — 18 skills (含批量 / with batch)

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| gameobject_create | 创建物体 / Create object | name, primitiveType, x, y, z |
| gameobject_delete | 删除物体 / Delete object | name, instanceId |
| gameobject_find | 查找物体 / Find object | name, tag, component, limit |
| gameobject_set_transform | 设置变换 / Set transform | name, posX, posY, posZ, rotX, rotY, rotZ, scaleX, scaleY, scaleZ |
| gameobject_duplicate | 复制物体 / Duplicate object | name, instanceId |
| gameobject_duplicate_batch | **批量复制 / Batch duplicate** | items (JSON array) |
| gameobject_set_parent | 设置父级 / Set parent | childName, parentName |
| gameobject_get_transform | 获取变换 / Get transform | name, instanceId |
| gameobject_set_active | 设置激活 / Set active state | name, instanceId, active |
| gameobject_set_tag | 设置标签 / Set tag | name, tag |
| gameobject_set_layer | 设置层级 / Set layer | name, layer |
| gameobject_rename | 重命名 / Rename | name, newName |
| gameobject_get_children | 获取子物体 / Get children | name, instanceId |
| gameobject_create_empty | 创建空物体 / Create empty | name, x, y, z |
| gameobject_set_static | 设置静态 / Set static | name, isStatic |
| gameobject_get_info | 获取信息 / Get info | name, instanceId |
| gameobject_create_batch | **批量创建 / Batch create** | items (JSON array) |
| gameobject_delete_batch | **批量删除 / Batch delete** | items (JSON array) |

### Component (组件) — 10 skills (含批量 / with batch)

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| component_add | 添加组件 / Add component | name, componentType |
| component_add_batch | **批量添加 / Batch add** | items (JSON array) |
| component_remove | 移除组件 / Remove component | name, componentType |
| component_remove_batch | **批量移除 / Batch remove** | items (JSON array) |
| component_list | 列出组件 / List components | name |
| component_set_property | 设置属性 / Set property | name, componentType, propertyName, value |
| component_set_property_batch | **批量设置属性 / Batch set property** | items (JSON array) |
| component_get_properties | 获取属性 / Get properties | name, componentType |
| component_copy | 复制组件 / Copy component | sourceName, targetName, componentType |
| component_set_enabled | 启用/禁用 / Enable/Disable | name, componentType, enabled |

### Material (材质) — 21 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| material_create | 创建材质 / Create material | name, shaderName, savePath |
| material_set_color | 设置颜色 / Set color | name, r, g, b, a, propertyName, intensity |
| material_set_emission | 设置发光 / Set emission | name, r, g, b, intensity |
| material_set_texture | 设置贴图 / Set texture | name, texturePath, propertyName |
| material_assign | 分配材质 / Assign material | name, materialPath |
| material_set_float | 设置浮点值 / Set float | name, propertyName, value |
| material_get_properties | 获取属性 / Get properties | name |
| material_set_keyword | 设置关键字 / Set keyword | name, keyword, enabled |
| material_set_render_queue | 设置渲染队列 / Set render queue | name, renderQueue |
| material_set_shader | 设置着色器 / Set shader | name, shaderName |
| material_duplicate | 复制材质 / Duplicate material | name, newName |
| material_set_vector | 设置向量 / Set vector | name, propertyName, x, y, z, w |
| material_get_info | 获取信息 / Get info | name |
| material_set_int | 设置整数 / Set integer | name, propertyName, value |
| material_list_keywords | 列出关键字 / List keywords | name |
| material_set_color_batch | **批量设置颜色 / Batch set color** | items (JSON array) |
| material_assign_batch | **批量分配 / Batch assign** | items (JSON array) |
| material_set_transparency | 设置透明度 / Set transparency | name, mode |
| material_set_metallic_smoothness | 设置金属度/光滑度 / Set metallic/smoothness | name, metallic, smoothness |
| material_set_normal_map | 设置法线贴图 / Set normal map | name, texturePath, scale |
| material_get_shader_properties | 获取着色器属性 / Get shader properties | name |

### Light (灯光) — 10 skills (含批量 / with batch)

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| light_create | 创建灯光 / Create light | name, lightType, x, y, z, r, g, b, intensity |
| light_set_properties | 设置属性 / Set properties | name, instanceId, r, g, b, intensity, range |
| light_set_properties_batch | **批量设置属性 / Batch set** | items (JSON array) |
| light_set_enabled | 开关灯光 / Toggle light | name, instanceId, enabled |
| light_set_enabled_batch | **批量开关 / Batch toggle** | items (JSON array) |
| light_get_info | 获取灯光信息 / Get light info | name, instanceId |
| light_find_all | 查找所有灯光 / Find all lights | lightType, limit |
| light_add_probe_group | 添加光照探针组 / Add probe group | name, gridX, gridY, gridZ |
| light_add_reflection_probe | 添加反射探针 / Add reflection probe | name, x, y, z |
| light_get_lightmap_settings | 获取光照贴图设置 / Get lightmap settings | - |

### Editor (编辑器) — 12 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| editor_play | 进入播放模式 / Enter play mode | - |
| editor_stop | 停止播放模式 / Stop play mode | - |
| editor_pause | 暂停/继续 / Pause/Resume | - |
| editor_select | 选中物体 / Select object | name, instanceId |
| editor_get_selection | 获取选中 / Get selection | - |
| **editor_get_context** | **获取完整上下文 / Get full context** | includeComponents, includeChildren |
| editor_undo | 撤销 / Undo | - |
| editor_redo | 重做 / Redo | - |
| editor_get_state | 获取编辑器状态 / Get editor state | - |
| editor_execute_menu | 执行菜单项 / Execute menu item | menuPath |
| editor_get_tags | 获取所有标签 / Get all tags | - |
| editor_get_layers | 获取所有图层 / Get all layers | - |

### Importer (导入设置) — 11 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| texture_get_settings | 获取纹理设置 / Get texture settings | assetPath |
| texture_set_settings | 设置纹理导入 / Set texture import | assetPath, textureType, maxSize, filterMode... |
| texture_set_settings_batch | **批量设置纹理 / Batch set texture** | items (JSON array) |
| audio_get_settings | 获取音频设置 / Get audio settings | assetPath |
| audio_set_settings | 设置音频导入 / Set audio import | assetPath, loadType, compressionFormat, quality... |
| audio_set_settings_batch | **批量设置音频 / Batch set audio** | items (JSON array) |
| model_get_settings | 获取模型设置 / Get model settings | assetPath |
| model_set_settings | 设置模型导入 / Set model import | assetPath, meshCompression, animationType... |
| model_set_settings_batch | **批量设置模型 / Batch set model** | items (JSON array) |
| asset_set_labels | 设置标签 / Set asset labels | assetPath, labels |
| asset_reimport | 重新导入 / Reimport asset | assetPath |

### Asset (资产) — 15 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| asset_import | 导入资产 / Import asset | sourcePath, destinationPath |
| asset_delete | 删除资产 / Delete asset | assetPath |
| asset_move | 移动/重命名 / Move/Rename | sourcePath, destinationPath |
| asset_duplicate | 复制资产 / Duplicate asset | assetPath |
| asset_find | 搜索资产 / Search assets | searchFilter, limit |
| asset_create_folder | 创建文件夹 / Create folder | folderPath |
| asset_refresh | 刷新资产库 / Refresh asset database | - |
| asset_get_info | 获取资产信息 / Get asset info | assetPath |
| asset_get_dependencies | 获取依赖 / Get dependencies | assetPath |
| asset_find_references | 查找引用 / Find references | assetPath |
| asset_get_labels | 获取标签 / Get labels | assetPath |
| asset_set_labels | 设置标签 / Set labels | assetPath, labels |
| asset_open | 打开资产 / Open asset | assetPath |
| asset_create | 创建资产 / Create asset | assetPath, type |
| asset_get_path | 获取路径 / Get path | name |

### Prefab (预制体) — 10 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| prefab_create | 创建预制体 / Create prefab | name, savePath |
| prefab_instantiate | 实例化预制体 / Instantiate prefab | prefabPath, x, y, z, name |
| prefab_instantiate_batch | **批量实例化 / Batch instantiate** | items (JSON array) |
| prefab_apply | 应用修改 / Apply changes | name |
| prefab_unpack | 解包预制体 / Unpack prefab | name, completely |
| prefab_create_variant | 创建预制体变体 / Create variant | name, savePath |
| prefab_find_instances | 查找实例 / Find instances | prefabPath |
| prefab_get_overrides | 获取覆盖 / Get overrides | name |
| prefab_revert_overrides | 还原覆盖 / Revert overrides | name |
| prefab_apply_overrides | 应用覆盖 / Apply overrides | name |

### Console (控制台) — 10 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| console_start_capture | 开始捕获日志 / Start log capture | - |
| console_stop_capture | 停止捕获 / Stop capture | - |
| console_get_logs | 获取日志 / Get logs | filter, limit |
| console_clear | 清空控制台 / Clear console | - |
| console_log | 输出日志 / Output log | message, type |
| console_export | 导出日志 / Export logs | filePath |
| console_get_stats | 获取统计 / Get stats | - |
| console_set_collapse | 设置折叠 / Set collapse | enabled |
| console_set_clear_on_play | 播放时清除 / Clear on play | enabled |
| console_set_pause_on_error | 错误时暂停 / Pause on error | enabled |

### Camera (相机) — 11 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| camera_create | 创建相机 / Create camera | name, x, y, z |
| camera_set_properties | 设置属性 / Set properties | name, fov, nearClip, farClip... |
| camera_get_properties | 获取属性 / Get properties | name |
| camera_look_at | 看向目标 / Look at target | name, targetName |
| camera_set_main | 设为主相机 / Set as main | name |
| camera_screenshot | 相机截图 / Camera screenshot | name, filename, width, height |
| camera_list | 列出相机 / List cameras | - |
| camera_scene_view_align | 对齐场景视图 / Align to scene view | name |
| camera_set_clear_flags | 设置清除标志 / Set clear flags | name, clearFlags |
| camera_set_culling_mask | 设置剔除遮罩 / Set culling mask | name, layers |
| camera_set_viewport | 设置视口 / Set viewport | name, x, y, w, h |

### Cinemachine (虚拟相机) — 23 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| cinemachine_create | 创建虚拟相机 / Create virtual camera | name, x, y, z |
| cinemachine_set_follow | 设置跟随目标 / Set follow target | name, targetName |
| cinemachine_set_look_at | 设置看向目标 / Set look-at target | name, targetName |
| cinemachine_set_body | 设置 Body 属性 / Set body | name, bodyType, properties |
| cinemachine_set_aim | 设置 Aim 属性 / Set aim | name, aimType, properties |
| cinemachine_set_noise | 设置噪波 / Set noise | name, noiseProfile |
| cinemachine_set_priority | 设置优先级 / Set priority | name, priority |
| cinemachine_set_active | 设置激活 / Activate camera | name |
| cinemachine_get_info | 获取信息 / Get info | name |
| cinemachine_create_blend | 创建混合 / Create blend | fromCamera, toCamera, blendTime |
| cinemachine_list | 列出虚拟相机 / List virtual cameras | - |
| cinemachine_create_mixing | 创建混合相机 / Create mixing camera | name |
| cinemachine_create_clearshot | 创建 ClearShot / Create ClearShot | name |
| cinemachine_create_target_group | 创建目标组 / Create target group | name |
| cinemachine_add_target | 添加目标 / Add target | groupName, targetName, weight |
| cinemachine_create_dolly_track | 创建轨道 / Create dolly track (Spline) | name, points |
| cinemachine_set_dolly | 设置轨道相机 / Set dolly camera | name, trackName |
| cinemachine_set_lens | 设置镜头 / Set lens | name, fov, nearClip, farClip |
| cinemachine_set_damping | 设置阻尼 / Set damping | name, x, y, z |
| cinemachine_set_dead_zone | 设置死区 / Set dead zone | name, width, height |
| cinemachine_set_screen_position | 设置屏幕位置 / Set screen position | name, x, y |
| cinemachine_set_offset | 设置偏移 / Set follow offset | name, x, y, z |
| cinemachine_remove_target | 移除目标 / Remove target | groupName, targetName |

### Timeline (时间轴) — 12 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| timeline_create | 创建时间轴 / Create timeline | name, savePath |
| timeline_add_track | 添加轨道 / Add track | name, trackType |
| timeline_remove_track | 移除轨道 / Remove track | name, trackIndex |
| timeline_add_clip | 添加片段 / Add clip | name, trackIndex, clipName, start, duration |
| timeline_remove_clip | 移除片段 / Remove clip | name, trackIndex, clipIndex |
| timeline_set_duration | 设置时长 / Set duration | name, duration |
| timeline_play | 播放时间轴 / Play timeline | name |
| timeline_stop | 停止时间轴 / Stop timeline | name |
| timeline_set_time | 设置时间 / Set time | name, time |
| timeline_get_info | 获取信息 / Get info | name |
| timeline_set_binding | 设置绑定 / Set binding | name, trackIndex, targetName |
| timeline_get_tracks | 获取轨道列表 / Get tracks | name |

### Physics (物理) — 12 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| physics_raycast | 射线检测 / Raycast | originX, originY, originZ, dirX, dirY, dirZ, distance |
| physics_sphere_cast | 球形检测 / Sphere cast | origin, direction, radius, distance |
| physics_box_cast | 盒形检测 / Box cast | origin, direction, halfExtents, distance |
| physics_overlap_sphere | 球形重叠检测 / Overlap sphere | x, y, z, radius |
| physics_create_material | 创建物理材质 / Create physics material | name, dynamicFriction, staticFriction, bounciness |
| physics_set_material | 设置物理材质 / Set physics material | name, materialName |
| physics_get_collision_matrix | 获取碰撞矩阵 / Get collision matrix | - |
| physics_set_collision_matrix | 设置碰撞矩阵 / Set collision matrix | layer1, layer2, collide |
| physics_set_gravity | 设置重力 / Set gravity | x, y, z |
| physics_get_gravity | 获取重力 / Get gravity | - |
| physics_add_collider | 添加碰撞体 / Add collider | name, colliderType |
| physics_add_rigidbody | 添加刚体 / Add rigidbody | name, mass, useGravity |

### Audio (音频) — 12 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| audio_find_clips | 查找音频片段 / Find audio clips | searchFilter, limit |
| audio_get_clip_info | 获取片段信息 / Get clip info | assetPath |
| audio_add_source | 添加音源 / Add audio source | name, clipPath |
| audio_get_source_info | 获取音源信息 / Get source info | name |
| audio_set_source_properties | 设置音源属性 / Set source properties | name, volume, pitch, loop... |
| audio_find_sources_in_scene | 查找场景音源 / Find sources in scene | - |
| audio_create_mixer | 创建混音器 / Create audio mixer | name, savePath |
| audio_play | 播放音频 / Play audio | name |
| audio_stop | 停止音频 / Stop audio | name |
| audio_set_3d | 设置 3D 音频 / Set 3D audio | name, spatialBlend, minDistance, maxDistance |
| audio_get_import_settings | 获取导入设置 / Get import settings | assetPath |
| audio_set_import_settings | 设置导入设置 / Set import settings | assetPath, loadType, compressionFormat... |

### NavMesh (导航网格) — 10 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| navmesh_bake | 烘焙导航网格 / Bake NavMesh | - |
| navmesh_add_agent | 添加导航代理 / Add NavMesh agent | name |
| navmesh_set_destination | 设置目标点 / Set destination | name, x, y, z |
| navmesh_add_obstacle | 添加障碍物 / Add obstacle | name, shape, carve |
| navmesh_sample_position | 采样位置 / Sample position | x, y, z, maxDistance |
| navmesh_calculate_path | 计算路径 / Calculate path | startX, startY, startZ, endX, endY, endZ |
| navmesh_set_area_cost | 设置区域代价 / Set area cost | area, cost |
| navmesh_get_settings | 获取设置 / Get settings | - |
| navmesh_set_settings | 设置参数 / Set settings | agentRadius, agentHeight, stepHeight... |
| navmesh_add_link | 添加链接 / Add OffMeshLink | name, start, end |

### Terrain (地形) — 10 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| terrain_create | 创建地形 / Create terrain | name, width, height, length |
| terrain_set_heightmap | 设置高度图 / Set heightmap | name, heightmapPath |
| terrain_perlin_noise | 柏林噪声 / Perlin noise | name, scale, amplitude |
| terrain_smooth | 平滑地形 / Smooth terrain | name, iterations |
| terrain_flatten | 展平区域 / Flatten area | name, height |
| terrain_paint_texture | 绘制纹理 / Paint texture | name, layerIndex, x, z, radius |
| terrain_add_layer | 添加纹理层 / Add texture layer | name, texturePath |
| terrain_get_info | 获取地形信息 / Get terrain info | name |
| terrain_set_resolution | 设置分辨率 / Set resolution | name, heightmapRes, detailRes |
| terrain_raise_lower | 升降地形 / Raise/Lower terrain | name, x, z, radius, amount |

### Script (脚本) — 12 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| script_create | 创建脚本 / Create script | name, scriptType, namespace |
| script_read | 读取脚本 / Read script | scriptPath |
| script_replace | 替换内容 / Replace content | scriptPath, oldText, newText |
| script_list | 列出脚本 / List scripts | folder, limit |
| script_get_info | 获取信息 / Get info | scriptPath |
| script_find_in_file | 文件内搜索 / Search in file | scriptPath, pattern |
| script_rename | 重命名 / Rename | scriptPath, newName |
| script_move | 移动脚本 / Move script | scriptPath, destinationFolder |
| script_delete | 删除脚本 / Delete script | scriptPath |
| script_create_batch | **批量创建 / Batch create** | items (JSON array) |
| script_get_references | 获取引用 / Get references | scriptPath |
| script_analyze | 分析脚本 / Analyze script | scriptPath |

### Shader (着色器) — 11 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| shader_create | 创建着色器 / Create shader | name, shaderType, savePath |
| shader_create_urp | 创建 URP 着色器 / Create URP shader | name, savePath |
| shader_read | 读取着色器 / Read shader | assetPath |
| shader_delete | 删除着色器 / Delete shader | assetPath |
| shader_check_errors | 检查错误 / Check errors | assetPath |
| shader_get_keywords | 获取关键字 / Get keywords | assetPath |
| shader_get_variant_count | 获取变体数 / Get variant count | assetPath |
| shader_set_global_keyword | 设置全局关键字 / Set global keyword | keyword, enabled |
| shader_list | 列出着色器 / List shaders | searchFilter |
| shader_get_properties | 获取属性 / Get properties | assetPath |
| shader_find_usage | 查找使用 / Find usage | assetPath |

### Animator (动画) — 10 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| animator_create_controller | 创建控制器 / Create controller | name, savePath |
| animator_add_parameter | 添加参数 / Add parameter | name, paramName, paramType |
| animator_set_parameter | 设置参数 / Set parameter | name, paramName, value |
| animator_get_parameters | 获取参数 / Get parameters | name |
| animator_add_state | 添加状态 / Add state | name, stateName, layerIndex |
| animator_add_transition | 添加过渡 / Add transition | name, fromState, toState |
| animator_assign | 分配控制器 / Assign controller | name, controllerPath |
| animator_get_info | 获取信息 / Get info | name |
| animator_set_layer | 设置图层 / Set layer | name, layerIndex, weight |
| animator_get_states | 获取状态列表 / Get states | name, layerIndex |

### UI System (UI 系统) — 16 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| ui_create_canvas | 创建画布 / Create canvas | name, renderMode |
| ui_create_button | 创建按钮 / Create button | name, text, parentName |
| ui_create_text | 创建文本 / Create text | name, text, parentName |
| ui_create_image | 创建图片 / Create image | name, spritePath, parentName |
| ui_create_slider | 创建滑块 / Create slider | name, parentName |
| ui_create_toggle | 创建开关 / Create toggle | name, text, parentName |
| ui_create_input_field | 创建输入框 / Create input field | name, placeholder, parentName |
| ui_create_dropdown | 创建下拉框 / Create dropdown | name, options, parentName |
| ui_set_anchors | 设置锚点 / Set anchors | name, preset |
| ui_set_rect_transform | 设置 RectTransform | name, anchoredX, anchoredY, width, height |
| ui_set_text | 设置文本 / Set text content | name, text |
| ui_add_layout | 添加布局 / Add layout | name, layoutType |
| ui_align | 对齐 / Align elements | names, alignment |
| ui_distribute | 分布 / Distribute elements | names, axis |
| ui_set_color | 设置颜色 / Set UI color | name, r, g, b, a |
| ui_create_scroll_view | 创建滚动视图 / Create scroll view | name, parentName |

### UI Toolkit (UI 工具包) — 15 skills [v1.6 新增 / New in v1.6]

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| uitk_create_uxml | 创建 UXML 文件 / Create UXML file | savePath, content |
| uitk_create_uss | 创建 USS 文件 / Create USS file | savePath, content |
| uitk_read_uxml | 读取 UXML / Read UXML | assetPath |
| uitk_read_uss | 读取 USS / Read USS | assetPath |
| uitk_add_uidocument | 添加 UIDocument / Add UIDocument | name, uxmlPath |
| uitk_create_panel_settings | 创建面板设置 / Create PanelSettings | name, savePath |
| uitk_get_panel_settings | 获取面板设置 / Get PanelSettings | assetPath |
| uitk_set_panel_settings | 设置面板属性 / Set PanelSettings | assetPath, properties |
| uitk_inspect_uxml | 检查 UXML 结构 / Inspect UXML structure | assetPath |
| uitk_create_template | 从模板创建 / Create from template | templateName, savePath |
| uitk_create_batch | **批量创建文件 / Batch create files** | items (JSON array) |
| uitk_list_files | 列出文件 / List UXML/USS files | searchFilter |
| uitk_delete_file | 删除文件 / Delete file | assetPath |
| uitk_set_uidocument | 设置 UIDocument / Set UIDocument properties | name, uxmlPath, panelSettingsPath |
| uitk_get_uidocument | 获取 UIDocument / Get UIDocument info | name |

### Workflow (工作流) — 22 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| workflow_task_start | 开始任务 / Start task | taskName |
| workflow_task_end | 结束任务 / End task | taskName |
| workflow_undo_task | 撤销整个任务 / Undo entire task | taskName |
| workflow_snapshot_object | 快照物体 / Snapshot object | name |
| workflow_snapshot_created | 快照已创建物体 / Snapshot created object | name |
| workflow_get_history | 获取历史 / Get history | limit |
| workflow_clear_history | 清除历史 / Clear history | - |
| workflow_get_task_list | 获取任务列表 / Get task list | - |
| workflow_bookmark_create | 创建书签 / Create bookmark | name |
| workflow_bookmark_restore | 恢复书签 / Restore bookmark | name |
| workflow_bookmark_list | 列出书签 / List bookmarks | - |
| workflow_bookmark_delete | 删除书签 / Delete bookmark | name |
| workflow_redo_task | 重做任务 / Redo task | taskName |
| workflow_get_task_detail | 获取任务详情 / Get task detail | taskName |
| workflow_session_save | 保存会话 / Save session | sessionName |
| workflow_session_load | 加载会话 / Load session | sessionName |
| workflow_session_list | 列出会话 / List sessions | - |
| workflow_session_delete | 删除会话 / Delete session | sessionName |
| history_undo | 多步撤销 / Multi-step undo | steps |
| history_redo | 多步重做 / Multi-step redo | steps |
| history_get_stack | 获取撤销栈 / Get undo stack | - |
| history_clear | 清除撤销历史 / Clear undo history | - |

### Package (包管理) — 11 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| package_list | 列出已安装包 / List installed packages | - |
| package_install | 安装包 / Install package | packageName, version |
| package_remove | 移除包 / Remove package | packageName |
| package_search | 搜索包 / Search packages | query |
| package_get_info | 获取信息 / Get info | packageName |
| package_get_versions | 获取版本 / Get versions | packageName |
| package_get_dependencies | 获取依赖 / Get dependencies | packageName |
| package_install_cinemachine | 安装 Cinemachine / Install Cinemachine | - |
| package_install_splines | 安装 Splines / Install Splines | - |
| package_update | 更新包 / Update package | packageName |
| package_embed | 嵌入包 / Embed package | packageName |

### Project (项目设置) — 11 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| project_get_settings | 获取项目设置 / Get project settings | - |
| project_set_rendering_pipeline | 设置渲染管线 / Set rendering pipeline | pipelineAssetPath |
| project_get_build_target | 获取构建目标 / Get build target | - |
| project_set_build_target | 设置构建目标 / Set build target | target |
| project_add_layer | 添加图层 / Add layer | layerName |
| project_add_tag | 添加标签 / Add tag | tagName |
| project_set_quality | 设置质量等级 / Set quality level | levelIndex |
| project_get_quality_settings | 获取质量设置 / Get quality settings | - |
| project_set_player_settings | 设置播放器设置 / Set player settings | companyName, productName... |
| project_get_player_settings | 获取播放器设置 / Get player settings | - |
| project_get_scripting_defines | 获取脚本宏 / Get scripting defines | - |

### ScriptableObject — 10 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| scriptableobject_create | 创建 SO / Create ScriptableObject | typeName, savePath |
| scriptableobject_get | 获取属性 / Get properties | assetPath |
| scriptableobject_set | 设置属性 / Set properties | assetPath, propertyName, value |
| scriptableobject_set_batch | **批量设置 / Batch set** | items (JSON array) |
| scriptableobject_delete | 删除 SO / Delete | assetPath |
| scriptableobject_find | 查找 SO / Find | typeName, limit |
| scriptableobject_list_types | 列出类型 / List types | - |
| scriptableobject_duplicate | 复制 SO / Duplicate | assetPath, newPath |
| scriptableobject_export_json | 导出 JSON / Export to JSON | assetPath, jsonFilePath |
| scriptableobject_import_json | 导入 JSON / Import from JSON | jsonFilePath, assetPath |

### Event (事件) — 10 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| event_add_listener | 添加监听器 / Add listener | name, eventName, targetName, methodName |
| event_remove_listener | 移除监听器 / Remove listener | name, eventName, index |
| event_get_listeners | 获取监听器 / Get listeners | name, eventName |
| event_clear_listeners | 清除监听器 / Clear listeners | name, eventName |
| event_add_listener_batch | **批量添加 / Batch add** | items (JSON array) |
| event_copy_listeners | 复制监听器 / Copy listeners | sourceName, targetName, eventName |
| event_set_enabled | 设置启用 / Set enabled | name, eventName, index, enabled |
| event_get_info | 获取事件信息 / Get event info | name |
| event_invoke | 调用事件 / Invoke event | name, eventName |
| event_set_call_state | 设置调用状态 / Set call state | name, eventName, index, callState |

### Debug (调试) — 10 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| debug_get_errors | 获取错误日志 / Get error logs | limit |
| debug_get_logs | 获取普通日志 / Get info logs | limit |
| debug_get_warnings | 获取警告 / Get warnings | limit |
| debug_check_compilation | 检查编译 / Check compilation | - |
| debug_get_stack_trace | 获取堆栈追踪 / Get stack trace | logIndex |
| debug_get_assemblies | 获取程序集 / Get assemblies | - |
| debug_get_defines | 获取宏定义 / Get defines | - |
| debug_get_memory_info | 获取内存信息 / Get memory info | - |
| debug_clear_logs | 清除日志 / Clear logs | - |
| debug_get_all | 获取所有日志 / Get all logs | limit |

### Smart (智能工具) — 10 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| smart_scene_query | 场景 SQL 查询 / Scene SQL query | query |
| smart_spatial_query | 空间查询 / Spatial query | center, radius |
| smart_scene_layout | 场景布局 / Scene layout | layoutType |
| smart_align_to_ground | 对齐到地面 / Align to ground | - |
| smart_distribute | 均匀分布 / Distribute evenly | axis |
| smart_replace_objects | 替换物体 / Replace objects | prefabPath |
| smart_grid_snap | 网格吸附 / Grid snap | gridSize |
| smart_reference_bind | 引用绑定 / Reference bind | name, fieldName, targetName |
| smart_auto_name | 自动命名 / Auto name | - |
| smart_find_similar | 查找相似 / Find similar | name |

### Profiler (性能分析) — 10 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| profiler_get_fps | 获取 FPS / Get FPS | - |
| profiler_get_memory | 获取内存 / Get memory | - |
| profiler_get_runtime_memory | 运行时内存 Top N / Runtime memory Top N | limit |
| profiler_get_texture_memory | 纹理内存 / Texture memory | - |
| profiler_get_mesh_memory | 网格内存 / Mesh memory | - |
| profiler_get_material_memory | 材质内存 / Material memory | - |
| profiler_get_audio_memory | 音频内存 / Audio memory | - |
| profiler_get_object_count | 对象数量 / Object count | - |
| profiler_get_rendering_stats | 渲染统计 / Rendering stats | - |
| profiler_get_asset_bundle_stats | AB 包统计 / Asset bundle stats | - |

### Optimization (优化) — 10 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| optimize_analyze_scene | 分析场景 / Analyze scene | - |
| optimize_find_large_assets | 查找大资产 / Find large assets | minSize |
| optimize_set_static_flags | 设置静态标志 / Set static flags | name, flags |
| optimize_get_static_flags | 获取静态标志 / Get static flags | name |
| optimize_audio_compression | 优化音频压缩 / Optimize audio compression | - |
| optimize_find_duplicate_materials | 查找重复材质 / Find duplicate materials | - |
| optimize_analyze_overdraw | 分析过度绘制 / Analyze overdraw | - |
| optimize_set_lod_group | 设置 LOD 组 / Set LOD group | name, lodDistances |
| optimize_texture_compression | 优化纹理压缩 / Optimize texture compression | - |
| optimize_mesh_optimization | 优化网格 / Optimize mesh | assetPath |

### Validation (验证) — 10 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| validate_missing_references | 检查缺失引用 / Check missing references | - |
| validate_mesh_collider_convex | 检查碰撞体凸包 / Check collider convex | - |
| validate_shader_errors | 检查着色器错误 / Check shader errors | - |
| validate_empty_folders | 检查空文件夹 / Check empty folders | - |
| validate_delete_empty_folders | 删除空文件夹 / Delete empty folders | - |
| validate_missing_scripts | 检查缺失脚本 / Check missing scripts | - |
| validate_fix_missing_scripts | 修复缺失脚本 / Fix missing scripts | - |
| validate_duplicate_names | 检查重名 / Check duplicate names | - |
| validate_large_textures | 检查大纹理 / Check large textures | maxSize |
| validate_project | 全面验证 / Full project validation | - |

### Cleaner (清理) — 10 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| cleaner_find_unused | 查找未使用资产 / Find unused assets | - |
| cleaner_find_duplicates | 查找重复文件 / Find duplicate files | - |
| cleaner_find_empty_folders | 查找空文件夹 / Find empty folders | - |
| cleaner_delete_unused | 删除未使用资产 / Delete unused assets | - |
| cleaner_delete_duplicates | 删除重复文件 / Delete duplicates | - |
| cleaner_delete_empty_folders | 删除空文件夹 / Delete empty folders | - |
| cleaner_fix_missing_scripts | 修复缺失脚本 / Fix missing scripts | - |
| cleaner_get_report | 获取清理报告 / Get cleanup report | - |
| cleaner_find_large_files | 查找大文件 / Find large files | minSize |
| cleaner_optimize_assets | 优化资产 / Optimize assets | - |

### Test (测试) — 10 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| test_run | 运行测试 / Run tests | testMode |
| test_run_by_name | 按名称运行 / Run by name | testName |
| test_run_by_category | 按分类运行 / Run by category | category |
| test_get_result | 获取结果 / Get result | jobId |
| test_list | 列出测试 / List tests | testMode |
| test_create_template | 创建模板 / Create template | name, testType |
| test_get_summary | 获取摘要 / Get summary | jobId |
| test_get_last_result | 获取上次结果 / Get last result | - |
| test_cancel | 取消测试 / Cancel test | jobId |
| test_get_coverage | 获取覆盖率 / Get coverage | - |

### Perception (感知) — 9 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| scene_summary | 场景摘要 / Scene summary | - |
| hierarchy_describe | 层级树描述 / Hierarchy describe (text tree) | maxDepth |
| scene_tag_layer_stats | 标签/图层统计 / Tag/Layer stats | - |
| scene_performance_hints | 性能提示 / Performance hints | - |
| script_overview | 脚本概览 / Script overview | - |
| scene_spatial_query | 空间查询 / Spatial query | center, radius |
| scene_material_summary | 材质摘要 / Material summary | - |
| scene_component_stats | 组件统计 / Component stats | - |
| scene_bounds | 场景边界 / Scene bounds | - |

### Sample (示例) — 8 skills

| Skill | 描述 / Description | 参数 / Parameters |
|-------|------|------|
| sample_hello | 你好示例 / Hello example | name |
| sample_create | 创建物体示例 / Create example | name, primitiveType |
| sample_delete | 删除物体示例 / Delete example | name |
| sample_transform | 变换示例 / Transform example | name, x, y, z |
| sample_material | 材质示例 / Material example | name, r, g, b |
| sample_scene_info | 场景信息示例 / Scene info example | - |
| sample_list_objects | 列出物体示例 / List objects example | - |
| sample_batch | 批量操作示例 / Batch example | items |

---

## 八、添加自定义 Skill / Add Custom Skills

在 `SkillsForUnity/Editor/Skills/` 目录下创建 C# 文件，使用 `[UnitySkill]` 属性标记静态方法：
Create a C# file in `SkillsForUnity/Editor/Skills/` and mark static methods with `[UnitySkill]`:

```csharp
using UnityEngine;
using UnitySkills;

namespace UnitySkills
{
    public static class MyCustomSkills
    {
        [UnitySkill("my_custom_skill", "My custom skill description / 我的自定义技能描述")]
        public static object MyCustomSkill(string param1, float param2 = 0)
        {
            // Your logic here / 你的逻辑
            return new { success = true, result = "..." };
        }
    }
}
```

Unity 重新编译后自动发现新 Skill，无需重启服务器。
New Skills are auto-discovered after Unity recompilation — no server restart required.

---

## 九、AI 集成 / AI Integration

将 `unity-skills/SKILL.md` 添加为 AI Skill，即可通过生成 Python 脚本控制 Unity。
Add `unity-skills/SKILL.md` as an AI Skill to control Unity via generated Python scripts.

### AI 对话示例 / AI Conversation Example
```
用户 / User: 在 Unity 中创建一个红色立方体 / Create a red cube in Unity
AI: 
import requests
requests.post("http://localhost:8090/skill/gameobject_create", 
    json={"name":"RedCube","primitiveType":"Cube","x":0,"y":1,"z":0})
requests.post("http://localhost:8090/skill/material_set_color",
    json={"name":"RedCube","r":1,"g":0,"b":0})
```

---

## 十、常见问题 / FAQ

### Q: 服务器启动失败？/ Server fails to start?
检查端口 8090 是否被占用。UnitySkills 会自动扫描 8090-8100 端口范围。
Check if port 8090 is occupied. UnitySkills auto-scans ports 8090-8100.

### Q: AI 无法连接？/ AI can't connect?
1. 确认服务器已启动（Console 有日志输出）/ Confirm server is running (check Console logs)
2. 访问 http://localhost:8090/health 验证 / Visit http://localhost:8090/health to verify
3. Windows 防火墙可能需要允许 / Windows Firewall may need to allow access

### Q: Domain Reload 后断连？/ Disconnected after Domain Reload?
v1.5.1+ 已修复此问题，服务器会自动恢复到同一端口。
Fixed in v1.5.1+. Server auto-recovers to the same port.

### Q: 超长任务超时？/ Long-running task timeout?
在 Unity 设置面板调整请求超时（默认 15 分钟）。Python 客户端会自动同步此配置。
Adjust request timeout in Unity settings panel (default: 15 minutes). Python client auto-syncs this config.

### Q: 支持多个 Unity 实例？/ Multiple Unity instances?
支持。UnitySkills 使用全局注册表和自动端口发现，可同时控制多个 Unity 项目。
Yes. UnitySkills uses a global registry and auto port discovery to control multiple Unity projects simultaneously.
