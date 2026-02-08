using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Collections;

namespace UnitySkills
{
    /// <summary>
    /// Smart Agentic Skills for advanced scene querying, layout, and auto-binding.
    /// Designed to give AI "reasoning" and "practical design" capabilities.
    /// </summary>
    public static class SmartSkills
    {
        // ==================================================================================
        // 1. Smart Query ("The SQL for Unity Scene")
        // ==================================================================================

        [UnitySkill("smart_scene_query", "Query objects by component property (params: componentName, propertyName, op, value). e.g. componentName='Light', propertyName='intensity', op='>', value='10'")]
        public static object SmartSceneQuery(
            string componentName, 
            string propertyName, 
            string op = "==",       // ==, !=, >, <, >=, <=, contains
            string value = null, 
            int limit = 50)
        {
            var results = new List<object>();
            
            // Resolve Type
            var type = GetTypeByName(componentName);
            if (type == null) 
                return new { success = false, error = $"Component type '{componentName}' not found. Try: Light, MeshRenderer, Camera, etc." };

            var components = Object.FindObjectsOfType(type);
            
            foreach (var comp in components)
            {
                if (results.Count >= limit) break;

                var val = GetMemberValue(comp, propertyName);
                if (val == null) continue;

                if (Compare(val, op, value))
                {
                    var go = (comp as Component).gameObject;
                    results.Add(new 
                    {
                        name = go.name,
                        instanceId = go.GetInstanceID(),
                        path = GameObjectFinder.GetPath(go),
                        propertyValue = FormatValue(val)
                    });
                }
            }

            return new 
            { 
                success = true, 
                count = results.Count, 
                query = $"{componentName}.{propertyName} {op} {value}",
                results
            };
        }

        // ==================================================================================
        // 2. Smart Layout ("The Automated Designer")
        // ==================================================================================

        [UnitySkill("smart_scene_layout", "Organize selected objects into a layout (Linear, Grid, Circle, Arc)")]
        public static object SmartSceneLayout(
            string layoutType = "Linear",   // Linear, Grid, Circle, Arc
            string axis = "X",              // X, Y, Z for Linear; ignored for Circle
            float spacing = 2.0f,           // Distance between items (or radius for Circle)
            int columns = 3,                // For Grid layout
            float arcAngle = 180f,          // For Arc layout (degrees)
            bool lookAtCenter = false)      // For Circle/Arc: rotate to face center
        {
            var selected = Selection.gameObjects.OrderBy(g => g.transform.GetSiblingIndex()).ToList();
            if (selected.Count == 0) 
                return new { success = false, error = "No GameObjects selected. Select objects in Hierarchy first." };

            // Workflow 支持
            foreach (var go in selected)
                WorkflowManager.SnapshotObject(go.transform);

            Undo.RecordObjects(selected.Select(g => g.transform).ToArray(), "Smart Layout");

            var startPos = selected[0].transform.position;
            Vector3 axisVec = ParseAxis(axis);

            for (int i = 0; i < selected.Count; i++)
            {
                Vector3 newPos = startPos;
                
                switch (layoutType.ToLower())
                {
                    case "linear":
                        newPos = startPos + axisVec * (i * spacing);
                        break;

                    case "grid":
                        int row = i / columns;
                        int col = i % columns;
                        // Grid on XZ plane by default
                        newPos = startPos + new Vector3(col * spacing, 0, -row * spacing); 
                        break;

                    case "circle":
                        float angle = i * (360f / selected.Count);
                        Vector3 offset = Quaternion.Euler(0, angle, 0) * (Vector3.forward * spacing);
                        newPos = startPos + offset;
                        break;

                    case "arc":
                        float startAngle = -arcAngle / 2f;
                        float stepAngle = selected.Count > 1 ? arcAngle / (selected.Count - 1) : 0;
                        float currentAngle = startAngle + stepAngle * i;
                        Vector3 arcOffset = Quaternion.Euler(0, currentAngle, 0) * (Vector3.forward * spacing);
                        newPos = startPos + arcOffset;
                        break;
                }
                
                selected[i].transform.position = newPos;
                
                if (lookAtCenter && (layoutType.ToLower() == "circle" || layoutType.ToLower() == "arc"))
                {
                    selected[i].transform.LookAt(startPos);
                }
            }

            return new { success = true, layout = layoutType, count = selected.Count, spacing };
        }

        // ==================================================================================
        // 3. Smart Binder ("The Auto-Wiring Engineer")
        // ==================================================================================

