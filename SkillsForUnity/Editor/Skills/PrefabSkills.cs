using UnityEngine;
using UnityEditor;
using System.IO;

namespace UnitySkills
{
    /// <summary>
    /// Prefab management skills - create, edit, save.
    /// </summary>
    public static class PrefabSkills
    {
        [UnitySkill("prefab_create", "Create a prefab from a GameObject")]
        public static object PrefabCreate(string gameObjectName, string savePath)
        {
            if (Validate.SafePath(savePath, "savePath") is object pathErr) return pathErr;

            var (go, findErr) = GameObjectFinder.FindOrError(name: gameObjectName);
            if (findErr != null) return findErr;

            var dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // 使用 SaveAsPrefabAssetAndConnect 将场景物体连接为预制体实例
            var prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(go, savePath, InteractionMode.UserAction);

            // 记录新创建的预制体资产
            WorkflowManager.SnapshotCreatedAsset(prefab);

            return new { success = true, prefabPath = savePath, name = prefab.name };
        }

        [UnitySkill("prefab_instantiate", "Instantiate a prefab in the scene")]
        public static object PrefabInstantiate(string prefabPath, float x = 0, float y = 0, float z = 0, string name = null)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
                return new { error = $"Prefab not found: {prefabPath}" };

            var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (instance == null)
                return new { error = $"Failed to instantiate prefab: {prefabPath}" };
            instance.transform.position = new Vector3(x, y, z);

            if (!string.IsNullOrEmpty(name))
                instance.name = name;

            Undo.RegisterCreatedObjectUndo(instance, "Instantiate Prefab");
            WorkflowManager.SnapshotObject(instance, SnapshotType.Created);

            return new { success = true, name = instance.name, instanceId = instance.GetInstanceID() };
        }

        [UnitySkill("prefab_instantiate_batch", "Instantiate multiple prefabs (Efficient). items: JSON array of {prefabPath, x, y, z, name, rotX, rotY, rotZ, scaleX, scaleY, scaleZ}")]
        public static object PrefabInstantiateBatch(string items)
        {
            // Cache loaded prefabs to avoid repeated AssetDatabase calls
            var prefabCache = new System.Collections.Generic.Dictionary<string, GameObject>();

            return BatchExecutor.Execute<BatchInstantiateItem>(items, item =>
            {
                if (string.IsNullOrEmpty(item.prefabPath))
                    throw new System.Exception("prefabPath required");

                if (!prefabCache.TryGetValue(item.prefabPath, out var prefab))
                {
                    prefab = AssetDatabase.LoadAssetAtPath<GameObject>(item.prefabPath);
                    if (prefab == null)
                    {
                        var guids = AssetDatabase.FindAssets(item.prefabPath + " t:Prefab");
                        if (guids.Length > 0)
                            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guids[0]));
                    }

                    if (prefab != null)
                        prefabCache[item.prefabPath] = prefab;
                }

                if (prefab == null)
                    throw new System.Exception($"Prefab not found: {item.prefabPath}");

                var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                if (instance == null)
                    throw new System.Exception($"Failed to instantiate prefab: {item.prefabPath}");
                instance.transform.position = new Vector3(item.x, item.y, item.z);

                if (item.rotX != 0 || item.rotY != 0 || item.rotZ != 0)
                    instance.transform.eulerAngles = new Vector3(item.rotX, item.rotY, item.rotZ);

                if (item.scaleX != 1 || item.scaleY != 1 || item.scaleZ != 1)
                    instance.transform.localScale = new Vector3(item.scaleX, item.scaleY, item.scaleZ);

                if (!string.IsNullOrEmpty(item.name))
                    instance.name = item.name;

