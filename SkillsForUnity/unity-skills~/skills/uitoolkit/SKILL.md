---
name: unity-uitoolkit
description: "UI Toolkit (UITK) for Unity — create/edit USS stylesheets and UXML layouts, configure UIDocument in scenes. Triggers: UI Toolkit, UITK, UXML, USS, UIDocument, PanelSettings, VisualElement, stylesheet, runtime UI, EditorWindow UI, 界面工具包, UI样式, 样式表, 可视化元素."
---

# Unity UI Toolkit Skills

Use this module for Unity UI Toolkit only: `UXML` for structure, `USS` for styling, `UIDocument` for scene attachment, and `PanelSettings` for runtime rendering.

> **Requires Unity 2022.3+**. Do not mix this module with `ui_*` UGUI/Canvas skills.
> **Localization**: Match visible UI text to the user's language. Chinese conversation -> Chinese labels/placeholders/button text. USS class names and CSS variables stay English.

## Guardrails

**Mode**: Full-Auto required

**DO NOT** (common hallucinations):
- `uitoolkit_create_button` / `uitoolkit_create_label` do not exist -> use `uitk_add_element`
- `uitoolkit_set_style` does not exist -> use `uitk_add_uss_rule`, `uitk_remove_uss_rule`, or `uitk_modify_element`
- `uitoolkit_create_canvas` does not exist -> UI Toolkit uses `UIDocument`, not Canvas
- `uitk_*` and `ui_*` are different systems. Do not mix UI Toolkit structure/styling assumptions into UGUI workflows
- USS is **not full CSS**. `display:grid`, `box-shadow`, `calc()`, `@media`, `::before`, `z-index`, and gradients are unsupported

**Routing**:
- For UGUI Canvas/Button/Text/Image -> use the `ui` module
- For XR world-space Canvas conversion -> use `xr_setup_ui_canvas`
- For generated starter layouts -> use `uitk_create_from_template`
- For attaching an existing UXML to a scene object -> use `uitk_create_document` or `uitk_set_document`

## Skills

### File Skills

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `uitk_create_uss` | Create USS file | `savePath`, `content?` |
| `uitk_create_uxml` | Create UXML file | `savePath`, `content?`, `ussPath?` |
| `uitk_read_file` | Read USS/UXML content | `filePath` |
| `uitk_write_file` | Overwrite file content | `filePath`, `content` |
| `uitk_delete_file` | Delete USS/UXML file | `filePath` |
| `uitk_find_files` | Search files by name/path | `type?`, `folder?`, `filter?`, `limit?` |
| `uitk_create_batch` | Create 2+ files in one call | `items` |

### Scene Skills

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `uitk_create_document` | Create `UIDocument` GameObject | `name`, `uxmlPath?`, `panelSettingsPath?`, `sortOrder?`, `parentName?`/`parentInstanceId?`/`parentPath?` |
| `uitk_set_document` | Change UIDocument asset bindings | `name`/`instanceId`, `uxmlPath?`, `panelSettingsPath?` |
| `uitk_create_panel_settings` | Create PanelSettings asset | `savePath`, `scaleMode`, `referenceResolutionX/Y`, Unity 6 world-space options |
| `uitk_get_panel_settings` | Read PanelSettings values | `assetPath` |
| `uitk_set_panel_settings` | Update PanelSettings selectively | `assetPath`, changed fields only |
| `uitk_list_documents` | List scene UIDocuments | none |
| `uitk_inspect_document` | Inspect live VisualElement tree | `name`/`instanceId`/`path`, `depth` |

### UXML Structure Skills

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `uitk_add_element` | Add a child element | `filePath`, `elementType`, `parentName?`, `elementName?`, `text?`, `classes?` |
| `uitk_remove_element` | Remove by `name` | `filePath`, `elementName` |
| `uitk_modify_element` | Change attributes/classes/text | `filePath`, `elementName`, `text?`, `classes?`, `style?`, `newName?`, `bindingPath?`, custom attribute fields |
| `uitk_clone_element` | Duplicate an element subtree | `filePath`, `elementName`, `newName?` |
| `uitk_inspect_uxml` | Parse UXML hierarchy | `filePath`, `depth?` |

### USS Style Skills

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `uitk_add_uss_rule` | Add or replace selector rule | `filePath`, `selector`, `properties` |
| `uitk_remove_uss_rule` | Remove selector rule | `filePath`, `selector` |
| `uitk_list_uss_variables` | Inspect design tokens / `var()` usage | `filePath` |

### Template and CodeGen Skills

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `uitk_create_from_template` | Generate paired UXML+USS | `template`, `savePath`, `name?` |
| `uitk_create_editor_window` | Generate EditorWindow script | `savePath`, `className`, `uxmlPath?`, `ussPath?`, `menuPath?` |
| `uitk_create_runtime_ui` | Generate runtime MonoBehaviour query scaffold | `savePath`, `className`, `elementQueries?` |

