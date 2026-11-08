### addressables_group_delete
Remove a group from the settings asset.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `groupName` | string | Yes | Matched case-insensitively. The default group is refused (`Cannot delete default group`). |

Returns `groupName` and `deleted`. The group's entries are dropped with it; the underlying assets are untouched.
