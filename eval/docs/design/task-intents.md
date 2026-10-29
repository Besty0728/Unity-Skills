# v3 任务包意图卡与挑战题构想

> 来源：`eval/design/_intent/v3-wp15-intent-cards.md`（总则与各档任务表）与 `eval/design/_intent/v3-architect-part8.md`（3.6 挑战题构想）。两文件已在本次整理中删除，可从 git 历史恢复。

## 总则（每张卡都适用）
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

## ops / challenge（8 道，见本文「挑战题构想」的 O1–O8）
OC1=O1 嵌套 prefab 枪口；OC2=O2 变体覆盖清理；OC3=O3 掉落表批量接线；OC4=O4 渲染器审计；OC5=O5 UI prefab 规格；OC6=O6 提取 prefab 保持入向引用；OC7=O7 大层级重构；OC8=O8 层与碰撞矩阵（仅专用工程，teardown 恢复设置，同 T3）。要求、陷阱、验收按「挑战题构想」；补充：
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

## dev / challenge（8 道，取本文「挑战题构想」的 D1–D5、D7–D9；D6 对象池并入保留题候选）
DC1=D1 背包堆叠修 bug；DC2=D2 门状态机；DC3=D3 安全重命名并移动；DC4=D4 弹道发射器；DC5=D5 暂停菜单（持久 onClick 用 targetInstanceId/targetEntityId）；DC6=D7 金币触发计分；DC7=D8 asmdef 修复（需求写明是否允许 `#if`）；DC8=D9 SO 难度配置。要求、陷阱、验收按「挑战题构想」。

## 挑战题构想（原 3.6）

**ops（8 道）**

| 编号 | 题目 | 要求 | 主要陷阱 | 验收方式 |
|---|---|---|---|---|
| O1 | 嵌套 prefab 枪口 | 只在 `Turret.prefab` 内的嵌套 Barrel 实例上添加 Muzzle（给定局部位姿）；`Barrel.prefab` 和 `Tank.prefab` 不变；场景中 3 个实例可见，其中一个实例已有的覆盖要保留 | 以根为基准的相对路径；在资产层面做覆盖 | YAML、sha256、修复后的 `prefab_get_overrides` 明细 |
| O2 | 变体覆盖清理 | 基础 prefab 改速度：Fast 变体保留 8，Tank 变体继承；撤销 Tank 误加的 scale 覆盖；组件只加在基础 prefab 上 | 从场景实例 apply 会污染基础 prefab | 逐变体读取加载后的值和覆盖列表 |
| O3 | 掉落表批量接线 | 36 个嵌套文件夹中的敌人 prefab：`Loot.table` 按"文件夹 → 等级"规则指向 SO | 两个文件夹里有同名 `LootTable_Common`（按 GUID 区分）；某个 prefab 有两个 Loot（componentIndex）；变体只改基础 | `prefab_set_serialized_property_batch` 写入的 GUID 引用 |
| O4 | 渲染器审计 | 240 个 renderer（含未激活分支）：使用 Standard shader 的槽位和"引用丢失"的槽位替换为 Fallback；本来为空的槽位保持为空；报告分类计数 | 利用 P3/P11 的截断标志和 P7 的 referenceState；`material_assign` 只能写槽位 0 | 逐 renderer 读材质数组 |
| O5 | UI prefab 规格 | `HUD.prefab`（嵌套 Canvas）：6 个 RectTransform 的 anchor/pivot/sizeDelta/anchoredPosition，兄弟顺序，启用一个未激活元素，LayoutGroup 间距 | 区分 anchoredPosition 与 localPosition | 在容差内比对 prefab 数值；场景实例不变 |
| O6 | 提取为 prefab 并保持入向引用 | `Gate`（含子物体 Hinge）做成 prefab，替换 4 个场景副本，保持世界位姿和兄弟序号；3 处 `Lever.target` 重新指向实例的 Hinge 子物体 | 删除原物体后引用悬空；指向根还是子物体 | 引用所属对象的路径 |
| O7 | 大层级重构 | 150 个物体按"标签 + 后缀"归入容器；容器有旋转和非均匀缩放；保持世界 TRS（不可表示的情形提前声明）；按自然序排序；6 个已正确的物体不动 | worldPositionStays 默认值；斜切 | 世界 TRS、顺序、journal 写入最少 |
| O8 | 层与碰撞矩阵 | 在空闲槽位加 2 个层，设置 3 对碰撞，按规则递归设置层并排除带 `KeepLayer` 的子树 | 只能改声明过的项 | 设置文件 diff 只出现声明的 key（teardown 同 T3）；仅限专用工程 |

**dev（9 道）**

| 编号 | 题目 | 要求 | 主要陷阱 | 验收方式 |
|---|---|---|---|---|
| D1 | 背包堆叠修 bug | 合并溢出、移除不触发事件、接受负数；需求 R1–R6 | 改了公共签名；只修示例 | 隐藏场景通过 `component_invoke_method` 断言返回值、状态和事件计数 |
| D2 | 门状态机 | Closed/Opening(0.5s)/Open/Closing，可中断，进度保留 | deltaTime 与 timeScale；协程泄漏 | `wait_seconds`/`wait_condition` 断言 `openness`，带容差窗口 |
| D3 | 安全重命名并移动 | 类改为 `Game.AI.Patroller` 并移动文件，重命名两个字段；6 个实例（含一个变体）的值和引用保留；行为不变 | GUID 保持（move，而不是删后重建）；`FormerlySerializedAs` | 序列化值 + 场景断言 |
| D4 | 弹道发射器 | `Fire(target)` 让弹丸在 3 个隐藏目标（高度不同）的 0.25m 内落地 | drag、ForceMode、固定步长 | 位置或碰撞条件 |
| D5 | 暂停菜单 | 用技能建按钮 + `PauseController`；持久化 onClick 用 P5 新增的 `targetInstanceId` 指向控制器 | 非持久监听；同名控制器 | `onClick.Invoke` 两次，断言 timeScale 和面板状态 |
| D6 | 对象池 | 用 `prefab_set_serialized_property` 配置 `EnemyPool`；预热后 Get/Release 复用；Get 时重置状态 | 用 Destroy 代替停用；Awake 与 Start 的顺序 | 3 轮循环中物体数量恒定 |
| D7 | 金币触发计分 | ScoreManager + Coin 触发器 | 触发需要一侧有 Rigidbody；一个层对被禁用；双碰撞体重复计数 | 运动学玩家走固定路径后断言分数 |
| D8 | asmdef 修复 | 补引用，把编辑器代码移入 Editor asmdef；不得删除 asmdef | 用 `#if` 绕过要在需求里明确是否允许 | 编译通过后跑场景断言 |
| D9 | SO 难度配置 | SO 类型 + 3 个资产，Spawner 读取 | 引用的是资产还是运行时实例 | 场景切换配置后断言 N 秒内的生成数 |
