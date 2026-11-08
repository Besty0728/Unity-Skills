### event_add_listener
Add a persistent listener to a UnityEvent (Editor time).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
| entityId | string | No | null | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |
| componentName | string | Yes | - | Component owning the event |
| eventName | string | Yes | - | Event field name (e.g. "onClick") |
| targetObjectName | string | No | null | GameObject holding the method to call, by name. The first locator given decides, in this order: `targetEntityId`, `targetInstanceId`, `targetPath`, this name; one that finds nothing is an error and never falls back to the next. A name shared by several objects takes the first and says so in `resolutionNotes`; a partial name matching several is refused with the candidates |
| targetEntityId | string | No | null | Target by entityId: a GameObject, one of its components (`targetComponentName` naming its type then binds that exact component) or an asset such as a ScriptableObject (bound as it is) |
| targetInstanceId | int | No | 0 | Target by legacy instance ID, meaning the same as `targetEntityId` (always 0 on Unity 6000.4+, use `targetEntityId`) |
| targetPath | string | No | null | Target GameObject by hierarchy path, e.g. `Canvas/Menu` |
| targetComponentName | string | No | null | Component on the target; omitted or `GameObject` calls the object itself. When an id names a component, this names its type (or a base type) to bind that component; another type is looked up on its GameObject. For an asset target it must name the asset's own type, or be omitted |
| methodName | string | Yes | - | Public method or `set_PropertyName` |
| mode | string | No | RuntimeOnly | Off, RuntimeOnly, or EditorAndRuntime |
| argType | string | No | void | void, int, float, string, bool, or object |
| floatArg | float | No | 0 | Static float argument |
| intArg | int | No | 0 | Static int argument |
| stringArg | string | No | null | Static string argument |
| boolArg | bool | No | false | Static bool argument |
| objectArgEntityId | string | No | null | `object`: argument by entityId: a scene object, one of its components or an asset. The Object argument is located like the target: the first of this, `objectArgInstanceId`, `objectArgPath`, `objectArgName` decides and one that finds nothing is an error |
| objectArgInstanceId | int | No | 0 | `object`: argument by legacy instance ID (always 0 on Unity 6000.4+) |
| objectArgPath | string | No | null | `object`: scene object argument by hierarchy path |
| objectArgName | string | No | null | `object`: scene object argument by name |
| objectArgAssetPath | string | No | null | `object`: project asset argument path; when given, the scene locators above are ignored |
| objectArgType | string | No | null | `object`: what to pass. A scene reference is the GameObject unless this names a component (`Transform`, `Rigidbody`...), and a component's own id then passes that exact component; for an asset, its type (`Material`...) |

**Returns:** `{ success, message, index, mode, target, targetType, targetEntityId, targetInstanceId, targetPath, targetAssetPath, method, argType }` (the bound target, so a same-name target can be checked)

The target needs one of `targetObjectName`, `targetEntityId`, `targetInstanceId` or `targetPath`; with none, dryRun, plan and the call itself are refused (`SEMANTIC_INVALID`, `Provide one of: ...`).

`argType` `object` passes the argument to a method that takes one Unity object, stored with the listener as its static argument. Of the overloads of that name, the one whose parameter type is nearest to the argument's own type takes it (`Foo(Transform)` over `Foo(Component)` over `Foo(Object)` for a Transform), and the stored listener resolves its overload the same way when it runs. Only the standard `UnityEvent` is reached; `event_set_listener` takes the same arguments under the `objectReference*` / `objectAssetPath` / `objectType` names.
