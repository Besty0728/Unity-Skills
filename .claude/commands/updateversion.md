# Update Version — 版本号更新 + CHANGELOG 自动生成

你是 UnitySkills 项目的版本更新助手。执行以下流程：

## 输入

用户必须提供新版本号参数（如 `/updateversion 1.6.8`）。
- 如果未提供版本号：停止执行，提示用法 `/updateversion <新版本号>`

## 步骤 1：预检查

1. 确认当前在 `beta` 分支（`git branch --show-current`），不在则停止
2. 检查是否有未提交的更改（`git status --porcelain`），如果有则**警告**用户（不阻止，因为可能正是要更新版本的改动）
3. 从 **main 分支**读取当前版本号：
   ```bash
   git show main:SkillsForUnity/Editor/Skills/SkillsLogger.cs | grep 'public const string Version'
   ```
4. 比较版本号：新版本号必须严格高于 main 的当前版本号（按语义化版本比较 major.minor.patch）
   - 如果不满足：告知用户 `当前 main 版本为 x.x.x，新版本号必须高于此值` 并停止
5. **Tag 冲突检查**：检查是否已存在 `v{NEW_VER}` tag：
   ```bash
   git tag -l "v{NEW_VER}"
   ```
   如果已存在，停止并提示用户：该版本 tag 已发布，不应重复使用已发布的版本号。
6. **变更内容检查**：预览 `git log main..beta --oneline`，如果输出为空（beta 与 main 完全一致，无任何新提交），告知用户"beta 相对 main 没有新提交，无法推断更新内容"，询问是否仍要创建一个仅包含版本号变更的空版本。

## 步骤 2：分析 beta 相对 main 的变更内容

通过 git 对比 beta 与 main 之间的差异，推断本次更新内容：

1. 获取提交列表：
   ```bash
   git log main..beta --oneline
   ```

2. 获取变更文件概览：
   ```bash
   git diff main..beta --stat
   ```

3. 对**关键变更文件**（`Editor/Skills/*.cs`、`unity-skills~/skills/**/*.md`、`unity_skills.py` 等功能文件）查看具体 diff：
   ```bash
   git diff main..beta -- <关键文件>
   ```
   - 忽略纯版本号变更、纯文档格式调整
   - 重点关注：新增/修改的 Skill 方法、参数变化、bug 修复、行为变更

4. 基于 commit messages + 实际代码 diff，按以下分类组织更新内容：
   - **Added**：新增功能、新 Skill、新参数
   - **Changed**：行为变更、API 调整、文档更新
   - **Fixed**：bug 修复
   - **Docs**（可选）：纯文档改动

5. 撰写风格参考 `CHANGELOG.md` 已有条目：
   - 每条以 **粗体标题** 开头，后跟 em dash（—）和描述
   - 描述要具体，包含技术细节但不冗长
   - 使用中文

## 步骤 3：更新版本号

按以下 **10 处**位点规范，依次更新版本号：

> ⚠️ 将下文 `{NEW_VER}` 替换为用户指定的新版本号，`{OLD_VER}` 为 main 当前版本号，`{TODAY}` 为今天日期 YYYY-MM-DD。

| 序号 | 文件 | 操作 |
|:----:|------|------|
| 1 | `SkillsForUnity/Editor/Skills/SkillsLogger.cs` | `Version = "{OLD_VER}"` → `Version = "{NEW_VER}"` |
| 2 | `agent.md` | 概览表格中版本行 `| 版本 | {OLD_VER} |` → `| 版本 | {NEW_VER} |` |
| 3 | `SkillsForUnity/package.json` | `"version": "{OLD_VER}"` → `"version": "{NEW_VER}"` |
| 4 | `CHANGELOG.md` | 在文件顶部（`## [{OLD_VER}]` 之前）插入新的 `## [{NEW_VER}] - {TODAY}` 条目，内容为步骤 2 推断的更新内容 |
| 5 | `SkillsForUnity/unity-skills~/scripts/unity_skills.py` | `__version__ = "{OLD_VER}"` → `__version__ = "{NEW_VER}"` |
| 6 | `README_CN.md` | 搜索 `{OLD_VER}` 并替换为 `{NEW_VER}`（如果存在） |
| 7 | `README.md` | 搜索 `{OLD_VER}` 并替换为 `{NEW_VER}`（如果存在） |
| 8 | `SkillsForUnity/unity-skills~/SKILL.md` | 搜索 `{OLD_VER}` 并替换为 `{NEW_VER}`（如果存在） |
| 9 | `SkillsForUnity/unity-skills~/skills/SKILL.md` | 搜索 `{OLD_VER}` 并替换为 `{NEW_VER}`（如果存在） |
| 10 | CHANGELOG 最后一条 Changed 追加 | `- **版本号更新** — ... 同步提升到 \`{NEW_VER}\`。` |

> ⚠️ **不要更新** `docs/SETUP_GUIDE.md` 和 `docs/SETUP_GUIDE_CN.md` 中的"指定版本"URL（如 `#v1.6.7`），这些由用户手动管理。

## 步骤 4：验证

执行快速检查命令，确认旧版本号只残留在 CHANGELOG.md 的历史条目中：

```bash
rg -n "{OLD_VER}" agent.md README.md README_CN.md SkillsForUnity/package.json SkillsForUnity/unity-skills~/scripts/unity_skills.py SkillsForUnity/Editor/Skills/SkillsLogger.cs SkillsForUnity/unity-skills~/SKILL.md SkillsForUnity/unity-skills~/skills/SKILL.md
```

- 上述文件中不应有任何匹配（CHANGELOG.md 不在检查列表中，历史条目保留旧版本号是正确的）
- 如果有残留，继续修复

## 步骤 5：输出摘要

以表格形式展示更新结果：

```
✅ 版本号已从 {OLD_VER} 更新到 {NEW_VER}

已更新文件：
| 文件 | 状态 |
|------|------|
| SkillsLogger.cs | ✅ |
| agent.md | ✅ |
| ... | ... |

CHANGELOG 新增内容：
{显示新增的 CHANGELOG 条目}

请审阅后提交：git add -A && git commit -m "chore: bump version to {NEW_VER}"
```

## 注意事项

- 不要自动执行 `git commit`，只提示用户审阅后提交
- CHANGELOG 内容使用中文（与项目风格一致）
- 如果 beta 相对 main 没有实质性代码变更（只有版本号变更），告知用户并询问是否继续
- `SkillsHttpServer.cs` / `SkillRouter.cs` 中的版本引用使用 `SkillsLogger.Version`，不需要修改
