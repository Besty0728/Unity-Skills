---
name: unity-skills
description: "Unity Editor automation via REST API for scenes, assets, scripts, workflows, and design advisory modules."
---

# Unity Skills

Use this skill when the user wants to automate the Unity Editor through the local UnitySkills REST server.

Current package snapshot:

- `448` REST skills
- `13` advisory design modules
- Unity maintenance baseline: `2022.3+`
- Default request timeout: `15 minutes`

## Core Rules

1. When the user mentions a specific Unity version, route first:

```python
import unity_skills
unity_skills.set_unity_version("2022.3")
```

2. When the task touches `2+` objects, prefer `*_batch` skills instead of looping single-item skills.

3. When the task spans multiple editor mutations, prefer a workflow wrapper:

```python
import unity_skills

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

These modules are optional. Load them when the user asks for architecture guidance, script design advice, refactoring direction, coupling reduction, performance review, or maintainability tradeoffs.

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

Use them on demand. Do not default to giant architecture dumps, forced UniTask adoption, or a global event bus unless the project context clearly justifies it.

## Module Index

For module-by-module documentation, open:

```text
unity-skills/skills/SKILL.md
```

For script-specific guidance, open:

```text
unity-skills/skills/script/SKILL.md
```
