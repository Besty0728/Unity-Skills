### editor_get_changes
Read the persistent change journal at `Library/UnitySkills/editor_changes.jsonl`. It captures scene object/component/property summaries plus imported, deleted, and moved asset paths across Domain Reloads. It never parses or returns raw `.unity` YAML.

On disk the journal is up to 11 files: the active `editor_changes.jsonl` plus rotated `editor_changes.1.jsonl` (newest) to `editor_changes.10.jsonl`. A file rotates at 2,000 entries or 8 MiB, is never rewritten, and the oldest rotated file is dropped. This skill serves only the newest 500 entries (the in-memory window, `retention`), which it reloads from those files after a Domain Reload.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `since` | long | No | `0` | Previous response cursor; `0` reads retained history |
| `types` | string | No | `all` | Comma-separated `scene`, `file`, `undo`, or `lifecycle` |
| `source` | string | No | `all` | `editor`/`manual`, `rest`, or `all` |
| `limit` | int | No | `100` | Newest entries to return, 1-500 |

**Returns:** `{hasChanges, cursor, oldestSeq, dropped, truncated, changes}`. Save `cursor` for the next call. `dropped=true` means older changes fell outside the 500-entry window, so rebuild full scene context.

**Entry fields:** `seq`, `type`, `source`, `agent`, `isPlaying`, `tsUtc`, `payload`. Entries written by older versions lack `agent` and `isPlaying`.
- `agent`: for `source:"rest"` the caller's agent id; `"mixed"` when callers with different ids overlapped in one REST window (calls in quick succession can share a window); `"unknown"` when the call carried no identity; `null` for `source:"editor"`. It is attribution evidence, not proof.
- `isPlaying`: `EditorApplication.isPlaying` when the entry was written (Play Mode changes are transient).
- Large changes are split, not cut: one asset callback or scene-change flush becomes consecutive entries of at most 200 changes (per list for `file_changes`), numbered `part` / `parts` when split. Only past 10,000 per flush does the last entry stop listing and count the rest in `omitted`.
- `file_changes` payload: `count` is the number of listed paths; `importedTotal` / `deletedTotal` / `movedTotal` count the paths the entry accounts for (listed plus `omitted`).
- `gameobject_reparented` carries `entityId`, `instanceId`, `scenePath`; `gameobject_destroyed` carries `entityId`, `instanceId`. `instanceId` is always `0` on Unity 6000.4+, so compare objects by `entityId`.

**Example**:

```python
import unity_skills

# On resume or after file-watch notification, inspect semantic changes first.
changes = unity_skills.call_skill("editor_get_changes", since=last_cursor)
last_cursor = changes["cursor"]
```
