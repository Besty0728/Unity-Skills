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
            // Record for Undo support via DynamicsManager asset
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/DynamicsManager.asset");
            if (assets != null && assets.Length > 0)
            {
                Undo.RecordObject(assets[0], "Set Gravity");
            }

            Physics.gravity = new Vector3(x, y, z);

            if (assets != null && assets.Length > 0)
            {
                EditorUtility.SetDirty(assets[0]);
            }

            return new { success = true, gravity = new { x, y, z } };
        }

        [UnitySkill("physics_raycast_all", "Cast a ray and return ALL hits (penetrating)")]
        public static object PhysicsRaycastAll(
            float originX, float originY, float originZ,
            float dirX, float dirY, float dirZ,
            float maxDistance = 1000f, int layerMask = -1)
        {
            var origin = new Vector3(originX, originY, originZ);
            var direction = new Vector3(dirX, dirY, dirZ);
            var hits = Physics.RaycastAll(origin, direction, maxDistance, layerMask);
            var results = hits.OrderBy(h => h.distance).Select(h => new
            {
                objectName = h.collider.gameObject.name,
                instanceId = h.collider.gameObject.GetInstanceID(),
                path = GameObjectFinder.GetPath(h.collider.gameObject),
                point = new { x = h.point.x, y = h.point.y, z = h.point.z },
                normal = new { x = h.normal.x, y = h.normal.y, z = h.normal.z },
                distance = h.distance
            }).ToArray();
            return new { count = results.Length, hits = results };
        }

        [UnitySkill("physics_spherecast", "Cast a sphere along a direction and get hit info")]
        public static object PhysicsSphereCast(
            float originX, float originY, float originZ,
            float dirX, float dirY, float dirZ,
            float radius, float maxDistance = 1000f, int layerMask = -1)
        {
            var origin = new Vector3(originX, originY, originZ);
            var direction = new Vector3(dirX, dirY, dirZ);
            if (Physics.SphereCast(origin, radius, direction, out RaycastHit hit, maxDistance, layerMask))
            {
                return new
                {
                    hit = true,
                    objectName = hit.collider.gameObject.name,
                    instanceId = hit.collider.gameObject.GetInstanceID(),
                    point = new { x = hit.point.x, y = hit.point.y, z = hit.point.z },
                    distance = hit.distance
                };
            }
            return new { hit = false };
        }

        [UnitySkill("physics_boxcast", "Cast a box along a direction and get hit info")]
        public static object PhysicsBoxCast(
            float originX, float originY, float originZ,
            float dirX, float dirY, float dirZ,
            float halfExtentX = 0.5f, float halfExtentY = 0.5f, float halfExtentZ = 0.5f,
            float maxDistance = 1000f, int layerMask = -1)
        {
            var origin = new Vector3(originX, originY, originZ);
            var direction = new Vector3(dirX, dirY, dirZ);
            var halfExtents = new Vector3(halfExtentX, halfExtentY, halfExtentZ);
            if (Physics.BoxCast(origin, halfExtents, direction, out RaycastHit hit, Quaternion.identity, maxDistance, layerMask))
            {
                return new
                {
                    hit = true,
                    objectName = hit.collider.gameObject.name,
                    instanceId = hit.collider.gameObject.GetInstanceID(),
                    point = new { x = hit.point.x, y = hit.point.y, z = hit.point.z },
                    distance = hit.distance
                };
            }
            return new { hit = false };
        }

        [UnitySkill("physics_overlap_box", "Check for colliders overlapping a box volume")]
        public static object PhysicsOverlapBox(
            float x, float y, float z,
            float halfExtentX = 0.5f, float halfExtentY = 0.5f, float halfExtentZ = 0.5f,
            int layerMask = -1)
        {
            var center = new Vector3(x, y, z);
            var halfExtents = new Vector3(halfExtentX, halfExtentY, halfExtentZ);
            var colliders = Physics.OverlapBox(center, halfExtents, Quaternion.identity, layerMask);
            var results = colliders.Select(c => new
            {
                objectName = c.gameObject.name,
                path = GameObjectFinder.GetPath(c.gameObject),
                isTrigger = c.isTrigger
            }).ToArray();
            return new { count = results.Length, colliders = results };
        }

        [UnitySkill("physics_create_material", "Create a PhysicMaterial asset")]
        public static object PhysicsCreateMaterial(
            string name = "New PhysicMaterial", string savePath = "Assets",
            float dynamicFriction = 0.6f, float staticFriction = 0.6f, float bounciness = 0f)
        {
            var mat = new PhysicsMaterial(name)
            {
                dynamicFriction = dynamicFriction,
                staticFriction = staticFriction,
                bounciness = bounciness
            };
            var path = System.IO.Path.Combine(savePath, name + ".physicMaterial");
            path = AssetDatabase.GenerateUniqueAssetPath(path);
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            AssetDatabase.CreateAsset(mat, path);
            AssetDatabase.SaveAssets();
            return new { success = true, path };
        }

        [UnitySkill("physics_set_material", "Set PhysicMaterial on a collider (supports name/instanceId/path)")]
        public static object PhysicsSetMaterial(
            string materialPath, string name = null, int instanceId = 0, string path = null)
        {
            var (go, err) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (err != null) return err;
            var collider = go.GetComponent<Collider>();
            if (collider == null) return new { error = $"No Collider on {go.name}" };
            var mat = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(materialPath);
            if (mat == null) return new { error = $"PhysicMaterial not found: {materialPath}" };
            WorkflowManager.SnapshotObject(collider);
            Undo.RecordObject(collider, "Set PhysicMaterial");
            collider.sharedMaterial = mat;
            return new { success = true, gameObject = go.name, material = materialPath };
        }

        [UnitySkill("physics_get_layer_collision", "Get whether two layers collide")]
        public static object PhysicsGetLayerCollision(int layer1, int layer2)
        {
            bool ignored = Physics.GetIgnoreLayerCollision(layer1, layer2);
            return new { layer1, layer2, collisionEnabled = !ignored };
        }

        [UnitySkill("physics_set_layer_collision", "Set whether two layers collide")]
        public static object PhysicsSetLayerCollision(int layer1, int layer2, bool enableCollision = true)
        {
            Physics.IgnoreLayerCollision(layer1, layer2, !enableCollision);
            return new { success = true, layer1, layer2, collisionEnabled = enableCollision };
        }
    }
}
