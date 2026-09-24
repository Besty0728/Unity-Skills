### gameobject_find
Find GameObjects matching criteria (filters combine).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | null | Name filter: case-insensitive substring, or a case-sensitive .NET regex with `useRegex` |
| `tag` | string | No | null | Tag filter; must be a registered tag |
| `layer` | string | No | null | Layer name filter |
| `component` | string | No | null | Component type filter |
| `useRegex` | bool | No | false | Treat `name` as a regex |
| `limit` | int | No | 50 | Max results |

**Returns**: `{count, objects: [{name, entityId, instanceId, path, tag, layer, position}]}` (`position` world).

An unregistered `tag` is rejected (`SEMANTIC_INVALID` listing valid tags); an unknown `layer` or `component` is silently ignored, so it does not filter.
