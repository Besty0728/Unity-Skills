# 当前接力状态（2026-10-26 23:04 JST）

这是当前状态的优先说明；下方较早内容是历史会话材料，若有冲突以本节为准。本机时区是 JST（UTC+9），runner、门禁记录和会话目录名里的时间是 UTC。

## 10-26 收工（最新，优先于下方所有小节）

- **面板改版：用户已验收，10-26 按用户指示提交并推送到 origin/beta**。提交是 `a77a4a3c`（runner 报告的等级与评语）、`61e87d1d`（安装器加 `/unity-skills-eval`、自定义安装记录）、`3dfcd455`（面板）、`e579beae`（只有面板的安装才记入自定义列表），另有一个交接文档提交；之前 25 个本地提交也一起推送了。`eval/RESULTS_V1.md`、`eval/reports/p0-calibration.md` 是用户自己的改动，没有提交。
- **推送前跑的检查**：4 组离线编译、门禁 hash 与基线逐题一致、`check_locales` / `check_meta_files` / `check_project_version` / `check_skill_frontmatter` 全部通过；`git diff --check` 干净。**最后一轮 UI 改动之后没有跑两条管线的全量 EditMode**，只跑过相关测试类。
- **测试安装已清理**：安装器测试借 `InstallCustom` 往临时目录装，曾在本机自定义列表里留下 13 张 TestAgent 卡片。已用临时 MenuItem 全部移掉，列表现在为空，临时脚本也已删除；`e579beae` 修复了根因。图标目录已删除，全盘没有今天新写入的技能安装；对照实验工程留在 EditorPrefs 里的 3 个 `UnitySkills_n_A01CB171_*` 键已删除。
- **验收后又改了这些**，这一轮只跑过相关测试类，没有跑全量：
  - **图标和折叠三角**：新增 `Editor/UI/VectorGlyphs.cs`，用 Painter2D 画实心三角和刷新、校验图标。
    - History、Analytics、Evaluation、Skills 四个页面的刷新按钮，以及 Skills 页的校验按钮，统一成 24×24 按钮、14px 矢量图标。
    - 历史页检查点组和技能页分类的折叠标志改成矢量三角。Unity 自带 Foldout 的箭头不动。
    - `UISkillsEditorIcons.Apply` 现在只有 Analytics 的清空按钮在用，图标改为 14px 居中。
  - **自定义 Agent 卡片的布局**：输入框只占剩余宽度，按钮宽度固定；高度统一 24px；左边加了「路径」「名称」标签。
  - **自定义安装记入列表**（用户选了：本机共用、可多个、加标签）：
    - 记录存在 EditorPrefs 键 `UnitySkills_CustomInstallTargets`，内容是 `[{name, path, icon}]`，同一路径只记一条。
    - 每条记录一张卡片，排在 6 个内置工具后面，可以更新、卸载、移出列表；`SkillInstallSyncService.RunSyncOnce` 也会同步它们。
    - Custom Path 只装主技能。
  - **自定义图标**：点卡片图标（新建时点「+」）选一张 PNG/JPG，1 MB 以内，复制到 `~/.unity_skills/custom-icons/`，文件名用内容的 SHA1；右键恢复默认；没有记录再用的副本会被删掉。
  - **测试**：新增 `SkillInstallerCustomTargetTests`，8 个用例，在 Built-in 上全过。安装器、文案、字形、History、Evaluation、窗口等相关测试类也都全过。
  - **文案**：三份语言文件各 1416 个键，键集一致，没有缺字。
- **E2E 全部通过**：「全部题目」在 Built-in 工程只含 ops 和 dev 的 basic，分别是 6/6 和 5/5，合并后 11/11，等级优秀。用户已在面板上看过。
- **已清理**：
  - 归因实验的两个工程 `leak/`、图集备份、文案中间文件、日志、skill 工程里的 `panel_group.json`，以及旧会话 scratchpad 里当天的全量测试日志。
  - 保留在本会话 scratchpad 的有：
    - `e2e.py`：`<track> <tier> [tasks]`，环境变量 `E2E_NEGATIVE=<task>` 让该题用反例；工程已开时复用现有编辑器。
    - `glyphs.py`：检查图集缺字。
    - `merge_keys.py`：合并文案键。
    - `gatehash-before.txt`：门禁 hash 基线。
- **skill 工程的编辑器还开着**，用户在用，没有关。
- **用户的两处时区改动也已提交**：`51b2d7cc`，`eval/RESULTS_V1.md` 和 `eval/reports/p0-calibration.md` 的时间改为 Asia/Tokyo。工作区干净。
- **全量 EditMode 已在后台独立运行（10-26 23:16 启动）**：
  - 脚本在 `eval/runs/pending-20261021/full1026/`，用 `bash start.sh` 启动：setsid 开新会话，父进程是 launchd，在 `caffeinate -is` 下运行，与任何 Claude 会话无关。
  - `run.py` 的顺序：
    1. Built-in：skill 工程。启动时编辑器已经开着就复用，跑完也不关。
    2. URP：UnitySkillsEval2022。由脚本打开，跑完关闭。
  - 每条管线先等编译结束，再调 `test_run` 跑 EditMode。
  - 结果写到 `eval/runs/pending-20261021/full1026-result.md`（两条管线的总数、通过、失败、跳过，以及失败详情），然后弹系统通知；过程日志在 `full1026/run.log`。
  - 预计约 50 分钟。合上笔记本盖子会让 Mac 睡眠，测试也会暂停。
  - 需要重跑时，先确认 `pgrep -f full1026/run.py` 没有输出，再执行 `bash start.sh`。
