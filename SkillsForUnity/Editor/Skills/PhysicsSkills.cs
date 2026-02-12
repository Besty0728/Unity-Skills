using UnityEngine;
using UnityEditor;
using System.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Physics skills - raycasts, overlap checks, gravity.
    /// </summary>
    public static class PhysicsSkills
    {
        [UnitySkill("physics_raycast", "Cast a ray and get hit info. Returns: {hit, collider, point, normal, distance}")]
        public static object PhysicsRaycast(
            float originX, float originY, float originZ,
            float dirX, float dirY, float dirZ,
            float maxDistance = 1000f,
            int layerMask = -1 // Default to all layers
        )
        {
            var origin = new Vector3(originX, originY, originZ);
            var direction = new Vector3(dirX, dirY, dirZ);
            
            if (Physics.Raycast(origin, direction, out RaycastHit hit, maxDistance, layerMask))
            {
                return new
                {
                    hit = true,
                    collider = hit.collider.name,
                    colliderInstanceId = hit.collider.GetInstanceID(),
                    objectName = hit.collider.gameObject.name,
                    objectInstanceId = hit.collider.gameObject.GetInstanceID(),
                    path = GameObjectFinder.GetPath(hit.collider.gameObject),
                    point = new { x = hit.point.x, y = hit.point.y, z = hit.point.z },
                    normal = new { x = hit.normal.x, y = hit.normal.y, z = hit.normal.z },
                    distance = hit.distance
                };
            }
            
            return new { hit = false };
        }

        [UnitySkill("physics_check_overlap", "Check for colliders in a sphere. Returns list of hit colliders.")]
        public static object PhysicsCheckOverlap(
            float x, float y, float z,
            float radius,
            int layerMask = -1
        )
        {
            var position = new Vector3(x, y, z);
            var colliders = Physics.OverlapSphere(position, radius, layerMask);
            
            var results = colliders.Select(c => new
            {
                collider = c.name,
                objectName = c.gameObject.name,
                path = GameObjectFinder.GetPath(c.gameObject),
                isTrigger = c.isTrigger
            }).ToArray();

            return new
            {
                count = results.Length,
                colliders = results
            };
        }

        [UnitySkill("physics_get_gravity", "Get global gravity setting")]
        public static object PhysicsGetGravity()
        {
            var g = Physics.gravity;
            return new { x = g.x, y = g.y, z = g.z };
        }

        [UnitySkill("physics_set_gravity", "Set global gravity setting")]
        public static object PhysicsSetGravity(float x, float y, float z)
        {
            // Record for Undo support
            var dynamicsManager = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/DynamicsManager.asset");
            if (dynamicsManager != null && dynamicsManager.Length > 0)
            {
                Undo.RecordObject(dynamicsManager[0], "Set Gravity");
            }

            Physics.gravity = new Vector3(x, y, z);

            if (dynamicsManager != null && dynamicsManager.Length > 0)
            {
                EditorUtility.SetDirty(dynamicsManager[0]);
            }

            return new { success = true, gravity = new { x, y, z } };
        }
    }
}
