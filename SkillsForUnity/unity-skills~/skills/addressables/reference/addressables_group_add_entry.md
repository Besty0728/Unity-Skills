### addressables_group_add_entry
Add an asset to a group by asset path, optionally under a custom address.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `assetPath` | string | Yes | Project-relative path, e.g. `Assets/Prefabs/Enemy.prefab`. Resolved via `AssetPathToGUID`; a miss returns `Asset not found`. |
| `groupName` | string | Yes | Target group. Matched **case-insensitively**; a miss returns `Group not found` with `addressables_group_list` as the suggested fix. |
| `address` | string | No | Custom address. Omit to keep the address Addressables assigns (the asset path for a new entry, the existing address for one being moved). |

Returns `assetPath`, `groupName`, `address` (the final address read back off the entry) and `guid`.
