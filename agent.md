# UnitySkills Agent 文档

> 本文档面向 AI Agent，提供项目全貌速览，帮助 AI 快速理解项目结构与开发规范。

---

## 📋 项目概览

| 属性 | 值 |
|------|-----|
| **项目名称** | UnitySkills |
| **版本** | 1.5.1 |
| **技术栈** | C# (Unity Editor) + Python (Client) |
| **Unity 版本** | 2021.3+ (已验证 Unity 6 / 6000.2.x) |
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
│  │ (Multi-Instance)│  │(Auto-Undo)  │  │  (431 Skills)   │  │
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
    - **设计决策：Base64 资源备份不限制文件大小**。Unity 项目中纹理、模型等资源可能超过 10MB，为保证完整的撤销/重做能力，WorkflowManager 对所有非脚本资源进行无限制的 Base64 快照备份。这是有意为之的设计，不是安全漏洞。

6.  **IPv4/IPv6 双绑定 & 启动自检** [v1.5.1]:
    - `HttpListener` 同时绑定 `http://localhost:{port}/` 和 `http://127.0.0.1:{port}/`，解决部分 Windows 系统 `localhost` 仅解析到 IPv6 导致 `127.0.0.1` 不可达的问题。
    - 启动后自动 Self-Test：通过 `EditorApplication.delayCall` + `ThreadPool` 异步请求两个地址的 `/health` 端点，结果输出到 Console，帮助用户快速定位连接问题。
    - `SceneScreenshot` 自动补全文件扩展名：当 `filename` 参数不含扩展名时自动追加 `.png`，确保截图文件可被 Unity 正常识别和预览。

**Producer-Consumer 模式** (线程安全)：
- **Producer** (HTTP 线程)：接收 HTTP 请求，入队到 `RequestJob` 队列
- **Consumer** (Unity 主线程)：通过 `EditorApplication.update` 处理队列中的任务
- **自动恢复**：Domain Reload 后自动重启服务器（端口持久化 + 秒级延迟重试 + 端口 fallback）
- **超时可配置**：请求超时默认 60 分钟，用户可在设置面板自定义，Python 客户端自动同步
- **超时值线程安全缓存**：`RequestTimeoutMs` 在 `Start()` 时缓存到静态字段，避免 ThreadPool 线程调用 `EditorPrefs`（主线程限定 API）导致 500 错误

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
│           ├── ComponentSkills.cs      # Component 操作 (10 skills)
│           ├── SceneSkills.cs          # Scene 管理 (18 skills, 含 Perception)
│           ├── MaterialSkills.cs       # Material 操作 (21 skills)
│           ├── CinemachineSkills.cs    # Cinemachine 3.x (23 skills)
│           ├── WorkflowSkills.cs       # Workflow 撤销/回滚 (22 skills, 含 bookmark/history)
│           ├── UISkills.cs             # UI 元素创建 (16 skills)
│           ├── AssetSkills.cs          # Asset 管理 (15 skills)
│           ├── EditorSkills.cs         # Editor 控制 (12 skills)
│           ├── AudioSkills.cs          # Audio 音频 (12 skills)
│           ├── TextureSkills.cs        # Texture 纹理 (12 skills)
│           ├── ModelSkills.cs          # Model 模型 (12 skills)
│           ├── TimelineSkills.cs       # Timeline 时间线 (12 skills)
│           ├── PhysicsSkills.cs        # Physics 物理 (12 skills)
│           ├── ScriptSkills.cs         # Script 管理 (12 skills, 含 analyze)
│           ├── AssetImportSkills.cs    # AssetImport 导入设置 (11 skills)
│           ├── ProjectSkills.cs        # Project 项目设置 (11 skills)
│           ├── ShaderSkills.cs         # Shader 操作 (11 skills)
│           ├── CameraSkills.cs         # Camera 相机 (11 skills)
│           ├── PackageSkills.cs        # Package 包管理 (11 skills)
│           ├── TerrainSkills.cs        # Terrain 地形 (10 skills)
│           ├── PrefabSkills.cs         # Prefab 操作 (10 skills)
│           ├── AnimatorSkills.cs       # Animator 管理 (10 skills)
│           ├── LightSkills.cs          # Light 配置 (10 skills)
│           ├── ValidationSkills.cs     # 项目验证 (10 skills)
│           ├── OptimizationSkills.cs   # 性能优化 (10 skills)
│           ├── CleanerSkills.cs        # 项目清理 (10 skills)
│           ├── NavMeshSkills.cs        # NavMesh 导航 (10 skills)
│           ├── ScriptableObjectSkills.cs # ScriptableObject (10 skills)
│           ├── ConsoleSkills.cs        # Console 控制台 (10 skills)
│           ├── DebugSkills.cs          # Debug 调试 (10 skills)
│           ├── EventSkills.cs          # Event 事件 (10 skills)
│           ├── SmartSkills.cs          # AI 推理技能 (10 skills)
│           ├── TestSkills.cs           # Test 测试 (10 skills)
│           ├── ProfilerSkills.cs       # Profiler 性能分析 (10 skills)
│           ├── PerceptionSkills.cs     # Perception 场景理解 (9 skills)
│           ├── SampleSkills.cs         # 基础示例 (8 skills)
│           └── ... (37 个 *Skills.cs 文件, 共 431 Skills)
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
- 请求超时: 用户可配置（默认 60 分钟），通过 /health 端点暴露给客户端自动同步
- Domain Reload 韧性: 主动释放端口 + 端口持久化 + 秒级延迟重试 + 端口 fallback
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

