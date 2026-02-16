# 🎮 UnitySkills

<p align="center">
  <img src="https://img.shields.io/badge/Unity-2021.3%2B-black?style=for-the-badge&logo=unity" alt="Unity">
  <img src="https://img.shields.io/badge/Skills-431-green?style=for-the-badge" alt="Skills">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-orange?style=for-the-badge" alt="License"></a>
  <a href="README_EN.md"><img src="https://img.shields.io/badge/README-English-blue?style=for-the-badge" alt="English"></a>
</p>

<p align="center">
  <b>基于 REST API 的 AI 驱动型 Unity 编辑器自动化引擎</b><br>
  <i>让 AI 通过Skills直接掌控 Unity 场景</i>
</p>

<p align="center">
  🎉 我们已被 <b>DeepWiki</b> 收录！<br>
  有问题？查阅 AI 生成的项目文档 → <a href="https://deepwiki.com/Besty0728/Unity-Skills"><img src="https://deepwiki.com/badge.svg" alt="Ask DeepWiki"></a>
</p>

## 🤝 致谢
本项目基于 [unity-mcp](https://github.com/CoplayDev/unity-mcp) 的优秀理念深度重构与功能扩展。

---

## 🚀 核心特性

- ⚡ **极致效能**：支持 **Result Truncation** 与 **SKILL.md** 瘦身，最大化节省 Token。
- 🛠️ **全能工具库**：内置 431 Skills，支持 **Batch (批处理)** 操作，大幅减少 HTTP 通信开销，显著提升执行效率。
- 🛡️ **安全第一**：支持 **Transactional (事务原子性)**，操作失败自动回滚，场景零残留。
- 🌍 **多实例支持**：自动端口发现、全局注册表，支持同时控制多个 Unity 项目。
- 🤖 **深度集成**：独家支持 **Antigravity Slash Commands**，解锁 `/unity-skills` 交互新体验。
- 🔌 **全环境兼容**：完美支持 Claude Code, Antigravity, Gemini CLI 等主流 AI 终端。
- 🎥 **Cinemachine 2.x/3.x 双版本支持**：自动检测 Unity 版本并安装对应 Cinemachine，支持 **MixingCamera**, **ClearShot**, **TargetGroup**, **Spline** 等高级相机控制。
- 🔗 **超长任务稳定连接**：请求超时用户可配置（默认 60 分钟），Domain Reload 后自动恢复同一端口，Python 客户端自动同步超时配置，彻底解决长时间任务断连问题。

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

**稳定版安装 (main)**:
```
https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity
```

**开发测试版安装 (beta)**:
```
https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity#beta
```

**指定版本安装** (如 v1.4.0):
```
https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity#v1.4.0
```

> 📦 所有版本包可在 [Releases](https://github.com/Besty0728/Unity-Skills/releases) 页面下载

### 2. 启动服务
在 Unity 中点击菜单：`Window > UnitySkills > Start Server`

### 3. 一键配置 AI Skills
1. 打开 `Window > UnitySkills > Skill Installer`。
2. 选择对应的终端图标（Claude / Antigravity / Gemini / Codex）。
3. 点击 **"Install"** 即可完成环境配置，无需手动拷贝代码。

> 安装器落盘文件说明（生成于目标目录）：
> - `SKILL.md`
> - `scripts/unity_skills.py`
> - `scripts/agent_config.json`（包含 Agent 标识）
> - Antigravity 额外生成 `workflows/unity-skills.md`

> **Codex 特别说明**：推荐使用**全局安装**。项目级安装需要在 `AGENTS.md` 中声明才能识别，全局安装后重启 Codex 即可。

📘 需要更完整的安装与使用说明，请查看：[docs/SETUP_GUIDE.md](docs/SETUP_GUIDE.md)

### 4. 手动安装 Skills（可选）
如果不支持不使用一键安装，可按以下**标准流程**手动部署（适用于所有支持 Skills 的工具）：

#### ✅ 标准安装规范A
1. **自定义安装**: 在安装界面，你可以选择 "Custom Path" 选项，将 Skills 安装到你指定的任意目录（例如 `Assets/MyTools/AI`），方便项目管理。

#### ✅ 标准安装规范B
1. **定位 Skills 源码目录**：本仓库的 `unity-skills/` 即为可分发的 Skills 模板（根目录包含 `SKILL.md`）。
2. **找到工具的 Skills 根目录**：不同工具路径不同，优先以该工具文档为准。
3. **完整复制**：将整个 `unity-skills/` 目录复制到工具的 Skills 根目录下。
4. **创建 agent_config.json**：在 `unity-skills/scripts/` 目录下创建 `agent_config.json` 文件：
   ```json
   {"agentId": "your-agent-name", "installedAt": "2026-02-11T00:00:00Z"}
   ```
   将 `your-agent-name` 替换为你使用的 AI 工具名称（如 `claude-code`、`antigravity`、`gemini-cli`、`codex`）。
5. **目录结构要求**：复制后需保持结构如下（示例）：
   - `unity-skills/SKILL.md`
   - `unity-skills/skills/`
   - `unity-skills/scripts/unity_skills.py`
   - `unity-skills/scripts/agent_config.json`
6. **重启工具**：让工具重新加载 Skills 列表。
7. **验证加载**：在工具内触发 Skills 列表/命令（或执行一次简单技能调用），确认可用。

#### 🔎 常见工具目录参考
以下为已验证的默认目录（若工具配置过自定义路径，请以自定义为准）：

- Claude Code：`~/.claude/skills/`
- Antigravity：`~/.agent/skills/`
- Gemini CLI：`~/.gemini/skills/`
- OpenAI Codex：`~/.codex/skills/`

#### 🧩 其他支持 Skills 的工具
若你使用的是其他支持 Skills 的工具，请按照该工具文档指定的 Skills 根目录进行安装。只要满足**标准安装规范**（根目录包含 `SKILL.md` 并保持 `skills/` 与 `scripts/` 结构），即可被正确识别。

---

## 📦 Skills 分类概要 (431)

| 分类 | 数量 | 核心功能 |
| :--- | :---: | :--- |
| **Cinemachine** | 23 | 2.x/3.x双版本自动安装/混合相机/ClearShot/TargetGroup/Spline |
| **Workflow** | 22 | 持久化历史/任务快照/会话级撤销/回滚/书签 |
| **Material** | 21 | 材质属性批量修改/HDR/PBR/Emission/关键字/渲染队列 |
| **GameObject** | 18 | 创建/查找/变换同步/批量操作/层级管理/重命名/复制 |
| **Scene** | 18 | 多场景加载/卸载/激活/截图/上下文/依赖分析/报告导出 |
| **UI System** | 16 | Canvas/Button/Text/Slider/Toggle/锚点/布局/对齐/分布 |
| **Asset** | 15 | 资产导入/删除/移动/复制/搜索/文件夹/批量操作/刷新 |
| **Editor** | 12 | 播放模式/选择/撤销重做/上下文获取/菜单执行 |
| **Timeline** | 12 | 轨道创建/删除/Clip管理/播放控制/绑定/时长设置 |
| **Physics** | 12 | 射线检测/球形投射/盒形投射/物理材质/层碰撞矩阵 |
| **Audio** | 12 | 音频导入设置/AudioSource/AudioClip/AudioMixer/批量 |
| **Texture** | 12 | 纹理导入设置/平台设置/Sprite/类型/尺寸查找/批量 |
| **Model** | 12 | 模型导入设置/Mesh信息/材质映射/动画/骨骼/批量 |
| **Script** | 12 | C#脚本创建/读取/替换/列表/信息/重命名/移动/分析 |
| **Package** | 11 | 包管理/安装/移除/搜索/版本/依赖/Cinemachine/Splines |
| **AssetImport** | 11 | 纹理/模型/音频/Sprite导入设置/标签管理/重导入 |
| **Project** | 11 | 渲染管线/构建设置/包管理/Layer/Tag/PlayerSettings/质量 |
| **Shader** | 11 | Shader创建/URP模板/编译检查/关键字/变体分析/全局关键字 |
| **Camera** | 11 | Scene View控制/Game Camera创建/属性/截图/正交切换/列表 |
| **Terrain** | 10 | 地形创建/高度图/Perlin噪声/平滑/平坦化/纹理绘制 |
| **NavMesh** | 10 | 烘焙/路径计算/Agent/Obstacle/采样/区域代价 |
| **Cleaner** | 10 | 未使用资源/重复文件/空文件夹/丢失脚本修复/依赖树 |
| **ScriptableObject** | 10 | 创建/读写/批量设置/删除/查找/JSON导入导出 |
| **Console** | 10 | 日志捕获/清理/导出/统计/暂停控制/折叠/播放清除 |
| **Debug** | 10 | 错误日志/编译检查/堆栈/程序集/定义符号/内存信息 |
| **Event** | 10 | UnityEvent监听器管理/批量添加/复制/状态控制/列举 |
| **Smart** | 10 | 场景SQL查询/空间查询/自动布局/对齐地面/网格吸附/随机化/替换 |
| **Test** | 10 | 测试运行/按名运行/分类/模板创建/汇总统计 |
| **Prefab** | 10 | 创建/实例化/覆盖应用与恢复/批量实例化/变体/查找实例 |
| **Component** | 10 | 添加/移除/属性配置/批量操作/复制/启用禁用 |
| **Optimization** | 10 | 纹理压缩/网格压缩/音频压缩/场景分析/静态标记/LOD/重复材质/过度绘制 |
| **Profiler** | 10 | FPS/内存/纹理/网格/材质/音频/渲染统计/对象计数/AssetBundle |
| **Light** | 10 | 灯光创建/类型配置/强度颜色/批量开关/探针组/反射探针/光照贴图 |
| **Validation** | 10 | 项目验证/空文件夹清理/引用检测/网格碰撞/Shader错误 |
| **Animator** | 10 | 动画控制器/参数/状态机/过渡/分配/播放 |
| **Perception** | 9 | 场景摘要/层级树/脚本分析/空间查询/材质概览/场景快照/依赖分析/报告导出/性能提示 |
| **Sample** | 8 | 基础示例：创建/删除/变换/场景信息 |

> ⚠️ 大部分模块支持 `*_batch` 批量操作，操作多个物体时应优先使用批量 Skills 以提升性能。

---

## 📂 项目结构

```bash
.
├── SkillsForUnity/                 # Unity 编辑器插件 (UPM Package)
│   ├── package.json                # com.besty.unity-skills
│   └── Editor/Skills/              # 核心 Skill 逻辑 (37 个 *Skills.cs, 共 431 Skills)
│       ├── SkillsHttpServer.cs     # HTTP 服务器核心 (Producer-Consumer)
│       ├── SkillRouter.cs          # 请求路由 & 反射发现 Skills
│       ├── WorkflowManager.cs      # 持久化工作流 (Task/Session/Snapshot)
│       ├── RegistryService.cs      # 全局注册表 (多实例发现)
│       ├── GameObjectFinder.cs     # 统一 GO 查找器 (name/instanceId/path)
│       ├── BatchExecutor.cs        # 泛型批处理框架
│       ├── GameObjectSkills.cs     # GameObject 操作 (18 skills)
│       ├── MaterialSkills.cs       # Material 操作 (21 skills)
│       ├── CinemachineSkills.cs    # Cinemachine 2.x/3.x (23 skills)
│       ├── WorkflowSkills.cs       # Workflow 撤销/回滚 (22 skills)
│       ├── PerceptionSkills.cs     # 场景理解 (9 skills)
│       └── ...                     # 431 Skills 源码
├── unity-skills/                   # 跨平台 AI Skill 模板 (分发给 AI 工具)
│   ├── SKILL.md                    # 主 Skill 定义 (AI 读取)
│   ├── scripts/
│   │   └── unity_skills.py         # Python 客户端库
│   ├── skills/                     # 按模块分类的 Skill 文档
│   └── references/                 # Unity 开发参考文档
├── docs/
│   └── SETUP_GUIDE.md              # 完整安装使用指南
├── CHANGELOG.md                    # 版本更新记录
└── LICENSE                         # MIT 开源协议
```

---

## ⭐ Star History

[![Star History Chart](https://api.star-history.com/svg?repos=Besty0728/Unity-Skills&type=Date)](https://star-history.com/#Besty0728/Unity-Skills&Date)

---

## 📄 开源协议
本项目采用 [MIT License](LICENSE) 许可。
