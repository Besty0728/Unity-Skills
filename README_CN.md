# 🎮 UnitySkills

<p align="center">
  <img src="docs/Unity-Skills-H.png" alt="Unity-Skills" width="800">
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Unity-2022.3%2B-black?style=for-the-badge&logo=unity" alt="Unity">
  <img src="https://img.shields.io/badge/Skills-805-green?style=for-the-badge" alt="Skills">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-orange?style=for-the-badge" alt="License"></a>
  <a href="README.md"><img src="https://img.shields.io/badge/README-English-blue?style=for-the-badge" alt="English"></a>
</p>

<p align="center">
  <b>基于 REST API 的 AI 驱动型 Unity 编辑器自动化引擎</b><br>
  <i>让 AI 通过 Skills 直接掌控 Unity 场景</i>
</p>

<p align="center">
  🎉 我们已被 <b>DeepWiki</b> 收录！<br>
  有问题？查阅 AI 生成的项目文档 → <a href="https://deepwiki.com/Besty0728/Unity-Skills"><img src="https://deepwiki.com/badge.svg" alt="Ask DeepWiki"></a>
</p>

> 当前官方维护基线为 **Unity 2022.3+**。仓库中仍可能保留部分对 Unity 2021 的兼容逻辑，但后续功能开发、回归验证与适配工作将以 **2022.3+ / Unity 6** 为主。

## 📈 项目贡献排名

<p align="center">
  <a href="https://trendshift.io/repositories/27085?utm_source=trendshift-badge&amp;utm_medium=badge&amp;utm_campaign=badge-trendshift-27085" target="_blank" rel="noopener noreferrer"><img src="https://trendshift.io/api/badge/trendshift/repositories/27085/daily?language=C%23" alt="Besty0728%2FUnity-Skills | Trendshift" width="250" height="55"/></a>
  <a href="https://trendshift.io/repositories/27085?utm_source=trendshift-badge&amp;utm_medium=badge&amp;utm_campaign=badge-trendshift-27085" target="_blank" rel="noopener noreferrer"><img src="https://trendshift.io/api/badge/trendshift/repositories/27085/weekly?language=C%23" alt="Besty0728%2FUnity-Skills | Trendshift" width="250" height="55"/></a>
  <a href="https://trendshift.io/repositories/27085?utm_source=trendshift-badge&amp;utm_medium=badge&amp;utm_campaign=badge-trendshift-27085" target="_blank" rel="noopener noreferrer"><img src="https://trendshift.io/api/badge/trendshift/repositories/27085/monthly?language=C%23" alt="Besty0728%2FUnity-Skills | Trendshift" width="250" height="55"/></a>
</p>

