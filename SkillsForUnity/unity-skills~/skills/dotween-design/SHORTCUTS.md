---
name: unity-dotween-shortcuts
description: "Shortcut extensions by target type — Transform (DOMove / DORotate / DOScale / DOPath / DOPunch / DOShake), RectTransform (DOAnchorPos / DOSizeDelta), Image / Text / CanvasGroup (DOFade / DOColor), Material (DOColor / DOFloat / DOOffset), AudioSource / Camera / Light / Rigidbody / Rigidbody2D."
type: reference
---

# DOTween Shortcuts Cheat Sheet

Sub-doc of [dotween-design](./SKILL.md). Shortcut extensions live across multiple files: `DOTween/ShortcutExtensions.cs` (core) plus Modules (`DOTweenModuleUI.cs`, `DOTweenModulePhysics.cs`, etc.).

All shortcuts follow this pattern:
1. Return `Tweener` or `TweenerCore<...>` with `target` pre-set (for `DOTween.Kill(target)` to work).
2. Require the corresponding Module for Unity-subsystem-specific types (UI / Physics / Audio / TMP).

## Transform — most common

From `ShortcutExtensions.cs`:

| Shortcut | Target | Source line |
|----------|--------|:----:|
| `transform.DOMove(Vector3 endValue, float duration, bool snapping = false)` | World position | 480 |
| `transform.DOMoveX / DOMoveY / DOMoveZ(float end, float duration, bool snapping = false)` | Single-axis world position | 491/502/513 |
| `transform.DOLocalMove(Vector3 end, float duration, bool snapping = false)` | Local position | |
| `transform.DORotate(Vector3 endValue, float duration, RotateMode mode = RotateMode.Fast)` | Euler rotation | 568 |
| `transform.DORotateQuaternion(Quaternion endValue, float duration)` | Quaternion rotation | 583 |
| `transform.DOScale(Vector3 endValue, float duration)` / `(float, float)` | Scale | 619/629 |
| `transform.DOScaleX / Y / Z(float endValue, float duration)` | Single-axis scale | 640/651/662 |
| `transform.DOLookAt(Vector3 towards, float duration, AxisConstraint = None, Vector3? up = null)` | Rotate toward point | 675 |
| `transform.DOPunchPosition(Vector3 punch, float duration, int vibrato = 10, float elasticity = 1, bool snapping = false)` | Position punch | 708 |
| `transform.DOPunchScale(Vector3 punch, float duration, int vibrato = 10, float elasticity = 1)` — **no snapping param** | Scale punch | 725 |
| `transform.DOPunchRotation(Vector3 punch, float duration, int vibrato = 10, float elasticity = 1)` | Rotation punch | 742 |
| `transform.DOShakePosition(float duration, float strength = 1, int vibrato = 10, float randomness = 90, bool snapping = false, bool fadeOut = true, ShakeRandomnessMode = Full)` | Position shake | 761 |
| `transform.DOShakePosition(float duration, Vector3 strength, int vibrato = 10, float randomness = 90, bool snapping = false, bool fadeOut = true, ShakeRandomnessMode = Full)` | Position shake (per-axis) | 779 |
| `transform.DOShakeRotation(float duration, float strength = 90, int vibrato = 10, float randomness = 90, bool fadeOut = true, ShakeRandomnessMode = Full)` — **no snapping** | Rotation shake | 796 |
| `transform.DOShakeScale(float duration, float strength = 1, int vibrato = 10, float randomness = 90, bool fadeOut = true, ShakeRandomnessMode = Full)` — **no snapping** | Scale shake | 830 |
| `transform.DOJump(Vector3 endValue, float jumpPower, int numJumps, float duration, bool snapping = false)` — **returns `Sequence`** | Bouncing jump | 868 |
| `transform.DOPath(Vector3[] path, float duration, PathType type = Linear, PathMode mode = Full3D, int resolution = 10, Color? gizmoColor = null)` | Path traversal | 995 |
| `transform.DOLocalPath(Vector3[] path, ...)` | Local-space path | 1016 |

