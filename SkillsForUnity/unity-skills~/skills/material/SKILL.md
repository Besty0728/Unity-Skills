---
name: unity-material
description: Edit material and shader properties across Built-in/URP/HDRP
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may proceed straight to dryRun.

## Triggers
- Changing how a surface looks
- Tweaking material parameters
- Swapping shaders
- 调整物体外观、修改材质参数、切换 Shader

# Unity Material Skills

> **BATCH-FIRST**: Use `*_batch` skills when operating on 2+ objects/materials.

## Operating Mode

- **Approval** (default): all mutating skills (`material_create`, `material_create_batch`, `material_assign`, `material_assign_batch`, `material_duplicate`, `material_set_color` / `_emission` / `_texture` / `_float` / `_int` / `_vector` / `_keyword` / `_render_queue` / `_shader` / `_texture_offset` / `_texture_scale` / `_gi_flags`, and the `*_batch` variants) need user grant; grant triggers a single server-side execution that returns the result.
- **Auto / Bypass**: those skills execute directly.
- Query skills (`material_get_properties`, `material_get_keywords`) are `SkillMode.SemiAuto` — they run in all three modes without grant.
- This module contains **no** Delete / PlayMode / Reload / high-risk skills (no NeverInSemi); to delete a material asset, call the `asset` module.

## Guardrails

**DO NOT** (common hallucinations):
- `material_set_metallic` / `material_set_smoothness` do not exist → use `material_set_float` with `propertyName="_Metallic"` or `"_Glossiness"` (Standard) / `"_Smoothness"` (URP)
- `material_set_color` r/g/b/a range is **0–1**, not 0–255
- `material_set_property` does not exist → use the specific setter: `material_set_float`, `material_set_int`, `material_set_vector`, `material_set_color`
- `material_get_color` does not exist → use `material_get_properties` (returns all properties including colors)

**Routing**:
- For shader changes → `material_set_shader` (this module)
- For texture tiling → `material_set_texture_scale` / `material_set_texture_offset`
- Pipeline-specific property names differ: check Render Pipeline Compatibility table in this doc

> ⚠️ **Targeting a GameObject edits the material asset, not that one object.** There is no per-object material instance here: every `material_set_*` resolves a GameObject to `renderer.sharedMaterial` and writes **the asset on disk** — an asset mutation, not a scene edit, whatever the individual skill's declared flags say. A freshly created primitive carries Unity's built-in `Default-Material`, so `material_set_color(name="Cube")` recolours *every* object in the project still using it — the classic "I coloured one cube and the whole scene changed" bug. Target by GameObject name only after confirming that object owns a material nobody else shares (`material_get_properties` → `materialPath` names the asset that would be written).

**Safe route for "make this object red"** — create, colour the asset by path, then assign:

```python
unity_skills.call_skill("material_create", name="Red", savePath="Assets/Materials")
unity_skills.call_skill("material_set_color", path="Assets/Materials/Red.mat", r=1, g=0, b=0)
unity_skills.call_skill("material_assign", name="Cube", materialPath="Assets/Materials/Red.mat")
```

> **Object Targeting**: Most single-object skills accept `name` (GameObject name) **or** `path`. Behaviour of `path`:
> - In `material_set_*` / `material_get_*` (color/emission/texture/float/int/vector/keyword/shader/render_queue/gi_flags/properties), `path` may be either a **GameObject hierarchy path** *or* a **material asset path** like `Assets/Materials/X.mat` — the skill auto-detects (paths starting with `Assets/` or ending with `.mat` are treated as material assets).
> - In `material_assign`, `path` is a **GameObject hierarchy path only**; the material to assign goes in the separate `materialPath` parameter.

## Skills Overview

| Single Object | Batch Version | Use Batch When |
|---------------|---------------|----------------|
| `material_create` | `material_create_batch` | Creating 2+ materials |
| `material_assign` | `material_assign_batch` | Assigning to 2+ objects |
| `material_set_color` | `material_set_colors_batch` | Setting colors on 2+ objects |
| `material_set_emission` | `material_set_emission_batch` | Setting emission on 2+ objects |

