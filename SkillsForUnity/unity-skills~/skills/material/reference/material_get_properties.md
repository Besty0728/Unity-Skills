### material_get_properties
Get all material properties.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `entityId` | string | No* | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | GameObject instance ID |
| `path` | string | No* | Material asset path |

**Returns**: `{success, target, materialPath, shader, renderQueue, keywords, giFlags, properties: {...}}`

`properties` is a **dictionary of five typed groups**, not a flat list: `colors`, `floats`, `vectors`, `textures`, `integers`. Each group is an array of `{name, description, value}` (floats also carry `min`/`max`; a texture's `value` is the texture's name or null). So a property lookup is `properties.colors[i].name`, never `properties[i]`.

`materialPath` is the asset path of the material actually inspected, and it is only ever a path you can feed straight back in. It is `""` for a built-in material, and also `""` for a material **embedded in another asset** such as an `.fbx`: that material does have a containing file, but the file's main asset is a GameObject rather than a Material, so handing the `.fbx` path back would fail with "material not found". An empty `materialPath` therefore means "this material cannot be addressed by path" — resolve it through its GameObject instead, and do not synthesise a path from the model's. Read it whenever you resolved the material through a GameObject: a non-empty value is the file a subsequent `material_set_*` on that object would write.
