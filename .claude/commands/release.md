# Release Workflow — beta → main 同步 + Release Note 生成

你是 UnitySkills 项目的发布助手。执行以下流程：

## 输入

用户可能提供版本号参数（如 `/release 1.6.7`），也可能不提供。
- 如果提供了版本号：用该版本号
- 如果未提供：从 `CHANGELOG.md` 顶部最新的 `## [x.x.x]` 条目自动解析

## 步骤 1：预检查

1. 确认当前在 `beta` 分支
2. 检查是否有未提交的更改（`git status`），如果有则停止并提示用户先提交
3. 从 `CHANGELOG.md` 读取最新版本条目（从 `## [x.x.x]` 到下一个 `## [` 之间的内容）
4. 确认版本号与 `SkillsForUnity/Editor/Skills/SkillsLogger.cs` 中的 `Version` 一致
5. **远程同步检查**：执行 `git fetch origin main`，然后检查 main 上是否有 beta 不包含的提交：
   ```bash
   git log beta..origin/main --oneline
   ```
   如果有输出，说明 main 上存在 beta 没有的提交，hard reset 会丢失这些提交。**停止并警告用户**，列出这些提交，让用户决定是否继续。

   同时检查本地 main 是否有未推送的提交（步骤 2 的 `reset --hard` 会直接销毁它们）：
   ```bash
   git log origin/main..main --oneline
   ```
   有输出同样停止并交给用户判断。
6. **Tag 冲突检查**：检查本地与远程是否已存在 `v{VERSION}` tag：
   ```bash
   git tag -l "v{VERSION}"
   git ls-remote --tags origin "refs/tags/v{VERSION}"
   ```
   如果已存在，**停止**并提示用户改用新的版本号。**绝不删除或移动已发布的 tag** —— 已发布的 tag 可能已被用户以 `#v{VERSION}` 形式安装，移动它会让同一个版本号在不同人手里指向不同代码。
7. **gh CLI 检查**：执行 `gh auth status`，确认 GitHub CLI 已登录。如果未登录，提示用户先运行 `! gh auth login`。

## 步骤 2：beta → main 同步

执行以下 git 操作（这是项目规定的同步方式，见 agent.md）：

```bash
git fetch origin
git checkout main
git reset --hard beta
git push origin main --force
git checkout beta
```

同步后**必须验证**三者指向同一 commit，不一致则停止：

```bash
git rev-parse main beta origin/main   # 三行输出必须完全相同
```

> ⚠️ 本地 `main` 长期不同步是常见陷阱：若跳过 `git checkout main` 这一步（例如在另一台机器上发过版），本地 `main` 会停在旧版本，导致后续"tag 打在哪"的判断全部失真。上面的三方比对就是为了挡住这种情况。

## 步骤 3：在 main 上打 tag

**tag 必须显式创建在 main 上，不要依赖 `gh release create --target`**——`--target` 只在 tag 尚不存在时生效，一旦 tag 已存在（哪怕指向错误的 commit）它会被静默忽略，发布出去的就是错版本。

```bash
git tag -a v{VERSION} main -m "v{VERSION}"
git push origin v{VERSION}
```

创建后验证 tag 确实可从 main 到达，失败则停止：

```bash
git merge-base --is-ancestor v{VERSION} origin/main && echo "OK: tag 在 main 上"
```

> 📌 因为 main 是 beta 的 fast-forward 副本，被打 tag 的那个 commit **必然同时出现在 beta 上**——这是该同步模型的固有结果，不是错误。判断标准只有一条：**能否从 main 到达**（上面的 `--is-ancestor` 检查）。在 `git log --graph` 里看到 tag 挨着 beta 是正常现象。

## 步骤 4：生成 Release Note

根据 CHANGELOG.md 的内容，按以下格式生成 Release Note：

### 格式模板

```markdown
# v{VERSION} — {一句话总结，用顿号分隔 3-4 个核心特性}

## ⭐ Highlights

- **{特性1标题}**：{一句话描述核心价值和影响}
- **{特性2标题}**：{一句话描述核心价值和影响}
- **{特性3标题}**：{一句话描述核心价值和影响}

## Added

{从 CHANGELOG ### Added 提取，每条保持简洁，去掉过度技术细节}

## Changed

{从 CHANGELOG ### Changed 提取}

## Fixed（如果有）

{从 CHANGELOG ### Fixed 提取}

## Docs（如果有）

{从 CHANGELOG ### Docs 提取}

{如有相关 Issue}
**#{issue_number} 此问题在该版本得到解决**

### 完整更改日志见 https://github.com/Besty0728/Unity-Skills/blob/main/CHANGELOG.md
```

### Highlights 撰写规则

- 从 Added/Changed/Fixed 中提炼 **2-4 个最有用户感知的特性**
- 用用户能理解的语言，而非纯技术描述
- 突出 **"能做什么"** 而非 "改了什么代码"
- 如果有数据量化（如技能数、性能提升），优先使用

### Added 分区详细程度（AI 自动判断）

根据版本内容量级自动选择详细程度：

**详细版**（新增大量 Skill 的大版本，如 +20 skills 以上）：
- 每个新模块下逐个列举 skill 名称和一句话说明
- 适合用户了解具体新增了什么能力
- 参考 v1.6.3 格式

**精简版**（基础设施/元数据/重构类更新）：
- 每个特性用 1-2 句话概括，不逐个列举
- 适合改动虽多但用户感知在宏观层面的版本
- 参考 v1.6.5 格式

判断标准：如果 Added 中有 **新增功能模块或大量新 Skill**，用详细版；如果是 **元数据增强、API 改进、文档优化** 等基础设施类，用精简版。

### Compatibility 分区（可选）

如果版本涉及兼容性变化（新增可选依赖包、Unity 版本支持等），在末尾添加 Compatibility 分区：

```markdown
## Compatibility

- ✅ Unity 2022.3+：...
- ✅ 向后兼容：...
```

## 步骤 5：输出

1. 将生成的 Release Note 写入 `.releases/v{VERSION}.md` 文件
2. 在终端显示完整的 Release Note 文本
3. 提示用户：
   - 文件已保存到 `.releases/v{VERSION}.md`（该目录已被 .gitignore 忽略）
   - 审阅并修改后可用以下命令发布（步骤 3 已创建并推送 tag，此处不再传 `--target`）：
     ```bash
     gh release create v{VERSION} --title "v{VERSION}" --notes-file .releases/v{VERSION}.md
     ```

## 注意事项

- 不要自动执行 `gh release create`，只输出建议命令
- **tag 由步骤 3 显式创建在 main 上**，不要让 `gh release create` 代劳建 tag
- 绝不移动或重新推送已发布的 tag；版本号有问题就发新版本号
- Release Note 使用中文（与项目文档风格一致）
- Highlights 要精炼有吸引力，不要照搬 CHANGELOG 原文
- 如果 CHANGELOG 条目很短，Highlights 可以只写 2 条
