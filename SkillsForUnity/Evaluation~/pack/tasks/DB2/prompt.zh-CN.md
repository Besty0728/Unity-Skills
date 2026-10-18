<!-- usk-eval prompt; task DB2; lang zh-CN -->
R1. 在 `{scripts}` 下新建脚本 `{cls}.cs`，声明 `public class {cls} : MonoBehaviour`（不要放在命名空间中），包含以下会被序列化的字段（声明为 `public` 或标记 `[SerializeField]`）：
   - `int maxHealth`，代码中的默认值为 {default}；
   - `int currentHealth`；
   - `int deathCount`；
   - `UnityEngine.Events.UnityEvent onDied`；
   并可选包含 `public event System.Action Died;`。项目必须编译通过且无错误。
R2. 在 `Awake()` 中将 `currentHealth` 初始化为 `maxHealth`，并将 `deathCount` 初始化为 0。
R3. 实现 `public int TakeDamage(int amount)`：当 `amount <= 0` 或 `currentHealth <= 0` 时不改变状态并直接返回当前的 `currentHealth`；当 `amount > 0` 且调用前 `currentHealth > 0` 时，将 `currentHealth` 减去 `amount` 并夹紧到不低于 0（`Mathf.Max(0, currentHealth - amount)`），若本次调用使 `currentHealth` 从大于 0 变为 0，则将 `deathCount` 加 1 并触发一次死亡事件（调用 `onDied?.Invoke()` 以及可选的 `Died?.Invoke()`；后续再次受到伤害或已处于 0 生命值时绝不能重复触发死亡事件），最后返回更新后的 `currentHealth`。
R4. 实现 `public int Heal(int amount)`：当 `amount <= 0` 或 `currentHealth <= 0`（已死亡的单位不能被治疗复活）时不改变状态并直接返回当前的 `currentHealth`；当 `amount > 0` 且 `currentHealth > 0` 时，将 `currentHealth` 加上 `amount` 并封顶到不超过 `maxHealth`（`Mathf.Min(maxHealth, currentHealth + amount)`），最后返回更新后的 `currentHealth`。
R5. 把恰好一个 `{cls}` 组件挂到 `{unit}` 上，并在场景中把它的 `maxHealth` 设为 {max_health}（该值必须保存在场景序列化数据中，而不只是代码默认值）。
R6. 不要把 `{cls}` 挂到其他任何对象上，不要修改 `{other}` 或其他现有对象，也不要新增除 `{cls}.cs` 之外的任何代码文件（编辑器脚本也算）。
R7. 场景进入 Play 模式后不得记录任何错误或异常，并且在依次调用 `TakeDamage` 与 `Heal` 的验收序列中（含负值和 0 参数、未致死伤害、超出上限的治疗、致死伤害、死后追加伤害和死后治疗），每次调用的返回值以及调用后的 `currentHealth` 和 `deathCount` 都符合 R2–R4 的规定。
R8. 在答案中报告脚本的资产路径 `scriptPath`、挂载了组件的单位对象的 `unitInstanceId`（Unity 6000.4 及以上 instanceId 为 0，填它的 entityId），以及场景中存储的 `maxHealth` 值。
