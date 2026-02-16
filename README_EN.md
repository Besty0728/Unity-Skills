# 🎮 UnitySkills


<p align="center">
  <img src="https://img.shields.io/badge/Unity-2021.3%2B-black?style=for-the-badge&logo=unity" alt="Unity">
  <img src="https://img.shields.io/badge/Skills-431-green?style=for-the-badge" alt="Skills">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-orange?style=for-the-badge" alt="License"></a>
</p>

<p align="center">
  <b>REST API-based AI-driven Unity Editor Automation Engine</b><br>
  <i>Let AI control Unity scenes directly through Skills</i>
</p>

<p align="center">
  🎉 We are now indexed by <b>DeepWiki</b>!<br>
  Got questions? Check out the AI-generated docs → <a href="https://deepwiki.com/Besty0728/Unity-Skills"><img src="https://deepwiki.com/badge.svg" alt="Ask DeepWiki"></a>
</p>

## 🤝 Acknowledgments
This project is a deep refactoring and feature extension based on the excellent concept of [unity-mcp](https://github.com/CoplayDev/unity-mcp).

---

## 🚀 Core Features

- ⚡ **Ultimate Efficiency**: Supports **Result Truncation** and **SKILL.md** optimization to maximize token savings.
- 🛠️ **Comprehensive Toolkit**: Built-in 431 Skills with **Batch** operations, significantly reducing HTTP overhead and improving execution efficiency.
- 🛡️ **Safety First**: Supports **Transactional** (atomic operations) with automatic rollback on failure, leaving no residue in scenes.
- 🌍 **Multi-Instance Support**: Automatic port discovery and global registry, enabling simultaneous control of multiple Unity projects.
- 🤖 **Deep Integration**: Exclusive support for **Antigravity Slash Commands**, unlocking the `/unity-skills` interactive experience.
- 🔌 **Full Environment Compatibility**: Perfect support for Claude Code, Antigravity, Gemini CLI, and other mainstream AI terminals.
- 🎥 **Cinemachine 2.x/3.x Dual Version Support**: Auto-detects Unity version and installs the corresponding Cinemachine, supporting **MixingCamera**, **ClearShot**, **TargetGroup**, **Spline**, and other advanced camera controls.
- 🔗 **Stable Long-Running Tasks**: User-configurable request timeout (default 60 minutes), automatic port recovery after Domain Reload, Python client auto-syncs timeout from server, fully resolving disconnection issues during long tasks.

---

## 🏗️ Supported IDEs / Terminals

This project has been deeply optimized for the following environments to ensure a continuous and stable development experience:

| AI Terminal | Support Status | Special Features |
| :--- | :---: | :--- |
| **Antigravity** | ✅ Fully Supported | Supports `/unity-skills` slash commands with native workflow integration. |
| **Claude Code** | ✅ Fully Supported | Intelligent Skill intent recognition, supports complex multi-step automation. |
| **Gemini CLI** | ✅ Fully Supported | Experimental support, adapted to the latest `experimental.skills` specification. |
| **Codex** | ✅ Fully Supported | Supports `$skill` explicit invocation and implicit intent recognition. |

---

## 🏁 Quick Start

### 1. Install Unity Plugin
Add via Unity Package Manager using Git URL:

**Stable Version (main)**:
```
https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity
```

**Beta Version (beta)**:
```
https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity#beta
```

**Specific Version** (e.g., v1.4.0):
```
https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity#v1.4.0
```

> 📦 All version packages are available on the [Releases](https://github.com/Besty0728/Unity-Skills/releases) page

### 2. Start Server
In Unity, click menu: `Window > UnitySkills > Start Server`

### 3. One-Click AI Skills Configuration
1. Open `Window > UnitySkills > Skill Installer`.
2. Select the corresponding terminal icon (Claude / Antigravity / Gemini / Codex).
3. Click **"Install"** to complete the environment configuration without manual code copying.

> Installer output files (generated in target directory):
> - `SKILL.md`
> - `scripts/unity_skills.py`
> - `scripts/agent_config.json` (contains Agent identifier)
> - Antigravity additionally generates `workflows/unity-skills.md`

> **Codex Note**: **Global installation** is recommended. Project-level installation requires declaration in `AGENTS.md` to be recognized; after global installation, restart Codex to use.

📘 For complete installation and usage instructions, see: [docs/SETUP_GUIDE.md](docs/SETUP_GUIDE.md)

### 4. Manual Skills Installation (Optional)
If one-click installation is not supported or preferred, follow this **standard procedure** for manual deployment (applicable to all tools supporting Skills):

#### ✅ Standard Installation Method A
1. **Custom Installation**: In the installation interface, select the "Custom Path" option to install Skills to any directory you specify (e.g., `Assets/MyTools/AI`) for easier project management.

#### ✅ Standard Installation Method B
1. **Locate Skills Source Directory**: The `unity-skills/` directory in this repository is the distributable Skills template (root directory contains `SKILL.md`).
2. **Find the Tool's Skills Root Directory**: Different tools have different paths; refer to the tool's documentation first.
3. **Copy Completely**: Copy the entire `unity-skills/` directory to the tool's Skills root directory.
4. **Create agent_config.json**: Create an `agent_config.json` file in the `unity-skills/scripts/` directory:
   ```json
   {"agentId": "your-agent-name", "installedAt": "2026-02-11T00:00:00Z"}
   ```
   Replace `your-agent-name` with the name of your AI tool (e.g., `claude-code`, `antigravity`, `gemini-cli`, `codex`).
5. **Directory Structure Requirements**: After copying, maintain the structure as follows (example):
   - `unity-skills/SKILL.md`
   - `unity-skills/skills/`
   - `unity-skills/scripts/unity_skills.py`
   - `unity-skills/scripts/agent_config.json`
6. **Restart the Tool**: Let the tool reload the Skills list.
7. **Verify Loading**: Trigger the Skills list/command in the tool (or execute a simple skill call) to confirm availability.

#### 🔎 Common Tool Directory Reference
The following are verified default directories (if the tool has a custom path configured, use that instead):

- Claude Code: `~/.claude/skills/`
- Antigravity: `~/.agent/skills/`
- Gemini CLI: `~/.gemini/skills/`
- OpenAI Codex: `~/.codex/skills/`

#### 🧩 Other Tools Supporting Skills
If you're using other tools that support Skills, install according to the Skills root directory specified in that tool's documentation. As long as the **standard installation specification** is met (root directory contains `SKILL.md` and maintains `skills/` and `scripts/` structure), it will be correctly recognized.

---

## 📦 Skills Category Overview (431)

| Category | Count | Core Functions |
| :--- | :---: | :--- |
| **Cinemachine** | 23 | 2.x/3.x dual version auto-install/MixingCamera/ClearShot/TargetGroup/Spline |
| **Workflow** | 22 | Persistent history/Task snapshots/Session-level undo/Rollback/Bookmarks |
| **Material** | 21 | Batch material property modification/HDR/PBR/Emission/Keywords/Render queue |
| **GameObject** | 18 | Create/Find/Transform sync/Batch operations/Hierarchy management/Rename/Duplicate |
| **Scene** | 18 | Multi-scene load/Unload/Activate/Screenshot/Context/Dependency analysis/Report export |
| **UI System** | 16 | Canvas/Button/Text/Slider/Toggle/Anchors/Layout/Alignment/Distribution |
| **Asset** | 15 | Asset import/Delete/Move/Copy/Search/Folders/Batch operations/Refresh |
| **Editor** | 12 | Play mode/Selection/Undo-Redo/Context retrieval/Menu execution |
| **Timeline** | 12 | Track create/Delete/Clip management/Playback control/Binding/Duration |
| **Physics** | 12 | Raycast/SphereCast/BoxCast/Physics materials/Layer collision matrix |
| **Audio** | 12 | Audio import settings/AudioSource/AudioClip/AudioMixer/Batch |
| **Texture** | 12 | Texture import settings/Platform settings/Sprite/Type/Size search/Batch |
| **Model** | 12 | Model import settings/Mesh info/Material mapping/Animation/Skeleton/Batch |
| **Script** | 12 | C# script create/Read/Replace/List/Info/Rename/Move/Analyze |
| **Package** | 11 | Package management/Install/Remove/Search/Versions/Dependencies/Cinemachine/Splines |
| **AssetImport** | 11 | Texture/Model/Audio/Sprite import settings/Label management/Reimport |
| **Project** | 11 | Render pipeline/Build settings/Package management/Layer/Tag/PlayerSettings/Quality |
| **Shader** | 11 | Shader create/URP templates/Compile check/Keywords/Variant analysis/Global keywords |
| **Camera** | 11 | Scene View control/Game Camera create/Properties/Screenshot/Orthographic toggle/List |
| **Terrain** | 10 | Terrain create/Heightmap/Perlin noise/Smooth/Flatten/Texture painting |
| **NavMesh** | 10 | Bake/Path calculation/Agent/Obstacle/Sampling/Area cost |
| **Cleaner** | 10 | Unused assets/Duplicate files/Empty folders/Missing script fix/Dependency tree |
| **ScriptableObject** | 10 | Create/Read-Write/Batch set/Delete/Find/JSON import-export |
| **Console** | 10 | Log capture/Clear/Export/Statistics/Pause control/Collapse/Clear on play |
| **Debug** | 10 | Error logs/Compile check/Stack trace/Assemblies/Define symbols/Memory info |
| **Event** | 10 | UnityEvent listener management/Batch add/Copy/State control/List |
| **Smart** | 10 | Scene SQL query/Spatial query/Auto layout/Snap to ground/Grid snap/Randomize/Replace |
| **Test** | 10 | Test run/Run by name/Categories/Template create/Summary statistics |
| **Prefab** | 10 | Create/Instantiate/Override apply & revert/Batch instantiate/Variants/Find instances |
| **Component** | 10 | Add/Remove/Property config/Batch operations/Copy/Enable-Disable |
| **Optimization** | 10 | Texture compression/Mesh compression/Audio compression/Scene analysis/Static flags/LOD/Duplicate materials/Overdraw |
| **Profiler** | 10 | FPS/Memory/Texture/Mesh/Material/Audio/Rendering stats/Object count/AssetBundle |
| **Light** | 10 | Light create/Type config/Intensity-Color/Batch toggle/Probe groups/Reflection probes/Lightmaps |
| **Validation** | 10 | Project validation/Empty folder cleanup/Reference detection/Mesh collider/Shader errors |
| **Animator** | 10 | Animation controller/Parameters/State machine/Transitions/Assign/Play |
| **Perception** | 9 | Scene summary/Hierarchy tree/Script analysis/Spatial query/Material overview/Scene snapshot/Dependency analysis/Report export/Performance hints |
| **Sample** | 8 | Basic examples: Create/Delete/Transform/Scene info |

> ⚠️ Most modules support `*_batch` batch operations. When operating on multiple objects, prioritize batch Skills for better performance.

---

## 📂 Project Structure

```bash
.
├── SkillsForUnity/                 # Unity Editor Plugin (UPM Package)
│   ├── package.json                # com.besty.unity-skills
│   └── Editor/Skills/              # Core Skill Logic (37 *Skills.cs files, 431 Skills)
│       ├── SkillsHttpServer.cs     # HTTP Server Core (Producer-Consumer)
│       ├── SkillRouter.cs          # Request Routing & Reflection-based Skill Discovery
│       ├── WorkflowManager.cs      # Persistent Workflow (Task/Session/Snapshot)
│       ├── RegistryService.cs      # Global Registry (Multi-instance Discovery)
│       ├── GameObjectFinder.cs     # Unified GO Finder (name/instanceId/path)
│       ├── BatchExecutor.cs        # Generic Batch Processing Framework
│       ├── GameObjectSkills.cs     # GameObject Operations (18 skills)
│       ├── MaterialSkills.cs       # Material Operations (21 skills)
│       ├── CinemachineSkills.cs    # Cinemachine 2.x/3.x (23 skills)
│       ├── WorkflowSkills.cs       # Workflow Undo/Rollback (22 skills)
│       ├── PerceptionSkills.cs     # Scene Understanding (9 skills)
│       └── ...                     # 431 Skills source code
├── unity-skills/                   # Cross-platform AI Skill Template (Distributed to AI Tools)
│   ├── SKILL.md                    # Main Skill Definitions (AI-readable)
│   ├── scripts/
│   │   └── unity_skills.py         # Python Client Library
│   ├── skills/                     # Modular Skill Documentation
│   └── references/                 # Unity Development References
├── docs/
│   └── SETUP_GUIDE.md              # Complete Setup & Usage Guide
├── CHANGELOG.md                    # Version Update Log
└── LICENSE                         # MIT License
```

---

## ⭐ Star History

[![Star History Chart](https://api.star-history.com/svg?repos=Besty0728/Unity-Skills&type=Date)](https://star-history.com/#Besty0728/Unity-Skills&Date)

---

## 📄 License
This project is licensed under the [MIT License](LICENSE).
