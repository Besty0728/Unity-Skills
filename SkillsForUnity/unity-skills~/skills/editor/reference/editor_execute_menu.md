### editor_execute_menu
Execute a menu command.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `menuPath` | string | Yes | Menu item path |

**Common Menu Paths**:
| Menu Path | Action |
|-----------|--------|
| `File/Save` | Save current scene |
| `File/Build Settings...` | Open build settings |
| `Edit/Play` | Toggle play mode |
| `GameObject/Create Empty` | Create empty object |
| `Window/General/Console` | Open console |
| `Assets/Refresh` | Refresh assets |

**Example**:

```python
import unity_skills

# Execute menu command
unity_skills.call_skill("editor_execute_menu", menuPath="File/Save")
```
