# WP15 OC5 / OC7 设计说明（wp15-oc-b）

作者：Betsy（wp15-oc-b）。文件：`SkillsForUnity/Evaluation~/pack/tasks/{OC5,OC7}/`、`eval/pack/private/{OC5,OC7}.py`、`eval/pack/solutions/{OC5,OC7}.py`、离线测试 `eval/tests/runner/test_pack_challenge_oc.py`。两题都是 ops / challenge，caseVersion 1，requires `{pipeline: "URP", mode: "bypass", dedicated: true}`；OC5 另声明 `packages: ["com.unity.ugui"]`（2022.3 与 6000.x 的默认工程都含该包，TMP 不需要：所有元素都是 Image/Panel，没有文本组件）。

## OC5 UI prefab 规格（20 分钟）

### 夹具
`R/<Canvas>`（WorldSpace Canvas，1280×720，名字按种子取自 MainCanvas/ScreenCanvas/UIRoot）> `<HUD>`（HUD/BattleHUD/PlayerHUD），后者是 `A/Prefabs/<HUD>.prefab` 的已连接实例。prefab 根是 1280×720 的面板（Image）并带一个嵌套 Canvas；直接子节点 TopBar（HorizontalLayoutGroup，无子物体）、Ammo、Minimap、Crosshair、Compass、Notice（未激活），创建顺序、初始锚点/位置/尺寸都来自 `ctx.rng`。实例上有一处覆盖：Ammo/Minimap/Compass 之一的 Image 颜色被改成 (0.2, 0.8, 1, 1)。

### 规格来源
`layout_spec(seed)` 用 `random.Random("OC5:%d" % seed)` 单独派生（与 build 的 rng 流无关），公开模块与私有模块各有一份逐字相同的副本；`test_layout_spec_agrees_between_public_and_private` 对 300 个种子钉住两者一致。规格：6 个元素的 anchorMin/anchorMax/pivot/anchoredPosition/sizeDelta、根子节点顺序、spacing 与 padding。`start_order`/`start_rect` 保证初始值每个元素至少一项与规格不同（测试覆盖）。

### 陷阱
1. 资产 vs 实例：改实例只产生覆盖，资产不变（P1–P5）；改实例后 `prefab_apply` 会把实例自己的颜色覆盖推进资产并清空覆盖（P5、P6），而顺序仍然改不了（P2）。
2. `posX/posY` 写的是 localPosition：根是 1280×720、锚点在角落时与 anchoredPosition 差 640/360（P1）。Crosshair 居中锚点恰好相等，所以该 negative 只让 P1 的 fraction 降到 1/6。
3. Notice 必须在资产里激活（`prefab_set_active`），实例上 set_active 只是覆盖（P3+P5）。
4. spacing/padding 是 `m_Spacing`、`m_Padding.m_Left/...`（P4）。
5. GUID 不变：删掉重建会丢 GUID、丢覆盖、把颜色写进资产（P10+P5+P6）。
6. 禁止 revert 实例覆盖（P5）。

### 断言与 negatives
P1 2.5 / P2 1 / P3 1 / P4 1 / P5 1.5 / P6 1 / P7 0.5 / P8 0.5 critical(G1,G2) / P9 0.5 / P10 0.5。
negatives：local_position {P1}、instance_only {P1–P5}、instance_then_apply {P2,P5,P6}、wrong_order {P2}、activate_instance {P3,P5}、spacing_only {P4}、revert_instance {P5}、edit_root {P6}、replace_asset {P5,P6,P10}、wrong_report {P7}、outside_root {P8}、direct_file {P9}。alternatives：edit_batch（单个 prefab_edit_batch 事务）、serialized_fields（m_AnchorMin 等 Vector2 逐项写）、save_run_scene。

### 离线无法验证的点（实机门禁要看）
- `prefab_get_overrides` 的幻影条目过滤依赖 DataEquals：资产被重新保存后，根 RectTransform 的 m_LocalPosition 等若序列化出 −0 之类差异，会凭空多出覆盖条目让 P5 误伤。C0 与终态用同一归一化比较，但数值漂移无法离线排除。
- `ui_create_canvas` WorldSpace 下 Canvas RectTransform 不被驱动、`component_add Canvas` 在带 Image 的面板上可行、`prefab_create` 返回 `connected:true`——按源码推断，未实跑。
- `replace_asset` negative 依赖 `asset_delete` 后实例变为 missing prefab 仍保留对象，再 `prefab_create` 能重连；若 Unity 行为不同，该 negative 的失败集可能变化。
- P6 把 6 个元素的 RectTransform 字段、Notice 的 activeSelf、根子节点的 siblingIndex、TopBar 布局组的 m_Spacing/m_Padding 排除后比较整个资产（节点 + 组件序列化值）。TopBar 没有子物体，所以没有布局驱动值漂移的问题；若 Unity 在保存时改写其他字段（例如 m_LocalEulerAnglesHint），门禁会暴露。
- 偏差来源还有 `prefab_list_components(includeProperties)` 的 ObjectReference 值：目前全部为 null，若某组件引用了资产内对象，其 entityId 文本会随重载变化。

### 预估
顶级模型 20–30 分钟；通过率估计 35–60%。主要丢分：改实例、posX/posY、忘记顺序或 padding。

## OC7 大层级重构（30 分钟）

