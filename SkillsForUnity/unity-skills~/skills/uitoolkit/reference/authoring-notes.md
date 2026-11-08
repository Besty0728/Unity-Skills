# UI Toolkit Authoring Notes

USS is not CSS, and the module's files must follow Unity's rules for UXML and USS. The entry [SKILL.md](../SKILL.md) links here for these details.

## USS vs CSS

| Pattern | Supported in USS | What to do |
|---------|------------------|------------|
| Flex layout | Yes | Use `flex-direction`, `flex-wrap`, `align-items`, `justify-content` |
| `border-radius`, `opacity`, `overflow:hidden` | Yes | Safe to use |
| Transforms and transitions | Yes | `translate`, `scale` and `rotate` work |
| CSS variables | Yes | Prefer `:root` tokens |
| `display:grid` / `display:block` / `display:inline` | No | Everything is flex; emulate grids with wrapping rows |
| `box-shadow` | No | Fake it with a nested background element |
| `linear-gradient()` / `radial-gradient()` | No | Use image textures |
| `calc()` / `@media` | No | Use explicit values and `PanelSettings.scaleMode` |
| `::before` / `::after` | No | Add a real child `VisualElement` |
| `z-index` | No | Later siblings render on top |

## Workarounds

| Need | USS-safe workaround |
|------|---------------------|
| Shadow | Extra child `VisualElement` behind the content |
| Responsive scaling | `PanelSettings.scaleMode = ScaleWithScreenSize` |
| Grid cards | `flex-direction: row`, `flex-wrap: wrap` and child widths |
| Circular avatar | Equal width and height, radius of half the size, `overflow:hidden` |
| Pseudo decoration | Extra absolutely positioned child |

## PanelSettings

- `ScaleWithScreenSize`: default for runtime HUD and menu UI.
- `ConstantPixelSize`: when strict pixel mapping matters.
- `ConstantPhysicalSize`: rare; only for physically sized UI.

## Files and Structure

- Prefer one UXML root that pulls shared tokens and styles through `<Style src="..."/>`.
- Keep USS next to its UXML when possible, so relative style references stay short.
- Run `uitk_inspect_uxml` before structural edits to a file you did not create.
- `uitk_create_uxml` adds a stylesheet reference automatically when `ussPath` is given.
- For incremental edits, read with `uitk_read_file`, edit, then write with `uitk_write_file`.
- Name a `PanelSettings` asset before attaching it: `uitk_create_document` needs a valid `PanelSettings` for runtime rendering.
- When USS and UXML share a folder, reference the stylesheet by file name (`<Style src="MyStyle.uss" />`); use a full asset path only across folders.
- Start with design tokens, then component rules, then layout containers.
- Writing USS or UXML imports that single asset through `AssetDatabase.ImportAsset`; the skills do not run a project-wide refresh. Dependent `PanelSettings` and `UIDocument` assets may still reimport.
