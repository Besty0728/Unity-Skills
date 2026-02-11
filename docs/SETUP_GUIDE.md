# UnitySkills 完整使用指南

REST API 直接控制 Unity Editor，让 AI 生成极简脚本完成场景操作！

---

## 📋 前置要求 (Prerequisites)

- **Unity 版本**: 2021.3 或更高 (推荐 Unity 6)
- **依赖包**:
    - `com.unity.cinemachine`: 3.1.3+ (核心依赖)
    - `com.unity.splines`: 2.8.0+ (v1.4.1 新增硬依赖)
- **注意**: 安装插件时会自动拉取上述依赖，无需手动安装。

---

## 一、安装 Unity 插件

### 方式 A：Git URL（推荐）

通过 Unity Package Manager 直接添加 Git URL：
```
Unity 菜单 → Window → Package Manager → + → Add package from git URL
```

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

### 方式 B：本地安装
将 `SkillsForUnity` 文件夹复制到 Unity 项目的 `Packages/` 目录

---

## 二、启动服务器

1. Unity 菜单：**Window → UnitySkills → Start REST Server**
2. Console 显示：`[UnitySkills] REST Server started at http://localhost:8090/`

---

## 三、验证

### 浏览器
打开 http://localhost:8090/skills 查看所有可用 Skills

### 命令行
```bash
curl http://localhost:8090/skills
```

---

## 四、AI 工具配置

UnitySkills 支持多种 AI 工具，可以通过 Unity 编辑器一键安装。

### 打开配置窗口
Unity 菜单：**Window → UnitySkills**，切换到 **AI Config** 标签页

### 支持的 AI 工具

| AI 工具 | 项目安装路径 | 全局安装路径 |
|---------|------------|------------|
| Claude Code | `.claude/skills/unity-skills/` | `~/.claude/skills/unity-skills/` |
| Antigravity | `.agent/skills/unity-skills/` | `~/.gemini/antigravity/skills/unity-skills/` |
| Gemini CLI | `.gemini/skills/unity-skills/` | `~/.gemini/skills/unity-skills/` |
| Codex | `.codex/skills/unity-skills/` | `~/.codex/skills/unity-skills/` |

### 一键安装
1. 在 UnitySkills 窗口的 AI Config 标签页
2. 选择要安装的 AI 工具
3. 点击 "安装到项目" 或 "全局安装"
4. 安装成功后会显示 "✓ 已安装"

> 安装器落盘文件说明（生成于目标目录）：
> - `SKILL.md`
> - `scripts/unity_skills.py`
> - `scripts/agent_config.json`（包含 Agent 标识）
> - Antigravity 额外生成 `workflows/unity-skills.md`

### Gemini CLI 特别说明
Gemini CLI 的 Skills 功能是实验性的，需要手动启用：
```bash
gemini
# 进入交互模式后输入
/settings
# 搜索 "Skills" 并启用 experimental.skills
```

### OpenAI Codex 特别说明

**推荐使用全局安装**：Codex 不会自动扫描项目级 `.codex/skills/` 目录，需要在 `AGENTS.md` 中明确声明才能识别。

全局安装路径（`~/.codex/skills/`）会被自动识别，安装后重启 Codex 即可：
```bash
# 重启 Codex 以加载新安装的 Skill
codex
```

如果必须使用项目级安装，需要手动在项目根目录的 `AGENTS.md` 中添加：
```markdown
## Available Skills
- unity-skills: Unity Editor automation via REST API
```

---

## 五、调用 Skills

### 基本格式
```bash
POST http://localhost:8090/skill/{skill_name}
Content-Type: application/json

{参数JSON}
```

### 示例

#### 创建物体
```bash
curl -X POST http://localhost:8090/skill/gameobject_create \
  -H "Content-Type: application/json" \
  -d '{"name":"MyCube","primitiveType":"Cube","x":0,"y":1,"z":0}'
```

#### 设置颜色
```bash
curl -X POST http://localhost:8090/skill/material_set_color \
  -d '{"gameObjectName":"MyCube","r":1,"g":0,"b":0}'
```

