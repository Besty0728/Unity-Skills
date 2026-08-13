# Contributing | 贡献指南

Thank you for contributing to Unity-Skills!
感谢你对 Unity-Skills 的贡献！

## Workflow | 贡献流程

1. **Fork** this repository | Fork 本仓库
2. **Create branch | 创建分支**: `git checkout -b feat/your-feature`
3. **Commit changes | 提交更改**: `git commit -m "feat: add new feature"`
4. **Push branch | 推送分支**: `git push origin feat/your-feature`
5. **Create Pull Request | 创建 PR**

## Before Submitting PR | 提交 PR 前

> ⚠️ **Required | 必须完成**

- [ ] Import into Unity 2022.3+ - Unity 6+ and verify no errors | 在 Unity 2022.3+ 至 Unity 6+ 中导入并确认无报错
- [ ] Test Skills work correctly in your AI tool (Claude Code, Cursor, etc.) | 在你的 AI 工具中测试 Skill 能正常使用
- [ ] Run HTTP server and verify endpoints respond | 启动 HTTP 服务并验证接口响应正常

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
- Follow Unity coding conventions | 遵循 Unity 编码规范
- PascalCase for classes and methods | 类和方法使用 PascalCase
- Comments in Chinese | 使用中文注释

### Python
- Use type annotations | 使用类型注解
- Prefer async | async 优先
- Use uv for dependencies | 使用 uv 管理依赖

## Adding New Skills | 添加新 Skill

1. Create file in `SkillsForUnity/Editor/Skills/` | 在该目录下创建新文件
2. Mark method with `[UnitySkill]` attribute | 使用特性标记方法：

```csharp
[UnitySkill("skill_name", "Skill description")]
public static object YourSkill(params)
{
    // Implementation
    return new { success = true };
}
```

3. Add corresponding `.md` doc in `skills/` | 在 skills/ 目录添加文档

## Version Update | 版本号更新

Use `/updateversion <version>` to update the explicit project-version anchors below. Do not globally replace a version number: third-party SDK compatibility docs may legitimately contain the same number. | 使用 `/updateversion <版本号>` 更新下列明确的项目版本锚点。不要全局替换版本数字：第三方 SDK 兼容性文档可能合法包含相同数字。

| File | Location |
|------|----------|
| `SkillsForUnity/Editor/Skills/SkillsLogger.cs` | `Version` constant (single C# source of truth) |
| `agent.md` | Version table |
| `SkillsForUnity/package.json` | `"version"` field |
| `CHANGELOG.md` | Add new entry at top |
| `SkillsForUnity/unity-skills~/scripts/unity_skills.py` | `__version__` |
| `README.md` | Explicit `Current: v...` marker only |
| `README_CN.md` | Explicit `当前：v...` marker only |

> If Unity baseline, skill counts, advisory-module counts, or install layout change, also update the matching `.github` docs/templates. | 若 Unity 基线、技能数、advisory 模块数或安装结构有变化，也要同步更新 `.github` 下相关文档和模板。

Version consistency check | 版本一致性检查：
```bash
python3 .github/scripts/check_project_version.py . --expected 2.6.0
```

The Unity update banner compares `SkillsLogger.Version` with GitHub's latest stable Release. Updating files or creating a tag does not notify users by itself; the maintainer-only `/release` workflow runs a candidate matrix, synchronizes `main`/`beta`, runs the tag matrix, and only then creates and verifies the stable GitHub Release. | Unity 更新横幅会比较 `SkillsLogger.Version` 与 GitHub 最新稳定 Release。仅更新文件或创建 tag 不会通知用户；维护者专用 `/release` 会先运行候选矩阵、同步 `main`/`beta`、运行 tag 矩阵，最后才创建并核验稳定 GitHub Release。

## CI & Tooling Etiquette | CI 与工具使用约定

### Compile-check workflow | 编译检查工作流

The **Unity Package Compile Matrix** (`.github/workflows/unity-package-compile.yml`) compiles and tests the package across multiple Unity versions and **consumes the maintainer's Unity license quota and CI minutes**. It runs automatically for release-tag pushes and may be dispatched manually by the maintainer for a pinned pre-release candidate. | **Unity Package Compile Matrix**（`.github/workflows/unity-package-compile.yml`）会跨多个 Unity 版本编译并测试本包，**会消耗维护者的 Unity 许可证配额与 CI 时长**。它会在发布 tag push 时自动运行，也可由维护者为固定的预发布候选提交手动触发。

> Please use it responsibly | 请合理使用：
> - Do **not** push tags just to trigger CI | **不要**为了触发 CI 而随意打 tag
> - Self-test locally with the **Before Submitting PR** checklist first | 提交 PR 前请先用 **Before Submitting PR** 清单本地自测
> - The compile matrix is run by the maintainer at release time — you do not need to trigger it from a PR | 编译矩阵由维护者在发版时统一运行——贡献者无需在 PR 中触发

### Custom commands (Claude Code) | 自定义命令（Claude Code）

This repo ships maintainer slash-commands under `.claude/commands/`: `updateversion`, `skillcount`, `metacheck`, `skillcheck`, and `release`. | 本仓库在 `.claude/commands/` 下内置了维护者用的斜杠命令：`updateversion`、`skillcount`、`metacheck`、`skillcheck` 与 `release`。

> Please use them responsibly | 请合理使用：
> - `skillcount` / `metacheck` / `skillcheck` / `updateversion` may be used to self-check your changes | 这几个可用于自检你的改动
> - **Do not run `release`** — it is maintainer-only: it dispatches license-consuming Unity matrices, updates `main` with a guarded force-with-lease, creates the release tag, and publishes the GitHub Release. Running it from a fork or PR will damage the release flow. | **请勿运行 `release`**——它是维护者专用：会触发消耗许可证额度的 Unity 矩阵、通过受保护的 force-with-lease 更新 `main`、创建发布 tag 并发布 GitHub Release，从 fork 或 PR 运行会破坏发布流程。

## Feedback | 问题反馈

- Bug reports: Use Issue template | Bug 报告请使用 Issue 模板
- Feature requests welcome | 功能建议欢迎提交 Feature Request
