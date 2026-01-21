---
name: unity-script
description: Create and manage C# scripts in Unity Editor via REST API
---

# Unity Script Skills

Work with C# scripts - create, read, delete, and search within scripts.

## Capabilities

- Create C# scripts from templates
- Read script contents
- Delete scripts
- Search for patterns in scripts

## Skills Reference

| Skill | Description |
|-------|-------------|
| `script_create` | Create C# script |
| `script_read` | Read script content |
| `script_delete` | Delete script |
| `script_find_in_file` | Search in scripts |

## Parameters

### script_create

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `scriptName` | string | Yes | - | Script class name |
| `folder` | string | No | "Assets/Scripts" | Save folder |
| `template` | string | No | "MonoBehaviour" | Template type |

### script_read

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `scriptPath` | string | Yes | Script asset path |

### script_delete

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `scriptPath` | string | Yes | Script to delete |

### script_find_in_file

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `pattern` | string | Yes | - | Search pattern |
| `folder` | string | No | "Assets" | Search folder |
| `isRegex` | bool | No | false | Use regex |

## Script Templates

| Template | Description |
|----------|-------------|
| `MonoBehaviour` | Standard Unity component |
| `ScriptableObject` | Data container |
| `Editor` | Custom editor |
| `EditorWindow` | Editor window |

## Example Usage

```python
import unity_skills

# Create a MonoBehaviour script
unity_skills.call_skill("script_create",
    scriptName="PlayerController",
    folder="Assets/Scripts/Player",
    template="MonoBehaviour"
)

# Create a ScriptableObject
unity_skills.call_skill("script_create",
    scriptName="GameSettings",
    folder="Assets/Scripts/Data",
    template="ScriptableObject"
)

# Read script content
content = unity_skills.call_skill("script_read",
    scriptPath="Assets/Scripts/Player/PlayerController.cs"
)
print(content['result']['content'])

# Search for usage of a method
results = unity_skills.call_skill("script_find_in_file",
    pattern="GetComponent",
    folder="Assets/Scripts"
)
for match in results['result']['matches']:
    print(f"{match['file']}: Line {match['line']}")

# Search with regex
results = unity_skills.call_skill("script_find_in_file",
    pattern="void\\s+Update\\s*\\(",
    folder="Assets/Scripts",
    isRegex=True
)

# Delete a script
unity_skills.call_skill("script_delete",
    scriptPath="Assets/Scripts/OldScript.cs"
)
```

## Response Format

### script_create Response

```json
{
  "status": "success",
  "skill": "script_create",
  "result": {
    "success": true,
    "path": "Assets/Scripts/Player/PlayerController.cs",
    "className": "PlayerController",
    "template": "MonoBehaviour"
  }
}
```

### script_find_in_file Response

```json
{
  "result": {
    "success": true,
    "pattern": "GetComponent",
    "totalMatches": 12,
    "matches": [
      {
        "file": "Assets/Scripts/Player/PlayerController.cs",
        "line": 25,
        "content": "rb = GetComponent<Rigidbody>();"
      },
      {
        "file": "Assets/Scripts/Enemy/EnemyAI.cs",
        "line": 18,
        "content": "animator = GetComponent<Animator>();"
      }
    ]
  }
}
```

## Best Practices

1. Use meaningful script names matching class name
2. Organize scripts in logical folders
3. Use templates for correct base class
4. Wait for compilation after creating scripts
5. Use regex search for complex patterns
