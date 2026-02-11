using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// GameObject management skills - create, modify, delete, find.
    /// Now supports finding by name, instanceId, or path.
    /// </summary>
    public static class GameObjectSkills
    {
        [UnitySkill("gameobject_create_batch", "Create multiple GameObjects in one call (Efficient). items: JSON array of {name, primitiveType, x, y, z}")]
        public static object GameObjectCreateBatch(string items)
        {
            if (string.IsNullOrEmpty(items))
                return new { error = "items parameter is required. Example: [{\"name\":\"Cube1\",\"primitiveType\":\"Cube\",\"x\":0,\"y\":0,\"z\":0}]" };

            try
            {
                var itemList = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BatchCreateItem>>(items);
                if (itemList == null || itemList.Count == 0)
                    return new { error = "items parameter is empty or invalid JSON" };

                var results = new List<object>();
                int successCount = 0;
                int failCount = 0;

                foreach (var item in itemList)
                {
                    try
                    {
                        GameObject go;
                        string primitiveType = item.primitiveType;

                        // Support "Empty", "", or null to create an empty GameObject
                        if (string.IsNullOrEmpty(primitiveType) ||
                            primitiveType.Equals("Empty", System.StringComparison.OrdinalIgnoreCase) ||
                            primitiveType.Equals("None", System.StringComparison.OrdinalIgnoreCase))
                        {
                            go = new GameObject(item.name);
                            primitiveType = null; // 标准化为 null
                        }
                        else if (System.Enum.TryParse<PrimitiveType>(primitiveType, true, out var pt))
                        {
                            go = GameObject.CreatePrimitive(pt);
                            go.name = item.name;
                        }
                        else
                        {
                            results.Add(new { name = item.name, success = false, error = $"Unknown primitive type: {primitiveType}" });
                            failCount++;
                            continue;
                        }

                        go.transform.position = new Vector3(item.x, item.y, item.z);
                        if (item.rotX != 0 || item.rotY != 0 || item.rotZ != 0)
                            go.transform.eulerAngles = new Vector3(item.rotX, item.rotY, item.rotZ);
                        if (item.scaleX != 1 || item.scaleY != 1 || item.scaleZ != 1)
                            go.transform.localScale = new Vector3(item.scaleX, item.scaleY, item.scaleZ);

                        Undo.RegisterCreatedObjectUndo(go, "Batch Create " + item.name);
                        WorkflowManager.SnapshotCreatedGameObject(go, primitiveType);

                        results.Add(new
                        {
                            success = true,
                            name = go.name,
                            instanceId = go.GetInstanceID(),
                            path = GameObjectFinder.GetPath(go),
                            position = new { x = item.x, y = item.y, z = item.z }
                        });
                        successCount++;
                    }
                    catch (System.Exception ex)
                    {
                        results.Add(new { name = item.name, success = false, error = ex.Message });
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

        private class BatchCreateItem
        {
            public string name { get; set; }
            public string primitiveType { get; set; }
            public float x { get; set; }
            public float y { get; set; }
            public float z { get; set; }
            public float rotX { get; set; }
            public float rotY { get; set; }
            public float rotZ { get; set; }
            public float scaleX { get; set; } = 1;
            public float scaleY { get; set; } = 1;
            public float scaleZ { get; set; } = 1;
        }

        [UnitySkill("gameobject_create", "Create a new GameObject. primitiveType: Cube, Sphere, Capsule, Cylinder, Plane, Quad, or Empty/null for empty object")]
        public static object GameObjectCreate(string name, string primitiveType = null, float x = 0, float y = 0, float z = 0)
        {
            GameObject go;

            // Support "Empty", "", or null to create an empty GameObject
            if (string.IsNullOrEmpty(primitiveType) ||
                primitiveType.Equals("Empty", System.StringComparison.OrdinalIgnoreCase) ||
                primitiveType.Equals("None", System.StringComparison.OrdinalIgnoreCase))
            {
                go = new GameObject(name);
                primitiveType = null; // 标准化为 null
            }
            else if (System.Enum.TryParse<PrimitiveType>(primitiveType, true, out var pt))
            {
                go = GameObject.CreatePrimitive(pt);
                go.name = name;
            }
            else
            {
                return new { error = $"Unknown primitive type: {primitiveType}. Use: Cube, Sphere, Capsule, Cylinder, Plane, Quad, or Empty/None for empty object" };
            }

            go.transform.position = new Vector3(x, y, z);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            WorkflowManager.SnapshotCreatedGameObject(go, primitiveType);

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                path = GameObjectFinder.GetPath(go),
                position = new { x, y, z }
            };
        }

        [UnitySkill("gameobject_rename", "Rename a GameObject (supports name/instanceId/path). Returns: {success, oldName, newName, instanceId}")]
        public static object GameObjectRename(string name = null, int instanceId = 0, string path = null, string newName = null)
        {
            if (Validate.Required(newName, "newName") is object err) return err;

            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var oldName = go.name;
            WorkflowManager.SnapshotObject(go);
            Undo.RecordObject(go, "Rename GameObject");
            go.name = newName;

            return new { 
                success = true, 
                oldName, 
                newName = go.name, 
                instanceId = go.GetInstanceID(),
                path = GameObjectFinder.GetPath(go)
            };
        }

        [UnitySkill("gameobject_rename_batch", "Rename multiple GameObjects in one call (Efficient). items: JSON array of {name, instanceId, path, newName}. Returns array with oldName, newName for each.")]
        public static object GameObjectRenameBatch(string items)
        {
            if (string.IsNullOrEmpty(items))
                return new { error = "items parameter is required. Example: [{\"instanceId\":12345,\"newName\":\"Cube_01\"}]" };

            try
            {
                var itemList = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BatchRenameItem>>(items);
                if (itemList == null || itemList.Count == 0)
                    return new { error = "items parameter is empty or invalid JSON" };

                var results = new List<object>();
                int successCount = 0;
                int failCount = 0;

                foreach (var item in itemList)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(item.newName))
                        {
                            results.Add(new { target = item.name ?? item.path ?? item.instanceId.ToString(), success = false, error = "newName is required" });
                            failCount++;
                            continue;
                        }

                        var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path);
                        if (error != null)
                        {
                            results.Add(new { target = item.name ?? item.path ?? item.instanceId.ToString(), success = false, error = "Object not found" });
                            failCount++;
                            continue;
                        }

                        var oldName = go.name;
                        WorkflowManager.SnapshotObject(go);
                        Undo.RecordObject(go, "Batch Rename " + go.name);
                        go.name = item.newName;

                        results.Add(new { success = true, oldName, newName = go.name, instanceId = go.GetInstanceID() });
                        successCount++;
                    }
                    catch (System.Exception ex)
                    {
                        results.Add(new { target = item.name ?? item.path, success = false, error = ex.Message });
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

        private class BatchRenameItem
        {
            public string name { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public string newName { get; set; }
        }

        [UnitySkill("gameobject_delete", "Delete a GameObject (supports name/instanceId/path)")]
        public static object GameObjectDelete(string name = null, int instanceId = 0, string path = null)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var deletedName = go.name;
            WorkflowManager.SnapshotObject(go); // Record pre-deletion state
            Undo.DestroyObjectImmediate(go);
            return new { success = true, deleted = deletedName };
        }

        [UnitySkill("gameobject_delete_batch", "Delete multiple GameObjects. items: JSON array of strings (names) or objects {name, instanceId, path}")]
        public static object GameObjectDeleteBatch(string items)
        {
            if (string.IsNullOrEmpty(items))
                return new { error = "items parameter is required. Example: [\"Cube1\", \"Cube2\"] or [{\"name\":\"Cube1\"}, {\"name\":\"Cube2\"}]" };

            try
            {
                // Try parsing as array of strings first
                List<string> stringItems = null;
                try { stringItems = Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(items); } catch {}

                List<BatchDeleteItem> objItems = null;
                if (stringItems == null)
                {
                    objItems = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BatchDeleteItem>>(items);
                }
                else
                {
                    objItems = stringItems.Select(s => new BatchDeleteItem { name = s }).ToList();
                }

                if (objItems == null || objItems.Count == 0)
                     return new { error = "items parameter is empty or invalid JSON" };

                var results = new List<object>();
                int successCount = 0;
                int failCount = 0;

                foreach (var item in objItems)
                {
                    try
                    {
                        var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path);
                        if (error != null)
                        {
                            results.Add(new { target = item.name ?? item.path, success = false, error = "Object not found" });
                            failCount++;
                            continue;
                        }

                        string name = go.name;
                        WorkflowManager.SnapshotObject(go); // Record pre-deletion state
                        Undo.DestroyObjectImmediate(go);
                        results.Add(new { target = name, success = true });
                        successCount++;
                    }
                    catch (System.Exception ex)
                    {
                        results.Add(new { target = item.name ?? item.path, success = false, error = ex.Message });
                        failCount++;
                    }
                }

                return new
                {
                    success = failCount == 0,
                    totalItems = objItems.Count,
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

        private class BatchDeleteItem
        {
            public string name { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
        }

        [UnitySkill("gameobject_find", "Find GameObjects by name/regex, tag, layer, or component")]
        public static object GameObjectFind(string name = null, bool useRegex = false, string tag = null, string layer = null, string component = null, int limit = 50)
        {
            // Efficiency: If tag is provided, use FindGameObjectsWithTag (faster).
            // But we need to filter further anyway.
            IEnumerable<GameObject> results;
            if (!string.IsNullOrEmpty(tag))
                results = GameObject.FindGameObjectsWithTag(tag);
            else
                results = Object.FindObjectsOfType<GameObject>();

            // Filter by Name (Regex or Contains)
            if (!string.IsNullOrEmpty(name))
            {
                if (useRegex)
                {
                    var regex = new System.Text.RegularExpressions.Regex(name);
                    results = results.Where(go => regex.IsMatch(go.name));
                }
                else
                {
                    results = results.Where(go => go.name.Contains(name));
                }
            }
            
            // Filter by Tag (if not already fetched by tag - double check in case we fell back)
            if (!string.IsNullOrEmpty(tag))
                results = results.Where(go => go.CompareTag(tag));
                
            // Filter by Layer
            if (!string.IsNullOrEmpty(layer))
            {
                int layerId = LayerMask.NameToLayer(layer);
                if (layerId != -1)
                    results = results.Where(go => go.layer == layerId);
            }

            // Filter by Component
            if (!string.IsNullOrEmpty(component))
            {
                var compType = System.Type.GetType(component) ?? 
                    System.AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => { try { return a.GetTypes(); } catch { return new System.Type[0]; } })
                        .FirstOrDefault(t => t.Name == component || t.FullName == component);
                
                if (compType != null)
                    results = results.Where(go => go.GetComponent(compType) != null);
            }

            var list = results.Take(limit).Select(go => new
            {
                name = go.name,
                instanceId = go.GetInstanceID(),
                path = GameObjectFinder.GetPath(go),
                tag = go.tag,
                layer = LayerMask.LayerToName(go.layer),
                position = new { x = go.transform.position.x, y = go.transform.position.y, z = go.transform.position.z }
            }).ToArray();

            return new { count = list.Length, objects = list };
        }

        [UnitySkill("gameobject_set_transform", "Set transform properties. For UI/RectTransform: use anchorX/Y, pivotX/Y, sizeDeltaX/Y. For 3D: use posX/Y/Z, rotX/Y/Z, scaleX/Y/Z")]
        public static object GameObjectSetTransform(
            string name = null, int instanceId = 0, string path = null,
            // World transform (3D objects)
            float? posX = null, float? posY = null, float? posZ = null,
            float? rotX = null, float? rotY = null, float? rotZ = null,
            float? scaleX = null, float? scaleY = null, float? scaleZ = null,
            // Local transform (both 3D and UI)
            float? localPosX = null, float? localPosY = null, float? localPosZ = null,
            // RectTransform specific (UI)
            float? anchoredPosX = null, float? anchoredPosY = null,
            float? anchorMinX = null, float? anchorMinY = null,
            float? anchorMaxX = null, float? anchorMaxY = null,
            float? pivotX = null, float? pivotY = null,
            float? sizeDeltaX = null, float? sizeDeltaY = null,
            float? width = null, float? height = null)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            WorkflowManager.SnapshotObject(go.transform);
            Undo.RecordObject(go.transform, "Set Transform");

            var rt = go.GetComponent<RectTransform>();
            bool isUI = rt != null;

            // World position
            if (posX.HasValue || posY.HasValue || posZ.HasValue)
            {
                var pos = go.transform.position;
                go.transform.position = new Vector3(
                    posX ?? pos.x,
                    posY ?? pos.y,
                    posZ ?? pos.z);
            }

            // Local position
            if (localPosX.HasValue || localPosY.HasValue || localPosZ.HasValue)
            {
                var pos = go.transform.localPosition;
                go.transform.localPosition = new Vector3(
                    localPosX ?? pos.x,
                    localPosY ?? pos.y,
                    localPosZ ?? pos.z);
            }

            // Rotation
            if (rotX.HasValue || rotY.HasValue || rotZ.HasValue)
            {
                var rot = go.transform.eulerAngles;
                go.transform.eulerAngles = new Vector3(
                    rotX ?? rot.x,
                    rotY ?? rot.y,
                    rotZ ?? rot.z);
            }

            // Scale
            if (scaleX.HasValue || scaleY.HasValue || scaleZ.HasValue)
            {
                var scale = go.transform.localScale;
                go.transform.localScale = new Vector3(
                    scaleX ?? scale.x,
                    scaleY ?? scale.y,
                    scaleZ ?? scale.z);
            }

            // RectTransform specific properties
            if (isUI)
            {
                // Anchored position
                if (anchoredPosX.HasValue || anchoredPosY.HasValue)
                {
                    var pos = rt.anchoredPosition;
                    rt.anchoredPosition = new Vector2(
                        anchoredPosX ?? pos.x,
                        anchoredPosY ?? pos.y);
                }

                // Anchor min
                if (anchorMinX.HasValue || anchorMinY.HasValue)
                {
                    var min = rt.anchorMin;
                    rt.anchorMin = new Vector2(
                        anchorMinX ?? min.x,
                        anchorMinY ?? min.y);
                }

                // Anchor max
                if (anchorMaxX.HasValue || anchorMaxY.HasValue)
                {
                    var max = rt.anchorMax;
                    rt.anchorMax = new Vector2(
                        anchorMaxX ?? max.x,
                        anchorMaxY ?? max.y);
                }

                // Pivot
                if (pivotX.HasValue || pivotY.HasValue)
                {
                    var pivot = rt.pivot;
                    rt.pivot = new Vector2(
                        pivotX ?? pivot.x,
                        pivotY ?? pivot.y);
                }

                // Size delta
                if (sizeDeltaX.HasValue || sizeDeltaY.HasValue)
                {
                    var size = rt.sizeDelta;
                    rt.sizeDelta = new Vector2(
                        sizeDeltaX ?? size.x,
                        sizeDeltaY ?? size.y);
                }

                // Width/Height shortcuts
                if (width.HasValue || height.HasValue)
                {
                    rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width ?? rt.rect.width);
                    rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height ?? rt.rect.height);
                }

                EditorUtility.SetDirty(rt);

                return new
                {
                    success = true,
                    name = go.name,
                    instanceId = go.GetInstanceID(),
                    isUI = true,
                    anchoredPosition = new { x = rt.anchoredPosition.x, y = rt.anchoredPosition.y },
                    anchorMin = new { x = rt.anchorMin.x, y = rt.anchorMin.y },
                    anchorMax = new { x = rt.anchorMax.x, y = rt.anchorMax.y },
                    pivot = new { x = rt.pivot.x, y = rt.pivot.y },
                    sizeDelta = new { x = rt.sizeDelta.x, y = rt.sizeDelta.y },
                    rect = new { width = rt.rect.width, height = rt.rect.height },
                    localPosition = new { x = go.transform.localPosition.x, y = go.transform.localPosition.y, z = go.transform.localPosition.z }
                };
            }

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                isUI = false,
                position = new { x = go.transform.position.x, y = go.transform.position.y, z = go.transform.position.z },
                localPosition = new { x = go.transform.localPosition.x, y = go.transform.localPosition.y, z = go.transform.localPosition.z },
                rotation = new { x = go.transform.eulerAngles.x, y = go.transform.eulerAngles.y, z = go.transform.eulerAngles.z },
                scale = new { x = go.transform.localScale.x, y = go.transform.localScale.y, z = go.transform.localScale.z }
            };
        }

        [UnitySkill("gameobject_set_transform_batch", "Set transform properties for multiple objects (Efficient). items: JSON array of objects with optional fields (name, posX, rotX, scaleX, etc.)")]
        public static object GameObjectSetTransformBatch(string items)
        {
            if (string.IsNullOrEmpty(items))
                return new { error = "items parameter is required. Example: [{\"name\":\"Cube1\",\"posX\":10},{\"name\":\"Cube2\",\"posY\":5}]" };

            try
            {
                var itemList = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BatchTransformItem>>(items);
                if (itemList == null || itemList.Count == 0)
                    return new { error = "items parameter is empty or invalid JSON" };

                var results = new List<object>();
                int successCount = 0;
                int failCount = 0;

                foreach (var item in itemList)
                {
                    try
                    {
                        var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path);
                        if (error != null)
                        {
                            results.Add(new { target = item.name ?? item.path, success = false, error = "Object not found" });
                            failCount++;
                            continue;
                        }

                        WorkflowManager.SnapshotObject(go.transform);
                        Undo.RecordObject(go.transform, "Batch Set Transform");

                        var rt = go.GetComponent<RectTransform>();
                        bool isUI = rt != null;

                        // World position
                        if (item.posX.HasValue || item.posY.HasValue || item.posZ.HasValue)
                        {
                            var pos = go.transform.position;
                            go.transform.position = new Vector3(
                                item.posX ?? pos.x,
                                item.posY ?? pos.y,
                                item.posZ ?? pos.z);
                        }

                        // Local position
                        if (item.localPosX.HasValue || item.localPosY.HasValue || item.localPosZ.HasValue)
                        {
                            var pos = go.transform.localPosition;
                            go.transform.localPosition = new Vector3(
                                item.localPosX ?? pos.x,
                                item.localPosY ?? pos.y,
                                item.localPosZ ?? pos.z);
                        }

                        // Rotation
                        if (item.rotX.HasValue || item.rotY.HasValue || item.rotZ.HasValue)
                        {
                            var rot = go.transform.eulerAngles;
                            go.transform.eulerAngles = new Vector3(
                                item.rotX ?? rot.x,
                                item.rotY ?? rot.y,
                                item.rotZ ?? rot.z);
                        }

                        // Scale
                        if (item.scaleX.HasValue || item.scaleY.HasValue || item.scaleZ.HasValue)
                        {
                            var scale = go.transform.localScale;
                            go.transform.localScale = new Vector3(
                                item.scaleX ?? scale.x,
                                item.scaleY ?? scale.y,
                                item.scaleZ ?? scale.z);
                        }

                        // UI Specifics
                        if (isUI)
                        {
                            if (item.width.HasValue) 
                                rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, item.width.Value);
                            if (item.height.HasValue) 
                                rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, item.height.Value);
                            
                            // Add other UI properties as needed (keeping batch item lightweight for now)
                        }

                        results.Add(new
                        {
                            success = true,
                            name = go.name,
                            pos = new { x = go.transform.position.x, y = go.transform.position.y, z = go.transform.position.z }
                        });
                        successCount++;
                    }
                    catch (System.Exception ex)
                    {
                        results.Add(new { target = item.name ?? item.path, success = false, error = ex.Message });
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

        private class BatchTransformItem
        {
            public string name { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            
            // World transform
            public float? posX { get; set; }
            public float? posY { get; set; }
            public float? posZ { get; set; }
            public float? rotX { get; set; }
            public float? rotY { get; set; }
            public float? rotZ { get; set; }
            public float? scaleX { get; set; }
            public float? scaleY { get; set; }
            public float? scaleZ { get; set; }
            
            // Local transform
            public float? localPosX { get; set; }
            public float? localPosY { get; set; }
            public float? localPosZ { get; set; }
            
            // UI
            public float? width { get; set; }
            public float? height { get; set; }
        }

        [UnitySkill("gameobject_duplicate", "Duplicate a GameObject (supports name/instanceId/path). Returns: originalName, copyName, copyInstanceId, copyPath")]
        public static object GameObjectDuplicate(string name = null, int instanceId = 0, string path = null)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var copy = Object.Instantiate(go, go.transform.parent);
            copy.name = go.name + "_Copy";
            Undo.RegisterCreatedObjectUndo(copy, "Duplicate " + go.name);
            WorkflowManager.SnapshotObject(copy, SnapshotType.Created);

            return new {
                success = true,
                originalName = go.name,
                copyName = copy.name,
                copyInstanceId = copy.GetInstanceID(),
                copyPath = GameObjectFinder.GetPath(copy)
            };
        }

        [UnitySkill("gameobject_duplicate_batch", "Duplicate multiple GameObjects in one call (Efficient). items: JSON array of {name, instanceId, path}. Returns array with originalName, copyName, copyInstanceId for each.")]
        public static object GameObjectDuplicateBatch(string items)
        {
            if (string.IsNullOrEmpty(items))
                return new { error = "items parameter is required. Example: [{\"name\":\"Cube1\"},{\"instanceId\":12345}]" };

            try
            {
                var itemList = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BatchDuplicateItem>>(items);
                if (itemList == null || itemList.Count == 0)
                    return new { error = "items parameter is empty or invalid JSON" };

                var results = new List<object>();
                int successCount = 0;
                int failCount = 0;

                foreach (var item in itemList)
                {
                    try
                    {
                        var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path);
                        if (error != null)
                        {
                            results.Add(new { target = item.name ?? item.path ?? item.instanceId.ToString(), success = false, error = "Object not found" });
                            failCount++;
                            continue;
                        }

                        var copy = Object.Instantiate(go, go.transform.parent);
                        copy.name = go.name + "_Copy";
                        Undo.RegisterCreatedObjectUndo(copy, "Batch Duplicate " + go.name);
                        WorkflowManager.SnapshotObject(copy, SnapshotType.Created);

                        results.Add(new
                        {
                            success = true,
                            originalName = go.name,
                            copyName = copy.name,
                            copyInstanceId = copy.GetInstanceID(),
                            copyPath = GameObjectFinder.GetPath(copy)
                        });
                        successCount++;
                    }
                    catch (System.Exception ex)
                    {
                        results.Add(new { target = item.name ?? item.path, success = false, error = ex.Message });
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

        private class BatchDuplicateItem
        {
            public string name { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
        }

        [UnitySkill("gameobject_set_parent", "Set the parent of a GameObject (supports name/instanceId/path)")]
        public static object GameObjectSetParent(string childName = null, int childInstanceId = 0, string childPath = null, 
            string parentName = null, int parentInstanceId = 0, string parentPath = null)
        {
            var (child, childError) = GameObjectFinder.FindOrError(childName, childInstanceId, childPath);
            if (childError != null) return childError;

            Transform parent = null;
            if (!string.IsNullOrEmpty(parentName) || parentInstanceId != 0 || !string.IsNullOrEmpty(parentPath))
            {
                var (parentGo, parentError) = GameObjectFinder.FindOrError(parentName, parentInstanceId, parentPath);
                if (parentError != null) return parentError;
                parent = parentGo.transform;
            }

            WorkflowManager.SnapshotObject(child.transform);
            Undo.SetTransformParent(child.transform, parent, "Set Parent");
            return new { 
                success = true, 
                child = child.name, 
                parent = parent?.name ?? "(root)",
                newPath = GameObjectFinder.GetPath(child)
            };
        }

        [UnitySkill("gameobject_get_info", "Get detailed info about a GameObject (supports name/instanceId/path)")]
        public static object GameObjectGetInfo(string name = null, int instanceId = 0, string path = null)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var components = go.GetComponents<Component>()
                .Where(c => c != null)
                .Select(c => c.GetType().Name)
                .ToArray();

            var children = new List<object>();
            foreach (Transform child in go.transform)
            {
                children.Add(new { name = child.name, instanceId = child.gameObject.GetInstanceID() });
            }

            return new
            {
                name = go.name,
                instanceId = go.GetInstanceID(),
                path = GameObjectFinder.GetPath(go),
                tag = go.tag,
                layer = LayerMask.LayerToName(go.layer),
                isActive = go.activeSelf,
                position = new { x = go.transform.position.x, y = go.transform.position.y, z = go.transform.position.z },
                rotation = new { x = go.transform.eulerAngles.x, y = go.transform.eulerAngles.y, z = go.transform.eulerAngles.z },
                scale = new { x = go.transform.localScale.x, y = go.transform.localScale.y, z = go.transform.localScale.z },
                parent = go.transform.parent?.name,
                childCount = go.transform.childCount,
                children,
                components
            };
        }

        [UnitySkill("gameobject_set_active", "Enable or disable a GameObject (supports name/instanceId/path)")]
        public static object GameObjectSetActive(string name = null, int instanceId = 0, string path = null, bool active = true)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            WorkflowManager.SnapshotObject(go);
            Undo.RecordObject(go, "Set Active");
            go.SetActive(active);

            return new { success = true, name = go.name, active };
        }

        [UnitySkill("gameobject_set_active_batch", "Enable or disable multiple GameObjects. items: JSON array of {name, active}")]
        public static object GameObjectSetActiveBatch(string items)
        {
            if (string.IsNullOrEmpty(items))
                return new { error = "items parameter is required. Example: [{\"name\":\"Cube1\",\"active\":true},{\"name\":\"Cube2\",\"active\":false}]" };

            try
            {
                var itemList = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BatchSetActiveItem>>(items);
                if (itemList == null || itemList.Count == 0)
                    return new { error = "items parameter is empty or invalid JSON" };

                var results = new List<object>();
                int successCount = 0;
                int failCount = 0;

                foreach (var item in itemList)
                {
                    try
                    {
                        var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path);
                        if (error != null)
                        {
                            results.Add(new { target = item.name ?? item.path, success = false, error = "Object not found" });
                            failCount++;
                            continue;
                        }

                        WorkflowManager.SnapshotObject(go);
                        Undo.RecordObject(go, "Batch Set Active");
                        go.SetActive(item.active);
                        results.Add(new { target = go.name, success = true, active = item.active });
                        successCount++;
                    }
                    catch (System.Exception ex)
                    {
                        results.Add(new { target = item.name ?? item.path, success = false, error = ex.Message });
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

        public class BatchSetActiveItem
        {
            public string name { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public bool active { get; set; } = true;
        }

        [UnitySkill("gameobject_set_layer_batch", "Set layer for multiple GameObjects. items: JSON array of {name, layer, recursive}")]
        public static object GameObjectSetLayerBatch(string items)
        {
            if (string.IsNullOrEmpty(items))
                return new { error = "items parameter is required." };

            try
            {
                var itemList = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BatchSetLayerItem>>(items);
                if (itemList == null || itemList.Count == 0)
                    return new { error = "items parameter is empty or invalid JSON" };

                var results = new List<object>();
                int successCount = 0;
                int failCount = 0;

                foreach (var item in itemList)
                {
                    try
                    {
                        var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path);
                        if (error != null)
                        {
                            results.Add(new { target = item.name ?? item.path, success = false, error = "Object not found" });
                            failCount++;
                            continue;
                        }

                        int layerId = LayerMask.NameToLayer(item.layer);
                        if (layerId == -1)
                        {
                             results.Add(new { target = go.name, success = false, error = $"Layer not found: {item.layer}" });
                             failCount++;
                             continue;
                        }

                        WorkflowManager.SnapshotObject(go);
                        Undo.RecordObject(go, "Batch Set Layer");
                        go.layer = layerId;

                        if (item.recursive)
                        {
                            foreach (Transform child in go.GetComponentsInChildren<Transform>(true))
                            {
                                Undo.RecordObject(child.gameObject, "Batch Set Layer Recursive");
                                child.gameObject.layer = layerId;
                            }
                        }

                        results.Add(new { target = go.name, success = true, layer = item.layer });
                        successCount++;
                    }
                    catch (System.Exception ex)
                    {
                        results.Add(new { target = item.name ?? item.path, success = false, error = ex.Message });
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

        private class BatchSetLayerItem
        {
            public string name { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public string layer { get; set; }
            public bool recursive { get; set; } = false;
        }

        [UnitySkill("gameobject_set_tag_batch", "Set tag for multiple GameObjects. items: JSON array of {name, tag}")]
        public static object GameObjectSetTagBatch(string items)
        {
            if (string.IsNullOrEmpty(items))
                return new { error = "items parameter is required." };

            try
            {
                var itemList = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BatchSetTagItem>>(items);
                if (itemList == null || itemList.Count == 0)
                    return new { error = "items parameter is empty or invalid JSON" };

                var results = new List<object>();
                int successCount = 0;
                int failCount = 0;

                foreach (var item in itemList)
                {
                    try
                    {
                        var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path);
                        if (error != null)
                        {
                            results.Add(new { target = item.name ?? item.path, success = false, error = "Object not found" });
                            failCount++;
                            continue;
                        }

                        WorkflowManager.SnapshotObject(go);
                        Undo.RecordObject(go, "Batch Set Tag");
                        try
                        {
                            go.tag = item.tag;
                            results.Add(new { target = go.name, success = true, tag = item.tag });
                            successCount++;
                        }
                        catch
                        {
                             results.Add(new { target = go.name, success = false, error = $"Tag not defined in Unity: {item.tag}" });
                             failCount++;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        results.Add(new { target = item.name ?? item.path, success = false, error = ex.Message });
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

        private class BatchSetTagItem
        {
            public string name { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public string tag { get; set; }
        }

        [UnitySkill("gameobject_set_parent_batch", "Set parent for multiple GameObjects. items: JSON array of {childName, parentName, ...}")]
        public static object GameObjectSetParentBatch(string items)
        {
            if (string.IsNullOrEmpty(items))
                return new { error = "items parameter is required." };

            try
            {
                var itemList = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BatchSetParentItem>>(items);
                if (itemList == null || itemList.Count == 0)
                    return new { error = "items parameter is empty or invalid JSON" };

                var results = new List<object>();
                int successCount = 0;
                int failCount = 0;

                foreach (var item in itemList)
                {
                    try
                    {
                        var (child, childError) = GameObjectFinder.FindOrError(item.childName, item.childInstanceId, item.childPath);
                        if (childError != null)
                        {
                             results.Add(new { target = item.childName ?? item.childPath, success = false, error = "Child object not found" });
                             failCount++;
                             continue;
                        }

                        Transform parent = null;
                        if (!string.IsNullOrEmpty(item.parentName) || item.parentInstanceId != 0 || !string.IsNullOrEmpty(item.parentPath))
                        {
                            var (parentGo, parentError) = GameObjectFinder.FindOrError(item.parentName, item.parentInstanceId, item.parentPath);
                            if (parentError != null)
                            {
                                 results.Add(new { target = item.childName, success = false, error = $"Parent not found: {item.parentName ?? item.parentPath}" });
                                 failCount++;
                                 continue;
                            }
                            parent = parentGo.transform;
                        }

                        WorkflowManager.SnapshotObject(child.transform);
                        Undo.SetTransformParent(child.transform, parent, "Batch Set Parent");
                        results.Add(new { 
                            target = child.name, 
                            success = true, 
                            parent = parent?.name ?? "(root)" 
                        });
                        successCount++;
                    }
                    catch (System.Exception ex)
                    {
                        results.Add(new { target = item.childName ?? item.childPath, success = false, error = ex.Message });
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

        private class BatchSetParentItem
        {
            public string childName { get; set; }
            public int childInstanceId { get; set; }
            public string childPath { get; set; }
            public string parentName { get; set; }
            public int parentInstanceId { get; set; }
            public string parentPath { get; set; }
        }
    }
}
