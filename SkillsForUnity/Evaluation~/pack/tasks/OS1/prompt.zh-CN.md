<!-- usk-eval prompt; task OS1; lang zh-CN -->
R1. `{subject}` 是被照主体（下称 Subject）。在 `{rig}` 下新建三盏灯：`{key}`（类型 Spot）、`{fill}`（类型 Point）、`{rim}`（类型 Spot），每个名字恰好一盏；三盏灯都必须是 `{rig}` 的直接子物体（在场景根上新建的灯要移到 `{rig}` 下）。
R2. 灯的位置用 Subject 的世界坐标系给出：以 Subject 的世界位置为原点，forward 为 Subject 世界空间的 +Z 轴（方位角 0），right 为世界空间的 +X 轴（方位角向右为正），up 为世界空间的 +Y 轴（仰角向上为正）；距离 d、方位角 az、仰角 el 对应的世界位置 = Subject 位置 + d × (cos(el)·sin(az) × right + sin(el) × up + cos(el)·cos(az) × forward)。Subject 有带旋转的父物体，要用它的世界旋转而不是本地旋转；所有缩放都是 1。`{key}`：d={keyD}，az={keyAz}°，el={keyEl}°；`{fill}`：d={fillD}，az={fillAz}°，el={fillEl}°；`{rim}`：d={rimD}，az={rimAz}°，el={rimEl}°。世界位置每个分量的误差不超过 0.05。
R3. 三盏灯的 forward（自身 +Z 轴）都必须指向 Subject 的世界位置：forward 与"灯指向 Subject"方向的夹角不超过 1 度（绕 forward 的滚转不限）。
R4. 强度（Light.intensity）：`{key}` 为 {keyIntensity}，`{fill}` 为 `{key}` 的 {fillPct}%，`{rim}` 为 `{key}` 的 {rimPct}%；误差 0.01。
R5. 颜色（RGB 各分量为 0 到 1 的浮点数，误差 0.01）：`{key}` 为 ({keyColor})，`{fill}` 为 ({fillColor})，`{rim}` 为 ({rimColor})。
R6. 阴影：`{key}` 为 Soft，`{fill}` 为 None，`{rim}` 为 Hard；spotAngle：`{key}` 为 {keySpot}，`{rim}` 为 {rimSpot}（误差 0.1）；三盏灯的 range 都为 {range}（误差 0.01）。
R7. 旧灯 `{old}` 必须停用（把它的 Light 组件 enabled 设为 false，或把它的 GameObject 设为未激活）；不要删除它，也不要修改它的其他属性和变换。
R8. 不要新建其他灯，不要修改 `{subject}`、`{rig}` 和其他任何对象。
R9. 在答案中报告三盏灯的完整层级路径。
