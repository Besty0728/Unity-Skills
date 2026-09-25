using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace UnitySkills.Tests.OptionalPackages
{
    /// <summary>
    /// Cinemachine vcam/brain/camera-manager skills against a scratch scene. CM2/CM3 API differences are
    /// bridged by CinemachineAdapter inside the skills themselves; where this fixture reflects on a
    /// component directly, MemberEither picks whichever of the CM3 (PascalCase) / CM2 (m_-prefixed) member
    /// names actually exists, so the same test body runs unmodified on both opt6000 (CM 3.1.7) and
    /// opt2022 (CM 2.10.2).
    /// </summary>
    [TestFixture]
    public class CinemachineSkillsLiveTests : OptionalPackageTestBase
    {
        protected override string ProbeSkill => "cinemachine_list_components";

        [Test]
        public void CreateVCam_IsInspectableWithDefaultPriority()
        {
            var result = Ok(Run("cinemachine_create_vcam", new JObject { ["name"] = "R6VCam" }));
            Assert.That(result["gameObjectName"]?.ToString(), Is.EqualTo("R6VCam"), result.ToString());
            Assert.That(result["instanceId"]?.Value<int>(), Is.Not.EqualTo(0), result.ToString());

            var info = Ok(Run("cinemachine_inspect_vcam", new JObject { ["vcamName"] = "R6VCam" }));
            Assert.That(info["name"]?.ToString(), Is.EqualTo("R6VCam"), info.ToString());
            Assert.That(info["priority"]?.Value<int>(), Is.EqualTo(10), info.ToString());
            Assert.That(info["follow"]?.ToString(), Is.EqualTo("None"), info.ToString());
        }

        [Test]
        public void SetLens_WritesFovAndClipPlanes_ReadBackByInspectVCam()
        {
            Ok(Run("cinemachine_create_vcam", new JObject { ["name"] = "R6VCam" }));

            var result = Ok(Run("cinemachine_set_lens", new JObject
            {
                ["vcamName"] = "R6VCam", ["fov"] = 55.5, ["nearClip"] = 0.3, ["farClip"] = 800,
            }));
            Assert.That(result["success"]?.Value<bool>(), Is.True, result.ToString());

            var info = Ok(Run("cinemachine_inspect_vcam", new JObject { ["vcamName"] = "R6VCam" }));
            var lens = info["lens"];
            Assert.That(lens?["FieldOfView"]?.Value<float>(), Is.EqualTo(55.5f).Within(1e-3f), info.ToString());
            Assert.That(lens?["NearClipPlane"]?.Value<float>(), Is.EqualTo(0.3f).Within(1e-3f), info.ToString());
            Assert.That(lens?["FarClipPlane"]?.Value<float>(), Is.EqualTo(800f).Within(1e-2f), info.ToString());
        }

        [Test]
        public void ListComponents_ReturnsCinemachineComponentNames()
        {
            var result = Ok(Run("cinemachine_list_components", new JObject()));
            Assert.That(result["count"]?.Value<int>(), Is.GreaterThan(0), result.ToString());
            Assert.That(result.ToString(), Does.Contain("Cinemachine"), result.ToString());
        }

        [Test]
        public void SetBlend_InvalidStyle_IsRejectedAndDefaultBlendUnchanged()
        {
            var brain = CreateMainCameraWithBrain();

            Ok(Run("cinemachine_set_blend", new JObject { ["style"] = "HardIn", ["time"] = 1.25 }));
            var beforeStyle = DefaultBlendStyle(brain);
            var beforeTime = DefaultBlendTime(brain);
            Assert.That(beforeStyle, Is.EqualTo("HardIn"));

            AssertSemanticInvalid(Run("cinemachine_set_blend", new JObject { ["style"] = "Smoothe", ["time"] = 9 }), "style");

            Assert.That(DefaultBlendStyle(brain), Is.EqualTo(beforeStyle),
                "A rejected style must not silently reset the brain's default blend (previously became Cut).");
            Assert.That(DefaultBlendTime(brain), Is.EqualTo(beforeTime).Within(1e-5f));

            var applied = Ok(Run("cinemachine_set_blend", new JObject { ["style"] = "EaseInOut", ["time"] = 3.5 }));
            Assert.That(applied["message"]?.ToString(), Does.Contain("EaseInOut"), applied.ToString());
            Assert.That(DefaultBlendStyle(brain), Is.EqualTo("EaseInOut"));
            Assert.That(DefaultBlendTime(brain), Is.EqualTo(3.5f).Within(1e-5f));
        }

        [Test]
        public void SetBrain_InvalidUpdateMethod_IsRejectedWithoutWriting()
        {
            var brain = CreateMainCameraWithBrain();

            var baseline = Ok(Run("cinemachine_set_brain", new JObject { ["updateMethod"] = "FixedUpdate" }));
            Assert.That(baseline["settings"]?["updateMethod"]?.ToString(), Is.EqualTo("FixedUpdate"), baseline.ToString());

            AssertSemanticInvalid(Run("cinemachine_set_brain", new JObject { ["updateMethod"] = "Blah" }), "updateMethod");

            Assert.That(MemberEither(brain, "UpdateMethod", "m_UpdateMethod").ToString(), Is.EqualTo("FixedUpdate"));
        }

        [Test]
        public void SetBrain_InvalidBlendStyle_IsRejectedWithoutWriting()
        {
            var brain = CreateMainCameraWithBrain();

            Ok(Run("cinemachine_set_brain", new JObject { ["defaultBlendStyle"] = "HardOut", ["defaultBlendTime"] = 1.75 }));
            var beforeStyle = DefaultBlendStyle(brain);
            var beforeTime = DefaultBlendTime(brain);
            Assert.That(beforeStyle, Is.EqualTo("HardOut"));

            AssertSemanticInvalid(Run("cinemachine_set_brain", new JObject { ["defaultBlendStyle"] = "Smoothe" }), "defaultBlendStyle");

            Assert.That(DefaultBlendStyle(brain), Is.EqualTo(beforeStyle));
            Assert.That(DefaultBlendTime(brain), Is.EqualTo(beforeTime).Within(1e-5f));
        }

        [Test]
        public void CreateFreeLook_ResolvesFollowAndLookAt()
        {
            CreatePlain("R6FollowTarget");
            CreatePlain("R6LookAtTarget");

            var result = Ok(Run("cinemachine_create_freelook", new JObject
            {
                ["name"] = "R6FreeLook", ["followName"] = "R6FollowTarget", ["lookAtName"] = "R6LookAtTarget",
            }));
            Assert.That(result["gameObjectName"]?.ToString(), Is.EqualTo("R6FreeLook"), result.ToString());

            var (follow, lookAt) = FreeLookTargets(Find("R6FreeLook"));
            Assert.That(follow?.gameObject.name, Is.EqualTo("R6FollowTarget"));
            Assert.That(lookAt?.gameObject.name, Is.EqualTo("R6LookAtTarget"));
        }

        [Test]
        public void CreateFreeLook_UnresolvedFollowName_CreatesNothing()
        {
            AssertError(Run("cinemachine_create_freelook", new JObject { ["name"] = "R6FreeLook", ["followName"] = "R6NoSuchTarget" }));

            AssertNoGameObject("R6FreeLook");
        }

        [Test]
        public void CreateStateDrivenCamera_TargetWithoutAnimator_CreatesNothing()
        {
            CreatePlain("R6PlainTarget");

            AssertSemanticInvalid(Run("cinemachine_create_state_driven_camera", new JObject
            {
                ["name"] = "R6StateDriven", ["targetAnimatorName"] = "R6PlainTarget",
            }), "targetAnimatorName");

            AssertNoGameObject("R6StateDriven");
        }

        [Test]
        public void ConfigureCameraManager_ClearShotWithAnimatorName_RejectsWithoutWritingBlend()
        {
            Ok(Run("cinemachine_create_clear_shot", new JObject { ["name"] = "R6ClearShot" }));
            var clearShot = ComponentNamed(Find("R6ClearShot"), "CinemachineClearShot");
            var beforeStyle = DefaultBlendStyle(clearShot);
            var beforeTime = DefaultBlendTime(clearShot);

            AssertSemanticInvalid(Run("cinemachine_configure_camera_manager", new JObject
            {
                ["cameraName"] = "R6ClearShot",
                ["animatorName"] = "R6DoesNotExist", // StateDriven-only parameter; a ClearShot must reject it structurally.
                ["defaultBlendStyle"] = "Linear",
                ["defaultBlendTime"] = 9.5,
            }), "animatorName");

            Assert.That(DefaultBlendStyle(clearShot), Is.EqualTo(beforeStyle), "A rejected call must not write the blend either.");
            Assert.That(DefaultBlendTime(clearShot), Is.EqualTo(beforeTime).Within(1e-5f));

            var applied = Ok(Run("cinemachine_configure_camera_manager", new JObject
            {
                ["cameraName"] = "R6ClearShot", ["defaultBlendStyle"] = "Linear", ["defaultBlendTime"] = 9.5,
            }));
            Assert.That(applied["message"]?.ToString(), Does.Contain("Linear"), applied.ToString());
            Assert.That(DefaultBlendStyle(clearShot), Is.EqualTo("Linear"));
            Assert.That(DefaultBlendTime(clearShot), Is.EqualTo(9.5f).Within(1e-5f));
        }

        [Test]
        public void ConfigureBody_OnlyUnusableParameter_IsAnError()
        {
            Ok(Run("cinemachine_create_vcam", new JObject { ["name"] = "R6VCam" }));
            Ok(Run("cinemachine_set_component", new JObject { ["vcamName"] = "R6VCam", ["stage"] = "Body", ["componentType"] = "HardLockToTarget" }));

            // "radius" only applies to OrbitalFollow/OrbitalTransposer bodies; HardLockToTarget matches no
            // branch in cinemachine_configure_body, so it has nothing to apply the parameter to.
            AssertError(Run("cinemachine_configure_body", new JObject { ["vcamName"] = "R6VCam", ["radius"] = 5f }));
        }

        [Test]
        public void ConfigureExtension_WrongExtensionName_IsRejectedInsteadOfFallingBackToFirst()
        {
            Ok(Run("cinemachine_create_vcam", new JObject { ["name"] = "R6VCam" }));
            Ok(Run("cinemachine_add_extension", new JObject { ["vcamName"] = "R6VCam", ["extensionName"] = "FollowZoom" }));

            var zoom = ComponentNamed(Find("R6VCam"), "CinemachineFollowZoom");
            Assert.That(zoom, Is.Not.Null);
            var beforeDamping = (float)MemberEither(zoom, "Damping", "m_Damping");

            // "Deoccluder" is a real, different extension type that this vcam does not have attached.
            AssertSemanticInvalid(Run("cinemachine_configure_extension", new JObject
            {
                ["vcamName"] = "R6VCam", ["extensionName"] = "Deoccluder", ["damping"] = 4.5,
            }), "extensionName");

            Assert.That((float)MemberEither(zoom, "Damping", "m_Damping"), Is.EqualTo(beforeDamping).Within(1e-5f),
                "A rejected extensionName must not fall back to configuring the vcam's only extension.");

            var applied = Ok(Run("cinemachine_configure_extension", new JObject
            {
                ["vcamName"] = "R6VCam", ["extensionName"] = "FollowZoom", ["width"] = 3.5, ["damping"] = 2.5,
            }));
            Assert.That(applied["warnings"]?.Type, Is.EqualTo(JTokenType.Array), applied.ToString());
            Assert.That((float)MemberEither(zoom, "Width", "m_Width"), Is.EqualTo(3.5f).Within(1e-4f));
            Assert.That((float)MemberEither(zoom, "Damping", "m_Damping"), Is.EqualTo(2.5f).Within(1e-4f));
        }

        // --- Helpers ---

        /// <summary>A bare scene GameObject not produced by any skill; the finder cache is invalidated
        /// immediately so a later skill call can resolve it by name regardless of call order.</summary>
        private static GameObject CreatePlain(string name)
        {
            var go = new GameObject(name);
            GameObjectFinder.InvalidateCache();
            return go;
        }

        private static void AssertNoGameObject(string name)
        {
            GameObjectFinder.InvalidateCache();
            Assert.That(GameObjectFinder.Find(name), Is.Null, $"'{name}' must not have been created.");
        }

        /// <summary>A Main Camera with a CinemachineBrain, needed by every brain/blend skill under test.
        /// component_add resolves "CinemachineBrain" by simple name, so this works unchanged on CM2 and CM3.</summary>
        private static Component CreateMainCameraWithBrain()
        {
            var camGo = CreatePlain("R6MainCamera");
            camGo.tag = "MainCamera";
            camGo.AddComponent<Camera>();
            GameObjectFinder.InvalidateCache();

            Ok(Run("component_add", new JObject { ["name"] = camGo.name, ["componentType"] = "CinemachineBrain" }));
            var brain = ComponentNamed(camGo, "CinemachineBrain");
            Assert.That(brain, Is.Not.Null, "component_add should have attached a CinemachineBrain to the Main Camera.");
            return brain;
        }

        /// <summary>
        /// CM3's cinemachine_create_freelook writes the Follow/LookAt properties on a CinemachineCamera.
        /// CM2 writes the m_Follow/m_LookAt fields directly on a CinemachineFreeLook -- that type also
        /// inherits Follow/LookAt properties, but they resolve through a parent-rig fallback unrelated to
        /// this fixture, so reading must target the exact member the skill itself wrote, not just whichever
        /// name happens to exist.
        /// </summary>
        private static (Transform follow, Transform lookAt) FreeLookTargets(GameObject go)
        {
            var cm3Vcam = ComponentNamed(go, "CinemachineCamera");
            if (cm3Vcam != null)
                return ((Transform)Member(cm3Vcam, "Follow"), (Transform)Member(cm3Vcam, "LookAt"));

            var cm2FreeLook = ComponentNamed(go, "CinemachineFreeLook");
            Assert.That(cm2FreeLook, Is.Not.Null, "cinemachine_create_freelook must attach a CinemachineCamera (CM3) or CinemachineFreeLook (CM2).");
            return ((Transform)Member(cm2FreeLook, "m_Follow"), (Transform)Member(cm2FreeLook, "m_LookAt"));
        }

        /// <summary>
        /// Reads a member whose name differs between Cinemachine 3.x (PascalCase, e.g. DefaultBlend/Style)
        /// and 2.x (the historical m_-prefixed field, e.g. m_DefaultBlend/m_Style). Existence is probed with
        /// the public Type.GetField/GetProperty overloads (which already search inherited members), then the
        /// actual read goes through the base class's Member() so private/serialized fields work too.
        /// </summary>
        private static object MemberEither(object target, string cm3Name, string cm2Name)
        {
            var type = target.GetType();
            bool hasCm3Member = type.GetField(cm3Name) != null || type.GetProperty(cm3Name) != null;
            return Member(target, hasCm3Member ? cm3Name : cm2Name);
        }

        /// <summary>The style name of a brain/camera-manager's DefaultBlend (CM3) / m_DefaultBlend (CM2) field.</summary>
        private static string DefaultBlendStyle(object blendOwner)
        {
            var blend = MemberEither(blendOwner, "DefaultBlend", "m_DefaultBlend");
            return MemberEither(blend, "Style", "m_Style").ToString();
        }

        private static float DefaultBlendTime(object blendOwner)
        {
            var blend = MemberEither(blendOwner, "DefaultBlend", "m_DefaultBlend");
            return (float)MemberEither(blend, "Time", "m_Time");
        }
    }
}

// Producer:Betsy
