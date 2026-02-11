# UnitySkills Agent 文档

> 本文档面向 AI Agent，提供项目全貌速览，帮助 AI 快速理解项目结构与开发规范。

---

## 📋 项目概览

| 属性 | 值 |
|------|-----|
| **项目名称** | UnitySkills |
| **版本** | 1.4.4 |
| **技术栈** | C# (Unity Editor) + Python (Client) |
| **Unity 版本** | 2021.3+ |
| **协议** | MIT |
| **核心功能** | 通过 REST API 让 AI 直接控制 Unity 编辑器 |

---

## 🏗️ 架构设计

```
┌─────────────────────────────────────────────────────────────┐
│                    AI Agent (Claude / Antigravity / Gemini)  │
│                         Skill Consumer                       │
└─────────────────────┬───────────────────────────────────────┘
                      │ HTTP REST API
                      ▼
┌─────────────────────────────────────────────────────────────┐
│                unity_skills.py Client                        │
│   call_skill() / workflow_context() / health() / get_skills()│
└─────────────────────┬───────────────────────────────────────┘
                      │ HTTP POST → localhost:8090-8100
                      ▼
┌─────────────────────────────────────────────────────────────┐
│             SkillsForUnity (Unity Editor Plugin)             │
│  ┌─────────────────┐  ┌─────────────┐  ┌─────────────────┐  │
│  │ SkillsHttpServer│→ │ SkillRouter │→ │[UnitySkill] 方法│  │
│  │ (Multi-Instance)│  │(Auto-Undo)  │  │  (277 Skills)   │  │
│  └─────────────────┘  └─────────────┘  └─────────────────┘  │
│           ↓                  ↓                              │
│  ┌─────────────────┐  ┌─────────────────────────────────┐   │
│  │RegistryService  │  │ WorkflowManager (Persistent Undo)│  │
│  │ (多实例发现)     │  │ (Task/Session/Snapshot 回滚)     │  │
│  └─────────────────┘  └─────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

### 核心设计模式 & 新特性 (v1.4+)

1.  **Multi-Instance (多实例支持)**:
    - Server 自动寻找可用端口 `8090-8100`。
    - 注册到全局 `~/.unity_skills/registry.json`，支持 AI 发现与连接。

2.  **Transactional Skills (原子化)**:
    - 所有 Skill 自动包裹在 Unity Undo Group 中。
    - 执行失败自动回滚 (Revert)，保证场景状态一致性。

3.  **Batch Operations (批处理)**:
    - 提供 `_batch` 后缀的 API (如 `gameobject_create_batch`)，一次请求处理 1000+ 物体。

4.  **Token Optimization (Summary Mode)**:
    - 大量数据返回时自动截断 (`verbose=false`)。
    - `SKILL.md` 专为 AI 阅读优化。

5.  **Persistent Workflow (持久化回滚)** [v1.4]:
    - `workflow_task_start/end`：创建可回滚的任务标签。
    - `workflow_undo_task/redo_task`：任意任务回滚与重做。
    - `workflow_session_*`：会话级（对话级）批量回滚。
    - 历史记录跨 Editor 重启持久保存。

**Producer-Consumer 模式** (线程安全)：
- **Producer** (HTTP 线程)：接收 HTTP 请求，入队到 `RequestJob` 队列
- **Consumer** (Unity 主线程)：通过 `EditorApplication.update` 处理队列中的任务
- **自动恢复**：Domain Reload 后自动重启服务器

---

## 📂 项目结构

```
Unity-Skills/
├── SkillsForUnity/                 # Unity 编辑器插件 (UPM Package)
│   ├── package.json                # com.besty.unity-skills
│   └── Editor/
│       └── Skills/
│           ├── SkillsHttpServer.cs     # HTTP 服务器核心 (Producer-Consumer)
│           ├── SkillRouter.cs          # 请求路由 & 反射发现 Skills
│           ├── WorkflowManager.cs      # 持久化工作流核心 (Task/Session)
│           ├── WorkflowModels.cs       # Snapshot/Task/Session 数据模型
│           ├── RegistryService.cs      # 全局注册表 (多实例发现)
│           ├── GameObjectFinder.cs     # 统一 GO 查找器 (name/instanceId/path)
│           ├── UnitySkillAttribute.cs  # [UnitySkill] 特性定义
│           ├── UnitySkillsWindow.cs    # 编辑器窗口 UI
│           ├── SkillInstaller.cs       # AI 工具一键安装器
│           ├── Localization.cs         # 中英双语 UI
│           │
│           ├── GameObjectSkills.cs     # GameObject 操作 (18 skills)
│           ├── ComponentSkills.cs      # Component 操作 (8 skills)
│           ├── SceneSkills.cs          # Scene 管理 (9 skills)
│           ├── MaterialSkills.cs       # Material 操作 (21 skills)
│           ├── CinemachineSkills.cs    # Cinemachine 3.x (23 skills)
│           ├── WorkflowSkills.cs       # Workflow 撤销/回滚 (22 skills)
│           ├── UISkills.cs             # UI 元素创建 (16 skills)
│           ├── EditorSkills.cs         # Editor 控制 (12 skills)
│           ├── AssetSkills.cs          # Asset 管理 (11 skills)
│           ├── TerrainSkills.cs        # Terrain 地形 (10 skills)
│           ├── PrefabSkills.cs         # Prefab 操作 (8 skills)
│           ├── AnimatorSkills.cs       # Animator 管理 (8 skills)
│           ├── LightSkills.cs          # Light 配置 (7 skills)
│           ├── ValidationSkills.cs     # 项目验证 (7 skills)
│           ├── ScriptSkills.cs         # Script 管理 (6 skills)
│           ├── ShaderSkills.cs         # Shader 操作 (6 skills)
│           ├── NextGenSkills.cs        # Perception 场景理解 (3 skills)
│           ├── SmartSkills.cs          # AI 推理技能 (3 skills)
│           └── ... (38 个 *Skills.cs 文件, 共 277 Skills)
│
├── unity-skills/                   # 跨平台 AI Skill 模板 (分发给 AI 工具)
│   ├── SKILL.md                    # 主 Skill 定义 (AI 读取)
│   ├── scripts/
│   │   └── unity_skills.py         # Python 客户端库
│   ├── skills/                     # 按模块分类的 Skill 文档
│   │   ├── gameobject/SKILL.md
│   │   ├── component/SKILL.md
│   │   ├── material/SKILL.md
│   │   └── ...
│   └── references/                 # Unity 开发参考文档
│       ├── 2d.md, 3d.md, physics.md
│       ├── shaders.md, ui.md
│       └── ...
│
├── docs/
│   └── SETUP_GUIDE.md              # 完整安装使用指南
├── README.md                       # 项目说明
├── CHANGELOG.md                    # 版本更新记录
└── LICENSE                         # MIT 协议
```

---

## 🔧 核心组件详解

### 1. SkillsHttpServer.cs

HTTP 服务器核心，采用 **Producer-Consumer** 架构保证线程安全：

```csharp
// 关键特性
- 端口: localhost:8090
- 自动恢复: Domain Reload 后通过 EditorPrefs 恢复状态
- Keep-Alive: 后台线程定时触发 Unity 更新，确保后台运行
- 速率限制: 内置防止过快请求的保护机制
```

### 2. SkillRouter.cs

反射发现所有标记 `[UnitySkill]` 的静态方法：

```csharp
// 核心方法
Initialize()      // 扫描所有程序集，发现 [UnitySkill] 方法
GetManifest()     // 返回所有 Skills 的 JSON 清单
Execute(name, json) // 执行指定 Skill
```

### 3. UnitySkillAttribute.cs

标记可被 REST API 调用的方法：

```csharp
[UnitySkill("skill_name", "描述信息")]
public static object MySkill(string param1, float param2 = 0)
{
    // 实现逻辑
    return new { success = true, result = "..." };
}
```

### 4. unity_skills.py

Python 客户端封装：

```python
import unity_skills

