### workflow_plan
Generate a combined execution plan for multiple skills on the server side.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `skillsJson` | string | Yes | - | JSON array of `{ "name": "...", "params": { ... } }` entries |

**Returns:** `{ totalSteps, totalRisk, steps, dependencies, warnings, mayDisconnect }`
