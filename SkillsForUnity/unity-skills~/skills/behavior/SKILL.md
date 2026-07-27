---
name: unity-behavior
description: Drive Unity Behavior (com.unity.behavior) behavior graphs — discover and inspect graph assets, create empty graphs, attach and bind BehaviorGraphAgent components, and read/write blackboard variables on agents or graph defaults. Use when wiring NPC/AI decision logic, behavior trees, behavior graphs, or blackboard variables, even if the user just says "behavior graph", "行为图", "行为树", or "给这个 NPC 挂个 AI". 操作 Unity Behavior 行为图(查找与检查图资产、创建空图、挂载并绑定 BehaviorGraphAgent、读写黑板变量);当用户要搭建 NPC/AI 决策逻辑、行为树、行为图或黑板变量时使用。
---

# Behavior Skills

Unity Behavior (`com.unity.behavior`, validated against 1.0.16) graph asset discovery, agent wiring, and blackboard variable access.

The package is **optional** and is not a declared dependency of UnitySkills. Every skill in this module reaches it through reflection; when it is absent each skill returns the same structured `PACKAGE_NOT_INSTALLED` response pointing at `package_install`.

## Operating Mode

- Query skills (`behavior_status`, `behavior_graph_list`, `behavior_graph_info`, `behavior_agent_get`, `behavior_agent_list`, `behavior_blackboard_list`) are `SkillMode.SemiAuto` — they run in all three modes without a grant.
- Mutators (`behavior_graph_create`, `behavior_agent_add`, `behavior_agent_set_graph`, `behavior_blackboard_set`) are `SkillMode.FullAuto` — under **Approval** they need a user grant; under **Auto** / **Bypass** they execute directly.
- No skill in this module carries `SkillOperation.Delete`, so none is auto-forbidden in Approval / Auto mode.

## Prerequisites

Install the package first if `behavior_status` reports `installed: false`:

```
package_install  packageName="com.unity.behavior"
```

Installation triggers a Domain Reload; wait for it to finish, then call `behavior_status` again before using any other skill in this module.

## The two asset types (read this before binding graphs)

A "Behavior Graph" `.asset` file contains **two** objects, and mixing them up is the most common failure:

| Object | Role | Where it lives |
|--------|------|----------------|
| `BehaviorAuthoringGraph` | Editing representation — nodes, blackboard, story. Main asset. | The `.asset` file itself |
| `BehaviorGraph` | Baked runtime graph. What `BehaviorGraphAgent.Graph` actually requires. | Sub-asset nested inside the same file |

Skills in this module always take the **file path** (`graphAssetPath` / `assetPath`) and resolve the correct object internally — you never address the sub-asset directly. `behavior_agent_set_graph` finds the nested `BehaviorGraph` for you and refuses the bind with `RUNTIME_GRAPH_MISSING` when the graph has never been compiled.

## Guardrails

**Routing**:
- Behavior graphs / blackboards / `BehaviorGraphAgent`: this module
- Animator state machines: use `animator_*`
- Generic component attach on a GameObject: `component_add` works, but `behavior_agent_add` additionally binds the graph and validates it

**Runtime-first rules**:
- Always call `behavior_blackboard_list` before `behavior_blackboard_set` — the variable name is case-sensitive and the declared CLR type decides which value shapes are accepted.
- Never invent a `graphAssetPath` from memory; get it from `behavior_graph_list`.
- If a skill returns `errorCode: "API_MISMATCH"`, the installed package version has a different member layout than this integration expects. Do **not** retry with different arguments — report the version mismatch and fall back to the Behavior editor window or direct `.asset` text editing.
- A graph created by `behavior_graph_create` is empty apart from the auto-inserted `Start` root. Adding actual behavior nodes requires the Behavior editor window (see Limitations).

## Skills

### `behavior_status`
Report package availability: `installed`, `version`, which core types resolved, project graph asset count, and scene agent count. Safe to call when the package is missing — this is the one skill that answers instead of erroring.

### `behavior_graph_list`
List behavior graph assets with `path`, `guid`, `nodeCount`, `variableCount`, and `hasRuntimeGraph`. Optional `filter` (substring on path), `folder`, `limit`.

### `behavior_graph_info`
Structure summary for one graph asset: node count with a per-type breakdown, individual nodes (id / type / position, capped by `maxNodes`), root count, the full blackboard variable list with types and default values, subgraph dependencies, and runtime graph state.

### `behavior_graph_create`
Create an empty `BehaviorAuthoringGraph` at `savePath` and force a reimport so Unity bakes the blackboard, the runtime `BehaviorGraph` sub-asset, the debug info sub-asset, and the mandatory `Start` root. Returns `hasRuntimeGraph`; a `warning` is returned when baking did not happen and the graph must be opened once in the editor window.

### `behavior_agent_add`
Add a `BehaviorGraphAgent` to a GameObject (`name` / `instanceId` / `path`), optionally binding `graphAssetPath` in the same call. The graph is resolved and validated **before** the component is added, so a bad path never leaves a half-configured agent. Idempotent — an existing agent is reused and reported via `componentAdded: false`.

### `behavior_agent_get`
Read one agent: bound graph name and asset path, `isInitialised` / `isStarted` / `isRunning`, the graph's blackboard variables, and the agent-level overrides.

### `behavior_agent_set_graph`
Bind `graphAssetPath` to an existing agent. Resolves the nested runtime graph and rejects uncompiled graphs.

### `behavior_agent_list`
Every `BehaviorGraphAgent` in the loaded scenes with hierarchy path, bound graph, active/enabled flags, and run state. Optional `graphFilter`, `includeInactive`, `limit`.

