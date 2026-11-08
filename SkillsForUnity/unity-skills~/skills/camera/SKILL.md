---
name: unity-camera
description: Control Unity Scene View and Game cameras
---

# Unity Camera Skills

Control the Scene View camera and Game Cameras: creation, transform, properties, culling mask, orthographic mode and screenshots. Parameters come from the schema (`GET /skills/schema?category=Camera&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Framing the Scene View
- Creating or adjusting Game Cameras
- Tweaking FOV, clipping planes or culling
- Capturing a Game Camera or Scene View screenshot

## Rules
- The skills control two different cameras. `camera_get_info`, `camera_set_transform`, `camera_look_at` and `camera_align_view_to_object` act on the editor Scene View viewport. `camera_create`, `camera_get_properties`, `camera_set_properties`, `camera_set_culling_mask`, `camera_set_orthographic` and `camera_screenshot` act on Game Cameras. `camera_get_info` will not show a camera you just created: use `camera_get_properties` or `camera_list`.
- `camera_set_transform` positions the pivot (the framed world point), not the eye.
- `camera_set_properties` echoes the post-write state and `applied`; that is the verification, so do not re-read. An unparseable `clearFlags` rejects the whole call before any write.
- `camera_screenshot` and `camera_sceneview_screenshot` save to a file. Use `returnImage` only for clients that cannot read the project's files; a local agent should read the PNG at `path`.
- `camera_sceneview_screenshot` needs the Scene View window open and visible for the overlay capture; it falls back to a clean offscreen render when the internal API is missing (`mode` says which).
- Cinemachine virtual cameras belong to the [cinemachine](../cinemachine/SKILL.md) module. Timeline camera tracks belong to [timeline](../timeline/SKILL.md). For screenshots of the whole Game View, use `scene_screenshot`.

## Pitfalls
- `camera_move`, `camera_rotate` and `camera_delete` do not exist: use `camera_set_transform` (Scene View), `gameobject_set_transform` (Game Camera) and `gameobject_delete` (Game Camera).
- `camera_set_fov` does not exist: use `camera_set_properties` with `fieldOfView`.

## Skills Overview

| Skill | Purpose |
|---|---|
| [`camera_get_info`](reference/camera_get_info.md) | Scene View viewport position and rotation |
| [`camera_set_transform`](reference/camera_set_transform.md) | Set the Scene View pivot, rotation and size |
| [`camera_look_at`](reference/camera_look_at.md) | Focus the Scene View on a world point |
| [`camera_align_view_to_object`](reference/camera_align_view_to_object.md) | Align the Scene View to a GameObject |
| [`camera_create`](reference/camera_create.md) | Create a Game Camera |
| [`camera_list`](reference/camera_list.md) | List cameras in the scene |
| [`camera_get_properties`](reference/camera_get_properties.md) | Read Game Camera properties |
| [`camera_set_properties`](reference/camera_set_properties.md) | Set FOV, clip planes, clear flags, background, depth |
| [`camera_set_culling_mask`](reference/camera_set_culling_mask.md) | Set the culling mask by layer names |
| [`camera_set_orthographic`](reference/camera_set_orthographic.md) | Switch a Game Camera between orthographic and perspective |
| [`camera_screenshot`](reference/camera_screenshot.md) | Render one Game Camera to a PNG |
| [`camera_sceneview_screenshot`](reference/camera_sceneview_screenshot.md) | Capture the editor Scene View to a PNG |
