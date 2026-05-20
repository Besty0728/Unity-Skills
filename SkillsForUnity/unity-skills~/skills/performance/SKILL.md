---
name: unity-performance
description: "Unity performance red-flag advisor. Use when users want performance review, code runs slow, frame drops, or need optimization guidance for Update, allocations, pooling, physics. Triggers: performance, optimize, slow, lag, frame drop, hot path, Update, allocation, pool, GC, profiler, 性能, 太慢了, 卡顿, 掉帧, 优化性能, 热路径, 内存分配, 对象池."
---

# Unity Performance Red Flags

Use this skill for a high-signal review of likely Unity performance issues. Focus on red flags, not speculative micro-optimizations.

## Check For

- Too many unrelated `Update` / `LateUpdate` / `FixedUpdate` loops
- Repeated `Find`, `GetComponent`, `Camera.main`, or tag lookups in hot paths
- Frequent `Instantiate` / `Destroy` suitable for pooling
- Avoidable per-frame allocations:
  - LINQ
  - string formatting
  - closures
  - boxing
- Reflection in runtime hot paths
- Expensive editor-only helpers leaking into runtime code
- Physics, animation, or UI updates happening at the wrong cadence

## Hidden Costs: Possibility ≠ Actuality

Three counter-intuitive traps where what seems "free" is actually expensive. Each cost is paid for *possibility* of work, not for work actually performed — which is why profilers rarely catch them directly.

### Write permission costs, even without writing
A component whose `Update` *can* mutate `transform.position` pays the cost whether the branch runs or not: dirty-flag and serialization systems treat write permission as a reason to poll every frame. Similarly, a `[SerializeField]` field that is never modified at runtime still sits on the serialization path. The fix is to **remove the permission**, not tighten the branch — split the rarely-needed writer into its own component and `AddComponent` only when needed. *Source: `NetcodeSamples/HelloNetcode/2_Intermediate/07_Optimization/Optimization.md` — "a system with the possibility of writing to a component, regardless of whether it writes to it or not, will always have to be serialized".*

### Sequential seeds produce correlated random streams
Seeding N RNG instances with `baseSeed + i` gives N *similar* streams — patrol paths line up, spawn jitter clumps, loot rolls cluster. Hash the index before seeding:
```csharp
// Wrong — N correlated streams
for (int i = 0; i < N; i++) rngs[i] = new System.Random(baseSeed + i);

// Correct — hash decorrelates adjacent seeds
for (int i = 0; i < N; i++)
    rngs[i] = new System.Random((int)((uint)baseSeed * 2654435761u ^ (uint)i));
```
`Unity.Mathematics.Random.CreateFromIndex(i)` applies this internally and is preferred when the math package is available. *Source: `Dots101/Entities101/Assets/HelloCube/3. Prefabs/SpawnSystem.cs:37-39` and its comment.*

### Logging fires in release builds by default
`Debug.Log` is **not** stripped in Player builds; only methods marked `[Conditional("UNITY_EDITOR")]` (such as `Debug.DrawLine`) have their arguments elided at the call site. That means a log line with interpolation or helper calls runs every frame in shipped games:
```csharp
// Wrong — GetPlayerInfo() and string interpolation execute in release
Debug.Log($"Player {GetPlayerInfo()} at {Time.time}");

// Better — guard the whole expression
if (Debug.isDebugBuild) Debug.Log($"Player {GetPlayerInfo()} at {Time.time}");
```
The same principle as the "possibility write" rule above — the runtime pays for the *possibility* of work, not only for the work.

## Output Format

- Confirmed red flags
- Likely red flags
- Changes worth doing now
- Changes not worth doing now
- Expected gain category: clarity / frame time / GC / scalability

## Guardrails

**Mode**: Documentation only — guidance for AI under all three operating modes; no REST skills to gate.

- Do not recommend large refactors without a meaningful hotspot.
- Do not replace simple code with unreadable “optimized” code unless the hot path is real.
