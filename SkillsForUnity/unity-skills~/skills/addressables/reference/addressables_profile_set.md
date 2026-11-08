### addressables_profile_set
Switch the active profile by name.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `profileName` | string | Yes | Must be a name from `addressables_profile_get`. A miss returns `Profile not found` with `addressables_profile_get` as the suggested fix. |

Returns `activeProfile`, `profileId`, and `changed` (false when that profile was already active).
