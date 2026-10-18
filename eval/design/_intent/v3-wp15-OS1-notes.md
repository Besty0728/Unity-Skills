# WP15 OS1 设计说明（wp15-oc-b）

作者：Betsy（wp15-oc-b）。文件：`SkillsForUnity/Evaluation~/pack/tasks/OS1/`、`eval/pack/private/OS1.py`、`eval/pack/solutions/OS1.py`、离线假编辑器 `eval/tests/runner/fake_ops_os1.py`、测试 `eval/tests/runner/test_pack_ops_os1.py`。ops / standard，caseVersion 1，minutes 15，requires `{pipeline: "URP", mode: "bypass", dedicated: true, packages: []}`。

## 夹具
`R/<Stage>`（名字按种子取自 Stage/Set/Booth，位置随机，yaw ∈ {25,40,55,70,110,140}，非零）> `<Subject>`（Capsule，本地 yaw ∈ {0,15,−20,30}，世界 yaw = 两者之和，恒非零）；`<Stage>` > `<Rig>`（空容器，随机位置与 yaw）；`<Stage>` > `<OldLamp>`（启用的 Point 灯，随机强度）。所有缩放为 1。`scene_create` 用 EmptyScene，场景里没有默认 Directional Light。

## 规格来源
`rig_spec(seed)` 用 `random.Random("OS1:%d" % seed)` 单独派生，公开/私有各一份逐字相同的副本，`test_rig_spec_agrees_between_public_and_private` 对 300 个种子钉住一致，并保证 fill/rim 的百分比 ≠ 100、两盏 Spot 的 spotAngle ≠ 默认 30。规格：三盏灯的名字集合（KeyLight/FillLight/RimLight 等）、方位角/仰角/距离、key 强度与 fill/rim 百分比、颜色、spotAngle、shadows（Soft/None/Hard 固定）、统一 range。

## 陷阱
1. 方位角/仰角是 Subject **世界**坐标系：Subject 在带 yaw 的 Stage 下，用 localEulerAngles 或世界轴都会放错位置（L2）。提示里把公式写死：pos = S + d·(cos el·sin az·right + sin el·up + cos el·cos az·forward)。
2. 没有 look-at 技能：要算世界欧拉角 rotY = atan2(v.x, v.z)、rotX = −asin(v.y/|v|)，再用 `gameobject_set_transform` 写（L3，容差 1°，roll 不限）。
3. `light_create` 建在场景根：必须 `gameobject_set_parent` 到 rig（L1；留在根上同时触发 G1 违规 L10）。
4. Spot 需要 spotAngle，Point 没有（L6）；fill/rim 强度按百分比换算（L4）；颜色 0–1 浮点（L5）。
5. 旧灯：停用（`light_set_enabled false` 或 `gameobject_set_active false` 都接受）而非删除，其他属性和变换不变（L7）。
6. `light_set_properties` / `light_get_info` / `light_set_enabled` 没有 entityId 参数：6000.4+ 上必须用 path 或 name；参考解全部用 path。

## 断言与 negatives
L1 1 / L2 1.5 / L3 1.5 / L4 1 / L5 1 / L6 1 / L7 1 / L8 0.75 / L9 0.5 / L10 0.5 critical(G1,G2) / L11 0.25。L2–L6 为三盏灯的 fraction。
negatives：local_axes {L2}、world_axes {L2}、no_aim {L3}、aim_away {L3}、flat_intensity {L4}、white_colors {L5}、swapped_shadows {L6}、default_spot {L6}、fill_as_spot {L1}、delete_old {L7}、leave_old {L7}、root_lights {L1,L10}、extra_light {L8}、wrong_report {L9}、outside_root {L10}、direct_file {L11}。alternatives：deactivate_old（GameObject 失活）、component_route（gameobject_create + component_add Light + component_set_property type + light_set_properties）、batch_props（light_set_properties_batch + light_set_enabled_batch）、save_run_scene。
L8 对 Stage 只要求"没有新增子物体"（删除旧灯只由 L7 判），对 Rig 只要求"子物体 ⊆ 三盏灯"（灯留在根上只由 L1/L10 判），避免级联。

## 离线无法验证的点
- Unity 的 euler→forward 约定与验收一致（ZXY，forward=+Z）按 `quat_from_euler` 推断；`gameobject_set_transform` 的 rotX/rotY 写世界欧拉角后 `gameobject_get_info.rotation` 读回的 0–360 规范化不影响四元数比较。
- `light_create` 在真实编辑器里的默认 shadows 为 Soft（文档），fill 的 None 由参考解显式写入；`component_add Light` 的默认 type 未知，component_route 备选显式设 type。
- `light_set_properties` 对 Point 灯传 spotAngle 会进 skipped 而非报错（按源码），参考解只给 Spot 传。
- 假编辑器的 `light_set_properties` 记一条 `property_changed`（targetType Light）；真实 journal 的字段名可能不同，但验收不依赖这些记录（只用 scope 违规）。

## 预估
顶级模型 10–20 分钟；通过率估计 50–80%。主要丢分：本地旋转/世界轴混淆、忘记朝向或 spotAngle、灯留在场景根。

## 共享文件
manifest 未改；lint/check/dry-run 在 /tmp 题包副本上通过。`eval/tests/runner/test_pack_ops_os1.py` 依赖 wp14 的 `pack_offline.py`。
