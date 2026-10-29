<!-- usk-eval prompt; task DC6; lang zh-CN -->
R1. 在 `{scripts}` 下新建两个 C# 脚本，每个脚本在全局命名空间中声明一个与文件同名的 MonoBehaviour 类：
   - `{mgr}.cs`：`public class {mgr} : MonoBehaviour`，带初始为 0 的公开字段 `int score`，以及把 `amount` 加到 `score` 上的方法 `public void Add(int amount)`；
   - `{coin}.cs`：`public class {coin} : MonoBehaviour`，带公开字段 `int value` 和 `{mgr} manager`。
   工程必须编译通过、没有错误。
R2. `{coin}` 在 Play 模式下的行为：当某个所在 GameObject 标签（tag）为 `Player` 的碰撞体进入金币的触发器时，金币调用 `manager.Add(value)`，然后用 `gameObject.SetActive(false)` 停用自己的 GameObject（不要销毁它）。无论有多少个 `Player` 碰撞体、在什么时候碰到它，每枚金币总共最多加一次分值。
R3. 把一个 `{mgr}` 挂到 `{board}` 上，它必须是场景中唯一的 `{mgr}`。给 `{coins}` 下的 7 个直接子物体（金币）各挂恰好一个 `{coin}`，不要挂到其他对象上；并在场景中为每枚金币设置：`manager` = `{board}` 上的 `{mgr}` 组件；`value` 在金币名称以 `Gold` 开头时为 {gold}，以 `Silver` 开头时为 {silver}。
R4. 金币的物理设置：把每枚金币已有的 `SphereCollider` 设为触发器（`isTrigger = true`），不要给任何金币添加别的碰撞体，并把每个金币对象放到层 `{loot}`（第 {iloot} 层）。本工程的层碰撞矩阵中，`{actor}`（第 {iactor} 层，即 `{player}` 和 `{box}` 所在的层）与 `{loot}` 碰撞，但与 `Default` 不碰撞。
R5. `{player}` 和它的子物体 `{box}` 的标签都是 `Player`；进入 Play 模式后，`{player}` 上的 `{mover}` 组件带着它们沿 `{route}` 下的路点移动，除此之外不得有任何东西移动它们。不要修改 `{mover}` 组件、`{route}` 及其路点，也不要修改 `{player}` 和 `{box}` 的名称、标签、层、变换、激活状态或碰撞体，也不要给它们添加碰撞体；可以给它们添加非碰撞体的组件。
R6. 不要修改层碰撞矩阵、层名称、标签或任何其他工程设置。
R7. 除 R1 的两个脚本外，不要新增、删除或修改任何代码文件（包括 `{mover}` 脚本和编辑器脚本），也不要修改其他对象：`{board}`、`{coins}` 和各金币的名称、层级路径、位置、旋转、缩放和激活状态保持不变。
R8. 验收会进入 Play 模式，等 `{player}` 到达最后一个路点后，读取 `{board}` 上的 `score` 以及每枚金币的激活状态：`score` 必须恰好等于路径途中被 `{player}` 或 `{box}` 的碰撞体碰到过球体的那些金币的分值之和，每枚只计一次；恰好这些金币处于停用状态，其余金币保持激活；运行期间不得出现错误或异常日志。
R9. 在答案中报告两个脚本的资产路径、`{board}` 的完整层级路径，以及 `finalScore`：`{player}` 走完整条路径后 `score` 的值。
