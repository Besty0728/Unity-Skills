# 当前接力状态（2026-10-21 19:45 JST）

这是当前状态的优先说明；下方较早内容是历史会话材料，若有冲突以本节为准。本机时区是 JST（UTC+9），runner、门禁记录和会话目录名里的时间是 UTC。

## 新会话怎么开始

把下面这段贴给新会话：

```
@SESSION-HANDOFF.md @eval/runs/pending-20261021/gate3-result.md 按"当前接力状态"继续：先做"下一步"，遇到"等用户决定"的事先问我。Unity 可用 Unity CLI 自行唤起，最多同时开两个编辑器（16 GB 内存）。
```

`gate3-result.md` 由后台脚本在本轮门禁跑完后自动生成；还没生成说明门禁还在跑。

## 现场

- 仓库 `/Users/betsy/CodeSpace/Unity-Skills`，分支 `beta`，HEAD `038d4b7b`。**上一轮和本轮的改动都还没提交**，清单见"已落盘的改动"。
- 两个 2022.3.62f2 URP 工程（端口每次启动可能互换，本轮原工程 8091、2022b 8090）：
  - `~/UnityProject/UnitySkillsEval2022`：原工程。
  - `~/UnityProject/UnitySkillsEval2022b`：为并行门禁复制的第二个工程。
    - 只复制了 Assets、Packages、ProjectSettings，没复制 Library；dedicated 标记和 `cli_config.json` 已一并带过来。
    - productName 和原工程相同，所以 `/health` 和实例注册表显示的名字也相同；runner 按工程路径找编辑器，不受影响。
    - 第一次打开停在 Untitled 场景，runner 会报 `SCENE_UNTITLED`，要先 `scene_load Assets/Scenes/SampleScene.unity`。
- **19:38 起两台编辑器并行跑 URP 剩余 16 题**，全部是独立后台进程，不依赖 Claude Code 会话，外面套了 `caffeinate` 防休眠（合上笔记本盖子仍会睡眠）。脚本和日志在 `eval/runs/pending-20261021/gate3/`：
  - `queue`：共享题队列，顺序 OC5 DB1 DB3 DB5 DB2 DB6 T1 T2 T9 DS1 DS3 DS2 DC5 DC1 DC3 DC7。
  - `worker.sh <名> <工程> <端口>`：A 端原工程、B 端 2022b，各取一题跑一题；每题结束后清一次本端撤销历史；退出码 2（环境问题）时把题放回队列并停下。日志 `a.log`、`b.log`。
  - `report.py`：两个 worker 都退出后，写 `eval/runs/pending-20261021/gate3-result.md`，向两台编辑器发 SIGTERM，并弹系统通知。
- 编辑器被 SIGTERM 结束后，重开必须带 `-unityskills-coldstart`：`~/.unity/bin/unity open <工程> --args -unityskills-coldstart`。
- skill 工程（Built-in，2022）已关闭；TestSkills（6000）本轮没开。

## 本轮进展（23:10）

- **第 3 轮（gate3）跑完**，结果见 `eval/runs/pending-20261021/gate3-result.md`：GREEN OC5 DB1 DB3 DB5 DB2 DB6 T1 T2 T9 DS2 DC5 DC1 DC3 DC7；RED DS1、DS3，都是按名义时刻采样的偏差（DS3 的死循环已修好，修好后计数比名义时刻超前约 0.3 秒）。
- **DS1、DS3 已改为按事件计时**（用户批准的计划，见 `~/.claude/plans/valiant-painting-hinton.md`），已在主工作区生效：
  - DS1 等守卫依次到达 W2、W3、W4、W1、W2；DS3 第一步读 containerA（必须为空），再按时间顺序等每次生成（`childCount` 至少为 k），最后一次生成 1 秒后读两个 container。都打 `timeSinceStartup` 戳检查间隔，容差同 DB3。
  - 公开题面 DS1 R4、DS3 R5 同步改写；DB3 R5 也改成和它现有的按事件计时评分一致。
  - 离线 fake 支持 `wait_condition` 逐帧轮询、`step_cost`、`timeSinceStartup`；`_matches` 支持对象和数组带容差比较。
  - manifest、lint、check 通过，离线全量 3.13 与 3.9 各 1675 个全过（含两道题在 20 fps 慢编辑器上判定不变的测试）。
