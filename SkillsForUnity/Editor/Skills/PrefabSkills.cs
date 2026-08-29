using UnityEngine;
using UnityEditor;
using System.IO;
using System.Linq;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// Prefab management skills: create, edit, save.
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

            var prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(go, savePath, InteractionMode.UserAction);

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
            string parentName = null, int parentInstanceId = 0, string parentPath = null, string parentEntityId = null)
        {
            GameObject parentGo = null;
            if (!string.IsNullOrEmpty(parentEntityId) || !string.IsNullOrEmpty(parentName) || parentInstanceId != 0 || !string.IsNullOrEmpty(parentPath))
            {
                var (found, parentErr) = GameObjectFinder.FindOrError(parentName, parentInstanceId, parentPath, entityId: parentEntityId);
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

            return new { success = true, name = instance.name, entityId = UnityObjectIdUtility.GetEntityId(instance), instanceId = UnityObjectIdUtility.GetObjectId(instance), path = GameObjectFinder.GetPath(instance) };
        }

        [UnitySkill("prefab_instantiate_batch", "Instantiate multiple prefabs (Efficient). items: JSON array of {prefabPath, x, y, z, name, rotX, rotY, rotZ, scaleX, scaleY, scaleZ, parentName, parentInstanceId, parentPath, parentEntityId}",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Create,
            Tags = new[] { "prefab", "instantiate", "batch", "spawn", "scene" },
            Outputs = new[] { "results", "name", "instanceId", "position" },
            RequiresInput = new[] { "prefabPath" },
            TracksWorkflow = true)]
        public static object PrefabInstantiateBatch(string items)
        {
            // Cache loaded prefabs to avoid repeated AssetDatabase round trips
            var prefabCache = new System.Collections.Generic.Dictionary<string, GameObject>();

            return BatchExecutor.Execute<BatchInstantiateItem>(items, item =>
            {
                if (string.IsNullOrEmpty(item.prefabPath))
                    return new { error = "prefabPath required" };

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
                    return new { error = $"Prefab not found: {item.prefabPath}" };

                var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                if (instance == null)
                    return new { error = $"Failed to instantiate prefab: {item.prefabPath}" };
                if (!string.IsNullOrEmpty(item.parentEntityId) || !string.IsNullOrEmpty(item.parentName) || item.parentInstanceId != 0 || !string.IsNullOrEmpty(item.parentPath))
                {
                    var (parentGo, parentErr) = GameObjectFinder.FindOrError(item.parentName, item.parentInstanceId, item.parentPath, entityId: item.parentEntityId);
                    if (parentErr != null) return new { error = $"Parent not found for '{item.name ?? item.prefabPath}'" };
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
                    entityId = UnityObjectIdUtility.GetEntityId(instance),
                    instanceId = UnityObjectIdUtility.GetObjectId(instance),
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
            public string parentEntityId { get; set; }
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
            PushInstanceOverridesToSource(prefabRoot);
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
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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
                // GetPropertyModifications returns the bookkeeping entries Unity writes into every
                // newly created prefab instance's modification list, regardless of whether the
                // value actually differs from the source — verified in practice: even a
                // completely untouched instance has m_LocalPosition.x/y/z,
                // m_LocalRotation.w/x/y/z, m_LocalEulerAnglesHint.x/y/z, and m_Name all in the
                // list, a constant ~11 "overrides", with hasOverrides always true.
                //
                // PropertyModification.target is not the live instance object in the scene, but a
                // reference to the *source prefab asset* (confirmed via instance ID: it matches
                // the object loaded from the asset, not the negative/session-only ID a scene
                // instance would have; and PrefabUtility.GetCorrespondingObjectFromSource(o.target)
                // always returns null, because the source itself has no source). So comparing
                // "instance value" to "source value" requires first mapping each source object
                // back to the live object in this instance — exactly the reverse of the direction
                // GetCorrespondingObjectFromSource supports — done here by walking the instance
                // hierarchy once and indexing by GetCorrespondingObjectFromSource(live).
                var liveBySource = new System.Collections.Generic.Dictionary<UnityEngine.Object, UnityEngine.Object>();
                void RegisterLive(UnityEngine.Object live)
                {
                    if (live == null) return;
                    var src = PrefabUtility.GetCorrespondingObjectFromSource(live);
                    if (src != null) liveBySource[src] = live;
                }
                RegisterLive(prefabRoot);
                foreach (var t in prefabRoot.GetComponentsInChildren<Transform>(true))
                {
                    RegisterLive(t.gameObject);
                    foreach (var comp in t.GetComponents<Component>())
                        RegisterLive(comp);
                }

                foreach (var o in overrides)
                {
                    if (o.target == null) continue;

                    // The instance's own name is unconditionally excluded from the override
                    // determination, regardless of whether it differs from the source name —
                    // verified against PrefabUtility.HasPrefabInstanceAnyOverrides's behavior: even
                    // with a custom name set on the instance, it still returns false. If this only
                    // filtered by value equality, almost every renamed instance in a scene would be
                    // falsely reported as having an override.
                    if (o.propertyPath == "m_Name") continue;

                    // If the source object has no live counterpart in this instance (e.g. it
                    // belongs to a nested prefab structure not touched by this traversal) — there's
                    // no way to prove it's just a phantom default, so it's kept to avoid silently
                    // dropping a genuine override.
                    if (!liveBySource.TryGetValue(o.target, out var liveInstance))
                    {
                        propOverrides.Add(new { target = o.target.name, property = o.propertyPath, value = o.value });
                        continue;
                    }

                    var instProp = new SerializedObject(liveInstance).FindProperty(o.propertyPath);
                    var srcProp = new SerializedObject(o.target).FindProperty(o.propertyPath);
                    if (instProp != null && srcProp != null && SerializedProperty.DataEquals(instProp, srcProp))
                        continue; // Instance value matches the source asset; a phantom bookkeeping entry, not a genuine override

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
                // Reuse the count from the actual field-by-field comparison above rather than
                // PrefabUtility.HasPrefabInstanceAnyOverrides — that aggregate value reads Unity's
                // cached modification list, which can be stale for a property just changed in
                // memory and not yet flushed into that cache (e.g. a caller bypassing
                // SetDirty/RecordPrefabInstancePropertyModifications to change a Transform field
                // directly). Deriving hasOverrides from propOverrides.Count keeps these two fields
                // in the same response self-consistent.
                hasOverrides = propOverrides.Count > 0 || addedComponents.Count > 0 || removedComponents.Count > 0 || addedObjects.Count > 0
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
            PullSourceValuesToInstance(prefabRoot);
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
            PushInstanceOverridesToSource(prefabRoot);
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
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object PrefabFindInstances(string prefabPath, int limit = 50)
        {
            if (Validate.Required(prefabPath, "prefabPath") is object err) return err;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) return new { error = $"Prefab not found: {prefabPath}" };

            var allObjects = FindHelper.FindAll<GameObject>();
            var instances = allObjects
                .Where(go => PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go) == prefabPath)
                .Take(limit)
                .Select(go => new { name = go.name, path = GameObjectFinder.GetPath(go), entityId = UnityObjectIdUtility.GetEntityId(go), instanceId = UnityObjectIdUtility.GetObjectId(go) })
                .ToArray();

            return new { success = true, prefabPath, count = instances.Length, instances };
        }

        [UnitySkill("prefab_set_property", "Set a property on a component inside a Prefab asset file. Supports basic types (int/float/bool/string/enum), vectors, colors, and asset references via assetReferencePath",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Modify,
            Tags = new[] { "prefab", "property", "set", "component", "asset" },
            Outputs = new[] { "prefabPath", "gameObject", "component", "property", "valueSet" },
            // Cannot write "prefabAsset": across the whole codebase it appears here alone — this
            // skill doesn't accept that parameter (the asset comes in via prefabPath), and no skill
            // outputs it either, so this token constrains nothing and links nothing up; an agent
            // taking it literally would just get UNKNOWN_PARAM. prefabPath is also prefab_create's
            // return value, so the corrected token also wires both into the Outputs→RequiresInput chain.
            RequiresInput = new[] { "prefabPath", "componentType" },
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

            // Locate the target GameObject inside the prefab (root, or find a child by name)
            GameObject targetGo = prefab;
            if (!string.IsNullOrEmpty(gameObjectName))
            {
                var child = prefab.transform.Find(gameObjectName);
                if (child == null)
                {
                    foreach (var t in prefab.GetComponentsInChildren<Transform>(true))
                    {
                        if (t.name == gameObjectName) { child = t; break; }
                    }
                }
                if (child == null)
                    return new { error = $"Child GameObject '{gameObjectName}' not found in prefab" };
                targetGo = child.gameObject;
            }

            var compType = ComponentSkills.FindComponentType(componentType);
            if (compType == null)
                return new { error = $"Component type not found: {componentType}" };

            var comp = targetGo.GetComponent(compType);
            if (comp == null)
                return new { error = $"Component '{componentType}' not found on '{targetGo.name}' in prefab" };

            var so = new SerializedObject(comp);
            var prop = FindSerializedProperty(so, propertyName);
            if (prop == null)
                return new { error = $"Property '{propertyName}' not found on {componentType}", availableProperties = ListSerializedProperties(so) };

            WorkflowManager.SnapshotObject(comp);

            // Dispatch the write based on the property type
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
                bool applied = false;
                bool typeSupported = true;
                try
                {
                    applied = SetSerializedPropertyValue(prop, value, out typeSupported);
                }
                catch (System.Exception ex)
                {
                    // For invalid text formats, the converter throws rather than returning null
                    // (e.g. passing "1,2" for a Vector3, a Quaternion given only two components,
                    // or a non-numeric key inside JSON-object form). Not catching this would
                    // surface as an unclassified SKILL_ERROR + abort carrying only the raw parser
                    // message. No other exception source exists within this call, so this is
                    // judged to be "the value is the problem" rather than swallowing a real bug.
                    return new { error = $"Invalid value '{value}' for property '{propertyName}' (type: {prop.propertyType}): {ex.Message}" };
                }

                if (!applied)
                {
                    // "Failed to set value" reads as "your value is wrong" — but for an unsupported
                    // property type, nothing the caller writes could ever succeed; only spelling
                    // this out stops it from retrying the same call with a different format.
                    // Both messages open with their own classifying word ("Invalid" / "Unsupported"),
                    // so SkillErrorClassifier's leading-word rule yields SEMANTIC_INVALID + fix_and_retry,
                    // instead of the unclassified SKILL_ERROR + abort the old wording
                    // "Failed to set value …" used to produce.
                    return typeSupported
                        ? new { error = $"Invalid value '{value}' for property '{propertyName}' (type: {prop.propertyType}) — that property type is supported but the text could not be parsed into it." }
                        : new { error = $"Unsupported serialized property type {prop.propertyType} for property '{propertyName}'. prefab_set_property writes Integer, Float, Boolean, String, Enum, Color, Vector2/3/4, Vector2Int/3Int, Quaternion, Rect, Bounds and LayerMask from 'value'; use assetReferencePath for an ObjectReference field." };
                }
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
        /// Finds the genuine property differences (values that actually differ) between a
        /// prefab instance and its source asset, as distinct from the phantom bookkeeping entries
        /// PrefabUtility.GetPropertyModifications attaches to every new instance
        /// (m_LocalPosition/m_LocalRotation/m_LocalEulerAnglesHint/m_Name).
        ///
        /// <para>PropertyModification.target points to the *source* prefab asset object rather
        /// than the live instance (confirmed via instance ID in practice), so
        /// GetCorrespondingObjectFromSource cannot be called on it directly — that would always
        /// return null. Here the live instance hierarchy is walked once, indexed by
        /// GetCorrespondingObjectFromSource(live) -&gt; live (the direction this API actually
        /// supports), and each modification's live object is looked up in reverse.
        /// The detection logic mirrors PrefabGetOverrides; a separate copy is kept here rather
        /// than extracting a shared helper, to avoid touching that already-verified method.</para>
        /// </summary>
        private static System.Collections.Generic.List<(UnityEngine.Object live, UnityEngine.Object source, string propertyPath)> FindGenuineOverrides(GameObject instanceRoot)
        {
            var result = new System.Collections.Generic.List<(UnityEngine.Object, UnityEngine.Object, string)>();
            var overrides = PrefabUtility.GetPropertyModifications(instanceRoot);
            if (overrides == null) return result;

            var liveBySource = new System.Collections.Generic.Dictionary<UnityEngine.Object, UnityEngine.Object>();
            void RegisterLive(UnityEngine.Object live)
            {
                if (live == null) return;
                var src = PrefabUtility.GetCorrespondingObjectFromSource(live);
                if (src != null) liveBySource[src] = live;
            }
            RegisterLive(instanceRoot);
            foreach (var t in instanceRoot.GetComponentsInChildren<Transform>(true))
            {
                RegisterLive(t.gameObject);
                foreach (var comp in t.GetComponents<Component>())
                    RegisterLive(comp);
            }

            foreach (var o in overrides)
            {
                if (o.target == null) continue;
                if (o.propertyPath == "m_Name") continue; // Unconditionally excluded from override determination, matching PrefabUtility.HasPrefabInstanceAnyOverrides's behavior
                if (!liveBySource.TryGetValue(o.target, out var liveInstance)) continue;

                var instProp = new SerializedObject(liveInstance).FindProperty(o.propertyPath);
                var srcProp = new SerializedObject(o.target).FindProperty(o.propertyPath);
                if (instProp == null || srcProp == null) continue;
                if (SerializedProperty.DataEquals(instProp, srcProp)) continue; // Matches the source; a phantom bookkeeping entry, not a genuine override

                result.Add((liveInstance, o.target, o.propertyPath));
            }
            return result;
        }

        /// <summary>
        /// Writes each genuine override property's live-instance value onto the corresponding
        /// prefab source asset object, and saves the asset.
        ///
        /// <para>Called before PrefabUtility.ApplyPrefabInstance. Verified in practice (by
        /// directly inspecting the raw YAML on disk): relying on that API alone leaves the source
        /// asset completely unchanged for a Transform override, even after separately trying
        /// EditorUtility.SetDirty, RecordPrefabInstancePropertyModifications, Undo.RecordObject,
        /// and SerializedObject.ApplyModifiedProperties — in this headless environment where the
        /// Inspector never repaints, none of them make Unity's native prefab-override comparison
        /// detect the difference. So instead of relying on that comparison, this copies values
        /// directly and explicitly calls AssetDatabase.SaveAssets; this codebase has already
        /// confirmed the same pattern around "a file change triggers a domain reload": background
        /// work Unity normally does automatically doesn't happen without a window-focus/idle event.</para>
        /// </summary>
        private static void PushInstanceOverridesToSource(GameObject instanceRoot)
        {
            var diffs = FindGenuineOverrides(instanceRoot);
            var touchedSources = new System.Collections.Generic.HashSet<UnityEngine.Object>();
            foreach (var (live, source, propertyPath) in diffs)
            {
                var liveProp = new SerializedObject(live).FindProperty(propertyPath);
                var srcSO = new SerializedObject(source);
                var srcProp = srcSO.FindProperty(propertyPath);
                if (liveProp == null || srcProp == null) continue;
                try { srcProp.boxedValue = liveProp.boxedValue; }
                catch { continue; /* Not every property type supports boxedValue */ }
                srcSO.ApplyModifiedProperties();
                EditorUtility.SetDirty(source);
                touchedSources.Add(source);
            }
            if (touchedSources.Count > 0)
                AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Writes each genuine override property's prefab-source-asset value back onto the live
        /// instance — the reverse direction of PushInstanceOverridesToSource.
        ///
        /// <para>Called before PrefabUtility.RevertPrefabInstance, for the same reason: that API
        /// relies on the same native override-comparison cache used by Apply, and in this
        /// environment that cache doesn't get populated by script-driven changes; otherwise
        /// revert would silently leave the instance's already-diverged Transform values untouched.</para>
        /// </summary>
        private static void PullSourceValuesToInstance(GameObject instanceRoot)
        {
            var diffs = FindGenuineOverrides(instanceRoot);
            foreach (var (live, source, propertyPath) in diffs)
            {
                var liveSO = new SerializedObject(live);
                var liveProp = liveSO.FindProperty(propertyPath);
                var srcProp = new SerializedObject(source).FindProperty(propertyPath);
                if (liveProp == null || srcProp == null) continue;
                try { liveProp.boxedValue = srcProp.boxedValue; }
                catch { continue; /* Not every property type supports boxedValue */ }
                liveSO.ApplyModifiedProperties();
                EditorUtility.SetDirty(live);
            }
        }

        /// <summary>
        /// Looks up a SerializedProperty by name, falling back through Unity naming conventions
        /// (m_PropertyName, _propertyName).
        /// </summary>
        private static SerializedProperty FindSerializedProperty(SerializedObject so, string propertyName)
        {
            var prop = so.FindProperty(propertyName);
            if (prop != null) return prop;

            // Unity convention: m_PropertyName
            var mName = "m_" + char.ToUpper(propertyName[0]) + propertyName.Substring(1);
            prop = so.FindProperty(mName);
            if (prop != null) return prop;

            // Underscore prefix: _propertyName
            prop = so.FindProperty("_" + propertyName);
            if (prop != null) return prop;

            // m_ prefix + first letter kept lowercase
            var mLower = "m_" + propertyName;
            prop = so.FindProperty(mLower);
            if (prop != null) return prop;

            return null;
        }

        /// <summary>
        /// Writes a SerializedProperty's value from a string, returning true on success.
        ///
        /// <para><paramref name="typeSupported"/> distinguishes two kinds of failure — a single
        /// "Failed to set value" message used to conflate them: false means there's no branch here
        /// at all for this <see cref="SerializedPropertyType"/> (nothing the caller writes in
        /// <c>value</c> could work); true means the type is supported but the given text couldn't
        /// be parsed. This switch is the sole source of truth for which types are supported — only
        /// the default branch clears this flag.</para>
        /// </summary>
        private static bool SetSerializedPropertyValue(SerializedProperty prop, string value, out bool typeSupported)
        {
            typeSupported = true;
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
                    // Try matching by name first, then fall back to matching by index
                    if (prop.enumDisplayNames != null)
                    {
                        for (int i = 0; i < prop.enumDisplayNames.Length; i++)
                        {
                            if (string.Equals(prop.enumDisplayNames[i], value, System.StringComparison.OrdinalIgnoreCase))
                            { prop.enumValueIndex = i; return true; }
                        }
                    }
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

                // m_LocalRotation is the most-written property on prefabs, and it's a Quaternion;
                // missing this branch would make every rotation write fall through to default,
                // returning "Failed to set value ... (type: Quaternion)".
                // ConvertValue accepts 3 components (Euler angles, in degrees) or 4 (raw x,y,z,w),
                // consistent with the Vector branches above.
                case SerializedPropertyType.Quaternion:
                    var quat = ComponentSkills.ConvertValue(value, typeof(Quaternion));
                    if (quat is Quaternion q) { prop.quaternionValue = q; return true; }
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
                    typeSupported = false;
                    return false;
            }
        }

        /// <summary>
        /// Lists top-level serialized properties, for error diagnostics.
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

// Producer:Betsy
