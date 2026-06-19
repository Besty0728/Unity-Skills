---
name: unity-project-scout
description: Advises on reconnoitering an existing Unity project — check Unity version, packages, asmdef layout, folders, and coding patterns before proposing changes. Use when first approaching an unfamiliar project, before proposing structural changes, or auditing the existing setup, even if the user just says "看看这个项目" or "项目用了什么". 为侦查现有 Unity 项目提供建议(在提改动前先查 Unity 版本、包、asmdef 结构、目录、编码风格);当用户要初次接触陌生项目、在提结构性改动前、或盘点现有配置时使用。
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

> **Mode**: Documentation only — no REST skills to gate; load freely under any operating mode (Approval / Auto / Bypass).

- Do not propose a clean-slate architecture if the project already has a consistent pattern.
- Do not recommend new dependencies until the current stack is clear.