Supported starter templates include `menu`, `hud`, `dialog`, `settings`, `inventory`, `list`, `tab-view`, `toolbar`, `card`, and `notification`.

## Core Domain Knowledge

### USS vs CSS

| Pattern | Supported in USS | What to do |
|---------|------------------|------------|
| Flex layout | Yes | Use `flex-direction`, `flex-wrap`, `align-items`, `justify-content` |
| `border-radius`, `opacity`, `overflow:hidden` | Yes | Safe to use |
| Transforms / transitions | Yes | `translate`, `scale`, `rotate` work |
| CSS variables | Yes | Prefer `:root` tokens |
| `display:grid` / `display:block` / `display:inline` | No | Everything is flex; emulate grids with wrapping rows |
| `box-shadow` | No | Fake with nested background element |
| `linear-gradient()` / `radial-gradient()` | No | Use image textures |
| `calc()` / `@media` | No | Use explicit values + `PanelSettings.scaleMode` |
| `::before` / `::after` | No | Add a real child `VisualElement` |
| `z-index` | No | Later siblings render on top |

### Common USS workarounds

| Need | USS-safe workaround |
|------|---------------------|
| Shadow | Extra child `VisualElement` behind content |
| Responsive scaling | `PanelSettings.scaleMode = ScaleWithScreenSize` |
| Grid cards | `flex-direction: row` + `flex-wrap: wrap` + child widths |
| Circular avatar | Equal width/height + radius = half size + `overflow:hidden` |
| Pseudo decoration | Add an extra absolutely positioned child |

### High-Frequency Parameters

| Skill | Parameters you usually need first |
|-------|-----------------------------------|
| `uitk_create_panel_settings` | `savePath`, `scaleMode`, `referenceResolutionX`, `referenceResolutionY` |
| `uitk_create_document` | `name`, `uxmlPath`, `panelSettingsPath`, `sortOrder?` |
| `uitk_add_element` | `filePath`, `elementType`, `parentName?`, `elementName?`, `text?`, `classes?` |
| `uitk_modify_element` | `filePath`, `elementName`, changed attributes only |
| `uitk_add_uss_rule` | `filePath`, `selector`, `properties` |

### `PanelSettings` choices

- `ScaleWithScreenSize`: default for runtime HUD/menu UI
- `ConstantPixelSize`: use when strict pixel mapping matters
- `ConstantPhysicalSize`: rare; only for physically sized UI requirements

Unity 6 world-space flows also need the scene-side document camera setup after configuring PanelSettings.

### File and Structure Rules

- Prefer one UXML root that references shared token/style files through `<Style src="..."/>`.
- Keep USS next to UXML when possible so relative style references stay short.
- Use `uitk_inspect_uxml` before complex structural edits if you did not create the file yourself.
- `uitk_create_uxml` can auto-reference a stylesheet when `ussPath` is provided.

## Workflow Notes

1. Create USS/UXML first, then attach them through `uitk_create_document`.
2. Runtime rendering needs a valid `PanelSettings` asset.
3. When USS and UXML are in the same folder, prefer `<Style src="MyStyle.uss" />`; use a full asset path only for cross-folder references.
4. Start with design tokens, then component rules, then layout containers.
5. For incremental edits, prefer `uitk_read_file` -> edit -> `uitk_write_file`.
6. When creating 2+ files, use `uitk_create_batch`.
7. Unity 6 world-space rendering uses PanelSettings world-space options plus the scene-side `UIDocument` camera setup.
8. Use `uitk_create_from_template` when the user needs a starter screen fast; use `uitk_add_element` / `uitk_add_uss_rule` for targeted edits on existing files.

## Minimal Example

```python
import unity_skills

unity_skills.call_skill("uitk_create_panel_settings",
    savePath="Assets/UI/GamePanel.asset",
    scaleMode="ScaleWithScreenSize",
    referenceResolutionX=1920,
    referenceResolutionY=1080
)

unity_skills.call_skill("uitk_create_uss",
    savePath="Assets/UI/HUD.uss",
    content=":root { --accent: #E8632B; } .title { color: var(--accent); }"
)

unity_skills.call_skill("uitk_create_uxml",
    savePath="Assets/UI/HUD.uxml",
    content="<?xml version=\"1.0\" encoding=\"utf-8\"?><engine:UXML xmlns:engine=\"UnityEngine.UIElements\"><Style src=\"HUD.uss\" /><engine:Label class=\"title\" text=\"Start\" /></engine:UXML>"
)

unity_skills.call_skill("uitk_create_document",
    name="HUD",
    uxmlPath="Assets/UI/HUD.uxml",
    panelSettingsPath="Assets/UI/GamePanel.asset"
)
```

## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
Load `USS_REFERENCE.md` before generating non-trivial USS systems, layout patterns, component styles, or complete examples.
