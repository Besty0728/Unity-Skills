---
name: unity-importer
description: "Asset import settings. Use when users want to configure texture, audio, or model import settings. Triggers: import settings, texture settings, audio settings, model settings, compression, max size, 导入设置, 纹理设置, Unity压缩."
---

# Unity Importer Skills

> **BATCH-FIRST**: Use `*_batch` skills when configuring 2+ assets.

## Guardrails

**Mode**: Full-Auto required

**DO NOT** (common hallucinations):
- `importer_import` does not exist → use `asset_import` (asset module) to import files; this module configures import *settings*
- `importer_set_format` does not exist → use specific skills: `importer_set_texture`, `importer_set_audio`, `importer_set_model`
- `importer_get_settings` does not exist → use specific skills: `importer_get_texture`, `importer_get_audio`, etc.
- After changing import settings, Unity may need `asset_reimport` to apply changes

**Routing**:
- For file import (copy into project) → use `asset` module's `asset_import`
- For texture/model/audio settings → use specific importer skills in this module

## Skills Overview

| Single Object | Batch Version | Use Batch When |
|---------------|---------------|----------------|
| `texture_set_settings` | `texture_set_settings_batch` | Configuring 2+ textures |
| `audio_set_settings` | `audio_set_settings_batch` | Configuring 2+ audio files |
| `model_set_settings` | `model_set_settings_batch` | Configuring 2+ models |

**Alternative Skills**:
- `texture_set_import_settings` - Set texture import settings (alternative API)
- `model_set_import_settings` - Set model import settings (alternative API)

**Query Skills** (no batch needed):
- `texture_get_settings` - Get texture import settings
- `audio_get_settings` - Get audio import settings
- `model_get_settings` - Get model import settings

---

## Texture Skills

### texture_get_settings
Get texture import settings.

### texture_set_settings
Set texture import settings.

### texture_set_settings_batch
Set texture import settings for multiple textures.

**Returns**: `{success, count, results: [{success, assetPath}]}`

### texture_set_import_settings
Set texture import settings (alternative API).

| Parameter | Type | Description |
|-----------|------|-------------|
| `assetPath` | string | Path like `Assets/Textures/icon.png` |
| `textureType` | string | Default, NormalMap, Sprite, EditorGUI, Cursor, Cookie, Lightmap, SingleChannel |
| `maxSize` | int | 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192 |
| `filterMode` | string | Point, Bilinear, Trilinear |
| `compression` | string | None, LowQuality, Normal, HighQuality |
| `mipmapEnabled` | bool | Generate mipmaps |
| `sRGB` | bool | sRGB color space |
| `readable` | bool | CPU readable (for GetPixel) |
| `spritePixelsPerUnit` | float | Pixels per unit for Sprite type |
| `wrapMode` | string | Repeat, Clamp, Mirror, MirrorOnce |

```python
# Single - convert to sprite
unity_skills.call_skill("texture_set_settings",
    assetPath="Assets/Textures/ui_button.png",
    textureType="Sprite",
    spritePixelsPerUnit=100,
    filterMode="Bilinear"
)

# Batch - convert multiple to sprites
unity_skills.call_skill("texture_set_settings_batch", items=[
    {"assetPath": "Assets/Textures/icon1.png", "textureType": "Sprite"},
    {"assetPath": "Assets/Textures/icon2.png", "textureType": "Sprite"},
    {"assetPath": "Assets/Textures/icon3.png", "textureType": "Sprite"}
])
```

---

## Audio Skills

### audio_get_settings
Get audio import settings.

### audio_set_settings
Set audio import settings.

### audio_set_settings_batch
Set audio import settings for multiple audio files.

**Returns**: `{success, count, results: [{success, assetPath}]}`

| Parameter | Type | Description |
|-----------|------|-------------|
| `assetPath` | string | Path like `Assets/Audio/bgm.mp3` |
| `forceToMono` | bool | Force to mono channel |
| `loadInBackground` | bool | Load in background thread |
| `preloadAudioData` | bool | Preload on scene load |
| `loadType` | string | DecompressOnLoad, CompressedInMemory, Streaming |
| `compressionFormat` | string | PCM, Vorbis, ADPCM |
| `quality` | float | 0.0 ~ 1.0 (Vorbis quality) |

