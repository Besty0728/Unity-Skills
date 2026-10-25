using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The pure three-way merge behind a restore's merge of a changed object: each leaf rule, nesting, arrays, object references,
    /// and the overwrite of a conflict. No Unity, so these run without a scene.
    /// </summary>
    [TestFixture]
    public class WorkflowJsonMergeTests
    {
        [Test]
        public void LeafTheStepDidNotChange_KeepsTheCurrentValue()
        {
            var result = Merge("{\"x\":1}", "{\"x\":1}", "{\"x\":9}");

            Assert.That(Json(result).Value<int>("x"), Is.EqualTo(9));
            Assert.That(result.ConflictPaths, Is.Empty);
            Assert.That(result.RevertedLeaves, Is.EqualTo(0));
        }

        [Test]
        public void LeafTheStepChangedAndNothingSince_GoesBackToTheBeforeValue()
        {
            var result = Merge("{\"x\":1}", "{\"x\":2}", "{\"x\":2}");

            Assert.That(Json(result).Value<int>("x"), Is.EqualTo(1));
            Assert.That(result.ConflictPaths, Is.Empty);
            Assert.That(result.RevertedLeaves, Is.EqualTo(1));
        }

        [Test]
        public void LeafAlreadyAtTheBeforeValue_StaysWithoutConflict()
        {
            var result = Merge("{\"x\":1}", "{\"x\":2}", "{\"x\":1}");

            Assert.That(Json(result).Value<int>("x"), Is.EqualTo(1));
            Assert.That(result.ConflictPaths, Is.Empty);
            Assert.That(result.RevertedLeaves, Is.EqualTo(0));
        }

        [Test]
        public void LeafChangedByBothSides_IsAConflictKeptUnlessOverwritten()
        {
            var kept = Merge("{\"x\":1}", "{\"x\":2}", "{\"x\":3}");
            Assert.That(kept.ConflictPaths, Is.EqualTo(new[] { "x" }));
            Assert.That(kept.ConflictValues["x"], Is.EqualTo("3"));
            Assert.That(Json(kept).Value<int>("x"), Is.EqualTo(3), "A conflict keeps the current value by default.");

            var overwritten = Merge("{\"x\":1}", "{\"x\":2}", "{\"x\":3}", "x");
            Assert.That(overwritten.ConflictPaths, Is.EqualTo(new[] { "x" }));
            Assert.That(Json(overwritten).Value<int>("x"), Is.EqualTo(1), "An overwritten conflict takes the before value.");
        }

        [Test]
        public void NestedObjects_MergeLeafByLeaf()
        {
            var result = Merge("{\"m\":{\"r\":1,\"g\":1}}", "{\"m\":{\"r\":0.2,\"g\":1}}", "{\"m\":{\"r\":0.2,\"g\":0.5}}");

            Assert.That(Json(result)["m"].Value<double>("r"), Is.EqualTo(1d));
            Assert.That(Json(result)["m"].Value<double>("g"), Is.EqualTo(0.5d), "The property the step did not touch keeps its current value.");
            Assert.That(result.RevertedLeaves, Is.EqualTo(1));
            Assert.That(result.ConflictPaths, Is.Empty);
        }

        [Test]
        public void NestedConflict_ItsPathIsDotted()
        {
            var result = Merge("{\"m\":{\"r\":1}}", "{\"m\":{\"r\":0.2}}", "{\"m\":{\"r\":0.7}}");

            Assert.That(result.ConflictPaths, Is.EqualTo(new[] { "m.r" }));
        }

        [Test]
        public void ArraysOfEqualLength_MergeByIndex()
        {
            var result = Merge("{\"a\":[1,2]}", "{\"a\":[5,2]}", "{\"a\":[5,3]}");

            Assert.That(Json(result)["a"].Values<int>().ToArray(), Is.EqualTo(new[] { 1, 3 }));
            Assert.That(result.ConflictPaths, Is.Empty);
        }

        [Test]
        public void ArrayElementConflict_PathHasTheIndex()
        {
            var result = Merge("{\"a\":[1,2]}", "{\"a\":[5,2]}", "{\"a\":[7,3]}");

            Assert.That(result.ConflictPaths, Is.EqualTo(new[] { "a[0]" }));
        }

        [Test]
        public void ArraysOfDifferentLength_AreOneLeaf()
        {
            const string before = "{\"a\":[1,2]}";
            const string after = "{\"a\":[1,2,3]}";
            const string current = "{\"a\":[1,2,4]}";

            var kept = Merge(before, after, current);
            Assert.That(kept.ConflictPaths, Is.EqualTo(new[] { "a" }));
            Assert.That(Json(kept)["a"].Values<int>().ToArray(), Is.EqualTo(new[] { 1, 2, 4 }));

            var overwritten = Merge(before, after, current, "a");
            Assert.That(Json(overwritten)["a"].Values<int>().ToArray(), Is.EqualTo(new[] { 1, 2 }));
        }

        [Test]
        public void MissingKey_CountsAsNull_SoAStepsNewKeyIsRemoved()
        {
            var result = Merge("{}", "{\"y\":5}", "{\"y\":5}");

            Assert.That(Json(result).ContainsKey("y"), Is.False);
            Assert.That(result.RevertedLeaves, Is.EqualTo(1));
        }

        [Test]
        public void ReferenceLeaves_AreNeverWritten()
        {
            var result = Merge("{\"r\":{\"instanceID\":100}}", "{\"r\":{\"instanceID\":200}}", "{\"r\":{\"instanceID\":300}}");

            Assert.That(Json(result)["r"].Value<int>("instanceID"), Is.EqualTo(300), "The current reference stays; the references are merged by path.");
            Assert.That(result.ConflictPaths, Is.Empty);
            Assert.That(result.RevertedLeaves, Is.EqualTo(0));
        }

        [Test]
        public void MergeReferences_RevertedPath_TakesTheBeforeEntry()
        {
            var conflicts = new List<string>();
            var merged = WorkflowJsonMerge.MergeReferences(
                Refs(("m", "gid-a")), Refs(("m", "gid-b")), Refs(("m", "gid-b")),
                new HashSet<string>(), conflicts, out int reverted);

            Assert.That(merged.Single().globalObjectId, Is.EqualTo("gid-a"));
            Assert.That(reverted, Is.EqualTo(1));
            Assert.That(conflicts, Is.Empty);
        }

        [Test]
        public void MergeReferences_UnchangedPath_KeepsTheCurrentEntry()
        {
            var merged = WorkflowJsonMerge.MergeReferences(
                Refs(("m", "gid-a")), Refs(("m", "gid-a")), Refs(("m", "gid-c")),
                new HashSet<string>(), new List<string>(), out _);

            Assert.That(merged.Single().globalObjectId, Is.EqualTo("gid-c"));
        }

        [Test]
        public void MergeReferences_Conflict_IsPrefixedAndOverwritable()
        {
            var conflicts = new List<string>();
            var kept = WorkflowJsonMerge.MergeReferences(
                Refs(("m", "gid-a")), Refs(("m", "gid-b")), Refs(("m", "gid-c")),
                new HashSet<string>(), conflicts, out _);
            Assert.That(conflicts, Is.EqualTo(new[] { "ref:m" }));
            Assert.That(kept.Single().globalObjectId, Is.EqualTo("gid-c"));

            var overwritten = WorkflowJsonMerge.MergeReferences(
                Refs(("m", "gid-a")), Refs(("m", "gid-b")), Refs(("m", "gid-c")),
                new HashSet<string> { "ref:m" }, new List<string>(), out _);
            Assert.That(overwritten.Single().globalObjectId, Is.EqualTo("gid-a"));
        }

        [Test]
        public void MergeReferences_RevertToNoReference_ClearsTheCurrentOne()
        {
            var merged = WorkflowJsonMerge.MergeReferences(
                new List<ObjectReferenceData>(), Refs(("m", "gid-b")), Refs(("m", "gid-b")),
                new HashSet<string>(), new List<string>(), out _);

            var cleared = merged.Single();
            Assert.That(cleared.propertyPath, Is.EqualTo("m"));
            Assert.That(cleared.globalObjectId, Is.Empty, "The reference the step added is cleared, not left as the current object.");
        }

        private static JsonMergeResult Merge(string before, string after, string current, params string[] overwrite) =>
            WorkflowJsonMerge.Merge(before, after, current, new HashSet<string>(overwrite));

        private static JObject Json(JsonMergeResult result) => JObject.Parse(result.MergedJson);

        private static List<ObjectReferenceData> Refs(params (string Path, string Gid)[] refs) =>
            refs.Select(reference => new ObjectReferenceData { propertyPath = reference.Path, globalObjectId = reference.Gid }).ToList();
    }
}

// Producer:Betsy
