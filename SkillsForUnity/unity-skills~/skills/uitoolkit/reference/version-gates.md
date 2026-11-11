# UI Toolkit Version Gates

Some skills and parameters call APIs that do not exist on every supported editor. Each one checks the running editor first. An unsupported call returns a structured `SEMANTIC_INVALID` with `requiredUnityVersion` and `currentUnityVersion`. Read the minimum version before you call.

## Unity 6 skills

| Skill | Minimum Unity | Use | Key parameters |
|-------|---------------|-----|----------------|
| `uitk_runtime_binding_add` | 6000.0 | Add or update a `<DataBinding>` on a UXML element | `filePath`, `elementName`, `property`, `bindingMode?`, `dataSource?`, `dataSourcePath?`, `extraAttributes?` |
| `uitk_runtime_binding_list` | none (read-only) | List bindings and data sources declared in a UXML file | `filePath` |
| `uitk_uxml_upgrade` | 6000.3 (not every build; see below) | Run registered UXML upgraders over assets | `filePath?`, `folder?`, `upgraderNames?`, `listOnly?`, `limit?` |
| `uitk_worldspace_panel_create` | 6000.2 | Create a world-space UI panel GameObject | `name`, `uxmlPath?`, `panelSettingsPath?`, `sizeMode?`, `worldSpaceSizeX/Y?`, `pivot?`, `pivotReferenceSize?`, `setPanelRenderMode?` |
| `uitk_worldspace_panel_get` | 6000.2 | Read a world-space panel's configuration | `name`, `entityId`, `instanceId` or `path` |
| `uitk_element_reference_get` | none (read-only) | List `authoring-id` values and nested authoring-id paths | `filePath`, `maxTemplateDepth?` |

## PanelSettings parameters

On both `uitk_create_panel_settings` and `uitk_set_panel_settings`:

- `renderMode`, `forceGammaRendering`, `bindingLogLevel`, `colliderUpdateMode`, `colliderIsTrigger` and `vertexBudget` need Unity 6.0+.
- `textureSlotCount` needs Unity 6000.3+.

On an older editor these return `SEMANTIC_INVALID` naming the parameter. They are never silently dropped. Both skills resolve every enum and asset path before the first write, so a rejected call leaves the asset unchanged.

## Runtime data binding

`uitk_runtime_binding_add` writes the binding into the UXML asset, so it persists. It produces the markup Unity's UI Builder produces:

```xml
<engine:Label text="Label" data-source="ExampleObject.asset" data-source-path="simpleLabel">
    <Bindings>
        <engine:DataBinding property="text" binding-mode="ToTarget" />
    </Bindings>
</engine:Label>
```

- `data-source` and `data-source-path` go on the element; `property` and `binding-mode` go on the `<DataBinding>`.
- `bindingMode` accepts `TwoWay`, `ToSource`, `ToTarget` or `ToTargetOnce`. Any other value is rejected before writing, because a bad `binding-mode` makes the whole UXML asset fail to import.
- Calling it again with the same `elementName` and `property` updates in place. The response `action` is `added` or `updated`, so re-running is safe.
- `extraAttributes` is a JSON object (for example `{"update-trigger":"OnSourceChanged"}`) written verbatim onto the `<DataBinding>` node. The skill does not validate these attributes, and a wrong name breaks the asset import. Use only attributes you have confirmed.
- Binding a C# data source needs `[CreateProperty]` on the source property (`Unity.Properties`). The skill writes markup only; it does not create or check the data source type.

## World-space panels

The underlying component depends on the editor version. The response's `component` field says which one was used:

- Unity 6000.5+: a `PanelRenderer` component (`UnityEngine.UIElements.PanelRenderer`, a `Renderer` subclass).
- Unity 6000.2 to 6000.4: a `UIDocument` with its world-space properties set.

Both accept the same parameters:

- `sizeMode`: `Dynamic` or `Fixed`. `Fixed` uses `worldSpaceSizeX` and `worldSpaceSizeY`.
- `pivot`: `Center`, `TopLeft`, `TopCenter`, `TopRight`, `LeftCenter`, `RightCenter`, `BottomLeft`, `BottomCenter` or `BottomRight`.
- `pivotReferenceSize`: `BoundingBox` or `Layout`.

An unsupported value is rejected with the valid list.

World-space rendering also needs the linked `PanelSettings` in world-space render mode. `setPanelRenderMode` defaults to `true`, which switches that asset (snapshotted and undoable); the response reports `panelRenderModeSetToWorldSpace`. Pass `false` to leave the asset alone.

## UXML upgrade

`uitk_uxml_upgrade` drives `UnityEditor.UIElements.UxmlUpgradeService`. Unity documents the service from 6000.3, but it is missing from some 6000.3 builds (6000.3.9f1 ships without it). The skill binds it by reflection and returns `SEMANTIC_INVALID` when it is absent, so being on 6000.3 or later does not guarantee the call runs.

Call with `listOnly=true` first. It reports whether the service exists and which upgraders are registered and enabled. The set is not fixed, and packages can register their own. Then pass `upgraderNames` (comma-separated) to run a subset, or omit it to run every enabled upgrader.

The response reports `changed: true` or `false` per asset by comparing the `.uxml` text before and after.

## Limitations

| Not available | Why | Do this instead |
|---------------|-----|-----------------|
| Binding converters and `update-trigger` as first-class parameters | Unity's Scripting API and manual do not document the UXML attribute names, and a guessed one breaks the asset import | Pass confirmed attributes through `extraAttributes` on `uitk_runtime_binding_add` |
| Removing one runtime binding | No dedicated skill | Roll back through the workflow history, or rewrite the element with `uitk_write_file` |
| Building a `VisualElementReference` or `AuthoringIdPath` on an asset | These runtime types are set through the Inspector on a MonoBehaviour field; no editor API authors one into a scene or prefab | Use `uitk_element_reference_get` to find the `authoring-id` path, then assign the reference in the Inspector or in your C# |
| Authoring `authoring-id` values | The skill reads existing attributes; it does not assign new ones | Author them in UI Builder (Unity 6000.5+) |
| World-space UI below Unity 6000.2 | `UIDocument` gained world-space properties in 6000.2, and `PanelRenderer` exists only from 6000.5 | Use a UGUI world-space Canvas (the [ui](../../ui/MODULE.md) module) or `xr_setup_ui_canvas` |
| Runtime data binding below Unity 6000.0 | `DataBinding` and `BindingMode` do not exist in 2022.3 | Use the `binding-path` SerializedObject binding for editor UI |

`uitk_element_reference_get` follows `<Template src="...">` declarations into other `.uxml` files, up to `maxTemplateDepth` (default 3), with cycle protection. Templates it cannot resolve are listed in `unresolvedTemplates`.
