---
name: unity-script-roles
description: Advises on assigning Unity script roles — whether a class should be a MonoBehaviour, ScriptableObject, plain C# service, or installer. Use when deciding a class's role, splitting responsibilities across types, or choosing between MonoBehaviour and plain C#, even if the user just asks "这个该用MonoBehaviour吗" or "职责怎么分". 为 Unity 脚本职责划分提供建议(某个类应作 MonoBehaviour、ScriptableObject、纯 C# 服务还是 installer);当用户要确定类的职责、在类型间拆分职责、或在 MonoBehaviour 与纯 C# 间抉择时使用。
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

> **Mode**: Documentation only — no REST skills to gate; load freely under any operating mode (Approval / Auto / Bypass).

- Do not make every class a `MonoBehaviour`.
- Do not force `ScriptableObject` onto runtime state that should stay in memory-only objects.
