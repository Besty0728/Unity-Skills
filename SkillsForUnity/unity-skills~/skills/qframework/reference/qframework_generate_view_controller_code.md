### qframework_generate_view_controller_code
Generate the bound-view code for a GameObject's `ViewController` component through `CodeGenKit.Generate(IBindGroup)`. Writes `.cs` and `.Designer.cs` immediately and returns before compilation (two-phase protocol, see [MODULE.md](../MODULE.md)). The target must already carry a `ViewController` component, or a subclass, with `ScriptsFolder` and `ScriptName` set.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No* | null | GameObject name (exact match first, then substring) |
| instanceId | int | No* | 0 | Legacy Unity instance ID (Unity below 6000.4) |
| path | string | No* | null | Hierarchy path, e.g. `Canvas/Panel/Target` |
| tag | string | No* | null | GameObject tag |
| componentType | string | No* | null | First GameObject carrying this component type |
| entityId | string | No* | null | Unity EntityId (Unity 6000.4+, preferred) |

*At least one of the six is required. Lookup priority follows `GameObjectFinder.Find`: `entityId`, `instanceId`, `path`, `name` (exact, then substring), `tag`, `componentType`.

**Returns:** `pendingCompile`, `error`, `expectedScriptPath`, `expectedDesignerScriptPath`, `className`, `namespaceName`, `scriptsFolder`

Refused under Approval and Auto (`MayTriggerReload`): Bypass or allowlist only.
