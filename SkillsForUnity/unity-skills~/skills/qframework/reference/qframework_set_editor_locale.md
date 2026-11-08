### qframework_set_editor_locale
Set QFramework's editor UI locale through `LocaleKitEditor.IsCN` (EditorPrefs key `EDITOR_CN`). Affects QFramework's own editor windows (ResKit, UIKit and others), not runtime localisation.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| isCN | bool | Yes | - | `true` for Chinese, `false` for English |

**Returns:** `isCN`, `changed`
