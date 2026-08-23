---
name: unity-dotween
description: Automate DOTween Free/Pro at editor time
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may proceed straight to dryRun.

## Triggers
- Setting up DOTween
- Validating install/modules
- Configuring DOTweenAnimation components
- 接入 DOTween、校验安装/模块、配置 DOTweenAnimation 组件

# DOTween Skills

DOTween Free/Pro support for project diagnostics, settings, module/API discovery, and runtime script generation. DOTween Pro-only `DOTweenAnimation` editor-time configuration remains available through `dotween_pro_*` skills.

## Guardrails

**Operating Mode** (v1.9 three-tier):
- **Approval** (default): query/diagnostic skills (`dotween_get_status`, `dotween_settings_get`, `dotween_settings_find`, `dotween_settings_validate`, `dotween_list_modules`, `dotween_list_shortcuts`, `dotween_pro_get_animation`, `dotween_pro_list_animations`) run directly. Mutators (settings configure, the remaining `dotween_pro_*` writers) are FullAuto — on `MODE_RESTRICTED`, run the grant protocol. The script generators and `dotween_pro_remove_animation` are auto-forbidden instead (next bullet); grant does **not** unlock those.
- **Auto** / **Bypass**: SemiAuto and FullAuto run directly.
- Auto-forbidden in this module: `dotween_generate_tween_script`, `dotween_generate_sequence_script`, `dotween_generate_lifetime_script` (all carry `MayTriggerReload = true`, `RiskLevel = "high"` because writing a new `.cs` triggers script compilation + Domain Reload), plus `dotween_pro_remove_animation` (`SkillOperation.Delete` — the Delete bit alone triggers NeverInSemi even though its `RiskLevel` stays `"low"`). All four return `MODE_FORBIDDEN` under Approval **and** Auto, and are reachable only under Bypass mode or via a user-managed Allowlist entry; the grant flow returns `MODE_FORBIDDEN` too. Note the other Pro writers (`dotween_pro_add_animation`, `dotween_pro_set_*`, `dotween_pro_copy_animation`, …) carry no Delete bit and stay grantable.
- When DOTween Free/Pro is missing, the `DOTweenPresenceDetector` does not add the `DOTWEEN` / `DOTWEEN_PRO` defines, so most skills return a "not installed" diagnostic instead of executing. The `dotween_pro_*` family additionally requires Pro because `DG.Tweening.DOTweenAnimation` is Pro-only.

**Prerequisites**:
- DOTween Free or Pro must be installed. `DOTweenPresenceDetector` adds `DOTWEEN` / `DOTWEEN_PRO` defines automatically after install.
- Free skills work with DOTween Free and Pro: status, settings read/find/validate/configure, module/shortcut listing, runtime script generation.
- `dotween_pro_*` skills require DOTween **Pro** because `DG.Tweening.DOTweenAnimation` is Pro-only.

**Do not confuse Free with Pro**:
- Free skills do **not** create or emulate `DOTweenAnimation` components.
- Runtime tween generation creates `.cs` scripts only; it does not auto-attach scripts to scene objects because Unity may need a Domain Reload first.
- For source-level runtime API design rules, load [dotween-design](../dotween-design/SKILL.md).

## Free Skills

### Diagnostics and settings

- `dotween_get_status` — report DOTween/Pro install status, `DOTweenSettings.asset` path, and visible module count.
- `dotween_settings_find` — list project assets named `DOTweenSettings`.
- `dotween_settings_get` — read common `DOTweenSettings.asset` fields.
- `dotween_settings_validate` — report missing settings, duplicate settings, invalid capacities, and notable SafeMode warnings.
- `dotween_settings_configure` — edit `Resources/DOTweenSettings.asset`; parameters: `defaultEaseType?`, `defaultAutoKill?`, `defaultLoopType?`, `safeMode?`, `logBehaviour?`, `tweenersCapacity?`, `sequencesCapacity?`. Parameters whose field this DOTween version does not declare come back in an `unsupported` array instead of counting as applied — DOTween Pro 1.0.381 has no capacity fields on the asset at all (its capacities are a runtime call, `DOTween.SetTweensCapacity`). Capacities must be >= 1; a bad enum value is rejected with the accepted names.

### API discovery

- `dotween_list_modules` — list loaded `DG.Tweening.DOTweenModule*`, `ShortcutExtensions`, `TweenExtensions`, and `TweenSettingsExtensions` types. Optional: `includeMethods=false`, `methodLimit=20`.
- `dotween_list_shortcuts` — list public extension methods. Optional filters: `targetType`, `methodPrefix`, `limit=100`.

### Runtime script generation

All generation skills require `className`, default `folder=Assets/Scripts/DOTween`, optional `namespaceName`, and never overwrite existing files.

- `dotween_generate_tween_script` — create one runtime tween MonoBehaviour.
- `dotween_generate_sequence_script` — create one runtime `Sequence` MonoBehaviour; optional `stepsJson` array of `{op:"Append|Join|AppendInterval", tweenKind, duration}`.
- `dotween_generate_lifetime_script` — create a lifecycle-safe wrapper with `SetLink(gameObject)` by default and `KillTween()` on disable/destroy.

