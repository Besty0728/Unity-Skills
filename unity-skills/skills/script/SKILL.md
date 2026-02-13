---
name: unity-script
description: "C# script management. Use when users want to create, read, or modify C# scripts. Triggers: script, C#, MonoBehaviour, code, class, method, Unity脚本, Unity代码, Unity创建脚本."
---

# Unity Script Skills

> **BATCH-FIRST**: Use `script_create_batch` when creating 2+ scripts.

## Skills Overview

| Single Object | Batch Version | Use Batch When |
|---------------|---------------|----------------|
| `script_create` | `script_create_batch` | Creating 2+ scripts |

**No batch needed**:
- `script_read` - Read script content
- `script_delete` - Delete script
- `script_find_in_file` - Search in scripts
- `script_append` - Append content to script

---

## Skills

### script_create
Create a C# script from template.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `scriptName` | string | Yes | - | Script class name |
| `folder` | string | No | "Assets/Scripts" | Save folder |
| `template` | string | No | "MonoBehaviour" | Template type |
| `namespace` | string | No | null | Optional namespace |

**Templates**: MonoBehaviour, ScriptableObject, Editor, EditorWindow

**Returns**: `{success, path, className, template}`

### script_create_batch
Create multiple scripts in one call.

```python
unity_skills.call_skill("script_create_batch", items=[
    {"scriptName": "PlayerController", "folder": "Assets/Scripts/Player", "template": "MonoBehaviour"},
    {"scriptName": "EnemyAI", "folder": "Assets/Scripts/Enemy", "template": "MonoBehaviour"},
    {"scriptName": "GameSettings", "folder": "Assets/Scripts/Data", "template": "ScriptableObject"}
])
```

### script_read
Read script content.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `scriptPath` | string | Yes | Script asset path |

**Returns**: `{success, path, content}`

### script_delete
Delete a script.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `scriptPath` | string | Yes | Script to delete |

### script_find_in_file
Search for patterns in scripts.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `pattern` | string | Yes | - | Search pattern |
| `folder` | string | No | "Assets" | Search folder |
| `isRegex` | bool | No | false | Use regex |
| `limit` | int | No | 100 | Max results |

**Returns**: `{success, pattern, totalMatches, matches: [{file, line, content}]}`

### script_append
Append content to a script.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `scriptPath` | string | Yes | - | Script path |
| `content` | string | Yes | - | Content to append |
| `atLine` | int | No | end | Line number to insert at |

---

## Example: Efficient Script Setup

```python
import unity_skills

# BAD: 3 API calls + 3 Domain Reloads
unity_skills.call_skill("script_create", scriptName="PlayerController", folder="Assets/Scripts/Player")
# Wait for Domain Reload...
unity_skills.call_skill("script_create", scriptName="EnemyAI", folder="Assets/Scripts/Enemy")
# Wait for Domain Reload...
unity_skills.call_skill("script_create", scriptName="GameManager", folder="Assets/Scripts/Core")
# Wait for Domain Reload...

# GOOD: 1 API call + 1 Domain Reload
unity_skills.call_skill("script_create_batch", items=[
    {"scriptName": "PlayerController", "folder": "Assets/Scripts/Player"},
    {"scriptName": "EnemyAI", "folder": "Assets/Scripts/Enemy"},
    {"scriptName": "GameManager", "folder": "Assets/Scripts/Core"}
])
# Wait for Domain Reload once...
```

## Important: Domain Reload

After creating scripts, Unity triggers a Domain Reload (recompilation). Wait 3-5 seconds before making additional calls.

```python
import time

unity_skills.call_skill("script_create", scriptName="MyScript")
time.sleep(5)  # Wait for Unity to recompile
unity_skills.call_skill("component_add", name="Player", componentType="MyScript")
```

## Best Practices

1. Use meaningful script names matching class name
2. Organize scripts in logical folders
3. Use templates for correct base class
4. Wait for compilation after creating scripts
5. Use regex search for complex patterns
6. **Use batch creation to minimize Domain Reloads**
