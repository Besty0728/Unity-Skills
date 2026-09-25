using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.OptionalPackages
{
    /// <summary>
    /// Netcode for GameObjects (com.unity.netcode.gameobjects) live skill tests: NetworkManager
    /// create/configure, NetworkObject attachment, NetworkRigidbody/NetworkRigidbody2D/NetworkAnimator
    /// component wiring, and the round6 fix where an invalid networkTopology used to be rejected only
    /// after the other fields in the same netcode_configure_manager call had already been written.
    /// </summary>
    [TestFixture]
    public class NetcodeSkillsLiveTests : OptionalPackageTestBase
    {
        protected override string ProbeSkill => "netcode_check_setup";

        [Test]
        public void CreateManager_AddsNetworkManagerWithUnityTransport()
        {
            var result = Ok(Run("netcode_create_manager", new JObject { ["name"] = "R6NetManager" }));
            Assert.That(result["success"]?.Value<bool>(), Is.True, result.ToString());
            Assert.That(result["transportType"]?.ToString(), Is.EqualTo("UnityTransport"));

            var go = Find("R6NetManager");
            Assert.That(ComponentNamed(go, "NetworkManager"), Is.Not.Null);
            Assert.That(ComponentNamed(go, "UnityTransport"), Is.Not.Null);
        }

        [Test]
        public void AddNetworkObject_ThenGetNetworkObjectInfo_ReadsBackConfiguredFields()
        {
            Ok(Run("gameobject_create", new JObject { ["name"] = "R6NetObj" }));
            var added = Ok(Run("netcode_add_network_object", new JObject
            {
                ["name"] = "R6NetObj", ["synchronizeTransform"] = true, ["dontDestroyWithOwner"] = true,
            }));
            Assert.That(added["success"]?.Value<bool>(), Is.True, added.ToString());
            Assert.That(ComponentNamed(Find("R6NetObj"), "NetworkObject"), Is.Not.Null);

            var info = Ok(Run("netcode_get_network_object_info", new JObject { ["name"] = "R6NetObj" }));
            Assert.That(info["found"]?.Value<bool>(), Is.True, info.ToString());
            Assert.That(info["synchronizeTransform"]?.Value<bool>(), Is.True);
            Assert.That(info["dontDestroyWithOwner"]?.Value<bool>(), Is.True);
        }

        [Test]
        public void ConfigureManager_InvalidTopology_RejectsAndLeavesTickRateUnwritten()
        {
            Ok(Run("netcode_create_manager", new JObject { ["name"] = "R6NetManager" }));

            // Round6 fix: networkTopology used to be parsed last, so an invalid value still let
            // every other field in the same call (tickRate here) get written before the rejection.
            AssertSemanticInvalid(Run("netcode_configure_manager", new JObject
            {
                ["name"] = "R6NetManager", ["networkTopology"] = "Bogus", ["tickRate"] = 77,
            }), "networkTopology");

            var info = Ok(Run("netcode_get_manager_info", new JObject { ["name"] = "R6NetManager" }));
            Assert.That(info["config"]?["tickRate"]?.Value<uint>(), Is.EqualTo(30u), info.ToString());
            Assert.That(info["config"]?["networkTopology"]?.ToString(), Is.EqualTo("ClientServer"));
        }

        [Test]
        public void ConfigureManager_ValidTopology_AppliesAndReadsBackViaGetManagerInfo()
        {
            Ok(Run("netcode_create_manager", new JObject { ["name"] = "R6NetManager" }));

            var configured = Ok(Run("netcode_configure_manager", new JObject
            {
                ["name"] = "R6NetManager", ["networkTopology"] = "DistributedAuthority", ["tickRate"] = 77,
            }));
            Assert.That(configured["applied"]?["networkTopology"]?.ToString(), Is.EqualTo("DistributedAuthority"), configured.ToString());
            Assert.That(configured["applied"]?["tickRate"]?.Value<uint>(), Is.EqualTo(77u));

            var info = Ok(Run("netcode_get_manager_info", new JObject { ["name"] = "R6NetManager" }));
            Assert.That(info["config"]?["networkTopology"]?.ToString(), Is.EqualTo("DistributedAuthority"), info.ToString());
            Assert.That(info["config"]?["tickRate"]?.Value<uint>(), Is.EqualTo(77u));
        }

        [Test]
        public void AddNetworkRigidbody_ThreeD_RequiresRigidbodyAndNetworkObject()
        {
            Ok(Run("gameobject_create", new JObject { ["name"] = "R6NetRb3D" }));
            Ok(Run("component_add", new JObject { ["name"] = "R6NetRb3D", ["componentType"] = "Rigidbody" }));
            Ok(Run("netcode_add_network_object", new JObject { ["name"] = "R6NetRb3D" }));

            var result = Ok(Run("netcode_add_network_rigidbody", new JObject { ["name"] = "R6NetRb3D" }));
            Assert.That(result["success"]?.Value<bool>(), Is.True, result.ToString());
            Assert.That(result["type"]?.ToString(), Is.EqualTo("NetworkRigidbody"));
            Assert.That(ComponentNamed(Find("R6NetRb3D"), "NetworkRigidbody"), Is.Not.Null);
        }

        [Test]
        public void AddNetworkRigidbody_TwoD_UsesRigidbody2DVariant()
        {
            Ok(Run("gameobject_create", new JObject { ["name"] = "R6NetRb2D" }));
            Ok(Run("component_add", new JObject { ["name"] = "R6NetRb2D", ["componentType"] = "Rigidbody2D" }));
            Ok(Run("netcode_add_network_object", new JObject { ["name"] = "R6NetRb2D" }));

            var result = Ok(Run("netcode_add_network_rigidbody", new JObject
            {
                ["name"] = "R6NetRb2D", ["useRigidbody2D"] = true,
            }));
            Assert.That(result["success"]?.Value<bool>(), Is.True, result.ToString());
            Assert.That(result["type"]?.ToString(), Is.EqualTo("NetworkRigidbody2D"));
            Assert.That(ComponentNamed(Find("R6NetRb2D"), "NetworkRigidbody2D"), Is.Not.Null);
        }

        [Test]
        public void AddNetworkAnimator_RequiresAnimatorAndNetworkObject()
        {
            Ok(Run("gameobject_create", new JObject { ["name"] = "R6NetAnim" }));
            Ok(Run("component_add", new JObject { ["name"] = "R6NetAnim", ["componentType"] = "Animator" }));
            Ok(Run("netcode_add_network_object", new JObject { ["name"] = "R6NetAnim" }));

            var result = Ok(Run("netcode_add_network_animator", new JObject { ["name"] = "R6NetAnim" }));
            Assert.That(result["success"]?.Value<bool>(), Is.True, result.ToString());
            Assert.That(ComponentNamed(Find("R6NetAnim"), "NetworkAnimator"), Is.Not.Null);
        }
    }
}

// Producer:Betsy
