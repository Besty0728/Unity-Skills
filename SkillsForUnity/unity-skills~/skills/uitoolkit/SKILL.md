---
name: unity-uitoolkit
description: "UI Toolkit (UITK) for Unity — create/edit USS stylesheets and UXML layouts, configure UIDocument in scenes. Includes USS design patterns for building polished UI (cards, nav bars, badges, transitions). Use when users want to create UI with UI Toolkit, UXML, USS, UIDocument, PanelSettings, VisualElement, 界面工具包."
---

# Unity UI Toolkit Skills

Work with Unity's web-style UI system: **UXML** (structure, like HTML) + **USS** (styling, like CSS) + **UIDocument** (scene display).

> **Requires Unity 2022.3+**. This module is separate from `ui_*` skills (uGUI/Canvas). Use `uitk_*` for UI Toolkit only.

> **Localization**: Match UI text language to the user's conversation language. When the user communicates in **Chinese (中文)**, use Chinese for all UXML text attributes — labels, buttons, titles, descriptions, tags, placeholders. Otherwise default to **English**. USS class names and CSS variables always stay in English.

## Skills Overview

| Skill | Category | Description |
|-------|----------|-------------|
| `uitk_create_uss` | File | Create USS stylesheet |
| `uitk_create_uxml` | File | Create UXML layout |
| `uitk_read_file` | File | Read USS/UXML content |
| `uitk_write_file` | File | Write/overwrite USS/UXML |
| `uitk_delete_file` | File | Delete USS/UXML file |
| `uitk_find_files` | File | Search USS/UXML in project |
| `uitk_create_document` | Scene | Create UIDocument GameObject |
| `uitk_set_document` | Scene | Modify UIDocument properties |
| `uitk_create_panel_settings` | Scene | Create PanelSettings asset (full property support) |
| `uitk_get_panel_settings` | Scene | Read all PanelSettings properties |
| `uitk_set_panel_settings` | Scene | Modify existing PanelSettings properties |
| `uitk_list_documents` | Scene | List scene UIDocuments |
| `uitk_inspect_uxml` | Inspect | Parse UXML element hierarchy |
| `uitk_create_from_template` | Template | Create UXML+USS from template |
| `uitk_create_batch` | Batch | Batch create USS/UXML files |
| `uitk_add_element` | UXML | Add element to UXML file |
| `uitk_remove_element` | UXML | Remove element from UXML by name |
| `uitk_modify_element` | UXML | Modify UXML element attributes |
| `uitk_clone_element` | UXML | Clone element in UXML |
| `uitk_add_uss_rule` | USS | Add/update USS rule by selector |
| `uitk_remove_uss_rule` | USS | Remove USS rule by selector |
| `uitk_list_uss_variables` | USS | Extract CSS custom properties |
| `uitk_create_editor_window` | CodeGen | Generate EditorWindow C# script |
| `uitk_create_runtime_ui` | CodeGen | Generate runtime UI MonoBehaviour |
| `uitk_inspect_document` | Inspect | Inspect live UIDocument hierarchy |

---

## USS Design Guide

USS (Unity Style Sheets) is the styling language for UI Toolkit. It is intentionally modeled after CSS but only implements a **subset**. This guide teaches you what USS can and cannot do, so you can generate polished UI without trial-and-error.

### USS vs CSS

| Feature | CSS | USS |
|---------|-----|-----|
| Flexbox (`flex-direction`, `flex-wrap`, `align-items`, `justify-content`) | Yes | **Yes** |
| `border-radius` | Yes | **Yes** |
| `opacity` | Yes | **Yes** |
| `overflow: hidden` | Yes | **Yes** |
| `transition` (property, duration, timing, delay) | Yes | **Yes** |
| `translate`, `scale`, `rotate` | Yes | **Yes** |
| CSS custom properties (`--var` / `var()`) | Yes | **Yes** |
| Pseudo-classes (`:hover`, `:active`, `:focus`, `:checked`, `:disabled`) | Yes | **Yes** |
| `text-shadow` (offset-x offset-y blur color) | Yes | **Yes** (same syntax) |
| `position: absolute / relative` | Yes | **Yes** |
| `width`, `height` (px / %) | Yes | **Yes** |
| `display: grid` | Yes | **No** — use `flex-wrap: wrap` |
| `display: inline / block` | Yes | **No** — everything is flex |
| `box-shadow` | Yes | **No** — fake with nested elements |
| `linear-gradient()` / `radial-gradient()` | Yes | **No** — use texture PNG |
| `calc()` | Yes | **No** |
| `@media` queries | Yes | **No** |
| `::before` / `::after` pseudo-elements | Yes | **No** — add child VisualElement |
| `z-index` | Yes | **No** — use document order |
| **Unity-specific** `-unity-font-style` | — | `normal / bold / italic / bold-and-italic` |
| **Unity-specific** `-unity-text-align` | — | `upper-left / upper-center / middle-center ...` |
| **Unity-specific** `-unity-background-scale-mode` | — | `stretch-to-fill / scale-and-crop / scale-to-fit` |
| **Unity-specific** `-unity-slice-*` | — | 9-slice border for background images |
| **Unity-specific** `-unity-text-outline-width / -color` | — | Text outline / stroke effect |

> **Key mental model**: Every VisualElement is a flex container. There is no `display: block/inline/grid`. Use `flex-direction: row` + `flex-wrap: wrap` for grid-like layouts.

