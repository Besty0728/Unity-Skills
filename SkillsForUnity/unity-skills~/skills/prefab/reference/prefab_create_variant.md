### prefab_create_variant
Create a prefab variant from an existing prefab.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `sourcePrefabPath` | string | Yes | - | Path to the source prefab asset |
| `variantPath` | string | Yes | - | Save path for the new variant |

**Returns:** `{ success, sourcePath, variantPath, name, isVariant }`: `variantPath` is the saved asset's path (`.prefab` appended when missing), `isVariant` whether Unity stored it as a variant of the source.