# 核心 API
unity_skills.call_skill("gameobject_create", name="Cube", primitiveType="Cube")
unity_skills.health()      # 检查服务器状态
unity_skills.get_skills()  # 获取所有可用 Skills

# Auto-Workflow (v1.4+) - 自动记录可回滚的操作
# 默认开启，所有修改操作自动创建 workflow task
unity_skills.set_auto_workflow(True)  # 开启/关闭

# Workflow Context - 多操作批量回滚
with unity_skills.workflow_context('Build Scene', 'Create player and env'):
    unity_skills.call_skill('gameobject_create', name='Player')
    unity_skills.call_skill('component_add', name='Player', componentType='Rigidbody')
# 所有操作可通过 workflow_undo_task 一次性回滚

# CLI 用法
python unity_skills.py --list
python unity_skills.py gameobject_create name=MyCube primitiveType=Cube
```

---

## 📊 Skills 模块汇总 (277)

| 模块 | Skills 数量 | 核心功能 |
|------|:-----------:|----------|
| **Cinemachine** | 23 | 2.x/3.x双版本支持/自动安装/混合相机/ClearShot/TargetGroup/Spline |
| **Workflow** | 22 | 持久化历史/任务快照/会话级撤销/回滚 |
| **Material** | 21 | 材质属性批量修改/HDR/PBR设置 |
| **GameObject** | 18 | 创建/查找/变换同步/批量操作/层级管理 |
| **UI System** | 16 | Canvas/Button/Text/Slider/锚点/布局 |
| **Editor** | 12 | 播放模式/选择/撤销重做/上下文获取 |
| **Asset** | 11 | 资产导入/搜索/文件夹/GUID管理 |
| **Terrain** | 10 | 地形创建/高度图/Perlin噪声/纹理绘制 |
| **Scene** | 9 | 多场景加载/卸载/激活/截图 |
| **Texture/Audio/Model** | 9 | 导入设置/压缩格式/质量优化 |
| **Prefab** | 8 | 创建/实例化/覆盖应用与恢复/批量实例化 |
| **Component** | 8 | 添加/移除/属性配置/批量操作 |
| **Animator** | 8 | 动画控制器/参数/状态机/过渡 |
| **Sample** | 8 | 示例场景/测试用例生成 |
| **Light** | 7 | 灯光创建/类型配置/强度颜色/批量开关 |
| **Validation** | 7 | 项目验证/空文件夹清理/引用检测 |
| **Script** | 6 | C# 脚本创建/编译检查/搜索 |
| **Shader** | 6 | Shader 查找/创建/属性列举 |
| **Debug** | 5 | 调试绘图/射线/Gizmos/Scene标注 |
| **Console** | 5 | 日志捕获/清理/输出监视 |
| **Cleaner** | 5 | 未使用资源/重复文件/丢失引用检测 |
| **ScriptableObject** | 5 | 创建SO实例/读写数据/查找 |
| **Event** | 4 | UnityEvent 监听器管理/调用 |
| **Project** | 4 | 渲染管线检测/Shader列表/质量设置 |
| **DebugEnhance** | 4 | 控制台日志增强/错误时暂停 |
| **Physics** | 4 | 物理材质/射线检测/层设置 |
| **Camera** | 4 | 相机创建/属性配置/截屏/视角对齐 |
| **AssetImport** | 4 | 强制重导入/批量修复/刷新 |
| **Test** | 4 | 断言测试/边界条件/性能测试 |
| **Perception** | 3 | 场景摘要/层级树/脚本分析 [Smart] |
| **Smart** | 3 | 场景SQL查询/自动布局/引用绑定 |
| **NavMesh** | 3 | 烘焙设置/代理创建/路径计算 |
| **Timeline** | 3 | 轨道创建/剪辑添加/绑定 |
| **Optimization** | 2 | 纹理压缩批量优化/模型网格压缩 |
| **Profiler** | 1 | 获取性能统计 (FPS/Memory) |
| **Package** | 7 | 包管理/Cinemachine安装/依赖处理 [v1.4.2] |

> ⚠️ **重要提示**：大部分模块都支持 `*_batch` 批量操作，操作多个物体时应优先使用批量 Skills。

---

## 🚀 快速使用

### 启动服务器

1. Unity 菜单: `Window > UnitySkills > Start Server`
2. Console 显示: `[UnitySkills] REST Server started at http://localhost:8090/`

