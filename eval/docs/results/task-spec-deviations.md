# P0 题目规格偏差与 S0–S2 证据

本文对照 [题目草案](../design/task-specs.md)、[14 题实现](../../tasks/)、[能力探针记录](../probes.md) 与归档的 Claude 会话进展整理。代码中确认的行为和历史 selftest 记录分开描述。历史 gate 不带当前 harness 源码哈希；它只能说明当时的单题矩阵结果，不能替代冻结后完整 selftest，也不能证明模型校准已完成。

## 题目与草案的差异

当前题目源码中每题权重总和均为 10，14 题均有至少 5 个负例和至少 1 个替代解。下表列出实现断言数与草案目标；断言合并属于覆盖范围/可诊断性变窄，不能按原草案断言数宣称等价覆盖。

| 题目 | 实际断言数 | 草案断言数 | 差异与已采用的降级 |
|---|---:|---:|---|
| S01 | 9 | — | 基础材质与对象创建题；本轮未发现与题目草案对照的断言数差异。历史 gate 记为 13/13 cases，但需按下文的源码哈希限制解读。 |
| S03 | 8 | 8 | Fixture 中 Lamp 使用 `AudioSource` 和禁用的 `BoxCollider`，不再放 `Light`。历史会话说明 URP 会给 Light 自动添加额外组件，使组件状态断言不稳定；实现仍检查 reparent、本地 TRS、子物体、组件和层级副作用。见 [s03.py](../../tasks/s03.py)。 |
| S04 | 6 | 6 | 未发现计划外降级；保留同名 sibling、AudioSource 选择和唯一目标断言。 |
| S06 | 6 | 6 | `in_memory` 负例改为 `flat_name`：当前资产创建技能必须提供保存路径，无法构造无保存路径材质；替代解还增加了 `duplicate_base`。见 [s06.py](../../tasks/s06.py)。 |
| S08 | 6 | 6 | E4 总数权重从草案的 0.5 调整为 1.0，以使总权重达到 10；其余仍测大小写、inactive、对象自身 collider、路径和只读性。见 [s08.py](../../tasks/s08.py)。 |
| D01 | 6 | 6 | 规格保持 63 个匹配项、跨页、inactive 和同名对象；未发现计划外降级。 |
| T1 | 13 | 18 | 将草案中的细项合并为较少断言；保留脚本内容/编译、SO 字段和 prefab GUID、目标组件与引用、无额外脚本及副作用检查。见 [t1.py](../../tasks/t1.py)。 |
| T2 | 13 | 20 | 将按钮/RectTransform/EventSystem/持久化调用细项合并；保留 listener 顺序、目标、方法、参数和运行状态的读回。见 [t2.py](../../tasks/t2.py)。 |
| T3 | 11 | 20 | 断言项合并。`m_ConnectedBody` 不在序列化属性读取结果中，因此验证器在完成其余检查和日志收集后，为候选对象临时改成唯一名称，再用 `component_get_properties.connectedBody` 读回名字；层碰撞对通过物理读取核验，因为 TagManager/DynamicsManager 修改不进入变更日志。teardown 独立恢复碰撞对与 layer slot 并读回确认。见 [probes.md](../probes.md)、[t3.py](../../tasks/t3.py)。 |
| T4 | 11 | 17 | 断言项合并。URP 会按 `giFlags` 重新派生 `_EMISSION`，所以“关闭 keyword”负例改成设 `giFlags=EmissiveIsBlack`；无保存路径的内存材质方案不可用，负例改为把 Glow 保存到错误路径。见 [probes.md](../probes.md)、[t4.py](../../tasks/t4.py)。 |
| T5 | 9 | 16 | 断言项合并。目标 crate 只随机放在 sibling index 1 或 2，且带 mass override 的 crate 不会是目标，保证“新实例 append 到末尾”负例稳定失败。Prefab 资产编辑的 scene-less journal 记录没有 assetPath，允许 `Crate`/`Crate_Heavy` 根路径记录；基础 prefab 是否被改仍由文件 hash 和 GUID 检查。YAML 的跨行引用由题目内 `_ref()` 同时处理字符串和字典。见 [t5.py](../../tasks/t5.py)。 |
| T7 | 11 | 15 | 断言项合并。草案约 120 个对象，实际实现为 113 个对象，其中 73 个目标仍满足指定 pattern；层级包含 68 个普通/失活目标、两个重复 `Crate_B07` 和三个 Stack 目标。见 [t7.py](../../tasks/t7.py)。 |
| T9 | 10 | 16 | 断言项合并；实现仍单独检查 FormerlySerializedAs、编译、旧值保留、禁止通过技能重设字段、脚本/场景未受影响。见 [t9.py](../../tasks/t9.py)。 |
| T10 | 9 | 15 | 断言项合并；实现仍读取磁盘 meta GUID、目标映射顺序、renderer slot 引用、非材质和目录清理。见 [t10.py](../../tasks/t10.py)。 |

