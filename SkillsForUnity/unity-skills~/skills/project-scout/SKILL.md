---
name: unity-project-scout
description: "Unity project reconnaissance advisor. Load this FIRST in existing projects before proposing architecture changes — inspects Unity version, packages, asmdefs, folders, coding patterns. Triggers: inspect project, scout project, project baseline, analyze project, what does this project use, existing codebase, 项目分析, 项目结构, 看看项目, 分析项目, 现有架构, 项目用了什么."
---

# Unity Project Scout

Use this before recommending architecture changes in an existing project.

## Inspect First

Collect only the information needed to avoid clashing with the current project:

- Unity version and render pipeline
- Installed packages and notable dependencies
- `asmdef` layout, if any
- Folder structure under `Assets/`
- Whether the project already uses:
  - `ScriptableObject` config
  - service/singleton patterns
  - event-driven flows
  - custom inspectors/property drawers
  - tests
- Existing naming and code organization style

## Suggested Tools / Inputs

- Unity project info and project settings
- Script/file search for patterns
- Local inspection of `Packages/manifest.json`, `Assets/`, and `*.asmdef`

## Output Format

- Technical baseline
- Existing architectural signals
- Existing conventions worth preserving
- Existing risks or inconsistencies
- Constraints for future suggestions
- Unknowns that still need confirmation

## Guardrails

**Mode**: Documentation only — guidance for AI under all three operating modes; no REST skills to gate.

- Do not propose a clean-slate architecture if the project already has a consistent pattern.
- Do not recommend new dependencies until the current stack is clear.
