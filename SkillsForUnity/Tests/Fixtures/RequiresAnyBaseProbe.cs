using UnityEngine;

namespace UnitySkills.Tests.Fixtures
{
    /// <summary>
    /// Fixture: requires the abstract RequiredBaseProbe, which any subclass satisfies. Add a concrete leaf first, since
    /// Unity cannot auto-add an abstract requirement.
    /// </summary>
    [RequireComponent(typeof(RequiredBaseProbe))]
    public class RequiresAnyBaseProbe : MonoBehaviour
    {
    }
}

// Producer:Betsy
