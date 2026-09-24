### batch_preview_set_property
Preview setting one component property or field across the queried objects.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `queryJson` | string | No | null | JSON filter object |
| `componentType` | string | Yes | - | Target component type |
| `propertyName` | string | Yes | - | C# property or field name |
| `value` | string | No | null | Literal value |
| `referencePath` | string | No | null | Scene reference path |
| `referenceName` | string | No | null | Scene reference object name |
| `assetPath` | string | No | null | Asset reference path |
| `sampleLimit` | int | No | 10 | Max preview items |

Value source precedence and encodings are those of `component_set_property` (`assetPath` > reference > `value`). Objects without the component, without the member, with an unconvertible value, or already holding the value are skipped and counted in `skipReasons`.

**Returns** (shared by every preview and fixer): `{success, status: "preview", confirmToken, kind, summary, riskLevel, rollbackAvailable, mayCreateJob, targetCount, executableCount, skipCount, sampleChanges: [{targetName, targetPath, entityId, action, before, after}], skipReasons: [{reason, count}], surfaceExclusion?}`. Commit with `batch_execute(confirmToken)`.
