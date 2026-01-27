---
name: unity-skills
description: "Unity Editor REST API. BATCH-FIRST: Use *_batch for 2+ objects."
---

# Unity Skills API

> **RULE**: 2+ objects → use `*_batch` skill (1 call vs N calls)

## Batch Skills (Use First!)

| Skill | items format |
|-------|--------------|
| `gameobject_create_batch` | `[{name, primitiveType?, x?, y?, z?, parentName?}]` |
| `gameobject_delete_batch` | `[{name?, instanceId?}]` or `["name1","name2"]` |
| `gameobject_set_transform_batch` | `[{name, posX?, posY?, posZ?, rotX?, rotY?, rotZ?, scaleX?, scaleY?, scaleZ?}]` |
| `gameobject_set_active_batch` | `[{name, active}]` |
| `gameobject_set_parent_batch` | `[{childName, parentName}]` |
| `gameobject_duplicate_batch` | `[{name?, instanceId?}]` |
| `gameobject_rename_batch` | `[{instanceId, newName}]` |
| `gameobject_set_layer_batch` | `[{name, layer}]` |
| `gameobject_set_tag_batch` | `[{name, tag}]` |
| `component_add_batch` | `[{name, componentType}]` |
| `component_remove_batch` | `[{name, componentType}]` |
| `component_set_property_batch` | `[{name, componentType, propertyName, value}]` |
| `material_create_batch` | `[{name, shaderName?, savePath?}]` |
| `material_assign_batch` | `[{name, materialPath}]` |
| `material_set_colors_batch` | `[{name?, path?, r, g, b, a?, intensity?}]` |
| `material_set_emission_batch` | `[{name?, path?, r, g, b, intensity?}]` |
| `prefab_instantiate_batch` | `[{prefabPath, x?, y?, z?, rotX?, rotY?, rotZ?, name?, parentName?}]` |
| `asset_import_batch` | `[{sourcePath, destinationPath}]` |
| `asset_delete_batch` | `[{path}]` |
| `asset_move_batch` | `[{sourcePath, destinationPath}]` |
| `ui_create_batch` | `[{type, name, parent?, text?, width?, height?, x?, y?}]` type: Button/Text/Image/Panel/Slider/Toggle/InputField |
| `script_create_batch` | `[{scriptName, folder?, template?, namespace?}]` |
| `light_set_properties_batch` | `[{name, intensity?, r?, g?, b?, range?, shadows?}]` |
| `light_set_enabled_batch` | `[{name, enabled}]` |
| `texture_set_settings_batch` | `[{assetPath, textureType?, maxSize?, filterMode?, compression?, mipmapEnabled?, spritePixelsPerUnit?}]` |
| `audio_set_settings_batch` | `[{assetPath, loadType?, compressionFormat?, quality?, forceToMono?}]` |
| `model_set_settings_batch` | `[{assetPath, animationType?, meshCompression?, importAnimation?}]` |

## Single Skills

### gameobject
| Skill | Parameters |
|-------|------------|
| `gameobject_create` | name?, primitiveType?, x?, y?, z?, parentName? |
| `gameobject_delete` | name?, instanceId?, path? |
| `gameobject_find` | name?, tag?, layer?, component?, useRegex?, limit? → returns list |
| `gameobject_get_info` | name?, instanceId?, path? |
| `gameobject_set_transform` | name, posX?, posY?, posZ?, rotX?, rotY?, rotZ?, scaleX?, scaleY?, scaleZ? |
| `gameobject_set_parent` | name, parentName |
| `gameobject_set_active` | name, active |
| `gameobject_duplicate` | name?, instanceId? → returns copyName, copyInstanceId |
| `gameobject_rename` | name?, instanceId?, newName |

primitiveType: Cube, Sphere, Capsule, Cylinder, Plane, Quad, Empty(null)

### component
| Skill | Parameters |
|-------|------------|
| `component_add` | name, componentType |
| `component_remove` | name, componentType |
| `component_list` | name → returns components[] |
| `component_set_property` | name, componentType, propertyName, value |
| `component_get_properties` | name, componentType |

componentType: Rigidbody, BoxCollider, SphereCollider, CapsuleCollider, MeshCollider, CharacterController, AudioSource, Light, Camera, Animator, etc.

