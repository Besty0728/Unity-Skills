# Changelog

All notable changes to UnitySkills will be documented in this file.

## [1.3.5] - 2025-01-21

### Fixed - YAML Syntax for Gemini CLI

**Root Cause**: Gemini CLI's YAML parser failed silently because the `description` field was unquoted. Long strings with special characters must be enclosed in double quotes.

**Fix**: Added double quotes around `description` field in all SKILL.md files.

### Changed - Unified Skill Folder Name

**Breaking Change**: All AI skill folders are now named `unityskills` (lowercase, no dashes).

**Affected Paths**:
- Claude Code: `~/.claude/skills/unityskills/`
- Antigravity: `~/.gemini/antigravity/skills/unityskills/`
- Gemini CLI: `~/.gemini/skills/unityskills/`

**Action Required**: If you had the old version installed, please uninstall and reinstall using Unity's AI Config tab.

### Changed - Repository Structure

- Renamed `claude_skill_unity/claude_skill_unity/` to `unityskills/` (no more nested directory)
- Renamed `.gemini/skills/unity-skills/` to `.gemini/skills/unityskills/`
- All SKILL.md files updated with `name: unityskills`
- Simplified README.md with cleaner installation guide

### Removed

- Deleted development test scripts from repository root:
  - `apply_urp_material.py`
  - `rainbow_cubes.py`
  - `test_material_v1_3.py`
  - `verify_urp.py`

## [1.3.4] - 2025-01-21

### Fixed - Gemini CLI Skill Installation

**Problem Solved**: Gemini CLI requires skill folder name to match the `name` field in SKILL.md. Previously the folder was named `unity-skills` but SKILL.md defined `name: unity-editor-control`.

**Changes**:
- Renamed skill folder from `unity-skills` to `unity-editor-control`
- Updated all installation paths in `SkillInstaller.cs`
- Updated README.md with correct folder names

### Fixed - Language Setting Persistence

**Problem Solved**: User's language selection (English/Chinese) was lost after Domain Reload (script compilation). The UI would always reset to English.

**Solution**: Language preference is now persisted to `EditorPrefs` and automatically restored after Domain Reload.

### Added - Domain Reload Documentation in SKILL.md

**Problem Solved**: AI tools would stop working when they encountered `Connection Refused` errors after creating scripts, not knowing this was expected behavior.

**Changes**:
- Added clear documentation about Domain Reload behavior in generated SKILL.md
- Added retry guidance and wait time recommendations
- Updated Python helper with `call_skill_with_retry()`, `wait_for_unity()`, and `create_script()` functions

### Added - Enhanced Python Helper

New functions in `unity_skills.py`:
- `call_skill_with_retry(skill_name, max_retries=3, retry_delay=2.0, **kwargs)` - Auto-retry on connection errors
- `wait_for_unity(timeout=10.0)` - Wait for server to become available
- `get_server_status()` - Get detailed server status
- `create_script(name, template, wait_for_compile=True)` - Create script with optional wait for recompilation

### Added - Enhanced Type Conversion System

**Problem Solved**: `component_set_property` previously couldn't handle complex types like Vector2, Vector3, Quaternion, Color, etc. Now supports comprehensive type parsing.

**Supported Types**:
- `Vector2/3/4` - e.g., `"(1.5, 2.0)"` or `"1.5, 2.0"`
- `Vector2Int/3Int` - e.g., `"(10, 20)"` or `"10, 20"`
- `Quaternion` - e.g., `"(0, 0.7, 0, 0.7)"` for XYZW or `"45"` for Y rotation
- `Color/Color32` - e.g., `"red"`, `"#FF5500"`, `"(1, 0.5, 0, 1)"` for RGBA
- `Rect` - e.g., `"(0, 0, 100, 50)"` for XYWH
- `Bounds` - e.g., `"(0, 0, 0, 1, 1, 1)"` for center+size
- `LayerMask` - e.g., `"Default"` or `"5"`
- `AnimationCurve` - Linear (0→1) curve
- `Enum` - e.g., `"Perspective"` for Camera.projection

### Added - Reference Type Property Setting

**Problem Solved**: Couldn't set Transform, GameObject, or Component references via string names.

**New Parameters for `component_set_property`**:
- `referencePath` - Set reference by hierarchy path (e.g., `"Canvas/Panel/Button"`)
- `referenceName` - Set reference by object name (e.g., `"MainCamera"`)

**Example Usage**:
```json
{
  "targetName": "CinemachineCamera",
  "componentType": "CinemachineCamera",
  "propertyName": "Follow",
  "referenceName": "Player"
}
```

### Added - Extended Component Namespace Support

**Problem Solved**: Third-party components like Cinemachine couldn't be found.

