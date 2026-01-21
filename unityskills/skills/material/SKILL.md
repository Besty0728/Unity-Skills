---
name: unity-material
description: Create and configure materials in Unity Editor via REST API with full HDR, emission, and keyword support
---

# Unity Material Skills

Control materials - define how objects look with colors, textures, shaders, and advanced properties like HDR emission and shader keywords.

## Capabilities

- Create new materials with auto render pipeline detection
- Set material colors with HDR intensity support
- Configure emission with auto-enable keywords
- Assign textures with tiling and offset
- Control shader keywords (_EMISSION, _NORMALMAP, etc.)
- Set float/int/vector properties
- Configure render queue and GI flags
- Query all material properties
- Duplicate existing materials

## Skills Reference

| Skill | Description |
|-------|-------------|
| `material_create` | Create a new material (auto-detects render pipeline) |
| `material_duplicate` | Duplicate an existing material |
| `material_assign` | Assign material to renderer |
| `material_set_color` | Set material color with optional HDR intensity |
| `material_set_emission` | Set emission color with auto-enable |
| `material_set_texture` | Set material texture |
| `material_set_texture_offset` | Set texture offset (UV position) |
| `material_set_texture_scale` | Set texture scale (tiling) |
| `material_set_float` | Set float property |
| `material_set_int` | Set integer property |
| `material_set_vector` | Set vector4 property |
| `material_set_keyword` | Enable/disable shader keyword |
| `material_set_render_queue` | Set render queue |
| `material_set_shader` | Change material shader |
| `material_set_gi_flags` | Set global illumination flags |
| `material_get_properties` | Get all material properties |
| `material_get_keywords` | Get enabled shader keywords |

**Total: 17 Skills**

## Parameters

### material_create

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | Yes | - | Material name |
| `shaderName` | string | No | auto-detect | Shader to use (auto-detects URP/HDRP/Standard) |
| `savePath` | string | No | null | Asset save path (can be folder or full path) |

### material_set_color

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | - | GameObject name |
| `instanceId` | int | No* | 0 | GameObject instance ID |
| `path` | string | No* | - | Material asset path |
| `r` | float | No | 1 | Red (0-1) |
| `g` | float | No | 1 | Green (0-1) |
| `b` | float | No | 1 | Blue (0-1) |
| `a` | float | No | 1 | Alpha (0-1) |
| `propertyName` | string | No | auto-detect | Color property name |
| `intensity` | float | No | 1.0 | HDR intensity multiplier (>1 for bloom) |

*At least one identifier required

### material_set_emission

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | - | GameObject name |
| `instanceId` | int | No* | 0 | GameObject instance ID |
| `path` | string | No* | - | Material asset path |
| `r` | float | No | 1 | Red (0-1) |
| `g` | float | No | 1 | Green (0-1) |
| `b` | float | No | 1 | Blue (0-1) |
| `intensity` | float | No | 1.0 | HDR intensity (>1 for bloom effect) |
| `enableEmission` | bool | No | true | Auto-enable _EMISSION keyword |

### material_set_keyword

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | - | GameObject name |
| `instanceId` | int | No* | 0 | GameObject instance ID |
| `path` | string | No* | - | Material asset path |
| `keyword` | string | Yes | - | Keyword name (e.g., "_EMISSION") |
| `enable` | bool | No | true | Enable or disable |

### material_set_render_queue

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | - | GameObject name |
| `path` | string | No* | - | Material asset path |
| `renderQueue` | int | No | -1 | Queue value (-1=shader default) |

**Render Queue Values:**
- 1000: Background
- 2000: Geometry (default opaque)
- 2450: AlphaTest
- 3000: Transparent
- 4000: Overlay

### material_get_properties

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `path` | string | No* | Material asset path |

Returns all colors, floats, vectors, textures, integers, and keywords.

## Common Shader Keywords

| Keyword | Description |
|---------|-------------|
| `_EMISSION` | Enable emission |
| `_NORMALMAP` | Enable normal mapping |
| `_METALLICGLOSSMAP` | Use metallic texture |
| `_SPECGLOSSMAP` | Use specular texture |
| `_ALPHATEST_ON` | Alpha cutout mode |
| `_ALPHABLEND_ON` | Alpha blend mode |
| `_PARALLAXMAP` | Enable parallax mapping |

