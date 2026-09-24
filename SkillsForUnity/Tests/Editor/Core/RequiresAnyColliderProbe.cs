using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Fixture: requires the abstract Collider, which any subclass satisfies. Add a concrete collider first, since
    /// Unity cannot auto-add an abstract requirement.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class RequiresAnyColliderProbe : MonoBehaviour
    {
    }
}

// Producer:Betsy
