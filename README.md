# 🎮 UnitySkills

> 💡 基于 [unity-mcp](https://github.com/CoplayDev/unity-mcp) 开发

> **通过 REST API 直接控制 Unity Editor** — 让 AI 生成极简脚本完成场景操作。

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Unity](https://img.shields.io/badge/Unity-2021.3%2B-black)](https://unity.com)
[![Skills](https://img.shields.io/badge/Skills-100%2B-green)](#支持的-skill)

---

## ✨ 特点

- 🚀 **极简调用** - 仅需 3 行 Python 代码即可与 Unity 交互
- ⚡ **零开销** - 直接 HTTP 通信，无 MCP 中间层损耗
- 🎯 **100+ Skills** - 覆盖 GameObject、Component、Material、UI 等 15 大类
- 🤖 **多 AI 支持** - Claude Code、Antigravity、Gemini CLI 一键安装

---

## 🏁 快速开始

### 1️⃣ 安装 Unity 插件

Unity Package Manager → Add package from git URL:

```
https://github.com/Besty0728/unity-mcp-skill.git?path=/SkillsForUnity
```

### 2️⃣ 启动 REST 服务

Unity 菜单：`Window > UnitySkills > Start Server`

### 3️⃣ 安装 AI Skill（一键安装）

1. Unity 打开 `Window > UnitySkills`
2. 切换到 **AI Config** 标签页
3. 选择你的 AI 工具，点击 **安装到项目** 或 **全局安装**

| AI 工具 | 安装位置 |
|---------|---------|
| **Claude Code** | `~/.claude/skills/unityskills/` |
| **Antigravity** | `~/.gemini/antigravity/skills/unityskills/` |
| **Gemini CLI** | `~/.gemini/skills/unityskills/` |

> 💡 **Gemini CLI** 需要先启用 Skills 功能（见下方）

---

## 🔧 Gemini CLI 启用 Skills

Gemini CLI 的 Skills 是实验性功能，需手动启用：

```bash
gemini
# 进入交互模式后
/settings
# 找到并启用: experimental.skills
```

验证安装：
```bash
/skills list    # 查看已安装的 skills
/skills reload  # 重新加载 skills
```

---

## 📦 支持的 Skill

| 分类 | 功能 |
|-----|------|
| **GameObject** | 创建、删除、查找、变换 |
| **Component** | 添加、移除、配置组件 |
| **Scene** | 场景管理、截图 |
| **Material** | 材质、HDR 发光、Keyword |
| **Prefab** | 预制体操作 |
| **Light** | 灯光创建和配置 |
| **UI** | Canvas、Button、Text 等 |
| **Animator** | 动画控制器管理 |
| **Script** | 脚本创建 |

## 📂 目录结构

```
├── SkillsForUnity/           # Unity Package
│   └── Editor/Skills/        # Skill 实现
├── unityskills/              # AI Skill 模板
│   ├── SKILL.md              # Skill 定义
│   └── scripts/              # Python Helper
└── .gemini/skills/           # Gemini CLI Skill
```

---

## 📄 License

[MIT License](LICENSE)