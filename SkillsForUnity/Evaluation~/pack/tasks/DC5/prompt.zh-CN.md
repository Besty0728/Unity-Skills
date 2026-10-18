<!-- usk-eval prompt; task DC5; lang zh-CN -->
R1. 在 `{scripts}` 下新建 C# 脚本 `{cls}.cs`，定义 `public class {cls} : MonoBehaviour`，带一个 GameObject 类型的可序列化字段 `panel`（public，或标记 [SerializeField]）和两个公开无参方法 `TogglePause()` 与 `Resume()`。工程必须编译通过、没有错误。
R2. 行为：游戏要么处于运行状态（`Time.timeScale` 为 1 且 `panel` 未激活），要么处于暂停状态（`Time.timeScale` 为 0 且 `panel` 激活）。`TogglePause()` 在游戏运行时暂停、在暂停时恢复运行，不论当前状态是由哪个调用造成的；`Resume()` 总是让游戏处于运行状态。进入 Play 模式时游戏处于运行状态（timeScale 为 1，`panel` 仍未激活），直到有按钮被点击。
R3. 把该组件挂到 `{host}` 上（场景中只有这一个该组件；场景里还有一个同名对象，不要挂到它上面），并在场景中把它的 `panel` 字段指向 `{panel}`。
R4. 用 UI 技能新建两个按钮：`{pauseBtn}` 直接位于 `{canvas}` 下，`{resumeBtn}` 直接位于 `{panel}` 下；两者都带 Button 组件。
R5. 给 `{pauseBtn}` 的 Button 组件的 onClick 事件添加一个持久化监听（序列化进场景、Inspector 可见，不是运行时 AddListener），调用 `{host}` 上该组件的 `TogglePause`；给 `{resumeBtn}` 的 onClick 添加一个持久化监听，调用同一组件的 `Resume`。每个 onClick 都只有这一个监听，call state 为 RuntimeOnly 或 EditorAndRuntime。
R6. `{panel}` 在场景中保持未激活；不要修改那个同名的对象、`{canvas}` 及其他任何已有对象，除这个脚本外不要新增或修改任何代码文件（包括编辑器脚本）。
R7. 验收会进入 Play 模式并读取初始状态，然后触发 `{pauseBtn}` 的 onClick 三次，再触发 `{resumeBtn}` 的 onClick，然后再触发 `{pauseBtn}` 一次、`{resumeBtn}` 一次；这六次点击中的每一次之后都读取 `Time.timeScale` 和 `{panel}` 的激活状态，它们必须符合 R2（点击当时处于未激活状态的按钮不起作用）。运行期间不得出现错误或异常日志。
R8. 在答案中报告脚本的资产路径、挂载了组件的对象的完整层级路径，以及两个按钮的完整层级路径。
