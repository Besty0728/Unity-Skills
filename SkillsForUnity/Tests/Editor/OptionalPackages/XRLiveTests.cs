using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace UnitySkills.Tests.OptionalPackages
{
    /// <summary>
    /// XR Interaction Toolkit skills against a real install (XRI 2.x on 2022, 3.x on 6000 — different namespaces,
    /// same simple type names). XRSkills.cs only touches XRI types through XRReflectionHelper, so these tests read
    /// state back the same way (ComponentNamed / Member) instead of referencing XRI types directly.
    /// </summary>
    [TestFixture]
    public class XRSkillsLiveTests : OptionalPackageTestBase
    {
        protected override string ProbeSkill => "xr_check_setup";

        private static GameObject NewCube(string name)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            GameObjectFinder.InvalidateCache();
            return go;
        }

        [Test]
        public void AddGrabInteractable_OnCube_AddsPhysicsAndInteractable()
        {
            NewCube("R6Cube");

            var result = Ok(Run("xr_add_grab_interactable", new JObject { ["name"] = "R6Cube" }));
            Assert.That(result["movementType"]?.ToString(), Is.EqualTo("VelocityTracking"), result.ToString());

            var go = Find("R6Cube");
            Assert.That(go.GetComponent<Rigidbody>(), Is.Not.Null, "A Rigidbody must be added for a mesh with no existing one.");
            var comp = ComponentNamed(go, "XRGrabInteractable");
            Assert.That(comp, Is.Not.Null);
            Assert.That(Member(comp, "movementType").ToString(), Is.EqualTo("VelocityTracking"));
        }

        [Test]
        public void AddGrabInteractable_MalformedAttachOffset_IsRejectedBeforeAnyWrite()
        {
            var go = NewCube("R6Cube");

            // ParseVector3 requires exactly three comma-separated numbers; "1,2" only has two.
            AssertSemanticInvalid(Run("xr_add_grab_interactable", new JObject
            {
                ["name"] = "R6Cube", ["attachTransformOffset"] = "1,2",
            }), "attachTransformOffset");

            Assert.That(go.GetComponent<Rigidbody>(), Is.Null, "Rejected before the Rigidbody was added.");
            Assert.That(ComponentNamed(go, "XRGrabInteractable"), Is.Null, "Rejected before the interactable was added.");
            Assert.That(go.transform.childCount, Is.EqualTo(0), "No attach point should have been created.");
        }

        [Test]
        public void AddGrabInteractable_UnknownMovementType_IsRejectedBeforeAnyWrite()
        {
            var go = NewCube("R6Cube");

            AssertSemanticInvalid(Run("xr_add_grab_interactable", new JObject
            {
                ["name"] = "R6Cube", ["movementType"] = "Bogus",
            }), "movementType");

            Assert.That(go.GetComponent<Rigidbody>(), Is.Null, "Rejected before the Rigidbody was added.");
            Assert.That(ComponentNamed(go, "XRGrabInteractable"), Is.Null, "Rejected before the interactable was added.");
        }

        [Test]
        public void AddGrabInteractable_AttachOffset_ParsesInvariantCultureAndApplies()
        {
            var go = NewCube("R6Cube");

            var result = Ok(Run("xr_add_grab_interactable", new JObject
            {
                ["name"] = "R6Cube", ["attachTransformOffset"] = "0,0.1,0",
            }));
            Assert.That(result["movementType"]?.ToString(), Is.EqualTo("VelocityTracking"), result.ToString());

            var attachPoint = go.transform.Find("Attach Point");
            Assert.That(attachPoint, Is.Not.Null, "attachTransformOffset must create a custom attach point.");
            var pos = attachPoint.localPosition;
            Assert.That(pos.x, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(pos.y, Is.EqualTo(0.1f).Within(1e-5f));
            Assert.That(pos.z, Is.EqualTo(0f).Within(1e-5f));

            var comp = ComponentNamed(go, "XRGrabInteractable");
            var attachTransform = Member(comp, "attachTransform") as Transform;
            Assert.That(attachTransform, Is.SameAs(attachPoint), "attachTransform must reference the created child.");
        }

        [Test]
        public void ConfigureInteractable_ModifiesAndReadsBack()
        {
            var go = NewCube("R6Cube");
            Ok(Run("xr_add_grab_interactable", new JObject { ["name"] = "R6Cube" }));

            var result = Ok(Run("xr_configure_interactable", new JObject
            {
                ["name"] = "R6Cube", ["movementType"] = "Kinematic", ["throwOnDetach"] = false, ["smoothPosition"] = false,
            }));
            Assert.That(result["changedProperties"]?.Values<string>(), Does.Contain("movementType"), result.ToString());

            var comp = ComponentNamed(go, "XRGrabInteractable");
            Assert.That(Member(comp, "movementType").ToString(), Is.EqualTo("Kinematic"));
            Assert.That(Member(comp, "throwOnDetach"), Is.False);
            Assert.That(Member(comp, "smoothPosition"), Is.False);
        }

        [Test]
        public void ConfigureInteractionLayers_UndefinedLayerName_IsRejectedBeforeAnyWrite()
        {
            var go = NewCube("R6Cube");
            Ok(Run("xr_add_simple_interactable", new JObject { ["name"] = "R6Cube" }));
            var comp = ComponentNamed(go, "XRSimpleInteractable");
            var before = (int)Member(Member(comp, "interactionLayers"), "value");

            AssertSemanticInvalid(Run("xr_configure_interaction_layers", new JObject
            {
                ["name"] = "R6Cube", ["layers"] = "R6TotallyUndefinedLayer", ["isInteractor"] = false,
            }), "layers");

            var after = (int)Member(Member(comp, "interactionLayers"), "value");
            Assert.That(after, Is.EqualTo(before), "An undefined layer name must not change the mask.");
        }

        [Test]
        public void ConfigureInteractionLayers_NumericMask_IsWrittenAndReadBack()
        {
            // interactionLayers is InteractionLayerMask, convertible from int only through its implicit operator:
            // XRReflectionHelper used Convert.ChangeType, which threw, so every write was dropped (reported as
            // success before round6, as "Could not write" after the first round6 fix).
            var go = NewCube("R6Cube");
            Ok(Run("xr_add_simple_interactable", new JObject { ["name"] = "R6Cube" }));
            var comp = ComponentNamed(go, "XRSimpleInteractable");

            Ok(Run("xr_configure_interaction_layers", new JObject
            {
                ["name"] = "R6Cube", ["layers"] = "5", ["isInteractor"] = false,
            }));
            Assert.That((int)Member(Member(comp, "interactionLayers"), "value"), Is.EqualTo(5));
        }

        [Test]
        public void ListInteractables_ReflectsSceneState()
        {
            NewCube("R6CubeA");
            NewCube("R6CubeB");
            Ok(Run("xr_add_simple_interactable", new JObject { ["name"] = "R6CubeA" }));
            Ok(Run("xr_add_simple_interactable", new JObject { ["name"] = "R6CubeB" }));

            var result = Ok(Run("xr_list_interactables", new JObject()));
            Assert.That(result["count"]?.Value<int>(), Is.EqualTo(2), result.ToString());
        }

        [Test]
        public void GetSceneReport_CountsCreatedRig()
        {
            Ok(Run("xr_setup_rig", new JObject()));

            var result = Ok(Run("xr_get_scene_report", new JObject()));
            Assert.That(result["summary"]?["origins"]?.Value<int>(), Is.EqualTo(1), result.ToString());
            Assert.That(result["summary"]?["interactionManagers"]?.Value<int>(), Is.EqualTo(1), result.ToString());
        }
    }
}

// Producer:Betsy
