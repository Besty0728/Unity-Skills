### editor_get_state
Get current editor state.

**Returns**: `{isPlaying, isPaused, isCompiling, timeSinceStartup, unityVersion, platform}`

**Example**:

```bash
# Check editor state before operations; wait while isCompiling is true
curl -s "http://localhost:<port>/skill/editor_get_state?expectProject=<project>" -d '{}'
```
