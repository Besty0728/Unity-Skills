using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>Fixture: requires a BoxCollider exactly (component_remove dependency checks).</summary>
    [RequireComponent(typeof(BoxCollider))]
    public class RequiresBoxColliderProbe : MonoBehaviour
    {
    }
}

// Producer:Betsy
