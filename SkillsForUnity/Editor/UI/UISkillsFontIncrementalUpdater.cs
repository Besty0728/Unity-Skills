using System;
using UnityEditor;
using UnityEngine.TextCore.Text;

namespace UnitySkills
{
    /// <summary>
    /// Appends a small, explicit set of glyphs to the already-baked, persistent
    /// Static <see cref="FontAsset"/> in place. This is deliberately a separate code
    /// path from <see cref="UISkillsFontAssetBaker.Bake"/> and must stay that way.
    ///
    /// Bake() calls FontAsset.CreateFontAsset against the bundled TTF alone and rebuilds
    /// the whole atlas from scratch. The bundled TTF has no Cyrillic coverage at all,
    /// while the shipped atlas contains Cyrillic glyphs inherited from a different,
    /// historical source font. A full rebake would silently drop every Cyrillic glyph
    /// the moment it runs, no matter what character set is fed into it. Until the
    /// bundled TTF is replaced with one that covers Cyrillic, a full rebake can never be
    /// correct for this asset -- so instead of rebuilding, this type only ever calls
    /// TryAddCharacters on the FontAsset object that is already on disk. Every existing
    /// glyph (Latin, Cyrillic, or CJK) is therefore preserved unconditionally; this can
    /// add glyphs, but it can never remove or replace one.
    /// </summary>
    internal static class UISkillsFontIncrementalUpdater
    {
        /// <summary>
        /// Adds <paramref name="charactersToAdd"/> to the FontAsset at
        /// <see cref="UISkillsFont.FontAssetPath"/> in place, then restores
        /// AtlasPopulationMode.Static and isMultiAtlasTexturesEnabled = false before
        /// saving. Returns null on success, or a diagnostic message if it aborted --
        /// on any failure path the asset is left exactly as it was found, never half-done.
        /// </summary>
        internal static string AddMissingGlyphs(string charactersToAdd)
        {
            if (string.IsNullOrEmpty(charactersToAdd))
                return "No characters supplied.";

            var fontAsset = AssetDatabase.LoadAssetAtPath<FontAsset>(UISkillsFont.FontAssetPath);
            if (fontAsset == null)
                return $"FontAsset not found at {UISkillsFont.FontAssetPath}";

            if (fontAsset.atlasPopulationMode != AtlasPopulationMode.Static)
                return "Refusing to touch a FontAsset that is not already AtlasPopulationMode.Static.";

            // TryAddCharacters only packs new glyphs into a Dynamic-mode atlas. Flip the
            // mode just long enough to pack, then flip it back before anything is saved --
            // the same FontAsset, Material and Texture2D sub-assets are reused throughout,
            // nothing is recreated or reparented.
            fontAsset.atlasPopulationMode = AtlasPopulationMode.Dynamic;

            bool succeeded;
            string missingCharacters;
            try
            {
                succeeded = fontAsset.TryAddCharacters(charactersToAdd, out missingCharacters, false);
            }
            catch (Exception ex)
            {
                fontAsset.atlasPopulationMode = AtlasPopulationMode.Static;
                return $"TryAddCharacters threw: {ex.Message}";
            }

            if (!succeeded || !string.IsNullOrEmpty(missingCharacters))
            {
                // Leave the asset exactly as it was; never save a half-finished state.
                fontAsset.atlasPopulationMode = AtlasPopulationMode.Static;
                return $"TryAddCharacters did not pack every character. Missing: '{missingCharacters}'";
            }

            fontAsset.atlasPopulationMode = AtlasPopulationMode.Static;
            fontAsset.isMultiAtlasTexturesEnabled = false;

            EditorUtility.SetDirty(fontAsset);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(UISkillsFont.FontAssetPath, ImportAssetOptions.ForceUpdate);

            return null;
        }
    }
}

// Producer:Betsy
