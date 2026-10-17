### gameobject_find
Find GameObjects matching criteria (filters combine).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | null | Name filter: case-insensitive substring, or a case-sensitive .NET regex with `useRegex` |
| `tag` | string | No | null | Tag filter; must be a registered tag |
| `layer` | string | No | null | Layer name filter |
| `component` | string | No | null | Component type filter |
| `useRegex` | bool | No | false | Treat `name` as a regex |
| `limit` | int | No | 50 | Max objects returned; a longer match list is cut and flagged by `truncated` |

**Returns**: `{count, totalMatched, truncated, objects: [{name, entityId, instanceId, path, tag, layer, position}]}` (`position` world). `count` is the number of objects returned (at most `limit`); `totalMatched` is every match before the cut and `truncated` is `true` when `limit` cut the list (raise `limit` to see the rest). Router paging (`pageOffset` / `pageLimit`) pages `objects` and adds its own `totalCount` / `limit` for the page; `totalMatched` and `truncated` still describe this skill's own cut.

An unregistered `tag` is rejected (`SEMANTIC_INVALID` listing valid tags); an unknown `layer` or `component`, or an invalid regex, is likewise rejected (`SEMANTIC_INVALID` with `validValues` / `similarTypes`), not silently ignored.
