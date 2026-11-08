---
name: unity-event
description: Wire UnityEvent persistent listeners at editor time
---

# Unity Event Skills

Inspect and modify persistent listeners on UnityEvents (for example `Button.onClick` or `Toggle.onValueChanged`), the same listeners shown in the Inspector's event slots. Parameters come from the schema (`GET /skills/schema?category=Event&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Hooking up UnityEvents in the Inspector
- Wiring button or trigger callbacks
- Scripting persistent listeners

## Rules
- Listeners added here are serialized with the prefab or scene, so they are configured at editor time. `event_invoke` is valid only in Play mode or at runtime; outside it, only `EditorAndRuntime` listeners fire.
- The target locator is decided by the first one given, in this order: `targetEntityId`, `targetInstanceId`, `targetPath`, `targetObjectName`. A locator that finds nothing is an error and never falls back to the next one. A name shared by several objects takes the first and says so in `resolutionNotes`; a partial name matching several is refused with the candidates.
- `event_get_listeners` returns `targetEntityId` for each listener. Pass it back to `event_add_listener` or `event_set_listener` to bind the exact same object (a specific component, an asset or a scene GameObject). `target` alone is only a name and can be shared.
- An `object` argument passes a Unity object to a method. Among overloads of that method name, the one whose parameter type is closest to the argument's type is used, and the stored listener resolves the same way when it runs.
- `event_add_listener` needs the exact component type and public method name on the target.
- `event_remove_listener` and `event_clear_listeners` are delete-class: under Approval or Auto they return `MODE_FORBIDDEN` (the user must use Bypass or an Allowlist entry).

## Pitfalls
- `event_create` and `event_trigger` do not exist: UnityEvents are declared in component source; this module only wires listeners.
- `event_subscribe` does not exist: use `event_add_listener`.
- `event_remove` does not exist: use `event_remove_listener`.
- For XR interaction events, use the [xr](../xr/SKILL.md) module's `xr_add_interaction_event`; for C# event code, write it through the [script](../script/SKILL.md) module.

## Skills Overview

| Skill | Purpose |
|---|---|
| [`event_list_events`](reference/event_list_events.md) | List the UnityEvent fields on a component |
| [`event_get_listeners`](reference/event_get_listeners.md) | Read persistent listeners with their target ids |
| [`event_get_listener_count`](reference/event_get_listener_count.md) | Count persistent listeners |
| [`event_add_listener`](reference/event_add_listener.md) | Add a persistent listener |
| [`event_add_listener_batch`](reference/event_add_listener_batch.md) | Add several listeners in one call |
| [`event_set_listener`](reference/event_set_listener.md) | Replace the listener at an index |
| [`event_set_listener_state`](reference/event_set_listener_state.md) | Set a listener's call state |
| [`event_remove_listener`](reference/event_remove_listener.md) | Remove one listener by index (delete-class) |
| [`event_clear_listeners`](reference/event_clear_listeners.md) | Remove all listeners of an event (delete-class) |
| [`event_copy_listeners`](reference/event_copy_listeners.md) | Copy listeners from one event to another |
| [`event_invoke`](reference/event_invoke.md) | Invoke an event explicitly (runtime only) |