**No batch needed**:
- `material_set_texture` - Set texture
- `material_set_texture_offset` / `material_set_texture_scale` - Texture tiling
- `material_set_float` / `material_set_int` / `material_set_vector` - Set properties
- `material_set_keyword` - Enable/disable shader keywords
- `material_set_render_queue` - Set render queue
- `material_set_shader` - Change shader
- `material_set_gi_flags` - Set global illumination flags
- `material_get_properties` / `material_get_keywords` - Query properties
- `material_duplicate` - Duplicate material

---

## Skills

### material_create
Create a new material (auto-detects render pipeline).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | Yes | - | Material name |
| `shaderName` | string | No | auto-detect | Shader (auto-detects URP/HDRP/Standard) |
| `savePath` | string | No | null | Save path (folder or full path) |

### material_create_batch
Create multiple materials.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects (see example below) |


**Returns**: `{success, totalItems, successCount, failCount, results: [{success, name, path}]}`

```python
unity_skills.call_skill("material_create_batch", items=[
    {"name": "Red", "savePath": "Assets/Materials"},
    {"name": "Blue", "savePath": "Assets/Materials"},
    {"name": "Green", "savePath": "Assets/Materials"}
])
```

### material_assign
Assign material to object's renderer.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | GameObject hierarchy path |
| `materialPath` | string | Yes | Material asset to assign (e.g. `Assets/Materials/X.mat`) |

### material_assign_batch
Assign materials to multiple objects.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects (see example below) |


**Returns**: `{success, totalItems, successCount, failCount, results: [{success, gameObject, material}]}` — each item echoes `gameObject` (the object name) and `material` (the assigned asset path); failures come back as `{error, target}`.

```python
unity_skills.call_skill("material_assign_batch", items=[
    {"name": "Cube1", "materialPath": "Assets/Materials/Red.mat"},
    {"name": "Cube2", "materialPath": "Assets/Materials/Blue.mat"}
])
```

### material_set_color
Set material color with optional HDR intensity.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | - | GameObject name |
| `instanceId` | int | No* | 0 | GameObject instance ID |
| `path` | string | No* | - | Material asset path |
| `r`, `g`, `b` | float | No | 1 | Color (0-1) |
| `a` | float | No | 1 | Alpha |
| `propertyName` | string | No | auto-detect | Color property |
| `intensity` | float | No | 1.0 | HDR intensity (>1 for bloom) |

**Returns**: `{success, target, color, intensity, propertyUsed, hdrEnabled}`. Auto-detection tries the pipeline's property first, then `_BaseColor`, `_Color`, `_TintColor`, `_EmissionColor`, and **`propertyUsed` reports which one it actually wrote** — check it before concluding a colour "didn't apply".

### material_set_colors_batch
Set colors on multiple objects. Each item accepts: identifier (`name`/`instanceId`/`path`) + `r`, `g`, `b`, `a`, optional per-item `propertyName`.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of `{name\|instanceId\|path, r, g, b, a}` per-item objects (see example below) |
| `propertyName` | string | No | auto-detect | Default color property applied to all items unless overridden |


**Returns**: `{success, totalItems, successCount, failCount, results: [{target, success}]}` — the per-item key is `target` (GameObject name, or the path when you addressed the asset), not `name`; failures come back as `{error, target}`.

```python
unity_skills.call_skill("material_set_colors_batch", items=[
    {"name": "Cube1", "r": 1, "g": 0, "b": 0},
    {"name": "Cube2", "r": 0, "g": 1, "b": 0},
    {"name": "Cube3", "r": 0, "g": 0, "b": 1}
])
```

### material_set_emission
Set emission color with auto-enable keyword.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | - | GameObject name |
| `instanceId` | int | No* | 0 | GameObject instance ID |
| `path` | string | No* | - | Material asset path |
| `r`, `g`, `b` | float | No | 1 | Emission color (0-1) |
| `intensity` | float | No | 1.0 | HDR intensity (>1 for bloom) |
| `enableEmission` | bool | No | true | Auto-enable _EMISSION keyword |

### material_set_emission_batch
Set emission on multiple objects.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects (see example below) |


**Returns**: `{success, totalItems, successCount, failCount, results: [{success, name}]}`

```python
unity_skills.call_skill("material_set_emission_batch", items=[
    {"name": "Neon1", "r": 1, "g": 0, "b": 1, "intensity": 5.0},
    {"name": "Neon2", "r": 0, "g": 1, "b": 1, "intensity": 5.0}
])
```