- **下一步**：
  1. 看 `full1026-result.md`。全绿就跳过下面的复跑；有失败就修复，另起提交。手动复跑全量的方法：Built-in 用 `classes.py FULL`，URP 用 `classes_urp.py FULL`，两个都在旧会话 scratchpad 里。再核对门禁 hash：`python3 -B eval/cli.py selftest3 --dry-run | grep 'gate inputs' | sort` 与 `gatehash-before.txt` 比对。
  2. 定位长会话下域重载变慢。

## 10-26 22:40 面板改版

- **面板改版已实现，没有提交，等用户验收**。计划在 `~/.claude/plans/cuddly-foraging-river.md`，已获批。AgentTeam：成员全用 haiku，lead 负责审查和纠错。
  - **视觉**：`UnitySkillsWindow.uss` 的两套 token 换成石墨配色，新增 tint 系列；标签栏的 8 个 token 钉回原值；按钮、卡片、横幅、徽标按预览改。
  - **历史页**：
    - `HistoryTabController` 的 `BuildTimelineGroups` 产出分组；自己画的手风琴，标题行放按钮。首屏 30 组，可以加载更早的组。
    - 按状态显示「预览回退」或「已回退到此 · 撤销回退」。
    - 新增 `WorkflowManager.PreviewRedo(taskId, caller)`；撤销回退走 `RedoRestore`，所以冲突列表不会丢。全局撤销按钮已删除。
  - **评测页**：
    - 四步结构：范围分当前等级、全部题目、挑选题目。去掉了宿主、模型、推理强度、usage 开关、RunMode 和 `--declared`。
    - 全部题目会写 `Library/UnitySkills/eval/panel_group.json`，只包含当前管线能跑的等级（Built-in 只有 ops/dev basic）。提示词是英文协议文本，常量放在 `EvaluationState`。
    - 单会话的提示词正文不在面板里显示（字库限制），只提供复制和打开文件。
  - **runner**：`report.py` 新增 `grade`、`verdict`；中文等级词是 优秀 / 良好 / 及格 / 待提升。新增测试 `eval/tests/runner/test_report_grade.py`。
  - **`/unity-skills-eval`**：新模板 `SkillsForUnity/unity-skills-eval~/SKILL.md`。
    - 安装器的 12 个目标都会同时装 `unity-skills` 和兄弟目录 `unity-skills-eval`；Custom Path 只装主技能。
    - `InstallSkill` 改为 internal，供测试使用。
    - 原 `skills/evaluation/SKILL.md` 精简成指向新技能的说明。
  - **文案和字形**：新增 History 20 个键、Evaluation 41 个键，删除 34 个废弃键，三份语言文件各 1406 个键，保持一致。图集补了 好 秀 良 种 词 跑 骤 七个字，五项检查全部通过（共 1024 个字符和字形）。
  - **文档**：CHANGELOG 3.0.0、README 中英文已同步。
- **验证结果**：
  - 离线：4 组编译 0 错误；runner 测试 1490 个在 3.13 和 3.9 下都通过；`eval/tests` 全过。
  - 门禁输入 hash：48 格与改动前逐题一致，基线在 scratchpad 的 `gatehash-before.txt`。
  - 全量 EditMode：Built-in 2588 个，URP 2588 个。初跑各有 3 个安装器测试失败，原因是测试借 `InstallCustom` 装 eval 技能，已改用 `InstallSkill`，四个安装器测试类在两条管线重跑全过。
  - E2E：在 skill 工程用脚本化 Agent 以面板的参数跑。ops basic 6/6 优秀；挑 S01、S03 两题、其中 S03 故意部分完成，结果为及格，评语里有「S03 (partial: R2)」。脚本在 scratchpad 的 `e2e.py`，用法见文件头。
- **下一步**：
  1. 用户在 skill 工程验收面板：深浅两种主题、历史页的回退和撤销回退、评测页结果卡；点一次「全部题目 → 生成题目」，然后由 lead 用 `e2e.py ops basic` 和 `e2e.py dev basic` 跑完，看合并后的进度和结论。
  2. 验收通过后按区块分别本地提交：视觉、历史页、评测页加 runner 报告、安装器加新技能加文档。不推送。
  3. 然后做长会话重载变慢的定位。

## 10-26 晚间状态（门禁）

- **3.0.0 门禁 48/48 全绿**（HEAD 仍是 `4a5cdc27`，代码没动过）。
  - gate5 跑出 44 绿 4 红：URP 的 DB1、DB2、DB3、DB6，结果见 `eval/runs/pending-20261021/gate5-result.md`。四题失败的都是 Play Mode 场景那条断言（C6/H6/D6/F6）：场景超时，或旋转角度超出容差。
  - gate6 在新开的编辑器 A 上补跑这 4 题，20:15–21:09 全绿，见 `gate6-result.md`。脚本在 `gate6/`，只开 A 一台，没有 console guard。
  - 进度页已重新生成，叙述部分同步更新：门禁完成，新增「重载变慢排查」。
