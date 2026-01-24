---
name: unity-importer
description: Get and set import settings for Textures, Audio, and 3D Models in Unity Editor via REST API
---

# Unity Importer Skills

Manage asset import settings for textures, audio files, and 3D models. Adjust compression, quality, animation types, and more.

## Capabilities

- Get/set texture import settings (type, size, compression, sprite mode)
- Get/set audio import settings (load type, compression, quality)
- Get/set model import settings (mesh compression, animation type, materials)
- Batch operations for efficient bulk processing

## Skills Reference

| Skill | Description |
|-------|-------------|
| `texture_get_settings` | Get texture import settings |
| `texture_set_settings` | Set texture import settings |
| `texture_set_settings_batch` | Batch set texture settings |
| `audio_get_settings` | Get audio import settings |
| `audio_set_settings` | Set audio import settings |
| `audio_set_settings_batch` | Batch set audio settings |
| `model_get_settings` | Get model import settings |
| `model_set_settings` | Set model import settings |
| `model_set_settings_batch` | Batch set model settings |

---

## Texture Parameters

### texture_get_settings / texture_set_settings

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `assetPath` | string | Yes | Path like `Assets/Textures/icon.png` |
| `textureType` | string | No | Default, NormalMap, Sprite, EditorGUI, Cursor, Cookie, Lightmap, SingleChannel |
| `maxSize` | int | No | 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192 |
| `filterMode` | string | No | Point, Bilinear, Trilinear |
| `compression` | string | No | None, LowQuality, Normal, HighQuality |
| `mipmapEnabled` | bool | No | Generate mipmaps |
| `sRGB` | bool | No | sRGB color space |
| `readable` | bool | No | CPU readable (for GetPixel) |
| `alphaIsTransparency` | bool | No | Treat alpha as transparency |
| `spritePixelsPerUnit` | float | No | Pixels per unit for Sprite type |
| `wrapMode` | string | No | Repeat, Clamp, Mirror, MirrorOnce |

---

## Audio Parameters

### audio_get_settings / audio_set_settings

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `assetPath` | string | Yes | Path like `Assets/Audio/bgm.mp3` |
| `forceToMono` | bool | No | Force to mono channel |
| `loadInBackground` | bool | No | Load in background thread |
| `preloadAudioData` | bool | No | Preload on scene load |
| `loadType` | string | No | DecompressOnLoad, CompressedInMemory, Streaming |
| `compressionFormat` | string | No | PCM, Vorbis, ADPCM |
| `quality` | float | No | 0.0 ~ 1.0 (Vorbis quality) |

---

## Model Parameters

### model_get_settings / model_set_settings

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `assetPath` | string | Yes | Path like `Assets/Models/char.fbx` |
| `globalScale` | float | No | Import scale factor |
| `meshCompression` | string | No | Off, Low, Medium, High |
| `isReadable` | bool | No | CPU readable mesh data |
| `generateSecondaryUV` | bool | No | Generate lightmap UVs |
| `importBlendShapes` | bool | No | Import blend shapes |
| `importCameras` | bool | No | Import cameras |
| `importLights` | bool | No | Import lights |
| `animationType` | string | No | None, Legacy, Generic, Humanoid |
| `importAnimation` | bool | No | Import animations |
| `materialImportMode` | string | No | None, ImportViaMaterialDescription, ImportStandard |

---

## Example Usage

```python
import unity_skills

# === Texture Examples ===

# Get current texture settings
settings = unity_skills.call_skill("texture_get_settings",
    assetPath="Assets/Textures/icon.png"
)
print(f"Type: {settings['result']['textureType']}")
print(f"Size: {settings['result']['maxTextureSize']}")

# Convert texture to Sprite with 100 pixels per unit
unity_skills.call_skill("texture_set_settings",
    assetPath="Assets/Textures/ui_button.png",
    textureType="Sprite",
    spritePixelsPerUnit=100,
    filterMode="Bilinear"
)

# Batch convert multiple textures to Sprite
unity_skills.call_skill("texture_set_settings_batch",
    items='[{"assetPath":"Assets/Textures/a.png","textureType":"Sprite"},{"assetPath":"Assets/Textures/b.png","textureType":"Sprite"}]'
)

# === Audio Examples ===

# Get audio settings
audio = unity_skills.call_skill("audio_get_settings",
    assetPath="Assets/Audio/music.mp3"
)
print(f"Load Type: {audio['result']['loadType']}")

# Set BGM to streaming for memory efficiency
unity_skills.call_skill("audio_set_settings",
    assetPath="Assets/Audio/bgm.mp3",
    loadType="Streaming",
    compressionFormat="Vorbis",
    quality=0.7
)

# Set SFX to decompress on load for low latency
unity_skills.call_skill("audio_set_settings",
    assetPath="Assets/Audio/sfx_hit.wav",
    loadType="DecompressOnLoad",
    forceToMono=True
)

# === Model Examples ===

# Get model settings
model = unity_skills.call_skill("model_get_settings",
    assetPath="Assets/Models/character.fbx"
)
print(f"Animation Type: {model['result']['animationType']}")

# Set up model for humanoid animation
unity_skills.call_skill("model_set_settings",
    assetPath="Assets/Models/character.fbx",
    animationType="Humanoid",
    meshCompression="Medium",
    generateSecondaryUV=True
)

# Optimize static props
unity_skills.call_skill("model_set_settings",
    assetPath="Assets/Models/prop_barrel.fbx",
    animationType="None",
    importAnimation=False,
    importCameras=False,
    importLights=False,
    meshCompression="High"
)
```

---

## Response Format

### texture_get_settings Response

```json
{
  "status": "success",
  "skill": "texture_get_settings",
  "result": {
    "success": true,
    "path": "Assets/Textures/icon.png",
    "textureType": "Sprite",
    "maxTextureSize": 2048,
    "filterMode": "Bilinear",
    "compression": "Normal",
    "mipmapEnabled": false,
    "sRGB": true,
    "spritePixelsPerUnit": 100
  }
}
```

### Batch Response

```json
{
  "status": "success",
  "skill": "texture_set_settings_batch",
  "result": {
    "success": true,
    "totalItems": 5,
    "successCount": 5,
    "failCount": 0,
    "results": [
      {"path": "Assets/Textures/a.png", "success": true},
      {"path": "Assets/Textures/b.png", "success": true}
    ]
  }
}
```

---

## Best Practices

1. **Textures**:
   - Use `Sprite` type for UI images
   - Disable mipmaps for UI textures to save memory
   - Use `Point` filter for pixel art
   - Set `readable=false` unless you need CPU access

2. **Audio**:
   - Use `Streaming` for long BGM tracks
   - Use `DecompressOnLoad` for short SFX
   - Use `Vorbis` compression with quality 0.5-0.7 for good balance

3. **Models**:
   - Use `Humanoid` animation type for characters with retargeting
   - Disable unused imports (cameras, lights) for props
   - Enable `generateSecondaryUV` for static objects using baked lighting
