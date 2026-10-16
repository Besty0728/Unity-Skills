# UnitySkills v3.0 评测 P0 契约（v3-contracts）

> 状态：P0 冻结稿，2026-10-17。worktree `eval-v3` @ `5cb0f951`（基于 2.9.1 候选 `8b52ee98`）。上游方案：`~/.claude/plans/v3-eval-architecture-draft.md`（已批准，下称"草案"）。
> 本文是各实现者（WP1–WP16）的唯一依据。与草案不一致处以本文为准，逐条列在 §11；对草案事实的更正列在 §7.7。
> 规范用词：**必须 / 不得** 为强制；"建议"为非强制。文中 `file:line` 均已对照 worktree 核实。路径简写：没有前缀的 `.cs` 位于 `SkillsForUnity/Editor/Skills/`，`UI/…` 位于 `SkillsForUnity/Editor/UI/`；"v1 `xxx.py`"指 `eval/harness/xxx.py`；`unity_skills.py` 指 `SkillsForUnity/unity-skills~/scripts/unity_skills.py`。

## 0. 通用约定

| 项 | 约定 |
|---|---|
| 时间戳 | runner 写入的时间一律 UTC，`YYYY-MM-DDTHH:MM:SS.mmmZ`（毫秒、`Z` 结尾）。时长单位为秒（float，保留 1 位小数） |
| 解析外部时间 | 必须用 `usk_eval.store.parse_utc()`：接受 `Z` 或 `±HH:MM`，小数 0–9 位（截断到 6 位）。原因：Python 3.9 的 `fromisoformat` 不接受 `Z`；journal 的 `tsUtc` 用 `ToString("o")` 写 7 位小数（`EditorChangeTrackerService.cs:627`）；telemetry/audit 用 `yyyy-MM-ddTHH:mm:ss.fffZ`（`SkillTelemetryService.cs:145`、`SkillsAuditLog.cs:125`） |
| JSON 文件 | UTF-8 无 BOM，`ensure_ascii=False, allow_nan=False, indent=1`；首个键为 `"schema": 1`。JSONL：每行一个对象，`separators=(",", ":")`，`\n` 结尾 |
| stdout | 见 §2.2：单个 `ensure_ascii=True` 的 JSON |
| 标识符 | sessionId `^s\d{8}T\d{6}[0-9a-f]{6}$`（UTC 时间 + 6 位 hex）；attemptId `^[0-9a-f]{12}$`（`secrets.token_hex(6)`，同 v1 `eval/harness/classify.py:18-19`）；runId `^r\d{8}T\d{6}[0-9a-f]{6}$`（正则沿用 v1 `eval/harness/fixture.py:17`，时间改 UTC）；cmdId `^cmd-\d{8}T\d{9}-[0-9a-f]{6}$`（UTC 到毫秒） |
| 路径 | 工程内资产路径一律 `/` 分隔、相对工程根（`Assets/...`）；磁盘绝对路径用本机原生格式；比较前做 `os.path.normcase(os.path.realpath(p))` |
| 前向兼容 | schema 1 内只允许**新增可选字段**；读方必须忽略未知字段。删除、改名、改语义必须升 schema。读到 `schema` 大于自身支持值：runner 报 `SCHEMA_UNSUPPORTED`，面板提示更新包 |
| Python | runner 与引导脚本只用标准库，兼容 3.9–3.13：不得使用 3.10+ 才有的语法与模块（`match`、`tomllib`、运行时求值的 `X \| Y` 类型注解等） |
| 字节码 | 一律 `python -B`；`run_eval.py` 与引导脚本在第一条 import 之前设 `sys.dont_write_bytecode = True`（包目录可能只读，也不得留下 `__pycache__`） |
| 源文件 | `.py` 为 UTF-8 带 BOM、结尾 `# Producer:Betsy`，注释用英文（AGENTS.md）；提示模板中的中文是产品输出，允许 |

## 1. 布局

### 1.1 包内（随 UPM 发布）

```
SkillsForUnity/
  package.json                       # version 是 runnerVersion 的唯一来源
  Evaluation~/                       # "~" 目录：Unity 不导入、无需 .meta；SkillInstaller 只复制 unity-skills~（SkillInstaller.cs:510-554）
    run_eval.py                      # 入口
    usk_eval/...                     # 见 1.2
    pack/
      manifest.json                  # §5.1
      tasks/<ID>/task.py             # PublicTask 子类，§5.2
      tasks/<ID>/prompt.zh-CN.md     # §5.3
      tasks/<ID>/prompt.en.md
  unity-skills~/
    scripts/unity_eval.py            # Agent 引导脚本，§1.4（安装器复制到各宿主的技能目录）
    skills/evaluation/SKILL.md       # advisory 文档（WP13），name: unity-evaluation
```

- `.github/scripts/check_meta_files.py:27-28` 要求 `SkillsForUnity/` 下所有 `.json` 都有 `.meta`，排除名单不含 `~` 目录；`scan_floor.py` 还与 git 已跟踪文件对比（草案事实 3）。WP13 必须让两者排除 `*~` 目录，否则 `Evaluation~/pack/manifest.json` 会让 CI 失败。当前 `SkillsForUnity` 的 `~` 目录里没有 `.json`（已核实），所以现状不触发。

### 1.2 `usk_eval/` 模块

| 模块 | 职责（一行） | 负责 |
|---|---|---|
| `run_eval.py` | 把自身目录插入 `sys.path`、关字节码、panel 模式先重定向 stdio，再调 `usk_eval.cli.main()` 并 `sys.exit` | WP6 |
| `usk_eval/__init__.py` | `RUNNER_DIR`、`package_version()`（读 `../package.json`，不硬编码版本） | WP6 |
| `usk_eval/cli.py` | argparse（错误也走信封）、命令分发、信封、退出码、panel 结果文件 | WP6 |
| `usk_eval/problems.py` | `Problem` 与 §3 问题码表（code → exit、fix 模板） | WP6 |
| `usk_eval/paths.py` | 工程定位、包根定位（§1.5）、状态目录与 IO 目录路径 | WP6 |
| `usk_eval/store.py` | 原子写（临时文件 + `os.replace`，含 Windows 重试）、带 schema 校验的读、JSONL 追加、`now_iso()`/`parse_utc()` | WP6 |
| `usk_eval/lock.py` | §2.6 锁协议（含 Windows 进程存活检测） | WP6 |
| `usk_eval/instance.py` | registry 查找与 instanceId 校验（移植 v1 `instance.py`，改进见 §11 #17） | WP6 |
| `usk_eval/rest.py` | REST 客户端（移植 v1 `rest.py` 的重试语义、只读守卫、`wait_idle`/`wait_job`/`batch`）；身份头见 §7.3；统计请求数 | WP6 |
| `usk_eval/environment.py` | 环境快照、漂移比较、check 各项、技能文档扫描、专用工程标识 | WP6 |
| `usk_eval/session.py` | `start/status/abort/report` 的会话逻辑、选题、`current.json`/`session.json` | WP6 |
| `usk_eval/attempt.py` | `next/submit` 状态机、构建与冻结顺序、崩溃恢复 | WP6 |
| `usk_eval/fixture.py` | scope 与 runId、run 场景进出、resting 场景恢复、层碰撞快照、归属校验清理（移植 v1 `fixture.py`） | WP6 |
| `usk_eval/journal.py` | 磁盘分段读取与 seq 连续性证明（§7.1）、`flatten`（v1 `journal.py:70-104` + 新字段） | WP6 |
| `usk_eval/scope.py` | `scope_violations`（含 `eval/design/scope-verifier-fix` 身份表补丁）、`adopted_paths`、`non_rest`、`direct_writes`、`write_records` | WP9 |
| `usk_eval/telemetry.py` | telemetry 全部轮转文件的时间窗读取（§2.8 S6） | WP6 |
| `usk_eval/classify.py` | 状态分类（§2.9） | WP6 |
| `usk_eval/report.py` | `report.json`/`report.md` 生成 | WP6 |
| `usk_eval/prompts.py` | 前缀 v2 模板、提示文件解析与渲染、会话提示、命令行渲染（§2.5、§6） | WP6 |
| `usk_eval/focus.py` | macOS 编译停滞时把 Editor 拉到前台（移植 v1 `focus.py`）；`USK_EVAL_MANUAL_FOCUS=1` 关闭 | WP6 |
| `usk_eval/task_api.py` | `PublicTask`、`Negative`、`BuildContext`/`VerifyContext`/`SolverContext`（§5.2、§5.7） | WP6（WP9 审） |
| `usk_eval/pack.py` | manifest 读取与校验、公开文件 hash 核对、公开题模块加载、选题 | WP8 |
| `usk_eval/private.py` | 私有 verify 获取（URL / zip / pack-dir / private-dir）、sha256 钉住、临时加载、删除、残留清理、可达性探测（§5.9） | WP8 |
| `usk_eval/usage/__init__.py` | 按宿主分派，组装 usage 记录（§4.7） | WP7 |
| `usk_eval/usage/claude.py` | Claude Code 会话日志读取（§8.2） | WP7 |
| `usk_eval/usage/codex.py` | Codex rollout 读取（§8.3） | WP7 |
| `usk_eval/verifylib/__init__.py` | 公开 API 统一导出（§5.8） | WP9 |
| `usk_eval/verifylib/scorecard.py` | `Scorecard`（v1 `verify.py:19-75` + req 映射） | WP9 |
| `usk_eval/verifylib/probe.py` | 只读 `Probe`（v1 `verify.py:138-210`） | WP9 |
| `usk_eval/verifylib/checks.py` | 数值、姿态、引用与状态比较工具（v1 `verify.py:81-132` + `checks.py`） | WP9 |
| `usk_eval/verifylib/uyaml.py` | Unity YAML 读取（v1 `uyaml.py` 原样移植） | WP9 |
| `usk_eval/verifylib/snapshot.py` | `canonical_digest`、`structure`、`hierarchy_signature`、`asset_listing`、`fingerprint` | WP9 |

### 1.3 仓库侧（不进包）

```
eval/
  cli.py                       # 维护者 CLI：selftest、批量、报告；直接 import usk_eval（WP12、WP16 先后修改）
  harness/{subject,codex_proxy}.py   # 保留（批量无头宿主）；其余 v1 harness 冻结，只服务 v1 测试与对照
  pack/private/<ID>.py         # 私有 verify（§5.5），在 GitHub 公开，submit 时下载；文件名 = manifest tasks.<ID>.private.file
  pack/solutions/<ID>.py       # reference / negatives / alternatives（§5.6），用户 runner 永不下载
  tools/lint_pack.py           # 可追溯 lint（草案 3.2）
  tools/build_pack_manifest.py # 计算 hash、写 manifest
  tools/check_eval_pack.py     # 核对 manifest 与 eval/pack/private；hash 变而 caseVersion 未升则失败
  tests/test_*.py              # v1 离线测试，保留
  tests/runner/__init__.py     # 必须存在：unittest discover 只递归进有 __init__.py 的子目录（本机 3.13 实测）
  tests/runner/test_*.py       # usk_eval 离线测试；先把 <repo>/SkillsForUnity/Evaluation~ 插入 sys.path
  tests/runner/fixtures/       # 合成日志与合成 journal（不得含真实对话内容）
  reserved/                    # 本地保留题；eval/.gitignore 第 3 行已忽略
```

### 1.4 Agent 引导脚本 `unity-skills~/scripts/unity_eval.py`

- 约 80 行，只依赖标准库，与版本无关；源文件 UTF-8 带 BOM、结尾 `# Producer:Betsy`。安装器会把 `.py` 改写为无 BOM 的 UTF-8（`SkillInstaller.cs:591-607`），所以脚本不得依赖 BOM；也不得写 shebang（BOM 在前时 shebang 无效）。
- 只负责定位，不读写评测状态。算法（顺序固定）：

```text
1. sys.version_info < (3, 9)                      -> 失败 PYTHON_TOO_OLD
2. project =
       --project P（argv 任意位置）
     | 环境变量 USK_EVAL_PROJECT
     | walk_up(cwd)
     | walk_up(dirname(abspath(__file__)))         # 工程级安装：<project>/.claude/skills/unity-skills/scripts/
     | registry 中唯一活实例的 path
   walk_up(d)：d 及其祖先中第一个同时有 Assets/ 目录与 ProjectSettings/ProjectVersion.txt 的目录
   registry：~/.unity_skills/registry.json（RegistryService.cs:19-22）中 status 为 running（或缺省）
             且 now - last_active <= 120 s 的条目（同 unity_skills.py:105-113）；
             恰 1 个 -> 其 path；多于 1 个 -> PROJECT_AMBIGUOUS；0 个 -> PROJECT_NOT_FOUND
3. root = resolve_package_root(project)（§1.5）    -> 失败 PACKAGE_NOT_FOUND
4. runner = root/Evaluation~/run_eval.py 不存在     -> 失败 RUNNER_NOT_FOUND（包低于 3.0）
5. argv 中没有 --project 时，在命令名之后插入 ["--project", project]
6. os.environ["USK_EVAL_ENTRY"] = abspath(__file__)；sys.dont_write_bytecode = True
7. sys.argv = [runner] + argv；runpy.run_path(runner, run_name="__main__")   # SystemExit 原样上抛，退出码不变
```

- 引导脚本自身失败时输出与 runner 同格式的信封（§2.2）：`{"ok":false,"command":<argv[0] 或 null>,"data":null,"problems":[...],"next":null}`，退出码 2。面板从不经过引导脚本。

### 1.5 包根定位（引导脚本、runner 与 C# `PackageRootLocator` 语义一致）

读 `<project>/Packages/packages-lock.json` 的 `dependencies["com.besty.unity-skills"]`：

| `source` | 包根 |
|---|---|
| `embedded` | 扫描 `<project>/Packages/*/package.json`，取 `name == "com.besty.unity-skills"` 的目录 |
| `local` | `version` 以 `file:` 开头；其后路径为绝对路径则直接用，否则相对 `<project>/Packages/` 解析 |
| 其他（`git`、`registry` 及任何未列出的值）或 lock 中无条目 | 候选 = `<project>/Library/PackageCache/com.besty.unity-skills@*` 中 `package.json` 名称匹配的目录。1 个即用；多于 1 个时优先"后缀是 lock `hash` 前缀"的目录，否则取 `package.json` mtime 最新者并给 notice `PACKAGE_ROOT_AMBIGUOUS`；0 个且 lock 无条目时再按 `embedded` 规则扫描 |

- 本机实测：8 个工程的 lock 文件中 `source` 取值有 `builtin/embedded/git/registry/local`；本包的条目见到 `git`（`version` 为 git URL，带 `hash`）与 `local`（`version` 为 `file:/abs/path/SkillsForUnity`）——`file:` 安装的 `source` 是 `local`，不是 `file`。
- PackageCache 目录后缀**不能**当成 git 提交：2022.3.62f2 为 lock `hash` 前 10 位（如 `@58d3339d90`）；6000.3.9f1 为包内 `package.json` 的 `_fingerprint` 前 12 位，与 lock `hash` 无关（如 lock `c0433593…` 对应 `@54357c6cfac1`）。所以多候选时只能按上表消歧，最终由 runner 的版本校验（`VERSION_MISMATCH`）兜底。
- C# `PackageRootLocator.TryGet(out root)` 用 `PackageInfo.FindForAssembly` / `FindForAssetPath("Packages/com.besty.unity-skills")` 的 `resolvedPath`（抽自 `SkillInstaller.cs:520-530`），不读 lock 文件。

## 2. Runner CLI

### 2.1 调用、全局参数、环境变量

```
python -B <entry> <command> [全局参数] [命令参数]
  <entry> = <pkg>/Evaluation~/run_eval.py，或引导脚本（它转调前者）
```

| 全局参数 | 默认 | 说明 |
|---|---|---|
| `--project PATH` | 见下 | Unity 工程根 |
| `--session SID` | `current.json.sessionId` | 指定会话；`next/submit` 只接受活动会话 |
| `--launcher agent\|panel\|batch` | `agent` | 调用方；决定输出通道、`submittedBy`、`integrity` |
| `--result-file PATH` | — | `launcher=panel` 时必填 |
| `--log-file PATH` | — | 额外日志汇；panel 模式同时承接 stdout/stderr |
| `--cmd-id ID` | — | 面板命令 id，回写到结果文件 |
| `--inline` | 关 | 在 stdout 的 `data` 中内联提示正文（`prompt`、`sessionPrompt`、`answerSchema`）；默认只给文件路径（§11 #4） |

runner 自己的工程定位顺序：`--project` → `USK_EVAL_PROJECT` → runner 所在位置（路径含 `/Library/PackageCache/` 取其之前的部分；位于 `<project>/Packages/<dir>/` 取 `Packages` 的父目录）→ `walk_up(cwd)` → registry 唯一活实例。引导脚本与面板总是显式传 `--project`。

| 环境变量 | 方向 | 用途 |
|---|---|---|
| `USK_EVAL_PROJECT` | 读 | 默认工程 |
| `USK_EVAL_ENTRY` | 引导脚本写，runner 读 | 渲染命令行时用的入口路径（§2.5） |
| `USK_EVAL_REGISTRY` | 读（测试） | 覆盖 `~/.unity_skills/registry.json` |
| `USK_EVAL_CLAUDE_ROOT`、`USK_EVAL_CODEX_ROOT` | 读（测试、E2E） | 覆盖宿主日志根目录（§8） |
| `USK_EVAL_PRIVATE_URLS` | 读（测试） | JSON 数组，覆盖 manifest 的 `private.urlTemplates`；设置后会话 `official=false` |
| `USK_EVAL_MANUAL_FOCUS` | 读 | `1` 关闭 macOS 拉前台 |
| `CLAUDECODE`、`CLAUDE_CODE_SESSION_ID`、`CLAUDE_CONFIG_DIR` | 读 | 宿主识别与 usage（§8） |
| `CODEX_THREAD_ID`、`CODEX_HOME`、`CODEX_SANDBOX` | 读 | 同上；`CODEX_THREAD_ID` 是否导出给工具子进程待 WP0x 实测 |

### 2.2 信封、输出通道、退出码

```ts
type Problem  = { code: string, message: string, fix: string, args?: string[] };  // args：fix 与本地化串里 {0}{1}… 的位置参数
type NextStep = { command: string, line: string, argv: string[] };                // line 可直接粘贴执行；argv 供程序使用
type Envelope = { ok: boolean, command: string | null, data: object | null,
                  problems: Problem[], next: NextStep | null };
```

