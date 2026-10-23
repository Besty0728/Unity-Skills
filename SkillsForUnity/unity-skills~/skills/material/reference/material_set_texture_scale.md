### material_set_texture_scale
Set texture scale (tiling).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `entityId` | string | No* | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | GameObject instance ID |
| `path` | string | No* | Material asset path |
| `propertyName` | string | No | Texture property name |
| `x`, `y` | float | Yes | Scale values |
