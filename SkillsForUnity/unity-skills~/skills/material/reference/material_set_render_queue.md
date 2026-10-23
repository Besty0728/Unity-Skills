### material_set_render_queue
Set material render queue.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `entityId` | string | No* | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | GameObject instance ID |
| `path` | string | No* | Material asset path |
| `renderQueue` | int | No | Render queue value (default -1 = shader default) |
