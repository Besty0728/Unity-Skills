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
            var go = GameObject.Find(gameObjectName);
            if (go == null)
                return new { error = $"GameObject not found: {gameObjectName}" };

            var dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var prefab = PrefabUtility.SaveAsPrefabAsset(go, savePath);
            return new { success = true, prefabPath = savePath, name = prefab.name };
        }

        [UnitySkill("prefab_instantiate", "Instantiate a prefab in the scene")]
        public static object PrefabInstantiate(string prefabPath, float x = 0, float y = 0, float z = 0, string name = null)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
                return new { error = $"Prefab not found: {prefabPath}" };

            var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            instance.transform.position = new Vector3(x, y, z);
            
            if (!string.IsNullOrEmpty(name))
                instance.name = name;

            Undo.RegisterCreatedObjectUndo(instance, "Instantiate Prefab");

            return new { success = true, name = instance.name, instanceId = instance.GetInstanceID() };
        }

        [UnitySkill("prefab_instantiate_batch", "Instantiate multiple prefabs (Efficient). items: JSON array of {prefabPath, x, y, z, name, rotX, rotY, rotZ, scaleX, scaleY, scaleZ}")]
        public static object PrefabInstantiateBatch(string items)
        {
            if (string.IsNullOrEmpty(items))
                return new { error = "items parameter is required. Example: [{\"prefabPath\":\"Assets/Cube.prefab\",\"x\":0,\"y\":0,\"z\":0}]" };

            try
            {
                var itemList = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.List<BatchInstantiateItem>>(items);
                if (itemList == null || itemList.Count == 0)
                    return new { error = "items parameter is empty or invalid JSON" };

                var results = new System.Collections.Generic.List<object>();
                int successCount = 0;
                int failCount = 0;

                // Cache loaded prefabs to avoid repeated AssetDatabase calls
                var prefabCache = new System.Collections.Generic.Dictionary<string, GameObject>();

                foreach (var item in itemList)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(item.prefabPath))
                        {
                            results.Add(new { success = false, error = "prefabPath required" });
                            failCount++;
                            continue;
                        }

                        if (!prefabCache.TryGetValue(item.prefabPath, out var prefab))
                        {
                            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(item.prefabPath);
                            if (prefab == null)
                            {
                                // Try finding by name if path fails
                                var guids = AssetDatabase.FindAssets(item.prefabPath + " t:Prefab");
                                if (guids.Length > 0)
                                    prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guids[0]));
                            }
                            
                            if (prefab != null)
                                prefabCache[item.prefabPath] = prefab;
                        }

                        if (prefab == null)
                        {
                            results.Add(new { target = item.name ?? "unnamed", success = false, error = $"Prefab not found: {item.prefabPath}" });
                            failCount++;
                            continue;
                        }

                        var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                        instance.transform.position = new Vector3(item.x, item.y, item.z);
                        
                        // Optional rotation and scale
                        if (item.rotX != 0 || item.rotY != 0 || item.rotZ != 0)
                            instance.transform.eulerAngles = new Vector3(item.rotX, item.rotY, item.rotZ);
                        
                        if (item.scaleX != 1 || item.scaleY != 1 || item.scaleZ != 1)
                            instance.transform.localScale = new Vector3(item.scaleX, item.scaleY, item.scaleZ);
                        
                        if (!string.IsNullOrEmpty(item.name))
                            instance.name = item.name;

                        Undo.RegisterCreatedObjectUndo(instance, "Batch Instantiate Prefab");
                        results.Add(new { 
                            success = true, 
                            name = instance.name, 
                            instanceId = instance.GetInstanceID(),
                            position = new { x = item.x, y = item.y, z = item.z }
                        });
                        successCount++;
                    }
                    catch (System.Exception ex)
                    {
                        results.Add(new { target = item.name ?? "unnamed", success = false, error = ex.Message });
                        failCount++;
                    }
                }

                return new
                {
                    success = failCount == 0,
                    totalItems = itemList.Count,
                    successCount,
                    failCount,
                    results
                };
            }
            catch (System.Exception ex)
            {
                return new { error = $"Failed to parse items JSON: {ex.Message}" };
            }
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
            var go = GameObject.Find(gameObjectName);
            if (go == null)
                return new { error = $"GameObject not found: {gameObjectName}" };

            var prefabRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (prefabRoot == null)
                return new { error = "GameObject is not a prefab instance" };

            var prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(prefabRoot);
            PrefabUtility.ApplyPrefabInstance(prefabRoot, InteractionMode.UserAction);

            return new { success = true, appliedTo = prefabPath };
        }

        [UnitySkill("prefab_unpack", "Unpack a prefab instance")]
        public static object PrefabUnpack(string gameObjectName, bool completely = false)
        {
            var go = GameObject.Find(gameObjectName);
            if (go == null)
                return new { error = $"GameObject not found: {gameObjectName}" };

            var mode = completely ? PrefabUnpackMode.Completely : PrefabUnpackMode.OutermostRoot;
            PrefabUtility.UnpackPrefabInstance(go, mode, InteractionMode.UserAction);

            return new { success = true, unpacked = gameObjectName };
        }
    }
}
