# 🎮 UnitySkills

<p align="center">
  <img src="docs/Unity-Skills-H.png" alt="Unity-Skills" width="800">
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Unity-2022.3%2B-black?style=for-the-badge&logo=unity" alt="Unity">
  <img src="https://img.shields.io/badge/Skills-726-green?style=for-the-badge" alt="Skills">
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

- 🛠️ **726 REST Skills Comprehensive Toolkit**: Includes 51 functional source modules plus 19 advisory design modules, with Batch operations for multi-object control.
- ⚡ **Revolutionary Efficiency Boost (v2.0.1+)**: Schema caching + exponential backoff polling + BATCH-FIRST guidance → **Token consumption ↓ 96%**, **simple tasks 4-6 calls → 1 call (↓ 75-83%)**. Current: v2.0.9.
- 🔐 **Three-Tier Permission Modes (v1.9.0+)**: Approval / Auto / Bypass with dual approval channels (Dialog / Panel), aligned with Claude Code permission modes; zero-impact upgrade for existing users.
- 🤖 **4 Major IDEs Native Support**: Claude Code / Antigravity / Codex / Cursor — one-click install and use.
- 🛡️ **Transactional Atomicity**: Failed operations auto-rollback, leaving scenes clean and safe.
- 🌍 **Multi-Instance Simultaneous Control**: Automatic port discovery and global registry for controlling multiple Unity projects at once.
- 🔗 **Ultra-Stable Long Connections**: Configurable request timeout (default 15 minutes), automatic recovery after Domain Reload, with retry hints during script compilation/asset updates.
- 🛡️ **Anti-Hallucination Guardrails**: Each Skill module includes DO NOT lists and routing rules to prevent calls to nonexistent commands or parameter errors.

---

## 🔐 Operating Modes (v1.9.0+)

UnitySkills ships with a true server-side permission system aligned with Claude Code permission modes. All mode switching happens in the Unity panel (**Window > UnitySkills > Server**) — chat trigger words are no longer supported.

| Mode | Default | Behavior | Use Case |
|:-----|:-------:|:---------|:---------|
| **Approval** | — | AI must request → user approves → execute (returns `MODE_RESTRICTED` + grant token) | Manual control, sensitive projects |
| **Auto** | New installs | AI runs FullAuto skills directly; server only blocks auto-detected high-risk ops (NeverInSemi) | Day-to-day development |
| **Bypass** | Existing installs (upgrade) | All skills run unrestricted; only `ConfirmationToken` gate remains for high-risk ops | Automation, CI, fast iteration |

**Two approval channels under Approval mode**:
- **Dialog** (default) — AI explains intent + grant token, user agrees in chat, AI replays the token via `POST /permission/grant`
- **Panel** (opt-in) — grant token only takes effect after user clicks **[Approve]** in the Unity panel; AI-issued grants without panel approval return `GRANT_PENDING_APPROVAL`

**Zero-impact upgrade for existing users**: the plugin detects legacy `UnitySkills_*` EditorPrefs keys and keeps **Bypass** as the default, preserving the previous Full-Auto behavior with no action required. New installations default to **Auto** — FullAuto skills run directly, only NeverInSemi (Delete / MayEnterPlayMode / MayTriggerReload / high-risk) operations are blocked. Switch to **Approval** in the Server tab if you need per-skill manual gating.

> ❌ Chat trigger words (e.g. `"full auto"` / `"semi-auto"`) are no longer recognized. Switch modes in **Window > UnitySkills > Server**.
>
> 📜 Audit log: `Library/UnitySkillsAudit.jsonl` (per-project, jsonl, auto-rolls at 1MB, keeps 3 files) records every grant / revoke / restricted hit / call. Open **Window > UnitySkills > Audit Log** to browse, filter, delete individual entries (✕), or wipe everything (🗑 Clear All) — deletions themselves are appended as `audit_deleted` / `audit_cleared` events so the log stays auditable.
>
> 🗑 The Skill Installer card shows a **per-scope uninstall** button that auto-adapts: disabled when nothing's installed, a single button labeled with its scope when only one is installed, and a dropdown (`Uninstall ▾`) listing Project / Global when both are installed.
>
> 19 advisory design modules (architecture, performance, design patterns, testability, package-specific source rules, etc.) are available in all modes and loaded on demand.

---

## 🏗️ Quick Install Supported IDE/Terminals

This project has been deeply optimized for the following environments to ensure a continuous and stable development experience (tools not listed below are not necessarily unsupported — they just lack a quick installer; use ***Custom Installation*** to the corresponding directory):

