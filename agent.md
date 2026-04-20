# UnitySkills — AI Agent 项目速览

> 通过 REST API 让 AI 直接控制 Unity 编辑器。576 个 REST Skills + 14 个 Advisory 模块。

| 项目 | 值 |
|------|----|
| 版本 | 1.7.3 |
| 技术栈 | C# (Unity Editor Plugin) + Python (Client) |
| Unity | 2022.3+（已验证 Unity 6 / 6000.x） |
| 协议 | MIT |
| 包名 | com.besty.unity-skills |

---

## 架构

```
AI Agent ──HTTP──▶ unity_skills.py ──POST localhost:8090-8100──▶ Unity Editor Plugin
                                                                    │
                                              SkillsHttpServer (Producer-Consumer)
                                                        │
                                              SkillRouter (反射发现 [UnitySkill])
                                                        │
                                              42 个 *Skills.cs (576 Skills)
                                                        │
                                         WorkflowManager (持久化撤销/回滚)
                                         RegistryService (多实例发现)
```

**线程模型**：HTTP 线程仅入队，Unity 主线程通过 `EditorApplication.update` 消费。零 Unity API 跨线程调用。

---

## 操作模式

| 模式 | 默认 | Skills | 场景 |
|------|:----:|:------:|------|
| 半自动 | ✅ | ~80 | AI 写 C# 代码，少量 Skills 辅助 |
| 全自动 | - | 576 | AI 直接操作编辑器 |

切换：用户说 "全自动模式"/"full auto" 或 "半自动模式"/"semi-auto"。详见 `SkillsForUnity/unity-skills~/SKILL.md`。

---

## 项目结构

```
Unity-Skills/
├── SkillsForUnity/                     # UPM Package
│   ├── package.json
│   ├── Editor/Skills/                  # 61 个 C# 文件
│   │   ├── SkillsHttpServer.cs         # HTTP 服务器 (Producer-Consumer)
│   │   ├── SkillRouter.cs              # 反射路由 + 参数绑定
│   │   ├── UnitySkillAttribute.cs      # [UnitySkill] 特性 (含 Category/Operation/Tags 元数据)
│   │   ├── WorkflowManager.cs          # 持久化工作流 (Task/Session/Snapshot)
│   │   ├── RegistryService.cs          # 全局注册表 (~/.unity_skills/registry.json)
│   │   ├── GameObjectFinder.cs         # 统一查找器 (name/instanceId/path)
│   │   ├── BatchExecutor.cs            # 批量操作框架
│   │   ├── SkillsLogger.cs             # 日志 + 版本常量源 (Version = "x.x.x")
│   │   ├── UnitySkillsWindow.cs        # 编辑器窗口 UI
│   │   ├── SkillInstaller.cs           # AI 工具一键安装
│   │   └── *Skills.cs × 42             # 功能模块 (共 576 Skills)
│   └── unity-skills~/                  # AI Skill 模板 (波浪线隐藏, 随包分发)
│       ├── SKILL.md                    # 主 Skill 文档 (AI 读取入口)
│       ├── scripts/unity_skills.py     # Python 客户端
│       ├── skills/                     # 53 个模块文档 (40 functional + 13 advisory)
│       └── references/                 # Unity 开发参考
├── .claude/commands/                   # 自定义命令
│   ├── updateversion.md                # /updateversion — 版本号更新 + CHANGELOG 生成
│   └── release.md                      # /release — beta→main 同步 + Release Note
├── docs/SETUP_GUIDE.md
├── CHANGELOG.md
├── README.md / README_CN.md
└── agent.md                            # 本文件
```

---

## 核心设计模式

**Skill 定义**：静态方法 + `[UnitySkill]` 特性，SkillRouter 启动时反射发现，参数通过 JSON 自动绑定。

