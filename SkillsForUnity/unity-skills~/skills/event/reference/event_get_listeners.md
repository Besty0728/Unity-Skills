### event_get_listeners
Get persistent listeners of a UnityEvent.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
| entityId | string | No | null | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |
| componentName | string | Yes | - | Component name |
| eventName | string | Yes | - | Event field name (e.g. "onClick") |

**Returns:** `{ success, gameObject, component, eventName, listenerCount, listeners }`; each listener is `{ index, target, targetType, targetEntityId, targetInstanceId, targetPath, targetAssetPath, method, state }`.

`target` is only a name, so same-name targets share it. `targetEntityId` names the target exactly: a GameObject, one specific component (two AudioSources on one GameObject have two ids) or an asset; `targetInstanceId` is the same id below Unity 6000.4 and always 0 from 6000.4. `targetPath` locates a scene object and `targetAssetPath` an asset (the one that does not apply is null). A destroyed target reads `target: "null"` with no entityId, instance id 0 and null paths.

Pass `targetEntityId` back to `event_add_listener` / `event_set_listener` to bind the same object: a component's id binds exactly that component when `targetComponentName` names its type, an asset's id binds the asset (an asset has no path or name locator), and `targetPath` finds a scene GameObject.
