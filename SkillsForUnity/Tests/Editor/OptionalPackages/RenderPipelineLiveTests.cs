using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UnitySkills.Tests.OptionalPackages
{
    /// <summary>URP asset settings against a throwaway asset, so no project's active pipeline asset is touched.</summary>
    [TestFixture]
    public class UrpSkillsLiveTests : OptionalPackageTestBase
    {
        protected override string ProbeSkill => "urp_get_info";

        private static string CreateProbeAsset()
        {
            EnsureProbeFolder();
            var path = $"{ProbeFolder}/R6Urp.asset";
            var asset = ScriptableObject.CreateInstance(PackageType("UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset"));
            AssetDatabase.CreateAsset(asset, path);
            return path;
        }

        [Test]
        public void SetAssetSettings_WritesEveryFieldAndReadsItBack()
        {
            var path = CreateProbeAsset();

            // Writes private serialized fields by name; this proves all eight names are live on the installed URP.
            var result = Ok(Run("urp_set_asset_settings", new JObject
            {
                ["assetPath"] = path, ["supportsHDR"] = false, ["msaaSampleCount"] = 4, ["renderScale"] = 0.75,
                ["supportsMainLightShadows"] = false, ["supportsAdditionalLightShadows"] = true,
                ["supportsCameraDepthTexture"] = true, ["supportsCameraOpaqueTexture"] = true, ["shadowDistance"] = 33,
            }));
            Assert.That(result["asset"], Is.Not.Null, result.ToString());

            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            Assert.That(Member(asset, "supportsHDR"), Is.False);
            Assert.That(Member(asset, "msaaSampleCount"), Is.EqualTo(4));
            Assert.That((float)Member(asset, "renderScale"), Is.EqualTo(0.75f).Within(1e-5f));
            Assert.That(Member(asset, "supportsMainLightShadows"), Is.False);
            Assert.That(Member(asset, "supportsCameraDepthTexture"), Is.True);
            Assert.That(Member(asset, "supportsCameraOpaqueTexture"), Is.True);
            Assert.That((float)Member(asset, "shadowDistance"), Is.EqualTo(33f).Within(1e-4f));
        }

        [Test]
        public void SetAssetSettings_InvalidMsaa_IsRejectedBeforeAnyWrite()
        {
            var path = CreateProbeAsset();
            var before = Member(AssetDatabase.LoadMainAssetAtPath(path), "supportsHDR");

            AssertSemanticInvalid(Run("urp_set_asset_settings", new JObject
            {
                ["assetPath"] = path, ["supportsHDR"] = !(bool)before, ["msaaSampleCount"] = 3,
            }), "msaaSampleCount");
            Assert.That(Member(AssetDatabase.LoadMainAssetAtPath(path), "supportsHDR"), Is.EqualTo(before));
        }

        [Test]
        public void GetInfo_ReadsTheExplicitAsset()
        {
            var path = CreateProbeAsset();
            Ok(Run("urp_set_asset_settings", new JObject { ["assetPath"] = path, ["renderScale"] = 1.25 }));

            var info = Ok(Run("urp_get_info", new JObject { ["assetPath"] = path }));
            Assert.That(info.ToString(), Does.Contain("1.25"), info.ToString());
        }
    }

    /// <summary>Decal projector writes; the rendering layer mask is serialized differently on URP 14 and 17.</summary>
    [TestFixture]
    public class DecalSkillsLiveTests : OptionalPackageTestBase
    {
        protected override string ProbeSkill => "decal_find_all";

        [Test]
        public void CreateThenSetProperties_ReadsBackMaskScaleModeAndSize()
        {
            Ok(Run("decal_create", new JObject { ["name"] = "R6Decal", ["x"] = 1 }));
            var projector = ComponentNamed(Find("R6Decal"), "DecalProjector");
            Assert.That(projector, Is.Not.Null);

            // URP 14 serializes the mask as m_DecalLayerMask: the old write to m_RenderingLayerMask never landed there.
            var result = Ok(Run("decal_set_properties", new JObject
            {
                ["name"] = "R6Decal", ["renderingLayerMask"] = 5, ["scaleMode"] = "InheritFromHierarchy", ["size"] = "2,3,4",
            }));
            Assert.That(result["renderingLayerMask"]?.Value<uint>(), Is.EqualTo(5u), result.ToString());
            Assert.That(MaskBits(Member(projector, "renderingLayerMask")), Is.EqualTo(5u));
            Assert.That(Member(projector, "scaleMode").ToString(), Is.EqualTo("InheritFromHierarchy"));
            Assert.That((Vector3)Member(projector, "size"), Is.EqualTo(new Vector3(2, 3, 4)));

            var info = Ok(Run("decal_get_info", new JObject { ["name"] = "R6Decal" }));
            Assert.That(info["renderingLayerMask"]?.Value<uint>(), Is.EqualTo(5u), info.ToString());
        }

        [Test]
        public void SetProperties_UndeclaredScaleModeNumber_IsRejectedBeforeAnyWrite()
        {
            Ok(Run("decal_create", new JObject { ["name"] = "R6Decal" }));
            var projector = ComponentNamed(Find("R6Decal"), "DecalProjector");
            var sizeBefore = (Vector3)Member(projector, "size");

            // Enum.TryParse accepted "99"; the size in the same call used to be written before the parse.
            AssertSemanticInvalid(Run("decal_set_properties", new JObject
            {
                ["name"] = "R6Decal", ["size"] = "7,7,7", ["scaleMode"] = "99",
            }), "scaleMode");
            Assert.That((Vector3)Member(projector, "size"), Is.EqualTo(sizeBefore));
        }

        [Test]
        public void FindAll_ListsTheCreatedProjector()
        {
            Ok(Run("decal_create", new JObject { ["name"] = "R6DecalA" }));
            Ok(Run("decal_create", new JObject { ["name"] = "R6DecalB" }));

            var all = Ok(Run("decal_find_all", new JObject()));
            Assert.That(all["count"]?.Value<int>(), Is.EqualTo(2), all.ToString());
        }

        // URP 14 exposes the mask as uint, URP 17 as the RenderingLayerMask struct.
        private static uint MaskBits(object mask)
        {
            if (mask is uint bits) return bits;
            if (mask is int signed) return unchecked((uint)signed);
            var value = mask.GetType().GetProperty("value")?.GetValue(mask);
            Assert.That(value, Is.Not.Null, $"Cannot read the bits of {mask.GetType().FullName}.");
            return Convert.ToUInt32(value);
        }
    }

    [TestFixture]
    public class VolumeSkillsLiveTests : OptionalPackageTestBase
    {
        protected override string ProbeSkill => "volume_list_component_types";

        private static string CreateProfile()
        {
            EnsureProbeFolder();
            var created = Ok(Run("volume_profile_create", new JObject { ["name"] = "R6Profile", ["savePath"] = $"{ProbeFolder}/R6Profile.asset" }));
            var path = created["path"]?.ToString();
            Assert.That(path, Is.Not.Null.And.Not.Empty, created.ToString());
            return path;
        }

        [Test]
        public void AddComponentThenSetParameter_ReadsBackTheValue()
        {
            var profile = CreateProfile();
            Ok(Run("volume_add_component", new JObject { ["profilePath"] = profile, ["componentType"] = "Bloom" }));
            Ok(Run("volume_set_parameter", new JObject
            {
                ["profilePath"] = profile, ["componentType"] = "Bloom", ["parameterName"] = "intensity", ["value"] = 2.5,
            }));

            var component = Ok(Run("volume_get_component", new JObject { ["profilePath"] = profile, ["componentType"] = "Bloom" }));
            var intensity = component.SelectTokens("$..parameters[?(@.name == 'intensity')]").FirstOrDefault();
            Assert.That(intensity?["value"]?.Value<float>(), Is.EqualTo(2.5f).Within(1e-5f), component.ToString());
            Assert.That(intensity?["overrideState"]?.Value<bool>(), Is.True);
        }

        [Test]
        public void SetParameter_UnknownParameter_IsAnError()
        {
            var profile = CreateProfile();
            Ok(Run("volume_add_component", new JObject { ["profilePath"] = profile, ["componentType"] = "Bloom" }));

            AssertError(Run("volume_set_parameter", new JObject
            {
                ["profilePath"] = profile, ["componentType"] = "Bloom", ["parameterName"] = "intensty", ["value"] = 1,
            }));
        }

        [Test]
        public void CreateGlobalVolume_UsesTheProfile()
        {
            var profile = CreateProfile();
            Ok(Run("volume_create", new JObject { ["name"] = "R6Volume", ["profilePath"] = profile }));

            var volume = ComponentNamed(Find("R6Volume"), "Volume");
            Assert.That(volume, Is.Not.Null);
            Assert.That(AssetDatabase.GetAssetPath((UnityEngine.Object)Member(volume, "sharedProfile")), Is.EqualTo(profile));
            Assert.That(Member(volume, "isGlobal"), Is.True);
        }
    }

    [TestFixture]
    public class PostProcessSkillsLiveTests : OptionalPackageTestBase
    {
        protected override string ProbeSkill => "postprocess_list_effects";

        private static string CreateProfile()
        {
            EnsureProbeFolder();
            var created = Ok(Run("volume_profile_create", new JObject { ["name"] = "R6Post", ["savePath"] = $"{ProbeFolder}/R6Post.asset" }));
            return created["path"]?.ToString();
        }

        [Test]
        public void AddVignetteThenSet_ReadsBackIntensity()
        {
            var profile = CreateProfile();
            Ok(Run("postprocess_add_effect", new JObject { ["profilePath"] = profile, ["effectType"] = "Vignette" }));
            Ok(Run("postprocess_set_vignette", new JObject { ["profilePath"] = profile, ["intensity"] = 0.4, ["rounded"] = true }));

            var effect = Ok(Run("postprocess_get_effect", new JObject { ["profilePath"] = profile, ["effectType"] = "Vignette" }));
            var intensity = effect.SelectTokens("$..parameters[?(@.name == 'intensity')]").FirstOrDefault();
            Assert.That(intensity?["value"]?.Value<float>(), Is.EqualTo(0.4f).Within(1e-5f), effect.ToString());
            var rounded = effect.SelectTokens("$..parameters[?(@.name == 'rounded')]").FirstOrDefault();
            Assert.That(rounded?["value"]?.Value<bool>(), Is.True, effect.ToString());
        }

        [Test]
        public void SetTonemapping_UnknownMode_IsRejected()
        {
            var profile = CreateProfile();
            Ok(Run("postprocess_add_effect", new JObject { ["profilePath"] = profile, ["effectType"] = "Tonemapping" }));

            AssertError(Run("postprocess_set_tonemapping", new JObject { ["profilePath"] = profile, ["mode"] = "Filmicc" }));
        }

        [Test]
        public void RemoveEffect_ItIsGoneFromTheProfile()
        {
            var profile = CreateProfile();
            Ok(Run("postprocess_add_effect", new JObject { ["profilePath"] = profile, ["effectType"] = "Bloom" }));
            Ok(Run("postprocess_remove_effect", new JObject { ["profilePath"] = profile, ["effectType"] = "Bloom" }));

            AssertError(Run("postprocess_get_effect", new JObject { ["profilePath"] = profile, ["effectType"] = "Bloom" }));
        }
    }
}

// Producer:Betsy
