<!-- usk-eval prompt; task DB6; lang zh-CN -->
R1. 在 `{scripts}` 下新建脚本 `{cls}.cs`，声明 `public class {cls} : MonoBehaviour`，带四个会被序列化的字段（public，或带 [SerializeField]）：`GameObject prefab`、`Transform container`、`float interval`（单位为秒，代码中的默认值为 {defaultInterval}）和 `int maxCount`（代码中的默认值为 {defaultCount}）。
R2. 进入 Play 模式后，该组件每隔 `interval` 秒游戏时间把 `prefab` 实例化为 `container` 的直接子物体（本地坐标 `(0, 0, 0)`，即世界坐标与 `container` 一致，误差在 0.01 以内）：第一次生成发生在 Play 开始后满 1 个 `interval` 时（Start 时不生成），生成数量达到 `maxCount` 后立即停止，不再生成。
R3. 把恰好一个 `{cls}` 组件挂到 `{emitter}` 上，并在场景中把它的 `prefab` 指向资产 `{prefab}`、`container` 指向 `{container}` 的 Transform、`interval` 设为 {interval}、`maxCount` 设为 {maxCount}（这些值必须存在场景里，而不只是代码默认值）。
R4. 不要把 `{cls}` 挂到其他任何对象上，不要修改其他对象或预制体（`{otherEmitter}`、`{otherContainer}` 与 `{otherPrefab}` 必须保持不变，`{container}` 在编辑模式下保持无子物体），也不要新增除这个脚本之外的任何代码文件（编辑器脚本也算）。
R5. 场景进入 Play 模式后不得记录任何错误或异常；在 Play 开始后第 0.5、1.5、2.5 …… 直至 `maxCount + 0.5` 个 `interval` 的各个采样时刻，`{container}` 的直接子物体数量依次为 0、1、2 …… `maxCount`（允许 ±0.15 秒的采样时间窗口）；在 `maxCount * interval + 1` 秒的最终采样时刻，`{container}` 恰好有 {maxCount} 个直接子物体，且每个子物体都是 `{prefab}` 的实例（名称以预制体资产名开头）并位于 `{container}` 的位置（误差 0.01 以内）。
R6. 在答案中报告脚本的资产路径、挂载了组件的发射器对象的 instanceId、所引用的容器对象的 instanceId，以及场景中存储的 interval 和 maxCount 值。
