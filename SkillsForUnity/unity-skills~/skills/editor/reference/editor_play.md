### editor_play
Enter play mode. Warning: any unsaved scene changes made during Play mode will be lost when exiting.

**Returns**: `{success, mode, jobId}` — `mode="playing"`, `jobId` returned from `AsyncJobService` so callers can poll `entering_play_mode` completion.
