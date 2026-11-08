### editor_select
Select a GameObject.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Object name |
| `entityId` | string | No* | Entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Object path |

*One identifier required

**Example**:

```bash
# Select and operate on object
curl -s "http://localhost:<port>/skill/editor_select?expectProject=<project>" -d '{"name":"Player"}'
curl -s "http://localhost:<port>/skill/editor_get_selection?expectProject=<project>" -d '{}'
```
