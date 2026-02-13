---
name: unity-material
description: "Unity material and shader properties. Use when users want to create materials, set colors, textures, emission, or shader properties. Triggers: material, shader, color, texture, emission, albedo, metallic, smoothness, 材质, 颜色, 纹理, 发光."
---

# Unity Material Skills

> **BATCH-FIRST**: Use `*_batch` skills when operating on 2+ objects/materials.

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
| `path` | string | No* | Material asset path (for asset) |
| `materialPath` | string | Yes | Material to assign |

### material_assign_batch
Assign materials to multiple objects.

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
Set colors on multiple objects.

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

**Returns**: `{colors, floats, vectors, textures, integers, keywords, renderQueue}`

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
| `destPath` | string | Yes | Destination path |

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
| `queue` | int | Yes | Render queue value |

### material_set_gi_flags
Set material global illumination flags.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `path` | string | No* | Material asset path |
| `flags` | string | Yes | GI flags (None/Emissive/RealtimeEmissive/BakedEmissive)

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
