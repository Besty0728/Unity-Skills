# UnitySkills — instructions for AI agents developing this repo

Audience: agents editing this repository. Agents *calling* the REST API read `SkillsForUnity/unity-skills~/SKILL.md` instead — that file is the shipped protocol doc (8,192-byte hard cap; new content goes under `references/`) and the authoritative description of operating modes, permission endpoints, batch/diff semantics and observability. Read it before touching anything the client sees.

| Field | Value |
|------|----|
| Version | 2.8.4 |
| Stack | C# Unity Editor plugin (UPM `com.besty.unity-skills`) + Python client |
| Unity | 2022.3+, verified on 6000.x |
| License | MIT |

## Architecture

`AI agent → unity_skills.py → HTTP localhost:8090-8100 → SkillsHttpServer (producer-consumer) → SkillRouter (reflects [UnitySkill]) → 56 *Skills.cs / 54 SkillCategory / 805 skills`. `WorkflowManager` = persistent undo/rollback, `RegistryService` = multi-instance discovery. 56 vs 54: `BatchSkills.cs` registers into Workflow + Validation, `DiagnoseSkills.cs` into Debug. The per-category table lives in `README.md`; `/skillcheck` keeps every count in sync — do not hand-edit counts.

- **Threading (hard rule)**: the HTTP thread only enqueues; the Unity main thread drains the queue from `EditorApplication.update`. Zero `UnityEngine.*`/`UnityEditor.*` calls off the main thread.
- **Every request, including GET `/health` `/jobs/{id}` `/skills`, goes through that main-thread queue** (≤20 per tick). A skill that blocks the main thread also freezes liveness; the Python client polls `GET /jobs/{id}` instead of `job_wait` for this reason.
- **No auth + wildcard CORS is intentional** (loopback bind is the accepted boundary). Do not report "add auth / tighten CORS / validate Origin" as a security finding.
- Optional-package modules (ProBuilder, XR, Netcode, YooAsset, DOTween, PrimeTween, Behavior, HybridCLR, Addressables, QFramework) detect their dependency and return `MISSING_PACKAGE`; URP-family modules (Volume/PostProcess/Decal/URP) compile to same-named `NoURP()` stubs without `com.unity.render-pipelines.universal`. QFramework has no UPM package, so detection is by reflected anchor type and it must not declare `RequiresPackages`.
- 28 advisory modules under `unity-skills~/skills/` are documentation only: no REST skills, no C# stub, ever.

Key files (`SkillsForUnity/Editor/`):
- `Skills/`: `SkillsHttpServer.cs`, `SkillRouter.cs`, `SkillPlanningService.cs` (/plan + dryRun engine, not a skill), `UnitySkillAttribute.cs`, `SkillErrorResponse.cs` + `SkillErrorCode.cs`, `SkillsLogger.cs` (single source of `Version`), `SkillsModeManager.cs`, `SkillsAuditLog.cs`, `ConfirmationTokenService.cs`, `WorkflowManager.cs`, `RegistryService.cs`, `GameObjectFinder.cs`, `BatchExecutor.cs`, `SkillInstaller.cs`, `AgentInstructionService.cs`, `UnityCliService.cs`, `ClientProcessResolver.cs` (caller identity via TCP port → PID → parent chain), `*Skills.cs` ×56.
- `Locales/{en,zh-CN,ru}.json`: all UI strings.
- `UI/`: `UnitySkillsWindow.{cs,uxml,uss}`, `Controllers/*.cs`, `Tabs/*.uxml`, `EditorUiScheduler.cs`, `ShortcutActions.cs`, `UISkillsFontIncrementalUpdater.cs`, `AuditLogWindow`, `AllowlistPickerWindow`, `UnityCliWindow`.
- `SkillsForUnity/unity-skills~/`: shipped template — `SKILL.md`, `scripts/unity_skills.py`, `skills/` (82 module docs: 54 REST + 28 advisory), `references/`.

## Rules

