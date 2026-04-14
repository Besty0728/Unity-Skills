# UnitySkills Setup & Usage Guide

> English | [中文](SETUP_GUIDE_CN.md)

---

## Requirements

- **Unity**: `2022.3+` (LTS recommended; Unity 6 fully supported)
- **Network**: localhost loopback (`127.0.0.1` / `localhost`)
- **Python** (optional): 3.7+ with `requests` package, for the Python client helper

---

## 1. Install the Unity Package

Open Unity Editor:

```
Window → Package Manager → + → Add package from git URL
```

Choose one of the following:

| Channel | URL |
|---------|-----|
| **Stable** (main) | `https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity` |
| **Beta** (dev) | `https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity#beta` |
| **Pinned version** | `https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity#v1.6.8` |

You can also download a specific release from the [Releases page](https://github.com/Besty0728/Unity-Skills/releases).

---

## 2. Start the Server

```
Window → UnitySkills → Start Server
```

On success, the Console will show:

```
[UnitySkills] REST Server started at http://localhost:8090/
```

Verify with:

```bash
curl http://localhost:8090/health
```

> **Note**: Script compilation, Domain Reload, and certain asset operations will briefly make the server unreachable. This is normal Unity Editor behavior — wait a few seconds and retry.

---

## 3. Install AI Skills

### Recommended: One-Click Installer

```
Window → UnitySkills → Skill Installer
```

Select your AI tool and click **Install**. The installer copies the `unity-skills~/` template directory to the correct location. The installed files include:

```
SKILL.md                    # Main skill definition (AI reads this)
skills/                     # Per-module skill docs (38 functional + 14 advisory)
scripts/unity_skills.py     # Python client library
scripts/agent_config.json   # Agent configuration
references/                 # Unity development references
```

> **Codex Note**: Global installation is recommended. For project-level installation, declare the skill in your `AGENTS.md`.

### Manual Installation

If one-click installation is not available for your tool, manually copy the contents of `SkillsForUnity/unity-skills~/` to your tool's skill directory.

**Common tool paths:**

| Tool | Skill Directory |
|------|----------------|
| Claude Code | `~/.claude/skills/` |
| Antigravity | `~/.agent/skills/` |
| Gemini CLI | `~/.gemini/skills/` |
| Codex | `~/.codex/skills/` (global) |
| Cursor | `~/.cursor/skills/` |

### Supported AI Tools

The following tools have been officially tested:

| Tool | Status | Highlights |
|------|:------:|------------|
| **Antigravity** | ✅ | Native `/unity-skills` slash command |
| **Claude Code** | ✅ | Intelligent skill intent recognition |
| **Gemini CLI** | ✅ | `experimental.skills` support |
| **Codex** | ✅ | `$skill` explicit call + implicit intent |

> ⚠️ **Universal Compatibility**: UnitySkills follows an open skill standard. **Any AI tool that can read markdown files and make HTTP requests** can use UnitySkills — not limited to the tools listed above. Simply copy the `unity-skills~/` directory contents to your tool's skill or prompt location and ensure the tool can reach `http://localhost:8090`.

---

## 4. Python Client

### Basic Usage

```python
import unity_skills

# Check server status
unity_skills.health()

# Call a skill
unity_skills.call_skill("gameobject_create",
    name="MyCube", primitiveType="Cube", x=0, y=1, z=0)

# Get all available skills
unity_skills.get_skills()
```

### Filtered Queries & Recommendations

```python
# Filter skills by metadata
unity_skills.get_skills(category="GameObject", operation="Create")
unity_skills.get_skills(tags="batch")
unity_skills.get_skills(read_only=True)
unity_skills.get_skills(q="screenshot")

# Intent-based recommendation (server-side scoring)
unity_skills.find_skills("create red cube", top_n=5)

# Find skills that produce a specific output
unity_skills.get_skill_chain("instanceId")
```

### Workflow Context

```python
# Group operations for batch undo/redo
with unity_skills.workflow_context("Build Scene", "Create player and environment"):
    unity_skills.call_skill("gameobject_create", name="Player")
    unity_skills.call_skill("component_add", name="Player", componentType="Rigidbody")
# All operations can be rolled back with workflow_undo_task
```

### CLI Usage

```bash
python unity_skills.py --list
python unity_skills.py gameobject_create name=MyCube primitiveType=Cube
```

---

## 5. REST API

### Direct HTTP Calls

```bash
# Health check
curl http://localhost:8090/health

# Get all skills
curl http://localhost:8090/skills

# Filter skills
curl "http://localhost:8090/skills?category=GameObject&operation=Create"

# Intent-based recommendation
curl "http://localhost:8090/skills/recommend?intent=create+cube&topN=5"

# Execute a skill
curl -X POST http://localhost:8090/skill/gameobject_create \
  -H "Content-Type: application/json" \
  -d '{"name":"MyCube","primitiveType":"Cube","x":1,"y":2,"z":3}'
```

### Response Format

All skills return a unified format:

```json
{
  "status": "success",
  "result": {
    "success": true,
    "name": "MyCube",
    "instanceId": 12345,
    "position": {"x": 1, "y": 2, "z": 3}
  }
}
```

---

## 6. Key Concepts

### Domain Reload & Temporary Unavailability

The following operations may trigger Unity compilation and briefly interrupt the server:

- `script_create`, `script_append`, `script_replace`
- `debug_force_recompile`, `debug_set_defines`
- Some `asset_import` / `asset_reimport` / `asset_move` operations
- Package install/remove

**Recommended handling**: Wait a few seconds, then call `wait_for_unity()` or use `call_skill_with_retry()`.

### Batch-First Principle

When operating on 2+ objects, always prefer `*_batch` skills:

```python
# ✅ Good — single request
unity_skills.call_skill("gameobject_create_batch", items=[
    {"name": "A", "primitiveType": "Cube", "x": -1},
    {"name": "B", "primitiveType": "Cube", "x": 1},
])

# ❌ Avoid — multiple requests
for name in ["A", "B"]:
    unity_skills.call_skill("gameobject_create", name=name)
```

### Multi-Instance Routing

When multiple Unity projects are open simultaneously:

```python
unity_skills.set_unity_version("2022.3")   # Route by Unity version
unity_skills.list_instances()               # Enumerate all instances
```

### Unified Job Model

Long-running or reload-prone operations such as batch execution, test runs, package management, and script mutations now return a unified `jobId`.

```python
job = unity_skills.call_skill("script_create", name="PlayerController")
unity_skills.get_job_status(job["jobId"])
unity_skills.wait_for_job(job["jobId"], timeout=90)
```

`test_get_result(jobId)` is still available as a compatibility wrapper, but `job_status`, `job_logs`, `job_list`, `job_wait`, and `job_cancel` are now the primary async APIs.

---

## 7. Troubleshooting

| Problem | Symptom | Solution |
|---------|---------|----------|
| Connection refused | `Cannot connect to http://localhost:8090` | Check if server is started; may be in compilation / Domain Reload |
| Request timeout | No response after 15 minutes | Check if it's a long-running task; increase timeout in Unity panel |
| Empty skill list | `/skills` returns error | Check Console for compilation errors |
| Disconnect after script creation | Server unreachable after `script_create` | Normal — wait for compilation, then retry |
| Wrong instance | Request hits wrong project | Use `set_unity_version()` or connect by project name |
| Workflow state mismatch | Client/server state diverged | Read `workflow_session_status`; client has built-in recovery |

---

## 8. References

| Resource | Description |
|----------|-------------|
| [README.md](../README.md) | Project overview (English) |
| [README_CN.md](../README_CN.md) | Project overview (Chinese) |
| [SKILL.md](../SkillsForUnity/unity-skills~/SKILL.md) | Complete skill API reference |
| [CHANGELOG.md](../CHANGELOG.md) | Version history |
| [agent.md](../agent.md) | AI agent project overview |