- **红灯原因：长会话下域重载越跑越慢**（插件业务逻辑没问题）。
  - URP 编辑器连跑约 10 小时，域重载从约 1 秒涨到 26 秒，约 460 次重载，越到后面涨得越快。进 Play Mode 一超过 60 秒，场景就超时。A 端场景耗时从 2 秒涨到 85 秒，这四题刚好排在队尾。
  - 变慢的阶段：`ProcessInitializeOnLoadAttributes`（392ms → 12.3s）、`AwakeInstancesAfterBackupRestoration`（155ms → 8.5s）、`BeforeProcessingInitializeOnLoad`（70ms → 1.8s）。`Loaded Objects` 只从 5858 涨到 8017，`MarkObjects` 基本不变。这些 URP 数据记自当时的 Editor-prev.log，原日志已被 gate6 轮换掉。
  - Built-in 那轮（skill 工程，没有 console guard）：264 次重载里从 1.03 秒涨到 1.97 秒。
  - **对照实验**（scratchpad 的 `leak/`）：`n` 是不装插件的空工程，`p` 是同样配置、只多装插件；`ReloadLoop.cs` 负责反复进出 Play Mode。
    - n：66 次重载，稳定在约 385ms。
    - p：67 次重载，稳定在约 500–535ms。插件有约 120ms 的固定开销，被动加载不会累积变慢。
  - **已证实一处插件开销**：把 skill 工程 34.5 MB、27246 行的 `console_log_history.jsonl` 拷进 p 后，重载稳定在约 1.05 秒，`ProcessInitializeOnLoadAttributes` 从 118ms 涨到 649ms。原因是 `ConsoleLogHistoryService.LoadPersistedHistory` 每次重载都用 Newtonsoft 逐行解析整个文件，上限 64 MB / 5 万条，算下来最多每次多约 1 秒。这解释了 Built-in 那轮的大部分增长。
  - **URP 涨到 26 秒的主因还没定位**：URP 两题之间都清过控制台，历史文件不会无限增长。已排除的有：batch_state（上限 100 个 job）、editor_changes（只读尾部 500 条）、评测 sessions 目录（只有面板会读）、PrefabAssetUndo（数量有限，且主要拖慢的不是 InitializeOnLoadMethod 阶段）。
  - 待用户决定：查到什么程度（要在 URP 里复现数小时的长会话再按子系统二分，可能是 Unity / URP 自身的问题）；控制台历史改成重载时不全量解析，属于三档，是否放进 3.0。
- **下一步**：
  1. 等用户定重载变慢的排查范围。
  2. 面板改版（门禁已绿，可以动 C#）。「全部题目」用不改 runner 的做法，仍待用户确认。
  3. 推送 beta、`/release` 都等用户决定。

## 10-25 夜间状态

- **HEAD `4a5cdc27`（chore: bump version to 3.0.0），全部为本地提交，未推送（beta 领先 origin 25 个）。用户 10-25：都留在本地，是否正式发布等用户进一步观察。用户 10-25 明确说暂不推送 beta。**
  - Workflow P1 已完成：1e85066a … 6e91cb3e，共 8 个提交。
  - **Workflow P2 已完成**：7db86a24（P2a 改后状态）、dcd8e133（P2b 逐目标裁决 + 属性级合并）、44b912a8（P2c）。至此 Workflow P0–P3 全部落地。
  - P2 计划：`~/.claude/plans/buzzing-discovering-lovelace.md`（已获批，已全部实施）。
  - 进度页：`eval/runs/pending-20261021/page/unityskills-3.0-progress.html`，用 `build.py` 重新生成，已同步到 P2c。
- **P2c（44b912a8）**：
  - `workflow_restore` 新增 `scope` 参数，逗号分隔多项：`entity:<id>`；物体名或层级路径，以 `/` 结尾时包含子孙及其组件；`Assets/`、`Packages/` 路径，以 `/` 结尾时为整个文件夹；设置键，以 `.` 结尾时按前缀匹配。
  - 过滤发生在分链之后，其余算法不变。有一项匹配不到任何目标，就返回 `SEMANTIC_INVALID`，消息以 `scope ` 开头，并列出最多 10 个目标。代码在新文件 `WorkflowManager.Restore.Scope.cs`。
  - 回退记录新增 `sc` 字段。带 scope 的回退不会让任何步显示 `(undone)`，也不算撤销了之前的回退（`UndoneSteps` / `RestoreRevertedLater` 都跳过它）。
  - 预览新增 `scope` 和 `danglingReferences`，后者尽力而为：取范围外的场景快照，用改后引用（没有就用改前引用）比对回退要删除的对象。
  - §6.3：恢复的 `.unity` 文件快照对应的场景正打开时，不写盘，只 `MarkSceneDirty`，并在 skippedItems 注明；预览条目显示 "not written"。`scene_save` 的描述同步改了。
  - **顺带修了一个早就存在的 bug**：材质、SO 这类 JSON 资产，以前回退只写回文件字节；改动还没存盘时，内存里的对象不变，回退等于没做。现在写回文件后，再把改前 JSON 写回内存对象并 `SaveAssetIfDirty`，与 P2b 合并路径的做法一致；`.unity` 不走这条。
  - 文档：workflow 入口加了 "Undoing One Step and Scope" 一节，删掉已解决的两条局限，入口现为 5906 B，预算 6144 B；两份 reference 补了 scope、merged、悬空引用和打开场景的说明。
  - 新测试 `WorkflowScopeTests` 12 个，覆盖：按名字、entity、子孙、资产路径、文件夹、设置前缀匹配；不匹配时拒绝；不标 undone 且 `sc` 重载后还在；悬空引用；场景打开和关闭两种 `.unity` 情况。