- **第 4 轮（gate4）23:07 起在跑**，全部独立后台：`eval/runs/pending-20261021/gate4/`。
  - URP 两台并行跑 DS1、DS3、DB3（A 原工程 8091，B 2022b 8090），`console_guard.py` 在两题之间清控制台历史。
  - `chain.sh`：两台 URP 跑完后关掉它们，打开 skill 工程（Built-in）跑 DB3，关掉它，生成 `eval/runs/pending-20261021/gate4-result.md` 并弹通知。
  - 这一轮全绿，2022 两格就收尾了。
- **第 4 轮之后自动接着跑 3.0 插件修复的合并验证**（`eval/runs/pending-20261021/verify3/verify3.py`，独立后台，等 gate4 的 `chain.sh` 退出后开始）：
  - 先用未改的代码打开 skill 工程，再 `git apply --3way` 应用 `v3fixes.patch` 和 `fix65.patch`（只改工作区，不提交）；`asset_refresh` 触发编译，3 分钟内没编译就 `debug_force_recompile`。
  - 编译通过后，用 `test_run_by_name` 跑 ConsoleLogHistoryServiceTests、NewCapabilitiesTests、ReadBackAndBatchReliabilityTests、SkillErrorClassifierTests、InstanceExpectationTests、SkillDocumentationConsistencyTests、LifecycleFlushTests、EditorChangeAttributionTests；再跑离线全量 3.13 与 3.9 和 Built-in 的 S01 冒烟；最后关掉编辑器。
  - 结果写在 `eval/runs/pending-20261021/verify3-result.md`，日志 `verify3/verify3.log`。若补丁应用失败或编译失败，工作区里留着已应用的部分，按结果文档处理（`git apply -R` 可撤回）。
- **3.0 Workflow 改造与效率优化的决定**（用户 10-21 通过 AskUserQuestion 拍板）见 `eval/runs/pending-20261021/decisions-3.0-workflow-efficiency.md`：Workflow P0–P3 全进 3.0；回退先做"回到状态"；检查点自动加 AI 主动，可关闭、默认开启；手动改动作为冲突由 AI 询问用户；3.0 全部落地后统一跑一轮门禁；效率目标为成功率 > token > 时延；模块文档先拿 3–5 个试点分层；2022 门禁收尾后跑真实模型小样本基线。
- **Issue #65 正在拖慢门禁**：三个工程的 `console_log_history.jsonl` 都满 5 万条，每 1–2 秒整份重写一次。`gate3/console_guard.py`（独立后台）占住队列锁，在两题之间对每台编辑器调 `console_clear`，题目进行中不发调用；日志 `gate3/console_guard.log`。
- **Issue #65 修复已备好**（用户 10-21 决定随 3.0 发布）：`eval/runs/pending-20261021/fix65.patch`，基于 beta，改 `ConsoleLogHistoryService.cs` 和它的测试文件；worktree 在旧会话 scratchpad 的 `fix65/`（合并后 `git worktree remove`）。
  - 历史满后只追加：挤掉的行留在文件里，累计到 1/4 上限（条数或字节）才压缩；Play Mode 下等满一整轮才压缩（`FlushWhenDue` 在主线程读 `isPlayingOrWillChangePlaymode` 传进 `FlushNow`）。持久化的 `dropped` 扣掉还留在文件里的行，与 `corruptRows` 同一做法，避免重载时重复计数。
  - 新增 4 个测试（追加不重写、1/4 后压缩、Play Mode 延后、重载只计一次 dropped）。已用 Unity 自带 csc 按 URP、Built-in 两套参数离线编译，0 错误、改动文件 0 警告；补丁能干净应用到当前工作区，与 v3fixes 不冲突。EditMode 测试还没跑，和 v3fixes 一起在门禁结束后跑（`ConsoleLogHistoryServiceTests` 全部 14 个）。
  - GitHub 回复已起草（英文），等用户确认后用 `gh issue comment 65` 发出。
- 性能调查结论写在 `eval/runs/pending-20261021/perf-findings.md`（#65、撤销历史整份重写、OC4 批量每项 0.23 秒），都要改插件 C#，等门禁结束后由用户决定。

## 门禁矩阵（2022.3.62f2）

