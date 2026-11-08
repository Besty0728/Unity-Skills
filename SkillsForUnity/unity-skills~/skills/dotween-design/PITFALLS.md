---
name: unity-dotween-pitfalls
description: "30 concrete DOTween pitfalls — missing target NRE, Safe Mode hiding bugs, autoKill confusion, Append vs Join timing errors, OnComplete on infinite loops, DOPath mode errors, module compile errors, tween pool leaks, Addressables/tween lifetime races."
type: reference
---

# DOTween Pitfalls

Sub-doc of [dotween-design](./SKILL.md). Every item is a real production bug. Format: ❌ wrong → ✅ right, with WHY.

---

### 1. Tween outlives target — Safe Mode swallows the warning

```csharp
void StartAnim()
{
    transform.DOMoveX(5f, 10f); // 10 second tween
}
// GameObject destroyed at t=5 → Safe Mode logs Warning, tween dies silently.
// Production debugging becomes a "why is this not finishing?" mystery.
```

```csharp
// ✅ Explicit lifetime binding
transform.DOMoveX(5f, 10f).SetLink(gameObject, LinkBehaviour.KillOnDestroy);
```

**Why**: Safe Mode catches MissingReferenceException (`DOTween.cs:49,51`) and kills the tween. `SetLink` makes intent explicit.

---

### 2. `SetAutoKill(false)` without explicit `.Kill()` — pool leak

```csharp
// ❌ Pool fills up over sessions
var idle = transform.DOScale(1.1f, 0.5f)
    .SetLoops(-1, LoopType.Yoyo)
    .SetAutoKill(false);
// (never killed — stays in pool when scene unloads)
```

```csharp
// ✅ Kill on destroy or scene exit
idle.SetLink(gameObject, LinkBehaviour.KillOnDestroy);
// or
void OnDestroy() { idle?.Kill(); }
```

---

### 3. Append when you meant Join — animations serialize unexpectedly

```csharp
// ❌ Rotation waits until Move finishes
DOTween.Sequence()
    .Append(transform.DOMove(a, 1f))
    .Append(transform.DORotate(b, 1f));
```

```csharp
// ✅ Parallel
DOTween.Sequence()
    .Append(transform.DOMove(a, 1f))
    .Join(transform.DORotate(b, 1f));
```

---

### 4. `DOTween.KillAll()` kills UI tweens too

```csharp
// ❌ Wipes everything — including persistent UI animations
void OnLevelComplete() { DOTween.KillAll(); }
```

```csharp
// ✅ Use ID grouping
// Earlier: button.DOColor(red, 0.5f).SetId("ui");
DOTween.KillAll(false, "ui"); // kill all except UI-tagged

// OR kill only gameplay-tagged tweens
DOTween.Kill("gameplay");
```

---

### 5. Multiple tweens on same property — collision

```csharp
// ❌ Two concurrent tweens fighting for position
transform.DOMoveX(5f, 1f);
transform.DOMoveX(0f, 1f); // fights with the first
```

```csharp
// ✅ Kill previous before starting new
transform.DOKill();
transform.DOMoveX(5f, 1f);
```

DOTween extension `transform.DOKill()` is shorthand for `DOTween.Kill(transform)`.

---

### 6. `SetLoops(-1)` + `OnComplete` — callback never fires

```csharp
// ❌ OnComplete never fires on infinite loop
transform.DORotate(Vector3.forward * 360, 2f)
    .SetLoops(-1, LoopType.Incremental)
    .OnComplete(() => Debug.Log("done")); // never fires
```

```csharp
// ✅ Use OnStepComplete for per-iteration, OnKill for final
transform.DORotate(Vector3.forward * 360, 2f)
    .SetLoops(-1, LoopType.Incremental)
    .OnStepComplete(() => StepHandler())
    .OnKill(() => FinalHandler());
```

---

### 7. `SetRelative` + absolute reasoning

```csharp
// ❌ Expected to end at x=5
transform.position = new Vector3(3, 0, 0);
transform.DOMoveX(5f, 1f).SetRelative(true); // ends at x = 3 + 5 = 8
```

`SetRelative` means "add N", not "end at N". Use without SetRelative for absolute.

---

### 8. `Ease.Flash` without 3-param overload

```csharp
// ❌ Uses default amplitude/period — may not flash visibly
transform.DOScale(Vector3.one, 1f).SetEase(Ease.Flash);
```

```csharp
// ✅ Explicit flash count + period
transform.DOScale(Vector3.one, 1f).SetEase(Ease.Flash, 5, 0.2f);
```

