# Contributing | 贡献指南

Thank you for contributing to Unity-Skills!
感谢你对 Unity-Skills 的贡献！

## Workflow | 贡献流程

**All pull requests target the `beta` branch.** `main` only ever moves through the maintainer's `/release` flow, which fast-forwards it to a verified `beta` commit; a PR opened against `main` will be asked to retarget. | **所有 PR 一律合并到 `beta` 分支。** `main` 只通过维护者的 `/release` 流程线性推进到已验证的 `beta` 提交；提交到 `main` 的 PR 会被要求改回 `beta`。

1. **Fork** this repository | Fork 本仓库
2. **Branch from `beta` | 从 `beta` 切分支**:
   ```bash
   git clone https://github.com/<your-name>/Unity-Skills.git
   cd Unity-Skills
   git checkout beta
   git checkout -b feat/your-feature
   ```
3. **Commit changes | 提交更改**: `git commit -m "feat: add new feature"`
4. **Push branch | 推送分支**: `git push origin feat/your-feature`
5. **Open the PR with base = `beta` | 创建 PR 时 base 选 `beta`**: `gh pr create --base beta`

> Do **not** bump version anchors or add a `CHANGELOG.md` entry in a PR. Versioning is maintainer-only (`/updateversion`) and a contributor bump will collide with the release flow. | PR 里**不要**改版本锚点或加 `CHANGELOG.md` 条目。版本更新是维护者专属流程（`/updateversion`），贡献者自行升版会与发布流程冲突。

## Verifying Your Change | 自检你的改动

Matrix green is the bar. Live-editor testing is **welcome but no longer required**. | 以矩阵通过为准。真机（Editor 实测）**欢迎但不再强制**。

### 1. Local static checks | 本地静态检查

These three scripts are exactly what the matrix runs first, and they need no Unity license: | 下面三个脚本就是矩阵最先跑的三个静态任务，不需要 Unity 许可证：

```bash
pip install pyyaml   # only needed by the frontmatter check | 仅 frontmatter 检查需要
python3 .github/scripts/check_project_version.py .   # version anchors agree | 版本锚点一致
python3 .github/scripts/check_skill_frontmatter.py . # SKILL.md frontmatter | 文档 frontmatter
python3 .github/scripts/check_meta_files.py .        # .meta pairing & GUIDs | .meta 配对与 GUID
```

> On an externally managed Python (Homebrew / recent Linux distros) `pip install` is blocked — use a virtualenv: `python3 -m venv .venv && .venv/bin/pip install pyyaml`, then run the scripts with `.venv/bin/python`. | 若 Python 由系统托管（Homebrew / 较新的 Linux 发行版），`pip install` 会被拒绝——请用虚拟环境：`python3 -m venv .venv && .venv/bin/pip install pyyaml`，再用 `.venv/bin/python` 运行脚本。

### 2. Run the compile matrix in your own fork | 在自己的 fork 上运行编译矩阵

The repository ships one verification matrix: **Unity Package Compile Matrix** (`.github/workflows/unity-package-compile.yml`). It triggers on `workflow_dispatch` (manual) and on pushing a `v*.*.*` tag. | 仓库只有一条检验矩阵：**Unity Package Compile Matrix**（`.github/workflows/unity-package-compile.yml`），触发方式为 `workflow_dispatch`（手动）与推送 `v*.*.*` tag。

| Job | What it checks | 检查内容 |
|-----|----------------|----------|
| `Project Version & Release Tag Consistency` | All version anchors agree | 所有版本锚点一致 |
| `SKILL.md Frontmatter Compliance` | Skill docs frontmatter | Skill 文档 frontmatter |
| `.meta Pairing & GUID Uniqueness` | `.meta` pairing, duplicate GUIDs | `.meta` 配对与 GUID 重复 |
| `Unity <version> EditMode` × 6 | Compile + EditMode tests on 2022.3.62f1, 6000.0.76f1, 6000.2.0f1, 6000.3.18f1, 6000.4.12f1, 6000.5.0f1 | 6 个 Unity 版本上的编译与 EditMode 测试 |
| `Test Summary & License Check` | Aggregates results; fails if any job did not succeed | 汇总结果；任一任务未成功即失败 |

