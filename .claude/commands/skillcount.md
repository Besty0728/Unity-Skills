# Skill Count — 技能数量统计与文档同步

你是 UnitySkills 项目的技能数量统计助手。扫描 C# 代码中的实际 Skill 数量，与文档中声称的数字对比并修正。

## 步骤 1：统计实际 Skill 数量

对每个 `*Skills.cs` 文件统计 `[UnitySkill(` 出现次数：

```bash
rg -c "\[UnitySkill\(" SkillsForUnity/Editor/Skills/ --glob "*Skills.cs" --sort path
```

记录：
- 每个文件的 Skill 数量
- 模块名（文件名去掉 `Skills.cs` 后缀）
- 总计

## 步骤 2：读取文档中的数字

检查以下文件中所有技能数量引用：

| 文件 | 搜索内容 |
|------|---------|
| `agent.md` | 总数引用（如 "714 个 REST Skills"）、模块计数表 |
| `README.md` | badge 数字、正文中的总数 |
| `README_CN.md` | 同上中文版 |
| `SkillsForUnity/unity-skills~/SKILL.md` | 总数引用 |

动态获取当前文档中声称的总数：从步骤 1 的实际统计结果出发，用 Grep 搜索文档中**与实际总数不同的旧数字**。先在上述文件中搜索常见的 skill 总数上下文（如 `\d+ 个 REST`、`\d+ Skills`、`Skills-\d+`），提取出文档当前声称的数字。

> ⚠️ **不修改** `CHANGELOG.md` 中的历史条目 — 那些是版本发布时的快照。

## 步骤 3：对比并修正

如果实际总数与文档不一致：

1. **替换总数**：将文档中所有旧总数替换为实际统计总数
2. **更新模块计数表**：更新 `agent.md` 中 `## Skills 模块` 表格的每个模块数量
3. **更新 README 模块表**：同步 `README.md` 和 `README_CN.md` 中的分类概要表

替换时注意上下文匹配，避免误替换（如版本号中的数字）。

## 步骤 4：验证

```bash
rg -n "{旧数字}" agent.md README.md README_CN.md SkillsForUnity/unity-skills~/SKILL.md
```

确认旧数字不再出现（除非是非技能计数上下文）。

## 步骤 5：输出摘要

```
📊 UnitySkills 技能数量统计
━━━━━━━━━━━━━━━━━━━━━━━━

总计：{实际总数} Skills（文档旧值：{旧数字}）

模块明细：
| 模块 | 数量 | 模块 | 数量 | 模块 | 数量 |
| ... |

已更新文件：
- agent.md ✅
- README.md ✅
- README_CN.md ✅
- SKILL.md ✅

请审阅后提交。
```

## 注意事项

- 这是**修正文档数字**的命令，不修改 C# 代码
- 不自动执行 `git commit`，只提示用户审阅后提交
- 如果实际数字与文档一致，输出 `✅ 所有文档中的技能数量已是最新（{N} Skills）`
