### batch_query_gameobjects
Query GameObjects with the unified batch filters (read-only).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `queryJson` | string | No | null | JSON filter object; null = every scene object (inactive ones only with `includeInactive`) |
| `sampleLimit` | int | No | 20 | Max sample objects returned |

`queryJson` keys (ANDed): `name` (substring), `namePattern` (.NET regex), `path` / `parentPath` (exact), `entityId`, `instanceId`, `tag`, `layer`, `active`, `isStatic`, `componentType`, `sceneName`, `prefabSource`, `includeInactive` (false), `caseSensitive` (false), `limit` (500).

**Case**: `name` and `namePattern` ignore case by default, so `"namePattern":"^Cube_"` also matches `cube_1` and `CUBE_2`. `"caseSensitive":true` matches case exactly; inside a pattern, .NET's inline `(?-i)` (case-sensitive from there on) and `(?i)` work whatever the flag says. `path`, `parentPath` and `sceneName` always ignore case.

Every skill taking `queryJson` validates it before querying or minting a token: malformed JSON, an unknown key (closest key suggested), an invalid `namePattern`, an unregistered `tag` or an unknown `layer` / `componentType` is rejected as `SEMANTIC_INVALID` with `parameter` `queryJson` / `queryJson.<key>` and `validValues` / `similarTypes`, instead of silently matching everything or nothing. `active: false` implies `includeInactive: true` (noted in `warnings`).

**Returns**: `{success, count, totalMatched, truncated, summary, query, objects: [{name, entityId, instanceId, path, scene, tag, layer, activeSelf, activeInHierarchy, componentCount}], warnings?}`. `count` is the number of matches found, stopping at `queryJson.limit`; `totalMatched` is every match before that cap and `truncated` is `true` when the cap cut the list (raise `limit` to cover the rest; the `summary` then reads `Matched N of M ...`). `objects` is a sample of at most `sampleLimit`, which never sets `truncated`. `query` echoes the parsed filter, `caseSensitive` included.