```python
# BGM - use streaming for memory efficiency
unity_skills.call_skill("audio_set_settings",
    assetPath="Assets/Audio/bgm.mp3",
    loadType="Streaming",
    compressionFormat="Vorbis",
    quality=0.7
)

# SFX - decompress for low latency
unity_skills.call_skill("audio_set_settings",
    assetPath="Assets/Audio/sfx_hit.wav",
    loadType="DecompressOnLoad",
    forceToMono=True
)

# Batch
unity_skills.call_skill("audio_set_settings_batch", items=[
    {"assetPath": "Assets/Audio/sfx1.wav", "loadType": "DecompressOnLoad"},
    {"assetPath": "Assets/Audio/sfx2.wav", "loadType": "DecompressOnLoad"}
])
```

---

## Model Skills

### model_get_settings
Get model import settings.

### model_set_settings
Set model import settings.

### model_set_settings_batch
Set model import settings for multiple models.

**Returns**: `{success, count, results: [{success, assetPath}]}`

### model_set_import_settings
Set model import settings (alternative API).

| Parameter | Type | Description |
|-----------|------|-------------|
| `assetPath` | string | Path like `Assets/Models/char.fbx` |
| `globalScale` | float | Import scale factor |
| `meshCompression` | string | Off, Low, Medium, High |
| `isReadable` | bool | CPU readable mesh data |
| `generateSecondaryUV` | bool | Generate lightmap UVs |
| `importBlendShapes` | bool | Import blend shapes |
| `importCameras` | bool | Import cameras |
| `importLights` | bool | Import lights |
| `animationType` | string | None, Legacy, Generic, Humanoid |
| `importAnimation` | bool | Import animations |
| `materialImportMode` | string | None, ImportViaMaterialDescription, ImportStandard |

```python
# Character with humanoid animation
unity_skills.call_skill("model_set_settings",
    assetPath="Assets/Models/character.fbx",
    animationType="Humanoid",
    meshCompression="Medium",
    generateSecondaryUV=True
)

# Static prop - optimize
unity_skills.call_skill("model_set_settings",
    assetPath="Assets/Models/prop_barrel.fbx",
    animationType="None",
    importAnimation=False,
    importCameras=False,
    importLights=False,
    meshCompression="High"
)

# Batch
unity_skills.call_skill("model_set_settings_batch", items=[
    {"assetPath": "Assets/Models/prop1.fbx", "animationType": "None", "meshCompression": "High"},
    {"assetPath": "Assets/Models/prop2.fbx", "animationType": "None", "meshCompression": "High"}
])
```

---

## Example: Efficient Asset Configuration

```python
import unity_skills

# BAD: 5 API calls
unity_skills.call_skill("texture_set_settings", assetPath="Assets/UI/btn1.png", textureType="Sprite")
unity_skills.call_skill("texture_set_settings", assetPath="Assets/UI/btn2.png", textureType="Sprite")
unity_skills.call_skill("texture_set_settings", assetPath="Assets/UI/btn3.png", textureType="Sprite")
unity_skills.call_skill("texture_set_settings", assetPath="Assets/UI/btn4.png", textureType="Sprite")
unity_skills.call_skill("texture_set_settings", assetPath="Assets/UI/btn5.png", textureType="Sprite")

# GOOD: 1 API call
unity_skills.call_skill("texture_set_settings_batch", items=[
    {"assetPath": f"Assets/UI/btn{i}.png", "textureType": "Sprite", "mipmapEnabled": False}
    for i in range(1, 6)
])
```

## Best Practices

### Textures
- Use `Sprite` type for UI images
- Disable mipmaps for UI textures to save memory
- Use `Point` filter for pixel art
- Set `readable=false` unless you need CPU access

### Audio
- Use `Streaming` for long BGM tracks
- Use `DecompressOnLoad` for short SFX
- Use `Vorbis` compression with quality 0.5-0.7 for good balance

### Models
- Use `Humanoid` animation type for characters with retargeting
- Disable unused imports (cameras, lights) for props
- Enable `generateSecondaryUV` for static objects using baked lighting

---

## Audio Skills (Extended)

### `audio_find_clips`
Search for AudioClip assets in the project.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| filter | string | No | `""` | Search filter string |
| limit | int | No | `50` | Maximum number of results |

**Returns:** `{ success, totalFound, showing, clips[] { path, name, length } }`

### `audio_get_clip_info`
Get detailed information about an AudioClip asset.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Path like `Assets/Audio/bgm.mp3` |

**Returns:** `{ success, name, path, length, channels, frequency, samples, loadType, loadState, ambisonic }`

