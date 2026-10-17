<!-- usk-eval prompt; task DB3; lang zh-CN -->
R1. 在 `{scripts}` 下新建脚本 `{cls}.cs`，声明 `public class {cls} : MonoBehaviour`，带一个会被序列化的 float 字段 `interval`（public，或带 [SerializeField]），单位为秒，代码中的默认值为 {default}。
R2. 该组件每隔 `interval` 秒游戏时间切换一次它所在 GameObject 上 Light 组件的 enabled：Play 开始后的第一个 interval 内灯保持开始时的状态（亮着），然后关闭，再过一个 interval 又打开，如此往复；组件在 Start 时不改变灯的状态，也不改变任何其他东西。
R3. 把恰好一个 `{cls}` 组件挂到 `{lamp}` 上，并在场景中把它的 `interval` 设为 {interval}（这个值必须存在场景里，而不只是代码默认值）。
R4. 不要把 `{cls}` 挂到其他任何对象上，不要修改其他对象（`{other}` 必须保持亮着），也不要新增除这个脚本之外的任何代码文件（编辑器脚本也算）。
R5. 场景进入 Play 模式后不得记录任何错误或异常，并且在 Play 开始后 0.5、1.5、2.5 和 3.5 个 interval 的时刻采样，`{lamp}` 的 Light 依次为亮、灭、亮、灭。
R6. 在答案中报告脚本的资产路径、挂载了组件的灯对象的 instanceId，以及场景中存储的 interval 值。
