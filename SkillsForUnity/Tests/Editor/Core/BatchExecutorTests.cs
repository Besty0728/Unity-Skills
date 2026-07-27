using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Unit coverage for BatchExecutor's per-item error isolation and input-validation contract.
    /// BatchExecutor is a stateless generic helper (no EditorPrefs, files, or scene state), so
    /// this fixture needs no SetUp/TearDown.
    /// </summary>
    [TestFixture]
    public class BatchExecutorTests
    {
        private class Item
        {
            public int value;
        }

        private static JObject ToJObject(object result) => JObject.Parse(JsonConvert.SerializeObject(result));

        [Test]
        public void Execute_ProcessorThrowsForOneItem_BecomesFailedResult_OthersStillSucceed()
        {
            const string itemsJson = "[{\"value\":1},{\"value\":2},{\"value\":3}]";

            var result = BatchExecutor.Execute<Item>(itemsJson, item =>
            {
                if (item.value == 2) throw new InvalidOperationException("boom");
                return new { success = true, value = item.value };
            }, itemIdentifier: item => item.value.ToString());

            var json = ToJObject(result);

            Assert.That(json["success"]?.Value<bool>(), Is.False);
            Assert.That(json["totalItems"]?.Value<int>(), Is.EqualTo(3));
            Assert.That(json["successCount"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(json["failCount"]?.Value<int>(), Is.EqualTo(1));

            var results = json["results"] as JArray;
            Assert.That(results, Has.Count.EqualTo(3), "The batch must report a result per item, not abort partway.");
            Assert.That(results[0]["success"]?.Value<bool>(), Is.True);
            Assert.That(results[1]["success"]?.Value<bool>(), Is.False);
            Assert.That(results[1]["error"]?.ToString(), Does.Contain("boom"));
            Assert.That(results[1]["target"]?.ToString(), Is.EqualTo("2"));
            Assert.That(results[2]["success"]?.Value<bool>(), Is.True);
        }

        [Test]
        public void Execute_EmptyItemsArray_ReturnsError()
        {
            var result = BatchExecutor.Execute<Item>("[]", item => new { success = true });
            var json = ToJObject(result);

            Assert.That(json["error"]?.ToString(), Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void Execute_NullItemsJson_ReturnsError()
        {
            var result = BatchExecutor.Execute<Item>(null, item => new { success = true });
            var json = ToJObject(result);

            Assert.That(json["error"]?.ToString(), Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void Execute_MalformedItemsJson_ReturnsParseError()
        {
            var result = BatchExecutor.Execute<Item>("{not an array", item => new { success = true });
            var json = ToJObject(result);

            StringAssert.Contains("Failed to parse items JSON", json["error"]?.ToString());
        }
    }
}

// Producer:Betsy
