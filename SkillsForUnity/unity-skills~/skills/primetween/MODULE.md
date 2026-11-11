# Unity PrimeTween Skills

PrimeTween Free support follows its own API rather than DOTween's: it discovers static factories, reads process-wide configuration, and generates runtime scripts that own and stop their `Tween` or `Sequence` handles. It does not write a DOTween-style settings asset or create PrimeTween Pro components. Parameters come from the schema (`GET /skills/schema?category=PrimeTween&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Checking a PrimeTween installation
- Exploring which PrimeTween APIs are available
- Generating Transform or Sequence animation scripts

## Rules
- PrimeTween must be installed as `com.kyrylokuzyk.primetween`.
- Query skills (`primetween_get_status`, `primetween_get_config`, `primetween_list_factories`) run in every mode.
- `primetween_generate_tween_script` and `primetween_generate_sequence_script` write a `.cs` file, which triggers compilation and a Domain Reload. They are high risk: refused under Approval and Auto, and they run only in Bypass or when allowlisted. Do not attempt the grant flow.
- Generated scripts support Transform `Position`, `LocalPosition`, `EulerAngles`, `LocalEulerAngles` and `Scale`. Run `primetween_list_factories` before requesting any other API.
- Generated scripts stop their owned handle on disable. PrimeTween handles are not reusable; there is no DOTween `SetLink` equivalent.
- `PrimeTweenConfig` is runtime state, not a serialized project asset.

## Skills

### `primetween_get_status`
Report whether PrimeTween is installed, its package version, assembly and visible core types.
**Parameters:** None.

### `primetween_get_config`
Read the global runtime values exposed by `PrimeTweenConfig`.
**Parameters:** None.

### `primetween_list_factories`
List the public static methods of one PrimeTween API type.
**Parameters:** `typeName="Tween"` (one of Tween, Sequence, Shake, PrimeTweenConfig), `methodPrefix?`, `limit=100`.

### `primetween_generate_tween_script`
Create a Transform-focused PrimeTween MonoBehaviour that owns its `Tween` and stops it on disable.
**Parameters:** `className`, `folder="Assets/Scripts/PrimeTween"`, `namespaceName?`, `tweenKind="LocalPosition"`, `duration=1`, `ease="OutQuad"`, `cycles=1`, `cycleMode="Restart"`, `autoPlay=true`.

### `primetween_generate_sequence_script`
Create a PrimeTween MonoBehaviour that chains and groups supported Transform tweens with `Sequence`.
**Parameters:** `className`, `folder="Assets/Scripts/PrimeTween"`, `namespaceName?`, `tweenKind="Scale"`, `duration=0.2`, `ease="OutBack"`, `cycles=1`, `sequenceCycleMode="Restart"`, `autoPlay=true`, `stepsJson?`.

`stepsJson` is a JSON array of `{ "op": "Chain" | "Group", "tweenKind": "Scale", "duration": 0.2 }`.