                Undo.RegisterCreatedObjectUndo(instance, "Batch Instantiate Prefab");
                WorkflowManager.SnapshotObject(instance, SnapshotType.Created);
                return new
                {
                    success = true,
                    name = instance.name,
                    instanceId = instance.GetInstanceID(),
                    position = new { x = item.x, y = item.y, z = item.z }
                };
            }, item => item.prefabPath);
        }

        private class BatchInstantiateItem
        {
            public string prefabPath { get; set; }
            public float x { get; set; }
            public float y { get; set; }
            public float z { get; set; }
            public string name { get; set; }
            public float rotX { get; set; }
            public float rotY { get; set; }
            public float rotZ { get; set; }
            public float scaleX { get; set; } = 1;
            public float scaleY { get; set; } = 1;
            public float scaleZ { get; set; } = 1;
        }

        [UnitySkill("prefab_apply", "Apply changes from instance to prefab")]
        public static object PrefabApply(string gameObjectName)
        {
            var (go, goErr) = GameObjectFinder.FindOrError(name: gameObjectName);
            if (goErr != null) return goErr;

            var prefabRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (prefabRoot == null)
                return new { error = "GameObject is not a prefab instance" };

            WorkflowManager.SnapshotObject(prefabRoot);
            var prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(prefabRoot);
            PrefabUtility.ApplyPrefabInstance(prefabRoot, InteractionMode.UserAction);

            return new { success = true, appliedTo = prefabPath };
        }

        [UnitySkill("prefab_unpack", "Unpack a prefab instance")]
        public static object PrefabUnpack(string gameObjectName, bool completely = false)
        {
            var (go, findErr) = GameObjectFinder.FindOrError(name: gameObjectName);
            if (findErr != null) return findErr;

            WorkflowManager.SnapshotObject(go);
            var mode = completely ? PrefabUnpackMode.Completely : PrefabUnpackMode.OutermostRoot;
            PrefabUtility.UnpackPrefabInstance(go, mode, InteractionMode.UserAction);

            return new { success = true, unpacked = gameObjectName };
        }

        [UnitySkill("prefab_get_overrides", "Get list of property overrides on a prefab instance")]
        public static object PrefabGetOverrides(string name = null, int instanceId = 0)
        {
            var (go, goErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId);
            if (goErr != null) return goErr;

            var prefabRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (prefabRoot == null) return new { error = "Not a prefab instance" };

            var overrides = PrefabUtility.GetPropertyModifications(prefabRoot);
            var addedComponents = PrefabUtility.GetAddedComponents(prefabRoot);
            var removedComponents = PrefabUtility.GetRemovedComponents(prefabRoot);
            var addedObjects = PrefabUtility.GetAddedGameObjects(prefabRoot);

            var propOverrides = new System.Collections.Generic.List<object>();
            if (overrides != null)
            {
                foreach (var o in overrides)
                {
                    if (o.target == null) continue;
                    propOverrides.Add(new { 
                        target = o.target.name, 
                        property = o.propertyPath, 
                        value = o.value 
                    });
                }
            }

            return new
            {
                success = true,
                prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(prefabRoot),
                propertyOverrides = propOverrides.Count,
                addedComponents = addedComponents.Count,
                removedComponents = removedComponents.Count,
                addedGameObjects = addedObjects.Count,
                hasOverrides = propOverrides.Count > 0 || addedComponents.Count > 0 || removedComponents.Count > 0
            };
        }

        [UnitySkill("prefab_revert_overrides", "Revert all overrides on a prefab instance back to prefab values")]
        public static object PrefabRevertOverrides(string name = null, int instanceId = 0)
        {
            var (go, findErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId);
            if (findErr != null) return findErr;

            var prefabRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (prefabRoot == null) return new { error = "Not a prefab instance" };

            WorkflowManager.SnapshotObject(prefabRoot);
            Undo.RecordObject(prefabRoot, "Revert Prefab Overrides");
            PrefabUtility.RevertPrefabInstance(prefabRoot, InteractionMode.UserAction);

            return new { success = true, reverted = prefabRoot.name };
        }

        [UnitySkill("prefab_apply_overrides", "Apply all overrides from instance to source prefab asset")]
        public static object PrefabApplyOverrides(string name = null, int instanceId = 0)
        {
            var (go, goErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId);
            if (goErr != null) return goErr;

            var prefabRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (prefabRoot == null) return new { error = "Not a prefab instance" };

            WorkflowManager.SnapshotObject(prefabRoot);
            var prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(prefabRoot);
            PrefabUtility.ApplyPrefabInstance(prefabRoot, InteractionMode.UserAction);

            return new { success = true, appliedTo = prefabPath };
        }
        [UnitySkill("prefab_create_variant", "Create a prefab variant from an existing prefab")]
        public static object PrefabCreateVariant(string sourcePrefabPath, string variantPath)
        {
            if (Validate.Required(sourcePrefabPath, "sourcePrefabPath") is object err) return err;
            if (Validate.SafePath(variantPath, "variantPath") is object pathErr) return pathErr;

            var source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePrefabPath);
            if (source == null) return new { error = $"Prefab not found: {sourcePrefabPath}" };

            var dir = Path.GetDirectoryName(variantPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var instance = PrefabUtility.InstantiatePrefab(source) as GameObject;
            var variant = PrefabUtility.SaveAsPrefabAsset(instance, variantPath);
            Object.DestroyImmediate(instance);

            return new { success = true, sourcePath = sourcePrefabPath, variantPath, name = variant.name };
        }

        [UnitySkill("prefab_find_instances", "Find all instances of a prefab in the current scene")]
        public static object PrefabFindInstances(string prefabPath, int limit = 50)
        {
            if (Validate.Required(prefabPath, "prefabPath") is object err) return err;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) return new { error = $"Prefab not found: {prefabPath}" };

            var allObjects = Object.FindObjectsOfType<GameObject>();
            var instances = allObjects
                .Where(go => PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go) == prefabPath)
                .Take(limit)
                .Select(go => new { name = go.name, path = GameObjectFinder.GetPath(go), instanceId = go.GetInstanceID() })
                .ToArray();

            return new { success = true, prefabPath, count = instances.Length, instances };
        }
    }
}
