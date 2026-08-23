# Operating Modes & Governance

> This is the full version of the "🔐 Operating Modes" and "🛡️ Governance Layer" sections in the [README](../README.md). 中文版：[操作模式与治理层](OPERATING_MODES_CN.md)

## 🔐 Operating Modes (v1.9.0+)

UnitySkills ships with a true server-side permission system aligned with Claude Code permission modes. All mode switching happens in the Unity panel — open **Window > UnitySkills**, click the ⚙ (Settings) button, and use the **Server** section — chat trigger words (e.g. `"full auto"` / `"semi-auto"`) are no longer supported.

| Mode | Default | Behavior | Use Case |
|:-----|:-------:|:---------|:---------|
| **Approval** | — | AI must request → user approves → execute (returns `MODE_RESTRICTED` + grant token) | Manual control, sensitive projects |
| **Auto** | New installs | AI runs FullAuto skills directly; server only blocks auto-detected high-risk ops (NeverInSemi) | Day-to-day development |
| **Bypass** | Existing installs (upgrade) | All skills run unrestricted; only `ConfirmationToken` gate remains for high-risk ops | Automation, CI, fast iteration |

### Two approval channels under Approval mode

- **Dialog** (default) — AI explains intent + grant token, user agrees in chat, AI replays the token via `POST /permission/grant`
- **Panel** (opt-in) — grant token only takes effect after user clicks **[Approve]** in the Unity panel; AI-issued grants without panel approval return `GRANT_PENDING_APPROVAL`

### Zero-impact upgrade for existing users

The plugin detects legacy `UnitySkills_*` EditorPrefs keys and keeps **Bypass** as the default, preserving the previous Full-Auto behavior with no action required. New installations default to **Auto** — FullAuto skills run directly, only NeverInSemi (Delete / MayEnterPlayMode / MayTriggerReload / high-risk) operations are blocked. Switch to **Approval** from the ⚙ Settings drawer's Server section if you need per-skill manual gating.

### Audit log

`Library/UnitySkillsAudit.jsonl` (per-project, jsonl, auto-rolls at 1MB, keeps 3 files) records every grant / revoke / restricted hit / call. Open it from the ⚙ Settings drawer → **View Audit Log** (or press <kbd>Alt</kbd>+<kbd>Shift</kbd>+<kbd>L</kbd>) to browse, filter, delete individual entries (✕), or wipe everything (🗑 Clear All) — deletions themselves are appended as `audit_deleted` / `audit_cleared` events so the log stays auditable.

### Skill Installer uninstall button

The Skill Installer card shows a **per-scope uninstall** button that auto-adapts: disabled when nothing's installed, a single button labeled with its scope when only one is installed, and a dropdown (`Uninstall ▾`) listing Project / Global when both are installed.

### Installer output files

One-click installation (**AI Config** tab → **Install**) copies the `unity-skills~/` template directory from the package to the target location, generating:

- `SKILL.md`
- `skills/`
- `references/`
- `scripts/unity_skills.py`
- `scripts/agent_config.json` (contains Agent identifier)

After you upgrade the package, tools you already installed are refreshed to the new version automatically (nothing new is ever installed for you); turn it off in ⚙ Settings ▸ **AI Tools**.

### Advisory modules

27 advisory design modules (architecture, performance, design patterns, testability, package-specific source rules, etc.) are available in all modes and loaded on demand.

## 🛡️ Governance at four points in the call lifecycle

An AI driving the Editor writes to real scenes, prefabs and `.meta` files. The interesting question isn't whether it *can* — it's what happens when it gets something wrong. UnitySkills answers that at four points in the call lifecycle.

- **Before execution — `?mode=dryRun` / `?mode=plan`**: `POST /skill/{name}?mode=dryRun` writes nothing and returns parameter validation (`missingParams` / `unknownParams` / `typeErrors` / `semanticErrors` / `warnings`) plus an impact estimate (`mutatesScene` / `mutatesAssets` / `mayTriggerReload` / `mayEnterPlayMode` / `riskLevel`). Skills with a semantic planner also return `steps` / `changes`.
- **At execution — per-operation risk gating**: every skill declares `RiskLevel` / `Operation` / `MayEnterPlayMode` / `MayTriggerReload` in its `[UnitySkill]` metadata, and the server derives NeverInSemi from that metadata — gating never depends on the AI behaving well. **Allowlist** grants a persistent pass to individual skills; the optional `ConfirmationToken` handshake (off by default — ⚙ Settings → Runtime → Require Confirmation) adds one more gate on high-risk skills.
- **After execution — JSONL audit trail**: every call, grant, revoke and blocked hit is appended to `Library/UnitySkillsAudit.jsonl` (1 MB rotation, primary + 3 historical files), browsable and filterable in the panel. Deleting audit entries is itself recorded, as `audit_deleted` / `audit_cleared`.
- **After a mistake — typed, persistent snapshot rollback**: Workflow snapshots are typed (`Modified` / `Created` / `Deleted` / `Moved` / `Setting`), and the asset file and its `.meta` are content-addressed independently (`fileHash` / `metaFileHash`) under `Library/UnitySkills/`, so history survives Domain Reloads and editor restarts. `workflow_undo_task` rolls back one task, not the whole project.
- **Batch as a transaction**: `POST /skills/batch` supports fail-fast or `continueOnError`, cross-step `$ref` to reuse an earlier step's output, rollback on failure, and `?diff=1` for the aggregated net change.
