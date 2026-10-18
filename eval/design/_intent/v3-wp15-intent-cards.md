# WP15 意图卡（lead 起草，起草模型据此展开）

总则（每张卡都适用）
- 格式与约定同 v1.1 基础题（wp11）与 WP14 移植题：双语逐条编号 R；断言 → reqIds；权重和 10；critical 只落在 G1/G2/G6 或"不得"类需求；≥5 negatives（每个恰好失败声明的断言）、≥1 alternative（含 save_run_scene）；handles/C0 不含角色标签与答案；id 一律经 verifylib object_id/address（6000.4+ entityId 兼容）。
- 隐性规则为零：每条断言都能在两种语言提示里找到明确依据；lint_pack 0 错误。
- 种子：build 用 ctx.rng 决定数值，且**必须能按种子重排结构与命名**（3.5 保留种子 = 同语义、结构与命名重排的变体），verify 只从 C0/handles/终态推导期望。
- 难度目标（顶级配置 k=3）：basic ≥85% 且每题每配置 ≥2/3；standard 45–90%；challenge 20–70%。
- 环境：ops 题不写 C#；dev 题写 C#，编译后用私有模块 SCENARIO（runner 在提交时调 editor_run_scenario）做运行时验收；NUnit 不得编译脚本或进 Play。standard/challenge 一律 requires {pipeline:"URP", dedicated:true}；basic 支持 ["URP","BuiltIn"]，不支持的写明理由。
- ID：ops OS1–OS2（standard）、OC1–OC8（challenge）；dev DB1–DB6（basic）、DS1–DS3（standard）、DC1–DC8（challenge）；保留题 OBR1 OSR1 OCR1 DBR1 DSR1 DCR1（只放本地，不进 git、不上 GitHub）。

## ops / standard（2 道新题）
| ID | 题目 | 技能面 | 搭建 | 需求要点 | 陷阱 | 验收 |
|---|---|---|---|---|---|---|
| OS1 | 三点布光 | light_*、gameobject_set_transform、look-at 计算 | 场景中一个 Subject（随机位置/朝向），无灯或有一盏需替换的旧灯 | Key/Fill/Rim 三盏灯：类型、颜色、强度比、阴影类型；位置按 Subject 的局部方位角/仰角/距离给出；全部朝向 Subject；旧灯停用而非删除 | 局部方位按 Subject 朝向而非世界轴；Spot 需 spotAngle；"朝向"容差 | 读灯组件属性 + 世界 TRS，容差判定 |
| OS2 | UI 菜单搭建 | ui_*、RectTransform、LayoutGroup | 空 Canvas（Screen Space Overlay，参考分辨率随种子） | 面板居中固定尺寸；3 个按钮在 VerticalLayoutGroup 中、间距与 padding；按钮文字（双语提示给定，文本用 legacy Text 或 TMP 皆可但需写明）；第三个按钮初始不可交互 | anchoredPosition 与 localPosition；LayoutGroup 控制尺寸时 sizeDelta 被覆盖 | 读 RectTransform/LayoutGroup/Button 属性与兄弟序 |

## ops / challenge（8 道，见 v3-architect-part8.md 3.6 的 O1–O8）
OC1=O1 嵌套 prefab 枪口；OC2=O2 变体覆盖清理；OC3=O3 掉落表批量接线；OC4=O4 渲染器审计；OC5=O5 UI prefab 规格；OC6=O6 提取 prefab 保持入向引用；OC7=O7 大层级重构；OC8=O8 层与碰撞矩阵（仅专用工程，teardown 恢复设置，同 T3）。要求、陷阱、验收按该文件；补充：
- 每题都要有"看似完成但错"的 negative（例：OC1 在场景实例上加 Muzzle 而非 prefab 资产；OC2 从场景实例 apply 污染基础 prefab；OC3 按名字而非 GUID 选 SO；OC4 只处理激活分支；OC7 worldPositionStays 用错）。
- OC4 的 240 renderer 用 batch 读取会触发截断：需求里写明"必须覆盖全部"，验收不依赖 Agent 报告，报告计数只作次要断言。

## dev / basic（6 道新题：写脚本 → 编译 → 挂载 → SCENARIO 断言）
| ID | 题目 | 需求要点 | 运行时验收 |
|---|---|---|---|
| DB1 | 匀速旋转 | `Spinner` MonoBehaviour，序列化字段 degreesPerSecond（默认值写明），挂到指定对象并设为给定值；绕给定轴（本地/世界写明） | 进 Play 后等 N 秒，角度变化在容差内；timeScale 改变时按比例 |
| DB2 | 生命值与死亡事件 | `Health`：maxHealth、TakeDamage(int) 夹到 0、死亡事件只触发一次、Heal 不超过上限 | component_invoke_method 调用序列，断言返回值/状态/事件计数 |
| DB3 | 闪烁灯 | 按周期切换 Light.enabled，周期序列化，可在 Inspector 设置 | 采样多个时间点的 enabled |
| DB4 | 跟随相机 | LateUpdate 中跟随目标 + 偏移，可选平滑（关闭时精确） | 移动目标若干帧后断言相机位置 |
| DB5 | 点击计数 | 按钮点击使标签数字递增；用持久化 onClick（event_add_listener）接线 | onClick.Invoke 两次，断言文本 |
| DB6 | 定时生成 | 每 interval 生成 prefab 实例到容器下，达到上限停止 | 等待后统计容器子物体数与时间点 |
共同陷阱：编译等待（waitUrl）；类名与文件名一致；挂载到正确对象；序列化字段值在场景中而非仅代码默认值；保存场景（G2 允许）。basic 支持 URP 与 BuiltIn。

