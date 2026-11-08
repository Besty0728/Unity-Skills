### light_set_properties_batch
Configure several lights in one call. Each item takes `name` / `entityId` / `instanceId` / `path` plus any of `r`, `g`, `b`, `a`, `intensity`, `range`, `spotAngle`, `shadows`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects |

All-or-nothing: a bad `shadows` value fails that item with `SEMANTIC_INVALID` and `validValues`, names the object in `target`, and rolls back every item the call already applied.

**Returns:** `{success, totalItems, successCount, failCount, results: [{target, success, lightType, applied, skipped, color, intensity, range, spotAngle, shadows}]}`. `applied` and `skipped` follow `light_set_properties`.

```bash
curl -s "http://localhost:<port>/skill/light_set_properties_batch?expectProject=<project>" -d '{"items":"[{\"name\":\"Light1\",\"intensity\":2.0,\"r\":1,\"g\":0.9,\"b\":0.8},{\"entityId\":\"<light2 entityId>\",\"intensity\":1.5,\"shadows\":\"Soft\"},{\"name\":\"Light3\",\"intensity\":2.0}]"}'
```
