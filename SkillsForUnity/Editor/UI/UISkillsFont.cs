using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace UnitySkills
{
    /// <summary>
    /// Applies the package's CJK font to UnitySkills editor windows.
    ///
    /// Unity 6's Advanced Text Generator requires a dynamic font backed by the source
    /// Font. Unity 2022 instead uses the pre-baked, persistent FontAsset because it can
    /// unload runtime-created atlas resources while TextElement meshes still use them.
    /// </summary>
    [InitializeOnLoad]
    internal static class UISkillsFont
    {
        internal const string TtfPath =
            "Packages/com.besty.unity-skills/Editor/UI/Fonts/UnitySkillsCN-Regular.ttf";
        internal const string FontAssetPath =
            "Packages/com.besty.unity-skills/Editor/UI/Fonts/UnitySkillsCN-UI.asset";

#if UNITY_6000_0_OR_NEWER
        private static Font _cjkFont;
#else
        private static FontAsset _cjkFont;
#endif
        private static bool _warned;

        static UISkillsFont()
        {
            EditorApplication.delayCall += ReapplyToOpenWindows;
        }

#if UNITY_6000_0_OR_NEWER
        private static Font GetFont()
        {
            if (_cjkFont != null)
                return _cjkFont;

            _cjkFont = AssetDatabase.LoadAssetAtPath<Font>(TtfPath);
            if (_cjkFont == null)
                WarnOnce($"CJK font is missing; using editor default: {TtfPath}");

            return _cjkFont;
        }
#else
        private static FontAsset GetFontAsset()
        {
            if (_cjkFont != null)
                return _cjkFont;

            var candidate = AssetDatabase.LoadAssetAtPath<FontAsset>(FontAssetPath);
            if (!IsPersistentAndComplete(candidate))
            {
                if (!_warned)
                    WarnOnce(
                        $"Pre-baked CJK FontAsset is missing or incomplete; using editor default: {FontAssetPath}");
                return null;
            }

            _cjkFont = candidate;
            return _cjkFont;
        }
#endif

        private static void WarnOnce(string message)
        {
            if (_warned)
                return;

            _warned = true;
            SkillsLogger.LogWarning(message);
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

#if UNITY_6000_0_OR_NEWER
            Apply(root, GetFont());
#else
            Apply(root, GetFontAsset());
#endif
        }

#if UNITY_6000_0_OR_NEWER
        internal static void Apply(VisualElement root, Font font)
        {
            if (root == null)
                return;

            root.style.unityFont = new StyleFont(StyleKeyword.Null);
            root.style.unityFontDefinition = font == null
                ? new StyleFontDefinition(StyleKeyword.Null)
                : new StyleFontDefinition(FontDefinition.FromFont(font));
        }
#else
        internal static void Apply(VisualElement root, FontAsset fontAsset)
        {
            if (root == null)
                return;

            root.style.unityFont = new StyleFont(StyleKeyword.Null);
            root.style.unityFontDefinition = fontAsset == null
                ? new StyleFontDefinition(StyleKeyword.Null)
                : new StyleFontDefinition(fontAsset);
        }
#endif
    }
}

// Producer:Betsy
