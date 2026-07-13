using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class WorkflowBookmarkTests
    {
        [Test]
        public void BookmarkSet_ThenBookmarkList_ReturnsCreatedBookmark()
        {
            var setJson = ToJObject(WorkflowSkills.BookmarkSet("BookmarkTest", "bookmark note"));
            Assert.That(setJson["success"]?.Value<bool>(), Is.True);

            var listJson = ToJObject(WorkflowSkills.BookmarkList());
            Assert.That(listJson["success"]?.Value<bool>(), Is.True);
            Assert.That(listJson["count"]?.Value<int>(), Is.GreaterThanOrEqualTo(1));
            Assert.That(listJson["bookmarks"]?.ToString(), Does.Contain("BookmarkTest"));

            var deleteJson = ToJObject(WorkflowSkills.BookmarkDelete("BookmarkTest"));
            Assert.That(deleteJson["success"]?.Value<bool>(), Is.True);
        }

        private static JObject ToJObject(object result)
        {
            return JObject.Parse(JsonConvert.SerializeObject(result));
        }
    }
}

// Producer:Betsy
