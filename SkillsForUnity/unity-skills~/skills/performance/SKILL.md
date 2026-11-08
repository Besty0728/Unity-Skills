---
name: unity-performance
description: Advise on Unity performance red flags
---

# Unity Performance Red Flags

Documentation only: no REST skills. Use for a high-signal review of likely Unity performance issues. Focus on red flags, not speculative micro-optimizations.

## When to use
- Reviewing performance
- Diagnosing frame drops or hitches
- Reducing allocations
- Planning pooling or optimization

## Check for
- Too many unrelated `Update` / `LateUpdate` / `FixedUpdate` loops
- Repeated `Find`, `GetComponent`, `Camera.main`, or tag lookups in hot paths
- Frequent `Instantiate` / `Destroy` that should be pooled
- Avoidable per-frame allocations: LINQ, string formatting, closures, boxing
- Reflection in runtime hot paths
- Expensive editor-only helpers leaking into runtime code
- Physics, animation, or UI updates running at the wrong cadence

## Hidden costs: possibility is not actuality
Three counter-intuitive traps where something that seems free is expensive. Each cost is paid for the *possibility* of work, not for work actually performed, which is why profilers rarely catch them directly.

### Write permission costs, even without writing
A component whose `Update` *can* mutate `transform.position` pays the cost whether or not the branch runs: dirty-flag and serialization systems treat write permission as a reason to poll every frame. Likewise, a `[SerializeField]` field that is never modified at runtime still sits on the serialization path. The fix is to **remove the permission**, not tighten the branch: split the rarely-needed writer into its own component and `AddComponent` it only when needed. *Source: `NetcodeSamples/HelloNetcode/2_Intermediate/07_Optimization/Optimization.md`: "a system with the possibility of writing to a component, regardless of whether it writes to it or not, will always have to be serialized".*

### Sequential seeds produce correlated random streams
Seeding N RNG instances with `baseSeed + i` gives N *similar* streams: patrol paths line up, spawn jitter clumps, loot rolls cluster. Hash the index before seeding:
```csharp
// Wrong: N correlated streams
for (int i = 0; i < N; i++) rngs[i] = new System.Random(baseSeed + i);

// Correct: the hash decorrelates adjacent seeds
for (int i = 0; i < N; i++)
    rngs[i] = new System.Random((int)((uint)baseSeed * 2654435761u ^ (uint)i));
```
`Unity.Mathematics.Random.CreateFromIndex(i)` applies this internally and is preferred when the math package is available. *Source: `Dots101/Entities101/Assets/HelloCube/3. Prefabs/SpawnSystem.cs:37-39` and its comment.*

### Logging fires in release builds by default
`Debug.Log` is **not** stripped in Player builds; only methods marked `[Conditional("UNITY_EDITOR")]` (such as `Debug.DrawLine`) have their arguments elided at the call site. A log line with interpolation or helper calls therefore runs every frame in shipped games:
```csharp
// Wrong: GetPlayerInfo() and string interpolation execute in release
Debug.Log($"Player {GetPlayerInfo()} at {Time.time}");

// Better: guard the whole expression
if (Debug.isDebugBuild) Debug.Log($"Player {GetPlayerInfo()} at {Time.time}");
```
Same principle as the write-permission rule: the runtime pays for the *possibility* of work, not only for the work.

## Output format
- Confirmed red flags
- Likely red flags
- Changes worth doing now
- Changes not worth doing now
- Expected gain category: clarity / frame time / GC / scalability

## Pitfalls
- Do not recommend large refactors without a meaningful hotspot.
- Do not replace simple code with unreadable "optimized" code unless the hot path is real.
