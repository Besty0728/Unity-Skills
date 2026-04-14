---
name: unity-skills
description: "Unity Editor automation via REST API — create scripts, analyze scenes, manage assets, control editor, and orchestrate workflows. Triggers: Unity, Unity Skills, in Unity, automate Unity, editor automation, create script, scene summary, build scene, 全自动模式, full auto, semi-auto, 半自动, Unity自动化, Unity编辑器, Unity技能, 操作Unity，在Unity中."
---

# Unity Skills

Use this skill when the user wants to automate the Unity Editor through the local UnitySkills REST server.

Current package snapshot:

- `513` REST skills
- `14` advisory design modules
- Unity maintenance baseline: `2022.3+`
- Default request timeout: `15 minutes`

## ⚙ Operating Mode

> **Default: SEMI-AUTO** — Only script, perception, scene management, editor context,
> asset basics, workflow, debug/console, and advisory modules are active.
> All other skills (gameobject_create, component_add, material_*, light_*, etc.) are OFF.

### Switch to Full-Auto

Activate ALL 513 skills when user explicitly says:
- "全自动模式" / "full auto" / "full-auto mode"
- "自动开发" / "自动化构建" / "auto build"
- "帮我搭建场景" / "build the scene for me"
- "直接操作 Unity" / "directly manipulate Unity"
- Any clear intent to have AI create/modify GameObjects, materials, lights, UI without writing C# code

### Switch back to Semi-Auto

- "半自动模式" / "semi-auto" / "代码优先" / "code-first"
- Session start always defaults to Semi-Auto

### Semi-Auto Active Categories

| Category | Modules | Representative Skills |
|----------|---------|----------------------|
| Script | script | script_create, script_read, script_replace, script_append |
| Perception | perception | scene_analyze, scene_health_check, project_stack_detect |
| Scene Mgmt | scene | scene_save, scene_load, scene_context, scene_find_objects |
| Editor | editor | editor_get_context, editor_undo, editor_redo |
| Asset Basic | asset | asset_refresh, asset_find, asset_get_info |
| Workflow | workflow | workflow_task_start/end, workflow_undo_task |
| Debug | debug, console | debug_check_compilation, console_get_logs |
| Advisory | 14 modules | All advisory modules (no REST skills) |

## Core Rules

1. When the user mentions a specific Unity version, route first:

```python
import unity_skills
unity_skills.set_unity_version("2022.3")
```

2. [Full-Auto] When the task touches `2+` objects, prefer `*_batch` skills instead of looping single-item skills.

3. When the task spans multiple editor mutations, prefer a workflow wrapper:

```python
import unity_skills

# Full-Auto mode example
with unity_skills.workflow_context("build_scene", "Create player and camera"):
    unity_skills.call_skill("gameobject_create", name="Player", primitiveType="Capsule")
    unity_skills.call_skill("camera_create", name="MainCamera", x=0, y=2, z=-6)
```

4. Script creation, script edits, define changes, package changes, some asset reimports, and test-template creation may trigger compilation or Domain Reload. During those windows the server can be temporarily unavailable. Wait and retry instead of assuming a fatal failure.

5. The `test_*` skills wrap Unity Test Runner async jobs inside the current editor instance. They return a `jobId` that should be polled with `test_get_result(jobId)`.

## Python Helper

Main helper file:

```text
unity-skills/scripts/unity_skills.py
```

Common helpers:

```python
import unity_skills

# Full-Auto mode example
unity_skills.call_skill("gameobject_create", name="Cube", primitiveType="Cube")
unity_skills.call_skill_with_retry("asset_refresh")
unity_skills.wait_for_unity(timeout=10)
print(unity_skills.list_instances())
print(unity_skills.get_server_status())
```

Script workflow helper:

```python
import unity_skills

result = unity_skills.create_script("PlayerController")
if result.get("success"):
    print(result.get("compilation"))
```

## Advisory Design Modules

These modules are **available in both Semi-Auto and Full-Auto modes**. They contain no REST skills — only architecture and design guidance.
Load them **on demand** when the user asks for architecture guidance, script design advice, refactoring, coupling reduction, performance review, pattern selection, or XR/VR/AR development.

- `skills/project-scout/SKILL.md`
- `skills/architecture/SKILL.md`
- `skills/adr/SKILL.md`
- `skills/performance/SKILL.md`
- `skills/asmdef/SKILL.md`
- `skills/blueprints/SKILL.md`
- `skills/script-roles/SKILL.md`
- `skills/scene-contracts/SKILL.md`
- `skills/testability/SKILL.md`
- `skills/patterns/SKILL.md`
- `skills/async/SKILL.md`
- `skills/inspector/SKILL.md`
- `skills/scriptdesign/SKILL.md`
- `skills/xr/SKILL.md`

Use them on demand. Do not default to giant architecture dumps, forced UniTask adoption, or a global event bus unless the project context clearly justifies it.

> **XR/VR rule**: Before calling any `xr_*` skill for the first time in a session, **always load `skills/xr/SKILL.md` first**. It contains verified API property names, Collider configuration rules, and anti-hallucination guardrails that prevent common setup errors. Skipping this step risks silent configuration failures.

## Module Index

For module-by-module documentation, open:

```text
unity-skills/skills/SKILL.md
```

For script-specific guidance, open:

```text
unity-skills/skills/script/SKILL.md
```
