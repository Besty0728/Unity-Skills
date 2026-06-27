# Meta Check — .meta GUID 健康度审计

你是 UnitySkills 项目的 .meta 文件 GUID 审计助手。扫描整个仓库的 `.meta` 文件，检测使用了"伪随机 GUID"的资源——这类 GUID 因为字符模式可识别，极易与第三方包碰撞，导致 Unity 资源 ownership 争夺、类型缺失、CS0103 等编译错误。同时对照"已知第三方包 GUID 黑名单"，抓出 fork/衍生场景下与上游逐字节相同的确定性冲突。

## 背景：为什么要做这件事

Unity 用 32 位十六进制 GUID 唯一标识每一个资源。真随机 GUID（uuid4）碰撞概率约 $2^{-128}$。但人手写或简单算法生成的"伪 GUID"（如 `a1b2c3d4e5f6...`、`0123456789abcdef...`）会因为同样的"看似合理的造法"在多个独立项目里同时出现，进而碰撞。

历史教训（v1.8.x）：`ValidationSkills.cs.meta` 因为 GUID `d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9` 与某些第三方包（如 `com.posthog.unity` 的 `WebGLExceptionIntegration.cs`）碰撞，导致用户项目里 `ValidationSkills.cs` 被 Unity 排除导入，`BatchSkills.cs` / `PerceptionSkills.cs` 出现 `CS0103: The name 'ValidationSkills' does not exist in the current context`。

另一类碰撞（v2.0.x，issue #41）：fork/衍生项目若连同上游 `.meta` 一起复制，会出现与上游**逐字节相同**的 GUID。这种 GUID 本身格式规范、也不命中下文任何伪随机启发式（例如 `a2f7ae0675bf4fb478a0a1df7a3f6c64`），步骤 2 的启发式审计无法发现，只能靠步骤 2b 的"已知第三方包 GUID 黑名单"对照。本项目曾因 `package.json.meta` 沿用上游 `com.coplaydev.unity-mcp` 的 GUID，导致两包同装时 Unity 报 6 条 missing script 警告。

## 步骤 1：扫描所有 .meta 收集 GUID

遍历仓库根目录下所有 `.meta` 文件（排除 `.git`、`Library`、`Temp`、`obj`、`Logs`），提取每个文件的 `guid:` 字段。

## 步骤 2：用启发式判定伪 GUID

一个 32 位 hex 字符串若同时满足"格式正确"且**任一**下列特征，判为可疑：

1. **连续 hex 递增**：包含长度 ≥ 4 的连续 hex 递增子串，如 `0123`、`1234`、`2345`、`3456`、`4567`、`5678`、`6789`、`789a`、`89ab`、`9abc`、`abcd`、`bcde`、`cdef`
2. **交错递增**：包含形如 `字母-数字-字母-数字-字母-数字-字母-数字` 且字母递增、数字递增的 8 字符段（例 `a1b2c3d4`、`b2c3d4e5`、`c0d1e2f3`）
3. **同字符重复**：包含 `aaaa`、`0000`、`ffff` 等连续 4 个以上相同字符
4. **字面 abcdef**：包含子串 `abcdef`（hex 字面表）
5. **长度异常**：不是恰好 32 位 hex

## 步骤 2b：已知第三方包 GUID 黑名单对照

启发式只能抓"可猜测的伪 GUID"，抓不了"与上游/第三方包逐字节相同"的确定性冲突。因此维护一份已知冲突 GUID 表，把本仓库所有 GUID 与之对照，命中即标记为"🔴 已知冲突 GUID"。

### 已知冲突 GUID 黑名单

| GUID | 来源 | 备注 |
|------|------|------|
| `a2f7ae0675bf4fb478a0a1df7a3f6c64` | `com.coplaydev.unity-mcp` 的 `package.json.meta`（本项目 fork 上游） | issue #41：两包同装触发 6 条 missing script 警告；已在 issue #41 修复中替换为本仓库独立 GUID `4a81947acf6349c4b37a44f35a71d7e8` |

### 如何扩充黑名单

fork/衍生项目应把上游仓库所有 `.meta` 的 GUID 纳入此表。一次性收集：

```bash
git clone --depth 1 <上游仓库 URL> /tmp/upstream
grep -rh "^guid:" /tmp/upstream --include="*.meta" | sort -u
```

把结果按"GUID → 来源文件"补进上表。同理，凡本项目依赖且可能同装的第三方 UPM 包，其 `.meta` GUID 都应纳入。

## 步骤 3：生成报告

```
🔍 .meta GUID 健康度审计
━━━━━━━━━━━━━━━━━━━━━━━━━━━━

📊 统计
- 扫描 .meta 文件：{N}
- 真随机（合格）：{X}
- 可疑伪 GUID：{Y}
- 已知冲突 GUID：{Z}

🔴 可疑伪 GUID 清单

  | GUID | 触发模式 | 文件 |
  |------|--------|------|
  | a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6 | interleave:a1b2c3d4@0 | Editor/Skills/LightSkills.cs.meta |
  | ... | ... | ... |

🔴 已知冲突 GUID（与第三方包逐字节相同）

  | GUID | 冲突来源 | 文件 |
  |------|---------|------|
  | a2f7ae0675bf4fb478a0a1df7a3f6c64 | com.coplaydev.unity-mcp/package.json.meta | SkillsForUnity/package.json.meta |
  | ... | ... | ... |

⚠️  风险说明

  伪 GUID 在用户项目里若与第三方包 GUID 碰撞，会触发：
  - GUID conflicts ... (current owner) 警告
  - 我们的资源被 Unity 排除导入
  - 引用该资源的 C# 代码出现 CS0103/缺失类型错误

  与第三方包逐字节同 GUID（黑名单命中）会触发：
  - Unity 把两个包的资产判为同一 ownership，资产互相覆盖
  - Behaviour 上出现 missing script 警告（见 issue #41）

🛠 修复建议

  1. 用 uuid4 重新生成 GUID：
     python -c "import uuid; print(uuid.uuid4().hex)"

  2. 替换 .meta 文件中的 `guid:` 字段（保留其他字段不变）

  3. 修复前先 grep 确认 GUID 没有被任何 .asset/.prefab/.cs 引用
     （我们的包应该不会有，因为 Skills 是反射发现，不靠 GUID）

  注：黑名单命中的 GUID（fork 沿用上游）走同样的 uuid4 替换流程——
     换成本仓库独立 GUID 即可彻底解除与上游的冲突。

━━━━━━━━━━━━━━━━━━━━━━━━━━━━
{结论：✅ 全部合格 / ⚠️ 发现 N 个伪 GUID，建议尽快修复 / 🔴 发现 M 个已知冲突 GUID，必须修复}
```

## 步骤 4：可选——自动修复模式

如果用户在调用时明确说 `--fix` 或"自动修复"，则：

1. 对每个可疑 GUID（含黑名单命中的冲突 GUID）用 uuid4 生成替换 GUID
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
- **黑名单需维护**：每次新增 fork 上游、或引入可能同装的第三方 UPM 包，都把其 `.meta` GUID 补进步骤 2b 黑名单——启发式查不出此类确定性冲突，只能靠黑名单