### 夹具
`R/<Yard>/<Zones>/<Tag>_<Suffix>` 6 个容器（Respawn/Finish × 3 个后缀，后缀集合、Yard/Loose/Zones 的名字、kind 都按种子取），每个容器旋转（各自不同的 yaw，部分带倾斜）、非均匀缩放、带位置；`R/<Yard>/<Loose>` 平铺 146 个对象（142 个带标签 + 4 个 Untagged）；每个容器里已有 1 个归位对象（名字位于自然序中间），另有 2 个对象放错了容器。合计 154 个对象，144 个需要移动。名字 `<Kind>_<n>_<Suffix>`：每个容器强制含一对同 kind 的 5–9 与 10–49 号（字典序与自然序相反）；每个后缀的 Respawn/Finish 容器共享 2 个名字（同名对象）；8 个对象旋转与容器不同、2 个放错容器的对象旋转等于当前容器 → 10 个无法保持缩放；6 个对象未激活。Zones 先于 Loose 创建（层级序），tags 用内置 Respawn/Finish（无需改 TagManager）。

### 陷阱
1. `gameobject_set_parent` 没有 worldPositionStays 参数，本身就保持世界位姿（Undo.SetTransformParent）；"修复"旧数字反而出错：scaleX/Y/Z 是本地缩放（H4），localPos 写世界坐标（H2），再改旋转（H3）。
2. 不可表示情形：旋转与容器不同的对象在非均匀缩放下无法保持 lossyScale，提示里明确规则（0.1°），答案 scaleNotPreserved 必须恰好等于该集合（H8）。
3. 同名对象：按名字定位只会动其中一个（H1、H5、H10）。
4. `gameobject_find(tag=…)` 默认 50 条且只返回激活对象（FindGameObjectsWithTag）；`batch_query_gameobjects` 默认不含未激活对象。提示写明"必须覆盖每一个对象"。
5. 自然序 + 已归位对象：不能只按顺序 append，需要 `gameobject_set_sibling_index`（无批量版本，用 /skills/batch 每 50 步）。参考解按自然序给每个移动对象设 index，跳过已归位对象，它自然落到剩下的那个位置。
6. 已归位对象不得操作（H6 查本地 TRS + journal 中以其 id/path 出现的记录）；放错容器的对象也要移动。
7. Untagged 留在原位（H7）。

### 断言与 negatives
H1 1.5 (fraction 到位) / H2 1.5 / H3 1 / H4 1 / H5 1.5 (fraction 容器顺序，比较 id 序列) / H6 1 / H7 0.5 / H8 0.5 / H9 0.5 / H10 0.25 / H11 0.5 critical(G1,G2) / H12 0.25。H2–H4 的分母是"已在正确容器下"的对象（避免与 H1 级联），一个都没有时记 0。
negatives：lexicographic {H5}、restore_scale {H4}、restore_local_position {H2}、touch_home {H6}、reset_rotation {H3}、move_untagged {H5,H7}、keyed_by_name {H1,H5,H10}、first_page {H1,H5,H8,H10}、recreate_one {H1,H5,H9}、declare_none {H8}、wrong_count {H10}、outside_root {H11}、direct_file {H12}。alternatives：single_calls、explicit_local（重设父物体后用四元数计算把本地 TRS 显式写回）、save_run_scene。
离线：`test_pack_challenge_oc.py` 用 `pack_offline.OpsTaskTests` + `YardFakeEditor`（补 set_tag_batch、set_parent_batch、带 tag 的 find/query）跑通 reference×2、全部 negatives/alternatives（legacy 与 6000.4 两种 id），并钉住种群结构（154/6/144/6/4、10 个不可表示、6 个未激活、每个后缀 ≥2 对同名、每个容器自然序 ≠ 字典序、换种子后结构与命名不同）。

### 离线无法验证的点
- 假编辑器的 set_parent 对非对齐对象也"保持"缩放（除法模型），真 Unity 会产生斜切；验收对这些对象只查位置与旋转，所以结论一致，但 `explicit_local` 的本地值与 Unity 分解结果是否在 1e-3 内一致要实跑。
- journal：一次 set_parent 是否只产生一条 `gameobject_reparented`（H9 以"同一对象出现两个不同 newParentPath"计数，重复事件不误伤）；`property_changed` 是否带子对象身份（H6 依赖）；`children_reordered` 只记父物体，所以对已归位对象做 set_sibling_index 无法被发现（提示仍禁止）。
- `GetAllSceneObjects` 的遍历序：keyed_by_name negative 已改为"按名字去重只动一个"，与遍历序无关。
- 154 个对象的 snapshot 约 310 次读取（get_info + Transform 序列化），实机耗时未知。

### 预估
顶级模型 25–40 分钟；通过率估计 25–50%。主要丢分：顺序（H5）、同名对象、缩放声明。

## 共享文件
- 没有改 manifest（等 lead 指示谁来重建）；lint/check/dry-run 都在 /tmp 的临时包副本上通过（0 错误 0 警告）。
- `python3 -B -m unittest discover eval/tests`（3.13）：1223 个用例，1 个失败 `pack rule 8: tasks/T5 is not listed in the manifest`——来自工作区里所有未进 manifest 的新题目录（含本组 OC5/OC7），manifest 重建后即消失。