---

### Design Tokens

Use `:root` CSS variables to build a consistent design system. All components reference tokens instead of hard-coded values.

```css
:root {
    /* Palette */
    --color-primary: #E8632B;
    --color-primary-dark: #C9521D;
    --color-secondary: #2B7DE8;
    --color-bg: #FFF8F0;
    --color-surface: #FFFFFF;
    --color-text: #1A1A1A;
    --color-muted: #666666;
    --color-border: #E0E0E0;
    --color-success: #34C759;
    --color-danger: #FF3B30;

    /* Spacing — 8px grid */
    --space-xs: 4px;
    --space-sm: 8px;
    --space-md: 16px;
    --space-lg: 24px;
    --space-xl: 32px;
    --space-2xl: 48px;

    /* Border radius */
    --radius-sm: 4px;
    --radius-md: 8px;
    --radius-lg: 16px;
    --radius-full: 9999px;

    /* Font sizes */
    --font-xs: 11px;
    --font-sm: 12px;
    --font-md: 14px;
    --font-lg: 18px;
    --font-xl: 24px;
    --font-2xl: 36px;
    --font-3xl: 48px;
}
```

> **Tip**: Create the tokens USS first, then `<Style src="tokens.uss"/>` at the top of every UXML so all components share the same design system.

---

### USS Properties Quick Reference

#### Flex Layout
```css
.container {
    display: flex;                       /* always flex in USS */
    flex-direction: row;                 /* row | column (default: column) */
    flex-wrap: wrap;                     /* nowrap | wrap */
    flex-grow: 1;
    flex-shrink: 0;
    flex-basis: auto;
    align-items: center;                 /* flex-start | flex-end | center | stretch */
    justify-content: space-between;      /* flex-start | flex-end | center | space-between | space-around */
}
```

#### Box Model
```css
.element {
    width: 200px;    height: 100px;
    min-width: 50px; max-width: 500px;
    margin: 8px;                         /* or margin-top/right/bottom/left */
    padding: 16px;                       /* or padding-top/right/bottom/left */
    border-width: 1px;                   /* or border-top-width etc. */
    border-color: #333;
    border-radius: 4px;                  /* or border-top-left-radius etc. */
}
```

#### Text
```css
.text {
    font-size: 16px;
    color: #E0E0E0;
    -unity-font-style: bold;             /* normal | bold | italic | bold-and-italic */
    -unity-text-align: middle-center;    /* upper-left | upper-center | middle-left | middle-center ... */
    white-space: normal;                 /* nowrap | normal */
    text-overflow: ellipsis;             /* clip | ellipsis */
    text-shadow: 2px 2px 4px rgba(0,0,0,0.3);
    -unity-text-outline-width: 1px;
    -unity-text-outline-color: #000;
}
```

#### Background & Appearance
```css
.element {
    background-color: rgba(0,0,0,0.5);
    background-image: url("Assets/UI/icon.png");
    -unity-background-scale-mode: scale-to-fit;  /* stretch-to-fill | scale-and-crop | scale-to-fit */
    border-color: #4A90D9;
    border-radius: 8px;
    opacity: 0.8;
    overflow: hidden;                    /* clips children to bounds */
}
```

#### Positioning
```css
.overlay {
    position: absolute;                  /* absolute | relative (default) */
    top: 10px; left: 20px; right: 10px; bottom: 0;
    translate: 50% 0;
}
```

#### Transforms
```css
.element {
    translate: 10px 20px;
    scale: 1.1 1.1;
    rotate: 15deg;
    transform-origin: center;            /* left | center | right + top | center | bottom */
}
```

#### Pseudo-classes
```css
.btn:hover    { background-color: #555; }
.btn:active   { background-color: #333; }
.btn:focus    { border-color: #4A90D9; }
.btn:checked  { background-color: #4A90D9; }
.btn:disabled { opacity: 0.4; }
```

---

### Layout Patterns

#### Card Grid (3-column, wrapping)
```css
.card-grid {
    flex-direction: row;
    flex-wrap: wrap;
    padding: var(--space-lg);
}
.card {
    width: 30%;
    margin: 1.5%;
    padding: var(--space-lg);
    background-color: var(--color-surface);
    border-radius: var(--radius-lg);
    border-width: 1px;
    border-color: var(--color-border);
}
```
```xml
<engine:VisualElement class="card-grid">
    <engine:VisualElement class="card"> ... </engine:VisualElement>
    <engine:VisualElement class="card"> ... </engine:VisualElement>
    <engine:VisualElement class="card"> ... </engine:VisualElement>
</engine:VisualElement>
```

#### Navigation Bar
```css
.navbar {
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
    height: 56px;
    padding: 0 var(--space-lg);
    background-color: var(--color-surface);
    border-bottom-width: 1px;
    border-color: var(--color-border);
}
.nav-brand {
    font-size: var(--font-lg);
    -unity-font-style: bold;
    color: var(--color-text);
}
.nav-links {
    flex-direction: row;
}
.nav-link {
    margin-left: var(--space-md);
    padding: var(--space-sm) var(--space-md);
    color: var(--color-muted);
    font-size: var(--font-md);
}
.nav-link:hover { color: var(--color-primary); }
```
```xml
<engine:VisualElement class="navbar">
    <engine:Label class="nav-brand" text="MyApp" />
    <engine:VisualElement class="nav-links">
        <engine:Label class="nav-link" text="Home" />
        <engine:Label class="nav-link" text="Features" />
        <engine:Label class="nav-link" text="Pricing" />
    </engine:VisualElement>
    <engine:Button class="btn btn-primary" text="Sign Up" />
</engine:VisualElement>
```

