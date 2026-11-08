### qframework_set_uikit_settings
Write the UIKit project settings and persist them to `ProjectConfig.json`. Only the parameters you pass change.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| namespaceName | string | No | null | New default namespace |
| uiScriptDir | string | No | null | New UI script output directory |
| uiPrefabDir | string | No | null | New UI prefab output directory |
| assemblyNamesToSearch | string | No | null | JSON array of assembly names; **replaces** the list, does not merge |

**Returns:** `changed`, `namespaceName`, `uiScriptDir`, `uiPrefabDir`, `assemblyNamesToSearch`, `isDefaultNamespace`
