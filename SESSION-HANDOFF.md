# 当前接力状态（2026-10-19，UTC）

这是当前状态的优先说明；下方较早内容是历史会话材料，若有冲突以本节为准。

## 从哪里继续

- 仓库：`/Users/betsy/CodeSpace/Unity-Skills`，分支 `beta`（开始前 `git pull --ff-only origin beta`）；`main` 只在正式发布流程中更新。
- 测试工程（包引用均已改指本仓库 `SkillsForUnity`）：`~/UnityProject/TestSkills`（6000.3.9f1，Built-in）、`~/UnityProject/skill`（2022.3.62f2，Built-in）、`~/UnityProject/UnitySkillsEval2022`（2022.3.62f2，URP，已标记 dedicated）。一次只开一个编辑器；Unity CLI 在 `~/.unity/bin/unity`。

## 已完成（2026-10-18 至 19）

- 修复粒子 P1/P2 合并遗留：`validateAll` 调用位置（原 CS0103 编译失败）、新建系统自带的 (Birth, None) 子发射器槽、固定步长分片模拟多走步数、batch/SKILL.md 字节预算；技能总数同步为 870；两个可疑 meta GUID 换成 uuid4。
- EditMode 全绿：6000.3.9f1 2356/2264/0/92，2022.3.62f2 2357/2257/0/100（包更新后首次启动会读到旧 locale 缓存，导致 4 个本地化测试失败，重跑即过；属已知小问题，见下）。
- DC4 重做为实机可评分：fixture 生成传感器脚本挂在靶台上（逐物理帧记录最近距离、刚体数、改动与离枪口计数），靶台去掉碰撞体；fake 与测试按真实 API 重写；删除 DS2 私有评分里的死代码。DC4 门禁在 2022.3.62f2 URP 上 GREEN 22/22，参考解实机命中距离约 1e-6 m。
- Python 测试：3.13 与 3.9 各 1665 个全过；lint / check / manifest 通过（37 题）。

## 下一阶段（等用户开启）

1. 门禁矩阵：本机此前没有任何门禁记录（`eval/runs/` 不入库）。基础题需要 2022 与 6000 × URP 与 Built-in，标准题和挑战题需要 URP；缺一个 6000 URP 的 dedicated 工程，需要新建。
2. 用 `suite3`（`claude -p`，便宜模型）对一两道题做开发期冒烟，不计入正式成绩；正式真实模型评测仍等用户指令。
3. 已知小问题：插件包更新后的第一次 domain reload 会把旧版 locale 资源读进 `SkillsLocalization` 缓存，新加的文案键要到下一次 reload 才生效。

---

# UnitySkills v3.0 任务交接文档

> 本文档由旧会话记录提炼而成，供新会话读取后继续工作。
> 旧会话 ID：19288e60-1fd9-45a2-8da7-9a5dd2372b2e（项目目录 -Users-betsy-CodeSpace-Unity-Skills）。
> 会话因原运行环境不可用而中断，中断时全体子任务成员处于"等待调度"状态，需要重新分派。

## 1. 用户全局规则（必须遵守）

- 始终用中文回复；用户名字是 Betsy。
- 任务分级：一档直接做 / 二档说一声再做 / 三档完整流程（PlanMode，声明"判定为三档，理由是…"）。
- 三档任务用 AgentTeam 模式：lead 只做澄清、拆分、分派、评审、纠错、汇报；成员通过 Agent 工具带 `name` 启动；探索用轻量模型，实现/文档用中档，架构/跨模块用高档。
- 未经用户同意不写记忆；不臆造 API（查源码/文档/问用户）；可维护性优先。
- 决策点用 ask-user-question 方式向用户提问抉择。

## 2. 任务总目标

UnitySkills 项目（Unity Editor 的 AI 技能插件，主仓库 `/Users/betsy/CodeSpace/Unity-Skills`，工作分支 `beta`）正在做两件事：

1. **v2.9.1 发布**（据会话记录应已完成）：预制体 skill、赞助商名单新增 Mirasim（官网 mirasim.ai，为 v3.0 提供大量 API 额度支持）、赞助商申请方式改为用户邮箱、版本号 2.9.1。
2. **v3.0 用户侧评测体系（测评集）**：用户在面板或 skill 里点"测评集测试"，AI 自动建测试环境、做题、按预留答案与评分标准打分。评分标准（私有 verify）放 GitHub 私有部分，不随包克隆，防作弊。**用户理想形态：复制提示词后在 agent 里正常运行即可**（不绑定特定命令行用法）。