- **PR #66**：作者自己关了 PR。10-25 已按用户确认在 PR 下留言致谢，说明改动已 cherry-pick 进 beta、随 3.0 发布。
- **PR #67**（同一作者，已自行关闭）：修 Apple Silicon + Metal 下 `ScreenCapture` 原生 SIGSEGV 的补丁，用户决定不合并。
  - 不合并的原因：他把截图改成逐个相机手动渲染，会丢掉 Overlay UI；`Camera.allCameras` 没按深度排序；`Screen.width` 不可靠；Play 模式下会多渲染一帧；截图任务原有的 try/catch 被删掉。
  - 待办：列入 3.0 兼容矩阵待核实项，装上 6000.4+ 后实测 `scene_screenshot` 和 play capture 在 URP + Metal 下是否崩溃。确认会崩，再由我们自己修。
- **用户 10-25 的决定**：
  - redo 用方案 B，只撤销那次回退本身。
  - `conflicts` 传 id 列表时，列出的覆盖，没列出的保留。
  - 暂不推送 beta。
  - eval/RESULTS_V1.md 和 eval/reports/p0-calibration.md 里的时区改动是用户自己的，不要动，也不要混进提交。
- **P2a（7db86a24）**：
  - 每步结束时，Modified 快照记录 `afterJson`、`afterObjectReferences`（存在 payload 里）和状态哈希 `afterStateHash`（头字段 `sh`）；Setting 快照记录改后值。
  - `WorkflowStateHash` 在比较时把会话内的 instanceID 引用置空，引用按 gid 排序比较，因此哈希跨会话稳定。回退步同样记录改后状态。
  - 场景对象在一步中没变化的 Modified 快照会被剪掉。
  - 写操作耗时复测：40 MB 日志下 wfMs p95 = 0。
- **P2b（dcd8e133）**，逐目标裁决的规则：
  - 当前状态仍是最后一步留下的：干净回退，丢弃该目标上的冲突。
  - 当前状态已等于改前：跳过。
  - 其他情况：用 `WorkflowJsonMerge` 做属性级三方合并。两边都改过的属性记为叶子冲突，id = `k` + SHA1(目标键 + "leaf:" + 路径)，默认保留。
  - 调用方自己未追踪的写入（日志显示是 caller 或 mixed 窗口）：直接覆盖。日志里完全没有记录的变化：报 "unknown" 冲突。
  - 纯文件资产（贴图、脚本、预制体，即有专用 importer 的资产）按指纹判断，磁盘被改过算冲突。材质、SO 按内存中的状态判断。
  - 没有改后状态的旧步骤沿用 P1 的规则。
  - `TryHydrate` 会记住第一次读取失败的原因，因此被隔离的 blob 仍报"已损坏"。
