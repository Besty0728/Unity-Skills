### batch_query_gameobjects
Query GameObjects with the unified batch filters (read-only).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `queryJson` | string | No | null | JSON filter object; null = every scene object (inactive ones only with `includeInactive`) |
| `sampleLimit` | int | No | 20 | Max sample objects returned |

`queryJson` keys (ANDed): `name` (substring), `namePattern` (regex), `path` / `parentPath` (exact), `entityId`, `instanceId`, `tag`, `layer`, `active`, `isStatic`, `componentType`, `sceneName`, `prefabSource`, `includeInactive` (false), `limit` (500).

Every skill taking `queryJson` validates it before querying or minting a token: malformed JSON, an unknown key (closest key suggested), an invalid `namePattern`, an unregistered `tag` or an unknown `layer` / `componentType` is rejected as `SEMANTIC_INVALID` with `parameter` `queryJson` / `queryJson.<key>` and `validValues` / `similarTypes`, instead of silently matching everything or nothing. `active: false` implies `includeInactive: true` (noted in `warnings`).

**Returns**: `{success, count, summary, query, objects: [{name, entityId, instanceId, path, scene, tag, layer, activeSelf, activeInHierarchy, componentCount}], warnings?}`; `count` is the full match count, `objects` at most `sampleLimit`.
