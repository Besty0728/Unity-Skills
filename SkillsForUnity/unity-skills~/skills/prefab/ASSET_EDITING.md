# Direct prefab asset editing

These APIs edit `.prefab` assets without spawning scene instances. Each asset transaction uses isolated contents and always unloads them. All steps must succeed before saving. Native Undo and Workflow undo/redo preserve the entire asset file and its `.meta`.

## Targeting and boundaries

Use `prefabPath` plus an exact root-relative `gameObjectPath` (empty means root); no scene instance IDs. Duplicate sibling names produce candidates with siblingIndex and reject ambiguous paths. Reads include inactive nodes. New names must not create sibling collisions.

Component indices are zero-based among the requested type's matches; use full concrete type names returned by `prefab_list_components` for an unambiguous round trip. Batch results describe the final saved state, including changes made by later steps.

Only the requested asset is saved. Variant and nested-prefab edits preserve their connections as overrides; they never apply to the base/source asset. Unity-invalid inherited structure changes are rejected. Root deletion/reparent/duplication, Transform replacement/removal, cycles, script/bookkeeping property writes and scene references are rejected. Node duplication supports child subtrees; creation makes an empty Transform or RectTransform node. No dedicated tag/layer API is added.

The serialized writer accepts nested fields, array sizes and elements, primitives, enums, vectors and colors through the shared component conversion rules. Reference fields accept a project asset, a node/component **within this prefab**, or an explicit clear. It cannot bind to scene objects. Multiple reference sources are rejected. For complex values not supported by the shared converter, a structured error is returned.

`prefab_edit_batch` and delete APIs carry Delete/high metadata, preventing hidden deletion through a weaker permission gate. Read APIs are SemiAuto. Other writes are FullAuto. DryRun inspects existing asset state without loading/editing prefab contents; later operations depending on earlier edits receive a warning and are fully validated during execution.

## Example

```python
unity_skills.call_skill("prefab_edit_batch", prefabPath="Assets/Prefabs/Enemy.prefab", steps=[
    {"skill": "prefab_create_gameobject", "args": {"name": "Hitbox"}},
    {"skill": "prefab_add_component", "args": {"gameObjectPath": "Hitbox", "componentType": "BoxCollider"}},
    {"skill": "prefab_set_serialized_property", "args": {"gameObjectPath": "Hitbox", "componentType": "BoxCollider", "propertyPath": "m_IsTrigger", "value": "true"}}
])
```

Use returned schemas for exact signatures. The existing `prefab_set_property` remains compatible: it keeps its parameters, field aliases and legacy component/property/valueSet fields, while saving through the same isolated transaction engine.

## Skills

### prefab_get_info

Read or edit the specified prefab asset using the shared root-relative targeting rules.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `gameObjectPath` | string | No | null | Root-relative path; null or empty targets the root. Ambiguous sibling paths are rejected. |

**Returns**: `{success, prefabPath, gameObjectPath, gameObject, parentPath, activeSelf, layer, tag, siblingIndex, childCount, localPosition, localEulerAngles, localScale, prefabAssetType}`. Writes additionally include skill. RectTransform nodes also return anchoredPosition, anchorMin, anchorMax, pivot and sizeDelta. Values are read back from the saved asset.

### prefab_get_hierarchy

Read or edit the specified prefab asset using the shared root-relative targeting rules.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `limit` | int | No | 200 | Maximum returned records; positive. |

**Returns**: `{success, prefabPath, total, truncated, nodes}`. Inactive nodes are included; each node has the info fields above.

### prefab_list_components

Read or edit the specified prefab asset using the shared root-relative targeting rules.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `gameObjectPath` | string | No | null | Root-relative path; null or empty targets the root. Ambiguous sibling paths are rejected. |
| `includeProperties` | bool | No | false | Include serialized Inspector fields per component. |
| `includeHidden` | bool | No | false | With `includeProperties`: also list the fields the Inspector hides, as in `prefab_get_serialized_properties`. |