- **验证**：462dd0b6 上 Built-in 全量 2560 个，0 失败，93 个跳过；URP 全量 2560 个，0 失败，81 个跳过。URP 工程打开后要先等编译结束再开测，`classes_urp.py` 已加上这一步。
- **下一步**（Workflow 代码已全部落地，进入收尾）：
  1. ~~History 页人工验收~~：用户 10-25 已点过，没有问题。
  2. ~~URP 全量 EditMode~~：10-25 在 HEAD 44b912a8 上跑完，2555 个，0 失败，81 个跳过。
  3. ~~REST 脚本实测~~：已完成，见上。
  4. `/updateversion 3.0.0`：10-25 已完成并按用户指示提交为 4a5cdc27。改了 5 个版本锚点、`.github/SECURITY.md` 受支持版本表（3.0.x），并在 CHANGELOG 新增 3.0.0 条目；`check_project_version` / meta / locales / json / `git diff --check` 全部通过。用户 10-25 说"继续本地推进"，暂不推送。
  5. **3.0.0 门禁（gate5）10-25 22:05 起在后台完全独立运行（22:07 两个 worker 开跑）**，目录是 `eval/runs/pending-20261021/gate5/`。
     - 22:00 那一轮的进程挂在 Claude 会话的进程组里，会话退出可能被一起杀掉，所以 5 分钟后停掉重开，旧日志在 `aborted-2200/`，没有写入任何门禁记录。
     - 现在用 `bash start.sh` 启动：setsid 开一个新会话，父进程是 launchd，在 caffeinate 下运行，和任何终端或 agent 都无关。
     - 跑完后自动依次执行：刷新进度页（`page/build.py`）→ 写 `gate5-result.md` → 弹系统通知。需要重跑时，先停掉所有 gate5 进程和编辑器，重置 `queue` / `queue-c`，再执行 `bash start.sh`。
     - `launch.sh` 在 `caffeinate -is` 下依次打开两台 URP 编辑器（A 原工程、B 2022b）。`editor.sh` 的 `port_of` 只认 pid 存活、且命令行里工程路径完全一致的注册表条目，并等编译结束、确认不在 Untitled 场景。
     - A、B 两个 worker 共用 `queue`（37 题，OC8、OC4 排在最前）；`console_guard.py` 在两题之间清控制台。
     - `chain.sh` 等两个 worker 都结束后关掉 URP 编辑器，打开 skill 工程跑 `queue-c`（Built-in 11 题），然后由 `report.py` 生成 `eval/runs/pending-20261021/gate5-result.md`，并弹系统通知。
     - 看进度：`grep -hE '^=== ' eval/runs/pending-20261021/gate5/*.log`；进度页的门禁矩阵只统计 pluginVersion 为 3.0.0 的记录。
     - 合上笔记本盖子会让 Mac 睡眠，门禁也会跟着暂停。
  6. 门禁全绿后：推送 beta、正式发布（`/release`）都等用户决定。
     - 23:44 查看：A 在跑 OC8（第 10/28 个用例），B 在跑 OC4（第 7/19 个），每个用例约 9–13 分钟。2.9.1 时 OC8 也要约 4.5 小时，速度没有退化，这两题本来就最长，所以排在最前。
  7. **面板改版计划（10-25 晚与用户定下的设计，门禁结束后才动 Unity 里的代码，避免重编译打断门禁）**。下面的设计以 HTML 预览为准：`temp/ui-preview/index.html`（`temp/` 被 git 忽略，没有提交）。预览分「当前样式」和「新风格」两栏，打开后选「并排对比」看。
     - **视觉（石墨 Graphite）**：用户已接受。
       - 新配色 token 替换 `UnitySkillsWindow.uss` 的 `:root`，深色、浅色两套都要。控件高 24px，圆角分 4、6、8 三档。横幅、HelpBox 和各种状态统一成「淡色底 + 同色描边」，删除类按钮改成红色描边的空心按钮。
       - **标签栏保持现在的下划线样式、120px 定宽和现在的颜色**，不跟着换新配色。
       - 限制：只用 USS 能实现的属性，不用阴影、渐变，也不靠加粗，因为打包的字体子集只有 Regular。
     - **历史页**：
       - 每个检查点做成手风琴，默认收起。标题行显示检查点名（比如「布置敌人前」）、编号 cN 和步数；标题下一行是「时间 · agent · 动作概括」，比如「创建 Enemy_02、Enemy_01 · 修改 Ground.mat」，动词按技能的 Operation 归类。
       - 自动检查点的标题写「自动检查点」，靠概括行说明内容。第一个检查点之前的步骤单独成一组。
       - 回退预览显示在所属检查点的组里，不再放在列表底部。
       - **每个检查点的按钮按状态切换**：没回退到它时显示「预览回退」；回退到它、且这次回退还没撤销时，标「已回退到此」，按钮变成「撤销回退」。撤销前也先预览冲突。去掉现在那个只能撤销最近一次回退的全局按钮。
       - 引擎已经支持按 id 撤销任意一次还没被撤销的回退：`workflow_redo_task` 传 `s<n>` 或 `c<n>`，见 `FindRestoreForRedo` / `GetUndoableRestores`，所以只改面板。
       - AI 调用 Workflow 本来就走 REST 技能（`workflow_checkpoint` / `workflow_list` / `workflow_restore` / `workflow_undo_task` / `workflow_redo_task`），和面板读写同一份历史，REST 实测已验证。
     - **评测页**：从五个区块精简成四步。
       - **顶部说明**两种开始方式：一是在面板生成提示词，复制给新的 Agent 会话；二是直接对 Agent 说「跑 UnitySkills 评测」或输入 `/unity-skills-eval`，AI 先问范围、题型、等级和语言，再自己开始。两种方式的进度和结果都显示在这一页。
       - **① 选择题目**：范围三选一，分别是当前等级（默认）、全部题目（ops 和 dev 全部等级，共 37 题，一段提示词做完）、挑选题目（在当前等级里勾选，勾一题就是单测）。另有题型、等级、提示语言三项，选好后点「生成题目」。
       - 会话模式固定为整套（一段提示词）。逐题模式只在命令行保留，门禁还在用。
       - 去掉宿主、模型、推理强度和 token 读取开关；环境检查收成标题栏的一行「环境就绪」加「重新检查」，有问题时才展开列表。
       - **② 交给你的 Agent**：显示提示词，加「复制提示词」。面板不自动启动 Agent，仍按现在的方式由用户手动粘贴。
       - **③ 进度**：一条进度条，加每道题的状态标签。Agent 每 `submit` 一题，进度自动更新，现有机制已经满足。
       - **④ 结果**：做完后显示在页面最上面，内容有：通过题数 / 总题数、「总体评价：良好」、完全通过率、平均完成度，以及一句自动拼出来的评语（列出部分完成和失败的题及未满足的要求，无效题注明不计入）。历史会话收进折叠区。
     - **总体评价规则**（用户 10-25 同意按建议做）：
       - 按完全通过率分档：≥90% 优秀，≥70% 良好，≥50% 及格，其余为待提升。完全通过率 = 通过 ÷（通过 + 部分 + 失败）。
       - 评语用模板拼接，不调用 AI，结果可复现。
       - 等级和评语写进 runner 的 `report.json`（新增 `grade` / `verdict` 字段）和 `report.md`，面板只负责显示。
     - **评测程序（runner）要改的**：
       - 新增「全部题目」选择（比如 `--tier all` 或 `--all`）。现在 `select_tasks` 只认单个题型 + 等级。
       - 面板把勾选的题目传给已有的 `--tasks`。
       - 新增 report 的等级和评语。
       - 结果卡里的模型名从哪来，二选一：让 Agent 在提示词流程里上报，或干脆不显示。实施前再和用户确认。
     - **新增独立 skill `/unity-skills-eval`**：
       - 和 unity-skills 并列安装，由 `SkillInstaller` 同时负责安装、卸载和同步。
       - 触发后 AI 先问范围、题型、等级、语言，再调用 `start`，并按会话提示执行。
       - 现有的 `skills/evaluation/SKILL.md` 模块文档改为指向它，或合并进去。
     - **实施注意**：
       - 新文案的中文字符必须已在 `UnitySkillsCN-UI.asset` 字形图集里，缺的用 `AddMissingGlyphs` 补；en、zh-CN、ru 三份语言文件的键要一致。
       - History 改成按组显示后，`ListView` 要换成 ScrollView 或可变高度的列表。
       - 动完 C# 要跑两条管线的全量测试。
       - 预估约 3.5 个工作日。**用户 10-25 定：全部随 3.0。**
     - **对门禁的影响**（23:55 核对 `eval/selftest3.py` 的 contract 9）：
       - 门禁记录的键 = 题号 + 用例版本 + Unity 大版本 + 插件版本号 + 管线 + gateInputsHash。
       - gateInputsHash 只包含三类文件：runner（`Evaluation~/run_eval.py` 和 `usk_eval/**/*.py`，但**不含 `usage/` 和 `report.py`**）；每题的题包文件、private 和 solutions；`eval/cli.py`、`selftest3.py`、`solver_driver.py`。插件的 C# 不在其中，版本号也仍是 3.0.0。
       - 按下面的做法实现，**48 格一格都不受影响，不用重跑**：
         - 视觉、历史页、评测页、挑选题目（用已有的 `--tasks`）都只改 C#。
         - 等级和评语放在 `report.py`。
         - 模型名从 `usage/` 已经读到的每题 model 取，来源是 Claude Code / Codex 日志；其他宿主没有就不显示。
         - `/unity-skills-eval` 是文档加安装器改动。
       - **唯一会触发全量重跑的是在 runner 里做「全部题目」**：改 `session.py`、`pack.py`、`cli.py` 会让全部 48 格的 hash 一起变。
       - 建议的替代做法：不改 runner。「全部题目」那段提示词让 Agent 依次跑 6 个等级，每个等级一次 `start`，做完再开下一个；面板把 6 份报告合成一个进度条和一个总体评价。这个做法待用户确认。
       - 替代重跑的验证：两条管线的 EditMode 全量测试，加上用新面板端到端实跑一次 basic 等级。
       - 硬约束：门禁跑完前，不能动 `SkillsForUnity` 下任何 `.cs`，也不能动 `usk_eval` 下的 `.py`。门禁每记一题都会检查源码是否比已加载的程序集新、输入在运行中有没有变，改了会让剩下的题全红。