### `audio_add_source`
Add an AudioSource component to a GameObject.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | `null` | GameObject name |
| instanceId | int | No | `0` | GameObject instance ID |
| path | string | No | `null` | GameObject hierarchy path |
| clipPath | string | No | `null` | AudioClip asset path to assign |
| playOnAwake | bool | No | `false` | Play on awake |
| loop | bool | No | `false` | Loop playback |
| volume | float | No | `1` | Volume (0-1) |

**Returns:** `{ success, gameObject, instanceId }`

### `audio_get_source_info`
Get AudioSource configuration.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | `null` | GameObject name |
| instanceId | int | No | `0` | GameObject instance ID |
| path | string | No | `null` | GameObject hierarchy path |

**Returns:** `{ success, gameObject, clip, volume, pitch, loop, playOnAwake, mute, spatialBlend, minDistance, maxDistance, priority }`

### `audio_set_source_properties`
Set AudioSource properties.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | `null` | GameObject name |
| instanceId | int | No | `0` | GameObject instance ID |
| path | string | No | `null` | GameObject hierarchy path |
| clipPath | string | No | `null` | AudioClip asset path |
| volume | float | No | `null` | Volume (0-1) |
| pitch | float | No | `null` | Pitch |
| loop | bool | No | `null` | Loop playback |
| playOnAwake | bool | No | `null` | Play on awake |
| mute | bool | No | `null` | Mute |
| spatialBlend | float | No | `null` | 0=2D, 1=3D |
| priority | int | No | `null` | Priority (0-256) |

**Returns:** `{ success, gameObject }`

### `audio_find_sources_in_scene`
Find all AudioSource components in the current scene.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| limit | int | No | `50` | Maximum number of results |

**Returns:** `{ success, totalFound, showing, sources[] { gameObject, path, clip, volume, loop, enabled } }`

### `audio_create_mixer`
Create a new AudioMixer asset.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| mixerName | string | No | `"NewAudioMixer"` | Name for the mixer |
| folder | string | No | `"Assets"` | Folder to create in |

**Returns:** `{ success, path, name }`

---

## Model Skills (Extended)

### `model_find_assets`
Search for model assets in the project.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| filter | string | No | `""` | Search filter string |
| limit | int | No | `50` | Maximum number of results |

**Returns:** `{ success, totalFound, showing, models[] { path, name } }`

### `model_get_mesh_info`
Get detailed Mesh information (vertices, triangles, submeshes).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | `null` | GameObject name |
| instanceId | int | No | `0` | GameObject instance ID |
| path | string | No | `null` | GameObject hierarchy path |
| assetPath | string | No | `null` | Model asset path (alternative to name/instanceId/path) |

**Returns:** `{ success, name, vertexCount, triangles, subMeshCount, bounds { center, size }, hasNormals, hasTangents, hasUV, hasUV2, hasColors, blendShapeCount, isReadable }`

### `model_get_materials_info`
Get material mapping for a model asset.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Model asset path |

**Returns:** `{ success, path, materialCount, materials[] { name, shader }, meshCount, meshes[] { name, vertices, triangles } }`

### `model_get_animations_info`
Get animation clip information from a model asset.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Model asset path |

**Returns:** `{ success, path, importAnimation, clipCount, clips[] { name, length, frameRate, wrapMode, isLooping }, clipDefinitions[] { name, firstFrame, lastFrame, loop } }`

### `model_set_animation_clips`
Configure animation clip splitting.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Model asset path |
| clips | string | Yes | - | JSON array of `{name, firstFrame, lastFrame, loop}` |

**Returns:** `{ success, path, clipCount }`

### `model_get_rig_info`
Get rig/skeleton binding information.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Model asset path |

**Returns:** `{ success, path, animationType, avatarSetup, sourceAvatar, optimizeGameObjects, isHuman }`

### `model_set_rig`
Set rig/skeleton binding type.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Model asset path |
| animationType | string | Yes | - | None/Legacy/Generic/Humanoid |
| avatarSetup | string | No | `null` | Avatar setup mode |

**Returns:** `{ success, path, animationType }`

---

## Texture Skills (Extended)

### `texture_find_assets`
Search for texture assets in the project.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| filter | string | No | `""` | Search filter string |
| limit | int | No | `50` | Maximum number of results |

**Returns:** `{ success, totalFound, showing, textures[] { path, name, width, height } }`

### `texture_get_info`
Get detailed texture information (dimensions, format, memory).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Texture asset path |