#### Hero Section (centered title + subtitle)
```css
.hero {
    align-items: center;
    justify-content: center;
    padding: var(--space-2xl) var(--space-lg);
    background-color: var(--color-bg);
}
.hero-title {
    font-size: var(--font-3xl);
    -unity-font-style: bold;
    color: var(--color-text);
    -unity-text-align: upper-center;
    margin-bottom: var(--space-md);
}
.hero-subtitle {
    font-size: var(--font-lg);
    color: var(--color-muted);
    -unity-text-align: upper-center;
    max-width: 600px;
}
```

#### Sidebar + Content (two-pane)
```css
.layout-split {
    flex-direction: row;
    flex-grow: 1;
}
.sidebar {
    width: 240px;
    padding: var(--space-lg);
    background-color: var(--color-surface);
    border-right-width: 1px;
    border-color: var(--color-border);
}
.content {
    flex-grow: 1;
    padding: var(--space-lg);
}
```

---

### Component Patterns

#### Icon Circle
```css
.icon-circle {
    width: 48px;
    height: 48px;
    border-radius: 24px;
    align-items: center;
    justify-content: center;
    flex-shrink: 0;
}
.icon-circle--primary { background-color: rgba(232,99,43,0.15); }
.icon-circle--success { background-color: rgba(52,199,89,0.15); }
.icon-circle--blue    { background-color: rgba(43,125,232,0.15); }
.icon-circle Label {
    font-size: var(--font-xl);
    -unity-text-align: middle-center;
}
```
```xml
<engine:VisualElement class="icon-circle icon-circle--primary">
    <engine:Label text="&#x2b50;" />
</engine:VisualElement>
```

#### Tag / Badge / Pill
```css
.tag {
    padding: 4px 12px;
    border-radius: var(--radius-full);
    font-size: var(--font-xs);
    -unity-font-style: bold;
    -unity-text-align: middle-center;
}
.tag--outline {
    border-width: 1px;
    border-color: var(--color-primary);
    color: var(--color-primary);
    background-color: rgba(0,0,0,0);
}
.tag--filled {
    background-color: var(--color-primary);
    color: #FFFFFF;
}
```
```xml
<engine:Label class="tag tag--outline" text="Design" />
<engine:Label class="tag tag--filled" text="NEW" />
```

#### Button Variants
```css
.btn {
    padding: var(--space-sm) var(--space-lg);
    border-radius: var(--radius-md);
    font-size: var(--font-md);
    -unity-font-style: bold;
    -unity-text-align: middle-center;
    border-width: 0;
    transition-property: background-color, scale;
    transition-duration: 0.15s;
    transition-timing-function: ease-out;
}
.btn:hover  { scale: 1.02 1.02; }
.btn:active { scale: 0.98 0.98; }

.btn-primary {
    background-color: var(--color-primary);
    color: #FFFFFF;
}
.btn-primary:hover { background-color: var(--color-primary-dark); }

.btn-outline {
    background-color: rgba(0,0,0,0);
    border-width: 1px;
    border-color: var(--color-border);
    color: var(--color-text);
}
.btn-outline:hover { border-color: var(--color-primary); color: var(--color-primary); }

.btn-ghost {
    background-color: rgba(0,0,0,0);
    color: var(--color-primary);
}
.btn-ghost:hover { background-color: rgba(232,99,43,0.1); }
```

#### Feature Card (full example)

USS:
```css
.feature-card {
    padding: var(--space-lg);
    background-color: var(--color-surface);
    border-radius: var(--radius-lg);
    border-width: 1px;
    border-color: var(--color-border);
    transition-property: translate, border-color;
    transition-duration: 0.2s;
}
.feature-card:hover {
    translate: 0 -2px;
    border-color: var(--color-primary);
}
.feature-card__header {
    flex-direction: row;
    align-items: center;
    margin-bottom: var(--space-md);
}
.feature-card__title {
    font-size: var(--font-lg);
    -unity-font-style: bold;
    color: var(--color-text);
    margin-left: var(--space-md);
}
.feature-card__desc {
    font-size: var(--font-md);
    color: var(--color-muted);
    margin-bottom: var(--space-md);
    white-space: normal;
}
.feature-card__tags {
    flex-direction: row;
    flex-wrap: wrap;
}
.feature-card__tags .tag { margin-right: var(--space-sm); margin-bottom: var(--space-xs); }
```

UXML:
```xml
<engine:VisualElement class="feature-card">
    <engine:VisualElement class="feature-card__header">
        <engine:VisualElement class="icon-circle icon-circle--primary">
            <engine:Label text="&#x1f680;" />
        </engine:VisualElement>
        <engine:Label class="feature-card__title" text="Fast Iteration" />
    </engine:VisualElement>
    <engine:Label class="feature-card__desc" text="Hot-reload USS changes instantly without recompiling. Edit styles and see results in real time." />
    <engine:VisualElement class="feature-card__tags">
        <engine:Label class="tag tag--outline" text="Performance" />
        <engine:Label class="tag tag--outline" text="DX" />
    </engine:VisualElement>
</engine:VisualElement>
```

---

### Transitions & Visual Effects

