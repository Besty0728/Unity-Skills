### workflow_undone_list
List restores that can still be undone with `workflow_redo_task`.

No parameters.

**Returns:** `{ success, count, restores: [{ seq, to, guard, agent, steps }] }`; `steps` is the number of steps the restore covered.
