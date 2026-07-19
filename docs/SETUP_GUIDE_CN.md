# UnitySkills 安装与使用指南

> [English](SETUP_GUIDE.md) | 中文

---

## 环境要求

- **Unity**：`2022.3+`（推荐 LTS；完整支持 Unity 6）
- **网络**：本地回环地址 `localhost` / `127.0.0.1`
- **Python**（可选）：3.7+，需安装 `requests` 包，用于 Python 客户端

> **v2.0.0 效率提升**：Schema 缓存 + 指数退避轮询 + BATCH-FIRST 引导 → Token 消耗 ↓ 96%，简单任务调用次数 ↓ 75-83%。详见 [Release Notes](https://github.com/Besty0728/Unity-Skills/releases/tag/v2.0.0)。

---

## 1. 安装 Unity 包

在 Unity 编辑器中打开：

```
Window → Package Manager → + → Add package from git URL
```

选择以下地址之一：

| 频道 | URL |
|------|-----|
| **稳定版** (main) | `https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity` |
| **开发测试版** (beta) | `https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity#beta` |
| **指定版本** | `https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity#v1.6.8` |

也可以从 [Releases 页面](https://github.com/Besty0728/Unity-Skills/releases) 下载特定版本。

---

## 2. 打开面板并启动服务

```
Window → UnitySkills          (或按 Alt+Shift+U)
```

然后点顶栏的服务开关启动。正常启动后，Console 会输出：

```
[UnitySkills] REST Server started at http://localhost:8090/
```

验证：

```bash
curl http://localhost:8090/health
```

> **注意**：脚本编译、Domain Reload 和部分资源操作会导致服务短暂不可达。这是 Unity 编辑器的正常行为 — 等待几秒后重试即可。

---

## 3. 安装 AI Skills

### 推荐：一键安装

```
Window → UnitySkills → AI Config 标签页
```

选择目标 AI 工具后点击 **Install**。安装器会将 `unity-skills~/` 模板目录复制到正确位置。安装的文件包括：

```
SKILL.md                    # 主 Skill 定义（AI 读取）
skills/                     # 按模块分类的 Skill 文档（49 功能 + 20 顾问）
scripts/unity_skills.py     # Python 客户端库
scripts/agent_config.json   # Agent 配置
references/                 # Unity 开发参考文档
```

> **Codex 说明**：Antigravity 和 Codex 工作区共享 `.agents/skills/`，装一次即两边可用。Codex 自动扫描发现 skills，无需在 `AGENTS.md` 中声明。

> **按 scope 卸载（v1.9.0+）**：每个 Agent 卡片的"卸载"按钮按当前安装状态智能形变 —— 未安装为灰态；仅一处装则按钮自带 scope 标签直接卸载该 scope；两处都装则显示 `Uninstall ▾` 下拉，分别选择 Project / Global。允许只移除一个 scope 的 skill，不动另一个。

### 手动安装

如果一键安装不支持你的工具，可手动将 `SkillsForUnity/unity-skills~/` 目录内容复制到工具的 skills 目录。

**常见工具路径：**

| 工具 | 工作区 | 全局 |
|------|--------|------|
| Claude Code | `.claude/skills/` | `~/.claude/skills/` |
| Antigravity | `.agents/skills/` | `~/.gemini/antigravity/skills/` |
| Codex | `.agents/skills/`（与 Antigravity 共享） | `~/.agents/skills/` |
| Cursor | `.cursor/skills/` | `~/.cursor/skills/` |

### 支持的 AI 工具

以下工具已通过官方测试：

| 工具 | 状态 | 特色 |
|------|:----:|------|
| **Antigravity** | ✅ | 开放 Agent Skills 标准；工作区与 Codex 共享 `.agents/skills/` |
| **Claude Code** | ✅ | 智能 Skill 意图识别 |
| **Codex** | ✅ | `$skill` 显式调用 + 隐式意图识别；自动扫描 `.agents/skills/` |
| **Cursor** | ✅ | 自动扫描 `.cursor/skills/` 和 `.agents/skills/`；支持 `/skill-name` 显式触发；可在 设置 → Rules 查看已加载技能 |

> ⚠️ **通用兼容性**：UnitySkills 遵循开放的 Skill 标准。**任何能读取 markdown 文件并发送 HTTP 请求的 AI 工具**都可以使用 UnitySkills — 不限于上述列表。只需将 `unity-skills~/` 目录内容复制到你的工具的 skill 或 prompt 位置，确保工具能访问 `http://localhost:8090` 即可。

---

## 4. 操作模式 (v1.9.0+)

UnitySkills 在**服务端**真正做权限拦截（不再只是 AI 路由建议），对齐 Claude Code permission modes。模式切换统一从 `Window → UnitySkills` 的 ⚙ 设置按钮 → **Server** 区完成。

| 模式 | 默认场景 | 行为 |
|------|----------|------|
| **Approval（审批）** | — | AI 调 FullAuto skill → 服务端返回 `MODE_RESTRICTED` + grant token → 用户批准 → AI 调 `POST /permission/grant` 重放 token → skill 执行 |
| **Auto（自动）** | 新安装 | AI 直接执行 FullAuto skill；服务端仅拦自动判定的 NeverInSemi（`Delete` / `MayEnterPlayMode` / `MayTriggerReload` / `RiskLevel="high"` + 兜底名单约 5 条） |
| **Bypass（全部直接放行）** | < v1.9 升级用户 | 全部放行，仅保留高危 `ConfirmationToken` 二次确认 |

**Approval 模式双轨审批**：
- **Dialog 渠道**（默认）—— AI 对话说明意图 + grant token，用户文字同意后 AI 直接重放 token
- **Panel 渠道**（面板开启 `Panel Approval Required`）—— grant token 必须在 Server 面板点 `[Approve]` 才生效，AI 提前调 grant 返回 `GRANT_PENDING_APPROVAL`

**老用户零感知升级**：插件检测旧版 `UnitySkills_*` EditorPrefs key 自动识别老安装，默认保持 **Bypass**，pre-v1.9 Full-Auto 行为完全保留，无需操作。

### 权限相关 REST 端点

```bash
curl http://localhost:8090/permission/status      # currentMode / panelApprovalRequired / pending / granted
curl -X POST http://localhost:8090/permission/grant -d '{"token":"..."}'
curl -X POST http://localhost:8090/permission/approve -d '{"token":"..."}'    # 面板侧批准
curl -X POST http://localhost:8090/permission/deny    -d '{"token":"..."}'    # 面板侧拒绝
curl -X POST http://localhost:8090/permission/revoke  -d '{"skill":"..."}'    # 撤销已授权 skill
curl http://localhost:8090/permission/audit?limit=100
```

`GET /health` 现在还返回 `currentMode` / `panelApprovalRequired` / `pendingCount` / `grantedCount` —— AI 启动时一次拉取即可获取完整权限状态。

### 审计日志

所有 grant / revoke / 被拒命中 / skill 调用都会写入 `Library/UnitySkillsAudit.jsonl`（per-project，不入 Git，异步追加，1MB 滚动，保留 3 份历史）。

从 ⚙ 设置抽屉 → **查看审计日志**（或按 Alt+Shift+L）打开 Console 风格的浏览器：

- 按事件类型或自由文本过滤
- 鼠标悬停某行 → 点末尾 **✕** 删除该单条
- 工具栏 **🗑 Clear All** 清空主日志 + 全部历史滚动文件
- 删除动作本身会写 `audit_deleted` / `audit_cleared` 追踪事件回日志 —— 删除行为本身被审计，日志依然是 trust anchor

> ❌ 不再识别对话触发词（如 `"全自动模式"` / `"semi-auto"`），模式切换必须走 ⚙ 设置抽屉的 Server 区。

---

## 5. Python 客户端

### 基本用法

```python
import unity_skills

# 检查服务器状态
unity_skills.health()

# 调用技能
unity_skills.call_skill("gameobject_create",
    name="MyCube", primitiveType="Cube", x=0, y=1, z=0)

# 获取所有可用技能
unity_skills.get_skills()
```

### 过滤查询与推荐

```python
# 按元数据过滤技能
unity_skills.get_skills(category="GameObject", operation="Create")
unity_skills.get_skills(tags="batch")
unity_skills.get_skills(read_only=True)
unity_skills.get_skills(q="screenshot")

# 意图推荐（服务端评分排序）
unity_skills.find_skills("create red cube", top_n=5)

# 查找产出特定字段的技能链
unity_skills.get_skill_chain("instanceId")
```

### 工作流上下文

```python
# 批量操作分组，支持一键撤销/重做
with unity_skills.workflow_context("Build Scene", "Create player and environment"):
    unity_skills.call_skill("gameobject_create", name="Player")
    unity_skills.call_skill("component_add", name="Player", componentType="Rigidbody")
# 所有操作可通过 workflow_undo_task 一次性回滚
```

快照现按类型分级（新建/移动/删除/修改/设置），资产字节存入去重的内容寻址文件存储，不再内嵌进历史 JSON，因此历史文件保持精简并会自动清理。删除操作现在可完整恢复，包括 `.cs` 脚本。**编辑器/项目设置类操作现在也可回退** —— 例如质量等级、渲染管线、物理重力/层碰撞、调试宏定义、控制台开关、新增标签等。使用 `workflow_clear_history` 可清空全部跟踪数据（不可逆；不会撤销已应用的修改）。

**已知限制：** `scene_save` 的 undo 恢复磁盘上的 `.unity` 文件，若场景正在打开需手动 Reload Scene 才生效；在从未保存过的场景中新建的对象，其标识跨编辑器重启会失效，undo 时会在明细中标记为失败；Package Manager 等外部副作用无法回滚。

### CLI 用法

```bash
python unity_skills.py --list
python unity_skills.py gameobject_create name=MyCube primitiveType=Cube
```

---

## 6. REST API

### 直接 HTTP 调用

```bash
# 健康检查
curl http://localhost:8090/health

# 获取所有技能
curl http://localhost:8090/skills

# 过滤技能
curl "http://localhost:8090/skills?category=GameObject&operation=Create"

# 意图推荐
curl "http://localhost:8090/skills/recommend?intent=create+cube&topN=5"

# 执行技能
curl -X POST http://localhost:8090/skill/gameobject_create \
  -H "Content-Type: application/json" \
  -d '{"name":"MyCube","primitiveType":"Cube","x":1,"y":2,"z":3}'
```

### 响应格式

所有技能返回统一格式：

```json
{
  "status": "success",
  "result": {
    "success": true,
    "name": "MyCube",
    "instanceId": 12345,
    "position": {"x": 1, "y": 2, "z": 3}
  }
}
```

---

## 7. 核心概念

### Domain Reload 与短暂不可达

以下操作可能触发 Unity 编译并短暂中断服务：

- `script_create`、`script_append`、`script_replace`
- `debug_force_recompile`、`debug_set_defines`
- 部分 `asset_import` / `asset_reimport` / `asset_move` 操作
- 包安装/移除

**建议处理方式**：等待几秒后，调用 `wait_for_unity()` 或使用 `call_skill_with_retry()`。

### 批量优先原则

操作 2 个及以上对象时，始终优先使用 `*_batch` 技能：

```python
# ✅ 正确 — 单次请求
unity_skills.call_skill("gameobject_create_batch", items=[
    {"name": "A", "primitiveType": "Cube", "x": -1},
    {"name": "B", "primitiveType": "Cube", "x": 1},
])

# ❌ 避免 — 多次请求
for name in ["A", "B"]:
    unity_skills.call_skill("gameobject_create", name=name)
```

### 多实例路由

同时打开多个 Unity 项目时：

```python
unity_skills.set_unity_version("2022.3")   # 按 Unity 版本路由
unity_skills.list_instances()               # 枚举所有实例
```

### 测试模块

`test_run` 和 `test_run_by_name` 是异步的 — 立即返回 `jobId`，使用 `test_get_result(jobId)` 轮询结果。

---

## 8. 常见排障

| 问题 | 现象 | 建议 |
|------|------|------|
| 连接失败 | `Cannot connect to http://localhost:8090` | 检查服务是否已启动；可能正在编译 / Domain Reload |
| 请求超时 | 超过 15 分钟后无响应 | 确认是否为长任务；在 Unity 面板中调高超时设置 |
| 技能列表为空 | `/skills` 返回异常 | 检查 Console 是否有编译错误 |
| 脚本创建后断连 | `script_create` 后服务不可达 | 正常现象 — 等待编译完成后重试 |
| 连接到错误实例 | 请求打到了错误项目 | 使用 `set_unity_version()` 或按项目名连接 |
| 工作流状态异常 | 客户端/服务端状态不一致 | 读取 `workflow_session_status`；客户端已内置恢复逻辑 |
| 权限被拒 | 响应含 `errorCode: MODE_RESTRICTED` / `MODE_FORBIDDEN` / `GRANT_PENDING_APPROVAL` | 用 `GET /permission/status` 查看当前模式；Approval 模式下重放 grant token；若开了 `Panel Approval Required`，需用户在 Server 标签页点 Approve |

---

## 9. 参考索引

| 资源 | 说明 |
|------|------|
| [README.md](../README.md) | 项目说明（英文） |
| [README_CN.md](../README_CN.md) | 项目说明（中文） |
| [SKILL.md](../SkillsForUnity/unity-skills~/SKILL.md) | 完整 Skill API 参考 |
| [CHANGELOG.md](../CHANGELOG.md) | 版本更新记录 |
| [agent.md](../agent.md) | AI Agent 项目概览 |