## dev / standard（3 道新题）
| ID | 题目 | 需求要点 | 陷阱 | 运行时验收 |
|---|---|---|---|---|
| DS1 | 巡逻路径 | 在子物体路点间匀速移动、到点停顿、循环；速度与停顿可配置 | 路点用世界坐标；停顿用 deltaTime 累计而非协程泄漏 | 多时间点位置断言（容差窗口） |
| DS2 | 触发门 | 带 Rigidbody 的运动学玩家进入触发区时门平移打开、离开后关闭；只响应 Player 标签 | 触发需一侧有 Rigidbody；标签比较；重复进入 | 驱动玩家走固定路径，断言门位置与事件次数 |
| DS3 | 可配置波次 | ScriptableObject `WaveConfig`（数量、间隔）+ 两个资产 + Spawner 引用其一；切换引用后行为改变 | 引用资产而非运行时实例；SO 字段默认值 | 两种配置下各跑一次，断言生成数与时间 |

## dev / challenge（8 道，取 v3-architect-part8.md 3.6 的 D1–D5、D7–D9；D6 对象池并入保留题候选）
DC1=D1 背包堆叠修 bug；DC2=D2 门状态机；DC3=D3 安全重命名并移动；DC4=D4 弹道发射器；DC5=D5 暂停菜单（持久 onClick 用 targetInstanceId/targetEntityId）；DC6=D7 金币触发计分；DC7=D8 asmdef 修复（需求写明是否允许 `#if`）；DC8=D9 SO 难度配置。要求、陷阱、验收按该文件。

## 保留题
（本节在复制时省略：保留题的构想只留在本地，不外传。）

## 流程（3.4，按题一单元）
1. 起草：A 厂商（Claude fable 或 opus，max）按卡写 public/private/solutions + 陷阱说明；半数题互换（B 厂商起草、A 厂商评审）：OS2、OC2、OC4、OC6、OC8、DB2、DB4、DB6、DS2、DC2、DC4、DC6、DC8 由 Codex（gpt-6.1-sol，max）起草。
2. 离线 lint + 离线测试。
3. 实机门禁（2022.3 URP 专用工程；basic 另跑 BuiltIn）：reference×2 fingerprint 一致、negative 精确失败、alternative 通过。
4. 另一厂商只读 public：列歧义 → 逐题模式盲解 1 次（真 runner）。
5. 另一厂商读 private：评审断言映射与误伤，提新 negative/alternative。
6. 分歧用 Unity 实跑裁决并固化为 selftest 用例，改动升 caseVersion（新题从 1 起）。
7. 试校准（第 9 步）等用户指令；冻结与私有文件上 GitHub 在发布流程中做。

## 2026-10-17 更新：出题模型改为三家（用户决定）
- 只用 Claude Opus 5.5、GPT-6.1 Sol、Gemini 4 Argon；不再用 Fable、GPT-6 Astra。已有 Fable 草稿（OS1、OC1/3/5/7、DB1/3/5、DS1/3）保留，由另两家审阅、修订；未完成的 Fable 草稿 DC1、DC5、DC7 由 Opus 5.5 补完。
- 调用：Opus 5.5 = Claude Code 子代理（model opus）；Sol = `codex exec --model gpt-6.1-sol -c model_reasoning_effort="max"`（mirasim 中转）；Argon = `claude -p --model gemini-4-argon`（Claude Code 宿主）。
- 起草分配：Opus 5.5 → DC1（补完）、DC3、DC5（修订）、DC7（补完）、6 道保留题（仅本地）；Sol → OC2、OC4（已起草）、OC6、OC8、OS2、DB4、DC2、DC6；Argon → DB2、DB6、DS2、DC4、DC8。
- 审阅（读 private，查断言映射与误伤、补负例/备选）：Fable 的 ops 草稿 → Sol；Fable 的 dev 草稿 → Argon；Opus 的 → Sol；Sol 的 → Argon；Argon 的 → Opus。
- 每题先过离线 lint/测试，再过 Linux 实机门禁（6000 URP 8092、2022 URP 8095；basic 另在 Built-in 8090/8094），最后才进入审阅。盲解（另一家只读 public、逐题跑真 runner）放在全部题目门禁通过之后统一安排。
