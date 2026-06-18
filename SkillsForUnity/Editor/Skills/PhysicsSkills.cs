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
        [UnitySkill("physics_raycast", "Cast a ray and get hit info. Returns: {hit, collider, point, normal, distance}",
            Category = SkillCategory.Physics, Operation = SkillOperation.Query,
            Tags = new[] { "raycast", "collision", "detection", "line-of-sight" },
            Outputs = new[] { "hit", "collider", "point", "normal", "distance" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object PhysicsRaycast(
            float originX, float originY, float originZ,
            float dirX, float dirY, float dirZ,
            float maxDistance = 1000f,
            int layerMask = -1 // Default to all layers
        )
        {
            var origin = new Vector3(originX, originY, originZ);
            var direction = new Vector3(dirX, dirY, dirZ);
            if (direction.sqrMagnitude < 1e-6f)
                return new { error = "Direction vector cannot be zero" };
            direction.Normalize();

            if (Physics.Raycast(origin, direction, out RaycastHit hit, maxDistance, layerMask))
            {
                return new
                {
                    hit = true,
                    collider = hit.collider.name,
                    colliderEntityId = UnityObjectIdUtility.GetEntityId(hit.collider),
                    colliderInstanceId = UnityObjectIdUtility.GetObjectId(hit.collider),
                    objectName = hit.collider.gameObject.name,
                    objectEntityId = UnityObjectIdUtility.GetEntityId(hit.collider.gameObject),
                    objectInstanceId = UnityObjectIdUtility.GetObjectId(hit.collider.gameObject),
                    path = GameObjectFinder.GetPath(hit.collider.gameObject),
                    point = new { x = hit.point.x, y = hit.point.y, z = hit.point.z },
                    normal = new { x = hit.normal.x, y = hit.normal.y, z = hit.normal.z },
                    distance = hit.distance
                };
            }
            
            return new { hit = false };
        }

        [UnitySkill("physics_check_overlap", "Check for colliders in a sphere. Returns list of hit colliders.",
            Category = SkillCategory.Physics, Operation = SkillOperation.Query,
            Tags = new[] { "overlap", "sphere", "collision", "detection" },
            Outputs = new[] { "count", "colliders" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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

        [UnitySkill("physics_get_gravity", "Get global gravity setting",
            Category = SkillCategory.Physics, Operation = SkillOperation.Query,
            Tags = new[] { "gravity", "global", "setting" },
            Outputs = new[] { "x", "y", "z" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object PhysicsGetGravity()
        {
            var g = Physics.gravity;
            return new { x = g.x, y = g.y, z = g.z };
        }

        [UnitySkill("physics_set_gravity", "Set global gravity setting", TracksWorkflow = true,
            Category = SkillCategory.Physics, Operation = SkillOperation.Modify,
            Tags = new[] { "gravity", "global", "setting" },
            Outputs = new[] { "success", "gravity" })]
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

        [UnitySkill("physics_raycast_all", "Cast a ray and return ALL hits (penetrating)",
            Category = SkillCategory.Physics, Operation = SkillOperation.Query,
            Tags = new[] { "raycast", "penetrating", "collision", "detection" },
            Outputs = new[] { "count", "hits" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object PhysicsRaycastAll(
            float originX, float originY, float originZ,
            float dirX, float dirY, float dirZ,
            float maxDistance = 1000f, int layerMask = -1)
        {
            var origin = new Vector3(originX, originY, originZ);
            var direction = new Vector3(dirX, dirY, dirZ);
            if (direction.sqrMagnitude < 1e-6f)
                return new { error = "Direction vector cannot be zero" };
            direction.Normalize();
            var hits = Physics.RaycastAll(origin, direction, maxDistance, layerMask);
            var results = hits.OrderBy(h => h.distance).Select(h => new
            {
                objectName = h.collider.gameObject.name,
                entityId = UnityObjectIdUtility.GetEntityId(h.collider.gameObject),
                instanceId = UnityObjectIdUtility.GetObjectId(h.collider.gameObject),
                path = GameObjectFinder.GetPath(h.collider.gameObject),
                point = new { x = h.point.x, y = h.point.y, z = h.point.z },
                normal = new { x = h.normal.x, y = h.normal.y, z = h.normal.z },
                distance = h.distance
            }).ToArray();
            return new { count = results.Length, hits = results };
        }

        [UnitySkill("physics_spherecast", "Cast a sphere along a direction and get hit info",
            Category = SkillCategory.Physics, Operation = SkillOperation.Query,
            Tags = new[] { "spherecast", "collision", "detection", "sweep" },
            Outputs = new[] { "hit", "objectName", "point", "distance" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object PhysicsSphereCast(
            float originX, float originY, float originZ,
            float dirX, float dirY, float dirZ,
            float radius, float maxDistance = 1000f, int layerMask = -1)
        {
            var origin = new Vector3(originX, originY, originZ);
            var direction = new Vector3(dirX, dirY, dirZ);
            if (direction.sqrMagnitude < 1e-6f)
                return new { error = "Direction vector cannot be zero" };
            direction.Normalize();
            if (Physics.SphereCast(origin, radius, direction, out RaycastHit hit, maxDistance, layerMask))
            {
                return new
                {
                    hit = true,
                    objectName = hit.collider.gameObject.name,
                    entityId = UnityObjectIdUtility.GetEntityId(hit.collider.gameObject),
                    instanceId = UnityObjectIdUtility.GetObjectId(hit.collider.gameObject),
                    point = new { x = hit.point.x, y = hit.point.y, z = hit.point.z },
                    distance = hit.distance
                };
            }
            return new { hit = false };
        }

        [UnitySkill("physics_boxcast", "Cast a box along a direction and get hit info",
            Category = SkillCategory.Physics, Operation = SkillOperation.Query,
            Tags = new[] { "boxcast", "collision", "detection", "sweep" },
            Outputs = new[] { "hit", "objectName", "point", "distance" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object PhysicsBoxCast(
            float originX, float originY, float originZ,
            float dirX, float dirY, float dirZ,
            float halfExtentX = 0.5f, float halfExtentY = 0.5f, float halfExtentZ = 0.5f,
            float maxDistance = 1000f, int layerMask = -1)
        {
            var origin = new Vector3(originX, originY, originZ);
            var direction = new Vector3(dirX, dirY, dirZ);
            if (direction.sqrMagnitude < 1e-6f)
                return new { error = "Direction vector cannot be zero" };
            direction.Normalize();
            var halfExtents = new Vector3(halfExtentX, halfExtentY, halfExtentZ);
            if (Physics.BoxCast(origin, halfExtents, direction, out RaycastHit hit, Quaternion.identity, maxDistance, layerMask))
            {
                return new
                {
                    hit = true,
                    objectName = hit.collider.gameObject.name,
                    entityId = UnityObjectIdUtility.GetEntityId(hit.collider.gameObject),
                    instanceId = UnityObjectIdUtility.GetObjectId(hit.collider.gameObject),
                    point = new { x = hit.point.x, y = hit.point.y, z = hit.point.z },
                    distance = hit.distance
                };
            }
            return new { hit = false };
        }

        [UnitySkill("physics_overlap_box", "Check for colliders overlapping a box volume",
            Category = SkillCategory.Physics, Operation = SkillOperation.Query,
            Tags = new[] { "overlap", "box", "collision", "detection" },
            Outputs = new[] { "count", "colliders" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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

        [UnitySkill("physics_create_material", "Create a PhysicMaterial asset. Automatically uses `PhysicsMaterial` on Unity 6+, `PhysicMaterial` on earlier versions.", TracksWorkflow = true,
            Category = SkillCategory.Physics, Operation = SkillOperation.Create,
            Tags = new[] { "material", "friction", "bounciness", "asset" },
            Outputs = new[] { "success", "path" })]
        public static object PhysicsCreateMaterial(
            string name = "New PhysicMaterial", string savePath = "Assets",
            float dynamicFriction = 0.6f, float staticFriction = 0.6f, float bounciness = 0f)
        {
            if (Validate.Required(name, "name") is object nameErr) return nameErr;
            if (name.Contains("/") || name.Contains("\\") || name.Contains(".."))
                return new { error = "name must not contain path separators" };
            if (Validate.SafePath(savePath, "savePath") is object pathErr) return pathErr;

#if UNITY_6000_0_OR_NEWER
            var mat = new PhysicsMaterial(name)
#else
            var mat = new PhysicMaterial(name)
#endif
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

        [UnitySkill("physics_set_material", "Set PhysicMaterial on a collider (supports name/instanceId/path). Automatically uses `PhysicsMaterial` on Unity 6+, `PhysicMaterial` on earlier versions.", TracksWorkflow = true,
            Category = SkillCategory.Physics, Operation = SkillOperation.Modify,
            Tags = new[] { "material", "collider", "friction", "bounciness" },
            Outputs = new[] { "success", "gameObject", "material" },
            RequiresInput = new[] { "gameObject", "physicMaterial" })]
        public static object PhysicsSetMaterial(
            string materialPath, string name = null, int instanceId = 0, string path = null)
        {
            var (go, err) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (err != null) return err;
            var collider = go.GetComponent<Collider>();
            if (collider == null) return new { error = $"No Collider on {go.name}" };
#if UNITY_6000_0_OR_NEWER
            var mat = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(materialPath);
#else
            var mat = AssetDatabase.LoadAssetAtPath<PhysicMaterial>(materialPath);
#endif
            if (mat == null) return new { error = $"PhysicMaterial not found: {materialPath}" };
            WorkflowManager.SnapshotObject(collider);
            Undo.RecordObject(collider, "Set PhysicMaterial");
            collider.sharedMaterial = mat;
            return new { success = true, gameObject = go.name, material = materialPath };
        }

        [UnitySkill("physics_get_layer_collision", "Get whether two layers collide",
            Category = SkillCategory.Physics, Operation = SkillOperation.Query,
            Tags = new[] { "layer", "collision", "matrix" },
            Outputs = new[] { "layer1", "layer2", "collisionEnabled" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object PhysicsGetLayerCollision(int layer1, int layer2)
        {
            bool ignored = Physics.GetIgnoreLayerCollision(layer1, layer2);
            return new { layer1, layer2, collisionEnabled = !ignored };
        }

        [UnitySkill("physics_set_layer_collision", "Set whether two layers collide", TracksWorkflow = true,
            Category = SkillCategory.Physics, Operation = SkillOperation.Modify,
            Tags = new[] { "layer", "collision", "matrix" },
            Outputs = new[] { "success", "layer1", "layer2", "collisionEnabled" })]
        public static object PhysicsSetLayerCollision(int layer1, int layer2, bool enableCollision = true)
        {
            Physics.IgnoreLayerCollision(layer1, layer2, !enableCollision);
            return new { success = true, layer1, layer2, collisionEnabled = enableCollision };
        }
    }
}
