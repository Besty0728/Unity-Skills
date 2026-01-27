---
name: unity-importer
description: "Get and set import settings for Textures, Audio, and Models. Use *_batch skills for 2+ assets."
---

# Unity Importer Skills

> **BATCH-FIRST**: Use `*_batch` skills when configuring 2+ assets.

## Skills Overview

| Single Object | Batch Version | Use Batch When |
|---------------|---------------|----------------|
| `texture_set_settings` | `texture_set_settings_batch` | Configuring 2+ textures |
| `audio_set_settings` | `audio_set_settings_batch` | Configuring 2+ audio files |
| `model_set_settings` | `model_set_settings_batch` | Configuring 2+ models |

**Query Skills** (no batch needed):
- `texture_get_settings` - Get texture import settings
- `audio_get_settings` - Get audio import settings
- `model_get_settings` - Get model import settings

---

## Texture Skills

### texture_get_settings / texture_set_settings

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

### audio_get_settings / audio_set_settings

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

### model_get_settings / model_set_settings

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