### AI 调用示例

```python
import unity_skills

# 创建红色立方体
unity_skills.call_skill("gameobject_create", 
    name="RedCube", primitiveType="Cube", x=0, y=1, z=0)
unity_skills.call_skill("material_set_color", 
    name="RedCube", r=1, g=0, b=0)

# 添加物理组件
unity_skills.call_skill("component_add", 
    name="RedCube", componentType="Rigidbody")

# 保存场景
unity_skills.call_skill("scene_save", scenePath="Assets/Scenes/Demo.unity")
```

### HTTP 直接调用

```bash
# 获取所有 Skills
curl http://localhost:8090/skills

# 创建物体
curl -X POST http://localhost:8090/skill/gameobject_create \
  -H "Content-Type: application/json" \
  -d '{"name":"MyCube","primitiveType":"Cube","x":1,"y":2,"z":3}'
```

---

## ⚠️ 重要注意事项

### 1. Domain Reload

创建 C# 脚本时，Unity 会触发 Domain Reload：

```python
result = unity_skills.call_skill('script_create', name='MyScript', template='MonoBehaviour')
if result.get('success'):
    # 等待 Unity 重新编译完成
    time.sleep(5)  # 或使用 wait_for_unity()
```

### 2. 线程安全

- 所有 Unity API 调用仅在主线程执行
- HTTP 请求线程仅负责入队/出队
- 使用 `EditorApplication.update` 消费任务队列

