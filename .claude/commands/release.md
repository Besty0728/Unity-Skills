# Release Workflow — 候选矩阵 → main/tag → 正式 Release

你是 UnitySkills 项目的正式发布助手。发布必须遵守以下顺序：

```text
版本一致性检查
  → beta 候选提交完整矩阵
  → 用户确认正式发布
  → beta/main 精确同步
  → 创建并推送 annotated tag
  → tag 自身完整矩阵
  → 创建稳定 GitHub Release
  → 核验 releases/latest 与 Release 跳转地址
```

Unity 更新提醒只读取 GitHub `releases/latest`。因此在最后一步正式 Release 创建成功之前，不能宣称用户已经收到更新通知。

## 输入

用户可提供不带 `v` 的版本号，例如 `/release 2.6.0`；未提供时从 `CHANGELOG.md` 顶部最新的 `## [x.y.z]` 解析。

- 版本必须严格为 `MAJOR.MINOR.PATCH`。
- tag 固定为 `v{VERSION}`。

## 阶段 1：只读预检查

1. 确认当前在 `beta` 分支。
2. 工作区必须完全干净：
   ```bash
   git status --porcelain
   ```
   有任何修改或未跟踪文件都停止，提示先提交或处理。
3. 检查 GitHub CLI 登录状态并获取最新远端引用：
   ```bash
   gh auth status
   git fetch origin main beta --tags
   ```
   任一失败都停止。
4. 将候选提交固定为变量并在整个流程中保持不变：
   ```bash
   RELEASE_SHA=$(git rev-parse beta)
   VERSION_TAG="v{VERSION}"
   ```
5. 确认本地 `beta` 没有遗漏远端提交，也没有基于过期的 main 发布：
   ```bash
   git log beta..origin/beta --oneline
   git log beta..origin/main --oneline
   ```
   任一有输出都停止并列出提交。
6. 检查本地 `main` 是否存在未推送提交。后续会把本地 `main` 精确移动到候选提交；若存在必须先交给用户处理：
   ```bash
   git log origin/main..main --oneline
   ```
7. 验证项目版本锚点、CHANGELOG 顶部版本及 Release tag 格式一致：
   ```bash
   python3 .github/scripts/check_project_version.py . \
     --expected "{VERSION}" \
     --tag "${VERSION_TAG}"
   ```
8. 查询当前 GitHub 最新稳定 Release。候选版本必须严格高于它：
   ```bash
   gh api repos/Besty0728/Unity-Skills/releases/latest \
     --jq '{tag_name,html_url,draft,prerelease}'
   ```
9. 同时确认本地 tag、远端 tag、GitHub Release 都不存在：
   ```bash
   git tag -l "${VERSION_TAG}"
   git ls-remote --tags origin \
     "refs/tags/${VERSION_TAG}" \
     "refs/tags/${VERSION_TAG}^{}"
   gh release view "${VERSION_TAG}"
   ```
   任一已存在都停止。绝不删除、移动或强制重推已发布 tag。

## 阶段 2：生成并审阅 Release Note

从 CHANGELOG 当前版本条目生成 `.releases/v{VERSION}.md`。该目录由 `.gitignore` 忽略。

格式：

```markdown
# v{VERSION} — {一句话总结，提炼 2-4 个核心特性}

## ⭐ Highlights

- **{特性标题}**：{用户能获得的能力或修复}

## Added

{从 CHANGELOG Added 提炼}

## Changed

{从 CHANGELOG Changed 提炼}

## Fixed

{存在时加入}

## Docs

{存在时加入}

## Compatibility

{涉及 Unity 版本、可选包或破坏性变化时加入}

### 完整更改日志见 https://github.com/Besty0728/Unity-Skills/blob/main/CHANGELOG.md
```

Highlights 使用用户语言，突出“能做什么”，不要照搬内部实现；大量新增 Skill 可用详细版，基础设施更新用精简版。

显示完整 Release Note 给用户审阅。正式发布确认之前可以继续修改该临时文件，但不得修改已固定的 `RELEASE_SHA` 对应源码；若源码发生变化，必须从阶段 1 重新开始并重新跑矩阵。