## 3. 关键目录与资源

| 路径 | 用途 |
|---|---|
| `/Users/betsy/CodeSpace/Unity-Skills` | 主仓库（beta 分支） |
| `/Users/betsy/CodeSpace/Unity-Skills-v3` | v3 开发工作树（dev worktree） |
| `/Users/betsy/CodeSpace/Unity-Skills-v3-gate` | 冻结门禁工作树（提交 ce51f909，不可改） |
| `/Users/betsy/CodeSpace/Unity-Skills-v3-wp4b` | WP4b 专用工作树 |
| `SkillsForUnity/Evaluation~/usk_eval/` | v3 runner（纯标准库 Python，随 UPM 包发布） |
| `SkillsForUnity/Evaluation~/pack/` | 公开题包（manifest.json + tasks/） |
| `eval/pack/private/`、`eval/pack/solutions/` | 私有验收与解法（不上包） |
| `eval/design/v3-contracts.md` | v3 总契约文档（约 124KB，§0–§11+，唯一权威） |
| `eval/selftest3.py`、`eval/harness/solver_driver.py`、`eval/cli.py` | selftest/门禁/CLI（已冻结定稿，注意改动会使 gateInputsHash 变化） |
| `eval/suite3.py` | 批量模式（新，见 §6 WP16） |
| `eval/RESULTS_V1.md` | v1 评测成绩汇总（已发布口径） |
| 编辑器 :8090 | Unity 6000.3.9f1，TestSkills 工程 |
| 编辑器 :8091 | Unity 2022.3.62f2，skill 工程（Built-in 管线） |
| 编辑器 :8092 | 挑战档实测用（注意该工程未标记 dedicated，见 §7） |

测试基线命令：`python3 -B -m unittest discover eval/tests`（3.13 与 3.9 都要跑；3.9 有 1 个已知 v1 tomllib 报错属预期）。禁止生成 `__pycache__`（一律 `-B`）。EditMode 基线：6000.3.9f1 为 1701 total/1623 pass/0 fail/78 skip；2022.3.62f2 为 1702/1616/0/86。

## 4. v3 架构要点（已定稿）

- runner 无常驻进程：`check/start/next/submit/status/abort/report/usage/cleanup` 短命令，状态写 `Library/UnitySkills/eval/`；journal 由插件轮转落盘（WP1 已做段轮转+退出冲刷），submit 时按 seq 连续性从磁盘读回。
- stdout 只输出单行 JSON 信封 `{"ok","command","data","problems","next"}`；退出码 0/2/3/4/5；面板模式加 `--result-file --log-file --launcher panel`（不写 stdout）。
- 题目三份拆分：public（随包）/ private verify（GitHub）/ solutions。
- 问题码体系见契约 §3.1/§3.2；新增过 `PRIVATE_PACK_TLS`（退出码 2，本地化键 `tab_eval_problem_private_pack_tls`，en/zh-CN/ru 已就位）。
- usage 读取（WP7 API：`capture_host`、`read_usage`、`detect_declared`、`parse_manual_args`、`manual_usage`、`unavailable_usage`）；重算 tokens=null 时保留旧记录并追加 errors（§11 #23）。
- `requires.pipeline` 支持单值/列表/null，多题取交集。
- 唯一 canonical id 规则：object_id 优先 → 能放进 C# int 的 instanceId → entityId（`verifylib.address` 为准，fixture.address 已委托）。

## 5. 已完成的用户决策

- v2.9.1 发布"直接允许"，随后**全量推进 3.0，按计划实现即可**；**完全做完后停下来，等用户指令再开启对应测试**（真实模型评测）。
- WP4b 的 D1–D6 已拍板：名称匹配改为真正层级顺序（改代码）；entityId/非0 instanceId 名不中即报错绝不回退；Tier1 200 技能必做 + Tier2 五处次级定位器；dryRun 做存在性检查；分页仅显式传参时回退唯一已声明数组，否则 pagingIgnored；locales 文案不改。
- OC3  Loot/LootTable 脚本由 fixture 编译期生成（沿用 v1 T1 先例），命名空间固定 `UnitySkillsEval.Tasks.OC3`。
- S04 的 LAYOUT、S08/D01 的配方属答案来源但保留公开（已定）。

## 6. 各工作包状态（中断时快照）