#### Smooth Transitions
```css
.interactive {
    transition-property: background-color, scale, translate, opacity, border-color;
    transition-duration: 0.2s;
    transition-timing-function: ease-out;
    /* transition-delay: 0s; */
}
```
> **Supported timing functions**: `ease`, `ease-in`, `ease-out`, `ease-in-out`, `linear`, `ease-in-sine`, `ease-out-sine`, `ease-in-bounce`, `ease-out-bounce`, `ease-in-elastic`, `ease-out-elastic`, `ease-in-back`, `ease-out-back`

#### Hover Lift
```css
.card       { translate: 0 0; transition-property: translate; transition-duration: 0.2s; }
.card:hover { translate: 0 -4px; }
```

#### Scale Pulse on Click
```css
.btn         { scale: 1 1; transition-property: scale; transition-duration: 0.1s; }
.btn:active  { scale: 0.95 0.95; }
```

#### Text Shadow & Outline
```css
.title-glow {
    text-shadow: 0 0 8px rgba(232,99,43,0.6);
    color: #FFFFFF;
}
.outlined-text {
    -unity-text-outline-width: 1px;
    -unity-text-outline-color: rgba(0,0,0,0.5);
    color: #FFFFFF;
}
```

#### Fade In (opacity transition)
```css
.fade-target {
    opacity: 0;
    transition-property: opacity;
    transition-duration: 0.3s;
}
.fade-target.visible {
    opacity: 1;
}
```
> **Note**: Adding/removing USS classes at runtime via C# triggers the transition automatically.

---

### Workarounds — USS Limitations

| Need | CSS approach | USS workaround |
|------|-------------|----------------|
| Box shadow | `box-shadow: 0 2px 8px rgba(0,0,0,0.15)` | Nest a slightly larger VisualElement behind with semi-transparent `background-color` and `border-radius` |
| Gradient background | `linear-gradient(...)` | Use `background-image` pointing to a gradient texture PNG (`Assets/UI/gradient.png`) |
| Circular avatar | `border-radius: 50%` | Set equal `width`/`height` + `border-radius` = half of width (e.g., `width: 48px; border-radius: 24px;`) + `overflow: hidden` |
| Grid layout | `display: grid; grid-template-columns: repeat(3, 1fr)` | `flex-direction: row; flex-wrap: wrap;` with percentage `width` on children (e.g., `width: 33%`) |
| Text truncation | `text-overflow: ellipsis` + `overflow: hidden` | Same — USS supports `text-overflow: ellipsis` with `overflow: hidden` on the label |
| Responsive layout | `@media (max-width: ...)` | Not available — use `PanelSettings.scaleMode = ScaleWithScreenSize` for consistent scaling |
| Pseudo-elements | `::before` / `::after` | Add an extra `<engine:VisualElement>` child and style it with `position: absolute` |
| Stacking order | `z-index` | Earlier siblings render behind later siblings — reorder elements in UXML |

#### Fake Box Shadow Pattern
```xml
<!-- UXML: shadow wrapper -->
<engine:VisualElement class="shadow-wrapper">
    <engine:VisualElement class="shadow-layer" />
    <engine:VisualElement class="card-content">
        <engine:Label text="Card with shadow" />
    </engine:VisualElement>
</engine:VisualElement>
```
```css
.shadow-wrapper { padding: 4px; }
.shadow-layer {
    position: absolute;
    top: 4px; left: 2px; right: 2px; bottom: 0;
    background-color: rgba(0,0,0,0.08);
    border-radius: 14px;
}
.card-content {
    background-color: var(--color-surface);
    border-radius: var(--radius-lg);
    padding: var(--space-lg);
}
```

---

## File Operation Skills

### uitk_create_uss
Create a new USS stylesheet. If `content` is omitted, generates a default template with CSS variables.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `savePath` | string | Yes | — | Asset path, e.g. `Assets/UI/HUD.uss` |
| `content` | string | No | template | Full USS content to write |

**Returns**: `{success, path, lines}`

---

### uitk_create_uxml
Create a new UXML layout file.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `savePath` | string | Yes | — | Asset path, e.g. `Assets/UI/HUD.uxml` |
| `content` | string | No | template | Full UXML content to write |
| `ussPath` | string | No | null | USS path to embed as `<Style src="..."/>` in default template |

**Returns**: `{success, path, lines}`

---

### uitk_read_file
Read the source content of a USS or UXML file.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filePath` | string | Yes | Asset path to USS or UXML file |

**Returns**: `{path, type, lines, content}`

---

### uitk_write_file
Overwrite a USS or UXML file with new content (creates file if it doesn't exist).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filePath` | string | Yes | Asset path to write |
| `content` | string | Yes | New file content |

**Returns**: `{success, path, lines}`

---