| 格 | 状态 |
|---|---|
| Built-in（11 道基础题） | **全绿**。DB3、DB6 是本会话补跑的 |
| URP（37 题） | GREEN 21 题：上一轮的 14 题，加 OC1 OC2 OC3 OC7 OC6 OC4 OC8（OC4、OC8 是 10-21 夜里重跑的，结果见 `eval/runs/pending-20261021/night-gate-result.md`）。OC5 上次 RED，修复已应用，本轮重跑。**本轮在跑**：OC5 和 DB1 DB3 DB5 DB2 DB6 T1 T2 T9 DS1 DS3 DS2 DC5 DC1 DC3 DC7 |
| 6000 URP / 6000 Built-in | 未开始，用户已推迟 |

看进度：`grep -hE '^gate |^=== ' eval/runs/pending-20261021/gate3/*.log`；门禁记录在 `eval/runs/selftest/gate.json`。

## 下一步（按顺序）

1. **看本轮结果**：读 `gate3-result.md`。RED 的题按老办法排查：看 `<工程>/Library/UnitySkills/eval/sessions/<会话>/attempts/*/result.json` 里 `score.assertions` 的 `actual`，修完只重跑那道题（`python3 -B eval/cli.py selftest3 --project <工程> --task <题> --abort-active`）。
   - DS1、DS2、DS3 仍然按名义时间采样，窗口 ±0.3 秒；如果失败，按 DB3 的思路改成按事件计时。
   - 修题前确认没有 selftest3 在跑（`pgrep -f "eval/cli.py selftest3"`）。
2. **URP 格全绿后**：更新本文，请用户确认后再提交并推送 `beta`。
3. **合并 3.0 插件三项修复**（见"待合并"）。
   - 在主工作区运行 `git apply --3way eval/runs/pending-20261021/v3fixes.patch`。这份补丁基于 beta，改 13 个文件。
   - 确认两台 URP 编辑器已关，用 `~/.unity/bin/unity open ~/UnityProject/skill --args -unityskills-coldstart` 打开 skill 工程并编译。补丁 10-21 已用 Unity 自带 csc 按两个工程的编译参数离线编译过（URP、Built-in 的 Editor 和 Tests.Editor 程序集都 0 错误、补丁文件 0 警告，脚本在旧会话 scratchpad 的 `csc/compile.py`），也确认能干净应用到当前工作区；EditMode 测试还没跑。
   - 用 `test_run_by_name` 跑 `NewCapabilitiesTests`、`ReadBackAndBatchReliabilityTests`、`SkillErrorClassifierTests`、`InstanceExpectationTests`、`SkillDocumentationConsistencyTests`；再跑离线测试；再跑 selftest S01 冒烟。
   - 全部通过后请用户确认提交。
   - 清理：`git worktree remove` 掉 `.claude/worktrees/agent-*` 两个（基于 main，已被补丁取代）和旧会话 scratchpad 里的 `v3fixes`，再删掉 `worktree-agent-*` 两个分支。
4. 然后停下，等用户开 6000 两格。

## 已落盘的改动（主工作区，未提交）

- **上一轮的改动**（DB6 解法死循环、DB3/DB6 事件计时评分、DB2 用 serialized 属性设值、OC1 反例、离线 fake 与测试、设计笔记、manifest）：本会话开头跑离线全量 3.13 与 3.9 各 1669 个，全过；lint、check、manifest 也都通过。
- **OC5 题目修复**（10-21 19:30 应用，只动 eval 侧）：
  - Canvas 的 `m_SortingOrder` 是隐藏属性，评分默认读不到，所以 `canvas_sort_order` 反例漏判。公开 `task.py` 和私有评分改为读取时带 `includeHidden=True`。
  - 真机上同一路径删除后重建预制体，Unity 会沿用原来的 GUID，所以 `replace_asset` 反例的预期失败集改为 {P5, P6}。
  - 离线 fake 改成和真机一致。应用后 manifest、lint、check 通过，离线全量 3.13 与 3.9 各 1669 个全过；lint 多出一条 "P10 没有反例" 的警告，可接受。
- **C# 补丁：变更日志补写 assetPath**：`EditorChangeTrackerService.cs` 改了，`EditorChangeAttributionTests` 新增一条用例，21/21 通过。真机上 OC1、OC2 之前多失败的 A9、A7 已经消失。

## 10-22 00:10 状态（用户 5 小时额度将满，暂停）