- agent/batch 模式：stdout **只**写一行 `json.dumps(envelope, ensure_ascii=True, separators=(",", ":"))` + `\n`；不得向 stdout 或 stderr 写任何其他内容（有的宿主把两个流合并）。日志只进 `runner.log`（§4.1）与 `--log-file`。
- `ok == (problems == [])`。`problems` 只放**阻断**项；不阻断的提示放 `data.notices: Problem[]`（同结构）。`problems` 非空时 `data` 仍可非空（`check` 照常给出全部检查结果；`submit` 转 pending 时给出 `accepted:true`）。
- `message`、`fix` 只用英文；面板按 code 本地化（键 `tab_eval_problem_<code_lower>`；`SkillsLocalization.Get(key, params object[] args)` 用 `string.Format`，`Localization.cs:227-239`，所以 `args` 是位置数组）。Agent 原样转告 `fix`。
- 退出码：`0` 正常（包括只有 notices）；`2` 环境需要用户处理；`3` 协议或状态错误；`4` 基础设施错误；`5` 锁超时。多个问题取最大值。argparse 错误也必须走信封（覆盖 `ArgumentParser.error`，它默认以 2 退出），code `BAD_ARGUMENTS`，退出码 3。
- 未捕获异常：信封 `INTERNAL_ERROR`（退出码 4），traceback 只写日志。

### 2.3 Panel 模式

- 条件：`--launcher panel`。`run_eval.py` 在 import `usk_eval` 之前预扫 argv，把 `sys.stdout`/`sys.stderr` 换成写 `--log-file`（缺省写 `Library/UnitySkills/eval/runner.log`）的 UTF-8 文本流，因为 GUI 父进程给的句柄可能无效。
- 结束时把 `Envelope & { exitCode: int, finishedAt: iso, cmdId: string | null }` 原子写到 `--result-file`（同样 `ensure_ascii=True`）。退出码写进文件，是因为 domain reload 后面板拿不到 `Process` 对象。
- 结果文件写不出时只剩进程退出码，面板按 §7.6 判 `crashed`。

### 2.4 命令

锁一列：`L` = 全程持锁（§2.6）；`—` = 不持锁，只读原子替换过的文件（可追加 `runner.log`）。

| 命令 | 调用方 | 锁 | 作用 |
|---|---|---|---|
| `check` | 面板、Agent | — | 只读检查，返回问题码 |
| `start` | 面板、Agent | L | 先做 check 全部项，再建会话 |
| `next` | Agent、驱动 | L | 构建或重发当前题，或返回 done |
| `submit` | Agent、面板代提交、驱动 | L | 冻结、验收、teardown、写 result；不回分数 |
| `status` | 两者 | — | 进度；任何时候都不含分数 |
| `abort` | 两者 | L | 取消活动题、teardown、会话 aborted |
| `report` | 面板 | 报告已存在时 —；需（重新）生成时 L | 会话结束后才可用 |
| `usage` | 面板、内部 | L | 重算或手填 usage |
| `cleanup` | 面板 | L | 归属校验后清理残留 run |

#### `check`

| 参数 | 默认 | 说明 |
|---|---|---|
| `--track ops\|dev` | `ops` | |
| `--tier basic\|standard\|challenge` | `basic` | |
| `--tasks ID[,ID...]` | 该赛道档位的全部题 | 子集；顺序即出题顺序 |
| `--pack-dir DIR` / `--private-zip ZIP` / `--private-dir DIR` | 包内 pack + URL 下载 | 互斥，§5.9 |

检查项（全部执行，问题全部列出）：实例定位与身份、版本一致、Python 版本；未编译/导入（`/health.compilation`）、不在 Play（`editor_get_state`，`EditorSkills.cs:349-366`）；模式为任务要求值、surface 为 `full`、dryRunPolicy 与 tokenLevel（只给 notice）；渲染管线（`project_get_render_pipeline.pipelineType` ∈ `BuiltIn/URP/HDRP/Custom`，`ProjectSkills.cs:66-71,234`）；专用工程标识（任务 `requires.dedicated`）；`requires.packages` 是否在 `packages-lock.json`；已加载场景无未保存修改且活动场景有路径（`scene_get_loaded`，`SceneSkills.cs:262-280`）；残留（`Assets/__UnitySkillsEval/` 下匹配 runId 正则且不属于活动题的目录，直接查文件系统）；活动会话（只给 notice）；telemetry 开关（`GET /analytics?window=1h` 的 `telemetryEnabled`，`SkillTelemetryService.cs:848`，该端点有 30 s 缓存）；私有包可达性（所选第一题，完整下载并核 hash，§5.9）；pack 完整性与 `plugin{min,max}`；已安装技能文档（§2.8 末尾）。

```ts
data = {
  project:     { path, dedicated: bool, markerSha256: string|null, environment: "dedicated"|"user-project" },
  package:     { root, source: string, runnerVersion: string },
  instance:    { id, port: int, pid: int, pluginVersion: string } | null,
  editor:      { isPlaying: bool, isCompiling: bool, isUpdating: bool } | null,
  settings:    { mode, surface, dryRunPolicy, tokenLevel, telemetryEnabled: bool|null } | null,
  pipeline:    { type, name, defaultShader } | null,
  scenes:      { loaded: [{ path, isDirty: bool, isActive: bool }], dirty: [path], activeUntitled: bool } | null,
  session:     { active: bool, sessionId: string|null, status: string|null },
  residue:     [{ runId, assetRoot }],
  pack:        { id, version, source: "package"|"pack-dir"|"zip", official: bool, modified: [file] },
  selection:   { track, tier, tasks: [id] },
  privatePack: { kind: "url"|"zip"|"pack-dir"|"private-dir", probedTask: string|null, reachable: bool, via: string|null },
  skillDocs:   [{ path, agentId: string|null, version: string|null, treeHash }],
  python:      { version, executable },
  ready: bool,                     // == ok
  notices: Problem[]
}
next = ready ? start 命令 : null
```

#### `start`

| 参数 | 默认 | 说明 |
|---|---|---|
| `--track` `--tier` `--tasks` | 同 check | |
| `--mode suite\|strict` | `suite` | 整套 / 逐题 |
| `--lang zh-CN\|en` | `en` | 面板把 ru 映射为 en |
| `--declared FILE` | — | §4.4 `declared` 的 JSON 文件（面板、驱动用）；缺项按 §8.1 探测，探测不到为 unknown |
| `--seed N` | manifest `seeds[0]` | 覆盖种子，会话 `official=false` |
| `--pack-dir` / `--private-zip` / `--private-dir` | — | 同 check；写入 `session.pack` |
| `--no-usage` | 读取 | 关闭宿主日志读取 |

效果：执行 check 全部项；有阻断问题则不建会话。否则建 `sessions/<sid>/`，写 `session.json`、`current.json`、会话提示（状态目录与 IO 目录各一份），不调用任何写技能。

```ts
data = { sessionId, created: bool, mode, lang, n: int, selection: { track, tier, tasks },
         official: bool,
         sessionPromptFile: abs-path,          // IO 目录，UTF-8 带 BOM
         sessionPrompt: string|null,           // 仅 --inline
         notices: Problem[] }
next = next 命令
```

幂等：已有活动会话时，若本次显式给出的 track/tier/tasks/mode/lang 与它一致（未给出的不比较），原样返回它（`created:false`）；不一致返回 `SESSION_ACTIVE`。

#### `next`

只有全局参数。流程见 §2.7、§2.8（N1–N9）。

```ts
// 出题（新建或重发）
data = { done: false, sessionId, taskId, caseVersion: int, attemptId, k: int, n: int,
         track, tier, minutes: int,
         promptFile: abs-path,                 // IO 目录，UTF-8 带 BOM
         answerFile: abs-path,                 // IO 目录；Agent 把答案写到这里
         submitCommand: string, submitArgv: string[],
         reissued: bool,                       // true = 活动题原样重发，未重建
         rerunOf: string|null,                 // 被重跑的 invalid 尝试
         prompt: string|null, answerSchema: object|null,   // 仅 --inline
         notices: Problem[] }
next = submit 命令

// 全部完成
data = { done: true, sessionId, n: int, completed: int, status: "completed" }
next = null
```

幂等：活动题为 `ready` 时原样重发（`reissued:true`，`state.reissues += 1`），不重建；为 `submitted/verifying` 时先续完提交再出下一题；为 `scored` 时先补 teardown；为 `building` 时按 §2.7 恢复。

#### `submit`

| 参数 | 说明 |
|---|---|
| `--attempt AID` | 必填 |
| `--answer FILE` | 与 `--no-answer`、`--retry-verify` 三选一 |
| `--no-answer` | 无答案提交（面板代提交、Agent 放弃） |
| `--retry-verify` | 只用于 `pending_verification` 的尝试；可同时给 `--private-zip` / `--private-dir` |
| `--submitted-by agent\|panel\|driver` | 缺省由 launcher 推出：agent→agent，panel→panel，batch→driver |

```ts
data = { accepted: true, alreadySubmitted: bool, pending: bool, sessionId, attemptId, taskId,
         remaining: int,                       // 尚未终结的题数（不含本题）
         done: bool,                           // 会话已完成
         stop: bool,                           // strict 模式恒为 true：Agent 应停止
         nextCommand: string|null }            // == next?.line；strict 模式为 null
```

- **不得**返回分数、status、断言或任何验收结论（整套模式下前一题的反馈会影响后面的题）。答案不能解析为 JSON 对象时照常接受并验收（`answer=null`），不告诉 Agent。
- 幂等：尝试已 `scored/torn_down` 时返回原接受数据（`alreadySubmitted:true`），不重新评分，`state.duplicateSubmits += 1`，第二份答案不采用。
- `--answer` 文件不存在：`ANSWER_NOT_FOUND`（退出码 3），尝试保持 `ready`，修正后可再提交。
- 私有包取不到：尝试转 `pending_verification`，`data.pending=true`，并返回问题 `VERIFICATION_PENDING`（退出码 2），`args[0]` 为重试命令。

#### `status`

```ts
data = { sessionId, status: "active"|"completed"|"aborted", mode, lang, selection, n: int,
         completed: int, remaining: int,
         active: { taskId, attemptId, phase, k: int, issuedAt: iso|null, elapsedSeconds: number, minutes: int } | null,
         tasks: [{ taskId, state, attempts: [attemptId] }],
         createdAt, endedAt: iso|null,
         report: { json: abs-path, md: abs-path } | null }
```

#### `abort`

参数：`--reason TEXT`（默认 `user`）。活动题（`building/ready/submitted/verifying/pending_verification`）冻结证据（journal 读到当前 cursor）→ status `cancelled` → teardown；会话 `aborted`，生成报告。幂等：会话已 aborted 时返回同一结果。

```ts
data = { sessionId, status: "aborted", cancelledAttempt: string|null, teardown: { ok: bool, error: string|null } }
```

#### `report`

参数：`--format json|md`（默认 `json`，只决定 data 附带哪份摘要；两份文件总是都生成）。会话未结束：`SESSION_NOT_FINISHED`。

```ts
data = { sessionId, status, reportJson: abs-path, reportMd: abs-path, summary: ReportSummary /* §4.9 */ }
```

#### `usage`

`usage --attempt AID` 重算宿主 usage；`usage --attempt AID --manual input=N output=N [cacheRead=N] [cacheWrite=N] [reasoning=N] [model=NAME]` 记录手填值（`source:"user"`；整数 ≥ 0；键名区分大小写）。写回 `result.json.usage`，并向 ledger 追加 `kind:"usage"` 行。

```ts
data = { attemptId, usage: UsageRecord /* §4.7 */ }
```

#### `cleanup`

参数：`--stale`（必填，目前唯一模式）、`--dry-run`。只删除路径**完全等于** `Assets/__UnitySkillsEval/<runId>`（runId 符合正则）且不属于活动题的目录；活动场景位于这些目录时，先按该尝试 `state.resting` 恢复场景，没有记录则返回 `CLEANUP_NEEDS_SCENE`。`Assets/__UnitySkillsEval` 删空后一并删除。

```ts
data = { dryRun: bool, removed: [{ runId, assetRoot }],
         skipped: [{ runId, reason: "active_attempt"|"ownership"|"error" }], restoredScenes: [path] }
```

### 2.5 命令行渲染

`submitCommand`、`nextCommand`、`next.line`、会话提示中的 `{next_cmd}`、G8 中的 `{submit_cmd}` 都由 `prompts.render_command(argv)` 生成：

```text
argv[0] = python：
  对 name in ("python3", "python")：shutil.which(name) 的 realpath == realpath(sys.executable) → 用 name
  否则用 sys.executable；Windows 上含空白时先取 GetShortPathNameW 短路径；仍含空白才加双引号
argv[1] = "-B"
argv[2] = entry = $USK_EVAL_ENTRY（经引导脚本）或 run_eval.py 的绝对路径
其余参数：含空白、引号或 shell 元字符时加双引号；路径不得以反斜杠结尾
next   : <py> -B <entry> next   --project <project> --session <sid>
submit : <py> -B <entry> submit --project <project> --session <sid> --attempt <aid> --answer <answerFile>
retry  : <py> -B <entry> submit --project <project> --session <sid> --attempt <aid> --retry-verify
```

已知限制（写进 advisory 文档）：PowerShell 中带引号的可执行路径要加 `& ` 前缀；只有拿不到短路径时才会出现，此时用 `argv`。

### 2.6 锁协议

文件 `Library/UnitySkills/eval/lock`，内容 `{"schema":1,"pid":int,"cmd":string,"launcher":string,"startedAt":iso,"host":hostname}`。

```text
acquire(deadline = now + 120 s):
  loop:
    try: fd = os.open(lock, O_CREAT | O_EXCL | O_WRONLY)；写内容；fsync；close；return
    except FileExistsError:
      info = 读 lock（失败为 None）
      stale = (info is None and lock 的 mtime 早于 10 s)
           or (info and not pid_alive(info.pid))
           or (info and now - info.startedAt > 3600 s)
      if stale:
        os.rename(lock, lock + ".stale-<pid>-<6hex>")   # 目标名唯一，原子；失败说明别人先接管，继续循环
        写日志；continue
      if now > deadline: 返回 LOCK_TIMEOUT(args=[info.pid, info.cmd])   # 退出码 5
      sleep 0.5 s
release():
  读 lock：pid 仍是自己才 os.remove；否则不动（已被接管）
pid_alive(pid):
  POSIX：os.kill(pid, 0)；ProcessLookupError → False；PermissionError → True
  Windows：OpenProcess(0x1000 = PROCESS_QUERY_LIMITED_INFORMATION) + GetExitCodeProcess == 259（STILL_ACTIVE）
           不得用 os.kill：Windows 上它对 CTRL 事件以外的任何信号都会 TerminateProcess
```

- `.stale-*` 文件超过 24 h 由下一次 acquire 删除。
- v1 用 `fcntl`（`eval/harness/runner.py:9,55-77`），Windows 不可用，本协议取代之。

### 2.7 状态机

**会话** `session.status`：`active`（start）→ `completed`（不再有 `pending/issued` 题）| `aborted`（abort）。进入终态时写报告、删除 IO 目录 `<io>/<sid>/`。

**题** `session.tasks[ID].state`：`pending` → `issued`（有未终结尝试）→ `done`（最近一次尝试终结且为 pass/partial/fail/contaminated）| `invalid`（最近一次尝试 invalid 且重跑额度已用完）| `cancelled`。尝试 invalid 且 `rerunsUsed < 1` 时题回到 `pending`，下一次 `next` 用同一种子新建尝试（`rerunOf` 指向它，`rerunsUsed += 1`，k 不变，notice `RERUN`）。与 v1 规则一致：只重跑 invalid，每题一次（`eval/harness/classify.py:3-7`）。

**尝试** `state.phase`：

| 起点 | 触发 | 终点 | 说明 |
|---|---|---|---|
| — | `next`，无活动尝试且有 pending 题 | `building` | 先写 state（scope、resting），再发任何 REST 写 |
| `building` | 构建完成（N8） | `ready` | 记 t0、cursor0，出题 |
| `building` | 构建异常或 `WriteUncertain` | `torn_down` | status `invalid`，reason `build_failed: …`；按归属清理；本次 `next` 返回 `FIXTURE_FAILED` |
| `building` | runner 崩溃后的下一次 `next`/`abort` | `torn_down` | status `invalid`，reason `build_interrupted`；按 `state.scope` 归属清理 |
| `ready` | `next` | `ready` | 重发 |
| `ready` | `submit` | `submitted` | 记 t1、复制答案（S1） |
| `ready`、`submitted`、`verifying`、`pending_verification` | `abort` | `torn_down` | status `cancelled`，证据冻结 |
| `submitted` | 冻结完成（S2–S6） | `verifying` | |
| `submitted`、`verifying` | 崩溃后同一 attempt 的 `submit`，或 `next` | 原相位续跑 | `submitted` 从 S2 重做冻结（窗口随之延长）；`verifying` 用已冻结证据从 S7 重做 |
| `verifying` | 私有包取不到 | `pending_verification` | fixture 保留，会话被阻断 |
| `pending_verification` | `submit --retry-verify` 成功 | `verifying` → `scored` | 期间 Editor 的任何后续变化都会被 Probe 读到，result 标 `verifiedLate:true` |
| `verifying` | 验收完成，或 verify 抛异常 | `scored` | 写 `result.json`；异常 → `invalid`（`verifier_error`） |
| `scored` | teardown 成功 | `torn_down` | 追加 ledger、计算 usage |
| `scored` | teardown 失败 | `torn_down` | pass/partial/fail 改判 `invalid`（`teardown_failed`，同 v1 `runner.py:39-46`）；残留交给 `RESIDUE_FOUND` 与 cleanup |
| `scored` | 崩溃后的 `next`/`abort` | `torn_down` | 补 teardown |

`torn_down` 是唯一终相位。尝试终结后若不再有 `pending/issued` 题，会话 → `completed`。

**strict 模式**：每次 `next` 把当前宿主会话 id（§8.1）记入 `session.hostSessions`。新出题前，若该宿主会话 id 已关联一个终结的尝试，返回 `STRICT_NEEDS_NEW_SESSION`；重发自己的活动题不受限。观测不到宿主会话 id 时照常出题，`state.host.isolation = "unverified"`，notice `ISOLATION_UNVERIFIED`。

**pending 阻断**：存在 `pending_verification` 尝试时，`next` 返回 `VERIFICATION_PENDING`，不新建 fixture（不允许两份 fixture 并存）。

### 2.8 构建、冻结、teardown 的规范顺序

**`next` 构建（N1–N9）**

