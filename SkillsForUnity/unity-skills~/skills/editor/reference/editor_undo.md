### editor_undo
Undo the last action.

**Example**:

```bash
# Safe experimentation with undo
curl -s "http://localhost:<port>/skill/gameobject_delete?expectProject=<project>" -d '{"name":"TestObject"}'
curl -s "http://localhost:<port>/skill/editor_undo?expectProject=<project>" -d '{}'
```
