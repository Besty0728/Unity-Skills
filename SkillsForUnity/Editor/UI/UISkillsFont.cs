using System;
using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;
using UnityEngine.TextCore.Text;

namespace UnitySkills
{
    /// <summary>
    /// Forces the UnitySkills editor windows to render text with a bundled CJK font
    /// instead of the editor's shared default font.
    ///
    /// Why: on macOS, the Unity editor's default UI Toolkit text path rasterizes CJK
    /// glyphs on demand into a single *shared* dynamic font atlas. When that atlas has
    /// to grow/repack, individual glyphs can come back with a stale/blank UV rect and
    /// render as empty advances — so a handful of common characters (e.g. 局/更/卸/定)
    /// silently disappear while everything else looks fine. It is glyph-specific and
    /// stable-per-session, not a style/bold/truncation issue.
    ///
    /// Fix: build a dedicated <see cref="FontAsset"/> from our bundled, subsetted
    /// Maple Mono CN (OFL 1.1) TTF. A dedicated FontAsset gets its OWN multi-atlas
    /// sized for just this panel's glyphs, so it never hits the shared-atlas
    /// contention that triggers the drop. Assigned to the window root via
    /// <c>unityFontDefinition</c>, which is an inherited property, so every label in
    /// the window picks it up. Glyphs the font lacks (emoji, rare Han) still fall back
    /// to the editor defaults.
    /// </summary>
    internal static class UISkillsFont
    {
        private const string TtfPath =
            "Packages/com.besty.unity-skills/Editor/UI/Fonts/UnitySkillsCN-Regular.ttf";

        private static FontAsset _cjkFont;
        private static bool _attempted;

        private static FontAsset GetFontAsset()
        {
            if (_attempted) return _cjkFont;
            _attempted = true;

            try
            {
                var font = AssetDatabase.LoadAssetAtPath<Font>(TtfPath);
                if (font == null)
                {
                    // Missing font is non-fatal: fall back to the editor default so the
                    // window still works (just with the original macOS glyph-drop quirk).
                    Debug.LogWarning($"[UnitySkills] CJK font not found, using editor default: {TtfPath}");
                    return null;
                }

                // Dynamic, multi-atlas FontAsset → own atlas, grows safely, no shared-atlas drop.
                _cjkFont = FontAsset.CreateFontAsset(font);
                if (_cjkFont != null)
                    _cjkFont.hideFlags = HideFlags.DontSave; // runtime-only, never persisted
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnitySkills] Failed to build CJK FontAsset: {ex.Message}");
                _cjkFont = null;
            }

            return _cjkFont;
        }

        /// <summary>
        /// Apply the bundled CJK font to a window's root element. Safe to call on every
        /// window; the FontAsset is built once and shared. No-op if the font is missing.
        /// </summary>
        public static void Apply(VisualElement root)
        {
            if (root == null) return;
            var fa = GetFontAsset();
            if (fa == null) return;
            root.style.unityFontDefinition = new StyleFontDefinition(fa);
        }
    }
}