## Example Usage

### Basic Material Creation

```python
import unity_skills

# Create a red metallic material
unity_skills.call_skill("material_create",
    name="RedMetal",
    savePath="Assets/Materials"  # Can be just a folder!
)

unity_skills.call_skill("material_set_color",
    path="Assets/Materials/RedMetal.mat",
    r=1, g=0.2, b=0.2
)

unity_skills.call_skill("material_set_float",
    path="Assets/Materials/RedMetal.mat",
    propertyName="_Metallic",
    value=0.9
)
```

### HDR Emission (Glowing Effect)

```python
# Create a glowing material
unity_skills.call_skill("material_create",
    name="GlowingMat",
    savePath="Assets/Materials/GlowingMat.mat"
)

# Method 1: Using material_set_emission (recommended)
unity_skills.call_skill("material_set_emission",
    path="Assets/Materials/GlowingMat.mat",
    r=0, g=1, b=0.5,        # Cyan-ish green
    intensity=3.0,           # HDR intensity for bloom
    enableEmission=True      # Auto-enables _EMISSION keyword
)

# Method 2: Using material_set_color with intensity
unity_skills.call_skill("material_set_color",
    path="Assets/Materials/GlowingMat.mat",
    r=1, g=0.5, b=0,
    propertyName="_EmissionColor",
    intensity=5.0  # Creates HDR color, auto-enables emission
)
```

### Manual Keyword Control

```python
# Enable emission manually
unity_skills.call_skill("material_set_keyword",
    path="Assets/Materials/MyMat.mat",
    keyword="_EMISSION",
    enable=True
)

# Enable normal mapping
unity_skills.call_skill("material_set_keyword",
    name="MyCube",
    keyword="_NORMALMAP",
    enable=True
)
```

### Texture Tiling

```python
# Set texture with custom tiling
unity_skills.call_skill("material_set_texture",
    path="Assets/Materials/MyMat.mat",
    texturePath="Assets/Textures/brick.png"
)

unity_skills.call_skill("material_set_texture_scale",
    path="Assets/Materials/MyMat.mat",
    x=4, y=4  # Tile 4x4
)

unity_skills.call_skill("material_set_texture_offset",
    path="Assets/Materials/MyMat.mat",
    x=0.25, y=0  # Offset by 25%
)
```

### Query Material Properties

```python
# Get all properties of a material
result = unity_skills.call_skill("material_get_properties",
    path="Assets/Materials/MyMat.mat"
)
# Returns: colors, floats, vectors, textures, keywords, renderQueue, etc.

# Get just keywords
keywords = unity_skills.call_skill("material_get_keywords",
    name="MyCube"
)
```

### Duplicate Material

```python
# Create a variant of an existing material
unity_skills.call_skill("material_duplicate",
    sourcePath="Assets/Materials/BaseMaterial.mat",
    newName="VariantMaterial",
    savePath="Assets/Materials/Variants"
)
```

## Response Format

```json
{
  "status": "success",
  "skill": "material_set_emission",
  "result": {
    "success": true,
    "target": "Assets/Materials/GlowMat.mat",
    "emissionColor": { "r": 0, "g": 1, "b": 0.5 },
    "intensity": 3.0,
    "hdrColor": { "r": 0, "g": 3.0, "b": 1.5 },
    "emissionEnabled": true
  }
}
```

## Render Pipeline Compatibility

The skills auto-detect and adapt to your render pipeline:

| Pipeline | Default Shader | Color Property | Texture Property |
|----------|---------------|----------------|------------------|
| Built-in | Standard | `_Color` | `_MainTex` |
| URP | Universal Render Pipeline/Lit | `_BaseColor` | `_BaseMap` |
| HDRP | HDRP/Lit | `_BaseColor` | `_BaseColorMap` |
```

## Best Practices

1. Save materials as assets for reuse
2. Use material instances (by name) for runtime changes
3. Use material assets (by path) for persistent changes
4. Check shader property names in Unity Inspector
5. URP/HDRP have different property names than Standard
