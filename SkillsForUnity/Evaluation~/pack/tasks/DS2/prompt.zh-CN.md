<!-- usk-eval prompt; task DS2; lang zh-CN -->
R1. 在 `{scripts}` 下新建 C# 脚本 `{cls}.cs`，定义 MonoBehaviour 类 `{cls}`，带五个公开的可序列化字段：`door`（Transform）、`openOffset`（Vector3，相对于门初始局部位置的局部偏移）、`speed`（float，每秒局部单位）、`enterCount`（int，默认 0）、`exitCount`（int，默认 0）。脚本必须编译通过。
R2. 行为：进入 Play 模式时记录 `door` 的初始 `localPosition`（记为 `closedPos`），目标打开位置为 `closedPos + openOffset`。只响应标签（tag）为 `Player` 的触发碰撞体（忽略 `{visitor}` 等其他标签的对象）：每当 `Player` 进入触发区时 `enterCount` 加 1；每当 `Player` 离开触发区时 `exitCount` 加 1。只要当前有 `Player` 在触发区内，每帧将 `door.localPosition` 以恒定速率 `speed`（`Vector3.MoveTowards`）移向 `closedPos + openOffset`；当前没有 `Player` 在触发区内时，每帧将 `door.localPosition` 以恒定速率 `speed` 移回 `closedPos`。支持多次进入与离开。
R3. 把 `{cls}` 组件挂到 `{zone}` 上（场景中只有这一个该组件），并在场景中设置：`door` = `{door}` 的 Transform，`openOffset` = ({ox}, {oy}, {oz})，`speed` = {speed}。
R4. 配置触发物理以使触发事件在 Play 模式下正常触发：将 `{zone}` 上的 `BoxCollider` 设为触发器（`isTrigger = true`），并在 `{player}` 上确保存在运动学刚体（`Rigidbody`，`isKinematic = true`，`useGravity = false`）。
R5. 验收会进入 Play 模式，在非 Player 对象 `{visitor}` 穿过触发区以及 `{player}` 沿路径两次进入并离开触发区的五个时刻（每个采样时刻均在门完成平移稳定至少 0.30 s 之后，且距每次 `Player` 进入或离开触发区的时刻至少 0.30 s），读取 `{door}` 的 `localPosition` 与 `{zone}` 上组件的 `enterCount`、`exitCount`：每个采样时刻门的局部位置与期望局部位置（关闭时为 `closedPos`，打开时为 `closedPos + openOffset`）的距离不得超过 0.15，且 `(enterCount, exitCount)` 依次等于 `(0, 0)`、`(1, 0)`、`(1, 1)`、`(2, 1)`、`(2, 2)`；运行期间不得出现错误或异常日志。
R6. 除新建 `{cls}.cs` 外，不要新增、删除或修改任何其他代码文件（包括已有的移动脚本与编辑器脚本）。
R7. 不要修改 `{door}`、`{zone}`、`{player}`、`{visitor}` 及路径路点的名称、标签或变换，也不要修改已有移动脚本组件的配置或其他任何对象和资产。
R8. 在答案中报告新建脚本的资产路径，以及 `{zone}`、`{door}`、`{player}` 的完整层级路径。
