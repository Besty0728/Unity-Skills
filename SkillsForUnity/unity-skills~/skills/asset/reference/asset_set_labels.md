### asset_set_labels
Set labels on an asset (overwrites existing labels).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `assetPath` | string | Yes | Asset path |
| `labels` | string | Yes | Comma-separated labels (e.g. `"ui,icon,hud"`). Empty entries are dropped |

**Returns**: `{success, assetPath, labels: [...]}`