**Critical defaults** to remember:
- `DOShakePosition` on **Transform**: default `strength = 1` and has a `snapping` parameter (`ShortcutExtensions.cs:761`).
- `DOShakePosition` on **Camera**: default `strength = 3` and has **no** `snapping` parameter (`ShortcutExtensions.cs:125`).
- `DOShakeRotation`: default `strength = 90` (degrees); never has a `snapping` parameter.
- `DOPunchScale`: no `snapping` parameter — different from `DOPunchPosition`.
- `DOJump`: returns `Sequence`, not `Tweener` — because it composes multiple arc tweens internally.

### `RotateMode` (`RotateMode.cs:11-33` — **4 values**)

| Value | Behavior |
|-------|----------|
| `Fast` (default) | Fastest way that never rotates beyond 360° |
| `FastBeyond360` | Fastest way that rotates beyond 360° (lets you pass > 360°) |
| `WorldAxisAdd` | Adds rotation using world axis (like `transform.Rotate(Space.World)`). **End value is always relative.** |
| `LocalAxisAdd` | Adds rotation using local axis (like `transform.Rotate(Space.Self)`). **End value is always relative.** |

### `PathMode` (`PathMode.cs:11-21` — **4 values**)

| Value | Purpose |
|-------|---------|
| `Ignore` | Ignores the path mode (and thus LookAt behaviour) |
| `Full3D` | Regular 3D path |
| `TopDown2D` | 2D top-down path (XY plane with Z constant) |
| `Sidescroller2D` | 2D side-scroller path |

**Trap**: `DOPath` signature defaults to `PathMode.Full3D` at `ShortcutExtensions.cs:995`. For a 2D game, always pass `PathMode.Sidescroller2D` or `TopDown2D` explicitly.

### `PathType` (`PathType.cs:12-20` — **3 values**)

| Value | Curve |
|-------|-------|
| `Linear` | Straight line segments between each waypoint |
| `CatmullRom` | Curved path using Catmull-Rom splines |
| `CubicBezier` | **Experimental.** Cubic Bezier; each point requires two extra control points (waypoints must come in groups of 3 per segment). |

## RectTransform — UI layout (Module: UI)

From `DOTweenModuleUI.cs`:

| Shortcut | Source line |
|----------|:----:|
| `rt.DOAnchorPos(Vector2 endValue, float duration, bool snapping = false)` | 208 |
| `rt.DOAnchorPosX / Y(float endValue, float duration, bool snapping = false)` | 218 / 228 |
| `rt.DOAnchorPos3D(Vector3 endValue, float duration, bool snapping = false)` | 239 |
| `rt.DOAnchorPos3DX / Y / Z(float end, float duration, bool snapping = false)` | 249 / 259 / 269 |
| `rt.DOAnchorMax(Vector2 endValue, float duration, bool snapping = false)` | 280 |
| `rt.DOAnchorMin(Vector2 endValue, float duration, bool snapping = false)` | 291 |
| `rt.DOPivot(Vector2 endValue, float duration)` — **no snapping** | 301 |
| `rt.DOPivotX / Y(float endValue, float duration)` — **no snapping** | 310 / 319 |
| `rt.DOSizeDelta(Vector2 endValue, float duration, bool snapping = false)` | 330 |
| `rt.DOJumpAnchorPos(Vector2 endValue, float jumpPower, int numJumps, float duration, bool snapping = false)` — returns `Sequence` | 394 |
| `rt.DOPunchAnchorPos(Vector2 punch, float duration, int vibrato = 10, float elasticity = 1, bool snapping = false)` | 347 |
| `rt.DOShakeAnchorPos(float duration, float strength = 100, int vibrato = 10, float randomness = 90, bool snapping = false, bool fadeOut = true, ShakeRandomnessMode = Full)` | 363 |
| `rt.DOShakeAnchorPos(float duration, Vector2 strength, ...)` | 378 |

