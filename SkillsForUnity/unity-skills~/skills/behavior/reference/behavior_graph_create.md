### behavior_graph_create
Create an empty `BehaviorAuthoringGraph` at `savePath` and force a reimport so Unity bakes the blackboard, the runtime `BehaviorGraph` sub-asset, the debug info sub-asset, and the mandatory `Start` root. Returns `hasRuntimeGraph`; a `warning` is returned when baking did not happen and the graph must be opened once in the editor window.
