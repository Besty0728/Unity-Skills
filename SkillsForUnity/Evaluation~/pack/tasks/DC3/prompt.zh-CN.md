<!-- usk-eval prompt; task DC3; lang zh-CN -->
R1. 把 MonoBehaviour 类 `{oldCls}`（脚本 `{oldPath}`）改名为 `{newFull}`（命名空间 `{ns}` 中的类 `{newCls}`），并把脚本移动到 `{newPath}`。要移动并重命名现有文件，使它保留原来的 GUID（例如用 asset_move，或用 script_move 加 script_rename）；不要删除它再新建文件。工程必须编译通过、没有错误。
R2. 在这个类中把字段 `{oldSpeed}` 改名为 `{newSpeed}`，把字段 `{oldRoute}` 改名为 `{newRoute}`；保留字段 `{pause}`，三个字段的类型和默认值都保持不变，公开方法 `RouteLength()` 和 `LapSeconds()` 及其行为也保持不变（方法内部只改动被重命名的标识符）。
R3. 按旧字段名保存的数据必须继续加载到改名后的字段中：改动之后，该组件的六个实例（场景中的 `{guardA}`、`{guardB}`、`{unitInst}`、`{variantInst}`，预制体 `{basePrefab}` 及其变体 `{variantPrefab}`）都仍在 `{newSpeed}`（原 `{oldSpeed}`）、`{newRoute}`（原 `{oldRoute}`：同样的 Transform、同样的顺序）和 `{pause}` 中保有原来的值，包括 `{unitInst}` 覆盖的速度和变体覆盖的速度；以后打开的、按旧字段名保存的场景或预制体也必须如此。
R4. 不要通过技能在这六个实例上设置 `{oldSpeed}`、`{newSpeed}`、`{oldRoute}`、`{newRoute}` 或 `{pause}`：这些值必须自行在改动后保留下来。
R5. 不要修改其他任何脚本，不要新增代码文件（包括编辑器脚本），也不要修改其他对象和资产。
R6. 验收会进入 Play 模式，对场景中四个实例上的组件分别调用 `LapSeconds()`：每次调用的返回值都必须等于由该实例保存的数据得出的值（RouteLength() 除以 `{newSpeed}`，再加上 `{pause}` 乘以路点数量，与改动前一致），且不得出现错误或异常日志。
R7. 在答案中报告改动后脚本的资产路径、它的 GUID，以及包含命名空间的类的完整名称。
