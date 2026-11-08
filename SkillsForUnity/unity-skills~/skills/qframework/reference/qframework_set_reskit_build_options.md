### qframework_set_reskit_build_options
Set the ResKit build-time options. `simulationMode` goes through `ResKitEditorAPI.SimulationMode`. QFramework has no public setter for `appendHash` or `autoGenerateClass`, so they are written to the EditorPrefs keys `ResKitView` reads itself (`KEY_APPEND_HASH`, `KEY_AUTOGENERATE_CLASS`); that is the only write path QFramework exposes.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| simulationMode | bool | No | null | `ResKitEditorAPI.SimulationMode` |
| appendHash | bool | No | null | Append a hash to AssetBundle names |
| autoGenerateClass | bool | No | null | Auto-generate the resource-name constant class |

**Returns:** `changed`, `simulationMode`, `appendHash`, `autoGenerateClass`
