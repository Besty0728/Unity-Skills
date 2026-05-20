---
name: unity-shadergraph
description: "Shader Graph asset creation, structure inspection, constrained blackboard editing, and constrained node-subset editing for `.shadergraph` / `.shadersubgraph`. Use when users want to create graphs, inspect real node/slot structure, edit properties or keywords, or perform safe low-level node operations. Triggers: Shader Graph, shadergraph, sub graph, blackboard property, shader keyword, node editing, slot connect, 着色图, 子图, ShaderGraph 属性, ShaderGraph 节点, ShaderGraph 连线."
---

# ShaderGraph Skills

Shader Graph asset workflows for Unity 2022.3+ with source-backed template handling, MultiJson inspection, and constrained internal-editor reflection writes.

## Guardrails

**Mode**: Mixed — query skills marked SkillMode.SemiAuto; mutators are SkillMode.FullAuto (need grant under Approval)

> Some skills (Delete / PlayMode / Reload / high-risk) are auto-forbidden in Approval/Auto modes — only Bypass can run them.

**Routing**:
- HLSL text shaders: use `shader_*`
- Shader Graph / Sub Graph assets: use this module
- Source-anchored design guidance before proposing graph architecture: load [shadergraph-design](../shadergraph-design/SKILL.md)

**Runtime-first rules**:
- Always call `shadergraph_get_structure` before any node-level edit; treat returned `nodeId` and `slotId` as the only valid identifiers
- Never invent slot names, node ids, or template availability from memory
- `shadergraph_set_node_defaults` only applies to unconnected input slots; if the slot is connected, disconnect first
- `shadergraph_set_node_settings` only writes the whitelist exposed by `shadergraph_list_supported_nodes`
- `PropertyNode` only binds existing blackboard properties; create the property first with `shadergraph_add_property`
- SubGraph editing is limited to ordinary nodes; this module does not edit `SubGraphOutputNode` structure

**Validated behavior**:
- Unity 2022.3 + `com.unity.shadergraph@14.0.12` does not ship `GraphTemplates/`; `shadergraph_create_graph` falls back to blank graph creation
- Unity 6 ShaderGraph packages may provide actual template directories; template listing and template-copy creation remain available there
- The supported node subset is runtime-filtered. The shared overlap is 28 nodes in live validation, while `AppendVectorNode` is currently Unity 6 only

## Skills

### `shadergraph_list_templates`
List Shader Graph templates shipped by the installed package.

### `shadergraph_create_graph`
Create a Shader Graph asset from a package template or blank fallback.

### `shadergraph_create_subgraph`
Create a blank Shader Sub Graph asset with a configured output slot.

### `shadergraph_list_assets`
List Shader Graph and Sub Graph assets in the project.

### `shadergraph_get_info`
Get a high-level summary of a Shader Graph or Sub Graph asset.

### `shadergraph_get_structure`
Inspect the live graph structure. Returns real `nodeId`, `position`, `slots`, `edges`, `properties`, and `keywords`.

### `shadergraph_list_supported_nodes`
List the constrained node whitelist, supported versions, slots, and editable settings.

### `shadergraph_add_node`
Add a supported node by `nodeType` with optional initial settings and position.

### `shadergraph_remove_node`
Remove a node by serialized `nodeId`; related edges are removed together.

### `shadergraph_move_node`
Move a node by serialized `nodeId`.

### `shadergraph_connect_nodes`
Connect a specific output slot to a specific input slot using `nodeId + slotId`.

### `shadergraph_disconnect_nodes`
Disconnect one exact edge using the same four-tuple.

### `shadergraph_set_node_defaults`
Set the default value of an unconnected input slot.

### `shadergraph_set_node_settings`
Write whitelisted node settings only.

### `shadergraph_list_properties`
List graph blackboard properties.

### `shadergraph_add_property`
Add a constrained blackboard property.

### `shadergraph_update_property`
Update a constrained blackboard property.

### `shadergraph_remove_property`
Remove a graph property.

### `shadergraph_list_keywords`
List graph blackboard keywords.

### `shadergraph_add_keyword`
Add a graph keyword.

### `shadergraph_update_keyword`
Update a graph keyword.

### `shadergraph_remove_keyword`
Remove a graph keyword.

### `shadergraph_reimport`
Force reimport of a Shader Graph asset after external edits.

## Workflow

1. Create or locate the target graph.
2. Read `shadergraph_get_structure`.
3. If needed, create blackboard properties or keywords first.
4. Call `shadergraph_list_supported_nodes` to confirm node type, settings whitelist, and slot layout.
5. Add/move nodes, then connect/disconnect using the live `nodeId/slotId` values.
6. Re-read `shadergraph_get_structure` after each significant edit if the next step depends on topology.

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