```csharp
[UnitySkill("skill_name", "描述",
    Category = SkillCategory.GameObject, Operation = SkillOperation.Create,
    Tags = new[] { "tag1" }, Outputs = new[] { "name", "instanceId" },
    TracksWorkflow = true)]
public static object SkillName(string name, float x = 0) { ... }
```

**事务性**：每个 Skill 自动包裹 Undo Group，失败自动回滚。`TracksWorkflow=true` 的 Skill 自动创建可回滚的工作流快照。

**批处理**：`*_batch` 后缀 API 通过 `BatchExecutor` 统一处理，一次请求操作 1000+ 物体。

**多实例**：Server 自动扫描 8090-8100 端口，注册到 `~/.unity_skills/registry.json`。

**Domain Reload 韧性**：EditorPrefs 持久化端口和重启意图，连续失败 5 次上限，Watchdog 自动重启死亡线程。

---

## Skills 模块 (42 个功能模块, 576 Skills)

| 模块 | 数量 | 模块 | 数量 | 模块 | 数量 |
|------|:----:|------|:----:|------|:----:|
| UI | 26 | UIToolkit | 25 | Workflow | 23 |
| Cinemachine | 23 | ProBuilder* | 22 | XR* | 22 |
| Material | 21 | Batch | 21 | GameObject | 18 |
| Perception | 18 | Editor | 12 | Script | 12 |
| Timeline | 12 | Physics | 12 | Asset | 11 |
| AssetImport | 11 | Camera | 11 | Package | 11 |
| Prefab | 11 | Project | 11 | Shader | 11 |
| Scene | 10 | Audio | 10 | Texture | 10 |
| Model | 10 | Component | 10 | Terrain | 10 |
| NavMesh | 10 | Cleaner | 10 | ScriptableObject | 10 |
| Console | 10 | Debug | 10 | Event | 10 |
| Smart | 10 | Test | 11 | Optimization | 10 |
| Profiler | 10 | Light | 10 | Validation | 10 |
| Animator | 10 | Sample | 8 | | |

*ProBuilder 需 `com.unity.probuilder`，XR 需 `com.unity.xr.interaction.toolkit`

> 大部分模块支持 `*_batch` 批量操作，操作 2+ 物体时应优先使用。

**Advisory 模块 (13)**：architecture, patterns, performance, asmdef, async, inspector, blueprints, adr, project-scout, scene-contracts, script-roles, scriptdesign, testability — 纯架构/设计指导，无 REST Skills。

---

## 调用方式

```python
# Python 客户端
import unity_skills
unity_skills.call_skill("gameobject_create", name="Cube", primitiveType="Cube", parentName="Parent")
unity_skills.health()
unity_skills.get_skills(category="GameObject", operation="Create")

# Workflow 上下文 (批量回滚)
with unity_skills.workflow_context('Build Scene'):
    unity_skills.call_skill('gameobject_create', name='Player')
    unity_skills.call_skill('component_add', name='Player', componentType='Rigidbody')
```

```bash
# HTTP 直接调用
curl http://localhost:8090/health
curl http://localhost:8090/skills
curl -X POST http://localhost:8090/skill/gameobject_create \
  -H "Content-Type: application/json" -d '{"name":"Cube","primitiveType":"Cube"}'
```

**响应格式**：`{"status":"success", "skill":"...", "result":{...}}`

---

## 开发规范

**Git 分支**：开发在 `beta`，通过 `/release` 同步到 `main`（线性历史，无 merge commit）。

**版本更新**：使用 `/updateversion <版本号>`，自动更新 10 处位点 + 生成 CHANGELOG。

**扩展 Skill**：在 `Editor/Skills/` 添加 `[UnitySkill]` 静态方法，重启服务器自动发现。`SkillsHttpServer.cs`/`SkillRouter.cs` 中版本引用使用 `SkillsLogger.Version`，禁止硬编码。

**编译期不可达属于预期**：脚本保存、包安装等触发 Domain Reload 时 REST 服务短暂不可达，客户端应等待重试。504/503 响应包含诊断信息。