1. N1 定位实例；校验版本、模式、surface、管线、Play、编译（同 check；失败直接返回问题，不建尝试）。
2. N2 `scene_get_loaded`：任一 `isDirty` → `SCENE_DIRTY`；活动场景 `path` 为空 → `SCENE_UNTITLED`（沿用 v1 `fixture.py:57-61`）。记 `resting.scenes = [{path, isActive, isLoaded}]`，取代硬编码的 `RESTING_SCENE`（`fixture.py:16`）。
3. N3 新建 runId 与 scope；写 `state.json`（phase `building`）、`session.tasks[ID].state = "issued"`、`current.json.activeAttemptId`。
4. N4 层碰撞快照（任务 `layer_pairs`，v1 `fixture.py:101-107`）；`asset_create_folder` 建 `Assets/__UnitySkillsEval`（不存在时）与 run 目录；`scene_create(scenePath)`（`SceneSkills.cs:19-46`：以 Single 模式新建并保存）；确认活动场景就是 run 场景；`gameobject_create(name=objectRoot)` 得 `rootInstanceId`（同 v1 `fixture.py:72-87`）。
5. N5 `task.build(ctx)` → handles（写入 state）；`wait_idle`；活动场景是 run 场景时 `scene_save`。
6. N6 `task.snapshot(ctx)` → C0；计算 fingerprint（§5.8）。
7. N7 记 `evalTreeHash.atIssue`（`<pkg>/Evaluation~` 整棵树，规则同 v1 `env.py:26-42`）与 `env.atIssue`（漂移键 + registry pid）。
8. N8 渲染提示，写状态目录 `attempts/<aid>/prompt.md` 与 IO 目录 `<io>/<sid>/<aid>/prompt.md`（UTF-8 带 BOM）；读 `cursor0`（`editor_get_changes since=0 limit=1` 的 `cursor`）与 `received0`（`/health.queueStats.totalReceived`）；记宿主会话 id 与 cwd；最后记 `t0 = now`，phase → `ready`。
9. N9 返回出题数据。

**`submit` 冻结与验收（S1–S12）**

1. S1 校验尝试是活动题且 phase 为 `ready`（或续跑）；用 `utf-8-sig` 读答案（接受 PowerShell 写的 BOM），复制到 `attempts/<aid>/answer.json`，记 sha256、解析错误、schema 错误；`t1 = now`；phase → `submitted`。
2. S2 `GET /health` 得 `received1`；`GET /jobs?limit=100`，对非终态作业（终态 = completed/failed/cancelled，`AsyncJobService.cs:1605-1608`）逐个 `GET /jobs/{id}?wait=30`，合计最多 300 s；仍未结束的计入 `evidence.freeze.inflightJobs`，不改判。
3. S3 读 cursor 记为 `refreshWindow.from`；调 `asset_refresh`（`AssetSkills.cs:452-500`），返回 `jobId` 时等作业结束；`wait_idle`；再读 cursor 记为 `refreshWindow.to`。窗口内的文件导入视为 Agent 直接写盘的结果，不算 runner 写入。
4. S4 `editor_get_state.isPlaying` 为真时调 `editor_stop`（`EditorSkills.cs:57-68`），记 `forcedPlayModeExit:true`，`wait_idle`。
5. S5 `editor_get_changes since=0 limit=1`：该调用先刷出防抖中的属性变更（`EditorChangeTrackerService.cs:98`），返回的 `cursor` 记为 `cursorEnd`。`cursorEnd < cursor0` → journal 不完整（`seq_reset`）。
6. S6 按 §7.1 从磁盘读 (cursor0, cursorEnd]，证明 seq 连续，写 `attempts/<aid>/journal.jsonl`。等到 `now ≥ t1 + 1 s` 后读 telemetry：`Library/UnitySkillsTelemetry.jsonl` 与 `UnitySkillsTelemetry.1.jsonl`…`.3.jsonl`（`SkillTelemetryService.cs:35-37,527-531`；v1 只读主文件，`metrics.py:386-388`），按"主文件 → .1 → .2 → .3"顺序读、跨文件按整行去重，保留 `t0 < ts <= t1` 且 `agent != "UnitySkillsEval"` 的记录，写 `attempts/<aid>/telemetry.jsonl`。记 `env.atSubmit` 与 `evalTreeHash.atSubmit`。phase → `verifying`。
7. S7 获取私有 verify（§5.9）；失败 → `pending_verification` 并返回。
8. S8 构造 `VerifyContext` 与 `Scorecard(ASSERTIONS)`；调 `verify(ctx, card)`（只读 Probe，沿用 v1 `rest.py:121-126` 的只读守卫）。
9. S9 dev 题且定义了 `SCENARIO`：确认未编译、不在 Play；调 `editor_run_scenario`（`EditorSkills.cs:21-28`）→ 用 `GET /jobs/{id}?wait=30` 轮询至终态（上限 `timeoutSeconds + 120 s`）→ 填 `ctx.scenario`；调 `verify_runtime(ctx, card)`。`cursorEnd` 之后的 journal 一律不读。
10. S10 `card.finish()`；为未通过的断言取 `HINTS[id][lang]`；在 `finally` 中删除私有文件（§5.9）；分类（§2.9）；写 `result.json`；phase → `scored`。
11. S11 teardown：`task.teardown(ctx)` → 恢复层碰撞 → 恢复 resting 场景（第一个场景 `scene_load(additive=false)`，其余按原顺序 `scene_load(additive=true)`，再 `scene_set_active(sceneName=<原活动场景路径去掉 .unity>)`；`SceneSkills.cs:48-62,338-352`）→ `asset_delete` run 目录（路径必须先通过归属校验）→ 确认已删除 → `wait_idle`。失败时等 5 s、`wait_idle` 后重试一次（同 v1 `runner.py:232-250`）。phase → `torn_down`。
12. S12 计算 usage（§8）；追加 ledger；更新 session；若会话完成：写报告、对全部尝试重算 usage（§11 #23）、删除 IO 目录与空的 `Assets/__UnitySkillsEval`。

**漂移键**（S6 比较 `env.atIssue` 与 `env.atSubmit`；沿用 v1 `env.py:19-21`，去掉 `pluginHash`、加 `pid`）：`version, instanceId, currentMode, tokenLevel, surfaceProfile, dryRunPolicy, summaryAutoTruncate, summaryPageSize, panelApprovalRequired, allowlistCount, pipelineType, pid`。`pid` 变化单独记 reason `editor_restarted`。

**技能文档扫描**（check 与 start 的 `skillDocs`）：工程级 `.claude`、`.agents`、`.cursor`、`.opencode`、`.kimi-code` 下的 `skills/unity-skills`；全局 `~/.claude/skills`、`~/.gemini/antigravity/skills`、`~/.agents/skills`、`~/.cursor/skills`、`~/.config/opencode/skills`、`$KIMI_CODE_HOME`（缺省 `~/.kimi-code`）`/skills` 下的 `unity-skills`（`SkillInstaller.cs:19-47`）。版本取 `scripts/agent_config.json` 的 `version`（`SkillInstaller.cs:498`）；treeHash 用 v1 规则并排除 `scripts/agent_config.json`（其中有 `installedAt`）。版本与 pluginVersion 不同 → notice `SKILL_DOCS_OUTDATED`；一份都没有 → notice `SKILL_DOCS_MISSING`。

### 2.9 分类（first match wins）

| 顺序 | status | 条件（reasons 用稳定前缀） |
|---|---|---|
| 1 | `cancelled` | abort（`aborted: <reason>`） |
| 2 | `invalid` | `build_failed: …`、`build_interrupted`、`verifier_error: <Type>: <msg>`、`scenario_infra: …`（scenario 作业丢失、REST 不可达；断言失败、编译错误、运行时报错不属此类，照常评分）、`journal_incomplete: gaps=… \| seq_reset`、`environment_drift: {...}`、`editor_restarted` |
| 3 | `contaminated` | `eval_files_changed: [...]`（evalTreeHash 前后不同，同 v1 `runner.py:143,189-190`）；`non_rest_changes: <n>`（见下）；batch 模式另加 v1 的外部端口与可疑访问检测（`classify.py:51-56`） |
| 4 | `pass` / `partial` / `fail` | `card.strict` → pass；`card.score > 0` → partial（追加 `failed: A1,A2`）；否则 fail（同 v1 `classify.py:71-75`） |
| 后置 | `invalid` | teardown 失败时把 pass/partial/fail 改判（`teardown_failed: …`） |

- `non_rest_changes` = 窗口内 `source == "editor"` 的记录，**排除**：`isPlaying == true`；文件记录（imported/deleted/moved）且路径（去掉 `.meta`）在 A 下（视为 Agent 直接写盘，交给 verify 的 G4 断言）；`scene_saved`/`scene_opened` 且 path 等于 run 场景；`allowed_change(ctx, rec)` 为真。其余 editor 来源的记录（用户手工改场景、用户 Undo 等）都计入。
- 只做标记、不改 status 的 reason（追加在 `reasons` 尾部）：`over_budget`、`forced_play_mode_exit`、`inflight_jobs: <n>`、`isolation_unverified`、`verified_late`。
- `critical` 断言失败不单独改判，只列在 `score.criticalFailed`（与 v1 一致）。

## 3. 问题码

本地化键一律 `tab_eval_problem_<code_lower>`（notices 共用同一命名空间）；三种语言文件键集合必须一致。`{0}`、`{1}` 对应 `Problem.args`。

### 3.1 阻断问题（`problems[]`）

| Code | 含义 | 发出者 | Exit | 本地化键 | Fix (en) |
|---|---|---|---|---|---|
| `PYTHON_TOO_OLD` | Python < 3.9 | 引导脚本、全部命令 | 2 | `tab_eval_problem_python_too_old` | Install Python 3.9 or newer and run the command with it. |
| `PROJECT_NOT_FOUND` | 找不到 Unity 工程 | 引导脚本、全部命令 | 2 | `tab_eval_problem_project_not_found` | Run the command inside the Unity project folder, or pass --project <path to the project>. |
| `PROJECT_AMBIGUOUS` | 多个活实例且无法从 cwd 判定 | 引导脚本、全部命令 | 2 | `tab_eval_problem_project_ambiguous` | Several Unity editors are running; pass --project <path to the project>. |
| `PACKAGE_NOT_FOUND` | 找不到 com.besty.unity-skills 包根 | 引导脚本、全部命令 | 2 | `tab_eval_problem_package_not_found` | Install com.besty.unity-skills in this project and let Unity finish resolving packages. |
| `RUNNER_NOT_FOUND` | 包内没有 `Evaluation~/run_eval.py` | 引导脚本 | 2 | `tab_eval_problem_runner_not_found` | The installed UnitySkills package has no evaluation runner; update UnitySkills to 3.0 or newer. |
| `INSTANCE_NOT_RUNNING` | registry 无条目或 /health 不应答 | check, start, next, submit, abort, cleanup | 2 | `tab_eval_problem_instance_not_running` | Open the project in Unity and make sure the UnitySkills server is running (Window > UnitySkills). |
| `INSTANCE_MISMATCH` | 应答的 instanceId 与工程不符（含 HTTP 409） | 同上 | 2 | `tab_eval_problem_instance_mismatch` | Another editor answers for this project; close duplicate editors and restart the UnitySkills server. |
| `VERSION_MISMATCH` | runnerVersion ≠ pluginVersion | check, start, next, submit | 2 | `tab_eval_problem_version_mismatch` | The editor runs UnitySkills {0} but this runner is {1}; wait until Unity finishes importing the package (or restart the editor), then retry. |
| `EDITOR_BUSY` | 正在编译或导入 | check, start, next | 2 | `tab_eval_problem_editor_busy` | Wait until Unity finishes compiling and importing, then retry. |
| `PLAY_MODE_ACTIVE` | 处于 Play 模式 | check, start, next | 2 | `tab_eval_problem_play_mode_active` | Exit Play Mode in Unity, then retry. |
| `MODE_NOT_BYPASS` | 模式不是任务要求的 bypass | check, start, next | 2 | `tab_eval_problem_mode_not_bypass` | Switch the UnitySkills operating mode to Bypass (Evaluation tab: Switch to Bypass; it is restored when the session ends). |
| `SURFACE_NOT_FULL` | surfaceProfile ≠ full | check, start, next | 2 | `tab_eval_problem_surface_not_full` | Set the UnitySkills skill surface to Full in the settings drawer. |
| `PIPELINE_UNSUPPORTED` | 渲染管线不符合 `requires.pipeline` | check, start | 2 | `tab_eval_problem_pipeline_unsupported` | Use a project created from the Universal 3D (URP) template. |
| `PROJECT_NOT_DEDICATED` | 所选题要求专用工程但没有标识 | check, start | 2 | `tab_eval_problem_project_not_dedicated` | Mark an empty URP project as the dedicated evaluation project in the Evaluation tab, or choose the basic tier. |
| `PACKAGE_MISSING` | 缺少 `requires.packages` 中的包 | check, start | 2 | `tab_eval_problem_package_missing` | Install the package {0} required by task {1}, or choose other tasks. |
| `SCENE_DIRTY` | 有已加载场景未保存 | check, start, next, cleanup | 2 | `tab_eval_problem_scene_dirty` | Save or discard the unsaved changes in {0}, then retry. |
| `SCENE_UNTITLED` | 活动场景没有文件路径 | check, start, next | 2 | `tab_eval_problem_scene_untitled` | Open or save a scene that has a file path, then retry. |
| `RESIDUE_FOUND` | 有不属于活动题的 run 残留 | check, start, next | 2 | `tab_eval_problem_residue_found` | Leftover evaluation data found ({0}); click Clean up in the Evaluation tab or run cleanup --stale. |
| `PRIVATE_PACK_UNREACHABLE` | 探测不到私有 verify | check, start | 2 | `tab_eval_problem_private_pack_unreachable` | Allow access to raw.githubusercontent.com or cdn.jsdelivr.net, or download the private pack zip from the release page and pass --private-zip <zip>. |
| `VERIFICATION_PENDING` | 尝试在等待验收（私有包下载失败） | submit, next | 2 | `tab_eval_problem_verification_pending` | Verification files could not be downloaded. Restore network access (or get the private pack zip), then run: {0} |
| `PACK_PLUGIN_RANGE` | pluginVersion 不在 pack 支持范围 | check, start | 2 | `tab_eval_problem_pack_plugin_range` | This task pack needs UnitySkills {0} to {1}; update the package. |
| `CLEANUP_NEEDS_SCENE` | 清理时活动场景是 run 场景且没有 resting 记录 | cleanup | 2 | `tab_eval_problem_cleanup_needs_scene` | Open one of your own scenes in Unity, then run cleanup again. |
| `BAD_ARGUMENTS` | 命令行参数错误 | 全部命令 | 3 | `tab_eval_problem_bad_arguments` | Check the command line: {0} |
| `SESSION_ACTIVE` | 已有活动会话且选择不同（check 中作为 notice） | start | 3 | `tab_eval_problem_session_active` | Session {0} is still running; continue it with next, or abort it first. |
| `NO_ACTIVE_SESSION` | 没有活动会话 | next, submit, abort | 3 | `tab_eval_problem_no_active_session` | Start an evaluation session first (Evaluation tab or the start command). |
| `SESSION_NOT_FOUND` | `--session` 不存在 | 全部命令 | 3 | `tab_eval_problem_session_not_found` | Use a session id listed in the Evaluation tab. |
| `SESSION_NOT_FINISHED` | 会话未结束就请求报告 | report | 3 | `tab_eval_problem_session_not_finished` | The report is available after the last task is submitted or the session is aborted. |
| `ATTEMPT_NOT_FOUND` | attemptId 不存在 | submit, usage | 3 | `tab_eval_problem_attempt_not_found` | Use the attempt id returned by next. |
| `ATTEMPT_NOT_ACTIVE` | 不是当前活动题 | submit | 3 | `tab_eval_problem_attempt_not_active` | Attempt {0} is not the current task; run next to get the current task. |
| `ATTEMPT_NOT_READY` | 尝试仍在构建 | submit | 3 | `tab_eval_problem_attempt_not_ready` | The task is still being prepared; run next and wait for its prompt. |
| `ANSWER_NOT_FOUND` | `--answer` 文件不存在 | submit | 3 | `tab_eval_problem_answer_not_found` | Write the answer JSON to {0}, then run the same submit command again. |
| `RETRY_NOT_APPLICABLE` | `--retry-verify` 用在非 pending 的尝试上 | submit | 3 | `tab_eval_problem_retry_not_applicable` | Only attempts that are waiting for verification can be retried. |
| `STRICT_NEEDS_NEW_SESSION` | 逐题模式下本宿主会话已做过一题 | next | 3 | `tab_eval_problem_strict_needs_new_session` | This agent session has already done one task. Open a new agent session and paste the same session prompt. |
| `TASKS_INVALID` | 选择为空或含未知 ID | check, start | 3 | `tab_eval_problem_tasks_invalid` | Fix the task selection: {0} |
| `REST_UNREACHABLE` | 重试窗口内 Unity 不应答 | 需要 REST 的命令 | 4 | `tab_eval_problem_rest_unreachable` | Unity stopped answering; make sure the editor is open and responsive, then retry. |
| `REST_ERROR` | runner 自己的 REST 调用被拒或返回业务错误 | 同上 | 4 | `tab_eval_problem_rest_error` | Unity rejected an evaluation request ({0}); see the runner log {1}. |
| `FIXTURE_FAILED` | 构建失败（该尝试判 invalid） | next | 4 | `tab_eval_problem_fixture_failed` | Preparing the task failed; run next again. If it fails again, report the runner log {0}. |
| `TEARDOWN_FAILED` | teardown 失败 | submit, abort, next | 4 | `tab_eval_problem_teardown_failed` | Cleaning up the task failed; click Clean up in the Evaluation tab or run cleanup --stale. |
| `PACK_INVALID` | manifest 缺失、不可解析或与模块不一致 | check, start, next | 4 | `tab_eval_problem_pack_invalid` | The evaluation pack is damaged; reinstall the UnitySkills package. |
| `STATE_CORRUPT` | 状态文件不可解析或缺 schema | 全部命令 | 4 | `tab_eval_problem_state_corrupt` | Evaluation state is damaged; abort the session in the Evaluation tab. If that fails, delete Library/UnitySkills/eval. |
| `SCHEMA_UNSUPPORTED` | 状态文件由更新的版本写入 | 全部命令 | 4 | `tab_eval_problem_schema_unsupported` | Evaluation state was written by a newer UnitySkills; update the package. |
| `IO_ERROR` | 读写失败 | 全部命令 | 4 | `tab_eval_problem_io_error` | Could not read or write {0}: {1} |
| `INTERNAL_ERROR` | 未预期的异常 | 全部命令 | 4 | `tab_eval_problem_internal_error` | Unexpected runner error; see the runner log {0} and report it. |
| `LOCK_TIMEOUT` | 120 s 内拿不到锁 | 持锁命令 | 5 | `tab_eval_problem_lock_timeout` | Another evaluation command is still running (pid {0}: {1}); wait for it to finish, then retry. |
| `RUNNER_CRASHED` | 面板判定：进程已退出但没有结果文件 | 面板 | — | `tab_eval_problem_runner_crashed` | The evaluation runner exited without a result; see {0}. |