**Returns:** `{ success, name, path, width, height, format, mipmapCount, isReadable, filterMode, wrapMode, memorySizeKB }`

### `texture_set_type`
Set texture type.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Texture asset path |
| textureType | string | Yes | - | Default/NormalMap/Sprite/EditorGUI/Cursor/Cookie/Lightmap/SingleChannel |

**Returns:** `{ success, path, textureType }`

### `texture_set_platform_settings`
Set platform-specific texture settings.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Texture asset path |
| platform | string | Yes | - | Standalone/iPhone/Android/WebGL |
| maxSize | int | No | `null` | Max texture size |
| format | string | No | `null` | TextureImporterFormat enum value |
| compressionQuality | int | No | `null` | Compression quality (0-100) |
| overridden | bool | No | `null` | Override platform settings (defaults to true when called) |

**Returns:** `{ success, path, platform, maxSize, format }`

### `texture_get_platform_settings`
Get platform-specific texture settings.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Texture asset path |
| platform | string | Yes | - | Standalone/iPhone/Android/WebGL |

**Returns:** `{ success, path, platform, overridden, maxTextureSize, format, compressionQuality }`

### `texture_set_sprite_settings`
Configure Sprite-specific settings (pixelsPerUnit, spriteMode).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Texture asset path |
| pixelsPerUnit | float | No | `null` | Pixels per unit |
| spriteMode | string | No | `null` | Single/Multiple/Polygon |

**Returns:** `{ success, path, pixelsPerUnit, spriteMode }`

### `texture_find_by_size`
Find textures by dimension range (minSize/maxSize in pixels).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| minSize | int | No | `0` | Minimum dimension in pixels |
| maxSize | int | No | `99999` | Maximum dimension in pixels |
| limit | int | No | `50` | Maximum number of results |

**Returns:** `{ success, count, textures[] { path, name, width, height } }`

---

## Asset Import Skills

### `asset_reimport`
Force reimport of an asset.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Asset path to reimport |

**Returns:** `{ success, reimported }`

### `asset_reimport_batch`
Reimport multiple assets matching a pattern.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| searchFilter | string | No | `"*"` | Search filter for FindAssets |
| folder | string | No | `"Assets"` | Folder to search in |
| limit | int | No | `100` | Maximum number of assets to reimport |

**Returns:** `{ success, count, assets[] }`

### `audio_set_import_settings`
Set audio clip import settings (alternative API from AssetImportSkills).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Audio asset path |
| forceToMono | bool | No | `null` | Force to mono channel |
| loadInBackground | bool | No | `null` | Load in background thread |
| loadType | string | No | `null` | DecompressOnLoad/CompressedInMemory/Streaming |
| compressionFormat | string | No | `null` | PCM/Vorbis/ADPCM |
| quality | int | No | `null` | Quality (0-100, divided by 100 internally) |

**Returns:** `{ success, assetPath, forceToMono, loadType, compressionFormat }`

### `sprite_set_import_settings`
Set sprite import settings (mode, pivot, packingTag, pixelsPerUnit).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Texture asset path |
| spriteMode | string | No | `null` | Single/Multiple/Polygon |
| pixelsPerUnit | float | No | `null` | Pixels per unit |
| packingTag | string | No | `null` | Sprite packing tag |
| pivotX | string | No | `null` | Pivot X (0-1) |
| pivotY | string | No | `null` | Pivot Y (0-1) |

**Returns:** `{ success, assetPath, spriteMode, pixelsPerUnit }`

### `texture_get_import_settings`
Get current texture import settings (alternative API from AssetImportSkills).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Texture asset path |

**Returns:** `{ success, assetPath, textureType, maxSize, compression, readable, mipmaps, spriteMode, pixelsPerUnit }`

### `model_get_import_settings`
Get current model import settings (alternative API from AssetImportSkills).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Model asset path |

**Returns:** `{ success, assetPath, globalScale, importAnimation, importMaterials, meshCompression, readable, generateColliders }`

### `audio_get_import_settings`
Get current audio import settings (alternative API from AssetImportSkills).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Audio asset path |

**Returns:** `{ success, assetPath, forceToMono, loadInBackground, loadType, compressionFormat, quality }`

### `asset_set_labels`
Set labels on an asset.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Asset path |
| labels | string | Yes | - | Comma-separated label list |

**Returns:** `{ success, assetPath, labels[] }`

### `asset_get_labels`
Get labels of an asset.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Asset path |

**Returns:** `{ success, assetPath, labels[] }`
