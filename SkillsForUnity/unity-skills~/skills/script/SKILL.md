---
name: unity-script
description: Create, read and analyze C# scripts
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may proceed straight to dryRun.

## Triggers
- Authoring or editing C# code
- Searching across scripts
- Refactoring file layout
- Checking compile errors
- 编写或编辑 C# 代码、跨脚本搜索、重构文件布局、检查编译错误

# Unity Script Skills

> **BATCH-FIRST**: Use `script_create_batch` when creating 2+ scripts.
> **DESIGN-FIRST**: Before creating gameplay scripts, actively consider coupling, performance, and maintainability. In an existing project, load `../project-scout/SKILL.md` first. If the user is asking for architecture or refactoring advice, load `../architecture/SKILL.md` and then `../patterns/SKILL.md`, `../async/SKILL.md`, `../inspector/SKILL.md`, `../performance/SKILL.md`, `../script-roles/SKILL.md`, `../scene-contracts/SKILL.md`, `../testability/SKILL.md`, or `../scriptdesign/SKILL.md` as needed.

## Operating Mode

- **Approval**: 只读类 skill（`script_read` / `script_list` / `script_find_in_file` / `script_get_info` / `script_get_compile_feedback`，标 `SkillMode.SemiAuto`）直接执行；本模块**没有可 grant 的写型 skill** —— 每一个写型 skill 都命中下一条的自动拦截，grant 只会再返一次 `MODE_FORBIDDEN`。
- **Auto / Bypass**: SemiAuto 与（Bypass 下的）写型 skill 直接执行。
- **本模块全部 7 个写型 skill 都是 Delete / Reload 类高危**：`script_create` / `script_create_batch` / `script_replace` / `script_append` / `script_rename` / `script_move` / `script_delete` 均标 `MayTriggerReload = true` + `RiskLevel = "high"`（落盘 .cs 必然触发 Domain Reload），`script_delete` 另标 `SkillOperation.Delete` —— 这些 skill 被 `IsForbiddenInSemi` 静态拦截，在 Approval **和** Auto 下都返 `MODE_FORBIDDEN`，**仅 Bypass 或 Allowlist 命中可执行**，不要尝试 grant 流程。

**DO NOT** (common hallucinations):
- `script_edit` / `script_update` do not exist → use `script_replace` for find-and-replace
- `script_write` does not exist → use `script_create` (new file) or `script_replace` (modify existing)
- `scriptName` parameter must NOT include `.cs` extension
- Templates only accept: MonoBehaviour, ScriptableObject, Editor, EditorWindow

**Routing**:
- To modify existing script content → `script_replace` (find/replace) or `script_append` (add lines)
- To read script → `script_read`
- To check compile errors → `script_get_compile_feedback`
- To analyze script API → use `perception` module's `script_analyze`

## Skills Overview

| Single Object | Batch Version | Use Batch When |
|---------------|---------------|----------------|
| `script_create` | `script_create_batch` | Creating 2+ scripts |

**No batch needed**:
- `script_read` - Read script content
- `script_list` - List C# script files under a folder
- `script_get_info` - Read class name, base class, public methods/fields
- `script_delete` - Delete script
- `script_find_in_file` - Search in scripts
- `script_append` - Append content to script
- `script_replace` - Find and replace inside one script (plain text or regex)
- `script_rename` - Rename a script file in place
- `script_move` - Move a script to another folder; a missing destination folder is created automatically
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
| `namespaceName` | string | No | null | Optional namespace |
| `checkCompile` | bool | No | true | Check compilation after create |
| `diagnosticLimit` | int | No | 20 | Max compile diagnostics |

**Templates**: MonoBehaviour, ScriptableObject, Editor, EditorWindow

**Returns**: `{success, status, path, jobId, className, namespaceName, designReminder, serverAvailability?}`

Poll the returned `jobId` (or call `script_get_compile_feedback`) to obtain compile diagnostics — they are not embedded in the synchronous response. `serverAvailability` carries the transient-unavailable hint when Unity is about to reload the script domain.

### script_create_batch
Create multiple scripts in one call.

**Returns**: `{success, totalItems, successCount, failCount, results: [{success, path, className}], compilation?}`

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

**Returns**: `{path, lines, content}`

### script_delete
Delete a script.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `scriptPath` | string | Yes | Script to delete |

**Returns**: `{success, status, deleted, jobId, serverAvailability?}`

### script_find_in_file
Search for patterns in scripts.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `pattern` | string | Yes | - | Search pattern |
| `folder` | string | No | "Assets" | Search folder |
| `isRegex` | bool | No | false | Use regex |
| `limit` | int | No | 50 | Max results |

**Returns**: `{pattern, matchCount, matches: [{file, line, content}]}`

### script_append
Append content to a script.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `scriptPath` | string | Yes | - | Script path |
| `content` | string | Yes | - | Content to append |
| `atLine` | int | No | end | Line number to insert at |
| `checkCompile` | bool | No | true | Check compilation after append |
| `diagnosticLimit` | int | No | 20 | Max compile diagnostics |

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

**Returns:** `{ success, status, path, jobId, replacements, serverAvailability? }`

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

**Returns:** `{ success, status, path, jobId, oldPath, newName, serverAvailability? }`

### `script_move`
Move a script to a new folder.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `scriptPath` | string | Yes | - | Script asset path |
| `newFolder` | string | Yes | - | Destination folder. Created (and registered in AssetDatabase) automatically if missing. |
| `checkCompile` | bool | No | true | Check compilation after move |
| `diagnosticLimit` | int | No | 20 | Max compile diagnostics |

**Returns:** `{ success, status, path, jobId, oldPath, newPath, serverAvailability? }`

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.

## Common Errors

Full transport-level codes (COMPILING/RATE_LIMIT etc.) → ../../references/protocol-error-codes.md

| Error | Trigger | Fix |
|---|---|---|
| `MISSING_PARAM` | A required parameter is missing, such as `scriptName` in `script_create` or `pattern` in `script_find_in_file`. | Supply the parameter named in the error; use `mode=dryRun` to see the schema. |
| `SEMANTIC_INVALID` | The input violates a naming/path rule, such as `scriptName must not contain path separators`, `newName must not contain path separators`, or the script already exists. | Remove path separators, choose a unique name, or rename/move the existing file first. |
| `TARGET_NOT_FOUND` | The script file, MonoScript, or target directory could not be found. | Verify the path with `asset_find` or `script_list`, then retry with the correct `scriptPath`. |
| `SKILL_ERROR` | A filesystem operation failed, such as `Failed to delete script` or an AssetDatabase move/rename error. | Read the error details, resolve the underlying file/AssetDatabase issue, and retry. |
