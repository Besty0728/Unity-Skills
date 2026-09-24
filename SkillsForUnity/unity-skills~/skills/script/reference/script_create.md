### script_create
Create one C# script from verbatim `content` or from a template.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `scriptName` | string | Yes | - | File and class name, no `.cs`, no path separators (`name` is an alias used when `scriptName` is omitted) |
| `folder` | string | No | "Assets/Scripts" | Save folder under `Assets/` or `Packages/`, created if missing; `Editor` / `EditorWindow` templates default to `Assets/Editor` |
| `template` | string | No | null | `MonoBehaviour` (null = this), `ScriptableObject`, `Editor`, `EditorWindow`, case-insensitive; any other string is written as literal source with `{CLASS}` / `{NAMESPACE}` substituted |
| `namespaceName` | string | No | null | Wraps the template class in a namespace |
| `content` | string | No | null | Complete source written verbatim (UTF-8, no BOM); `template` / `namespaceName` are then ignored |
| `checkCompile` | bool | No | true | Collect compile diagnostics in the job |
| `diagnosticLimit` | int | No | 20 | Max compile diagnostics |

**Returns**: `{success, status: "accepted", path, jobId, waitUrl, serverAvailability, className, namespaceName, warnings?, designReminder}`; GET `waitUrl` for the compile result. `warnings` flags an ignored `template` / `namespaceName` and content that declares no type named `scriptName`.

**Design first** (also sent back as `designReminder`): decide the class role before generating gameplay code (thin MonoBehaviour bridge, ScriptableObject config, or plain C# domain/service class); keep coupling low with explicit dependencies, small responsibilities and events over hidden globals; avoid needless `Update`, repeated `Find`, hot-path reflection and avoidable allocations; use clear names without cryptic abbreviations, logical folders and Inspector-friendly fields; start from the smallest structure that works instead of boilerplate dumps; add UniTask or a global event bus only when the project justifies it. In an existing project read the `project-scout` advisory doc first; for architecture or refactoring advice see the advisory docs `architecture`, then `patterns`, `async`, `inspector`, `performance`, `script-roles`, `scene-contracts`, `testability`, `scriptdesign` (each `skills/<name>/SKILL.md`).