Source: `TweenSettingsExtensions.cs:204` — the 3-param SetEase overload.

---

### 9. `DOShakePosition` parameter order confusion

```csharp
// ❌ Meant 90° randomness, got 90 strength
transform.DOShakePosition(1f, 90f); // second arg IS strength (float)
```

Full signature: `DOShakePosition(duration, strength, vibrato, randomness, snapping, fadeOut, randomnessMode)`. Source: `ShortcutExtensions.Camera:125` and similar.

---

### 10. `DOPath` defaulting to 3D for 2D games

```csharp
// ❌ 2D sprite jitters through Z axis
spriteTransform.DOPath(waypoints, 2f); // defaults to PathType.Linear, PathMode.Full3D
```

```csharp
// ✅ 2D-correct
spriteTransform.DOPath(waypoints, 2f, PathType.Linear, PathMode.Sidescroller2D);
```

---

### 11. `DOVirtual.Float` without Kill — never garbage collected

```csharp
// ❌ Virtual tween with closure capture — stays alive until manually killed
DOVirtual.Float(0, 1, 10f, t => _buffer.Fill(t));
// If the enclosing MonoBehaviour is destroyed, this tween keeps capturing the destroyed buffer.
```

```csharp
// ✅ Link to caller lifetime
DOVirtual.Float(0, 1, 10f, t => _buffer.Fill(t))
    .SetTarget(gameObject)
    .OnKill(() => _buffer = null);
// OR in OnDestroy: DOTween.Kill(gameObject);
```

---

### 12. `OnUpdate` closure allocates per call

```csharp
// ❌ String concat every frame
tween.OnUpdate(() => Debug.Log("pos " + transform.position));
```

```csharp
// ✅ Cache or skip debug in hot paths
```

---

### 13. `Image.DOFade` vs `CanvasGroup.DOFade` scope mismatch

```csharp
// ❌ Only this Image fades; children don't
image.DOFade(0f, 0.3f);
```

```csharp
// ✅ For fading a group (image + children)
canvasGroup.DOFade(0f, 0.3f);
```

---

### 14. TextMeshPro `.DOText` but wrong module

```csharp
tmp.DOText("hello", 1f); // CS1061: TMP_Text does not contain DOText
```

**Fix**: Tools → Demigiant → DOTween Utility Panel → add `DOTween Pro` TMP Module (Pro only) or use `DG.Tweening.TMPro` community module.

---

### 15. Sequence `SetLoops(-1)` + inner tween `SetAutoKill(false)`

```csharp
// ❌ Inner tween autoKill conflicts with Sequence loop
var innerTween = transform.DOMoveX(5, 1f).SetAutoKill(false);
var seq = DOTween.Sequence()
    .Append(innerTween)
    .SetLoops(-1);
// Behavior: inner tween may not reset between sequence loops
```

Inner tweens inside a Sequence should be transient (default autoKill=true). Control looping at the Sequence level only.

---

### 16. `DOPunchScale` + subsequent `DOScale` — fights

```csharp
// Punch restores to original scale, but DOScale set end value
transform.DOPunchScale(Vector3.one * 0.2f, 0.3f);
transform.DOScale(Vector3.one * 2, 1f); // may start from already-modified scale
```

**Fix**: Sequence them, or `.Kill()` the punch before scaling:

```csharp
transform.DOKill();
transform.DOScale(Vector3.one * 2, 1f);
```

---

### 17. Test Runner — stale tweens across tests

```csharp
// ❌ Tween from previous test still alive
[Test] public void TestA() { transform.DOMove(a, 10f); }
[Test] public void TestB() { /* still sees TestA's tween */ }
```

```csharp
[TearDown]
public void TearDown() { DOTween.Clear(destroy: true); }
```

---

### 18. `Sequence` killed then `.Append` called

```csharp
// ❌ ObjectDisposedException or no-op (Safe Mode)
var seq = DOTween.Sequence().Append(t1);
seq.Kill();
seq.Append(t2); // seq is disposed
```

Check `seq.IsActive()` before appending, or restart by creating a new Sequence.

---

### 19. `DOTween.PauseAll()` broader than intended

```csharp
// ❌ Pauses UI animations too (including critical dialogs)
DOTween.PauseAll();
```

```csharp
// ✅ Scope with ID
// Setup: gameplayTween.SetId("gameplay");
DOTween.Pause("gameplay");
```

---

### 20. `SetUpdate(UpdateType.Fixed)` + high `Time.timeScale`

```csharp
// ❌ Fixed tween at timeScale=3 steps 3x more per fixed frame — tween ends 3x faster
transform.DOMove(a, 1f).SetUpdate(UpdateType.Fixed);
Time.timeScale = 3f;
```

