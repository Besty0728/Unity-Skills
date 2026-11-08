### particle_get_module
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | null | GameObject name (`entityId` preferred) |
| `path` | string | No | null | Hierarchy path |
| `instanceId` | int | No | 0 | Instance ID (older Unity only) |
| `entityId` | string | No* | null | Target (or `name` / `path` / `instanceId`) |
| `module` | string | Yes | - | A module id from `particle_get_capabilities` (`main`, `shape`, `noise`, `system`, `renderer`, ...) |
