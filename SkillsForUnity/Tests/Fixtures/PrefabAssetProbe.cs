using UnityEngine;

namespace UnitySkills.Tests.Fixtures
{
    /// <summary>Serialized fields used to verify saved prefab references and nested values.</summary>
    public sealed class PrefabAssetProbe : MonoBehaviour
    {
        [System.Serializable]
        public sealed class Settings { public float speed = 2; }
        public GameObject target;
        public Component component;
        public Material material;
        public Settings settings = new Settings();
        public int[] values = new[] { 1, 2 };
        public string label = "original";
    }
}

// Producer:Betsy
