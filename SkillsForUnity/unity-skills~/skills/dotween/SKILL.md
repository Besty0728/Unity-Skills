---
name: unity-dotween
description: "DOTween Pro DOTweenAnimation editor-time setup and tuning. Use when adding UI/tween animations, batch stagger entrances, or adjusting existing DOTweenAnimation components. Triggers: DOTween, DOTweenAnimation, UI animation, tween, ease, loops, stagger, 补间动画, 动画配置, UI 动画, 缓动, 循环."
---

# DOTween Skills

DOTween Pro `DOTweenAnimation` component editor-time configuration — create, batch, stagger, tune, inspect, remove, and configure `DOTweenSettings.asset`. Runtime tweens (`DOMove`, `Sequence.Append`, etc.) are out of scope; use C# scripts for those and load the [dotween-design](../dotween-design/SKILL.md) advisory module for source-anchored rules.

## Guardrails

**Mode**: Full-Auto required.

**Prerequisites**:
- DOTween (Free or Pro) must be installed. After install, the `DOTWEEN` scripting define is added automatically by `DOTweenPresenceDetector`; no manual asmdef or Player Settings changes needed.
- All 8 animation-operation skills (`dotween_pro_*`) require DOTween **Pro** — they return a `NoDOTweenPro()` error if only Free is installed (DOTweenAnimation is Pro-only).
- `dotween_settings_configure` only requires DOTween Free.

**DO NOT** (common hallucinations):
- `dotween_create`, `dotween_tween`, `dotween_move` — do not exist. This module does NOT wrap runtime tween API (`transform.DOMove`, `Sequence`, etc.). Write C# code for runtime tweens.
- `dotween_pro_set_animation_field` with `fieldName=duration/ease/loops/loopType/easeType/easeCurve` — **rejected**; use the dedicated skills (`dotween_pro_set_duration`, `dotween_pro_set_ease`, `dotween_pro_set_loops`).
- `dotween_pro_add_animation animationType=Move` without `endValueV3` — fails with a clear error. Each animation type expects a specific `endValue*` parameter (see table below).

**Routing**:
- Runtime tween API design rules → load [dotween-design](../dotween-design/SKILL.md) advisory
- Async await on tween completion (`ToUniTask`) → load [unitask-design](../unitask-design/SKILL.md) advisory
- Generic component field editing (non-DOTweenAnimation) → `component` module

## animationType → endValue mapping

| animationType | Required parameter |
|---|---|
| `Move / LocalMove / Rotate / LocalRotate / Scale / PunchPosition / PunchRotation / PunchScale / ShakePosition / ShakeRotation / ShakeScale / AnchorPos3D` | `endValueV3` (`"1,2,3"` or `"[1,2,3]"`) |
| `AnchorPos / UIWidthHeight` | `endValueV2` (`"1,2"`) |
| `Fade / FillAmount / CameraOrthoSize / CameraFieldOfView / Value` | `endValueFloat` |
| `Color / CameraBackgroundColor` | `endValueColor` (`"#FF8800"` or `"1,0.5,0,1"`) |
| `Text` | `endValueString` |
| `UIRect` | `endValueRect` (`"x,y,width,height"`) |

## Skills

### `dotween_pro_add_animation`
Add one DOTweenAnimation to a GameObject and configure all core fields.
**Parameters:** `target` / `animationType` / `endValueV3?` / `endValueFloat?` / `endValueColor?` / `endValueV2?` / `endValueString?` / `endValueRect?` / `duration=1` / `ease="OutQuad"` / `loops=1` / `loopType="Yoyo"` / `delay=0` / `isRelative=false` / `isFrom=false` / `autoPlay=true` / `autoKill=true` / `id?`

### `dotween_pro_batch_add_animation`
Add the same animation to multiple GameObjects.
**Parameters:** `targetsJson` (JSON string array) + all params of `dotween_pro_add_animation`.

### `dotween_pro_stagger_animations`
Batch-add with incrementing delay — UI cascade entrance pattern (menu items sliding in).
**Parameters:** `targetsJson` / `animationType` / `endValueV3?` / `endValueFloat?` / `endValueColor?` / `endValueV2?` / `duration=0.5` / `ease="OutBack"` / `loops=1` / `loopType="Yoyo"` / `baseDelay=0` / `staggerDelay=0.1` / `isFrom=true` / `autoPlay=true` / `autoKill=true`
Delay for target at index `i` = `baseDelay + i * staggerDelay`.

### `dotween_pro_set_duration`
Change `duration` on an existing DOTweenAnimation.
**Parameters:** `target` / `animationIndex=0` / `duration`

### `dotween_pro_set_ease`
Change ease on an existing DOTweenAnimation. Supply `ease` (Ease enum name: `OutQuad`, `InOutElastic`, etc., 38 values) or `easeCurveJson` (JSON for a custom `AnimationCurve`).
**Parameters:** `target` / `animationIndex=0` / `ease="OutQuad"` / `easeCurveJson?`

### `dotween_pro_set_loops`
Change loops count and optional loopType. `loops=-1` = infinite.
**Parameters:** `target` / `animationIndex=0` / `loops` / `loopType?` (`Restart / Yoyo / Incremental`)

### `dotween_pro_set_animation_field`
Generic setter for any DOTweenAnimation field EXCEPT `duration/ease/easeType/easeCurve/loops/loopType` (use the dedicated skills for those). Valid targets include `delay / isRelative / isFrom / autoPlay / autoKill / id / endValueV3 / endValueFloat / endValueColor / optionalFloat0 / ...`.
**Parameters:** `target` / `animationIndex=0` / `fieldName` / `fieldValue` (string; vec/color parsed automatically)

### `dotween_pro_get_animation`
Read all serialized fields of a single DOTweenAnimation (useful before tuning).
**Parameters:** `target` / `animationIndex=0` — returns `{ fields: {...} }`

### `dotween_pro_list_animations`
List DOTweenAnimation components on a target (optionally recursive) or across the whole scene.
**Parameters:** `target?` / `recursive=false`

### `dotween_pro_copy_animation`
Copy all fields from `sourceTarget[sourceIndex]` to a new DOTweenAnimation on `destTarget`.
**Parameters:** `sourceTarget` / `destTarget` / `sourceIndex=0`

### `dotween_pro_remove_animation`
Remove one DOTweenAnimation component by index.
**Parameters:** `target` / `animationIndex=0`

### `dotween_settings_configure`
Edit `Resources/DOTweenSettings.asset`. Only parameters supplied are modified. Requires DOTween (Free or Pro) and a prior one-time Setup from `Tools > Demigiant > DOTween Utility Panel`.
**Parameters:** `defaultEaseType?` / `defaultAutoKill?` / `defaultLoopType?` / `safeMode?` / `logBehaviour?` / `tweenersCapacity?` / `sequencesCapacity?`

## Typical Workflows

**UI menu cascade entrance**:
```
1. dotween_pro_stagger_animations targetsJson='["Btn1","Btn2","Btn3"]'
   animationType=AnchorPos endValueV3="0,-20,0" duration=0.4
   ease=OutBack staggerDelay=0.08 isFrom=true
```

**Tuning an existing animation**:
```
1. dotween_pro_get_animation target=HeroPanel         → inspect current fields
2. dotween_pro_set_ease target=HeroPanel ease=InOutSine
3. dotween_pro_set_duration target=HeroPanel duration=0.6
```

**Batch button pulse effect**:
```
1. dotween_pro_batch_add_animation targetsJson='["BtnA","BtnB"]'
   animationType=PunchScale endValueV3="0.1,0.1,0.1" duration=0.3
   ease=OutQuad autoPlay=false
```
