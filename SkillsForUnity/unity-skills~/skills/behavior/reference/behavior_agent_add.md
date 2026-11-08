### behavior_agent_add
Add a `BehaviorGraphAgent` to a GameObject (`name` / `entityId` / `instanceId` / `path`), optionally binding `graphAssetPath` in the same call. The graph is resolved and validated **before** the component is added, so a bad path never leaves a half-configured agent. Idempotent — an existing agent is reused and reported via `componentAdded: false`.