### 3.2 非阻断提示（`data.notices[]`，不影响 `ok` 与退出码）

| Code | 发出者 | 本地化键 | Message (en) |
|---|---|---|---|
| `SESSION_ACTIVE` | check | `tab_eval_problem_session_active` | （同 3.1） |
| `TELEMETRY_DISABLED` | check, start | `tab_eval_problem_telemetry_disabled` | Telemetry is off, so skill-call evidence will be missing. Turn it on in the UnitySkills settings. |
| `SKILL_DOCS_OUTDATED` | check, start | `tab_eval_problem_skill_docs_outdated` | The installed skill docs at {0} are version {1}; reinstall them from the AI Config tab. |
| `SKILL_DOCS_MISSING` | check, start | `tab_eval_problem_skill_docs_missing` | No installed UnitySkills skill docs were found; install them from the AI Config tab. |
| `SETTINGS_NONSTANDARD` | check, start | `tab_eval_problem_settings_nonstandard` | The dryRun policy ({0}) or token level ({1}) differs from the reference setup (off / maximum); results are less comparable. |
| `PACK_MODIFIED` | check, start | `tab_eval_problem_pack_modified` | Pack files differ from the manifest ({0}); this session is marked unofficial. |
| `PROJECT_USER` | check, start | `tab_eval_problem_project_user` | This is not a dedicated evaluation project; the report marks the environment as user-project. |
| `PACKAGE_ROOT_AMBIGUOUS` | 全部命令 | `tab_eval_problem_package_root_ambiguous` | Several cached copies of the package exist; using {0}. |
| `ISOLATION_UNVERIFIED` | next | `tab_eval_problem_isolation_unverified` | The agent session cannot be identified, so strict-mode isolation is not verified. |
| `RERUN` | next | `tab_eval_problem_rerun` | The previous attempt of this task was invalid for technical reasons; please do the task again. |
| `USAGE_UNAVAILABLE` | submit（面板）, usage | `tab_eval_problem_usage_unavailable` | Token usage could not be read ({0}); you can enter it in the Evaluation tab. |

## 4. 磁盘格式

### 4.1 目录

```
<project>/Library/UnitySkills/eval/                    # 状态目录（G6 禁止 Agent 读写）
  lock
  current.json
  ledger.jsonl
  runner.log                                            # 还没有会话时的日志
  sessions/<sid>/session.json
  sessions/<sid>/session-prompt.<lang>.md               # 面板复制用
  sessions/<sid>/report.json
  sessions/<sid>/report.md
  sessions/<sid>/runner.log
  sessions/<sid>/attempts/<aid>/state.json
  sessions/<sid>/attempts/<aid>/prompt.md               # 归档
  sessions/<sid>/attempts/<aid>/answer.json             # 提交时的答案副本
  sessions/<sid>/attempts/<aid>/journal.jsonl           # (cursor0, cursorEnd] 的原始 journal 条目
  sessions/<sid>/attempts/<aid>/telemetry.jsonl         # 窗口内的非 runner telemetry 行
  sessions/<sid>/attempts/<aid>/scenario.json           # dev：scenario 作业终态快照
  sessions/<sid>/attempts/<aid>/result.json
  panel/<cmdId>.json                                    # 面板写
  panel/<cmdId>.result.json                             # runner 写
  panel/<cmdId>.log
<project>/Library/UnitySkillsEvalIO/<sid>/              # IO 目录（Agent 可读写；会话结束时删除）
  session-prompt.md                                     # UTF-8 带 BOM
  <aid>/prompt.md                                       # UTF-8 带 BOM
  <aid>/answer.json                                     # Agent 写入
```

`runner.log` 行格式：`<iso> pid=<pid> cmd=<command> <LEVEL> <message>`；traceback 以 4 个空格缩进续行。不得记录答案内容与宿主日志内容。

### 4.2 写入纪律与不得存储的内容

- 所有 `.json` 原子写：同目录 `<name>.tmp-<pid>-<6hex>` → flush + fsync → `os.replace`。Windows 上 `os.replace` 遇 `PermissionError` 重试 5 次、间隔 100 ms（杀毒软件、读者句柄）。
- C# 读取：`new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)`；`IOException` 或 JSON 解析失败重试 3 次、间隔 50 ms。面板不得写 runner 状态文件，只写 `panel/<cmdId>.json`、专用工程标识与 EditorPrefs。
- JSONL（ledger、runner.log、journal 与 telemetry 副本）：一次 `write` 写完整一行；读方忽略末尾不完整的行。ledger 只追加，从不改写。
- 读到 `schema` 缺失或不是 int → `STATE_CORRUPT`；大于 1 → `SCHEMA_UNSUPPORTED`。
- **不得存储**（任何 runner 文件、日志、IO 目录）：角色标签（decoy/target/expected 一类，lint 黑名单见 §5.2）、期望集合与期望值、参考答案、私有 verify 源码及其中间推导结果（`result.json` 中 verify 自己写下的 `expected/actual/hint` 除外，那是该题结束后的诊断）、宿主日志的内容字段（只存数字与白名单字段，§8.4）、私有包的下载内容。只读发现题的 C0 只存摘要（§5.2）。

### 4.3 `current.json`

| 字段 | 类型 | 必填 | 含义 |
|---|---|---|---|
| `schema` | int (1) | Y | |
| `sessionId` | string | Y | 最近一次 start 建立的会话 |
| `status` | `active` \| `completed` \| `aborted` | Y | 与 `session.status` 同步 |
| `activeAttemptId` | string \| null | Y | 未终结的尝试 |
| `updatedAt` | iso | Y | |

### 4.4 `session.json`

| 字段 | 类型 | 必填 | 含义 |
|---|---|---|---|
| `schema` | int | Y | 1 |
| `sessionId` | string | Y | |
| `status` | `active` \| `completed` \| `aborted` | Y | |
| `createdAt` / `endedAt` | iso / iso \| null | Y | |
| `abortReason` | string \| null | Y | |
| `runnerVersion` | string | Y | `<pkg>/package.json` 的 version |
| `pluginVersion` | string | Y | start 时的 `/health.version`（`SkillsHttpServer.Health.cs:181`） |
| `pack` | object | Y | `{id, version, manifestSha256, source: "package"\|"pack-dir"\|"zip", official: bool, modified: [file], private: {kind: "url"\|"zip"\|"pack-dir"\|"private-dir", path: string\|null}}`；`source`：`package` = 公开题来自包、私有文件来自 URL 或 `--private-dir`，`zip` = 公开题来自包、私有文件来自 `--private-zip`，`pack-dir` = 两者都来自目录 |
| `project` | object | Y | `{path, dedicated: bool, markerSha256: string\|null, environment: "dedicated"\|"user-project"}` |
| `instance` | object | Y | `{id, port, pid}` |
| `selection` | object | Y | `{track, tier, tasks: [id], seedOverride: int\|null}` |
| `order` | [taskId] | Y | 出题顺序（= `selection.tasks`） |
| `mode` | `suite` \| `strict` | Y | |
| `lang` | `zh-CN` \| `en` | Y | |
| `launcher` | `panel` \| `agent` \| `batch` | Y | 执行 start 的一方 |
| `integrity` | `local-unverified` \| `batch-isolated` | Y | 只有 batch 是后者 |
| `declared` | object | Y | `{host, hostVersion, model, effort}`，每项为 `{value: string\|null, source: "detected"\|"user"\|"batch"\|"unknown"}`；host 取值 `claude-code\|codex\|cursor\|other` |
| `usageReading` | bool | Y | `--no-usage` 时为 false |
| `environment` | object | Y | `{unity, os, python, pipeline, pipelineName, defaultShader, colorProperty, mode, tokenLevel, surface, dryRunPolicy, summaryAutoTruncate, summaryPageSize, panelApprovalRequired, telemetryEnabled, skillDocs: [{path, agentId, version, treeHash}]}` |
| `tasks` | object | Y | `{<taskId>: {state: "pending"\|"issued"\|"done"\|"invalid"\|"cancelled", attempts: [attemptId], rerunsUsed: int}}` |
| `hostSessions` | object | Y | `{<hostSessionId>: [attemptId]}`，只存 id |
| `ioDir` | abs path | Y | `<project>/Library/UnitySkillsEvalIO/<sid>` |
| `report` | object \| null | Y | `{json, md, generatedAt}`（路径相对会话目录） |

`--declared FILE` 的格式：`{"schema":1,"host":{"value":"claude-code","source":"user"},"hostVersion":{"value":"…","source":"user"},"model":{…},"effort":{…}}`，缺项按 §8.1 探测。

### 4.5 `state.json`（每次尝试一份）

| 字段 | 类型 | 必填 | 含义 |
|---|---|---|---|
| `schema` | int | Y | 1 |
| `sessionId`、`attemptId`、`taskId` | string | Y | |
| `caseVersion`、`seed`、`k` | int | Y | k 从 1 起 |
| `phase` | `building` \| `ready` \| `submitted` \| `verifying` \| `pending_verification` \| `scored` \| `torn_down` | Y | |
| `status` | `pass` \| `partial` \| `fail` \| `invalid` \| `contaminated` \| `cancelled` \| null | Y | 终结前为 null |
| `reasons` | [string] | Y | |
| `rerunOf` | string \| null | Y | |
| `createdAt`、`issuedAt`、`submittedAt`、`scoredAt`、`tornDownAt` | iso \| null | Y | issuedAt = t0，submittedAt = t1 |
| `scope` | object | Y | `{runId, objectRoot, assetRoot, scenePath, rootInstanceId: int\|null, folderCreated: bool, sceneCreated: bool}` |
| `resting` | object | Y | `{scenes: [{path, isActive: bool, isLoaded: bool}]}` |
| `settings` | object | Y | `{collision: {"<a>,<b>": bool}}` |
| `handles` | object | Y | build 的返回值：只有身份与路径（§5.2） |
| `c0` | object | Y | `{data: object, digests: {<name>: sha256hex}}` |
| `fingerprint` | string \| null | Y | sha256 hex（§5.8） |
| `journal` | object | Y | `{cursor0: int\|null, cursorEnd: int\|null, refreshWindow: {from: int, to: int}\|null, forcedPlayModeExit: bool, complete: bool\|null, gaps: [[int, int]], segments: [fileName], conflicts: int, corruptLines: int}` |
| `telemetry` | object | Y | `{t0: iso\|null, t1: iso\|null}` |
| `http` | object | Y | `{received0: int\|null, received1: int\|null, runnerCalls: int}` |
| `env` | object | Y | `{atIssue: {<漂移键>: value}, atSubmit: {…}\|null}` |
| `host` | object | Y | `{kind: "claude-code"\|"codex"\|"none", sessionIds: [string], cwd: abs-path, isolation: "verified"\|"unverified"\|"n/a"}` |
| `prompt` | object | Y | `{lang, sha256, archive: relpath, ioFile: abs-path}` |
| `answer` | object | Y | `{path: abs-path, present: bool, sha256: string\|null, error: null\|"invalid_json"\|"not_object", schemaErrors: [string]}` |
| `submittedBy` | `agent` \| `panel` \| `driver` \| null | Y | |
| `reissues`、`duplicateSubmits` | int | Y | |
| `evalTreeHash` | object | Y | `{atIssue: hex\|null, atSubmit: hex\|null}` |
| `private` | object \| null | Y | `{kind, file, sha256, attempts: [{via, error}]}`，不含内容 |
| `timings` | object | Y | `{buildSeconds: number\|null, verifySeconds: number\|null}` |
| `teardown` | object \| null | Y | `{ok: bool, actions: [string], error: string\|null}` |

- `runnerCalls`：runner 在读 `received0` 之后、直到读 `received1`（含这一次）期间自己发出的 HTTP 请求数（任何 runner 命令在尝试 `ready` 期间发出的请求都累加进来）。`httpApprox = received1 − received0 − runnerCalls`；`totalReceived` 在 `SkillsHttpServer.Listener.cs:162,226` 递增，包含所有客户端与 `/health`，所以只是近似值。
- `host.cwd` 是 `next` 时 runner 进程的 cwd，只用于 Codex rollout 匹配（§8.3）。

### 4.6 `result.json`

| 字段 | 类型 | 必填 | 含义 |
|---|---|---|---|
| `schema` | int | Y | 1 |
| `sessionId`、`attemptId`、`taskId` | string | Y | |
| `caseVersion`、`seed` | int | Y | |
| `packId`、`packVersion` | string | Y | |
| `rerunOf` | string \| null | Y | |
| `status` | `pass` \| `partial` \| `fail` \| `invalid` \| `contaminated` \| `cancelled` | Y | |
| `reasons` | [string] | Y | |
| `score` | object \| null | Y | `{score: 0–10, completion: 0–100, strict: bool, criticalFailed: [id], assertions: [{id, req: [reqId], weight, critical, desc, pass, fraction, expected, actual, note, evidence, hint: string\|null}]}`；未验收（如构建期 cancelled）为 null |
| `requirementsFailed` | [reqId] | Y | 未通过断言的 req 并集，排序 |
| `declaredComplete` | bool \| null | Y | 取自答案 |
| `answer` | object | Y | `{present, sha256, error, schemaErrors}` |
| `submittedBy` | `agent` \| `panel` \| `driver` \| null | Y | |
| `timings` | object | Y | `{issuedAt, submittedAt, wall, fixture, verify, minutes, overBudget: bool}`（wall = t1 − t0） |
| `evidence` | object | Y | 见下 |
| `usage` | UsageRecord | Y | §4.7 |
| `verifier` | object \| null | Y | `{file, sha256, kind: "url"\|"zip"\|"pack-dir"\|"private-dir", via: string\|null, official: bool, deleted: bool}` |
| `integrity` | `local-unverified` \| `batch-isolated` | Y | |
| `isolation` | `verified` \| `unverified` \| `n/a` | Y | 只对 strict 有意义 |
| `verifiedLate` | bool | Y | 经 retry-verify 验收 |
| `teardown` | object | Y | `{ok, actions, error}` |

```ts
evidence = {
  journal:   { complete: bool, gaps: [[int, int]], segments: [string], records: int, nonRest: int,
               agents: [string], refreshWindow: { from: int, to: int } | null,
               forcedPlayModeExit: bool, directWrites: int },
  telemetry: { available: bool, files: [string], skillCalls: int, bySkill: { [skill]: int },
               errors: { [errorCode]: int }, agents: { [agent]: int }, modes: { [mode]: int } },
  httpApprox: int | null,
  freeze:    { inflightJobs: int },
  scenario:  { status: string, passed: bool | null, error: string | null, interrupted: bool,
               executionUncertain: bool, steps: [object], compilation: object | null } | null,
  drift:     { [key]: [before, after] },
  tampered:  [string]
}
```

`assertions[].hint` 在 S10 从私有模块的 `HINTS[id][lang]` 复制（只对未通过的断言），因为私有文件在验收后即删除。

### 4.7 Usage 记录（`UsageRecord`）

| 字段 | 类型 | 含义 |
|---|---|---|
| `host` | `claude-code` \| `codex` \| `other` \| `none` | |
| `source` | `claude-session-log` \| `codex-rollout` \| `stream` \| `proxy` \| `user` \| `none` | stream、proxy 只用于 batch |
| `sessionIds` | [string] | Claude 会话 id / Codex thread id |
| `window` | `{from: iso, to: iso}` | (t0, t1] |
| `model` | `{values: [string], source: "provider-returned"\|"host-requested"\|"user"\|null}` | |
| `effort` | `{values: [string], source: "host-reported"\|null}` | §11 #26 |
| `tokens` | `{input, output, cacheRead, cacheWrite, reasoning, processed}` \| null | 全是 int；`reasoning` 已含在 `output` 内，不计入 processed |
| `raw` | object \| null | 宿主原字段名的合计（§8.2、§8.3） |
| `messages` | int \| null | 计入的 assistant 响应数 |
| `subagents` | `{files: int, tokens: {…同 tokens}}` \| null | 已含在 `tokens` 内 |
| `parserVersion` | int | 1 |
| `unavailableReason` | string \| null | `disabled_by_user` \| `no_host_session` \| `session_log_not_found` \| `rollout_not_found` \| `ambiguous_rollouts` \| `counter_reset` \| `no_messages_in_window` \| `parse_error` |
| `errors` | [{file, line, error}] | 最多 20 条；file 只存文件名，error 只存异常类型名 |
| `computedAt` | iso | |

### 4.8 `ledger.jsonl`

只追加，永不改写或删除。两种行，读方以同一 attemptId 最新的 `usage` 行为准：

```json
{"schema":1,"kind":"attempt","recordedAt":"2026-10-17T03:10:00.000Z","sessionId":"s20261017T030000a1b2c3","attemptId":"0a1b2c3d4e5f","taskId":"S01","caseVersion":2,"packId":"unity-skills-eval","packVersion":"1.1.0","track":"ops","tier":"basic","seed":7001,"mode":"suite","lang":"zh-CN","launcher":"agent","status":"pass","reasons":[],"score":10.0,"completion":100.0,"strict":true,"rerunOf":null,"declared":{"host":"claude-code","model":"claude-opus-5-5","effort":"high"},"usage":{"source":"claude-session-log","processed":123456},"integrity":"local-unverified","official":true,"result":"sessions/s20261017T030000a1b2c3/attempts/0a1b2c3d4e5f/result.json"}
{"schema":1,"kind":"usage","recordedAt":"2026-10-17T03:30:00.000Z","attemptId":"0a1b2c3d4e5f","usage":{"source":"user","processed":120000}}
```

### 4.9 `report.json`（摘要；`report.md` 是同内容的渲染）

`{schema, sessionId, status, generatedAt, runnerVersion, pluginVersion, pack, selection, mode, lang, declared, environment, integrity, official, summary, tasks, notices}`：