### material
| Skill | Parameters |
|-------|------------|
| `material_create` | name, shaderName?, savePath? |
| `material_assign` | name?, instanceId?, path?, materialPath |
| `material_set_color` | name?, path?, r, g, b, a?, propertyName?, intensity? |
| `material_set_emission` | name?, path?, r, g, b, intensity?, enableEmission? |
| `material_set_texture` | name?, path?, texturePath, propertyName? |
| `material_set_float` | name?, path?, propertyName, value |
| `material_set_int` | name?, path?, propertyName, value |
| `material_set_vector` | name?, path?, propertyName, x, y, z?, w? |
| `material_set_keyword` | name?, path?, keyword, enable? |
| `material_set_render_queue` | name?, path?, renderQueue |
| `material_set_shader` | name?, path?, shaderName |
| `material_set_texture_offset` | name?, path?, propertyName?, x, y |
| `material_set_texture_scale` | name?, path?, propertyName?, x, y |
| `material_get_properties` | name?, path? |
| `material_get_keywords` | name?, path? |
| `material_duplicate` | sourcePath, newName, savePath? |

### scene
| Skill | Parameters |
|-------|------------|
| `scene_create` | scenePath |
| `scene_load` | scenePath, additive? |
| `scene_save` | scenePath? |
| `scene_get_info` | (none) → name, path, isDirty, rootObjects |
| `scene_get_hierarchy` | maxDepth? → hierarchy tree |
| `scene_screenshot` | filename?, width?, height? |

### light
| Skill | Parameters |
|-------|------------|
| `light_create` | name?, lightType?, x?, y?, z?, r?, g?, b?, intensity?, range?, spotAngle?, shadows? |
| `light_set_properties` | name, r?, g?, b?, intensity?, range?, shadows? |
| `light_get_info` | name |
| `light_find_all` | lightType?, limit? → returns list |
| `light_set_enabled` | name, enabled |

lightType: Directional, Point, Spot, Area | shadows: none, hard, soft

### prefab
| Skill | Parameters |
|-------|------------|
| `prefab_create` | gameObjectName?, instanceId?, savePath |
| `prefab_instantiate` | prefabPath, x?, y?, z?, name?, parentName? |
| `prefab_apply` | gameObjectName?, instanceId? |
| `prefab_unpack` | gameObjectName?, instanceId?, completely? |

### asset
| Skill | Parameters |
|-------|------------|
| `asset_import` | sourcePath, destinationPath |
| `asset_delete` | assetPath |
| `asset_move` | sourcePath, destinationPath |
| `asset_duplicate` | assetPath |
| `asset_find` | searchFilter, searchInFolders?, limit? → returns paths |
| `asset_create_folder` | folderPath |
| `asset_refresh` | (none) |
| `asset_get_info` | assetPath |

searchFilter: t:Texture2D, t:Material, t:Prefab, t:AudioClip, t:Script, name, l:Label

### ui
| Skill | Parameters |
|-------|------------|
| `ui_create_canvas` | name?, renderMode? |
| `ui_create_panel` | name?, parent?, r?, g?, b?, a?, width?, height? |
| `ui_create_button` | name?, parent?, text?, width?, height?, x?, y? |
| `ui_create_text` | name?, parent?, text?, fontSize?, r?, g?, b?, a?, width?, height? |
| `ui_create_image` | name?, parent?, spritePath?, r?, g?, b?, a?, width?, height? |
| `ui_create_inputfield` | name?, parent?, placeholder?, width?, height? |
| `ui_create_slider` | name?, parent?, minValue?, maxValue?, value?, width?, height? |
| `ui_create_toggle` | name?, parent?, label?, isOn? |
| `ui_set_text` | name, text |
| `ui_find_all` | uiType?, limit? |

renderMode: ScreenSpaceOverlay, ScreenSpaceCamera, WorldSpace | uiType: Button, Text, Image, Panel, Slider, Toggle, InputField

### script
| Skill | Parameters |
|-------|------------|
| `script_create` | scriptName, folder?, template?, namespace? |
| `script_read` | scriptPath → content |
| `script_delete` | scriptPath |
| `script_find_in_file` | pattern, folder?, isRegex?, limit? → matches |
| `script_append` | scriptPath, content, atLine? |