Dispatch it **from your fork**, on your feature branch: | 在**你自己的 fork** 上、针对你的分支手动触发：

```bash
gh workflow run unity-package-compile.yml \
  --repo <your-name>/Unity-Skills \
  --ref feat/your-feature
```

Or: your fork → **Actions** → **Unity Package Compile Matrix** → **Run workflow** → pick your branch. | 或：你的 fork → **Actions** → **Unity Package Compile Matrix** → **Run workflow** → 选择你的分支。

> ⚠️ The six EditMode jobs need Unity credentials configured as secrets **in your fork** — `UNITY_LICENSE`, `UNITY_EMAIL`, `UNITY_PASSWORD` (Game-CI activation flow). Without them the three static jobs still run, but EditMode fails at activation. | 六个 EditMode 任务需要**在你的 fork 中**配置 Unity 凭据 Secret：`UNITY_LICENSE`、`UNITY_EMAIL`、`UNITY_PASSWORD`（Game-CI 激活流程）。未配置时三个静态任务照常运行，EditMode 会在激活阶段失败。

> Link the run URL (or paste the summary table) in your PR description so reviewers can see the result. | 请在 PR 描述里附上运行链接（或粘贴汇总表格），方便审阅者查看结果。

> Do **not** dispatch the matrix on the upstream repository and do **not** push tags: upstream runs consume the maintainer's Unity license quota and CI minutes, and tags belong to the `/release` flow. | **不要**在上游仓库触发矩阵，也**不要**推送 tag：上游运行会消耗维护者的 Unity 许可证配额与 CI 时长，tag 属于 `/release` 流程。

> `Update Star History` (`.github/workflows/update-star-history.yml`) is a scheduled maintenance job gated to the default branch — contributors never need to run it. | `Update Star History`（`.github/workflows/update-star-history.yml`）是限定默认分支的定时维护任务，贡献者无需运行。

### 3. Optional live check | 可选的真机检查

If you have the editor at hand, importing the package into Unity 2022.3+ / Unity 6+, starting the server from **Window > UnitySkills** and calling the affected endpoint is a useful extra signal — it is not a merge requirement. | 如果手边有编辑器，把包导入 Unity 2022.3+ / Unity 6+、从 **Window > UnitySkills** 启动服务并调用相关接口是有价值的补充验证——但不是合并的前置条件。

> Maintenance baseline | 维护基线：官方新增功能开发、回归验证与适配以 **Unity 2022.3+ / Unity 6** 为主。仓库可能保留部分旧版兼容逻辑，但不再作为主要适配目标。

## Commit Message Format | 提交信息格式

```
type: description
类型: 简述

Types | 类型：
- feat: New feature | 新功能
- fix: Bug fix | 修复 Bug
- docs: Documentation | 文档更新
- chore: Build/tooling | 构建/工具变更
- refactor: Code refactoring | 代码重构
```

## Code Style | 代码规范

### C# (Unity)
- Follow Unity coding conventions; PascalCase for classes and methods; comments in Chinese | 遵循 Unity 编码规范；类和方法使用 PascalCase；使用中文注释
- **Threading is a hard constraint**: the HTTP thread only enqueues; every `UnityEngine.*` / `UnityEditor.*` call runs on the main thread through the existing queue | **线程模型是硬约束**：HTTP 线程仅入队，所有 `UnityEngine.*` / `UnityEditor.*` 调用都通过既有队列在主线程执行
- **Editor UI is UI Toolkit only** (`.cs` + `.uxml` + `.uss` under `Editor/UI/`). No IMGUI — no `OnGUI` / `EditorGUILayout` / `GUILayout` | **Editor UI 只用 UI Toolkit**（`Editor/UI/` 下 `.cs` + `.uxml` + `.uss` 三件套）。禁止 IMGUI：不用 `OnGUI` / `EditorGUILayout` / `GUILayout`
- User-facing strings go through `SkillsLocalization`; no hardcoded UI text in `.cs` | 面向用户的字符串走 `SkillsLocalization`；`.cs` 里不硬编码界面文案
- `SkillsLogger.Version` is the single version source; log through `SkillsLogger`, not `Debug.Log*` | `SkillsLogger.Version` 是版本号唯一源；日志走 `SkillsLogger`，不直调 `Debug.Log*`
- Reuse the shared helpers instead of rewriting them: `GameObjectFinder`, `Validate`, `BatchExecutor`, `SkillsCommon.JsonSettings`, `AsyncJobService` | 复用公共辅助层而不是重写：`GameObjectFinder`、`Validate`、`BatchExecutor`、`SkillsCommon.JsonSettings`、`AsyncJobService`