### uitk_delete_file
Delete a USS or UXML file.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filePath` | string | Yes | Asset path to delete |

**Returns**: `{success, deleted}`

---

### uitk_find_files
Search for USS and/or UXML files in the project.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `type` | string | No | `"all"` | `"uss"`, `"uxml"`, or `"all"` |
| `folder` | string | No | `"Assets"` | Search root folder |
| `filter` | string | No | null | Substring filter on path |
| `limit` | int | No | 200 | Max results |

**Returns**: `{count, files: [{path, type, name}]}`

---

## Scene Operation Skills

### uitk_create_document
Create a new GameObject with a `UIDocument` component attached.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | `"UIDocument"` | GameObject name |
| `uxmlPath` | string | No | null | VisualTreeAsset (.uxml) path |
| `panelSettingsPath` | string | No | null | PanelSettings (.asset) path |
| `sortOrder` | int | No | 0 | Rendering sort order |
| `parentName` | string | No | null | Parent GameObject name |
| `parentInstanceId` | int | No | 0 | Parent by instance ID |
| `parentPath` | string | No | null | Parent by hierarchy path |

**Returns**: `{success, name, instanceId, hasUxml, hasPanelSettings, sortOrder}`

---

### uitk_set_document
Modify UIDocument properties on an existing scene GameObject. Adds UIDocument if not present.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | Find by GameObject name |
| `instanceId` | int | No* | 0 | Find by instance ID |
| `path` | string | No* | null | Find by hierarchy path |
| `uxmlPath` | string | No | — | New VisualTreeAsset path |
| `panelSettingsPath` | string | No | — | New PanelSettings path |
| `sortOrder` | int | No | — | New sort order |

*At least one of `name`/`instanceId`/`path` required.

**Returns**: `{success, name, instanceId, visualTreeAsset, panelSettings, sortingOrder}`

---

### uitk_create_panel_settings
Create a `PanelSettings` ScriptableObject asset with full property support.

**Core Parameters:**

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `savePath` | string | Yes | — | Asset path, e.g. `Assets/UI/Panel.asset` |
| `scaleMode` | string | No | `"ScaleWithScreenSize"` | `ConstantPixelSize`, `ConstantPhysicalSize`, `ScaleWithScreenSize` |
| `referenceResolutionX` | int | No | 1920 | Reference width (ScaleWithScreenSize) |
| `referenceResolutionY` | int | No | 1080 | Reference height (ScaleWithScreenSize) |
| `screenMatchMode` | string | No | `"MatchWidthOrHeight"` | `MatchWidthOrHeight`, `Shrink`, `Expand` |
| `themeStyleSheetPath` | string | No | null | ThemeStyleSheet asset path |

**General Properties (Unity 2022.3+):**

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `textSettingsPath` | string | null | PanelTextSettings asset path |
| `targetTexturePath` | string | null | RenderTexture asset path (render to texture) |
| `targetDisplay` | int | null | Target display 0-7 |
| `sortOrder` | float | null | Rendering sort order |
| `scale` | float | null | Panel scale factor |
| `match` | float | null | Width/height match 0-1 (ScaleWithScreenSize) |
| `referenceDpi` | float | null | Reference DPI (ConstantPhysicalSize) |
| `fallbackDpi` | float | null | Fallback DPI |
| `referenceSpritePixelsPerUnit` | float | null | Sprite pixels per unit |

**Dynamic Atlas:**

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `dynamicAtlasMinSize` | int | null | Minimum atlas size |
| `dynamicAtlasMaxSize` | int | null | Maximum atlas size |
| `dynamicAtlasMaxSubTextureSize` | int | null | Max sub-texture size |
| `dynamicAtlasFilters` | string | null | `"Everything"` / `"None"` / comma-separated: `"Readability,Size,Format,ColorSpace,FilterMode"` |

**Color Clear:**

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `clearColor` | bool | null | Enable color clear |
| `colorClearR` | float | null | Clear color red (0-1) |
| `colorClearG` | float | null | Clear color green (0-1) |
| `colorClearB` | float | null | Clear color blue (0-1) |
| `colorClearA` | float | null | Clear color alpha (0-1) |
| `clearDepthStencil` | bool | null | Enable depth/stencil clear |

**Unity 6+ Only** (ignored on older versions):

| Parameter | Type | Description |
|-----------|------|-------------|
| `renderMode` | string | `"ScreenSpaceOverlay"` / `"WorldSpace"` |
| `forceGammaRendering` | bool | Force gamma rendering |
| `bindingLogLevel` | string | Binding log level (`"None"`, `"Once"`, `"All"`) |
| `colliderUpdateMode` | string | World Space collider mode (`"Match3DBoundingBox"`, `"Match2DDocumentRect"`, `"KeepExistingCollider"`) |
| `colliderIsTrigger` | bool | World Space collider is trigger |
| `vertexBudget` | int | Vertex budget for buffer management |
| `textureSlotCount` | int | Texture slot count for buffer management (Unity 6.3+) |

**Returns**: `{success, path, scaleMode, referenceResolution, screenMatchMode}`

---

### uitk_get_panel_settings
Read all properties of a `PanelSettings` asset.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `assetPath` | string | Yes | PanelSettings asset path |

**Returns**: `{path, scaleMode, referenceResolution, screenMatchMode, themeStyleSheet, textSettings, targetTexture, targetDisplay, sortingOrder, scale, match, referenceDpi, fallbackDpi, referenceSpritePixelsPerUnit, dynamicAtlasSettings, clearColor, colorClearValue, clearDepthStencil}`

On Unity 6+ also includes: `renderMode, forceGammaRendering, bindingLogLevel, colliderUpdateMode, colliderIsTrigger, vertexBudget`. On Unity 6.3+ also includes: `textureSlotCount`

---

### uitk_set_panel_settings
Modify properties on an existing `PanelSettings` asset. Only explicitly provided parameters are changed; others remain untouched.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `assetPath` | string | Yes | PanelSettings asset path |

All other parameters are the same as `uitk_create_panel_settings` (all optional). Pass only the properties you want to change.

**Returns**: `{success, path, scaleMode, referenceResolution, screenMatchMode}`

---

### uitk_list_documents
List all UIDocument components in the active scene.

No parameters.

**Returns**: `{count, documents: [{name, instanceId, visualTreeAsset, panelSettings, sortingOrder, active}]}`

---

## Inspection Skills

### uitk_inspect_uxml
Parse a UXML file and return its element hierarchy as a tree.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `filePath` | string | Yes | — | UXML asset path |
| `depth` | int | No | 5 | Max traversal depth |

**Returns**: `{path, hierarchy: {tag, attributes, children[]}}`

---

## Template Skills

### uitk_create_from_template
Generate a paired UXML+USS from a built-in template. Files are named `{name}.uxml` and `{name}.uss` under `savePath`.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `template` | string | Yes | Template type (see below) |
| `savePath` | string | Yes | Target directory, e.g. `Assets/UI` |
| `name` | string | No | Base filename (defaults to template name) |

**Template types**:

| Template | Contents |
|----------|----------|
| `menu` | Full-screen menu with title, Play/Settings/Quit buttons |
| `hud` | Absolute-positioned HUD: minimap, score label, health bar |
| `dialog` | Modal dialog: title, message, OK/Cancel buttons |
| `settings` | Settings panel: Volume sliders, Toggle, DropdownField |
| `inventory` | 3x3 grid inventory with ScrollView |
| `list` | Scrollable item list |
| `tab-view` | Tab bar with switchable content panels |
| `toolbar` | Horizontal toolbar with buttons, separators, spacer |
| `card` | Card component with image, title, description, tags |
| `notification` | Notification/Toast stack (info, success, warning, error) |

**Returns**: `{success, template, ussPath, uxmlPath, name}`

---

## Batch Skills

### uitk_create_batch
Batch create multiple USS and UXML files in one call.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `items` | string | Yes | JSON array of file descriptors |

**Item schema**:
```json
{
  "type": "uss",
  "savePath": "Assets/UI/Menu.uss",
  "content": "...",
  "ussPath": "..."
}
```

**Returns**: `{success, totalItems, successCount, failCount, results[]}`

---

## UXML Elements Quick Reference

### Layout Containers
```xml
<engine:VisualElement name="root" class="my-class" />
<engine:ScrollView mode="Vertical" name="scroll" />
<engine:GroupBox label="Section Title" />
<engine:Foldout text="Advanced" value="false" />
<engine:TwoPaneSplitView />
```

### Text & Labels
```xml
<engine:Label text="Hello World" name="my-label" />
<engine:TextField label="Name:" value="default" name="input" />
<engine:TextField multiline="true" />
```

### Buttons & Toggle
```xml
<engine:Button text="Click Me" name="btn" />
<engine:Toggle label="Enable" value="true" name="toggle" />
<engine:RadioButton label="Option A" value="true" />
<engine:RadioButtonGroup label="Choose:">
    <engine:RadioButton label="A" />
    <engine:RadioButton label="B" />
