# Unity UI Toolkit Skills

UI Toolkit only: UXML for structure, USS for styling, `UIDocument` for scene attachment, and `PanelSettings` for runtime rendering. Parameters come from the schema (`GET /skills/schema?names=<skill>&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Authoring runtime or editor UI with UI Toolkit
- Writing USS or UXML
- Attaching a UIDocument, or binding UI to data
- Building world-space UI

## Rules
- Requires Unity 2022.3+. Do not mix these skills with the `ui_*` UGUI skills; see the [ui](../ui/MODULE.md) module.
- Visible text (labels, placeholders, buttons) follows the user's language. USS class names and CSS variables stay English.
- USS is not full CSS. `display:grid`, `box-shadow`, `calc()`, `@media`, `::before`, `z-index` and gradients are unsupported. See [authoring-notes](reference/authoring-notes.md) and the [USS reference](reference/uss-reference.md).
- `uitk_write_file` is not a general file writer. `filePath` must end in `.uss` or `.uxml` (case-insensitive); any other extension, including none, is rejected with `SEMANTIC_INVALID`, before any folder is created. To write a `.cs` file, use `script_create`.
- `uitk_create_editor_window` and `uitk_create_runtime_ui` write a `.cs` file and trigger a domain reload. They are refused under Approval and Auto, and a `?mode=transactional` batch that contains either is rejected with `400` before it runs. Call them outside a transactional batch.
- Version-gated skills and parameters are listed in [version-gates](reference/version-gates.md). Read the minimum Unity version first.
- `binding-path` on `uitk_add_element` and `uitk_modify_element` is the old SerializedObject editor binding, not runtime data binding. Use `uitk_runtime_binding_add` for runtime binding.
- For a world-space panel use `uitk_worldspace_panel_create`. Do not add a `PanelRenderer` with `component_add`.
- Unity 6 world-space panels: create the `PanelSettings` first, then `uitk_worldspace_panel_create` (not `uitk_create_document`).
- Attach an existing UXML to a scene object with `uitk_create_document` or `uitk_set_document`. For a starter layout, use `uitk_create_from_template`.

## Pitfalls
- `uitoolkit_create_button`, `uitoolkit_create_label` and `uitoolkit_create_canvas` do not exist. Use `uitk_add_element`, and `uitk_create_document` (UI Toolkit has no Canvas).
- `uitoolkit_set_style` does not exist. Use `uitk_add_uss_rule`, `uitk_remove_uss_rule` or `uitk_modify_element`.
- `uitk_*` and `ui_*` are different systems. Do not carry UI Toolkit structure or styling assumptions into UGUI, or the reverse.

## Skill Groups

| Group | Skills |
|-------|--------|
| Files | `uitk_create_uss`, `uitk_create_uxml`, `uitk_read_file`, `uitk_write_file`, `uitk_delete_file`, `uitk_find_files`, `uitk_create_batch` |
| Scene | `uitk_create_document`, `uitk_set_document`, `uitk_create_panel_settings`, `uitk_get_panel_settings`, `uitk_set_panel_settings`, `uitk_list_documents`, `uitk_inspect_document` |
| UXML structure | `uitk_add_element`, `uitk_remove_element`, `uitk_modify_element`, `uitk_clone_element`, `uitk_inspect_uxml` |
| USS styling | `uitk_add_uss_rule`, `uitk_remove_uss_rule`, `uitk_list_uss_variables` |
| Templates and code | `uitk_create_from_template`, `uitk_create_editor_window`, `uitk_create_runtime_ui` |
| Unity 6 (version-gated) | `uitk_runtime_binding_add`, `uitk_runtime_binding_list`, `uitk_uxml_upgrade`, `uitk_worldspace_panel_create`, `uitk_worldspace_panel_get`, `uitk_element_reference_get` |

Starter templates for `uitk_create_from_template`: `menu`, `hud`, `dialog`, `settings`, `inventory`, `list`, `tab-view`, `toolbar`, `card` and `notification`.

Delete skills (`uitk_delete_file`, `uitk_remove_element`, `uitk_remove_uss_rule`) are refused under Approval and Auto; the schema's `approvalBehavior` shows how each is gated.

## Workflow
1. Create the USS and UXML files, then attach them with `uitk_create_document`.
2. Build design tokens first, then component rules, then layout containers.
3. For incremental edits, read with `uitk_read_file`, edit, then write with `uitk_write_file`. For 2 or more new files, use `uitk_create_batch`.
4. Runtime data binding: create the element first, add one `uitk_runtime_binding_add` per bound property, then check with `uitk_runtime_binding_list`.
5. Use `uitk_create_from_template` for a fast starter screen; use `uitk_add_element` and `uitk_add_uss_rule` for targeted edits.

## Example

```
curl -s "http://localhost:<port>/skill/uitk_create_panel_settings?expectProject=<project>" -d '{"savePath":"Assets/UI/GamePanel.asset","scaleMode":"ScaleWithScreenSize","referenceResolutionX":1920,"referenceResolutionY":1080}'
curl -s "http://localhost:<port>/skill/uitk_create_uss?expectProject=<project>" -d '{"savePath":"Assets/UI/HUD.uss","content":":root { --accent: #E8632B; } .title { color: var(--accent); }"}'
curl -s "http://localhost:<port>/skill/uitk_create_uxml?expectProject=<project>" -d '{"savePath":"Assets/UI/HUD.uxml","content":"<engine:UXML xmlns:engine=\"UnityEngine.UIElements\"><Style src=\"HUD.uss\" /><engine:Label class=\"title\" text=\"Start\" /></engine:UXML>"}'
curl -s "http://localhost:<port>/skill/uitk_create_document?expectProject=<project>" -d '{"name":"HUD","uxmlPath":"Assets/UI/HUD.uxml","panelSettingsPath":"Assets/UI/GamePanel.asset"}'
```

## Errors

Transport-level codes (COMPILING, RATE_LIMIT and others): [error codes](../../references/protocol-error-codes.md). Version and extension rejections come back as `SEMANTIC_INVALID` with the valid values or the required version.