### Python
- The bundled client (`unity-skills~/scripts/unity_skills.py`) is synchronous and depends only on the stdlib plus `requests` — keep it that way; do not introduce new third-party dependencies or an async rewrite | 随包分发的客户端（`unity-skills~/scripts/unity_skills.py`）是同步实现，只依赖标准库与 `requests`——请保持现状，不要引入新的第三方依赖或改写为 async
- Keep type annotations | 保留类型注解
- `.github/scripts/*.py` are stdlib-only, except `check_skill_frontmatter.py` which may use `pyyaml` (the workflow installs it) | `.github/scripts/*.py` 只用标准库，`check_skill_frontmatter.py` 可用 `pyyaml`（工作流会安装）

## Adding New Skills | 添加新 Skill

1. Add a `[UnitySkill]` static method to an existing module in `SkillsForUnity/Editor/Skills/` (or create `XxxSkills.cs` and register the new module in the `SkillCategory` enum). `SkillRouter` discovers it by reflection at startup. | 在 `SkillsForUnity/Editor/Skills/` 的已有模块里加 `[UnitySkill]` 静态方法（或新建 `XxxSkills.cs`，并在 `SkillCategory` 枚举登记新模块）。`SkillRouter` 启动时反射自动发现。

```csharp
[UnitySkill("skill_name", "Description — first sentence is what the AI reads",
    Category  = SkillCategory.GameObject,
    Operation = SkillOperation.Create,
    Tags      = new[] { "primitive", "hierarchy" },
    Outputs   = new[] { "instanceId", "path" },
    TracksWorkflow = true,     // write skills | 写型 skill
    MutatesScene   = true,     // risk metadata must be accurate | 风险元数据按实际填
    RiskLevel      = "medium")]
public static object SkillName(string name, float x = 0f)
{
    if (Validate.Required(name, "name") is object err) return err;
    // Implementation
    return new { success = true };
}
```

2. `Category` and `Operation` are required; `Tags` / `Outputs` are strongly recommended. Risk metadata drives the permission system — the server derives blocking from it, so mis-declaring it is a security bug, and skills must not implement their own authorization checks. | `Category` 与 `Operation` 必填，`Tags` / `Outputs` 强烈建议。风险元数据驱动权限系统——服务端据此判定拦截，填错等同安全缺陷；Skill 内部不要自写授权判断。
3. Return business errors with `SkillErrorResponse.Build(code, msg, ...)` instead of throwing; write operations register `Undo` and take a `WorkflowManager` snapshot. | 业务错误用 `SkillErrorResponse.Build(code, msg, ...)` 返回而不是抛异常；写操作要注册 `Undo` 并调用 `WorkflowManager` 快照。
4. Document it in `SkillsForUnity/unity-skills~/skills/<module>/SKILL.md`. Advisory (design-guidance) modules are docs-only — no C# stub. | 在 `SkillsForUnity/unity-skills~/skills/<模块>/SKILL.md` 补文档。Advisory（设计指导）模块只有文档，不加 C# stub。
5. Keep the bundled root `SKILL.md` lean — it is the always-loaded entry point and is kept within a tight token budget (~8 KB). New depth belongs in `skills/` or `references/`. | 随包根 `SKILL.md` 要保持精简——它是常驻加载的入口文档，有严格的 token 预算（约 8 KB）。新增细节请放到 `skills/` 或 `references/`。
6. Run `/skillcheck` so the skill count (currently **785** REST skills across 54 functional modules + 27 advisory modules) stays in sync across `README.md`, `README_CN.md`, `agent.md` and the skill docs. | 运行 `/skillcheck`，让技能总数（当前 **785** 个 REST Skills，54 个功能模块 + 27 个 advisory 模块）在 `README.md`、`README_CN.md`、`agent.md` 与技能文档间保持同步。

