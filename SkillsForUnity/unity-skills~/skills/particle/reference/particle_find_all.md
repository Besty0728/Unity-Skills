### particle_find_all
Systems in the open scenes in Hierarchy order (no assets, no Prefab Mode objects).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `rootEntityId` | string | No | null | Only under this object (also `rootName` / `rootPath` / `rootInstanceId`) |
| `includeInactive` | bool | No | true | Include inactive objects |
| `limit` | int | No | 100 | Page size, 1-500 |
| `offset` | int | No | 0 | Systems to skip |

**Returns**: `{total, count, offset, limit, truncated, systems: [{name, entityId, instanceId, path, scene, activeInHierarchy, isPlaying, isPaused, duration, loop, maxParticles, emissionEnabled, renderMode, material, isPrefabInstance}]}`