```ts
ReportSummary = { tasks: int, completed: int, pass: int, partial: int, fail: int, invalid: int,
                  contaminated: int, cancelled: int, strictPassRate: number, meanCompletion: number,
                  wallSeconds: number,
                  usage: { processed: int | null, input: int | null, output: int | null, cacheRead: int | null,
                           cacheWrite: int | null, reasoning: int | null, sources: [string] } }
tasks[i] = { taskId, caseVersion, attemptId, status, completion: number | null, strict: bool | null,
             requirementsFailed: [reqId], failed: [{ id, req: [reqId], hint: string | null }],
             timings, usage, reasons }
```

HINTS 只出现在报告（与 result.json）里。

### 4.10 面板命令文件与结果文件

`panel/<cmdId>.json`（面板写：Process.Start 前写一次 `pid:null`，启动后重写 `pid`；消费结果后写 `ackAt`）：

| 字段 | 类型 | 含义 |
|---|---|---|
| `schema` | int | 1 |
| `cmdId` | string | 同文件名 |
| `command` | string | runner 命令名 |
| `argv` | [string] | 完整命令行（含 python 与 runner 路径） |
| `resultFile`、`logFile` | abs path | `panel/<cmdId>.result.json`、`panel/<cmdId>.log` |
| `startedAt` | iso | |
| `pid` | int \| null | |
| `ackAt` | iso \| null | 面板已消费结果的时间 |

`panel/<cmdId>.result.json`（runner 写）：`Envelope & {exitCode: int, finishedAt: iso, cmdId: string|null}`（§2.3）。面板保留最近 50 组，更早的由面板删除。

## 5. 题包格式

### 5.1 `pack/manifest.json`

```json
{
  "schema": 1,
  "packId": "unity-skills-eval",
  "packVersion": "1.1.0",
  "plugin": {"min": "3.0.0", "max": null},
  "tracks": {
    "ops": {"basic": ["S01", "S03", "S04", "S06", "S08", "D01"], "standard": [], "challenge": []},
    "dev": {"basic": [], "standard": [], "challenge": []}
  },
  "tasks": {
    "S01": {
      "track": "ops", "tier": "basic", "caseVersion": 2, "minutes": 10, "seeds": [7001],
      "requires": {"pipeline": "URP", "mode": "bypass", "dedicated": false, "packages": []},
      "public": {"task.py": "<sha256>", "prompt.zh-CN.md": "<sha256>", "prompt.en.md": "<sha256>"},
      "private": {"file": "S01.py", "sha256": "<sha256>", "bytes": 5123}
    }
  },
  "private": {
    "urlTemplates": [
      "https://raw.githubusercontent.com/Besty0728/Unity-Skills/{ref}/eval/pack/private/{file}",
      "https://cdn.jsdelivr.net/gh/Besty0728/Unity-Skills@{ref}/eval/pack/private/{file}"
    ],
    "refs": ["v{plugin}", "main", "beta"]
  }
}
```

| 字段 | 规则 |
|---|---|
| `packVersion` | semver；题目集合或任一 caseVersion 变化至少升 minor；只改 URL 等元数据升 patch |
| `plugin.min` / `plugin.max` | pluginVersion 不在 [min, max]（max 为 null 表示无上限）→ `PACK_PLUGIN_RANGE` |
| `tracks` | 每档为有序列表，即默认出题顺序；同一 ID 只出现一次 |
| `tasks.<ID>.track` / `tier` / `caseVersion` / `minutes` / `requires` | 必须与所在 tracks 位置及 task.py 的类属性一致（`pack.py` 加载时校验，不一致 → `PACK_INVALID`） |
| `minutes` | 建议用时（G8）与 `overBudget` 的阈值 |
| `seeds` | 公开种子，`seeds[0]` 为默认；保留种子不进公开 manifest（v1 公开种子为 `7001 + i*101`，`eval/tasks/__init__.py:4-5`） |
| `requires.pipeline` | `project_get_render_pipeline.pipelineType` 必须等于它 |
| `requires.mode` | `/health.currentMode` 必须等于它（wire 值 `approval\|auto\|bypass`，`SkillsModeManager.cs:601-602`） |
| `requires.dedicated` | true 时必须有专用工程标识（草案：standard、challenge 为 true，basic 为 false） |
| `requires.packages` | UPM 包名列表，必须出现在 `packages-lock.json.dependencies` |
| `public` | 相对 `pack/tasks/<ID>/` 的文件名 → 原始字节 sha256；不符 → notice `PACK_MODIFIED`，会话 `official=false` |
| `private.file` / `sha256` / `bytes` | 私有文件名（相对 `eval/pack/private/`）、原始字节 sha256、字节数（下载上限 = bytes + 1024） |
| `private.urlTemplates` | 只允许 `{ref}`、`{file}` 两个占位；runner 不得访问模板以外的主机 |
| `private.refs` | `{plugin}` 展开为 pluginVersion；按 refs × urlTemplates 的顺序尝试 |

`session.pack.manifestSha256` = manifest.json 原始字节的 sha256。

### 5.2 公开题模块 `pack/tasks/<ID>/task.py`

```python
from usk_eval.task_api import PublicTask


class S01(PublicTask):
    id = "S01"
    track = "ops"               # "ops" | "dev"
    tier = "basic"              # "basic" | "standard" | "challenge"
    case_version = 2            # int, must equal the manifest
    minutes = 10                # must equal the manifest
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": False, "packages": []}
    layer_pairs = ()            # physics layer pairs snapshotted and restored (v1 Task.layer_pairs)
    needs_focus = False         # build/teardown triggers compilation

    def build(self, ctx):           # required; writes only under R/A; returns handles (dict)
        ...

    def snapshot(self, ctx):        # required; returns {"data": {...}, "digests": {...}}
        ...

    def prompt_vars(self, ctx):     # required; returns {name: str|int|float}
        ...

    def answer_schema(self):        # required; §5.4
        ...

    def teardown(self, ctx):        # optional; restores settings outside the run folder; returns dict or None
        return None


TASK = S01()
```

规则：
- `build(ctx)`：随机性只来自 `ctx.rng`（每次构建新建 `random.Random(seed)`）；同一种子两次构建的 fingerprint 必须相同（selftest 门禁）；只能写 `ctx.R` 下的对象与 `ctx.A` 下的资产（`requires.dedicated` 的题可以改工程设置，但必须在 `teardown` 中恢复）；返回 handles。
- handles：JSON 可序列化；值只能是身份与路径（instanceId、资产或层级路径，以及它们的列表/字典）；键名不得匹配 `(?i)expected|target|decoy|inactive|answer|correct|wrong|trap|truth|solution|reference|legacy`（lint 强制 + 人工复核；v1 S01 的 `legacyPath` 就是靠键名暴露了诱饵，`eval/tasks/s01.py:47-50`）。
- `snapshot(ctx)` → C0：写入题只存"不应变化"的部分（对照物、共享材质、工程设置）；只读发现题（D01、S08 一类）不得存被盘点的子树，只存 `digests[name] = canonical_digest(structure(...))`（§5.8），v1 落盘的 `structureC0/expectedPaths`（`eval/tasks/s08.py:94-97`）不再需要。
- `prompt_vars(ctx)`：不得覆盖内置占位符名（§6.3）；值原样插入，不做二次格式化。
- docstring 与注释只写事实，不得写陷阱或判分方式（v1 的 docstring 都列了陷阱，如 `eval/tasks/s01.py:1-5`、`eval/tasks/s08.py:1-7`）。
- 公开模块只能 import `usk_eval.task_api`、`usk_eval.verifylib` 与标准库；不得 import 私有模块或 solutions。`pack.py` 以模块名 `usk_eval_task_<ID>` 经 `importlib.util.spec_from_file_location` 加载。

### 5.3 提示文件 `prompt.<lang>.md`

```text
<!-- usk-eval prompt; task S01; lang zh-CN -->
R1. 在 `{props}` 下新建名为 `Crate` 的立方体。
R2. Crate 的世界坐标为 (0, 0.5, 0)，旋转为 0，本地缩放为 1。
R3. 新建材质 `CrateMaterial`，使用本工程渲染管线的默认 Lit 着色器，保存为 `{mat}`。
   以 3 个空格缩进的续行属于上一条需求。
R4. 把该材质赋给 Crate；不要修改其他对象和资产。
```

- UTF-8 无 BOM、LF。可选的首行 `<!-- … -->` 注释在渲染时去掉。每条需求以 `R<n>. ` 开头（n 从 1 连续递增）；不以 `R<n>. ` 开头的非空行是上一条的续行；不得有标题或其他段落。
- 占位符遵循 `str.format_map`：`{name}`；字面花括号写 `{{` / `}}`（例如正则 `[0-9]{{2}}`，或把正则作为占位符传入）。
- 两种语言的 R 编号集合、数字字面量、占位符集合必须一致（lint 规则 2）。
- 渲染：去注释行 → `format_map(prompt_vars ∪ 内置占位符)` → 作为前缀的 `{requirements}`。

### 5.4 答案 schema

`answer_schema()` 返回 JSON Schema 子集：

```python
{
    "type": "object",
    "required": ["objectPath", "objectInstanceId", "materialPath"],
    "properties": {
        "objectPath": {"type": "string", "desc": {"zh-CN": "Crate 的完整层级路径", "en": "full hierarchy path of Crate"}},
        "objectInstanceId": {"type": "integer"},
        "materialPath": {"type": "string", "desc": {"zh-CN": "材质资产路径", "en": "material asset path"}},
    },
}
```

- 支持的 `type`：`object`、`array`、`string`、`integer`、`number`、`boolean`；`object` 用 `properties` 与 `required`，`array` 用 `items`；`desc` 是双语字典，可省略。
- runner 自动追加必填的 `declaredComplete: boolean`（G8）。
- `{schema}` 渲染为单行类 JSON：标量写 `"<string: 描述>"`、`<integer>`、`<number>`、`<boolean>`（类型词固定英文，描述取会话语言）；数组写 `[<item>, ...]`；对象按 properties 的顺序递归。例：`{"objectPath": "<string: Crate 的完整层级路径>", "objectInstanceId": <integer>, "materialPath": "<string: 材质资产路径>", "declaredComplete": <boolean>}`。
- runner 只做非阻断校验（顶层是对象、必填键存在、类型正确；`integer` 不接受 bool 与小数），错误写 `answer.schemaErrors`（≤ 20 条）；评分完全由 verify 决定。

### 5.5 私有模块 `eval/pack/private/<ID>.py`

```python
from usk_eval import verifylib as V

ASSERTIONS = [
    # (id, weight, critical, [reqIds], desc); weights sum to 10; reqIds name R<n> or G<n> and are never empty
    ("A1", 1.5, False, ["R1"], "exactly one new Crate under the given parent with the expected components"),
    ("A8", 1.0, True, ["G1", "G2"], "no side effects outside R/A and no other scene saved"),
]


def verify(ctx, card):          # required; static checks with the read-only Probe and frozen evidence
    ...


def verify_runtime(ctx, card):  # required when SCENARIO is defined; reads ctx.scenario
    ...


def allowed_change(ctx, rec):   # optional; lets scope and non-REST checks accept specific records
    return False


SCENARIO = None                 # optional (dev): dict, or callable(ctx) -> dict
HINTS = {"A1": {"zh-CN": "…", "en": "…"}}   # optional; only shown in the final report
```

- verify 不得写 Editor：`VerifyContext` 不提供写客户端；Probe 的 `read` 拒绝非只读技能（沿用 v1 `rest.py:114-126`，依据 `/skills/schema?wire=v2` 的 flags）。verify 抛异常 → `invalid`（`verifier_error`）。没有被评估的断言由 `card.finish()` 记为 unobservable 失败（v1 `verify.py:43-50`）。
- `SCENARIO` 结构 `{"steps": [...], "executionMode": "play"|"edit", "timeoutSeconds": int}`，约束与插件一致（`RuntimeScenarioService.cs:49-58,81-110`）：1–100 步；kind ∈ `invoke/read/assert/wait_frames/wait_seconds/wait_condition`；至少一个 `assert` 或 `wait_condition`；`invoke` 只能用 `component_invoke_method`；`read/assert/wait_condition` 必须是只读技能；`timeoutSeconds` 1–600；同时只能有一个活动 scenario；编译中拒绝启动。runner 把 `steps` 序列化成 JSON 字符串传给 `editor_run_scenario`（参数类型是 `string steps`，`EditorSkills.cs:25`）。
- `ctx.scenario` 取自终态作业（`GET /jobs/{id}` 在终态时附 `resultData`，`SkillsHttpServer.Jobs.cs:144-193`）：`{status, passed, error, interrupted, executionUncertain, steps: [{index, kind, skill, expected, actual, tolerance, passed}], compilation, logWindow}`（字段来源 `RuntimeScenarioService.cs:233-284`）。`passed:true` 已经包含"日志完整、没有 Error/Exception/Assert 日志、不在编译"（`:267-284`）。
- 私有模块只能 import `usk_eval.verifylib` 与标准库；不得 import solutions、写文件或联网。

### 5.6 Solutions 模块 `eval/pack/solutions/<ID>.py`

```python
from usk_eval.task_api import Negative


def reference(ctx):             # returns the answer dict; must strict-pass
    ...


NEGATIVES = [Negative("local_coords", local_coords, {"A2"}, note="")]   # >= 5; expect_failed is the exact failed set
ALTERNATIVES = [("root_then_move", root_then_move)]                      # >= 1; each must strict-pass
```

- 求解器在 `SolverContext` 中运行（写客户端身份 `SelftestSolver`，§7.3）；因为 handles 不再带角色标签，可通过 `ctx.private` 复用私有模块的推导函数。
- 门禁判据（沿用 `eval/cli.py:80-128`，数量门槛 `:89-90`）：reference ×2（同一种子）strict pass 且 fingerprint 相同；每个 negative 的 status ∈ {partial, fail} 且失败集合等于 expect_failed；每个 alternative strict pass；任何 case 出现 invalid 或 contaminated 都算门禁失败。

### 5.7 ctx 字段

| 字段 | Build（build / snapshot / prompt_vars / teardown） | Verify（verify / verify_runtime / allowed_change） | Solver |
|---|---|---|---|
| `c` / `client` | 读写客户端（`UnitySkillsEval`） | 无（None） | 读写客户端（`SelftestSolver`） |
| `read(skill, **args)`、`probe` | ✓ | ✓（只读） | ✓ |
| `seed` / `rng` | ✓ / ✓ | ✓ / — | ✓ / — |
| `scope`、`R`、`A`、`scene`、`path(*parts)`、`asset(*parts)` | ✓ | ✓ | ✓ |
| `project_path`、`env`、`lang`、`track`、`tier`、`task` | ✓ | ✓（env 为出题时的快照） | ✓ |
| `handles` | build 返回后可用 | ✓ | ✓ |
| `c0`（即 `c0.data`）、`c0_digests` | — | ✓ | — |
| `answer`、`answer_error`、`answer_schema_errors` | — | ✓ | — |
| `records`、`journal_complete`、`journal`（cursor0、cursorEnd、refreshWindow、forcedPlayModeExit） | — | ✓ | — |
| `violations(extra=None)`、`non_rest()`、`direct_writes()`、`write_records()`、`saved_scenes()`、`saved_other_scenes()` | — | ✓ | — |
| `telemetry`（窗口汇总，同 `evidence.telemetry`） | — | ✓ | — |
| `scenario` | — | 只在 verify_runtime | — |
| `private` | — | — | ✓ |
| `wait_idle()`、`wait_job(jobId)` | ✓ | — | ✓ |
| `log(msg)` | ✓ | ✓ | ✓ |

- `records` 每条 = v1 `flatten` 的结果（`eval/harness/journal.py:70-104`）加上 `agent`、`isPlaying`、`tsUtc`、`origin`（seq ∈ refreshWindow 为 `"refresh"`，否则 `"attempt"`）。
- `violations()`：`scope.scope_violations(records, R, A, scene, allowed)`；先剔除 `isPlaying` 记录，以及 agent 为 runner 且 origin 为 attempt 的记录（runner 自己的 `editor_stop` 等）；包含身份表补丁（`eval/design/scope-verifier-fix`，它依赖 §7.1 新增的身份字段）。
- `direct_writes()`：A 下的文件记录中 `origin == "refresh"` 或 `source == "editor"` 的那些，即 Agent 绕过技能直接写盘的证据（供 ops 的 G4 断言）。
- `write_records()`：除下列之外的全部记录：run 场景的 `scene_saved`、run 场景文件（`.unity`、`.unity.meta`）的导入、`isPlaying` 记录、runner 自身记录。只读题的"零写入"断言用它（草案 3.1：只读题忽略本轮场景的 scene_saved 与场景文件导入）。
- `saved_scenes()`：全部 `scene_saved`；`saved_other_scenes()`：path 不等于 run 场景的那些（G2）。

### 5.8 verifylib 公开 API

`from usk_eval.verifylib import ...`：

| 名称 | 来源 | 说明 |
|---|---|---|
| `Scorecard(spec)` | v1 `verify.py:19-75` | spec 为 5 元组；方法 `check(aid, passed, expected=None, actual=None, note=None, **evidence)`、`fraction(aid, value, …)`、`unobservable(aid, reason)`、`finish()`、`failed()`、`requirements_failed()`（新）、`as_dict()`（每行加 `req`）；属性 `score`、`strict`、`critical_failed`；权重合计必须为 10 |
| `Probe(client)` | v1 `verify.py:138-210` | `go, go_path, children, components, component_count, ser, owner, material, asset, guid, deps, so, query`；只读 |
| `floats, vec_close, quat_from_euler, quat_angle_deg, quat_close, parse_ref, REF_RE` | v1 `verify.py:15-132` | |
| `go_state, material_state, material_color, close_tuple, same, new_children, local_trs, identity_rotation, answer, as_int, norm_path, world_from_local` | v1 `checks.py:12-121` | |
| `uyaml`（`load, loads, by_type, index, meta_guid, material_colors, scalar, Doc`） | v1 `uyaml.py` | 子模块 |
| `canonical_digest(obj)` | 新 | `sha256(json.dumps(obj, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")).hexdigest()` |
| `structure(probe, root_iids)` | 新（泛化 v1 `s08.py:99-114`） | 子树规范结构，深度优先：`{id, name, active, tag, layer, path, components: [[type, enabled]], children: [id]}` |
| `hierarchy_signature(probe, root_iid, limit=8)`、`asset_listing(project_path, asset_root)` | v1 `task.py:138-159` | |
| `fingerprint(ctx)` | v1 `task.py:131-135`、`runner.py:138` | 先把 runId 替换成 `<RUN>`，再 `canonical_digest({"hierarchy": …, "assets": …})` |
| `scope_violations, adopted_paths, under` | `usk_eval.scope` 重导出 | |

### 5.9 私有 verify 的获取

