### script_find_in_file
Search every `.cs` file under a folder, line by line.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `pattern` | string | Yes | - | Case-sensitive substring; a .NET regex with `isRegex` |
| `folder` | string | No | "Assets" | Search folder (recursive) |
| `isRegex` | bool | No | false | Use regex; handy for complex patterns |
| `limit` | int | No | 50 | Max matches; the search stops there |

**Returns**: `{pattern, matchCount, matches: [{file, line, content}]}` (`line` 1-based, `content` trimmed).