## 阶段 3：推送 beta 并运行预发布完整矩阵

1. 将固定候选提交推送到远端 `beta`，然后验证远端精确一致：
   ```bash
   git push origin beta
   git fetch origin beta
   test "$(git rev-parse origin/beta)" = "${RELEASE_SHA}"
   ```
2. 记录当前最新的 workflow run ID，再从 `beta` 手动触发完整矩阵：
   ```bash
   BEFORE_RUN_ID=$(gh run list \
     --workflow unity-package-compile.yml \
     --limit 1 \
     --json databaseId \
     --jq '.[0].databaseId // 0')

   gh workflow run unity-package-compile.yml --ref beta
   ```
3. 轮询寻找同时满足以下条件的新运行：
   - workflow 为 `unity-package-compile.yml`
   - event 为 `workflow_dispatch`
   - `headSha == RELEASE_SHA`
   - `databaseId > BEFORE_RUN_ID`

   找不到、超时或 SHA 不一致时停止，不能误用旧的绿色运行。
4. 等待该运行完成：
   ```bash
   gh run watch "${PREFLIGHT_RUN_ID}" --exit-status
   ```
   任一静态检查、Unity 版本或最终汇总失败都停止。修复后生成新提交，并从阶段 1 重新开始。

## 阶段 4：正式发布确认闸门

在执行任何 main 重写或 tag 创建前，向用户展示：

- `{VERSION}` 与 `RELEASE_SHA`
- Release Note 文件和完整内容
- 预发布矩阵的成功 URL
- `origin/main` 将从哪个 SHA 精确移动到哪个 SHA
- 接下来会创建不可复用的 `v{VERSION}` tag 并在检查成功后创建正式 GitHub Release

必须获得用户明确的“确认正式发布”后才能继续。普通的“看看”“准备一下”“生成说明”不构成授权。

## 阶段 5：安全同步 beta → main

用户确认可能跨越一个对话回合，不能假设之前的 shell 变量或远端状态仍然有效。继续前重新建立并核验固定值：

```bash
RELEASE_SHA="{阶段 4 展示并获确认的完整 SHA}"
VERSION_TAG="v{VERSION}"

git fetch origin main beta --tags
test -z "$(git status --porcelain)"
test "$(git branch --show-current)" = "beta"
test "$(git rev-parse beta)" = "${RELEASE_SHA}"
test "$(git rev-parse origin/beta)" = "${RELEASE_SHA}"
test -z "$(git log beta..origin/main --oneline)"
test -z "$(git tag -l "${VERSION_TAG}")"
test -z "$(git ls-remote --tags origin "refs/tags/${VERSION_TAG}" "refs/tags/${VERSION_TAG}^{}")"
```

同时通过 `gh run view "${PREFLIGHT_RUN_ID}"` 再次确认预发布矩阵仍是同一 `RELEASE_SHA` 且结论为 `success`。任一条件变化都使之前的确认失效，回到阶段 1 重新审计。

不使用裸 `--force`，使用已 fetch 的 main SHA 作为租约，防止检查后远端 main 被其他人更新：

```bash
MAIN_REMOTE_BEFORE=$(git rev-parse origin/main)

git push \
  --force-with-lease=main:"${MAIN_REMOTE_BEFORE}" \
  origin "${RELEASE_SHA}:refs/heads/main"

git fetch origin main beta
git update-ref refs/heads/main "${RELEASE_SHA}"
```

随后四者必须完全相同：

```bash
git rev-parse main beta origin/main origin/beta
```

并再次确认当前 `HEAD` 仍在 `beta`、工作区干净。租约失败意味着远端 main 在确认后发生变化，必须停止并重新审计，不能升级为裸 `--force`。

## 阶段 6：创建并验证 annotated tag

tag 必须显式打在已验证的 `RELEASE_SHA` 上，不能依赖 `gh release create --target`：

```bash
git tag -a "${VERSION_TAG}" "${RELEASE_SHA}" -m "${VERSION_TAG}"
git push origin "refs/tags/${VERSION_TAG}"
```

本地验证：