- **gate4**：4 题全部 GREEN。2022 URP 37/37、Built-in 11/11，结果见 `gate4-result.md`。
- **verify3**：v3fixes 和 fix65 已应用到主工作区（未提交）。编译通过，8 个 EditMode 测试类共 171 个全过，离线 3.13 和 3.9 通过，S01 冒烟 GREEN，结果见 `verify3-result.md`。
- **进度页**：`page/unityskills-3.0-progress.html`，用 `python3 eval/runs/pending-20261021/page/build.py` 重新生成。
- **小样本基线（A 臂）的参数已定，但还没启动**：
  - 参数：sonnet、运维基础 6 题加 T4、每题 3 次、verify3 结束后跑。
  - 没启动的原因：`claude -p` 会占用同一个订阅额度。等额度恢复、用户说开始之后再跑。
  - 启动步骤：打开 UnitySkillsEval2022，`console_clear`，然后先加 `--dry-run` 看一遍，再正式跑：
    - `python3 -B eval/cli.py --project ~/UnityProject/UnitySkillsEval2022 suite3 --suite base-a-1022 --host claude --models sonnet --track ops --tier basic --reps 3`
    - 同一个 suite 名，改成 `--tier standard --task T4`
  - 还没核实的一点：同一个 suite 能否跨两个 tier。cohort 绑定可能会拒绝，`--dry-run` 能看出来。
  - B 臂换一个新的 suite 名，因为 suite 绑定了 frozen docs。
- **等用户确认提交**：eval 侧改动、插件修复各一个提交；docs3 等 A/B 结束后再提交。
- **verify4**（00:20 起，后台独立运行，不消耗 Claude 额度）：`verify4/verify4.py`，日志 `verify4/verify4.log`，完成后写 `verify4-result.md` 并弹通知。依次做四件事：
  1. skill 工程跑全量 EditMode；
  2. 临时应用 docs3，跑文档测试，然后撤回并核对工作区逐字节一致；
  3. 两台 URP 编辑器跑回归门禁 T7、OC3、OC7、OC8、T10、D01；
  4. URP 工程跑全量 EditMode。
  - 预计约 1.5 小时。
- **10-22 01:00 夜间安排（用户去睡觉，Claude Code 可关闭）**：
  - **verify4**：后台跑，完成后写 `verify4-result.md`。
    - 全量 EditMode 里有 2 个 token level 测试失败：`PerProjectSettingsTests.TokenLevelSources_FallBackToTheOldGlobalValues` 和 `SkillsTokenLevelTests.LegacyGlobalSettings_AreReadAsTheyAre_WithoutAOneShotDefault`。
    - 和补丁无关：v3fixes 对 `SkillsModeManager` 只是把一个方法从 private 改成 internal。怀疑是执行顺序或本机旧全局设置的影响，`next5` 会单独重跑这两个类。
  - **`night/commit.py`**：等 verify4 结果，6 道回归门禁全绿时做两个本地提交（eval 侧、插件修复，用户已同意，不推送），结果写到 `commit-result.md`。
    - 门禁不全绿，或工作区有计划外的改动，就不提交。
    - 注意：verify3 用 `git apply --3way` 打补丁，v3fixes 和 fix65 的文件已经在暂存区。提交脚本用 `git commit -- <paths>` 按路径分开提交。
  - **`baseline/next5.py`**：verify4 结束后运行：
    1. 单独重跑那两个测试类；
    2. 跑 sonnet 基线 A 臂：运维基础 6 题和 T4，每题 3 次；
    3. 写出 `baseline-a-result.md`，原始记录在 `eval/runs/suite3/base-a-1022/`。
  - **Workflow**：
    - 设计稿已定稿，见 `workflow-design.md` 顶部的 owner 决定。
    - P0 由会话内的 opus 成员在 scratchpad 的 `p0/` worktree 里实现（基于 HEAD + v3fixes + fix65 + `tracker.patch`）。关掉 Claude Code 它就会中断，改到一半的内容留在 worktree 里，下次用 `git -C <p0> diff` 接着做。
    - 补丁导出要以 HEAD + 这三个补丁为基线（`docs3.patch` 也是同样做法）。
    - P0 不改根 SKILL.md，根文档那一行跟 docs3 一起处理。
