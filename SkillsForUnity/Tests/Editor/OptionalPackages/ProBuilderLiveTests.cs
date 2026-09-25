using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace UnitySkills.Tests.OptionalPackages
{
    /// <summary>
    /// ProBuilder modeling skills against a real install (5.x on 2022, 6.x on 6000). ProBuilderSkills.cs only
    /// references ProBuilder types behind #if PROBUILDER, so these tests read state back through the skills' own
    /// JSON (vertex/face counts) or plain UnityEngine components (MeshRenderer) rather than ProBuilderMesh itself.
    /// </summary>
    [TestFixture]
    public class ProBuilderSkillsLiveTests : OptionalPackageTestBase
    {
        protected override string ProbeSkill => "probuilder_get_info";

        [Test]
        public void CreateShape_Cube_ReturnsCountsAndAddsProBuilderMesh()
        {
            var result = Ok(Run("probuilder_create_shape", new JObject { ["shape"] = "Cube", ["name"] = "R6Cube" }));
            Assert.That(result["faceCount"]?.Value<int>(), Is.EqualTo(6), result.ToString());
            Assert.That(result["vertexCount"]?.Value<int>(), Is.GreaterThan(0), result.ToString());

            var go = Find("R6Cube");
            Assert.That(ComponentNamed(go, "ProBuilderMesh"), Is.Not.Null);
        }

        [Test]
        public void ExtrudeFaces_IncreasesFaceAndVertexCounts_ReadBackViaGetInfo()
        {
            var created = Ok(Run("probuilder_create_shape", new JObject { ["shape"] = "Cube", ["name"] = "R6Cube" }));
            var vertsBefore = created["vertexCount"].Value<int>();

            var extruded = Ok(Run("probuilder_extrude_faces", new JObject
            {
                ["name"] = "R6Cube", ["faceIndexes"] = "0", ["distance"] = 0.5, ["method"] = "FaceNormal",
            }));
            // extrudedFaceCount is what ProBuilderMesh.Extrude returns: the side faces it created, 4 for one quad.
            Assert.That(extruded["extrudedFaceCount"]?.Value<int>(), Is.EqualTo(4), extruded.ToString());
            Assert.That(extruded["totalFaces"]?.Value<int>(), Is.EqualTo(10), extruded.ToString());
            Assert.That(extruded["totalVertices"]?.Value<int>(), Is.GreaterThan(vertsBefore), extruded.ToString());

            var info = Ok(Run("probuilder_get_info", new JObject { ["name"] = "R6Cube" }));
            Assert.That(info["faceCount"]?.Value<int>(), Is.EqualTo(extruded["totalFaces"]?.Value<int>()), info.ToString());
            Assert.That(info["vertexCount"]?.Value<int>(), Is.EqualTo(extruded["totalVertices"]?.Value<int>()), info.ToString());
        }

        [Test]
        public void ExtrudeFaces_OutOfRangeFaceIndexes_IsRejected()
        {
            Ok(Run("probuilder_create_shape", new JObject { ["shape"] = "Cube", ["name"] = "R6Cube" }));

            AssertSemanticInvalid(Run("probuilder_extrude_faces", new JObject
            {
                ["name"] = "R6Cube", ["faceIndexes"] = "999",
            }), "faceIndexes");
        }

        [Test]
        public void MoveVertices_ShiftsSelectedVertex_ReadBackViaGetVertices()
        {
            Ok(Run("probuilder_create_shape", new JObject { ["shape"] = "Cube", ["name"] = "R6Cube" }));
            var before = Ok(Run("probuilder_get_vertices", new JObject { ["name"] = "R6Cube", ["vertexIndexes"] = "0" }));
            var beforeX = before["vertices"]?[0]?["x"]?.Value<float>() ?? 0f;

            Ok(Run("probuilder_move_vertices", new JObject { ["name"] = "R6Cube", ["vertexIndexes"] = "0", ["deltaX"] = 1.0 }));

            var after = Ok(Run("probuilder_get_vertices", new JObject { ["name"] = "R6Cube", ["vertexIndexes"] = "0" }));
            var afterX = after["vertices"]?[0]?["x"]?.Value<float>() ?? 0f;
            Assert.That(afterX, Is.EqualTo(beforeX + 1.0f).Within(1e-4f), after.ToString());
        }

        [Test]
        public void SetMaterial_WithColor_AppliesToRenderer()
        {
            Ok(Run("probuilder_create_shape", new JObject { ["shape"] = "Cube", ["name"] = "R6Cube" }));

            var result = Ok(Run("probuilder_set_material", new JObject
            {
                ["name"] = "R6Cube", ["r"] = 1.0, ["g"] = 0.0, ["b"] = 0.0, ["a"] = 1.0,
            }));
            Assert.That(result["color"]?["r"]?.Value<float>(), Is.EqualTo(1f).Within(1e-4f), result.ToString());

            var renderer = Find("R6Cube").GetComponent<MeshRenderer>();
            var color = renderer.sharedMaterial.color;
            Assert.That(color.r, Is.EqualTo(1f).Within(1e-3f));
            Assert.That(color.g, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(color.b, Is.EqualTo(0f).Within(1e-3f));
        }

        [Test]
        public void GetInfo_ReportsTopologyForCreatedShape()
        {
            Ok(Run("probuilder_create_shape", new JObject { ["shape"] = "Cube", ["name"] = "R6Cube" }));

            var info = Ok(Run("probuilder_get_info", new JObject { ["name"] = "R6Cube" }));
            Assert.That(info["shapeType"]?.ToString(), Is.EqualTo("Cube"), info.ToString());
            Assert.That(info["faceCount"]?.Value<int>(), Is.EqualTo(6), info.ToString());
            Assert.That(info["edgeCount"]?.Value<int>(), Is.GreaterThan(0), info.ToString());
        }

        [Test]
        public void GetVertices_ByIndex_ReturnsRequestedPositions()
        {
            Ok(Run("probuilder_create_shape", new JObject { ["shape"] = "Cube", ["name"] = "R6Cube" }));

            var result = Ok(Run("probuilder_get_vertices", new JObject { ["name"] = "R6Cube", ["vertexIndexes"] = "0,1" }));
            Assert.That(((JArray)result["vertices"]).Count, Is.EqualTo(2), result.ToString());
        }
    }
}

// Producer:Betsy
