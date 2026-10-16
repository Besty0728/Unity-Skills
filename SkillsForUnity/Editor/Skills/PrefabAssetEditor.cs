using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnitySkills.Internal
{
    /// <summary>Isolated prefab contents, with one save per successful asset transaction.</summary>
    internal static class PrefabAssetEditor
    {
        private sealed class EditFailure : Exception
        {
            internal readonly SkillErrorCode Code;
            internal readonly object Details;
            internal EditFailure(string message, SkillErrorCode code = SkillErrorCode.SemanticInvalid, object details = null)
                : base(message) { Code = code; Details = details; }
        }

        private sealed class Receipt
        {
            internal string Skill;
            internal GameObject Target;
            internal Component Component;
            internal string Property;
            internal string Requested;
            internal string Warning;
            internal string OriginalPath;
            internal bool Deleted;
            internal bool RemovedComponent;
            internal Type MatchType;
            internal Func<GameObject, object> ReadBack;
        }

        internal sealed class AssetGroup
        {
            public string prefabPath;
            public JArray steps;
        }

        internal static Func<GameObject, string, bool> SaveOverrideForTests;

        internal static readonly Dictionary<string, string[]> OperationFields = new Dictionary<string, string[]>(StringComparer.Ordinal);

        static PrefabAssetEditor()
        {
            // Filled below with the public wrapper contracts, without routing another skill invocation.
            InitializeFields();
        }

        private static JObject Json(object value) => value as JObject ?? JObject.FromObject(value, JsonSerializer.Create(SkillsCommon.JsonSettings));
        private static object Error(Exception exception, bool rolledBack = false)
        {
            var failure = exception as EditFailure;
            return JObject.Parse(SkillErrorResponse.Build(failure?.Code ?? SkillErrorCode.SkillError,
                exception.Message, details: failure?.Details,
                suggestedFixes: new[] { new SuggestedFix { action = "fix_param", reason = "Inspect the prefab hierarchy and correct the failed operation before retrying." } },
                retryStrategy: SkillErrorResponse.RetryFixAndRetry,
                extra: new Dictionary<string, object> { ["rolledBack"] = rolledBack }));
        }

        internal static GameObject RequireAsset(string path, bool writing = false)
        {
            var required = Validate.Required(path, "prefabPath");
            if (required != null) throw new EditFailure("prefabPath is required", SkillErrorCode.MissingParam);
            if (Validate.SafePath(path, "prefabPath") != null || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                throw new EditFailure("prefabPath must be a safe .prefab asset path");
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null || PrefabUtility.GetPrefabAssetType(asset) == PrefabAssetType.Model)
                throw new EditFailure($"Prefab asset not found or not editable: {path}", SkillErrorCode.TargetNotFound);
            if (writing && (!WorkflowFileStore.TryGetSafeAssetFullPath(path, out var fullPath) ||
                (File.GetAttributes(fullPath) & FileAttributes.ReadOnly) != 0 || !AssetDatabase.IsOpenForEdit(path)))
                throw new EditFailure($"Prefab asset is not writable: {path}");
            return asset;
        }

        internal static object Read(string prefabPath, Func<GameObject, object> reader)
        {
            try
            {
                var response = Json(reader(RequireAsset(prefabPath)));
                response["success"] = true;
                response["prefabPath"] = prefabPath;
                return response;
            }
            catch (Exception ex) { return Error(ex); }
        }

        internal static GameObject Resolve(GameObject root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;
            if (path.StartsWith("/", StringComparison.Ordinal) || path.EndsWith("/", StringComparison.Ordinal))
                throw new EditFailure("gameObjectPath must be a root-relative path without leading or trailing slashes");
            var current = root.transform;
            foreach (var segment in path.Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == "..") throw new EditFailure("Invalid hierarchy path");
                var matches = new List<Transform>();
                for (var i = 0; i < current.childCount; i++)
                    if (current.GetChild(i).name == segment) matches.Add(current.GetChild(i));
                if (matches.Count == 0) throw new EditFailure($"Prefab node not found: {path}", SkillErrorCode.TargetNotFound);
                if (matches.Count > 1) throw new EditFailure($"Ambiguous prefab node path: {path}", details: new
                {
                    candidates = matches.Select(t => new { gameObjectPath = PathOf(root, t.gameObject), siblingIndex = t.GetSiblingIndex() }).ToArray()
                });
                current = matches[0];
            }
            return current.gameObject;
        }

        internal static string PathOf(GameObject root, GameObject target)
        {
            if (target == root) return "";
            var parts = new List<string>();
            for (var node = target.transform; node != root.transform; node = node.parent)
            {
                if (node == null) throw new EditFailure("Object is outside the requested prefab");
                parts.Add(node.name);
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static Type ComponentType(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new EditFailure("componentType is required", SkillErrorCode.MissingParam);
            var type = ComponentSkills.FindComponentType(name);
            if (type == null || !typeof(Component).IsAssignableFrom(type) || type.IsAbstract || type.ContainsGenericParameters)
                throw new EditFailure($"Component type not found or not concrete: {name}", SkillErrorCode.TargetNotFound);
            return type;
        }

        private static Component FindComponent(GameObject target, string typeName, int index)
        {
            var matches = target.GetComponents(ComponentType(typeName));
            if (index < 0 || index >= matches.Length) throw new EditFailure($"Component index {index} not found for {typeName} on {target.name}", SkillErrorCode.TargetNotFound);
            return matches[index];
        }

        internal static object DescribeNode(GameObject root, GameObject target)
        {
            var t = target.transform;
            var result = new JObject
            {
                ["gameObject"] = target.name, ["gameObjectPath"] = PathOf(root, target),
                ["parentPath"] = target == root ? JValue.CreateNull() : new JValue(PathOf(root, t.parent.gameObject)),
                ["activeSelf"] = target.activeSelf, ["layer"] = target.layer, ["tag"] = target.tag,
                ["siblingIndex"] = t.GetSiblingIndex(), ["childCount"] = t.childCount,
                ["localPosition"] = Vector(t.localPosition), ["localEulerAngles"] = Vector(t.localEulerAngles),
                ["localScale"] = Vector(t.localScale), ["prefabAssetType"] = PrefabUtility.GetPrefabAssetType(root).ToString()
            };
            if (t is RectTransform rect)
            {
                result["anchoredPosition"] = Vector(rect.anchoredPosition);
                result["anchorMin"] = Vector(rect.anchorMin); result["anchorMax"] = Vector(rect.anchorMax);
                result["pivot"] = Vector(rect.pivot); result["sizeDelta"] = Vector(rect.sizeDelta);
            }
            return result;
        }

        private static JObject Vector(Vector3 v) => new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };
        private static JObject Vector(Vector2 v) => new JObject { ["x"] = v.x, ["y"] = v.y };

        internal static object DescribeHierarchy(GameObject root, int limit)
        {
            if (limit < 1) throw new EditFailure("limit must be positive");
            var transforms = root.GetComponentsInChildren<Transform>(true);
            return new { total = transforms.Length, truncated = transforms.Length > limit,
                nodes = transforms.Take(limit).Select(t => DescribeNode(root, t.gameObject)).ToArray() };
        }

        internal static object DescribeComponents(GameObject root, GameObject target, bool includeProperties)
        {
            var indexes = new Dictionary<Type, int>();
            var list = new List<object>();
            foreach (var component in target.GetComponents<Component>())
            {
                if (component == null) { list.Add(new { missingScript = true }); continue; }
                var type = component.GetType();
                indexes.TryGetValue(type, out var index);
                indexes[type] = index + 1;
                var item = new JObject { ["componentType"] = type.FullName, ["componentIndex"] = index };
                var enabled = new SerializedObject(component).FindProperty("m_Enabled");
                if (enabled != null && enabled.propertyType == SerializedPropertyType.Boolean) item["enabled"] = enabled.boolValue;
                if (includeProperties) item["properties"] = JArray.FromObject(SerializedPropertySkillUtility.ListProperties(component));
                list.Add(item);
            }
            return new { gameObjectPath = PathOf(root, target), components = list };
        }

        internal static object DescribeProperties(GameObject root, GameObject target, string typeName, int index, bool children, int limit)
        {
            var component = FindComponent(target, typeName, index);
            return new { gameObjectPath = PathOf(root, target), componentType = component.GetType().FullName, componentIndex = index,
                properties = SerializedPropertySkillUtility.ListProperties(component, children, limit) };
        }

        internal static object Edit(string prefabPath, string skill, object args) => Run(prefabPath,
            new JArray(new JObject { ["skill"] = skill, ["args"] = Json(args) }), false);

        internal static object EditSteps(string prefabPath, string steps)
        {
            if (Validate.RequiredJsonArray(steps, "steps") is object error) return JsonError(error);
            try { return Run(prefabPath, JArray.Parse(steps), true); }
            catch (JsonException ex) { return Error(new EditFailure(ex.Message, SkillErrorCode.InvalidJson)); }
        }

        private static object JsonError(object error) => error is string text ? JObject.Parse(text) : error;

        internal static object Batch(string skill, string items)
        {
            if (Validate.RequiredJsonArray(items, "items") is object error) return JsonError(error);
            try
            {
                JArray array;
                try { array = JArray.Parse(items); }
                catch (JsonException ex) { throw new EditFailure(ex.Message, SkillErrorCode.InvalidJson); }
                if (array.Count > 50) throw new EditFailure("A prefab batch accepts at most 50 items");
                var groups = new List<AssetGroup>();
                foreach (var token in array)
                {
                    if (!(token is JObject item)) throw new EditFailure("Each batch item must be an object", SkillErrorCode.InvalidJson);
                    var path = Required(item, "prefabPath");
                    var group = groups.FirstOrDefault(g => string.Equals(g.prefabPath, path, StringComparison.Ordinal));
                    if (group == null) { group = new AssetGroup { prefabPath = path, steps = new JArray() }; groups.Add(group); }
                    var args = (JObject)item.DeepClone(); args.Remove("prefabPath");
                    group.steps.Add(new JObject { ["skill"] = skill, ["args"] = args });
                }
                var result = Json(BatchExecutor.Execute<AssetGroup>(JsonConvert.SerializeObject(groups, SkillsCommon.JsonSettings),
                    g => Run(g.prefabPath, g.steps, true), g => g.prefabPath));
                // Partial asset success is intentional. Do not expose a top-level business error:
                // the router would undo successful assets and discard their workflow snapshots.
                result.Remove("error"); result.Remove("errorCode");
                result["completed"] = true;
                result["totalItems"] = array.Count;
                result["assetCount"] = groups.Count;
                result["countsUnit"] = "assets";
                return result;
            }
            catch (Exception ex) { return Error(ex); }
        }

        private static object Run(string prefabPath, JArray steps, bool batch, bool legacy = false)
        {
            GameObject contents = null;
            byte[] before = null;
            byte[] metaBefore = null;
            var saved = false;
            var workflowCount = WorkflowManager.CurrentTask?.snapshots?.Count ?? 0;
            try
            {
                var asset = RequireAsset(prefabPath, true);
                ValidateSteps(steps);
                before = File.ReadAllBytes(prefabPath);
                metaBefore = File.Exists(prefabPath + ".meta") ? File.ReadAllBytes(prefabPath + ".meta") : null;
                contents = PrefabUtility.LoadPrefabContents(prefabPath);
                var receipts = new List<Receipt>();
                foreach (JObject step in steps) receipts.Add(Apply(contents, step.Value<string>("skill"), (JObject)step["args"], legacy));
                // Freeze final paths only after every step, so a rename/reparent later in the group is reflected.
                foreach (var receipt in receipts) PrepareReadBack(contents, receipt);
                WorkflowManager.SnapshotObject(asset);
                saved = true;
                bool success;
                if (SaveOverrideForTests != null) success = SaveOverrideForTests(contents, prefabPath);
                else PrefabUtility.SaveAsPrefabAsset(contents, prefabPath, out success);
                if (!success) throw new EditFailure($"Unity refused to save prefab: {prefabPath}", SkillErrorCode.SkillError);
                PrefabUtility.UnloadPrefabContents(contents); contents = null;
                AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceUpdate);
                var persisted = RequireAsset(prefabPath);
                var results = receipts.Select(r => Json(r.ReadBack(persisted))).ToArray();
                PrefabAssetUndo.Record(prefabPath, before, metaBefore, File.ReadAllBytes(prefabPath),
                    File.Exists(prefabPath + ".meta") ? File.ReadAllBytes(prefabPath + ".meta") : null);
                foreach (var result in results) { result["success"] = true; result["prefabPath"] = prefabPath; }
                return batch ? (object)new { success = true, prefabPath, results } : results[0];
            }
            catch (Exception ex)
            {
                if (saved && before != null)
                {
                    File.WriteAllBytes(prefabPath, before);
                    if (metaBefore != null) File.WriteAllBytes(prefabPath + ".meta", metaBefore);
                    AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceUpdate);
                }
                if (WorkflowManager.IsRecording) WorkflowManager.TruncateCurrentTask(workflowCount);
                return Error(ex, true);
            }
            finally { if (contents != null) PrefabUtility.UnloadPrefabContents(contents); }
        }

        internal static void ValidateSteps(JArray steps)
        {
            if (steps == null || steps.Count == 0 || steps.Count > 50) throw new EditFailure("steps must contain 1 to 50 operations");
            foreach (var token in steps)
            {
                if (!(token is JObject step) || !(step["args"] is JObject args)) throw new EditFailure("Each step must contain skill and args", SkillErrorCode.InvalidJson);
                if (step.Properties().Any(p => p.Name != "skill" && p.Name != "args")) throw new EditFailure("Unknown step field", SkillErrorCode.UnknownParam);
                var skill = step.Value<string>("skill");
                if (skill == null || !OperationFields.TryGetValue(skill, out var fields)) throw new EditFailure($"Unsupported prefab edit operation: {skill}", SkillErrorCode.InvalidSkillName);
                foreach (var field in args.Properties())
                    if (!fields.Contains(field.Name)) throw new EditFailure($"Unknown parameter {field.Name} for {skill}", SkillErrorCode.UnknownParam);
            }
        }

        private static string Required(JObject args, string name)
        {
            var value = args.Value<string>(name);
            if (Validate.Required(value, name) != null) throw new EditFailure($"{name} is required", SkillErrorCode.MissingParam);
            return value;
        }
        private static bool Boolean(JObject args, string name, bool fallback = false)
        {
            if (args[name] == null || args[name].Type == JTokenType.Null) return fallback;
            if (args[name].Type != JTokenType.Boolean) throw new EditFailure($"{name} must be boolean", SkillErrorCode.TypeMismatch);
            return args.Value<bool>(name);
        }
        private static int Integer(JObject args, string name, int fallback = 0)
        {
            if (args[name] == null || args[name].Type == JTokenType.Null) return fallback;
            if (args[name].Type != JTokenType.Integer) throw new EditFailure($"{name} must be integer", SkillErrorCode.TypeMismatch);
            return args.Value<int>(name);
        }
        private static void RequireField(JObject args, string name)
        {
            if (args[name] == null || args[name].Type == JTokenType.Null) throw new EditFailure($"{name} is required", SkillErrorCode.MissingParam);
        }
        private static void ValidName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name == "." || name == "..") throw new EditFailure("Node name must be nonempty and contain no path separators");
        }
        private static void UniqueName(Transform parent, string name, Transform except = null)
        {
            ValidName(name);
            for (var i = 0; i < parent.childCount; i++)
                if (parent.GetChild(i) != except && parent.GetChild(i).name == name) throw new EditFailure($"Sibling name already exists: {name}");
        }
        private static void EditableStructure(GameObject root, GameObject target)
        {
            if (target == root) throw new EditFailure("Cannot delete, duplicate or reparent the prefab root");
            if (PrefabUtility.IsPartOfPrefabInstance(target) && !PrefabUtility.IsAddedGameObjectOverride(target))
                throw new EditFailure("Cannot remove or reparent a node inherited from a variant or nested prefab; edit its source explicitly");
        }

        private static Receipt Apply(GameObject root, string skill, JObject args, bool legacy)
        {
            var target = Resolve(root, args.Value<string>("gameObjectPath"));
            var receipt = new Receipt { Skill = skill, Target = target, OriginalPath = PathOf(root, target) };
            var typeName = args.Value<string>("componentType");
            var index = Integer(args, "componentIndex");
            switch (skill)
            {
                case "prefab_add_component":
                {
                    var type = ComponentType(typeName);
                    if (typeof(Transform).IsAssignableFrom(type)) throw new EditFailure("Transform cannot be added or replaced");
                    var old = target.GetComponents(type);
                    for (var constrained = type; constrained != null && typeof(Component).IsAssignableFrom(constrained); constrained = constrained.BaseType)
                        if (constrained.IsDefined(typeof(DisallowMultipleComponent), false) && target.GetComponent(constrained) != null)
                            throw new EditFailure($"Component disallows multiple instances: {typeName}");
                    var component = target.AddComponent(type);
                    if (component == null || old.Contains(component)) throw new EditFailure($"Unity refused to add component: {typeName}");
                    receipt.Component = component; receipt.MatchType = type;
                    break;
                }
                case "prefab_remove_component":
                {
                    var component = FindComponent(target, typeName, index);
                    if (component is Transform) throw new EditFailure("Transform cannot be removed");
                    var dependents = ComponentSkills.FindBlockingDependents(target, new[] { component });
                    if (dependents.Length > 0) throw new EditFailure($"Component is required by: {string.Join(", ", dependents)}");
                    // Unity supports removed-component overrides, including inherited components.
                    Object.DestroyImmediate(component);
                    if (component != null) throw new EditFailure($"Unity refused to remove component: {typeName}");
                    receipt.RemovedComponent = true; receipt.MatchType = ComponentType(typeName);
                    break;
                }
                case "prefab_set_component_enabled":
                {
                    RequireField(args, "enabled");
                    var component = FindComponent(target, typeName, index);
                    var so = new SerializedObject(component);
                    var prop = so.FindProperty("m_Enabled");
                    if (prop == null || prop.propertyType != SerializedPropertyType.Boolean) throw new EditFailure($"Component cannot be enabled/disabled: {typeName}");
                    prop.boolValue = Boolean(args, "enabled"); so.ApplyModifiedPropertiesWithoutUndo();
                    receipt.Component = component; receipt.MatchType = ComponentType(typeName); receipt.Property = "m_Enabled";
                    break;
                }
                case "prefab_set_serialized_property":
                    SetProperty(root, receipt, args, legacy); break;
                case "prefab_create_gameobject":
                {
                    var parent = Resolve(root, args.Value<string>("parentPath"));
                    var name = Required(args, "name"); UniqueName(parent.transform, name);
                    var created = new GameObject(name, Boolean(args, "rectTransform") ? typeof(RectTransform) : typeof(Transform));
                    created.transform.SetParent(parent.transform, false);
                    receipt.Target = created;
                    break;
                }
                case "prefab_delete_gameobject":
                    EditableStructure(root, target); Object.DestroyImmediate(target); receipt.Deleted = true; break;
                case "prefab_duplicate_gameobject":
                {
                    if (target == root) throw new EditFailure("Cannot duplicate the prefab root");
                    var name = args.Value<string>("newName") ?? target.name + "_Copy";
                    UniqueName(target.transform.parent, name);
                    var duplicate = Object.Instantiate(target, target.transform.parent, false);
                    duplicate.name = name;
                    RestoreNestedConnections(target, duplicate);
                    receipt.Target = duplicate;
                    break;
                }
                case "prefab_rename_gameobject":
                {
                    var name = Required(args, "newName"); ValidName(name);
                    if (target != root) UniqueName(target.transform.parent, name, target.transform);
                    target.name = name; break;
                }
                case "prefab_set_transform": SetTransform(target.transform, args); break;
                case "prefab_set_parent":
                {
                    EditableStructure(root, target);
                    var parent = Resolve(root, args.Value<string>("parentPath"));
                    if (parent == target || parent.transform.IsChildOf(target.transform)) throw new EditFailure("Reparent would create a hierarchy cycle");
                    UniqueName(parent.transform, target.name, target.transform);
                    target.transform.SetParent(parent.transform, Boolean(args, "worldPositionStays")); break;
                }
                case "prefab_set_active": RequireField(args, "active"); target.SetActive(Boolean(args, "active")); break;
                case "prefab_set_sibling_index":
                    RequireField(args, "index");
                    if (target == root) throw new EditFailure("The prefab root has no editable sibling index");
                    EditableStructure(root, target);
                    target.transform.SetSiblingIndex(Integer(args, "index")); break;
                default: throw new EditFailure($"Unknown operation: {skill}", SkillErrorCode.InvalidSkillName);
            }
            if (receipt.Target != null && PrefabUtility.IsPartOfPrefabInstance(receipt.Target))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(receipt.Target);
                PrefabUtility.RecordPrefabInstancePropertyModifications(receipt.Target.transform);
                if (receipt.Component != null) PrefabUtility.RecordPrefabInstancePropertyModifications(receipt.Component);
            }
            return receipt;
        }

        private static void RestoreNestedConnections(GameObject original, GameObject duplicate)
        {
            var originals = original.GetComponentsInChildren<Transform>(true);
            var copies = duplicate.GetComponentsInChildren<Transform>(true);
            var copyByOriginal = new Dictionary<GameObject, GameObject>();
            for (var i = 0; i < originals.Length; i++) copyByOriginal[originals[i].gameObject] = copies[i].gameObject;
            foreach (var node in originals)
            {
                if (!PrefabUtility.IsAnyPrefabInstanceRoot(node.gameObject)) continue;
                var source = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(node.gameObject));
                var copy = copyByOriginal[node.gameObject];
                if (copy == null || source == null) throw new EditFailure("Cannot preserve a missing nested prefab connection");
                if (PrefabUtility.GetCorrespondingObjectFromSource(copy) == source) continue;
                var removed = PrefabUtility.GetRemovedComponents(node.gameObject);
                PrefabUtility.ConvertToPrefabInstance(copy, source, new ConvertToPrefabInstanceSettings
                {
                    objectMatchMode = ObjectMatchMode.ByHierarchy,
                    changeRootNameToAssetName = false,
                    componentsNotMatchedBecomesOverride = true,
                    gameObjectsNotMatchedBecomesOverride = true,
                    recordPropertyOverridesOfMatches = true,
                    logInfo = false
                }, InteractionMode.AutomatedAction);
                foreach (var removal in removed)
                {
                    if (!copyByOriginal.TryGetValue(removal.containingInstanceGameObject, out var containingCopy) || containingCopy == null)
                        throw new EditFailure("Cannot preserve a removed component override during duplication");
                    foreach (var component in containingCopy.GetComponents<Component>())
                        if (component != null && PrefabUtility.GetCorrespondingObjectFromSource(component) == removal.assetComponent)
                            Object.DestroyImmediate(component);
                }
                if (PrefabUtility.GetCorrespondingObjectFromSource(copy) != source)
                    throw new EditFailure("Unity refused to preserve the nested prefab connection");
            }
        }

        private static void SetProperty(GameObject root, Receipt receipt, JObject args, bool legacy)
        {
            var typeName = Required(args, "componentType");
            var component = FindComponent(receipt.Target, typeName, Integer(args, "componentIndex"));
            var property = PrepareProperty(root, component, args, out var so, out var requestedReference,
                out var requested, out var warning, legacy);
            var isReference = property.propertyType == SerializedPropertyType.ObjectReference;
            receipt.Requested = requested; receipt.Warning = warning;
            receipt.Property = property.propertyPath;
            so.ApplyModifiedPropertiesWithoutUndo();
            so.Update();
            if (!legacy && isReference && so.FindProperty(receipt.Property).objectReferenceValue != requestedReference)
                throw new EditFailure("The component refused the requested reference");
            receipt.Component = component; receipt.MatchType = ComponentType(typeName);
        }

        private static SerializedProperty PrepareProperty(GameObject root, Component component, JObject args,
            out SerializedObject so, out Object requestedReference, out string requested, out string warning, bool legacy = false)
        {
            so = new SerializedObject(component);
            var property = SerializedPropertySkillUtility.FindProperty(so, Required(args, "propertyPath"));
            if (property == null) throw new EditFailure("Serialized property not found", SkillErrorCode.TargetNotFound,
                new { properties = SerializedPropertySkillUtility.ListProperties(component, true, 60) });
            if (!property.editable || property.propertyPath == "m_Script" || property.propertyPath.StartsWith("m_GameObject", StringComparison.Ordinal) ||
                property.propertyPath.StartsWith("m_Father", StringComparison.Ordinal) || property.propertyPath.StartsWith("m_Children", StringComparison.Ordinal) ||
                (property.propertyPath.StartsWith("m_Prefab", StringComparison.Ordinal) || property.propertyPath == "m_CorrespondingSourceObject"))
                throw new EditFailure("Cannot write structural or script bookkeeping properties");
            var referencePath = args.Value<string>("referenceGameObjectPath");
            var assetPath = args.Value<string>("assetPath");
            var clear = Boolean(args, "clearReference");
            var sources = (referencePath != null ? 1 : 0) + (!string.IsNullOrEmpty(assetPath) ? 1 : 0) + (clear ? 1 : 0);
            if (sources > 1) throw new EditFailure("Specify only one reference source");
            requestedReference = null;
            requested = null;
            warning = null;
            var isReference = property.propertyType == SerializedPropertyType.ObjectReference;
            if (sources > 0 && !isReference) throw new EditFailure("Reference arguments require an ObjectReference property");
            if (isReference)
            {
                if (referencePath != null)
                {
                    var referenced = Resolve(root, referencePath);
                    var referenceType = args.Value<string>("referenceComponentType");
                    requestedReference = string.IsNullOrEmpty(referenceType) ? (Object)referenced :
                        FindComponent(referenced, referenceType, Integer(args, "referenceComponentIndex"));
                }
                else if (!string.IsNullOrEmpty(assetPath))
                {
                    if (Validate.SafePath(assetPath, "assetPath") != null) throw new EditFailure("Invalid assetPath");
                    requestedReference = AssetDatabase.LoadAssetAtPath<Object>(assetPath);
                    if (requestedReference == null) throw new EditFailure($"Asset not found: {assetPath}", SkillErrorCode.TargetNotFound);
                }
                else if (legacy)
                    throw new EditFailure($"Unsupported serialized property type {property.propertyType}; use assetReferencePath for an ObjectReference field.");
                else if (!clear && args.Value<string>("value") != "null")
                    throw new EditFailure("Object references need assetPath, referenceGameObjectPath or clearReference:true; scene references are not supported");
                property.objectReferenceValue = requestedReference;
                if (!legacy && property.objectReferenceValue != requestedReference) throw new EditFailure("Reference type is incompatible with the field");
                requested = legacy ? assetPath : null;
            }
            else
            {
                RequireField(args, "value");
                if (legacy)
                {
                    bool applied;
                    bool supported;
                    string error;
                    try { applied = PrefabSkills.SetSerializedPropertyValue(property, args.Value<string>("value"), out supported, out error, out warning); }
                    catch (Exception ex) { throw new EditFailure($"Invalid value '{args.Value<string>("value")}' for property '{property.propertyPath}': {ex.Message}"); }
                    if (!applied)
                        throw new EditFailure(supported ? $"Invalid value '{args.Value<string>("value")}' for property '{property.propertyPath}': {error}"
                            : $"Unsupported serialized property type {property.propertyType}; use assetReferencePath for an ObjectReference field.");
                }
                else if (!SerializedPropertySkillUtility.TrySetProperty(property, args.Value<string>("value"), null, 0, null, null, null,
                        out var error, out warning)) throw new EditFailure(error);
                requested = SerializedPropertySkillUtility.DescribeValue(property);
            }
            return property;
        }

        private static float Axis(JObject args, string field, float current)
        {
            var value = args[field];
            if (value == null || value.Type == JTokenType.Null) return current;
            if (value.Type != JTokenType.Float && value.Type != JTokenType.Integer) throw new EditFailure($"{field} must be numeric", SkillErrorCode.TypeMismatch);
            var result = value.Value<float>();
            if (float.IsNaN(result) || float.IsInfinity(result)) throw new EditFailure($"{field} must be finite");
            return result;
        }
        private static Vector3 Axes(JObject a, string prefix, Vector3 v) => new Vector3(Axis(a, prefix + "X", v.x), Axis(a, prefix + "Y", v.y), Axis(a, prefix + "Z", v.z));
        private static Vector2 Axes(JObject a, string prefix, Vector2 v) => new Vector2(Axis(a, prefix + "X", v.x), Axis(a, prefix + "Y", v.y));
        private static void SetTransform(Transform transform, JObject args)
        {
            transform.localPosition = Axes(args, "pos", transform.localPosition);
            transform.localEulerAngles = Axes(args, "rot", transform.localEulerAngles);
            transform.localScale = Axes(args, "scale", transform.localScale);
            if (transform is RectTransform rect)
            {
                rect.anchorMin = Axes(args, "anchorMin", rect.anchorMin); rect.anchorMax = Axes(args, "anchorMax", rect.anchorMax);
                rect.pivot = Axes(args, "pivot", rect.pivot); rect.sizeDelta = Axes(args, "sizeDelta", rect.sizeDelta);
                rect.anchoredPosition = Axes(args, "anchoredPos", rect.anchoredPosition);
            }
            else if (args.Properties().Any(p => p.Value.Type != JTokenType.Null &&
                (p.Name.StartsWith("anchor", StringComparison.Ordinal) || p.Name.StartsWith("pivot", StringComparison.Ordinal) || p.Name.StartsWith("sizeDelta", StringComparison.Ordinal))))
                throw new EditFailure("RectTransform layout fields require a RectTransform");
        }

        private static void PrepareReadBack(GameObject root, Receipt receipt)
        {
            var path = receipt.Target != null ? PathOf(root, receipt.Target) : receipt.OriginalPath;
            var gone = receipt.Target == null;
            var componentGone = receipt.Component == null;
            var index = componentGone ? -1 : Array.IndexOf(receipt.Target.GetComponents(receipt.MatchType), receipt.Component);
            receipt.ReadBack = persisted =>
            {
                var result = new JObject { ["skill"] = receipt.Skill, ["gameObjectPath"] = path };
                if (gone) { result["deleted"] = true; return result; }
                var target = Resolve(persisted, path);
                result.Merge(Json(DescribeNode(persisted, target)));
                if (receipt.RemovedComponent)
                {
                    result["removed"] = true;
                    result["componentType"] = receipt.MatchType.FullName;
                    result["remainingCount"] = target.GetComponents(receipt.MatchType).Length;
                }
                else if (receipt.MatchType != null)
                {
                    result["componentType"] = receipt.MatchType.FullName;
                    if (componentGone) { result["removed"] = true; return result; }
                    var component = FindComponent(target, receipt.MatchType.FullName, index);
                    result["componentIndex"] = index;
                    if (receipt.Property != null)
                    {
                        var prop = new SerializedObject(component).FindProperty(receipt.Property);
                        if (prop == null) throw new EditFailure("Saved property was not found", SkillErrorCode.SkillError);
                        result["propertyPath"] = receipt.Property;
                        result["valueSet"] = DescribePropertyValue(persisted, prop);
                        if (receipt.Requested != null && result.Value<string>("valueSet") != receipt.Requested) result["valueRequested"] = receipt.Requested;
                        if (receipt.Warning != null) result["warnings"] = new JArray(receipt.Warning);
                    }
                }
                return result;
            };
        }

        private static string DescribePropertyValue(GameObject root, SerializedProperty property)
        {
            if (property.propertyType != SerializedPropertyType.ObjectReference) return SerializedPropertySkillUtility.DescribeValue(property);
            var value = property.objectReferenceValue;
            if (value == null) return "null";
            var go = value as GameObject ?? (value as Component)?.gameObject;
            if (go != null && (go == root || go.transform.IsChildOf(root.transform)))
                return PathOf(root, go) + (value is Component comp ? ":" + comp.GetType().FullName + "[" + Array.IndexOf(go.GetComponents(comp.GetType()), comp) + "]" : "");
            return AssetDatabase.GetAssetPath(value);
        }

        internal static object LegacySetProperty(string path, string type, string property, string value, string assetReferencePath, string gameObjectName)
        {
            try
            {
                var root = RequireAsset(path);
                var targetPath = gameObjectName;
                if (!string.IsNullOrEmpty(gameObjectName) && !gameObjectName.Contains('/'))
                {
                    var direct = root.transform.Cast<Transform>().Where(t => t.name == gameObjectName).ToArray();
                    var matches = direct.Length > 0 ? direct : root.GetComponentsInChildren<Transform>(true).Where(t => t.name == gameObjectName).ToArray();
                    if (matches.Length != 1) throw new EditFailure($"Prefab child name not found or ambiguous: {gameObjectName}",
                        matches.Length == 0 ? SkillErrorCode.TargetNotFound : SkillErrorCode.SemanticInvalid);
                    targetPath = PathOf(root, matches[0].gameObject);
                }
                if (string.IsNullOrEmpty(value) && string.IsNullOrEmpty(assetReferencePath)) throw new EditFailure("Either 'value' or 'assetReferencePath' must be provided", SkillErrorCode.MissingParam);
                var result = Run(path, new JArray(new JObject { ["skill"] = "prefab_set_serialized_property",
                    ["args"] = Json(new { componentType = type, propertyPath = property, value, assetPath = assetReferencePath, gameObjectPath = targetPath }) }), false, legacy: true);
                if (SkillResultHelper.TryGetError(result, out _)) return result;
                var response = Json(result);
                response["component"] = type == null ? null : ComponentType(type).Name;
                response["property"] = response["propertyPath"];
                return response;
            }
            catch (Exception ex) { return Error(ex); }
        }

        /// <summary>Checks existing asset state only: dryRun never creates contents or applies a property.</summary>
        internal static void Preview(string prefabPath, string skill, JObject args)
        {
            var root = RequireAsset(prefabPath, true);
            ValidateSteps(new JArray(new JObject { ["skill"] = skill, ["args"] = args }));
            var method = typeof(PrefabSkills).GetMethods().First(m => m.GetCustomAttributes(typeof(UnitySkillAttribute), false)
                .Cast<UnitySkillAttribute>().Any(a => a.Name == skill));
            foreach (var parameter in method.GetParameters())
            {
                if (parameter.Name == "prefabPath") continue;
                if (!parameter.HasDefaultValue && (args[parameter.Name] == null || args[parameter.Name].Type == JTokenType.Null))
                    throw new EditFailure($"{parameter.Name} is required", SkillErrorCode.MissingParam);
            }
            var target = Resolve(root, args.Value<string>("gameObjectPath"));
            if (skill == "prefab_delete_gameobject" || skill == "prefab_set_parent") EditableStructure(root, target);
            if (skill == "prefab_set_parent")
            {
                var parent = Resolve(root, args.Value<string>("parentPath"));
                if (parent == target || parent.transform.IsChildOf(target.transform)) throw new EditFailure("Reparent would create a hierarchy cycle");
                UniqueName(parent.transform, target.name, target.transform);
            }
            if (skill == "prefab_create_gameobject") UniqueName(Resolve(root, args.Value<string>("parentPath")).transform, Required(args, "name"));
            if (skill == "prefab_rename_gameobject")
            {
                var name = Required(args, "newName"); ValidName(name);
                if (target != root) UniqueName(target.transform.parent, name, target.transform);
            }
            if (skill == "prefab_duplicate_gameobject")
            {
                if (target == root) throw new EditFailure("Cannot duplicate the prefab root");
                UniqueName(target.transform.parent, args.Value<string>("newName") ?? target.name + "_Copy");
            }
            if (skill == "prefab_set_active") Boolean(args, "active");
            if (skill == "prefab_set_sibling_index") { EditableStructure(root, target); Integer(args, "index"); }
            if (skill == "prefab_set_transform")
            {
                foreach (var field in args.Properties().Where(p => p.Name != "gameObjectPath")) Axis(args, field.Name, 0);
                if (!(target.transform is RectTransform) && args.Properties().Any(p => p.Value.Type != JTokenType.Null &&
                    (p.Name.StartsWith("anchor", StringComparison.Ordinal) || p.Name.StartsWith("pivot", StringComparison.Ordinal) || p.Name.StartsWith("sizeDelta", StringComparison.Ordinal))))
                    throw new EditFailure("RectTransform layout fields require a RectTransform");
            }
            if (!skill.Contains("component") && skill != "prefab_set_serialized_property") return;
            var type = ComponentType(Required(args, "componentType"));
            if (skill == "prefab_add_component")
            {
                if (typeof(Transform).IsAssignableFrom(type)) throw new EditFailure("Transform cannot be added or replaced");
                for (var constrained = type; constrained != null && typeof(Component).IsAssignableFrom(constrained); constrained = constrained.BaseType)
                    if (constrained.IsDefined(typeof(DisallowMultipleComponent), false) && target.GetComponent(constrained) != null)
                        throw new EditFailure("Component disallows multiple instances");
                return;
            }
            var component = FindComponent(target, type.FullName, Integer(args, "componentIndex"));
            if (skill == "prefab_remove_component")
            {
                if (component is Transform) throw new EditFailure("Transform cannot be removed");
                var dependents = ComponentSkills.FindBlockingDependents(target, new[] { component });
                if (dependents.Length > 0) throw new EditFailure("Component is required by: " + string.Join(", ", dependents));
            }
            if (skill == "prefab_set_component_enabled")
            {
                Boolean(args, "enabled");
                if (new SerializedObject(component).FindProperty("m_Enabled") == null) throw new EditFailure("Component cannot be enabled/disabled");
            }
            if (skill == "prefab_set_serialized_property")
            {
                // Conversion changes only this SerializedObject copy; dryRun never applies it.
                PrepareProperty(root, component, args, out _, out _, out _, out _);
            }
        }

        private static void InitializeFields()
        {
            OperationFields["prefab_add_component"] = new[] { "componentType", "gameObjectPath" };
            OperationFields["prefab_remove_component"] = new[] { "componentType", "gameObjectPath", "componentIndex" };
            OperationFields["prefab_set_component_enabled"] = new[] { "componentType", "enabled", "gameObjectPath", "componentIndex" };
            OperationFields["prefab_set_serialized_property"] = new[] { "componentType", "propertyPath", "gameObjectPath", "componentIndex", "value", "assetPath", "referenceGameObjectPath", "referenceComponentType", "referenceComponentIndex", "clearReference" };
            OperationFields["prefab_create_gameobject"] = new[] { "name", "parentPath", "rectTransform" };
            OperationFields["prefab_delete_gameobject"] = new[] { "gameObjectPath" };
            OperationFields["prefab_duplicate_gameobject"] = new[] { "gameObjectPath", "newName" };
            OperationFields["prefab_rename_gameobject"] = new[] { "newName", "gameObjectPath" };
            OperationFields["prefab_set_transform"] = new[] { "gameObjectPath", "posX", "posY", "posZ", "rotX", "rotY", "rotZ", "scaleX", "scaleY", "scaleZ", "anchoredPosX", "anchoredPosY", "anchorMinX", "anchorMinY", "anchorMaxX", "anchorMaxY", "pivotX", "pivotY", "sizeDeltaX", "sizeDeltaY" };
            OperationFields["prefab_set_parent"] = new[] { "gameObjectPath", "parentPath", "worldPositionStays" };
            OperationFields["prefab_set_active"] = new[] { "active", "gameObjectPath" };
            OperationFields["prefab_set_sibling_index"] = new[] { "index", "gameObjectPath" };
        }
    }
}

// Producer:Betsy
