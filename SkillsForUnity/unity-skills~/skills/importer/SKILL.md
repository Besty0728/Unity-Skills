---
name: unity-importer
description: "Asset import settings. Use when users want to configure texture, audio, or model import settings. Triggers: import settings, texture settings, audio settings, model settings, compression, max size, 导入设置, 纹理设置, Unity压缩."
---

# Unity Importer Skills

Use this module to change import **settings** for textures, audio, and models that already exist in the project.

> **Batch-first**: Prefer the batch setters when configuring `2+` assets of the same category.

## Guardrails

**Mode**: Full-Auto required

**DO NOT** (common hallucinations):
- `importer_import` does not exist -> use `asset_import` in the `asset` module to bring files into the project
- `importer_set_format` does not exist -> use the specific texture/audio/model setters
- `importer_get_settings` does not exist -> use the category-specific getters
- Settings changes do not always apply instantly in memory. Reimport may still be required

**Routing**:
- File import or refresh -> `asset`
- Texture settings -> `texture_*`
- Audio settings -> `audio_*`
- Model settings -> `model_*`
- Alternative importer bridge skills -> `texture_set_import_settings`, `audio_set_import_settings`, `model_set_import_settings`
- Force importer refresh -> `asset_reimport` or `asset_reimport_batch`

## Skills

### Texture Route

Import settings:

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `texture_get_settings` | Read texture importer settings | `assetPath` |
| `texture_set_settings` | Set texture importer settings | `assetPath`, `textureType?`, `maxSize?`, `filterMode?`, `compression?`, `mipmapEnabled?`, `sRGB?`, `readable?`, `wrapMode?` |
| `texture_set_settings_batch` | Batch texture settings | `items` |
| `texture_get_import_settings` | Read minimal importer settings (type/maxSize/compression/filter/srgb/readable/mipmap) | `assetPath` |
| `texture_set_import_settings` | Alternative texture import bridge | similar texture fields |

Query and runtime info:

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `texture_find_assets` | Search Texture2D assets by AssetDatabase filter | `filter?`, `limit?` (default 50) |
| `texture_get_info` | Inspect dimensions, format, and runtime memory size | `assetPath` |
| `texture_find_by_size` | Find textures in a dimension range (pixels) | `minSize?` (0), `maxSize?` (99999), `limit?` (50) |

Typed / platform overrides:

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `texture_set_type` | Switch texture type | `assetPath`, `textureType` (`Default`/`NormalMap`/`Sprite`/`EditorGUI`/`Cursor`/`Cookie`/`Lightmap`/`SingleChannel`) |
| `texture_set_platform_settings` | Override per-platform settings | `assetPath`, `platform` (`Standalone`/`iPhone`/`Android`/`WebGL`), `maxSize?`, `format?`, `compressionQuality?`, `overridden?` |
| `texture_get_platform_settings` | Read per-platform override | `assetPath`, `platform` |
| `texture_set_sprite_settings` | Sprite-specific knobs (PPU, mode) | `assetPath`, `pixelsPerUnit?`, `spriteMode?` (`Single`/`Multiple`/`Polygon`) |
| `sprite_set_import_settings` | Sprite importer bridge (PPU, packingTag, pivot) | `assetPath`, `spriteMode?`, `pixelsPerUnit?`, `packingTag?`, `pivotX?`, `pivotY?` |

Common texture decisions:
- UI sprites -> `textureType="Sprite"`, usually `mipmapEnabled=false`
- Pixel art -> `filterMode="Point"`
- Runtime CPU reads -> `readable=true` only when necessary
- Platform-tuned builds -> prefer `texture_set_platform_settings` over global `texture_set_settings`

### Audio Route

Import settings:

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `audio_get_settings` | Read audio importer settings | `assetPath` |
| `audio_set_settings` | Set audio importer settings | `assetPath`, `forceToMono?`, `loadInBackground?`, `loadType?`, `compressionFormat?`, `quality?` |
| `audio_set_settings_batch` | Batch audio settings | `items` |
| `audio_get_import_settings` | Read minimal importer defaults (loadType/format/quality/forceToMono/loadInBackground) | `assetPath` |
| `audio_set_import_settings` | Alternative audio import bridge | similar audio fields |