- **P2c 之后的修复（462dd0b6）**：
  - REST 实测发现：A 创建并移动了物体，B 后来也移动了它，A 回退并选 `conflicts:"keep"`，结果物体还是被删了。原因是 Transform 链走了属性合并，目标级冲突被换成了叶子冲突，而 `KeepUnconflictedWorks` 拦不住删除。
  - 用户 10-25 决定：回退要删除一个物体时，别人后来对它的**组件或子物体**的改动也算冲突。实测证实之前这类改动会被静默丢掉。
  - 实现：
    - `ResolveRestoreTargets` 把被删物体所有组件和子物体的键记进 `RestoreWork.OwnedKeys` 和 `scan.RemovedKeys`，并加入冲突目标；
    - `AdjudicateWorks` 跳过 `RemovedKeys` 里的目标，不做属性级裁决，保留目标级冲突；
    - `KeepUnconflictedWorks` 中，删除型工作被自身目标或 `OwnedKeys` 上任何未覆盖的冲突拦住，叶子冲突也算。
  - 测试：`WorkflowConflictTests` 新增 4 个，共 16 个全过。`workflow_restore.md` 的 Conflicts 段补了一句说明。
- **REST 实测**：脚本在 scratchpad 的 `restp2/rest_p2.py`，用 `--phase 1|3|4|4c|6|2` 指定项目，结果写到 `restp2/result-*.md`。第 1、3、4、4c、6 项全部 PASS（16/16/25/17/14）。第 2 项（手改冲突）10-25 由用户配合实测，8 PASS、1 FAIL：唯一的失败是脚本在拖动中途取样（-4.44），用户松手后停在 -4.91；回退后保留的正是 -4.91，响应里 x 记为保留的叶子冲突，属于脚本问题，不是插件问题。**设计稿 §11.2 的第 1–4、6 项全部实测通过。**
- **light_set_properties 按名字找不到刚建的灯：已结案，只出现在测试环境**。测试里连续直接调用 `SkillRouter.Execute`，中间不会执行服务器每个请求后都做的 `GameObjectFinder.InvalidateCache()`。`gameobject_create` 会把新物体登记进缓存，`light_create` 不会。线上每个请求、每个批处理步骤之后都会失效缓存，所以不受影响。
- **注意**：用 pkill 关掉编辑器后，实例注册表会留下过期记录；之后新开的编辑器如果占用同一个端口，按工程找编辑器的脚本会连错。两台编辑器不要同时启动，或者启动后核对 `/health` 里的工程名。
- **待核实**：P2b 用 `IsJsonAsset`（importer 为基类 `AssetImporter` 或为空）区分 JSON 资产和纯文件资产，但 `.unity` 归哪一类没有核实过。场景关闭时撤销 `scene_save` 的新测试能通过，说明结果正确，只是判定路径不清楚。
- **用户规则**：探查和改文件的成员一律用 haiku，lead 审查和纠错。
  - 成员前台最多跑 10 分钟；全量测试由 lead 在后台跑。辅助脚本在本会话 scratchpad：`/private/tmp/claude-501/-Users-betsy-CodeSpace-Unity-Skills/40dd579c-875c-4713-95db-6cd7a749b625/scratchpad/`，`classes.py <类名…|FULL>`（会自动开、关 skill 工程）、`csc_extra.py <仓库路径>`（离线编译 4 组）、`wfbench.py`。
  - 两个成员不能同时打开 Unity。测试脚本跑完会关掉编辑器。

