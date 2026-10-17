<!-- usk-eval prompt; task DS3; lang zh-CN -->
R1. 在 `{scripts}` 下新建两个 C# 脚本：`{cfgCls}.cs` 定义 ScriptableObject 类 `{cfgCls}`，带公开字段 `count`（int）和 `interval`（float，秒）；`{spawnerCls}.cs` 定义 MonoBehaviour 类 `{spawnerCls}`，带公开字段 `config`（类型 `{cfgCls}`）、`prefab`（GameObject）和 `container`（Transform）。两个脚本都必须编译通过。
R2. 创建两个 `{cfgCls}` 类型的资产：`{assetA}`（count = {countA}，interval = {intervalA}）和 `{assetB}`（count = {countB}，interval = {intervalB}）。
R3. 行为：进入 Play 模式后，每个 `{spawnerCls}` 组件按它引用的 config 资产工作：每隔 `interval` 秒把 `prefab` 实例化为 `container` 的直接子物体，第一次实例化发生在进入 Play 后 `interval` 秒，直到该组件生成的实例数达到 `count` 为止，之后不再生成。数值必须来自所引用的资产：不要在脚本里写死，也不要在运行时新建配置实例代替它。
R4. 给 `{spawnerA}` 挂上 `{spawnerCls}`：config = `{assetA}`，prefab = `{prefab}`，container = `{containerA}` 的 Transform；给 `{spawnerB}` 挂上 `{spawnerCls}`：config = `{assetB}`，prefab = `{prefab}`，container = `{containerB}` 的 Transform。场景中只有这两个该组件。
R5. 验收会进入 Play 模式，在若干时刻读取两个 container 的直接子物体：时刻 t 的子物体数必须介于按 R3 规则在 t−0.15 秒时应有的数量与 t+0.15 秒时应有的数量之间（含两端）；生成结束后每个 container 的子物体数恰好等于其 count，且每个子物体都是 `{prefab}` 的实例；运行期间不得出现错误或异常日志。
R6. 除这两个脚本外，不要新增或修改任何代码文件（包括编辑器脚本）。
R7. 不要修改 `{prefab}`、两个 container 和两个 Spawner 对象的名称与变换，也不要修改其他任何对象和资产。
R8. 在答案中报告两个脚本的资产路径、两个配置资产的路径，以及两个 Spawner 对象的完整层级路径。
