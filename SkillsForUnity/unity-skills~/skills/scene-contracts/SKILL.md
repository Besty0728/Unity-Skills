---
name: unity-scene-contracts
description: Advise on Unity scene composition contracts
---

# Unity Scene Contracts

Documentation only: no REST skills. Use when scene setup must be explicit instead of relying on hidden runtime lookups.

## When to use
- Defining required scene objects
- Planning bootstrap and wiring
- Documenting scene dependencies

## Define
- Required root objects
- Required components on each root
- References assigned in the Inspector
- Objects that act as bootstrap/installers
- Objects that are runtime-spawned
- Assumptions to validate early

## Output format
- Scene object contract
- Bootstrap sequence
- Inspector wiring rules
- Validation rules
- Hidden dependency risks

## Pitfalls
- Prefer explicit scene wiring over chains of runtime `Find`.
- Keep bootstrap objects small and focused.
