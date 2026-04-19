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
        [UnitySkill("gameobject_create_batch", "Create multiple GameObjects in one call (Efficient). items: JSON array of {name, primitiveType, x, y, z, parentName, parentInstanceId, parentPath}",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Create,
            Tags = new[] { "primitive", "empty", "hierarchy", "batch" },
            Outputs = new[] { "gameObject", "instanceId", "path", "position" },
            TracksWorkflow = true)]
        public static object GameObjectCreateBatch(string items)
        {
            return BatchExecutor.Execute<BatchCreateItem>(items, item =>
            {
                GameObject go;
                string primitiveType = item.primitiveType;

                // Support "Empty", "", or null to create an empty GameObject
                if (string.IsNullOrEmpty(primitiveType) ||
                    primitiveType.Equals("Empty", System.StringComparison.OrdinalIgnoreCase) ||
                    primitiveType.Equals("None", System.StringComparison.OrdinalIgnoreCase))
                {
                    go = new GameObject(item.name);
                    primitiveType = null; // Normalize to null for downstream metadata and workflow tracking.
                }
                else if (System.Enum.TryParse<PrimitiveType>(primitiveType, true, out var pt))
                {
                    go = GameObject.CreatePrimitive(pt);
                    go.name = item.name;
                }
                else
                {
                    throw new System.Exception($"Unknown primitive type: {primitiveType}");
                }

                // Set parent if specified
                if (!string.IsNullOrEmpty(item.parentName) || item.parentInstanceId != 0 || !string.IsNullOrEmpty(item.parentPath))
                {
                    var (parentGo, parentErr) = GameObjectFinder.FindOrError(item.parentName, item.parentInstanceId, item.parentPath);
                    if (parentErr != null) throw new System.Exception($"Parent not found for '{item.name}'");
                    go.transform.SetParent(parentGo.transform, false);
                }

                go.transform.localPosition = new Vector3(item.x, item.y, item.z);
                if (item.rotX != 0 || item.rotY != 0 || item.rotZ != 0)
                    go.transform.eulerAngles = new Vector3(item.rotX, item.rotY, item.rotZ);
                if (item.scaleX != 1 || item.scaleY != 1 || item.scaleZ != 1)
                    go.transform.localScale = new Vector3(item.scaleX, item.scaleY, item.scaleZ);

                Undo.RegisterCreatedObjectUndo(go, "Batch Create " + item.name);
                WorkflowManager.SnapshotCreatedGameObject(go, primitiveType);

                return new
                {
                    success = true,
                    name = go.name,
                    instanceId = go.GetInstanceID(),
                    path = GameObjectFinder.GetPath(go),
                    position = new { x = item.x, y = item.y, z = item.z }
                };
            }, item => item.name);
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
            public string parentName { get; set; }
            public int parentInstanceId { get; set; }
            public string parentPath { get; set; }
        }

        [UnitySkill("gameobject_create", "Create a new GameObject. primitiveType: Cube, Sphere, Capsule, Cylinder, Plane, Quad, or Empty/null for empty object",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Create,
            Tags = new[] { "primitive", "empty", "hierarchy" },
            Outputs = new[] { "gameObject", "instanceId", "path", "position" },
            TracksWorkflow = true,
            MutatesScene = true, RiskLevel = "medium")]
        public static object GameObjectCreate(string name, string primitiveType = null, float x = 0, float y = 0, float z = 0,
            string parentName = null, int parentInstanceId = 0, string parentPath = null)
        {
            // Resolve parent first so we fail fast before creating the object
            GameObject parentGo = null;
            if (!string.IsNullOrEmpty(parentName) || parentInstanceId != 0 || !string.IsNullOrEmpty(parentPath))
            {
                var (found, parentErr) = GameObjectFinder.FindOrError(parentName, parentInstanceId, parentPath);
                if (parentErr != null) return parentErr;
                parentGo = found;
            }

            GameObject go;

            // Support "Empty", "", or null to create an empty GameObject
            if (string.IsNullOrEmpty(primitiveType) ||
                primitiveType.Equals("Empty", System.StringComparison.OrdinalIgnoreCase) ||
                primitiveType.Equals("None", System.StringComparison.OrdinalIgnoreCase))
            {
                go = new GameObject(name);
                primitiveType = null; // Normalize to null for downstream metadata and workflow tracking.
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

            if (parentGo != null)
                go.transform.SetParent(parentGo.transform, false);

            go.transform.localPosition = new Vector3(x, y, z);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            WorkflowManager.SnapshotCreatedGameObject(go, primitiveType);

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                path = GameObjectFinder.GetPath(go),
                parent = parentGo != null ? parentGo.name : "(root)",
                position = new { x, y, z }
            };
        }

        [UnitySkill("gameobject_rename", "Rename a GameObject (supports name/instanceId/path). Returns: {success, oldName, newName, instanceId}",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Modify,
            Tags = new[] { "rename", "name", "identity" },
            Outputs = new[] { "oldName", "newName", "instanceId", "path" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true)]
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

        [UnitySkill("gameobject_rename_batch", "Rename multiple GameObjects in one call (Efficient). items: JSON array of {name, instanceId, path, newName}. Returns array with oldName, newName for each.",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Modify,
            Tags = new[] { "rename", "name", "identity", "batch" },
            Outputs = new[] { "oldName", "newName", "instanceId" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true)]
        public static object GameObjectRenameBatch(string items)
        {
            return BatchExecutor.Execute<BatchRenameItem>(items, item =>
            {
                if (string.IsNullOrEmpty(item.newName))
                    throw new System.Exception("newName is required");

                var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path);
                if (error != null) throw new System.Exception("Object not found");

                var oldName = go.name;
                WorkflowManager.SnapshotObject(go);
                Undo.RecordObject(go, "Batch Rename " + go.name);
                go.name = item.newName;

                return new { success = true, oldName, newName = go.name, instanceId = go.GetInstanceID() };
            }, item => item.name ?? item.path ?? item.instanceId.ToString());
        }

        private class BatchRenameItem
        {
            public string name { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public string newName { get; set; }
        }

        [UnitySkill("gameobject_delete", "Delete a GameObject (supports name/instanceId/path)",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Delete,
            Tags = new[] { "destroy", "remove", "hierarchy" },
            Outputs = new[] { "deleted" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true,
            MutatesScene = true, RiskLevel = "medium")]
        public static object GameObjectDelete(string name = null, int instanceId = 0, string path = null)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var deletedName = go.name;
            WorkflowManager.SnapshotObject(go); // Record pre-deletion state
            Undo.DestroyObjectImmediate(go);
            return new { success = true, deleted = deletedName };
        }

        [UnitySkill("gameobject_delete_batch", "Delete multiple GameObjects. items: JSON array of strings (names) or objects {name, instanceId, path}",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Delete,
            Tags = new[] { "destroy", "remove", "hierarchy", "batch" },
            Outputs = new[] { "deleted" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true)]
        public static object GameObjectDeleteBatch(string items)
        {
            if (Validate.RequiredJsonArray(items, "items") is object err) return err;

            try
            {
                var normalizedItems = NormalizeDeleteBatchItems(items);
                return BatchExecutor.Execute<BatchDeleteItem>(normalizedItems, item =>
                {
                    var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path);
                    if (error != null)
                        throw new System.Exception("Object not found");

                    var deletedName = go.name;
                    WorkflowManager.SnapshotObject(go);
                    Undo.DestroyObjectImmediate(go);
                    return new { target = deletedName, success = true };
                }, item => item.name ?? item.path ?? item.instanceId.ToString());
            }
            catch (System.Exception ex)
            {
                return new { error = $"Failed to parse items JSON: {ex.Message}" };
            }
        }

        private static string NormalizeDeleteBatchItems(string items)
        {
            var tokens = Newtonsoft.Json.Linq.JArray.Parse(items);
            var normalized = tokens.Select(token =>
            {
                if (token.Type == Newtonsoft.Json.Linq.JTokenType.String)
                    return new BatchDeleteItem { name = token.ToObject<string>() };

                return token.ToObject<BatchDeleteItem>();
            }).ToList();

            return Newtonsoft.Json.JsonConvert.SerializeObject(normalized);
        }

        private class BatchDeleteItem
        {
            public string name { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
        }

        [UnitySkill("gameobject_find", "Find GameObjects by name/regex, tag, layer, or component",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Query,
            Tags = new[] { "search", "filter", "regex", "tag", "layer" },
            Outputs = new[] { "list", "instanceId", "path", "tag", "layer" },
            ReadOnly = true)]
        public static object GameObjectFind(string name = null, bool useRegex = false, string tag = null, string layer = null, string component = null, int limit = 50)
        {
            // Efficiency: If tag is provided, use FindGameObjectsWithTag (faster).
            // But we need to filter further anyway.
            IEnumerable<GameObject> results;
            if (!string.IsNullOrEmpty(tag))
                results = GameObject.FindGameObjectsWithTag(tag);
            else
                results = GameObjectFinder.GetSceneObjects();

            // Filter by Name (Regex or Contains)
            if (!string.IsNullOrEmpty(name))
            {
                if (useRegex)
                {
                    var regex = new System.Text.RegularExpressions.Regex(name, System.Text.RegularExpressions.RegexOptions.None, System.TimeSpan.FromSeconds(1));
                    results = results.Where(go => regex.IsMatch(go.name));
                }
                else
                {
                    results = results.Where(go => go.name.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) >= 0);
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
                var compType = ComponentSkills.FindComponentType(component);
                
                if (compType != null)
                    results = results.Where(go => go.GetComponent(compType) != null);
            }

            var list = results.Take(limit).Select(go => new
            {
                name = go.name,
                instanceId = go.GetInstanceID(),
                path = GameObjectFinder.GetCachedPath(go),
                tag = go.tag,
                layer = LayerMask.LayerToName(go.layer),
                position = new { x = go.transform.position.x, y = go.transform.position.y, z = go.transform.position.z }
            }).ToArray();

            return new { count = list.Length, objects = list };
        }

        [UnitySkill("gameobject_set_transform", "Set transform properties. For UI/RectTransform: use anchorX/Y, pivotX/Y, sizeDeltaX/Y. For 3D: use posX/Y/Z, rotX/Y/Z, scaleX/Y/Z",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Modify,
            Tags = new[] { "transform", "position", "rotation", "scale", "rectTransform" },
            Outputs = new[] { "instanceId", "position", "rotation", "scale" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true)]
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

            if (TryMergeVector3(posX, posY, posZ, go.transform.position, out var newPos))
                go.transform.position = newPos;
            if (TryMergeVector3(localPosX, localPosY, localPosZ, go.transform.localPosition, out var newLocalPos))
                go.transform.localPosition = newLocalPos;
            if (TryMergeVector3(rotX, rotY, rotZ, go.transform.eulerAngles, out var newRot))
                go.transform.eulerAngles = newRot;
            if (TryMergeVector3(scaleX, scaleY, scaleZ, go.transform.localScale, out var newScale))
                go.transform.localScale = newScale;

            // RectTransform specific properties
            if (isUI)
            {
                if (TryMergeVector2(anchoredPosX, anchoredPosY, rt.anchoredPosition, out var newAnchoredPos))
                    rt.anchoredPosition = newAnchoredPos;
                if (TryMergeVector2(anchorMinX, anchorMinY, rt.anchorMin, out var newAnchorMin))
                    rt.anchorMin = newAnchorMin;
                if (TryMergeVector2(anchorMaxX, anchorMaxY, rt.anchorMax, out var newAnchorMax))
                    rt.anchorMax = newAnchorMax;
                if (TryMergeVector2(pivotX, pivotY, rt.pivot, out var newPivot))
                    rt.pivot = newPivot;
                if (TryMergeVector2(sizeDeltaX, sizeDeltaY, rt.sizeDelta, out var newSizeDelta))
                    rt.sizeDelta = newSizeDelta;

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

        [UnitySkill("gameobject_set_transform_batch", "Set transform properties for multiple objects (Efficient). items: JSON array of objects with optional fields (name, posX, rotX, scaleX, etc.)",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Modify,
            Tags = new[] { "transform", "position", "rotation", "scale", "batch" },
            Outputs = new[] { "instanceId", "position" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true)]
        public static object GameObjectSetTransformBatch(string items)
        {
            return BatchExecutor.Execute<BatchTransformItem>(items, item =>
            {
                var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path);
                if (error != null) throw new System.Exception("Object not found");

                WorkflowManager.SnapshotObject(go.transform);
                Undo.RecordObject(go.transform, "Batch Set Transform");

                var rt = go.GetComponent<RectTransform>();
                bool isUI = rt != null;

                if (TryMergeVector3(item.posX, item.posY, item.posZ, go.transform.position, out var newPos))
                    go.transform.position = newPos;
                if (TryMergeVector3(item.localPosX, item.localPosY, item.localPosZ, go.transform.localPosition, out var newLocalPos))
                    go.transform.localPosition = newLocalPos;
                if (TryMergeVector3(item.rotX, item.rotY, item.rotZ, go.transform.eulerAngles, out var newRot))
                    go.transform.eulerAngles = newRot;
                if (TryMergeVector3(item.scaleX, item.scaleY, item.scaleZ, go.transform.localScale, out var newScale))
                    go.transform.localScale = newScale;

                if (isUI)
                {
                    if (TryMergeVector2(item.anchoredPosX, item.anchoredPosY, rt.anchoredPosition, out var newAnchoredPos))
                        rt.anchoredPosition = newAnchoredPos;
                    if (TryMergeVector2(item.anchorMinX, item.anchorMinY, rt.anchorMin, out var newAnchorMin))
                        rt.anchorMin = newAnchorMin;
                    if (TryMergeVector2(item.anchorMaxX, item.anchorMaxY, rt.anchorMax, out var newAnchorMax))
                        rt.anchorMax = newAnchorMax;
                    if (TryMergeVector2(item.pivotX, item.pivotY, rt.pivot, out var newPivot))
                        rt.pivot = newPivot;
                    if (TryMergeVector2(item.sizeDeltaX, item.sizeDeltaY, rt.sizeDelta, out var newSizeDelta))
                        rt.sizeDelta = newSizeDelta;
                    if (item.width.HasValue)
                        rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, item.width.Value);
                    if (item.height.HasValue)
                        rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, item.height.Value);
                }

                return new
                {
                    success = true,
                    name = go.name,
                    pos = new { x = go.transform.position.x, y = go.transform.position.y, z = go.transform.position.z }
                };
            }, item => item.name ?? item.path);
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

            // RectTransform (UI)
            public float? anchoredPosX { get; set; }
            public float? anchoredPosY { get; set; }
            public float? anchorMinX { get; set; }
            public float? anchorMinY { get; set; }
            public float? anchorMaxX { get; set; }
            public float? anchorMaxY { get; set; }
            public float? pivotX { get; set; }
            public float? pivotY { get; set; }
            public float? sizeDeltaX { get; set; }
            public float? sizeDeltaY { get; set; }
            public float? width { get; set; }
            public float? height { get; set; }
        }

        private static bool TryMergeVector3(float? x, float? y, float? z, Vector3 current, out Vector3 result)
        {
            if (!x.HasValue && !y.HasValue && !z.HasValue) { result = current; return false; }
            result = new Vector3(x ?? current.x, y ?? current.y, z ?? current.z);
            return true;
        }

        private static bool TryMergeVector2(float? x, float? y, Vector2 current, out Vector2 result)
        {
            if (!x.HasValue && !y.HasValue) { result = current; return false; }
            result = new Vector2(x ?? current.x, y ?? current.y);
            return true;
        }

        [UnitySkill("gameobject_duplicate", "Duplicate a GameObject (supports name/instanceId/path). Returns: originalName, copyName, copyInstanceId, copyPath",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Create,
            Tags = new[] { "duplicate", "copy", "clone", "hierarchy" },
            Outputs = new[] { "copyName", "copyInstanceId", "copyPath" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true)]
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

        [UnitySkill("gameobject_duplicate_batch", "Duplicate multiple GameObjects in one call (Efficient). items: JSON array of {name, instanceId, path}. Returns array with originalName, copyName, copyInstanceId for each.",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Create,
            Tags = new[] { "duplicate", "copy", "clone", "hierarchy", "batch" },
            Outputs = new[] { "copyName", "copyInstanceId", "copyPath" },
            RequiresInput = new[] { "gameObject" })]
        public static object GameObjectDuplicateBatch(string items)
        {
            return BatchExecutor.Execute<BatchDuplicateItem>(items, item =>
            {
                var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path);
                if (error != null) throw new System.Exception("Object not found");

                var copy = Object.Instantiate(go, go.transform.parent);
                copy.name = go.name + "_Copy";
                Undo.RegisterCreatedObjectUndo(copy, "Batch Duplicate " + go.name);
                WorkflowManager.SnapshotObject(copy, SnapshotType.Created);

                return new
                {
                    success = true,
                    originalName = go.name,
                    copyName = copy.name,
                    copyInstanceId = copy.GetInstanceID(),
                    copyPath = GameObjectFinder.GetPath(copy)
                };
            }, item => item.name ?? item.path ?? item.instanceId.ToString());
        }

        private class BatchDuplicateItem
        {
            public string name { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
        }

        [UnitySkill("gameobject_set_parent", "Set the parent of a GameObject (supports name/instanceId/path)",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Modify,
            Tags = new[] { "parent", "hierarchy", "reparent" },
            Outputs = new[] { "child", "parent", "newPath" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true)]
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

        [UnitySkill("gameobject_get_info", "Get detailed info about a GameObject (supports name/instanceId/path)",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Query,
            Tags = new[] { "inspect", "info", "details", "components" },
            Outputs = new[] { "instanceId", "path", "tag", "layer", "components", "children" },
            RequiresInput = new[] { "gameObject" },
            ReadOnly = true)]
        public static object GameObjectGetInfo(string name = null, int instanceId = 0, string path = null)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var componentBuffer = new List<Component>(8);
            go.GetComponents(componentBuffer);
            var components = new List<string>(componentBuffer.Count);
            foreach (var component in componentBuffer)
            {
                if (component != null)
                    components.Add(component.GetType().Name);
            }

            var children = new List<object>(go.transform.childCount);
            foreach (Transform child in go.transform)
            {
                children.Add(new
                {
                    name = child.name,
                    instanceId = child.gameObject.GetInstanceID(),
                    path = GameObjectFinder.GetCachedPath(child.gameObject)
                });
            }

            return new
            {
                name = go.name,
                instanceId = go.GetInstanceID(),
                path = GameObjectFinder.GetCachedPath(go),
                tag = go.tag,
                layer = LayerMask.LayerToName(go.layer),
                isActive = go.activeSelf,
                position = new { x = go.transform.position.x, y = go.transform.position.y, z = go.transform.position.z },
                rotation = new { x = go.transform.eulerAngles.x, y = go.transform.eulerAngles.y, z = go.transform.eulerAngles.z },
                scale = new { x = go.transform.localScale.x, y = go.transform.localScale.y, z = go.transform.localScale.z },
                parent = go.transform.parent?.name,
                parentPath = go.transform.parent != null ? GameObjectFinder.GetCachedPath(go.transform.parent.gameObject) : null,
                childCount = go.transform.childCount,
                children,
                components = components.ToArray()
            };
        }

        [UnitySkill("gameobject_set_active", "Enable or disable a GameObject (supports name/instanceId/path)",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Modify,
            Tags = new[] { "active", "enable", "disable", "visibility" },
            Outputs = new[] { "name", "active" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true)]
        public static object GameObjectSetActive(string name = null, int instanceId = 0, string path = null, bool active = true)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            WorkflowManager.SnapshotObject(go);
            Undo.RecordObject(go, "Set Active");
            go.SetActive(active);

            return new { success = true, name = go.name, active };
        }

        [UnitySkill("gameobject_set_active_batch", "Enable or disable multiple GameObjects. items: JSON array of {name, active}",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Modify,
            Tags = new[] { "active", "enable", "disable", "visibility", "batch" },
            Outputs = new[] { "name", "active" },
            RequiresInput = new[] { "gameObject" })]
        public static object GameObjectSetActiveBatch(string items)
        {
            return BatchExecutor.Execute<BatchSetActiveItem>(items, item =>
            {
                var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path);
                if (error != null) throw new System.Exception("Object not found");

                WorkflowManager.SnapshotObject(go);
                Undo.RecordObject(go, "Batch Set Active");
                go.SetActive(item.active);
                return new { target = go.name, success = true, active = item.active };
            }, item => item.name ?? item.path);
        }

        public class BatchSetActiveItem
        {
            public string name { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public bool active { get; set; } = true;
        }

        [UnitySkill("gameobject_set_layer_batch", "Set layer for multiple GameObjects. items: JSON array of {name, layer, recursive}",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Modify,
            Tags = new[] { "layer", "rendering", "physics", "batch" },
            Outputs = new[] { "name", "layer" },
            RequiresInput = new[] { "gameObject" })]
        public static object GameObjectSetLayerBatch(string items)
        {
            return BatchExecutor.Execute<BatchSetLayerItem>(items, item =>
            {
                var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path);
                if (error != null) throw new System.Exception("Object not found");

                int layerId = LayerMask.NameToLayer(item.layer);
                if (layerId == -1)
                    throw new System.Exception($"Layer not found: {item.layer}");

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

                return new { target = go.name, success = true, layer = item.layer };
            }, item => item.name ?? item.path);
        }

        private class BatchSetLayerItem
        {
            public string name { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public string layer { get; set; }
            public bool recursive { get; set; } = false;
        }

        [UnitySkill("gameobject_set_tag_batch", "Set tag for multiple GameObjects. items: JSON array of {name, tag}",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Modify,
            Tags = new[] { "tag", "identity", "batch" },
            Outputs = new[] { "name", "tag" },
            RequiresInput = new[] { "gameObject" })]
        public static object GameObjectSetTagBatch(string items)
        {
            return BatchExecutor.Execute<BatchSetTagItem>(items, item =>
            {
                var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path);
                if (error != null) throw new System.Exception("Object not found");

                WorkflowManager.SnapshotObject(go);
                Undo.RecordObject(go, "Batch Set Tag");
                go.tag = item.tag;
                return new { target = go.name, success = true, tag = item.tag };
            }, item => item.name ?? item.path);
        }

        private class BatchSetTagItem
        {
            public string name { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public string tag { get; set; }
        }

        [UnitySkill("gameobject_set_parent_batch", "Set parent for multiple GameObjects. items: JSON array of {childName, parentName, ...}",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Modify,
            Tags = new[] { "parent", "hierarchy", "reparent", "batch" },
            Outputs = new[] { "child", "parent" },
            RequiresInput = new[] { "gameObject" })]
        public static object GameObjectSetParentBatch(string items)
        {
            return BatchExecutor.Execute<BatchSetParentItem>(items, item =>
            {
                var (child, childError) = GameObjectFinder.FindOrError(item.childName, item.childInstanceId, item.childPath);
                if (childError != null) throw new System.Exception("Child object not found");

                Transform parent = null;
                if (!string.IsNullOrEmpty(item.parentName) || item.parentInstanceId != 0 || !string.IsNullOrEmpty(item.parentPath))
                {
                    var (parentGo, parentError) = GameObjectFinder.FindOrError(item.parentName, item.parentInstanceId, item.parentPath);
                    if (parentError != null)
                        throw new System.Exception($"Parent not found: {item.parentName ?? item.parentPath}");
                    parent = parentGo.transform;
                }

                WorkflowManager.SnapshotObject(child.transform);
                Undo.SetTransformParent(child.transform, parent, "Batch Set Parent");
                return new
                {
                    target = child.name,
                    success = true,
                    parent = parent?.name ?? "(root)"
                };
            }, item => item.childName ?? item.childPath);
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