**Supported Namespaces**:
- Unity Core: `UnityEngine`, `UnityEngine.UI`, `UnityEngine.Events`, `TMPro`
- Cinemachine: `Cinemachine`, `Unity.Cinemachine` (both old and new namespaces)
- Input System: `UnityEngine.InputSystem`
- XR: `UnityEngine.XR.*`
- Third-party: `DG.Tweening` (DOTween), `Rewired`
- And 15+ more namespaces

### Added - Intelligent GameObject Finding

**Problem Solved**: AI tools often fail to find objects due to naming assumptions (e.g., "MainCamera" vs "Main Camera").

**New Smart Search Features**:
- Case-insensitive matching
- Contains-match fallback (e.g., "camera" finds "Main Camera")
- Component-based search (e.g., find objects with Camera component)
- Tag-based search
- Similar name suggestions on failure

**New `GameObjectFinder` Methods**:
- `SmartFind(query)` - AI-friendly search with fallbacks
- `FindByNameContains(partial)` - Partial name matching
- `FindByComponent<T>()` - Find by component type
- `FindOrError(query)` - Returns helpful suggestions on failure

### Added - Full RectTransform Support

**Problem Solved**: `gameobject_set_transform` only worked for 3D objects.

**New Parameters for UI Objects**:
- `localPosX/Y/Z` - Local position (distinct from world position)
- `anchoredPosX/Y` - UI anchored position
- `anchorMinX/Y`, `anchorMaxX/Y` - Anchor points
- `pivotX/Y` - Pivot point
- `sizeDeltaX/Y` - Size delta
- `width`, `height` - Direct size shortcuts

### Changed - Component Skills Improvements

**`component_add`**:
- Returns `fullTypeName` (e.g., `"Unity.Cinemachine.CinemachineCamera"`)
- Provides similar type suggestions on failure

**`component_remove`**:
- New `componentIndex` parameter to remove specific instance when multiple exist
- Checks `RequireComponent` dependencies before removal

**`component_list`**:
- New `includeProperties` parameter to show properties per component

**`component_get_properties`**:
- New `includePrivate` parameter to show private fields

### Changed - Error Messages

All error responses now include:
- Actionable suggestions
- Retry guidance for Domain Reload scenarios
- Similar item suggestions when search fails

### Technical Details

**Health Endpoint Updates** (`/health`):
```json
{
  "version": "1.3.4",
  "note": "If you get 'Connection Refused', Unity may be reloading scripts. Wait 2-3 seconds and retry."
}
```

---

## [1.3.3] - 2025-01-24

### Added - Domain Reload Auto-Recovery

**Problem Solved**: Previously, when a script file was created or modified via `script_create` skill, Unity would recompile all scripts which triggers a Domain Reload. This destroyed the running server instance, requiring manual restart.

**Solution**: Implemented comprehensive Domain Reload recovery system:

- **AssemblyReloadEvents Integration**: Server now listens to `beforeAssemblyReload` and `afterAssemblyReload` events
- **State Persistence**: Server running state is saved to `EditorPrefs` before Domain Reload
- **Auto-Restart**: Server automatically restarts after compilation completes using `EditorApplication.delayCall`
- **User Control**: New "Auto-restart after compile" toggle in the Server tab to enable/disable this behavior

### Added - Live Server Statistics

- Real-time queue count display with color coding (gray=0, cyan=pending, yellow=backlog)
- Total processed requests counter
- Architecture display showing "Producer-Consumer" pattern

### Changed - Server Architecture Improvements

- **Strict Producer-Consumer Pattern**: HTTP thread now ONLY receives and enqueues requests
- **Main Thread Processing**: ALL Unity API calls and business logic run on main thread
- **Thread-Safe Timing**: Replaced `EditorApplication.timeSinceStartup` with `DateTime.UtcNow.Ticks` to avoid cross-thread Unity API access
- **Improved Shutdown**: `Stop(bool permanent)` and `StopPermanent()` methods for controlled shutdown behavior

### Changed - UI Improvements

- New localization strings for auto-restart feature (English/Chinese)
- Stop button now uses `StopPermanent()` for explicit user-initiated stops

### Technical Details

**New API Methods**:
- `SkillsHttpServer.AutoStart` - Gets/sets auto-start preference (persisted in EditorPrefs)
- `SkillsHttpServer.StopPermanent()` - Stops server without auto-restart
- `SkillsHttpServer.Stop(bool permanent)` - Stops server with optional permanent flag

**Health Endpoint Updates**:
The `/health` endpoint now returns additional fields:
```json
{
  "status": "ok",
  "version": "1.3.3",
  "autoRestart": true,
  "domainReloadRecovery": true,
  ...
}
```

## [1.3.2] - 2025-01-XX

- Initial Producer-Consumer architecture
- Server stability improvements
- Live statistics display

## [1.3.0] - 2025-01-XX

- 76+ Skills available
- Chinese/English UI support
- One-click AI tool configuration
- REST API server on port 8090
