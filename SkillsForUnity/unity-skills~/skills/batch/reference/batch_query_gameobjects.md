### batch_query_gameobjects
Query GameObjects with the unified batch filters (read-only).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `queryJson` | string | No | null | JSON filter object; null = every scene object (inactive ones only with `includeInactive`) |
| `sampleLimit` | int | No | 20 | Max sample objects returned |

`queryJson` keys (ANDed): `name` (substring), `namePattern` (regex), `path` / `parentPath` (exact), `entityId`, `instanceId`, `tag`, `layer`, `active`, `isStatic`, `componentType`, `sceneName`, `prefabSource`, `includeInactive` (false), `limit` (500).

**Returns**: `{success, count, summary, query, objects: [{name, entityId, instanceId, path, scene, tag, layer, activeSelf, activeInHierarchy, componentCount}]}`; `count` is the full match count, `objects` at most `sampleLimit`.
