---
name: unity-skills
description: "Unity Editor automation via REST API — create scripts, analyze scenes, manage assets, control editor, and orchestrate workflows. Triggers: Unity, Unity Skills, in Unity, automate Unity, editor automation, create script, scene summary, build scene, 全自动模式, full auto, semi-auto, 半自动, Unity自动化, Unity编辑器, Unity技能, 操作Unity，在Unity中."
---

# Unity Skills

Use this skill when the user wants to automate the Unity Editor through the local UnitySkills REST server.

## Canonical Schema First

For exact skill names, parameters, defaults, and returns, query schema first:
- `unity_skills.get_skill_schema()`
- `GET /skills/schema`
- `GET /skills?category=<Category>`

Use module `SKILL.md` files for routing guidance, guardrails, and minimal examples, not as the canonical source of exact signatures.

Current snapshot: `714` REST skills, `51` functional source modules, `68` module documentation directories (`49` REST/module docs + `19` advisory docs), Unity `2022.3+`, default timeout `15 minutes`.

Python helper: `unity-skills/scripts/unity_skills.py`

## Operating Mode

Operating modes are an **AI routing policy**, not a server-side permission gate. The REST server still exposes all `714` skills through `/skills` and `/skills/schema`; the mode rules control which modules the agent should choose by default.

> **Default: SEMI-AUTO**. Prefer only `script`, `perception`, `scene`, `editor`, `asset`, `workflow`, `debug`, `console`, and advisory modules. Hold back object creation/configuration modules until the user clearly requests direct Unity manipulation.

### Switch to Full-Auto

Use Full-Auto routing when user explicitly says:
- "全自动模式" / "full auto" / "full-auto mode"
- "自动开发" / "自动化构建" / "auto build"
- "帮我搭建场景" / "build the scene for me"
- "直接操作 Unity" / "directly manipulate Unity"
- Any clear intent to have AI create/modify GameObjects, materials, lights, UI without writing C# code

### Switch back to Semi-Auto

- "半自动模式" / "semi-auto" / "代码优先" / "code-first"
- Session start always defaults to Semi-Auto

### Semi-Auto Routing Scope

The current Semi-Auto REST scope is about `121` REST entries across these categories, plus `19` advisory modules. Treat the category list as the routing rule; use schema queries for exact callable counts and signatures.

| Category | Modules | Representative Skills |
|----------|---------|----------------------|
| Script | script | script_create, script_read, script_replace, script_append |
| Perception | perception | scene_analyze, scene_health_check, project_stack_detect |
| Scene Mgmt | scene | scene_save, scene_load, scene_context, scene_find_objects |
| Editor | editor | editor_get_context, editor_undo, editor_redo |
| Asset Basic | asset | asset_refresh, asset_find, asset_get_info |
| Workflow | workflow | workflow_task_start/end, workflow_undo_task; use workflow/batch helpers for planning, preview, jobs, and rollback, not free-form scene construction |
| Debug | debug, console | debug_check_compilation, console_get_logs |
| Advisory | 19 modules | Design-only guidance modules (no REST skills) |

## Core Rules

1. If the user specifies a Unity version or editor line, set instance/version routing first with `unity_skills.set_unity_version(...)`.
2. In Full-Auto mode, prefer `*_batch` skills whenever the task touches `2+` objects.
3. For multi-step editor mutations, prefer workflow wrappers instead of free-form mutation sequences.
4. Script edits, define changes, package changes, some imports, and test template creation can trigger compilation or Domain Reload. Wait and retry on transient unavailability.
5. `test_*` skills are async. They return a `jobId` and must be polled with `test_get_result(jobId)`.

## Route

- Module index: `unity-skills/skills/SKILL.md`
- Script guidance: `unity-skills/skills/script/SKILL.md`
- Advisory guidance: load advisory modules on demand from the module index

> **XR rule**: Before calling any `xr_*` skill in a session, load `skills/xr/SKILL.md` first. XR is reflection-based; wrong property names can fail silently.
