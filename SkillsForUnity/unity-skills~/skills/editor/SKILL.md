---
name: unity-editor
description: Control and observe the Unity Editor state
---

## Triggers
- Reading persistent scene/file changes
- Driving play mode
- Inspecting runtime state
- Executing menu items
- 读取持久化场景/文件变更、操控 Play Mode、检查运行时状态、执行菜单项

# Unity Editor Skills

Observe and control the Unity Editor without parsing scene YAML. Parameters come from the schema (`GET /skills/recommend?includeSchema=true` or `/skills/schema?names=`), never from a skill's name — do not call a skill with guessed parameters.

## Operating Mode
- **Approval**: `editor_get_changes`, `editor_get_selection`, `editor_get_context`, `editor_get_state`, `editor_get_tags`, `editor_get_layers`, `editor_playmode_inspect` are `SemiAuto` and run directly; `editor_select`, `editor_undo`, `editor_redo`, `editor_playmode_step` are `FullAuto` and need a grant.
- **Auto / Bypass**: `FullAuto` skills run directly.
- **NeverInSemi** (`MODE_FORBIDDEN` under Approval and Auto; only Bypass or an Allowlist hit can call them; the grant flow does not apply, do not attempt it): `editor_play`, `editor_play_capture`, `editor_stop`, `editor_pause` (`MayEnterPlayMode`); `editor_run_scenario` (`MayEnterPlayMode`, `MayTriggerReload`, `RiskLevel="high"`); `editor_execute_menu` (`MayTriggerReload`: `Assets/Refresh`, `Assets/Reimport All` and similar trigger a domain reload).

## Shared rules
- **Resume / file watching**: when file watching reports changes, or the AI resumes after the user edited Unity, call `editor_get_changes` before reading `.unity` YAML. `dropped=true` means `since` is older than the 500-entry in-memory window (`retention`): rebuild context with `scene_context` / `scene_diff`. `Library/UnitySkills/` keeps more history, but this module does not return it.
- **Before play-mode work**: check `editor_get_state` first. Do not modify the scene during Play Mode — changes are lost on exit.
- **Play-mode jobs**: `editor_play`, `editor_play_capture`, `editor_playmode_step` and `editor_run_scenario` return jobs; poll `job_status`. Never `job_wait` for jobs that depend on `EditorApplication.update` ticks (it blocks the main thread those ticks need).
- **Identifiers**: `editor_select` and `editor_playmode_inspect` accept one of `name` / `entityId` / `instanceId` / `path`; prefer `entityId` (the only id on Unity 6000.4+). Use `editor_get_context` to get entityIds for batch operations.
- **Undo**: `editor_undo` / `editor_redo` make experimentation safe.
- **Menus**: `editor_execute_menu` needs the exact menu path; typos fail silently, and reload-triggering items make the server briefly unreachable.
- **Routing**: compilation check → `debug_check_compilation` (debug module); console errors → `debug_get_errors`; scene save → `scene_save` (scene module) or `editor_execute_menu` menuPath `"File/Save"`.
- **Pause on error**: provided by the console module, not this one — use `console_set_pause_on_error` from [console/SKILL.md](../console/SKILL.md).

## Skills Overview

| Group | Skills |
|---|---|
| Play mode | `editor_play`, `editor_play_capture`, `editor_stop`, `editor_pause`, `editor_playmode_step`, `editor_playmode_inspect` |
| Verification | `editor_run_scenario` (invoke / read / assert / wait steps as one async job; repair loop: [runtime verification](../../references/protocol-runtime-verification.md)) |
| Selection and undo | `editor_select`, `editor_get_selection`, `editor_undo`, `editor_redo` |
| State and changes | `editor_get_context`, `editor_get_changes`, `editor_get_state`, `editor_get_tags`, `editor_get_layers` |
| Menu | `editor_execute_menu` |

`console_set_pause_on_error` (console module) pauses play mode on error.

Per-skill tables, item fields and return shapes: `reference/<skill_name>.md` — rarely needed, the schema notes the parameters.

**DO NOT** (common hallucinations):
- `editor_run` does not exist → use `editor_play` to enter play mode
- `editor_compile` / `editor_recompile` do not exist → use `debug_force_recompile`
- `editor_save` does not exist → use `editor_execute_menu` with menuPath `"File/Save"`
- `editor_execute_menu` requires exact menu path — typos cause silent failure

## Exact Signatures

Names, parameters, defaults and returns come from `GET /skills/schema?category=Editor` (or `unity_skills.get_skill_schema()`), not this file.

## Common Errors

| Error | Trigger → fix |
|---|---|
| `TARGET_NOT_FOUND` | The GameObject or menu item was not found (e.g. `Menu item not found or failed`) → verify with `gameobject_find` / `scene_get_hierarchy`, or check the exact menu path spelling. |
| `SKILL_ERROR` | Play-mode state conflict: `Already in play mode`, `Not in play mode`, or a frame-step job already active → enter/exit play mode first, or wait for the step job to finish. |

Transport codes (`COMPILING`, `RATE_LIMIT`, ...) → [error codes](../../references/protocol-error-codes.md).
