using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>Fixture: a ScriptableObject with a bool field (strict bool parsing in scriptableobject_set_batch).</summary>
    public class BoolProbeAsset : ScriptableObject
    {
        public bool flag = true;
        public int count;
    }
}

// Producer:Betsy
