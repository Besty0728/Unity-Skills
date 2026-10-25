using System.Collections.Generic;
using NUnit.Framework;
using UnitySkills.Internal;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The pure after-state hashes of P2: object-reference leaves are blanked, references are keyed by property path,
    /// and instance ids never reach the hash.
    /// </summary>
    [TestFixture]
    public class WorkflowStateHashTests
    {
        private static readonly string UnsavedSceneId = "GlobalObjectId_V1-2-" + new string('0', 32) + "-5-0";

        [Test]
        public void Normalize_BlanksReferenceLeavesAndKeepsTheRest()
        {
            string json = "{\"m_Name\":\"a\",\"ref\":{\"instanceID\":123},\"vec\":{\"x\":1,\"y\":2}}";

            string normalized = WorkflowStateHash.Normalize(json);

            Assert.That(normalized, Is.EqualTo("{\"m_Name\":\"a\",\"ref\":{},\"vec\":{\"x\":1,\"y\":2}}"));
        }

        [Test]
        public void Normalize_KeepsAssetReferencesBecauseTheirGuidIsStable()
        {
            string json = "{\"asset\":{\"fileID\":10,\"guid\":\"abc\",\"type\":2}}";

            Assert.That(WorkflowStateHash.Normalize(json), Is.EqualTo(json));
        }

        [Test]
        public void Normalize_LeavesAnObjectWithAnotherKeyAlone()
        {
            string json = "{\"a\":{\"x\":1,\"instanceID\":5,\"extra\":2}}";

            Assert.That(WorkflowStateHash.Normalize(json), Is.EqualTo(json));
        }

        [Test]
        public void Normalize_ReturnsTheInputWhenItDoesNotParse()
        {
            Assert.That(WorkflowStateHash.Normalize("not json {"), Is.EqualTo("not json {"));
        }

        [Test]
        public void Hash_IsTheSameForJsonsThatDifferOnlyInInstanceIds()
        {
            var before = new List<ObjectReferenceData> { Reference("m_Target", "GlobalObjectId_V1-0-aa-1-0", 100) };
            var after = new List<ObjectReferenceData> { Reference("m_Target", "GlobalObjectId_V1-0-aa-1-0", 900) };

            string beforeHash = WorkflowStateHash.StateHash("{\"ref\":{\"instanceID\":-100},\"x\":1}", before);
            string afterHash = WorkflowStateHash.StateHash("{\"ref\":{\"instanceID\":-900},\"x\":1}", after);

            Assert.That(afterHash, Is.EqualTo(beforeHash));
        }

        [Test]
        public void ReferencesKey_IsIndependentOfOrder()
        {
            var first = Reference("m_A", "GlobalObjectId_V1-0-aa-1-0", 1);
            var second = Reference("m_B", "GlobalObjectId_V1-0-bb-2-0", 2);

            Assert.That(WorkflowStateHash.ReferencesKey(new[] { second, first }),
                Is.EqualTo(WorkflowStateHash.ReferencesKey(new[] { first, second })));
        }

        [Test]
        public void ReferencesKey_NamesAnUnsavedSceneObjectByItsEntityId()
        {
            var first = Reference("m_Target", UnsavedSceneId, 1, "entity-1");
            var second = Reference("m_Target", UnsavedSceneId, 1, "entity-2");

            Assert.That(WorkflowStateHash.ReferencesKey(new[] { first }), Is.EqualTo("m_Target=entity:entity-1"));
            Assert.That(WorkflowStateHash.ReferencesKey(new[] { second }), Is.Not.EqualTo(WorkflowStateHash.ReferencesKey(new[] { first })));
        }

        [Test]
        public void Hash_ChangesWhenALeafChanges()
        {
            string before = WorkflowStateHash.StateHash("{\"x\":1}", null);
            string after = WorkflowStateHash.StateHash("{\"x\":2}", null);

            Assert.That(after, Is.Not.EqualTo(before));
        }

        [Test]
        public void Hash_IsSixteenLowercaseHexDigits()
        {
            Assert.That(WorkflowStateHash.StateHash("{\"x\":1}", null), Does.Match("^[0-9a-f]{16}$"));
        }

        private static ObjectReferenceData Reference(string propertyPath, string globalObjectId, int instanceId, string entityId = "")
        {
            return new ObjectReferenceData
            {
                propertyPath = propertyPath,
                globalObjectId = globalObjectId,
                objectInstanceId = instanceId,
                entityId = entityId
            };
        }
    }
}

// Producer:Betsy