</engine:RadioButtonGroup>
```

### Sliders & Progress
```xml
<engine:Slider label="Volume" low-value="0" high-value="1" value="0.8" name="slider" />
<engine:SliderInt label="Count" low-value="1" high-value="10" value="5" />
<engine:ProgressBar title="Loading..." value="0.5" />
<engine:MinMaxSlider min-value="0" max-value="100" low-limit="0" high-limit="100" />
```

### Dropdowns & Lists
```xml
<engine:DropdownField label="Quality" choices="Low,Medium,High" value="Medium" name="dd" />
<engine:ListView name="list-view" />
<engine:TreeView name="tree-view" />
```

### Numeric Fields
```xml
<engine:IntegerField label="Count" value="0" name="count" />
<engine:FloatField label="Speed" value="1.5" />
<engine:LongField label="ID" value="0" />
<engine:Vector2Field label="Position" />
<engine:Vector3Field label="Position" />
<engine:RectField label="Bounds" />
<engine:ColorField label="Color" value="#FF0000FF" />
```

### Style Reference in UXML
```xml
<!-- USS in same directory as UXML — use just the filename -->
<Style src="MyStyle.uss" />

<!-- USS in a different directory — use full project-relative path -->
<Style src="Assets/UI/Shared/tokens.uss" />
```

---

## End-to-End Example

Build a feature-card page with navigation bar, hero section, and 3-column card grid.

```python
import unity_skills

# 1. Create PanelSettings (required for runtime rendering)
unity_skills.call_skill("uitk_create_panel_settings",
    savePath="Assets/UI/GamePanel.asset",
    scaleMode="ScaleWithScreenSize",
    referenceResolutionX=1920,
    referenceResolutionY=1080
)

