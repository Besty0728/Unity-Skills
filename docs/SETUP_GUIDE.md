# UnitySkills 安装与排障指南

本文档面向本地开发环境，说明如何安装 UnitySkills、如何把 AI Skill 模板放到目标工具、以及在编译或 Domain Reload 期间应该如何处理短暂不可达。

## 环境要求

- Unity：`2022.3+`
- 推荐重点验证版本：`2022.3 LTS` 与 `Unity 6`
- 网络环境：本地回环地址 `localhost` / `127.0.0.1`
- 典型 AI 客户端：Claude Code、Codex、Gemini CLI、Antigravity、Cursor

## 安装 Unity 包

### Package Manager 安装

在 Unity 中打开：

```text
Window > Package Manager > + > Add package from git URL
```

使用以下地址之一：

稳定版：

```text
https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity
```

Beta：

```text
https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity#beta
```

指定版本：

```text
https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity#v1.6.3
```

## 启动服务

在 Unity 编辑器中打开：

```text
Window > UnitySkills > Start Server
```

正常情况下，Console 会输出类似内容：

```text
[UnitySkills] REST Server started at http://localhost:8090/
```

## 安装 AI Skill 模板

### 推荐：使用 Unity 内置安装器

打开：

```text
Window > UnitySkills > Skill Installer
```

选择目标 AI 工具后执行安装。安装器会复制包内的 `unity-skills~/` 模板目录到目标位置。

目标目录应至少包含：

- `SKILL.md`
- `skills/`
- `scripts/unity_skills.py`
- `scripts/agent_config.json`

### 手动安装

如果不使用安装器，请把 UPM 包内的 `SkillsForUnity/unity-skills~/` 目录内容复制到你的 AI 工具技能目录中。

常见目录：

- Claude Code：`~/.claude/skills/`
- Codex：`~/.codex/skills/`
- Gemini CLI：`~/.gemini/skills/`
- Antigravity：`~/.agent/skills/`
- Cursor：`~/.cursor/skills/`

对于 Codex，推荐全局安装。项目级安装时，还需要在项目根目录的 `AGENTS.md` 中声明该技能。

## Python 客户端行为

`unity_skills.py` 当前具备以下行为：

- 默认请求超时为 `900` 秒，也就是 `15 分钟`
- 初始化时会从 `/health` 同步服务端超时设置
- 复用 `requests.Session`，减少频繁新建连接
- 遇到编译或 Domain Reload 导致的短暂断连时，会把错误标记为可重试
- `WorkflowContext` 在超时或连接异常后，会尝试读取服务端状态并恢复工作流一致性

## 编译、Domain Reload 与短暂不可达

以下操作都可能让服务短时间不可达：

- `script_create`
- `script_append`
- `script_replace`
- `debug_force_recompile`
- `debug_set_defines`
- 某些 `asset_import` / `asset_reimport` / `asset_move`
- 测试模板创建
- 部分包安装或移除

这是 Unity 编辑器行为，不是异常崩溃。建议做法：

1. 收到“暂时不可用”或连接超时后，先等待几秒。
2. 调用 `wait_for_unity()` 或使用 `call_skill_with_retry()`。
3. 脚本生成后，优先读取编译反馈，再继续后续步骤。

脚本示例：

```python
import unity_skills

result = unity_skills.create_script("PlayerController")
if result.get("success"):
    print(result.get("compilation"))
```

## 多实例路由

如果本机同时打开多个 Unity 项目，优先通过版本或目标名选择实例：

```python
import unity_skills

unity_skills.set_unity_version("2022.3")
unity_skills.call_skill("project_get_info")
```

也可以通过注册表枚举实例：

```python
import unity_skills

print(unity_skills.list_instances())
```

## 批量优先原则

当你要操作 2 个及以上对象时，优先使用 `*_batch` 技能，原因是：

- 请求数更少
- 编译窗口更短
- 工作流快照更集中
- AI 更不容易在循环里打爆请求队列

示例：

```python
unity_skills.call_skill(
    "gameobject_create_batch",
    items=[
        {"name": "Cube_A", "primitiveType": "Cube", "x": -1},
        {"name": "Cube_B", "primitiveType": "Cube", "x": 1},
    ],
)
```

## 测试模块说明

- `test_run` 和 `test_run_by_name` 对接的是 Unity Test Runner。
- 调用后立即返回 `jobId`。
- 使用 `test_get_result(jobId)` 轮询结果。
- 这不是启动独立的 Unity 可执行进程，而是在当前编辑器上下文里执行测试任务。

## 常见排障

| 问题 | 现象 | 建议 |
| --- | --- | --- |
| 连接失败 | `Cannot connect to http://localhost:8090` | 检查 Unity 是否已启动服务，或是否正处于编译 / Domain Reload |
| 请求超时 | 超过 15 分钟后返回超时 | 先确认是否是长任务；必要时在 Unity 面板中调高超时设置 |
| 技能列表为空 | `/skills` 返回异常 | 检查控制台是否有编译错误，确保插件成功导入 |
| 脚本创建后断连 | 创建脚本后接口暂时不可用 | 正常现象，等待编译完成后重试 |
| 多实例误连 | 请求打到了错误项目 | 先调用 `set_unity_version()` 或按目标名连接 |
| 工作流状态异常 | 本地认为开始了任务，但服务端状态不一致 | 重新读取 `workflow_session_status`，当前客户端已内置恢复逻辑 |

## 文档索引

- [中文 README](../README.md)
- [English README](../README_EN.md)
- [AI Skill 入口](../SkillsForUnity/unity-skills~/SKILL.md)
- [更新日志](../CHANGELOG.md)
