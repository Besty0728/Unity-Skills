using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// ?wire=v2 on GET /skills/meta and on dryRun: the v1 bodies stay byte-identical, the v2 bodies drop the redundant echoes
    /// (meta: workflowTrackedSkills; dryRun: the full skill description block and unprovided optional parameters).
    /// </summary>
    [TestFixture]
    public class SkillWireV2MetaDryRunTests
    {
        private SurfaceProfileKind _savedProfile;

        [SetUp]
        public void SetUp()
        {
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
        }

        [TearDown]
        public void TearDown() => SkillsSurfaceProfile.Current = _savedProfile;

        [Test]
        public void Meta_V1_IsUnchangedByTheOverload()
        {
            Assert.That(SkillRouter.GetMeta(1), Is.EqualTo(SkillRouter.GetMeta()));
            var v1 = JObject.Parse(SkillRouter.GetMeta());
            Assert.That(v1["workflowTrackedSkills"], Is.Not.Null);
            Assert.That(v1["wire"], Is.Null);
        }

        [Test]
        public void Meta_V2_DropsWorkflowTrackedSkills_KeepsEveryOtherConstant()
        {
            var v1 = JObject.Parse(SkillRouter.GetMeta());
            var v2 = JObject.Parse(SkillRouter.GetMeta(2));
            Assert.That(v2["wire"].ToString(), Is.EqualTo("v2"));
            Assert.That(v2["workflowTrackedSkills"], Is.Null);
            foreach (var key in new[] { "manifestType", "schemaVersion", "version", "defaults", "categories", "operationTypes", "reservedBodyParameters" })
                Assert.That(JToken.DeepEquals(v1[key], v2[key]), Is.True, key);
            Assert.That(SkillRouter.GetMeta(2).Length, Is.LessThan(SkillRouter.GetMeta().Length / 3));
        }

        [Test]
        public void Meta_WireResolution_FromQueryString()
        {
            Assert.That(SkillRouter.ResolveWireVersion("?wire=v2"), Is.EqualTo(2));
            Assert.That(SkillRouter.ResolveWireVersion("?wire=2"), Is.EqualTo(2));
            Assert.That(SkillRouter.ResolveWireVersion(""), Is.EqualTo(1));
            Assert.That(SkillRouter.ResolveWireVersion(null), Is.EqualTo(1));
            Assert.That(SkillRouter.ResolveWireVersion("?wire=v1&x=1"), Is.EqualTo(1));
        }

        [Test]
        public void Meta_FastPath_ServesTheMatchingWireBody()
        {
            var v1 = SkillRouter.GetMeta();
            var v2 = SkillRouter.GetMeta(2);
            Assert.That(SkillRouter.TryGetCachedGetResponse("/skills/meta", "", out var json1, out var etag1), Is.True);
            Assert.That(SkillRouter.TryGetCachedGetResponse("/skills/meta", "?wire=v2", out var json2, out var etag2), Is.True);
            Assert.That(json1, Is.EqualTo(v1));
            Assert.That(json2, Is.EqualTo(v2));
            Assert.That(etag1, Is.Not.EqualTo(etag2));
        }

        [Test]
        public void DryRun_V1_IsUnchangedByTheOverload()
        {
            const string body = "{\"name\":\"TargetObject\",\"componentType\":\"BoxCollider\",\"propertyName\":\"center\",\"value\":\"0,0.5,0\"}";
            Assume.That(SkillRouter.HasSkill("component_set_property"), Is.True);
            Assert.That(SkillRouter.DryRun("component_set_property", body, 1), Is.EqualTo(SkillRouter.DryRun("component_set_property", body)));
            var v1 = JObject.Parse(SkillRouter.DryRun("component_set_property", body));
            Assert.That(v1["skill"]["description"], Is.Not.Null);
            Assert.That(v1["wire"], Is.Null);
        }

        [Test]
        public void DryRun_V2_KeepsVerdictFields_SlimsEchoes()
        {
            const string body = "{\"name\":\"TargetObject\",\"componentType\":\"BoxCollider\",\"propertyName\":\"center\",\"value\":\"0,0.5,0\"}";
            Assume.That(SkillRouter.HasSkill("component_set_property"), Is.True);
            var v1 = JObject.Parse(SkillRouter.DryRun("component_set_property", body));
            var v2 = JObject.Parse(SkillRouter.DryRun("component_set_property", body, 2));

            Assert.That(v2["wire"].ToString(), Is.EqualTo("v2"));
            Assert.That(v2["status"].ToString(), Is.EqualTo("dryRun"));
            Assert.That(v2["valid"].Value<bool>(), Is.EqualTo(v1["valid"].Value<bool>()));
            foreach (var key in new[] { "validation", "impact", "authorization", "steps", "changes", "note" })
                Assert.That(JToken.DeepEquals(v1[key], v2[key]), Is.True, key);

            var skill = (JObject)v2["skill"];
            Assert.That(skill["name"].ToString(), Is.EqualTo("component_set_property"));
            Assert.That(skill["description"], Is.Null);
            Assert.That(skill["flags"], Is.InstanceOf<JArray>());
            Assert.That(((JArray)skill["flags"]).Select(f => f.ToString()), Does.Contain("tracksWorkflow"));

            var parameters = (JArray)v2["parameters"];
            Assert.That(parameters.Count, Is.GreaterThan(0));
            Assert.That(parameters.Count, Is.LessThan(((JArray)v1["parameters"]).Count));
            foreach (var p in parameters)
                Assert.That(p["provided"].Value<bool>() || p["required"].Value<bool>(), Is.True, p.ToString());
            Assert.That(SkillRouter.DryRun("component_set_property", body, 2).Length, Is.LessThan(SkillRouter.DryRun("component_set_property", body).Length));
        }

        [Test]
        public void DryRun_V2_UnknownSkill_StillReturnsSkillNotFound()
        {
            var body = JObject.Parse(SkillRouter.DryRun("no_such_skill_xyz", "{}", 2));
            Assert.That(body["errorCode"].ToString(), Is.EqualTo("SKILL_NOT_FOUND"));
        }
    }
}

// Producer:Betsy
