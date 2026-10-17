using UnityEngine;

namespace UnitySkills.Tests.Fixtures
{
    /// <summary>Fixture: a ScriptableObject asset with a parameterless method, a UnityEvent listener target that is an asset, not a scene object.</summary>
    public class EventProbeAsset : ScriptableObject
    {
        public int hits;

        public void Hit() => hits++;
    }
}

// Producer:Betsy
