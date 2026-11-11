# Unity Editor Skills

Observe and control the Unity Editor without parsing scene YAML: play mode, selection, undo and redo, change tracking, state and menu items. Parameters come from the schema (`GET /skills/schema?category=Editor&wire=v2`), never from a skill's name. Execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Reading persistent scene or file changes
- Driving play mode
- Inspecting runtime state
- Executing menu items

## Rules
- Play-mode skills are NeverInSemi: `editor_play`, `editor_play_capture`, `editor_stop`, `editor_pause` and `editor_run_scenario` (`MayEnterPlayMode`). `editor_execute_menu` is also NeverInSemi because reload-triggering items (`Assets/Refresh`, `Assets/Reimport All`) reload the domain. Under Approval and Auto they return `MODE_FORBIDDEN`, and the grant flow does not apply.
- Resume or file watching: when changes are reported, or the AI resumes after the user edited Unity, call `editor_get_changes` before reading `.unity` YAML. `dropped=true` means `since` is older than the 500-entry window (`retention`); rebuild context with `scene_context` or `scene_diff`.
- Check `editor_get_state` before play-mode work. Changes made during Play Mode are lost on exit.
- `editor_play`, `editor_play_capture`, `editor_playmode_step` and `editor_run_scenario` return jobs; poll `job_status`. Never `job_wait` on a job that depends on `EditorApplication.update` ticks: it blocks the main thread those ticks need.
- `editor_select` and `editor_playmode_inspect` take one of `name`, `entityId`, `instanceId` or `path`; prefer `entityId`. `editor_get_context` returns entityIds for batch work.
- `editor_undo` and `editor_redo` make experiments reversible.
- Pause on error belongs to the console module: `console_set_pause_on_error` ([console](../console/MODULE.md)).
- Routing: compilation check to `debug_check_compilation` (debug); console errors to `debug_get_errors`; scene save to `scene_save` (scene).

## Pitfalls
- `editor_execute_menu` needs the exact menu path. A miss returns `Menu item not found or failed: <path>`, which the router reports as `TARGET_NOT_FOUND`; verify with `gameobject_find`, `scene_get_hierarchy` or the path spelling.
- `SKILL_ERROR` with `Already in play mode`, `Not in play mode`, or a frame-step job already active: enter or exit play mode first, or wait for the step job.
- `editor_run` does not exist: use `editor_play`.
- `editor_compile` and `editor_recompile` do not exist: use `debug_force_recompile`.
- `editor_save` does not exist: use `editor_execute_menu` with menuPath `"File/Save"`.

## Skills Overview

| Group | Skills |
|---|---|
| Play mode | `editor_play`, `editor_play_capture`, `editor_stop`, `editor_pause`, `editor_playmode_step`, `editor_playmode_inspect` |
| Verification | `editor_run_scenario` (invoke, read, assert and wait steps as one async job; repair loop: [runtime verification](../../references/protocol-runtime-verification.md)) |
| Selection and undo | `editor_select`, `editor_get_selection`, `editor_undo`, `editor_redo` |
| State and changes | `editor_get_context`, `editor_get_changes`, `editor_get_state`, `editor_get_tags`, `editor_get_layers` |
| Menu | `editor_execute_menu` |

Per-skill tables, item fields and return shapes: `reference/<skill_name>.md`, rarely needed because the schema notes the parameters.
