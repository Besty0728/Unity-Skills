### material_set_texture
Set material texture.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | - | GameObject name |
| `entityId` | string | No* | - | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | 0 | GameObject instance ID |
| `path` | string | No* | - | Material asset path |
| `texturePath` | string | Yes | - | Texture asset path |
| `propertyName` | string | No | auto-detect | Texture property |
