using UnityEngine;

namespace UnitySkills.Tests.Fixtures
{
    /// <summary>Fixture: requires a RequiredLeafProbe exactly (component_remove dependency checks).</summary>
    [RequireComponent(typeof(RequiredLeafProbe))]
    public class RequiresLeafProbe : MonoBehaviour
    {
    }
}

// Producer:Betsy
