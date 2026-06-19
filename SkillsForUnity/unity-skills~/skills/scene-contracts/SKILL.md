---
name: unity-scene-contracts
description: Advises on Unity scene composition contracts — required scene objects, component dependencies, bootstrap logic, and reference wiring. Use when defining what a scene must contain, planning bootstrap/wiring, or documenting scene dependencies, even if the user just says "场景里要有什么" or "引用怎么连". 为 Unity 场景装配契约提供建议(场景必备对象、组件依赖、bootstrap 逻辑、引用连线);当用户要界定场景必须包含什么、规划启动/装配、或记录场景依赖时使用。
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

> **Mode**: Documentation only — no REST skills to gate; load freely under any operating mode (Approval / Auto / Bypass).

- Prefer explicit scene wiring over chains of runtime `Find`.
- Keep bootstrap objects small and focused.
