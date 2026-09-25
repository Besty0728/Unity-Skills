using UnityEngine;

namespace UnitySkills.Tests.Fixtures
{
    /// <summary>
    /// Fixture: abstract requirement target, standing in for an abstract built-in such as Collider. Fixtures live in a
    /// runtime assembly, and a clean project like CI's enables no optional engine module (no Physics) for runtime
    /// assemblies, so dependency fixtures must not reference module types.
    /// </summary>
    public abstract class RequiredBaseProbe : MonoBehaviour
    {
    }
}

// Producer:Betsy
