---
name: unity-timeline
description: Edit Unity Timeline — create Timeline assets and add typed tracks (animation, activation, audio, signal, etc.). Use when building cutscenes or sequences, creating a Timeline asset, or adding tracks and clips, even if the user just says "时间轴" or "做个过场动画". 编辑 Unity Timeline(创建 Timeline 资产、添加带类型的轨道:动画、激活、音频、信号等);当用户要制作过场或序列、创建 Timeline 资产、或添加轨道与片段时使用。
---

# Timeline Skills

Create and modify Unity Timeline assets — add typed tracks, drop clips on tracks, bind objects, set duration / wrap mode, and play/pause/stop the editor preview through the PlayableDirector.

## Operating Mode

- **Approval**：查询类 skill（`timeline_list_tracks`，源码标 `SkillMode.SemiAuto`）直接执行；其余变更/播放类（`timeline_create` / add_*_track / `timeline_add_clip` / `timeline_set_duration` / `timeline_play` / `timeline_set_binding`，标 `SkillMode.FullAuto`）需用户 grant，grant 后服务端一步执行返结果。
- **Auto / Bypass**：未被禁列表拦截的 skill 直接执行。
- 本模块**含 Delete 类 skill**：`timeline_remove_track` 标记为 `SkillOperation.Delete`，被 `IsForbiddenInSemi` 静态拦截 —— 仅 **Bypass** 模式或加入 **Allowlist** 才能调用。
- `timeline_play` 仅驱动 Editor 预览（PlayableDirector.Evaluate / Play 在编辑器上下文），不会进入 Play mode。

**DO NOT** (common hallucinations):
- `timeline_create_animation` / `timeline_add_track` do not exist → use the typed track skills: `timeline_add_animation_track`, `timeline_add_audio_track`, `timeline_add_activation_track`, `timeline_add_control_track`, `timeline_add_signal_track`
- `timeline_add_keyframe` does not exist → Timeline uses clips, not direct keyframes; use `timeline_add_clip`
- `timeline_set_duration` sets the Timeline asset duration, not individual clip duration

**Routing**:
- For Animator parameters/states → use `animator` module
- For runtime animation playback → use `editor_play` or write C# via `script` module

## Skills

### `timeline_create`
Create a new Timeline asset and Director instance.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | Yes | - | Name of the timeline/object |
| folder | string | No | "Assets/Timelines" | Folder to save asset |

**Returns:** `{ success, assetPath, gameObjectName, directorInstanceId }`

### `timeline_add_audio_track`
Add an Audio track to a Timeline.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | - | GameObject name with PlayableDirector |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | - | GameObject hierarchy path |
| trackName | string | No | "Audio Track" | Name of the new track |

**Returns:** `{ success, trackName }`

### `timeline_add_animation_track`
Add an Animation track to a Timeline, optionally binding an object.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | - | GameObject name with PlayableDirector |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | - | GameObject hierarchy path |
| trackName | string | No | "Animation Track" | Name of the new track |
| bindingObjectName | string | No | - | Name of the GameObject to bind (animator) |

**Returns:** `{ success, trackName, boundObject }`

### `timeline_add_activation_track`
Add an Activation track to control object visibility.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | - | GameObject name with PlayableDirector |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | - | GameObject hierarchy path |
| trackName | string | No | "Activation Track" | Name of the new track |

**Returns:** `{ success, trackName }`

### `timeline_add_control_track`
Add a Control track for nested Timelines or prefab spawning.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | - | GameObject name with PlayableDirector |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | - | GameObject hierarchy path |
| trackName | string | No | "Control Track" | Name of the new track |

**Returns:** `{ success, trackName }`

### `timeline_add_signal_track`
Add a Signal track for event markers.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | - | GameObject name with PlayableDirector |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | - | GameObject hierarchy path |
| trackName | string | No | "Signal Track" | Name of the new track |

**Returns:** `{ success, trackName }`

### `timeline_remove_track`
Remove a track by name from a Timeline.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | - | GameObject name with PlayableDirector |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | - | GameObject hierarchy path |
| trackName | string | No | - | Name of the track to remove |

**Returns:** `{ success, removed }`

### `timeline_list_tracks`
List all tracks in a Timeline.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | - | GameObject name with PlayableDirector |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | - | GameObject hierarchy path |

**Returns:** `{ count, tracks: [{ name, type, muted, clipCount }] }`

### `timeline_add_clip`
Add a clip to a track by track name.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | - | GameObject name with PlayableDirector |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | - | GameObject hierarchy path |
| trackName | string | No | - | Name of the target track |
| start | double | No | 0 | Clip start time in seconds |
| duration | double | No | 1 | Clip duration in seconds |

**Returns:** `{ success, trackName, clipStart, clipDuration }`

### `timeline_set_duration`
Set Timeline duration and wrap mode.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | - | GameObject name with PlayableDirector |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | - | GameObject hierarchy path |
| duration | double | No | 0 | Fixed duration in seconds |
| wrapMode | string | No | - | Wrap mode: Hold/Loop/None |

**Returns:** `{ success, duration, wrapMode }`

### `timeline_play`
Play, pause, or stop a Timeline (Editor preview).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | - | GameObject name with PlayableDirector |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | - | GameObject hierarchy path |
| action | string | No | "play" | Action: play/pause/stop |

**Returns:** `{ success, action, time }`

### `timeline_set_binding`
Set the binding object for a track.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | - | GameObject name with PlayableDirector |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | - | GameObject hierarchy path |
| trackName | string | No | - | Name of the track |
| bindingObjectName | string | No | - | Name of the object to bind |

**Returns:** `{ success, trackName, boundTo }`

## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
