### material_set_vector
Set a Vector4 property on a material.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `entityId` | string | No* | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | GameObject instance ID |
| `path` | string | No* | Material asset path |
| `propertyName` | string | Yes | Property name |
| `x`, `y`, `z`, `w` | float | Yes | Vector components |
