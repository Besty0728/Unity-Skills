---
name: unity-dotween
description: "DOTween Free/Pro automation. Free skills inspect settings/modules and generate runtime tween/sequence scripts; Pro skills configure DOTweenAnimation components. Triggers: DOTween, DOTweenAnimation, UI animation, tween, ease, loops, stagger, 补间动画, 动画配置, UI 动画, 缓动, 循环."
---

# DOTween Skills

DOTween Free/Pro support for project diagnostics, settings, module/API discovery, and runtime script generation. DOTween Pro-only `DOTweenAnimation` editor-time configuration remains available through `dotween_pro_*` skills.

## Guardrails

**Mode**: Full-Auto required.

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
**Parameters:** `target` / `animationType` / `endValueV3?` / `endValueFloat?` / `endValueColor?` / `endValueV2?` / `endValueString?` / `endValueRect?` / `duration=1` / `ease="OutQuad"` / `loops=1` / `loopType="Yoyo"` / `delay=0` / `isRelative=false` / `isFrom=false` / `autoPlay=true` / `autoKill=true` / `id?`

### `dotween_pro_batch_add_animation`
Add the same animation to multiple GameObjects.
**Parameters:** `targetsJson` (JSON string array) + all params of `dotween_pro_add_animation`.

### `dotween_pro_stagger_animations`
Batch-add with incrementing delay — UI cascade entrance pattern.
**Parameters:** `targetsJson` / `animationType` / `endValueV3?` / `endValueFloat?` / `endValueColor?` / `endValueV2?` / `duration=0.5` / `ease="OutBack"` / `loops=1` / `loopType="Yoyo"` / `baseDelay=0` / `staggerDelay=0.1` / `isFrom=true` / `autoPlay=true` / `autoKill=true`

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