## 🛡️ 代码质量保障 (v1.5.0 全项目审计)

v1.5.0 对全部 38 个 C# 文件 + Python 客户端进行了完整审计，修复 36 项缺陷：

### 安全防护
- **ReDoS 防护**: 所有用户输入正则表达式添加 1 秒超时 (`ScriptSkills`, `GameObjectSkills`)
- **路径注入防护**: skill name 校验拒绝 `/` `\` `..` 等路径字符 (`SkillsHttpServer`)
- **空引用防护**: `PrefabSkills`/`SceneSkills`/`UISkills`/`CinemachineSkills`/`SmartSkills` 等 7 处 null 检查
- **资源泄漏防护**: `LightSkills` 错误路径清理 GameObject；`SkillsHttpServer` Stop() 线程 Join

### 数据完整性
- **原子文件写入**: `WorkflowManager.SaveHistory()` 先写 `.tmp` 再原子替换，防止崩溃丢数据
- **快照上限**: 单任务最多 500 条快照，防止批量操作内存溢出
- **进程存活检查**: `RegistryService` 清理条目时验证进程是否存活，避免僵尸注册
- **AnimatorSkills**: `controller.parameters` 数组副本修改后回写

### 已知设计决策（非缺陷）
- `WorkflowManager.SnapshotObject()` 内部已有 `_currentTask == null` 守卫，外部调用无需额外检查
- `ManualResetEventSlim` 通过 ownership transfer 模式管理，WaitAndRespond finally 中 Dispose
- `get_skills()`/`health()` 使用 `requests.get` 而非 Session 对象，属简单 GET 请求的设计选择
- Base64 资源备份不限制文件大小，保证完整撤销/重做能力
- `script_create` 同时接受 `scriptName` 和 `name` 参数（`scriptName` 优先），空值时返回错误而非生成无名文件
- `light_add_probe_group` 支持 `gridX/gridY/gridZ` + `spacingX/spacingY/spacingZ` 参数，一步创建网格布局光照探针

### Unity 6 兼容性修复 (v1.5.1)

以下修复确保在 Unity 6 (6000.2.x) 上正常运行：

- **`console_set_collapse` / `console_set_clear_on_play`**: Unity 6 移除了 `ConsoleWindow.s_ConsoleFlags`，改为多级回退策略
- **`cinemachine_set_active`**: CM3 的 `Priority` 属性不支持 LINQ `Max()` 泛型比较，改用手动迭代
- **`audio_create_mixer`**: `ScriptableObject.CreateInstance(AudioMixerController)` 触发 `ExtensionOfNativeClass` 异常，改用 `CreateMixerControllerAtPath` 工厂方法。注："Mixer is not initialized" 日志为 Unity 6 内部已知问题，不影响功能
- **`event_add_listener`**: `GetComponent("GameObject")` 返回 null，新增特殊处理
- **`component_set_enabled`**: 新增 `Renderer` 和 `Collider` 类型支持（它们不继承 `Behaviour`）
- **`optimize_find_duplicate_materials`**: `mat.color` 访问不存在的 `_Color` 属性时异常，改为 `HasProperty` 安全检查
- **Splines 版本适配**: Unity 6 自动使用 Splines 2.8.3，Unity 2022 使用 2.8.0

---

## 📊 Skills 模块汇总 (431)

| 模块 | Skills 数量 | 核心功能 |
|------|:-----------:|----------|
| **Cinemachine** | 23 | 2.x/3.x双版本支持/自动安装/混合相机/ClearShot/TargetGroup/Spline |
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
