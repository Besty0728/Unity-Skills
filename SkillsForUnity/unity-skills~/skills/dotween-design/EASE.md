---
name: unity-dotween-ease
description: "Ease enum (36 public values incl. Flash family + 2 INTERNAL), SetEase overloads — basic enum, amplitude+period for Elastic/Back, AnimationCurve, custom EaseFunction, Flash-family 3-param overload, default ease from DOTweenSettings."
type: reference
---

# DOTween Easing

Sub-doc of [dotween-design](./SKILL.md). Covers the `Ease` enum and every way to apply easing.

## The `Ease` enum — full list

Source: `Ease.cs:9-53`. **38 enum entries total**; 36 user-facing + 2 reserved (`INTERNAL_Zero`, `INTERNAL_Custom`):

| Group | Count | Values |
|-------|:----:|--------|
| Special | 2 | `Unset` (default before any SetEase — DOTween then uses `defaultEaseType`), `Linear` |
| Sine | 3 | `InSine / OutSine / InOutSine` |
| Quad | 3 | `InQuad / OutQuad / InOutQuad` |
| Cubic | 3 | `InCubic / OutCubic / InOutCubic` |
| Quart | 3 | `InQuart / OutQuart / InOutQuart` |
| Quint | 3 | `InQuint / OutQuint / InOutQuint` |
| Expo | 3 | `InExpo / OutExpo / InOutExpo` |
| Circ | 3 | `InCirc / OutCirc / InOutCirc` |
| Elastic | 3 | `InElastic / OutElastic / InOutElastic` |
| Back | 3 | `InBack / OutBack / InOutBack` |
| Bounce | 3 | `InBounce / OutBounce / InOutBounce` |
| Flash | 4 | `Flash / InFlash / OutFlash / InOutFlash` |
| Internal (do not use) | 2 | `INTERNAL_Zero`, `INTERNAL_Custom` (auto-assigned by DOTween for zero-duration / custom-curve tweens) |

Breakdown: 2 special + 10 groups × 3 = 32 math eases + 4 Flash = 36 user-facing. Most commonly used: `OutQuad`, `OutCubic`, `OutBack`, `InOutSine`, `OutBounce`.

## Choosing an ease — visual reference

| Intent | Ease |
|--------|------|
| Subtle smooth slow-in-slow-out | `InOutSine` |
| "Snappy" settle | `OutCubic` or `OutQuart` |
| Natural deceleration (falling) | `OutQuad` |
| Dramatic acceleration | `InExpo` or `InQuart` |
| Bounce at end (like UI popping in) | `OutBack` (slight overshoot) |
| Physical bounce | `OutBounce` |
| Jelly / spring | `OutElastic` |
| Flashing / blink | `Flash` family |

Visualize all curves: https://easings.net/ (Linear, Sine, Quad, Cubic, Quart, Quint, Expo, Circ, Back, Elastic, Bounce families match 1:1).

## `SetEase` overloads

`TweenSettingsExtensions.cs`:

```csharp
// Basic
public static T SetEase<T>(this T t, Ease ease) where T : Tween;                                  // line 167

// With overshoot (for Back / Elastic — controls "extra" amount)
public static T SetEase<T>(this T t, Ease ease, float overshoot) where T : Tween;                 // line 184

// With amplitude and period (for Elastic — amplitude controls how far past; period controls oscillation rate)
public static T SetEase<T>(this T t, Ease ease, float amplitude, float period) where T : Tween;   // line 204

// Custom AnimationCurve (any curve from the inspector)
public static T SetEase<T>(this T t, AnimationCurve animCurve) where T : Tween;                   // line 217

// Custom function (float time, float duration, float overshoot, float period) -> float
public static T SetEase<T>(this T t, EaseFunction customEase) where T : Tween;                    // line 227
```

Signature of `EaseFunction` (from `Core/Delegates.cs:21`):
```csharp
public delegate float EaseFunction(float time, float duration, float overshootOrAmplitude, float period);
```

Its doc-comment (`Core/Delegates.cs:19`) states: "Must return a value between 0 and 1."

## `OutBack` / `InBack` overshoot