#### 保存场景
```bash
curl -X POST http://localhost:8090/skill/scene_save \
  -d '{"scenePath":"Assets/Scenes/MyScene.unity"}'
```

---

## 六、Python 客户端

```python
import requests

UNITY_URL = "http://localhost:8090"

def call_skill(name, **kwargs):
    return requests.post(f"{UNITY_URL}/skill/{name}", json=kwargs).json()

# 使用
call_skill("gameobject_create", name="Cube", primitiveType="Cube", x=0, y=1, z=0)
call_skill("material_set_color", gameObjectName="Cube", r=1, g=0, b=0)
call_skill("editor_play")
```

---

## 七、完整 Skills 列表

> ⚠️ **提示**：大部分模块支持 `*_batch` 批量操作，操作多个物体时应优先使用批量 Skills。

### Scene (场景) - 6 skills
| Skill | 描述 | 参数 |
|-------|------|------|
| scene_create | 创建新场景 | scenePath |
| scene_load | 加载场景 | scenePath, additive |
| scene_save | 保存场景 | scenePath |
| scene_get_info | 获取场景信息 | - |
| scene_get_hierarchy | 获取层级树 | maxDepth |
| scene_screenshot | 截图 | filename, width, height |

### GameObject (物体) - 8 skills (含批量)
| Skill | 描述 | 参数 |
|-------|------|------|
| gameobject_create | 创建物体 | name, primitiveType, x, y, z |
| gameobject_delete | 删除物体 | name, instanceId |
| gameobject_find | 查找物体 | name, tag, component, limit |
| gameobject_set_transform | 设置变换 | name, posX, posY, posZ, rotX, rotY, rotZ, scaleX, scaleY, scaleZ |
| gameobject_duplicate | 复制物体 | name, instanceId |
| gameobject_duplicate_batch | **批量复制** | items (JSON数组) |
| gameobject_set_parent | 设置父级 | childName, parentName |

### Component (组件) - 8 skills (含批量)
| Skill | 描述 | 参数 |
|-------|------|------|
| component_add | 添加组件 | gameObjectName, componentType |
| component_add_batch | **批量添加** | items (JSON数组) |
| component_remove | 移除组件 | gameObjectName, componentType |
| component_remove_batch | **批量移除** | items (JSON数组) |
| component_list | 列出组件 | gameObjectName |
| component_set_property | 设置属性 | gameObjectName, componentType, propertyName, value |
| component_set_property_batch | **批量设置属性** | items (JSON数组) |
| component_get_properties | 获取属性 | gameObjectName, componentType |

### Material (材质) - 17 skills
| Skill | 描述 | 参数 |
|-------|------|------|
| material_create | 创建材质 | name, shaderName, savePath |
| material_set_color | 设置颜色 | gameObjectName, r, g, b, a, propertyName, intensity |
| material_set_emission | 设置发光 | gameObjectName, r, g, b, intensity |
| material_set_texture | 设置贴图 | gameObjectName, texturePath, propertyName |
| material_assign | 分配材质 | gameObjectName, materialPath |
| material_set_float | 设置浮点值 | gameObjectName, propertyName, value |

### Light (灯光) - 7 skills (含批量)
| Skill | 描述 | 参数 |
|-------|------|------|
| light_create | 创建灯光 | name, lightType, x, y, z, r, g, b, intensity |
| light_set_properties | 设置属性 | name, instanceId, r, g, b, intensity, range |
| light_set_properties_batch | **批量设置属性** | items (JSON数组) |
| light_set_enabled | 开关灯光 | name, instanceId, enabled |
| light_set_enabled_batch | **批量开关** | items (JSON数组) |
| light_get_info | 获取灯光信息 | name, instanceId |
| light_find_all | 查找所有灯光 | lightType, limit |

