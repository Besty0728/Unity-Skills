<!-- usk-eval prompt; task T3; lang zh-CN -->
R1. `{planks}` 下有 5 个同名的 `Plank`。按世界坐标 X 从小到大把它们依次编号为木板 1 到 5（层级顺序与编号无关）。给每块木板添加 Rigidbody（质量 2）和 HingeJoint；每块木板上有且只有一个 Rigidbody 和一个 HingeJoint。
R2. 木板 1 的 HingeJoint 的 connectedBody 设为 `{anchor}` 的 Rigidbody；木板 2 到 5 中每一块的 connectedBody 设为编号比它小一的那块木板的 Rigidbody。
R3. 所有 HingeJoint 的轴（axis）设为 (0, 0, 1)。
R4. 新建物理材质，保存为 `{mat}`，动摩擦 0.8，静摩擦 0.9，弹性 0。A 下只能有这一个物理材质资产，不要留下其他物理材质资产。
R5. 只把这个物理材质赋给这 5 块木板各自的 BoxCollider，其他碰撞体都不要改。
R6. 把这 5 块木板本身（不含它们的子物体）的 Layer 设为 `{l1}`（第 {i1} 层）。
R7. 在物理层碰撞矩阵中关闭 `{l1}` 与 `{l2}`（第 {i2} 层）之间的碰撞。这是本题明确允许修改的唯一工程设置：其他层对的碰撞设置不要改，也不要增加、删除或重命名任何层。
R8. 除以上要求外，不要修改任何其他对象、组件和资产：`{spare}` 下的 `Plank` 不是木板，`{anchor}` 和 `{planks}` 自身及其子物体顺序也不要改；各木板的名称、标签、激活状态、位置、旋转、缩放、子物体和原有的组件顺序都保持不变。
R9. 在答案中报告：5 块木板各自的编号和 instanceId，物理材质的资产路径，木板所在 Layer 的层序号。
