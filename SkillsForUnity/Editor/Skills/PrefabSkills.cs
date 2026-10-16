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
            Outputs = new[] { "prefabPath", "name", "connected" },
            RequiredParams = new[] { "savePath" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true,
            MutatesScene = true, MutatesAssets = true, RiskLevel = "medium")]
        public static object PrefabCreate(string name = null, int instanceId = 0, string path = null,
            [SkillParam("Asset path under Assets/ (or a writable package); '.prefab' is appended when missing. An existing prefab there is overwritten.")]
            string savePath = null)
        {
            if (Validate.Required(savePath, "savePath") is object reqErr) return reqErr;
            if (Validate.SafePath(savePath, "savePath") is object pathErr) return pathErr;

            var (go, findErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path);
            if (findErr != null) return findErr;

            savePath = WithPrefabExtension(savePath);
            var dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(go, savePath, InteractionMode.UserAction);
            if (prefab == null)
                return new { error = $"Failed to save '{go.name}' as a prefab at '{savePath}'" };

            WorkflowManager.SnapshotCreatedAsset(prefab);

            var prefabPath = AssetDatabase.GetAssetPath(prefab);
            return new
            {
                success = true,
                prefabPath,
                name = prefab.name,
                connected = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go) == prefabPath
            };
        }

        // The planner predicts '<savePath>.prefab'; SaveAsPrefabAsset* rejects any other extension.
        private static string WithPrefabExtension(string assetPath) =>
            assetPath.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase) ? assetPath : assetPath + ".prefab";

        [UnitySkill("prefab_instantiate", "Instantiate a prefab in the scene",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Create,
            Tags = new[] { "prefab", "instantiate", "scene", "spawn" },
            Outputs = new[] { "name", "instanceId" },
            RequiresInput = new[] { "prefabPath" },
            TracksWorkflow = true, MutatesScene = true)]
        public static object PrefabInstantiate(string prefabPath,
            [SkillParam("x/y/z: localPosition relative to the given parent; with no parent* locator, equals world position (scene root).")]
            float x = 0, float y = 0, float z = 0, string name = null,
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
            RequiresInput = new[] { "items" },
            TracksWorkflow = true, MutatesScene = true)]
        public static object PrefabInstantiateBatch(
            [SkillParam("JSON array of {prefabPath, x/y/z?, name?, rotX/Y/Z?, scaleX/Y/Z? (default 1), parentName|parentPath|parentInstanceId|parentEntityId?}.")]
            string items)
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

                // Resolved before instantiating, so a bad parent leaves no stray instance behind.
                GameObject parentGo = null;
                if (!string.IsNullOrEmpty(item.parentEntityId) || !string.IsNullOrEmpty(item.parentName) || item.parentInstanceId != 0 || !string.IsNullOrEmpty(item.parentPath))
                {
                    var (foundParent, parentErr) = GameObjectFinder.FindOrError(item.parentName, item.parentInstanceId, item.parentPath, entityId: item.parentEntityId);
                    if (parentErr != null) return new { error = $"Parent not found for '{item.name ?? item.prefabPath}'" };
                    parentGo = foundParent;
                }

                var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                if (instance == null)
                    return new { error = $"Failed to instantiate prefab: {item.prefabPath}" };
                if (parentGo != null)
                    instance.transform.SetParent(parentGo.transform, false);

                instance.transform.localPosition = new Vector3(item.x, item.y, item.z);

                if (item.rotX != 0 || item.rotY != 0 || item.rotZ != 0)
                    instance.transform.eulerAngles = new Vector3(item.rotX, item.rotY, item.rotZ);

                if (item.scaleX != 1 || item.scaleY != 1 || item.scaleZ != 1)
                    instance.transform.localScale = new Vector3(item.scaleX, item.scaleY, item.scaleZ);

                if (!string.IsNullOrEmpty(item.name))
                    instance.name = item.name;

                Undo.RegisterCreatedObjectUndo(instance, "Batch Instantiate Prefab");
                WorkflowManager.SnapshotObject(instance, SnapshotType.Created);
                var world = instance.transform.position;
                var local = instance.transform.localPosition;
                return new
                {
                    success = true,
                    name = instance.name,
                    entityId = UnityObjectIdUtility.GetEntityId(instance),
                    instanceId = UnityObjectIdUtility.GetObjectId(instance),
                    position = new { x = world.x, y = world.y, z = world.z },
                    localPosition = new { x = local.x, y = local.y, z = local.z }
                };
            }, item => item.prefabPath, atomic: true);
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
            RequiresInput = new[] { "gameObject" },
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
            RequiresInput = new[] { "gameObject" },
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
            RequiresInput = new[] { "gameObject" },
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
            RequiresInput = new[] { "gameObject" }, TracksWorkflow = true, MutatesScene = true)]
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
            RequiresInput = new[] { "gameObject" }, MutatesAssets = true)]
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
            Outputs = new[] { "sourcePath", "variantPath", "name", "isVariant" },
            RequiredParams = new[] { "variantPath" },
            RequiresInput = new[] { "sourcePrefabPath" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object PrefabCreateVariant(string sourcePrefabPath, string variantPath)
        {
            if (Validate.Required(sourcePrefabPath, "sourcePrefabPath") is object err) return err;
            if (Validate.SafePath(variantPath, "variantPath") is object pathErr) return pathErr;

            var source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePrefabPath);
            if (source == null) return new { error = $"Prefab not found: {sourcePrefabPath}" };

            variantPath = WithPrefabExtension(variantPath);
            var dir = Path.GetDirectoryName(variantPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var instance = PrefabUtility.InstantiatePrefab(source) as GameObject;
            if (instance == null) return new { error = $"Failed to instantiate prefab: {sourcePrefabPath}" };
            GameObject variant;
            try
            {
                variant = PrefabUtility.SaveAsPrefabAssetAndConnect(instance, variantPath, InteractionMode.AutomatedAction);
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
            if (variant == null)
                return new { error = $"Failed to save a variant of '{sourcePrefabPath}' at '{variantPath}'" };

            WorkflowManager.SnapshotCreatedAsset(variant);

            return new
            {
                success = true,
                sourcePath = sourcePrefabPath,
                variantPath = AssetDatabase.GetAssetPath(variant),
                name = variant.name,
                isVariant = PrefabUtility.GetPrefabAssetType(variant) == PrefabAssetType.Variant
            };
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
            RequiredParams = new[] { "propertyName" },
            // Cannot write "prefabAsset": across the whole codebase it appears here alone — this
            // skill doesn't accept that parameter (the asset comes in via prefabPath), and no skill
            // outputs it either, so this token constrains nothing and links nothing up; an agent
            // taking it literally would just get UNKNOWN_PARAM. prefabPath is also prefab_create's
            // return value, so the corrected token also wires both into the Outputs→RequiresInput chain.
            RequiresInput = new[] { "prefabPath", "componentType" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true)]
        public static object PrefabSetProperty(
            string prefabPath = null, string componentType = null,
            [SkillParam("Tries the exact name, then m_<Name>, _<name> and m_<name> (Unity's serialized-field naming conventions).")]
            string propertyName = null,
            string value = null,
            [SkillParam("For an ObjectReference property; when given it is written and 'value' is ignored even if both are provided.")]
            string assetReferencePath = null,
            [SkillParam("Transform.Find path from the prefab root (e.g. 'Child/Grandchild'), or a bare name found anywhere in the hierarchy; omit for the root.")]
            string gameObjectName = null)
        {
            return PrefabAssetEditor.LegacySetProperty(prefabPath, componentType, propertyName,
                value, assetReferencePath, gameObjectName);
        }


        [UnitySkill("prefab_get_info", "Read prefab asset get info. prefabPath is the asset; gameObjectPath is root-relative when accepted.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Query,
            RequiredParams = new[] { "prefabPath" },
            Tags = new[] { "prefab", "asset", "query" }, Outputs = new[] { "prefabPath" },
            ReadOnly = true, Mode = SkillMode.SemiAuto)]
        public static object PrefabGetInfo(string prefabPath, string gameObjectPath = null) =>
            PrefabAssetEditor.Read(prefabPath, root => PrefabAssetEditor.DescribeNode(root, PrefabAssetEditor.Resolve(root, gameObjectPath)));

        [UnitySkill("prefab_get_hierarchy", "List the nodes of the prefab asset at prefabPath, inactive ones included, up to limit (default 200; truncated reports a cut). Each node carries its root-relative path, active state, layer, tag and local transform.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Query,
            RequiredParams = new[] { "prefabPath" },
            Tags = new[] { "prefab", "asset", "query" }, Outputs = new[] { "prefabPath" },
            ReadOnly = true, Mode = SkillMode.SemiAuto)]
        public static object PrefabGetHierarchy(string prefabPath, int limit = 200) =>
            PrefabAssetEditor.Read(prefabPath, root => PrefabAssetEditor.DescribeHierarchy(root, limit));

        [UnitySkill("prefab_list_components", "Read prefab asset list components. prefabPath is the asset; gameObjectPath is root-relative when accepted.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Query,
            RequiredParams = new[] { "prefabPath" },
            Tags = new[] { "prefab", "asset", "query" }, Outputs = new[] { "prefabPath" },
            ReadOnly = true, Mode = SkillMode.SemiAuto)]
        public static object PrefabListComponents(string prefabPath, string gameObjectPath = null, bool includeProperties = false) =>
            PrefabAssetEditor.Read(prefabPath, root => PrefabAssetEditor.DescribeComponents(root, PrefabAssetEditor.Resolve(root, gameObjectPath), includeProperties));

        [UnitySkill("prefab_get_serialized_properties", "Read prefab asset get serialized properties. prefabPath is the asset; gameObjectPath is root-relative when accepted.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Query,
            RequiredParams = new[] { "prefabPath", "componentType" },
            Tags = new[] { "prefab", "asset", "query" }, Outputs = new[] { "prefabPath" },
            ReadOnly = true, Mode = SkillMode.SemiAuto)]
        public static object PrefabGetSerializedProperties(string prefabPath, string componentType, string gameObjectPath = null, int componentIndex = 0, bool includeChildren = true, int limit = 200) =>
            PrefabAssetEditor.Read(prefabPath, root => PrefabAssetEditor.DescribeProperties(root, PrefabAssetEditor.Resolve(root, gameObjectPath), componentType, componentIndex, includeChildren, limit));

        [UnitySkill("prefab_add_component", "Add componentType to the node at gameObjectPath (root-relative; omitted = root) in the prefab asset at prefabPath; only that asset is saved. RequireComponent dependencies come along; a DisallowMultipleComponent duplicate is refused.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Create,
            RequiredParams = new[] { "prefabPath", "componentType" },
            Tags = new[] { "prefab", "asset", "edit" }, Outputs = new[] { "prefabPath", "gameObjectPath" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true, RiskLevel = "medium")]
        public static object PrefabAddComponent(string prefabPath, string componentType, string gameObjectPath = null) =>
            PrefabAssetEditor.Edit(prefabPath, "prefab_add_component", new { componentType, gameObjectPath });

        [UnitySkill("prefab_add_component_batch", "Edit prefab assets in groups. items: JSON array of {prefabPath, componentType, gameObjectPath}. Each asset group is atomic; successful assets remain saved when another group fails.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Create,
            RequiredParams = new[] { "items" },
            Tags = new[] { "prefab", "asset", "batch" }, Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true, RiskLevel = "medium")]
        public static object PrefabAddComponentBatch(
            [SkillParam("JSON array of {prefabPath, componentType, gameObjectPath? (omitted = root)}, each as in prefab_add_component.")]
            string items) =>
            PrefabAssetEditor.Batch("prefab_add_component", items);

        [UnitySkill("prefab_remove_component", "Remove the componentIndex-th (zero-based) componentType from the node at gameObjectPath (root-relative; omitted = root) in the prefab asset at prefabPath; only that asset is saved. Transform and components another one requires are refused.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Delete,
            RequiredParams = new[] { "prefabPath", "componentType" },
            Tags = new[] { "prefab", "asset", "edit" }, Outputs = new[] { "prefabPath", "gameObjectPath" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true, RiskLevel = "high")]
        public static object PrefabRemoveComponent(string prefabPath, string componentType, string gameObjectPath = null, int componentIndex = 0) =>
            PrefabAssetEditor.Edit(prefabPath, "prefab_remove_component", new { componentType, gameObjectPath, componentIndex });

        [UnitySkill("prefab_remove_component_batch", "Edit prefab assets in groups. items: JSON array of {prefabPath, componentType, gameObjectPath, componentIndex}. Each asset group is atomic; successful assets remain saved when another group fails.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Delete,
            RequiredParams = new[] { "items" },
            Tags = new[] { "prefab", "asset", "batch" }, Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true, RiskLevel = "high")]
        public static object PrefabRemoveComponentBatch(
            [SkillParam("JSON array of {prefabPath, componentType, gameObjectPath? (omitted = root), componentIndex? (default 0)}, each as in prefab_remove_component.")]
            string items) =>
            PrefabAssetEditor.Batch("prefab_remove_component", items);

        [UnitySkill("prefab_set_component_enabled", "Set enabled on the componentIndex-th (zero-based) componentType of the node at gameObjectPath (root-relative; omitted = root) in the prefab asset at prefabPath; only that asset is saved.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Modify,
            RequiredParams = new[] { "prefabPath", "componentType", "enabled" },
            Tags = new[] { "prefab", "asset", "edit" }, Outputs = new[] { "prefabPath", "gameObjectPath" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true, RiskLevel = "medium")]
        public static object PrefabSetComponentEnabled(string prefabPath, string componentType, bool enabled, string gameObjectPath = null, int componentIndex = 0) =>
            PrefabAssetEditor.Edit(prefabPath, "prefab_set_component_enabled", new { componentType, enabled, gameObjectPath, componentIndex });

        [UnitySkill("prefab_set_component_enabled_batch", "Edit prefab assets in groups. items: JSON array of {prefabPath, componentType, enabled, gameObjectPath, componentIndex}. Each asset group is atomic; successful assets remain saved when another group fails.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Modify,
            RequiredParams = new[] { "items" },
            Tags = new[] { "prefab", "asset", "batch" }, Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true, RiskLevel = "medium")]
        public static object PrefabSetComponentEnabledBatch(
            [SkillParam("JSON array of {prefabPath, componentType, enabled, gameObjectPath? (omitted = root), componentIndex? (default 0)}, each as in prefab_set_component_enabled.")]
            string items) =>
            PrefabAssetEditor.Batch("prefab_set_component_enabled", items);

        [UnitySkill("prefab_set_serialized_property", "Write propertyPath on the componentIndex-th componentType of the node at gameObjectPath (root-relative; omitted = root) in the prefab asset at prefabPath; only that asset is saved. Plain fields take value; an object reference takes one of assetPath, referenceGameObjectPath (with referenceComponentType/referenceComponentIndex for a component in this prefab) or clearReference.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Modify,
            RequiredParams = new[] { "prefabPath", "componentType", "propertyPath" },
            Tags = new[] { "prefab", "asset", "edit" }, Outputs = new[] { "prefabPath", "gameObjectPath" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true, RiskLevel = "medium")]
        public static object PrefabSetSerializedProperty(string prefabPath, string componentType, string propertyPath, string gameObjectPath = null, int componentIndex = 0, string value = null, string assetPath = null, string referenceGameObjectPath = null, string referenceComponentType = null, int referenceComponentIndex = 0, bool clearReference = false) =>
            PrefabAssetEditor.Edit(prefabPath, "prefab_set_serialized_property", new { componentType, propertyPath, gameObjectPath, componentIndex, value, assetPath, referenceGameObjectPath, referenceComponentType, referenceComponentIndex, clearReference });

        [UnitySkill("prefab_set_serialized_property_batch", "Edit prefab assets in groups. items: JSON array of {prefabPath, componentType, propertyPath, gameObjectPath, componentIndex, value, assetPath, referenceGameObjectPath, referenceComponentType, referenceComponentIndex, clearReference}. Each asset group is atomic; successful assets remain saved when another group fails.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Modify,
            RequiredParams = new[] { "items" },
            Tags = new[] { "prefab", "asset", "batch" }, Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true, RiskLevel = "medium")]
        public static object PrefabSetSerializedPropertyBatch(
            [SkillParam("JSON array of {prefabPath, componentType, propertyPath, gameObjectPath? (omitted = root), componentIndex? (default 0), value?, assetPath?, referenceGameObjectPath?, referenceComponentType?, referenceComponentIndex? (default 0), clearReference? (default false)}, each as in prefab_set_serialized_property: value for a plain field, exactly one of assetPath, referenceGameObjectPath or clearReference:true for an object reference.")]
            string items) =>
            PrefabAssetEditor.Batch("prefab_set_serialized_property", items);

        [UnitySkill("prefab_create_gameobject", "Create a node called name under parentPath (root-relative; omitted = root) in the prefab asset at prefabPath, with a RectTransform when rectTransform is true; only that asset is saved. Sibling names must stay unique.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Create,
            RequiredParams = new[] { "prefabPath", "name" },
            Tags = new[] { "prefab", "asset", "edit" }, Outputs = new[] { "prefabPath", "gameObjectPath" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true, RiskLevel = "medium")]
        public static object PrefabCreateGameObject(string prefabPath, string name, string parentPath = null, bool rectTransform = false) =>
            PrefabAssetEditor.Edit(prefabPath, "prefab_create_gameobject", new { name, parentPath, rectTransform });

        [UnitySkill("prefab_create_gameobject_batch", "Edit prefab assets in groups. items: JSON array of {prefabPath, name, parentPath, rectTransform}. Each asset group is atomic; successful assets remain saved when another group fails.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Create,
            RequiredParams = new[] { "items" },
            Tags = new[] { "prefab", "asset", "batch" }, Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true, RiskLevel = "medium")]
        public static object PrefabCreateGameObjectBatch(
            [SkillParam("JSON array of {prefabPath, name, parentPath? (omitted = root), rectTransform? (default false)}, each as in prefab_create_gameobject.")]
            string items) =>
            PrefabAssetEditor.Batch("prefab_create_gameobject", items);

        [UnitySkill("prefab_delete_gameobject", "Delete the node at gameObjectPath (root-relative) and its subtree from the prefab asset at prefabPath; only that asset is saved. The root and nodes inherited from a variant base or nested prefab are refused.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Delete,
            RequiredParams = new[] { "prefabPath", "gameObjectPath" },
            Tags = new[] { "prefab", "asset", "edit" }, Outputs = new[] { "prefabPath", "gameObjectPath" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true, RiskLevel = "high")]
        public static object PrefabDeleteGameObject(string prefabPath, string gameObjectPath) =>
            PrefabAssetEditor.Edit(prefabPath, "prefab_delete_gameobject", new { gameObjectPath });

        [UnitySkill("prefab_delete_gameobject_batch", "Edit prefab assets in groups. items: JSON array of {prefabPath, gameObjectPath}. Each asset group is atomic; successful assets remain saved when another group fails.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Delete,
            RequiredParams = new[] { "items" },
            Tags = new[] { "prefab", "asset", "batch" }, Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true, RiskLevel = "high")]
        public static object PrefabDeleteGameObjectBatch(
            [SkillParam("JSON array of {prefabPath, gameObjectPath}, each as in prefab_delete_gameobject.")]
            string items) =>
            PrefabAssetEditor.Batch("prefab_delete_gameobject", items);

        [UnitySkill("prefab_duplicate_gameobject", "Duplicate the node at gameObjectPath (root-relative) and its subtree as a sibling called newName (default <name>_Copy) in the prefab asset at prefabPath; only that asset is saved. The root cannot be duplicated.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Create,
            RequiredParams = new[] { "prefabPath", "gameObjectPath" },
            Tags = new[] { "prefab", "asset", "edit" }, Outputs = new[] { "prefabPath", "gameObjectPath" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true, RiskLevel = "medium")]
        public static object PrefabDuplicateGameObject(string prefabPath, string gameObjectPath, string newName = null) =>
            PrefabAssetEditor.Edit(prefabPath, "prefab_duplicate_gameobject", new { gameObjectPath, newName });

        [UnitySkill("prefab_duplicate_gameobject_batch", "Edit prefab assets in groups. items: JSON array of {prefabPath, gameObjectPath, newName}. Each asset group is atomic; successful assets remain saved when another group fails.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Create,
            RequiredParams = new[] { "items" },
            Tags = new[] { "prefab", "asset", "batch" }, Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true, RiskLevel = "medium")]
        public static object PrefabDuplicateGameObjectBatch(
            [SkillParam("JSON array of {prefabPath, gameObjectPath, newName? (default <name>_Copy)}, each as in prefab_duplicate_gameobject.")]
            string items) =>
            PrefabAssetEditor.Batch("prefab_duplicate_gameobject", items);

        [UnitySkill("prefab_rename_gameobject", "Rename the node at gameObjectPath (root-relative; omitted = root) to newName in the prefab asset at prefabPath; only that asset is saved. Sibling names must stay unique.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Modify,
            RequiredParams = new[] { "prefabPath", "newName" },
            Tags = new[] { "prefab", "asset", "edit" }, Outputs = new[] { "prefabPath", "gameObjectPath" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true, RiskLevel = "medium")]
        public static object PrefabRenameGameObject(string prefabPath, string newName, string gameObjectPath = null) =>
            PrefabAssetEditor.Edit(prefabPath, "prefab_rename_gameobject", new { newName, gameObjectPath });

        [UnitySkill("prefab_rename_gameobject_batch", "Edit prefab assets in groups. items: JSON array of {prefabPath, newName, gameObjectPath}. Each asset group is atomic; successful assets remain saved when another group fails.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Modify,
            RequiredParams = new[] { "items" },
            Tags = new[] { "prefab", "asset", "batch" }, Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true, RiskLevel = "medium")]
        public static object PrefabRenameGameObjectBatch(
            [SkillParam("JSON array of {prefabPath, newName, gameObjectPath? (omitted = root)}, each as in prefab_rename_gameobject.")]
            string items) =>
            PrefabAssetEditor.Batch("prefab_rename_gameobject", items);

        [UnitySkill("prefab_set_transform", "Set posX/Y/Z, rotX/Y/Z, scaleX/Y/Z, and on a RectTransform anchoredPos, anchorMin, anchorMax, pivot and sizeDelta X/Y, of the node at gameObjectPath (root-relative; omitted = root) in the prefab asset at prefabPath; only that asset is saved. Transform axes are local and omitted axes are kept.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Modify,
            RequiredParams = new[] { "prefabPath" },
            Tags = new[] { "prefab", "asset", "edit" }, Outputs = new[] { "prefabPath", "gameObjectPath" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true, RiskLevel = "medium")]
        public static object PrefabSetTransform(string prefabPath, string gameObjectPath = null, float? posX = null, float? posY = null, float? posZ = null, float? rotX = null, float? rotY = null, float? rotZ = null, float? scaleX = null, float? scaleY = null, float? scaleZ = null, float? anchoredPosX = null, float? anchoredPosY = null, float? anchorMinX = null, float? anchorMinY = null, float? anchorMaxX = null, float? anchorMaxY = null, float? pivotX = null, float? pivotY = null, float? sizeDeltaX = null, float? sizeDeltaY = null) =>
            PrefabAssetEditor.Edit(prefabPath, "prefab_set_transform", new { gameObjectPath, posX, posY, posZ, rotX, rotY, rotZ, scaleX, scaleY, scaleZ, anchoredPosX, anchoredPosY, anchorMinX, anchorMinY, anchorMaxX, anchorMaxY, pivotX, pivotY, sizeDeltaX, sizeDeltaY });

        [UnitySkill("prefab_set_transform_batch", "Edit prefab assets in groups. items: JSON array of {prefabPath, gameObjectPath, posX, posY, posZ, rotX, rotY, rotZ, scaleX, scaleY, scaleZ, anchoredPosX, anchoredPosY, anchorMinX, anchorMinY, anchorMaxX, anchorMaxY, pivotX, pivotY, sizeDeltaX, sizeDeltaY}. Transform axes are local and omitted axes are kept. Each asset group is atomic; successful assets remain saved when another group fails.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Modify,
            RequiredParams = new[] { "items" },
            Tags = new[] { "prefab", "asset", "batch" }, Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true, RiskLevel = "medium")]
        public static object PrefabSetTransformBatch(
            [SkillParam("JSON array of {prefabPath, gameObjectPath? (omitted = root), posX/Y/Z?, rotX/Y/Z?, scaleX/Y/Z?, anchoredPosX/Y?, anchorMinX/Y?, anchorMaxX/Y?, pivotX/Y?, sizeDeltaX/Y?}, each as in prefab_set_transform: local axes, omitted axes keep their value, and the anchoredPos/anchorMin/anchorMax/pivot/sizeDelta fields need a RectTransform node.")]
            string items) =>
            PrefabAssetEditor.Batch("prefab_set_transform", items);

        [UnitySkill("prefab_set_parent", "Move the node at gameObjectPath under parentPath (both root-relative; omitted parentPath = root) in the prefab asset at prefabPath, keeping its world pose when worldPositionStays is true; only that asset is saved. The root, inherited nodes and cycles are refused.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Modify,
            RequiredParams = new[] { "prefabPath", "gameObjectPath" },
            Tags = new[] { "prefab", "asset", "edit" }, Outputs = new[] { "prefabPath", "gameObjectPath" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true, RiskLevel = "medium")]
        public static object PrefabSetParent(string prefabPath, string gameObjectPath, string parentPath = null, bool worldPositionStays = false) =>
            PrefabAssetEditor.Edit(prefabPath, "prefab_set_parent", new { gameObjectPath, parentPath, worldPositionStays });

        [UnitySkill("prefab_set_parent_batch", "Edit prefab assets in groups. items: JSON array of {prefabPath, gameObjectPath, parentPath, worldPositionStays}. Each asset group is atomic; successful assets remain saved when another group fails.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Modify,
            RequiredParams = new[] { "items" },
            Tags = new[] { "prefab", "asset", "batch" }, Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true, RiskLevel = "medium")]
        public static object PrefabSetParentBatch(
            [SkillParam("JSON array of {prefabPath, gameObjectPath, parentPath? (omitted = root), worldPositionStays? (default false)}, each as in prefab_set_parent.")]
            string items) =>
            PrefabAssetEditor.Batch("prefab_set_parent", items);

        [UnitySkill("prefab_set_active", "Set active (activeSelf) on the node at gameObjectPath (root-relative; omitted = root) in the prefab asset at prefabPath; only that asset is saved.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Modify,
            RequiredParams = new[] { "prefabPath", "active" },
            Tags = new[] { "prefab", "asset", "edit" }, Outputs = new[] { "prefabPath", "gameObjectPath" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true, RiskLevel = "medium")]
        public static object PrefabSetActive(string prefabPath, bool active, string gameObjectPath = null) =>
            PrefabAssetEditor.Edit(prefabPath, "prefab_set_active", new { active, gameObjectPath });

        [UnitySkill("prefab_set_active_batch", "Edit prefab assets in groups. items: JSON array of {prefabPath, active, gameObjectPath}. Each asset group is atomic; successful assets remain saved when another group fails.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Modify,
            RequiredParams = new[] { "items" },
            Tags = new[] { "prefab", "asset", "batch" }, Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true, RiskLevel = "medium")]
        public static object PrefabSetActiveBatch(
            [SkillParam("JSON array of {prefabPath, active, gameObjectPath? (omitted = root)}, each as in prefab_set_active.")]
            string items) =>
            PrefabAssetEditor.Batch("prefab_set_active", items);

        [UnitySkill("prefab_set_sibling_index", "Set the sibling index of the node at gameObjectPath (root-relative) to index in the prefab asset at prefabPath; only that asset is saved. The root and inherited nodes are refused.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Modify,
            RequiredParams = new[] { "prefabPath", "index", "gameObjectPath" },
            Tags = new[] { "prefab", "asset", "edit" }, Outputs = new[] { "prefabPath", "gameObjectPath" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true, RiskLevel = "medium")]
        public static object PrefabSetSiblingIndex(string prefabPath, int index, string gameObjectPath) =>
            PrefabAssetEditor.Edit(prefabPath, "prefab_set_sibling_index", new { index, gameObjectPath });

        [UnitySkill("prefab_set_sibling_index_batch", "Edit prefab assets in groups. items: JSON array of {prefabPath, index, gameObjectPath}. Each asset group is atomic; successful assets remain saved when another group fails.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Modify,
            RequiredParams = new[] { "items" },
            Tags = new[] { "prefab", "asset", "batch" }, Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true, RiskLevel = "medium")]
        public static object PrefabSetSiblingIndexBatch(
            [SkillParam("JSON array of {prefabPath, gameObjectPath, index}, each as in prefab_set_sibling_index.")]
            string items) =>
            PrefabAssetEditor.Batch("prefab_set_sibling_index", items);

        [UnitySkill("prefab_edit_batch", "Atomically edit one prefab asset. steps: JSON array of {skill, args}; only prefab asset edit operations are allowed, maximum 50 steps. Earlier-created paths are available to later steps.",
            Category = SkillCategory.Prefab, Operation = SkillOperation.Modify | SkillOperation.Delete,
            RequiredParams = new[] { "prefabPath", "steps" },
            Tags = new[] { "prefab", "asset", "batch", "transaction" }, Outputs = new[] { "prefabPath", "results" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true, RiskLevel = "high")]
        public static object PrefabEditBatch(string prefabPath, string steps) =>
            PrefabAssetEditor.EditSteps(prefabPath, steps);

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
        /// Writes a SerializedProperty's value from a string, returning true on success.
        ///
        /// <para><paramref name="typeSupported"/> distinguishes two kinds of failure — a single
        /// "Failed to set value" message used to conflate them: false means there's no branch here
        /// at all for this <see cref="SerializedPropertyType"/> (nothing the caller writes in
        /// <c>value</c> could work); true means the type is supported but the given text couldn't
        /// be parsed, with the reason in <paramref name="error"/> when one is known. This switch is the
        /// sole source of truth for which types are supported — only the default branch clears this flag.</para>
        /// </summary>
        internal static bool SetSerializedPropertyValue(SerializedProperty prop, string value, out bool typeSupported,
            out string error, out string warning)
        {
            typeSupported = true;
            error = null;
            warning = null;
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
                    if (SerializedPropertySkillUtility.IsTypeDefaultText(value)) { prop.boolValue = false; return true; }
                    if (SkillParamUtil.TryParseBoolText(value, out var boolVal)) { prop.boolValue = boolVal; return true; }
                    error = "valid values are true, false, 1, 0, yes, no, on, off";
                    return false;

                case SerializedPropertyType.String:
                    prop.stringValue = value;
                    return true;

                case SerializedPropertyType.Enum:
                    // Same rules as the serialized-property writers: names, display names, "A,B" flags,
                    // and numbers as member indexes (warned when index and value differ).
                    return SerializedPropertySkillUtility.TrySetEnum(prop, value, out error, out warning);

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

        #endregion
    }
}

// Producer:Betsy
