### workflow_clear_history
Permanently clear **ALL** workflow history: every task, every restore record, and every backed-up file blob in the content-addressed store. High-risk and irreversible.
**This ONLY deletes tracking history — it does NOT undo or revert any change already applied to your project assets, scenes, or settings.** To roll changes back, use `workflow_undo_task` / `workflow_session_undo` *before* clearing.

Marked `SkillOperation.Delete` + `RiskLevel="high"`, so it is `NeverInSemi` (returns `MODE_FORBIDDEN` in Approval/Auto; only Bypass or an Allowlist hit can call it).

**Parameters:** None

**Returns:** `{ success, before, after, message }` where `before`/`after` each report `{ tasks, restores, historyFileBytes, fileStoreBytes }`.
