# Meta Check — .meta GUID 健康度审计

你是 UnitySkills 项目的 .meta 文件 GUID 审计助手。扫描整个仓库的 `.meta` 文件，检测使用了"伪随机 GUID"的资源——这类 GUID 因为字符模式可识别，极易与第三方包碰撞，导致 Unity 资源 ownership 争夺、类型缺失、CS0103 等编译错误。

## 背景：为什么要做这件事

Unity 用 32 位十六进制 GUID 唯一标识每一个资源。真随机 GUID（uuid4）碰撞概率约 $2^{-128}$。但人手写或简单算法生成的"伪 GUID"（如 `a1b2c3d4e5f6...`、`0123456789abcdef...`）会因为同样的"看似合理的造法"在多个独立项目里同时出现，进而碰撞。

历史教训（v1.8.x）：`ValidationSkills.cs.meta` 因为 GUID `d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9` 与某些第三方包（如 `com.posthog.unity` 的 `WebGLExceptionIntegration.cs`）碰撞，导致用户项目里 `ValidationSkills.cs` 被 Unity 排除导入，`BatchSkills.cs` / `PerceptionSkills.cs` 出现 `CS0103: The name 'ValidationSkills' does not exist in the current context`。

## 步骤 1：扫描所有 .meta 收集 GUID

遍历仓库根目录下所有 `.meta` 文件（排除 `.git`、`Library`、`Temp`、`obj`、`Logs`），提取每个文件的 `guid:` 字段。

## 步骤 2：用启发式判定伪 GUID

一个 32 位 hex 字符串若同时满足"格式正确"且**任一**下列特征，判为可疑：

1. **连续 hex 递增**：包含长度 ≥ 4 的连续 hex 递增子串，如 `0123`、`1234`、`2345`、`3456`、`4567`、`5678`、`6789`、`789a`、`89ab`、`9abc`、`abcd`、`bcde`、`cdef`
2. **交错递增**：包含形如 `字母-数字-字母-数字-字母-数字-字母-数字` 且字母递增、数字递增的 8 字符段（例 `a1b2c3d4`、`b2c3d4e5`、`c0d1e2f3`）
3. **同字符重复**：包含 `aaaa`、`0000`、`ffff` 等连续 4 个以上相同字符
4. **字面 abcdef**：包含子串 `abcdef`（hex 字面表）
5. **长度异常**：不是恰好 32 位 hex

## 步骤 3：生成报告

```
🔍 .meta GUID 健康度审计
━━━━━━━━━━━━━━━━━━━━━━━━━━━━

📊 统计
- 扫描 .meta 文件：{N}
- 真随机（合格）：{X}
- 可疑伪 GUID：{Y}

🔴 可疑伪 GUID 清单

  | GUID | 触发模式 | 文件 |
  |------|--------|------|
  | a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6 | interleave:a1b2c3d4@0 | Editor/Skills/LightSkills.cs.meta |
  | ... | ... | ... |

⚠️  风险说明

  伪 GUID 在用户项目里若与第三方包 GUID 碰撞，会触发：
  - GUID conflicts ... (current owner) 警告
  - 我们的资源被 Unity 排除导入
  - 引用该资源的 C# 代码出现 CS0103/缺失类型错误

🛠 修复建议

  1. 用 uuid4 重新生成 GUID：
     python -c "import uuid; print(uuid.uuid4().hex)"

  2. 替换 .meta 文件中的 `guid:` 字段（保留其他字段不变）

  3. 修复前先 grep 确认 GUID 没有被任何 .asset/.prefab/.cs 引用
     （我们的包应该不会有，因为 Skills 是反射发现，不靠 GUID）

━━━━━━━━━━━━━━━━━━━━━━━━━━━━
{结论：✅ 全部合格 / ⚠️ 发现 N 个伪 GUID，建议尽快修复}
```

## 步骤 4：可选——自动修复模式

如果用户在调用时明确说 `--fix` 或"自动修复"，则：

1. 对每个可疑 GUID 用 uuid4 生成替换 GUID
2. 在仓库内 grep 确认无外部引用（如有外部引用，在报告里高亮，**不要**自动替换）
3. 仅修改 `.meta` 文件的 `guid:` 行，保留 `fileFormatVersion` / `MonoImporter` / 其他字段不变
4. 输出 old → new 映射表
5. 生成简短 CHANGELOG 候选条目

否则只生成报告，不修改文件。

## 注意事项

- **只读默认**：不带 `--fix` 时永远不动文件
- **碰撞惯例**：碰撞 ≠ 一定会出问题，但伪 GUID 因字符模式可猜测，**碰撞概率比真随机 GUID 高出多个数量级**，应一律视为待修复
- **白名单**：如果某些资源 GUID 在 Unity 引擎硬编码（如 default material），出于稳定性反而不能改 —— 但我们的 Skills 仓库里没有这种资源，可以无脑替换
- **生成的 GUID 自校验**：替换 GUID 也要再过一遍启发式（极小概率新 GUID 偶然命中模式），如有命中重新生成
- **路径表示**：报告中文件路径统一用正斜杠，便于跨平台阅读
