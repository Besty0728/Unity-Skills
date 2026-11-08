### workflow_session_start
Start a new session (conversation-level). All changes will be tracked and can be undone together.
**Call this at the beginning of each conversation.**

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| tag | string | No | null | Label for the session |

**Returns:** `{ success, sessionId, message }`

**Example**


```
curl -s "http://localhost:<port>/skill/workflow_session_start?expectProject=<project>" -d '{"tag":"Build Player"}'
curl -s "http://localhost:<port>/skill/gameobject_create?expectProject=<project>" -d '{"name":"Player","primitiveType":"Capsule"}'
curl -s "http://localhost:<port>/skill/component_add?expectProject=<project>" -d '{"name":"Player","componentType":"Rigidbody"}'
curl -s "http://localhost:<port>/skill/workflow_session_end?expectProject=<project>" -d '{}'
curl -s "http://localhost:<port>/skill/workflow_session_list?expectProject=<project>" -d '{}'
curl -s "http://localhost:<port>/skill/workflow_session_undo?expectProject=<project>" -d '{"sessionId":"<sessionId from workflow_session_list>"}'
```
