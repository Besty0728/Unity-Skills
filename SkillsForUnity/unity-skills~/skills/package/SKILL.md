---
name: unity-package
description: Manage Unity Package Manager (UPM) — install, remove, refresh, search, inspect, and list packages. Use when adding or removing UPM packages, checking installed versions, searching the registry, or scripting package operations, even if the user just says "装个包" or "UPM". 管理 Unity Package Manager(UPM:安装、移除、刷新、搜索、查看、列出包);当用户要添加或移除 UPM 包、检查已装版本、搜索 registry、或脚本化包操作时使用。
---

# Package Skills

Manage installed Unity packages and package-related helper flows such as Cinemachine and Splines setup.

## Guardrails

**Operating Mode** (v1.9 three-tier):
- **Approval** (default): query skills (`package_list`, `package_check`, `package_search`, `package_get_dependencies`, `package_get_versions`, `package_get_cinemachine_status`) run directly. Mutators (`package_install`, `package_remove`, `package_install_cinemachine`, `package_install_splines`, `package_refresh`) are FullAuto — on `MODE_RESTRICTED`, run the grant protocol.
- **Auto** / **Bypass**: SemiAuto and FullAuto run directly.
- Auto-forbidden in this module: `package_install` and `package_remove` (`MayTriggerReload = true`, `RiskLevel = "high"`; `package_remove` also carries `SkillOperation.Delete`). They are reachable only under Bypass mode or via a user-managed Allowlist entry; the grant flow returns `MODE_FORBIDDEN`.
- Install/remove/refresh jobs return immediately with a `jobId`; the actual package import + Domain Reload happens asynchronously and may make the REST server transiently unavailable. Poll with `job_status` / `job_wait`.

**DO NOT** (common hallucinations):
- `package_add` / `package_update` do not exist -> use `package_install`
- `package_get_info` does not exist -> use `package_list`, `package_check`, `package_get_dependencies`, or `package_get_versions`
- `package_search` searches the installed package cache only; it does not query the Unity Registry
- Package query skills initialize their cache automatically after Domain Reload. During the short cold-start window they return `{ success: false, status: "refreshing", cacheReady: false, retryStrategy: "wait_and_retry", retryAfterSeconds: 2 }`; retry instead of treating this as `installed=false`.
- Package install/remove/refresh jobs can trigger package import and Domain Reload; expect transient server unavailability and use returned job IDs

**Routing**:
- For Cinemachine quick setup -> use `package_install_cinemachine`
- For Splines quick setup -> use `package_install_splines`
- For project manifest inspection -> use `project_get_packages`
- For define symbol changes after package installation -> use `debug_set_defines`

## Skills

### `package_list`
List all installed packages currently cached by UnitySkills.
**Parameters:** None.

**Returns:** `{ success, count, packages }`

### `package_check`
Check whether a package is installed.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `packageId` | string | Yes | - | Package ID such as `com.unity.cinemachine` |

**Returns:** `{ packageId, installed, version }`

### `package_install`
Install a package. Returns an async job when the request is accepted.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `packageId` | string | Yes | - | Package ID to install |
| `version` | string | No | null | Optional explicit version |

**Returns:** `{ success, status, jobId, message, serverAvailability }`

### `package_remove`
Remove an installed package. Returns an async job when the request is accepted.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `packageId` | string | Yes | - | Installed package ID to remove |

**Returns:** `{ success, status, jobId, message, serverAvailability }`

### `package_refresh`
Refresh the installed package cache used by query skills.
**Parameters:** None.

**Returns:** `{ success, status, jobId, message }`

### `package_install_cinemachine`
Install Cinemachine using the supported package/version strategy.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `version` | int | No | 3 | `2` for CM2, `3` for CM3 |

**Notes:**
- CM3 auto-installs the Splines dependency.
- If the requested line is already installed, this skill can return immediate success instead of a job.

**Returns:** `{ success, status?, jobId?, message, serverAvailability? }`

### `package_install_splines`
Install or upgrade Unity Splines using the recommended version for the current Unity editor line.
**Parameters:** None.

**Returns:** `{ success, status?, jobId?, message, serverAvailability? }`

### `package_get_cinemachine_status`
Get current Cinemachine and Splines installation status.
**Parameters:** None.

**Returns:** `{ cinemachine, splines }`

### `package_search`
Search the installed package cache by package name or display name.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `query` | string | Yes | - | Search keyword |

**Returns:** `{ success, query, count, packages }`

### `package_get_dependencies`
Get dependency information for one installed package.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `packageId` | string | Yes | - | Installed package ID |

**Returns:** `{ success, packageId, version, dependencyCount, dependencies }`

### `package_get_versions`
Get available versions for one installed package.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `packageId` | string | Yes | - | Installed package ID |

**Returns:** `{ success, packageId, currentVersion, compatibleVersion, latestVersion, allVersions }`

## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
