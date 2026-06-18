using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    internal static class SerializedPropertySkillUtility
    {
        public static SerializedProperty FindProperty(SerializedObject serializedObject, string propertyPath)
        {
            if (serializedObject == null || string.IsNullOrWhiteSpace(propertyPath))
            {
                return null;
            }

            var property = serializedObject.FindProperty(propertyPath);
            if (property != null) return property;

            var mName = "m_" + char.ToUpperInvariant(propertyPath[0]) + propertyPath.Substring(1);
            property = serializedObject.FindProperty(mName);
            if (property != null) return property;

            property = serializedObject.FindProperty("_" + propertyPath);
            if (property != null) return property;

            return serializedObject.FindProperty("m_" + propertyPath);
        }

        public static object[] ListProperties(UnityEngine.Object target, bool includeChildren = true, int limit = 200)
        {
            var serializedObject = new SerializedObject(target);
            serializedObject.Update();

            var results = new List<object>();
            var iterator = serializedObject.GetIterator();
            var enterChildren = true;
            while (iterator.NextVisible(enterChildren) && results.Count < Math.Max(1, limit))
            {
                enterChildren = includeChildren;
                var copy = iterator.Copy();
                results.Add(new
                {
                    propertyPath = copy.propertyPath,
                    name = copy.name,
                    displayName = copy.displayName,
                    propertyType = copy.propertyType.ToString(),
                    type = copy.type,
                    isArray = copy.isArray,
                    editable = copy.editable,
                    value = DescribeValue(copy)
                });
            }

            return results.ToArray();
        }

        public static bool TrySetProperty(
            SerializedProperty property,
            string value,
            string referenceName,
            int referenceInstanceId,
            string referencePath,
            string assetPath,
            string objectType,
            out string error)
        {
            error = null;
            if (property == null)
            {
                error = "SerializedProperty is null";
                return false;
            }

            if (!property.editable)
            {
                error = $"Property '{property.propertyPath}' is not editable";
                return false;
            }

            try
            {
                switch (property.propertyType)
                {
                    case SerializedPropertyType.Integer:
                    case SerializedPropertyType.LayerMask:
                    case SerializedPropertyType.ArraySize:
                        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                        {
                            property.longValue = longValue;
                            return true;
                        }
                        error = $"Expected integer value for '{property.propertyPath}'";
                        return false;

                    case SerializedPropertyType.Boolean:
                        property.boolValue = ParseBool(value);
                        return true;

                    case SerializedPropertyType.Float:
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
                        {
                            property.doubleValue = doubleValue;
                            return true;
                        }
                        error = $"Expected float value for '{property.propertyPath}'";
                        return false;

                    case SerializedPropertyType.String:
                        property.stringValue = value ?? string.Empty;
                        return true;

                    case SerializedPropertyType.Color:
                        property.colorValue = (Color)ComponentSkills.ConvertValue(value, typeof(Color));
                        return true;

                    case SerializedPropertyType.ObjectReference:
                        if (string.IsNullOrWhiteSpace(assetPath) &&
                            string.IsNullOrWhiteSpace(referenceName) &&
                            referenceInstanceId == 0 &&
                            string.IsNullOrWhiteSpace(referencePath) &&
                            (value == null || string.Equals(value, "null", StringComparison.OrdinalIgnoreCase)))
                        {
                            property.objectReferenceValue = null;
                            return true;
                        }

                        if (!TryResolveObjectReference(referenceName, referenceInstanceId, referencePath, assetPath, objectType, out var objectValue, out error))
                        {
                            return false;
                        }
                        property.objectReferenceValue = objectValue;
                        return true;

                    case SerializedPropertyType.Enum:
                        return TrySetEnum(property, value, out error);

                    case SerializedPropertyType.Vector2:
                        property.vector2Value = (Vector2)ComponentSkills.ConvertValue(value, typeof(Vector2));
                        return true;

                    case SerializedPropertyType.Vector3:
                        property.vector3Value = (Vector3)ComponentSkills.ConvertValue(value, typeof(Vector3));
                        return true;

                    case SerializedPropertyType.Vector4:
                        property.vector4Value = (Vector4)ComponentSkills.ConvertValue(value, typeof(Vector4));
                        return true;

                    case SerializedPropertyType.Rect:
                        property.rectValue = (Rect)ComponentSkills.ConvertValue(value, typeof(Rect));
                        return true;

                    case SerializedPropertyType.Bounds:
                        property.boundsValue = (Bounds)ComponentSkills.ConvertValue(value, typeof(Bounds));
                        return true;

                    case SerializedPropertyType.Quaternion:
                        property.quaternionValue = (Quaternion)ComponentSkills.ConvertValue(value, typeof(Quaternion));
                        return true;

                    case SerializedPropertyType.Vector2Int:
                        property.vector2IntValue = (Vector2Int)ComponentSkills.ConvertValue(value, typeof(Vector2Int));
                        return true;

                    case SerializedPropertyType.Vector3Int:
                        property.vector3IntValue = (Vector3Int)ComponentSkills.ConvertValue(value, typeof(Vector3Int));
                        return true;

                    case SerializedPropertyType.RectInt:
                        return TrySetRectInt(property, value, out error);

                    case SerializedPropertyType.BoundsInt:
                        return TrySetBoundsInt(property, value, out error);

                    case SerializedPropertyType.Character:
                        if (string.IsNullOrEmpty(value))
                        {
                            property.intValue = 0;
                        }
                        else
                        {
                            property.intValue = value[0];
                        }
                        return true;

                    default:
                        error = $"Unsupported SerializedPropertyType '{property.propertyType}' for '{property.propertyPath}'";
                        return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TryResolveObjectReference(
            string referenceName,
            int referenceInstanceId,
            string referencePath,
            string assetPath,
            string objectType,
            out UnityEngine.Object result,
            out string error)
        {
            result = null;
            error = null;

            if (!string.IsNullOrWhiteSpace(assetPath))
            {
                var targetType = ResolveUnityObjectType(objectType) ?? typeof(UnityEngine.Object);
                result = AssetDatabase.LoadAssetAtPath(assetPath, targetType);
                if (result == null)
                {
                    result = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                }

                if (result == null)
                {
                    error = $"Asset not found: {assetPath}";
                    return false;
                }

                if (targetType != typeof(UnityEngine.Object) && !targetType.IsAssignableFrom(result.GetType()))
                {
                    error = $"Asset '{assetPath}' is {result.GetType().Name}, expected {targetType.Name}";
                    return false;
                }

                return true;
            }

            var go = GameObjectFinder.Find(name: referenceName, instanceId: referenceInstanceId, path: referencePath);
            if (go == null)
            {
                error = $"Scene reference not found: name='{referenceName}', instanceId={referenceInstanceId}, path='{referencePath}'";
                return false;
            }

            if (string.IsNullOrWhiteSpace(objectType) ||
                string.Equals(objectType, "GameObject", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(objectType, "UnityEngine.GameObject", StringComparison.OrdinalIgnoreCase))
            {
                result = go;
                return true;
            }

            if (string.Equals(objectType, "Transform", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(objectType, "UnityEngine.Transform", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(objectType, "RectTransform", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(objectType, "UnityEngine.RectTransform", StringComparison.OrdinalIgnoreCase))
            {
                result = go.GetComponent(objectType.EndsWith("RectTransform", StringComparison.OrdinalIgnoreCase)
                    ? typeof(RectTransform)
                    : typeof(Transform));
                if (result == null)
                {
                    error = $"Component '{objectType}' not found on '{go.name}'";
                    return false;
                }
                return true;
            }

            var componentType = ComponentSkills.FindComponentType(objectType);
            if (componentType != null)
            {
                result = go.GetComponent(componentType);
                if (result == null)
                {
                    error = $"Component '{objectType}' not found on '{go.name}'";
                    return false;
                }
                return true;
            }

            error = $"Unity Object type not found: {objectType}";
            return false;
        }

        public static List<object> CompareSerializedProperties(UnityEngine.Object source, UnityEngine.Object target, int limit = 50)
        {
            var sourceValues = SnapshotComparableProperties(source);
            var targetValues = SnapshotComparableProperties(target);
            var mismatches = new List<object>();

            foreach (var entry in sourceValues)
            {
                if (!targetValues.TryGetValue(entry.Key, out var targetValue))
                {
                    mismatches.Add(new { propertyPath = entry.Key, source = entry.Value, target = "(missing)" });
                    continue;
                }

                if (!string.Equals(entry.Value, targetValue, StringComparison.Ordinal))
                {
                    mismatches.Add(new { propertyPath = entry.Key, source = entry.Value, target = targetValue });
                }

                if (mismatches.Count >= limit)
                {
                    break;
                }
            }

            return mismatches;
        }

        public static string DescribeValue(SerializedProperty property)
        {
            if (property == null) return null;

            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.ArraySize:
                    return property.longValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean:
                    return property.boolValue.ToString();
                case SerializedPropertyType.Float:
                    return property.doubleValue.ToString("G9", CultureInfo.InvariantCulture);
                case SerializedPropertyType.String:
                    return property.stringValue;
                case SerializedPropertyType.Color:
                    var c = property.colorValue;
                    return $"{c.r:G9},{c.g:G9},{c.b:G9},{c.a:G9}";
                case SerializedPropertyType.ObjectReference:
                    return property.objectReferenceValue == null
                        ? "null"
                        : $"{UnityObjectIdUtility.GetEntityId(property.objectReferenceValue)}:{property.objectReferenceValue.GetType().FullName}:{property.objectReferenceValue.name}";
                case SerializedPropertyType.Enum:
                    return property.enumValueIndex.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Vector2:
                    return Format(property.vector2Value);
                case SerializedPropertyType.Vector3:
                    return Format(property.vector3Value);
                case SerializedPropertyType.Vector4:
                    return Format(property.vector4Value);
                case SerializedPropertyType.Rect:
                    return Format(property.rectValue);
                case SerializedPropertyType.Bounds:
                    return Format(property.boundsValue);
                case SerializedPropertyType.Quaternion:
                    return Format(property.quaternionValue);
                case SerializedPropertyType.Vector2Int:
                    return $"{property.vector2IntValue.x},{property.vector2IntValue.y}";
                case SerializedPropertyType.Vector3Int:
                    return $"{property.vector3IntValue.x},{property.vector3IntValue.y},{property.vector3IntValue.z}";
                case SerializedPropertyType.RectInt:
                    var ri = property.rectIntValue;
                    return $"{ri.x},{ri.y},{ri.width},{ri.height}";
                case SerializedPropertyType.BoundsInt:
                    var bi = property.boundsIntValue;
                    return $"{bi.position.x},{bi.position.y},{bi.position.z},{bi.size.x},{bi.size.y},{bi.size.z}";
                case SerializedPropertyType.Character:
                    return property.intValue.ToString(CultureInfo.InvariantCulture);
                default:
                    return property.propertyType.ToString();
            }
        }

        private static Dictionary<string, string> SnapshotComparableProperties(UnityEngine.Object target)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var serializedObject = new SerializedObject(target);
            serializedObject.Update();

            var iterator = serializedObject.GetIterator();
            var enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = true;
                if (iterator.propertyPath == "m_Script") continue;
                if (iterator.propertyType == SerializedPropertyType.Generic ||
                    iterator.propertyType == SerializedPropertyType.ArraySize)
                {
                    continue;
                }

                var copy = iterator.Copy();
                result[copy.propertyPath] = DescribeValue(copy);
            }

            return result;
        }

        private static bool TrySetEnum(SerializedProperty property, string value, out string error)
        {
            error = null;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                if (index >= 0 && index < property.enumNames.Length)
                {
                    property.enumValueIndex = index;
                    return true;
                }
                error = $"Enum index {index} out of range for '{property.propertyPath}'";
                return false;
            }

            for (var i = 0; i < property.enumNames.Length; i++)
            {
                if (string.Equals(property.enumNames[i], value, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(property.enumDisplayNames[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    property.enumValueIndex = i;
                    return true;
                }
            }

            error = $"Enum value '{value}' not found for '{property.propertyPath}'";
            return false;
        }

        private static bool TrySetRectInt(SerializedProperty property, string value, out string error)
        {
            var parts = ParseInts(value, 4, out error);
            if (parts == null) return false;
            property.rectIntValue = new RectInt(parts[0], parts[1], parts[2], parts[3]);
            return true;
        }

        private static bool TrySetBoundsInt(SerializedProperty property, string value, out string error)
        {
            var parts = ParseInts(value, 6, out error);
            if (parts == null) return false;
            property.boundsIntValue = new BoundsInt(parts[0], parts[1], parts[2], parts[3], parts[4], parts[5]);
            return true;
        }

        private static int[] ParseInts(string value, int expectedCount, out string error)
        {
            error = null;
            var parts = (value ?? string.Empty)
                .Trim('(', ')', '[', ']', '{', '}')
                .Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != expectedCount)
            {
                error = $"Expected {expectedCount} integer values, got {parts.Length}";
                return null;
            }
            return parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        }

        private static bool ParseBool(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            return value == "true" || value == "1" || value == "yes" || value == "on";
        }

        private static Type ResolveUnityObjectType(string objectType)
        {
            if (string.IsNullOrWhiteSpace(objectType)) return null;
            if (string.Equals(objectType, "GameObject", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(objectType, "UnityEngine.GameObject", StringComparison.OrdinalIgnoreCase))
            {
                return typeof(GameObject);
            }
            if (string.Equals(objectType, "Transform", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(objectType, "UnityEngine.Transform", StringComparison.OrdinalIgnoreCase))
            {
                return typeof(Transform);
            }
            if (string.Equals(objectType, "RectTransform", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(objectType, "UnityEngine.RectTransform", StringComparison.OrdinalIgnoreCase))
            {
                return typeof(RectTransform);
            }

            var componentType = ComponentSkills.FindComponentType(objectType);
            if (componentType != null) return componentType;

            return SkillsCommon.GetAllLoadedTypes()
                .FirstOrDefault(t =>
                    typeof(UnityEngine.Object).IsAssignableFrom(t) &&
                    (string.Equals(t.Name, objectType, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(t.FullName, objectType, StringComparison.Ordinal)));
        }

        private static string Format(Vector2 v) => $"{v.x:G9},{v.y:G9}";
        private static string Format(Vector3 v) => $"{v.x:G9},{v.y:G9},{v.z:G9}";
        private static string Format(Vector4 v) => $"{v.x:G9},{v.y:G9},{v.z:G9},{v.w:G9}";
        private static string Format(Rect r) => $"{r.x:G9},{r.y:G9},{r.width:G9},{r.height:G9}";
        private static string Format(Bounds b) => $"{Format(b.center)},{Format(b.size)}";
        private static string Format(Quaternion q) => $"{q.x:G9},{q.y:G9},{q.z:G9},{q.w:G9}";
    }
}
