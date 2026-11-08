### prefab_instantiate_batch
Instantiate multiple prefabs in one call.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `items` | json string | Yes | Array of instantiation configs |

**Item properties**: `prefabPath`, `name`, `x`, `y`, `z` (local position, relative to the parent if set), `rotX`, `rotY`, `rotZ`, `scaleX`, `scaleY`, `scaleZ`, `parentEntityId`, `parentName`, `parentInstanceId`, `parentPath`

**Returns**: `{success, totalItems, successCount, failCount, results: [{success, name, entityId, instanceId, position, localPosition}]}`: `position` is the world position read back, `localPosition` the local one. All-or-nothing: one failed item removes every instance the call created (`rolledBack: true`).

```bash
curl -s "http://localhost:<port>/skill/prefab_instantiate_batch?expectProject=<project>" -d '{"items":"[{\"prefabPath\":\"Assets/Prefabs/Enemy.prefab\",\"x\":0,\"z\":0,\"name\":\"Enemy_01\"},{\"prefabPath\":\"Assets/Prefabs/Enemy.prefab\",\"x\":2,\"z\":0,\"name\":\"Enemy_02\"}]"}'
```
