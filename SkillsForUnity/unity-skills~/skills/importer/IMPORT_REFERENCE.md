---
name: unity-importer-reference
description: "Extended importer reference — importer/search helpers, per-platform override details, and import best-practice examples beyond the importer SKILL.md. 导入器扩展参考(importer/搜索辅助、分平台覆盖细节、超出 importer SKILL.md 的导入最佳实践示例)。"
type: reference
---

# Importer Reference

Load this file when you need extended importer/search helpers, platform override details, or importer-side best-practice examples. The main `SKILL.md` keeps only routing and the most common setting decisions.

## Efficient Configuration Example

```python
import unity_skills

unity_skills.call_skill("texture_set_settings_batch", items=[
    {"assetPath": f"Assets/UI/btn{i}.png", "textureType": "Sprite", "mipmapEnabled": False}
    for i in range(1, 6)
])
```

## Texture Reference

### Core Fields

| Field | Meaning |
|-------|---------|
| `textureType` | `Default`, `NormalMap`, `Sprite`, `EditorGUI`, `Cursor`, `Cookie`, `Lightmap`, `SingleChannel` |
| `maxSize` | `32` to `8192` |
| `filterMode` | `Point`, `Bilinear`, `Trilinear` |
| `compression` | `None`, `LowQuality`, `Normal`, `HighQuality` |
| `mipmapEnabled` | Generate mipmaps |
| `sRGB` | sRGB color space |
| `readable` | CPU-readable texture data |
| `spritePixelsPerUnit` | Pixels-per-unit for sprites |
| `wrapMode` | `Repeat`, `Clamp`, `Mirror`, `MirrorOnce` |

### Extended Texture Skills

| Skill | Use |
|-------|-----|
| `texture_find_assets` | Search texture assets |
| `texture_get_info` | Read size/format/memory info |
| `texture_set_type` | Change texture type only |
| `texture_set_platform_settings` | Override per-platform settings |
| `texture_get_platform_settings` | Read per-platform settings |
| `texture_set_sprite_settings` | Sprite mode / pixels per unit |
| `texture_find_by_size` | Search by dimension range |
| `texture_get_import_settings` | Alternative texture importer getter |
| `sprite_set_import_settings` | Sprite-specific import bridge |

### Platform Override Fields

| Field | Meaning |
|-------|---------|
| `platform` | `Standalone`, `iPhone`, `Android`, `WebGL` |
| `maxSize` | Max texture size for that platform |
| `format` | `TextureImporterFormat` enum value |
| `compressionQuality` | `0-100` |
| `overridden` | Enable platform override |

## Audio Reference

### Core Fields

| Field | Meaning |
|-------|---------|
| `forceToMono` | Convert to mono |
| `loadInBackground` | Background load |
| `preloadAudioData` | Preload on scene load |
| `loadType` | `DecompressOnLoad`, `CompressedInMemory`, `Streaming` |
| `compressionFormat` | `PCM`, `Vorbis`, `ADPCM` |
| `quality` | `0.0-1.0` for main audio setter |

### Extended Audio Skills

| Skill | Use |
|-------|-----|
| `audio_find_clips` | Search AudioClip assets |
| `audio_get_clip_info` | Read clip info |
| `audio_add_source` | Add AudioSource to GameObject |
| `audio_get_source_info` | Read AudioSource config |
| `audio_set_source_properties` | Configure AudioSource |
| `audio_find_sources_in_scene` | Find scene AudioSources |
| `audio_create_mixer` | Create AudioMixer asset |
| `audio_get_import_settings` | Alternative importer getter |

### Practical Presets

| Asset type | Suggested settings |
|-----------|--------------------|
| BGM | `Streaming` + `Vorbis` + medium quality |
| Short SFX | `DecompressOnLoad` |
| Large SFX bank | `CompressedInMemory` or mono where acceptable |

## Model Reference

### Core Fields

| Field | Meaning |
|-------|---------|
| `globalScale` | Import scale factor |
| `meshCompression` | `Off`, `Low`, `Medium`, `High` |
| `isReadable` | CPU-readable mesh |
| `generateSecondaryUV` | Generate lightmap UVs |
| `importBlendShapes` | Import blend shapes |
| `importCameras` | Import embedded cameras |
| `importLights` | Import embedded lights |
| `animationType` | `None`, `Legacy`, `Generic`, `Humanoid` |
| `importAnimation` | Import clip data |
| `materialImportMode` | Material import behavior |

### Extended Model Skills

| Skill | Use |
|-------|-----|
| `model_find_assets` | Search model assets |
| `model_get_mesh_info` | Read mesh topology info |
| `model_get_materials_info` | Read material mapping |
| `model_get_animations_info` | Read clip info |
| `model_set_animation_clips` | Configure clip splits |
| `model_get_rig_info` | Read rig/avatar settings |
| `model_set_rig` | Change rig mode |
| `model_get_import_settings` | Alternative importer getter |

### Practical Presets

| Asset type | Suggested settings |
|-----------|--------------------|
| Humanoid character | `animationType="Humanoid"` |
| Static prop | disable cameras/lights/animation, compress mesh |
| Baked environment mesh | enable secondary UVs when needed |

## Asset Reimport Helpers

| Skill | Use |
|-------|-----|
| `asset_reimport` | Reimport one asset by path |
| `asset_reimport_batch` | Reimport many assets by search filter |
| `asset_set_labels` | Set labels on an asset (comma-separated string) |
| `asset_get_labels` | Get labels of an asset → `{ assetPath, labels }` |

## Best Practices

1. Batch assets by category and apply one policy per batch.
2. Only enable `readable` when runtime CPU access is necessary.
3. Reimport deliberately after changing importer-critical fields.
4. Keep character rig settings and clip splits documented, because they are easy to drift.
5. Use platform overrides only when there is a real target-platform constraint.
