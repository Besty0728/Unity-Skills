---
name: unity-decal
description: Create and configure URP Decal Projectors
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may proceed straight to dryRun.

## Triggers
- Adding decals in URP
- Configuring a Decal Projector
- Enabling the Decal Renderer Feature
- 在 URP 中添加贴花、配置 Decal Projector、启用 Decal Renderer Feature

# Decal Skills

URP Decal Projector creation and configuration (URP only; HDRP decal APIs are not covered here).

## Operating Mode

- Query skills (`decal_get_info`, `decal_find_all`) are `SkillMode.SemiAuto` — they run in all three modes without grant.
- Mutating skills (`decal_create`, `decal_set_properties`, `decal_set_properties_batch`, `decal_ensure_renderer_feature`) are `SkillMode.FullAuto` — under **Approval** they need user grant (grant triggers one server-side execute returning the result); under **Auto** / **Bypass** they execute directly.
- `decal_delete` carries `SkillOperation.Delete` and is **auto-forbidden** in Approval / Auto modes (NeverInSemi). Only **Bypass** or the user-managed **Allowlist** can run it.

## URP Package Stub

This module is compiled against `com.unity.render-pipelines.universal` (`URP`). When URP is not installed, **every** skill returns a stub `{ error: "Universal Render Pipeline package … is not installed." }` (`RenderPipelineSkillsCommon.NoURP()`). The stub is a diagnostic payload, not a permission denial — it does **not** require grant and is **not** treated as NeverInSemi.

## Guardrails

**Routing**:
- For renderer feature management in general: `urp`
- For DecalProjector scene operations: this module

**Runtime-first rules**:
- Call `decal_ensure_renderer_feature` before assuming the current URP renderer is decal-ready
- Use `decal_get_info` / `decal_find_all` to discover real projector state before editing
- `decal_set_properties_batch` expects `items` to be a JSON array string
- This module targets the URP Decal workflow first; do not assume HDRP decal APIs are covered here

## Skills

### `decal_create`
Create a Decal Projector.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "Decal Projector" | Name of the new GameObject |
| `materialPath` | string | No | null | Decal material asset path; left unassigned when omitted |
| `x` | float | No | 0 | World position X |
| `y` | float | No | 0 | World position Y |
| `z` | float | No | 0 | World position Z |

### `decal_get_info`
Inspect a Decal Projector.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | null | GameObject name |
| `instanceId` | int | No* | 0 | GameObject instance ID |
| `path` | string | No* | null | GameObject hierarchy path |

\* Supply at least one locator — an empty call is refused up front rather than reported as "not found".

### `decal_set_properties`
Modify Decal Projector properties. Every property below is applied only when supplied, so a call changes exactly the fields you name.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | null | GameObject name |
| `instanceId` | int | No* | 0 | GameObject instance ID |
| `path` | string | No* | null | GameObject hierarchy path |
| `materialPath` | string | No | null | Decal material asset path |
| `drawDistance` | float | No | null | Max distance at which the decal is drawn |
| `fadeScale` | float | No | null | Fraction of `drawDistance` at which fading starts |
| `fadeFactor` | float | No | null | Overall opacity multiplier |
| `startAngleFade` | float | No | null | Angle (degrees) where angle-based fading begins |
| `endAngleFade` | float | No | null | Angle (degrees) where angle-based fading completes |
| `uvScale` | string | No | null | UV tiling as `"x,y"` |
| `uvBias` | string | No | null | UV offset as `"x,y"` |
| `size` | string | No | null | Projector box size as `"x,y,z"` |
| `pivot` | string | No | null | Projector pivot offset as `"x,y,z"` |
| `renderingLayerMask` | uint | No | null | Rendering layer mask bits |
| `scaleMode` | string | No | null | `ScaleInvariant` or `InheritFromHierarchy` |

### `decal_find_all`
List Decal Projectors in the scene.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `limit` | int | No | 50 | Max projectors returned |

### `decal_delete`
Delete a Decal Projector GameObject.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | null | GameObject name |
| `instanceId` | int | No* | 0 | GameObject instance ID |
| `path` | string | No* | null | GameObject hierarchy path |

### `decal_set_properties_batch`
Batch-edit Decal Projectors.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of `{name, instanceId, path, materialPath, drawDistance, fadeScale, fadeFactor, startAngleFade, endAngleFade, uvScale, uvBias, size, pivot, renderingLayerMask, scaleMode}` — same per-item keys as `decal_set_properties` |

### `decal_ensure_renderer_feature`
Ensure the target URP renderer has a DecalRendererFeature.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `assetPath` | string | No | null | URP asset path; the active URP asset is used when omitted |
| `rendererIndex` | int | No | -1 | Renderer index within the URP asset; `-1` selects the asset's default renderer |
| `rendererDataPath` | string | No | null | `UniversalRendererData` asset path — takes precedence over `rendererIndex`, and must belong to the resolved URP asset |

Returns `alreadyExists: true` when the feature was already present, so the call is safe to repeat.

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
