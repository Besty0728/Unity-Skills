# Update Version — 安全更新项目版本 + 生成 CHANGELOG

你是 UnitySkills 项目的版本更新助手。此命令只负责准备一个可发布的版本候选；它不会创建 tag 或 GitHub Release，也不会立即向已安装用户发送更新提醒。

Unity 内的更新提醒使用 `SkillsLogger.Version` 与 GitHub `releases/latest` 的稳定 Release 比较。只有正式发布新的非 draft、非 prerelease GitHub Release 后，用户才会看到更新横幅，并跳转到该版本的 `/releases/tag/v{VERSION}` 页面。

## 输入

用户必须提供不带 `v` 的稳定语义版本号，例如 `/updateversion 2.6.0`。

- 只接受严格的 `MAJOR.MINOR.PATCH`。
- 不接受 `v2.6.0`、`2.6`、`2.6.0-beta.1` 或四段版本号。
- 未提供或格式不合法时停止，并提示：`/updateversion <新版本号>`。

## 步骤 1：远端与版本线预检查

1. 确认当前在 `beta` 分支；不在则停止。
2. 确认 GitHub CLI 已登录：
   ```bash
   gh auth status
   ```
3. 获取最新远端引用。任何 fetch 失败都必须停止，不能用过期的本地引用继续判断：
   ```bash
   git fetch origin main beta --tags
   ```
4. 检查工作区：
   ```bash
   git status --porcelain
   ```
   有未提交改动时明确警告，但不阻止，因为这些改动可能正是本次待发布内容。后续只修改约定的版本锚点，不覆盖其他改动。
5. 确认 `beta` 没有遗漏远端提交：
   ```bash
   git log beta..origin/beta --oneline
   git log beta..origin/main --oneline
   ```
   任一命令有输出都停止：本地 `beta` 落后于远端，必须先同步或处理分歧。
6. 从 `origin/main` 的 `SkillsLogger.Version` 读取当前稳定版本 `{OLD_VER}`，不要依赖可能过期的本地 `main`：
   ```bash
   git show origin/main:SkillsForUnity/Editor/Skills/SkillsLogger.cs
   ```
7. 查询 GitHub 当前最新稳定 Release：
   ```bash
   gh api repos/Besty0728/Unity-Skills/releases/latest \
     --jq '{tag_name,html_url,draft,prerelease}'
   ```
   - `tag_name` 必须严格为 `vMAJOR.MINOR.PATCH`。
   - 必须是非 draft、非 prerelease。
   - 正常情况下其版本应等于 `{OLD_VER}`；若不一致，停止并报告“main 当前版本与 latest Release 已错位”，不要猜测基准版本。
8. 验证当前工作树内的所有项目版本锚点仍等于 `{OLD_VER}`：
   ```bash
   python3 .github/scripts/check_project_version.py . --expected "{OLD_VER}"
   ```
   不一致时先修复已有漂移，不得继续批量升版。
9. 用数值元组比较语义版本。`{NEW_VER}` 必须严格高于 `{OLD_VER}`，也必须严格高于 GitHub latest Release 的版本；禁止使用字符串字典序比较。
10. 判定本次更迭级别 `{BUMP_LEVEL}` 并记录下来，步骤 4 与步骤 5 都依赖它：
    - 把 `{OLD_VER}` 与 `{NEW_VER}` 各自拆成 `MAJOR.MINOR.PATCH` 三段整数后逐段比较，不做字符串比较。
    - `MAJOR` 段不同 → `major`；`MAJOR` 相同而 `MINOR` 段不同 → `minor`；只有 `PATCH` 段不同 → `patch`。
    - 例：`2.9.1 → 3.0.0` 是 `major`；`2.6.3 → 2.7.0` 是 `minor`；`2.3.1 → 2.3.2` 是 `patch`。
    - 只有 `major` / `minor` 会让 `.github/SECURITY.md` 进入本次改动范围（见步骤 4）。

## 步骤 2：版本号占用检查

同时检查本地 tag、远端 tag 和 GitHub Release；任一已存在都停止，版本号不可复用：

```bash
git tag -l "v{NEW_VER}"
git ls-remote --tags origin \
  "refs/tags/v{NEW_VER}" \
  "refs/tags/v{NEW_VER}^{}"
gh release view "v{NEW_VER}"
```

