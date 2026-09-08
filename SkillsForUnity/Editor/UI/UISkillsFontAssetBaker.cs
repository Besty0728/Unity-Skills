using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;

namespace UnitySkills
{
    internal static class UISkillsFontAssetBaker
    {
#if UNITY_SKILLS_FONT_BAKER
        [MenuItem("Tools/UnitySkills Development/Bake UI Font Asset")]
        private static void BakeFromMenu() => Bake();
#endif

        /// <summary>
        /// Rebuilds the UI FontAsset from scratch by calling FontAsset.CreateFontAsset
        /// against the bundled TTF (UnitySkillsCN-Regular.ttf) alone.
        ///
        /// This bundled TTF currently has no Cyrillic coverage, while the shipped atlas
        /// contains Cyrillic glyphs inherited from a different, historical source font.
        /// A full rebake therefore drops every Cyrillic glyph unconditionally, no matter
        /// what CollectUiCharacters() returns -- so a full rebake can never be correct
        /// for this asset until the bundled TTF is replaced with one that covers Cyrillic.
        /// To add a small number of missing glyphs without rebuilding anything, use
        /// <see cref="UISkillsFontIncrementalUpdater.AddMissingGlyphs"/> instead.
        ///
        /// As a safety net against ever running this against an incomplete character
        /// collector again, this method refuses to run if the newly collected character
        /// set is not a strict superset of the existing atlas's character set (see
        /// <see cref="FindCharactersDroppedByRebake"/>).
        /// </summary>
        internal static void Bake()
        {
            var source = AssetDatabase.LoadAssetAtPath<Font>(UISkillsFont.TtfPath);
            if (source == null)
                throw new FileNotFoundException("UI font source is missing", UISkillsFont.TtfPath);

            var characters = CollectUiCharacters();

            var existingAsset = AssetDatabase.LoadAssetAtPath<FontAsset>(UISkillsFont.FontAssetPath);
            if (existingAsset != null)
            {
                var existingCharacters = new string(existingAsset.characterTable
                    .Select(entry => (char)entry.unicode).ToArray());
                var dropped = FindCharactersDroppedByRebake(existingCharacters, characters);
                if (dropped.Length > 0)
                {
                    var sample = dropped.Take(20).Select(value => $"U+{(int)value:X4}");
                    throw new System.InvalidOperationException(
                        "Refusing to rebake: the newly collected character set is not a superset " +
                        "of the existing atlas, so rebaking would silently drop " +
                        $"{dropped.Length} glyph(s) already baked in. Sample: {string.Join(" ", sample)}");
                }
            }

            var fontAsset = FontAsset.CreateFontAsset(
                source, 32, 3, GlyphRenderMode.SDFAA, 4096, 4096,
                AtlasPopulationMode.Dynamic, false);
            if (fontAsset == null)
                throw new System.InvalidOperationException("TextCore failed to create the UI FontAsset.");

            fontAsset.name = "UnitySkillsCN UI";

            if (!fontAsset.TryAddCharacters(characters, out var missing, false))
                throw new System.InvalidOperationException(
                    $"UI font atlas is too small or the source font is missing characters: {missing}");

            if (AssetDatabase.LoadAssetAtPath<Object>(UISkillsFont.FontAssetPath) != null)
                AssetDatabase.DeleteAsset(UISkillsFont.FontAssetPath);
            AssetDatabase.CreateAsset(fontAsset, UISkillsFont.FontAssetPath);

            foreach (var texture in fontAsset.atlasTextures.Where(texture => texture != null))
            {
                texture.name = "UnitySkillsCN UI Atlas";
                AssetDatabase.AddObjectToAsset(texture, fontAsset);
            }

            if (fontAsset.material != null)
            {
                fontAsset.material.name = "UnitySkillsCN UI Material";
                AssetDatabase.AddObjectToAsset(fontAsset.material, fontAsset);
            }

            fontAsset.atlasPopulationMode = AtlasPopulationMode.Static;
            fontAsset.isMultiAtlasTexturesEnabled = false;
            EditorUtility.SetDirty(fontAsset);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(UISkillsFont.FontAssetPath, ImportAssetOptions.ForceUpdate);
            Debug.Log($"[UnitySkills] Baked {characters.Length} UI characters to {UISkillsFont.FontAssetPath}");
        }

        /// <summary>
        /// Collects every character actually used by UI copy. This must cover
        /// Editor/Locales/*.json -- that is the real source of user-facing strings today
        /// (Localization.cs and Editor/UI/**.{cs,uxml,uss} are scanned too for any text
        /// that still lives in code or markup). U+26A0 (warning sign) stays excluded: the
        /// topbar renders status via a vector shape rather than that glyph, see
        /// topbar UI decisions.
        /// </summary>
        internal static string CollectUiCharacters()
        {
            var paths = new List<string>
            {
                "Packages/com.besty.unity-skills/Editor/Skills/Localization.cs"
            };
            paths.AddRange(Directory.GetFiles(
                "Packages/com.besty.unity-skills/Editor/UI", "*.*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".cs") || path.EndsWith(".uxml") || path.EndsWith(".uss")));
            paths.AddRange(Directory.GetFiles(
                "Packages/com.besty.unity-skills/Editor/Locales", "*.json", SearchOption.TopDirectoryOnly));

            var chars = new HashSet<char>();
            for (var value = 32; value <= 126; value++)
                chars.Add((char)value);

            foreach (var path in paths)
            {
                foreach (var value in File.ReadAllText(path, Encoding.UTF8))
                {
                    if (!char.IsControl(value) && !char.IsSurrogate(value) && value != '\u26A0')
                        chars.Add(value);
                }
            }

            return new string(chars.OrderBy(value => value).ToArray());
        }

        /// <summary>
        /// Returns the characters present in <paramref name="existingCharacters"/> but
        /// absent from <paramref name="newCharacters"/> -- i.e. the glyphs a rebake with
        /// newCharacters would silently drop from the atlas. An empty result means
        /// newCharacters is a superset of existingCharacters and a rebake would not lose
        /// any character coverage. Pure and side-effect free so tests can exercise the
        /// superset check with injected fake character sets, without touching the real
        /// FontAsset.
        /// </summary>
        internal static char[] FindCharactersDroppedByRebake(string existingCharacters, string newCharacters)
        {
            var kept = new HashSet<char>(newCharacters ?? string.Empty);
            return (existingCharacters ?? string.Empty)
                .Where(value => !kept.Contains(value))
                .Distinct()
                .OrderBy(value => value)
                .ToArray();
        }
    }
}

// Producer:Betsy