### 3. 响应格式

所有 Skills 返回统一格式：

```json
{
  "status": "success",
  "skill": "gameobject_create",
  "result": {
    "success": true,
    "name": "MyCube",
    "instanceId": 12345,
    "position": {"x": 1, "y": 2, "z": 3}
  }
}
```

---

## 🤖 支持的 AI 终端

| 终端 | 支持状态 | 特色 |
|------|:--------:|------|
| **Antigravity** | ✅ | 支持 `/unity-skills` 斜杠命令 |
| **Claude Code** | ✅ | 智能识别 Skill 意图 |
| **Gemini CLI** | ✅ | 实验性 `experimental.skills` 支持 |
| **Codex** | ✅ | 支持 `$skill` 显式调用和隐式识别 |

---

## 📦 安装方式

### Unity 插件安装

```
Window → Package Manager → + → Add package from git URL
https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity
```

### AI Skills 配置

使用 Unity 编辑器一键安装：
1. `Window > UnitySkills` 打开窗口
2. 切换到 **AI Config** 标签页
3. 选择目标 AI 工具 (Claude / Antigravity / Gemini)
4. 点击 **Install** 完成配置

---

## 🔍 扩展开发

### 自定义 Skill

```csharp
using UnitySkills;

public static class MyCustomSkills
{
    [UnitySkill("my_custom_skill", "自定义操作描述")]
    public static object MyCustomSkill(string param1, float param2 = 0)
    {
        // 你的逻辑
        return new { success = true, message = "操作完成" };
    }
}
```

重启 REST 服务器后自动发现新 Skill。

---

## 📚 参考资源

| 文件 | 用途 |
|------|------|
| [SKILL.md](unity-skills/SKILL.md) | 完整 Skill API 参考 |
| [SETUP_GUIDE.md](docs/SETUP_GUIDE.md) | 详细安装使用指南 |
| [CHANGELOG.md](CHANGELOG.md) | 版本更新记录 |
| [references/](unity-skills/references/) | Unity 开发参考文档 |

---

## 📌 版本号更新规范

> ⚠️ **重要规则**：每次发布新版本时，必须同步更新以下 **6 处** 版本号：

| 序号 | 文件路径 | 位置 |
|:----:|----------|------|
| 1 | `agent.md` | 第 12 行 `\| **版本** \|` 表格 |
| 2 | `package.json` | 第 3 行 `"version": "x.x.x"` |
| 3 | `CHANGELOG.md` | 顶部新增 `## [x.x.x] - YYYY-MM-DD` 条目 |
| 4 | `SkillsHttpServer.cs` | `version = "x.x.x"` (health endpoint) |
| 5 | `SkillRouter.cs` | `version = "x.x.x"` (manifest) |
| 6 | `README.md` *(可选)* | 模块表中的 `[vX.X]` 标签 |

### 快速检查命令

```bash
# 检查所有版本号是否一致
grep -rn "1.3.1" --include="*.cs" --include="*.json" --include="*.md" | grep -E "version|版本"
```

---

## 🔀 Git 分支规则

> ⚠️ **重要规则**：main 和 beta 分支必须保持线性同步，不使用 merge commit。

### 同步方式

```bash
git checkout main
git reset --hard beta
git push origin main --force
```

### 规则说明

- **开发过程中**：只在 beta 分支操作，提交到 beta
- **开发完成后**：将 beta 同步到 main，保持双分支一致
- main 和 beta 保持相同的提交历史（线性）
- 不使用 merge commit，使用 `git reset --hard` 让分支指向同一提交
- 每次提交独立显示，最大化 GitHub 贡献记录
- 同步后使用 `git push --force` 更新远程
