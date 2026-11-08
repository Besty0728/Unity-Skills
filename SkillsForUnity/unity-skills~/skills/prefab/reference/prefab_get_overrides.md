### prefab_get_overrides
Count the overrides on a prefab instance; `includeDetails: true` also lists each one.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | null | Prefab instance name |
| `entityId` | string | No* | null | Entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | null | Hierarchy path of the instance (or any object inside it); picks one of several same-name instances |
| `includeDetails` | bool | No | false | Also return `overrides`, one entry per override |
| `limit` | int | No | 50 | With `includeDetails`: max overrides listed |

**Returns**: `{success, prefabPath, propertyOverrides, addedComponents, removedComponents, addedGameObjects, hasOverrides}` — `propertyOverrides`/`addedComponents`/`removedComponents`/`addedGameObjects` are **counts** (int), not arrays; `hasOverrides` is `true` when any of those counts is non-zero.

With `includeDetails: true` the same response gains `overrides`, one entry per counted override, plus `count` (entries returned, at most `limit`), `totalMatched` (every override: always the sum of the four counts) and `truncated` (`true` when `limit` cut the list: raise it to see the rest). Entries come property overrides first, then added components, removed components and added GameObjects. Every entry has `kind` (`property`, `addedComponent`, `removedComponent` or `addedGameObject`), `gameObjectPath` (relative to the instance root, `""` = the root) and `component` (`GameObject` for GameObject-level properties). `gameObjectPath` is null when Unity reports no live object for the entry (a component whose script is missing, a property whose source object has no counterpart in this instance); the entry is listed all the same, and a property entry then also carries `target`. `property` entries add `componentIndex`, `property` (the serialized path, e.g. `m_LocalPosition.x`) and `value` (plus `objectReference` for a reference override); `addedComponent` adds `componentIndex`; `addedGameObject` adds `name`. `componentIndex` is the Nth component of exactly that type on the GameObject, the index `prefab_list_components` reports and the prefab asset edits (`prefab_set_serialized_property`...) take, so a subclass component of the same base type does not shift it. No GameObject's name (`m_Name`, the root's or a child's) is ever reported as an override: renaming is not counted.
