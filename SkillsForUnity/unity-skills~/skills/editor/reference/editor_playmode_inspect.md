### editor_playmode_inspect
Inspect a GameObject's live runtime state: transform (position/rotation/scale), `activeSelf`/`activeInHierarchy`, and — when `componentType` is given — that component's public fields and properties (reuses the same reflection helper as `component_get_properties`, not reimplemented). Works during Play Mode, including while paused, and also in Edit Mode, where it returns editor-time values — check `isPlaying`/`isPaused` in the response to know which state the values reflect.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Object name |
| `entityId` | string | No* | Entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Object path |
| `componentType` | string | No | Component type name; when set, also returns its public fields/properties |

*One identifier required

**Returns**: `{success, gameObject, entityId, instanceId, path, activeSelf, activeInHierarchy, transform: {position, localPosition, rotation, localScale}, isPlaying, isPaused, component}`. `component` is `null` when `componentType` is omitted, an error object when the type isn't found or isn't attached, or `{gameObject, component, fullTypeName, properties, fields}` (same shape as `component_get_properties`) otherwise.
