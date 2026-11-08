---
name: unity-timeline
description: Edit Unity Timeline assets and tracks
---

# Unity Timeline Skills

Create and modify Unity Timeline assets: add typed tracks, place clips, bind objects, set duration and wrap mode, and play, pause or stop the editor preview through the PlayableDirector. Parameters come from the schema (`GET /skills/schema?names=<skill>&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Building cutscenes or sequences
- Creating Timeline assets
- Adding tracks and clips

## Rules
- Track and clip skills take the target GameObject by `name`, `entityId`, `instanceId` or `path`. That GameObject must carry a PlayableDirector.
- `timeline_set_duration` sets the Timeline asset duration and wrap mode, not the duration of an individual clip.
- `timeline_play` drives the editor preview only (`PlayableDirector.Evaluate` or `Play` in editor context). It does not enter Play Mode.
- Animator parameters and states belong to the [animator](../animator/SKILL.md) module. For runtime playback use `editor_play`, or write C# through the [script](../script/SKILL.md) module.
- `timeline_remove_track` is a delete operation; its `approvalBehavior` in the schema shows how it is gated.

## Pitfalls
- `timeline_create_animation`, `timeline_add_track` and `timeline_add_keyframe` do not exist. Use the typed track skills (`timeline_add_animation_track`, `timeline_add_audio_track`, `timeline_add_activation_track`, `timeline_add_control_track`, `timeline_add_signal_track`), and `timeline_add_clip` for clips. Timeline uses clips, not direct keyframes.

## Skills

### timeline_create
Create a Timeline asset and a PlayableDirector instance.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | Yes | - | Name of the timeline and its GameObject |
| folder | string | No | "Assets/Timelines" | Folder to save the asset |

**Returns**: `{success, assetPath, gameObjectName, directorInstanceId}`

### timeline_add_audio_track
Add an Audio track.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name, entityId, instanceId, path | - | No | - | Target GameObject with a PlayableDirector (`name`, `entityId`, `instanceId` default 0, or `path`) |
| trackName | string | No | "Audio Track" | Name of the new track |

**Returns**: `{success, trackName}`

### timeline_add_animation_track
Add an Animation track, optionally binding an object.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name, entityId, instanceId, path | - | No | - | Target GameObject with a PlayableDirector |
| trackName | string | No | "Animation Track" | Name of the new track |
| bindingObjectName | string | No | - | Name of the GameObject to bind (with an Animator) |
| bindingEntityId | string | No | - | Entity ID of the GameObject to bind; outranks `bindingObjectName` |

**Returns**: `{success, trackName, boundObject}`

### timeline_add_activation_track
Add an Activation track to control object visibility.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name, entityId, instanceId, path | - | No | - | Target GameObject with a PlayableDirector |
| trackName | string | No | "Activation Track" | Name of the new track |

**Returns**: `{success, trackName}`

### timeline_add_control_track
Add a Control track for nested Timelines or prefab spawning.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name, entityId, instanceId, path | - | No | - | Target GameObject with a PlayableDirector |
| trackName | string | No | "Control Track" | Name of the new track |

**Returns**: `{success, trackName}`

### timeline_add_signal_track
Add a Signal track for event markers.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name, entityId, instanceId, path | - | No | - | Target GameObject with a PlayableDirector |
| trackName | string | No | "Signal Track" | Name of the new track |

**Returns**: `{success, trackName}`

### timeline_remove_track
Remove a track by name.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name, entityId, instanceId, path | - | No | - | Target GameObject with a PlayableDirector |
| trackName | string | Yes | - | Name of the track to remove |

**Returns**: `{success, removed}`

### timeline_list_tracks
List the tracks in a Timeline.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name, entityId, instanceId, path | - | No | - | Target GameObject with a PlayableDirector |

**Returns**: `{count, tracks: [{name, type, muted, clipCount}]}`

### timeline_add_clip
Add a clip to a track.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name, entityId, instanceId, path | - | No | - | Target GameObject with a PlayableDirector |
| trackName | string | Yes | - | Name of the target track |
| start | double | No | 0 | Clip start time in seconds |
| duration | double | No | 1 | Clip duration in seconds |

**Returns**: `{success, trackName, clipStart, clipDuration}`

### timeline_set_duration
Set the Timeline duration and wrap mode.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name, entityId, instanceId, path | - | No | - | Target GameObject with a PlayableDirector |
| duration | double | No | 0 | Fixed duration in seconds |
| wrapMode | string | No | - | `Hold`, `Loop` or `None` |

**Returns**: `{success, duration, wrapMode}`

### timeline_play
Play, pause or stop the Timeline in the editor preview.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name, entityId, instanceId, path | - | No | - | Target GameObject with a PlayableDirector |
| action | string | No | "play" | `play`, `pause` or `stop` |

**Returns**: `{success, action, time}`

### timeline_set_binding
Set the binding object for a track.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name, entityId, instanceId, path | - | No | - | Target GameObject with a PlayableDirector |
| trackName | string | Yes | - | Name of the track |
| bindingObjectName | string | No* | - | Name of the object to bind |
| bindingEntityId | string | No* | - | Entity ID of the object to bind; outranks `bindingObjectName` |

\* One of `bindingObjectName` or `bindingEntityId` is required.

**Returns**: `{success, trackName, boundTo}`