| AI Terminal | Support Status | Special Features |
| :--- | :---: | :--- |
| **Antigravity** | ✅ Supported | Open Agent Skills standard via `.agents/skills/` (workspace) and `~/.gemini/antigravity/skills/` (global). |
| **Claude Code** | ✅ Supported | Intelligent Skill intent recognition, supports complex multi-step automation. |
| **Codex** | ✅ Supported | Supports `$skill` explicit invocation and implicit intent recognition. Shares `.agents/skills/` with Antigravity in workspace mode. |
| **Cursor** | ✅ Supported | Auto-discovers `.cursor/skills/` and `.agents/skills/`; supports `/skill-name` explicit invocation; visible in Settings → Rules. |

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
2. Select the corresponding terminal icon (Claude / Antigravity / Codex / Cursor).
3. Click **"Install"** to complete the environment configuration without manual code copying.

> The installer copies the `unity-skills~/` template directory from the package to the target location.
>
> Installer output files (generated in target directory):
> - `SKILL.md`
> - `skills/`
> - `references/`
> - `scripts/unity_skills.py`
> - `scripts/agent_config.json` (contains Agent identifier)

> **Codex Note**: Antigravity and Codex share `.agents/skills/` in workspace mode — installing once for either makes the skill available to both. Codex auto-discovers skills in `.agents/skills/`; no `AGENTS.md` declaration needed.

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
   Replace `your-agent-name` with the name of your AI tool (e.g., `claude-code`, `antigravity`, `codex`, `cursor`).
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
- Antigravity: `~/.gemini/antigravity/skills/` (global) or `.agents/skills/` (workspace)
- OpenAI Codex: `~/.agents/skills/` (global) or `.agents/skills/` (workspace, shared with Antigravity)
- Cursor: `~/.cursor/skills/` (global) or `.cursor/skills/` (workspace); also auto-discovers `.agents/skills/`

#### 🧩 Other Tools Supporting Skills
If you're using other tools that support Skills, install according to the Skills root directory specified in that tool's documentation. As long as the **standard installation specification** is met (root directory contains `SKILL.md` and maintains `skills/`, `references/`, and `scripts/` structure), it will be correctly recognized.

</details>

---

<details>
<summary><h2>📦 Skills Category Overview (726)</h2></summary>

| Category | Count | Core Functions |
| :--- | :---: | :--- |
| **YooAsset** | 40 | Hot-update bundle builds/Collector full CRUD/BuildReport asset and dependency analysis/PlayMode runtime validation/Reporter-Debugger-AssetArtScanner tools |
| **Workflow** | 23 | Persistent history/Task snapshots/Session-level undo/Rollback/Bookmarks/Batch query-preview-execute jobs |
| **Cinemachine** | 34 | 2.x/3.x dual version auto-install/MixingCamera/ClearShot/TargetGroup/Spline |
| **Netcode** | 33 | Netcode for GameObjects setup/prefabs/lifecycle/host-server-client workflows |
| **UI** | 29 | Canvas/Button/Text/InputField/Dropdown/ScrollView/Layout/Alignment/Image and selectable utilities |
| **UI Toolkit** | 25 | UXML/USS file management/UIDocument/PanelSettings full property read-write/Template generation/Structure inspection/Batch create |
| **ShaderGraph** | 23 | Shader Graph create/inspect/blackboard edit/constrained node editing |
| **ProBuilder** | 22 | ProBuilder shape creation/face-edge operations/UV tools/pivot edits/batch creation/mesh combination |
| **XR** | 22 | XR rig setup/interactors/interactables/teleportation/continuous move/UI/haptics/interaction layers |
| **Material** | 21 | Batch material property modification/HDR/PBR/Emission/Keywords/Render queue |
| **PostProcess** | 10 | SRP post-processing effect management |
| **GameObject** | 18 | Create/Find/Transform sync/Batch operations/Hierarchy management/Rename/Duplicate |
| **Perception** | 18 | Scene summary/health checks/stack detection/context export/dependency analysis/hotspots/diff/tag-layer stats/performance hints |
| **Volume** | 9 | VolumeProfile/Volume/VolumeComponent creation and parameter editing |
| **Validation** | 10 | Project validation/Empty folder cleanup/Reference detection/Mesh collider/Shader errors |
| **URP** | 7 | URP asset/renderer/renderer feature inspection and edits |
| **Decal** | 7 | URP Decal Projector create/inspect/configure/delete workflows |
| **DOTween** | 21 | DOTweenAnimation editor-time setup and tuning |
| **Editor** | 12 | Play mode/Selection/Undo-Redo/Context retrieval/Menu execution |
| **Physics** | 12 | Raycast/SphereCast/BoxCast/Physics materials/Layer collision matrix |
| **Script** | 12 | C# script create/Read/Replace/List/Info/Rename/Move/Analyze |
| **Timeline** | 12 | Track create/Delete/Clip management/Playback control/Binding/Duration |
| **Asset** | 11 | Asset import/Delete/Move/Copy/Search/Folders/Batch operations/Refresh |
| **AssetImport** | 11 | Texture/Model/Audio/Sprite import settings/Label management/Reimport |
| **Camera** | 12 | Scene View control/Game Camera create/Properties/Screenshot/Orthographic toggle/List |
| **Graphics** | 11 | GraphicsSettings/QualitySettings/SRP asset operations |
| **Package** | 11 | Package management/Install/Remove/Search/Versions/Dependencies/Cinemachine/Splines |
| **Prefab** | 11 | Create/Instantiate/Override apply & revert/Batch instantiate/Variants/Find instances/Asset property editing |
| **Shader** | 11 | Shader create/URP templates/Compile check/Keywords/Variant analysis/Global keywords |
| **Test** | 13 | Test run/Run by name/Categories/Template create/Summary statistics |
| **Animator** | 10 | Animation controller/Parameters/State machine/Transitions/Assign/Play |
| **Audio** | 10 | Audio import settings/AudioSource/AudioClip/AudioMixer/Batch |
| **Cleaner** | 10 | Unused assets/Duplicate files/Empty folders/Missing script fix/Dependency tree |
| **Component** | 14 | Add/Remove/Property config/Batch operations/Copy/Enable-Disable |
| **Console** | 10 | Log capture/Clear/Export/Statistics/Pause control/Collapse/Clear on play |
| **Debug** | 10 | Error logs/Compile check/Stack trace/Assemblies/Define symbols/Memory info |
| **Event** | 11 | UnityEvent listener management/Batch add/Copy/State control/List |
| **Light** | 10 | Light create/Type config/Intensity-Color/Batch toggle/Probe groups/Reflection probes/Lightmaps |
| **Model** | 10 | Model import settings/Mesh info/Material mapping/Animation/Skeleton/Batch |
| **NavMesh** | 10 | Bake/Path calculation/Agent/Obstacle/Sampling/Area cost |
| **Optimization** | 10 | Texture compression/Mesh compression/Audio compression/Scene analysis/Static flags/LOD/Duplicate materials/Overdraw |
| **Profiler** | 10 | FPS/Memory/Texture/Mesh/Material/Audio/Rendering stats/Object count/AssetBundle |
| **Scene** | 10 | Multi-scene load/Unload/Activate/Screenshot/Context/Dependency analysis/Report export |
| **ScriptableObject** | 10 | Create/Read-Write/Batch set/Delete/Find/JSON import-export |
| **Smart** | 10 | Scene SQL query/Spatial query/Auto layout/Snap to ground/Grid snap/Randomize/Replace |
| **Terrain** | 10 | Terrain create/Heightmap/Perlin noise/Smooth/Flatten/Texture painting |
| **Texture** | 10 | Texture import settings/Platform settings/Sprite/Type/Size search/Batch |
| **Project** | 9 | Render pipeline/Build settings/Package management/Layer/Tag/PlayerSettings/Quality |
| **Sample** | 8 | Basic examples: Create/Delete/Transform/Scene info |
| **Diagnose** | 1 | Aggregated Editor health snapshot (console/compile/workflow/server/jobs) |

