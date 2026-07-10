using UnityEditor;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace UnitySkills
{
    /// <summary>
    /// Applies the package's pre-baked CJK font to UnitySkills editor windows.
    ///
    /// The FontAsset, material and atlas texture are persistent AssetDatabase objects.
    /// Runtime-created atlases are deliberately forbidden: Unity 2022 can unload their
    /// textures while TextElement meshes still reference them, causing DrawTextInfo
    /// null/missing-reference exceptions and blank glyphs.
    /// </summary>
    [InitializeOnLoad]
    internal static class UISkillsFont
    {
        internal const string TtfPath =
            "Packages/com.besty.unity-skills/Editor/UI/Fonts/UnitySkillsCN-Regular.ttf";
        internal const string FontAssetPath =
            "Packages/com.besty.unity-skills/Editor/UI/Fonts/UnitySkillsCN-UI.asset";

        private static FontAsset _cjkFont;
        private static bool _warned;

        static UISkillsFont()
        {
            EditorApplication.delayCall += ReapplyToOpenWindows;
        }

        private static FontAsset GetFontAsset()
        {
            if (_cjkFont != null)
                return _cjkFont;

            var candidate = AssetDatabase.LoadAssetAtPath<FontAsset>(FontAssetPath);
            if (!IsPersistentAndComplete(candidate))
            {
                if (!_warned)
                {
                    _warned = true;
                    SkillsLogger.LogWarning(
                        $"Pre-baked CJK FontAsset is missing or incomplete; using editor default: {FontAssetPath}");
                }
                return null;
            }

            _cjkFont = candidate;
            return _cjkFont;
        }

        internal static bool IsPersistentAndComplete(FontAsset fontAsset)
        {
            if (fontAsset == null || fontAsset.atlasPopulationMode != AtlasPopulationMode.Static)
                return false;
            if (fontAsset.material == null || fontAsset.atlasTextures == null ||
                fontAsset.atlasTextures.Length == 0)
                return false;
            if (AssetDatabase.GetAssetPath(fontAsset) != FontAssetPath ||
                AssetDatabase.GetAssetPath(fontAsset.material) != FontAssetPath)
                return false;

            foreach (var texture in fontAsset.atlasTextures)
            {
                if (texture == null || AssetDatabase.GetAssetPath(texture) != FontAssetPath)
                    return false;
            }

            return fontAsset.material.mainTexture == fontAsset.atlasTextures[0];
        }

        private static void ReapplyToOpenWindows()
        {
            foreach (var window in UnityEngine.Resources.FindObjectsOfTypeAll<UnitySkillsWindow>())
                Apply(window.rootVisualElement);
            foreach (var window in UnityEngine.Resources.FindObjectsOfTypeAll<UnitySkillsAuditWindow>())
                Apply(window.rootVisualElement);
            foreach (var window in UnityEngine.Resources.FindObjectsOfTypeAll<AllowlistPickerWindow>())
                Apply(window.rootVisualElement);
        }

        /// <summary>
        /// Reapplying replaces any stale runtime font definition left on a surviving
        /// EditorWindow by an older package version.
        /// </summary>
        public static void Apply(VisualElement root)
        {
            if (root == null)
                return;

            var fontAsset = GetFontAsset();
            root.style.unityFont = new StyleFont(StyleKeyword.Null);
            root.style.unityFontDefinition = fontAsset == null
                ? new StyleFontDefinition(StyleKeyword.Null)
                : new StyleFontDefinition(fontAsset);
        }
    }
}
