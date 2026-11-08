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

```bash
# Execute menu command
curl -s "http://localhost:<port>/skill/editor_execute_menu?expectProject=<project>" -d '{"menuPath":"File/Save"}'
```