# 2. Create USS with design tokens + component styles
unity_skills.call_skill("uitk_create_uss",
    savePath="Assets/UI/Features.uss",
    content=""":root {
    --color-primary: #E8632B;
    --color-primary-dark: #C9521D;
    --color-bg: #FFF8F0;
    --color-surface: #FFFFFF;
    --color-text: #1A1A1A;
    --color-muted: #666666;
    --color-border: #E0E0E0;
    --space-sm: 8px; --space-md: 16px; --space-lg: 24px; --space-xl: 32px;
    --radius-md: 8px; --radius-lg: 16px; --radius-full: 9999px;
    --font-sm: 12px; --font-md: 14px; --font-lg: 18px; --font-xl: 24px; --font-2xl: 36px;
}
.page { width: 100%; height: 100%; background-color: var(--color-bg); }
.navbar {
    flex-direction: row; align-items: center; justify-content: space-between;
    height: 56px; padding: 0 var(--space-lg);
    background-color: var(--color-surface); border-bottom-width: 1px; border-color: var(--color-border);
}
.nav-brand { font-size: var(--font-lg); -unity-font-style: bold; color: var(--color-text); }
.nav-links { flex-direction: row; }
.nav-link { margin-left: var(--space-md); color: var(--color-muted); font-size: var(--font-md); }
.nav-link:hover { color: var(--color-primary); }
.hero { align-items: center; justify-content: center; padding: var(--space-xl); }
.hero-title { font-size: var(--font-2xl); -unity-font-style: bold; color: var(--color-text); margin-bottom: var(--space-sm); }
.hero-sub { font-size: var(--font-lg); color: var(--color-muted); }
.card-grid { flex-direction: row; flex-wrap: wrap; padding: 0 var(--space-lg); }
.card {
    width: 30%; margin: 1.5%; padding: var(--space-lg);
    background-color: var(--color-surface); border-radius: var(--radius-lg);
    border-width: 1px; border-color: var(--color-border);
    transition-property: translate, border-color; transition-duration: 0.2s;
}
.card:hover { translate: 0 -2px; border-color: var(--color-primary); }
.card__icon { width: 48px; height: 48px; border-radius: 24px; align-items: center; justify-content: center; margin-bottom: var(--space-md); }
.card__icon--orange { background-color: rgba(232,99,43,0.15); }
.card__icon--blue { background-color: rgba(43,125,232,0.15); }
.card__icon--green { background-color: rgba(52,199,89,0.15); }
.card__icon Label { font-size: var(--font-xl); -unity-text-align: middle-center; }
.card__title { font-size: var(--font-lg); -unity-font-style: bold; color: var(--color-text); margin-bottom: var(--space-sm); }
.card__desc { font-size: var(--font-md); color: var(--color-muted); white-space: normal; margin-bottom: var(--space-md); }
.card__tags { flex-direction: row; flex-wrap: wrap; }
.tag { padding: 4px 12px; border-radius: var(--radius-full); font-size: 11px; -unity-font-style: bold; border-width: 1px; border-color: var(--color-primary); color: var(--color-primary); margin-right: var(--space-sm); }
.btn { padding: var(--space-sm) var(--space-lg); border-radius: var(--radius-md); -unity-font-style: bold; border-width: 0; transition-property: background-color, scale; transition-duration: 0.15s; }
.btn:hover { scale: 1.02 1.02; }
.btn-primary { background-color: var(--color-primary); color: #FFFFFF; }
.btn-primary:hover { background-color: var(--color-primary-dark); }
"""
)

# 3. Create UXML layout referencing the USS
unity_skills.call_skill("uitk_create_uxml",
    savePath="Assets/UI/Features.uxml",
    content="""<?xml version="1.0" encoding="utf-8"?>
<engine:UXML xmlns:engine="UnityEngine.UIElements">
    <Style src="Features.uss" />
    <engine:VisualElement class="page">
        <!-- Nav Bar -->
        <engine:VisualElement class="navbar">
            <engine:Label class="nav-brand" text="SkillForge" />
            <engine:VisualElement class="nav-links">
                <engine:Label class="nav-link" text="Home" />
                <engine:Label class="nav-link" text="Features" />
                <engine:Label class="nav-link" text="Docs" />
            </engine:VisualElement>
            <engine:Button class="btn btn-primary" text="Get Started" />
        </engine:VisualElement>
        <!-- Hero -->
        <engine:VisualElement class="hero">
            <engine:Label class="hero-title" text="Build Amazing UI" />
            <engine:Label class="hero-sub" text="Powerful components for your Unity project" />
        </engine:VisualElement>
        <!-- Card Grid -->
        <engine:VisualElement class="card-grid">
            <engine:VisualElement class="card">
                <engine:VisualElement class="card__icon card__icon--orange">
                    <engine:Label text="&#x1f680;" />
                </engine:VisualElement>
                <engine:Label class="card__title" text="Fast Iteration" />
                <engine:Label class="card__desc" text="Hot-reload USS changes instantly without recompiling." />
                <engine:VisualElement class="card__tags">
                    <engine:Label class="tag" text="Performance" />
                    <engine:Label class="tag" text="DX" />
                </engine:VisualElement>
            </engine:VisualElement>
            <engine:VisualElement class="card">
                <engine:VisualElement class="card__icon card__icon--blue">
                    <engine:Label text="&#x1f3a8;" />
                </engine:VisualElement>
                <engine:Label class="card__title" text="Design Tokens" />
                <engine:Label class="card__desc" text="CSS variables for colors, spacing, and typography." />
                <engine:VisualElement class="card__tags">
                    <engine:Label class="tag" text="Theming" />
                </engine:VisualElement>
            </engine:VisualElement>
            <engine:VisualElement class="card">
                <engine:VisualElement class="card__icon card__icon--green">
                    <engine:Label text="&#x2699;" />
                </engine:VisualElement>
                <engine:Label class="card__title" text="Flex Layout" />
                <engine:Label class="card__desc" text="Familiar flexbox model for responsive card grids." />
                <engine:VisualElement class="card__tags">
                    <engine:Label class="tag" text="Layout" />
                    <engine:Label class="tag" text="Flex" />
                </engine:VisualElement>
            </engine:VisualElement>
        </engine:VisualElement>
    </engine:VisualElement>
</engine:UXML>
"""
)