Clip query and info:

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `audio_find_clips` | Search `AudioClip` assets by filter | `filter?`, `limit?` (default 50) |
| `audio_get_clip_info` | Inspect length/channels/frequency/samples of a clip | `assetPath` |

Scene runtime (`AudioSource` / `AudioMixer`):

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `audio_add_source` | Add an `AudioSource` to a GameObject | target (`name`/`instanceId`/`path`), `clipPath?`, `playOnAwake?` (false), `loop?` (false), `volume?` (1) |
| `audio_get_source_info` | Read the AudioSource configuration | target |
| `audio_set_source_properties` | Update AudioSource fields | target, `clipPath?`, `volume?`, `pitch?`, `loop?`, `playOnAwake?`, `mute?`, `spatialBlend?`, `priority?` |
| `audio_find_sources_in_scene` | List all AudioSources in the active scene | `limit?` (default 50) |
| `audio_create_mixer` | Create a new `AudioMixer` asset | `mixerName?` (default `NewAudioMixer`), `folder?` (default `Assets`) |

Common audio decisions:
- Long BGM -> `loadType="Streaming"`
- Short SFX -> `loadType="DecompressOnLoad"`
- Memory-sensitive SFX libraries -> consider `forceToMono=true`
- Scene-side AudioSource tuning -> prefer `audio_set_source_properties` over manual component edits

### Model Route

Import settings:

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `model_get_settings` | Read model importer settings | `assetPath` |
| `model_set_settings` | Set model importer settings | `assetPath`, `globalScale?`, `meshCompression?`, `isReadable?`, `generateSecondaryUV?`, `animationType?`, `importAnimation?`, `importCameras?`, `importLights?`, `materialImportMode?` |
| `model_set_settings_batch` | Batch model settings | `items` |
| `model_get_import_settings` | Read minimal importer defaults (scale/compression/animationType/importAnimation/materialImportMode) | `assetPath` |
| `model_set_import_settings` | Alternative model import bridge | similar model fields |

Query and info:

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `model_find_assets` | Search model assets by filter | `filter?`, `limit?` (default 50) |
| `model_get_mesh_info` | Mesh vertex / triangle / submesh stats | target (`name`/`instanceId`/`path`) or `assetPath` |
| `model_get_materials_info` | Inspect sub-asset materials embedded in the model | `assetPath` |
| `model_get_animations_info` | List animation clips and framerates on the model | `assetPath` |
| `model_get_rig_info` | Read animationType, avatar, skeleton binding info | `assetPath` |

Animation and rig:

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `model_set_animation_clips` | Configure animation clip splits | `assetPath`, `clips` (JSON array of `{name, firstFrame, lastFrame, loop}`) |
| `model_set_rig` | Switch rig/skeleton mode | `assetPath`, `animationType` (`None`/`Legacy`/`Generic`/`Humanoid`), `avatarSetup?` |

Common model decisions:
- Characters -> `animationType="Humanoid"` when retargeting is required
- Static props -> disable cameras/lights/animation imports when unused
- Baked-lighting meshes -> enable secondary UVs when appropriate
- After `model_set_rig` or `model_set_animation_clips` -> call `asset_reimport` to refresh clips and avatar

## Reimport Rule

After importer changes, use reimport when you need Unity to fully refresh the asset:

| Skill | Use |
|-------|-----|
| `asset_reimport` | Reimport one asset |
| `asset_reimport_batch` | Reimport assets matching a search scope |

## Minimal Example

```python
import unity_skills

unity_skills.call_skill("texture_set_settings_batch", items=[
    {"assetPath": "Assets/UI/icon_play.png", "textureType": "Sprite", "mipmapEnabled": False},
    {"assetPath": "Assets/UI/icon_pause.png", "textureType": "Sprite", "mipmapEnabled": False}
])

unity_skills.call_skill("audio_set_settings",
    assetPath="Assets/Audio/bgm.mp3",
    loadType="Streaming",
    compressionFormat="Vorbis",
    quality=0.7
)

unity_skills.call_skill("asset_reimport", assetPath="Assets/Audio/bgm.mp3")
```

## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
Load `IMPORT_REFERENCE.md` for extended asset search/query helpers, platform overrides, rig/animation details, and importer-side best practices.
