### editor_undo
Undo the last action.

**Example**:

```python
import unity_skills

# Safe experimentation with undo
unity_skills.call_skill("gameobject_delete", name="TestObject")
unity_skills.call_skill("editor_undo")  # Restore if needed
```
