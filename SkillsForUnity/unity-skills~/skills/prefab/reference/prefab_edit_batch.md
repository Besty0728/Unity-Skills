### prefab_edit_batch
Execute ordered prefab asset edits on isolated contents, saving only when every step succeeds. Only the 12 single write operations in this reference are accepted. Scene skills, nested batch calls, unknown operation arguments and per-step prefabPath are rejected.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `steps` | string | Yes | — | Nonempty JSON array of {skill,args}; 1–50 allowlisted prefab edit operations. |

**Returns**: `{success, prefabPath, results}`. Each result reports final saved state after all steps, including later renames/reparents; a subsequently removed target reports deleted/removed. Failure returns a structured error with rolledBack:true.
