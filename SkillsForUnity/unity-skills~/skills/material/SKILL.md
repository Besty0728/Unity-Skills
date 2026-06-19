---
name: unity-material
description: Edit Unity material and shader properties across Built-in/URP/HDRP — colors, textures, floats, keywords, render queue, batch-apply. Use when changing how a surface looks, tweaking material parameters, or swapping shaders. 编辑材质与 Shader 属性(Built-in/URP/HDRP:颜色、贴图、浮点值、关键字、渲染队列、批量应用);当用户要调整物体外观、改材质参数或切换 Shader 时使用。
---

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
- `material_set_texture_offset/scale` - Texture tiling
- `material_set_float/int/vector` - Set properties
- `material_set_keyword` - Enable/disable shader keywords
- `material_set_render_queue` - Set render queue
- `material_set_shader` - Change shader
- `material_get_properties/keywords` - Query properties
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


**Returns**: `{success, totalItems, successCount, failCount, results: [{success, name, materialPath}]}`

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
| `name` | string | No* | GameObject name |
| `path` | string | No* | Material asset path |
| `r`, `g`, `b` | float | No | 1 | Color (0-1) |
| `a` | float | No | 1 | Alpha |
| `propertyName` | string | No | auto-detect | Color property |
| `intensity` | float | No | 1.0 | HDR intensity (>1 for bloom) |

### material_set_colors_batch
Set colors on multiple objects. Each item accepts: identifier (`name`/`instanceId`/`path`) + `r`, `g`, `b`, `a`, optional per-item `propertyName`.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of `{name|instanceId|path, r, g, b, a}` per-item objects (see example below) |
| `propertyName` | string | No | auto-detect | Default color property applied to all items unless overridden |


**Returns**: `{success, totalItems, successCount, failCount, results: [{success, name}]}`

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
| `name` | string | No* | GameObject name |
| `path` | string | No* | Material asset path |
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
| `name` | string | No* | GameObject name |
| `path` | string | No* | Material asset path |
| `texturePath` | string | Yes | - | Texture asset path |
| `propertyName` | string | No | auto-detect | Texture property |

### material_set_float
Set a float property on a material.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `path` | string | No* | Material asset path |
| `propertyName` | string | Yes | Property name |
| `value` | float | Yes | Value |

### material_set_int
Set an integer property on a material.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `path` | string | No* | Material asset path |
| `propertyName` | string | Yes | Property name |
| `value` | int | Yes | Value |

### material_set_keyword
Enable/disable shader keywords.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | GameObject name |
| `path` | string | No* | Material asset path |
| `keyword` | string | Yes | - | Keyword name |
| `enable` | bool | No | true | Enable or disable |

**Common Keywords**: `_EMISSION`, `_NORMALMAP`, `_METALLICGLOSSMAP`, `_ALPHATEST_ON`, `_ALPHABLEND_ON`

### material_get_properties
Get all material properties.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `path` | string | No* | Material asset path |

**Returns**: `{success, target, shader, renderQueue, keywords, giFlags, properties: {colors, floats, vectors, textures, integers}}`

### material_get_keywords
Get all enabled shader keywords on a material.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `path` | string | No* | Material asset path |

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
| `path` | string | No* | Material asset path |
| `shaderName` | string | Yes | Shader name |

### material_set_vector
Set a Vector4 property on a material.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `path` | string | No* | Material asset path |
| `propertyName` | string | Yes | Property name |
| `x`, `y`, `z`, `w` | float | Yes | Vector components |

### material_set_texture_offset
Set texture offset (tiling position).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `path` | string | No* | Material asset path |
| `propertyName` | string | No | Texture property name |
| `x`, `y` | float | Yes | Offset values |

### material_set_texture_scale
Set texture scale (tiling).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `path` | string | No* | Material asset path |
| `propertyName` | string | No | Texture property name |
| `x`, `y` | float | Yes | Scale values |

### material_set_render_queue
Set material render queue.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `path` | string | No* | Material asset path |
| `renderQueue` | int | Yes | Render queue value |

### material_set_gi_flags
Set material global illumination flags.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `path` | string | No* | GameObject hierarchy path or material asset path |
| `flags` | string | Yes | GI flags: `None` / `RealtimeEmissive` / `BakedEmissive` / `EmissiveIsBlack` / `AnyEmissive` (default `RealtimeEmissive` if omitted in code; required here)

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

## Best Practices

1. Save materials as assets for reuse
2. Use material instances (by name) for runtime changes
3. Use material assets (by path) for persistent changes
4. Check shader property names in Unity Inspector
5. URP/HDRP have different property names than Standard

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
