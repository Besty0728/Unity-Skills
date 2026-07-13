---
name: unity-primetween
description: Inspect PrimeTween Free, discover its public animation factories, and generate lifecycle-aware PrimeTween runtime scripts. Use when checking a PrimeTween installation, exploring supported animation APIs, or generating Transform/Sequence animation code.
---

# PrimeTween Skills

PrimeTween Free support is intentionally tailored to its API rather than mirroring DOTween: it discovers static factories, reads process-wide configuration, and generates runtime scripts that own and stop their `Tween` or `Sequence` handles. It does not configure a DOTween-style settings asset or create PrimeTween Pro components.

## Guardrails

- PrimeTween must be installed as `com.kyrylokuzyk.primetween`.
- Query skills run directly in all operating modes. Script generators create a C# asset and can trigger compilation, so they are high-risk and require Bypass or an Allowlist entry in Auto/Approval modes.
- `primetween_get_config` is read-only. `PrimeTweenConfig` is runtime state, not a serialized project configuration asset.
- Generated scripts support Transform `Position`, `LocalPosition`, `EulerAngles`, `LocalEulerAngles`, and `Scale`. Use `primetween_list_factories` before requesting an API outside that supported generator set.
- PrimeTween handles are non-reusable. Generated scripts stop their owned live handle on disable instead of using a DOTween `SetLink` equivalent.

## Free Skills

### `primetween_get_status`
Report whether PrimeTween is installed, its package version, assembly, and visible core types.

**Parameters:** None.

### `primetween_get_config`
Read the current global runtime values exposed by `PrimeTweenConfig`.

**Parameters:** None.

### `primetween_list_factories`
List public static methods from one PrimeTween API type.

**Parameters:** `typeName="Tween"` (`Tween`, `Sequence`, `Shake`, or `PrimeTweenConfig`), `methodPrefix?`, `limit=100`.

### `primetween_generate_tween_script`
Create a Transform-focused PrimeTween MonoBehaviour that owns its `Tween` and stops it on disable.

**Parameters:** `className`, `folder="Assets/Scripts/PrimeTween"`, `namespaceName?`, `tweenKind="LocalPosition"`, `duration=1`, `ease="OutQuad"`, `cycles=1`, `cycleMode="Restart"`, `autoPlay=true`.

### `primetween_generate_sequence_script`
Create a PrimeTween MonoBehaviour that uses `Sequence.Chain` and `Sequence.Group` with supported Transform tween factories.

**Parameters:** `className`, `folder="Assets/Scripts/PrimeTween"`, `namespaceName?`, `tweenKind="Scale"`, `duration=0.2`, `ease="OutBack"`, `cycles=1`, `sequenceCycleMode="Restart"`, `autoPlay=true`, `stepsJson?`.

`stepsJson` is a JSON array of `{ "op": "Chain|Group", "tweenKind": "Scale", "duration": 0.2 }`.

## Exact Signatures

Exact names, parameters, defaults, and return values are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