**`DOShakeAnchorPos` default `strength = 100`** (not 3 like Transform, not 1 like Transform.DOShakeScale). UI operates in pixel space — different baseline.

## Image / Text / CanvasGroup / Graphic (Module: UI)

| Shortcut | Target | Source line |
|----------|--------|:----:|
| `canvasGroup.DOFade(float endValue, float duration)` | `CanvasGroup.alpha` | 29 |
| `graphic.DOColor(Color endValue, float duration)` | `Graphic.color` | 43 |
| `graphic.DOFade(float endValue, float duration)` | `Graphic.color.a` | 53 |
| `image.DOColor(Color endValue, float duration)` | `Image.color` | 67 |
| `image.DOFade(float endValue, float duration)` | `Image.color.a` | 77 |
| `image.DOFillAmount(float endValue, float duration)` | `Image.fillAmount` | 87 |
| `text.DOColor(Color endValue, float duration)` | `Text.color` | 484 |
| `text.DOFade(float endValue, float duration)` | `Text.color.a` | 517 |
| `text.DOText(string endValue, float duration, bool richTextEnabled = true, ScrambleMode scrambleMode = ScrambleMode.None, string scrambleChars = null)` | Typewriter | 533 |
| `outline.DOColor(Color endValue, float duration)` | `Outline.effectColor` | 173 |
| `outline.DOFade(float endValue, float duration)` | `Outline.effectColor.a` | 183 |
| `outline.DOScale(Vector2 endValue, float duration)` | `Outline.effectDistance` (distance, NOT transform scale) | 193 |
| `layoutElement.DOFlexibleSize(Vector2 endValue, float duration, bool snapping = false)` | `LayoutElement.flexibleWidth/Height` | 128 |
| `scrollRect.DONormalizedPos(Vector2 endValue, float duration, bool snapping = false)` | `ScrollRect.normalizedPosition` | 434 |

**Note** (`DOTweenModuleUI.cs:193`): `Outline.DOScale` tweens `effectDistance` (the shadow/outline spread Vector2), NOT transform scale. Common source of confusion.

**`Image.DOFade` vs `CanvasGroup.DOFade`**:
- `Image.DOFade` → changes `Image.color.a` (affects only this Image)
- `CanvasGroup.DOFade` → changes `CanvasGroup.alpha` (affects all children)

## Material (core)

From `ShortcutExtensions.cs:246+`:

| Shortcut | Effect |
|----------|--------|
| `material.DOColor(Color end, float duration)` | Default `_Color` shader prop |
| `material.DOColor(Color end, string property, float duration)` | Named shader prop |
| `material.DOColor(Color end, int propertyID, float duration)` | `Shader.PropertyToID` |
| `material.DOFade(float endAlpha, ...)` | Alpha only |
| `material.DOFloat(float end, string property, ...)` | Any float shader prop |
| `material.DOOffset(Vector2 end, ...)` | `_MainTex` offset |
| `material.DOTiling(Vector2 end, ...)` | `_MainTex` tiling |
| `material.DOVector(Vector4 end, string property, ...)` | Named vector prop |