`UpdateType.Fixed` uses `Time.fixedDeltaTime` scaled by `Time.timeScale`. Be aware when applying slow-motion via timeScale.

---

### 21. `TweenCallback<T>` generic missing

```csharp
// ❌ OnWaypointChange expects TweenCallback<int>, not TweenCallback
tween.OnWaypointChange(i => Debug.Log(i));
```

Most callbacks are `TweenCallback` (no args). Some (`OnWaypointChange`, `OnTargetChange`) are `TweenCallback<T>`. Source: `Core/Delegates.cs`.

---

### 22. Addressables-instantiated tween target + bundle release race

Covered in [INTEGRATION.md](./INTEGRATION.md). Always `SetLink` or await tween before `Addressables.ReleaseInstance`.

---

### 23. Netcode — tween state diverges across clients

Covered in [INTEGRATION.md](./INTEGRATION.md). Use `UpdateType.Manual` driven by server tick, or reconstruct tween on each client from synced parameters.

---

### 24. `DOTween.MarkDirty` causing test flakes

`DOTween.MarkDirty` is an internal optimization trigger. External code rarely calls it. If a test fails intermittently around tween ordering, check whether you're triggering `MarkDirty` via `DOTween.To(...)` + `SetTarget(null)` (null target triggers internal cleanup).

---

### 25. `DOPath` start position != current target position

```csharp
// ❌ Waypoints don't include start position — tween jumps to first waypoint
transform.position = Vector3.zero;
transform.DOPath(new[] { Vector3.right * 5, Vector3.right * 10 }, 2f);
// Jumps to (5,0,0) on frame 1.
```

```csharp
// ✅ Include start position or use relative
transform.DOPath(new[] {
    transform.position,  // start here
    Vector3.right * 5,
    Vector3.right * 10
}, 2f);
```

---

### 26. `SetRecyclable(true)` + cached tween references

```csharp
// ❌ Cached reference points to recycled tween
_cachedTween = transform.DOMove(a, 1f);
_cachedTween.Kill();
// DOTween recycles the Tween instance. Another caller may have it now.
_cachedTween.Restart(); // ?? restarts some other tween
```

```csharp
// ✅ Disable recycling for cached references
_cachedTween = transform.DOMove(a, 1f).SetRecyclable(false);
```

---

### 27. `SetId(object)` boxing

```csharp
// ❌ Boxes the int every call
tween.SetId(42);
```

```csharp
// ✅ Use typed overload
tween.SetId(42); // object overload first; ensure compiler picks int overload via explicit cast if ambiguous
```

Source: `TweenSettingsExtensions.cs:79`. Use string ID for clarity: `tween.SetId("intro");`.

---

### 28. `Tween.Goto(time)` vs `Restart` — state machine

```csharp
tween.Goto(0.5f, andPlay: true);   // seek + play from 0.5s
tween.Restart();                   // reset to 0, play
```

`Goto` does NOT reset loop counters. For a Sequence with loops, `Goto(0)` continues in the current loop iteration. `Restart` resets everything.

---

### 29. IL2CPP AOT + generic `TweenerCore` stripping

On iOS / WebAssembly with IL2CPP, aggressive stripping can remove `TweenerCore<Vector3, Vector3, VectorOptions>` instantiations that are only created via reflection / generic paths.

```xml
<!-- link.xml -->
<linker>
  <assembly fullname="DOTween">
    <type fullname="DG.Tweening.Core.TweenerCore`3" preserve="all"/>
  </assembly>
</linker>
```

Tools → Demigiant → DOTween Utility Panel → "Create ASMDEF" adds this automatically.

---

### 30. DOTween Pro API (`DOTweenAnimation` component) in Free project

```csharp
// ❌ Fails to compile in Free version
gameObject.GetComponent<DOTweenAnimation>();
```

DOTween Pro APIs (the `DOTweenAnimation` component, path editor, animation tab) are NOT in the Free source. If you must support both, gate with `#if DOTWEEN_PRO` (not an official define — define it yourself in Pro-only asmdef's Define Constraints).

---

## How to use this list

- **Code review**: Scan PRs for any of these patterns before approving.
- **Onboarding**: New devs read this once before writing their first tween.
- **Bug hunts**: When "tween doesn't fire", start from #6, #17, #18. When "NRE in tween", start from #1, #11. When "tween runs too fast/slow", start from #7, #20.

Safe Mode is a safety net, not a solution — every Safe Mode warning in the console represents a real lifecycle bug that should be fixed.