# 4. Create UIDocument in scene
unity_skills.call_skill("uitk_create_document",
    name="FeaturesUI",
    uxmlPath="Assets/UI/Features.uxml",
    panelSettingsPath="Assets/UI/GamePanel.asset"
)

# 5. Read → Modify → Write workflow (change accent color)
result = unity_skills.call_skill("uitk_read_file", filePath="Assets/UI/Features.uss")
updated = result["result"]["content"].replace("#E8632B", "#6C5CE7")
unity_skills.call_skill("uitk_write_file",
    filePath="Assets/UI/Features.uss",
    content=updated
)
```

---

## UXML Element Operation Skills

### uitk_add_element
Add an element to a UXML file.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `filePath` | string | Yes | — | UXML file path |
| `elementType` | string | Yes | — | Element type: Label/Button/Toggle/Slider/TextField/VisualElement/etc. |
| `parentName` | string | No | root | Parent element name |
| `elementName` | string | No | null | New element name attribute |
| `text` | string | No | null | Text attribute |
| `classes` | string | No | null | CSS class(es) |
| `style` | string | No | null | Inline style |
| `bindingPath` | string | No | null | Binding path |

### uitk_remove_element
Remove an element from a UXML file by name.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filePath` | string | Yes | UXML file path |
| `elementName` | string | Yes | Name attribute of element to remove |

### uitk_modify_element
Modify attributes of a UXML element.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filePath` | string | Yes | UXML file path |
| `elementName` | string | Yes | Target element name |
| `text` | string | No | New text attribute |
| `classes` | string | No | New class attribute |
| `style` | string | No | New inline style |
| `newName` | string | No | Rename the element |
| `bindingPath` | string | No | New binding path |
| `setAttribute` | string | No | Custom attribute name |
| `setAttributeValue` | string | No | Custom attribute value |

### uitk_clone_element
Clone (duplicate) an element and its children in a UXML file.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `filePath` | string | Yes | — | UXML file path |
| `elementName` | string | Yes | — | Element to clone |
| `newName` | string | No | null | Name for the cloned element |

---

## USS Operation Skills

### uitk_add_uss_rule
Add or update a USS rule. If the selector already exists, it is replaced.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filePath` | string | Yes | USS file path |
| `selector` | string | Yes | CSS selector, e.g. `.my-class` or `.btn:hover` |
| `properties` | string | Yes | Semicolon-separated properties, e.g. `"background-color: #333; padding: 8px"` |

### uitk_remove_uss_rule
Remove a USS rule by its selector.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filePath` | string | Yes | USS file path |
| `selector` | string | Yes | Selector to remove |

### uitk_list_uss_variables
Extract all CSS custom property definitions and `var()` references from a USS file.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filePath` | string | Yes | USS file path |

**Returns**: `{path, definedCount, variables: [{name, value}], referencedVariables[]}`

---

## Code Generation Skills

### uitk_create_editor_window
Generate an EditorWindow C# script with UI Toolkit pattern (CreateGUI + UXML/USS binding).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `savePath` | string | Yes | — | C# script path |
| `className` | string | Yes | — | Class name |
| `windowTitle` | string | No | className | Window title |
| `uxmlPath` | string | No | null | UXML to load in CreateGUI |
| `ussPath` | string | No | null | USS to attach |
| `menuPath` | string | No | "Window/{className}" | MenuItem path |

### uitk_create_runtime_ui
Generate a runtime MonoBehaviour script with UIDocument query pattern.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `savePath` | string | Yes | — | C# script path |
| `className` | string | Yes | — | Class name |
| `elementQueries` | string | No | null | Element queries: `"Button:my-button,Label:score-label"` |

---

## Scene Inspection Skills

### uitk_inspect_document
Inspect the live VisualElement hierarchy of a UIDocument component in the scene.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | GameObject name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | — | Hierarchy path |
| `depth` | int | No | 5 | Max traversal depth |

**Returns**: `{gameObject, instanceId, hierarchy: {type, name, classes[], children[]}}`

---

## Workflow Notes

1. **File → Scene**: Create USS + UXML first, then assign to UIDocument in scene
2. **PanelSettings required**: Without PanelSettings, UIDocument won't render at runtime
3. **Style src paths**: When USS and UXML are in the **same folder**, use just the filename: `<Style src="MyStyle.uss" />`. Use a full path like `Assets/UI/tokens.uss` only when referencing a file in a different directory
4. **Design tokens first**: Create a `tokens.uss` with `:root` variables, reference it in every UXML
5. **Read-Modify-Write**: Use `uitk_read_file` → edit content → `uitk_write_file` for incremental changes
6. **Batch for efficiency**: Use `uitk_create_batch` when creating 2+ files to reduce API calls
7. **Test in Game view**: USS changes are visible in Game view immediately — no domain reload needed
8. **Localization**: Match UI text to the user's language. Chinese user → `text="开始"`, English user → `text="Start"`. Keep USS class names in English always
9. **World Space (Unity 6+)**: Set `renderMode="WorldSpace"` in PanelSettings, then configure `worldCamera` on the UIDocument component in the scene