### material_set_texture
Set material texture.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | - | GameObject name |
| `instanceId` | int | No* | 0 | GameObject instance ID |
| `path` | string | No* | - | Material asset path |
| `texturePath` | string | Yes | - | Texture asset path |
| `propertyName` | string | No | auto-detect | Texture property |

### material_set_float
Set a float property on a material.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `instanceId` | int | No* | GameObject instance ID |
| `path` | string | No* | Material asset path |
| `propertyName` | string | Yes | Property name |
| `value` | float | Yes | Value |

### material_set_int
Set an integer property on a material.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `instanceId` | int | No* | GameObject instance ID |
| `path` | string | No* | Material asset path |
| `propertyName` | string | Yes | Property name |
| `value` | int | Yes | Value |

### material_set_keyword
Enable/disable shader keywords.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | - | GameObject name |
| `instanceId` | int | No* | 0 | GameObject instance ID |
| `path` | string | No* | - | Material asset path |
| `keyword` | string | Yes | - | Keyword name |
| `enable` | bool | No | true | Enable or disable |

**Common Keywords**: `_EMISSION`, `_NORMALMAP`, `_METALLICGLOSSMAP`, `_ALPHATEST_ON`, `_ALPHABLEND_ON`

### material_get_properties
Get all material properties.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `instanceId` | int | No* | GameObject instance ID |
| `path` | string | No* | Material asset path |

**Returns**: `{success, target, materialPath, shader, renderQueue, keywords, giFlags, properties: {...}}`

`properties` is a **dictionary of five typed groups**, not a flat list: `colors`, `floats`, `vectors`, `textures`, `integers`. Each group is an array of `{name, description, value}` (floats also carry `min`/`max`; a texture's `value` is the texture's name or null). So a property lookup is `properties.colors[i].name`, never `properties[i]`.

`materialPath` is the asset path of the material actually inspected, and it is only ever a path you can feed straight back in. It is `""` for a built-in material, and also `""` for a material **embedded in another asset** such as an `.fbx`: that material does have a containing file, but the file's main asset is a GameObject rather than a Material, so handing the `.fbx` path back would fail with "material not found". An empty `materialPath` therefore means "this material cannot be addressed by path" — resolve it through its GameObject instead, and do not synthesise a path from the model's. Read it whenever you resolved the material through a GameObject: a non-empty value is the file a subsequent `material_set_*` on that object would write.

### material_get_keywords
Get all enabled shader keywords on a material.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `instanceId` | int | No* | GameObject instance ID |
| `path` | string | No* | Material asset path |