### Editor (编辑器) - 12 skills
| Skill | 描述 | 参数 |
|-------|------|------|
| editor_play | 进入播放模式 | - |
| editor_stop | 停止播放模式 | - |
| editor_pause | 暂停/继续 | - |
| editor_select | 选中物体 | gameObjectName, instanceId |
| editor_get_selection | 获取选中 | - |
| **editor_get_context** | **获取完整上下文** | includeComponents, includeChildren |
| editor_undo | 撤销 | - |
| editor_redo | 重做 | - |
| editor_get_state | 获取编辑器状态 | - |
| editor_execute_menu | 执行菜单项 | menuPath |
| editor_get_tags | 获取所有标签 | - |
| editor_get_layers | 获取所有图层 | - |

### Importer (导入设置) - 9 skills [v1.2 新增]
| Skill | 描述 | 参数 |
|-------|------|------|
| texture_get_settings | 获取纹理设置 | assetPath |
| texture_set_settings | 设置纹理导入 | assetPath, textureType, maxSize, filterMode... |
| texture_set_settings_batch | **批量设置纹理** | items (JSON数组) |
| audio_get_settings | 获取音频设置 | assetPath |
| audio_set_settings | 设置音频导入 | assetPath, loadType, compressionFormat, quality... |
| audio_set_settings_batch | **批量设置音频** | items (JSON数组) |
| model_get_settings | 获取模型设置 | assetPath |
| model_set_settings | 设置模型导入 | assetPath, meshCompression, animationType... |
| model_set_settings_batch | **批量设置模型** | items (JSON数组) |

### Asset (资产) - 8 skills
| Skill | 描述 | 参数 |
|-------|------|------|
| asset_import | 导入资产 | sourcePath, destinationPath |
| asset_delete | 删除资产 | assetPath |
| asset_move | 移动/重命名 | sourcePath, destinationPath |
| asset_duplicate | 复制资产 | assetPath |
| asset_find | 搜索资产 | searchFilter, limit |
| asset_create_folder | 创建文件夹 | folderPath |
| asset_refresh | 刷新资产库 | - |
| asset_get_info | 获取资产信息 | assetPath |

### Prefab (预制体) - 5 skills
| Skill | 描述 | 参数 |
|-------|------|------|
| prefab_create | 创建预制体 | gameObjectName, savePath |
| prefab_instantiate | 实例化预制体 | prefabPath, x, y, z, name |
| prefab_instantiate_batch | **批量实例化** | items (JSON数组) |
| prefab_apply | 应用修改 | gameObjectName |
| prefab_unpack | 解包预制体 | gameObjectName, completely |

### Console (控制台) - 5 skills
| Skill | 描述 | 参数 |
|-------|------|------|
| console_start_capture | 开始捕获日志 | - |
| console_stop_capture | 停止捕获 | - |
| console_get_logs | 获取日志 | filter, limit |
| console_clear | 清空控制台 | - |
| console_log | 输出日志 | message, type |

---

## 八、添加自定义 Skill

在 `SkillsForUnity/Editor/Skills/` 目录下创建 C# 文件，使用 `[UnitySkill]` 属性标记静态方法：

```csharp
using UnityEngine;
using UnitySkills;

namespace UnitySkills
{
    public static class MyCustomSkills
    {
        [UnitySkill("my_custom_skill", "我的自定义技能描述")]
        public static object MyCustomSkill(string param1, float param2 = 0)
        {
            // 你的逻辑
            return new { success = true, result = "..." };
        }
    }
}
```

Unity 重新编译后自动发现新 Skill（无需重启服务器）。

---

## 九、AI 集成

将 `unity-skills/SKILL.md` 添加为 AI Skill，即可通过生成 Python 脚本控制 Unity。

### AI 对话示例
```
用户: 在 Unity 中创建一个红色立方体
AI: 
import requests
requests.post("http://localhost:8090/skill/gameobject_create", 
    json={"name":"RedCube","primitiveType":"Cube","x":0,"y":1,"z":0})
requests.post("http://localhost:8090/skill/material_set_color",
    json={"gameObjectName":"RedCube","r":1,"g":0,"b":0})
```