| 方式 | 来源 | hash 不符时 | official |
|---|---|---|---|
| 默认（URL） | 按 `refs`（展开后）× `urlTemplates` 依次 GET；超时 15 s，跟随重定向，读取上限 bytes + 1024，User-Agent 同 §7.3 | 换下一个 URL | true |
| `--private-zip ZIP` | zip 根目录下名为 `private.file` 的条目（Release 资产） | 视为取不到 | true |
| `--private-dir DIR` | `DIR/<file>`（维护者 selftest 用 `eval/pack/private`） | 允许 | 不符时 false |
| `--pack-dir DIR` | `DIR/manifest.json`、`DIR/tasks/<ID>/…`、`DIR/private/<file>`（保留题、开发） | 允许 | false |

- 加载：内容写到 `tempfile.mkdtemp(prefix="usk-eval-")/<12hex>.py`（工程之外），模块名 `usk_eval_private_<aid>_<6hex>`，用 `importlib.util.spec_from_file_location` 加载；`finally` 中 `shutil.rmtree` 并从 `sys.modules` 删除。runner 每次启动都清理 `tempfile.gettempdir()` 下修改时间超过 1 h 的 `usk-eval-*`。
- 全部来源失败 → `pending_verification`；`state.private.attempts` 记 `{via, error}`（如 `"http 404"`、`"timeout"`、`"hash_mismatch"`），不记内容。
- `check` / `start` 的可达性探测只测所选第一题：完整下载并核 hash，然后丢弃。
- 局限（写进报告）：私有文件在 GitHub 公开，只能防随手发现；官方横向比较用从不发布的保留题（`--pack-dir`）。

## 6. 提示

### 6.1 前缀 v2（zh-CN，逐字）

```text
你正在参加 UnitySkills 评测（会话 {session}，第 {k}/{n} 题，赛道 {track}，难度 {tier}）。工程 {project}（Unity {unity}，{pipeline}）已在 Unity Editor 中打开；UnitySkills REST 服务 http://127.0.0.1:{port}，instanceId {instance}。
通用规则：
G1 只允许修改场景对象根 `{R}`（下称 R）下的对象和资产根 `{A}`（下称 A）下的资产，题目另有明确要求的除外。
G2 可以保存本轮场景 `{scene}`；不要打开、切换、重新加载、新建、另存或保存其他任何场景。
G3 不要修改操作模式、档位、dryRun 策略、已安装的包或工程设置，题目另有明确要求的除外。
G4 [ops] 场景和资产的修改只能通过 UnitySkills 技能完成：不要直接写 .unity/.prefab/.mat/.asset/.meta 文件，不要新建或修改 C# 脚本。
   [dev] 可以在 A/Scripts 下新建或修改 C# 脚本；其他场景和资产修改只能通过 UnitySkills 技能完成。
G5 每个请求都锁定实例 {instance}（X-Expect-Instance 头或 unity_skills.py 的 target）；不要向其他端口发请求。
G6 不要读取或修改评测程序、它的状态目录 Library/UnitySkills/eval、验收文件或其他报告，不要尝试获取参考答案。
G7 遇到权限限制、缺少依赖或编译重载，按 UnitySkills 文档的正常协议处理，不得绕过。
G8 建议用时 {minutes} 分钟。完成（或确认无法完成）后，按下方格式把答案写成 JSON 文件，然后只运行一次：
   {submit_cmd}
   提交只触发程序验收，不返回分数，请不要自行评分；无法完成时把 declaredComplete 写为 false 并照常提交。
任务：
{requirements}
答案格式（JSON）：{schema}
```

### 6.2 前缀 v2（en，逐字）

```text
You are taking a UnitySkills evaluation (session {session}, task {k}/{n}, track {track}, tier {tier}). Project {project} (Unity {unity}, {pipeline}) is open in the Unity Editor; UnitySkills REST server http://127.0.0.1:{port}, instanceId {instance}.
General rules:
G1 Modify only objects under the scene root `{R}` (R) and assets under `{A}` (A), unless the task explicitly says otherwise.
G2 You may save this run's scene `{scene}`. Do not open, switch, reload, create, save-as or save any other scene.
G3 Do not change the operating mode, token tier, dryRun policy, installed packages or project settings unless the task explicitly says so.
G4 [ops] Make scene and asset changes only through UnitySkills skills: do not write .unity/.prefab/.mat/.asset/.meta files directly; do not create or edit C# scripts.
   [dev] You may create or edit C# scripts under A/Scripts; make every other scene and asset change through UnitySkills skills.
G5 Pin every request to instance {instance} (X-Expect-Instance header or unity_skills.py target); send nothing to other ports.
G6 Do not read or modify the evaluation runner, its state directory Library/UnitySkills/eval, verification files or other reports; do not try to obtain reference solutions.
G7 Handle permission limits, missing dependencies and compile reloads through the normal UnitySkills protocol; never bypass them.
G8 Suggested time: {minutes} minutes. When done (or sure you cannot finish), write the answer as a JSON file in the format below, then run exactly once:
   {submit_cmd}
   Submitting only triggers program verification and returns no score; do not grade yourself. If you cannot finish, set declaredComplete to false and submit anyway.
Task:
{requirements}
Answer format (JSON): {schema}
```

### 6.3 渲染规则

- 每条断言的 req 都必须指向 G 或 R（lint 规则 1、3）。G2 取代 v1 只禁止切换/重载/另存的规则（`eval/harness/task.py:17`）：v1 的 14 题对任何 `scene_saved` 都扣分；现在保存本轮场景是允许的，其他场景的保存仍由 scope 判越界（`eval/harness/journal.py:141-143`）。
- G4 只渲染本题赛道的那一行，去掉 `[ops] ` / `[dev] ` 标记与续行缩进，行首保持 `G4 `。即 zh-CN ops 渲染为 `G4 场景和资产的修改只能通过 UnitySkills 技能完成：不要直接写 .unity/.prefab/.mat/.asset/.meta 文件，不要新建或修改 C# 脚本。`，zh-CN dev 渲染为 `G4 可以在 A/Scripts 下新建或修改 C# 脚本；其他场景和资产修改只能通过 UnitySkills 技能完成。`；en 同理。
- 语言取会话 `lang`；面板为 ru 时用 en。
- 渲染方式：模板用 `str.format_map(vars)` 一次填完；`{requirements}`、`{schema}`、`{submit_cmd}` 的值是已渲染好的文本，不会被再次格式化，其中的花括号安全。

| 内置占位符 | 值 |
|---|---|
| `{session}` | sessionId |
| `{k}` / `{n}` | 本题序号（从 1 起）/ 总题数 |
| `{track}` / `{tier}` | `ops`、`dev` / `basic`、`standard`、`challenge` |
| `{project}` | 工程根绝对路径（原生格式） |
| `{unity}` | `/health.unityVersion` |
| `{pipeline}` | `pipelineType`（如 `URP`） |
| `{port}` / `{instance}` | 实例端口 / instanceId |
| `{R}` / `{A}` / `{scene}` | objectRoot / assetRoot / run 场景路径 |
| `{minutes}` | manifest 的 minutes |
| `{submit_cmd}` | §2.5 的 submit 行 |
| `{requirements}` | 渲染后的需求行，以 `\n` 连接 |
| `{schema}` | §5.4 的渲染结果 |

### 6.4 会话提示

会话提示占位符：`{session}`、`{n}`、`{track}`、`{tier}`、`{next_cmd}`（§2.5 的 next 行）。整套模式与逐题模式各一份。

zh-CN，整套（suite）：

```text
这是一次 UnitySkills 评测（会话 {session}，整套模式，共 {n} 题，赛道 {track}，难度 {tier}）。请严格按以下流程进行：
1. 运行：{next_cmd}
2. 如果返回的 data.done 为 true：告诉用户评测已完成，请到 Unity 中 UnitySkills 窗口的“评测”页查看报告，然后结束。
3. 否则读取 data.promptFile（UTF-8 文本），按其中的通用规则和任务完成本题，并按其中的提交命令提交一次。
4. 提交后回到第 1 步。不要回头修改已经提交的题。
任何命令返回的 problems 不为空时，把每一条的 fix 原样转告用户，然后停止。
```

zh-CN，逐题（strict）：

```text
这是一次 UnitySkills 评测（会话 {session}，逐题模式，共 {n} 题，赛道 {track}，难度 {tier}）。本会话只做一题：
1. 运行：{next_cmd}
2. 如果返回的 data.done 为 true：告诉用户评测已完成，请到 Unity 中 UnitySkills 窗口的“评测”页查看报告，然后结束。
3. 否则读取 data.promptFile（UTF-8 文本），按其中的通用规则和任务完成本题，并按其中的提交命令提交一次。
4. 提交后停止，并提醒用户：新开一个会话，粘贴同一段提示，继续下一题。
任何命令返回的 problems 不为空时，把每一条的 fix 原样转告用户，然后停止。
```

en，suite：

```text
This is a UnitySkills evaluation (session {session}, suite mode, {n} tasks, track {track}, tier {tier}). Follow this procedure exactly:
1. Run: {next_cmd}
2. If the returned data.done is true, tell the user that the evaluation is finished and that the report is on the "Evaluation" tab of the UnitySkills window in Unity, then stop.
3. Otherwise read data.promptFile (UTF-8 text), complete the task under its general rules, and submit once with the submit command it gives.
4. After submitting, go back to step 1. Do not revisit tasks you have already submitted.
If any command returns a non-empty problems list, pass each fix on to the user verbatim, then stop.
```

en，strict：

```text
This is a UnitySkills evaluation (session {session}, strict mode, {n} tasks, track {track}, tier {tier}). Do exactly one task in this session:
1. Run: {next_cmd}
2. If the returned data.done is true, tell the user that the evaluation is finished and that the report is on the "Evaluation" tab of the UnitySkills window in Unity, then stop.
3. Otherwise read data.promptFile (UTF-8 text), complete the task under its general rules, and submit once with the submit command it gives.
4. After submitting, stop and remind the user to open a new session and paste this same prompt to continue with the next task.
If any command returns a non-empty problems list, pass each fix on to the user verbatim, then stop.
```

- 面板 tab 标题 `tab_evaluation` 的 zh-CN 值必须是"评测"、en 值是"Evaluation"，与会话提示一致（WP10）。

### 6.5 提交命令与"会话中不回分数"

- G8 的 `{submit_cmd}` 就是 `next` 返回的 `submitCommand`：一题一条，带该题的 `--attempt` 与 `--answer <answerFile>`。Agent 若把答案写到别处，只要 `--answer` 指向实际文件即可。
- 会话进行中，`next`、`submit`、`status` 都不返回分数、status、断言或任何验收结论；分数只在会话结束后的 report 与面板中出现。原因：整套模式下前一题的反馈会影响后面的题（对应 v0.3 §9.1）。

## 7. 插件侧契约

### 7.1 Journal（`EditorChangeTrackerService.cs`：只新增字段与轮转，`ReadChanges` 语义不变）

现状：内存 ring 500 条（`:22`）；`Publish` 每条追加一行（`:619-638`，`:692`），每溢出 100 次就把文件重写为最新 500 条（`:29,631-637,686-688`）；`EnsureLoaded` 只读一个文件（`:640-675`）；路径 `Library/UnitySkills/editor_changes.jsonl`（`:30`），测试用 `OverrideLogPathForTests`（`:59,701-703`）。

**轮转**

| 项 | 契约 |
|---|---|
| 活动段 | `Library/UnitySkills/editor_changes.jsonl`（路径不变） |
| 轮转段 | 同目录 `editor_changes.1.jsonl` … `editor_changes.10.jsonl`（1 最新），命名方式同 telemetry（`SkillTelemetryService.cs:527-531`）；测试路径下为 `<stem>.<n><ext>` |
| 触发 | 每次追加后，活动段行数 ≥ 2000，或字节数 ≥ 8 MiB |
| 步骤 | ① 删除 `.10`（如有）；② i = 9 … 1：`.i` 存在就 `File.Move(.i, .(i+1))`；③ `File.Move(active, .1)`。任一步失败立即停止，继续往活动段追加，下次追加后重试；不得覆盖已有文件 |
| 压缩 | 删除 `CompactEveryOverflowEntries` 路径：磁盘从不改写 |
| 内存 ring / `ReadChanges` | 不变：`retention: 500`，`oldestSeq`、`dropped`、`truncated` 仍按 ring 计算（`:95-136`） |
| `EnsureLoaded` | 读活动段；不足 500 条时从 `.1`（仍不足再 `.2`…）补到 500；`_seq` = 活动段与最新一个存在的轮转段中的最大 seq；同时记下活动段行数 |
| 容量 | 最多 11 个文件 |

**读方（runner `journal.py`）如何证明 seq 连续**

1. 依次读活动段、`.1`、`.2` … `.10`（从新到旧。读的过程中发生轮转只会让同一段被读两次，不会漏读）；一旦已读到 seq ≤ cursor0 + 1 的条目，就不再读更旧的段。
2. 逐行解析：空行跳过；活动段最后一行解析失败视为正在写入，忽略；其他行解析失败计入 `corruptLines`。
3. 取 seq ∈ (cursor0, cursorEnd] 的条目，按 seq 去重；同一 seq 的两份内容（规范化 JSON 后）不同 → `conflicts += 1`。
4. (cursor0, cursorEnd] 内每个整数都存在且 `conflicts == 0` 才算 complete。不完整时等 0.5 s 重读整套，最多 3 次；仍不完整 → `complete:false`，`gaps` 记缺失区间。
5. 按 seq 升序写 `attempts/<aid>/journal.jsonl`，`segments` 记读过的文件名。

**新字段（精确 JSON 名）**

| 位置 | 字段 | 类型 | 语义 |
|---|---|---|---|
| 条目顶层（`seq, type, source` 之后、`tsUtc` 之前） | `agent` | string \| null | `source == "rest"`：REST 调用方的 agent id；同一个 REST 深度窗口内出现多个不同调用方时为 `"mixed"`；没有请求上下文时为 `"unknown"`。`source == "editor"`：null |
| 条目顶层 | `isPlaying` | bool | 发布时的 `EditorApplication.isPlaying` |
| `file_changes.payload` | `omitted` | int | 因 200 条上限（`:707-711`）丢掉的路径数（三类合计） |
| `file_changes.payload` | `importedTotal`、`deletedTotal`、`movedTotal` | int | 截断前、去重后的数量；`count` 保持截断后的语义（`:152-158`） |
| `gameobject_reparented` | `entityId`、`instanceId`、`scenePath` | string、int、string | 取值方式同 `AddGameObjectIdentity`（`:610-617`） |
| `gameobject_destroyed`（catalog 已知） | `entityId`、`instanceId` | string、int | `CatalogEntry` 增加 `InstanceId`，在 `UpsertCatalog`（`:547-560`）时记录 |
| `gameobject_destroyed`（catalog 未知，`:445`） | `instanceId` | int | 6000.4 以下为 `int.Parse(entityId)`，6000.4+ 为 0 |

- `entityId` 的格式：6000.4 以下是 instanceId 的十进制字符串（可为负数），6000.4+ 是 EntityId 的 ulong 十进制字符串；`instanceId` 在 6000.4+ 恒为 0（`UnityObjectIdUtility.cs:12-48`）。身份比较一律用 `entityId`。
- `agent` 的取值：`BeginRestExecution`（由 `SkillRouter.Execute.cs:52` 调用，处在 `SkillsHttpServer.Dispatch.cs:358` 与 `SkillsHttpServer.Batch.cs:287` 的 `BeginRequestContext` 范围内）读 `SkillsAuditLog.CaptureRequestContext()`（`SkillsAuditLog.cs:77`）。`Active` 时：`AgentIdIsExplicit` 为真用 `FallbackAgentId`；否则 `ClientProcessResolver.TryGetAgentId(RemotePort)` 命中就用它，不命中用 `FallbackAgentId`。不 `Active` 记 `"unknown"`。REST 深度从 0 变为 1 时设定窗口 agent；深度 > 0 时遇到不同的值改为 `"mixed"`；`EndRestExecution` 延迟一帧递减（`:83-93`），深度回到 0 时清空。
- 防抖属性：`PendingPropertyChange`（`:41-45`）增加捕获时的 `Agent` 与 `IsPlaying`；`FlushPendingProperties`（`:374-396`）改为按 (Source, Agent, IsPlaying) 分组发布，条目取组的值。否则防抖延迟到 REST 深度归零之后才发布，归属会丢。
- 另在 `EditorApplication.quitting` 时调用 `FlushPendingProperties`（现在只挂了 `beforeAssemblyReload`，`:72`）。
- 文档：`skills/editor/SKILL.md:36,138-139` 写明磁盘历史（最多 11 段）与新字段；`retention` 仍指内存窗口。

示例：

```json
{"seq":1042,"type":"scene_changes","source":"rest","agent":"ClaudeCode","isPlaying":false,"tsUtc":"2026-10-17T03:04:05.1234567Z","payload":{"count":1,"omitted":0,"changes":[{"kind":"gameobject_reparented","name":"Crate","fromPath":"Crate","toPath":"EV_r20261017T030000a1b2c3/Props/Crate","newParentPath":"EV_r20261017T030000a1b2c3/Props","newParentEntityId":"-1234","entityId":"-5678","instanceId":-5678,"scenePath":"Assets/__UnitySkillsEval/r20261017T030000a1b2c3/Eval_r20261017T030000a1b2c3.unity"}]}}
{"seq":1043,"type":"file_changes","source":"editor","agent":null,"isPlaying":false,"tsUtc":"2026-10-17T03:04:06.0000000Z","payload":{"imported":["Assets/__UnitySkillsEval/r20261017T030000a1b2c3/Scripts/Door.cs"],"deleted":[],"moved":[],"count":1,"omitted":0,"importedTotal":1,"deletedTotal":0,"movedTotal":0}}
```

### 7.2 Telemetry 与 Audit 的 flush

- `SkillTelemetryService` 与 `SkillsAuditLog` 各加一个方法（不写日志，遵守启动策略）：

```csharp
[InitializeOnLoadMethod]
private static void RegisterLifecycleFlush()
{
    AssemblyReloadEvents.beforeAssemblyReload += FlushSync;
    EditorApplication.quitting += FlushSync;
}
```

  `FlushSync` 已存在（`SkillTelemetryService.cs:240`、`SkillsAuditLog.cs:286-289`）；`SkillsAuditLog.cs` 需加 `using UnityEditor;`。现状只有 200 ms 延迟 flush（`SkillTelemetryService.cs:44,443-459`；`SkillsAuditLog.cs:40,350-366`），domain reload 与退出时可能丢记录。