### Editor UI — UI Toolkit only
- No IMGUI (`OnGUI`, `EditorGUILayout`, `GUILayout`, `OnInspectorGUI`). New UI = `.cs + .uxml + .uss` in `Editor/UI/`, loaded via `Packages/com.besty.unity-skills/Editor/UI/...` path constants; in `CreateGUI()` add the USS then `CloneTree`; query nodes with `rootVisualElement.Q<T>("name")`.
- Periodic refresh only via `EditorUiScheduler.RepeatSafe(element, ms, body)`. Never bare `schedule.Execute().Every()` mutating the tree (issue #44: throws during repaint and loops) and never `EditorApplication.update` polling. Expensive data refreshes on tab activation / button / value change, not on the tick.
- One controller per UXML subtree: `XxxController(VisualElement root, EditorWindow owner)`; the window only assembles. New tab = `Tabs/X.uxml` (+ `.meta` with fresh GUID) + `Controllers/XTabController.cs` + one `MainTabDefinition` appended to `UnitySkillsWindow.MainTabs`. Copy `HistoryTabController`, the smallest complete example.
- `[MenuItem("Window/UnitySkills")]` is a leaf and must stay the only item under that prefix (Unity swallows a leaf that coexists with a submenu). Secondary panels open via in-panel buttons + shortcuts only.
- Shortcuts: a `[Shortcut]` static method in `Editor/UI/ShortcutActions.cs` plus a `Commands` entry; unbound by default; bindings live in ShortcutManager, not EditorPrefs.
- Styling only via USS classes and `var(--color-*)` tokens from `UnitySkillsWindow.uss` `:root` (add a `.unity-theme-light` value for every new color). Visibility via `UiVisibility.SetVisible(element, bool)`, never `style.display` or inline `display:none` in UXML. Inline styles only for runtime-computed values (font asset, viewport-derived width, data-driven flexGrow, loaded textures).
- No emoji icons (Unity 2022 editor font cannot render them); use USS geometry or `UISkillsEditorIcons`.
- Settled topbar decisions (2026-09-06), not regressions: status dot visible at every width; settings gear pinned top-right; narrow-mode bulb is vector geometry.

### Skills
```csharp
[UnitySkill("skill_name", "First sentence is for the AI; mention parameters.",
    Category = SkillCategory.GameObject, Operation = SkillOperation.Create, // combine with |
    Tags = new[] { "primitive" }, Outputs = new[] { "instanceId", "path" },
    TracksWorkflow = true, MutatesScene = true, RiskLevel = "medium")]      // low/medium/high
public static object SkillName(string name, float x = 0f) { ... }
```
- Required: name, description, `Category`, `Operation`. Strongly recommended: `Tags`, `Outputs`; `TracksWorkflow=true` on writers; risk flags (`MutatesScene`, `MayTriggerReload`, `MayEnterPlayMode`, …) filled truthfully — `Outputs` must be a subset of what the method actually returns.
- `Mode` defaults to `FullAuto`; declare `SemiAuto` only for pure reads with zero side effects. `Operation.Delete`, `MayEnterPlayMode`, `MayTriggerReload`, or `RiskLevel="high"` auto-classify as NeverInSemi. Permission gating, audit, allowlists and confirmation tokens are framework concerns driven by metadata — never write authorization logic inside a skill.
- Business errors return `SkillErrorResponse.Build(code, msg, ...)` (structured `errorCode` / `suggestedFixes` / `retryStrategy`); only unknown framework errors may throw.
- Validation: `if (Validate.Required(x, "x") is object err) return err;`.
- Writers register Undo and call `WorkflowManager.SnapshotXxx(...)` when `TracksWorkflow=true`. `SnapshotType` = Modified/Created/Deleted/Moved/Setting; settings restore through `WorkflowSettingRestorerRegistry`; file + `.meta` are content-addressed (`fileHash`/`metaFileHash`, schemaVersion 5); cleanup never deletes referenced blobs; limit `0` = unlimited.
- Provide `xxx` and `xxx_batch` pairs; batch uses `BatchExecutor.Execute<TItem>(items, perItem, idFn)`.
- New module: `SkillCategory` enum entry + `XxxSkills.cs` + `unity-skills~/skills/<module>/SKILL.md`; reflection discovers the methods. Run `/skillcheck` afterwards.

### Shared helpers — do not reimplement
| Need | Use | Not |
|------|-----|-----|
| Find GameObject | `GameObjectFinder.FindOrError(name, instanceId, path)` | `GameObject.Find` |
| Validate params | `Validate.Required` / `Validate.RequiredJsonArray` | hand-written if-chains |
| Batch | `BatchExecutor.Execute<T>` | manual loops |
| JSON | `SkillsCommon.JsonSettings` (Newtonsoft) | `JsonUtility`, ad-hoc settings |
| Optional packages | `XxxReflectionHelper.cs` + `DOTweenPresenceDetector` pattern | direct type references |
| Async | `AsyncJobService` / `BatchJobService` | own Task/Thread |
| Scene-wide find | `FindHelper.FindAll<T>()` | `FindObjectsOfType` |
| Periodic UI | `EditorUiScheduler.RepeatSafe` | bare scheduler / `EditorApplication.update` |

### Constants, logging, prefs, compatibility
- Version comes only from `SkillsLogger.Version`; never hardcode a version literal. Bump versions only through `/updateversion`.
- Log only via `SkillsLogger.{Log,LogWarning,LogError,LogVerbose,LogAgent}` with `PREFIX_*` colors; never `Debug.Log*`.
- Startup policy (owner decision): a normal editor launch / domain reload prints exactly one Info line. New startup diagnostics are Verbose; a startup Warning must correspond to a real failed action (e.g. port fallback), never a pre-emptive scan.
- EditorPrefs keys are prefixed `UnitySkills_`.
- Wrap Unity 6 APIs in `#if UNITY_6000_0_OR_NEWER` / `#else` (see `FindHelper`); prefer 2022.2+ non-obsolete events. Namespaces: `UnitySkills` for features, `UnitySkills.Internal` for helpers.

### Localization and font atlas
- All UI text lives in `Editor/Locales/{en,zh-CN,ru}.json`, read via `SkillsLocalization.Get(key[, args])`. No string dictionaries or UI literals in `.cs`. The three JSON files must have identical key sets.
- Every non-ASCII character in `Editor/UI/**`, `Editor/Skills/Localization.cs` and `Editor/Locales/*.json` must already exist in the `UnitySkillsCN-UI.asset` atlas (`UISkillsFontTests` enforces it; the scanner reads raw text, so comments count). Check the atlas before adding CJK; in tooling use `\uXXXX` escapes.
- Never run `UISkillsFontAssetBaker.Bake` (bundled TTF has no Cyrillic, atlas does; bake always fails). Add glyphs only with `UISkillsFontIncrementalUpdater.AddMissingGlyphs(string)` from a throwaway MenuItem via `editor_execute_menu`, then delete the scaffold. Verify afterwards: added set == intended, nothing removed, character count == glyph count, no glyph index shared by two characters, `UISkillsFont.IsPersistentAndComplete()` true.
- Russian copy must avoid uppercase U+0426 U+0427 U+0428 U+0429 U+042A U+042C and U+0401; reword instead.

### Comments, encoding, file tail
- Source comments are English. Chinese only when quoting UI copy, an upstream message, or a localization key, inside quotes. Chinese inside string literals is product output — leave it.
- `.cs` / `.py` are UTF-8 with BOM, code starting right after it. `.md` / `.uxml` / `.uss` / `.meta` have no BOM.
- Every script ends with a blank line then `// Producer:Betsy` (`# Producer:Betsy` in Python) and a newline.

## Workflow and commands
Commands are prompt files under `.claude/commands/`; agents without slash commands read the file and follow it.
- `/skillcheck` (`.claude/commands/skillcheck.md`) — C# ↔ `skills/*/SKILL.md` consistency audit and skill-count sync across `AGENTS.md`, README×2 and the root `SKILL.md`. Run after adding, renaming or removing any skill.
- `/metacheck` (`.claude/commands/metacheck.md`) — `.meta` GUID audit; run after adding assets or `.meta` files.
- `/updateversion <MAJOR.MINOR.PATCH>` (`.claude/commands/updateversion.md`) — bumps the declared version anchors and writes the CHANGELOG entry; verifies with `.github/scripts/check_project_version.py`. Never search-and-replace version numbers globally.
- `/release [version]` (`.claude/commands/release.md`) — candidate matrix → beta/main sync → tag → stable GitHub Release. Develop on `beta`; `main` is linear, no merge commits. Never move or re-publish a released tag: users install by `#vX.Y.Z`, so fix forward with a new number.
- Domain reload makes the REST server briefly unreachable (503/504 with diagnostics); clients retrying is expected. Do not modify the server to hide it.
