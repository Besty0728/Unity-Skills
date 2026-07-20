# 🎮 UnitySkills

<p align="center">
  <img src="docs/Unity-Skills-H.png" alt="Unity-Skills" width="800">
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Unity-2022.3%2B-black?style=for-the-badge&logo=unity" alt="Unity">
  <img src="https://img.shields.io/badge/Skills-740-green?style=for-the-badge" alt="Skills">
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

## 🤝 致谢
本项目基于 [unity-mcp](https://github.com/CoplayDev/unity-mcp) 的优秀理念深度重构与功能扩展。

---

## 🚀 核心特性

- 🛠️ **740 REST Skills 全能库**：包含 52 个功能源码模块和 23 个 advisory 设计模块，支持 Batch 批处理，一次操控多个对象。
- ⚡ **调用效率革命性提升 (v2.0.1+)**：Schema 缓存 + 指数退避轮询 + BATCH-FIRST 引导 → **Token 消耗 ↓ 96%**，**简单任务 4-6 次调用 → 1 次（↓ 75-83%）**。当前：v2.2.1。
- 🔐 **三档权限模式 (v1.9.0+)**：Approval / Auto / Bypass，配合双轨审批渠道（Dialog / Panel），对齐 Claude Code permission modes；老用户升级零感知。
- 🤖 **4 大 IDE 原生支持**：Claude Code / Antigravity / Codex / Cursor，一键安装即用。
- 🛡️ **事务原子性保障**：操作失败自动回滚，场景永不残留，确保流程安全。
- 🌍 **多实例同时控制**：自动端口发现与全局注册表，支持同时操控多个 Unity 项目。
- 🔗 **超长稳定连接**：请求超时可配（默认 15 分钟），Domain Reload 后自动恢复，脚本编译/资源重导入等短暂中断会提示重试。
- 🛡️ **防幻觉 Guardrails**：每个 Skill 模块内置 DO NOT 清单和路由规则，防止 AI 调用不存在的命令或参数错误。

---

## 🔐 操作模式 (v1.9.0+)

UnitySkills 引入真正的服务端权限系统，对齐 Claude Code permission modes。模式切换统一在 Unity 面板完成——打开 **Window > UnitySkills**，点 ⚙（设置）按钮进入 **Server** 区——**不再支持对话触发词**。

| 模式 | 默认 | 行为 | 适用场景 |
|:-----|:----:|:-----|:---------|
| **Approval（审批）** | — | AI 想做事 → 服务端返回 `MODE_RESTRICTED` + grant token → 用户审批 → AI 重放 token 后执行 | 重控制、敏感项目 |
| **Auto（自动）** | 新安装 | AI 直接执行 FullAuto skill；服务端仅拦自动判定的高危操作（NeverInSemi） | 日常开发 |
| **Bypass（全部直接放行）** | 老安装升级保持 | 全部放行，仅保留高危 `ConfirmationToken` 二次确认 | 自动化任务、CI、快速迭代 |

**Approval 模式双轨审批**：
- **Dialog 渠道**（默认）—— AI 对话说明意图 + grant token，用户文字同意后 AI 调 `POST /permission/grant` 重放
- **Panel 渠道**（面板可选开启）—— grant token 必须在 Unity 面板点 **[Approve]** 才生效；AI 未经面板批准直接 grant 会返回 `GRANT_PENDING_APPROVAL`

**老用户升级零感知**：插件检测旧版 `UnitySkills_*` EditorPrefs key 自动识别老安装，默认保持 **Bypass**，行为与原 Full-Auto 完全一致，无需任何操作。新安装默认 **Auto** —— FullAuto skill 直接执行，仅 NeverInSemi（Delete / MayEnterPlayMode / MayTriggerReload / 高危）操作会被服务端拦截。若需要按 skill 手动审批，在 ⚙ 设置抽屉的 Server 区切到 **Approval**。

> ❌ 不再识别对话触发词（如 `"全自动模式"` / `"semi-auto"`），请在 **Window > UnitySkills** 面板点 ⚙ 设置按钮切换。
>
> 📜 审计日志：`Library/UnitySkillsAudit.jsonl`（per-project，jsonl，1MB 滚动，保留 3 份），记录每次 grant / revoke / 被拒命中 / 调用。从 ⚙ 设置抽屉 → **查看审计日志**（或快捷键 <kbd>Alt</kbd>+<kbd>Shift</kbd>+<kbd>L</kbd>）打开浏览器，可浏览、过滤、单条删除（✕）或整体清空（🗑 Clear All）—— 删除动作本身会写 `audit_deleted` / `audit_cleared` 追踪事件，日志依然可审计。
>
> 🗑 Skill Installer 卡片的"卸载"按钮按 scope 智能形变：未装为灰态；仅一处装则按钮自带 scope 标签直接卸载；两处都装则显示 `Uninstall ▾` 下拉，分别选择 Project / Global。
>
> 23 个 advisory 设计模块（架构、性能、设计模式、可测试性、包级源码规则等）在所有模式下均可用，按需自动加载。

---

## 🏗️ 快速安装支持的IDE/终端

本项目针对以下环境进行了深度优化，确保持续、稳定的开发体验（未在下表中的不代表不支持，只是没有快捷安装，可选用 ***自定义安装*** 到对应目录）：

| AI 终端 | 支持状态 | 特色功能 |
| :--- | :---: | :--- |
| **Antigravity** | ✅ 支持 | 基于开放 Agent Skills 标准，工作区使用 `.agents/skills/`，全局使用 `~/.gemini/antigravity/skills/`。 |
| **Claude Code** | ✅ 支持 | 智能识别 Skill 意图，支持复杂多步自动化。 |
| **Codex** | ✅ 支持 | 支持 `$skill` 显式调用和隐式意图识别。工作区与 Antigravity 共享 `.agents/skills/`。 |
| **Cursor** | ✅ 支持 | 自动扫描 `.cursor/skills/` 和 `.agents/skills/`；支持 `/skill-name` 显式触发；可在 设置 → Rules 查看已加载技能。 |

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
2. 选择对应的终端图标（Claude / Antigravity / Codex / Cursor）。
3. 点击 **"Install"** 即可完成环境配置，无需手动拷贝代码。

> 安装器会复制包内的 `unity-skills~/` 模板目录到目标位置。
>
> 安装器落盘文件说明（生成于目标目录）：
> - `SKILL.md`
> - `skills/`
> - `references/`
> - `scripts/unity_skills.py`
> - `scripts/agent_config.json`（包含 Agent 标识）

> **Codex 特别说明**：Antigravity 和 Codex 工作区都使用 `.agents/skills/` —— 装一次即两边可用。Codex 自动扫描 `.agents/skills/` 发现 skills，无需在 `AGENTS.md` 中声明。

📘 需要更完整的安装与使用说明，请查看：[安装指南](docs/SETUP_GUIDE_CN.md) | [Setup Guide](docs/SETUP_GUIDE.md)

<details>
<summary><h3>4. 手动安装 Skills（可选）</h3></summary>

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

#### 🧩 其他支持 Skills 的工具
若你使用的是其他支持 Skills 的工具，请按照该工具文档指定的 Skills 根目录进行安装。只要满足**标准安装规范**（根目录包含 `SKILL.md` 并保持 `skills/`、`references/` 与 `scripts/` 结构），即可被正确识别。

</details>

---

<details>
<summary><h2>📦 Skills 分类概要 (740)</h2></summary>

| 分类 | 数量 | 核心功能 |
| :--- | :---: | :--- |
| **YooAsset** | 40 | 热更新打包/Collector 完整 CRUD/BuildReport 资产与依赖分析/PlayMode 运行时验证/Reporter-Debugger-AssetArtScanner 工具 |
| **Workflow** | 24 | 持久化历史/分级任务快照/内容寻址文件存储/自动清理/会话级撤销/回滚/清空历史/书签/批量查询预览执行作业 |
| **Cinemachine** | 34 | 2.x/3.x双版本自动安装/混合相机/ClearShot/TargetGroup/Spline |
| **Netcode** | 33 | Netcode for GameObjects 设置/预制体/生命周期/Host-Server-Client 工作流 |
| **UI** | 29 | Canvas/Button/Text/InputField/Dropdown/ScrollView/Layout/对齐/Image 与 Selectable 工具 |
| **UI Toolkit** | 25 | UXML/USS文件管理/UIDocument/PanelSettings全属性读写/模板生成/结构检查/批量创建 |
| **ShaderGraph** | 23 | Shader Graph 创建/检查/黑板编辑/受限节点编辑 |
| **ProBuilder** | 22 | ProBuilder 形体创建/面边操作/UV工具/枢轴编辑/批量创建/网格合并 |
| **XR** | 22 | XR rig 搭建/Interactor/Interactable/传送/连续移动/UI/触觉反馈/交互层配置 |
| **Material** | 21 | 材质属性批量修改/HDR/PBR/Emission/关键字/渲染队列 |
| **PostProcess** | 10 | SRP 后处理效果管理 |
| **GameObject** | 19 | 创建/查找/变换同步/批量操作/层级管理/重命名/复制 |
| **Perception** | 18 | 场景摘要/健康检查/栈检测/上下文导出/依赖分析/热点发现/差异对比/Tag-Layer统计/性能提示 |
| **Volume** | 9 | VolumeProfile/Volume/VolumeComponent 创建与参数编辑 |
| **Validation** | 10 | 项目验证/空文件夹清理/引用检测/网格碰撞/Shader错误 |
| **URP** | 7 | URP 资产/Renderer/Renderer Feature 检查与编辑 |
| **Decal** | 7 | URP Decal Projector 创建/检查/配置/删除工作流 |
| **DOTween** | 21 | DOTweenAnimation 编辑器期配置与调参 |
| **PrimeTween** | 5 | PrimeTween Free 检查、工厂方法发现与运行时补间/序列脚本生成 |
| **Editor** | 14 | Play 模式运行捕获/选择/撤销重做/上下文获取/变更日志/菜单执行 |
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
| **Debug** | 10 | 错误日志/编译检查/堆栈/程序集/定义符号/内存信息 |
| **Event** | 11 | UnityEvent监听器管理/批量添加/复制/状态控制/列举 |
| **Light** | 10 | 灯光创建/类型配置/强度颜色/批量开关/探针组/反射探针/光照贴图 |
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
| **Sample** | 8 | 基础示例：创建/删除/变换/场景信息 |
| **Diagnose** | 1 | 编辑器健康聚合快照（控制台/编译/工作流/服务器/作业） |

> ⚠️ 大部分模块支持 `*_batch` 批量操作，操作多个物体时应优先使用批量 Skills 以提升性能。
>
> 🧠 `unity-skills/skills/` 目录下额外提供 **23 个 advisory 设计模块**，用于在脚本编写前辅助 AI 进行架构、性能、可维护性、Inspector 设计与包级源码规则决策。

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
│   │   ├── skills/                 # 71 个模块文档（48 个 REST/模块文档 + 23 个 advisory 文档）
│   │   └── references/             # Unity 开发参考文档
│   └── Editor/Skills/              # 核心 Skill 逻辑 (52 个 *Skills.cs, 共 740 Skills)
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
│       └── ...                     # 740 Skills 源码
├── docs/
│   └── SETUP_GUIDE.md              # 完整安装使用指南
├── CHANGELOG.md                    # 版本更新记录
└── LICENSE                         # MIT 开源协议
```

---

## ⭐Star History

<a href="https://www.star-history.com/?repos=Besty0728%2FUnity-Skills&type=date&logscale=&legend=top-left">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/chart?repos=Besty0728/Unity-Skills&type=date&theme=dark&logscale&legend=top-left&sealed_token=puHA56V-rw188klMvHmwK9HLr_ngIGDkOUeEYYDkfQK3jex-mQSY6gtQdUX5bpeyxWr5NL-itvG9WkEDtFn3BasAn6uuPtHh-qWIVrwRkXb6cGi5wYWeehiHyw-STZw9Aam6AX47PMNtw62yrm-WZOp7UaUFK96GqOHmKTDCcx9ryitoAMkHAcOO18sG" />
   <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/chart?repos=Besty0728/Unity-Skills&type=date&logscale&legend=top-left&sealed_token=puHA56V-rw188klMvHmwK9HLr_ngIGDkOUeEYYDkfQK3jex-mQSY6gtQdUX5bpeyxWr5NL-itvG9WkEDtFn3BasAn6uuPtHh-qWIVrwRkXb6cGi5wYWeehiHyw-STZw9Aam6AX47PMNtw62yrm-WZOp7UaUFK96GqOHmKTDCcx9ryitoAMkHAcOO18sG" />
   <img alt="Star History Chart" src="https://api.star-history.com/chart?repos=Besty0728/Unity-Skills&type=date&logscale&legend=top-left&sealed_token=puHA56V-rw188klMvHmwK9HLr_ngIGDkOUeEYYDkfQK3jex-mQSY6gtQdUX5bpeyxWr5NL-itvG9WkEDtFn3BasAn6uuPtHh-qWIVrwRkXb6cGi5wYWeehiHyw-STZw9Aam6AX47PMNtw62yrm-WZOp7UaUFK96GqOHmKTDCcx9ryitoAMkHAcOO18sG" />
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
