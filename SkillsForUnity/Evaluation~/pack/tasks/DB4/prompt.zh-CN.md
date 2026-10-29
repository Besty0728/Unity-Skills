<!-- usk-eval prompt; task DB4; lang zh-CN -->
R1. 在 `{scripts}` 下新建脚本 `{cls}.cs`，声明 `public class {cls} : MonoBehaviour`，带四个会被序列化的字段（public，或带 [SerializeField]）：`Transform target`、`Vector3 offset`（代码中的默认值为 {defaultOffset}，用数字字面量写成 `new Vector3(x, y, z)` 字段初始化器）、`bool smoothing`（代码中的默认值为 true）和 `float smoothSpeed`（代码中的默认值为 {defaultSpeed}）。脚本必须能无错误地编译。
R2. 该组件在自己的 `LateUpdate()` 方法中完成工作（不是 `Update` 或 `FixedUpdate`），每帧一次：计算点 `target.position + offset`，其中 `offset` 是世界空间向量，直接相加（不随 `target` 旋转或缩放）。`smoothing` 为 false 时，把它所在 GameObject 的世界坐标（`transform.position`）精确设为该点；`smoothing` 为 true 时，改为用 `Vector3.Lerp(当前位置, 该点, smoothSpeed * Time.deltaTime)` 让世界坐标向该点移动。
R3. 把恰好一个 `{cls}` 组件挂到相机对象 `{cam}` 上，并在场景中把它的 `target` 指向 `{rover}` 的 Transform、`offset` 设为 {offset}、`smoothing` 设为 false（这些值必须存在场景里，而不只是代码默认值）。
R4. 不要把 `{cls}` 挂到其他任何对象上，除 `{cam}` 外不要修改任何对象（包括 `{rover}` 和 `{other}`），也不要新增除这个脚本之外的任何代码文件（编辑器脚本也算）。
R5. 场景进入 Play 模式后不得记录任何错误或异常，并且相机在关闭平滑时按上述方式跟随：从最初几帧起，以及游戏运行中 `{rover}` 被移动（例如被其他脚本或工具瞬移到别处）后的下一帧起，`{cam}` 的世界坐标都等于 `{rover}` 的世界坐标加上 {offset}，每个轴的误差在 0.001 以内。
R6. 在答案中报告脚本的资产路径、挂载了组件的对象的 instanceId，以及它的 `target` 字段在场景中引用的对象的 instanceId（Unity 6000.4 及以上 instanceId 为 0，改填 entityId）。