## 10-24 21:50 状态

- HEAD `a2c56cec`。beta 上未推送的本地提交：f56e6510、5ebc2adc、2dafb3c5、c3fcf133、f05deb79（安装器修复）、a2c56cec（交接文档）。
- **Workflow P3 已实现，未提交，等用户确认**。方案见 `~/.claude/plans/buzzing-discovering-lovelace.md`。
  - **拆分**：`WorkflowManager` 拆成 4 个 partial（纯移动）。拆分后的原样快照在旧会话 scratchpad 的 `p3split/`，blob 已用 `git hash-object -w` 写入对象库，可以据此单独生成第一个提交。
  - **存储**：新增 `WorkflowStore.cs`。`Library/UnitySkills/workflow/` 下是 `log.jsonl`、封存段及其 `.refs`、`head.json`、`blobs/`，记录为任务状态记录（task/drop/clear/agent）。旧历史移到 `workflow_legacy/`，14 天后自动删除。
  - **设置与界面**：`CapacityMB`（512）和 `MaxAgeDays`（30）两个新键；History 页从 5 个字段减到 2 个，新增旧备份行和删除按钮；三语文案、workflow 文档已同步；死代码已清理。
  - **验证**：
    - Built-in 和 URP 全量 EditMode 各 2436 个，0 失败；UISkillsFontTests 8/8。
    - 实测结果见 `eval/runs/pending-20261021/p3-bench-result.md`：40 MB 日志下写操作 wfMs p95 = 0，和空历史一样。
    - 6 万任务的全量重放耗时 1.0–2.5 s，折合每 1 万任务 160–420 ms，处在 300 ms 目标的临界线上。
  - **成员分工**：p3-store（opus）因额度中断，lead 收尾；p3-ui、p3-cleanup 是 haiku。lead 纠正过两处：新 UI 行的内联样式改成 USS 类；两个 `.meta` 改为兄弟文件的完整格式。
- **下一步**：用户确认后提交 P3（拆分一个、存储加界面一个），然后做 P1。用户规则：探查和改文件的成员一律用 haiku，lead 负责纠错。

## 10-23 21:00 状态

- **21:40 更新**：beta 上未推送的提交现在是 f56e6510、5ebc2adc、`2dafb3c5`（docs3，已并入 SkillCountDocsTests 修复）、`c3fcf133`（P0 + tokenfix + pagesize）。
  - 已合并的 worktree 和 agent 分支都已清理，skill-p0 的插件引用改为指向主工作区。
  - **安装器清理退役文件**（三档，方案见 `~/.claude/plans/buzzing-discovering-lovelace.md`，用户已批准）：已实现，**未提交**。
    - 改了 `SkillInstaller.cs`、`SkillInstallSyncService.cs`（只改注释），新增 `SkillInstallerRetireTests.cs`（含 .meta）。
    - 验证：新测试 8/8、Uninstall 3/3、Sync 30/30；skill 工程全量 2424 个，0 失败；用 10-23 的旧全局副本备份实装一次，`SKILL_FULL.md` 被清掉，和模板逐项一致。
    - 等用户确认提交。
  - 下面几条是 21:00 时的记录，其中"下一步"的 1、2 已完成。
- **HEAD `bb2d00d2`（docs3，本地提交，未推送）**。beta 上未推送的提交：f56e6510、5ebc2adc、bb2d00d2。
- **B 臂已完成**，结果见 `eval/runs/pending-20261021/baseline-b-result.md`：两臂都是 21/21，各题平均 token 加总 B/A 为 0.97。
  - 宿主从 2.1.302 升到 2.1.308，cohort 不允许续跑 base-b-1022，改用 `base-b-1023` / `base-b-1023-t4`。
  - D01（1.34）和 S06（1.17）偏高，都是单次多走几步造成的，两臂都没有读 skill 文档。
