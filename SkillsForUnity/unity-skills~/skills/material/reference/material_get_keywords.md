### material_get_keywords
Get all enabled shader keywords on a material.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `entityId` | string | No* | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | GameObject instance ID |
| `path` | string | No* | Material asset path |

**Returns**: `{success, target, materialPath, shader, enabledKeywords, commonKeywordStatus}` — `materialPath` is the resolved asset path of the material actually inspected (same guarantee as `material_get_properties`: `""` for a built-in material, and `""` for one embedded in another asset such as an `.fbx`, because that container's path is not feedable back in), and `commonKeywordStatus` is a `{keyword, enabled}` array covering the 14 usual suspects, so an absent keyword is distinguishable from an unchecked one.
