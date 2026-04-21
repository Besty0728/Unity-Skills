# 🎮 UnitySkills

<p align="center">
  <img src="docs/Unity-Skills-H.png" alt="Unity-Skills" width="800">
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Unity-2022.3%2B-black?style=for-the-badge&logo=unity" alt="Unity">
  <img src="https://img.shields.io/badge/Skills-588-green?style=for-the-badge" alt="Skills">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-orange?style=for-the-badge" alt="License"></a>
  <a href="README_CN.md"><img src="https://img.shields.io/badge/README-中文-blue?style=for-the-badge" alt="中文"></a>
</p>

<p align="center">
  <b>REST API-based AI-driven Unity Editor Automation Engine</b><br>
  <i>Let AI control Unity scenes directly through Skills</i>
</p>

<p align="center">
  🎉 We are now indexed by <b>DeepWiki</b>!<br>
  Got questions? Check out the AI-generated docs → <a href="https://deepwiki.com/Besty0728/Unity-Skills"><img src="https://deepwiki.com/badge.svg" alt="Ask DeepWiki"></a>
</p>

> The current official maintenance baseline is **Unity 2022.3+**. Some Unity 2021 compatibility logic may still remain in the codebase, but future feature work, regression testing, and adaptation will focus on **2022.3+ / Unity 6**.

