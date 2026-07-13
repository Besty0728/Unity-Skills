using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class EditorUndoRedoTests
    {
        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [Test]
        public void EditorUndoRedo_RestoresLatestSkillMutation()
        {
            var create = Execute("gameobject_create", @"{""name"":""UndoProbe"",""primitiveType"":""Cube""}");
            Assert.That(create["status"]?.ToString(), Is.EqualTo("success"));
            Assert.That(GameObject.Find("UndoProbe"), Is.Not.Null);

            var undo = Execute("editor_undo", "{}");
            Assert.That(undo["status"]?.ToString(), Is.EqualTo("success"));
            Assert.That(GameObject.Find("UndoProbe"), Is.Null);

            var redo = Execute("editor_redo", "{}");
            Assert.That(redo["status"]?.ToString(), Is.EqualTo("success"));
            Assert.That(GameObject.Find("UndoProbe"), Is.Not.Null);
        }

        private static JObject Execute(string skillName, string jsonBody)
        {
            return JObject.Parse(SkillRouter.Execute(skillName, jsonBody));
        }
    }
}

// Producer:Betsy
