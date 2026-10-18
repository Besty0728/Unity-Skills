part 8/11 — 3.4 出题流程、3.5 保留题、3.6 挑战题构想（ops 8 + dev 9）

### 3.4 跨厂商出题流程与验收标准

1. 意图卡（lead）：写清赛道、档位、要考的技能面、目标难度。
2. 起草（A 厂商顶级模型，max effort，例如 Claude Fable 5.1 或 Opus 5.5）：产出 public、private、solutions 和陷阱说明。
3. 离线 lint。
4. 在真实 Unity 上跑 selftest 门禁：reference 跑 2 次且 fingerprint 一致；每个 negative 恰好在声明的断言上失败；每个 alternative 通过。
5. B 厂商（GPT Sol 6.1 或 Astra 6，max；通过 codex 插件或 `codex exec`）只看 public 部分：列出歧义，并用 runner 逐题模式盲解。
6. B 厂商再读 private 部分评审断言：是否都有需求映射，有没有误伤合法解法；提出新的 alternative 或 negative。
7. 分歧一律用 Unity 实跑裁决，并固化成新的 selftest 用例，不靠意见；任何改动都升 caseVersion。
8. 半数题目互换起草和评审的角色。
9. 试校准：只用出题模型（两家各取顶级配置，逐题模式，k=3）。验收区间：
   - basic：合计通过率 ≥85%，且每题每配置至少 2/3 通过。
   - standard：45–90%。
   - challenge：20–70%。
   - 高于上限就加强（新增需求与断言的映射）后重跑门禁；低于下限先查 B 盲解证据里有没有歧义。
   - invalid 必须为 0，不能有未决争议。
10. 冻结：写入 manifest 的 hash，私有文件上 GitHub，保留变体只放本地。

### 3.5 保留题策略

- 每个档位保留 ≥1 道完整新题（从不发布）；所有公开题另有保留种子（结构和命名重排，语义相同）。
- 官方横向比较 = 批量模式 + `--pack-dir` 指向本地保留包，只发布汇总结果。
- 题库版本退役时，可以把保留题转为公开，再补新的保留题。

### 3.6 挑战题构想

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