**Returns**: `{success, prefabPath, gameObjectPath, components}`. Entries report full componentType and its exact-type componentIndex, optional enabled/properties; missing scripts are reported as missingScript:true.

### prefab_get_serialized_properties

Read or edit the specified prefab asset using the shared root-relative targeting rules.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `componentType` | string | Yes | — | Concrete component type, simple or namespace-qualified. |
| `gameObjectPath` | string | No | null | Root-relative path; null or empty targets the root. Ambiguous sibling paths are rejected. |
| `componentIndex` | int | No | 0 | Zero-based index among components matching componentType. |
| `includeChildren` | bool | No | true | Include nested serialized fields. |
| `limit` | int | No | 200 | Maximum returned records; positive. |
| `includeHidden` | bool | No | false | Also list fields the Inspector hides (`[HideInInspector]` members, Unity's `m_ObjectHideFlags` / `m_PrefabInstance` bookkeeping); they count toward `limit`. |

**Returns**: `{success, prefabPath, gameObjectPath, componentType, componentIndex, properties}`. Inspector paths and serialized values use the existing component serialized-property utility; an `ObjectReference` property also has `referenceState` (`none`, `missing` or `assigned`), which tells a never-assigned reference from one whose target is gone when `value` reads `"null"`.

### prefab_add_component

Read or edit the specified prefab asset using the shared root-relative targeting rules.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `componentType` | string | Yes | — | Concrete component type, simple or namespace-qualified. |
| `gameObjectPath` | string | No | null | Root-relative path; null or empty targets the root. Ambiguous sibling paths are rejected. |

**Returns**: `{success, prefabPath, skill, gameObjectPath, componentType, componentIndex}` plus node info. RequireComponent dependencies are added by Unity; DisallowMultipleComponent conflicts are rejected.

### prefab_add_component_batch

Grouped batch version of `prefab_add_component`. Each item contains `prefabPath` and the single-operation arguments. Items targeting the same asset execute in input order and save together. At most 50 items.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `items` | string | Yes | — | Nonempty JSON array of items with prefabPath and the single-operation arguments. |

**Returns**: `{success, completed, totalItems, assetCount, countsUnit, successCount, failCount, results}`. completed means processing finished; success means all asset groups succeeded. totalItems counts input items; successCount/failCount count **assets**, indicated by countsUnit:"assets". Each results entry is an asset transaction containing its own operation results or structured error. Partial success has no top-level error, so successful assets and their Undo/Workflow history remain committed. Retry only failed asset groups.

### prefab_remove_component

Read or edit the specified prefab asset using the shared root-relative targeting rules.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `componentType` | string | Yes | — | Concrete component type, simple or namespace-qualified. |
| `gameObjectPath` | string | No | null | Root-relative path; null or empty targets the root. Ambiguous sibling paths are rejected. |
| `componentIndex` | int | No | 0 | Zero-based index among components matching componentType. |

**Returns**: `{success, prefabPath, skill, gameObjectPath, componentType, removed, remainingCount}` plus node info. Transform and required components cannot be removed.

### prefab_remove_component_batch

Grouped batch version of `prefab_remove_component`. Each item contains `prefabPath` and the single-operation arguments. Items targeting the same asset execute in input order and save together. At most 50 items.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `items` | string | Yes | — | Nonempty JSON array of items with prefabPath and the single-operation arguments. |

**Returns**: `{success, completed, totalItems, assetCount, countsUnit, successCount, failCount, results}`. completed means processing finished; success means all asset groups succeeded. totalItems counts input items; successCount/failCount count **assets**, indicated by countsUnit:"assets". Each results entry is an asset transaction containing its own operation results or structured error. Partial success has no top-level error, so successful assets and their Undo/Workflow history remain committed. Retry only failed asset groups.

### prefab_set_component_enabled

Read or edit the specified prefab asset using the shared root-relative targeting rules.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `componentType` | string | Yes | — | Concrete component type, simple or namespace-qualified. |
| `enabled` | bool | Yes | — | Saved component enabled value; only components exposing m_Enabled are supported. |
| `gameObjectPath` | string | No | null | Root-relative path; null or empty targets the root. Ambiguous sibling paths are rejected. |
| `componentIndex` | int | No | 0 | Zero-based index among components matching componentType. |

**Returns**: `{success, prefabPath, skill, gameObjectPath, componentType, componentIndex, propertyPath, valueSet, valueRequested?, warnings?}` plus node info. valueSet is read after saving; internal references use root-relative path and, for components, a full type and index suffix.

### prefab_set_component_enabled_batch

Grouped batch version of `prefab_set_component_enabled`. Each item contains `prefabPath` and the single-operation arguments. Items targeting the same asset execute in input order and save together. At most 50 items.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `items` | string | Yes | — | Nonempty JSON array of items with prefabPath and the single-operation arguments. |

**Returns**: `{success, completed, totalItems, assetCount, countsUnit, successCount, failCount, results}`. completed means processing finished; success means all asset groups succeeded. totalItems counts input items; successCount/failCount count **assets**, indicated by countsUnit:"assets". Each results entry is an asset transaction containing its own operation results or structured error. Partial success has no top-level error, so successful assets and their Undo/Workflow history remain committed. Retry only failed asset groups.

### prefab_set_serialized_property

Read or edit the specified prefab asset using the shared root-relative targeting rules.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `componentType` | string | Yes | — | Concrete component type, simple or namespace-qualified. |
| `propertyPath` | string | Yes | — | Inspector serialized path; also tries m_Name, _name and m_name aliases. |
| `gameObjectPath` | string | No | null | Root-relative path; null or empty targets the root. Ambiguous sibling paths are rejected. |
| `componentIndex` | int | No | 0 | Zero-based index among components matching componentType. |
| `value` | string | No | null | Serialized value text; empty strings are supported. Object references require a reference source or explicit null. |
| `assetPath` | string | No | null | Project asset reference; cannot be combined with an internal reference or clearReference. |
| `referenceGameObjectPath` | string | No | null | Root-relative reference within this prefab. Empty string references the root; null means omitted. |
| `referenceComponentType` | string | No | null | Omit to reference a GameObject; otherwise select its component type. |
| `referenceComponentIndex` | int | No | 0 | Zero-based matching component index on the reference node. |
| `clearReference` | bool | No | false | Explicitly clear an ObjectReference field. |

**Returns**: `{success, prefabPath, skill, gameObjectPath, componentType, componentIndex, propertyPath, valueSet, valueRequested?, warnings?}` plus node info. valueSet is read after saving; internal references use root-relative path and, for components, a full type and index suffix.

### prefab_set_serialized_property_batch

Grouped batch version of `prefab_set_serialized_property`. Each item contains `prefabPath` and the single-operation arguments. Items targeting the same asset execute in input order and save together. At most 50 items.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `items` | string | Yes | — | Nonempty JSON array of items with prefabPath and the single-operation arguments. |

**Returns**: `{success, completed, totalItems, assetCount, countsUnit, successCount, failCount, results}`. completed means processing finished; success means all asset groups succeeded. totalItems counts input items; successCount/failCount count **assets**, indicated by countsUnit:"assets". Each results entry is an asset transaction containing its own operation results or structured error. Partial success has no top-level error, so successful assets and their Undo/Workflow history remain committed. Retry only failed asset groups.

### prefab_create_gameobject

Read or edit the specified prefab asset using the shared root-relative targeting rules.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `name` | string | Yes | — | New child name; cannot contain / or collide with a sibling. |
| `parentPath` | string | No | null | Root-relative parent; omitted/empty means prefab root. |
| `rectTransform` | bool | No | false | Create a RectTransform node instead of a Transform node. |

**Returns**: `{success, prefabPath, gameObjectPath, gameObject, parentPath, activeSelf, layer, tag, siblingIndex, childCount, localPosition, localEulerAngles, localScale, prefabAssetType}`. Writes additionally include skill. RectTransform nodes also return anchoredPosition, anchorMin, anchorMax, pivot and sizeDelta. Values are read back from the saved asset.

### prefab_create_gameobject_batch

Grouped batch version of `prefab_create_gameobject`. Each item contains `prefabPath` and the single-operation arguments. Items targeting the same asset execute in input order and save together. At most 50 items.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `items` | string | Yes | — | Nonempty JSON array of items with prefabPath and the single-operation arguments. |

**Returns**: `{success, completed, totalItems, assetCount, countsUnit, successCount, failCount, results}`. completed means processing finished; success means all asset groups succeeded. totalItems counts input items; successCount/failCount count **assets**, indicated by countsUnit:"assets". Each results entry is an asset transaction containing its own operation results or structured error. Partial success has no top-level error, so successful assets and their Undo/Workflow history remain committed. Retry only failed asset groups.

### prefab_delete_gameobject

Read or edit the specified prefab asset using the shared root-relative targeting rules.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `gameObjectPath` | string | Yes | — | Root-relative path; null or empty targets the root. Ambiguous sibling paths are rejected. |

**Returns**: `{success, prefabPath, skill, gameObjectPath, deleted}`. Deleting the root or inherited nodes is rejected.

### prefab_delete_gameobject_batch

Grouped batch version of `prefab_delete_gameobject`. Each item contains `prefabPath` and the single-operation arguments. Items targeting the same asset execute in input order and save together. At most 50 items.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `items` | string | Yes | — | Nonempty JSON array of items with prefabPath and the single-operation arguments. |

**Returns**: `{success, completed, totalItems, assetCount, countsUnit, successCount, failCount, results}`. completed means processing finished; success means all asset groups succeeded. totalItems counts input items; successCount/failCount count **assets**, indicated by countsUnit:"assets". Each results entry is an asset transaction containing its own operation results or structured error. Partial success has no top-level error, so successful assets and their Undo/Workflow history remain committed. Retry only failed asset groups.

### prefab_duplicate_gameobject

Read or edit the specified prefab asset using the shared root-relative targeting rules.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `gameObjectPath` | string | Yes | — | Root-relative path; null or empty targets the root. Ambiguous sibling paths are rejected. |
| `newName` | string | No | null | New node name; cannot contain / or collide with a sibling. |

**Returns**: `{success, prefabPath, gameObjectPath, gameObject, parentPath, activeSelf, layer, tag, siblingIndex, childCount, localPosition, localEulerAngles, localScale, prefabAssetType}`. Writes additionally include skill. RectTransform nodes also return anchoredPosition, anchorMin, anchorMax, pivot and sizeDelta. Values are read back from the saved asset.

### prefab_duplicate_gameobject_batch

Grouped batch version of `prefab_duplicate_gameobject`. Each item contains `prefabPath` and the single-operation arguments. Items targeting the same asset execute in input order and save together. At most 50 items.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `items` | string | Yes | — | Nonempty JSON array of items with prefabPath and the single-operation arguments. |

**Returns**: `{success, completed, totalItems, assetCount, countsUnit, successCount, failCount, results}`. completed means processing finished; success means all asset groups succeeded. totalItems counts input items; successCount/failCount count **assets**, indicated by countsUnit:"assets". Each results entry is an asset transaction containing its own operation results or structured error. Partial success has no top-level error, so successful assets and their Undo/Workflow history remain committed. Retry only failed asset groups.

### prefab_rename_gameobject

Read or edit the specified prefab asset using the shared root-relative targeting rules.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `newName` | string | Yes | — | New node name; cannot contain / or collide with a sibling. |
| `gameObjectPath` | string | No | null | Root-relative path; null or empty targets the root. Ambiguous sibling paths are rejected. |

**Returns**: `{success, prefabPath, gameObjectPath, gameObject, parentPath, activeSelf, layer, tag, siblingIndex, childCount, localPosition, localEulerAngles, localScale, prefabAssetType}`. Writes additionally include skill. RectTransform nodes also return anchoredPosition, anchorMin, anchorMax, pivot and sizeDelta. Values are read back from the saved asset.

### prefab_rename_gameobject_batch

Grouped batch version of `prefab_rename_gameobject`. Each item contains `prefabPath` and the single-operation arguments. Items targeting the same asset execute in input order and save together. At most 50 items.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `items` | string | Yes | — | Nonempty JSON array of items with prefabPath and the single-operation arguments. |

**Returns**: `{success, completed, totalItems, assetCount, countsUnit, successCount, failCount, results}`. completed means processing finished; success means all asset groups succeeded. totalItems counts input items; successCount/failCount count **assets**, indicated by countsUnit:"assets". Each results entry is an asset transaction containing its own operation results or structured error. Partial success has no top-level error, so successful assets and their Undo/Workflow history remain committed. Retry only failed asset groups.

### prefab_set_transform

Read or edit the specified prefab asset using the shared root-relative targeting rules.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `gameObjectPath` | string | No | null | Root-relative path; null or empty targets the root. Ambiguous sibling paths are rejected. |
| `posX` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `posY` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `posZ` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `rotX` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `rotY` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `rotZ` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `scaleX` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `scaleY` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `scaleZ` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `anchoredPosX` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `anchoredPosY` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `anchorMinX` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `anchorMinY` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `anchorMaxX` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `anchorMaxY` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `pivotX` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `pivotY` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `sizeDeltaX` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `sizeDeltaY` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |

**Returns**: `{success, prefabPath, gameObjectPath, gameObject, parentPath, activeSelf, layer, tag, siblingIndex, childCount, localPosition, localEulerAngles, localScale, prefabAssetType}`. Writes additionally include skill. RectTransform nodes also return anchoredPosition, anchorMin, anchorMax, pivot and sizeDelta. Values are read back from the saved asset.

### prefab_set_transform_batch

Grouped batch version of `prefab_set_transform`. Each item contains `prefabPath` and the single-operation arguments. Items targeting the same asset execute in input order and save together. At most 50 items.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `items` | string | Yes | — | Nonempty JSON array of items with prefabPath and the single-operation arguments. |

**Returns**: `{success, completed, totalItems, assetCount, countsUnit, successCount, failCount, results}`. completed means processing finished; success means all asset groups succeeded. totalItems counts input items; successCount/failCount count **assets**, indicated by countsUnit:"assets". Each results entry is an asset transaction containing its own operation results or structured error. Partial success has no top-level error, so successful assets and their Undo/Workflow history remain committed. Retry only failed asset groups.

### prefab_set_parent

Read or edit the specified prefab asset using the shared root-relative targeting rules.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `gameObjectPath` | string | Yes | — | Root-relative path; null or empty targets the root. Ambiguous sibling paths are rejected. |
| `parentPath` | string | No | null | Root-relative parent; omitted/empty means prefab root. |
| `worldPositionStays` | bool | No | false | Whether reparent preserves preview world coordinates; default preserves local transform. |

**Returns**: `{success, prefabPath, gameObjectPath, gameObject, parentPath, activeSelf, layer, tag, siblingIndex, childCount, localPosition, localEulerAngles, localScale, prefabAssetType}`. Writes additionally include skill. RectTransform nodes also return anchoredPosition, anchorMin, anchorMax, pivot and sizeDelta. Values are read back from the saved asset.

### prefab_set_parent_batch

Grouped batch version of `prefab_set_parent`. Each item contains `prefabPath` and the single-operation arguments. Items targeting the same asset execute in input order and save together. At most 50 items.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `items` | string | Yes | — | Nonempty JSON array of items with prefabPath and the single-operation arguments. |

**Returns**: `{success, completed, totalItems, assetCount, countsUnit, successCount, failCount, results}`. completed means processing finished; success means all asset groups succeeded. totalItems counts input items; successCount/failCount count **assets**, indicated by countsUnit:"assets". Each results entry is an asset transaction containing its own operation results or structured error. Partial success has no top-level error, so successful assets and their Undo/Workflow history remain committed. Retry only failed asset groups.

### prefab_set_active

Read or edit the specified prefab asset using the shared root-relative targeting rules.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `active` | bool | Yes | — | Saved activeSelf value. |
| `gameObjectPath` | string | No | null | Root-relative path; null or empty targets the root. Ambiguous sibling paths are rejected. |

**Returns**: `{success, prefabPath, gameObjectPath, gameObject, parentPath, activeSelf, layer, tag, siblingIndex, childCount, localPosition, localEulerAngles, localScale, prefabAssetType}`. Writes additionally include skill. RectTransform nodes also return anchoredPosition, anchorMin, anchorMax, pivot and sizeDelta. Values are read back from the saved asset.

### prefab_set_active_batch

Grouped batch version of `prefab_set_active`. Each item contains `prefabPath` and the single-operation arguments. Items targeting the same asset execute in input order and save together. At most 50 items.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `items` | string | Yes | — | Nonempty JSON array of items with prefabPath and the single-operation arguments. |

**Returns**: `{success, completed, totalItems, assetCount, countsUnit, successCount, failCount, results}`. completed means processing finished; success means all asset groups succeeded. totalItems counts input items; successCount/failCount count **assets**, indicated by countsUnit:"assets". Each results entry is an asset transaction containing its own operation results or structured error. Partial success has no top-level error, so successful assets and their Undo/Workflow history remain committed. Retry only failed asset groups.

### prefab_set_sibling_index

Read or edit the specified prefab asset using the shared root-relative targeting rules.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `index` | int | Yes | — | Sibling index; Unity clamps it to the available range. |
| `gameObjectPath` | string | Yes | — | Root-relative path; null or empty targets the root. Ambiguous sibling paths are rejected. |

**Returns**: `{success, prefabPath, gameObjectPath, gameObject, parentPath, activeSelf, layer, tag, siblingIndex, childCount, localPosition, localEulerAngles, localScale, prefabAssetType}`. Writes additionally include skill. RectTransform nodes also return anchoredPosition, anchorMin, anchorMax, pivot and sizeDelta. Values are read back from the saved asset.

### prefab_set_sibling_index_batch

Grouped batch version of `prefab_set_sibling_index`. Each item contains `prefabPath` and the single-operation arguments. Items targeting the same asset execute in input order and save together. At most 50 items.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `items` | string | Yes | — | Nonempty JSON array of items with prefabPath and the single-operation arguments. |

**Returns**: `{success, completed, totalItems, assetCount, countsUnit, successCount, failCount, results}`. completed means processing finished; success means all asset groups succeeded. totalItems counts input items; successCount/failCount count **assets**, indicated by countsUnit:"assets". Each results entry is an asset transaction containing its own operation results or structured error. Partial success has no top-level error, so successful assets and their Undo/Workflow history remain committed. Retry only failed asset groups.

### prefab_edit_batch

Execute ordered prefab asset edits on isolated contents, saving only when every step succeeds. Only the 12 single write operations in this reference are accepted. Scene skills, nested batch calls, unknown operation arguments and per-step prefabPath are rejected.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `steps` | string | Yes | — | Nonempty JSON array of {skill,args}; 1–50 allowlisted prefab edit operations. |

**Returns**: `{success, prefabPath, results}`. Each result reports final saved state after all steps, including later renames/reparents; a subsequently removed target reports deleted/removed. Failure returns a structured error with rolledBack:true.
