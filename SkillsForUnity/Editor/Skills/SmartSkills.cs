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
                    var go = (comp is Component c) ? c.gameObject : null;
                    if (go == null) continue;
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

            // 2. Find Member (field, then Unity naming conventions, then property)
            var type = comp.GetType();
            var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null)
                field = type.GetField("m_" + char.ToUpper(fieldName[0]) + fieldName.Substring(1), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null)
                field = type.GetField("_" + fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            PropertyInfo propFallback = null;
            if (field == null)
            {
                propFallback = type.GetProperty(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (propFallback != null && !propFallback.CanWrite) propFallback = null;
            }

            if (field == null && propFallback == null)
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
            var fieldType = field != null ? field.FieldType : propFallback.PropertyType;
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
                var existing = (field != null ? field.GetValue(comp) : propFallback.GetValue(comp)) as IEnumerable;
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
            }

            // Set value
            if (isArray)
            {
                var array = System.Array.CreateInstance(elementType, convertedList.Count);
                convertedList.CopyTo(array);
                if (field != null) field.SetValue(comp, array);
                else propFallback.SetValue(comp, array);
            }
            else
            {
                var list = System.Activator.CreateInstance(fieldType) as IList;
                foreach (var item in convertedList) list.Add(item);
                if (field != null) field.SetValue(comp, list);
                else propFallback.SetValue(comp, list);
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

        [UnitySkill("smart_scene_query_spatial", "Find objects within a sphere/box region, optionally filtered by component")]
        public static object SmartSceneQuerySpatial(
            float x, float y, float z, float radius = 10f,
            string componentFilter = null, int limit = 50)
        {
            var center = new Vector3(x, y, z);
            var colliders = Physics.OverlapSphere(center, radius);
            var results = new List<object>();
            foreach (var col in colliders)
            {
                if (results.Count >= limit) break;
                var go = col.gameObject;
                if (!string.IsNullOrEmpty(componentFilter))
                {
                    var type = GetTypeByName(componentFilter);
                    if (type != null && go.GetComponent(type) == null) continue;
                }
                results.Add(new
                {
                    name = go.name, instanceId = go.GetInstanceID(),
                    path = GameObjectFinder.GetPath(go),
                    distance = Vector3.Distance(center, go.transform.position)
                });
            }
            return new { success = true, count = results.Count, center = new { x, y, z }, radius, results };
        }

        [UnitySkill("smart_align_to_ground", "Raycast selected objects downward to align them to the ground")]
        public static object SmartAlignToGround(float maxDistance = 100f, bool alignRotation = false)
        {
            var selected = Selection.gameObjects;
            if (selected.Length == 0) return new { error = "No objects selected" };
            int aligned = 0;
            foreach (var go in selected)
            {
                WorkflowManager.SnapshotObject(go.transform);
                Undo.RecordObject(go.transform, "Align To Ground");
                if (Physics.Raycast(go.transform.position + Vector3.up * 0.1f, Vector3.down, out RaycastHit hit, maxDistance))
                {
                    go.transform.position = hit.point;
                    if (alignRotation) go.transform.up = hit.normal;
                    aligned++;
                }
            }
            return new { success = true, aligned, total = selected.Length };
        }

        [UnitySkill("smart_distribute", "Evenly distribute selected objects between first and last positions")]
        public static object SmartDistribute(string axis = "X")
        {
            var selected = Selection.gameObjects.OrderBy(g => g.transform.GetSiblingIndex()).ToList();
            if (selected.Count < 3) return new { error = "Need at least 3 selected objects" };
            Vector3 axisVec = ParseAxis(axis);
            foreach (var go in selected) WorkflowManager.SnapshotObject(go.transform);
            Undo.RecordObjects(selected.Select(g => g.transform).ToArray(), "Smart Distribute");
            float startVal = Vector3.Dot(selected[0].transform.position, axisVec);
            float endVal = Vector3.Dot(selected[selected.Count - 1].transform.position, axisVec);
            for (int i = 1; i < selected.Count - 1; i++)
            {
                float t = i / (float)(selected.Count - 1);
                float targetVal = Mathf.Lerp(startVal, endVal, t);
                float currentVal = Vector3.Dot(selected[i].transform.position, axisVec);
                selected[i].transform.position += axisVec * (targetVal - currentVal);
            }
            return new { success = true, distributed = selected.Count, axis };
        }

        [UnitySkill("smart_snap_to_grid", "Snap selected objects to a grid")]
        public static object SmartSnapToGrid(float gridSize = 1f)
        {
            var selected = Selection.gameObjects;
            if (selected.Length == 0) return new { error = "No objects selected" };
            foreach (var go in selected)
            {
                WorkflowManager.SnapshotObject(go.transform);
                Undo.RecordObject(go.transform, "Snap To Grid");
                var p = go.transform.position;
                go.transform.position = new Vector3(
                    Mathf.Round(p.x / gridSize) * gridSize,
                    Mathf.Round(p.y / gridSize) * gridSize,
                    Mathf.Round(p.z / gridSize) * gridSize);
            }
            return new { success = true, snapped = selected.Length, gridSize };
        }

        [UnitySkill("smart_randomize_transform", "Randomize position/rotation/scale of selected objects within ranges")]
        public static object SmartRandomizeTransform(
            float posRange = 0f, float rotRange = 0f, float scaleMin = 1f, float scaleMax = 1f)
        {
            var selected = Selection.gameObjects;
            if (selected.Length == 0) return new { error = "No objects selected" };
            foreach (var go in selected)
            {
                WorkflowManager.SnapshotObject(go.transform);
                Undo.RecordObject(go.transform, "Randomize Transform");
                if (posRange > 0) go.transform.position += new Vector3(Random.Range(-posRange, posRange), Random.Range(-posRange, posRange), Random.Range(-posRange, posRange));
                if (rotRange > 0) go.transform.eulerAngles += new Vector3(Random.Range(-rotRange, rotRange), Random.Range(-rotRange, rotRange), Random.Range(-rotRange, rotRange));
                if (scaleMin != 1f || scaleMax != 1f) { float s = Random.Range(scaleMin, scaleMax); go.transform.localScale = new Vector3(s, s, s); }
            }
            return new { success = true, randomized = selected.Length };
        }

        [UnitySkill("smart_replace_objects", "Replace selected objects with a prefab (preserving transforms)")]
        public static object SmartReplaceObjects(string prefabPath)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) return new { error = $"Prefab not found: {prefabPath}" };
            var selected = Selection.gameObjects.ToArray();
            if (selected.Length == 0) return new { error = "No objects selected" };
            var newObjects = new List<GameObject>();
            foreach (var go in selected)
            {
                var newGo = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                newGo.transform.SetParent(go.transform.parent);
                newGo.transform.position = go.transform.position;
                newGo.transform.rotation = go.transform.rotation;
                newGo.transform.localScale = go.transform.localScale;
                newGo.transform.SetSiblingIndex(go.transform.GetSiblingIndex());
                Undo.RegisterCreatedObjectUndo(newGo, "Replace Object");
                Undo.DestroyObjectImmediate(go);
                newObjects.Add(newGo);
            }
            Selection.objects = newObjects.ToArray();
            return new { success = true, replaced = selected.Length, prefab = prefabPath };
        }

        [UnitySkill("smart_select_by_component", "Select all objects that have a specific component")]
        public static object SmartSelectByComponent(string componentName)
        {
            var type = GetTypeByName(componentName);
            if (type == null) return new { error = $"Component type '{componentName}' not found" };
            var components = Object.FindObjectsOfType(type);
            var gameObjects = components.OfType<Component>().Select(c => c.gameObject).Distinct().ToArray();
            Selection.objects = gameObjects;
            return new { success = true, selected = gameObjects.Length, component = componentName };
        }
    }
}
