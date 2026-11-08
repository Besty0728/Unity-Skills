### prefab_find_instances
Find the instances of a prefab in the current scene: instance roots only, never the children of an instance.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `prefabPath` | string | Yes | - | Prefab asset path to search for |
| `limit` | int | No | 50 | Maximum number of instances to return |
| `includeInactive` | bool | No | false | Also list instances that are inactive in the hierarchy |

**Returns:** `{ success, prefabPath, count, totalMatched, truncated, instances: [{ name, path, entityId, instanceId, active, nested }] }`

`count` is how many instances came back (at most `limit`), `totalMatched` every match, and `truncated: true` means `limit` cut the list: raise it to see the rest. `active` is the instance's active state in the hierarchy; `nested` is true for an instance root inside another prefab instance (a nested prefab), which counts as an instance of its own prefab. An instance of a variant belongs to the variant's path, not its base prefab's. Objects open in Prefab Mode are not part of the scene.
