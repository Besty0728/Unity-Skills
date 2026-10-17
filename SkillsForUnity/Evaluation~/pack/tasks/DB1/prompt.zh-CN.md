<!-- usk-eval prompt; task DB1; lang zh-CN -->
R1. 在 `{scripts}` 下新建脚本 `{cls}.cs`，声明 `public class {cls} : MonoBehaviour`，带一个会被序列化的 float 字段 `degreesPerSecond`（public，或带 [SerializeField]），代码中的默认值为 {default}。
R2. 该组件在每一帧让它所在的 GameObject 绕自身的本地 Y 轴旋转，速度为每秒游戏时间 `degreesPerSecond` 度，与帧率无关（按 Time.deltaTime 计算，而不是每帧固定转一个角度）；它不改变任何其他东西。
R3. 把恰好一个 `{cls}` 组件挂到 `{blades}` 上，并在场景中把它的 `degreesPerSecond` 设为 {speed}（这个值必须存在场景里，而不只是代码默认值）。
R4. 不要把 `{cls}` 挂到其他任何对象上（包括 `{other}`），不要修改其他对象，也不要新增除这个脚本之外的任何代码文件（编辑器脚本也算）。
R5. 场景进入 Play 模式后不得记录任何错误或异常，并且 `{blades}` 以上述速度持续转动：运行 {wait} 秒后它大约转过 {turn} 度。
R6. 在答案中报告脚本的资产路径、挂载了组件的对象的 instanceId，以及场景中存储的 degreesPerSecond 值。
