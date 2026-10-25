### workflow_checkpoint
Names a point in the undo history. Without `at` it adds a checkpoint before the caller's next recorded step; with `at` it renames an existing checkpoint.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| label | string | Yes | - | The name, e.g. `"before lighting"` |
| at | string | No | null | A `c<n>` id from `workflow_list` to rename instead of adding one |

**Returns:** `{ id, label }`. An unknown `at` returns `NOT_FOUND`.

Any write skill also takes a `_checkpoint` body key (`{"_checkpoint":"before lighting", ...}`), which names the checkpoint before that call's step. A failed call leaves no checkpoint. A `/skills/batch` body takes the same key for its first recorded step.
