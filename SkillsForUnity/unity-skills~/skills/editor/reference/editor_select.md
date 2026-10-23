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

```python
import unity_skills

# Select and operate on object
unity_skills.call_skill("editor_select", name="Player")
selection = unity_skills.call_skill("editor_get_selection")
```
