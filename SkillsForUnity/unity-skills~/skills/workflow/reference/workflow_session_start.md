### workflow_session_start
Start a new session (conversation-level). All changes will be tracked and can be undone together.
**Call this at the beginning of each conversation.**

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| tag | string | No | null | Label for the session |

**Returns:** `{ success, sessionId, message }`

**Example**


```python
import unity_skills

# Session-level: wrap entire conversation for bulk undo
unity_skills.call_skill("workflow_session_start", tag="Build Player")
unity_skills.call_skill("gameobject_create", name="Player", primitiveType="Capsule")
unity_skills.call_skill("component_add", name="Player", componentType="Rigidbody")
unity_skills.call_skill("workflow_session_end")
# Later: undo entire session
sessions = unity_skills.call_skill("workflow_session_list")
unity_skills.call_skill("workflow_session_undo", sessionId=sessions["sessions"][0]["sessionId"])
```
