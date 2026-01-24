# UnitySkills Agent 文档

> 本文档面向 AI Agent，提供项目全貌速览，帮助 AI 快速理解项目结构与开发规范。

---

## 📋 项目概览

| 属性 | 值 |
|------|-----|
| **项目名称** | UnitySkills |
| **版本** | 1.2.0 |
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
│        Python 封装层 - call_skill() / health() / get_skills()│
└─────────────────────┬───────────────────────────────────────┘
                      │ HTTP POST → localhost:8090
                      ▼
┌─────────────────────────────────────────────────────────────┐
│             SkillsForUnity (Unity Editor Plugin)             │
│  ┌─────────────────┐  ┌─────────────┐  ┌─────────────────┐  │
│  │ SkillsHttpServer│→ │ SkillRouter │→ │[UnitySkill] 方法│  │
│  │ (Multi-Instance)│  │(Undo-Aware) │  │  (Atomic)       │  │
│  └─────────────────┘  └─────────────┘  └─────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

### 核心设计模式 & 新特性 (v1.1)

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
│           ├── UnitySkillAttribute.cs  # [UnitySkill] 特性定义
│           ├── UnitySkillsWindow.cs    # 编辑器窗口 UI
│           ├── SkillInstaller.cs       # AI 工具一键安装器
│           │
│           ├── GameObjectSkills.cs     # GameObject 操作 (8 skills, +1 batch)
│           ├── ComponentSkills.cs      # Component 操作 (8 skills)
│           ├── SceneSkills.cs          # Scene 管理 (6 skills)
│           ├── MaterialSkills.cs       # Material 操作 (17 skills)
│           ├── LightSkills.cs          # Light 配置 (7 skills, +2 batch)
│           ├── AnimatorSkills.cs       # Animator 管理 (8 skills)
│           ├── UISkills.cs             # UI 元素创建 (10 skills)
│           ├── PrefabSkills.cs         # Prefab 操作 (5 skills)
│           ├── AssetSkills.cs          # Asset 管理 (8 skills)
│           ├── EditorSkills.cs         # Editor 控制 (12 skills)
│           ├── ConsoleSkills.cs        # Console 日志 (5 skills)
│           ├── ScriptSkills.cs         # Script 管理 (4 skills)
│           ├── ShaderSkills.cs         # Shader 操作 (3 skills)
│           ├── ValidationSkills.cs     # 项目验证 (7 skills)
│           ├── TextureSkills.cs        # 纹理导入设置 (3 skills) [v1.2]
│           ├── AudioSkills.cs          # 音频导入设置 (3 skills) [v1.2]
│           ├── ModelSkills.cs          # 模型导入设置 (3 skills) [v1.2]
│           └── ...
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

# CLI 用法
python unity_skills.py --list
python unity_skills.py gameobject_create name=MyCube primitiveType=Cube
```

---

## 📊 Skills 模块汇总 (115+)

| 模块 | Skills 数量 | 核心功能 |
|------|:-----------:|----------|
| **GameObject** | 8 | 创建/删除/查找/变换同步/**批量复制** |
| **Component** | 8 | 添加/移除/属性配置/批量操作 |
| **Scene** | 6 | 场景切换/保存/截图 |
| **Material** | 17 | HDR 发光/材质属性批量修改 |
| **Light** | 7 | 灯光创建/属性配置/**批量开关** |
| **Animator** | 8 | 动画控制器/参数/状态机 |
| **UI** | 10 | Canvas/Button/Text/Slider 创建 |
| **Prefab** | 5 | 预制体创建/实例化/**批量实例化** |
| **Asset** | 8 | 资产导入/搜索/文件夹管理 |
| **Editor** | 12 | 播放模式/选择/撤销重做/**上下文获取** |
| **Console** | 5 | 日志捕获/清理/输出 |
| **Script** | 4 | C# 脚本创建/读取/搜索 |
| **Shader** | 3 | Shader 创建/列表 |
| **Validation** | 7 | 项目验证/清理空文件夹/检测未使用资产 |
| **Texture** | 3 | 纹理类型/尺寸/压缩设置 (v1.2 新增) |
| **Audio** | 3 | 音频加载类型/压缩格式/质量设置 (v1.2 新增) |
| **Model** | 3 | 模型网格压缩/动画类型/材质导入 (v1.2 新增) |

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