- **docs3 触发实测 6/6 符合预期**，见 `trigger-result.md`：5 条 Unity 请求都加载了 unity-skills，设计问答还会读 architecture 等建议文档；非 Unity 请求没有误触发。
- **Claude Code 全局副本已重装**（通过 2022b 工程里的临时 MenuItem 调用 `SkillInstaller.InstallClaude(true)`，之后已删除临时脚本）。旧模板残留的 `references/SKILL_FULL.md` 是手动删掉的，备份在旧会话 scratchpad 的 `backup/`。
  - **发现**：安装器只覆盖、不删除，模板里删掉的文件会一直留在用户已装的副本里（SKILL_FULL.md 就是这样）。要不要处理，等用户决定。
- **主工作区已应用但未提交**：tokenfix、workflow-p0、pagesize，以及 `SkillCountDocsTests` 的修复。
  - pagesize：`SkillRouter.DefaultSummaryPageSize` 改为 `SkillsTokenLevel.FullPageSize`（20），新安装解析为 Full。用户 10-23 批准。
  - `SkillCountDocsTests` 修复：删掉根 SKILL.md 的计数检查。docs3 删了那行计数，但漏改了这个测试。
- **全量 EditMode**（`apply3-result.md`）：Built-in 和 URP 各 2416 个，唯一的失败就是上面那个计数测试。修复后单独重跑 SkillCountDocsTests 3/3、SkillDocumentationConsistencyTests 16/16。两个 token level 的老失败已经消失。
- **下一步**：
  1. 等用户确认后提交。建议分两个提交：计数测试修复并入 docs3（fixup），P0、tokenfix、pagesize 单独一个。
  2. 清理已合并的 worktree：旧会话 scratchpad 的 docs3、fix65、p0、tokenfix、v3fixes，以及 `.claude/worktrees/agent-*` 两个和对应分支。注意 skill-p0 工程的插件引用指向 p0 worktree。
  3. Workflow P3 → P1 → P2（三档，进 PlanMode）。

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

## 10-22 20:40 状态（最新，用户要求代码编写到此停下）

- **基线 A 臂**：21/21 通过，结果见 `baseline-a-result.md`。suite 是 base-a-1022（运维基础 6 题 × 3 次）和 base-a-1022-t4（T4 × 3 次）。同一个 suite 不能跨难度档，所以 T4 单独一个 suite。
- **docs3**：20:36 已应用到主工作区，**未提交**；根 SKILL.md 现在是 7937 B。
- **B 臂（20:45 用户要求只挂无需 Claude 的测试，已中止）**：只开了第一个 slot 就停了，留下了 `eval/runs/suite3/base-b-1022`。下次接着跑同名 suite 会自动续上那一槽，开跑前先用 `--dry-run` 看看。原计划：20:36 启动，跑 `baseline/b_arm.py`，suite 是 base-b-1022 和 base-b-1022-t4。它会消耗 Claude 额度。跑完写 `baseline-b-result.md`，内含 A/B 逐题对比（通过数、平均 processed token、B/A 比值）。
- **判据**：成功率不降，再看 token。
  - 有回退：先 `git apply -R docs3.patch` 撤回根文档改动，再逐项定位。
  - 通过：单独提交 docs3。
- **P0 已就绪**：`workflow-p0.patch` 经过审查和一轮修复；在 skill-p0 实测 WorkflowHotPathTests 25/25，全量 2416 个测试只有那 2 个已知失败。B 臂结束后再应用。
- **tokenfix 已就绪**：`tokenfix.patch` 只改测试，已在 skill-p0 验证通过（5/5、13/13）。B 臂结束后应用。
- **等用户决定**：新安装的 TokenLevel 现在解析成 Custom（Full + 截断开 + 每页 10），建议把每页默认值改成 20，落在 Full 档。
- **用户规则**：AgentTeam 按难度分派 haiku/sonnet/opus，不要全用 opus；用户不在时不自动跑消耗 Claude 额度的任务。

## 10-22 19:50 状态

- **已提交到本地 beta，未推送**：
  - `f56e6510`：eval 侧改动；
  - `5ebc2adc`：插件修复（v3fixes、#65、tracker）。
  - 夜间自动提交没做成，因为 `commit.py` 把输出 `strip()` 后吃掉了第一行开头的空格，路径解析出错；改为手动提交。
- **verify4**：回归门禁 6/6 GREEN，OC8 一题跑了 4.5 小时（31 个用例）。docs3 临时验证 16/16 通过，撤回后工作区逐字节一致。
- **全量 EditMode**：两个工程各 2391 个测试，都是同样 2 个 token level 失败，单独重跑也失败。
  - 和补丁无关：读取旧全局设置时会写入默认值，和测试约定的行为不符，HEAD 上就有这个问题。
  - 已派 `token-fix` 成员查根因，补丁输出到 `tokenfix.patch`。
- **基线 A 臂**：19:45 启动，跑 `baseline/next5.py`，预计 4–5 小时，结果写到 `baseline-a-result.md`。
- **P0**：
  - 在 `~/UnityProject/skill-p0` 实测：这是 skill 工程的副本，插件引用指向 p0 worktree，脚本是 `p0test/p0test.py`，结果写到 `p0test-result.md`。
  - 独立审查由 `p0-review` 成员进行。
- **后续顺序**：
  1. A 臂跑完后应用 docs3，跑 B 臂；
  2. P0 审查和实测都通过后应用 P0；
  3. 然后做 P3、P1、P2。

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
