using NUnit.Framework;
using UnityEngine.TestTools;
using UnitySkills.Internal;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class WorkflowSnapshotPathSanitizeTests
    {
        [Test]
        public void ProjectSettingsPath_KeepsAssetPath_ClearsFileHashes()
        {
            var snapshot = new ObjectSnapshot
            {
                assetPath = "ProjectSettings/TagManager.asset",
                fileHash = "hash",
                metaFileHash = "metaHash"
            };

            WorkflowManager.SanitizeSnapshot(snapshot, "test");

            Assert.AreEqual("ProjectSettings/TagManager.asset", snapshot.assetPath);
            Assert.IsNull(snapshot.fileHash);
            Assert.IsNull(snapshot.metaFileHash);
            LogAssert.NoUnexpectedReceived();
        }

        [TestCase("ProjectSettings/../../secret.txt")]
        [TestCase("../outside.asset")]
        [TestCase("/etc/passwd")]
        public void UnsafePath_ClearsAssetPathAndFileHash(string unsafePath)
        {
            var snapshot = new ObjectSnapshot
            {
                assetPath = unsafePath,
                fileHash = "hash",
                metaFileHash = "metaHash"
            };

            WorkflowManager.SanitizeSnapshot(snapshot, "test");

            Assert.IsNull(snapshot.assetPath);
            Assert.IsNull(snapshot.fileHash);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void AssetsPath_IsUntouched()
        {
            var snapshot = new ObjectSnapshot
            {
                assetPath = "Assets/Foo.asset",
                fileHash = "hash",
                metaFileHash = "metaHash"
            };

            WorkflowManager.SanitizeSnapshot(snapshot, "test");

            Assert.AreEqual("Assets/Foo.asset", snapshot.assetPath);
            Assert.AreEqual("hash", snapshot.fileHash);
            Assert.AreEqual("metaHash", snapshot.metaFileHash);
            LogAssert.NoUnexpectedReceived();
        }
    }
}

// Producer:Betsy