> ⚠️ Most modules support `*_batch` batch operations. When operating on multiple objects, prioritize batch Skills for better performance.
>
> 🧠 `unity-skills/skills/` also includes **19 advisory design modules** for architecture, script design, performance, maintainability, Inspector guidance, and package-specific source rules.

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
│   │   ├── skills/                 # 68 module docs (49 REST/module docs + 19 advisory docs)
│   │   └── references/             # Unity Development References
│   └── Editor/Skills/              # Core Skill Logic (51 *Skills.cs files, 726 Skills)
│       ├── SkillsHttpServer.cs     # HTTP Server Core (Producer-Consumer)
│       ├── SkillRouter.cs          # Request Routing & Reflection-based Skill Discovery
│       ├── WorkflowManager.cs      # Persistent Workflow (Task/Session/Snapshot)
│       ├── RegistryService.cs      # Global Registry (Multi-instance Discovery)
│       ├── GameObjectFinder.cs     # Unified GO Finder (name/instanceId/path)
│       ├── BatchExecutor.cs        # Generic Batch Processing Framework
│       ├── GameObjectSkills.cs     # GameObject Operations (18 skills)
│       ├── MaterialSkills.cs       # Material Operations (21 skills)
│       ├── CinemachineSkills.cs    # Cinemachine 2.x/3.x (34 skills)
│       ├── WorkflowSkills.cs       # Workflow Undo/Rollback (23 skills)
│       ├── PerceptionSkills.cs     # Scene Understanding (18 skills)
│       └── ...                     # 726 Skills source code
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

**Bundled font (separate license):** the editor window bundles a subsetted CJK font
`SkillsForUnity/Editor/UI/Fonts/UnitySkillsCN-Regular.ttf`, derived from
[Maple Mono](https://github.com/subframe7536/maple-font) (CN variant), licensed under the
**SIL Open Font License 1.1** — not MIT. The full license and attribution travel with the
font in that folder (`OFL.txt`, `THIRD-PARTY-NOTICES.md`).