- `gh release view` 返回“未找到”才表示该检查通过。
- 不得删除、移动或强制重推已发布 tag。
- 若版本曾经创建过 tag 但尚未创建 Release，同样视为已占用；修复后应换用新的 patch 版本。

## 步骤 3：分析 beta 相对稳定版的变更

统一以 `origin/main` 为稳定基线，不使用本地 `main`：

```bash
git log origin/main..beta --oneline
git diff origin/main --stat
git status --short
```

`git diff origin/main` 会同时覆盖 `beta` 已提交差异及当前已跟踪的工作区改动。还要单独审阅 `git status` 中的未跟踪文件。

如果既没有提交差异，也没有实质性工作区差异，停止并询问用户是否确实要创建仅含版本号的空版本。

对关键功能文件查看具体 diff，重点关注：

- `SkillsForUnity/Editor/**/*.cs`
- `SkillsForUnity/Editor/**/*.uxml`、`*.uss`
- `SkillsForUnity/Editor/Locales/*.json`
- `SkillsForUnity/unity-skills~/skills/**/*.md`
- `SkillsForUnity/unity-skills~/scripts/unity_skills.py`
- `.github/workflows/**` 与 `.github/scripts/**`
- 用户可感知的 README、安装和兼容性变化

基于 commit message 与实际 diff 组织 CHANGELOG：

- **Added**：新增功能、新 Skill、新参数。
- **Changed**：行为、API、工作流或文档契约变化。
- **Fixed**：缺陷修复。
- **Docs**：纯文档变化，可选。

每条使用中文，以 `**粗体标题** — 描述` 编写；描述具体但不堆砌实现细节。

## 步骤 4：只更新明确的项目版本锚点

使用 `apply_patch` 精确修改以下 5 类锚点（第 6 处 `.github/SECURITY.md` 是条件锚点，只在 minor / major 更迭时纳入，见本步骤末尾）：

| 文件 | 唯一允许修改的版本锚点 |
|------|------------------------|
| `SkillsForUnity/Editor/Skills/SkillsLogger.cs` | `public const string Version = "{OLD_VER}";` |
| `SkillsForUnity/package.json` | 顶层 `"version": "{OLD_VER}"` |
| `SkillsForUnity/unity-skills~/scripts/unity_skills.py` | `__version__ = "{OLD_VER}"` |
| `agent.md` | 项目表格行 `| 版本 | {OLD_VER} |` |
| `CHANGELOG.md` | 在原最新条目前插入 `## [{NEW_VER}] - {TODAY}` 与本次变更内容 |

> README×2 的"当前版本"标记已于 2.7.0 移除（README 不再承载版本锚点），`check_project_version.py` 已同步；不要往 README 回加版本标记。

CHANGELOG 的 `Changed` 中追加：

```markdown
- **版本号更新** — `SkillsLogger.Version` / `package.json` / Python helper `__version__` / `agent.md` 同步提升到 `{NEW_VER}`。
```

`{BUMP_LEVEL}` 为 `minor` 或 `major` 时，同一条目末尾补一句：`受支持版本表（.github/SECURITY.md）同步到 {NEW_MAJOR}.{NEW_MINOR}.x。`

### 条件锚点：SECURITY.md 受支持版本表（仅 minor / major 更迭）

`.github/SECURITY.md` 的 “Supported Versions | 支持的版本” 表按版本线声明支持范围，因此只在版本线本身发生变化时才需要同步：

| `{BUMP_LEVEL}` | 示例 | `.github/SECURITY.md` |
|----------------|------|------------------------|
| `major` | `2.9.1 → 3.0.0` | 必须更新受支持版本表 |
| `minor` | `2.6.3 → 2.7.0` | 必须更新受支持版本表 |
| `patch` | `2.3.1 → 2.3.2` | 不修改，在步骤 6 摘要中注明“无需变更” |

需要更新时，沿用该表现有的两行结构与“只支持当前版本线”的策略，仅替换版本数字，不改列标题、不增删行：

```markdown
| Version | Supported |
|---------|-----------|
| {NEW_MAJOR}.{NEW_MINOR}.x   | ✅ |
| < {NEW_MAJOR}.{NEW_MINOR}   | ❌ |
```

