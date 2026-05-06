using UnityEngine;
using UnityEditor;
using System.IO;
using System.Linq;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// Prefab management skills - create, edit, save.
    /// </summary>
    public static class PrefabSkills
    {
        [UnitySkill("prefab_create", "Create a prefab from a GameObject",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Create,
            Tags = new[] { "prefab", "asset", "save", "create" },
            Outputs = new[] { "prefabPath", "name" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true,
            MutatesScene = true, MutatesAssets = true, RiskLevel = "medium")]
        public static object PrefabCreate(string name = null, int instanceId = 0, string path = null, string savePath = null)
        {
            if (Validate.Required(savePath, "savePath") is object reqErr) return reqErr;
            if (Validate.SafePath(savePath, "savePath") is object pathErr) return pathErr;

            var (go, findErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path);
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

        [UnitySkill("prefab_instantiate", "Instantiate a prefab in the scene",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Create,
            Tags = new[] { "prefab", "instantiate", "scene", "spawn" },
            Outputs = new[] { "name", "instanceId" },
            RequiresInput = new[] { "prefabPath" },
            TracksWorkflow = true)]
        public static object PrefabInstantiate(string prefabPath, float x = 0, float y = 0, float z = 0, string name = null,
            string parentName = null, int parentInstanceId = 0, string parentPath = null)
        {
            // Resolve parent first
            GameObject parentGo = null;
            if (!string.IsNullOrEmpty(parentName) || parentInstanceId != 0 || !string.IsNullOrEmpty(parentPath))
            {
                var (found, parentErr) = GameObjectFinder.FindOrError(parentName, parentInstanceId, parentPath);
                if (parentErr != null) return parentErr;
                parentGo = found;
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
                return new { error = $"Prefab not found: {prefabPath}" };

            var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (instance == null)
                return new { error = $"Failed to instantiate prefab: {prefabPath}" };

            if (parentGo != null)
                instance.transform.SetParent(parentGo.transform, false);

            instance.transform.localPosition = new Vector3(x, y, z);

            if (!string.IsNullOrEmpty(name))
                instance.name = name;

            Undo.RegisterCreatedObjectUndo(instance, "Instantiate Prefab");
            WorkflowManager.SnapshotObject(instance, SnapshotType.Created);

            return new { success = true, name = instance.name, instanceId = instance.GetInstanceID(), path = GameObjectFinder.GetPath(instance) };
        }

        [UnitySkill("prefab_instantiate_batch", "Instantiate multiple prefabs (Efficient). items: JSON array of {prefabPath, x, y, z, name, rotX, rotY, rotZ, scaleX, scaleY, scaleZ, parentName, parentInstanceId, parentPath}",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Create,
            Tags = new[] { "prefab", "instantiate", "batch", "spawn", "scene" },
            Outputs = new[] { "results", "name", "instanceId", "position" },
            RequiresInput = new[] { "prefabPath" },
            TracksWorkflow = true)]
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
                // Set parent if specified
                if (!string.IsNullOrEmpty(item.parentName) || item.parentInstanceId != 0 || !string.IsNullOrEmpty(item.parentPath))
                {
                    var (parentGo, parentErr) = GameObjectFinder.FindOrError(item.parentName, item.parentInstanceId, item.parentPath);
                    if (parentErr != null) throw new System.Exception($"Parent not found for '{item.name ?? item.prefabPath}'");
                    instance.transform.SetParent(parentGo.transform, false);
                }

                instance.transform.localPosition = new Vector3(item.x, item.y, item.z);

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
            public string parentName { get; set; }
            public int parentInstanceId { get; set; }
            public string parentPath { get; set; }
        }

        [UnitySkill("prefab_apply", "Apply all overrides from prefab instance to the source prefab asset. Equivalent to prefab_apply_overrides.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Modify,
            Tags = new[] { "prefab", "apply", "overrides", "save" },
            Outputs = new[] { "appliedTo" },
            RequiresInput = new[] { "prefabInstance" },
            TracksWorkflow = true,
            MutatesScene = true, MutatesAssets = true, RiskLevel = "medium")]
        public static object PrefabApply(string name = null, int instanceId = 0, string path = null)
        {
            var (go, goErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path);
            if (goErr != null) return goErr;

            var prefabRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (prefabRoot == null)
                return new { error = "GameObject is not a prefab instance" };

            WorkflowManager.SnapshotObject(prefabRoot);
            var prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(prefabRoot);
            PrefabUtility.ApplyPrefabInstance(prefabRoot, InteractionMode.UserAction);

            return new { success = true, appliedTo = prefabPath };
        }

        [UnitySkill("prefab_unpack", "Unpack a prefab instance. completely=false: unpack outermost root only; completely=true: fully unpack all nested prefabs.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Modify,
            Tags = new[] { "prefab", "unpack", "disconnect", "instance" },
            Outputs = new[] { "unpacked" },
            RequiresInput = new[] { "prefabInstance" },
            TracksWorkflow = true,
            MutatesScene = true, RiskLevel = "medium")]
        public static object PrefabUnpack(string name = null, int instanceId = 0, string path = null, bool completely = false)
        {
            var (go, findErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path);
            if (findErr != null) return findErr;

            WorkflowManager.SnapshotObject(go);
            var mode = completely ? PrefabUnpackMode.Completely : PrefabUnpackMode.OutermostRoot;
            PrefabUtility.UnpackPrefabInstance(go, mode, InteractionMode.UserAction);

            return new { success = true, unpacked = go.name };
        }

        [UnitySkill("prefab_get_overrides", "Get list of property overrides on a prefab instance",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Query,
            Tags = new[] { "prefab", "overrides", "inspect", "diff" },
            Outputs = new[] { "prefabPath", "propertyOverrides", "addedComponents", "removedComponents", "addedGameObjects", "hasOverrides" },
            RequiresInput = new[] { "prefabInstance" },
            ReadOnly = true)]
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

        [UnitySkill("prefab_revert_overrides", "Revert all overrides on a prefab instance back to prefab values",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Modify,
            Tags = new[] { "prefab", "revert", "overrides", "reset" },
            Outputs = new[] { "reverted" },
            RequiresInput = new[] { "prefabInstance" })]
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

        [UnitySkill("prefab_apply_overrides", "Apply all overrides from instance to source prefab asset. Equivalent to prefab_apply.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Modify,
            Tags = new[] { "prefab", "apply", "overrides", "save" },
            Outputs = new[] { "appliedTo" },
            RequiresInput = new[] { "prefabInstance" })]
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
        [UnitySkill("prefab_create_variant", "Create a prefab variant from an existing prefab",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Create,
            Tags = new[] { "prefab", "variant", "create", "inheritance" },
            Outputs = new[] { "sourcePath", "variantPath", "name" },
            RequiresInput = new[] { "sourcePrefabPath" },
            TracksWorkflow = true)]
        public static object PrefabCreateVariant(string sourcePrefabPath, string variantPath)
        {
            if (Validate.Required(sourcePrefabPath, "sourcePrefabPath") is object err) return err;
            if (Validate.SafePath(variantPath, "variantPath") is object pathErr) return pathErr;

            var source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePrefabPath);
            if (source == null) return new { error = $"Prefab not found: {sourcePrefabPath}" };

            var dir = Path.GetDirectoryName(variantPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var instance = PrefabUtility.InstantiatePrefab(source) as GameObject;
            var variant = PrefabUtility.SaveAsPrefabAssetAndConnect(
                instance, variantPath, InteractionMode.AutomatedAction);
            Object.DestroyImmediate(instance);

            return new { success = true, sourcePath = sourcePrefabPath, variantPath, name = variant.name };
        }

        [UnitySkill("prefab_find_instances", "Find all instances of a prefab in the current scene",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Query,
            Tags = new[] { "prefab", "find", "instances", "scene" },
            Outputs = new[] { "prefabPath", "count", "instances" },
            RequiresInput = new[] { "prefabPath" },
            ReadOnly = true)]
        public static object PrefabFindInstances(string prefabPath, int limit = 50)
        {
            if (Validate.Required(prefabPath, "prefabPath") is object err) return err;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) return new { error = $"Prefab not found: {prefabPath}" };

            var allObjects = FindHelper.FindAll<GameObject>();
            var instances = allObjects
                .Where(go => PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go) == prefabPath)
                .Take(limit)
                .Select(go => new { name = go.name, path = GameObjectFinder.GetPath(go), instanceId = go.GetInstanceID() })
                .ToArray();

            return new { success = true, prefabPath, count = instances.Length, instances };
        }

        [UnitySkill("prefab_set_property", "Set a property on a component inside a Prefab asset file. Supports basic types (int/float/bool/string/enum), vectors, colors, and asset references via assetReferencePath",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Modify,
            Tags = new[] { "prefab", "property", "set", "component", "asset" },
            Outputs = new[] { "prefabPath", "gameObject", "component", "property", "valueSet" },
            RequiresInput = new[] { "prefabAsset", "componentType" },
            TracksWorkflow = true)]
        public static object PrefabSetProperty(
            string prefabPath = null, string componentType = null, string propertyName = null,
            string value = null, string assetReferencePath = null, string gameObjectName = null)
        {
            if (Validate.Required(prefabPath, "prefabPath") is object reqErr1) return reqErr1;
            if (Validate.SafePath(prefabPath, "prefabPath") is object pathErr) return pathErr;
            if (Validate.Required(componentType, "componentType") is object reqErr2) return reqErr2;
            if (Validate.Required(propertyName, "propertyName") is object reqErr3) return reqErr3;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) return new { error = $"Prefab not found: {prefabPath}" };

            // Find target GameObject inside prefab (root or child by name)
            GameObject targetGo = prefab;
            if (!string.IsNullOrEmpty(gameObjectName))
            {
                var child = prefab.transform.Find(gameObjectName);
                if (child == null)
                {
                    // Deep search
                    foreach (var t in prefab.GetComponentsInChildren<Transform>(true))
                    {
                        if (t.name == gameObjectName) { child = t; break; }
                    }
                }
                if (child == null)
                    return new { error = $"Child GameObject '{gameObjectName}' not found in prefab" };
                targetGo = child.gameObject;
            }

            // Find component
            var compType = ComponentSkills.FindComponentType(componentType);
            if (compType == null)
                return new { error = $"Component type not found: {componentType}" };

            var comp = targetGo.GetComponent(compType);
            if (comp == null)
                return new { error = $"Component '{componentType}' not found on '{targetGo.name}' in prefab" };

            // Use SerializedObject to edit prefab asset
            var so = new SerializedObject(comp);
            var prop = FindSerializedProperty(so, propertyName);
            if (prop == null)
                return new { error = $"Property '{propertyName}' not found on {componentType}", availableProperties = ListSerializedProperties(so) };

            WorkflowManager.SnapshotObject(comp);

            // Set value based on property type
            if (!string.IsNullOrEmpty(assetReferencePath))
            {
                if (prop.propertyType != SerializedPropertyType.ObjectReference)
                    return new { error = $"Property '{propertyName}' is not an Object reference field (type: {prop.propertyType})" };

                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetReferencePath);
                if (asset == null)
                    return new { error = $"Asset not found: {assetReferencePath}" };

                prop.objectReferenceValue = asset;
            }
            else if (!string.IsNullOrEmpty(value))
            {
                if (!SetSerializedPropertyValue(prop, value))
                    return new { error = $"Failed to set value '{value}' on property '{propertyName}' (type: {prop.propertyType})" };
            }
            else
            {
                return new { error = "Either 'value' or 'assetReferencePath' must be provided" };
            }

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(comp);
            AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                prefabPath,
                gameObject = targetGo.name,
                component = componentType,
                property = propertyName,
                valueSet = !string.IsNullOrEmpty(assetReferencePath) ? assetReferencePath : value
            };
        }

        #region Prefab SerializedProperty Helpers

        /// <summary>
        /// Find a SerializedProperty by name with Unity naming convention fallbacks (m_PropertyName, _propertyName).
        /// </summary>
        private static SerializedProperty FindSerializedProperty(SerializedObject so, string propertyName)
        {
            // Direct match
            var prop = so.FindProperty(propertyName);
            if (prop != null) return prop;

            // Unity convention: m_PropertyName
            var mName = "m_" + char.ToUpper(propertyName[0]) + propertyName.Substring(1);
            prop = so.FindProperty(mName);
            if (prop != null) return prop;

            // Underscore prefix: _propertyName
            prop = so.FindProperty("_" + propertyName);
            if (prop != null) return prop;

            // Try lowercase first char with m_ prefix
            var mLower = "m_" + propertyName;
            prop = so.FindProperty(mLower);
            if (prop != null) return prop;

            return null;
        }

        /// <summary>
        /// Set a SerializedProperty value from a string. Returns true on success.
        /// </summary>
        private static bool SetSerializedPropertyValue(SerializedProperty prop, string value)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    if (int.TryParse(value, out var intVal)) { prop.intValue = intVal; return true; }
                    if (long.TryParse(value, out var longVal)) { prop.longValue = longVal; return true; }
                    return false;

                case SerializedPropertyType.Float:
                    if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var floatVal))
                    { prop.floatValue = floatVal; return true; }
                    if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var doubleVal))
                    { prop.doubleValue = doubleVal; return true; }
                    return false;

                case SerializedPropertyType.Boolean:
                    var lower = value.ToLower().Trim();
                    prop.boolValue = lower == "true" || lower == "1" || lower == "yes" || lower == "on";
                    return true;

                case SerializedPropertyType.String:
                    prop.stringValue = value;
                    return true;

                case SerializedPropertyType.Enum:
                    // Try name match first
                    if (prop.enumDisplayNames != null)
                    {
                        for (int i = 0; i < prop.enumDisplayNames.Length; i++)
                        {
                            if (string.Equals(prop.enumDisplayNames[i], value, System.StringComparison.OrdinalIgnoreCase))
                            { prop.enumValueIndex = i; return true; }
                        }
                    }
                    // Try index
                    if (int.TryParse(value, out var enumIdx)) { prop.enumValueIndex = enumIdx; return true; }
                    return false;

                case SerializedPropertyType.Color:
                    var color = ComponentSkills.ConvertValue(value, typeof(Color));
                    if (color is Color c) { prop.colorValue = c; return true; }
                    return false;

                case SerializedPropertyType.Vector2:
                    var v2 = ComponentSkills.ConvertValue(value, typeof(Vector2));
                    if (v2 is Vector2 vec2) { prop.vector2Value = vec2; return true; }
                    return false;

                case SerializedPropertyType.Vector3:
                    var v3 = ComponentSkills.ConvertValue(value, typeof(Vector3));
                    if (v3 is Vector3 vec3) { prop.vector3Value = vec3; return true; }
                    return false;

                case SerializedPropertyType.Vector4:
                    var v4 = ComponentSkills.ConvertValue(value, typeof(Vector4));
                    if (v4 is Vector4 vec4) { prop.vector4Value = vec4; return true; }
                    return false;

                case SerializedPropertyType.Rect:
                    var rect = ComponentSkills.ConvertValue(value, typeof(Rect));
                    if (rect is Rect r) { prop.rectValue = r; return true; }
                    return false;

                case SerializedPropertyType.Bounds:
                    var bounds = ComponentSkills.ConvertValue(value, typeof(Bounds));
                    if (bounds is Bounds b) { prop.boundsValue = b; return true; }
                    return false;

                case SerializedPropertyType.Vector2Int:
                    var v2i = ComponentSkills.ConvertValue(value, typeof(Vector2Int));
                    if (v2i is Vector2Int vec2i) { prop.vector2IntValue = vec2i; return true; }
                    return false;

                case SerializedPropertyType.Vector3Int:
                    var v3i = ComponentSkills.ConvertValue(value, typeof(Vector3Int));
                    if (v3i is Vector3Int vec3i) { prop.vector3IntValue = vec3i; return true; }
                    return false;

                case SerializedPropertyType.LayerMask:
                    if (int.TryParse(value, out var mask)) { prop.intValue = mask; return true; }
                    var layer = LayerMask.NameToLayer(value);
                    if (layer >= 0) { prop.intValue = 1 << layer; return true; }
                    return false;

                default:
                    return false;
            }
        }

        /// <summary>
        /// List top-level serialized properties for error diagnostics.
        /// </summary>
        private static string[] ListSerializedProperties(SerializedObject so)
        {
            var names = new System.Collections.Generic.List<string>();
            var prop = so.GetIterator();
            bool enter = true;
            while (prop.NextVisible(enter) && names.Count < 30)
            {
                enter = false;
                if (prop.name == "m_Script") continue;
                names.Add(prop.name);
            }
            return names.ToArray();
        }

        #endregion
    }
}
