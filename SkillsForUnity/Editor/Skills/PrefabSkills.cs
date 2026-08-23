using UnityEngine;
using UnityEditor;
using System.IO;
using System.Linq;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// Prefab 管理技能：创建、编辑、保存。
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
            // 缓存已加载的 prefab，避免重复走 AssetDatabase
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
                // GetPropertyModifications 会返回 Unity 写进每个新建 prefab 实例修改列表的记账条目，
                // 无论其值是否真的与源不同——实地核查确认：即使是完全未动过的实例，
                // m_LocalPosition.x/y/z、m_LocalRotation.w/x/y/z、m_LocalEulerAnglesHint.x/y/z
                // 和 m_Name 都在列表里，恒定给出约 11 个"override"，hasOverrides 永远为 true。
                //
                // PropertyModification.target 并非场景中的活实例对象，而是指向*源 prefab 资产*的引用
                //（实测确认：其 instance ID 与资产加载出的对象一致，而不是场景实例那种负数/仅本会话有效的
                // ID；且 PrefabUtility.GetCorrespondingObjectFromSource(o.target) 恒返回 null，
                // 因为源自身没有源）。因此要比较"实例值"与"源值"，唯一办法是先把每个源对象映射回
                // 本实例中的活对象——正好是 GetCorrespondingObjectFromSource 所支持方向的反向——
                // 做法是遍历实例层级一遍，按 GetCorrespondingObjectFromSource(live) 建索引。
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

                    // 实例自身的名字无条件排除在 override 判定之外，与它是否不同于源名无关——
                    // 对照 PrefabUtility.HasPrefabInstanceAnyOverrides 确认过：即使给实例改了自定义名，
                    // 它依然为 false。若此处只按值相等过滤，场景里几乎每个改过名的实例都会被误报为有 override。
                    if (o.propertyPath == "m_Name") continue;

                    // 找不到该源对象在活实例中的对应物（例如它属于本次遍历未触及的嵌套 prefab 结构）——
                    // 无法证明它只是幽灵默认值，因此保留，以免静默丢弃一个真实 override。
                    if (!liveBySource.TryGetValue(o.target, out var liveInstance))
                    {
                        propOverrides.Add(new { target = o.target.name, property = o.propertyPath, value = o.value });
                        continue;
                    }

                    var instProp = new SerializedObject(liveInstance).FindProperty(o.propertyPath);
                    var srcProp = new SerializedObject(o.target).FindProperty(o.propertyPath);
                    if (instProp != null && srcProp != null && SerializedProperty.DataEquals(instProp, srcProp))
                        continue; // 实例值与源资产一致，属幽灵记账条目而非真实 override

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
                // 沿用上面实地比对得出的计数，不用 PrefabUtility.HasPrefabInstanceAnyOverrides——
                // 那个汇总值读的是 Unity 缓存的修改列表，对于刚在内存里改过、尚未刷进该缓存的属性
                // 可能是过期的（例如调用方绕过 SetDirty/RecordPrefabInstancePropertyModifications
                // 直接改了 Transform 字段）。由 propOverrides.Count 推导 hasOverrides，
                // 才能让同一份响应里的这两个字段自洽。
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
            // 不能写 "prefabAsset"：全代码库里只此一处出现——本 skill 不接受该参数（资产由 prefabPath 传入），
            // 也没有任何 skill 输出它，于是这个记号既约束不到什么也串不起链路，照字面理解它的 agent
            // 只会拿到 UNKNOWN_PARAM。prefabPath 同时也是 prefab_create 的返回值，
            // 改正后的记号还顺带把两者接进 Outputs→RequiresInput 链。
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

            // 在 prefab 内定位目标 GameObject（根，或按名字找子节点）
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

            // 按属性类型分派写入
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
                    // 文本格式非法时转换器是抛异常而不是返回 null（如给 Vector3 传 "1,2"、
                    // 只给两个分量的 Quaternion、JSON 对象形式里出现非数字键）。不捕获就会表现为
                    // 未分类的 SKILL_ERROR + abort，且只带原始解析器消息。此调用内不会有别的异常来源，
                    // 因此这里判定为"值有问题"而非吞掉一个 bug。
                    return new { error = $"Invalid value '{value}' for property '{propertyName}' (type: {prop.propertyType}): {ex.Message}" };
                }

                if (!applied)
                {
                    // "Failed to set value" 会被读成"你的值不对"——但对不受支持的属性类型来说，
                    // 调用方写什么都不可能成功，只有把这点说清楚才能阻止它换个格式重试同一次调用。
                    // 两条消息都以自己的判定词开头（"Invalid" / "Unsupported"），
                    // 以便 SkillErrorClassifier 的首词判定规则给出 SEMANTIC_INVALID + fix_and_retry，
                    // 而不是旧文案 "Failed to set value …" 换来的未分类 SKILL_ERROR + abort。
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
        /// 找出 prefab 实例与其源资产之间真正的属性差异（值确实不同），区别于
        /// PrefabUtility.GetPropertyModifications 对每个新实例都会附带的幽灵记账条目
        /// （m_LocalPosition/m_LocalRotation/m_LocalEulerAnglesHint/m_Name）。
        ///
        /// <para>PropertyModification.target 指向的是*源* prefab 资产对象而非活实例
        /// （经 instance ID 实测确认），因此不能对它直接调 GetCorrespondingObjectFromSource——
        /// 那样恒返回 null。此处遍历活实例层级一遍，按 GetCorrespondingObjectFromSource(live) -&gt; live
        /// 建索引（这才是该 API 支持的方向），再反查每条修改对应的活对象。
        /// 检测逻辑与 PrefabGetOverrides 相同；此处保留一份独立副本而不抽取共用，
        /// 以免动到那个已验证过的方法。</para>
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
                if (o.propertyPath == "m_Name") continue; // 按 PrefabUtility.HasPrefabInstanceAnyOverrides 的行为，无条件排除在 override 判定外
                if (!liveBySource.TryGetValue(o.target, out var liveInstance)) continue;

                var instProp = new SerializedObject(liveInstance).FindProperty(o.propertyPath);
                var srcProp = new SerializedObject(o.target).FindProperty(o.propertyPath);
                if (instProp == null || srcProp == null) continue;
                if (SerializedProperty.DataEquals(instProp, srcProp)) continue; // 与源一致，属幽灵记账条目而非真实 override

                result.Add((liveInstance, o.target, o.propertyPath));
            }
            return result;
        }

        /// <summary>
        /// 把每个真实 override 属性的活实例值写到对应的 prefab 源资产对象上，并保存资产。
        ///
        /// <para>在 PrefabUtility.ApplyPrefabInstance 之前调用。实测（直接查看磁盘上的原始 YAML）确认：
        /// 单靠那个 API 对 Transform override 会让源资产完全不变，即便先分别试过
        /// EditorUtility.SetDirty、RecordPrefabInstancePropertyModifications、Undo.RecordObject
        /// 和 SerializedObject.ApplyModifiedProperties 也一样——在这个无头、Inspector 不重绘的环境里，
        /// 没有一种能让 Unity 原生的 prefab override 比对识别出差异。因此不依赖那套比对，
        /// 直接做值拷贝并显式调用 AssetDatabase.SaveAssets；本代码库在"文件变更触发域重载"上
        /// 已经确认过同一模式：Unity 平时自动做的后台工作，在没有窗口聚焦/空闲事件时不会发生。</para>
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
                catch { continue; /* 并非所有属性类型都支持 boxedValue */ }
                srcSO.ApplyModifiedProperties();
                EditorUtility.SetDirty(source);
                touchedSources.Add(source);
            }
            if (touchedSources.Count > 0)
                AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// 把每个真实 override 属性的 prefab 源资产值写回活实例，与 PushInstanceOverridesToSource
        /// 方向相反。
        ///
        /// <para>在 PrefabUtility.RevertPrefabInstance 之前调用，原因相同：那个 API 依赖与 Apply
        /// 同一套原生 override 比对缓存，而该缓存在本环境下不会因脚本驱动的改动而填充，
        /// 否则 revert 会静默地放着实例上已偏离的 Transform 值不动。</para>
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
                catch { continue; /* 并非所有属性类型都支持 boxedValue */ }
                liveSO.ApplyModifiedProperties();
                EditorUtility.SetDirty(live);
            }
        }

        /// <summary>
        /// 按名字查找 SerializedProperty，并按 Unity 命名约定回退尝试（m_PropertyName、_propertyName）。
        /// </summary>
        private static SerializedProperty FindSerializedProperty(SerializedObject so, string propertyName)
        {
            var prop = so.FindProperty(propertyName);
            if (prop != null) return prop;

            // Unity 约定：m_PropertyName
            var mName = "m_" + char.ToUpper(propertyName[0]) + propertyName.Substring(1);
            prop = so.FindProperty(mName);
            if (prop != null) return prop;

            // 下划线前缀：_propertyName
            prop = so.FindProperty("_" + propertyName);
            if (prop != null) return prop;

            // m_ 前缀 + 首字母保持小写
            var mLower = "m_" + propertyName;
            prop = so.FindProperty(mLower);
            if (prop != null) return prop;

            return null;
        }

        /// <summary>
        /// 从字符串写入 SerializedProperty 的值，成功返回 true。
        ///
        /// <para><paramref name="typeSupported"/> 区分两种失败——单一的 "Failed to set value"
        /// 消息曾把它们混为一谈：false 表示此处根本没有该 <see cref="SerializedPropertyType"/>
        /// 的分支（调用方在 <c>value</c> 里写什么都不行）；true 表示类型支持，但给的文本解析不出来。
        /// 哪些类型受支持以这个 switch 为唯一事实来源——只有 default 分支会清掉该标志。</para>
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
                    // 先按名字匹配，再退回按索引
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

                // m_LocalRotation 是 prefab 上被写得最多的属性，而它是 Quaternion；此处缺了这一分支
                // 就会让所有旋转写入落到 default，返回 "Failed to set value ... (type: Quaternion)"。
                // ConvertValue 接受 3 分量（欧拉角，度）或 4 分量（原始 x,y,z,w），与上面的 Vector 分支一致。
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
        /// 列出顶层序列化属性，用于错误诊断。
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
