---
description: Meta Check — .meta GUID 健康度审计（伪随机 GUID + 第三方黑名单）
argument-hint: "[--fix]"
allowed-tools: Bash(python3 .github/scripts/check_meta_files.py:*), Bash(git grep:*), Read, Grep, Edit
---

# Meta Check

伪随机 GUID（`a1b2c3d4…`、`0123…`）或与第三方包逐字节相同的 GUID，会在用户项目里与其他包争夺资源 ownership，表现为 missing script、CS0103。机械检查全部在脚本里，本命令只负责解释结果与修复。

## 步骤 1：运行脚本

```bash
python3 .github/scripts/check_meta_files.py . --strict
```

脚本覆盖：`.meta` 配对、GUID 重复、第三方黑名单（`KNOWN_CONFLICT_GUIDS`，命中即失败）、伪 GUID 启发式（`sequential` / `interleave` / `repeat` / `abcdef` / `length`）。不带 `--strict` 时启发式只告警，CI 即如此运行。

## 步骤 2：解释结果

- 逐条列出 GUID、触发模式、文件；黑名单命中说明来源。
- 启发式对真随机 GUID 有小概率误中（如 `sequential` 约 0.6%/个）；仍一律视为待修复，换一个 uuid4 的代价近乎为零。
- 无发现则报告「全部合格」并结束。

## 步骤 3：修复（仅带 `--fix`）

不带 `--fix` 永不修改文件，只给出建议。带 `--fix` 时对每个被标记的 GUID：

1. `git grep -n <guid>` 查引用（排除其自身 `.meta`）。**有外部引用则不替换**，高亮并询问用户。
2. 无引用则生成 `python3 -c "import uuid; print(uuid.uuid4().hex)"`，只改该 `.meta` 的 `guid:` 行，其余字段不动。新值需过脚本，命中启发式则重新生成。
3. 输出 old → new 映射表与一条 CHANGELOG 候选条目。
4. 重跑步骤 1 确认全部通过。

## 维护黑名单

fork 上游或引入可能同装的第三方 UPM 包时，把其 `.meta` GUID 补进脚本的 `KNOWN_CONFLICT_GUIDS`（附来源与原因）。收集方式：在任意临时目录浅克隆上游，然后 `grep -rh "^guid:" <克隆目录> --include="*.meta" | sort -u`。黑名单只防确定性冲突，启发式查不出。

## 注意

- 路径统一用正斜杠。
- 本仓库 Skills 靠反射发现，不依赖 GUID 引用，通常可直接替换。
