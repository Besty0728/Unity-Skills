### component_set_serialized_property_batch
Set Inspector serialized properties on several components in one call.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects |

**Item properties**: `name`, `instanceId`, `path`, `componentType`, `propertyPath`, `value`, `referenceName`, `referenceInstanceId`, `referencePath`, `assetPath`, `objectType`. `value` is a string here (numbers are accepted; a JSON object fails to parse, so send vectors as `"1,2,3"`).

**Returns**: `{success, totalItems, successCount, failCount, results}`; each result is the `component_set_serialized_property` result, or `{error, target}`. All-or-nothing.
