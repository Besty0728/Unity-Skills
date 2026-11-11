# Unity Behavior Skills

Unity Behavior (`com.unity.behavior`, validated against 1.0.16): graph asset discovery, agent wiring and blackboard variable access. The package is optional and not a UnitySkills dependency; every skill reaches it through reflection. Parameters come from the schema (`GET /skills/schema?category=Behavior&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Wiring NPC or AI decision logic
- Binding behavior graphs to agents
- Reading or writing blackboard variables

## Rules
- If `behavior_status` reports `installed: false`, install with `package_install` (`{"packageName":"com.unity.behavior"}`), wait for the Domain Reload, then call `behavior_status` again. Without the package, every skill returns `MISSING_PACKAGE`.
- Always call `behavior_graph_list` for a `graphAssetPath`; never guess one.
- Always call `behavior_blackboard_list` before `behavior_blackboard_set`: variable names are case-sensitive and the declared CLR type decides the accepted value shapes.
- `API_MISMATCH` means the installed package has a different member layout. Do not retry with other arguments; report the version and fall back to the Behavior editor window.
- The asset file holds two objects: `BehaviorAuthoringGraph` (the editing representation, the main asset) and `BehaviorGraph` (the baked runtime graph, a sub-asset). Skills take the file path and resolve the right object. `behavior_agent_set_graph` returns `RUNTIME_GRAPH_MISSING` for a graph that was never compiled.
- `behavior_graph_create` returns an empty graph apart from the auto-inserted `Start` root. Node editing is not scriptable: use the Behavior editor window (`Window > AI > Behavior`).
- Edit mode vs Play mode: `BehaviorGraphAgent` copies its graph only on `Init()`, which runs in Play mode. In Edit mode `behavior_blackboard_set` with a GameObject writes an agent-level override, reads return the shared asset defaults, and the per-agent values are in `overrides`. In Play mode writes affect only that running agent.
- `graphAssetPath` on `behavior_blackboard_set` changes the shared default for every agent without an override for that variable.
- Out of scope: adding or editing nodes, creating, renaming, retyping or deleting blackboard variables, event channels, subgraph linking, runtime serialization, and agent lifecycle (`Init` / `Start` / `End` / `Restart`). For node-level changes, duplicate a known-good graph with `asset_duplicate`, or edit the `.asset` YAML with the [yaml-editing](../yaml-editing/MODULE.md) rules.
- Public types (`BehaviorGraphAgent`, `BehaviorGraph`, `BlackboardReference`, `Blackboard`) are stable; authoring types (`BehaviorAuthoringGraph`, `GraphAsset`) are internal to the package and can move in a package upgrade. `behavior_status` lists which types resolved.
- Animator state machines belong to `animator_*`. `component_add` attaches the component only; `behavior_agent_add` also binds and validates the graph.

## Pitfalls
- Do not invent a `graphAssetPath`: list the graphs first.
- A bad `graphAssetPath` in `behavior_agent_add` is refused before the component is added, so no half-configured agent is left behind.

## Skills Overview

| Skill | Purpose |
|---|---|
| [`behavior_status`](reference/behavior_status.md) | Package availability and resolved types; answers even when the package is missing |
| [`behavior_graph_list`](reference/behavior_graph_list.md) | List graph assets with node and variable counts |
| [`behavior_graph_info`](reference/behavior_graph_info.md) | Node topology and blackboard contract of one graph |
| [`behavior_graph_create`](reference/behavior_graph_create.md) | Create an empty graph and bake its runtime data |
| [`behavior_agent_add`](reference/behavior_agent_add.md) | Add a `BehaviorGraphAgent`, optionally bound to a graph |
| [`behavior_agent_get`](reference/behavior_agent_get.md) | Read one agent's bound graph, run state and overrides |
| [`behavior_agent_set_graph`](reference/behavior_agent_set_graph.md) | Bind a graph to an existing agent |
| [`behavior_agent_list`](reference/behavior_agent_list.md) | List every agent in the loaded scenes |
| [`behavior_blackboard_list`](reference/behavior_blackboard_list.md) | List variables of an asset or an agent |
| [`behavior_blackboard_set`](reference/behavior_blackboard_set.md) | Set a variable on an agent or a graph asset |
