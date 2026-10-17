using UnityEngine;

namespace UnitySkills.Tests.Fixtures
{
    /// <summary>Fixture: one serialized field of every type serialized-property reads describe as comma-separated numbers.</summary>
    public class FormattingProbe : MonoBehaviour
    {
        public float scalar;
        public Vector2 vector2;
        public Vector3 vector3;
        public Vector4 vector4;
        public Color color;
        public Quaternion quaternion = Quaternion.identity;
        public Rect rect;
        public Bounds bounds;
        public Vector2Int vector2Int;
        public Vector3Int vector3Int;
        public RectInt rectInt;
        public BoundsInt boundsInt;
    }
}

// Producer:Betsy
