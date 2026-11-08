---
name: unity-importer
description: Configure asset import settings
---

# Unity Importer Skills

Change the import settings of textures, audio and models already in the project. Parameters come from the schema (`GET /skills/schema?category=Importer&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md). Extended helpers, field meanings and presets: [reference/import-reference.md](reference/import-reference.md).

## When to use
- Adjusting how textures, audio or models import
- Setting texture compression or sprite modes
- Applying per-platform overrides, or rig and animation clip splits

## Rules
- Batch first: use the `*_batch` setters when configuring 2 or more assets of one category.
- Getters and finders run in every mode. Setters and reimports need a grant under Approval.
- No skill here deletes or reloads the domain. Setting changes do not always apply in memory at once: call `asset_reimport` or `asset_reimport_batch` after changing importer-critical fields.
- `compression` takes `Uncompressed`, `Compressed`, `CompressedHQ` or `CompressedLQ`; Inspector wording is accepted. Per-platform `compressionQuality` is a separate 0-100 integer.
- `sprite_set_import_settings` forces `textureType` to `Sprite` for `Single`, `Multiple` and `Polygon`.
- `animationType` `Human` is also accepted as `Humanoid`.

## Pitfalls
- Texture `format` values depend on the editor version: read `validValues` on a rejection.
- Platform overrides apply only when `overridden` is enabled.
- After `model_set_rig` or `model_set_animation_clips`, reimport so clips and avatar refresh.

## Texture

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `texture_get_settings` | Read importer settings | `assetPath` |
| `texture_set_settings` | Set importer settings | `assetPath`, `textureType?`, `maxSize?`, `filterMode?`, `compression?`, `mipmapEnabled?`, `sRGB?`, `readable?`, `wrapMode?` |
| `texture_set_settings_batch` | Batch texture settings | `items` |
| `texture_get_import_settings` | Minimal importer settings | `assetPath` |
| `texture_set_import_settings` | Alternative texture import bridge | texture fields |
| `texture_find_assets` | Search Texture2D assets | `filter?`, `limit?` (50) |
| `texture_get_info` | Dimensions, format and runtime memory | `assetPath` |
| `texture_find_by_size` | Textures in a pixel range | `minSize?` (0), `maxSize?` (99999), `limit?` (50) |
| `texture_set_type` | Switch texture type | `assetPath`, `textureType` |
| `texture_set_platform_settings` | Override per-platform settings | `assetPath`, `platform` (`Standalone`, `iPhone`, `Android`, `WebGL`), `maxSize?`, `format?`, `compressionQuality?`, `overridden?` |
| `texture_get_platform_settings` | Read per-platform override | `assetPath`, `platform` |
| `texture_set_sprite_settings` | PPU and sprite mode | `assetPath`, `pixelsPerUnit?`, `spriteMode?` |
| `sprite_set_import_settings` | Sprite importer bridge (PPU, packing tag, pivot) | `assetPath`, `spriteMode?`, `pixelsPerUnit?`, `packingTag?`, `pivotX?`, `pivotY?` |

## Audio

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `audio_get_settings` | Read importer settings | `assetPath` |
| `audio_set_settings` | Set importer settings | `assetPath`, `forceToMono?`, `loadInBackground?`, `loadType?`, `compressionFormat?`, `quality?` |
| `audio_set_settings_batch` | Batch audio settings | `items` |
| `audio_get_import_settings` | Minimal importer defaults | `assetPath` |
| `audio_set_import_settings` | Alternative audio import bridge | audio fields |
| `audio_find_clips` | Search AudioClip assets | `filter?`, `limit?` (50) |
| `audio_get_clip_info` | Length, channels, frequency, samples | `assetPath` |
| `audio_add_source` | Add an AudioSource to a GameObject | target, `clipPath?`, `playOnAwake?` (false), `loop?` (false), `volume?` (1) |
| `audio_get_source_info` | AudioSource configuration | target |
| `audio_set_source_properties` | Update AudioSource fields | target, `clipPath?`, `volume?`, `pitch?`, `loop?`, `playOnAwake?`, `mute?`, `spatialBlend?`, `priority?` |
| `audio_find_sources_in_scene` | List AudioSources in the active scene | `limit?` (50) |
| `audio_create_mixer` | Create an AudioMixer asset | `mixerName?` (`NewAudioMixer`), `folder?` (`Assets`) |

## Model

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `model_get_settings` | Read importer settings | `assetPath` |
| `model_set_settings` | Set importer settings | `assetPath`, `globalScale?`, `meshCompression?`, `isReadable?`, `generateSecondaryUV?`, `animationType?`, `importAnimation?`, `importCameras?`, `importLights?`, `materialImportMode?` |
| `model_set_settings_batch` | Batch model settings | `items` |
| `model_get_import_settings` | Minimal importer defaults | `assetPath` |
| `model_set_import_settings` | Alternative model import bridge | model fields |
| `model_find_assets` | Search model assets | `filter?`, `limit?` (50) |
| `model_get_mesh_info` | Vertex, triangle and submesh stats | target or `assetPath` |
| `model_get_materials_info` | Sub-asset materials embedded in the model | `assetPath` |
| `model_get_animations_info` | Animation clips and framerates | `assetPath` |
| `model_get_rig_info` | Rig, avatar and skeleton binding | `assetPath` |
| `model_set_animation_clips` | Split animation clips | `assetPath`, `clips` (JSON array of `{name, firstFrame, lastFrame, loop}`) |
| `model_set_rig` | Switch rig mode | `assetPath`, `animationType` (`None`, `Legacy`, `Generic`, `Human`), `avatarSetup?` |

## Reimport

| Skill | Use |
|-------|-----|
| `asset_reimport` | Reimport one asset |
| `asset_reimport_batch` | Reimport assets matching a search scope |

**DO NOT** (common hallucinations):
- `importer_import` does not exist: use `asset_import` (`asset` module).
- `importer_set_format` does not exist: use the category setters.
- `importer_get_settings` does not exist: use the category getters.
