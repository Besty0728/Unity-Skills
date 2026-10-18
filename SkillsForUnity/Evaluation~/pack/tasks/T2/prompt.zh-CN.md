<!-- usk-eval prompt; task T2; lang zh-CN -->
R1. 在 `{canvas}` 下直接创建 UGUI 按钮 `StartButton`，带 RectTransform、Image 和 Button 组件；按钮文字用 TextMeshPro（按钮之下的 TextMeshProUGUI 组件），内容为 `Start Game`。
R2. StartButton 的 RectTransform：anchorMin 与 anchorMax 都是 (0.5, 0)，pivot 为 (0.5, 0)，anchoredPosition 为 (0, 120)，尺寸为 320×80。
R3. 场景里还没有 EventSystem：在 `{ui}` 下创建名为 `EventSystem` 的对象，挂 EventSystem 和 StandaloneInputModule 组件；整个场景只能有这一个 EventSystem。
R4. 按钮 onClick 的持久监听恰好两条，顺序为：第一条调用标签为 Finish 的那个 `Menu` 对象的 GameObject.SetActive(false)；第二条调用 `{gc}` 上 `{cls}` 组件的 StartGame()。两条监听都只在运行时调用（RuntimeOnly）。
R5. 不要修改其他对象和资产；除 StartButton 外不要新建 Button，也不要新建 Canvas。
R6. 在 Play 模式下点击该按钮一次后，标签为 Finish 的 Menu 应变为未激活，其他 Menu 保持激活，`{cls}` 组件的 startCount 应为 1；Play 模式运行期间不得出现错误或异常日志。
R7. 在答案中报告 StartButton 的 instanceId、EventSystem 对象的 instanceId，以及按顺序列出每条监听所调用对象所在 GameObject 的 instanceId 和方法名。
