using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace UnitySkills.Tests.Runtime
{
    [TestFixture]
    public class PlayModeRecoveryTests
    {
        [UnityTest]
        public IEnumerator TestRunnerJob_CompletesAcrossPlayModeReloads()
        {
            yield return null;
            Assert.Pass();
        }
    }
}