template: MonoBehaviour, ScriptableObject, Editor, EditorWindow

### editor
| Skill | Parameters |
|-------|------------|
| `editor_play` | (none) |
| `editor_stop` | (none) |
| `editor_pause` | (none) |
| `editor_select` | gameObjectName?, instanceId? |
| `editor_get_selection` | (none) → selected objects with instanceId |
| `editor_get_context` | includeComponents?, includeChildren? → selection, assets, scene info |
| `editor_undo` | (none) |
| `editor_redo` | (none) |
| `editor_get_state` | (none) → isPlaying, isPaused, isCompiling |
| `editor_execute_menu` | menuPath |
| `editor_get_tags` | (none) |
| `editor_get_layers` | (none) |

menuPath: "File/Save", "Edit/Play", "GameObject/Create Empty", "Assets/Refresh"

### animator
| Skill | Parameters |
|-------|------------|
| `animator_create_controller` | name, folder? |
| `animator_add_parameter` | controllerPath, paramName, paramType, defaultValue? |
| `animator_get_parameters` | controllerPath |
| `animator_set_parameter` | name, paramName, paramType, floatValue?/intValue?/boolValue? |
| `animator_play` | name, stateName, layer?, normalizedTime? |
| `animator_get_info` | name |
| `animator_assign_controller` | name, controllerPath |
| `animator_list_states` | controllerPath, layer? |

paramType: float, int, bool, trigger

### shader
| Skill | Parameters |
|-------|------------|
| `shader_create` | shaderName, savePath, template? |
| `shader_read` | shaderPath |
| `shader_list` | filter?, limit? |

template: Unlit, Standard, Transparent

### console
| Skill | Parameters |
|-------|------------|
| `console_start_capture` | (none) |
| `console_stop_capture` | (none) |
| `console_get_logs` | filter?, limit? |
| `console_clear` | (none) |
| `console_log` | message, type? |

filter/type: Log, Warning, Error

### validation
| Skill | Parameters |
|-------|------------|
| `validate_scene` | checkMissingScripts?, checkMissingPrefabs?, checkDuplicateNames? |
| `validate_find_missing_scripts` | searchInPrefabs? |
| `validate_fix_missing_scripts` | dryRun? (default true) |
| `validate_cleanup_empty_folders` | rootPath?, dryRun? (default true) |
| `validate_find_unused_assets` | assetType?, limit? |
| `validate_texture_sizes` | maxRecommendedSize?, limit? |
| `validate_project_structure` | rootPath?, maxDepth? |

### importer
| Skill | Parameters |
|-------|------------|
| `texture_get_settings` | assetPath |
| `texture_set_settings` | assetPath, textureType?, maxSize?, filterMode?, compression?, mipmapEnabled?, sRGB?, readable?, spritePixelsPerUnit?, wrapMode? |
| `audio_get_settings` | assetPath |
| `audio_set_settings` | assetPath, forceToMono?, loadInBackground?, preloadAudioData?, loadType?, compressionFormat?, quality? |
| `model_get_settings` | assetPath |
| `model_set_settings` | assetPath, globalScale?, meshCompression?, isReadable?, generateSecondaryUV?, importBlendShapes?, importCameras?, importLights?, animationType?, importAnimation?, materialImportMode? |

textureType: Default, NormalMap, Sprite, EditorGUI, Cursor, Cookie, Lightmap, SingleChannel
filterMode: Point, Bilinear, Trilinear | compression: None, LowQuality, Normal, HighQuality
loadType: DecompressOnLoad, CompressedInMemory, Streaming | compressionFormat: PCM, Vorbis, ADPCM
animationType: None, Legacy, Generic, Humanoid | meshCompression: Off, Low, Medium, High

## Notes
- Response: `{success: true/false, ...data}` or `{success: false, error: "message"}`
- All operations auto-revert on failure
- After `script_create`, wait 3-5s for Unity recompile
- Use `instanceId` (from `editor_get_selection`/`editor_get_context`) for guaranteed uniqueness
- `name?` means use name OR instanceId OR path to identify object
