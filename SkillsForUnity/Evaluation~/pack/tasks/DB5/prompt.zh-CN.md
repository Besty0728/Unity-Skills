<!-- usk-eval prompt; task DB5; lang zh-CN -->
R1. 在 `{scripts}` 下新建脚本 `{cls}.cs`，声明 `public class {cls} : MonoBehaviour`，带一个会被序列化的字段 `label`（类型 `UnityEngine.UI.Text`，public 或带 [SerializeField]）和一个无参数的 public 方法 `OnClick()`。
R2. `OnClick()` 每被调用一次就把一个从 0 开始的计数加 1，并把新的计数以十进制数字写进 `label.text`（例如调用两次后标签显示 2）。
R3. 把恰好一个 `{cls}` 组件挂到 `{button}` 上，并把它的 `label` 字段指向 `{label}` 上的 Text 组件。
R4. 给 `{button}` 上 Button 组件的 `onClick` 事件添加一个持久监听（序列化进场景、在 Inspector 中可见的那种，而不是运行时 AddListener），调用同一对象上 `{cls}` 的 `OnClick`；该事件只有这一个监听，其调用状态为 RuntimeOnly 或 EditorAndRuntime。
R5. 不要给 `{other}` 的 onClick 添加监听，不要修改其他对象，标签在场景中保持显示 0，也不要新增除这个脚本之外的任何代码文件（编辑器脚本也算）。
R6. 场景进入 Play 模式后不得记录任何错误或异常，并且在该按钮的 onClick 被触发两次之后，标签显示 2。
R7. 在答案中报告脚本的资产路径、挂载了组件的按钮对象的 instanceId，以及 label 所指 Text 组件所在对象的 instanceId。
