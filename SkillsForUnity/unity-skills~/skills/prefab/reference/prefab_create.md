### prefab_create
Create a prefab from a scene GameObject.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Source object name |
| `entityId` | string | No* | Entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Object path |
| `savePath` | string | Yes | Prefab save path; `.prefab` is appended when missing |

*At least one source identifier required.

**Returns**: `{success, prefabPath, name, connected}`: `prefabPath` is the saved asset's path, `connected` whether the scene object is now an instance of it. A save Unity refuses is an error, not a success.
