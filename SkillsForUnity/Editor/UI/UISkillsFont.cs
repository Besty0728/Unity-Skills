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

        // Roots the font was applied to — re-applied after a rebuild (see PlayModeStateChanged).
        private static readonly System.Collections.Generic.List<WeakReference<VisualElement>> _appliedRoots
            = new System.Collections.Generic.List<WeakReference<VisualElement>>();

        static UISkillsFont()
        {
            // Entering/leaving Play mode runs UnloadUnusedAssets. The FontAsset itself is
            // pinned (DontSave ⇒ DontUnloadUnusedAsset) but a wedged editor state can still
            // kill the derived atlas Material (TextCore engine bug) — repaint then throws
            // MissingReferenceException from inside the render pass and the panel wedges.
            // Rebuild + re-apply on every transition so windows never repaint a dead font.
            EditorApplication.playModeStateChanged += _ => HealIfDead();
        }

        private static bool FontIsAlive()
        {
            // Unity fake-null checks: the FontAsset AND its atlas material must both be alive.
            if (_cjkFont == null) return false;
            try { return _cjkFont.material != null; }
            catch (Exception) { return false; }
        }

        private static void HealIfDead()
        {
            if (_attempted && !FontIsAlive())
            {
                _cjkFont = null;
                _attempted = false; // force a rebuild on next GetFontAsset()

                for (int i = _appliedRoots.Count - 1; i >= 0; i--)
                {
                    if (!_appliedRoots[i].TryGetTarget(out var root) || root.panel == null)
                    {
                        _appliedRoots.RemoveAt(i);
                        continue;
                    }
                    Apply(root, track: false);
                }
                SkillsLogger.LogVerbose("CJK FontAsset was destroyed (unload/domain event) — rebuilt and re-applied.");
            }
        }

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
                    SkillsLogger.LogWarning($"CJK font not found, using editor default: {TtfPath}");
                    return null;
                }

                // Dynamic, multi-atlas FontAsset → own atlas, grows safely, no shared-atlas drop.
                _cjkFont = FontAsset.CreateFontAsset(font);
                if (_cjkFont != null)
                {
                    // DontSave includes DontUnloadUnusedAsset. It MUST also go on the derived
                    // sub-objects: CreateFontAsset leaves the atlas Material/Textures at
                    // HideFlags.None, so the Play-mode UnloadUnusedAssets sweep collects them
                    // while the FontAsset survives — open windows then repaint a dead Material
                    // (MissingReferenceException mid-render) and the panel wedges into an
                    // InvalidOperationException loop. Pinning the sub-objects kills the root cause.
                    _cjkFont.hideFlags = HideFlags.DontSave;
                    if (_cjkFont.material != null)
                        _cjkFont.material.hideFlags = HideFlags.DontSave;
                    if (_cjkFont.atlasTextures != null)
                    {
                        foreach (var tex in _cjkFont.atlasTextures)
                            if (tex != null) tex.hideFlags = HideFlags.DontSave;
                    }
                }
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Failed to build CJK FontAsset: {ex.Message}");
                _cjkFont = null;
            }

            return _cjkFont;
        }

        /// <summary>
        /// Apply the bundled CJK font to a window's root element. Safe to call on every
        /// window; the FontAsset is built once and shared, and rebuilt transparently if a
        /// Play-mode transition destroyed it. No-op if the font is missing.
        /// </summary>
        public static void Apply(VisualElement root) => Apply(root, track: true);

        private static void Apply(VisualElement root, bool track)
        {
            if (root == null) return;

            if (_attempted && !FontIsAlive())
            {
                _cjkFont = null;
                _attempted = false; // self-heal: rebuild instead of handing out a corpse
            }

            var fa = GetFontAsset();
            if (fa == null) return;
            root.style.unityFontDefinition = new StyleFontDefinition(fa);

            if (track)
            {
                for (int i = 0; i < _appliedRoots.Count; i++)
                    if (_appliedRoots[i].TryGetTarget(out var existing) && ReferenceEquals(existing, root))
                        return;
                _appliedRoots.Add(new WeakReference<VisualElement>(root));
            }
        }
    }
}