| 包 | 状态 |
|---|---|
| WP1 journal | ✅ 段轮转+生命周期冲刷完成 |
| WP2 router | ✅ P1/P2 修复完成（严格定位、分页键保留） |
| WP3 query | ✅ totalMatched/truncated、caseSensitive 完成 |
| WP4 defects | ✅ C# 已冻结（含 includeHidden 全部 reader、event 定位组） |
| WP4b entityId | 🔵 提交 1 已就绪于 wp4b 工作树（建议提交信息 `fix(finder): hierarchy-order lookups...`），提交 2–6 待做；注意与 WP4 的两处文件重叠安排 |
| WP4c reload (P15 空200) | 🔵 根因已实机确认（Mono HttpListener.Stop 对挂起连接冲刷默认 200 空体），修复已暂存编译通过，提交 4 待做（含字节码守卫等三项跟进）；注意 Cinemachine/ProBuilder/XRI/Netcode/Splines 离线编译覆盖缺口待决策 |
| WP6 runner | ✅ 核心+全部跟进完成（EDITOR_BUSY 等待、importedAtSubmit 标记、suite3 批量） |
| WP7 usage / WP8 pack / WP9 verify | ✅ 完成 |
| WP10 panel | ✅ 本地化键已就位 |
| WP11 tasks | ✅ 6 道基础题定稿；**冻结副本正式门禁 GREEN 83/83**（17 分 28 秒，gate3 全绿）；WP11 是 manifest 唯一集成者 |
| WP12 driver | ✅ selftest3/solver_driver 定稿；E2E 22/24（1 跳过 1 已知失败 domain_reload 场景）；曾误报门禁无效已撤回 |
| WP13 entry | ✅ unity_eval.py 引导 + evaluation skill |
| WP14 ops | 🟡 manifest 重建协调中断 |
| WP15 题目 | 🟡 OC1/OC3/OC5/OC7/OS1 已起草离线绿、待集成；OC2/OC4 由 Codex 作业产出待验收；DB1 实机 selftest 在 8092 跑到一半；DB3/DB5、DS 系列进行中 |
| WP16 批量 | ✅ suite3 批量模式 + build_private_zip.py + 发布增补草案 + eval/README v3 维护者文档 |

## 7. 中断时的未决事项（按优先级）

1. **manifest 重建与第三波提交**：WP11 是唯一集成者；WP16 改了 `eval/cli.py` 与 runner，**所有题的 gateInputsHash 已变，提交后门禁必须重跑**。注意此前教训：改指工作树后必须先强制重编译并确认 Editor.log 里的包路径，再跑门禁。
2. **WP15 包重导入边界**：插件单事件最多记 200 条路径（MaxChangesPerEvent），331 个导入会塌成 200+1 条 omitted 记录，导致 non_rest=1 误判。当时选了方案 (a) 暂时接受，并按内容 hash+mtime 双重校验防绕过；(b) 插件侧 importedTotals 增强留作 WP1 后续。
3. **WP4b 提交 1–6**、**P15 提交 4**（含离线编译覆盖缺口决策）。
4. **T1（dev/standard，URP，40 分钟）首次实机跑**待启动；URP 两条假设待实机验证（S01 unlit_shader 假定 URP 有 Unlit/Color；S06 emission_color 假定 URP Lit 有 _EmissionColor）。
5. **8092 工程 `PROJECT_NOT_DEDICATED`**：挑战档要求 dedicated:true，OC1/OC3 实机验证因此暂停，需用户决定由面板标记还是临时快照放宽。
6. **suite3 待用户拍板**：是否改名；各 rep 是否换种子（换则成绩非官方）；发布草案三问（dev 基础题是否也要 BuiltIn 门禁、zip 文件名、tag 下载抽查失败是否阻断发布）。
7. **suite3 真实冒烟 slot 等用户指令**（遵守 §5 的"做完后停下来"）。
8. DS3 断言与 lint_pack 的 2 个失败属在途工作，非缺陷。

## 8. 继续工作的第一步建议

1. 先 `cd /Users/betsy/CodeSpace/Unity-Skills-v3 && git status` 盘点未提交改动；
2. 重跑双 Python 版本全量测试确认基线（预期：仅 lint_pack manifest 与 DS3 两个已知失败）；
3. 重新分派子任务：WP11 manifest 重建 → 第三波提交 → 冻结副本重跑门禁 → WP4b/P15 提交 → WP15 题目集成与实机验证；
4. 遇到 §7 中标注"需用户决定"的条目，逐一向用户提问。
