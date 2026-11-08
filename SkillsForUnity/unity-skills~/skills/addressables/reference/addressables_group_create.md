### addressables_group_create
Create a group with the standard `BundledAssetGroupSchema` + `ContentUpdateGroupSchema` pair. Not set as the default group and not read-only.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `groupName` | string | Yes | Desired group name. Rejected when blank. |

Returns `groupName` (**the name that actually landed on disk**), `requestedName`, `renamed`, `guid`, and a `note` when the two differ. The skill renames on collision instead of failing: always use the returned `groupName` in follow-up calls.
