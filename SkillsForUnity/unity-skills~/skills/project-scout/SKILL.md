---
name: unity-project-scout
description: Advise on reconnoitering an existing Unity project
---

# Unity Project Scout

Documentation only: no REST skills. Use this before recommending architecture changes in an existing project.

## When to use
- First approaching an unfamiliar project
- Before proposing structural changes
- Auditing an existing setup

## Inspect first
Collect only what is needed to avoid clashing with the current project:
- Unity version and render pipeline
- Installed packages and notable dependencies (`Packages/manifest.json`)
- `asmdef` layout, if any
- Folder structure under `Assets/`
- Whether the project already uses ScriptableObject config, service/singleton patterns, event-driven flows, custom inspectors/property drawers, or tests
- Existing naming and code organization style

## Output format
- Technical baseline
- Existing architectural signals
- Conventions worth preserving
- Existing risks or inconsistencies
- Constraints for future suggestions
- Unknowns that still need confirmation

## Pitfalls
- Do not propose a clean-slate architecture if the project already has a consistent pattern.
- Do not recommend new dependencies until the current stack is clear.
