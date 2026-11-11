# Decal Skills

URP Decal Projector creation and configuration. URP only: the HDRP decal APIs are not covered here. Parameters come from the schema (`GET /skills/schema?category=Decal&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Adding decals in URP
- Configuring a Decal Projector
- Enabling the Decal Renderer Feature

## Rules
- Call `decal_ensure_renderer_feature` before assuming the current URP renderer is decal-ready; it returns `alreadyExists: true` when the feature is present, so it is safe to repeat.
- Use `decal_get_info` or `decal_find_all` to read the real projector state before editing.
- `decal_set_properties` applies only the fields you pass; any invalid value rejects the whole call before anything is written.
- When URP is not installed, every skill returns a stub `{ error: "Universal Render Pipeline package … is not installed." }` (`RenderPipelineSkillsCommon.NoURP()`). It is a diagnostic payload, not a permission denial.
- For renderer feature management in general, use the `urp` module.

## Pitfalls
- `decal_get_info` and `decal_set_properties` need at least one locator (`name`, `entityId`, `instanceId` or `path`); an empty call is refused rather than reported as "not found".
- `decal_set_properties_batch` expects `items` as a JSON array string.

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
| `entityId` | string | No* | null | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | 0 | GameObject instance ID |
| `path` | string | No* | null | GameObject hierarchy path |

\* Supply at least one locator — an empty call is refused up front rather than reported as "not found".

### `decal_set_properties`
Modify Decal Projector properties. Every property below is applied only when supplied, so a call changes exactly the fields you name; any invalid value rejects the whole call before anything is written.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | null | GameObject name |
| `entityId` | string | No* | null | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
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
| `scaleMode` | string | No | null | `ScaleInvariant` or `InheritFromHierarchy` (other values, including bare numbers, are rejected) |

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
| `entityId` | string | No* | null | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | 0 | GameObject instance ID |
| `path` | string | No* | null | GameObject hierarchy path |

### `decal_set_properties_batch`
Batch-edit Decal Projectors.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of `{name, instanceId, path, entityId, materialPath, drawDistance, fadeScale, fadeFactor, startAngleFade, endAngleFade, uvScale, uvBias, size, pivot, renderingLayerMask, scaleMode}` — same per-item keys as `decal_set_properties` |

### `decal_ensure_renderer_feature`
Ensure the target URP renderer has a DecalRendererFeature.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `assetPath` | string | No | null | URP asset path; the active URP asset is used when omitted |
| `rendererIndex` | int | No | -1 | Renderer index within the URP asset; `-1` selects the asset's default renderer |
| `rendererDataPath` | string | No | null | `UniversalRendererData` asset path — takes precedence over `rendererIndex`, and must belong to the resolved URP asset |

Returns `alreadyExists: true` when the feature was already present, so the call is safe to repeat.
