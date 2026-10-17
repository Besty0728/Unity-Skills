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
        public Component lastComponent;
        public string lastPick;

        public void Ping() => pings++;
        public void SetFlag(bool value) => flag = value;
        public void TakeObject(GameObject target) => lastObject = target;
        public void TakeMaterial(Material material) => lastMaterial = material;
        public void TakeComponent(Component component) => lastComponent = component;

        // Three overloads that accept a Transform: a listener must pick the nearest one.
        public void Pick(UnityEngine.Object value) => lastPick = "Object";
        public void Pick(Component value) => lastPick = "Component";
        public void Pick(Transform value) => lastPick = "Transform";
    }
}

// Producer:Betsy
