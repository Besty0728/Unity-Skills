---
name: unity-scene-contracts
description: "Scene composition contract advisor for Unity. Use when users want to define required scene objects, component dependencies, bootstrap logic, or reference wiring rules. Triggers: scene contract, bootstrap, required references, scene wiring, scene dependencies, what must be in the scene, 场景契约, 场景装配, 场景依赖, 场景里必须有什么, 引用怎么连."
---

# Unity Scene Contracts

Use this skill when scene setup needs to be explicit instead of relying on hidden runtime lookups.

## Define

- Required root objects
- Required components on each root
- Which references are assigned in Inspector
- Which objects act as bootstrap/installers
- Which objects are runtime-spawned
- Which assumptions should be validated early

## Output Format

- Scene object contract
- Bootstrap sequence
- Inspector wiring rules
- Validation rules
- Hidden dependency risks

## Guardrails

**Mode**: Documentation only — guidance for AI under all three operating modes; no REST skills to gate.

- Prefer explicit scene wiring over chains of runtime `Find`.
- Keep bootstrap objects small and focused.
