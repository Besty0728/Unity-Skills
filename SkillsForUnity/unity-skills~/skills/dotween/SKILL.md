---
name: unity-dotween
description: "DOTween Free/Pro automation. Free skills probe install status, read/validate/configure DOTweenSettings, list modules and shortcut extension methods, and generate runtime tween / Sequence / lifetime-safe MonoBehaviour scripts. Pro skills (`dotween_pro_*`) add/configure/inspect `DG.Tweening.DOTweenAnimation` components on scene objects, including stagger/cascade entrance. Triggers: DOTween, DOTweenFree, DOTweenPro, DOTweenAnimation, DOTweenSettings, DG.Tweening, tween, Sequence, ease, easing, OutQuad, OutBack, loops, Yoyo, stagger, cascade, DOMove, DOLocalMove, DORotate, DOScale, DOAnchorPos, DOFade, DOColor, DOPunchPosition, DOShakePosition, SetLink, autoKill, SafeMode, UI animation, UI 动画, 补间动画, 补间, 缓动, 缓动函数, 动画配置, 循环, 序列, 动效, 脚本生成, 错峰动画."
---

# DOTween Skills

DOTween Free/Pro support for project diagnostics, settings, module/API discovery, and runtime script generation. DOTween Pro-only `DOTweenAnimation` editor-time configuration remains available through `dotween_pro_*` skills.

## Guardrails

**Operating Mode** (v1.9 three-tier):
- **Approval** (default): query/diagnostic skills (`dotween_get_status`, `dotween_settings_get`, `dotween_settings_find`, `dotween_settings_validate`, `dotween_list_modules`, `dotween_list_shortcuts`, `dotween_pro_get_animation`, `dotween_pro_list_animations`) run directly. Mutators (settings configure, script generators, all other `dotween_pro_*`) are FullAuto — on `MODE_RESTRICTED`, run the grant protocol.
- **Auto** / **Bypass**: SemiAuto and FullAuto run directly.
- Auto-forbidden in this module: `dotween_generate_tween_script`, `dotween_generate_sequence_script`, `dotween_generate_lifetime_script` (all carry `MayTriggerReload = true`, `RiskLevel = "high"` because writing a new `.cs` triggers script compilation + Domain Reload). Reachable only under Bypass mode or via a user-managed Allowlist entry; the grant flow returns `MODE_FORBIDDEN`.
- When DOTween Free/Pro is missing, the `DOTweenPresenceDetector` does not add the `DOTWEEN` / `DOTWEEN_PRO` defines, so most skills return a "not installed" diagnostic instead of executing. The `dotween_pro_*` family additionally requires Pro because `DG.Tweening.DOTweenAnimation` is Pro-only.

**Prerequisites**:
- DOTween Free or Pro must be installed. `DOTweenPresenceDetector` adds `DOTWEEN` / `DOTWEEN_PRO` defines automatically after install.
- Free skills work with DOTween Free and Pro: status, settings read/find/validate/configure, module/shortcut listing, runtime script generation.
- `dotween_pro_*` skills require DOTween **Pro** because `DG.Tweening.DOTweenAnimation` is Pro-only.

**Do not confuse Free with Pro**:
- Free skills do **not** create or emulate `DOTweenAnimation` components.
- Runtime tween generation creates `.cs` scripts only; it does not auto-attach scripts to scene objects because Unity may need a Domain Reload first.
- For source-level runtime API design rules, load [dotween-design](../dotween-design/SKILL.md).

## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file. The signatures shown below are illustrative; treat the schema endpoint as the source of truth.

## Free Skills

### Diagnostics and settings

- `dotween_get_status` — report DOTween/Pro install status, `DOTweenSettings.asset` path, and visible module count.
- `dotween_settings_find` — list project assets named `DOTweenSettings`.
- `dotween_settings_get` — read common `DOTweenSettings.asset` fields.
- `dotween_settings_validate` — report missing settings, duplicate settings, invalid capacities, and notable SafeMode warnings.
- `dotween_settings_configure` — edit `Resources/DOTweenSettings.asset`; parameters: `defaultEaseType?`, `defaultAutoKill?`, `defaultLoopType?`, `safeMode?`, `logBehaviour?`, `tweenersCapacity?`, `sequencesCapacity?`.

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
**Parameters:** `target` / `animationType` / `endValueV3` / `endValueFloat` / `endValueColor` / `endValueV2` / `endValueString` / `endValueRect` / `duration` / `ease` / `loops` / `loopType` / `delay` / `isRelative` / `isFrom` / `autoPlay` / `autoKill` / `id`
(Defaults and exact types live in `GET /skills/schema` — see "## Exact Signatures" above.)

### `dotween_pro_batch_add_animation`
Add the same animation to multiple GameObjects.
**Parameters:** `targetsJson` (JSON string array) — plus every parameter of dotween_pro_add_animation above.

### `dotween_pro_stagger_animations`
Batch-add with incrementing delay — UI cascade entrance pattern.
**Parameters:** `targetsJson` / `animationType` / `endValueV3` / `endValueFloat` / `endValueColor` / `endValueV2` / `duration` / `ease` / `loops` / `loopType` / `baseDelay` / `staggerDelay` / `isFrom` / `autoPlay` / `autoKill`
(Defaults and exact types live in `GET /skills/schema` — see "## Exact Signatures" above.)

### `dotween_pro_set_duration`
Change `duration` on an existing DOTweenAnimation. Parameters: `target`, `animationIndex=0`, `duration`.

### `dotween_pro_set_ease`
Change ease on an existing DOTweenAnimation. Parameters: `target`, `animationIndex=0`, `ease="OutQuad"`, `easeCurveJson?`.

### `dotween_pro_set_loops`
Change loops count and optional loopType. Parameters: `target`, `animationIndex=0`, `loops`, `loopType?`.

### `dotween_pro_set_animation_field`
Generic setter for DOTweenAnimation fields except `duration/ease/easeType/easeCurve/loops/loopType`; use dedicated skills for those.

### `dotween_pro_get_animation`
Read all serialized fields of one DOTweenAnimation. Parameters: `target`, `animationIndex=0`.

### `dotween_pro_list_animations`
List DOTweenAnimation components on a target or across the scene. Parameters: `target?`, `recursive=false`.

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
