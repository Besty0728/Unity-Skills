using UnityEngine;
using UnityEngine.Events;

namespace UnitySkills.Tests.Fixtures
{
    /// <summary>Fixture: a UnityEvent to wire, and receivers for the argument kinds a persistent listener can carry.</summary>
    public class EventProbe : MonoBehaviour
    {
        public UnityEvent onFire = new UnityEvent();

        public int pings;
        public bool flag;
        public GameObject lastObject;
        public Material lastMaterial;

        public void Ping() => pings++;
        public void SetFlag(bool value) => flag = value;
        public void TakeObject(GameObject target) => lastObject = target;
        public void TakeMaterial(Material material) => lastMaterial = material;
    }
}

// Producer:Betsy