## 🤝 Acknowledgments
This project is a deep refactoring and feature extension based on the excellent concept of [unity-mcp](https://github.com/CoplayDev/unity-mcp).

---

## 🚀 Core Features

- 🛠️ **588 REST Skills Comprehensive Toolkit**: Includes 14 advisory design modules with Batch operations for multi-object control.
- 🎛️ **Dual-Mode Flexibility**: Switch between Semi-Auto (code-first) and Full-Auto (direct manipulation) for different workflows.
- 🤖 **4 Major IDEs Native Support**: Claude Code / Antigravity / Gemini CLI / Codex — one-click install and use.
- 🛡️ **Transactional Atomicity**: Failed operations auto-rollback, leaving scenes clean and safe.
- 🌍 **Multi-Instance Simultaneous Control**: Automatic port discovery and global registry for controlling multiple Unity projects at once.
- 🔗 **Ultra-Stable Long Connections**: Configurable request timeout (default 15 minutes), automatic recovery after Domain Reload, with retry hints during script compilation/asset updates.
- 🛡️ **Anti-Hallucination Guardrails**: Each Skill module includes DO NOT lists and routing rules to prevent calls to nonexistent commands or parameter errors.

---

## 🎛️ Operating Modes

| Mode | Default | Available Skills | Use Case |
|:-----|:-------:|:----------------:|:---------|
| **Semi-Auto** | ✅ | ~80 | AI writes C# code + light Skills assist (script, perception, scene, editor, asset, workflow, debug) |
| **Full-Auto** | — | All 588 | AI directly manipulates Unity (create objects, configure materials/lights/UI, build scenes) |

**How to switch**:
- → Full-Auto: `"full auto"` / `"full-auto mode"` / `"build the scene for me"` / `"directly manipulate Unity"`
- → Semi-Auto: `"semi-auto"` / `"code-first"` — each new session defaults to Semi-Auto

> 14 advisory design modules (architecture, performance, design patterns, testability, etc.) are available in both modes and loaded on demand.

---

## 🏗️ Quick Install Supported IDE/Terminals

This project has been deeply optimized for the following environments to ensure a continuous and stable development experience (tools not listed below are not necessarily unsupported — they just lack a quick installer; use ***Custom Installation*** to the corresponding directory):

| AI Terminal | Support Status | Special Features |
| :--- | :---: | :--- |
| **Antigravity** | ✅ Supported | Supports `/unity-skills` slash commands with native workflow integration. |
| **Claude Code** | ✅ Supported | Intelligent Skill intent recognition, supports complex multi-step automation. |
| **Gemini CLI** | ✅ Supported | Experimental support, adapted to the latest `experimental.skills` specification. |
| **Codex** | ✅ Supported | Supports `$skill` explicit invocation and implicit intent recognition. |

---

## 🏁 Quick Start

> **Overview**: Install Unity Plugin → Start UnitySkills Server → AI Uses Skills

<p align="center">
  <img src="docs/installation-demo.gif" alt="一键安装演示" width="800">
</p>

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

**Specific Version** (e.g., v1.6.0):
```
https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity#v1.6.0
```

> 📦 All version packages are available on the [Releases](https://github.com/Besty0728/Unity-Skills/releases) page

### 2. Start Server
In Unity, click menu: `Window > UnitySkills > Start Server`

> ⏳ `script_*`, `debug_force_recompile`, `debug_set_defines`, some asset reimports, and package changes may trigger compilation or Domain Reload. Temporary REST unavailability during that window is expected; wait a moment and retry.

### 3. One-Click AI Skills Configuration
1. Open `Window > UnitySkills > Skill Installer`.
2. Select the corresponding terminal icon (Claude / Antigravity / Gemini / Codex).
3. Click **"Install"** to complete the environment configuration without manual code copying.

> The installer copies the `unity-skills~/` template directory from the package to the target location.
>
> Installer output files (generated in target directory):
> - `SKILL.md`
> - `skills/`
> - `references/`
> - `scripts/unity_skills.py`
> - `scripts/agent_config.json` (contains Agent identifier)
> - Antigravity additionally generates `workflows/unity-skills.md`

> **Codex Note**: **Global installation** is recommended. Project-level installation requires declaration in `AGENTS.md` to be recognized; after global installation, restart Codex to use.

📘 For complete installation and usage instructions, see: [Setup Guide](docs/SETUP_GUIDE.md) | [安装指南](docs/SETUP_GUIDE_CN.md)

<details>
<summary><h3>4. Manual Skills Installation (Optional)</h3></summary>

If one-click installation is not supported or preferred, follow this **standard procedure** for manual deployment (applicable to all tools supporting Skills):

#### ✅ Standard Installation Method A
1. **Custom Installation**: In the installation interface, select the "Custom Path" option to install Skills to any directory you specify (e.g., `Assets/MyTools/AI`) for easier project management.

#### ✅ Standard Installation Method B
1. **Locate Skills Source Directory**: The `SkillsForUnity/unity-skills~/` directory in the UPM package is the distributable Skills template (root directory contains `SKILL.md`).
2. **Find the Tool's Skills Root Directory**: Different tools have different paths; refer to the tool's documentation first.
3. **Copy Completely**: Copy the entire contents of `unity-skills~/` to the tool's Skills root directory (rename to `unity-skills/`).
4. **Create agent_config.json**: Create an `agent_config.json` file in the `unity-skills/scripts/` directory:
   ```json
   {"agentId": "your-agent-name", "installedAt": "2026-02-11T00:00:00Z"}
   ```
   Replace `your-agent-name` with the name of your AI tool (e.g., `claude-code`, `antigravity`, `gemini-cli`, `codex`).
5. **Directory Structure Requirements**: After copying, maintain the structure as follows (example):
   - `unity-skills/SKILL.md`
   - `unity-skills/skills/`
   - `unity-skills/references/`
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
If you're using other tools that support Skills, install according to the Skills root directory specified in that tool's documentation. As long as the **standard installation specification** is met (root directory contains `SKILL.md` and maintains `skills/`, `references/`, and `scripts/` structure), it will be correctly recognized.

</details>

---

<details>
<summary><h2>📦 Skills Category Overview (588)</h2></summary>

| Category | Count | Core Functions |
| :--- | :---: | :--- |
| **Cinemachine** | 23 | 2.x/3.x dual version auto-install/MixingCamera/ClearShot/TargetGroup/Spline |
| **Workflow** | 23 | Persistent history/Task snapshots/Session-level undo/Rollback/Bookmarks |
| **Batch** | 21 | Unified batch query/preview/execute/report pipeline with async jobs and retry support |
| **Material** | 21 | Batch material property modification/HDR/PBR/Emission/Keywords/Render queue |
| **GameObject** | 18 | Create/Find/Transform sync/Batch operations/Hierarchy management/Rename/Duplicate |
| **Scene** | 10 | Multi-scene load/Unload/Activate/Screenshot/Context/Dependency analysis/Report export |
| **UI** | 26 | Canvas/Button/Text/InputField/Dropdown/ScrollView/Layout/Alignment/Image and selectable utilities |
| **UI Toolkit** | 25 | UXML/USS file management/UIDocument/PanelSettings full property read-write/Template generation/Structure inspection/Batch create |
| **Asset** | 11 | Asset import/Delete/Move/Copy/Search/Folders/Batch operations/Refresh |
| **Editor** | 12 | Play mode/Selection/Undo-Redo/Context retrieval/Menu execution |
| **Timeline** | 12 | Track create/Delete/Clip management/Playback control/Binding/Duration |
| **Physics** | 12 | Raycast/SphereCast/BoxCast/Physics materials/Layer collision matrix |
| **Audio** | 10 | Audio import settings/AudioSource/AudioClip/AudioMixer/Batch |
| **Texture** | 10 | Texture import settings/Platform settings/Sprite/Type/Size search/Batch |
| **Model** | 10 | Model import settings/Mesh info/Material mapping/Animation/Skeleton/Batch |
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
| **Test** | 11 | Test run/Run by name/Categories/Template create/Summary statistics |
| **Prefab** | 11 | Create/Instantiate/Override apply & revert/Batch instantiate/Variants/Find instances/Asset property editing |
| **Component** | 10 | Add/Remove/Property config/Batch operations/Copy/Enable-Disable |
| **Optimization** | 10 | Texture compression/Mesh compression/Audio compression/Scene analysis/Static flags/LOD/Duplicate materials/Overdraw |
| **Profiler** | 10 | FPS/Memory/Texture/Mesh/Material/Audio/Rendering stats/Object count/AssetBundle |
| **Light** | 10 | Light create/Type config/Intensity-Color/Batch toggle/Probe groups/Reflection probes/Lightmaps |
| **Validation** | 10 | Project validation/Empty folder cleanup/Reference detection/Mesh collider/Shader errors |
| **Animator** | 10 | Animation controller/Parameters/State machine/Transitions/Assign/Play |
| **Perception** | 18 | Scene summary/health checks/stack detection/context export/dependency analysis/hotspots/diff/tag-layer stats/performance hints |
| **ProBuilder** | 22 | ProBuilder shape creation/face-edge operations/UV tools/pivot edits/batch creation/mesh combination |
| **XR** | 22 | XR rig setup/interactors/interactables/teleportation/continuous move/UI/haptics/interaction layers |
| **Sample** | 8 | Basic examples: Create/Delete/Transform/Scene info |

> ⚠️ Most modules support `*_batch` batch operations. When operating on multiple objects, prioritize batch Skills for better performance.
>
> 🧠 `unity-skills/skills/` also includes **14 advisory design modules** for architecture, script design, performance, maintainability, and Inspector guidance.

</details>

---

## 📂 Project Structure

```bash
.
├── SkillsForUnity/                 # Unity Editor Plugin (UPM Package)
│   ├── package.json                # com.besty.unity-skills
│   ├── unity-skills~/              # Cross-platform AI Skill Template (tilde-hidden, bundled with package)
│   │   ├── SKILL.md                # Main Skill Definitions (AI-readable)
│   │   ├── scripts/
│   │   │   └── unity_skills.py     # Python Client Library
│   │   ├── skills/                 # Modular Skill Documentation + 14 advisory modules
│   │   └── references/             # Unity Development References
│   └── Editor/Skills/              # Core Skill Logic (42 *Skills.cs files, 588 Skills)
│       ├── SkillsHttpServer.cs     # HTTP Server Core (Producer-Consumer)
│       ├── SkillRouter.cs          # Request Routing & Reflection-based Skill Discovery
│       ├── WorkflowManager.cs      # Persistent Workflow (Task/Session/Snapshot)
│       ├── RegistryService.cs      # Global Registry (Multi-instance Discovery)
│       ├── GameObjectFinder.cs     # Unified GO Finder (name/instanceId/path)
│       ├── BatchExecutor.cs        # Generic Batch Processing Framework
│       ├── GameObjectSkills.cs     # GameObject Operations (18 skills)
│       ├── MaterialSkills.cs       # Material Operations (21 skills)
│       ├── CinemachineSkills.cs    # Cinemachine 2.x/3.x (23 skills)
│       ├── WorkflowSkills.cs       # Workflow Undo/Rollback (23 skills)
│       ├── PerceptionSkills.cs     # Scene Understanding (18 skills)
│       └── ...                     # 588 Skills source code
├── docs/
│   └── SETUP_GUIDE.md              # Complete Setup & Usage Guide
├── CHANGELOG.md                    # Version Update Log
└── LICENSE                         # MIT License
```

---

## ⭐Star History

<a href="https://www.star-history.com/?type=date&repos=Besty0728%2FUnity-Skills">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/chart?repos=Besty0728/Unity-Skills&type=date&theme=dark&legend=top-left" />
   <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/chart?repos=Besty0728/Unity-Skills&type=date&legend=top-left" />
   <img alt="Star History Chart" src="https://api.star-history.com/chart?repos=Besty0728/Unity-Skills&type=date&legend=top-left" />
 </picture>
</a>

---

## 📄 License
This project is licensed under the [MIT License](LICENSE).