能力降级由 [probes.md](../probes.md) 中的探针结果支持，特别是 `m_ConnectedBody` 的可见性、URP emission 行为、prefab override 计数和 `gameobject_duplicate` 断链、`physics_create_material` 对缺失目录的失败，以及检索技能上限/大小写行为。以上是实现与探针层面的结论，不代表这些题已通过冻结后全量 selftest。

## Selftest 证据边界

[历史 gate](../../runs/selftest/gate.json) 记录 14 题均 green，但 README 明确要求旧 gate 必须有匹配的 source hash 才能授权后续模型运行。归档会话最后的状态是：单题工作者曾各自报告绿色结果，但它们运行时的 harness 版本不完全相同；最终 harness 的完整 14 题 selftest 正在运行时，Claude 调用遇到平台 503，随后用户中断。后续 [恢复记录](../../runs/resumption-20261011.json) 也明确写着此前完整 selftest 在 T4 首个 reference 后中断，并要求在 harness/Codex adapter 修复后完整重跑，目标为 294 次。

可追溯的历史单题记录包括 [T3](../../runs/selftest/20261010T002255.json)、[T4](../../runs/selftest/20261010T002518.json)、[T5](../../runs/selftest/20261010T002731.json)、[T7](../../runs/selftest/20261009T234959.json) 和 [T10](../../runs/selftest/20261009T233735.json)。这些文件是当时的单题矩阵，不是当前冻结源代码的完整 selftest 证明。本报告不据此声称 14 题最终全绿，也不声称已有任何模型的校准实跑通过。

## S0–S2 验收证据

| 阶段 | 已有证据 | 状态与限制 |
|---|---|---|
| S0 校准工程 / SDK | [setup-20261011.json](../../runs/setup-20261011.json)：registry 实例 `UnitySkillsEval2022_B95ADA34`、8091、Unity 2022.3.62f2、SDK 2.9.0、git source/hash、URP、pipeline/default shader、mode 与健康字段；`problems` 为空。冻结文档 hash 为 `5bbb9d…d040f5`，246 个文件；baseline cleanliness 为 true。 | 校准工程和安装身份有直接记录。该 setup 快照当时 active scene 是评测 run scene；[recovery-20261011.json](../../runs/recovery-20261011.json) 记录之后切回 `Assets/Scenes/SampleScene.unity` 并删除了中断 run folder。后续已从原会话提取成功清理与保存、读回记录，见 [historical-8090-cleanup.json](../../runs/historical-8090-cleanup.json)；属于历史证据，未对当前 8090 的 TestSkills 工程执行清理。 |
| S1 能力探针 | [probes.md](../probes.md) 汇总 Unity/URP 版本、场景和日志、编译重载、序列化读取、材质、prefab、UI、检索、模型身份等探针；原始 JSON 位于 [runs/probes](../../runs/probes/)。 | 有文档化结论及降级方案；报告中的探针环境是 UnitySkillsEval2022 / 2022.3.62f2 + URP 14.0.12。 |
| S2 框架 / 单测 / setup-check | [harness](../../harness/)、[tasks](../../tasks/)、[tests](../../tests/) 与 [CLI](../../cli.py) 已存在。最近一次离线验证命令 `python3 -m unittest discover eval/tests` 的结果为 **65 tests OK，exit 0**（当前会话记录）。setup 快照 `problems: []` 见 [setup-20261011.json](../../runs/setup-20261011.json)。 | 离线测试和 setup 记录可确认。setup 产物已补入本轮实际工具结果对应的命令与退出码 0。 |

本轮另有 [Codex provider preflight](../../runs/preflight/codex-20261011T043747Z-summary.json)：Astra、6.1 Sol、5.6 Terra（high）和 6 Luna（max）均 `available: true`、return code 0、无 `evidenceProblems` 或 host errors。它验证 provider 可启动和 model identity/token 采集，不是 Unity 任务实跑；费用字段为 null，不能据此估算美元成本。

## 已知限制

- 本文只新增文档，不修改题目、harness、CLI 或测试，也没有运行 Unity。
- 本轮 setup/probe/selftest 证据不等同于 S3–S5 的最终验收。冻结 harness 后应重新运行完整 selftest；根据 [恢复记录](../../runs/resumption-20261011.json)，其目标是覆盖 14 题及 7 个 host/model/effort 配置的全套 294 次 selftest attempt。
- 当前材料没有最终的 `p0-calibration.md`，也没有足以据此报告 42/126 次模型实跑结果的证据。

## Recovered S0 evidence

The resumed Claude transcript contains the original successful cleanup of project `skill` on
port 8090: deleted `EV_S01`, deleted `Assets/__UnitySkillsEval`, saved
`Assets/Scenes/SampleScene.unity`, read back zero matching objects and `isDirty=false` with only
Main Camera and Directional Light. The extracted historical tool command/result is saved in
`../runs/historical-8090-cleanup.json`. This is historical evidence, not a new action against
current port 8090 (which now belongs to a different project, TestSkills).
