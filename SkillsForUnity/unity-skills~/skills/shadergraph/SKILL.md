---
name: unity-shadergraph
description: Create and inspect Shader Graph assets
---

# Unity ShaderGraph Skills

Shader Graph asset workflows for Unity 2022.3+: template handling, MultiJson inspection and constrained internal-editor reflection writes. Parameters come from the schema (`GET /skills/schema?names=<skill>&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Creating a Shader Graph
- Inspecting graph structure
- Making controlled blackboard or node edits

## Rules
- HLSL text shaders belong to the [shader](../shader/SKILL.md) module. Load [shadergraph-design](../shadergraph-design/SKILL.md) before proposing graph architecture.
- Always call `shadergraph_get_structure` before a node-level edit. Treat its `nodeId` and `slotId` values as the only valid identifiers.
- Never invent slot names, node ids or template names from memory. Confirm node types, settings and slot layout with `shadergraph_list_supported_nodes`.
- `shadergraph_set_node_defaults` applies only to unconnected input slots; disconnect the slot first if it is connected.
- `shadergraph_set_node_settings` writes only the whitelist that `shadergraph_list_supported_nodes` exposes.
- A `PropertyNode` binds only an existing blackboard property; create the property first with `shadergraph_add_property`.
- SubGraph editing is limited to ordinary nodes; this module does not edit `SubGraphOutputNode` structure.
- If an error mentions a reflection or type lookup failure, report the version mismatch and stop. Do not retry with different argument shapes.
- Internal types are pinned to `com.unity.shadergraph` 14.0.x (Unity 2022.3) with limited Unity 6 coverage. A package update that changes internal types or the `MultiJson` schema can make mutators fail or silently no-op until the registry is updated.
- Unity 2022.3 with `com.unity.shadergraph@14.0.12` ships no `GraphTemplates/`; `shadergraph_create_graph` then falls back to a blank graph. Unity 6 may provide template directories.
- The supported node subset is filtered at runtime. In live validation 28 nodes overlapped; `AppendVectorNode` is Unity 6 only.

## Pitfalls
- Reading structure first and then editing by `nodeId` avoids most failures. Re-read `shadergraph_get_structure` after an edit when the next step depends on topology.

## Skills

### shadergraph_list_templates
List Shader Graph templates shipped by the installed package.

### shadergraph_create_graph
Create a Shader Graph asset from a package template, or a blank graph as fallback.

### shadergraph_create_subgraph
Create a blank Shader Sub Graph asset with a configured output slot.

### shadergraph_list_assets
List Shader Graph and Sub Graph assets in the project.

### shadergraph_get_info
Get a high-level summary of a Shader Graph or Sub Graph asset.

### shadergraph_get_structure
Inspect the live graph structure. Returns real `nodeId`, `position`, `slots`, `edges`, `properties` and `keywords`.

### shadergraph_list_supported_nodes
List the constrained node whitelist, supported versions, slots and editable settings.

### shadergraph_add_node
Add a supported node by `nodeType`, with optional initial settings and position.

### shadergraph_remove_node
Remove a node by its serialized `nodeId`; related edges are removed with it.

### shadergraph_move_node
Move a node by its serialized `nodeId`.

### shadergraph_connect_nodes
Connect an output slot to an input slot using `nodeId` and `slotId`.

### shadergraph_disconnect_nodes
Disconnect one exact edge, using the same four-value tuple.

### shadergraph_set_node_defaults
Set the default value of an unconnected input slot.

### shadergraph_set_node_settings
Write whitelisted node settings only.

### shadergraph_list_properties
List graph blackboard properties.

### shadergraph_add_property
Add a constrained blackboard property.

### shadergraph_update_property
Update a constrained blackboard property.

### shadergraph_remove_property
Remove a graph property.

### shadergraph_list_keywords
List graph blackboard keywords.

### shadergraph_add_keyword
Add a graph keyword.

### shadergraph_update_keyword
Update a graph keyword.

### shadergraph_remove_keyword
Remove a graph keyword.

### shadergraph_reimport
Force a reimport of a Shader Graph asset after external edits.

## Workflow

1. Create or locate the target graph.
2. Read `shadergraph_get_structure`.
3. Create blackboard properties or keywords first when the edit needs them.
4. Confirm node type, settings whitelist and slot layout with `shadergraph_list_supported_nodes`.
5. Add or move nodes, then connect or disconnect them by live `nodeId` and `slotId`.