- 只改这张表。“Reporting a Vulnerability”“Response Timeline”等其它章节一律不动。
- 表下方的 Unity 维护基线说明只有在基线真的变化时才改，不随版本号机械改写。
- 不新增该表当前不存在的支持档位（如 LTS、多版本线并行支持）。若本次发布确实要继续支持上一条版本线，停下来询问用户，不要自行发明支持策略。
- `check_project_version.py` **不覆盖** SECURITY.md，这条改动只能靠人工核对 diff（见步骤 5）。

### 禁止宽泛替换

- 不得在 README、SKILL.md 或整个仓库里把所有 `{OLD_VER}` 替换成 `{NEW_VER}`。
- 第三方 SDK、NGO、PICO、Unity 包兼容性说明中的相同数字必须保留。
- `VersionCheckServiceTests` 中用于边界测试的旧版本字符串是测试数据，不随项目升版机械修改。
- 不修改 `docs/SETUP_GUIDE.md`、`docs/SETUP_GUIDE_CN.md` 中用户手动维护的 `#v...` 指定版本安装示例。
- `SkillsHttpServer.cs`、`SkillRouter.cs`、`FooterController.cs` 等运行时位置引用 `SkillsLogger.Version`，无需硬编码修改。

## 步骤 5：机器可验证的一致性检查

依次执行：

```bash
python3 .github/scripts/check_project_version.py . --expected "{NEW_VER}"
python3 .github/scripts/check_meta_files.py .
python3 .github/scripts/check_locales.py .
python3 -m json.tool SkillsForUnity/package.json >/dev/null
git diff --check
```

另外确认：

1. `CHANGELOG.md` 顶部最新版本恰好为 `{NEW_VER}`，且只新增一个该版本标题。
2. 没有创建 `v{NEW_VER}` tag，也没有 GitHub Release。
3. `git diff` 只改动预期文件与本次原有功能改动，没有第三方版本文档被误替换。
4. 若本次改动涉及版本提醒代码，运行 `VersionCheckServiceTests`；普通版本号更新无需改写其中的测试数据，完整测试由 `/release` 的预发布矩阵负责。
5. 按 `{BUMP_LEVEL}` 核对 `.github/SECURITY.md`：
   ```bash
   git diff --stat -- .github/SECURITY.md
   git diff -- .github/SECURITY.md
   ```
   - `minor` / `major`：diff 必须存在，且只包含受支持版本表两行的版本数字变化——`✅` 行为 `{NEW_MAJOR}.{NEW_MINOR}.x`，`❌` 行为 `< {NEW_MAJOR}.{NEW_MINOR}`；漏改视为本次升版未完成。
   - `patch`：diff 必须为空，`.github/SECURITY.md` 不得出现在本次改动文件列表中。

验证标准是“所有当前版本锚点都等于新版本”，不是“旧版本号在仓库中完全消失”。旧版本作为历史记录、兼容性边界或测试数据存在是正确的。

## 步骤 6：输出交接摘要

输出必须明确区分“版本候选已准备”和“更新通知已发布”：

```text
✅ 版本候选已从 {OLD_VER} 更新到 {NEW_VER}（{BUMP_LEVEL} 级更迭）
✅ 项目版本锚点一致性检查通过
✅ 本地 / 远端 tag 与 GitHub Release 均未占用 v{NEW_VER}
{SECURITY_LINE}

当前尚未向用户发送更新通知。
只有 /release 完成双矩阵检查并发布稳定 GitHub Release 后，
releases/latest 才会变为 v{NEW_VER}，Unity 更新横幅才会生效。

请审阅后提交：
git add <本次预期文件>
git commit -m "chore: bump version to {NEW_VER}"
```

`{SECURITY_LINE}` 按 `{BUMP_LEVEL}` 二选一：

- `minor` / `major`：`✅ SECURITY.md 受支持版本表已同步到 {NEW_MAJOR}.{NEW_MINOR}.x`
- `patch`：`➖ SECURITY.md 无需变更（patch 级更迭不改动受支持版本表）`

同时列出所有修改文件，并完整展示新增的 CHANGELOG 条目。

## 注意事项

- 不自动执行 `git commit`、`git tag`、`git push` 或 `gh release create`。
- 不把“package.json 已更新”“tag 已创建”和“用户会收到提醒”混为一谈。
- 更新提醒的唯一远端来源是 GitHub 最新稳定 Release，而不是最新 commit 或孤立 tag。
- `.github/SECURITY.md` 是条件锚点而非常规锚点：patch 级更迭改动它属于越权修改，minor / major 级更迭漏改它属于升版不完整。