        [UnitySkill("smart_reference_bind", "Auto-fill a List/Array field with objects matching tag or name pattern")]
        public static object SmartReferenceBind(
            string targetName,          // Target GameObject name
            string componentName,       // Component on target
            string fieldName,           // Field to fill
            string sourceTag = null,    // Find by tag
            string sourceName = null,   // Find by name contains
            bool appendMode = false)    // If true, append to existing; if false, replace
        {
            // 1. Find Target
            var targetGo = GameObject.Find(targetName);
            if (targetGo == null) 
                return new { success = false, error = $"Target '{targetName}' not found" };

            var comp = targetGo.GetComponent(componentName);
            if (comp == null) 
                return new { success = false, error = $"Component '{componentName}' not found on target" };

            // 2. Find Member
            var type = comp.GetType();
            var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            
            if (field == null) 
                return new { success = false, error = $"Field '{fieldName}' not found on {componentName}" };

            // 3. Find Source Objects
            var sources = new List<GameObject>();
            if (!string.IsNullOrEmpty(sourceTag))
            {
                try { sources.AddRange(GameObject.FindGameObjectsWithTag(sourceTag)); }
                catch { return new { success = false, error = $"Tag '{sourceTag}' does not exist" }; }
            }
            if (!string.IsNullOrEmpty(sourceName))
            {
                sources.AddRange(Object.FindObjectsOfType<GameObject>().Where(g => g.name.Contains(sourceName)));
            }
            sources = sources.Distinct().ToList();

            if (sources.Count == 0) 
                return new { success = false, error = "No source objects found matching criteria" };

            // 4. Validate field type
            var fieldType = field.FieldType;
            bool isList = fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(List<>);
            bool isArray = fieldType.IsArray;

            if (!isList && !isArray)
                return new { success = false, error = $"Field '{fieldName}' is not a List<> or Array type" };

            WorkflowManager.SnapshotObject(comp);
            Undo.RecordObject(comp, "Smart Bind");

            // Element Type
            var elementType = isArray ? fieldType.GetElementType() : fieldType.GetGenericArguments()[0];

            // Convert GameObjects to ElementType
            var convertedList = new ArrayList();
            
            // Append mode: start with existing items
            if (appendMode)
            {
                var existing = field.GetValue(comp) as IEnumerable;
                if (existing != null)
                {
                    foreach (var item in existing) convertedList.Add(item);
                }
            }
            
            foreach (var src in sources)
            {
                if (elementType == typeof(GameObject))
                {
                    if (!convertedList.Contains(src)) convertedList.Add(src);
                }
                else if (typeof(Component).IsAssignableFrom(elementType))
                {
                    var c = src.GetComponent(elementType);
                    if (c != null && !convertedList.Contains(c)) convertedList.Add(c);
                }
                else if (elementType == typeof(Transform))
                {
                    if (!convertedList.Contains(src.transform)) convertedList.Add(src.transform);
                }
            }

            // Set value
            if (isArray)
            {
                var array = System.Array.CreateInstance(elementType, convertedList.Count);
                convertedList.CopyTo(array);
                field.SetValue(comp, array);
            }
            else
            {
                var list = System.Activator.CreateInstance(fieldType) as IList;
                foreach (var item in convertedList) list.Add(item);
                field.SetValue(comp, list);
            }

            EditorUtility.SetDirty(comp);

            return new { success = true, boundCount = convertedList.Count, field = fieldName, appendMode };
        }

        // ==================================================================================
        // Helpers
        // ==================================================================================

        private static System.Type GetTypeByName(string name)
        {
            // Fast path: common Unity types
            var common = new Dictionary<string, System.Type>(System.StringComparer.OrdinalIgnoreCase)
            {
                {"Light", typeof(Light)},
                {"Camera", typeof(Camera)},
                {"MeshRenderer", typeof(MeshRenderer)},
                {"MeshFilter", typeof(MeshFilter)},
                {"BoxCollider", typeof(BoxCollider)},
                {"SphereCollider", typeof(SphereCollider)},
                {"Rigidbody", typeof(Rigidbody)},
                {"AudioSource", typeof(AudioSource)},
                {"Animator", typeof(Animator)},
                {"Transform", typeof(Transform)},
            };
            
            if (common.TryGetValue(name, out var t)) return t;

            // Slow path: reflection
            return System.AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return new System.Type[0]; } })
                .FirstOrDefault(type => type.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase));
        }

        private static object GetMemberValue(object obj, string memberName)
        {
            var type = obj.GetType();
            var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) return field.GetValue(obj);

            var prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanRead) return prop.GetValue(obj);
            
            return null;
        }

        private static bool Compare(object val, string op, string target)
        {
            if (val == null) return false;
            
            try
            {
                string valStr = val.ToString();
                
                // Boolean special case
                if (val is bool b)
                {
                    bool targetBool = target?.ToLower() == "true";
                    return op == "==" ? b == targetBool : b != targetBool;
                }
                
                // Numeric comparison
                if (double.TryParse(valStr, out double vNum) && double.TryParse(target, out double tNum))
                {
                    switch (op)
                    {
                        case "==": return System.Math.Abs(vNum - tNum) < 0.0001;
                        case "!=": return System.Math.Abs(vNum - tNum) >= 0.0001;
                        case ">": return vNum > tNum;
                        case "<": return vNum < tNum;
                        case ">=": return vNum >= tNum;
                        case "<=": return vNum <= tNum;
                    }
                }

                // String comparison
                switch (op)
                {
                    case "==": return valStr.Equals(target, System.StringComparison.OrdinalIgnoreCase);
                    case "!=": return !valStr.Equals(target, System.StringComparison.OrdinalIgnoreCase);
                    case "contains": return valStr.ToLower().Contains(target?.ToLower() ?? "");
                }
            }
            catch { }
            return false;
        }

        private static Vector3 ParseAxis(string axis)
        {
            switch (axis?.ToUpper())
            {
                case "X": return Vector3.right;
                case "Y": return Vector3.up;
                case "Z": return Vector3.forward;
                case "-X": return Vector3.left;
                case "-Y": return Vector3.down;
                case "-Z": return Vector3.back;
            }
            return Vector3.right;
        }
        
        private static string FormatValue(object val)
        {
            if (val is float f) return f.ToString("F2");
            if (val is double d) return d.ToString("F2");
            if (val is Vector3 v3) return $"({v3.x:F1}, {v3.y:F1}, {v3.z:F1})";
            if (val is Color c) return $"RGBA({c.r:F2}, {c.g:F2}, {c.b:F2}, {c.a:F2})";
            return val?.ToString() ?? "null";
        }
    }
}
