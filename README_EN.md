# 🎮 UnitySkills


<p align="center">
  <img src="https://img.shields.io/badge/Unity-2021.3%2B-black?style=for-the-badge&logo=unity" alt="Unity">
  <img src="https://img.shields.io/badge/Skills-277-green?style=for-the-badge" alt="Skills">
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
- 🛠️ **Comprehensive Toolkit**: Built-in 277 Skills with **Batch** operations, significantly reducing HTTP overhead and improving execution efficiency.
- 🛡️ **Safety First**: Supports **Transactional** (atomic operations) with automatic rollback on failure, leaving no residue in scenes.
- 🌍 **Multi-Instance Support**: Automatic port discovery and global registry, enabling simultaneous control of multiple Unity projects.
- 🤖 **Deep Integration**: Exclusive support for **Antigravity Slash Commands**, unlocking the `/unity-skills` interactive experience.
- 🔌 **Full Environment Compatibility**: Perfect support for Claude Code, Antigravity, Gemini CLI, and other mainstream AI terminals.
- 🎥 **Cinemachine 2.x/3.x Dual Version Support**: Auto-detects Unity version and installs the corresponding Cinemachine, supporting **MixingCamera**, **ClearShot**, **TargetGroup**, **Spline**, and other advanced camera controls.

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

## 📦 Skills Category Overview (277)

| Category | Count | Core Functions |
| :--- | :---: | :--- |
| **Material** | 21 | Batch material property modification/HDR/PBR settings |
| **GameObject** | 18 | Create/Find/Transform sync/Batch operations/Hierarchy management |
| **Editor** | 12 | Play mode/Selection/Undo-Redo/Context retrieval |
| **Asset** | 11 | Asset import/Search/Folders/GUID management |
| **UI System** | 11 | Canvas/Button/Text/Slider/RectTransform |
| **Component** | 8 | Add/Remove/Property configuration/Copy-Paste |
| **Animator** | 8 | Animation controller/Parameters/State machine/Transitions |
| **Sample** | 8 | Sample scenes/Test case generation |
| **Light** | 7 | Light creation/Type configuration/Intensity-Color/Batch toggle |
| **Validation** | 7 | Project validation/Empty folder cleanup/Reference detection |
| **Terrain** | 6 | Terrain creation/Heightmap editing/Texture painting [v1.4] |
| **Perception** | 3 | Scene summary/Hierarchy description/Script analysis [v1.4] |
| **Smart** | 3 | Scene query/Auto layout/Reference assembly [v1.5 NEW] |
| **UI Layout** | 5 | Anchors/Size/Layout groups/Alignment/Distribution [v1.5 NEW] |
| **Scene** | 6 | Scene switching/Save/Load/Screenshot |
| **Script** | 6 | C# script creation/Compile check/Search |
| **Shader** | 6 | Shader find/Create/Property enumeration |
| **Workflow** | 6 | **[NEW]** Persistent history/Rollback/Snapshot/Tag management [v1.4] |
| **DebugEnhance** | 4 | Console logs/Clear/Error pause [v1.4] |
| **AssetImport** | 4 | Re-import/Texture settings/Model settings [v1.4] |
| **Cleaner** | 5 | Unused assets/Duplicate files/Missing reference detection [v1.4] |
| **Physics** | 4 | Physics materials/Raycasting/Layer settings |
| **Cinemachine** | 23 | 2.x/3.x dual version auto-install/Mixing camera/TargetGroup/Spline [v1.4.2] |
| **Package** | 7 | Package management/Cinemachine installation/Dependency handling [v1.4.2 NEW] |

> ⚠️ Most modules support `*_batch` batch operations. When operating on multiple objects, prioritize batch Skills for better performance.

---

## 📂 Project Structure

```bash
.
├── SkillsForUnity/                 # Unity Editor Plugin (Package Core)
│   └── Editor/Skills/              # Core Skill Logic & C# Installer
│       ├── EditorSkills.cs         # Editor control (includes editor_get_context)
│       ├── NavMeshSkills.cs        # NavMesh baking & agents [New]
│       ├── CinemachineSkills.cs    # Virtual camera control [New]
│       ├── TimelineSkills.cs       # Timeline management [New]
│       ├── TextureSkills.cs        # Texture import settings
│       ├── AudioSkills.cs          # Audio import settings
│       ├── ModelSkills.cs          # Model import settings
│       └── ...                     # 190+ Skills source code
├── unity-skills/                   # Cross-platform AI Skill Template (Core Source)
│   ├── SKILL.md                    # Skill definitions & Prompt design
│   ├── scripts/                    # Python Helper wrappers
│   └── skills/                     # Modular Skill documentation
│       ├── editor/SKILL.md
│       ├── importer/SKILL.md       # [v1.2 New]
│       └── ...
├── CHANGELOG.md                    # Detailed update log & roadmap
└── LICENSE                         # MIT License
```

---

## 📄 License
This project is licensed under the [MIT License](LICENSE).
