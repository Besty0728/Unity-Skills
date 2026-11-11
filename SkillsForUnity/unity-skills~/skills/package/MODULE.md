# Unity Package Skills

Manage installed Unity packages (UPM) and the helper flows for Cinemachine and Splines. Parameters come from the schema (`GET /skills/schema?category=Package&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Checking whether a package is installed, or which version
- Listing dependencies or available versions of an installed package
- Adding or removing UPM packages
- Setting up Cinemachine or Splines

## Rules
- Query skills run in every mode. `package_refresh` is the only mutator that a grant can unlock under Approval (on `MODE_RESTRICTED`, run the grant protocol).
- `package_install`, `package_remove`, `package_install_cinemachine` and `package_install_splines` are high risk and may trigger a reload. They return `MODE_FORBIDDEN` under Approval and Auto, and a grant does not unlock them; they run only in Bypass or when allowlisted. Do not attempt the grant flow for them.
- Install, remove and refresh return a `jobId` at once. Import and Domain Reload run asynchronously and may make the REST server briefly unavailable. Poll with `job_status` or `job_wait`.
- Query skills answer `{ success: false, status: "refreshing", cacheReady: false, retryStrategy: "wait_and_retry", retryAfterSeconds: 2 }` during the cold start after a Domain Reload. Retry; do not read this as `installed=false`.

## Pitfalls
- `package_search` searches the installed package cache by name or display name. It does not query the Unity Registry.
- `package_check` before the cache loads returns `installed` and `version` from a direct lookup, with `cacheReady: false` and `isDirectDependency` / `similarInstalled` set to null. Call again for those fields.
- `package_install_cinemachine` accepts `version` 2 or 3 only; CM3 installs Splines as a dependency. It can return success at once when the requested line is already installed.

## Routing
- Project manifest inspection → `project_get_packages`.
- Define symbols after a package install → `debug_set_defines`.

## Skills

### `package_list`
List the installed packages cached by UnitySkills.
**Parameters:** None.

**Returns:** `{ success, count, packages }`

### `package_check`
Check whether a package is installed.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `packageId` | string | Yes | - | Package ID such as `com.unity.cinemachine` |

**Returns:** `{ packageId, installed, version, isDirectDependency, cacheReady, checkedPackages, similarInstalled, dependentSkills }`. `isDirectDependency` is true for a `manifest.json` entry, false when only another installed package requires it.

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
Refresh the installed package cache used by the query skills.
**Parameters:** None.

**Returns:** `{ success, status, jobId, message }`

### `package_install_cinemachine`
Install Cinemachine using the supported package and version strategy.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `version` | int | No | 3 | `2` for CM2, `3` for CM3; any other value is rejected |

**Returns:** `{ success, status?, jobId?, message, serverAvailability? }`

### `package_install_splines`
Install or upgrade Unity Splines, using the recommended version for the current Unity editor line.
**Parameters:** None.

**Returns:** `{ success, status?, jobId?, message, serverAvailability? }`

### `package_get_cinemachine_status`
Get the Cinemachine and Splines installation status.
**Parameters:** None.

**Returns:** `{ cinemachine, splines }`

### `package_search`
Search the installed package cache by package name or display name.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `query` | string | Yes | - | Search keyword |

**Returns:** `{ success, query, count, packages }`

### `package_get_dependencies`
Get the dependencies of one installed package.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `packageId` | string | Yes | - | Installed package ID |

**Returns:** `{ success, packageId, version, dependencyCount, dependencies }`

### `package_get_versions`
Get the available versions of one installed package.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `packageId` | string | Yes | - | Installed package ID |

**Returns:** `{ success, packageId, currentVersion, compatibleVersion, latestVersion, allVersions }`

**DO NOT** (common hallucinations):
- `package_add` and `package_update` do not exist: use `package_install`.
- `package_get_info` does not exist: use `package_list`, `package_check`, `package_get_dependencies` or `package_get_versions`.