### `behavior_blackboard_list`
List blackboard variables. Pass `graphAssetPath` to read the **asset's** authoring defaults (`source: "asset"`), or a GameObject locator to read an **agent's** graph variables plus its overrides (`source: "agent"`). One of the two is required.

### `behavior_blackboard_set`
Set one variable value.

| Parameter | Meaning |
|-----------|---------|
| `variable` | Variable name, case-sensitive. Required. |
| `value` | New value. Accepted shapes depend on the declared type (see below). |
| `name` / `instanceId` / `path` | GameObject locator — writes the **agent-level override**. |
| `graphAssetPath` | Writes the **graph asset default** and rebakes the runtime blackboard. |

Provide either the GameObject locator or `graphAssetPath`; the GameObject wins if both are given.

**Returns**: `target` (`"agent"` or `"asset"`), `variable`, `type`, the normalized `value`, and a `note` explaining where the write landed.

Supported value types and accepted input shapes:

| Declared type | Accepted `value` |
|---------------|------------------|
| `int` / `float` / `double` / `bool` / `string` | JSON scalar, or a parseable string |
| enum | Member name (case-insensitive) |
| `Vector2` / `Vector3` / `Vector4` / `Quaternion` | `[1,2,3]`, `{"x":1,"y":2,"z":3}`, or `"1,2,3"` |
| `Color` | `{"r":1,"g":0,"b":0,"a":1}`, `[1,0,0,1]`, or `"#FF0000"` |
| `Vector2Int` / `Vector3Int` | Same as the float vectors, truncated |
| `GameObject` / `Component` | `Assets/...` prefab path, scene hierarchy path, or scene object name |
| Other `UnityEngine.Object` | `Assets/...` or `Packages/...` asset path |

Setting an unknown variable returns the available names in `availableVariables`.

## Edit mode vs Play mode

`BehaviorGraphAgent` instantiates a private copy of its graph on `Init()`, which only happens in Play mode. This changes what reads and writes mean:

- **Edit mode** — `behavior_blackboard_set` with a GameObject writes an *agent-level override* (exactly what the Inspector shows). Reads of the graph variables return the **shared asset defaults**, not per-agent values; the per-agent values are in the `overrides` array.
- **Play mode** — writes go to the running graph instance, so they take effect immediately on that agent only.

This is why `behavior_blackboard_list` reports both `variables` and `overrides` for an agent.

## Workflow

1. `behavior_status` — confirm the package is installed.
2. `behavior_graph_list` — locate the graph, or `behavior_graph_create` to make a new one.
3. Author the node graph in the Behavior editor window (node editing is not scriptable — see Limitations).
4. `behavior_graph_info` — confirm the node topology and read the blackboard contract.
5. `behavior_agent_add` with `graphAssetPath` — attach and bind in one call.
6. `behavior_blackboard_list` — read the exact variable names and declared types.
7. `behavior_blackboard_set` — per-agent overrides, or graph-wide defaults via `graphAssetPath`.
8. `behavior_agent_list` — verify the whole scene's agent wiring.

## Limitations

**Node-level graph editing is not supported.** There is no skill to add, remove, connect, or reconfigure nodes inside a behavior graph. This is a deliberate exclusion, not an oversight:

- The authoring node graph is stored as a `[SerializeReference]` polymorphic `List<NodeModel>` with `PortModel` cross-references, an embedded `SerializableCommandBuffer`, and a version-stamped `NodeModelInfo` cache. Writing it correctly means reproducing the package's command/dispatcher pipeline, not just setting fields.
- Every edit must be followed by a consistent rebake of three sub-assets (runtime graph, runtime blackboard, debug info) plus `VersionTimestamp` synchronization, or the asset silently desyncs from its baked runtime graph.
- The serialization schema is explicitly versioned (`kLatestSerializationVersion`, currently 3) and has changed within the 1.0.x line. Reflection-driven topology writes would break on a patch release.

**Alternatives when you need node-level changes**:
1. **Preferred** — author the graph in the Behavior editor window (`Window > AI > Behavior`, or double-click the asset), then use this module to inspect and wire it up.
2. Duplicate a known-good graph with `asset_duplicate` and rebind it, instead of building topology from nothing.
3. For mechanical, repetitive edits to an existing graph, edit the `.asset` YAML directly — load the [yaml-editing](../yaml-editing/SKILL.md) advisory first for the managed-reference and `fileID` rules, and re-verify with `behavior_graph_info` afterwards.

**Other limitations**:
- `behavior_graph_create` produces an empty graph. Node authoring still requires the editor window.
- Blackboard variables cannot be created, renamed, retyped, or deleted — only existing variables can be assigned. Add variables in the Behavior editor's Blackboard panel.
- Event channels, subgraph dynamic linking, and runtime serialization (`Serialize` / `Deserialize`) are not exposed.
- `behavior_blackboard_set` against `graphAssetPath` changes the shared default for every agent that has no override for that variable.
- Agent lifecycle control (`Init` / `Start` / `End` / `Restart`) is not exposed; those are Play-mode runtime concerns better handled from game code.

## Version Sensitivity

Type and member names were taken from `com.unity.behavior` **1.0.16** sources and cross-checked against the published Scripting API. Public surface (`BehaviorGraphAgent`, `BehaviorGraph`, `BlackboardReference`, `Blackboard`, `BlackboardVariable`, `VariableModel`) is documented and stable. Authoring surface (`BehaviorAuthoringGraph`, `GraphAsset`, `BlackboardAsset`, `BehaviorBlackboardAuthoringAsset`) is **internal** to the package and reached by name — a package upgrade can move it. Every such lookup is null-checked and reports `API_MISMATCH` rather than throwing; `behavior_status` shows exactly which types resolved.

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