- 文件格式不变。runner 读 `Library/UnitySkillsTelemetry.jsonl` 与 `UnitySkillsTelemetry.{1..3}.jsonl`（1 MB 轮转，`SkillTelemetryService.cs:35-37,499-531`）；行字段 `ts, skill, agent, mode, ok, errorCode`（仅 `ok=false` 时出现）`, ms`（`:416-441`）；`mode` ∈ `execute, dryRun, plan`（`SkillsHttpServer.Dispatch.cs:522-524`）与 `batch_step, batch_step_dryRun`（`:548`）。Audit 文件 `Library/UnitySkillsAudit.jsonl` 与 `.1–.3`（`SkillsAuditLog.cs:16-19,34-36`）v3.0 runner 不读。
- `/analytics` 有 30 s 缓存（`SkillTelemetryService.cs:70-73,184-188`）：runner 只用它读 `telemetryEnabled`，计数一律读文件。

### 7.3 流量身份

| 流量 | `X-Agent-Id` | `User-Agent` |
|---|---|---|
| runner（check / start / next / submit / abort / cleanup 等，含私有包下载） | `UnitySkillsEval` | `unity-skills-eval/<runnerVersion>` |
| selftest 求解器、E2E 模拟 Agent | `SelftestSolver` | `unity-skills-eval-selftest/<runnerVersion>` |

- 两个值都不在 `DisplayNameMap`（`ClientProcessResolver.cs:815-849`），`CanonicalizeExplicitAgentId` 原样保留（`:866-872`）；带显式头时服务器跳过进程链解析（`SkillsHttpServer.Listener.cs:207-210`），telemetry 与 journal 的 `agent` 都取该值。
- runner 的每个请求还带 `X-Expect-Instance: <instanceId>`（同 v1 `rest.py:50-52`）。v1 的 `evaluator`（`rest.py:40`、`metrics.py:386`）不再使用。

### 7.4 专用工程标识

- 路径 `<project>/ProjectSettings/UnitySkillsEvaluation.json`，UTF-8 无 BOM，由面板在用户确认后写入：

```json
{"schema":1,"kind":"UnitySkillsEvaluationProject","createdAt":"2026-10-17T03:00:00.000Z","createdWith":"3.0.0"}
```

- runner 判定：文件可解析、`schema == 1` 且 `kind == "UnitySkillsEvaluationProject"` → `dedicated:true`；`markerSha256` = 文件原始字节的 sha256；`createdWith` 只作参考。basic 档在没有标识的工程上运行时 `environment:"user-project"`，并给 notice `PROJECT_USER`。

### 7.5 模式切换与条件恢复

- 模式是整机共享的 EditorPrefs：键 `UnitySkills_OperatingMode`，值为枚举名 `Approval/Auto/Bypass`（`SkillsModeManager.cs:75,163-181`；`SkillModeEnum.cs:5`），wire 值为小写（`:601-602`）。非 Bypass 下 NeverInSemi 技能被拦（`SkillRouter.Gates.cs:133-136`），而 fixture 清理（`asset_delete`）与 dev 赛道（编译、Play）都需要它们。runner 只检查，从不改模式。
- 面板"切到 Bypass（会话结束后恢复）"：当前不是 Bypass 时，先写 EditorPrefs `UnitySkills_EvalRestoreMode` = JSON 字符串 `{"schema":1,"previousMode":"auto","armedAt":"<iso>","projectPath":"<abs>","sessionId":null}`，再设 `SkillsModeManager.CurrentMode = Bypass`；已经是 Bypass 则不写键。
- 绑定：面板观察到本工程 `current.json` 出现 `createdAt ≥ armedAt` 的会话时，把它的 `sessionId` 写进键。
- 恢复条件（全部满足）：键存在；`projectPath` 等于本工程；`current.json` 没有 active 会话；键已绑定且该会话已 completed/aborted，或用户点了"立即恢复"。恢复动作：`CurrentMode == Bypass` 时设回 `previousMode` 并删键；否则（用户已手动改过）只删键。
- 检查时机：Evaluation tab controller 创建时、tab 激活时、轮询中观察到会话进入终态时、面板发起的 abort / report 完成时。不得用 `InitializeOnLoad` 扫描（启动策略）。键存在期间 tab 显示"模式将在会话结束后恢复"。

### 7.6 Evaluation tab ↔ runner

- 接线（WP10）：`MainTabId` 末尾加 `Evaluation`（`UI/UnitySkillsWindow.cs:33-40`），`MainTabs` 末尾加 `("tab-btn-evaluation", "tab-wrap-evaluation", "tab-content-evaluation", "tab-underline-evaluation", "tab_evaluation")`（`:63-70`），在 `:201-205` 之后创建 controller。
- Python：EditorPrefs `UnitySkills_EvalPythonPath` → `PythonLocator`（草案 2.6）。runner 路径：`PackageRootLocator.TryGet(out root)` + `Evaluation~/run_eval.py`。
- 启动 `EvaluationRunnerService.Launch(command, args)`：
  1. 生成 cmdId，原子写 `panel/<cmdId>.json`（`pid:null`）。
  2. `Process.Start`：`<python> -B <runner> <command> <args> --project <project> --launcher panel --result-file <panel/<cmdId>.result.json> --log-file <panel/<cmdId>.log> --cmd-id <cmdId>`；`UseShellExecute=false`，不重定向任何流，`CreateNoWindow=true`，环境变量加 `PYTHONUTF8=1`、`PYTHONIOENCODING=utf-8`、`PYTHONDONTWRITEBYTECODE=1`。
  3. 重写命令文件填入 `pid`。不等待进程：runner 会回调同一个 Editor 的 REST，而 REST 在主线程排队处理。
- 跟踪：tab 可见且存在 `ackAt == null` 的命令文件时，用 `EditorUiScheduler.RepeatSafe(element, 1000, body)`（`UI/EditorUiScheduler.cs:33`）每秒检查：结果文件存在 → 解析、刷新界面、写 `ackAt`；结果文件不存在且 pid 已退出（或该 pid 进程的 `StartTime` 比 `startedAt` 晚 5 s 以上，说明 pid 被复用）超过 2 s → 判 `crashed`，显示 `tab_eval_problem_runner_crashed` 与日志路径。
- reload 之后：controller 创建时扫描 `panel/*.json` 中 `ackAt == null` 的条目，重新挂接。不用 `InitializeOnLoad`。
- 会话进度：面板直接读 `current.json`、`session.json` 与各 `state.json` 的 `phase`；会话进行中不显示分数，结束后读 `report.json`。`lock` 存在且其 pid 存活时显示"runner 忙"，并禁用会改状态的按钮。
- 提示正文：面板不渲染 CJK 正文（字体图集无法保证），只提供复制（`EditorGUIUtility.systemCopyBuffer`，内容取 `sessions/<sid>/session-prompt.<lang>.md`）与打开文件。
- 问题码与 notices：显示 `tab_eval_problem_<code_lower>`，用 `args` 填参。
- 代提交：`submit --attempt <aid>`；`state.answer.path` 指向的文件存在时加 `--answer <path>`，否则 `--no-answer`。

### 7.7 对草案事实的更正与补充

| 草案说法 | worktree 实际 | 影响 |
|---|---|---|
| 事实 7：`/health` 有 HTTP 线程快速路径（`SkillsHttpServer.cs:145-149`） | 那段只是注释；没有 `SendHealthFastPath` 的实现，`HealthVitals.FromSnapshot()`（`SkillsHttpServer.Health.cs:52`）与 `WantsLiveHealth()`（`:272`）都没有调用方；`/health` 在主线程由 `SkillsHttpServer.Dispatch.cs:213-221` 处理，只是排在 light 通道（`SkillsHttpServer.Queue.cs:195-210`），与 AGENTS.md"所有请求都过主线程队列"一致 | 主线程忙时 `/health` 也会阻塞：runner 必须把 `/health` 超时当作"忙"，在重试窗口内重试（同 v1 `rest.py:63-92`），不得据此判定实例已死。`queueStats.totalReceived` 仍可用（`SkillsHttpServer.Health.cs:230-234`） |
| 1.12：Codex 是否导出会话 id 待验证 | codex-cli 0.161.0 的二进制字符串中有 `CODEX_THREAD_ID`；rollout 的 `session_meta.payload.id` 就是文件名里的 thread id，`payload.session_id` 是根会话 id（子代理线程指向父会话，`thread_source == "subagent"`） | 读取器优先用 `CODEX_THREAD_ID`；是否导出给工具子进程仍需 WP0x 实测 |
| 1.12：Claude 子代理 `<id>/subagents/agent-*.jsonl` | 本机文件名形如 `agent-a<name>-<16hex>.jsonl`，旁边有同名 `.meta.json` | glob 必须是 `agent-*.jsonl`，不得读 `.meta.json` |
| 1.1：引导脚本按 `Library/PackageCache/com.besty.unity-skills@*` 找包 | Unity 6000.3 的目录后缀是 `_fingerprint` 的前缀，不是 git hash | 按 §1.5 消歧 |
| 1.5 telemetry"主文件和 .1–.3" | 轮转文件名是 `UnitySkillsTelemetry.1.jsonl` 等（`SkillTelemetryService.cs:527-531`），不是 `.jsonl.1` | §2.8 S6 |
| 事实 9：87 个离线测试通过 | Python 3.13.14 下 87/87 通过；`/usr/bin/python3`（3.9.6）下 86 通过、1 个 error（`eval/harness/subject.py:346` 用了 3.11 才有的 `tomllib`） | CI 的 3.9 job 需跳过该测试（§11 #36） |

## 8. Token usage 读取

> 实现补充、本机实测与白名单增补见 §8.6；与 §8.2–§8.3 冲突处以 §8.6 为准。

### 8.1 输入与宿主识别

| 输入 | 来源 |
|---|---|
| 时间窗 | (t0, t1]，即 `state.issuedAt` 与 `state.submittedAt` |
| 宿主 | `CLAUDE_CODE_SESSION_ID` 存在或 `CLAUDECODE == "1"` → `claude-code`；否则 `CODEX_THREAD_ID` 或 `CODEX_SANDBOX` 存在 → `codex`；否则 `none` |
| 会话 id | Claude：`CLAUDE_CODE_SESSION_ID`；Codex：`CODEX_THREAD_ID`（如有）。`next` 与 `submit` 各读一次，并集记入 `state.host.sessionIds` |
| cwd | `next` 时 runner 进程的 `os.getcwd()`（Codex 匹配用） |
| 开关 | `session.usageReading == false` → `source:"none"`，`unavailableReason:"disabled_by_user"` |

- 面板代提交时进程里没有宿主环境变量，沿用 `next` 时记下的 `state.host`。宿主为 `none` → `source:"none"`、`unavailableReason:"no_host_session"`，面板可手填（`usage --manual`）。
- `declared` 的缺项可以从同一份日志探测（`source:"detected"`）：host；hostVersion（Claude 行顶层 `version`，Codex `session_meta.payload.cli_version`）；model；effort。

### 8.2 Claude Code 会话日志

1. 根：`USK_EVAL_CLAUDE_ROOT` → `$CLAUDE_CONFIG_DIR/projects`（本机 Claude Code 可执行文件中确有 `CLAUDE_CONFIG_DIR`）→ `~/.claude/projects`。
2. 文件：`<root>/*/<sid>.jsonl`（主会话）与 `<root>/*/<sid>/subagents/agent-*.jsonl`（子代理），按路径排序，主会话在前。
3. 行：`json.loads` 失败则跳过并记 `{file, line, error}`；只取 `type == "assistant"`、`message.id` 为字符串、`timestamp` 可解析的行。
4. 去重：同一 `message.id` 只保留**文件顺序中的最后一行**。同一次 API 响应按内容块拆成多行（`apiBlockIndex`）；本机样本中一个子代理文件的 98 个 id 里有 29 个各行 usage 不同，最后一行才是最终值。跨文件出现同一 id 时以后读到的为准。
5. 时间窗：对去重后的行，保留 `timestamp` 落在 (t0, t1] 内的。
6. 累加 `message.usage`：`input_tokens` → input，`output_tokens` → output，`cache_read_input_tokens` → cacheRead，`cache_creation_input_tokens` → cacheWrite，`output_tokens_details.thinking_tokens` → reasoning（已含在 output 内）。缺失或非整数按 0。`processed = input + output + cacheRead + cacheWrite`（Anthropic 的 input 不含缓存部分，与 v1 `metrics.py:358` 一致）。
7. `messages` = 计入的 id 数；`subagents` = 子代理文件部分的 files 与 tokens 小计（已含在总数内）。
8. model：计入行的 `message.model` 去重排序，`source:"provider-returned"`；effort：计入行顶层的 `effort` 去重，`source:"host-reported"`。（同一行顶层还有 `requestedModel`，即宿主请求的模型，只用于 `declared` 探测。）
9. `raw` = `{input_tokens, output_tokens, cache_read_input_tokens, cache_creation_input_tokens, thinking_tokens}` 的合计。
10. 找不到主会话文件 → `session_log_not_found`；窗口内没有行 → tokens 全为 0，`unavailableReason:"no_messages_in_window"`。

### 8.3 Codex rollout

1. 根：`USK_EVAL_CODEX_ROOT` → `$CODEX_HOME/sessions` → `~/.codex/sessions`；文件为 `YYYY/MM/DD/rollout-<本地时间>-<threadId>.jsonl`。
2. 候选：有 `CODEX_THREAD_ID` 时取文件名以 `-<id>.jsonl` 结尾的文件；否则扫描覆盖 [t0 − 1 天, t1 + 1 天] 本地日期的日期目录（目录不存在时退回递归 glob），保留首个 `session_meta` 满足 `normcase(realpath(payload.cwd)) == normcase(realpath(state.host.cwd))`、`payload.thread_source != "subagent"`、且窗口内至少有一个 `token_count` 事件的文件。
3. 恰 1 个候选即主线程；0 个 → `rollout_not_found`；多于 1 个 → tokens 为 null、`ambiguous_rollouts`。
4. 计数：取 `type == "event_msg"`、`payload.type == "token_count"` 且 `payload.info` 非 null 的事件；`before` = 时间 ≤ t0 的最后一个（没有则全 0），`after` = 时间 ≤ t1 的最后一个；对 `payload.info.total_token_usage` 的 `input_tokens, cached_input_tokens, cache_write_input_tokens, output_tokens, reasoning_output_tokens, total_tokens` 求差。任一差为负 → tokens 为 null、`counter_reset`。
5. 映射（Codex 的 input 包含缓存部分，同 v1 `metrics.py:317-328`）：`input = max(0, Δinput − Δcached − Δcache_write)`，`cacheRead = Δcached`，`cacheWrite = Δcache_write`，`output = Δoutput`，`reasoning = Δreasoning_output`，`processed = Δinput + Δoutput`。
6. 子代理：`session_meta.payload.session_id` 等于主线程的 `session_id` 且 `thread_source == "subagent"` 的 rollout，按同法在窗口内求差，计入 `subagents` 并加进总数。
7. model：窗口内的 `turn_context.payload.model`（窗口内没有则取 t0 之前最后一个），`source:"host-requested"`；effort：`turn_context.payload.effort`。
8. `messages` = 窗口内 `type == "response_item"`、`payload.type == "message"`、`payload.role == "assistant"` 的条数。
9. `raw` = 上述 6 个字段的差值。parserVersion 1 不读 `token_usage_record` 行（本机 0.161.0 已出现这种行类型）。

### 8.4 隐私白名单

- 只允许读取：顶层 `type, timestamp, sessionId, isSidechain, effort, requestedModel, version, ordinal`；`message.id, message.model` 与 `message.usage` 的数值字段；`payload.type, payload.id, payload.session_id, payload.parent_thread_id, payload.cwd, payload.thread_source, payload.cli_version, payload.subagent_history_start_ordinal, payload.model, payload.effort, payload.role, payload.info.total_token_usage.*`。其中 `ordinal`、`parent_thread_id`、`subagent_history_start_ordinal` 是 §8.6 增补的，只含 id 与整数。
- 不得存储或记录：消息内容、工具输入输出、指令、`base_instructions`、文件名以外的路径（`payload.cwd` 只比较、不存）。出错只记文件名、行号与异常类型名。
- 测试：合成日志在内容字段放哨兵字符串，断言 usage 输出、result、ledger、runner.log 中都不出现它。

### 8.5 局限

- 整套模式下上下文逐题累积，单题成本不能和逐题模式直接比较；报告记录会话模式与语言。
- 宿主日志格式变化导致解析失败时置 null（`parse_error`），不影响评分；解析规则变化时 `parserVersion` 递增。

### 8.6 实现补充（WP7；与 §8.2–§8.3 冲突处以本节为准）

入口 `usk_eval.usage`：`detect_host`、`capture_host`、`merge_session_ids`、`read_usage(host, t0, t1, enabled, env)`、`detect_declared`、`manual_usage`、`parse_manual_args`、`unavailable_usage`、`ledger_summary`、`PARSER_VERSION`。`host` 即 `state.host`；日志类问题不抛异常，只进 `unavailableReason` 与 `errors`；t0 / t1 无法解析才抛 `ValueError`，`t1` 缺省为现在；时间一律经 `store.parse_utc / iso / now_iso`。`tokens` 为 null 当且仅当 `unavailableReason` 非空且不是 `no_messages_in_window`。

**实现选择**（每条是"选择 —— 理由"）