- **01:30 用户额度不够，暂停**：
  - **P0 已写完（成员在停止前完成）**：补丁在 `eval/runs/pending-20261021/workflow-p0.patch`。
    - 基线：HEAD + v3fixes + fix65 + `tracker.patch`。已验证在基线上重放后，和 worktree 逐字节一致。
    - 规模：改了 18 个文件，新增 `WorkflowHotPathTests.cs`（含 .meta），共 15 个测试。
    - 离线编译：四个程序集 0 错误，用的是 `csc/csc_extra.py`，它会把 rsp 里没有的新文件也一起编译。
    - 还没做：
      - 我对补丁的审查；
      - 在 Unity 里跑 WorkflowHotPathTests，以及回归 WorkflowPersistenceTests、WorkflowBackupResilienceTests、GuideProfileDetourTests、ImportWorkerGateTests、SkillDocumentationConsistencyTests、UISkillsFontTests。
    - 根 SKILL.md 的 P0 行（181 B）：**Undo your own writes**: `workflow_list` → `workflow_undo_task {"taskId":"s<n>"}`, newest first; an older task restores over later edits → [workflow](skills/workflow/SKILL.md).
    - 推迟到 P3：全局只有一个"当前任务"；事务批放在手动任务里回滚后快照残留；异步批每个分块各存一次盘。
    - 推迟到 P1：空闲间隔扣除编译和重载时间；归并 curl、Python 这类泛化 agent 标识。
  - **基线链 `next5.py` 已停**，因为 `claude -p` 会耗用户额度。
    - 一次 claude 运行都还没开始，token level 两个测试类的单独重跑也还没做。
    - **用户纠正（01:55）：用户不在时，不自动跑任何会消耗 Claude 额度的任务。** 基线等用户下次对话时再手动启动：`python3.13 -B eval/runs/pending-20261021/baseline/next5.py 1`。不耗额度的那部分已经拆成 `baseline/tokentests.py`，等 verify4 结束后自动跑，结果写到 `tokentests-result.md`。
  - **照常运行**：verify4（不耗额度）、`night/commit.py`（门禁全绿才提交）。

## 待合并（不在主工作区，等门禁收尾）

- **3.0 插件三项修复**（`eval/runs/pending-20261021/v3fixes.patch`）：用户 2026-10-21 下令，随 3.0 发布。原计划文件已被后续计划覆盖，要点以本节和 memory 为准，memory 见 `telemetry-upload-reserved` 和 `v3-fixes-analytics-telemetry-target`。
  1. `GET /analytics?agent=` 过滤：原来根本没读这个参数。agent 值按现有规则规范化，`(unknown)` 匹配没有 agent 的记录，带过滤时回显 `agent`，缓存键改成 window 加 agent。
  2. 遥测只在失败的行里加 `err`（错误文本，160 字符以内）和 `target`（参数摘要，120 字符以内），先做路径脱敏、再截断：工程根目录写成 `<project>`，用户主目录写成 `~`。`DeleteWindow` 重写日志时保留这两个字段；`recentErrors` 也带上；顺带修了 errorCode 会误取嵌套值的问题。上传功能只预留设计，不写代码。
  3. TARGET_NOT_FOUND 的响应带 `instance`，写成 `"<productName>@<port>"`，文件夹名和 productName 不同时在括号里补文件夹名。只加在这一个错误码上，控制 token；`*_batch` 有任一项未命中时，在顶层加一次。
