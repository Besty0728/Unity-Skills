### qframework_generate_ui_panel_code
Generate UIKit panel and Designer code for a UI prefab through `UICodeGenerator.DoCreateCode`. The target must be a regular or variant Prefab asset; QFramework silently does nothing otherwise, so this skill validates first and returns a real error.

`UICodeGenerator` writes `<Panel>.cs` only when it does not exist, but always rewrites `<Panel>.Designer.cs`. This skill snapshots the Designer file either way, so `workflow_*` undo covers both files.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| prefabPath | string | Yes | - | Project-relative path to a regular or variant Prefab asset, e.g. `Assets/Art/UIPrefab/LoginPanel.prefab` |

**Returns:** `pendingCompile`, `error`, `expectedScriptPath`, `expectedDesignerPath`, `prefabPath`, `uiScriptDir`, `uiPrefabDir`, `namespaceName`

The Designer-file key here is `expectedDesignerPath`, while the view-controller skill uses `expectedDesignerScriptPath`. The difference is deliberate; do not assume one key works for both.

Refused under Approval and Auto (`MayTriggerReload`): Bypass or allowlist only.
