<!-- usk-eval prompt; task DC4; lang zh-CN -->
R1. 在 `{scripts}` 下新建 C# 脚本 `{cls}.cs`，定义 MonoBehaviour 类 `{cls}`，带两个可序列化字段（public 或标有 `[SerializeField]`）：`muzzle`（Transform）和 `projectilePrefab`（GameObject），以及公开方法 `public GameObject Fire(Transform target)`。工程必须编译通过、没有错误。
R2. `Fire(Transform target)` 的行为：在 Play 模式下传入非空 `target` 调用时，在 `muzzle` 的世界位置（`muzzle.position`）实例化 `projectilePrefab`，保留 `projectilePrefab` 上的 Rigidbody 参数（不要改写生成实例的 mass、drag 或 useGravity），综合当前工程的 `Physics.gravity`、该 Rigidbody 的质量与线性阻力（`drag` / `linearDamping`）以及 `Time.fixedDeltaTime`，通过 Unity 物理（`AddForce` 或初速度）发射该 Rigidbody，使其弹道抵达 `target.position`，并返回所生成的 `GameObject`。不要把弹丸直接瞬移到目标处，不要设为 `target` 的子物体，不要把 Rigidbody 设为 kinematic 或关闭重力，也不要在发射后用 `Update`、`FixedUpdate` 或协程直接驱动其位置。
R3. 把 `{cls}` 挂到 `{battery}` 上（场景中只有这一个该组件），并在场景中设置：`muzzle` = `{muzzle}` 的 Transform，`projectilePrefab` = `{prefab}`。
R4. 不要修改 `{prefab}`、工程的 `Physics.gravity`、`Time.fixedDeltaTime`，也不要修改 `{battery}`、`{muzzle}` 或其他现有场景对象与资产的变换和名称。
R5. 除 `{cls}.cs` 外，不要新增或修改任何代码文件（包括编辑器脚本）。
R6. 验收会进入 Play 模式并调用 `Fire(Transform)` 共 3 次，每次传入 3 个处于不同世界位置与高度的隐藏目标 Transform 之一：每次调用返回的 GameObject 都必须是新生成的 `{prefab}` 实例，带有非运动学 Rigidbody（`useGravity == true`，保持原有的质量与阻力），从 `{muzzle}` 处以不低于 1.0 m/s 的初速度发射，且在发射后 2.5 s 内其首次碰撞位置（或自由飞行物理弹道上的最近点）与当次 `target.position` 的距离不超过 0.25 m；运行期间不得出现错误或异常日志。
R7. 在答案中报告脚本的资产路径、`{battery}` 的完整层级路径，以及 `{prefab}` 的资产路径。