**Returns**: `{success, target, materialPath, shader, enabledKeywords, commonKeywordStatus}` — `materialPath` is the resolved asset path of the material actually inspected (same guarantee as `material_get_properties`: `""` for a built-in material, and `""` for one embedded in another asset such as an `.fbx`, because that container's path is not feedable back in), and `commonKeywordStatus` is a `{keyword, enabled}` array covering the 14 usual suspects, so an absent keyword is distinguishable from an unchecked one.

### material_duplicate
Duplicate a material asset.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `sourcePath` | string | Yes | Source material path |
| `newName` | string | Yes | Name for the duplicated material |
| `savePath` | string | No | Optional folder/path override for the duplicated material |

### material_set_shader
Change the shader of a material.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `instanceId` | int | No* | GameObject instance ID |
| `path` | string | No* | Material asset path |
| `shaderName` | string | Yes | Shader name |

### material_set_vector
Set a Vector4 property on a material.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `instanceId` | int | No* | GameObject instance ID |
| `path` | string | No* | Material asset path |
| `propertyName` | string | Yes | Property name |
| `x`, `y`, `z`, `w` | float | Yes | Vector components |

### material_set_texture_offset
Set texture offset (tiling position).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `instanceId` | int | No* | GameObject instance ID |
| `path` | string | No* | Material asset path |
| `propertyName` | string | No | Texture property name |
| `x`, `y` | float | Yes | Offset values |

### material_set_texture_scale
Set texture scale (tiling).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `instanceId` | int | No* | GameObject instance ID |
| `path` | string | No* | Material asset path |
| `propertyName` | string | No | Texture property name |
| `x`, `y` | float | Yes | Scale values |

### material_set_render_queue
Set material render queue.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `instanceId` | int | No* | GameObject instance ID |
| `path` | string | No* | Material asset path |
| `renderQueue` | int | Yes | Render queue value |

### material_set_gi_flags
Set material global illumination flags.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | - | GameObject name |
| `instanceId` | int | No* | 0 | GameObject instance ID |
| `path` | string | No* | - | GameObject hierarchy path or material asset path |
| `flags` | string | No | `RealtimeEmissive` | GI flags: `None` / `RealtimeEmissive` / `BakedEmissive` / `EmissiveIsBlack` / `AnyEmissive` |

---

## Example: Efficient Material Setup

```python
import unity_skills

# BAD: 6 API calls
unity_skills.call_skill("material_create", name="Mat1", savePath="Assets/Materials")
unity_skills.call_skill("material_create", name="Mat2", savePath="Assets/Materials")
unity_skills.call_skill("material_set_color", path="Assets/Materials/Mat1.mat", r=1, g=0, b=0)
unity_skills.call_skill("material_set_color", path="Assets/Materials/Mat2.mat", r=0, g=0, b=1)
unity_skills.call_skill("material_assign", name="Cube1", materialPath="Assets/Materials/Mat1.mat")
unity_skills.call_skill("material_assign", name="Cube2", materialPath="Assets/Materials/Mat2.mat")

# GOOD: 3 API calls
unity_skills.call_skill("material_create_batch", items=[
    {"name": "Mat1", "savePath": "Assets/Materials"},
    {"name": "Mat2", "savePath": "Assets/Materials"}
])
unity_skills.call_skill("material_set_colors_batch", items=[
    {"path": "Assets/Materials/Mat1.mat", "r": 1, "g": 0, "b": 0},
    {"path": "Assets/Materials/Mat2.mat", "r": 0, "g": 0, "b": 1}
])
unity_skills.call_skill("material_assign_batch", items=[
    {"name": "Cube1", "materialPath": "Assets/Materials/Mat1.mat"},
    {"name": "Cube2", "materialPath": "Assets/Materials/Mat2.mat"}
])
```

## Render Pipeline Compatibility

Skills auto-detect and adapt to your render pipeline:

| Pipeline | Default Shader | Color Property | Texture Property |
|----------|---------------|----------------|------------------|
| Built-in | Standard | `_Color` | `_MainTex` |
| URP | Universal Render Pipeline/Lit | `_BaseColor` | `_BaseMap` |
| HDRP | HDRP/Lit | `_BaseColor` | `_BaseColorMap` |

> **URP Lit exposes both `_BaseColor` and a legacy `_Color`, and only `_BaseColor` is authoritative.** `material_get_properties` lists both, but writing `_BaseColor` does **not** update the `_Color` entry — a verification pass that reads `_Color` will report the old colour and look like a failed write. Compare against `_BaseColor`, and trust the setter's `propertyUsed` field over your own guess about which property was involved. Only pass `propertyName` explicitly when auto-detection picked the wrong one.

## Best Practices

1. Save materials as assets for reuse
2. Write by asset path (`path="Assets/.../X.mat"`): it states exactly which asset changes
3. Targeting by GameObject name writes that renderer's **shared** material asset — see the warning at the top of this file; it is not a per-object override
4. Check shader property names in Unity Inspector, or read them from `material_get_properties`
5. URP/HDRP have different property names than Standard

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.

## Common Errors

Full transport-level codes (COMPILING/RATE_LIMIT etc.) → ../../references/protocol-error-codes.md

| Error | Trigger | Fix |
|---|---|---|
| `TARGET_NOT_FOUND` | The material asset, GameObject/renderer, shader, texture, or property could not be found (e.g., `Material asset not found`, `No Renderer component found`, `Shader not found`). | Verify the asset path with `asset_find`, the object with `gameobject_find`, or inspect available properties with `material_get_properties`. |
| `MISSING_PARAM` | A required parameter is missing, such as `materialPath`, `sourcePath`, `texturePath`, `propertyName`, `keyword`, or `shaderName`. | Supply the parameter named in the error and retry; use `mode=dryRun` for the full schema. |
| `SEMANTIC_INVALID` | An invalid value was supplied, such as an unrecognized GI flag, an invalid asset path, or a property name the shader does not use. | Correct the value using the allowed range/enum/path convention described in the error. |
