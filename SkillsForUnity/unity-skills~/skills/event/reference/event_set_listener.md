### event_set_listener
Replace a persistent listener at a specific index.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
| entityId | string | No | null | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |
| componentName | string | Yes | - | Source component name |
| eventName | string | Yes | - | Event field name |
| index | int | No | 0 | Listener index to replace |
| targetName | string | No | null | Target GameObject name; like the three locators below, one of the four is needed. The first given decides, in this order: `targetEntityId`, `targetInstanceId`, `targetPath`, this name; one that finds nothing is an error and never falls back to the next |
| targetInstanceId | int | No | 0 | Target by legacy instance ID (always 0 on Unity 6000.4+) |
| targetPath | string | No | null | Target GameObject by hierarchy path |
| targetEntityId | string | No | null | Target by entityId: a GameObject, one of its components or an asset, as for `event_add_listener` (`event_get_listeners` reports it, so a listener rebinds to the exact component it already had) |
| targetComponentName | string | No | null | Target component name, or `GameObject`; with a component's id it names that component's type (or a base type) |
| methodName | string | Yes | - | Public method or `set_PropertyName` |
| mode | string | No | RuntimeOnly | Off, RuntimeOnly, or EditorAndRuntime |
| argType | string | No | void | void, int, float, string, bool, object |
| floatArg | float | No | 0 | Static float argument |
| intArg | int | No | 0 | Static int argument |
| stringArg | string | No | null | Static string argument |
| boolArg | bool | No | false | Static bool argument |
| objectReferenceName | string | No | null | Scene object argument name |
| objectReferenceInstanceId | int | No | 0 | Scene object argument instance ID |
| objectReferencePath | string | No | null | Scene object argument path |
| objectReferenceEntityId | string | No | null | Object argument entityId: a scene object, one of its components (`objectType` naming its type passes that exact component) or an asset; an id that names nothing is an error |
| objectAssetPath | string | No | null | Project asset argument path |
| objectType | string | No | null | Object/component type for object argument |

**Returns:** `{ success, index, target, targetType, targetEntityId, targetInstanceId, targetPath, targetAssetPath, method, state, argType }`
