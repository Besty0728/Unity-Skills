---
name: unity-script-roles
description: Advise on assigning Unity script roles
---

# Unity Script Roles

Documentation only: no REST skills. Use before creating a batch of gameplay scripts, so AI does not generate everything as `MonoBehaviour`.

## When to use
- Deciding a class's role
- Splitting responsibilities across types
- Choosing between MonoBehaviour and plain C#

## Common roles
- `MonoBehaviour` bridge
- `ScriptableObject` config/data
- Pure C# domain/service
- Presenter / controller
- State / state-machine node
- Installer / bootstrap helper

## Output format
- Script name
- Recommended role
- Main responsibility
- Main dependencies
- Why this role fits better than the alternatives

## Pitfalls
- Do not make every class a `MonoBehaviour`.
- Do not force `ScriptableObject` onto runtime state that should stay in memory-only objects.
