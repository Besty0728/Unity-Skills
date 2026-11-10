### editor_get_state
Get current editor state.

**Returns**: `{isPlaying, isPaused, isCompiling, timeSinceStartup, unityVersion, platform, domainReload}`

`domainReload` is `{count, lastMs, recentMs, baselineMs, restartRecommended}` for this Editor session, the same object `/health` reports. When `restartRecommended` is true, reloads have slowed down several times over: tell the user to save and restart Unity. Do not close the Editor yourself.

**Example**:

```bash
# Check editor state before operations; wait while isCompiling is true
curl -s "http://localhost:<port>/skill/editor_get_state?expectProject=<project>" -d '{}'
```