## 🤝 致谢
本项目基于 [unity-mcp](https://github.com/CoplayDev/unity-mcp) 的优秀理念深度重构与功能扩展。

---

## 🚀 核心特性

- 🛠️ **805 REST Skills 全能库**：56 个源文件、54 个分类，另有 28 个 advisory 设计模块，支持 Batch 批处理。
- ⚡ **支持UnityCLI**：支持绑定UnityCLI，对应项目冷启动，不用开启UnityHub
- 🔐 **三档权限模式**：Approval / Auto / Bypass + 双轨审批，对齐 Claude Code permission modes。
- 🤖 **6 大 IDE 原生支持**：Claude Code / Antigravity / Codex / Cursor / OpenCode / Kimi Code，一键安装即用。
- 🛡️ **事务原子性**：操作失败自动回滚，场景永不残留。
- 🌍 **多实例同时控制**：自动端口发现与全局注册表，同时操控多个 Unity 项目。
- 🔗 **超长稳定连接**：请求超时可配（默认 15 分钟），Domain Reload 后自动恢复。
- 🛡️ **防幻觉 Guardrails**：每个 Skill 模块内置 DO NOT 清单与路由规则。

---

## 🛡️ 为什么选 UnitySkills：治理层

AI 驱动编辑器，写的是真实的场景、Prefab 和 `.meta` 文件。真正的问题不是"它能不能做到"，而是"它做错时会发生什么"。UnitySkills 在调用生命周期的四个节点回答这个问题：

- **执行前**：`?mode=dryRun` / `?mode=plan` 预演——只返回参数校验与影响预估，不落地任何改动。
- **执行时**：每条 skill 的风险元数据由服务端自动判定高危拦截（NeverInSemi）——拦不拦从不取决于 AI 是否自觉；Allowlist 可按条持久放行。
- **执行后**：每次调用、授权、撤销、拦截写入 JSONL 审计日志，面板内可浏览，删除审计条目本身也入账。
- **出错后**：五类持久化快照跨 Domain Reload 存活，`workflow_undo_task` 回退一个任务而非整个项目；`POST /skills/batch` 批量即事务（跨步 `$ref`、失败回滚、`?diff=1` 净变化）。

完整机制说明 → [操作模式与治理层](docs/OPERATING_MODES_CN.md)

### 横向对比

| 维度 | UnitySkills | 典型 MCP 桥接方案 | Unity 官方 AI Assistant |
| :--- | :--- | :--- | :--- |
| **权限粒度** | 操作级：三档模式（Approval / Auto / Bypass）+ 每条 skill 的风险元数据 + 按 skill 的 Allowlist | 无权限模型，连上即可调用全部工具面 | 客户端级信任（Pending Connections → Allow / Revoke）；授权之后不再按操作区分 |
| **审计** | 每次调用 / 授权 / 撤销 / 拦截写结构化 JSONL，面板内可浏览，删除动作同样入账 | 仅进程日志，无结构化的逐次调用留痕 | 以对话历史与 Checkpoints 呈现，而非逐操作的审计记录 |
| **回滚粒度** | 任务级，五类快照，主文件与 `.meta` 内容寻址，跨会话持久 | 依赖 Unity 原生 Undo 栈，Domain Reload 后不保证仍可回退 | 每次 prompt 前对整项目打 Checkpoint，恢复即整项目回到该时点 |
| **执行前预演** | `?mode=dryRun` / `?mode=plan`：参数语义校验 + 影响预估，不落地任何改动 | 调研未见对等能力 | 未见执行前的参数校验或影响预演 |
| **批量事务** | `POST /skills/batch`：fail-fast / `continueOnError`、跨步 `$ref`、失败回滚、`?diff=1` 净变化 | 逐条工具调用，无事务语义 | 未以批量事务形式提供 |

> **UnitySkills** 一列描述的都是本仓库中已实现、可对着源码或直接调端点核对的机制。另外两列基于 **2026-07 的公开资料与开源仓库调研**，描述的是一类方案的普遍形态而非某个具体项目，相关能力可能已经更新。

---

## 🔐 操作模式

UnitySkills 引入真正的服务端权限系统，对齐 Claude Code permission modes。模式切换统一在 Unity 面板完成：**Window > UnitySkills** → ⚙ 设置 → **Server** 区（不再支持对话触发词）。

| 模式 | 默认 | 行为 |
|:-----|:----:|:-----|
| **Approval（审批）** | — | 服务端返回 grant token，用户审批后 AI 重放执行 |
| **Auto（自动）** | 新安装 | FullAuto skill 直接执行，高危操作（NeverInSemi）自动拦截 |
| **Bypass（放行）** | 老安装升级 | 全部放行，仅保留可选的高危二次确认 |

老用户升级自动识别旧安装并保持 **Bypass**，行为与原 Full-Auto 完全一致，无需任何操作。双轨审批（Dialog / Panel）、审计日志（`Library/UnitySkillsAudit.jsonl`）、Allowlist 与卸载按钮等完整说明 → [操作模式与治理层](docs/OPERATING_MODES_CN.md)。

> 28 个 advisory 设计模块（架构、性能、设计模式、可测试性、包级源码规则等）在所有模式下均可用，按需自动加载。

---

## 🏗️ 快速安装支持的IDE/终端

本项目针对以下环境深度优化（未列出的工具不代表不支持，只是没有快捷安装，可选用 ***自定义安装***；各工具的技能目录见下方「手动安装」折叠区）：

| AI 终端 | 支持状态 | 说明 |
| :--- | :---: | :--- |
| **Antigravity** | ✅ 支持 | 开放 Agent Skills 标准，工作区与 Codex 共享 `.agents/skills/` |
| **Claude Code** | ✅ 支持 | 智能识别 Skill 意图，支持复杂多步自动化 |
| **Codex** | ✅ 支持 | 支持 `$skill` 显式调用和隐式意图识别 |
| **Cursor** | ✅ 支持 | 自动扫描技能目录，支持 `/skill-name` 显式触发 |
| **Kimi Code** | ✅ 支持 | 原生技能目录扫描，支持 `/skill:unity-skills` 显式触发 |
| **OpenCode** | ✅ 支持 | 原生扫描工作区与全局技能目录 |

---

## 🏁 快速开始

> **总体路线**：安装 Unity 插件 → 开启 UnitySkills 服务器 → AI 使用 Skill

<p align="center">
  <img src="docs/installation-demo.gif" alt="一键安装演示" width="800">
</p>

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

**指定版本安装** (如 v1.6.0):
```
https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity#v1.6.0
```

> 📦 所有版本包可在 [Releases](https://github.com/Besty0728/Unity-Skills/releases) 页面下载

### 2. 打开面板并启动服务
在 Unity 中打开菜单：`Window > UnitySkills`（或按 <kbd>Alt</kbd>+<kbd>Shift</kbd>+<kbd>U</kbd>），用顶栏的服务开关启动；启动后会跨 Domain Reload 自动恢复。

> ⏳ `script_*`、`debug_force_recompile`、`debug_set_defines`、部分资源重导入、包安装/移除等操作会触发编译或 Domain Reload，REST 服务短暂不可达属于正常现象，请稍候重试。

### 3. 一键配置 AI Skills
1. 打开 `Window > UnitySkills`，切到 **AI Config** 标签页。
2. 选择对应的终端图标，点击 **"Install"** 即完成配置——安装器会把包内 `unity-skills~/` 模板复制到目标位置，无需手动拷贝。

> 🔄 **更新自动同步**：升级插件后，已安装过的 AI 工具会自动同步到新版本（不会自动安装新目标）；可在 ⚙ 设置抽屉的 **AI 工具** 区关闭。
>
> **Codex 特别说明**：Antigravity 和 Codex 工作区共享 `.agents/skills/`——装一次即两边可用，Codex 无需在 `AGENTS.md` 中声明。

📘 需要更完整的安装与使用说明，请查看：[安装指南](docs/SETUP_GUIDE_CN.md) | [Setup Guide](docs/SETUP_GUIDE.md)

<details>
<summary><b>4. 手动安装 Skills（可选）</b></summary>

如果不使用一键安装，可按以下**标准流程**手动部署（适用于所有支持 Skills 的工具）：

#### ✅ 标准安装规范 A
1. **自定义安装**：在安装界面选择 "Custom Path" 选项，将 Skills 安装到任意指定目录（例如 `Assets/MyTools/AI`），方便项目管理。

#### ✅ 标准安装规范 B
1. **定位 Skills 源码目录**：UPM 包内的 `SkillsForUnity/unity-skills~/` 即为可分发的 Skills 模板（根目录包含 `SKILL.md`）。
2. **找到工具的 Skills 根目录**：不同工具路径不同，优先以该工具文档为准。
3. **完整复制**：将整个 `unity-skills~/` 目录内容复制到工具的 Skills 根目录下（重命名为 `unity-skills/`）。
4. **创建 agent_config.json**：在 `unity-skills/scripts/` 目录下创建 `agent_config.json` 文件：
   ```json
   {"agentId": "your-agent-name", "installedAt": "2026-02-11T00:00:00Z"}
   ```
   将 `your-agent-name` 替换为你使用的 AI 工具名称（如 `claude-code`、`antigravity`、`codex`、`cursor`）。
5. **目录结构要求**：复制后需保持结构如下（示例）：
   - `unity-skills/SKILL.md`
   - `unity-skills/skills/`
   - `unity-skills/references/`
   - `unity-skills/scripts/unity_skills.py`
   - `unity-skills/scripts/agent_config.json`
6. **重启工具**：让工具重新加载 Skills 列表。
7. **验证加载**：在工具内触发 Skills 列表/命令（或执行一次简单技能调用），确认可用。

#### 🔎 常见工具目录参考
以下为已验证的默认目录（若工具配置过自定义路径，请以自定义为准）：

- Claude Code：`~/.claude/skills/`
- Antigravity：`~/.gemini/antigravity/skills/`（全局）或 `.agents/skills/`（工作区）
- OpenAI Codex：`~/.agents/skills/`（全局）或 `.agents/skills/`（工作区，与 Antigravity 共享）
- Cursor：`~/.cursor/skills/`（全局）或 `.cursor/skills/`（工作区）；也会自动扫描 `.agents/skills/`
- OpenCode：`~/.config/opencode/skills/`（全局）或 `.opencode/skills/`（工作区）
- Kimi Code：`~/.kimi-code/skills/`（全局，或 `$KIMI_CODE_HOME/skills/`）或 `.kimi-code/skills/`（项目）；也会自动扫描 `.agents/skills/`

#### 🧩 其他支持 Skills 的工具
若你使用的是其他支持 Skills 的工具，请按照该工具文档指定的 Skills 根目录进行安装。只要满足**标准安装规范**（根目录包含 `SKILL.md` 并保持 `skills/`、`references/` 与 `scripts/` 结构），即可被正确识别。

</details>

---

<details>
<summary><b>📦 Skills 分类概要 (805)</b></summary>

| 分类 | 数量 | 核心功能 |
| :--- | :---: | :--- |
| **YooAsset** | 40 | 热更新打包/Collector 完整 CRUD/BuildReport 资产与依赖分析/PlayMode 运行时验证/Reporter-Debugger-AssetArtScanner 工具 |
| **Behavior** | 10 | Unity Behavior 行为图资产/Agent 组件/黑板变量（com.unity.behavior，反射实现） |
| **HybridCLR** | 12 | HybridCLR 热更新设置/代码生成/DLL 编译与拷贝流水线（com.code-philosophy.hybridclr，反射实现） |
| **Workflow** | 40 | 持久化历史/分级任务快照/内容寻址文件存储/自动清理/会话级撤销/回滚/清空历史/书签/批量查询预览执行作业 |
| **Cinemachine** | 34 | 2.x/3.x双版本自动安装/混合相机/ClearShot/TargetGroup/Spline |
| **Netcode** | 39 | Netcode for GameObjects 设置/预制体/生命周期/Host-Server-Client 工作流 /NGO 2.5+ 挂载与组件控制器 |
| **UI** | 29 | Canvas/Button/Text/InputField/Dropdown/ScrollView/Layout/对齐/Image 与 Selectable 工具 |
| **UI Toolkit** | 31 | UXML/USS文件管理/UIDocument/PanelSettings全属性读写/模板生成/结构检查/批量创建 /运行时数据绑定/UXML 升级/世界空间面板 |
| **ShaderGraph** | 23 | Shader Graph 创建/检查/黑板编辑/受限节点编辑 |
| **ProBuilder** | 22 | ProBuilder 形体创建/面边操作/UV工具/枢轴编辑/批量创建/网格合并 |
| **XR** | 22 | XR rig 搭建/Interactor/Interactable/传送/连续移动/UI/触觉反馈/交互层配置 |
| **Material** | 21 | 材质属性批量修改/HDR/PBR/Emission/关键字/渲染队列 |
| **PostProcess** | 10 | SRP 后处理效果管理 |
| **GameObject** | 19 | 创建/查找/变换同步/批量操作/层级管理/重命名/复制 |
| **Perception** | 18 | 场景摘要/健康检查/栈检测/上下文导出/依赖分析/热点发现/差异对比/Tag-Layer统计/性能提示 |
| **Volume** | 9 | VolumeProfile/Volume/VolumeComponent 创建与参数编辑 |
| **Validation** | 16 | 项目验证/空文件夹清理/引用检测/网格碰撞/Shader错误 |
| **URP** | 7 | URP 资产/Renderer/Renderer Feature 检查与编辑 |
| **Decal** | 7 | URP Decal Projector 创建/检查/配置/删除工作流 |
| **DOTween** | 21 | DOTweenAnimation 编辑器期配置与调参 |
| **PrimeTween** | 5 | PrimeTween Free 检查、工厂方法发现与运行时补间/序列脚本生成 |
| **Editor** | 16 | Play 模式运行捕获/逐帧步进/运行时状态查询/选择/撤销重做/上下文获取/变更日志/菜单执行 |
| **Physics** | 12 | 射线检测/球形投射/盒形投射/物理材质/层碰撞矩阵 |
| **Script** | 12 | C#脚本创建/读取/替换/列表/信息/重命名/移动/分析 |
| **Timeline** | 12 | 轨道创建/删除/Clip管理/播放控制/绑定/时长设置 |
| **Asset** | 12 | 资产导入/删除/移动/复制/搜索/文件夹/批量建文件夹/批量操作/刷新 |
| **AssetImport** | 11 | 纹理/模型/音频/Sprite导入设置/标签管理/重导入 |
| **Camera** | 12 | Scene View控制/Game Camera创建/属性/截图/正交切换/列表 |
| **Graphics** | 11 | GraphicsSettings/QualitySettings/SRP 资产操作 |
| **Package** | 11 | 包管理/安装/移除/搜索/版本/依赖/Cinemachine/Splines |
| **Prefab** | 11 | 创建/实例化/覆盖应用与恢复/批量实例化/变体/查找实例/资产属性设置 |
| **Shader** | 11 | Shader创建/URP模板/编译检查/关键字/变体分析/全局关键字 |
| **Test** | 13 | 测试运行/按名运行/分类/模板创建/汇总统计 |
| **Animator** | 10 | 动画控制器/参数/状态机/过渡/分配/播放 |
| **Audio** | 10 | 音频导入设置/AudioSource/AudioClip/AudioMixer/批量 |
| **Cleaner** | 10 | 未使用资源/重复文件/空文件夹/丢失脚本修复/依赖树 |
| **Component** | 14 | 添加/移除/属性配置/批量操作/复制/启用禁用 |
| **Console** | 10 | 日志捕获/清理/导出/统计/暂停控制/折叠/播放清除 |
| **Debug** | 11 | 错误日志/编译检查/堆栈/程序集/定义符号/内存信息/编辑器健康诊断 |
| **Event** | 11 | UnityEvent监听器管理/批量添加/复制/状态控制/列举 |
| **Light** | 11 | 灯光创建/类型配置/强度颜色/批量开关/探针组/反射探针/光照贴图 |
| **Model** | 10 | 模型导入设置/Mesh信息/材质映射/动画/骨骼/批量 |
| **NavMesh** | 10 | 烘焙/路径计算/Agent/Obstacle/采样/区域代价 |
| **Optimization** | 10 | 纹理压缩/网格压缩/音频压缩/场景分析/静态标记/LOD/重复材质/过度绘制 |
| **Profiler** | 10 | FPS/内存/纹理/网格/材质/音频/渲染统计/对象计数/AssetBundle |
| **Scene** | 10 | 多场景加载/卸载/激活/截图/上下文/依赖分析/报告导出 |
| **ScriptableObject** | 13 | 创建/读写/序列化属性路径写入(嵌套/数组/引用)/批量设置/删除/查找/JSON导入导出 |
| **Smart** | 10 | 场景SQL查询/空间查询/自动布局/对齐地面/网格吸附/随机化/替换 |
| **Terrain** | 10 | 地形创建/高度图/Perlin噪声/平滑/平坦化/纹理绘制 |
| **Texture** | 10 | 纹理导入设置/平台设置/Sprite/类型/尺寸查找/批量 |
| **Project** | 10 | Player 出包/渲染管线/构建设置/包管理/Layer/Tag/PlayerSettings/质量 |
| **Addressables** | 8 | Addressable 资产组/Profiles/Labels/构建路径/构建/条目增删（com.unity.addressables，反射实现） |
| **QFramework** | 20 | QFramework 架构层代码生成/ViewController 与 UIKit 面板代码生成/UIKit 设置/ResKit AssetBundle 标记-构建-清理/架构扫描/API 文档查询（无 UPM 包，反射实现） |
| **Sample** | 8 | 基础示例：创建/删除/变换/场景信息 |

> ⚠️ 大部分模块支持 `*_batch` 批量操作，操作多个物体时应优先使用批量 Skills 以提升性能。
>
> 🧠 `unity-skills/skills/` 目录下额外提供 **28 个 advisory 设计模块**，用于在脚本编写前辅助 AI 进行架构、性能、可维护性、Inspector 设计与包级源码规则决策。

</details>

---

## 📂 项目结构

```bash
.
├── SkillsForUnity/                 # Unity 编辑器插件 (UPM Package)
│   ├── package.json                # com.besty.unity-skills
│   ├── unity-skills~/              # 跨平台 AI Skill 模板 (波浪线隐藏目录, 随包分发)
│   │   ├── SKILL.md                # 主 Skill 定义 (AI 读取)
│   │   ├── scripts/
│   │   │   └── unity_skills.py     # Python 客户端库
│   │   ├── skills/                 # 82 个模块文档（54 个 REST/模块文档 + 28 个 advisory 文档）
│   │   └── references/             # Unity 开发参考文档
│   └── Editor/Skills/              # 核心 Skill 逻辑 (56 个 *Skills.cs → 54 个 SkillCategory 分类，共 805 Skills)
│       ├── SkillsHttpServer.cs     # HTTP 服务器核心 (Producer-Consumer)
│       ├── SkillRouter.cs          # 请求路由 & 反射发现 Skills
│       ├── WorkflowManager.cs      # 持久化工作流 (Task/Session/Snapshot)
│       ├── RegistryService.cs      # 全局注册表 (多实例发现)
│       ├── GameObjectFinder.cs     # 统一 GO 查找器 (name/instanceId/path)
│       ├── BatchExecutor.cs        # 泛型批处理框架
│       ├── GameObjectSkills.cs     # GameObject 操作 (18 skills)
│       ├── MaterialSkills.cs       # Material 操作 (21 skills)
│       ├── CinemachineSkills.cs    # Cinemachine 2.x/3.x (34 skills)
│       ├── WorkflowSkills.cs       # Workflow 撤销/回滚 (24 skills)
│       ├── PerceptionSkills.cs     # 场景理解 (18 skills)
│       └── ...                     # 805 Skills 源码
├── docs/
│   └── SETUP_GUIDE.md              # 完整安装使用指南
├── CHANGELOG.md                    # 版本更新记录
└── LICENSE                         # MIT 开源协议
```

---

## ⭐Star History

<a href="https://www.star-history.com/?repos=Besty0728%2FUnity-Skills&type=date&logscale=&legend=top-left">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="docs/star-history-dark.svg" />
   <source media="(prefers-color-scheme: light)" srcset="docs/star-history-light.svg" />
   <img alt="Star History Chart" src="docs/star-history-light.svg" />
 </picture>
</a>

---

## 📄 开源协议
本项目采用 [MIT License](LICENSE) 许可。

**内置字体(独立协议):** 编辑器窗口内置了一个子集化的中文字体
`SkillsForUnity/Editor/UI/Fonts/UnitySkillsCN-Regular.ttf`,来源于
[Maple Mono](https://github.com/subframe7536/maple-font)(CN 变体),遵循
**SIL Open Font License 1.1**(非 MIT)。完整许可证与署名随字体置于该目录
(`OFL.txt`、`THIRD-PARTY-NOTICES.md`)。