- **skill 文档整治**（`eval/runs/pending-20261021/docs3.patch`，120 个文件）：处理审查出的问题 1–6，属于 3.0 P0 的文档部分；计划见 `~/.claude/plans/valiant-painting-hinton.md`。
  - **基线**：补丁基于 HEAD + v3fixes + fix65，必须在这两个补丁之后应用。已在新 worktree 上重放验证，结果和 docs3 完全一致。
  - **应用时机**：先跑完小样本基线（A 臂，用现在的文档），再应用补丁跑 B 臂做配对对比。成功率下降就先回滚根文档的改动，再逐项定位（9-25 那次根文档瘦身就是这样被回滚的，见 3ef753a5）。
  - **根 SKILL.md**：8185 B → 7937 B。
    - description 从 520 字符缩到 339 字符，纯英文，Unity 设计问答也会触发。
    - 删掉计数行；Python 助手那行并进 Windows 链接行。
    - 新增撤销路由：`workflow_list` → `workflow_undo_task`。
    - 其余句子都没动。
  - **references/**：
    - 删掉 `SKILL_FULL.md`。别处没有的三条规则已经迁走：`INVALID_MODE` 迁到 error-codes，`?diff=1` 迁到 observability，`search_skills` 迁到 discovery。
    - `README`、`index` 补上链接，`windows.md` 按当前根文档重写。
    - 现在 references/ 下每个文件都能从根文档链接到达。
  - **拆分试点**：material、editor、cinemachine、particle 拆成入口加 `reference/<skill>.md`，共 103 个文件。
    - 入口大小分别为 6276、4939、6177、13507 B。
    - particle 补写了 15 个技能的文档，签名和代码逐项核对过。
    - 测试里加了 PilotModules 和预算（入口大小加约 4%）。
  - **规则同步**：`/skillcheck` 和 AGENTS.md 不再把计数同步到根 SKILL.md，试点从 4 个改成 8 个，并新增 references 可达性检查。
  - **已通过的验证**：
    - 离线一致性脚本 `eval/runs/pending-20261021/docs3/docs3_check.py <树>` 通过；
    - csc 离线编译 URP 和 Built-in 都是 0 错误；
    - 离线全量 3.13 和 3.9 各 1665 个全过。
  - **还没做的验证**：EditMode `SkillDocumentationConsistencyTests`、B 臂配对对比，以及用无头 `claude -p` 实测触发（3 条中文、2 条英文、1 条非 Unity）。
  - **应用后**：在面板里重装 Claude Code（全局）副本，或改成只装工程级，等用户决定。
  - **后续**：试点验证有效后，扩展到其余大于 15 KB 的模块；P0 代码落地后再改撤销路由那一行。

## 本轮发现

- **真正的瓶颈是 `workflow_history.json` 膨胀**：
  - 原工程开工前清空过，跑了约 1 小时 OC 题就涨回 17.4 MB（5936 条快照）。插件每次写操作都要整份重写这个文件，OC4 搭建 fixture 的时间因此从 4 分钟涨到 17 分钟。
  - 两台编辑器同时跑对速度影响很小：同一批量调用每次只慢约 15%。
  - 结论：两台并行是值得的，但每隔几题就要清一次历史，而且只能在两题之间清——用例进行中外部发来的 REST 调用会被记进该例的遥测。本轮 `gate3/worker.sh` 已改成每题结束后自动清。
- **Mac 会闲置睡眠**：10-21 05:09–07:37 门禁因此停了约 2.5 小时。无人值守时必须套 `caffeinate -is`。
- **OC4 的 `component_set_serialized_property_batch` 每次调用约 34 秒**：怀疑每一项都调了 `WorkflowManager.SnapshotObject`，进而调 `GlobalObjectId.GetGlobalObjectIdSlow`，还没实测确认。
- **复制出来的工程**第一次打开停在 Untitled 场景，runner 会报 `SCENE_UNTITLED`，要先 `scene_load Assets/Scenes/SampleScene.unity`。
- **用户反馈的 3 个插件问题**已核实，修复就在上面的 v3fixes 里。

## 注意事项

- 门禁运行中不要重建 manifest，也不要改公开题包文件：runner 会比对 manifest 哈希，报 PACK_INVALID。不要改正在跑的那道题的私有评分和解法文件，否则记为 `inputs_changed`。
- C# 改动会让开着的编辑器都重编译，只能在没有用例在跑时做。
- 用 `kill` 结束编辑器后，重开必须带 `-unityskills-coldstart`，工程里还要有 `cli_config.json`。

## 已知问题

- `workflow_history.json` 的性能问题，见"本轮发现"，现在有实测证据了。
- 插件包更新后第一次 domain reload 会读到旧的 locale 缓存：4 个本地化测试首次运行失败，重跑即过。

## 等用户决定

- workflow_history 的性能问题是否本轮修（建议修：门禁一跑几小时就会拖慢）。
- 门禁结束后 `UnitySkillsEval2022b` 是保留作第二台 URP 门禁工程，还是删掉。
- 6000 两格何时跑，以及是否新建 6000 URP dedicated 工程。
- DB4、DC2、DC6、DC8（规划了但没入包）是补齐，还是按 37 题发布。
- suite3 冒烟、真实模型评测与校准的时机。

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
