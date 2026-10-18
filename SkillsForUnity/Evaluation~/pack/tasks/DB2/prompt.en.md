<!-- usk-eval prompt; task DB2; lang en -->
R1. Under `{scripts}`, create the script `{cls}.cs` declaring `public class {cls} : MonoBehaviour` (in the global namespace) with the following serialized fields (declared `public`, or marked `[SerializeField]`):
   - `int maxHealth`, whose default value in the code is {default};
   - `int currentHealth`;
   - `int deathCount`;
   - `UnityEngine.Events.UnityEvent onDied`;
   and optionally `public event System.Action Died;`. The project must compile without errors.
R2. In `Awake()`, initialize `currentHealth` to `maxHealth` and `deathCount` to 0.
R3. Implement `public int TakeDamage(int amount)`: when `amount <= 0` or `currentHealth <= 0`, leave the state unchanged and return `currentHealth`; when `amount > 0` and `currentHealth > 0` before the call, subtract `amount` from `currentHealth` clamped to at least 0 (`Mathf.Max(0, currentHealth - amount)`), and if this call transitions `currentHealth` from greater than 0 to 0, increment `deathCount` by 1 and fire the death event once (invoke `onDied?.Invoke()` and optionally `Died?.Invoke()`; subsequent damage calls while already at 0 health must never fire the death event again), then return the updated `currentHealth`.
R4. Implement `public int Heal(int amount)`: when `amount <= 0` or `currentHealth <= 0` (a dead unit cannot be healed back to life), leave the state unchanged and return `currentHealth`; when `amount > 0` and `currentHealth > 0`, add `amount` to `currentHealth` capped at `maxHealth` (`Mathf.Min(maxHealth, currentHealth + amount)`), and return the updated `currentHealth`.
R5. Attach exactly one `{cls}` component to `{unit}` and set its `maxHealth` to {max_health} in the scene (the value must be stored in the scene's serialized data, not only as the code default).
R6. Do not attach `{cls}` to any other object, do not modify `{other}` or any other existing object, and add no code file other than `{cls}.cs` (editor scripts included).
R7. When the scene runs in Play Mode no error or exception may be logged, and across a sequence of `TakeDamage` and `Heal` invocations (covering negative and 0 amounts, non-lethal damage, healing past the cap, lethal damage, overkill damage while already dead, and healing while dead), every return value and the resulting `currentHealth` and `deathCount` match R2–R4.
R8. In the answer, report the asset path of the script (`scriptPath`), the `unitInstanceId` of the unit object carrying the component, and the `maxHealth` value stored in the scene.
