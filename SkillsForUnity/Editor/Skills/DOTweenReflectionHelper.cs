using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// Reflection helper for DOTween / DOTween Pro. All field access on
    /// DOTweenAnimation goes through this class so the project compiles
    /// cleanly without a compile-time reference to DOTween.
    ///
    /// Field naming in DOTween Pro has historically been stable but not
    /// source-public — candidate arrays are used for the high-frequency
    /// fields so minor renames across versions are tolerated.
    /// </summary>
    internal static class DOTweenReflectionHelper
    {
        // ==================================================================================
        // Type Lookup
        // ==================================================================================

        public const string DOTweenTypeName = "DG.Tweening.DOTween";
        public const string DOTweenAnimationTypeName = "DG.Tweening.DOTweenAnimation";
        public const string EaseEnumTypeName = "DG.Tweening.Ease";
        public const string LoopTypeEnumTypeName = "DG.Tweening.LoopType";

        public static Type FindTypeInAssemblies(string fullName) => SkillsCommon.FindTypeByName(fullName);

        public static bool IsDOTweenInstalled => FindTypeInAssemblies(DOTweenTypeName) != null;
        public static bool IsDOTweenProInstalled => FindTypeInAssemblies(DOTweenAnimationTypeName) != null;

        public static object NoDOTween() => new
        {
            error = "DOTween is not installed. Import DOTween (Free or Pro) from the Asset Store, " +
                    "or add via Package Manager > Add by git URL https://github.com/Demigiant/dotween.git. " +
                    "The DOTWEEN scripting define is added automatically after install."
        };

        public static object NoDOTweenPro() => new
        {
            error = "DOTween Pro is not installed. The DOTweenAnimation component is a Pro-only feature. " +
                    "Purchase and import DOTween Pro from the Unity Asset Store."
        };

        // ==================================================================================
        // High-frequency Field Name Candidates
        // ==================================================================================

        public static readonly string[] DurationFieldCandidates = { "duration" };
        public static readonly string[] DelayFieldCandidates = { "delay" };
        public static readonly string[] LoopsFieldCandidates = { "loops" };
        public static readonly string[] LoopTypeFieldCandidates = { "loopType" };
        public static readonly string[] EaseFieldCandidates = { "easeType", "ease" };
        public static readonly string[] EaseCurveFieldCandidates = { "easeCurve", "customCurve" };
        public static readonly string[] AnimationTypeFieldCandidates = { "animationType" };
        public static readonly string[] TargetTypeFieldCandidates = { "targetType" };
        public static readonly string[] IsRelativeFieldCandidates = { "isRelative" };
        public static readonly string[] IsFromFieldCandidates = { "isFrom" };
        public static readonly string[] AutoPlayFieldCandidates = { "autoPlay" };
        public static readonly string[] AutoKillFieldCandidates = { "autoKill" };
        public static readonly string[] IdFieldCandidates = { "id" };

        public static readonly string[] EndValueV3Candidates = { "endValueV3" };
        public static readonly string[] EndValueV2Candidates = { "endValueV2" };
        public static readonly string[] EndValueFloatCandidates = { "endValueFloat" };
        public static readonly string[] EndValueColorCandidates = { "endValueColor" };
        public static readonly string[] EndValueStringCandidates = { "endValueString" };
        public static readonly string[] EndValueRectCandidates = { "endValueRect" };

        /// <summary>
        /// Field names that MUST be modified via dedicated skills (set_duration,
        /// set_ease, set_loops). The generic set_animation_field rejects these.
        /// </summary>
        public static readonly HashSet<string> ReservedByDedicatedSkills =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "duration", "ease", "easeType", "easeCurve", "customCurve",
                "loops", "loopType"
            };

        // ==================================================================================
        // Field Access (reflection)
        // ==================================================================================

        private const BindingFlags InstanceFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        public static FieldInfo ResolveField(Type type, string[] candidates)
        {
            if (type == null || candidates == null) return null;
            foreach (var name in candidates)
            {
                var f = type.GetField(name, InstanceFlags);
                if (f != null) return f;
            }
            return null;
        }

        public static FieldInfo ResolveField(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            return type.GetField(name, InstanceFlags);
        }

        public static bool SetFieldByCandidates(object instance, string[] candidates, object value)
        {
            if (instance == null) return false;
            var field = ResolveField(instance.GetType(), candidates);
            if (field == null) return false;
            var converted = ConvertValue(value, field.FieldType);
            if (converted == null && value != null) return false;
            field.SetValue(instance, converted);
            return true;
        }

        public static bool SetFieldByName(object instance, string fieldName, object value)
        {
            if (instance == null || string.IsNullOrEmpty(fieldName)) return false;
            var field = ResolveField(instance.GetType(), fieldName);
            if (field == null) return false;
            var converted = ConvertValue(value, field.FieldType);
            if (converted == null && value != null) return false;
            field.SetValue(instance, converted);
            return true;
        }

        public static object GetFieldByCandidates(object instance, string[] candidates)
        {
            if (instance == null) return null;
            var field = ResolveField(instance.GetType(), candidates);
            return field?.GetValue(instance);
        }

        public static Dictionary<string, object> DumpAllFields(object instance)
        {
            var result = new Dictionary<string, object>();
            if (instance == null) return result;

            foreach (var f in instance.GetType().GetFields(InstanceFlags))
            {
                if (f.IsInitOnly) continue;
                try
                {
                    var val = f.GetValue(instance);
                    result[f.Name] = StringifyForPayload(val);
                }
                catch { /* skip fields that can't be read */ }
            }
            return result;
        }

        // ==================================================================================
        // Ease & Loop Parsing (enum-by-name with curve fallback)
        // ==================================================================================

        public static bool TrySetEase(object animInstance, string easeName, string easeCurveJson)
        {
            if (animInstance == null) return false;

            if (!string.IsNullOrEmpty(easeCurveJson))
            {
                var curve = ParseAnimationCurve(easeCurveJson);
                if (curve != null)
                {
                    SetFieldByCandidates(animInstance, EaseCurveFieldCandidates, curve);
                    var easeField = ResolveField(animInstance.GetType(), EaseFieldCandidates);
                    if (easeField != null && easeField.FieldType.IsEnum)
                    {
                        try
                        {
                            var customValue = Enum.Parse(easeField.FieldType, "INTERNAL_Custom", ignoreCase: true);
                            easeField.SetValue(animInstance, customValue);
                            return true;
                        }
                        catch { /* INTERNAL_Custom may not exist on very old versions */ }
                    }
                }
            }

            if (string.IsNullOrEmpty(easeName)) return false;

            var field = ResolveField(animInstance.GetType(), EaseFieldCandidates);
            if (field == null || !field.FieldType.IsEnum) return false;

            try
            {
                var val = Enum.Parse(field.FieldType, easeName, ignoreCase: true);
                field.SetValue(animInstance, val);
                return true;
            }
            catch { return false; }
        }

        public static bool TrySetLoopType(object animInstance, string loopTypeName)
        {
            if (animInstance == null || string.IsNullOrEmpty(loopTypeName)) return false;
            var field = ResolveField(animInstance.GetType(), LoopTypeFieldCandidates);
            if (field == null || !field.FieldType.IsEnum) return false;
            try
            {
                var val = Enum.Parse(field.FieldType, loopTypeName, ignoreCase: true);
                field.SetValue(animInstance, val);
                return true;
            }
            catch { return false; }
        }

        public static bool TrySetAnimationType(object animInstance, string animationTypeName)
        {
            if (animInstance == null || string.IsNullOrEmpty(animationTypeName)) return false;
            var field = ResolveField(animInstance.GetType(), AnimationTypeFieldCandidates);
            if (field == null || !field.FieldType.IsEnum) return false;
            try
            {
                var val = Enum.Parse(field.FieldType, animationTypeName, ignoreCase: true);
                field.SetValue(animInstance, val);
                return true;
            }
            catch { return false; }
        }

        // ==================================================================================
        // animationType → endValue routing
        // ==================================================================================

        private static readonly HashSet<string> _vec3AnimTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Move", "LocalMove", "Rotate", "LocalRotate", "Scale",
            "PunchPosition", "PunchRotation", "PunchScale",
            "ShakePosition", "ShakeRotation", "ShakeScale",
            "AnchorPos3D"
        };

        private static readonly HashSet<string> _vec2AnimTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AnchorPos", "UIWidthHeight"
        };

        private static readonly HashSet<string> _floatAnimTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Fade", "FillAmount", "CameraOrthoSize", "CameraFieldOfView", "Value"
        };

        private static readonly HashSet<string> _colorAnimTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Color", "CameraBackgroundColor"
        };

        private static readonly HashSet<string> _stringAnimTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Text"
        };

        private static readonly HashSet<string> _rectAnimTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "UIRect"
        };

        /// <summary>
        /// Routes a string end-value into the correct endValueXxx field based on animationType.
        /// Returns (success, error).
        /// </summary>
        public static (bool ok, string error) ApplyEndValue(
            object animInstance,
            string animationType,
            string endValueV3Str,
            float? endValueFloat,
            string endValueColorStr,
            string endValueV2Str,
            string endValueStringValue,
            string endValueRectStr)
        {
            if (animInstance == null) return (false, "animation instance is null");
            if (string.IsNullOrEmpty(animationType)) return (false, "animationType is required");

            if (_vec3AnimTypes.Contains(animationType))
            {
                if (string.IsNullOrEmpty(endValueV3Str))
                    return (false, $"animationType '{animationType}' requires endValueV3 (e.g. \"1,2,3\")");
                if (!TryParseVector3(endValueV3Str, out var v3))
                    return (false, $"Failed to parse endValueV3 '{endValueV3Str}'");
                if (!SetFieldByCandidates(animInstance, EndValueV3Candidates, v3))
                    return (false, "endValueV3 field not found on DOTweenAnimation");
                return (true, null);
            }
            if (_vec2AnimTypes.Contains(animationType))
            {
                var src = !string.IsNullOrEmpty(endValueV2Str) ? endValueV2Str : endValueV3Str;
                if (string.IsNullOrEmpty(src))
                    return (false, $"animationType '{animationType}' requires endValueV2 (e.g. \"1,2\")");
                if (!TryParseVector2(src, out var v2))
                    return (false, $"Failed to parse endValueV2 '{src}'");
                if (!SetFieldByCandidates(animInstance, EndValueV2Candidates, v2))
                    return (false, "endValueV2 field not found");
                return (true, null);
            }
            if (_floatAnimTypes.Contains(animationType))
            {
                if (!endValueFloat.HasValue)
                    return (false, $"animationType '{animationType}' requires endValueFloat");
                if (!SetFieldByCandidates(animInstance, EndValueFloatCandidates, endValueFloat.Value))
                    return (false, "endValueFloat field not found");
                return (true, null);
            }
            if (_colorAnimTypes.Contains(animationType))
            {
                if (string.IsNullOrEmpty(endValueColorStr))
                    return (false, $"animationType '{animationType}' requires endValueColor (e.g. \"#FF8800\" or \"1,0.5,0,1\")");
                if (!TryParseColor(endValueColorStr, out var c))
                    return (false, $"Failed to parse endValueColor '{endValueColorStr}'");
                if (!SetFieldByCandidates(animInstance, EndValueColorCandidates, c))
                    return (false, "endValueColor field not found");
                return (true, null);
            }
            if (_stringAnimTypes.Contains(animationType))
            {
                if (endValueStringValue == null)
                    return (false, $"animationType '{animationType}' requires endValueString");
                if (!SetFieldByCandidates(animInstance, EndValueStringCandidates, endValueStringValue))
                    return (false, "endValueString field not found");
                return (true, null);
            }
            if (_rectAnimTypes.Contains(animationType))
            {
                if (string.IsNullOrEmpty(endValueRectStr))
                    return (false, $"animationType '{animationType}' requires endValueRect (e.g. \"x,y,width,height\")");
                if (!TryParseRect(endValueRectStr, out var r))
                    return (false, $"Failed to parse endValueRect '{endValueRectStr}'");
                if (!SetFieldByCandidates(animInstance, EndValueRectCandidates, r))
                    return (false, "endValueRect field not found");
                return (true, null);
            }

            return (false, $"Unknown animationType '{animationType}'");
        }

        // ==================================================================================
        // Value Conversion
        // ==================================================================================

        public static object ConvertValue(object value, Type targetType)
        {
            if (value == null) return null;
            if (targetType.IsInstanceOfType(value)) return value;

            if (targetType.IsEnum)
            {
                if (value is string s)
                {
                    try { return Enum.Parse(targetType, s, ignoreCase: true); }
                    catch { return null; }
                }
                try { return Enum.ToObject(targetType, Convert.ChangeType(value, Enum.GetUnderlyingType(targetType))); }
                catch { return null; }
            }

            if (value is string str)
            {
                if (targetType == typeof(Vector3) && TryParseVector3(str, out var v3)) return v3;
                if (targetType == typeof(Vector2) && TryParseVector2(str, out var v2)) return v2;
                if (targetType == typeof(Color) && TryParseColor(str, out var col)) return col;
                if (targetType == typeof(Rect) && TryParseRect(str, out var rect)) return rect;
                if (targetType == typeof(bool) && bool.TryParse(str, out var b)) return b;
                if (targetType == typeof(float) && float.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var fl)) return fl;
                if (targetType == typeof(int) && int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv)) return iv;
            }

            try { return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        private static bool TryParseVector3(string s, out Vector3 v)
        {
            v = default;
            if (string.IsNullOrEmpty(s)) return false;
            var trimmed = s.Trim().TrimStart('[', '(').TrimEnd(']', ')');
            var parts = trimmed.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return false;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return false;
            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) return false;
            v = new Vector3(x, y, z);
            return true;
        }

        private static bool TryParseVector2(string s, out Vector2 v)
        {
            v = default;
            if (string.IsNullOrEmpty(s)) return false;
            var trimmed = s.Trim().TrimStart('[', '(').TrimEnd(']', ')');
            var parts = trimmed.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return false;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return false;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return false;
            v = new Vector2(x, y);
            return true;
        }

        private static bool TryParseColor(string s, out Color c)
        {
            c = default;
            if (string.IsNullOrEmpty(s)) return false;
            var str = s.Trim();
            if (str.StartsWith("#") || str.Length == 6 || str.Length == 8)
            {
                if (ColorUtility.TryParseHtmlString(str.StartsWith("#") ? str : "#" + str, out c)) return true;
            }
            var trimmed = str.TrimStart('[', '(').TrimEnd(']', ')');
            var parts = trimmed.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var r)) return false;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var g)) return false;
            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) return false;
            float a = 1f;
            if (parts.Length >= 4 && !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out a)) a = 1f;
            c = new Color(r, g, b, a);
            return true;
        }

        private static bool TryParseRect(string s, out Rect r)
        {
            r = default;
            if (string.IsNullOrEmpty(s)) return false;
            var trimmed = s.Trim().TrimStart('[', '(').TrimEnd(']', ')');
            var parts = trimmed.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) return false;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return false;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return false;
            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)) return false;
            if (!float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) return false;
            r = new Rect(x, y, w, h);
            return true;
        }

        private static AnimationCurve ParseAnimationCurve(string json)
        {
            try
            {
                var curve = JsonUtility.FromJson<AnimationCurve>(json);
                if (curve != null && curve.length > 0) return curve;
            }
            catch { /* fall through to manual parse */ }

            try
            {
                var parsed = Newtonsoft.Json.Linq.JArray.Parse(json);
                var keys = parsed
                    .Select(k => new Keyframe(
                        (float)k["time"],
                        (float)k["value"],
                        k["inTangent"] != null ? (float)k["inTangent"] : 0f,
                        k["outTangent"] != null ? (float)k["outTangent"] : 0f))
                    .ToArray();
                if (keys.Length > 0) return new AnimationCurve(keys);
            }
            catch { /* not JSON array either */ }

            return null;
        }

        private static object StringifyForPayload(object val)
        {
            if (val == null) return null;
            if (val is UnityEngine.Object uo) return uo != null ? uo.name : null;
            if (val is Vector3 v3) return $"{v3.x},{v3.y},{v3.z}";
            if (val is Vector2 v2) return $"{v2.x},{v2.y}";
            if (val is Color c) return $"#{ColorUtility.ToHtmlStringRGBA(c)}";
            if (val is Rect r) return $"{r.x},{r.y},{r.width},{r.height}";
            if (val is Enum e) return e.ToString();
            if (val is AnimationCurve ac) return $"AnimationCurve({ac.length} keys)";
            var t = val.GetType();
            if (t.IsPrimitive || t == typeof(string) || t == typeof(decimal)) return val;
            return val.ToString();
        }
    }
}
