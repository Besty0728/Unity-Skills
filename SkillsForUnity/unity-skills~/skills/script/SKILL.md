---
name: unity-script
description: Create, read and analyze C# scripts
---

## Triggers
- Authoring or editing C# code
- Searching across scripts
- Refactoring file layout
- Checking compile errors
- 编写或编辑 C# 代码、跨脚本搜索、重构文件布局、检查编译错误

# Unity Script Skills

Create, edit, move and inspect C# scripts and get their compile result. Parameters come from the schema (recommend `includeSchema=true` or `/skills/schema?names=`), which notes the non-obvious ones — never from a skill's name. Execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## Operating Mode
Read-only `SemiAuto` skills run directly in every mode: `script_read`, `script_list`, `script_find_in_file`, `script_get_info`, `script_get_compile_feedback`. All 7 writers — `script_create`, `script_create_batch`, `script_replace`, `script_append`, `script_rename`, `script_move`, `script_delete` — are `MayTriggerReload` + `RiskLevel:"high"` (a written `.cs` reloads the domain; `script_delete` is also `Delete`), so they are NeverInSemi: Approval **and** Auto return `MODE_FORBIDDEN`, and only Bypass or an Allowlist hit runs them. Never request a grant for them → [operating mode](../../references/protocol-operating-mode.md).

## Compile and reload
- Every write returns at once: `{success, status: "accepted", path, jobId, waitUrl, serverAvailability}`; compilation follows, and the server is briefly unreachable during the domain reload.
- One call gets the compile result: GET the returned `waitUrl` (`/jobs/<jobId>?wait=90`), e.g. `curl -s --retry 20 --retry-connrefused --retry-all-errors --retry-delay 2 "http://localhost:<port>/jobs/<jobId>?wait=90"`. It answers once compilation settles: job `status` `completed`, or `failed` on compile errors, with diagnostics in `resultData.compilation` (`isCompiling`, `hasErrors`, `errorCount`, `errors: [{type, message, file, line}]`). During the reload it may refuse or return `503`: repeat the same URL; `waitTimedOut: true` means GET it again.
- `script_get_compile_feedback(scriptPath)` returns the same diagnostics on demand. Fix errors first: `component_add` finds a new MonoBehaviour only after a clean compile.
- `checkCompile: false` skips the diagnostics; `diagnosticLimit` caps them (default 20).

## Shared rules
- `scriptName` is the file and class name, without `.cs` or path separators. Paths (`folder`, `scriptPath`, `newFolder`) are project-relative, starting with `Assets/` or `Packages/`; a missing folder is created. An existing file is never overwritten (`Script already exists`).
- `script_create` writes `content` verbatim when given (then `template` / `namespaceName` are ignored, with a warning); otherwise it fills a template — `MonoBehaviour` (default), `ScriptableObject`, `Editor`, `EditorWindow` (the last two default to `Assets/Editor`) — wrapped in `namespaceName` if set. Any other bare name is rejected (`validValues` lists these four); text containing code stays a literal template with `{CLASS}`/`{NAMESPACE}` filled, but use `content` for custom classes. A MonoBehaviour or ScriptableObject binds to its file only when the class name equals the file name; content declaring no such type gets a warning.
- 2+ new scripts → `script_create_batch`: one domain reload instead of N.

## Skills Overview
- Write: `script_create`, `script_create_batch`, `script_replace` (find/replace, plain or regex), `script_append` (insert lines), `script_rename` (file only; the class inside is not renamed), `script_move` (creates the destination folder), `script_delete`.
- Read: `script_read`, `script_list`, `script_find_in_file` (plain or regex, per line), `script_get_info` (class, base class, public members), `script_get_compile_feedback`.

Per-skill tables and return shapes: `reference/<skill_name>.md` — rarely needed, the schema notes the parameters.

**DO NOT** (common hallucinations):
- `script_edit` / `script_update` do not exist → use `script_replace` for find-and-replace
- `script_write` does not exist → use `script_create` (new file) or `script_replace` (modify existing)

**Elsewhere**: API analysis of a compiled script → `script_analyze` (perception).

## Exact Signatures

Names, parameters, defaults and required flags come from `GET /skills/schema?category=Script` (or `?names=a,b`), not this file.

## Common Errors

| Error | Trigger → fix |
|---|---|
| `MISSING_PARAM` | e.g. `scriptName`, `pattern`, `find`, `newName` → supply it; the error (or a dryRun) lists all parameters. |
| `SEMANTIC_INVALID` | Path separators in `scriptName` / `newName`, a path outside `Assets/` / `Packages/`, or the script already exists → fix the name or path, or rename/move the existing file. |
| `TARGET_NOT_FOUND` | Script, MonoScript or folder missing → find the right `scriptPath` with `script_list` / `asset_find`. |
| `SKILL_ERROR` | A filesystem or AssetDatabase move/rename/delete failed → read the message, resolve it, retry. |

Transport codes (`COMPILING`, `RATE_LIMIT`, ...) → [error codes](../../references/protocol-error-codes.md).
