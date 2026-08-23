using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    /// <summary>
    /// 组件管理技能：增删组件、读写属性。
    /// 支持按 name / instanceId / path 定位目标，并带有较完善的类型转换与引用解析。
    /// </summary>
    public static class ComponentSkills
    {
        private static readonly Dictionary<string, System.Type> _typeCache = new Dictionary<string, System.Type>();

        // 属性 / 字段查找缓存，避免重复反射。
        // 注意：非线程安全——只允许在 Unity 主线程访问
        // （由 SkillsHttpServer 的生产者-消费者模型保证）。
        private static readonly Dictionary<string, (PropertyInfo prop, FieldInfo field)> _memberCache =
            new Dictionary<string, (PropertyInfo, FieldInfo)>();
        
        // 按简单类名查找组件类型时额外搜索的命名空间（含常见第三方插件）。
        private static readonly string[] ExtendedNamespaces = new[]
        {
            // Unity 内置
            "UnityEngine.",
            "UnityEngine.UI.",
            "UnityEngine.Rendering.",
            "UnityEngine.Rendering.Universal.",
            "UnityEngine.Rendering.HighDefinition.",
            "UnityEngine.Animations.",
            "UnityEngine.Playables.",
            "UnityEngine.AI.",
            "UnityEngine.Audio.",
            "UnityEngine.Video.",
            "UnityEngine.VFX.",
            "UnityEngine.Tilemaps.",
            "UnityEngine.U2D.",
            // Cinemachine（两个大版本命名空间不同）
            "Cinemachine.",
            "Unity.Cinemachine.",
            // TextMeshPro
            "TMPro.",
            // Input System
            "UnityEngine.InputSystem.",
            // XR
            "UnityEngine.XR.",
            "UnityEngine.XR.Interaction.Toolkit.",
            // 常见第三方
            "DG.Tweening.",
            "Rewired.",
        };

        [UnitySkill("component_add", "Add a component to a GameObject (supports name/instanceId/path). Works with Cinemachine, TextMeshPro, etc.",
            Category = SkillCategory.Component, Operation = SkillOperation.Create,
            Tags = new[] { "add", "attach", "behaviour", "rigidbody", "collider", "script" },
            Outputs = new[] { "gameObject", "instanceId", "component", "fullTypeName" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true,
            MutatesScene = true)]
        public static object ComponentAdd(string name = null, int instanceId = 0, string path = null, string componentType = null)
        {
            if (Validate.Required(componentType, "componentType") is object err) return err;

            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var type = FindComponentType(componentType);
            if (type == null)
                return new { 
                    error = $"Component type not found: {componentType}",
                    hint = "Try using full type name like 'CinemachineVirtualCamera' or 'Unity.Cinemachine.CinemachineCamera'",
                    availableTypes = GetSimilarTypes(componentType)
                };

            // 不允许多实例的组件，已存在就不再添加。
            if (go.GetComponent(type) != null && !AllowMultiple(type))
                return new { 
                    warning = $"Component {type.Name} already exists on {go.name}",
                    gameObject = go.name,
                    entityId = UnityObjectIdUtility.GetEntityId(go),
                    instanceId = UnityObjectIdUtility.GetObjectId(go)
                };

            var comp = Undo.AddComponent(go, type);

            if (WorkflowManager.IsRecording)
            {
                WorkflowManager.SnapshotCreatedComponent(comp);
            }

            EditorUtility.SetDirty(go);

            return new {
                success = true,
                gameObject = go.name,
                entityId = UnityObjectIdUtility.GetEntityId(go),
                instanceId = UnityObjectIdUtility.GetObjectId(go),
                component = type.Name,
                fullTypeName = type.FullName
            };
        }

        [UnitySkill("component_add_batch", "Add components to multiple GameObjects. items: JSON array of {name, componentType, path}",
            Category = SkillCategory.Component, Operation = SkillOperation.Create,
            Tags = new[] { "add", "attach", "behaviour", "batch" },
            // 这里声明的是批处理外层信封的键，不是 results[] 里逐项的键。写成内层键会让
            // /skills/chain 以为本技能产出顶层 `component`，从而把它当作后续步骤的输入，
            // 而那一步根本读不到。
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true, MutatesScene = true)]
        public static object ComponentAddBatch(string items)
        {
            return BatchExecutor.Execute<BatchAddComponentItem>(items, item =>
            {
                var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path);
                if (error != null) return new { error = "Object not found", target = item.name ?? item.path };

                if (string.IsNullOrEmpty(item.componentType))
                    return new { error = "componentType required" };

                var type = FindComponentType(item.componentType);
                if (type == null)
                    return new { error = $"Component type not found: {item.componentType}" };

                // 不允许多实例的组件，已存在就不再添加。
                if (go.GetComponent(type) != null && !AllowMultiple(type))
                    return new { target = go.name, success = true, warning = "Component already exists", component = type.Name };

                var comp = Undo.AddComponent(go, type);

                if (WorkflowManager.IsRecording)
                    WorkflowManager.SnapshotCreatedComponent(comp);

                EditorUtility.SetDirty(go);
                return new { target = go.name, success = true, component = type.Name };
            }, item => item.name ?? item.path);
        }

        private class BatchAddComponentItem
        {
            public string name { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public string componentType { get; set; }
        }

        [UnitySkill("component_remove", "Remove a component from a GameObject (supports name/instanceId/path)",
            Category = SkillCategory.Component, Operation = SkillOperation.Delete,
            Tags = new[] { "remove", "detach", "destroy" },
            Outputs = new[] { "gameObject", "removed" },
            RequiresInput = new[] { "gameObject", "component" },
            TracksWorkflow = true, SkipAutoPresnapshot = true,
            MutatesScene = true,
            RiskLevel = "medium")]
        public static object ComponentRemove(string name = null, int instanceId = 0, string path = null, string componentType = null, int componentIndex = 0)
        {
            if (Validate.Required(componentType, "componentType") is object err) return err;

            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var type = FindComponentType(componentType);
            if (type == null)
                return new { error = $"Component type not found: {componentType}" };

            // 同类型可能挂了多份，用 componentIndex 指定要删哪一份。
            var components = go.GetComponents(type);
            if (components.Length == 0)
                return new { error = $"Component not found on {go.name}: {componentType}" };

            if (componentIndex >= components.Length)
                return new { error = $"Component index {componentIndex} out of range. Found {components.Length} components of type {componentType}" };

            var comp = components[componentIndex];

            var requiredBy = GetRequiredByComponents(go, type);
            if (requiredBy.Any())
                return new {
                    error = $"Cannot remove {componentType} - required by: {string.Join(", ", requiredBy)}",
                    hint = "Remove dependent components first"
                };

            if (!WorkflowManager.DeleteSceneObject(comp))
                return new { error = $"Failed to capture and remove {componentType}" };
            EditorUtility.SetDirty(go);

            return new { success = true, gameObject = go.name, removed = componentType };
        }

        [UnitySkill("component_remove_batch", "Remove components from multiple GameObjects. items: JSON array of {name, componentType, path}",
            Category = SkillCategory.Component, Operation = SkillOperation.Delete,
            Tags = new[] { "remove", "detach", "destroy", "batch" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "gameObject", "component" },
            TracksWorkflow = true, SkipAutoPresnapshot = true,
            MutatesScene = true,
            RiskLevel = "medium")]
        public static object ComponentRemoveBatch(string items)
        {
            return BatchExecutor.Execute<BatchRemoveComponentItem>(items, item =>
            {
                var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path);
                if (error != null) return new { error = "Object not found", target = item.name ?? item.path };

                if (string.IsNullOrEmpty(item.componentType))
                    return new { error = "componentType required" };

                var type = FindComponentType(item.componentType);
                if (type == null)
                    return new { error = $"Component type not found: {item.componentType}" };

                var components = go.GetComponents(type);
                if (components.Length == 0)
                    return new { error = $"Component not found: {item.componentType}", target = go.name };

                Undo.RecordObject(go, "Batch Remove Component");
                foreach (var c in components)
                {
                    if (!WorkflowManager.DeleteSceneObject(c))
                        return new { error = $"Failed to capture and remove {item.componentType}" };
                }

                EditorUtility.SetDirty(go);
                return new { target = go.name, success = true, removed = type.Name, count = components.Length };
            }, item => item.name ?? item.path);
        }

        private class BatchRemoveComponentItem
        {
            public string name { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public string componentType { get; set; }
        }

        [UnitySkill("component_list", "List all components on a GameObject with detailed info (supports name/instanceId/path)",
            Category = SkillCategory.Component, Operation = SkillOperation.Query,
            Tags = new[] { "list", "inspect", "enumerate" },
            Outputs = new[] { "gameObject", "instanceId", "path", "components" },
            RequiresInput = new[] { "gameObject" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ComponentList(string name = null, int instanceId = 0, string path = null, bool includeProperties = false)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var components = go.GetComponents<Component>()
                .Where(c => c != null)
                .Select(c => {
                    var info = new Dictionary<string, object>
                    {
                        { "type", c.GetType().Name },
                        { "fullType", c.GetType().FullName },
                    };

                    // Behaviour、Renderer、Collider/Collider2D 各自声明了自己的 enabled
                    // （后三者都不继承 Behaviour），所以必须逐类型判断：只按 Behaviour 转型
                    // 会漏掉它们并退回默认 true，把已被 component_set_enabled 禁用的
                    // Renderer/Collider 报成 enabled:true。没有 enabled 概念的类型
                    // （如 Transform）则干脆不输出该字段，不去猜。
                    if (c is Behaviour behaviour)
                        info["enabled"] = behaviour.enabled;
                    else if (c is Renderer renderer)
                        info["enabled"] = renderer.enabled;
                    else if (c is Collider collider)
                        info["enabled"] = collider.enabled;
                    else if (c is Collider2D collider2D)
                        info["enabled"] = collider2D.enabled;

                    if (includeProperties)
                    {
                        var props = GetComponentPropertiesSummary(c);
                        if (props.Any())
                            info["keyProperties"] = props;
                    }
                    
                    return info;
                })
                .ToArray();

            return new {
                gameObject = go.name,
                entityId = UnityObjectIdUtility.GetEntityId(go),
                instanceId = UnityObjectIdUtility.GetObjectId(go),
                path = GameObjectFinder.GetPath(go),
                componentCount = components.Length,
                components
            };
        }

        [UnitySkill("component_set_property", "Set a property/field on a component. Supports Vector2/3/4, Color, scene references by name/path, project assets by assetPath. Vector and Color values accept both the comma form (\"1,2,3\") and the JSON object form ({\"x\":1,\"y\":2,\"z\":3} / {\"r\":1,\"g\":0,\"b\":0,\"a\":1}); Color also accepts #RRGGBB and named colours. In the object form every vector component is required (a partial {\"y\":2} is rejected, not zero-filled); Color's \"a\" may be omitted and defaults to 1. valueSet echoes the stored value in a round-trippable form.",
            Category = SkillCategory.Component, Operation = SkillOperation.Modify,
            Tags = new[] { "property", "field", "value", "reference" },
            Outputs = new[] { "gameObject", "component", "property", "valueSet", "valueType" },
            RequiresInput = new[] { "gameObject", "component" },
            TracksWorkflow = true)]
        public static object ComponentSetProperty(
            string name = null, int instanceId = 0, string path = null,
            string componentType = null, string propertyName = null,
            string value = null, string referencePath = null, string referenceName = null,
            string assetPath = null)
        {
            if (string.IsNullOrEmpty(componentType) || string.IsNullOrEmpty(propertyName))
                return new { error = "componentType and propertyName are required" };

            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var type = FindComponentType(componentType);
            if (type == null)
                return new { error = $"Component type not found: {componentType}" };
                
            var comp = go.GetComponent(type);
            if (comp == null)
                return new { error = $"Component not found: {componentType}" };

            var (prop, field) = FindMember(type, propertyName);

            if (prop == null && field == null)
                return new {
                    error = $"Property/field not found: {propertyName}",
                    availableProperties = GetAvailableProperties(type)
                };

            WorkflowManager.SnapshotObject(comp);
            Undo.RecordObject(comp, "Set Property");

            try
            {
                var targetType = prop?.PropertyType ?? field.FieldType;
                object converted;

                // 工程资源引用（ScriptableObject、Prefab、Material、Texture 等）。
                if (!string.IsNullOrEmpty(assetPath))
                {
                    converted = ResolveAssetReference(targetType, assetPath);
                    if (converted == null)
                        return new { error = $"Asset not found or type mismatch: '{assetPath}' (expected {targetType.Name})" };
                }
                // 场景内引用（Transform / GameObject / Component）。
                else if (!string.IsNullOrEmpty(referencePath) || !string.IsNullOrEmpty(referenceName))
                {
                    converted = ResolveReference(targetType, referencePath, referenceName);
                    if (converted == null)
                        return new { error = $"Could not resolve reference for {propertyName}. Target: path='{referencePath}', name='{referenceName}'" };
                }
                else
                {
                    converted = ConvertValue(value, targetType);
                }

                if (prop != null && prop.CanWrite)
                    prop.SetValue(comp, converted);
                else if (field != null)
                    field.SetValue(comp, converted);
                else
                    return new { error = $"Property {propertyName} is read-only" };

                EditorUtility.SetDirty(comp);
                
                return new { 
                    success = true, 
                    gameObject = go.name, 
                    component = componentType,
                    property = propertyName,
                    valueSet = FormatValue(converted),
                    valueType = targetType.Name
                };
            }
            catch (PropertyValueException ex)
            {
                // 这是"值被拒绝"而不是"技能坏了"：显式声明错误码，路由才会回
                // "改值后重试"，而不是 SKILL_ERROR + abort。
                return new
                {
                    error = ex.Message,
                    errorCode = SkillParamUtil.SemanticInvalidCode,
                    parameter = "value",
                };
            }
            catch (System.Exception ex)
            {
                return new {
                    error = ex.Message,
                };
            }
        }

        [UnitySkill("component_set_property_batch","Set properties on multiple components (Efficient). items: JSON array of {name, componentType, propertyName, value, referencePath, referenceName, assetPath}",
            Category = SkillCategory.Component, Operation = SkillOperation.Modify,
            Tags = new[] { "property", "field", "value", "reference", "batch" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "gameObject", "component" },
            TracksWorkflow = true)]
        public static object ComponentSetPropertyBatch(string items)
        {
            return BatchExecutor.Execute<BatchSetPropertyItem>(items, item =>
            {
                if (string.IsNullOrEmpty(item.componentType) || string.IsNullOrEmpty(item.propertyName))
                    return new { error = "componentType and propertyName required" };

                var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path);
                if (error != null) return new { error = "Object not found", target = item.name ?? item.path };

                var type = FindComponentType(item.componentType);
                if (type == null)
                    return new { error = $"Component type not found: {item.componentType}" };

                var comp = go.GetComponent(type);
                if (comp == null)
                    return new { error = $"Component not found: {item.componentType}", target = go.name };

                var (prop, field) = FindMember(type, item.propertyName);

                if (prop == null && field == null)
                    return new { error = $"Property/field not found: {item.propertyName}" };

                WorkflowManager.SnapshotObject(comp);
                Undo.RecordObject(comp, "Batch Set Property");

                var targetType = prop?.PropertyType ?? field.FieldType;
                object converted;

                if (!string.IsNullOrEmpty(item.assetPath))
                {
                    converted = ResolveAssetReference(targetType, item.assetPath);
                    if (converted == null)
                        return new { error = $"Asset not found or type mismatch: '{item.assetPath}' (expected {targetType.Name})" };
                }
                else if (!string.IsNullOrEmpty(item.referencePath) || !string.IsNullOrEmpty(item.referenceName))
                {
                    converted = ResolveReference(targetType, item.referencePath, item.referenceName);
                    if (converted == null)
                        return new { error = $"Reference resolution failed for {item.propertyName}" };
                }
                else
                {
                    // Newtonsoft 把 JSON 数字还原成 double/long，直接 ToString() 会跟随编辑器
                    // 区域设置：小数点为逗号的机器上 1.5 会变成 "1,5"，而 ConvertValue 按
                    // 文化不变格式解析不了。嵌套的对象 / 数组重新序列化，
                    // 让 {"x":..,"y":..} 这种形式原样送到 ConvertValue。
                    string valStr;
                    if (item.value == null)
                        valStr = null;
                    else if (item.value is JToken token)
                        valStr = token.Type == JTokenType.String
                            ? token.Value<string>()
                            : token.ToString(Formatting.None);
                    else
                        valStr = SkillParamUtil.FormatScalarR(item.value);

                    converted = ConvertValue(valStr, targetType);
                }

                if (prop != null && prop.CanWrite)
                    prop.SetValue(comp, converted);
                else if (field != null)
                    field.SetValue(comp, converted);
                else
                    return new { error = $"Property {item.propertyName} is read-only" };

                EditorUtility.SetDirty(comp);
                return new
                {
                    target = go.name,
                    success = true,
                    property = item.propertyName,
                    valueSet = FormatValue(converted),
                    valueType = targetType.Name
                };
            }, item => item.name ?? item.path);
        }

        [UnitySkill("component_get_serialized_properties", "List Inspector serialized properties on a component via SerializedObject (supports nested fields and array/list property paths)",
            Category = SkillCategory.Component, Operation = SkillOperation.Query,
            Tags = new[] { "serialized", "inspector", "property", "field" },
            Outputs = new[] { "gameObject", "component", "properties" },
            RequiresInput = new[] { "gameObject", "component" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ComponentGetSerializedProperties(
            string name = null, int instanceId = 0, string path = null,
            string componentType = null, bool includeChildren = true, int limit = 200)
        {
            if (Validate.Required(componentType, "componentType") is object reqErr) return reqErr;

            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var type = FindComponentType(componentType);
            if (type == null) return new { error = $"Component type not found: {componentType}" };

            var comp = go.GetComponent(type);
            if (comp == null) return new { error = $"Component not found: {componentType}" };

            return new
            {
                success = true,
                gameObject = go.name,
                component = componentType,
                fullTypeName = type.FullName,
                properties = SerializedPropertySkillUtility.ListProperties(comp, includeChildren, limit)
            };
        }

        [UnitySkill("component_set_serialized_property", "Set an Inspector serialized property on a component by propertyPath. Supports nested fields, arrays/lists, object references, vectors, colors, enums, and primitives.",
            Category = SkillCategory.Component, Operation = SkillOperation.Modify,
            Tags = new[] { "serialized", "inspector", "property", "field", "reference" },
            Outputs = new[] { "gameObject", "component", "propertyPath", "valueSet" },
            RequiresInput = new[] { "gameObject", "component" },
            TracksWorkflow = true)]
        public static object ComponentSetSerializedProperty(
            string name = null, int instanceId = 0, string path = null,
            string componentType = null, string propertyPath = null, string value = null,
            string referenceName = null, int referenceInstanceId = 0, string referencePath = null,
            string assetPath = null, string objectType = null)
        {
            if (Validate.Required(componentType, "componentType") is object reqErr1) return reqErr1;
            if (Validate.Required(propertyPath, "propertyPath") is object reqErr2) return reqErr2;

            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var type = FindComponentType(componentType);
            if (type == null) return new { error = $"Component type not found: {componentType}" };

            var comp = go.GetComponent(type);
            if (comp == null) return new { error = $"Component not found: {componentType}" };

            var serializedObject = new SerializedObject(comp);
            serializedObject.Update();
            var property = SerializedPropertySkillUtility.FindProperty(serializedObject, propertyPath);
            if (property == null)
            {
                return new
                {
                    error = $"Serialized property not found: {propertyPath}",
                    availableProperties = SerializedPropertySkillUtility.ListProperties(comp, true, 60)
                };
            }

            WorkflowManager.SnapshotObject(comp);
            Undo.RecordObject(comp, "Set Serialized Property");

            if (!SerializedPropertySkillUtility.TrySetProperty(
                    property, value, referenceName, referenceInstanceId, referencePath, assetPath, objectType, out var setError))
            {
                return new { error = setError };
            }

            serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(comp);

            return new
            {
                success = true,
                gameObject = go.name,
                component = componentType,
                propertyPath = property.propertyPath,
                valueSet = SerializedPropertySkillUtility.DescribeValue(property)
            };
        }

        [UnitySkill("component_set_serialized_property_batch", "Set Inspector serialized properties on multiple components. items: JSON array of {name, instanceId, path, componentType, propertyPath, value, referenceName, referenceInstanceId, referencePath, assetPath, objectType}",
            Category = SkillCategory.Component, Operation = SkillOperation.Modify,
            Tags = new[] { "serialized", "inspector", "property", "field", "batch" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "gameObject", "component" },
            TracksWorkflow = true)]
        public static object ComponentSetSerializedPropertyBatch(string items)
        {
            return BatchExecutor.Execute<BatchSetSerializedPropertyItem>(items, item =>
            {
                var result = ComponentSetSerializedProperty(
                    item.name, item.instanceId, item.path,
                    item.componentType, item.propertyPath, item.value,
                    item.referenceName, item.referenceInstanceId, item.referencePath,
                    item.assetPath, item.objectType);
                if (SkillResultHelper.TryGetError(result, out var error))
                {
                    return new { error = error, target = item.name ?? item.path };
                }
                return result;
            }, item => item.name ?? item.path ?? item.instanceId.ToString());
        }

        private class BatchSetPropertyItem
        {
            public string name { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public string componentType { get; set; }
            public string propertyName { get; set; }
            public object value { get; set; }
            public string referencePath { get; set; }
            public string referenceName { get; set; }
            public string assetPath { get; set; }
        }

        private class BatchSetSerializedPropertyItem
        {
            public string name { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public string componentType { get; set; }
            public string propertyPath { get; set; }
            public string value { get; set; }
            public string referenceName { get; set; }
            public int referenceInstanceId { get; set; }
            public string referencePath { get; set; }
            public string assetPath { get; set; }
            public string objectType { get; set; }
        }

        [UnitySkill("component_get_properties", "Get all properties of a component (supports name/instanceId/path)",
            Category = SkillCategory.Component, Operation = SkillOperation.Query,
            Tags = new[] { "property", "field", "inspect", "reflection" },
            Outputs = new[] { "gameObject", "component", "properties", "fields" },
            RequiresInput = new[] { "gameObject", "component" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ComponentGetProperties(string name = null, int instanceId = 0, string path = null, string componentType = null, bool includePrivate = false)
        {
            if (Validate.Required(componentType, "componentType") is object err) return err;

            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var type = FindComponentType(componentType);
            if (type == null)
                return new { error = $"Component type not found: {componentType}" };
                
            var comp = go.GetComponent(type);
            if (comp == null)
                return new { error = $"Component not found: {componentType}" };

            var bindingFlags = BindingFlags.Public | BindingFlags.Instance;
            if (includePrivate)
                bindingFlags |= BindingFlags.NonPublic;

            var props = type.GetProperties(bindingFlags)
                .Where(p => p.CanRead && !p.GetIndexParameters().Any())
                .Select(p =>
                {
                    try 
                    { 
                        var val = ReadPropertyValueSafely(comp, p);
                        return new { 
                            name = p.Name, 
                            type = p.PropertyType.Name, 
                            fullType = p.PropertyType.FullName,
                            value = FormatValue(val),
                            canWrite = p.CanWrite
                        }; 
                    }
                    catch { return new { name = p.Name, type = p.PropertyType.Name, fullType = p.PropertyType.FullName, value = "(error reading)", canWrite = p.CanWrite }; }
                })
                .ToArray();

            var fields = type.GetFields(bindingFlags)
                .Select(f =>
                {
                    try 
                    { 
                        var val = f.GetValue(comp);
                        return new { 
                            name = f.Name, 
                            type = f.FieldType.Name, 
                            fullType = f.FieldType.FullName,
                            value = FormatValue(val),
                            isSerializable = f.IsPublic || f.GetCustomAttribute<SerializeField>() != null
                        }; 
                    }
                    catch { return new { name = f.Name, type = f.FieldType.Name, fullType = f.FieldType.FullName, value = "(error reading)", isSerializable = false }; }
                })
                .ToArray();

            return new { 
                gameObject = go.name, 
                component = componentType, 
                fullTypeName = type.FullName,
                properties = props,
                fields = fields
            };
        }

        private static object ReadPropertyValueSafely(Component comp, PropertyInfo property)
        {
            if (comp is Renderer renderer)
            {
                if (property.Name == "material")
                    return renderer.sharedMaterial;
                if (property.Name == "materials")
                    return renderer.sharedMaterials;
            }

            return property.GetValue(comp);
        }

        #region Type Finding (Enhanced for Third-Party)
        
        /// <summary>
        /// 跨命名空间广泛搜索组件类型，可命中 Cinemachine、TextMeshPro 等常见插件。
        /// </summary>
        public static System.Type FindComponentType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            
            if (_typeCache.TryGetValue(name, out var cached))
                return cached;

            System.Type result = null;
            
            // 1. 先按原样当全限定名试。
            result = System.Type.GetType(name);
            if (result != null && typeof(Component).IsAssignableFrom(result))
            {
                CacheType(name, result);
                return result;
            }

            // 2. 取出简单类名。
            var simpleName = name.Contains(".") ? name.Substring(name.LastIndexOf('.') + 1) : name;

            // 3. 拼常见命名空间再试。
            foreach (var ns in ExtendedNamespaces)
            {
                result = TryGetTypeFromAssemblies(ns + simpleName);
                if (result != null && typeof(Component).IsAssignableFrom(result))
                {
                    CacheType(name, result);
                    return result;
                }
            }

            // 4. 兜底：按简单名遍历全部已加载程序集（最慢，但覆盖最全）。
            result = SkillsCommon.GetAllLoadedTypes()
                .FirstOrDefault(t =>
                    (t.Name.Equals(simpleName, System.StringComparison.OrdinalIgnoreCase) ||
                     t.FullName == name) &&
                    typeof(Component).IsAssignableFrom(t));

            if (result != null)
            {
                CacheType(name, result);
            }

            return result;
        }

        private static void CacheType(string name, System.Type type)
        {
            if (_typeCache.Count > 5000) _typeCache.Clear();
            _typeCache[name] = type;
        }

        private static System.Type TryGetTypeFromAssemblies(string fullName)
        {
            // 只在这几个常见程序集里找，避免全量扫描。
            var assemblyNames = new[] {
                "UnityEngine",
                "UnityEngine.UI",
                "UnityEngine.CoreModule",
                "Unity.TextMeshPro",
                "Unity.Cinemachine",
                "Cinemachine",
                "Unity.InputSystem",
                "Unity.RenderPipelines.Universal.Runtime",
                "Unity.RenderPipelines.HighDefinition.Runtime"
            };

            foreach (var asmName in assemblyNames)
            {
                try
                {
                    var type = System.Type.GetType($"{fullName}, {asmName}");
                    if (type != null) return type;
                }
                catch { /* Type not in this assembly — expected during package detection */ }
            }
            return null;
        }

        private static string[] GetSimilarTypes(string searchTerm)
        {
            var simpleName = searchTerm.Contains(".") ? searchTerm.Substring(searchTerm.LastIndexOf('.') + 1) : searchTerm;
            
            return SkillsCommon.GetAllLoadedTypes()
                .Where(t => typeof(Component).IsAssignableFrom(t) &&
                           t.Name.IndexOf(simpleName, System.StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(10)
                .Select(t => t.FullName)
                .ToArray();
        }

        private static bool AllowMultiple(System.Type type)
        {
            try { return type.GetCustomAttributes(typeof(DisallowMultipleComponent), true).Length == 0; }
            catch { return true; }
        }

        private static string[] GetRequiredByComponents(GameObject go, System.Type targetType)
        {
            try
            {
                return go.GetComponents<Component>()
                    .Where(c => c != null && c.GetType() != targetType)
                    .Where(c => c.GetType().GetCustomAttributes(typeof(RequireComponent), true)
                        .OfType<RequireComponent>()
                        .Any(r => r.m_Type0 == targetType || r.m_Type1 == targetType || r.m_Type2 == targetType))
                    .Select(c => c.GetType().Name)
                    .ToArray();
            }
            catch { return new string[0]; }
        }
        
        #endregion

        #region Value Conversion (Enhanced)

        /// <summary>
        /// 把字符串值转换为目标类型，覆盖基元类型、Unity 常用结构体、枚举与 AnimationCurve。
        /// </summary>
        internal static object ConvertValue(string value, System.Type targetType)
        {
            if (value == null || value.Equals("null", System.StringComparison.OrdinalIgnoreCase))
                return targetType.IsValueType ? System.Activator.CreateInstance(targetType) : null;

            if (targetType == typeof(string)) return value;
            if (targetType == typeof(int)) return int.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(float)) return float.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(double)) return double.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool)) return ParseBool(value);
            if (targetType == typeof(long)) return long.Parse(value, CultureInfo.InvariantCulture);
            
            if (targetType == typeof(Vector2)) return ParseVector2(value);
            if (targetType == typeof(Vector3)) return ParseVector3(value);
            if (targetType == typeof(Vector4)) return ParseVector4(value);
            if (targetType == typeof(Vector2Int)) return ParseVector2Int(value);
            if (targetType == typeof(Vector3Int)) return ParseVector3Int(value);
            
            if (targetType == typeof(Quaternion)) return ParseQuaternion(value);
            if (targetType == typeof(Color)) return ParseColor(value);
            if (targetType == typeof(Color32)) return ParseColor32(value);
            if (targetType == typeof(Rect)) return ParseRect(value);
            if (targetType == typeof(Bounds)) return ParseBounds(value);
            if (targetType == typeof(LayerMask)) return ParseLayerMask(value);
            
            if (targetType.IsEnum)
                return System.Enum.Parse(targetType, value, true);

            if (targetType == typeof(AnimationCurve))
                return ParseAnimationCurve(value);

            return System.Convert.ChangeType(value, targetType);
        }

        private static bool ParseBool(string value)
        {
            value = value.ToLower().Trim();
            return value == "true" || value == "1" || value == "yes" || value == "on";
        }

        private static Vector2 ParseVector2(string value)
        {
            if (SkillParamUtil.LooksLikeJsonObject(value))
            {
                var json = ParseJsonObjectFloats(value, new[] { "x", "y" }, new float[] { 0, 0 }, requiredKeyCount: 2);
                return new Vector2(json[0], json[1]);
            }
            var parts = ParseFloatArray(value, 2);
            return new Vector2(parts[0], parts[1]);
        }

        private static Vector3 ParseVector3(string value)
        {
            if (SkillParamUtil.LooksLikeJsonObject(value))
            {
                var json = ParseJsonObjectFloats(value, new[] { "x", "y", "z" }, new float[] { 0, 0, 0 }, requiredKeyCount: 3);
                return new Vector3(json[0], json[1], json[2]);
            }
            var parts = ParseFloatArray(value, 3);
            return new Vector3(parts[0], parts[1], parts[2]);
        }

        private static Vector4 ParseVector4(string value)
        {
            if (SkillParamUtil.LooksLikeJsonObject(value))
            {
                var json = ParseJsonObjectFloats(value, new[] { "x", "y", "z", "w" }, new float[] { 0, 0, 0, 0 }, requiredKeyCount: 4);
                return new Vector4(json[0], json[1], json[2], json[3]);
            }
            var parts = ParseFloatArray(value, 4);
            return new Vector4(parts[0], parts[1], parts[2], parts[3]);
        }

        private static Vector2Int ParseVector2Int(string value)
        {
            var parts = ParseIntArray(value, 2);
            return new Vector2Int(parts[0], parts[1]);
        }

        private static Vector3Int ParseVector3Int(string value)
        {
            var parts = ParseIntArray(value, 3);
            return new Vector3Int(parts[0], parts[1], parts[2]);
        }

        private static Quaternion ParseQuaternion(string value)
        {
            // 同时接受欧拉角（3 个值）和四元数（4 个值）。
            var parts = ParseFloatArray(value, -1); // -1 表示长度可变
            if (parts.Length == 3)
                return Quaternion.Euler(parts[0], parts[1], parts[2]);
            if (parts.Length == 4)
                return new Quaternion(parts[0], parts[1], parts[2], parts[3]);
            throw new System.ArgumentException("Quaternion requires 3 (euler) or 4 (xyzw) values");
        }

        private static Color ParseColor(string value)
        {
            // 模块文档一直标注支持的 JSON 对象形式。
            if (SkillParamUtil.LooksLikeJsonObject(value))
            {
                var json = ParseJsonObjectFloats(value, new[] { "r", "g", "b", "a" }, new float[] { 0, 0, 0, 1 }, requiredKeyCount: 3);
                return new Color(json[0], json[1], json[2], json[3]);
            }

            // 十六进制形式。
            if (value.StartsWith("#"))
            {
                if (ColorUtility.TryParseHtmlString(value, out var color))
                    return color;
            }
            
            // 颜色名形式。
            var namedColor = GetNamedColor(value);
            if (namedColor.HasValue)
                return namedColor.Value;

            // 逗号分隔的浮点数形式。
            var parts = ParseFloatArray(value, -1);
            if (parts.Length == 3)
                return new Color(parts[0], parts[1], parts[2], 1);
            if (parts.Length == 4)
                return new Color(parts[0], parts[1], parts[2], parts[3]);
            throw new System.ArgumentException("Color requires 3-4 float values (0-1) or hex string (#RRGGBB)");
        }

        private static Color32 ParseColor32(string value)
        {
            var color = ParseColor(value);
            return color;
        }

        private static Color? GetNamedColor(string name)
        {
            switch (name.ToLower().Trim())
            {
                case "red": return Color.red;
                case "green": return Color.green;
                case "blue": return Color.blue;
                case "white": return Color.white;
                case "black": return Color.black;
                case "yellow": return Color.yellow;
                case "cyan": return Color.cyan;
                case "magenta": return Color.magenta;
                case "gray": case "grey": return Color.gray;
                case "clear": return Color.clear;
                default: return null;
            }
        }

        private static Rect ParseRect(string value)
        {
            var parts = ParseFloatArray(value, 4);
            return new Rect(parts[0], parts[1], parts[2], parts[3]);
        }

        private static Bounds ParseBounds(string value)
        {
            var parts = ParseFloatArray(value, 6);
            return new Bounds(
                new Vector3(parts[0], parts[1], parts[2]),
                new Vector3(parts[3], parts[4], parts[5]));
        }

        private static LayerMask ParseLayerMask(string value)
        {
            // 先当层名解析，失败再当整数掩码。
            int layer = LayerMask.NameToLayer(value);
            if (layer != -1)
                return 1 << layer;
            if (int.TryParse(value, out var mask))
                return mask;
            throw new System.ArgumentException($"Invalid layer: {value}");
        }

        private static AnimationCurve ParseAnimationCurve(string value)
        {
            value = value.ToLower().Trim();
            switch (value)
            {
                case "linear": return AnimationCurve.Linear(0, 0, 1, 1);
                case "easein": return new AnimationCurve(new Keyframe(0, 0, 0, 0), new Keyframe(1, 1, 2, 0));
                case "easeout": return new AnimationCurve(new Keyframe(0, 0, 0, 2), new Keyframe(1, 1, 0, 0));
                case "easeinout": return AnimationCurve.EaseInOut(0, 0, 1, 1);
                case "constant": return AnimationCurve.Constant(0, 1, 1);
                default: return AnimationCurve.Linear(0, 0, 1, 1);
            }
        }

        /// <summary>
        /// 表示"调用方给的值目标属性接受不了"。有了它，catch 处才能回一个点名出错参数的
        /// 结构化 SEMANTIC_INVALID 错误：转换器内部裸抛的 FormatException 会被归为未分类的
        /// SKILL_ERROR，而路由对它的配套动作是 abort 而非"改值后重试"。
        /// 所有消息一律以 "Invalid" 开头，这样本文件之外只看得到文本的调用方，
        /// 也能通过分类器的首词判定规则得到同一结论。
        /// </summary>
        private sealed class PropertyValueException : System.Exception
        {
            public PropertyValueException(string message) : base(message) { }
        }

        /// <summary>
        /// 把 JSON 对象读成有序的 float 数组：向量用 <c>{"x":1,"y":2,"z":3}</c>，
        /// 颜色用 <c>{"r":1,"g":0,"b":0,"a":1}</c>。这是模块文档一直标注的形式。
        ///
        /// <para>键名大小写不敏感、顺序任意，但前 <paramref name="requiredKeyCount"/> 个必须齐全：
        /// 向量不允许只给一部分——<c>{"y":2}</c> 若被当成 <c>(0, 2, 0)</c> 并报成功，
        /// 就会在调用方从未提及的两个轴上移动对象，看起来像瞬移。超出该数量的键可以省略并沿用
        /// 默认值，颜色不给 "a" 仍保持不透明就是靠这一条。无法识别的键按错误处理而不是静默跳过：
        /// 否则 {"x":1,"why":2} 会让 y 留在 0 却报成功。</para>
        /// </summary>
        private static float[] ParseJsonObjectFloats(string value, string[] keys, float[] defaults, int requiredKeyCount)
        {
            JObject obj;
            try
            {
                obj = JObject.Parse(value);
            }
            catch (JsonException ex)
            {
                throw new PropertyValueException(
                    $"Invalid JSON object '{value}': {ex.Message}. Expected {{{string.Join(", ", keys.Select(k => $"\"{k}\": <number>"))}}}.");
            }

            var result = (float[])defaults.Clone();
            var supplied = new bool[keys.Length];

            foreach (var property in obj.Properties())
            {
                int index = System.Array.FindIndex(keys,
                    k => string.Equals(k, property.Name, System.StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                    throw new PropertyValueException(
                        $"Invalid key '{property.Name}'. Expected only: {string.Join(", ", keys)}.");

                if (property.Value == null || property.Value.Type == JTokenType.Null)
                    continue;

                try
                {
                    result[index] = property.Value.Value<float>();
                }
                catch (System.Exception ex)
                {
                    throw new PropertyValueException(
                        $"Invalid value for key '{property.Name}': " +
                        $"{property.Value.ToString(Formatting.None)} is not a number ({ex.Message}).");
                }
                supplied[index] = true;
            }

            var missing = new List<string>();
            for (int i = 0; i < requiredKeyCount && i < keys.Length; i++)
            {
                if (!supplied[i]) missing.Add(keys[i]);
            }

            if (missing.Count > 0)
            {
                var required = string.Join(", ", keys.Take(requiredKeyCount));
                var optional = requiredKeyCount < keys.Length
                    ? $" Optional: {string.Join(", ", keys.Skip(requiredKeyCount))}."
                    : string.Empty;
                throw new PropertyValueException(
                    $"Invalid value '{value}': missing required key(s) {string.Join(", ", missing)}. " +
                    $"All of these are required: {required}.{optional}");
            }

            return result;
        }

        private static float[] ParseFloatArray(string value, int expectedCount)
        {
            value = value.Trim('(', ')', '[', ']', '{', '}');
            var parts = value.Split(new[] { ',', ' ', ';' }, System.StringSplitOptions.RemoveEmptyEntries);
            
            if (expectedCount > 0 && parts.Length != expectedCount)
                throw new System.ArgumentException($"Expected {expectedCount} values, got {parts.Length}");
            
            return parts.Select(p => float.Parse(p.Trim(), CultureInfo.InvariantCulture)).ToArray();
        }

        private static int[] ParseIntArray(string value, int expectedCount)
        {
            value = value.Trim('(', ')', '[', ']', '{', '}');
            var parts = value.Split(new[] { ',', ' ', ';' }, System.StringSplitOptions.RemoveEmptyEntries);
            
            if (expectedCount > 0 && parts.Length != expectedCount)
                throw new System.ArgumentException($"Expected {expectedCount} values, got {parts.Length}");
            
            // 与上面浮点路径一样必须用 InvariantCulture：小数点为逗号的编辑器区域设置下，
            // "1.000" 里的点会被当成千分位分隔符，值就变了。
            return parts.Select(p => int.Parse(p.Trim(), CultureInfo.InvariantCulture)).ToArray();
        }

        #endregion

        #region Reference Resolution

        /// <summary>
        /// 按 path 或 name 解析场景内的 Unity 对象引用，支持 Transform、GameObject 与组件。
        /// </summary>
        private static object ResolveReference(System.Type targetType, string referencePath, string referenceName)
        {
            // 统一走 GameObjectFinder，其内部优先 path 后 name。
            GameObject targetGo = GameObjectFinder.Find(name: referenceName, path: referencePath);

            if (targetGo == null)
                return null;

            if (targetType == typeof(Transform))
                return targetGo.transform;
            if (targetType == typeof(GameObject))
                return targetGo;
            if (typeof(Component).IsAssignableFrom(targetType))
                return targetGo.GetComponent(targetType);

            return null;
        }

        /// <summary>
        /// 按资源路径解析工程资源引用，支持任意 UnityEngine.Object：
        /// ScriptableObject、Prefab（GameObject）、Material、Texture、AudioClip 等。
        /// </summary>
        private static object ResolveAssetReference(System.Type targetType, string assetPath)
        {
            // 先按目标类型精确加载。
            var asset = AssetDatabase.LoadAssetAtPath(assetPath, targetType);
            if (asset != null) return asset;

            // 兜底：按通用 Object 加载后再判断是否可赋值。
            asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset != null && targetType.IsAssignableFrom(asset.GetType()))
                return asset;

            return null;
        }

        #endregion

        #region Helpers

        /// <summary>
        /// 属性值对外报告的形式。所有数字都过一遍可无损往返的文化不变格式化器：
        /// 直接字符串插值会截断（0.192156866 报成 0.1921569，写回去就是另一个颜色），
        /// 且会跟随编辑器区域设置——小数点为逗号时输出 "(0,5, 1, 1)"，
        /// 这种串没有调用方能解析回向量。
        /// </summary>
        private static string FormatValue(object val)
        {
            if (val == null) return "null";
            if (val is Vector2 v2) return SkillParamUtil.FormatVector2(v2);
            if (val is Vector3 v3) return SkillParamUtil.FormatVector3(v3);
            if (val is Vector4 v4) return SkillParamUtil.FormatVector4(v4);
            if (val is Quaternion q) return SkillParamUtil.FormatVector3(q.eulerAngles);
            if (val is Color c) return SkillParamUtil.FormatColor(c);
            if (val is UnityEngine.Object obj) return obj.name;
            return SkillParamUtil.FormatScalarR(val);
        }

        /// <summary>
        /// 按名查找属性或字段（带缓存）：先精确匹配，再退回大小写不敏感匹配。
        /// </summary>
        private static (PropertyInfo prop, FieldInfo field) FindMember(System.Type type, string memberName)
        {
            var cacheKey = $"{type.FullName}:{memberName}";
            if (_memberCache.TryGetValue(cacheKey, out var cached))
                return cached;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var prop = type.GetProperty(memberName, flags);
            var field = type.GetField(memberName, flags);

            if (prop == null && field == null)
            {
                prop = type.GetProperties(flags)
                    .FirstOrDefault(p => p.Name.Equals(memberName, System.StringComparison.OrdinalIgnoreCase));
                field = type.GetFields(flags)
                    .FirstOrDefault(f => f.Name.Equals(memberName, System.StringComparison.OrdinalIgnoreCase));
            }

            var result = (prop, field);
            if (_memberCache.Count > 500) _memberCache.Clear();
            _memberCache[cacheKey] = result;
            return result;
        }

        private static string[] GetAvailableProperties(System.Type type)
        {
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite)
                .Select(p => $"{p.Name} ({p.PropertyType.Name})")
                .Take(20);
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(f => $"{f.Name} ({f.FieldType.Name})")
                .Take(20);
            return props.Concat(fields).ToArray();
        }

        private static Dictionary<string, object> GetComponentPropertiesSummary(Component c)
        {
            var result = new Dictionary<string, object>();
            var type = c.GetType();
            
            // 按组件类型只挑关键属性输出，避免把整个 Inspector 都倒出来。
            if (c is Transform t)
            {
                result["position"] = FormatValue(t.position);
                result["rotation"] = FormatValue(t.rotation);
                result["scale"] = FormatValue(t.localScale);
            }
            else if (c is RectTransform rt)
            {
                result["anchoredPosition"] = FormatValue(rt.anchoredPosition);
                result["sizeDelta"] = FormatValue(rt.sizeDelta);
            }
            else if (c is Camera cam)
            {
                result["fieldOfView"] = cam.fieldOfView;
                result["orthographic"] = cam.orthographic;
            }

            return result;
        }

        #endregion

        [UnitySkill("component_copy", "Copy a component from one GameObject to another",
            Category = SkillCategory.Component, Operation = SkillOperation.Create,
            Tags = new[] { "copy", "paste", "duplicate", "transfer" },
            Outputs = new[] { "source", "target", "componentType" },
            RequiresInput = new[] { "gameObject", "component" },
            TracksWorkflow = true)]
        public static object ComponentCopy(string sourceName = null, int sourceInstanceId = 0, string sourcePath = null, string targetName = null, int targetInstanceId = 0, string targetPath = null, string componentType = null)
        {
            if (Validate.Required(componentType, "componentType") is object err) return err;
            var (srcGo, srcErr) = GameObjectFinder.FindOrError(name: sourceName, instanceId: sourceInstanceId, path: sourcePath);
            if (srcErr != null) return srcErr;
            var (dstGo, dstErr) = GameObjectFinder.FindOrError(name: targetName, instanceId: targetInstanceId, path: targetPath);
            if (dstErr != null) return dstErr;

            var type = FindComponentType(componentType);
            if (type == null) return new { error = $"Component type not found: {componentType}" };

            var srcComp = srcGo.GetComponent(type);
            if (srcComp == null) return new { error = $"No {componentType} on {sourceName}" };

            UnityEditorInternal.ComponentUtility.CopyComponent(srcComp);
            UnityEditorInternal.ComponentUtility.PasteComponentAsNew(dstGo);
            return new { success = true, source = sourceName, target = targetName, componentType };
        }

        [UnitySkill("component_copy_exact", "Copy a component from one GameObject to another and verify every serialized Inspector field matches after paste",
            Category = SkillCategory.Component, Operation = SkillOperation.Create,
            Tags = new[] { "copy", "paste", "duplicate", "serialized", "exact" },
            Outputs = new[] { "source", "target", "componentType", "verified", "mismatchCount" },
            RequiresInput = new[] { "gameObject", "component" },
            TracksWorkflow = true)]
        public static object ComponentCopyExact(string sourceName = null, int sourceInstanceId = 0, string sourcePath = null, string targetName = null, int targetInstanceId = 0, string targetPath = null, string componentType = null)
        {
            if (Validate.Required(componentType, "componentType") is object err) return err;
            var (srcGo, srcErr) = GameObjectFinder.FindOrError(name: sourceName, instanceId: sourceInstanceId, path: sourcePath);
            if (srcErr != null) return srcErr;
            var (dstGo, dstErr) = GameObjectFinder.FindOrError(name: targetName, instanceId: targetInstanceId, path: targetPath);
            if (dstErr != null) return dstErr;

            var type = FindComponentType(componentType);
            if (type == null) return new { error = $"Component type not found: {componentType}" };

            var srcComp = srcGo.GetComponent(type);
            if (srcComp == null) return new { error = $"No {componentType} on {srcGo.name}" };

            var before = new HashSet<Component>(dstGo.GetComponents(type).OfType<Component>());

            WorkflowManager.SnapshotObject(dstGo);
            Undo.RegisterCompleteObjectUndo(dstGo, "Copy Component Exact");

            UnityEditorInternal.ComponentUtility.CopyComponent(srcComp);
            if (!UnityEditorInternal.ComponentUtility.PasteComponentAsNew(dstGo))
            {
                return new { error = $"Failed to paste component as new: {componentType}" };
            }

            var copied = dstGo.GetComponents(type).OfType<Component>().FirstOrDefault(c => !before.Contains(c));
            if (copied == null)
            {
                return new { error = $"Could not locate copied component after paste: {componentType}" };
            }

            Undo.RegisterCreatedObjectUndo(copied, "Copy Component Exact");
            WorkflowManager.SnapshotObject(copied, SnapshotType.Created);
            EditorUtility.SetDirty(copied);

            var mismatches = SerializedPropertySkillUtility.CompareSerializedProperties(srcComp, copied);
            if (mismatches.Count > 0)
            {
                return new
                {
                    success = false,
                    source = srcGo.name,
                    target = dstGo.name,
                    componentType,
                    verified = false,
                    mismatchCount = mismatches.Count,
                    mismatches = mismatches.ToArray()
                };
            }

            return new
            {
                success = true,
                source = srcGo.name,
                target = dstGo.name,
                componentType,
                copiedComponentIndex = System.Array.IndexOf(dstGo.GetComponents(type), copied),
                verified = true,
                mismatchCount = 0
            };
        }

        [UnitySkill("component_set_enabled", "Enable or disable a component (Behaviour, Renderer, Collider, etc.)",
            Category = SkillCategory.Component, Operation = SkillOperation.Modify,
            Tags = new[] { "enable", "disable", "toggle", "active" },
            Outputs = new[] { "gameObject", "componentType", "enabled" },
            RequiresInput = new[] { "gameObject", "component" },
            TracksWorkflow = true)]
        public static object ComponentSetEnabled(string name = null, int instanceId = 0, string path = null, string componentType = null, bool enabled = true)
        {
            if (Validate.Required(componentType, "componentType") is object err) return err;
            var (go, findErr) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (findErr != null) return findErr;

            var type = FindComponentType(componentType);
            if (type == null) return new { error = $"Component type not found: {componentType}" };

            var comp = go.GetComponent(type);
            if (comp == null) return new { error = $"No {componentType} on {go.name}" };

            Undo.RecordObject(comp, "Set Component Enabled");
            if (comp is Behaviour behaviour) behaviour.enabled = enabled;
            else if (comp is Renderer renderer) renderer.enabled = enabled;
            else if (comp is Collider collider) collider.enabled = enabled;
            else return new { error = $"{componentType} does not have an enabled property" };

            return new { success = true, gameObject = go.name, componentType, enabled };
        }
    }
}

// Producer:Betsy
