---
name: unity-script-roles
description: "Script role planner for Unity. Use when users want to decide class responsibilities — which should be MonoBehaviour, ScriptableObject, pure C# service, or installer. Triggers: script roles, class roles, what should be MonoBehaviour, service class, presenter, installer, responsibility, 脚本职责, 类的职责, 用MonoBehaviour还是纯C#, 怎么分类, 职责划分."
---

# Unity Script Roles

Use this skill before creating a batch of gameplay scripts.

## Goal

Turn a rough script list into explicit roles so AI does not generate everything as `MonoBehaviour`.

## Output Format

- Script name
- Recommended role
- Main responsibility
- Main dependencies
- Why this role fits better than the alternatives

## Common Roles

- `MonoBehaviour` bridge
- `ScriptableObject` config/data
- pure C# domain/service
- presenter / controller
- state / state machine node
- installer / bootstrap helper

## Guardrails

**Mode**: Documentation only — guidance for AI under all three operating modes; no REST skills to gate.

- Do not make every class a `MonoBehaviour`.
- Do not force `ScriptableObject` onto runtime state that should stay in memory-only objects.
