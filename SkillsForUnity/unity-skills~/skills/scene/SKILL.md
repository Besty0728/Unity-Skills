---
name: unity-scene
description: Manage Unity scenes — create, load (single/additive), save, unload, switch the active scene, and get scene info/hierarchy. Use when opening or saving scenes, loading additively, switching the active scene, or querying scene contents, even if the user just says "打开场景" or "切场景". 管理 Unity 场景(创建、加载、叠加加载、保存、卸载、切换活动场景、获取场景信息与层级);当用户要打开或保存场景、叠加加载、切换活动场景、或查询场景内容时使用。
---

# Unity Scene Skills

Control Unity scenes - the containers that hold all your GameObjects.

## Operating Mode

- **Approval**：本模块 Mixed —— `scene_get_info` / `scene_get_hierarchy` / `scene_get_loaded` / `scene_find_objects` 标 `SkillMode.SemiAuto`，可直接执行；`scene_screenshot` / `scene_unload` / `scene_set_active` 未设 Mode 字段（默认 FullAuto），Approval 模式下需 grant。
- **Auto / Bypass**：FullAuto 直接执行。
- **含 NeverInSemi 高危 skill**：`scene_create` / `scene_load` / `scene_save`（标 `RiskLevel="high"`，因为切换/覆盖整个场景文件影响范围极大）。这些在 Approval/Auto 下返 `MODE_FORBIDDEN`，仅 Bypass 或 Allowlist 命中可调。

**DO NOT** (common hallucinations):
- `scene_delete` / `scene_rename` do not exist → delete scene files via `asset_delete`, rename via `asset_move`
- `scene_list` does not exist → use `scene_get_loaded` (loaded scenes) or `asset_find` with `t:Scene` (all scene assets)
- `scene_find_objects` is a simple name/tag/component filter; for regex/layer/path search use `gameobject_find` (SkillMode.FullAuto)

**Routing**:
- For detailed hierarchy tree → use `perception` module's `hierarchy_describe`
- For scene statistics → use `perception` module's `scene_summarize`
- For screenshot → `scene_screenshot` (this module) captures the **Game View** final composited image (all cameras + UI; in Play mode this is the live runtime frame); `camera_screenshot` (camera module, SkillMode.FullAuto) renders a single Game Camera off-screen

## Skills Overview

| Skill | Description |
|-------|-------------|
| `scene_create` | Create a new scene |
| `scene_load` | Load a scene |
| `scene_save` | Save current scene |
| `scene_get_info` | Get scene information |
| `scene_get_hierarchy` | Get hierarchy tree |
| `scene_screenshot` | Capture screenshot |
| `scene_get_loaded` | Get all loaded scenes |
| `scene_unload` | Unload an additive scene |
| `scene_set_active` | Set active scene |
| `scene_find_objects` | Search objects by name/tag/component |

---

## Skills

### scene_create
Create a new scene.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `scenePath` | string | Yes | Path for new scene (e.g., "Assets/Scenes/MyScene.unity") |

### scene_load
Load a scene.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `scenePath` | string | Yes | - | Scene asset path |
| `additive` | bool | No | false | Load additively (keep current scene) |

### scene_save
Save the current scene.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `scenePath` | string | No | Save path (null = save current) |

### scene_get_info
Get current scene information.

No parameters.

**Returns**: `{success, name, path, isDirty, rootObjectCount, rootObjects: [name]}`

### scene_get_hierarchy
Get full scene hierarchy tree.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `maxDepth` | int | No | 10 | Maximum hierarchy depth |

**Returns**: `{success, hierarchy: [{name, instanceId, children: [...]}]}`

### scene_screenshot
Capture a screenshot of the **Game View** — the final composited frame of all cameras + UI. In Play mode this is the live runtime image, **not** the Scene/editor view. For a single Game Camera's render use `camera_screenshot` instead.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `filename` | string | No | "screenshot.png" | Bare filename only (no path separators); saved under `Assets/Screenshots/` |
| `width` | int | No | 1920 | Image width |
| `height` | int | No | 1080 | Image height |

**Returns**: `{success, path, width, height, isPlaying, note}`. `isPlaying` indicates whether the frame is a live runtime image (Play mode) or a static Edit-mode frame.

**Async**: `ScreenCapture.CaptureScreenshot` writes the PNG ~1 frame later. If reading `path` immediately fails, wait ~200ms and retry.

### scene_get_loaded
Get list of all currently loaded scenes.

No parameters.

**Returns**: `{success, scenes: [{name, path, isActive, isDirty}]}`

### scene_unload
Unload a loaded scene (additive).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `sceneName` | string | Yes | Scene name to unload |

### scene_set_active
Set the active scene (for multi-scene editing).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `sceneName` | string | Yes | Scene name to set active |

### scene_find_objects
Search GameObjects by name pattern, tag, or component type. For advanced search (regex, layer, path) use gameobject_find.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `namePattern` | string | No | - | Name substring to match (case-insensitive) |
| `tag` | string | No | - | Filter by tag |
| `componentType` | string | No | - | Filter by component type name |
| `limit` | int | No | 50 | Max results to return |

**Returns**: `{success, count, objects: [{name, path, instanceId, active, tag}]}`

---

## Example Usage

```python
import unity_skills

# Create a new scene
unity_skills.call_skill("scene_create", scenePath="Assets/Scenes/Level1.unity")

# Load an existing scene
unity_skills.call_skill("scene_load", scenePath="Assets/Scenes/MainMenu.unity")

# Load scene additively (multi-scene)
unity_skills.call_skill("scene_load", scenePath="Assets/Scenes/UI.unity", additive=True)

# Get current scene info
info = unity_skills.call_skill("scene_get_info")
print(f"Scene: {info['name']}, Objects: {info['rootObjectCount']}")

# Get full hierarchy (useful for understanding scene structure)
hierarchy = unity_skills.call_skill("scene_get_hierarchy", maxDepth=5)

# Save scene
unity_skills.call_skill("scene_save")

# Take screenshot
unity_skills.call_skill("scene_screenshot", filename="preview.png", width=1920, height=1080)
```

## Best Practices

1. Always save before loading a new scene
2. Use additive loading for UI overlays
3. Keep scene hierarchy organized with empty parent objects
4. Use `scene_get_info` to verify scene state
5. Screenshots are saved under `Assets/Screenshots/` (filename is a bare name; any path separators are stripped)

## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
