---
name: unity-script
description: "C# script management for creating, reading, and modifying Unity scripts."
---

# Unity Script Skills

> **BATCH-FIRST**: Use `script_create_batch` when creating 2+ scripts.
> **DESIGN-FIRST**: Before creating gameplay scripts, actively consider coupling, performance, and maintainability. In an existing project, load `../project-scout/SKILL.md` first. If the user is asking for architecture or refactoring advice, load `../architecture/SKILL.md` and then `../patterns/SKILL.md`, `../async/SKILL.md`, `../inspector/SKILL.md`, `../performance/SKILL.md`, `../script-roles/SKILL.md`, `../scene-contracts/SKILL.md`, `../testability/SKILL.md`, or `../scriptdesign/SKILL.md` as needed.

## Skills Overview

| Single Object | Batch Version | Use Batch When |
|---------------|---------------|----------------|
| `script_create` | `script_create_batch` | Creating 2+ scripts |

**No batch needed**:
- `script_read` - Read script content
- `script_delete` - Delete script
- `script_find_in_file` - Search in scripts
- `script_append` - Append content to script
- `script_get_compile_feedback` - Check compile errors for one script after Unity finishes compiling
- `create_script()` in `scripts/unity_skills.py` now waits for Unity to come back once and refreshes compile feedback automatically after script creation.

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

**Returns**: `{success, path, className, namespaceName, designReminder, compilation?}`

`compilation` includes:
- `isCompiling`
- `hasErrors`
- `errorCount`
- `errors[]`
- `nextAction`

### script_create_batch
Create multiple scripts in one call.

Before batch creation, decide whether each script should be:
- a thin `MonoBehaviour` bridge
- a `ScriptableObject` configuration asset
- or a plain C# domain/service class generated from a custom template

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

### script_get_compile_feedback
Get compile diagnostics related to one script.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `scriptPath` | string | Yes | - | Script path |
| `limit` | int | No | 20 | Max diagnostics |

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

## Important: Domain Reload And Compile Feedback

After creating or editing scripts, Unity triggers a Domain Reload (recompilation). Use the returned `compilation` field first. If `isCompiling=true`, wait for Unity to finish and then call `script_get_compile_feedback`.

```python
import time

result = unity_skills.call_skill("script_create", scriptName="MyScript")
time.sleep(5)  # Wait for Unity to recompile if result["compilation"]["isCompiling"] is true
feedback = unity_skills.call_skill("script_get_compile_feedback", scriptPath=result["path"])
unity_skills.call_skill("component_add", name="Player", componentType="MyScript")
```

## Best Practices

1. Use meaningful script names matching class name
2. Organize scripts in logical folders
3. Before creating gameplay code, decide the class role first: MonoBehaviour, ScriptableObject, or plain C# helper/service
4. Actively reduce coupling: prefer explicit dependencies, small responsibilities, and event-driven notifications over hidden globals
5. Actively consider performance: avoid unnecessary `Update`, repeated `Find`, reflection in hot paths, and avoidable allocations
6. Actively consider maintainability: clear naming, explicit ownership, Inspector-friendly fields, and simple module boundaries
7. Avoid giant boilerplate/template dumps. Start from the smallest structure that solves the current need
8. Do not default to UniTask or a global event bus unless the project context justifies them
9. Avoid cryptic abbreviations in class, field, and method names unless they are already a project convention
10. Use templates for correct base class
11. Wait for compilation after creating scripts
12. After script edits, call `script_get_compile_feedback` and fix reported errors
13. Use regex search for complex patterns
14. **Use batch creation to minimize Domain Reloads**

---

## Additional Skills

### `script_replace`
Find and replace content in a script file.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `scriptPath` | string | Yes | - | Script asset path |
| `find` | string | Yes | - | Text or pattern to find |
| `replace` | string | Yes | - | Replacement text |
| `isRegex` | bool | No | false | Use regex matching |
| `checkCompile` | bool | No | true | Check compilation after replace |
| `diagnosticLimit` | int | No | 20 | Max compile diagnostics |

**Returns:** `{ success, path, replacements, compilation? }`

### `script_list`
List C# script files in the project.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `folder` | string | No | "Assets" | Folder to search in |
| `filter` | string | No | null | Filter string for path matching |
| `limit` | int | No | 100 | Max results |

**Returns:** `{ count, scripts: [{ path, name }] }`

### `script_get_info`
Get script info (class name, base class, methods).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `scriptPath` | string | Yes | - | Script asset path |

**Returns:** `{ path, className, baseClass, namespaceName, isMonoBehaviour, publicMethods, publicFields }`

### `script_rename`
Rename a script file.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `scriptPath` | string | Yes | - | Script asset path |
| `newName` | string | Yes | - | New script name (without extension) |
| `checkCompile` | bool | No | true | Check compilation after rename |
| `diagnosticLimit` | int | No | 20 | Max compile diagnostics |

**Returns:** `{ success, path, oldPath, newName, compilation? }`

### `script_move`
Move a script to a new folder.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `scriptPath` | string | Yes | - | Script asset path |
| `newFolder` | string | Yes | - | Destination folder |
| `checkCompile` | bool | No | true | Check compilation after move |
| `diagnosticLimit` | int | No | 20 | Max compile diagnostics |

**Returns:** `{ success, path, oldPath, newPath, compilation? }`
