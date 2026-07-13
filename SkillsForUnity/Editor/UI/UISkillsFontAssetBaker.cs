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

        internal static void Bake()
        {
            var source = AssetDatabase.LoadAssetAtPath<Font>(UISkillsFont.TtfPath);
            if (source == null)
                throw new FileNotFoundException("UI font source is missing", UISkillsFont.TtfPath);

            var fontAsset = FontAsset.CreateFontAsset(
                source, 32, 3, GlyphRenderMode.SDFAA, 2048, 2048,
                AtlasPopulationMode.Dynamic, false);
            if (fontAsset == null)
                throw new System.InvalidOperationException("TextCore failed to create the UI FontAsset.");

            fontAsset.name = "UnitySkillsCN UI";

            var characters = CollectUiCharacters();
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

        internal static string CollectUiCharacters()
        {
            var paths = new List<string>
            {
                "Packages/com.besty.unity-skills/Editor/Skills/Localization.cs"
            };
            paths.AddRange(Directory.GetFiles(
                "Packages/com.besty.unity-skills/Editor/UI", "*.*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".cs") || path.EndsWith(".uxml") || path.EndsWith(".uss")));

            var chars = new HashSet<char>();
            for (var value = 32; value <= 126; value++)
                chars.Add((char)value);

            foreach (var path in paths)
            {
                foreach (var value in File.ReadAllText(path, Encoding.UTF8))
                {
                    if (!char.IsControl(value) && !char.IsSurrogate(value))
                        chars.Add(value);
                }
            }

            return new string(chars.OrderBy(value => value).ToArray());
        }
    }
}

// Producer:Betsy
