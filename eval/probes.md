# S1 能力探针结论

环境：UnitySkillsEval2022（2022.3.62f2 + URP 14.0.12），插件 v2.9.0 = `58d3339d`，端口 8091。复跑命令：`python3 eval/cli.py probe`，原始证据保存在 `eval/runs/probes/*.json`（只存本地）。

| 待确认项 | 结论 | 对题目的影响 / 降级方案 |
|---|---|---|
| 本轮专属场景的生命周期 | `scene_create` 建出场景并设为活动场景；编辑后 isDirty 变为 true，`scene_save` 后清零；`scene_load` 切回 SampleScene 再删目录，清理干净 | fixture 一律在 `Assets/__UnitySkillsEval/<runId>/Eval_<runId>.unity` 里建，用户场景不会被保存 |
| 变更日志记录的形态 | 观察到 `gameobject_created`、`components_changed`、`property_changed`（含 previousValue 和 value）、`gameobject_reparented`（用 fromPath/toPath，没有 path 字段）、`imported`、`moved`（含 from/to）、`scene_saved` | 越界判定已按 fromPath/toPath 修正；副作用检查以日志为主 |
| 跨编译和域重载 | 场景对象的 instanceId 不变；日志连续，没有 dropped 或 truncated；作业完成并等待空闲后，新类型可以直接 `component_add` | T1/T9 可以按 fixture 时记下的 instanceId 验收 |
| 序列化读取能否读到对象引用 | MeshRenderer 槽位读回 `id:UnityEngine.Material:name`，MeshFilter 读回 `id:UnityEngine.Mesh:Cube`；**HingeJoint 的 `m_ConnectedBody` 不在可见属性里，读不到**（`ListProperties` 用的是 `NextVisible`） | T3 木板改用唯一名字，关节目标用 `component_get_properties.connectedBody` 按名字和唯一性验收；这个读取缺口记为 P9 |
| Cube 身份 | `model_get_mesh_info` 返回 name=Cube、24 个顶点、12 个三角面 | S01 等题用它验证 Cube |
| URP 材质 | `material_create` 默认 shader 是 `Universal Render Pipeline/Lit`；颜色属性 `_BaseColor`；`material_set_emission` 会打开 `_EMISSION`；**`_Color` 是 URP/Lit 的隐藏兼容属性，写它不报错，但渲染用的是 `_BaseColor`** | 材质题按 `_BaseColor` 验收，误写 `_Color` 会判失败，是公平陷阱 |
| 材质路径 | `material_get_properties{instanceId}` 返回槽 0 的 materialPath；其余槽只有 id 和名字 | 多槽题用 `scene_materials` 的 users，加上名字在项目内唯一，作为降级；严格身份留给 P1 的 C# 验收器 |
| 预制体 | `prefab_get_overrides` 只给计数，排除 `m_Name` 和与源相同的虚项（实例位置偏移也算覆盖）；变体的依赖树包含基础 prefab；`batch_query_gameobjects{prefabSource}` 可用；**`gameobject_duplicate` 出来的副本不再是预制体实例** | T5 的覆盖检查改为解析磁盘上 prefab 的 YAML（`eval/harness/uyaml.py`）；duplicate 断链作为 T5 陷阱 |
| SO 读取器 | 能读 .physicMaterial（摩擦、弹性）和 `ProjectSettings/TagManager.asset`（layers 数组）；对 prefab 只能读主 GameObject | 物理材质数值可以验收 |
| 加层 | `scriptableobject_set_serialized_property{ProjectSettings/TagManager.asset, layers.Array.data[i]}` 能写入自定义层，写空字符串可以还原 | T3 由控制器挑一个空闲层槽写入，运行结束后还原 |
| `physics_create_material` | 目标目录不存在时报 `INTERNAL Invalid path`（`GenerateUniqueAssetPath` 在建目录之前调用）；同名会自动改名为 "Rubber 1" | 插件缺陷 P8；题目预先建好目录，避免触发 |
| UGUI 与 TMP | `ui_create_button` 的文字是 TextMeshProUGUI；没有导入 TMP Essentials 时也不弹阻塞窗口，编辑器保持响应；`m_OnClick.m_PersistentCalls` 能完整读出：`m_Target`（id:GameObject:name）、`m_MethodName`、`m_Mode`（6 = Bool）、`m_BoolArgument`、`m_CallState`（2 = RuntimeOnly）；`event_set_listener` 能按 targetInstanceId 改绑（先 `event_add_listener` 再改）；`mode` 参数表示调用状态，参数类型由 `argType` 指定 | T2 可做 |
| 后台编译 | 前台是 Finder 时，评测 Editor 在 7 秒内完成脚本编译，`script_get_info.className` 能读到 | 不常驻抢焦点；只有编译或导入连续忙 20 秒以上时才按 pid 切到前台（`harness/focus.py`） |
| 检索上限 | `gameobject_find` 用 limit 50 时 count=50，**静默截断**；正则用 limit 500 时能拿到全部 60 个；`batch_query_gameobjects` 的 namePattern 不区分大小写 | D01/T7 的陷阱成立 |
| 模型路由 | 请求 `claude-opus-5-5[1M]` / `claude-sonnet-5-5[1M]` / `claude-fable-5-1[1M]` 时，init 事件分别报告对应的 `…[1m]`，每条消息报告不带后缀的基础名，`costBasis=list` | 模型比对按"init 等于请求、消息等于基础名"判断；最小请求的花费分别约为 $0.011 / $0.006 / $0.065 |
