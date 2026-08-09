---
name: unity-script-roles
description: Advise on assigning Unity script roles. 为 Unity 脚本职责划分提供建议。
---

## Triggers
- Deciding a class's role
- Splitting responsibilities across types
- Choosing between MonoBehaviour and plain C#
- 确定类的职责、在类型间拆分职责、在 MonoBehaviour 与纯 C# 间抉择

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

> **Mode**: Documentation only — no REST skills to gate; load freely under any operating mode (Approval / Auto / Bypass).

- Do not make every class a `MonoBehaviour`.
- Do not force `ScriptableObject` onto runtime state that should stay in memory-only objects.
