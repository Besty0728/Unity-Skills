### editor_get_context
Get full editor context including selection, assets, and scene info.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `includeComponents` | bool | No | false | Include component list |
| `includeChildren` | bool | No | false | Include children info |

**Returns**:
- `selectedGameObjects`: `{count, objects}`, the objects selected in the Hierarchy (entityId, instanceId, path, tag, layer)
- `selectedAssets`: Assets in Project window (GUID, path, type, isFolder)
- `activeScene`: Current scene info (name, path, isDirty)
- `focusedWindow`: Name of focused editor window
- `isPlaying`, `isCompiling`: Editor state

**Example**:

```bash
# Full context (useful for understanding current state)
curl -s "http://localhost:<port>/skill/editor_get_context?expectProject=<project>" -d '{"includeComponents":true}'
```
