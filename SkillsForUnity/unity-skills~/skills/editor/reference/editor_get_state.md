### editor_get_state
Get current editor state.

**Returns**: `{isPlaying, isPaused, isCompiling, timeSinceStartup, unityVersion, platform}`

**Example**:

```python
import unity_skills

# Check editor state before operations
state = unity_skills.call_skill("editor_get_state")
if state['isCompiling']:
    print("Wait for compilation to finish")
```
