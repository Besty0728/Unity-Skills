using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Collections;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// Agent-facing smart skills: advanced scene queries, layout, and auto-wiring.
    /// The goal is to give AI "reasoning" and "hands-on design" capability.
    /// </summary>
    public static class SmartSkills
    {
        private static readonly Dictionary<string, System.Type> CommonUnityTypes = new Dictionary<string, System.Type>(System.StringComparer.OrdinalIgnoreCase)
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

        // ==================================================================================
        // 1. Smart query ("Unity scene-flavored SQL")
        // ==================================================================================

        [UnitySkill("smart_scene_query", "Query objects by component property (params: componentName, propertyName, op, value). e.g. componentName='Light', propertyName='intensity', op='>', value='10'",
            Category = SkillCategory.Smart, Operation = SkillOperation.Query,
            Tags = new[] { "query", "component", "property", "filter", "search" },
            Outputs = new[] { "count", "query", "results" },
            // Both would be rejected by the Validate.Required call below; since neither has a CLR default value, the schema used to mark them as optional,
            // and an empty-body dry-run was also judged valid. What's declared here is the parameter name (not a semantic "component" marker),
            // because that's the actual form this skill accepts, and both really are required -- there's no "either/or".
            RequiresInput = new[] { "componentName", "propertyName" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object SmartSceneQuery(
            string componentName = null,
            string propertyName = null,
            string op = "==",       // values: ==, !=, >, <, >=, <=, contains
            string value = null,
            int limit = 50,
            string query = null)
        {
            if (string.IsNullOrWhiteSpace(componentName) && !string.IsNullOrWhiteSpace(query))
            {
                return new
                {
                    success = false,
                    error = "query shorthand is not supported. Use componentName/propertyName/op/value, e.g. componentName='Light', propertyName='intensity', op='>', value='2'."
                };
            }

            if (Validate.Required(componentName, "componentName") is object componentErr) return componentErr;
            if (Validate.Required(propertyName, "propertyName") is object propertyErr) return propertyErr;

            var results = new List<object>();

            var type = GetTypeByName(componentName);
            if (type == null) 
                return new { success = false, error = $"Component type '{componentName}' not found. Try: Light, MeshRenderer, Camera, etc." };

            var components = FindHelper.FindAll(type, includeInactive: false);
            
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
                        entityId = UnityObjectIdUtility.GetEntityId(go),
                        instanceId = UnityObjectIdUtility.GetObjectId(go),
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
        // 2. Smart layout ("automated designer")
        // ==================================================================================

        [UnitySkill("smart_scene_layout", "Organize selected objects into a layout (Linear, Grid, Circle, Arc). Requires objects selected in Hierarchy first.", TracksWorkflow = true,
            Category = SkillCategory.Smart, Operation = SkillOperation.Modify,
            Tags = new[] { "layout", "arrange", "grid", "circle", "linear" },
            Outputs = new[] { "layout", "count", "spacing" },
            RequiresInput = new[] { "selection" })]
        public static object SmartSceneLayout(
            string layoutType = "Linear",   // values: Linear, Grid, Circle, Arc
            string axis = "X",              // Linear uses X/Y/Z; ignored under Circle
            float spacing = 2.0f,           // spacing between elements (radius under Circle)
            int columns = 3,                // only used by Grid layout
            float arcAngle = 180f,          // only used by Arc layout (in degrees)
            bool lookAtCenter = false)      // for Circle/Arc: rotate to face the center
        {
            var selected = Selection.gameObjects.OrderBy(g => g.transform.GetSiblingIndex()).ToList();
            if (selected.Count == 0)
                return new { success = false, error = "No GameObjects selected. Select objects in Hierarchy first." };

            // Both word lists are validated before anything gets moved. An unknown layoutType wouldn't match any switch branch,
            // which would leave every object stuck at newPos = startPos -- i.e. the whole selection collapses onto the first object's position, and still reports success.
            var layout = layoutType?.ToLower();
            if (layout != "linear" && layout != "grid" && layout != "circle" && layout != "arc")
                return SkillParamUtil.InvalidValueError(layoutType, "layoutType",
                    new[] { "Linear", "Grid", "Circle", "Arc" });

            if (!TryParseAxis(axis, out var axisVec))
                return SkillParamUtil.InvalidValueError(axis, "axis",
                    new[] { "X", "Y", "Z", "-X", "-Y", "-Z" });

            // Workflow support
            foreach (var go in selected)
                WorkflowManager.SnapshotObject(go.transform);

            Undo.RecordObjects(selected.Select(g => g.transform).ToArray(), "Smart Layout");

            var startPos = selected[0].transform.position;

            for (int i = 0; i < selected.Count; i++)
            {
                Vector3 newPos = startPos;
                
                switch (layout)
                {
                    case "linear":
                        newPos = startPos + axisVec * (i * spacing);
                        break;

                    case "grid":
                        int row = i / columns;
                        int col = i % columns;
                        // Grid defaults to laying out on the XZ plane
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
                
                if (lookAtCenter && (layout == "circle" || layout == "arc"))
                {
                    selected[i].transform.LookAt(startPos);
                }
            }

            return new { success = true, layout = layoutType, count = selected.Count, spacing };
        }

        // ==================================================================================
        // 3. Smart wiring ("auto-wiring engineer")
        // ==================================================================================

        [UnitySkill("smart_reference_bind", "Auto-fill a List/Array field with objects matching tag or name pattern", TracksWorkflow = true,
            Category = SkillCategory.Smart, Operation = SkillOperation.Modify,
            Tags = new[] { "bind", "reference", "auto-wire", "list", "field" },
            Outputs = new[] { "boundCount", "field", "appendMode" },
            RequiresInput = new[] { "gameObject", "component" })]
        public static object SmartReferenceBind(
            string targetName,          // name of the target GameObject
            string componentName,       // component on the target
            string fieldName,           // the field to populate
            string sourceTag = null,    // find by tag
            string sourceName = null,   // find by name containing this substring
            bool appendMode = false)    // true appends to existing elements, false replaces entirely
        {
            if (string.IsNullOrEmpty(fieldName)) return new { error = "fieldName is required" };

            // 1. Find the target object
            var targetGo = GameObjectFinder.Find(name: targetName);
            if (targetGo == null) 
                return new { success = false, error = $"Target '{targetName}' not found" };

            var comp = targetGo.GetComponent(componentName);
            if (comp == null) 
                return new { success = false, error = $"Component '{componentName}' not found on target" };

            // 2. Find the member (field first, then Unity naming-convention variants, finally property)
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

            // 3. Find the source objects
            var sources = new List<GameObject>();
            if (!string.IsNullOrEmpty(sourceTag))
            {
                try { sources.AddRange(GameObject.FindGameObjectsWithTag(sourceTag)); }
                catch { return new { success = false, error = $"Tag '{sourceTag}' does not exist" }; }
            }
            if (!string.IsNullOrEmpty(sourceName))
            {
                sources.AddRange(FindHelper.FindAll<GameObject>().Where(g => g.name.Contains(sourceName)));
            }
            sources = sources.Distinct().ToList();

            if (sources.Count == 0) 
                return new { success = false, error = "No source objects found matching criteria" };

            // 4. Validate the field type
            var fieldType = field != null ? field.FieldType : propFallback.PropertyType;
            bool isList = fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(List<>);
            bool isArray = fieldType.IsArray;

            if (!isList && !isArray)
                return new { success = false, error = $"Field '{fieldName}' is not a List<> or Array type" };

            var elementType = isArray ? fieldType.GetElementType() : fieldType.GetGenericArguments()[0];

            // sourceTag/sourceName both resolve to GameObjects, and the loop below can only convert them into GameObject or
            // Component elements (via GetComponent). Any element type that's neither of those (Material, ScriptableObject,
            // a plain interface...) can never match any source object, so every item gets silently dropped and the field gets overwritten with an empty array/list --
            // success:true, boundCount:0, no error. So this rejects before any write happens, instead of clearing a field the caller never asked to clear.
            if (elementType != typeof(GameObject) && !typeof(Component).IsAssignableFrom(elementType))
            {
                return new
                {
                    success = false,
                    error = $"Field '{fieldName}' has element type '{elementType.Name}', which is neither GameObject nor a Component — sourceTag/sourceName resolve to GameObjects, so this field can never be bound this way.",
                    errorCode = SkillParamUtil.SemanticInvalidCode,
                    parameter = "fieldName",
                };
            }

            WorkflowManager.SnapshotObject(comp);
            Undo.RecordObject(comp, "Smart Bind");
            var convertedList = new ArrayList();
            
            // append mode: start from the existing elements
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
        // Helper methods
        // ==================================================================================

        private static System.Type GetTypeByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            // Fast path: common Unity types (static dictionary)
            if (CommonUnityTypes.TryGetValue(name, out var t)) return t;

            // Slow path: reflection lookup
            return SkillsCommon.GetAllLoadedTypes()
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
            catch (System.Exception ex) { SkillsLogger.LogVerbose($"Condition eval failed: {ex.Message}"); }
            return false;
        }

        /// <summary>
        /// Converts an axis token into a direction vector. An invalid value returns false, rather than silently defaulting to Vector3.right --
        /// the latter would make a typo look like a deliberately chosen +X layout.
        /// </summary>
        private static bool TryParseAxis(string axis, out Vector3 direction)
        {
            switch (axis?.Trim().ToUpperInvariant())
            {
                case "X": direction = Vector3.right; return true;
                case "Y": direction = Vector3.up; return true;
                case "Z": direction = Vector3.forward; return true;
                case "-X": direction = Vector3.left; return true;
                case "-Y": direction = Vector3.down; return true;
                case "-Z": direction = Vector3.back; return true;
                default: direction = Vector3.right; return false;
            }
        }

        // Round-trippable and locale-independent. The F2/F1-style formatting this replaces would round the reported value into a number that no longer equals the actual stored value when read back,
        // and it also follows the editor's locale -- on a machine where the decimal separator is a comma it would output "(1,5, 0, 0)", which can't be parsed back as a vector at all.
        private static string FormatValue(object val)
        {
            if (val is Vector3 v3) return SkillParamUtil.FormatVector3(v3);
            if (val is Color c) return $"RGBA{SkillParamUtil.FormatColor(c)}";
            return SkillParamUtil.FormatScalarR(val);
        }

        [UnitySkill("smart_scene_query_spatial", "Find objects within a sphere/box region, optionally filtered by component",
            Category = SkillCategory.Smart, Operation = SkillOperation.Query,
            Tags = new[] { "spatial", "sphere", "overlap", "physics", "search" },
            Outputs = new[] { "count", "center", "radius", "results" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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
                    name = go.name, entityId = UnityObjectIdUtility.GetEntityId(go), instanceId = UnityObjectIdUtility.GetObjectId(go),
                    path = GameObjectFinder.GetPath(go),
                    distance = Vector3.Distance(center, go.transform.position)
                });
            }
            return new { success = true, count = results.Count, center = new { x, y, z }, radius, results };
        }

        [UnitySkill("smart_align_to_ground", "Raycast selected objects downward to align them to the ground. Requires objects selected in Hierarchy first.", TracksWorkflow = true,
            Category = SkillCategory.Smart, Operation = SkillOperation.Modify,
            Tags = new[] { "align", "ground", "raycast", "snap" },
            Outputs = new[] { "aligned", "total" },
            RequiresInput = new[] { "selection" })]
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

        [UnitySkill("smart_distribute", "Evenly distribute selected objects between first and last positions. Requires at least 3 objects selected in Hierarchy first.", TracksWorkflow = true,
            Category = SkillCategory.Smart, Operation = SkillOperation.Modify,
            Tags = new[] { "distribute", "spacing", "even", "arrange" },
            Outputs = new[] { "distributed", "axis" },
            RequiresInput = new[] { "selection" })]
        public static object SmartDistribute(string axis = "X")
        {
            var selected = Selection.gameObjects.OrderBy(g => g.transform.GetSiblingIndex()).ToList();
            if (selected.Count < 3) return new { error = "Need at least 3 selected objects" };
            // An invalid axis used to be silently treated as +X, so objects were laid out along an axis the caller never specified,
            // while the response still echoed back the very axis it had passed in.
            if (!TryParseAxis(axis, out var axisVec))
                return SkillParamUtil.InvalidValueError(axis, "axis",
                    new[] { "X", "Y", "Z", "-X", "-Y", "-Z" });
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

        [UnitySkill("smart_snap_to_grid", "Snap selected objects to a grid", TracksWorkflow = true,
            Category = SkillCategory.Smart, Operation = SkillOperation.Modify,
            Tags = new[] { "snap", "grid", "align", "position" },
            Outputs = new[] { "snapped", "gridSize" },
            RequiresInput = new[] { "selection" })]
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

        [UnitySkill("smart_randomize_transform", "Randomize position/rotation/scale of selected objects within ranges", TracksWorkflow = true,
            Category = SkillCategory.Smart, Operation = SkillOperation.Modify,
            Tags = new[] { "randomize", "transform", "scatter", "variation" },
            Outputs = new[] { "randomized" },
            RequiresInput = new[] { "selection" })]
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

        [UnitySkill("smart_replace_objects", "Replace selected objects with a prefab (preserving transforms). Requires objects selected in Hierarchy first.", TracksWorkflow = true,
            Category = SkillCategory.Smart, Operation = SkillOperation.Modify | SkillOperation.Delete,
            Tags = new[] { "replace", "prefab", "swap", "substitute" },
            Outputs = new[] { "replaced", "prefab" },
            RequiresInput = new[] { "selection", "prefabPath" },
            RiskLevel = "high")]
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

        [UnitySkill("smart_select_by_component", "Select all objects that have a specific component",
            Category = SkillCategory.Smart, Operation = SkillOperation.Execute,
            Tags = new[] { "select", "component", "filter", "batch" },
            Outputs = new[] { "selected", "component" })]
        public static object SmartSelectByComponent(string componentName = null, string componentType = null)
        {
            componentName = componentName ?? componentType;
            if (Validate.Required(componentName, "componentName") is object componentErr) return componentErr;

            var type = GetTypeByName(componentName);
            if (type == null) return new { error = $"Component type '{componentName}' not found" };
            var components = FindHelper.FindAll(type, includeInactive: false);
            var gameObjects = components.OfType<Component>().Select(c => c.gameObject).Distinct().ToArray();
            Selection.objects = gameObjects;
            return new { success = true, selected = gameObjects.Length, component = componentName };
        }
    }
}

// Producer:Betsy
