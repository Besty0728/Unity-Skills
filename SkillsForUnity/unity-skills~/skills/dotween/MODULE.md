# DOTween Skills

DOTween Free and Pro support: project diagnostics, settings, module and API discovery, runtime script generation, and editor-time configuration of `DOTweenAnimation` components through the `dotween_pro_*` skills. Parameters come from the schema (`GET /skills/schema?category=DOTween&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Setting up DOTween
- Validating install and modules
- Configuring DOTweenAnimation components

## Rules
- Install DOTween Free or Pro first. `DOTweenPresenceDetector` adds the `DOTWEEN` / `DOTWEEN_PRO` defines after install; without them most skills return a "not installed" diagnostic.
- Free skills work with Free and Pro. The `dotween_pro_*` family requires Pro, because `DG.Tweening.DOTweenAnimation` is Pro-only. Free skills do not create or emulate `DOTweenAnimation` components.
- Runtime tween generation writes `.cs` files only. It never attaches them to scene objects, because Unity may need a Domain Reload first.
- `dotween_generate_tween_script`, `dotween_generate_sequence_script` and `dotween_generate_lifetime_script` are refused under Approval and Auto (`MODE_FORBIDDEN`; writing a `.cs` triggers compilation and a reload), and so is `dotween_pro_remove_animation` (delete-class). Only Bypass or an Allowlist entry runs them, and a grant does not unlock them. The other Pro writers, such as `dotween_pro_add_animation` and `dotween_pro_set_*`, are grantable.
- For source-level runtime API design, load [dotween-design](../dotween-design/MODULE.md).

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

Single-target Pro skills locate the GameObject by `targetEntityId` (preferred; the only id on Unity 6000.4+), `targetInstanceId`, `targetPath` or `target` (name).

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
Copy all fields from `sourceTarget[sourceIndex]` to a new DOTweenAnimation on `destTarget`. Fields that could not be copied are listed in `skippedFields`.

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
