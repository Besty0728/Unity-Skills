<!-- usk-eval prompt; task OC1; lang zh-CN -->
R1. 预制体 `{turret}` 内嵌套着 `{barrel}` 的一个实例，它在 `{turret}` 中相对预制体根的路径是 `{barrelNode}`。在预制体资产 `{turret}` 本身里，给这个节点添加一个名为 `Muzzle` 的空 GameObject，作为它的直接子物体。
R2. Muzzle 相对其父节点的本地位置为 ({mx}, {my}, {mz})，本地欧拉角为 ({rx}, {ry}, {rz})，本地缩放为 ({s}, {s}, {s})；除 Transform 外 Muzzle 不带任何组件。
R3. 不得修改 `{barrel}` 和 `{tank}`（这两个文件必须保持原样）；`{turret}` 里除了新增 Muzzle 之外不得有其他变化。
R4. 场景中 `{container}` 下有 3 个预制体实例（2 个 `{tank}` 的实例和 1 个 `{turret}` 的实例）。它们都必须通过预制体继承得到 Muzzle，而不是在实例上各自添加对象：完成后每个实例的覆盖列表必须与你开始前完全相同。其中一个实例原本就带有覆盖（属性覆盖和一个新增的组件），必须原样保留。
R5. 除 R1–R4 要求的改动外，不要改动 `{container}` 下的任何对象，也不要新增、删除或修改资产根下的任何资产或文件。
R6. 在答案中报告：Turret.prefab 的资产路径；Muzzle 在 Turret.prefab 内相对预制体根的路径；3 个实例中每个 Muzzle 的完整层级路径和 instanceId（Unity 6000.4 及以上 instanceId 为 0，填它的 entityId）；原本带有覆盖的那个实例的完整层级路径，以及完成后它的覆盖总数（属性覆盖 + 新增组件 + 移除组件 + 新增对象）。