Common parameters: `targetKind=Transform`, `tweenKind=DOMove`, `duration=1`, `ease=OutQuad`, `loops=1`, `autoPlay=true`, `useSetLink=true`.

Supported v1 `targetKind` / `tweenKind` pairs:
- `Transform`: `DOMove`, `DOLocalMove`, `DORotate`, `DOLocalRotate`, `DOScale`, `DOPunchPosition`, `DOShakePosition`
- `RectTransform`: `DOAnchorPos`, `DOSizeDelta`
- `CanvasGroup`: `DOFade`
- `Graphic` / `Image`: `DOColor`, `DOFade`
- `Generic`: `DOTween.To`

Example:
```text
dotween_generate_tween_script className=HeroPanelIntro targetKind=RectTransform tweenKind=DOAnchorPos duration=0.35 ease=OutBack
```

Sequence example:
```text
dotween_generate_sequence_script className=ButtonPop targetKind=Transform stepsJson='[
  {"op":"Append","tweenKind":"DOScale","duration":0.12},
  {"op":"AppendInterval","duration":0.05},
  {"op":"Join","tweenKind":"DOPunchPosition","duration":0.25}
]'
```

## Pro Skills

### `dotween_pro_add_animation`
Add one DOTweenAnimation to a GameObject and configure all core fields.
**Parameters:** `target` / `animationType` / `endValueV3?` / `endValueFloat?` / `endValueColor?` / `endValueV2?` / `endValueString?` / `endValueRect?` / `duration=1` / `ease="OutQuad"` / `loops=1` / `loopType="Yoyo"` / `delay=0` / `isRelative=false` / `isFrom=false` / `autoPlay=true` / `autoKill=true` / `id?`

Shared numeric/enum contract of the three add skills, checked before anything is added: `duration > 0`, `loops` is -1 (infinite) or >= 1, `delay >= 0`, and `animationType` / `ease` / `loopType` must be members of the enums the installed DOTween declares (a misspelled `ease` used to be ignored silently).

### `dotween_pro_batch_add_animation`
Add the same animation to multiple GameObjects.
**Parameters:** `targetsJson` (JSON string array) + all params of dotween_pro_add_animation.

### `dotween_pro_stagger_animations`
Batch-add with incrementing delay — UI cascade entrance pattern.
**Parameters:** `targetsJson` / `animationType` / `endValueV3?` / `endValueFloat?` / `endValueColor?` / `endValueV2?` / `duration=0.5` / `ease="OutBack"` / `loops=1` / `loopType="Yoyo"` / `baseDelay=0` / `staggerDelay=0.1` / `isFrom=true` / `autoPlay=true` / `autoKill=true`
`baseDelay` and `staggerDelay` must both be >= 0; a negative one is rejected up front rather than clamped away by DOTween after the fact.

### `dotween_pro_set_duration`
Change `duration` on an existing DOTweenAnimation. Parameters: `target`, `animationIndex=0`, `duration` (**required**, > 0).

### `dotween_pro_set_ease`
Change ease on an existing DOTweenAnimation. Parameters: `target`, `animationIndex=0`, `ease="OutQuad"`, `easeCurveJson?`. `easeCurveJson` wins when both are sent; an unparseable curve is rejected rather than quietly falling back to `ease`.

### `dotween_pro_set_loops`
Change loops count and/or loopType. Parameters: `target`, `animationIndex=0`, `loops?` (-1 = infinite, otherwise >= 1), `loopType?`. Send at least one of `loops` / `loopType` — omitting both is refused instead of resetting loops to 1, and sending only one leaves the other as it is.

### `dotween_pro_set_animation_field`
Generic setter for DOTweenAnimation fields except `duration/ease/easeType/easeCurve/loops/loopType`; use dedicated skills for those. `fieldName` and `fieldValue` are both required — pass an explicit `""` to clear a string field.

### `dotween_pro_get_animation`
Read all serialized fields of one DOTweenAnimation. Parameters: `target`, `animationIndex=0`.

### `dotween_pro_list_animations`
List DOTweenAnimation components on a target or across the scene. Parameters: `target?`, `recursive=false`. `animationIndex` is the component's position on its own GameObject, which is exactly the index the setters/remover take — pass it straight through.

### `dotween_pro_copy_animation`
Copy all fields from `sourceTarget[sourceIndex]` to a new DOTweenAnimation on `destTarget`.

### `dotween_pro_remove_animation`
Remove one DOTweenAnimation component by index.

## animationType → endValue mapping

| animationType | Required parameter |
|---|---|
| `Move / LocalMove / Rotate / LocalRotate / Scale / PunchPosition / PunchRotation / PunchScale / ShakePosition / ShakeRotation / ShakeScale / AnchorPos3D` | `endValueV3` (`"1,2,3"` or `"[1,2,3]"`) |
| `AnchorPos / UIWidthHeight` | `endValueV2` (`"1,2"`) |
| `Fade / FillAmount / CameraOrthoSize / CameraFieldOfView / Value` | `endValueFloat` |
| `Color / CameraBackgroundColor` | `endValueColor` (`"#FF8800"` or `"1,0.5,0,1"`) |
| `Text` | `endValueString` |
| `UIRect` | `endValueRect` (`"x,y,width,height"`) |

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
