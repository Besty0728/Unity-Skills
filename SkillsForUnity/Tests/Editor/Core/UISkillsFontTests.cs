using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class UISkillsFontTests
    {
        [Test]
        public void FontAsset_IsStaticAndAllRenderResourcesArePersistent()
        {
            var fontAsset = AssetDatabase.LoadAssetAtPath<FontAsset>(UISkillsFont.FontAssetPath);

            Assert.That(fontAsset, Is.Not.Null);
            Assert.That(fontAsset.atlasPopulationMode, Is.EqualTo(AtlasPopulationMode.Static));
            Assert.That(UISkillsFont.IsPersistentAndComplete(fontAsset), Is.True);
            Assert.That(AssetDatabase.GetAssetPath(fontAsset.material),
                Is.EqualTo(UISkillsFont.FontAssetPath));
            Assert.That(AssetDatabase.GetAssetPath(fontAsset.atlasTextures[0]),
                Is.EqualTo(UISkillsFont.FontAssetPath));
        }

        [Test]
        public void CustomFont_ContainsEveryFixedUiCharacter()
        {
            var fontAsset = AssetDatabase.LoadAssetAtPath<FontAsset>(UISkillsFont.FontAssetPath);
            Assert.That(fontAsset, Is.Not.Null);
            var characters = UISkillsFontAssetBaker.CollectUiCharacters();
            var missing = characters
                .Where(value => !fontAsset.HasCharacter(value, false, false))
                .Distinct()
                .ToArray();

            Assert.That(missing, Is.Empty,
                "Missing fixed UI characters: " + string.Join(" ",
                    missing.Select(value => $"{value} (U+{(int)value:X4})")));
        }

        /// <summary>
        /// 图集里每个字符都必须独占一个 glyph。
        ///
        /// 这类故障 HasCharacter 是看不见的：增量补字如果追加了字符记录却复用了 glyph 索引，
        /// 所有 HasCharacter 检查照样全过，而面板会把一个字符的字形画到另一个字符的位置上——
        /// 受影响文本被彻底画错却无声无息，现有断言一个都抓不到。两个字符共用一个 glyph 索引
        /// 就是这个故障的数值形态。
        ///
        /// 用计数而非枚举字符：图集会随每条新 UI 文案增长，所以断言的对象是"双射本身"而不是某个尺寸。
        /// </summary>
        [Test]
        public void FontAsset_MapsEveryCharacterToItsOwnGlyph()
        {
            var fontAsset = AssetDatabase.LoadAssetAtPath<FontAsset>(UISkillsFont.FontAssetPath);
            Assert.That(fontAsset, Is.Not.Null);

            var characterTable = fontAsset.characterTable;
            Assert.That(characterTable, Is.Not.Null.And.Not.Empty, "Character table is empty.");

            var duplicated = characterTable
                .GroupBy(character => character.glyphIndex)
                .Where(group => group.Count() > 1)
                .ToArray();

            Assert.That(duplicated, Is.Empty,
                $"{duplicated.Length} glyph index/indices are shared by more than one character, " +
                "so those characters render each other's shapes. Offenders: " +
                string.Join("; ", duplicated.Take(10).Select(group =>
                    $"glyph {group.Key} <- " + string.Join(", ",
                        group.Select(character => $"U+{character.unicode:X4}")))));

            Assert.That(characterTable.Select(character => character.glyphIndex).Distinct().Count(),
                Is.EqualTo(characterTable.Count),
                "Character-to-glyph mapping must be a bijection.");
        }

        [Test]
        public void Apply_UsesVersionCompatibleCustomFontAndIsIdempotent()
        {
#if UNITY_6000_0_OR_NEWER
            var expected = AssetDatabase.LoadAssetAtPath<Font>(UISkillsFont.TtfPath);
#else
            var expected = AssetDatabase.LoadAssetAtPath<FontAsset>(UISkillsFont.FontAssetPath);
#endif
            var root = new VisualElement();
            root.style.unityFontDefinition = new StyleFontDefinition(StyleKeyword.Null);

            UISkillsFont.Apply(root);
            UISkillsFont.Apply(root);

            Assert.That(root.style.unityFont.keyword, Is.EqualTo(StyleKeyword.Null));
#if UNITY_6000_0_OR_NEWER
            Assert.That(root.style.unityFontDefinition.value.font, Is.SameAs(expected));
            Assert.That(root.style.unityFontDefinition.value.fontAsset, Is.Null);
#else
            Assert.That(root.style.unityFontDefinition.value.fontAsset, Is.SameAs(expected));
            Assert.That(root.style.unityFontDefinition.value.font, Is.Null);
#endif
        }

        [Test]
        public void Apply_WithMissingCustomFont_ClearsStaleFontDefinition()
        {
            var root = new VisualElement();
            UISkillsFont.Apply(root);

#if UNITY_6000_0_OR_NEWER
            UISkillsFont.Apply(root, (Font)null);
#else
            UISkillsFont.Apply(root, (FontAsset)null);
#endif

            Assert.That(root.style.unityFont.keyword, Is.EqualTo(StyleKeyword.Null));
            Assert.That(root.style.unityFontDefinition.keyword, Is.EqualTo(StyleKeyword.Null));
            Assert.That(root.style.unityFontDefinition.value.font, Is.Null);
            Assert.That(root.style.unityFontDefinition.value.fontAsset, Is.Null);
        }

        [Test]
        public void Apply_UsesCustomFontRegardlessOfCurrentLanguage()
        {
            var saved = SkillsLocalization.Current;
            try
            {
                foreach (var language in new[]
                         {
                             SkillsLocalization.Language.English,
                             SkillsLocalization.Language.Russian,
                             SkillsLocalization.Language.Chinese
                         })
                {
                    SkillsLocalization.Current = language;
                    var root = new VisualElement();

                    UISkillsFont.Apply(root);

#if UNITY_6000_0_OR_NEWER
                    var expected = AssetDatabase.LoadAssetAtPath<Font>(UISkillsFont.TtfPath);
                    Assert.That(root.style.unityFontDefinition.value.font, Is.SameAs(expected),
                        $"Custom font must be applied for {language}");
#else
                    var expected = AssetDatabase.LoadAssetAtPath<FontAsset>(UISkillsFont.FontAssetPath);
                    Assert.That(root.style.unityFontDefinition.value.fontAsset, Is.SameAs(expected),
                        $"Custom font must be applied for {language}");
#endif
                }
            }
            finally
            {
                SkillsLocalization.Current = saved;
            }
        }

#if !UNITY_6000_0_OR_NEWER
        [Test]
        public void AppliedFontAsset_SurvivesImmediateUnusedAssetCleanup()
        {
            var root = new VisualElement();
            UISkillsFont.Apply(root);

            EditorUtility.UnloadUnusedAssetsImmediate();

            var fontAsset = root.style.unityFontDefinition.value.fontAsset;
            Assert.That(fontAsset, Is.Not.Null);
            Assert.That(fontAsset.material, Is.Not.Null);
            Assert.That(fontAsset.atlasTextures[0], Is.Not.Null);
            Assert.That(fontAsset.material.mainTexture, Is.SameAs(fontAsset.atlasTextures[0]));
        }
#endif

        [Test]
        public void Stylesheets_DoNotRequestSyntheticBold()
        {
            var paths = new[]
            {
                "Packages/com.besty.unity-skills/Editor/UI/UnitySkillsWindow.uss",
                "Packages/com.besty.unity-skills/Editor/UI/AuditLogWindow.uss",
                "Packages/com.besty.unity-skills/Editor/UI/AllowlistPickerWindow.uss",
                "Packages/com.besty.unity-skills/Editor/UI/UnityCliWindow.uss",
            };

            foreach (var path in paths)
            {
                Assert.That(File.ReadAllText(path), Does.Not.Contain("-unity-font-style: bold;"),
                    $"All UnitySkills text should use the bundled font's native Regular weight: {path}");
            }
        }
    }
}

// Producer:Betsy