**Performance note**: `material.DOColor` creates a unique material instance per call (Unity's default). Batch-render scenes with many tween-driven materials lose SRP Batcher compatibility — use MaterialPropertyBlock for per-instance variation instead.

## AudioSource / AudioMixer (Module: Audio)

From `DOTweenModuleAudio.cs`:

| Shortcut | Effect | Source line |
|----------|--------|:----:|
| `audioSource.DOFade(float endValue, float duration)` | Volume (0–1) | 23 |
| `audioSource.DOPitch(float endValue, float duration)` | Pitch | 35 |
| `audioMixer.DOSetFloat(string floatName, float endValue, float duration)` | Mixer exposed float param | 51 |

`DOTweenModuleAudio.cs` also exposes `DOComplete / DOKill / DOFlip / DOGoto / DOPause / DOPlay / DOPlayBackwards / DOPlayForward / DORestart / DORewind / DOSmoothRewind / DOTogglePause` on `AudioMixer` as target-group controls (lines 72–186).

## Camera (core — `ShortcutExtensions.cs:39-180`)

| Shortcut | Source line |
|----------|:----:|
| `camera.DOAspect(float endValue, float duration)` | 39 |
| `camera.DOColor(Color endValue, float duration)` (background color) | 49 |
| `camera.DOFarClipPlane / DONearClipPlane / DOFieldOfView / DOOrthoSize(float end, float duration)` | 59 / 69 / 79 / 89 |
| `camera.DOPixelRect(Rect end, float duration)` / `camera.DORect(Rect end, float duration)` | 99 / 109 |
| `camera.DOShakePosition(float duration, float strength = 3, int vibrato = 10, float randomness = 90, bool fadeOut = true, ShakeRandomnessMode = Full)` — **default strength = 3**, no snapping | 125 |
| `camera.DOShakePosition(float duration, Vector3 strength, ...)` | 143 |
| `camera.DOShakeRotation(float duration, float strength = 90, ...)` — default strength = 90 | 162 |

## Light (core — `ShortcutExtensions.cs:197-229`)

| Shortcut | Source line |
|----------|:----:|
| `light.DOColor(Color endValue, float duration)` | 197 |
| `light.DOIntensity(float endValue, float duration)` | 207 |
| `light.DOShadowStrength(float endValue, float duration)` | 217 |

## Rigidbody (Module: Physics — `DOTweenModulePhysics.cs`)

| Shortcut | Source line |
|----------|:----:|
| `rb.DOMove(Vector3 endValue, float duration, bool snapping = false)` | 26 |
| `rb.DOMoveX / Y / Z(float endValue, float duration, bool snapping = false)` | 37 / 48 / 59 |
| `rb.DORotate(Vector3 endValue, float duration, RotateMode mode = RotateMode.Fast)` | 70 |
| `rb.DOLookAt(Vector3 towards, float duration, AxisConstraint = None, Vector3? up = null)` | 83 |
| `rb.DOJump(Vector3 endValue, float jumpPower, int numJumps, float duration, bool snapping = false)` — returns `Sequence` | 102 |

**Use these on physics bodies, not `transform.DOMove`** — `Rigidbody.DOMove` uses `MovePosition` which cooperates with the physics solver. Transform-based tweening teleports and fights the solver.

## TextMeshPro (Module: TMP — requires `DOTween Pro` or community TMP Module)

The TMP module is **not bundled with DOTween Free** source in `_DOTween.Assembly/`. Requires either:
- DOTween Pro (`DOTweenProTMP.cs` + runtime file)
- Community integration (Tools → Demigiant → DOTween Utility Panel → Modules)

Typical extensions once the module is generated:
- `tmp.DOColor / DOFade / DOText` (TMP_Text)
- `tmp.DOFaceColor / DOOutlineColor / DOGlowColor` (material-level)

## `DOTween.To` — custom property tween

`DOTween.cs:506+`:

```csharp
public static Tweener To(DOGetter<float> getter, DOSetter<float> setter, float endValue, float duration);
public static Tweener To<T1, T2>(DOGetter<T1> getter, DOSetter<T1> setter, T2 endValue, float duration);
// overloads for Vector2/3/4, Color, Quaternion, Rect, etc.
```

```csharp
// Tween a non-Unity property
float myValue = 0f;
DOTween.To(() => myValue, v => myValue = v, 1f, 2f)
    .SetEase(Ease.InOutCubic);
```

**Important**: `DOTween.To` does NOT set a target automatically. If you want `DOTween.Kill(someTarget)` to work, add `.SetTarget(someTarget)`.

## `DOVirtual` — untargeted tweens

`DOVirtual.cs`:

```csharp
// Line 26 / 40 / 54 / 68 / 82
public static Tweener Float  (float   from, float   to, float duration, TweenCallback<float>   onVirtualUpdate);
public static Tweener Int    (int     from, int     to, float duration, TweenCallback<int>     onVirtualUpdate);
public static Tweener Vector2(Vector2 from, Vector2 to, float duration, TweenCallback<Vector2> onVirtualUpdate);
public static Tweener Vector3(Vector3 from, Vector3 to, float duration, TweenCallback<Vector3> onVirtualUpdate);
public static Tweener Color  (Color   from, Color   to, float duration, TweenCallback<Color>   onVirtualUpdate);

// Line 176 — defaults to ignoreTimeScale = true
public static Tween DelayedCall(float delay, TweenCallback callback, bool ignoreTimeScale = true);

// Line 97-170 — evaluate an ease curve directly (no tween created)
public static float   EasedValue(float from, float to, float lifetimePercentage, Ease easeType);
public static float   EasedValue(float from, float to, float lifetimePercentage, Ease easeType, float overshoot);
public static float   EasedValue(float from, float to, float lifetimePercentage, Ease easeType, float amplitude, float period);
public static float   EasedValue(float from, float to, float lifetimePercentage, AnimationCurve easeCurve);
public static Vector3 EasedValue(Vector3 from, Vector3 to, float lifetimePercentage, Ease easeType);
// ... Vector3 overloads mirror float
```

```csharp
// Timer / delayed one-shot — by default ignores Time.timeScale
DOVirtual.DelayedCall(2f, () => Debug.Log("2s later"));

// Animate a non-target value
DOVirtual.Float(0, 1, 1f, t => _shader.SetFloat("_Progress", t));

// Evaluate ease without tweening anything
float y = DOVirtual.EasedValue(0, 10, 0.5f, Ease.OutBack);
```

## `.SetRelative()` for delta values

`TweenSettingsExtensions.cs:772,782`:

```csharp
public static T SetRelative<T>(this T t) where T : Tween;
public static T SetRelative<T>(this T t, bool isRelative) where T : Tween;
```

```csharp
transform.DOMoveX(2f, 1f).SetRelative(); // ends at transform.position.x + 2
```

Applies to any shortcut. Common for "slide by N units" animations where start position varies.

## `.SetSpeedBased()` for speed-as-duration

`TweenSettingsExtensions.cs:793,803`:

```csharp
public static T SetSpeedBased<T>(this T t) where T : Tween;
public static T SetSpeedBased<T>(this T t, bool isSpeedBased) where T : Tween;
```

Reinterprets the `duration` parameter as **units per second** rather than total seconds. The tween's actual duration becomes `delta / speed`.

```csharp
// 5 units/second rather than 5-second total — duration depends on distance
transform.DOMove(target, 5f).SetSpeedBased();
```

## `.SetSnapping()` for pixel-perfect UI

```csharp
rt.DOAnchorPos(target, 0.3f).SetSnapping(true);
```

Rounds intermediate values to integers — good for pixel-art UI where sub-pixel jitter is unwanted.

## Shortcuts checklist

- [ ] Correct Module generated for each target type (UI / Physics / Audio / TMP) — or compile errors.
- [ ] `DOPath` for 2D scenes uses `PathMode.Sidescroller2D` or `TopDown2D`, not default `Full3D`.
- [ ] `Image.DOFade` vs `CanvasGroup.DOFade` chosen based on scope (this Image vs all children).
- [ ] `material.DOColor` usage doesn't break SRP Batcher unless the instance cost is acceptable.
- [ ] `DOTween.To(...)` for custom props explicitly sets target via `.SetTarget(...)` for later Kill operations.
- [ ] Rigidbody targets use physics shortcuts (`rb.DOMove`), not `transform.DOMove`.

See [PITFALLS.md](./PITFALLS.md) for shortcut-specific bugs.
