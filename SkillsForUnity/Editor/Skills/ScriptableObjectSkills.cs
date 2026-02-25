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
        [UnitySkill("scriptableobject_create", "Create a new ScriptableObject asset")]
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

        [UnitySkill("scriptableobject_get", "Get properties of a ScriptableObject")]
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

        [UnitySkill("scriptableobject_set", "Set a field/property on a ScriptableObject")]
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
                    var converted = ConvertValue(value, field.FieldType);
                    field.SetValue(asset, converted);
                }
                else if (prop != null && prop.CanWrite)
                {
                    var converted = ConvertValue(value, prop.PropertyType);
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

        [UnitySkill("scriptableobject_list_types", "List available ScriptableObject types")]
        public static object ScriptableObjectListTypes(string filter = null, int limit = 50)
        {
            var types = System.AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return new System.Type[0]; } })
                .Where(t => t.IsSubclassOf(typeof(ScriptableObject)) && !t.IsAbstract)
                .Where(t => string.IsNullOrEmpty(filter) || t.Name.Contains(filter))
                .Take(limit)
                .Select(t => new { name = t.Name, fullName = t.FullName })
                .ToArray();

            return new { count = types.Length, types };
        }

        [UnitySkill("scriptableobject_duplicate", "Duplicate a ScriptableObject asset")]
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

        [UnitySkill("scriptableobject_set_batch", "Set multiple fields on a ScriptableObject at once. fields: JSON object {fieldName: value, ...}")]
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
                if (field != null) { field.SetValue(asset, ConvertValue(kv.Value, field.FieldType)); set++; }
            }
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            return new { success = true, fieldsSet = set };
        }

        [UnitySkill("scriptableobject_delete", "Delete a ScriptableObject asset")]
        public static object ScriptableObjectDelete(string assetPath)
        {
            if (Validate.SafePath(assetPath, "assetPath", isDelete: true) is object pathErr) return pathErr;
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
            if (asset == null) return new { error = $"ScriptableObject not found: {assetPath}" };
            WorkflowManager.SnapshotObject(asset);
            AssetDatabase.DeleteAsset(assetPath);
            return new { success = true, deleted = assetPath };
        }

        [UnitySkill("scriptableobject_find", "Find ScriptableObject assets by type name")]
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

        [UnitySkill("scriptableobject_export_json", "Export a ScriptableObject to JSON")]
        public static object ScriptableObjectExportJson(string assetPath, string savePath = null)
        {
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
            if (asset == null) return new { error = $"ScriptableObject not found: {assetPath}" };
            var json = EditorJsonUtility.ToJson(asset, true);
            if (!string.IsNullOrEmpty(savePath))
            {
                if (Validate.SafePath(savePath, "savePath") is object pathErr) return pathErr;
                File.WriteAllText(savePath, json);
                return new { success = true, path = savePath };
            }
            return new { success = true, json };
        }

        [UnitySkill("scriptableobject_import_json", "Import JSON data into a ScriptableObject")]
        public static object ScriptableObjectImportJson(string assetPath, string json = null, string jsonFilePath = null)
        {
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
            if (asset == null) return new { error = $"ScriptableObject not found: {assetPath}" };
            var data = json;
            if (string.IsNullOrEmpty(data) && !string.IsNullOrEmpty(jsonFilePath))
            {
                if (Validate.SafePath(jsonFilePath, "jsonFilePath") is object pathErr) return pathErr;
                data = File.ReadAllText(jsonFilePath);
            }
            if (string.IsNullOrEmpty(data)) return new { error = "No JSON data provided" };
            WorkflowManager.SnapshotObject(asset);
            Undo.RecordObject(asset, "Import JSON to SO");
            EditorJsonUtility.FromJsonOverwrite(data, asset);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            return new { success = true, assetPath };
        }

        private static System.Type FindScriptableObjectType(string name)
        {
            return System.AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return new System.Type[0]; } })
                .FirstOrDefault(t => t.Name == name && t.IsSubclassOf(typeof(ScriptableObject)));
        }

        private static object ConvertValue(string value, System.Type targetType)
        {
            if (targetType == typeof(string)) return value;
            if (targetType == typeof(int)) return int.Parse(value);
            if (targetType == typeof(float)) return float.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool)) return bool.Parse(value);
            if (targetType == typeof(Vector3))
            {
                var parts = value.Split(',');
                return new Vector3(float.Parse(parts[0], CultureInfo.InvariantCulture), float.Parse(parts[1], CultureInfo.InvariantCulture), float.Parse(parts[2], CultureInfo.InvariantCulture));
            }
            if (targetType == typeof(Color))
            {
                var parts = value.Split(',');
                return new Color(float.Parse(parts[0], CultureInfo.InvariantCulture), float.Parse(parts[1], CultureInfo.InvariantCulture), float.Parse(parts[2], CultureInfo.InvariantCulture),
                    parts.Length > 3 ? float.Parse(parts[3], CultureInfo.InvariantCulture) : 1);
            }
            return System.Convert.ChangeType(value, targetType);
        }
    }
}
