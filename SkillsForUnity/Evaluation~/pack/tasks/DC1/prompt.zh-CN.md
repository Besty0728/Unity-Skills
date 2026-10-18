<!-- usk-eval prompt; task DC1; lang zh-CN -->
R1. 脚本 `{script}` 中的 `{cls}` 组件是一个按堆叠存放物品的库存，它的堆叠逻辑有缺陷。请就地修改这个脚本文件来修复：保持它的路径和 GUID（不要删除、重建、移动或重命名它），`{chest}` 上的组件保持挂载。修复后工程必须编译通过、没有错误。
R2. 公开接口必须保持原样：类名；`[Serializable]` 的嵌套类 `Stack` 及其字段 `string item`、`int count`；字段 `int maxStack`、`int slotCount`、`List<Stack> stacks`；事件 `public event Action Changed`（保持为事件）；以及方法 `int CountOf(string item)`、`int Add(string item, int count)`、`int Remove(string item, int count)` 的名称、参数类型及顺序、返回类型，且都是 public。不要新增、删除或修改其他任何 public 成员；可以新增 private 成员。
R3. `Add(item, count)`：count 小于等于 0 时返回 0、不做任何改动、不触发 Changed。否则先按列表顺序把该物品已有的堆叠补到 maxStack，然后在 stacks 的数量小于 slotCount 时在 stacks 末尾追加该物品的新堆叠（每个至多 maxStack），返回放不下的数量；只要至少存入了 1 个单位就触发 Changed 恰好一次，否则不触发。
R4. `Remove(item, count)`：count 小于等于 0，或 stacks 中没有该物品的堆叠时，返回 0、不做任何改动、不触发 Changed。否则从该物品在列表中最后一个堆叠开始、向列表前部依次移除至多 count 个单位，把数量减到 0 的堆叠从 stacks 中删除，返回实际移除的数量，并触发 Changed 恰好一次（这种情况下至少会移除 1 个单位）。
R5. `CountOf(item)` 返回该物品在所有堆叠中的总数。任何调用之后，每个堆叠的 count 都在 1 到 maxStack 之间，并且 stacks 的数量不超过 slotCount。
R6. 要修正通用逻辑，而不只是让某个示例成立：例如 maxStack 为 10、slotCount 为 3 时，从空库存 `Add("Ore", 25)` 返回 0 并留下 10、10、5 三个堆叠，再 `Add("Wood", 4)` 返回 4，然后 `Remove("Ore", 12)` 返回 12 并留下 10、3 两个堆叠。验收使用 `{chest}` 的组件上保存的 maxStack、slotCount 和 stacks，以及另外的数量。
R7. 不要修改 `{watcher}`（题目环境的一部分），不要新增其他代码文件，不要改动其他对象，也不要改动 `{chest}` 上组件在场景中保存的 maxStack、slotCount 和 stacks。
R8. 验收会进入 Play 模式，在 `{chest}` 的组件上调用一串 Add / Remove / CountOf，检查每次调用的返回值、每次调用后的 stacks，以及每次调用后同一对象上 `{watcherCls}` 记录到的 Changed 触发次数（它只在事件真正触发时计数）。运行期间不得出现错误或异常日志。
R9. 在答案中报告脚本的资产路径和挂载了库存组件的对象的 instanceId。