```bash
test "$(git rev-parse "${VERSION_TAG}^{}")" = "${RELEASE_SHA}"
git merge-base --is-ancestor "${VERSION_TAG}^{}" origin/main
```

远端验证必须展开 annotated tag，不能只比较 tag 对象 SHA：

```bash
TAG_OBJECT=$(gh api \
  "repos/Besty0728/Unity-Skills/git/ref/tags/${VERSION_TAG}" \
  --jq '.object.sha')
TAG_TYPE=$(gh api \
  "repos/Besty0728/Unity-Skills/git/ref/tags/${VERSION_TAG}" \
  --jq '.object.type')
test "${TAG_TYPE}" = "tag"
TAG_COMMIT=$(gh api \
  "repos/Besty0728/Unity-Skills/git/tags/${TAG_OBJECT}" \
  --jq '.object.sha')
test "${TAG_COMMIT}" = "${RELEASE_SHA}"
```

若本地 tag 已创建但 push 因网络错误失败，只有在远端仍不存在同名 tag、且本地 `v{VERSION}^{}` 精确等于 `RELEASE_SHA` 时，才可重试同一条 push；不得重建或移动它。

## 阶段 7：等待 tag 自身的完整矩阵

推送 tag 会自动触发 `Unity Package Compile Matrix`。轮询并锁定：

- event 为 `push`
- `headBranch == v{VERSION}`
- `headSha == RELEASE_SHA`

然后执行：

```bash
gh run watch "${TAG_RUN_ID}" --exit-status
```

- 成功后才能创建 GitHub Release。
- 网络、runner 或许可证等瞬时基础设施问题，可在确认源码无误后 rerun 同一个 tag run。
- 如果是源码或测试真实失败，停止发布，不得移动 tag；修复后使用新的 patch 版本重新走完整流程。

## 阶段 8：创建正式稳定 GitHub Release

tag 矩阵成功后，使用已经存在且已验证的 tag 创建 Release：

```bash
gh release create "${VERSION_TAG}" \
  --verify-tag \
  --latest \
  --title "${VERSION_TAG}" \
  --notes-file ".releases/${VERSION_TAG}.md"
```

不得使用 `--prerelease` 或 `--draft`。不得传 `--target`，因为 tag 已显式创建并验证。

若命令因网络中断返回不确定结果，先执行 `gh release view "${VERSION_TAG}"` 判断 Release 是否已经创建，再决定是否重试，不得盲目重复发布操作。

## 阶段 9：核验更新提醒真实数据源

GitHub API 可能有短暂传播延迟；在有限时间内轮询：

```bash
gh api repos/Besty0728/Unity-Skills/releases/latest \
  --jq '{tag_name,html_url,draft,prerelease}'
```

最终必须同时满足：

- `tag_name == v{VERSION}`
- `draft == false`
- `prerelease == false`
- `html_url == https://github.com/Besty0728/Unity-Skills/releases/tag/v{VERSION}`
- 远端 tag 展开后的 commit 等于 `RELEASE_SHA`
- `main`、`beta`、`origin/main`、`origin/beta` 均等于 `RELEASE_SHA`
- tag 触发的完整矩阵结论为 `success`

只有全部满足后，才能报告：Unity 的稳定版更新提醒现在会把旧版本用户引导到这个 Release 页面。客户端有 24 小时成功缓存，因此已打开的 Unity 不保证立刻刷新。

## 最终输出

输出：

- 正式 Release URL
- `VERSION_TAG` 与 `RELEASE_SHA`
- 预发布矩阵 URL
- tag 矩阵 URL
- main/beta/远端四方一致性结果
- `releases/latest` 核验结果
- 当前分支与工作区状态

## 不可违反的规则

- 未得到明确正式发布确认，不修改 main、不创建 tag、不创建 Release。
- 不移动、不删除、不强制重推已发布 tag。
- 不跳过候选提交矩阵，也不拿旧 run 或不同 SHA 的绿色结果代替。
- 不在 tag 矩阵失败时先发布 Release。
- 不把 tag 页面当作更新横幅目标；目标必须是 `/releases/tag/v{VERSION}` 的正式 Release 页面。
