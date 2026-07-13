using UnityEngine;
using UnityEditor;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Globalization;

namespace UnitySkills
{
    /// <summary>
    /// ScriptableObject management skills.
    /// </summary>
    public static class ScriptableObjectSkills
    {
        [UnitySkill("scriptableobject_create", "Create a new ScriptableObject asset",
            Category = SkillCategory.ScriptableObject, Operation = SkillOperation.Create,
            Tags = new[] { "scriptableobject", "create", "asset", "data" },
            Outputs = new[] { "type", "path" },
            TracksWorkflow = true)]
        public static object ScriptableObjectCreate(string typeName, string savePath)
        {
            if (Validate.SafePath(savePath, "savePath") is object pathErr) return pathErr;

            var type = FindScriptableObjectType(typeName);
            if (type == null)
                return new { error = $"ScriptableObject type not found: {typeName}" };

            var instance = ScriptableObject.CreateInstance(type);
            if (instance == null)
                return new { error = $"Failed to create instance of: {typeName}" };

            var dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            AssetDatabase.CreateAsset(instance, savePath);
            WorkflowManager.SnapshotObject(instance, SnapshotType.Created);
            AssetDatabase.SaveAssets();

            return new { success = true, type = typeName, path = savePath };
        }

        [UnitySkill("scriptableobject_get", "Get properties of a ScriptableObject",
            Category = SkillCategory.ScriptableObject, Operation = SkillOperation.Query,
            Tags = new[] { "scriptableobject", "get", "inspect", "properties" },
            Outputs = new[] { "path", "typeName", "fields", "properties" },
            RequiresInput = new[] { "assetPath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ScriptableObjectGet(string assetPath)
        {
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
            if (asset == null)
                return new { error = $"ScriptableObject not found: {assetPath}" };

            var type = asset.GetType();
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(f => new { name = f.Name, type = f.FieldType.Name, value = f.GetValue(asset)?.ToString() })
                .ToArray();

            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && !p.GetIndexParameters().Any())
                .Select(p =>
                {
                    try { return new { name = p.Name, type = p.PropertyType.Name, value = p.GetValue(asset)?.ToString() }; }
                    catch { return new { name = p.Name, type = p.PropertyType.Name, value = "(error)" }; }
                })
                .ToArray();

            return new
            {
                path = assetPath,
                typeName = type.Name,
                fields,
                properties = props
            };
        }

        [UnitySkill("scriptableobject_set", "Set a top-level public field/property on a ScriptableObject via reflection. For nested paths, arrays, object references or private [SerializeField] fields use scriptableobject_set_serialized_property",
            Category = SkillCategory.ScriptableObject, Operation = SkillOperation.Modify,
            Tags = new[] { "scriptableobject", "set", "field", "property" },
            Outputs = new[] { "field", "value" },
            RequiresInput = new[] { "assetPath" },
            TracksWorkflow = true)]
        public static object ScriptableObjectSet(string assetPath, string fieldName, string value)
        {
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
            if (asset == null)
                return new { error = $"ScriptableObject not found: {assetPath}" };

            var type = asset.GetType();
            var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            var prop = type.GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance);

            if (field == null && prop == null)
                return new { error = $"Field/property not found: {fieldName}" };

            WorkflowManager.SnapshotObject(asset);
            Undo.RecordObject(asset, "Set ScriptableObject Field");

            try
            {
                if (field != null)
                {
                    var converted = ComponentSkills.ConvertValue(value, field.FieldType);
                    field.SetValue(asset, converted);
                }
                else if (prop != null && prop.CanWrite)
                {
                    var converted = ComponentSkills.ConvertValue(value, prop.PropertyType);
                    prop.SetValue(asset, converted);
                }

                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssets();

                return new { success = true, field = fieldName, value };
            }
            catch (System.Exception ex)
            {
                return new { error = ex.Message };
            }
        }

        [UnitySkill("scriptableobject_list_types", "List available ScriptableObject types",
            Category = SkillCategory.ScriptableObject, Operation = SkillOperation.Query,
            Tags = new[] { "scriptableobject", "types", "list", "search" },
            Outputs = new[] { "count", "types" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ScriptableObjectListTypes(string filter = null, int limit = 50)
        {
            var types = SkillsCommon.GetAllLoadedTypes()
                .Where(t => t.IsSubclassOf(typeof(ScriptableObject)) && !t.IsAbstract)
                .Where(t => string.IsNullOrEmpty(filter) || t.Name.Contains(filter))
                .Take(limit)
                .Select(t => new { name = t.Name, fullName = t.FullName })
                .ToArray();

            return new { count = types.Length, types };
        }

        [UnitySkill("scriptableobject_duplicate", "Duplicate a ScriptableObject asset",
            Category = SkillCategory.ScriptableObject, Operation = SkillOperation.Create,
            Tags = new[] { "scriptableobject", "duplicate", "copy", "clone" },
            Outputs = new[] { "original", "copy" },
            RequiresInput = new[] { "assetPath" })]
        public static object ScriptableObjectDuplicate(string assetPath)
        {
            if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;

            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
            if (asset == null)
                return new { error = $"ScriptableObject not found: {assetPath}" };

            var newPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);
            AssetDatabase.CopyAsset(assetPath, newPath);

            var newAsset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(newPath);
            if (newAsset != null)
                WorkflowManager.SnapshotObject(newAsset, SnapshotType.Created);

            return new { success = true, original = assetPath, copy = newPath };
        }

        [UnitySkill("scriptableobject_set_batch", "Set multiple fields on a ScriptableObject at once. fields: JSON object {fieldName: value, ...}",
            Category = SkillCategory.ScriptableObject, Operation = SkillOperation.Modify,
            Tags = new[] { "scriptableobject", "set", "batch", "fields" },
            Outputs = new[] { "fieldsSet" },
            RequiresInput = new[] { "assetPath" },
            TracksWorkflow = true)]
        public static object ScriptableObjectSetBatch(string assetPath, string fields)
        {
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
            if (asset == null) return new { error = $"ScriptableObject not found: {assetPath}" };
            var dict = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, string>>(fields);
            if (dict == null || dict.Count == 0) return new { error = "No fields provided" };
            WorkflowManager.SnapshotObject(asset);
            Undo.RecordObject(asset, "Set SO Batch");
            var type = asset.GetType();
            int set = 0;
            foreach (var kv in dict)
            {
                var field = type.GetField(kv.Key, BindingFlags.Public | BindingFlags.Instance);
                if (field != null) { field.SetValue(asset, ComponentSkills.ConvertValue(kv.Value, field.FieldType)); set++; }
            }
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            return new { success = true, fieldsSet = set };
        }

        [UnitySkill("scriptableobject_delete", "Delete a ScriptableObject asset",
            Category = SkillCategory.ScriptableObject, Operation = SkillOperation.Delete,
            Tags = new[] { "scriptableobject", "delete", "remove", "asset" },
            Outputs = new[] { "deleted" },
            RequiresInput = new[] { "assetPath" },
            TracksWorkflow = true)]
        public static object ScriptableObjectDelete(string assetPath)
        {
            if (Validate.SafePath(assetPath, "assetPath", isDelete: true) is object pathErr) return pathErr;
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
            if (asset == null) return new { error = $"ScriptableObject not found: {assetPath}" };
            WorkflowManager.SnapshotObject(asset);
            AssetDatabase.DeleteAsset(assetPath);
            return new { success = true, deleted = assetPath };
        }

        [UnitySkill("scriptableobject_find", "Find ScriptableObject assets by type name",
            Category = SkillCategory.ScriptableObject, Operation = SkillOperation.Query,
            Tags = new[] { "scriptableobject", "find", "search", "asset" },
            Outputs = new[] { "count", "assets" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ScriptableObjectFind(string typeName, string searchPath = "Assets", int limit = 50)
        {
            var guids = AssetDatabase.FindAssets($"t:{typeName}", new[] { searchPath });
            var results = guids.Take(limit).Select(g =>
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                return new { path = p, name = Path.GetFileNameWithoutExtension(p) };
            }).ToArray();
            return new { success = true, count = results.Length, assets = results };
        }

        [UnitySkill("scriptableobject_export_json", "Export a ScriptableObject to JSON",
            Category = SkillCategory.ScriptableObject, Operation = SkillOperation.Query,
            Tags = new[] { "scriptableobject", "export", "json", "serialize" },
            Outputs = new[] { "json", "path" },
            RequiresInput = new[] { "assetPath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ScriptableObjectExportJson(string assetPath, string savePath = null)
        {
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
            if (asset == null) return new { error = $"ScriptableObject not found: {assetPath}" };
            var json = EditorJsonUtility.ToJson(asset, true);
            if (!string.IsNullOrEmpty(savePath))
            {
                if (Validate.SafePath(savePath, "savePath") is object pathErr) return pathErr;
                File.WriteAllText(savePath, json, SkillsCommon.Utf8NoBom);
                return new { success = true, path = savePath };
            }
            return new { success = true, json };
        }

        [UnitySkill("scriptableobject_import_json", "Import JSON data into a ScriptableObject. Accepts bare field JSON {\"field\":value} (auto-wrapped) or the {\"MonoBehaviour\":{...}} format produced by scriptableobject_export_json. Returns a warning when no serialized field changed",
            Category = SkillCategory.ScriptableObject, Operation = SkillOperation.Modify,
            Tags = new[] { "scriptableobject", "import", "json", "deserialize" },
            Outputs = new[] { "assetPath", "warning" },
            RequiresInput = new[] { "assetPath" },
            TracksWorkflow = true)]
        public static object ScriptableObjectImportJson(string assetPath, string json = null, string jsonFilePath = null)
        {
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
            if (asset == null) return new { error = $"ScriptableObject not found: {assetPath}" };
            var data = json;
            if (string.IsNullOrEmpty(data) && !string.IsNullOrEmpty(jsonFilePath))
            {
                if (Validate.SafePath(jsonFilePath, "jsonFilePath") is object pathErr) return pathErr;
                data = File.ReadAllText(jsonFilePath, System.Text.Encoding.UTF8);
            }
            if (string.IsNullOrEmpty(data)) return new { error = "No JSON data provided" };

            // EditorJsonUtility.FromJsonOverwrite silently ignores bare field JSON; it expects the
            // {"MonoBehaviour":{...}} envelope that scriptableobject_export_json produces, so wrap bare objects.
            try
            {
                var root = Newtonsoft.Json.Linq.JToken.Parse(data);
                if (!(root is Newtonsoft.Json.Linq.JObject rootObject))
                    return new { error = "JSON root must be an object" };
                if (rootObject.Property("MonoBehaviour") == null)
                    data = new Newtonsoft.Json.Linq.JObject { ["MonoBehaviour"] = rootObject }.ToString();
            }
            catch (System.Exception ex)
            {
                return new { error = $"Invalid JSON: {ex.Message}" };
            }

            WorkflowManager.SnapshotObject(asset);
            Undo.RecordObject(asset, "Import JSON to SO");
            var before = EditorJsonUtility.ToJson(asset);
            EditorJsonUtility.FromJsonOverwrite(data, asset);
            var changed = !string.Equals(EditorJsonUtility.ToJson(asset), before, System.StringComparison.Ordinal);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            if (!changed)
            {
                return new
                {
                    success = true,
                    assetPath,
                    warning = "No serialized field changed - either the values already match or no field name matched. Compare field names with scriptableobject_export_json output."
                };
            }
            return new { success = true, assetPath };
        }

        [UnitySkill("scriptableobject_get_serialized_properties", "List Inspector serialized properties of a ScriptableObject asset (propertyPath, type, current value), including private [SerializeField] and nested/array children. Use returned propertyPath with scriptableobject_set_serialized_property",
            Category = SkillCategory.ScriptableObject, Operation = SkillOperation.Query,
            Tags = new[] { "scriptableobject", "serialized", "inspector", "property", "list" },
            Outputs = new[] { "path", "typeName", "properties" },
            RequiresInput = new[] { "assetPath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ScriptableObjectGetSerializedProperties(string assetPath, bool includeChildren = true, int limit = 200)
        {
            if (Validate.Required(assetPath, "assetPath") is object reqErr) return reqErr;

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset == null)
                return new { error = $"Asset not found: {assetPath}" };

            return new
            {
                success = true,
                path = assetPath,
                typeName = asset.GetType().Name,
                properties = SerializedPropertySkillUtility.ListProperties(asset, includeChildren, limit)
            };
        }

        [UnitySkill("scriptableobject_set_serialized_property", "Set an Inspector serialized property on a ScriptableObject asset by propertyPath. Supports nested paths (stats.speed), array elements (items.Array.data[2]), array resize (items.Array.size), private [SerializeField] fields, object references via valueAssetPath, gradients, animation curves, and flags enums",
            Category = SkillCategory.ScriptableObject, Operation = SkillOperation.Modify,
            Tags = new[] { "scriptableobject", "serialized", "inspector", "property", "nested", "array", "reference" },
            Outputs = new[] { "assetPath", "propertyPath", "valueSet" },
            RequiresInput = new[] { "assetPath" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RiskLevel = "medium")]
        public static object ScriptableObjectSetSerializedProperty(
            string assetPath, string propertyPath, string value = null,
            string valueAssetPath = null, string valueObjectType = null)
        {
            if (Validate.Required(assetPath, "assetPath") is object reqErr1) return reqErr1;
            if (Validate.Required(propertyPath, "propertyPath") is object reqErr2) return reqErr2;

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset == null)
                return new { error = $"Asset not found: {assetPath}" };

            var serializedObject = new SerializedObject(asset);
            serializedObject.Update();
            var property = SerializedPropertySkillUtility.FindProperty(serializedObject, propertyPath);
            if (property == null)
            {
                return new
                {
                    error = $"Serialized property not found: {propertyPath}",
                    availableProperties = SerializedPropertySkillUtility.ListProperties(asset, true, 60)
                };
            }

            WorkflowManager.SnapshotObject(asset);
            Undo.RecordObject(asset, "Set SO Serialized Property");

            if (!SerializedPropertySkillUtility.TrySetProperty(
                    property, value, null, 0, null, valueAssetPath, valueObjectType, out var setError))
            {
                return new { error = setError };
            }

            serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                assetPath,
                propertyPath = property.propertyPath,
                valueSet = SerializedPropertySkillUtility.DescribeValue(property)
            };
        }

        [UnitySkill("scriptableobject_set_serialized_property_batch", "Set multiple Inspector serialized properties on one ScriptableObject asset. items: JSON array of {propertyPath, value, valueAssetPath, valueObjectType}",
            Category = SkillCategory.ScriptableObject, Operation = SkillOperation.Modify,
            Tags = new[] { "scriptableobject", "serialized", "property", "batch" },
            Outputs = new[] { "successCount", "failCount", "results" },
            RequiresInput = new[] { "assetPath" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RiskLevel = "medium")]
        public static object ScriptableObjectSetSerializedPropertyBatch(string assetPath, string items)
        {
            if (Validate.Required(assetPath, "assetPath") is object reqErr) return reqErr;

            return BatchExecutor.Execute<BatchSetSerializedPropertyItem>(items, item =>
            {
                var result = ScriptableObjectSetSerializedProperty(
                    assetPath, item.propertyPath, item.value, item.valueAssetPath, item.valueObjectType);
                if (SkillResultHelper.TryGetError(result, out var error))
                {
                    throw new System.Exception(error);
                }
                return result;
            }, item => item.propertyPath);
        }

        private class BatchSetSerializedPropertyItem
        {
            public string propertyPath { get; set; }
            public string value { get; set; }
            public string valueAssetPath { get; set; }
            public string valueObjectType { get; set; }
        }

        private static System.Type FindScriptableObjectType(string name)
        {
            return SkillsCommon.GetAllLoadedTypes()
                .FirstOrDefault(t => string.Equals(t.Name, name, System.StringComparison.OrdinalIgnoreCase) && t.IsSubclassOf(typeof(ScriptableObject)));
        }

    }
}

// Producer:Betsy
