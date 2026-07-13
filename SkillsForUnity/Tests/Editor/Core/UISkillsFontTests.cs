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
#if !UNITY_6000_0_OR_NEWER
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
#endif

        [Test]
        public void CustomFont_ContainsEveryFixedUiCharacter()
        {
#if UNITY_6000_0_OR_NEWER
            var font = AssetDatabase.LoadAssetAtPath<Font>(UISkillsFont.TtfPath);
            Assert.That(font, Is.Not.Null);
#else
            var fontAsset = AssetDatabase.LoadAssetAtPath<FontAsset>(UISkillsFont.FontAssetPath);
#endif
            var characters = UISkillsFontAssetBaker.CollectUiCharacters();
            var missing = characters
#if UNITY_6000_0_OR_NEWER
                .Where(value => !font.HasCharacter(value))
#else
                .Where(value => !fontAsset.HasCharacter(value, false, false))
#endif
                .Distinct()
                .ToArray();

            Assert.That(missing, Is.Empty,
                "Missing fixed UI characters: " + string.Join(" ",
                    missing.Select(value => $"{value} (U+{(int)value:X4})")));
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
