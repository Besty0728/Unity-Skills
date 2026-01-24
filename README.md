# 🎮 UnitySkills


<p align="center">
  <img src="https://img.shields.io/badge/Unity-2021.3%2B-black?style=for-the-badge&logo=unity" alt="Unity">
  <img src="https://img.shields.io/badge/Skills-115%2B-green?style=for-the-badge" alt="Skills">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-orange?style=for-the-badge" alt="License"></a>
</p>

<p align="center">
  <b>基于 REST API 的 AI 驱动型 Unity 编辑器自动化引擎</b><br>
  <i>让 AI 通过Skills直接掌控 Unity 场景</i>
</p>

## 🤝 致谢
本项目基于 [unity-mcp](https://github.com/CoplayDev/unity-mcp) 的优秀理念深度重构与功能扩展。

---

## 🚀 核心特性

- ⚡ **极致效能**：支持 **Result Truncation** 与 **SKILL.md** 瘦身，最大化节省 Token。
- 🛠️ **全能工具库**：内置 115+ Skills，支持 **Batch (批处理)** 操作，性能提升 100 倍。
- 🛡️ **安全第一**：支持 **Transactional (事务原子性)**，操作失败自动回滚，场景零残留。
- 🌍 **多实例支持**：自动端口发现、全局注册表，支持同时控制多个 Unity 项目。
- 🤖 **深度集成**：独家支持 **Antigravity Slash Commands**，解锁 `/unity-skills` 交互新体验。
- 🔌 **全环境兼容**：完美支持 Claude Code, Antigravity, Gemini CLI 等主流 AI 终端。

---

## 🏗️ 支持的 IDE / 终端

本项目针对以下环境进行了深度优化，确保持续、稳定的开发体验：

| AI 终端 | 支持状态 | 特色功能 |
| :--- | :---: | :--- |
| **Antigravity** | ✅ 完美支持 | 支持 `/unity-skills` 斜杠命令，原生集成工作流。 |
| **Claude Code** | ✅ 完美支持 | 智能识别 Skill 意图，支持复杂多步自动化。 |
| **Gemini CLI** | ✅ 完美支持 | 实验性支持，适配最新 `experimental.skills` 规范。 |
| **Codex** | ✅ 完美支持 | 支持 `$skill` 显式调用和隐式意图识别。 |

---

## 🏁 快速开始

### 1. 安装 Unity 插件
通过 Unity Package Manager 直接添加 Git URL：
```
https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity
```

> ⚠️ **重要提示**：请不要随意更新 Unity Package，此项目会经常开发测试，请认准 [Releases](https://github.com/Besty0728/Unity-Skills/releases) 版本。`main` 分支为稳定版，`beta` 分支为开发测试版。

**稳定版安装 (main)**:
```
https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity
```

**开发测试版安装 (beta)**:
```
https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity#beta
```

### 2. 启动服务
在 Unity 中点击菜单：`Window > UnitySkills > Start Server`

### 3. 一键配置 AI Skills
1. 打开 `Window > UnitySkills > Skill Installer`。
2. 选择对应的终端图标（Claude / Antigravity / Gemini / Codex）。
3. 点击 **"Install"** 即可完成环境配置，无需手动拷贝代码。

> 安装器落盘文件说明（生成于目标目录）：
> - `SKILL.md`
> - `scripts/unity_skills.py`
> - Antigravity 额外生成 `workflows/unity-skills.md`

> **Codex 特别说明**：推荐使用**全局安装**。项目级安装需要在 `AGENTS.md` 中声明才能识别，全局安装后重启 Codex 即可。

📘 需要更完整的安装与使用说明，请查看：[docs/SETUP_GUIDE.md](docs/SETUP_GUIDE.md)

### 4. 手动安装 Skills（可选）
如果不使用一键安装，可按以下**标准流程**手动部署（适用于所有支持 Skills 的工具）：

#### ✅ 标准安装规范
1. **定位 Skills 源码目录**：本仓库的 `unity-skills/` 即为可分发的 Skills 模板（根目录包含 `SKILL.md`）。
2. **找到工具的 Skills 根目录**：不同工具路径不同，优先以该工具文档为准。
3. **完整复制**：将整个 `unity-skills/` 目录复制到工具的 Skills 根目录下。
4. **目录结构要求**：复制后需保持结构如下（示例）：
   - `unity-skills/SKILL.md`
   - `unity-skills/skills/`
   - `unity-skills/scripts/`
5. **重启工具**：让工具重新加载 Skills 列表。
6. **验证加载**：在工具内触发 Skills 列表/命令（或执行一次简单技能调用），确认可用。

#### 🔎 常见工具目录参考
以下为已验证的默认目录（若工具配置过自定义路径，请以自定义为准）：

- Claude Code：`~/.claude/skills/`
- Antigravity：`~/.agent/skills/`
- Gemini CLI：`~/.gemini/skills/`
- OpenAI Codex：`~/.codex/skills/`

#### 🧩 其他支持 Skills 的工具
若你使用的是其他支持 Skills 的工具，请按照该工具文档指定的 Skills 根目录进行安装。只要满足**标准安装规范**（根目录包含 `SKILL.md` 并保持 `skills/` 与 `scripts/` 结构），即可被正确识别。

---

## 📦 Skills 分类概要 (115+)

| 分类 | 数量 | 核心功能 |
| :--- | :---: | :--- |
| **GameObject** | 8 | 增删改查、层级管理、变换同步、**批量复制**。 |
| **Component** | 8 | 智能组件挂载、属性劫持与配置、批量操作。 |
| **Scene** | 6 | 场景无缝切换、状态保存、高清截图。 |
| **Material** | 17 | HDR 发光控制、材质球属性批量修改。 |
| **Light** | 7 | 灯光创建/配置、**批量开关/属性设置**。 |
| **UI System** | 10 | 响应式 Canvas 构建、Button/Text 组件生成。 |
| **Editor** | 12 | 播放模式、选择、撤销重做、**上下文获取**。 |
| **Importer** | 9 | 纹理/音频/模型导入设置调整。 |
| **Others** | 38+ | 涵盖 Animator, Prefab, Validation, Console 等。 |

> ⚠️ 大部分模块支持 `*_batch` 批量操作，操作多个物体时应优先使用批量 Skills 以提升性能。

---

## 📂 项目结构

```bash
.
├── SkillsForUnity/                 # Unity 编辑器插件 (Package 核心)
│   └── Editor/Skills/              # 核心 Skill 逻辑与 C# 安装器
│       ├── EditorSkills.cs         # 编辑器控制 (含 editor_get_context)
│       ├── TextureSkills.cs        # 纹理导入设置 [v1.2 新增]
│       ├── AudioSkills.cs          # 音频导入设置 [v1.2 新增]
│       ├── ModelSkills.cs          # 模型导入设置 [v1.2 新增]
│       └── ...                     # 其他 Skills 模块
├── unity-skills/                   # 跨平台 AI Skill 模板 (核心源码)
│   ├── SKILL.md                    # Skill 定义与 Prompt 设计
│   ├── scripts/                    # Python Helper 封装
│   └── skills/                     # 分模块 Skill 文档
│       ├── editor/SKILL.md
│       ├── importer/SKILL.md       # [v1.2 新增]
│       └── ...
├── CHANGELOG.md                    # 详尽的更新记录与路线图
└── LICENSE                         # MIT 开源协议
```

---

## 📄 开源协议
本项目采用 [MIT License](LICENSE) 许可。
