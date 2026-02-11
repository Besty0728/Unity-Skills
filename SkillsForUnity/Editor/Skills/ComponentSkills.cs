using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// Component management skills - add, remove, get, set properties.
    /// Now supports finding by name, instanceId, or path.
    /// Enhanced with advanced type conversion and reference resolution.
    /// </summary>
    public static class ComponentSkills
    {
        // Cache for component type lookups to improve performance
        private static readonly Dictionary<string, System.Type> _typeCache = new Dictionary<string, System.Type>();
        
        // Common third-party namespaces to search
        private static readonly string[] ExtendedNamespaces = new[]
        {
            // Unity built-in
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
            // Cinemachine (multiple versions)
            "Cinemachine.",
            "Unity.Cinemachine.",
            // TextMeshPro
            "TMPro.",
            // Input System
            "UnityEngine.InputSystem.",
            // XR
            "UnityEngine.XR.",
            "UnityEngine.XR.Interaction.Toolkit.",
            // Common third-party
            "DG.Tweening.",
            "Rewired.",
        };

        [UnitySkill("component_add", "Add a component to a GameObject (supports name/instanceId/path). Works with Cinemachine, TextMeshPro, etc.")]
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

            // Check if component already exists (for single-instance components)
            if (go.GetComponent(type) != null && !AllowMultiple(type))
                return new { 
                    warning = $"Component {type.Name} already exists on {go.name}",
                    gameObject = go.name,
                    instanceId = go.GetInstanceID()
                };

            var comp = Undo.AddComponent(go, type);

            // Record created component for workflow undo if recording
            if (WorkflowManager.IsRecording)
            {
                WorkflowManager.SnapshotCreatedComponent(comp);
            }

            EditorUtility.SetDirty(go);

            return new {
                success = true,
                gameObject = go.name,
                instanceId = go.GetInstanceID(),
                component = type.Name,
                fullTypeName = type.FullName
            };
        }

        [UnitySkill("component_add_batch", "Add components to multiple GameObjects. items: JSON array of {name, componentType, path}")]
        public static object ComponentAddBatch(string items)
        {
            if (string.IsNullOrEmpty(items))
                return new { error = "items parameter is required. Example: [{\"name\":\"Cube1\",\"componentType\":\"Rigidbody\"}]" };

            try
            {
                var itemList = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BatchAddComponentItem>>(items);
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

                        if (string.IsNullOrEmpty(item.componentType))
                        {
                            results.Add(new { target = item.name ?? item.path, success = false, error = "componentType required" });
                            failCount++;
                            continue;
                        }

                        var type = FindComponentType(item.componentType);
                        if (type == null)
                        {
                            results.Add(new { target = item.name ?? item.path, success = false, error = $"Component type not found: {item.componentType}" });
                            failCount++;
                            continue;
                        }

                        // Check if component already exists (for single-instance components)
                        if (go.GetComponent(type) != null && !AllowMultiple(type))
                        {
                            results.Add(new { target = go.name, success = true, warning = "Component already exists", component = type.Name });
                            successCount++; // Count as success but with warning
                            continue;
                        }

                        var comp = Undo.AddComponent(go, type);

                        // Record created component for workflow undo if recording
                        if (WorkflowManager.IsRecording)
                        {
                            WorkflowManager.SnapshotCreatedComponent(comp);
                        }

                        EditorUtility.SetDirty(go);
                        results.Add(new { target = go.name, success = true, component = type.Name });
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

        private class BatchAddComponentItem
        {
            public string name { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public string componentType { get; set; }
        }

        [UnitySkill("component_remove", "Remove a component from a GameObject (supports name/instanceId/path)")]
        public static object ComponentRemove(string name = null, int instanceId = 0, string path = null, string componentType = null, int componentIndex = 0)
        {
            if (Validate.Required(componentType, "componentType") is object err) return err;

            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var type = FindComponentType(componentType);
            if (type == null)
                return new { error = $"Component type not found: {componentType}" };

            // Support removing specific component instance by index
            var components = go.GetComponents(type);
            if (components.Length == 0)
                return new { error = $"Component not found on {go.name}: {componentType}" };

            if (componentIndex >= components.Length)
                return new { error = $"Component index {componentIndex} out of range. Found {components.Length} components of type {componentType}" };

            var comp = components[componentIndex];

            // Check if it's a required component
            var requiredBy = GetRequiredByComponents(go, type);
            if (requiredBy.Any())
                return new {
                    error = $"Cannot remove {componentType} - required by: {string.Join(", ", requiredBy)}",
                    hint = "Remove dependent components first"
                };

            WorkflowManager.SnapshotObject(comp);
            Undo.DestroyObjectImmediate(comp);
            EditorUtility.SetDirty(go);

            return new { success = true, gameObject = go.name, removed = componentType };
        }

        [UnitySkill("component_remove_batch", "Remove components from multiple GameObjects. items: JSON array of {name, componentType, path}")]
        public static object ComponentRemoveBatch(string items)
        {
            if (string.IsNullOrEmpty(items))
                 return new { error = "items parameter is required." };

            try
            {
                var itemList = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BatchRemoveComponentItem>>(items);
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

                        if (string.IsNullOrEmpty(item.componentType))
                        {
                            results.Add(new { target = item.name ?? item.path, success = false, error = "componentType required" });
                            failCount++;
                            continue;
                        }

                        var type = FindComponentType(item.componentType);
                        if (type == null)
                        {
                            results.Add(new { target = item.name ?? item.path, success = false, error = $"Component type not found: {item.componentType}" });
                            failCount++;
                            continue;
                        }

                        var components = go.GetComponents(type);
                        if (components.Length == 0)
                        {
                            results.Add(new { target = go.name, success = false, error = $"Component not found: {item.componentType}" });
                            failCount++;
                            continue;
                        }

                        // Remove all instances of this type? Or just one?
                        // Batch operation typically implies removing ALL of that type unless specified otherwise.
                        // For safety, let's remove the first one mostly, or iterate if needed.
                        // But ComponentRemove (single) removes specific index.
                        // Let's implement removing ALL components of that type for the batch operation to be powerful cleaning tool.

                        Undo.RecordObject(go, "Batch Remove Component");
                        foreach (var c in components)
                        {
                            WorkflowManager.SnapshotObject(c);
                            Undo.DestroyObjectImmediate(c);
                        }
                        
                        EditorUtility.SetDirty(go);
                        results.Add(new { target = go.name, success = true, removed = type.Name, count = components.Length });
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

        private class BatchRemoveComponentItem
        {
            public string name { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public string componentType { get; set; }
        }

        [UnitySkill("component_list", "List all components on a GameObject with detailed info (supports name/instanceId/path)")]
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
                        { "enabled", (c as Behaviour)?.enabled ?? true }
                    };
                    
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
                instanceId = go.GetInstanceID(), 
                path = GameObjectFinder.GetPath(go), 
                componentCount = components.Length,
                components 
            };
        }

        [UnitySkill("component_set_property", "Set a property/field on a component. Supports Vector2/3/4, Color, references by name/path")]
        public static object ComponentSetProperty(
            string name = null, int instanceId = 0, string path = null, 
            string componentType = null, string propertyName = null, 
            string value = null, string referencePath = null, string referenceName = null)
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

            // Find property or field (including private with SerializeField)
            var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var field = type.GetField(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (prop == null && field == null)
            {
                // Try case-insensitive search
                prop = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(p => p.Name.Equals(propertyName, System.StringComparison.OrdinalIgnoreCase));
                field = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(f => f.Name.Equals(propertyName, System.StringComparison.OrdinalIgnoreCase));
            }

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

                // Handle reference types (Transform, GameObject, Component references)
                if (!string.IsNullOrEmpty(referencePath) || !string.IsNullOrEmpty(referenceName))
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
                    valueSet = converted?.ToString() ?? "null",
                    valueType = targetType.Name
                };
            }
            catch (System.Exception ex)
            {
                return new { 
                    error = ex.Message,
                };
            }
        }

        [UnitySkill("component_set_property_batch", "Set properties on multiple components (Efficient). items: JSON array of {name, componentType, propertyName, value, referencePath, referenceName}")]
        public static object ComponentSetPropertyBatch(string items)
        {
            if (string.IsNullOrEmpty(items))
                return new { error = "items parameter is required. Example: [{\"name\":\"Cube1\",\"componentType\":\"Light\",\"propertyName\":\"intensity\",\"value\":5.0}]" };

            try
            {
                var itemList = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BatchSetPropertyItem>>(items);
                if (itemList == null || itemList.Count == 0)
                    return new { error = "items parameter is empty or invalid JSON" };

                var results = new List<object>();
                int successCount = 0;
                int failCount = 0;

                foreach (var item in itemList)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(item.componentType) || string.IsNullOrEmpty(item.propertyName))
                        {
                            results.Add(new { target = item.name ?? item.path, success = false, error = "componentType and propertyName required" });
                            failCount++;
                            continue;
                        }

                        var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path);
                        if (error != null)
                        {
                            results.Add(new { target = item.name ?? item.path, success = false, error = "Object not found" });
                            failCount++;
                            continue;
                        }

                        var type = FindComponentType(item.componentType);
                        if (type == null)
                        {
                            results.Add(new { target = item.name ?? item.path, success = false, error = $"Component type not found: {item.componentType}" });
                            failCount++;
                            continue;
                        }
                        
                        var comp = go.GetComponent(type);
                        if (comp == null)
                        {
                            results.Add(new { target = item.name ?? item.path, success = false, error = $"Component not found: {item.componentType}" });
                            failCount++;
                            continue;
                        }

                        // Find property or field
                        var prop = type.GetProperty(item.propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        var field = type.GetField(item.propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                        // Case-insensitive fallback
                        if (prop == null && field == null)
                        {
                            prop = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                .FirstOrDefault(p => p.Name.Equals(item.propertyName, System.StringComparison.OrdinalIgnoreCase));
                            field = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                .FirstOrDefault(f => f.Name.Equals(item.propertyName, System.StringComparison.OrdinalIgnoreCase));
                        }

                        if (prop == null && field == null)
                        {
                            results.Add(new { target = item.name ?? item.path, success = false, error = $"Property/field not found: {item.propertyName}" });
                            failCount++;
                            continue;
                        }

                        WorkflowManager.SnapshotObject(comp);
                        Undo.RecordObject(comp, "Batch Set Property");

                        var targetType = prop?.PropertyType ?? field.FieldType;
                        object converted;

                        // Handle reference types
                        if (!string.IsNullOrEmpty(item.referencePath) || !string.IsNullOrEmpty(item.referenceName))
                        {
                            converted = ResolveReference(targetType, item.referencePath, item.referenceName);
                            if (converted == null)
                            {
                                results.Add(new { target = item.name ?? item.path, success = false, error = $"Reference resolution failed for {item.propertyName}" });
                                failCount++;
                                continue;
                            }
                        }
                        else
                        {
                            // Handle primitive value conversion
                            var valStr = item.value?.ToString();
                            try {
                                converted = ConvertValue(valStr, targetType);
                            } catch (System.Exception ex) {
                                results.Add(new { target = item.name ?? item.path, success = false, error = $"Conversion error: {ex.Message}" });
                                failCount++;
                                continue;
                            }
                        }

                        if (prop != null && prop.CanWrite)
                            prop.SetValue(comp, converted);
                        else if (field != null)
                            field.SetValue(comp, converted);
                        else
                        {
                            results.Add(new { target = item.name ?? item.path, success = false, error = $"Property {item.propertyName} is read-only" });
                            failCount++;
                            continue;
                        }

                        EditorUtility.SetDirty(comp);
                        results.Add(new { target = go.name, success = true, property = item.propertyName });
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
        }

        [UnitySkill("component_get_properties", "Get all properties of a component (supports name/instanceId/path)")]
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
                        var val = p.GetValue(comp);
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

        #region Type Finding (Enhanced for Third-Party)
        
        /// <summary>
        /// Find component type with extensive namespace search.
        /// Supports Cinemachine, TextMeshPro, and other common plugins.
        /// </summary>
        public static System.Type FindComponentType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            
            // Check cache first
            if (_typeCache.TryGetValue(name, out var cached))
                return cached;

            System.Type result = null;
            
            // 1. Try exact type name (might be full namespace)
            result = System.Type.GetType(name);
            if (result != null && typeof(Component).IsAssignableFrom(result))
            {
                _typeCache[name] = result;
                return result;
            }
            
            // 2. Extract simple name
            var simpleName = name.Contains(".") ? name.Substring(name.LastIndexOf('.') + 1) : name;
            
            // 3. Try common namespaces
            foreach (var ns in ExtendedNamespaces)
            {
                result = TryGetTypeFromAssemblies(ns + simpleName);
                if (result != null && typeof(Component).IsAssignableFrom(result))
                {
                    _typeCache[name] = result;
                    return result;
                }
            }
            
            // 4. Search all loaded assemblies by simple name (slowest but most comprehensive)
            result = System.AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return new System.Type[0]; } })
                .FirstOrDefault(t => 
                    (t.Name.Equals(simpleName, System.StringComparison.OrdinalIgnoreCase) || 
                     t.FullName == name) && 
                    typeof(Component).IsAssignableFrom(t));

            if (result != null)
                _typeCache[name] = result;
                
            return result;
        }

        private static System.Type TryGetTypeFromAssemblies(string fullName)
        {
            // Try common assembly names
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
                catch { }
            }
            return null;
        }

        private static string[] GetSimilarTypes(string searchTerm)
        {
            var simpleName = searchTerm.Contains(".") ? searchTerm.Substring(searchTerm.LastIndexOf('.') + 1) : searchTerm;
            
            return System.AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return new System.Type[0]; } })
                .Where(t => typeof(Component).IsAssignableFrom(t) && 
                           t.Name.Contains(simpleName, System.StringComparison.OrdinalIgnoreCase))
                .Take(10)
                .Select(t => t.FullName)
                .ToArray();
        }

        private static bool AllowMultiple(System.Type type)
        {
            return type.GetCustomAttributes(typeof(DisallowMultipleComponent), true).Length == 0;
        }

        private static string[] GetRequiredByComponents(GameObject go, System.Type targetType)
        {
            return go.GetComponents<Component>()
                .Where(c => c != null && c.GetType() != targetType)
                .Where(c => c.GetType().GetCustomAttributes(typeof(RequireComponent), true)
                    .OfType<RequireComponent>()
                    .Any(r => r.m_Type0 == targetType || r.m_Type1 == targetType || r.m_Type2 == targetType))
                .Select(c => c.GetType().Name)
                .ToArray();
        }
        
        #endregion

        #region Value Conversion (Enhanced)

        /// <summary>
        /// Convert string value to target type with extensive support.
        /// </summary>
        private static object ConvertValue(string value, System.Type targetType)
        {
            if (value == null || value.Equals("null", System.StringComparison.OrdinalIgnoreCase))
                return null;

            // Primitives
            if (targetType == typeof(string)) return value;
            if (targetType == typeof(int)) return int.Parse(value);
            if (targetType == typeof(float)) return float.Parse(value);
            if (targetType == typeof(double)) return double.Parse(value);
            if (targetType == typeof(bool)) return ParseBool(value);
            if (targetType == typeof(long)) return long.Parse(value);
            
            // Unity Vector types
            if (targetType == typeof(Vector2)) return ParseVector2(value);
            if (targetType == typeof(Vector3)) return ParseVector3(value);
            if (targetType == typeof(Vector4)) return ParseVector4(value);
            if (targetType == typeof(Vector2Int)) return ParseVector2Int(value);
            if (targetType == typeof(Vector3Int)) return ParseVector3Int(value);
            
            // Unity other types
            if (targetType == typeof(Quaternion)) return ParseQuaternion(value);
            if (targetType == typeof(Color)) return ParseColor(value);
            if (targetType == typeof(Color32)) return ParseColor32(value);
            if (targetType == typeof(Rect)) return ParseRect(value);
            if (targetType == typeof(Bounds)) return ParseBounds(value);
            if (targetType == typeof(LayerMask)) return ParseLayerMask(value);
            
            // Enums
            if (targetType.IsEnum)
                return System.Enum.Parse(targetType, value, true);

            // AnimationCurve (simple format)
            if (targetType == typeof(AnimationCurve))
                return ParseAnimationCurve(value);

            // Fallback
            return System.Convert.ChangeType(value, targetType);
        }

        private static bool ParseBool(string value)
        {
            value = value.ToLower().Trim();
            return value == "true" || value == "1" || value == "yes" || value == "on";
        }

        private static Vector2 ParseVector2(string value)
        {
            var parts = ParseFloatArray(value, 2);
            return new Vector2(parts[0], parts[1]);
        }

        private static Vector3 ParseVector3(string value)
        {
            var parts = ParseFloatArray(value, 3);
            return new Vector3(parts[0], parts[1], parts[2]);
        }

        private static Vector4 ParseVector4(string value)
        {
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
            // Support both euler angles (3 values) and quaternion (4 values)
            var parts = ParseFloatArray(value, -1); // -1 means variable length
            if (parts.Length == 3)
                return Quaternion.Euler(parts[0], parts[1], parts[2]);
            if (parts.Length == 4)
                return new Quaternion(parts[0], parts[1], parts[2], parts[3]);
            throw new System.ArgumentException("Quaternion requires 3 (euler) or 4 (xyzw) values");
        }

        private static Color ParseColor(string value)
        {
            // Support hex format
            if (value.StartsWith("#"))
            {
                if (ColorUtility.TryParseHtmlString(value, out var color))
                    return color;
            }
            
            // Support named colors
            var namedColor = GetNamedColor(value);
            if (namedColor.HasValue)
                return namedColor.Value;

            // Support float values
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
            // Try as layer name first
            int layer = LayerMask.NameToLayer(value);
            if (layer != -1)
                return 1 << layer;
            // Try as integer
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
                case "easein": return AnimationCurve.EaseInOut(0, 0, 1, 1);
                case "easeout": return AnimationCurve.EaseInOut(0, 0, 1, 1);
                case "constant": return AnimationCurve.Constant(0, 1, 1);
                default: return AnimationCurve.Linear(0, 0, 1, 1);
            }
        }

        private static float[] ParseFloatArray(string value, int expectedCount)
        {
            // Remove parentheses and brackets
            value = value.Trim('(', ')', '[', ']', '{', '}');
            var parts = value.Split(new[] { ',', ' ', ';' }, System.StringSplitOptions.RemoveEmptyEntries);
            
            if (expectedCount > 0 && parts.Length != expectedCount)
                throw new System.ArgumentException($"Expected {expectedCount} values, got {parts.Length}");
            
            return parts.Select(p => float.Parse(p.Trim())).ToArray();
        }

        private static int[] ParseIntArray(string value, int expectedCount)
        {
            value = value.Trim('(', ')', '[', ']', '{', '}');
            var parts = value.Split(new[] { ',', ' ', ';' }, System.StringSplitOptions.RemoveEmptyEntries);
            
            if (expectedCount > 0 && parts.Length != expectedCount)
                throw new System.ArgumentException($"Expected {expectedCount} values, got {parts.Length}");
            
            return parts.Select(p => int.Parse(p.Trim())).ToArray();
        }

        #endregion

        #region Reference Resolution

        /// <summary>
        /// Resolve a reference to a Unity Object by path or name.
        /// Supports Transform, GameObject, and Component references.
        /// </summary>
        private static object ResolveReference(System.Type targetType, string referencePath, string referenceName)
        {
            GameObject targetGo = null;

            // Find the target GameObject
            if (!string.IsNullOrEmpty(referencePath))
                targetGo = GameObjectFinder.FindByPath(referencePath);
            else if (!string.IsNullOrEmpty(referenceName))
                targetGo = GameObject.Find(referenceName);

            if (targetGo == null)
                return null;

            // Return appropriate type
            if (targetType == typeof(Transform))
                return targetGo.transform;
            if (targetType == typeof(GameObject))
                return targetGo;
            if (typeof(Component).IsAssignableFrom(targetType))
                return targetGo.GetComponent(targetType);

            return null;
        }

        #endregion

        #region Helpers

        private static string FormatValue(object val)
        {
            if (val == null) return "null";
            if (val is Vector2 v2) return $"({v2.x}, {v2.y})";
            if (val is Vector3 v3) return $"({v3.x}, {v3.y}, {v3.z})";
            if (val is Vector4 v4) return $"({v4.x}, {v4.y}, {v4.z}, {v4.w})";
            if (val is Quaternion q) return $"({q.eulerAngles.x}, {q.eulerAngles.y}, {q.eulerAngles.z})";
            if (val is Color c) return $"({c.r}, {c.g}, {c.b}, {c.a})";
            if (val is UnityEngine.Object obj) return obj.name;
            return val.ToString();
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

        private static string GetTypeConversionHint(System.Type type)
        {
            if (type == typeof(Vector2)) return "Use format: x,y (e.g., '100,50')";
            if (type == typeof(Vector3)) return "Use format: x,y,z (e.g., '1,2,3')";
            if (type == typeof(Vector4)) return "Use format: x,y,z,w (e.g., '1,2,3,4')";
            if (type == typeof(Color)) return "Use format: r,g,b,a (0-1) or hex (#RRGGBB) or name (red, blue, etc.)";
            if (type == typeof(Quaternion)) return "Use euler angles: x,y,z (e.g., '0,90,0')";
            if (typeof(Component).IsAssignableFrom(type) || type == typeof(Transform) || type == typeof(GameObject))
                return "Use referencePath or referenceName parameter to set object references";
            return null;
        }

        private static Dictionary<string, object> GetComponentPropertiesSummary(Component c)
        {
            var result = new Dictionary<string, object>();
            var type = c.GetType();
            
            // Get key properties based on component type
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
    }
}
