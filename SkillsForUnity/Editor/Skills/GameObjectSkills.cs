using UnityEngine;
using UnityEditor;
using UnityEditorInternal;
using System.Linq;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// GameObject management skills: create, modify, delete, find.
    /// Supports locating by name, entityId, legacy instanceId, or path.
    /// </summary>
    public static class GameObjectSkills
    {
        private const string TargetNameNote = "Exact name first (case-insensitive; with duplicates the pick is reported in resolutionNotes), else a unique whole-word or substring match; an ambiguous match is an error.";
        private const string TargetPathNote = "Hierarchy path 'Parent/Child' (case-insensitive, scene-name prefix optional). Lookup order entityId > instanceId > path > name; a missed path falls back to name.";

        [UnitySkill("gameobject_create_batch", "Create multiple GameObjects in one call (Efficient). items: JSON array of {name, primitiveType, x, y, z, rotX, rotY, rotZ, scaleX, scaleY, scaleZ, space, parentName, parentPath, parentInstanceId, parentEntityId}. space 'local' (default): x/y/z = localPosition, rot = localEulerAngles; 'world': world position/rotation applied after parenting; scale is always local. parentName/parentPath may name an item earlier in the same call (checked before the scene; the latest match wins). All-or-nothing: if any item fails nothing is created (rolledBack:true, the other items report reverted:true). Results read back position (world), localPosition, rotation (world euler), scale, path, parentPath.",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Create,
            Tags = new[] { "primitive", "empty", "hierarchy", "batch" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "items" },
            TracksWorkflow = true, MutatesScene = true,
            RiskLevel = "medium")]
        public static object GameObjectCreateBatch(
            [SkillParam("JSON array of {name, primitiveType?, x/y/z?, rotX/Y/Z?, scaleX/Y/Z? (default 1), space? (local|world), parentName|parentPath|parentInstanceId|parentEntityId?}.")]
            string items)
        {
            var created = new CreatedInCall();
            return BatchExecutor.Execute<BatchCreateItem>(items, item =>
            {
                // Everything that can reject the item runs before its object exists, so a failed item never leaves an orphan.
                if (!TryParseSpace(item.space, out var worldSpace))
                    return SkillParamUtil.InvalidValueError(item.space, "space", SpaceValues, item.name);
                if (!TryResolvePrimitive(item.primitiveType, out var primitive, out var primitiveError))
                    return new { error = primitiveError, parameter = "primitiveType", target = item.name };

                var (parentGo, parentError) = ResolveParent(item.parentName, item.parentInstanceId, item.parentPath, item.parentEntityId, created);
                if (parentError != null)
                    return ParentItemError(parentError, item.name);

                var go = CreateConfigured(item.name, primitive, parentGo, worldSpace,
                    new Vector3(item.x, item.y, item.z),
                    new Vector3(item.rotX, item.rotY, item.rotZ),
                    new Vector3(item.scaleX, item.scaleY, item.scaleZ),
                    "Batch Create " + item.name);
                created.Add(go);
                return DescribeCreated(go);
            }, item => item.name, atomic: true);
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
            public string space { get; set; }
            public string parentName { get; set; }
            public string parentEntityId { get; set; }
            public int parentInstanceId { get; set; }
            public string parentPath { get; set; }
        }

        [UnitySkill("gameobject_create", "Create a new GameObject. primitiveType: Cube, Sphere, Capsule, Cylinder, Plane, Quad, or Empty/null for empty object. x/y/z and rotX/Y/Z are local to the parent unless space='world' (world position/rotation, applied after parenting); scaleX/Y/Z is localScale. Returns read-back position (world), localPosition, rotation (world euler), scale, path, parentPath.",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Create,
            Tags = new[] { "primitive", "empty", "hierarchy" },
            // What's listed here must be keys the response actually carries. Writing "gameObject" is wrong —
            // that's the RequiresInput token name, not a key this skill returns, and it would leave an agent
            // planning off Outputs waiting on a field that never shows up.
            // Neither spelling affects chaining: the planner satisfies the "gameObject" token from
            // name/path/instanceId/entityId in the response, and never reads the literal from Outputs.
            Outputs = new[] { "name", "entityId", "instanceId", "path", "parent", "parentPath", "position", "localPosition", "rotation", "scale" },
            TracksWorkflow = true,
            MutatesScene = true, RiskLevel = "medium")]
        public static object GameObjectCreate(string name,
            [SkillParam("Cube, Sphere, Capsule, Cylinder, Plane or Quad (case-insensitive); omit it, or pass Empty/None, for an empty GameObject.")]
            string primitiveType = null,
            [SkillParam("Position X, local to the parent unless space='world' (world when there is no parent).")]
            float x = 0,
            [SkillParam("Position Y, local to the parent unless space='world'.")]
            float y = 0,
            [SkillParam("Position Z, local to the parent unless space='world'.")]
            float z = 0,
            string parentName = null, int parentInstanceId = 0,
            [SkillParam("Parent hierarchy path, e.g. 'World/Props'. Parent lookup order: parentEntityId > parentInstanceId > parentPath > parentName; omit all for a scene-root object.")]
            string parentPath = null, string parentEntityId = null,
            [SkillParam("rotX/rotY/rotZ: Euler angles in degrees, local to the parent unless space='world'.")]
            float rotX = 0, float rotY = 0, float rotZ = 0,
            [SkillParam("scaleX/scaleY/scaleZ: localScale; space does not apply to scale.")]
            float scaleX = 1, float scaleY = 1, float scaleZ = 1,
            [SkillParam("Coordinate space of x/y/z and rotX/rotY/rotZ: 'local' (default) = localPosition/localEulerAngles relative to the parent (same as world when there is no parent); 'world' = world position/rotation, applied after parenting. Scale is always localScale.")]
            string space = "local")
        {
            if (!TryParseSpace(space, out var worldSpace))
                return SkillParamUtil.InvalidValueError(space, "space", SpaceValues);

            // Resolve the parent object first, so a bad parent path fails before the object is created.
            var (parentGo, parentErr) = ResolveParent(parentName, parentInstanceId, parentPath, parentEntityId, null);
            if (parentErr != null) return parentErr;

            if (!TryResolvePrimitive(primitiveType, out var primitive, out var primitiveError))
                return new { error = primitiveError };

            var go = CreateConfigured(name, primitive, parentGo, worldSpace,
                new Vector3(x, y, z), new Vector3(rotX, rotY, rotZ), new Vector3(scaleX, scaleY, scaleZ), "Create " + name);
            return DescribeCreated(go);
        }

        private static readonly string[] SpaceValues = { "local", "world" };

        private static bool TryParseSpace(string space, out bool worldSpace)
        {
            worldSpace = false;
            if (string.IsNullOrEmpty(space) || space.Equals("local", System.StringComparison.OrdinalIgnoreCase))
                return true;
            worldSpace = space.Equals("world", System.StringComparison.OrdinalIgnoreCase);
            return worldSpace;
        }

        /// <summary>"Empty", "None", "" or null mean an empty GameObject (null primitive); anything else must name a PrimitiveType.</summary>
        private static bool TryResolvePrimitive(string primitiveType, out PrimitiveType? primitive, out string error)
        {
            primitive = null;
            error = null;
            if (string.IsNullOrEmpty(primitiveType) ||
                primitiveType.Equals("Empty", System.StringComparison.OrdinalIgnoreCase) ||
                primitiveType.Equals("None", System.StringComparison.OrdinalIgnoreCase))
                return true;

            if (System.Enum.TryParse<PrimitiveType>(primitiveType, true, out var parsed))
            {
                primitive = parsed;
                return true;
            }

            error = $"Unknown primitive type: {primitiveType}. Use: Cube, Sphere, Capsule, Cylinder, Plane, Quad, or Empty/None for empty object";
            return false;
        }

        /// <summary>
        /// Creates, parents and places one GameObject, then registers it with Undo, the workflow and the finder cache.
        /// Local space writes localPosition/localEulerAngles; world space writes position/eulerAngles after parenting,
        /// so the parent's transform can't shift the requested world values. Scale is always localScale.
        /// </summary>
        private static GameObject CreateConfigured(string name, PrimitiveType? primitive, GameObject parent, bool worldSpace,
            Vector3 position, Vector3 eulerAngles, Vector3 scale, string undoName)
        {
            GameObject go;
            if (primitive.HasValue)
            {
                go = GameObject.CreatePrimitive(primitive.Value);
                go.name = name;
            }
            else
            {
                go = new GameObject(name);
            }

            var t = go.transform;
            if (parent != null)
                t.SetParent(parent.transform, false);

            if (worldSpace)
            {
                t.position = position;
                t.eulerAngles = eulerAngles;
            }
            else
            {
                t.localPosition = position;
                t.localEulerAngles = eulerAngles;
            }
            t.localScale = scale;

            Undo.RegisterCreatedObjectUndo(go, undoName);
            // The canonical enum name, not the caller's spelling: redo rebuilds the primitive with a case-sensitive parse.
            WorkflowManager.SnapshotCreatedGameObject(go, primitive?.ToString());
            GameObjectFinder.RegisterCreated(go);
            return go;
        }

        /// <summary>The created object's state read back from its Transform, with the same keys and spaces as gameobject_get_info.</summary>
        private static object DescribeCreated(GameObject go)
        {
            var t = go.transform;
            var parent = t.parent;
            return new
            {
                success = true,
                name = go.name,
                entityId = UnityObjectIdUtility.GetEntityId(go),
                instanceId = UnityObjectIdUtility.GetObjectId(go),
                path = GameObjectFinder.GetPath(go),
                parent = parent != null ? parent.name : "(root)",
                parentPath = parent != null ? GameObjectFinder.GetPath(parent.gameObject) : null,
                position = new { x = t.position.x, y = t.position.y, z = t.position.z },
                localPosition = new { x = t.localPosition.x, y = t.localPosition.y, z = t.localPosition.z },
                rotation = new { x = t.eulerAngles.x, y = t.eulerAngles.y, z = t.eulerAngles.z },
                scale = new { x = t.localScale.x, y = t.localScale.y, z = t.localScale.z }
            };
        }

        /// <summary>
        /// Resolves a create's parent before anything exists. With <paramref name="created"/> (a batch), objects made by
        /// earlier items win: parentPath is matched against their paths, then the scene; parentName against their names
        /// (latest first), then the scene. entityId/instanceId are exact and go straight to the finder.
        /// </summary>
        private static (GameObject parent, object error) ResolveParent(string parentName, int parentInstanceId, string parentPath,
            string parentEntityId, CreatedInCall created)
        {
            if (string.IsNullOrEmpty(parentEntityId) && string.IsNullOrEmpty(parentName) &&
                parentInstanceId == 0 && string.IsNullOrEmpty(parentPath))
                return (null, null);

            if (created != null && string.IsNullOrEmpty(parentEntityId) && parentInstanceId == 0)
            {
                if (!string.IsNullOrEmpty(parentPath))
                {
                    var byPath = created.FindByPath(parentPath);
                    if (byPath != null)
                        return (byPath, null);
                    if (GameObjectFinder.FindByPath(parentPath) != null)
                        return GameObjectFinder.FindOrError(path: parentPath);
                }

                if (!string.IsNullOrEmpty(parentName))
                {
                    var byName = created.FindByName(parentName);
                    if (byName != null)
                    {
                        if (!string.IsNullOrEmpty(parentPath))
                            GameObjectFinder.AddResolutionNote($"path '{parentPath}' not found; resolved by name '{parentName}' instead (path: {GameObjectFinder.GetPath(byName)})");
                        return (byName, null);
                    }
                }
            }

            return GameObjectFinder.FindOrError(parentName, parentInstanceId, parentPath, entityId: parentEntityId);
        }

        /// <summary>A batch item's parent failure, keeping the finder's code and candidates so the item says what to fix.</summary>
        private static object ParentItemError(object finderError, string target)
        {
            SkillResultHelper.TryGetMemberValue(finderError, "error", out var message);
            SkillResultHelper.TryGetMemberValue(finderError, "errorCode", out var errorCode);
            SkillResultHelper.TryGetMemberValue(finderError, "suggestions", out var suggestions);
            SkillResultHelper.TryGetMemberValue(finderError, "candidates", out var candidates);
            return new
            {
                error = $"Parent of '{target}' could not be resolved: {message}",
                errorCode,
                parameter = "parent",
                target,
                suggestions,
                candidates
            };
        }

        /// <summary>
        /// Objects made by earlier items of the current create call. A later item's parentPath / parentName resolves here
        /// before the scene (the latest matching item wins), so one batch can build a hierarchy top-down; the planner
        /// predicts the same precedence for dryRun.
        /// </summary>
        private sealed class CreatedInCall
        {
            private readonly List<GameObject> _objects = new List<GameObject>();

            public void Add(GameObject go) => _objects.Add(go);

            public GameObject FindByPath(string path)
            {
                var wanted = GameObjectFinder.NormalizePathKey(path);
                if (wanted == null)
                    return null;

                for (int i = _objects.Count - 1; i >= 0; i--)
                {
                    var go = _objects[i];
                    if (go == null)
                        continue;
                    var actual = GameObjectFinder.GetPath(go);
                    if (actual.Equals(wanted, System.StringComparison.OrdinalIgnoreCase) ||
                        (go.scene.name + "/" + actual).Equals(wanted, System.StringComparison.OrdinalIgnoreCase))
                        return go;
                }
                return null;
            }

            public GameObject FindByName(string name)
            {
                GameObject match = null;
                int count = 0;
                for (int i = _objects.Count - 1; i >= 0; i--)
                {
                    var go = _objects[i];
                    if (go == null || !go.name.Equals(name, System.StringComparison.OrdinalIgnoreCase))
                        continue;
                    count++;
                    if (match == null)
                        match = go;
                }

                if (match == null)
                    return null;
                if (!string.Equals(match.name, name, System.StringComparison.Ordinal))
                    GameObjectFinder.AddResolutionNote($"parent name '{name}' matched '{match.name}' case-insensitively (created earlier in this call, path: {GameObjectFinder.GetPath(match)})");
                if (count > 1)
                    GameObjectFinder.AddResolutionNote($"parent name '{name}' matches {count} objects created earlier in this call; used the latest (path: {GameObjectFinder.GetPath(match)})");
                return match;
            }
        }

        [UnitySkill("gameobject_rename", "Rename a GameObject (supports name/instanceId/path). Returns, read back from the renamed object: {success, oldName, newName, entityId, instanceId, path}",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Modify,
            Tags = new[] { "rename", "name", "identity" },
            Outputs = new[] { "oldName", "newName", "instanceId", "path" },
            RequiredParams = new[] { "newName" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true, MutatesScene = true)]
        public static object GameObjectRename(string name = null, int instanceId = 0, string path = null, string newName = null, string entityId = null)
        {
            if (Validate.Required(newName, "newName") is object err) return err;

            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path, entityId: entityId);
            if (error != null) return error;

            var oldName = go.name;
            WorkflowManager.SnapshotObject(go);
            Undo.RecordObject(go, "Rename GameObject");
            go.name = newName;

            return new { 
                success = true, 
                oldName, 
                newName = go.name, 
                entityId = UnityObjectIdUtility.GetEntityId(go),
                instanceId = UnityObjectIdUtility.GetObjectId(go),
                path = GameObjectFinder.GetPath(go)
            };
        }

        [UnitySkill("gameobject_rename_batch", "Rename multiple GameObjects in one call (Efficient). items: JSON array of {name, instanceId, path, newName}. Returns array with oldName, newName for each.",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Modify,
            Tags = new[] { "rename", "name", "identity", "batch" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "items" },
            TracksWorkflow = true, MutatesScene = true)]
        public static object GameObjectRenameBatch(
            [SkillParam("JSON array of {name|path|instanceId|entityId, newName}.")]
            string items)
        {
            return BatchExecutor.Execute<BatchRenameItem>(items, item =>
            {
                if (string.IsNullOrEmpty(item.newName))
                    return new { error = "newName is required" };

                var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path, entityId: item.entityId);
                if (error != null) return new { error = "Object not found", target = item.name ?? item.path ?? item.entityId };

                var oldName = go.name;
                WorkflowManager.SnapshotObject(go);
                Undo.RecordObject(go, "Batch Rename " + go.name);
                go.name = item.newName;

                return new { success = true, oldName, newName = go.name, entityId = UnityObjectIdUtility.GetEntityId(go), instanceId = UnityObjectIdUtility.GetObjectId(go) };
            }, item => item.name ?? item.path ?? item.entityId ?? item.instanceId.ToString(), atomic: true);
        }

        private class BatchRenameItem
        {
            public string name { get; set; }
            public string entityId { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public string newName { get; set; }
        }

        [UnitySkill("gameobject_delete", "Delete a GameObject (supports name/instanceId/path)",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Delete,
            Tags = new[] { "destroy", "remove", "hierarchy" },
            Outputs = new[] { "deleted" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true, SkipAutoPresnapshot = true,
            MutatesScene = true, RiskLevel = "medium")]
        public static object GameObjectDelete(
            [SkillParam(TargetNameNote)] string name = null,
            int instanceId = 0,
            [SkillParam(TargetPathNote)] string path = null,
            string entityId = null)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path, entityId: entityId);
            if (error != null) return error;

            var deletedName = go.name;
            if (!WorkflowManager.DeleteSceneObject(go))
                return new { error = $"Failed to capture and delete: {deletedName}" };
            return new { success = true, deleted = deletedName };
        }

        [UnitySkill("gameobject_delete_batch", "Delete multiple GameObjects. items: JSON array of strings (names) or objects {name, instanceId, path}",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Delete,
            Tags = new[] { "destroy", "remove", "hierarchy", "batch" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "items" },
            TracksWorkflow = true, SkipAutoPresnapshot = true,
            MutatesScene = true,
            RiskLevel = "medium")]
        public static object GameObjectDeleteBatch(
            [SkillParam("JSON array of names, or of {name|path|instanceId|entityId} objects; the two forms may be mixed.")]
            string items)
        {
            if (Validate.RequiredJsonArray(items, "items") is object err) return err;

            try
            {
                var normalizedItems = NormalizeDeleteBatchItems(items);
                return BatchExecutor.Execute<BatchDeleteItem>(normalizedItems, item =>
                {
                    var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path, entityId: item.entityId);
                    if (error != null)
                        return new { error = "Object not found", target = item.name ?? item.path ?? item.entityId };

                    var deletedName = go.name;
                    if (!WorkflowManager.DeleteSceneObject(go))
                        return new { error = "Failed to capture and delete object" };
                    return new { target = deletedName, success = true };
                }, item => item.name ?? item.path ?? item.entityId ?? item.instanceId.ToString(), atomic: true);
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
            public string entityId { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
        }

        [UnitySkill("gameobject_find", "Find GameObjects by name/regex, tag, layer, or component",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Query,
            Tags = new[] { "search", "filter", "regex", "tag", "layer" },
            Outputs = new[] { "count", "objects" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object GameObjectFind(
            [SkillParam("Case-insensitive substring; with useRegex=true a case-sensitive .NET regex.")]
            string name = null,
            bool useRegex = false, string tag = null,
            [SkillParam("Layer name as in Tags & Layers, not an index.")]
            string layer = null,
            string component = null, int limit = 50)
        {
            // A tag that isn't registered in TagManager makes GameObject.FindGameObjectsWithTag throw
            // UnityException, so this must be rejected at the entry point rather than letting that call
            // become an unhandled exception.
            if (!string.IsNullOrEmpty(tag) && !IsTagDefined(tag))
                return SkillParamUtil.InvalidValueError(tag, "tag", InternalEditorUtility.tags);

            // If a tag is given, narrow the scope first with FindGameObjectsWithTag (faster); filtering continues below regardless.
            IEnumerable<GameObject> results;
            if (!string.IsNullOrEmpty(tag))
                results = GameObject.FindGameObjectsWithTag(tag);
            else
                results = GameObjectFinder.GetSceneObjects();

            // Filter by name (regex or contains).
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
            
            // Re-check the tag again, in case the earlier path took the fallback branch.
            if (!string.IsNullOrEmpty(tag))
                results = results.Where(go => go.CompareTag(tag));
                
            if (!string.IsNullOrEmpty(layer))
            {
                int layerId = LayerMask.NameToLayer(layer);
                if (layerId != -1)
                    results = results.Where(go => go.layer == layerId);
            }

            // Filter by component type.
            if (!string.IsNullOrEmpty(component))
            {
                var compType = ComponentSkills.FindComponentType(component);
                
                if (compType != null)
                    results = results.Where(go => go.GetComponent(compType) != null);
            }

            var list = results.Take(limit).Select(go => new
            {
                name = go.name,
                entityId = UnityObjectIdUtility.GetEntityId(go),
                instanceId = UnityObjectIdUtility.GetObjectId(go),
                path = GameObjectFinder.GetCachedPath(go),
                tag = go.tag,
                layer = LayerMask.LayerToName(go.layer),
                position = new { x = go.transform.position.x, y = go.transform.position.y, z = go.transform.position.z }
            }).ToArray();

            return new { count = list.Length, objects = list };
        }

        [UnitySkill("gameobject_set_transform", "Set transform properties (supports name/instanceId/path). posX/Y/Z = world position, rotX/Y/Z = world euler angles, scaleX/Y/Z = localScale, localPosX/Y/Z = local position (applied after posX/Y/Z, so it wins on the same axis); omitted axes keep their current value. UI/RectTransform also takes anchoredPosX/Y, anchorMinX/Y, anchorMaxX/Y, pivotX/Y, sizeDeltaX/Y, width/height. Returns the read-back position (world), localPosition, rotation (world euler) and scale (local), plus the RectTransform values for UI.",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Modify,
            Tags = new[] { "transform", "position", "rotation", "scale", "rectTransform" },
            Outputs = new[] { "instanceId", "position", "rotation", "scale" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true, MutatesScene = true)]
        public static object GameObjectSetTransform(
            [SkillParam(TargetNameNote)] string name = null,
            int instanceId = 0,
            [SkillParam(TargetPathNote)] string path = null,
            // World transform (3D objects)
            [SkillParam("posX/posY/posZ: world position; omitted axes keep their value. localPosX/Y/Z are applied after and win on the same axis.")]
            float? posX = null, float? posY = null, float? posZ = null,
            [SkillParam("rotX/rotY/rotZ: world Euler angles in degrees (transform.eulerAngles); omitted axes keep their value.")]
            float? rotX = null, float? rotY = null, float? rotZ = null,
            [SkillParam("scaleX/scaleY/scaleZ: localScale (relative to the parent, not world scale).")]
            float? scaleX = null, float? scaleY = null, float? scaleZ = null,
            // Local transform (shared by 3D and UI)
            [SkillParam("localPosX/Y/Z: position relative to the parent, applied after posX/Y/Z so it wins on the same axis.")]
            float? localPosX = null, float? localPosY = null, float? localPosZ = null,
            // RectTransform-specific (UI)
            [SkillParam("RectTransform only (ignored on non-UI objects): anchoredPosition X/Y, the pivot's offset from the anchor reference point.")]
            float? anchoredPosX = null, float? anchoredPosY = null,
            [SkillParam("RectTransform only: anchorMinX/Y and anchorMaxX/Y are 0-1 fractions of the parent rect.")]
            float? anchorMinX = null, float? anchorMinY = null,
            float? anchorMaxX = null, float? anchorMaxY = null,
            [SkillParam("RectTransform only: pivotX/Y are 0-1 fractions of the object's own rect (0.5 = centre).")]
            float? pivotX = null, float? pivotY = null,
            [SkillParam("RectTransform only: sizeDeltaX/Y = size relative to the distance between the anchors (equals the size when the anchors coincide).")]
            float? sizeDeltaX = null, float? sizeDeltaY = null,
            [SkillParam("RectTransform only: width/height set the rect size for the current anchors, applied after sizeDeltaX/Y.")]
            float? width = null, float? height = null,
            string entityId = null)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path, entityId: entityId);
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

            // RectTransform-specific properties.
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

                // width/height are convenience shorthand for sizeDelta.
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
                    entityId = UnityObjectIdUtility.GetEntityId(go),
                    instanceId = UnityObjectIdUtility.GetObjectId(go),
                    isUI = true,
                    anchoredPosition = new { x = rt.anchoredPosition.x, y = rt.anchoredPosition.y },
                    anchorMin = new { x = rt.anchorMin.x, y = rt.anchorMin.y },
                    anchorMax = new { x = rt.anchorMax.x, y = rt.anchorMax.y },
                    pivot = new { x = rt.pivot.x, y = rt.pivot.y },
                    sizeDelta = new { x = rt.sizeDelta.x, y = rt.sizeDelta.y },
                    rect = new { width = rt.rect.width, height = rt.rect.height },
                    localPosition = new { x = go.transform.localPosition.x, y = go.transform.localPosition.y, z = go.transform.localPosition.z },
                    position = new { x = go.transform.position.x, y = go.transform.position.y, z = go.transform.position.z },
                    rotation = new { x = go.transform.eulerAngles.x, y = go.transform.eulerAngles.y, z = go.transform.eulerAngles.z },
                    scale = new { x = go.transform.localScale.x, y = go.transform.localScale.y, z = go.transform.localScale.z }
                };
            }

            return new
            {
                success = true,
                name = go.name,
                entityId = UnityObjectIdUtility.GetEntityId(go),
                instanceId = UnityObjectIdUtility.GetObjectId(go),
                isUI = false,
                position = new { x = go.transform.position.x, y = go.transform.position.y, z = go.transform.position.z },
                localPosition = new { x = go.transform.localPosition.x, y = go.transform.localPosition.y, z = go.transform.localPosition.z },
                rotation = new { x = go.transform.eulerAngles.x, y = go.transform.eulerAngles.y, z = go.transform.eulerAngles.z },
                scale = new { x = go.transform.localScale.x, y = go.transform.localScale.y, z = go.transform.localScale.z }
            };
        }

        [UnitySkill("gameobject_set_transform_batch", "Set transform properties for multiple objects (Efficient). items: JSON array of {name, instanceId, path, entityId, posX/Y/Z, rotX/Y/Z, scaleX/Y/Z, localPosX/Y/Z, anchoredPosX/Y, anchorMinX/Y, anchorMaxX/Y, pivotX/Y, sizeDeltaX/Y, width, height}, with the same spaces as gameobject_set_transform (pos/rot world, scale local, localPos wins on the same axis). If any item fails the whole call is rolled back (rolledBack:true, the other items report reverted:true). Each result reads back position (world; legacy alias pos), localPosition, rotation and scale.",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Modify,
            Tags = new[] { "transform", "position", "rotation", "scale", "batch" },
            // Only list the outer envelope's keys, echoed per item inside results[]. Declaring entityId
            // here would mislead a chaining planner into thinking this skill produces a top-level entityId.
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "items" },
            TracksWorkflow = true, MutatesScene = true)]
        public static object GameObjectSetTransformBatch(
            [SkillParam("JSON array of {name|path|instanceId|entityId, posX/Y/Z?, rotX/Y/Z?, scaleX/Y/Z?, localPosX/Y/Z?, anchoredPosX/Y?, anchorMinX/Y?, anchorMaxX/Y?, pivotX/Y?, sizeDeltaX/Y?, width?, height?}.")]
            string items)
        {
            return BatchExecutor.Execute<BatchTransformItem>(items, item =>
            {
                var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path, entityId: item.entityId);
                if (error != null) return new { error = "Object not found", target = item.name ?? item.path ?? item.entityId };

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

                    EditorUtility.SetDirty(rt);

                    // Echo back every field this call can write, matching gameobject_set_transform's two response shapes.
                    // pos is the legacy name of position (world), kept for existing callers.
                    return new
                    {
                        success = true,
                        name = go.name,
                        entityId = UnityObjectIdUtility.GetEntityId(go),
                        instanceId = UnityObjectIdUtility.GetObjectId(go),
                        isUI = true,
                        position = new { x = go.transform.position.x, y = go.transform.position.y, z = go.transform.position.z },
                        pos = new { x = go.transform.position.x, y = go.transform.position.y, z = go.transform.position.z },
                        localPosition = new { x = go.transform.localPosition.x, y = go.transform.localPosition.y, z = go.transform.localPosition.z },
                        rotation = new { x = go.transform.eulerAngles.x, y = go.transform.eulerAngles.y, z = go.transform.eulerAngles.z },
                        scale = new { x = go.transform.localScale.x, y = go.transform.localScale.y, z = go.transform.localScale.z },
                        anchoredPosition = new { x = rt.anchoredPosition.x, y = rt.anchoredPosition.y },
                        anchorMin = new { x = rt.anchorMin.x, y = rt.anchorMin.y },
                        anchorMax = new { x = rt.anchorMax.x, y = rt.anchorMax.y },
                        pivot = new { x = rt.pivot.x, y = rt.pivot.y },
                        sizeDelta = new { x = rt.sizeDelta.x, y = rt.sizeDelta.y },
                        rect = new { width = rt.rect.width, height = rt.rect.height }
                    };
                }

                return new
                {
                    success = true,
                    name = go.name,
                    entityId = UnityObjectIdUtility.GetEntityId(go),
                    instanceId = UnityObjectIdUtility.GetObjectId(go),
                    isUI = false,
                    position = new { x = go.transform.position.x, y = go.transform.position.y, z = go.transform.position.z },
                    pos = new { x = go.transform.position.x, y = go.transform.position.y, z = go.transform.position.z },
                    localPosition = new { x = go.transform.localPosition.x, y = go.transform.localPosition.y, z = go.transform.localPosition.z },
                    rotation = new { x = go.transform.eulerAngles.x, y = go.transform.eulerAngles.y, z = go.transform.eulerAngles.z },
                    scale = new { x = go.transform.localScale.x, y = go.transform.localScale.y, z = go.transform.localScale.z }
                };
            }, item => item.name ?? item.path ?? item.entityId, atomic: true);
        }

        private class BatchTransformItem
        {
            public string name { get; set; }
            public string entityId { get; set; }
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

            // RectTransform（UI）
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

        [UnitySkill("gameobject_duplicate", "Duplicate a GameObject (supports name/instanceId/path) as a sibling named <name>_Copy. Returns, read back from the copy: originalName, copyName, copyEntityId, copyInstanceId, copyPath, copyParentPath",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Create,
            Tags = new[] { "duplicate", "copy", "clone", "hierarchy" },
            Outputs = new[] { "copyName", "copyInstanceId", "copyPath", "copyParentPath" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true, MutatesScene = true)]
        public static object GameObjectDuplicate(string name = null, int instanceId = 0, string path = null, string entityId = null)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path, entityId: entityId);
            if (error != null) return error;

            var copy = Object.Instantiate(go, go.transform.parent);
            copy.name = go.name + "_Copy";
            Undo.RegisterCreatedObjectUndo(copy, "Duplicate " + go.name);
            WorkflowManager.SnapshotObject(copy, SnapshotType.Created);
            GameObjectFinder.RegisterCreated(copy);

            return DescribeCopy(go, copy);
        }

        /// <summary>The copy's identity read back from the new object, including where it landed in the hierarchy.</summary>
        private static object DescribeCopy(GameObject original, GameObject copy)
        {
            return new
            {
                success = true,
                originalName = original.name,
                copyName = copy.name,
                copyEntityId = UnityObjectIdUtility.GetEntityId(copy),
                copyInstanceId = UnityObjectIdUtility.GetObjectId(copy),
                copyPath = GameObjectFinder.GetPath(copy),
                copyParentPath = copy.transform.parent != null ? GameObjectFinder.GetPath(copy.transform.parent.gameObject) : null
            };
        }

        [UnitySkill("gameobject_duplicate_batch", "Duplicate multiple GameObjects in one call (Efficient). items: JSON array of {name, instanceId, path, entityId}. Each result carries originalName, copyName, copyEntityId, copyInstanceId, copyPath, copyParentPath; if any item fails the whole call is rolled back (rolledBack:true, the other items report reverted:true).",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Create,
            Tags = new[] { "duplicate", "copy", "clone", "hierarchy", "batch" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "items" },
            TracksWorkflow = true, MutatesScene = true)]
        public static object GameObjectDuplicateBatch(
            [SkillParam("JSON array of {name|path|instanceId|entityId}.")]
            string items)
        {
            return BatchExecutor.Execute<BatchDuplicateItem>(items, item =>
            {
                var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path, entityId: item.entityId);
                if (error != null) return new { error = "Object not found", target = item.name ?? item.path ?? item.entityId };

                var copy = Object.Instantiate(go, go.transform.parent);
                copy.name = go.name + "_Copy";
                Undo.RegisterCreatedObjectUndo(copy, "Batch Duplicate " + go.name);
                WorkflowManager.SnapshotObject(copy, SnapshotType.Created);
                GameObjectFinder.RegisterCreated(copy);

                return DescribeCopy(go, copy);
            }, item => item.name ?? item.path ?? item.entityId ?? item.instanceId.ToString(), atomic: true);
        }

        private class BatchDuplicateItem
        {
            public string name { get; set; }
            public string entityId { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
        }

        [UnitySkill("gameobject_set_parent", "Set the parent of a GameObject (supports name/instanceId/path)",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Modify,
            Tags = new[] { "parent", "hierarchy", "reparent" },
            Outputs = new[] { "child", "parent", "newPath" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true, MutatesScene = true)]
        public static object GameObjectSetParent(string childName = null, int childInstanceId = 0, string childPath = null,
            string parentName = null, int parentInstanceId = 0,
            [SkillParam("New parent's hierarchy path; omit every parent* locator to move the child to the scene root. The child keeps its world position and rotation.")]
            string parentPath = null,
            string childEntityId = null, string parentEntityId = null)
        {
            var (child, childError) = GameObjectFinder.FindOrError(childName, childInstanceId, childPath, entityId: childEntityId);
            if (childError != null) return childError;

            Transform parent = null;
            if (!string.IsNullOrEmpty(parentEntityId) || !string.IsNullOrEmpty(parentName) || parentInstanceId != 0 || !string.IsNullOrEmpty(parentPath))
            {
                var (parentGo, parentError) = GameObjectFinder.FindOrError(parentName, parentInstanceId, parentPath, entityId: parentEntityId);
                if (parentError != null) return parentError;
                parent = parentGo.transform;
            }

            WorkflowManager.SnapshotObject(child.transform);
            Undo.SetTransformParent(child.transform, parent, "Set Parent");
            return new { 
                success = true, 
                child = child.name, 
                childEntityId = UnityObjectIdUtility.GetEntityId(child),
                parent = parent?.name ?? "(root)",
                parentEntityId = parent != null ? UnityObjectIdUtility.GetEntityId(parent.gameObject) : null,
                newPath = GameObjectFinder.GetPath(child)
            };
        }

        [UnitySkill("gameobject_get_info", "Get detailed info about a GameObject (supports name/instanceId/path)",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Query,
            Tags = new[] { "inspect", "info", "details", "components" },
            Outputs = new[] { "name", "entityId", "instanceId", "path", "tag", "layer", "isActive",
                "position", "rotation", "scale", "parent", "parentPath", "childCount", "children", "components" },
            RequiresInput = new[] { "gameObject" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object GameObjectGetInfo(
            [SkillParam(TargetNameNote)] string name = null,
            int instanceId = 0,
            [SkillParam(TargetPathNote)] string path = null,
            string entityId = null)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path, entityId: entityId);
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
                    entityId = UnityObjectIdUtility.GetEntityId(child.gameObject),
                    instanceId = UnityObjectIdUtility.GetObjectId(child.gameObject),
                    path = GameObjectFinder.GetCachedPath(child.gameObject)
                });
            }

            return new
            {
                name = go.name,
                entityId = UnityObjectIdUtility.GetEntityId(go),
                instanceId = UnityObjectIdUtility.GetObjectId(go),
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

        [UnitySkill("gameobject_set_sibling_index", "Set a GameObject's sibling index — its position among the children of its parent, or among the scene's root objects when it has no parent (supports name/instanceId/path). index is clamped into the valid range and the response reports clamped=true when that happened.",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Modify,
            Tags = new[] { "sibling", "order", "reorder", "hierarchy" },
            Outputs = new[] { "name", "index", "previousIndex", "clamped" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true,
            MutatesScene = true)]
        public static object GameObjectSetSiblingIndex(string name = null, int instanceId = 0, string path = null, int index = 0, string entityId = null)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path, entityId: entityId);
            if (error != null) return error;

            var t = go.transform;
            int siblingCount = t.parent != null ? t.parent.childCount : go.scene.rootCount;
            int previousIndex = t.GetSiblingIndex();
            int clampedIndex = Mathf.Clamp(index, 0, siblingCount - 1);

            WorkflowManager.SnapshotObject(t);
            Undo.SetSiblingIndex(t, clampedIndex, "Set Sibling Index");

            return new
            {
                success = true,
                name = go.name,
                entityId = UnityObjectIdUtility.GetEntityId(go),
                path = GameObjectFinder.GetPath(go),
                parent = t.parent != null ? t.parent.name : "(scene root)",
                previousIndex,
                index = t.GetSiblingIndex(),
                clamped = clampedIndex != index
            };
        }

        [UnitySkill("gameobject_set_active", "Enable or disable a GameObject (supports name/instanceId/path)",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Modify,
            Tags = new[] { "active", "enable", "disable", "visibility" },
            Outputs = new[] { "name", "active" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true, MutatesScene = true)]
        public static object GameObjectSetActive(string name = null, int instanceId = 0, string path = null, bool active = true, string entityId = null)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path, entityId: entityId);
            if (error != null) return error;

            WorkflowManager.SnapshotObject(go);
            Undo.RecordObject(go, "Set Active");
            go.SetActive(active);

            return new { success = true, name = go.name, entityId = UnityObjectIdUtility.GetEntityId(go), active };
        }

        [UnitySkill("gameobject_set_active_batch", "Enable or disable multiple GameObjects. items: JSON array of {name, active}",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Modify,
            Tags = new[] { "active", "enable", "disable", "visibility", "batch" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "items" },
            TracksWorkflow = true, MutatesScene = true)]
        public static object GameObjectSetActiveBatch(
            [SkillParam("JSON array of {name|path|instanceId|entityId, active? (default true)}.")]
            string items)
        {
            return BatchExecutor.Execute<BatchSetActiveItem>(items, item =>
            {
                var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path, entityId: item.entityId);
                if (error != null) return new { error = "Object not found", target = item.name ?? item.path ?? item.entityId };

                WorkflowManager.SnapshotObject(go);
                Undo.RecordObject(go, "Batch Set Active");
                go.SetActive(item.active);
                return new { target = go.name, entityId = UnityObjectIdUtility.GetEntityId(go), success = true, active = item.active };
            }, item => item.name ?? item.path ?? item.entityId, atomic: true);
        }

        public class BatchSetActiveItem
        {
            public string name { get; set; }
            public string entityId { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public bool active { get; set; } = true;
        }

        [UnitySkill("gameobject_set_layer_batch", "Set layer for multiple GameObjects. items: JSON array of {name, layer, recursive}",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Modify,
            Tags = new[] { "layer", "rendering", "physics", "batch" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "items" },
            TracksWorkflow = true, MutatesScene = true)]
        public static object GameObjectSetLayerBatch(
            [SkillParam("JSON array of {name|path|instanceId|entityId, layer (layer name), recursive? (default false)}.")]
            string items)
        {
            return BatchExecutor.Execute<BatchSetLayerItem>(items, item =>
            {
                var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path, entityId: item.entityId);
                if (error != null) return new { error = "Object not found", target = item.name ?? item.path ?? item.entityId };

                int layerId = LayerMask.NameToLayer(item.layer);
                if (layerId == -1)
                    return new { error = $"Layer not found: {item.layer}" };

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

                return new { target = go.name, entityId = UnityObjectIdUtility.GetEntityId(go), success = true, layer = item.layer };
            }, item => item.name ?? item.path ?? item.entityId, atomic: true);
        }

        private class BatchSetLayerItem
        {
            public string name { get; set; }
            public string entityId { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public string layer { get; set; }
            public bool recursive { get; set; } = false;
        }

        [UnitySkill("gameobject_set_tag_batch", "Set tag for multiple GameObjects. items: JSON array of {name, tag}",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Modify,
            Tags = new[] { "tag", "identity", "batch" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "items" },
            TracksWorkflow = true, MutatesScene = true)]
        public static object GameObjectSetTagBatch(
            [SkillParam("JSON array of {name|path|instanceId|entityId, tag}; the tag must already exist in the Tag Manager.")]
            string items)
        {
            return BatchExecutor.Execute<BatchSetTagItem>(items, item =>
            {
                var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path, entityId: item.entityId);
                if (error != null) return new { error = "Object not found", target = item.name ?? item.path ?? item.entityId };

                // Assigning an unregistered tag to go.tag fails silently in the editor (the assignment
                // is a no-op, the object keeps its original tag) without throwing, so an entry with a
                // misspelled tag must be explicitly rejected rather than reported as success:true.
                string target = item.name ?? item.path ?? item.entityId;
                if (!IsTagDefined(item.tag))
                    return SkillParamUtil.InvalidValueError(item.tag, "tag", InternalEditorUtility.tags, target);

                WorkflowManager.SnapshotObject(go);
                Undo.RecordObject(go, "Batch Set Tag");
                go.tag = item.tag;
                return new { target = go.name, entityId = UnityObjectIdUtility.GetEntityId(go), success = true, tag = item.tag };
            }, item => item.name ?? item.path ?? item.entityId, atomic: true);
        }

        /// <summary>
        /// Determines whether <paramref name="tag"/> is already registered in TagManager. An unregistered
        /// tag makes both GameObject.tag's setter and GameObject.FindGameObjectsWithTag fail — the former
        /// silently (no throw, no effect), the latter by throwing UnityException — so any tag write
        /// or tag-filtered read must pass this check first, rather than discovering the problem on impact.
        /// </summary>
        private static bool IsTagDefined(string tag)
        {
            if (string.IsNullOrEmpty(tag))
                return false;
            return InternalEditorUtility.tags.Contains(tag);
        }

        private class BatchSetTagItem
        {
            public string name { get; set; }
            public string entityId { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public string tag { get; set; }
        }

        [UnitySkill("gameobject_set_parent_batch", "Set parent for multiple GameObjects. items: JSON array of {childName, parentName, ...}",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Modify,
            Tags = new[] { "parent", "hierarchy", "reparent", "batch" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "items" },
            TracksWorkflow = true, MutatesScene = true)]
        public static object GameObjectSetParentBatch(
            [SkillParam("JSON array of {childName|childPath|childInstanceId|childEntityId, parentName|parentPath|parentInstanceId|parentEntityId?}; no parent locator = scene root.")]
            string items)
        {
            return BatchExecutor.Execute<BatchSetParentItem>(items, item =>
            {
                var (child, childError) = GameObjectFinder.FindOrError(item.childName, item.childInstanceId, item.childPath, entityId: item.childEntityId);
                if (childError != null) return new { error = "Child object not found", target = item.childName ?? item.childPath ?? item.childEntityId };

                Transform parent = null;
                if (!string.IsNullOrEmpty(item.parentEntityId) || !string.IsNullOrEmpty(item.parentName) || item.parentInstanceId != 0 || !string.IsNullOrEmpty(item.parentPath))
                {
                    var (parentGo, parentError) = GameObjectFinder.FindOrError(item.parentName, item.parentInstanceId, item.parentPath, entityId: item.parentEntityId);
                    if (parentError != null)
                        return new { error = $"Parent not found: {item.parentName ?? item.parentPath ?? item.parentEntityId}" };
                    parent = parentGo.transform;
                }

                WorkflowManager.SnapshotObject(child.transform);
                Undo.SetTransformParent(child.transform, parent, "Batch Set Parent");
                return new
                {
                    target = child.name,
                    entityId = UnityObjectIdUtility.GetEntityId(child),
                    success = true,
                    parent = parent?.name ?? "(root)"
                };
            }, item => item.childName ?? item.childPath ?? item.childEntityId, atomic: true);
        }

        private class BatchSetParentItem
        {
            public string childName { get; set; }
            public string childEntityId { get; set; }
            public int childInstanceId { get; set; }
            public string childPath { get; set; }
            public string parentName { get; set; }
            public string parentEntityId { get; set; }
            public int parentInstanceId { get; set; }
            public string parentPath { get; set; }
        }
    }
}

// Producer:Betsy
