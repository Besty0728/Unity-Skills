using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
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

                    case SerializedPropertyType.Gradient:
                        return TrySetGradient(property, value, out error);

                    case SerializedPropertyType.AnimationCurve:
                        return TrySetAnimationCurve(property, value, out error);

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
                    // enumValueIndex is -1 for combined [Flags] masks; fall back to the raw bitmask.
                    return property.enumValueIndex >= 0
                        ? property.enumValueIndex.ToString(CultureInfo.InvariantCulture)
                        : $"flags:{property.intValue.ToString(CultureInfo.InvariantCulture)}";
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
                case SerializedPropertyType.Gradient:
                    var gradient = property.gradientValue;
                    return gradient == null
                        ? "null"
                        : $"Gradient(colorKeys={gradient.colorKeys.Length},alphaKeys={gradient.alphaKeys.Length},mode={gradient.mode})";
                case SerializedPropertyType.AnimationCurve:
                    var curve = property.animationCurveValue;
                    return curve == null ? "null" : $"AnimationCurve(keys={curve.length})";
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

                // Out-of-range numbers are written as a raw bitmask so [Flags] combinations
                // (e.g. "3" = A|B, "-1" = Everything) stay expressible.
                property.intValue = index;
                return true;
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

            if (value != null && value.IndexOfAny(new[] { ',', '|' }) >= 0)
            {
                return TrySetEnumFlags(property, value, out error);
            }

            error = $"Enum value '{value}' not found for '{property.propertyPath}'. Valid names: {string.Join(", ", property.enumNames)}. [Flags] enums accept comma-separated names or a raw bitmask number";
            return false;
        }

        private static bool TrySetEnumFlags(SerializedProperty property, string value, out string error)
        {
            error = null;
            var names = property.enumNames;
            var displayNames = property.enumDisplayNames;
            var originalValue = property.intValue;
            var parts = value.Split(new[] { ',', '|' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                error = $"Enum value '{value}' not found for '{property.propertyPath}'. Valid names: {string.Join(", ", names)}";
                return false;
            }

            var combined = 0;
            foreach (var part in parts)
            {
                var token = part.Trim();
                var matchIndex = -1;
                for (var i = 0; i < names.Length; i++)
                {
                    if (string.Equals(names[i], token, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(displayNames[i], token, StringComparison.OrdinalIgnoreCase))
                    {
                        matchIndex = i;
                        break;
                    }
                }

                if (matchIndex < 0)
                {
                    property.intValue = originalValue;
                    error = $"Enum value '{token}' not found for '{property.propertyPath}'. Valid names: {string.Join(", ", names)}";
                    return false;
                }

                // The enumValueIndex round-trip resolves each name to its underlying constant,
                // so combined masks work without reflecting on the enum type (native enums included).
                property.enumValueIndex = matchIndex;
                combined |= property.intValue;
            }

            property.intValue = combined;
            return true;
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

        private static bool TrySetGradient(SerializedProperty property, string value, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                error = $"Expected gradient JSON for '{property.propertyPath}', e.g. {{\"colorKeys\":[{{\"color\":\"1,0,0,1\",\"time\":0}},{{\"color\":\"0,0,1,1\",\"time\":1}}],\"alphaKeys\":[{{\"alpha\":1,\"time\":0}},{{\"alpha\":1,\"time\":1}}],\"mode\":\"Blend\"}}";
                return false;
            }

            GradientPayload payload;
            try
            {
                payload = JsonConvert.DeserializeObject<GradientPayload>(value);
            }
            catch (Exception ex)
            {
                error = $"Invalid gradient JSON: {ex.Message}";
                return false;
            }

            if (payload?.colorKeys == null || payload.colorKeys.Length == 0)
            {
                error = "Gradient JSON must contain a non-empty colorKeys array";
                return false;
            }

            var colorKeys = payload.colorKeys
                .Select(k => new GradientColorKey((Color)ComponentSkills.ConvertValue(k.color, typeof(Color)), k.time))
                .ToArray();
            var alphaKeys = payload.alphaKeys != null && payload.alphaKeys.Length > 0
                ? payload.alphaKeys.Select(k => new GradientAlphaKey(k.alpha, k.time)).ToArray()
                : new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) };

            var gradient = new Gradient();
            gradient.SetKeys(colorKeys, alphaKeys);
            if (!string.IsNullOrEmpty(payload.mode))
            {
                if (!Enum.TryParse<GradientMode>(payload.mode, true, out var gradientMode))
                {
                    error = $"Unknown gradient mode '{payload.mode}' (expected Blend or Fixed)";
                    return false;
                }
                gradient.mode = gradientMode;
            }

            property.gradientValue = gradient;
            return true;
        }

        private static bool TrySetAnimationCurve(SerializedProperty property, string value, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                error = $"Expected animation curve JSON for '{property.propertyPath}', e.g. {{\"keys\":[{{\"time\":0,\"value\":0}},{{\"time\":1,\"value\":1,\"inTangent\":2,\"outTangent\":2}}],\"preWrapMode\":\"ClampForever\",\"postWrapMode\":\"Loop\"}}";
                return false;
            }

            AnimationCurvePayload payload;
            try
            {
                payload = JsonConvert.DeserializeObject<AnimationCurvePayload>(value);
            }
            catch (Exception ex)
            {
                error = $"Invalid animation curve JSON: {ex.Message}";
                return false;
            }

            if (payload?.keys == null || payload.keys.Length == 0)
            {
                error = "Animation curve JSON must contain a non-empty keys array";
                return false;
            }

            var curve = new AnimationCurve(payload.keys
                .Select(k => new Keyframe(k.time, k.value, k.inTangent, k.outTangent))
                .ToArray());
            if (!TryParseWrapMode(payload.preWrapMode, out var preWrap, out error)) return false;
            if (preWrap.HasValue) curve.preWrapMode = preWrap.Value;
            if (!TryParseWrapMode(payload.postWrapMode, out var postWrap, out error)) return false;
            if (postWrap.HasValue) curve.postWrapMode = postWrap.Value;

            property.animationCurveValue = curve;
            return true;
        }

        private static bool TryParseWrapMode(string text, out WrapMode? mode, out string error)
        {
            mode = null;
            error = null;
            if (string.IsNullOrEmpty(text)) return true;
            if (Enum.TryParse<WrapMode>(text, true, out var parsed))
            {
                mode = parsed;
                return true;
            }
            error = $"Unknown wrap mode '{text}' (expected Default, Once, Loop, PingPong or ClampForever)";
            return false;
        }

        private class GradientPayload
        {
            public GradientColorKeyPayload[] colorKeys { get; set; }
            public GradientAlphaKeyPayload[] alphaKeys { get; set; }
            public string mode { get; set; }
        }

        private class GradientColorKeyPayload
        {
            public string color { get; set; }
            public float time { get; set; }
        }

        private class GradientAlphaKeyPayload
        {
            public float alpha { get; set; }
            public float time { get; set; }
        }

        private class AnimationCurvePayload
        {
            public KeyframePayload[] keys { get; set; }
            public string preWrapMode { get; set; }
            public string postWrapMode { get; set; }
        }

        private class KeyframePayload
        {
            public float time { get; set; }
            public float value { get; set; }
            public float inTangent { get; set; }
            public float outTangent { get; set; }
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

// Producer:Betsy