```csharp
// Default overshoot 1.70158 (DOTween constant)
transform.DOScale(Vector3.one, 0.3f).SetEase(Ease.OutBack);

// Milder overshoot
transform.DOScale(Vector3.one, 0.3f).SetEase(Ease.OutBack, overshoot: 0.8f);

// Dramatic overshoot
transform.DOScale(Vector3.one, 0.3f).SetEase(Ease.OutBack, overshoot: 2.5f);
```

## `OutElastic` — amplitude & period

```csharp
// Default amplitude = 1.70158, period = 0.3 (slight bounce)
transform.DOMoveY(5f, 1f).SetEase(Ease.OutElastic);

// Stiff spring (fast oscillation, small amplitude)
transform.DOMoveY(5f, 1f).SetEase(Ease.OutElastic, amplitude: 0.5f, period: 0.1f);

// Floppy spring (slow oscillation, big amplitude)
transform.DOMoveY(5f, 1f).SetEase(Ease.OutElastic, amplitude: 2f, period: 0.8f);
```

## `Flash` family — the 3-argument special

`Flash` / `InFlash` / `OutFlash` / `InOutFlash` require the 3-parameter overload:

```csharp
// Flashes 5 times during the tween, alpha period 0.2
transform.DOScale(big, 1f)
    .SetEase(Ease.Flash, overshootOrAmplitude: 5, period: 0.2f);
```

- `overshootOrAmplitude` — number of flashes.
- `period` — base alpha pulse length.

The `Flash` ease outputs non-monotonic 0→1 curves — position oscillates around endpoint.

## `AnimationCurve` ease

For designer-tweakable curves without recompile:

```csharp
public AnimationCurve customCurve; // assign in Inspector

void Start()
{
    transform.DOMove(target, 1f).SetEase(customCurve);
}
```

DOTween evaluates `animCurve.Evaluate(t)` with `t ∈ [0..1]`. The curve MUST map `[0..1] -> [0..1]` — otherwise the tween value undershoots/overshoots unpredictably.

## Custom `EaseFunction`

```csharp
public static float MyEase(float time, float duration, float overshoot, float period)
{
    float t = time / duration;
    return 1f - Mathf.Pow(1f - t, 4f); // equivalent to OutQuart
}

transform.DOMove(target, 1f).SetEase((EaseFunction)MyEase);
```

Caller's responsibility to return values in `[0..1]` (or accept weird overshoots).

## Default ease

`DOTweenSettings.defaultEaseType` — read at Init (`DOTween.cs:273`). Default `DOTween.defaultEaseType = Ease.OutQuad`. Change via DOTween Utility Panel or programmatically:

```csharp
DOTween.defaultEaseType = Ease.OutCubic;
DOTween.defaultEaseOvershootOrAmplitude = 1.70158f;  // Back / Elastic default
DOTween.defaultEasePeriod = 0f;                      // 0 => use computed default per ease type
```

## `DOShake*` — ease doesn't apply

Shake eases (`DOShakePosition`, `DOShakeRotation`, `DOShakeScale`) ignore `SetEase` — they use internal random sampling. Instead, control via their own parameters:

```csharp
transform.DOShakePosition(duration: 1f, strength: 3, vibrato: 10, randomness: 90, fadeOut: true);
```

- `strength` — max displacement
- `vibrato` — number of shake cycles per second
- `randomness` — how much each cycle diverges from previous
- `fadeOut` — shake intensity decays to 0

## `DOPunch*` — also bypasses ease

Like shake, punch tweens use custom internal curves:

```csharp
transform.DOPunchPosition(Vector3.up * 0.2f, duration: 0.3f, vibrato: 10, elasticity: 1);
```

## Easing checklist

- [ ] `Flash` / `InFlash` / `OutFlash` eases always use the 3-param `SetEase(Ease, amplitude, period)` overload.
- [ ] `AnimationCurve` eases use curves normalized to `[0..1] -> [0..1]` — validate before runtime.
- [ ] Custom `EaseFunction` return values are in `[0..1]` unless overshoot is intended.
- [ ] `DOShake*` / `DOPunch*` tweens don't chain `SetEase` (no effect).
- [ ] Default ease in `DOTweenSettings` matches project aesthetic (avoid Linear for UI).
- [ ] `Ease.Unset` not explicitly passed — lets DOTween fall back to `defaultEaseType`.

See [PITFALLS.md](./PITFALLS.md) for ease-related bugs.
