<!-- usk-eval prompt; task T5; lang zh-CN -->
R1. 以 `{base}` 为基础，创建预制体变体（Prefab Variant）`{variant}`。
R2. 在变体资产中把根物体 Rigidbody 的质量（mass）设为 50。
R3. `{row}` 下有三个同名的 `Crate`，都是 `{base}` 的实例。把其中世界旋转 Y 约为 75° 的那一个换成 `{variant}` 的一个新实例，并删除旧的那个 Crate。
R4. 新实例必须是 `{variant}` 的预制体实例（Prefab instance），名字为 `Crate`，父物体仍是 `{row}`，同级顺序（sibling index）与被换掉的 Crate 原来的相同。
R5. 新实例的世界位置、世界旋转和缩放（localScale）与被换掉的 Crate 原来的一致。
R6. 不得把其他 Crate 的覆盖（override）应用（Apply）到预制体；`{base}` 不能有任何改动。
R7. 除上述修改外，不要修改其他对象和资产：另外两个 Crate 和 `{row}` 下其他对象的属性、覆盖和同级顺序都保持不变。
R8. 在答案中报告变体资产的路径、新实例的 instanceId，以及它的同级顺序（从 0 开始）。
