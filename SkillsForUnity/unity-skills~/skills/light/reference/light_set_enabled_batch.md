### light_set_enabled_batch
Enable or disable several lights. Unlike `light_set_enabled`, `enabled` is required on every item: an omitted value is rejected, not guessed. All-or-nothing.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of `{name|entityId|instanceId|path, enabled}` |

**Returns:** `{success, totalItems, successCount, failCount, results: [{target, success, enabled}]}`. An item missing `enabled` fails with `MISSING_PARAM` and rolls back every light the call already toggled.

```bash
curl -s "http://localhost:<port>/skill/light_set_enabled_batch?expectProject=<project>" -d '{"items":"[{\"name\":\"Torch1\",\"enabled\":false},{\"name\":\"Torch2\",\"enabled\":false}]"}'
```