## Version Update | 版本号更新

Maintainer-only. `/updateversion <version>` updates the explicit project-version anchors below. Do not globally replace a version number: third-party SDK compatibility docs may legitimately contain the same number. | 维护者专用。`/updateversion <版本号>` 更新下列明确的项目版本锚点。不要全局替换版本数字：第三方 SDK 兼容性文档可能合法包含相同数字。

| File | Location |
|------|----------|
| `SkillsForUnity/Editor/Skills/SkillsLogger.cs` | `Version` constant (single C# source of truth) |
| `agent.md` | Version table |
| `SkillsForUnity/package.json` | `"version"` field |
| `CHANGELOG.md` | Add new entry at top |
| `SkillsForUnity/unity-skills~/scripts/unity_skills.py` | `__version__` |
| `README.md` | Explicit `Current: v...` marker only |
| `README_CN.md` | Explicit `当前：v...` marker only |
| `.github/SECURITY.md` | Supported-versions table — **minor / major bumps only**, never patch |

> If Unity baseline, skill counts, advisory-module counts, or install layout change, also update the matching `.github` docs/templates. | 若 Unity 基线、技能数、advisory 模块数或安装结构有变化，也要同步更新 `.github` 下相关文档和模板。

Version consistency check (no argument = every anchor must agree with `SkillsLogger.Version`) | 版本一致性检查（不带参数即校验所有锚点与 `SkillsLogger.Version` 一致）：
```bash
python3 .github/scripts/check_project_version.py .
```

The Unity update banner compares `SkillsLogger.Version` with GitHub's latest stable Release. Updating files or creating a tag does not notify users by itself; the maintainer-only `/release` workflow runs a candidate matrix, synchronizes `main`/`beta`, runs the tag matrix, and only then creates and verifies the stable GitHub Release. | Unity 更新横幅会比较 `SkillsLogger.Version` 与 GitHub 最新稳定 Release。仅更新文件或创建 tag 不会通知用户；维护者专用 `/release` 会先运行候选矩阵、同步 `main`/`beta`、运行 tag 矩阵，最后才创建并核验稳定 GitHub Release。

## Custom commands (Claude Code) | 自定义命令（Claude Code）

This repo ships maintainer slash-commands under `.claude/commands/`: `updateversion`, `metacheck`, `skillcheck` (consistency audit + skill-count sync), and `release`. | 本仓库在 `.claude/commands/` 下内置了维护者用的斜杠命令：`updateversion`、`metacheck`、`skillcheck`（一致性审计 + 技能数量同步）与 `release`。

> Please use them responsibly | 请合理使用：
> - `metacheck` / `skillcheck` may be used to self-check your changes | 这两个可用于自检你的改动
> - `updateversion` is maintainer-only — see **Version Update** above | `updateversion` 是维护者专用，见上文 **版本号更新**
> - **Do not run `release`** — it is maintainer-only: it dispatches license-consuming Unity matrices, updates `main` with a guarded force-with-lease, creates the release tag, and publishes the GitHub Release. Running it from a fork or PR will damage the release flow. | **请勿运行 `release`**——它是维护者专用：会触发消耗许可证额度的 Unity 矩阵、通过受保护的 force-with-lease 更新 `main`、创建发布 tag 并发布 GitHub Release，从 fork 或 PR 运行会破坏发布流程。

## Feedback | 问题反馈

- Bug reports: Use Issue template | Bug 报告请使用 Issue 模板
- Feature requests welcome | 功能建议欢迎提交 Feature Request
