### qframework_set_language_defines
Replace the LocaleKit `LanguageDefineConfig.LanguageDefines` list in `Assets/QFrameworkData/LocaleKit/Resources/LanguageDefineConfig.asset`, creating the asset if needed. **Replaces the whole list**: languages left out of `languages` are dropped.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| languages | string | Yes | - | JSON array of `QFramework.Language` enum names (mirrors `UnityEngine.SystemLanguage`), e.g. `["English","ChineseSimplified"]` |

**Returns:** `error`, `languages`, `assetPath`
