---
name: unity-uitoolkit-uss-reference
description: "Extended UI Toolkit USS reference — design tokens, reusable layout/component patterns, and an end-to-end USS example beyond the uitoolkit SKILL.md. UI Toolkit USS 扩展参考(设计令牌、可复用布局/组件模式、超出 uitoolkit SKILL.md 的端到端 USS 示例)。"
type: reference
---

# UI Toolkit USS Reference

Load this file when you need deeper USS design material, reusable layout/component patterns, or a full end-to-end example. The main `SKILL.md` keeps only routing rules and guardrails.

## Design Tokens

```css
:root {
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

    --space-xs: 4px;
    --space-sm: 8px;
    --space-md: 16px;
    --space-lg: 24px;
    --space-xl: 32px;
    --space-2xl: 48px;

    --radius-sm: 4px;
    --radius-md: 8px;
    --radius-lg: 16px;
    --radius-full: 9999px;

    --font-xs: 11px;
    --font-sm: 12px;
    --font-md: 14px;
    --font-lg: 18px;
    --font-xl: 24px;
    --font-2xl: 36px;
    --font-3xl: 48px;
}
```

## Property Quick Reference

### Flex Layout

```css
.container {
    display: flex;
    flex-direction: row;
    flex-wrap: wrap;
    flex-grow: 1;
    flex-shrink: 0;
    flex-basis: auto;
    align-items: center;
    justify-content: space-between;
}
```

### Box Model

```css
.element {
    width: 200px;
    height: 100px;
    min-width: 50px;
    max-width: 500px;
    margin: 8px;
    padding: 16px;
    border-width: 1px;
    border-color: #333;
    border-radius: 4px;
}
```

### Text

```css
.text {
    font-size: 16px;
    color: #E0E0E0;
    -unity-font-style: bold;
    -unity-text-align: middle-center;
    white-space: normal;
    text-overflow: ellipsis;
    text-shadow: 2px 2px 4px rgba(0,0,0,0.3);
    -unity-text-outline-width: 1px;
    -unity-text-outline-color: #000;
}
```

### Background, Position, Transform

```css
.panel {
    background-color: rgba(0,0,0,0.5);
    background-image: url("Assets/UI/icon.png");
    -unity-background-scale-mode: scale-to-fit;
    border-radius: 8px;
    overflow: hidden;
}

.overlay {
    position: absolute;
    top: 10px;
    left: 20px;
    right: 10px;
    bottom: 0;
    translate: 50% 0;
}

.interactive {
    translate: 10px 20px;
    scale: 1.1 1.1;
    rotate: 15deg;
    transition-property: background-color, scale, translate, opacity, border-color;
    transition-duration: 0.2s;
    transition-timing-function: ease-out;
}
```

## Layout Patterns

### Card Grid

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

### Navigation Bar

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

### Hero Section

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

### Sidebar + Content

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

## Component Patterns

### Icon Circle

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
```

### Tag / Badge / Pill

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

### Button Variants

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
```

### Feature Card

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
```

## Visual Effects

### Hover Lift and Click Pulse

```css
.card {
    translate: 0 0;
    transition-property: translate;
    transition-duration: 0.2s;
}

.card:hover { translate: 0 -4px; }

.btn {
    scale: 1 1;
    transition-property: scale;
    transition-duration: 0.1s;
}

.btn:active { scale: 0.95 0.95; }
```

### Text Glow / Outline

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

### Fake Box Shadow Pattern

```xml
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
    top: 4px;
    left: 2px;
    right: 2px;
    bottom: 0;
    background-color: rgba(0,0,0,0.08);
    border-radius: 14px;
}

.card-content {
    background-color: var(--color-surface);
    border-radius: var(--radius-lg);
    padding: var(--space-lg);
}
```

## UXML Elements Quick Reference

```xml
<engine:VisualElement name="root" class="my-class" />
<engine:ScrollView mode="Vertical" name="scroll" />
<engine:GroupBox label="Section Title" />
<engine:Foldout text="Advanced" value="false" />
<engine:Button text="Click Me" name="btn" />
<engine:Label text="Hello World" name="my-label" />
<engine:TextField label="Name:" value="default" name="input" />
<engine:Toggle label="Enable" value="true" name="toggle" />
<engine:Slider label="Volume" low-value="0" high-value="1" value="0.8" />
<engine:DropdownField label="Quality" choices="Low,Medium,High" value="Medium" />
<engine:IntegerField label="Count" value="0" />
<engine:Vector3Field label="Position" />
<Style src="MyStyle.uss" />
```

## End-to-End Example

```python
import unity_skills

unity_skills.call_skill("uitk_create_panel_settings",
    savePath="Assets/UI/GamePanel.asset",
    scaleMode="ScaleWithScreenSize",
    referenceResolutionX=1920,
    referenceResolutionY=1080
)

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
}
.page { width: 100%; height: 100%; background-color: var(--color-bg); }
.navbar { flex-direction: row; align-items: center; justify-content: space-between; }
.card-grid { flex-direction: row; flex-wrap: wrap; }
"""
)

unity_skills.call_skill("uitk_create_uxml",
    savePath="Assets/UI/Features.uxml",
    content="""<?xml version="1.0" encoding="utf-8"?>
<engine:UXML xmlns:engine="UnityEngine.UIElements">
    <Style src="Features.uss" />
    <engine:VisualElement class="page">
        <engine:VisualElement class="navbar">
            <engine:Label text="SkillForge" />
        </engine:VisualElement>
        <engine:VisualElement class="card-grid" />
    </engine:VisualElement>
</engine:UXML>
"""
)

unity_skills.call_skill("uitk_create_document",
    name="FeaturesUI",
    uxmlPath="Assets/UI/Features.uxml",
    panelSettingsPath="Assets/UI/GamePanel.asset"
)
```
