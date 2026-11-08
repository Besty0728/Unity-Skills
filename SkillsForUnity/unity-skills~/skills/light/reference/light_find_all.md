### light_find_all
Find all lights in the open scenes.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `lightType` | string | No | null | Filter by type |
| `limit` | int | No | 50 | Maximum results |

**Returns:** `{count, lights: [{name, instanceId, path, lightType, intensity, enabled}]}`