1. Claude `message.model == "<synthetic>"` 的行整行跳过，不计入 `messages` 与 `model.values` —— 它是 Claude Code 为失败的 API 调用写的本地占位（`isApiErrorMessage: true`，id 是 UUID，用量全 0），不是提供方响应。
2. `subagents.files` 只数窗口内有计入内容的子代理文件（Claude：有计入的消息；Codex：有窗口内 `token_count`）；读取成功时 `subagents` 恒为对象（没有就 `files: 0`），`null` 留给"没读" —— 更早题目的子代理不能算进本题。
3. 读取失败（`session_log_not_found`、`rollout_not_found`、`ambiguous_rollouts`、`counter_reset`、`parse_error`）时 `source` 仍是所尝试的来源；只有根本没读（`disabled_by_user`、`no_host_session`）才是 `none` —— 报告里能看出尝试过什么。
4. `parse_error` 的触发：Claude 有含 `"assistant"` 的候选行但没有一行能解析成 JSON 对象；Codex 有 `token_count`（`info` 为对象）但没有一个带 `total_token_usage` 对象；按 thread id 找到的 rollout 前 5 行里没有 `session_meta`；读取器内部的意外异常（只记异常类型名）。
5. Claude 行的 `timestamp` 缺失或不可解析：跳过并记入 `errors`（只记行号与 `ValueError` 类型名）；`model`、`effort`、`version`、`cli_version` 只接受 ≤120 个可打印字符的字符串，其余丢弃 —— 宿主把字段改成自由文本时也漏不进记录。
6. Codex 的 `counter_reset` 比 §8.3 #4 更严：窗口内任一事件相对前一个 `token_count` 有字段下降即判定，不止比较两端 —— 中途归零再回升会让两端差值偏小而不报错。
7. Codex 窗口内没有任何有效 `token_count`（主线程与子代理都没有）→ tokens 全 0、`no_messages_in_window`，与 Claude 对齐；按 thread id 定位时不要求主线程窗口内有事件。
8. Codex 的 model / effort 按文件取（窗口内的 `turn_context`，没有则取 t0 之前最后一个），再对窗口内有计数的主线程与子代理文件取并集；`messages` 同样是各文件之和 —— 整题常在一个回合里完成，窗口内往往没有 `turn_context`。
9. 子代理归属：调用方是 user 线程（`thread_source != "subagent"`）时取同 `session_id` 的全部 `subagent` rollout；调用方本身是子代理（`CODEX_THREAD_ID` 是子代理 id）时只取经 `parent_thread_id` 可达的后代，不含兄弟与父线程。子代理在 [t0 − 1 天, t1 + 1 天] 的日期目录里找（这些目录都不存在时递归整棵树）；按 thread id 找主线程先查这些目录，再退回整棵树。
10. 子代理 rollout 开头复制了父线程的历史（时间戳统一盖成 fork 时刻，含父线程的 assistant 消息与 `turn_context`）：凡 `ordinal < payload.subagent_history_start_ordinal` 的行一律不读（只对子代理文件；字段缺失则不跳过），副本里即使有 `token_count` 也不参与差值；`session_meta` 只取文件中第一个 —— 否则 `messages`、model、effort 会重复计入。
11. §8.4 白名单增补 `ordinal`、`parent_thread_id`、`subagent_history_start_ordinal`（只含 id 与整数；lead 已确认）。
12. 标准库（≤ 3.13）读不了 zstd，而 Codex 会把旧 rollout 压成 `.jsonl.zst`：按 thread id 只找到 `.zst` 时返回 `rollout_not_found`，并在 `errors` 记 `{file: <文件名>, line: 0, error: "CompressedRollout"}`；按目录匹配不看 `.zst`。
13. `detect_declared`：Claude 取窗口末（缺省为现在）之前最后一条 assistant 行的 `version`、`requestedModel`（缺则 `message.model`；`[1m]` 之类后缀原样保留）、`effort`；Codex 取主 rollout 的 `cli_version` 与最后一个 `turn_context` 的 `model` / `effort`。无窗口时 Codex 按目录匹配只认最近 30 分钟内有计数的 rollout。
14. 宿主 id 只接受 `[A-Za-z0-9][A-Za-z0-9._-]{0,127}`（会拼进文件名）；默认根目录的 home 在 Windows 上取 `USERPROFILE`（Git Bash 的 `HOME` 是 POSIX 路径）；`payload.cwd` 去掉 `\\?\` 前缀后再比较。

**本机实测**（macOS；Claude Code 2.1.296；Codex 0.142–0.162 的 rollout；只读结构，未保存任何内容）

- rollout 压缩：`~/.codex/sessions` 的 183 个文件里 147 个是 `.jsonl.zst`。已压缩的最晚在 `2026/10/05` 目录，未压缩的最早在 `2026/10/09` 目录（实测日 10-17），压缩阈值落在 8–12 天之间，具体规则未查到，不要依赖。所以 Codex 的 usage 必须在 `submit`（S12）当场算出并存下；会话结束时的重算（§11 #23）与日后的 `usage --attempt` 只有在日志还没被压缩时才读得到。建议 runner 在重算得到 `tokens: null` 而已有成功记录时保留旧记录、只给 notice（与 §11 #23"以最新 usage 行为准"需一并约定）。Claude 日志不压缩。
- 子代理占多数：36 个未压缩 rollout 里 33 个是 `thread_source == "subagent"`，一个根会话最多带 25 个子代理文件。子代理的 `session_meta.payload.session_id` 是根线程的会话 id，`payload.id` 才是自己的 thread id，文件名以 `-<自己的 id>.jsonl` 结尾。
- `thread_source` 取值：`user`、`subagent`、`agent_created_thread`（独立的根会话，`session_id == id`），旧版本没有这个字段；除 `subagent` 外一律按根线程处理。
- fork 历史：28 个文件含两个 `session_meta`（第二个是父线程的副本，`thread_source: "user"`），到 `subagent_history_start_ordinal` 为止的行都是副本；子代理自己的计数从 0 起（首个事件的 `last_token_usage == total_token_usage`）；本机副本里没见到 `token_count`。
- Claude 日志里的特殊行：`cost-state`（`totalCostUSD`、`modelUsage`，不是 `assistant`，不读）；`<synthetic>` 占位行（采样的 2388 条 assistant 行里有 4 条）；`output_tokens_details` 可能为 `null` 或缺失（按 0）；`usage.iterations` 至多一项且与顶层数值相同（不读）；同一 id 的多行里只有 `output_tokens` 与 `thinking_tokens` 会变（1228 个 id 中 161 个），最后一行最大。
- Codex 的 `token_count`：约 4.8 万个事件（其间夹着 400 条 `compacted`）无一次计数下降；`info: null` 共 15 条，都在已压缩的旧文件里；6 个计数字段在所有样本里都存在，缺失仍按 0。
- `CODEX_THREAD_ID`：Codex 0.161.0 二进制里它出现在 `unified_exec` / `exec_command` 的子进程环境构造附近（同处还有 `CODEX_VERSION`、`CODEX_PERMISSION_PROFILE`），很可能导出给工具子进程；这台机器上没有 Codex 的工具 shell，未能实测，仍属 WP0x。

**局限**（补充 §8.5）

- Codex 按目录匹配要求 runner 的 cwd 与 `session_meta.payload.cwd` 规范化后相等；Agent 先 `cd` 到别处就得到 `rollout_not_found`（有 `CODEX_THREAD_ID` 时不受影响）。
- Claude 的子代理与 Agent Teams 成员日志都在主会话的 `subagents/` 下：窗口内同一会话里所有成员的用量都会计入，分不出调用者。
- 面板代提交的进程没有 `CLAUDE_CONFIG_DIR` / `CODEX_HOME`，自定义日志根的用户读不到。
- §8.1 的识别顺序让 `CLAUDECODE=1` 优先：在 Claude Code 里再启动的 Codex 会被识别成 Claude Code。

## 9. 版本与门禁

| 项 | 规则 |
|---|---|
| runnerVersion | `<pkg>/package.json` 的 `version`（当前 `2.9.1`，`SkillsForUnity/package.json:3`）；runner 不得硬编码 |
| pluginVersion | `/health.version` = `SkillsLogger.Version`（`SkillsLogger.cs:17`、`SkillsHttpServer.Health.cs:181`） |
| 版本校验 | check / start / next / submit：runnerVersion 必须**等于** pluginVersion，否则 `VERSION_MISMATCH`。runner 依赖插件写出的 journal 字段等格式；两个锚点由 `/updateversion` 同步 |
| packVersion | §5.1，与插件版本独立 |
| 插件范围 | pluginVersion 不在 [plugin.min, plugin.max] → `PACK_PLUGIN_RANGE` |
| caseVersion | int。题目的任何改动（task.py、两份提示、私有模块中的断言/权重/容差/判定、seeds、minutes、requires）都必须加 1。v1 冻结为 caseVersion 1（`eval/reports/case-freeze-v1.json`）；所有移植题从 2 开始，新题从 1 开始。`check_eval_pack.py` 与上一个发布的 manifest 对比：public / private hash 变了而 caseVersion 没变 → 失败 |
| 可比性键 | (packId, taskId, caseVersion)；v1 与 v3 的成绩不比较（草案 3.1） |

**门禁**
- 门禁键 = `canonical_digest({"taskId", "caseVersion", "unityMajor", "pluginVersion", "gateInputsHash"})`；`unityMajor` = `unityVersion` 第一个点之前的部分（`2022`、`6000`）。两个平台分别门禁（草案：2022.3 URP 与 6000.x）。
- `gateInputsHash`（每题一个）：对下列文件按相对路径排序，逐个把 `relpath + "\0" + sha256(LF 规范化内容) + "\n"` 喂进 sha256（同 v1 `env.py:26-42` 的规则）：
  - `SkillsForUnity/Evaluation~/run_eval.py`；
  - `SkillsForUnity/Evaluation~/usk_eval/**/*.py`，**不含** `usk_eval/usage/**` 与 `usk_eval/report.py`（不影响判定，收窄以减少重跑门禁）；
  - `SkillsForUnity/Evaluation~/pack/tasks/<ID>/**`；
  - `eval/pack/private/<ID>.py`、`eval/pack/solutions/<ID>.py`；
  - `eval/cli.py` 与 WP12 的 selftest 驱动模块（WP12 在 `eval/cli.py` 中把清单写成常量 `GATE_INPUTS`）。
- 插件 C# 不进 hash，靠 `pluginVersion` 进键：开发中途改插件不会自动让门禁失效，所以 `/release` 的 `check_eval_pack` 步骤必须对候选版本重跑门禁（草案 P7）。
- 门禁记录 `eval/runs/selftest/gate.json`：`{"schema":1,"gates":{"<gateKey>":{"taskId","caseVersion","unityMajor","pluginVersion","gateInputsHash","green":bool,"cases":int,"failing":[case],"recordedAt"}}}`。

## 10. 基线

| 项 | 结果 |
|---|---|
| 离线 v1（3.13） | `python3 -B -m unittest discover eval/tests`，Python 3.13.14：`Ran 87 tests in 4.725s`，`OK`；运行后没有 `__pycache__`，git 状态干净 |
| 离线 v1（3.9） | `/usr/bin/python3 -B -m unittest discover eval/tests`，Python 3.9.6：`Ran 87 tests`，86 通过，1 个 error：`test_codex_subject.CommandTests.test_neutral_codex_home_copies_auth_and_only_transport`，`ModuleNotFoundError: No module named 'tomllib'`（`eval/harness/subject.py:346`） |
| EditMode（2.9.1 候选 `8b52ee98`，lead 提供） | Unity 6000.3.9f1：1701 total / 1623 passed / 0 failed / 78 skipped；Unity 2022.3.62f2：1702 / 1616 / 0 / 86 |
| 环境 | macOS（Darwin 27.0.0）；worktree `eval-v3` @ `5cb0f951`，工作区干净 |

## 11. 实现选择

钉契约时自行决定的点；每条是"选择 —— 理由"。

1. `problems[]` 只放阻断项，非阻断放 `data.notices[]` —— 保持 `ok == (problems == [])`，Agent 只需看 ok。
2. `message`/`fix` 只用英文，另加可选 `args`（位置数组） —— 面板按 code 本地化，`SkillsLocalization.Get` 用 `string.Format` 填参（`Localization.cs:227-239`）。
3. 信封 `next` 是对象 `{command, line, argv}` —— `line` 供人或 Agent 粘贴，`argv` 让程序免解析。
4. 提示与答案经 IO 目录 `<project>/Library/UnitySkillsEvalIO/<sid>/` 交换，stdout 默认不内联提示正文（`--inline` 才内联） —— G6 禁止读状态目录；`ensure_ascii` 会把中文提示变成 `\uXXXX`，既难读又让中文会话多耗 token、扭曲 usage；IO 目录在工程内（cwd 是工程时宿主不弹写权限确认），又在 Library 下不进版本库。
5. 提示文件写 UTF-8 带 BOM，答案按 `utf-8-sig` 读 —— Windows PowerShell 5.1 的 `Get-Content`/`Set-Content` 默认依赖 BOM。
6. 命令行里的 python 优先写裸名（PATH 指向同一解释器时），Windows 路径含空格时取短路径 —— 在 bash/zsh/PowerShell/cmd 中都能直接执行。
7. 答案文件不存在 → 协议错误、可重试；内容非法 → 照常验收、answer 为 null —— 前者多半是路径笔误，后者属于能力表现，而且不能给反馈。
8. `check`、`status`（以及报告已存在时的 `report`）不持锁 —— 它们只读原子替换的文件；dev 题的提交可能持锁数分钟，查询不应因此超时。
9. 过期锁判据另加"无内容且超过 10 s"与"年龄超过 3600 s"；Windows 用 OpenProcess 判断存活 —— 覆盖崩溃在写锁内容之前与 PID 复用；Windows 上 `os.kill(pid, 0)` 会终止进程。
10. journal 轮转段命名 `editor_changes.{n}.jsonl`（活动段 + 10 段，1 最新），另设 8 MiB 字节上限，删除压缩 —— 与 telemetry 命名一致；单条 scene_changes 可达数十 KB，只按条数计会让磁盘失控；压缩会删掉历史。
11. journal `agent`：rest 条目为调用方 id（多个调用方为 `"mixed"`、无上下文为 `"unknown"`），editor 条目为 null；防抖属性按捕获时的 agent / isPlaying 分组 —— 防抖要等 REST 深度归零后才发布，发布时再取值会丢归属。
12. 文件截断计数用平铺字段 `omitted / importedTotal / deletedTotal / movedTotal`，`count` 语义不变 —— 只新增，便于 grep。
13. 重父级记录额外加 `scenePath` —— scope 身份表补丁要按场景判断位置。
14. journal 在 `quitting` 时也刷防抖属性 —— 退出前最后的修改不丢。
15. 模式恢复键是绑定 `projectPath` 与 `sessionId` 的 JSON，只在当前仍是 Bypass 时恢复、否则只删键，不用 InitializeOnLoad —— EditorPrefs 整机共享，不能让别的工程触发恢复；尊重用户手动改过的模式；遵守启动策略。
16. 面板结果文件 = 信封 + `exitCode / finishedAt / cmdId`，并新增 `--cmd-id` —— domain reload 后面板拿不到进程退出码。
17. 期望 instanceId 用 registry 条目自身的 `path`（Unity 视角）计算，先用 realpath 比对工程 —— v1 用本地 realpath 计算，经符号链接打开的工程会误报不匹配。
18. surfaceProfile 必须是 full（阻断），dryRunPolicy、tokenLevel 不是参考值时只给 notice —— guide 等档位会隐藏写技能，题目无法完成；后两者只影响可比性。
19. 新增维护者参数 `--private-dir DIR`；zip 条目放在根目录、以 manifest 的 `file` 命名 —— selftest 可以直接用仓库里的 `eval/pack/private`，不必拼 pack-dir。
20. 静态 `verify` 在前，dev 的 SCENARIO 与 `verify_runtime` 在后；SCENARIO 可以是 dict 或 callable —— 遵循草案的冻结顺序；步骤常常要引用 handles 里的 instanceId。
21. `answer_schema` 用 JSON Schema 子集加双语 `desc`，runner 自动加必填的 `declaredComplete`，`{schema}` 渲染用英文类型词 —— 两种语言共用一份结构，并能做非阻断校验。
22. caseVersion 用 int（v1 是字符串 `"1"`） —— 便于比较和校验递增。
23. usage 在 submit 时算一次，会话结束时对全部尝试重算，以 ledger 中最新的 `usage` 行为准；但重算结果 `tokens` 为 null 时不覆盖此前 `tokens` 非 null 的记录（Codex 旧 rollout 会被压缩成 `.jsonl.zst`，见 §8.6），只在该记录的 `errors` 里追加重算失败原因 —— 宿主日志可能滞后写入，也可能事后变得不可读。
24. Claude 先按 message.id 去重（保留最后一行），再按该行时间判断是否落在窗口内 —— 最后一行才有最终 usage，也避免同一响应跨窗口重复计数。
25. Codex 优先用 `CODEX_THREAD_ID`，否则按 cwd 加时间窗匹配；子代理按 `session_id` 归并 —— 二进制里有该变量但导出与否未证实；多会话并行时 cwd 匹配可能有歧义，此时置 null。
26. usage 记录增加 `effort{values, source}` 与 `errors[]` —— 两种宿主日志都带 effort（v1 里 effort 是槽位身份的一部分）；错误只记类型，便于排查又不泄露内容。
27. 门禁键含 `pluginVersion` 而不是插件 C# 的 hash，GATE_INPUTS 排除 `usage/` 与 `report.py` —— 收窄门禁输入、降低重跑成本（草案风险项），发布前强制重跑。
28. 冻结时在途作业最多等 300 s，超时只记 `inflightJobs`、不改判 —— Agent 留下的长作业是它自己的行为，不是基础设施故障。
29. 出题与提交之间 registry pid 变化判 `invalid`（`editor_restarted`） —— 未保存的 run 场景会丢失，结果无法归因。
30. 新 id 一律用 UTC —— 跨时区、跨进程排序一致（v1 的 runId 用本地时间）。
31. `{project}` 渲染为工程绝对路径，`{pipeline}` 为 pipelineType —— dev 题要用绝对路径写脚本；v1 用的也是 pipelineType。
32. G4 只渲染本赛道的那一行并去掉标记 —— 提示里不出现与本题无关的规则。
33. 会话结束时删除空的 `Assets/__UnitySkillsEval` —— 不在用户工程里留下空目录与 `.meta`。
34. `pending_verification` 阻断 `next` —— 不允许两份 fixture 并存（新的 run 场景会以 Single 模式替换旧的）。
35. non-REST 豁免清单（Play 期间、A 下的文件、run 场景的保存与打开、allowed_change） —— 交互模式下 Unity 自动刷新导入 Agent 写的文件是常态，按 v1 一律判污染会误伤；清单需在 P3 E2E 中实测确认。
36. v1 测试 `test_codex_subject` 在低于 3.11 时用 `skipUnless(sys.version_info >= (3, 11))` 跳过（由 WP13 修改） —— `subject.py` 只给维护者在 3.11+ 使用，CI 的 3.9 job 不应因此失败。
37. notices 与 problems 共用 `tab_eval_problem_<code_lower>` 命名空间 —— 面板只维护一套映射。
38. 窗口内的 telemetry 行复制到 `attempts/<aid>/telemetry.jsonl` —— 1 MB 轮转很快会冲掉原文件，复核需要原始证据。
39. handles 键名黑名单在草案的 `expected/target/decoy/inactive/answer` 之外加入 `correct/wrong/trap/truth/solution/reference/legacy` —— v1 S01 的 `legacyPath` 正是靠键名暴露了诱饵。
40. 会话结束后删除 IO 目录 —— 提示与答案已归档在状态目录，避免残留。
