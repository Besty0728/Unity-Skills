#if COM_UNITY_MODULES_PARTICLESYSTEM
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    public static partial class ParticleSkills
    {
        internal const int MaxCurveKeys = 64;
        internal const int MaxGradientKeys = 8;
        internal const int MaxBursts = 8;
        internal const float DefaultKeyWeight = 1f / 3f;
        internal const float DefaultBurstRepeatInterval = 0.01f;

        /// <summary>
        /// Reads and writes every value shape of the particle contract. Reading is strict and path-aware: unknown keys,
        /// duplicate keys, wrong JSON types, non-finite numbers, out-of-range values and illegal enum names are all
        /// reported with the exact JSON path, and nothing is defaulted silently except the documented optional keys.
        /// Writing produces the one canonical form readback returns, which is also accepted back as input.
        /// </summary>
        internal static class Codec
        {
            // ===================== JSON text =====================

            /// <summary>Parses JSON text, rejecting duplicate keys (which a plain parse would silently collapse).</summary>
            public static JToken ParseText(string text, string path, Problems problems)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    problems.Add(path, $"{path} is empty; send a JSON value.", CodeMissingParam);
                    return null;
                }

                try
                {
                    using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double })
                    {
                        var token = JToken.ReadFrom(reader, new JsonLoadSettings
                        {
                            DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                            CommentHandling = CommentHandling.Ignore,
                            LineInfoHandling = LineInfoHandling.Ignore
                        });
                        if (reader.Read() && reader.TokenType != JsonToken.Comment)
                        {
                            problems.Add(path, $"{path} has content after the JSON value; send exactly one JSON value.", CodeInvalidJson);
                            return null;
                        }
                        return token;
                    }
                }
                catch (JsonReaderException ex)
                {
                    var inner = string.IsNullOrEmpty(ex.Path) ? path : path + "." + ex.Path;
                    var message = ex.Message.IndexOf("already exists", StringComparison.OrdinalIgnoreCase) >= 0
                        ? $"Duplicate key in {path}: {ex.Message} Every key may appear once; merge the duplicates."
                        : $"{path} is not valid JSON: {ex.Message}";
                    problems.Add(inner, message, CodeInvalidJson);
                    return null;
                }
            }

            /// <summary>A JSON payload may arrive as an object/array or as a string holding it; both mean the same.</summary>
            public static JToken Normalize(JToken token, string path, Problems problems)
            {
                if (token == null)
                    return null;
                if (token.Type == JTokenType.String)
                    return ParseText(token.Value<string>(), path, problems);
                return token;
            }

            public static string Describe(JToken token)
            {
                if (token == null)
                    return "nothing";
                switch (token.Type)
                {
                    case JTokenType.Object: return "an object";
                    case JTokenType.Array: return "an array";
                    case JTokenType.String: return $"the string {token.ToString(Formatting.None)}";
                    case JTokenType.Null: return "null";
                    case JTokenType.Boolean: return token.Value<bool>() ? "true" : "false";
                    default: return token.ToString(Formatting.None);
                }
            }

            public static JObject ExpectObject(JToken token, string path, Problems problems, string what)
            {
                if (token is JObject obj)
                    return obj;
                problems.Add(path, $"{path} must be {what}, got {Describe(token)}.", CodeTypeMismatch);
                return null;
            }

            /// <summary>Rejects unknown keys (with the closest valid one) and reports missing required keys. True when no key problem.</summary>
            public static bool CheckKeys(JObject obj, string path, Problems problems, IReadOnlyCollection<string> allowed, IReadOnlyCollection<string> required)
            {
                bool ok = true;
                foreach (var property in obj.Properties())
                {
                    if (allowed.Contains(property.Name))
                        continue;
                    ok = false;
                    problems.Add(Join(path, property.Name), UnknownKeyMessage(property.Name, path, allowed));
                }

                if (required != null)
                {
                    foreach (var key in required)
                    {
                        if (obj.Property(key) != null)
                            continue;
                        ok = false;
                        problems.Add(Join(path, key), $"{Join(path, key)} is required.", CodeMissingParam);
                    }
                }
                return ok;
            }

            public static string UnknownKeyMessage(string key, string path, IEnumerable<string> allowed)
            {
                var list = allowed.ToArray();
                var closest = SkillsCommon.ClosestMatch(key, list);
                var where = string.IsNullOrEmpty(path) ? "" : $" in {path}";
                return $"Unknown key '{key}'{where}" +
                       (closest != null ? $" (did you mean '{closest}'?)" : "") +
                       $". Valid keys: {string.Join(", ", list)}.";
            }

            public static string Join(string path, string key) => string.IsNullOrEmpty(path) ? key : path + "." + key;

            public static string Index(string path, int index) => path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";

            // ===================== Scalars =====================

            public static bool ReadBool(JToken token, string path, Problems problems, out bool value)
            {
                value = false;
                if (token != null && token.Type == JTokenType.Boolean)
                {
                    value = token.Value<bool>();
                    return true;
                }
                problems.Add(path, $"{path} must be true or false, got {Describe(token)}.", CodeTypeMismatch);
                return false;
            }

            public static bool ReadDouble(JToken token, string path, Problems problems, out double value)
            {
                value = 0;
                if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
                {
                    problems.Add(path, $"{path} must be a number, got {Describe(token)}.", CodeTypeMismatch);
                    return false;
                }

                try { value = token.Value<double>(); }
                catch (Exception)
                {
                    problems.Add(path, $"{path} is not a representable number: {token.ToString(Formatting.None)}.", CodeTypeMismatch);
                    return false;
                }

                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    problems.Add(path, $"{path} must be a finite number, got {token.ToString(Formatting.None)}.");
                    return false;
                }
                return true;
            }

            /// <summary>A finite float within [min, max]; the bounds are optional and <paramref name="minExclusive"/> makes the lower one strict.</summary>
            public static bool ReadFloat(JToken token, string path, Problems problems, out float value,
                double? min = null, double? max = null, bool minExclusive = false)
            {
                value = 0f;
                if (!ReadDouble(token, path, problems, out var number))
                    return false;
                if (Math.Abs(number) > float.MaxValue)
                {
                    problems.Add(path, $"{path} is outside the float range: {FormatNumber(number)}.");
                    return false;
                }
                if (!CheckBounds(number, path, problems, min, max, minExclusive))
                    return false;
                value = (float)number;
                return true;
            }

            public static bool CheckBounds(double number, string path, Problems problems, double? min, double? max, bool minExclusive)
            {
                if (min.HasValue && (minExclusive ? number <= min.Value : number < min.Value))
                {
                    problems.Add(path, $"{path} must be {(minExclusive ? ">" : ">=")} {FormatNumber(min.Value)}{(max.HasValue ? $" and <= {FormatNumber(max.Value)}" : "")}, got {FormatNumber(number)}.");
                    return false;
                }
                if (max.HasValue && number > max.Value)
                {
                    problems.Add(path, $"{path} must be <= {FormatNumber(max.Value)}{(min.HasValue ? $" and {(minExclusive ? ">" : ">=")} {FormatNumber(min.Value)}" : "")}, got {FormatNumber(number)}.");
                    return false;
                }
                return true;
            }

            public static bool ReadInt(JToken token, string path, Problems problems, out int value, double? min = null, double? max = null)
            {
                value = 0;
                if (token == null || token.Type != JTokenType.Integer)
                {
                    var hint = token != null && token.Type == JTokenType.Float ? " (whole numbers only)" : "";
                    problems.Add(path, $"{path} must be an integer{hint}, got {Describe(token)}.", CodeTypeMismatch);
                    return false;
                }

                long raw;
                try { raw = token.Value<long>(); }
                catch (Exception)
                {
                    problems.Add(path, $"{path} is outside the integer range: {token.ToString(Formatting.None)}.");
                    return false;
                }

                if (raw < int.MinValue || raw > int.MaxValue)
                {
                    problems.Add(path, $"{path} is outside the 32-bit integer range: {raw}.");
                    return false;
                }
                if (!CheckBounds(raw, path, problems, min, max, false))
                    return false;
                value = (int)raw;
                return true;
            }

            public static bool ReadUInt(JToken token, string path, Problems problems, out uint value)
            {
                value = 0;
                if (token == null || token.Type != JTokenType.Integer)
                {
                    problems.Add(path, $"{path} must be an integer in 0..4294967295, got {Describe(token)}.", CodeTypeMismatch);
                    return false;
                }

                long raw;
                try { raw = token.Value<long>(); }
                catch (Exception)
                {
                    problems.Add(path, $"{path} must be in 0..4294967295, got {token.ToString(Formatting.None)}.");
                    return false;
                }

                if (raw < 0 || raw > uint.MaxValue)
                {
                    problems.Add(path, $"{path} must be in 0..4294967295, got {raw}.");
                    return false;
                }
                value = (uint)raw;
                return true;
            }

            /// <summary>An enum member by name, case-insensitive; numbers are rejected so a typo never lands on a member by value.</summary>
            public static bool ReadEnum(JToken token, string path, Problems problems, Type enumType, string[] allowed, out object value)
            {
                value = null;
                var names = allowed ?? LiveEnumNames(enumType);
                if (token == null || token.Type != JTokenType.String)
                {
                    problems.Add(path, $"{path} must be one of {string.Join(", ", names)} (a string), got {Describe(token)}.", CodeTypeMismatch);
                    return false;
                }

                var text = token.Value<string>().Trim();
                var match = names.FirstOrDefault(name => string.Equals(name, text, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    var known = LiveEnumNames(enumType).FirstOrDefault(name => string.Equals(name, text, StringComparison.OrdinalIgnoreCase));
                    var why = known != null ? $" '{known}' is a Unity value this field does not accept on write." : "";
                    problems.Add(path, $"Invalid value '{text}' for {path}.{why} Valid values: {string.Join(", ", names)}.");
                    return false;
                }

                value = Enum.Parse(enumType, match);
                return true;
            }

            /// <summary>Non-obsolete member names in declaration order, one name per value (the first declared wins).</summary>
            public static string[] LiveEnumNames(Type enumType)
            {
                var seen = new HashSet<long>();
                var names = new List<string>();
                foreach (var field in enumType.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    if (field.GetCustomAttributes(typeof(ObsoleteAttribute), false).Length > 0)
                        continue;
                    var raw = Convert.ToInt64(field.GetRawConstantValue(), CultureInfo.InvariantCulture);
                    if (seen.Add(raw))
                        names.Add(field.Name);
                }
                return names.ToArray();
            }

            public static string FormatNumber(double value) => value.ToString("R", CultureInfo.InvariantCulture);

            // ===================== Vectors and colours =====================

            public static bool ReadVector3(JToken token, string path, Problems problems, out Vector3 value, double? min = null, double? max = null)
            {
                value = default;
                var obj = ExpectObject(token, path, problems, "an object {x, y, z}");
                if (obj == null || !CheckKeys(obj, path, problems, Xyz, Xyz))
                    return false;
                bool ok = ReadFloat(obj["x"], Join(path, "x"), problems, out var x, min, max);
                ok &= ReadFloat(obj["y"], Join(path, "y"), problems, out var y, min, max);
                ok &= ReadFloat(obj["z"], Join(path, "z"), problems, out var z, min, max);
                value = new Vector3(x, y, z);
                return ok;
            }

            public static bool ReadVector2(JToken token, string path, Problems problems, out Vector2 value, double? min = null, double? max = null)
            {
                value = default;
                var obj = ExpectObject(token, path, problems, "an object {x, y}");
                if (obj == null || !CheckKeys(obj, path, problems, Xy, Xy))
                    return false;
                bool ok = ReadFloat(obj["x"], Join(path, "x"), problems, out var x, min, max);
                ok &= ReadFloat(obj["y"], Join(path, "y"), problems, out var y, min, max);
                value = new Vector2(x, y);
                return ok;
            }

            /// <summary>
            /// An HDR colour {r, g, b, a}: channels are linear floats as Unity stores them (no gamma conversion), r/g/b may
            /// exceed 1, alpha is 0-1 and defaults to 1. <paramref name="allowAlpha"/> false is for gradient colour keys,
            /// whose alpha Unity discards (alpha belongs in alphaKeys).
            /// </summary>
            public static bool ReadColor(JToken token, string path, Problems problems, out Color value, bool allowAlpha = true)
            {
                value = default;
                var obj = ExpectObject(token, path, problems, allowAlpha ? "an object {r, g, b, a}" : "an object {r, g, b}");
                if (obj == null)
                    return false;
                if (!allowAlpha && obj.Property("a") != null)
                {
                    problems.Add(Join(path, "a"), $"{Join(path, "a")} is not accepted: Unity discards the alpha of gradient colour keys. Put opacity in the gradient's alphaKeys.");
                    return false;
                }
                if (!CheckKeys(obj, path, problems, allowAlpha ? Rgba : Rgb, Rgb))
                    return false;
                bool ok = ReadFloat(obj["r"], Join(path, "r"), problems, out var r, 0, null);
                ok &= ReadFloat(obj["g"], Join(path, "g"), problems, out var g, 0, null);
                ok &= ReadFloat(obj["b"], Join(path, "b"), problems, out var b, 0, null);
                float a = 1f;
                if (allowAlpha && obj.Property("a") != null)
                    ok &= ReadFloat(obj["a"], Join(path, "a"), problems, out a, 0, 1);
                value = new Color(r, g, b, a);
                return ok;
            }

            private static readonly string[] Xyz = { "x", "y", "z" };
            private static readonly string[] Xy = { "x", "y" };
            private static readonly string[] Rgb = { "r", "g", "b" };
            private static readonly string[] Rgba = { "r", "g", "b", "a" };

            // ===================== MinMaxCurve =====================

            private static readonly string[] CurveModeNames = { "Constant", "TwoConstants", "Curve", "TwoCurves" };

            /// <summary>
            /// A MinMaxCurve in the canonical shape ({mode, constant} / {mode, min, max} / {mode, multiplier, curve} /
            /// {mode, multiplier, minCurve, maxCurve}), or a bare number as shorthand for Constant. The value always
            /// replaces the whole property; the bounds of <paramref name="field"/> apply to the constants.
            /// </summary>
            public static bool ReadCurve(FieldSpec field, JToken token, string path, Problems problems, out ParticleSystem.MinMaxCurve value)
            {
                value = default;
                var allowedModes = field.CurveModes ?? AllCurveModes;
                if (token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float))
                {
                    if (!allowedModes.Contains(ParticleSystemCurveMode.Constant))
                    {
                        problems.Add(path, $"{path} does not accept a constant (a bare number means mode Constant). Accepted modes: {string.Join(", ", allowedModes)}.");
                        return false;
                    }
                    if (!ReadFloat(token, path, problems, out var constant, field.Min, field.Max, field.MinExclusive))
                        return false;
                    value = new ParticleSystem.MinMaxCurve(constant);
                    return true;
                }

                var obj = ExpectObject(token, path, problems, "a MinMaxCurve object ({\"mode\": \"Constant\", \"constant\": 1}) or a number");
                if (obj == null)
                    return false;

                var modePath = Join(path, "mode");
                if (obj.Property("mode") == null)
                {
                    problems.Add(modePath, $"{modePath} is required: one of {string.Join(", ", allowedModes)}.", CodeMissingParam);
                    return false;
                }
                if (!ReadEnum(obj["mode"], modePath, problems, typeof(ParticleSystemCurveMode), allowedModes.Select(m => m.ToString()).ToArray(), out var modeObj))
                    return false;
                var mode = (ParticleSystemCurveMode)modeObj;

                switch (mode)
                {
                    case ParticleSystemCurveMode.Constant:
                    {
                        if (!CheckKeys(obj, path, problems, new[] { "mode", "constant" }, new[] { "constant" }))
                            return false;
                        if (!ReadFloat(obj["constant"], Join(path, "constant"), problems, out var constant, field.Min, field.Max, field.MinExclusive))
                            return false;
                        value = new ParticleSystem.MinMaxCurve(constant);
                        return true;
                    }
                    case ParticleSystemCurveMode.TwoConstants:
                    {
                        if (!CheckKeys(obj, path, problems, new[] { "mode", "min", "max" }, new[] { "min", "max" }))
                            return false;
                        bool ok = ReadFloat(obj["min"], Join(path, "min"), problems, out var min, field.Min, field.Max, field.MinExclusive);
                        ok &= ReadFloat(obj["max"], Join(path, "max"), problems, out var max, field.Min, field.Max, field.MinExclusive);
                        if (!ok)
                            return false;
                        if (min > max)
                        {
                            problems.Add(Join(path, "min"), $"{Join(path, "min")} ({FormatNumber(min)}) must be <= {Join(path, "max")} ({FormatNumber(max)}).");
                            return false;
                        }
                        value = new ParticleSystem.MinMaxCurve(min, max);
                        return true;
                    }
                    case ParticleSystemCurveMode.Curve:
                    {
                        if (!CheckKeys(obj, path, problems, new[] { "mode", "multiplier", "curve" }, new[] { "multiplier", "curve" }))
                            return false;
                        bool ok = ReadFloat(obj["multiplier"], Join(path, "multiplier"), problems, out var multiplier);
                        var curve = ReadAnimationCurve(obj["curve"], Join(path, "curve"), problems, field);
                        if (!ok || curve == null)
                            return false;
                        value = new ParticleSystem.MinMaxCurve(multiplier, curve);
                        return true;
                    }
                    default:
                    {
                        if (!CheckKeys(obj, path, problems, new[] { "mode", "multiplier", "minCurve", "maxCurve" }, new[] { "multiplier", "minCurve", "maxCurve" }))
                            return false;
                        bool ok = ReadFloat(obj["multiplier"], Join(path, "multiplier"), problems, out var multiplier);
                        var minCurve = ReadAnimationCurve(obj["minCurve"], Join(path, "minCurve"), problems, field);
                        var maxCurve = ReadAnimationCurve(obj["maxCurve"], Join(path, "maxCurve"), problems, field);
                        if (!ok || minCurve == null || maxCurve == null)
                            return false;
                        value = new ParticleSystem.MinMaxCurve(multiplier, minCurve, maxCurve);
                        return true;
                    }
                }
            }

            internal static readonly ParticleSystemCurveMode[] AllCurveModes =
            {
                ParticleSystemCurveMode.Constant, ParticleSystemCurveMode.TwoConstants,
                ParticleSystemCurveMode.Curve, ParticleSystemCurveMode.TwoCurves
            };

            private static readonly string[] CurveKeys = { "preWrapMode", "postWrapMode", "keys" };
            private static readonly string[] KeyframeKeys =
            {
                "time", "value", "inTangent", "outTangent", "inWeight", "outWeight", "weightedMode",
                "leftTangentMode", "rightTangentMode", "broken"
            };

            /// <summary>
            /// {preWrapMode?, postWrapMode?, keys:[...]}. Keys are sorted by time; two keys at one time are rejected. A side
            /// whose tangent mode is not Free gets its tangent computed by Unity (Constant = stepped, with no tangent value
            /// in JSON); Unity also marks a key broken when either side is Linear or Constant.
            /// </summary>
            public static AnimationCurve ReadAnimationCurve(JToken token, string path, Problems problems, FieldSpec field)
            {
                var obj = ExpectObject(token, path, problems, "an AnimationCurve object {keys:[...]}");
                if (obj == null || !CheckKeys(obj, path, problems, CurveKeys, new[] { "keys" }))
                    return null;

                bool ok = true;
                var pre = WrapMode.ClampForever;
                var post = WrapMode.ClampForever;
                if (obj.Property("preWrapMode") != null)
                    ok &= ReadWrapMode(obj["preWrapMode"], Join(path, "preWrapMode"), problems, out pre);
                if (obj.Property("postWrapMode") != null)
                    ok &= ReadWrapMode(obj["postWrapMode"], Join(path, "postWrapMode"), problems, out post);

                var keysPath = Join(path, "keys");
                if (!(obj["keys"] is JArray keys))
                {
                    problems.Add(keysPath, $"{keysPath} must be an array of keyframes, got {Describe(obj["keys"])}.", CodeTypeMismatch);
                    return null;
                }
                if (keys.Count < 1 || keys.Count > MaxCurveKeys)
                {
                    problems.Add(keysPath, $"{keysPath} must hold 1 to {MaxCurveKeys} keyframes, got {keys.Count}.");
                    return null;
                }

                var parsed = new List<(int index, Keyframe key, AnimationUtility.TangentMode left, AnimationUtility.TangentMode right, bool broken)>();
                for (int i = 0; i < keys.Count; i++)
                {
                    var keyPath = Index(keysPath, i);
                    if (ReadKeyframe(keys[i], keyPath, problems, field, out var key, out var left, out var right, out var broken))
                        parsed.Add((i, key, left, right, broken));
                    else
                        ok = false;
                }
                if (!ok)
                    return null;

                var sorted = parsed.OrderBy(entry => entry.key.time).ToList();
                for (int i = 1; i < sorted.Count; i++)
                {
                    if (sorted[i].key.time == sorted[i - 1].key.time)
                    {
                        problems.Add(Index(keysPath, sorted[i].index),
                            $"{Index(keysPath, sorted[i].index)}.time {FormatNumber(sorted[i].key.time)} repeats the time of {Index(keysPath, sorted[i - 1].index)}; two keys at one time are not accepted.");
                        ok = false;
                    }
                }
                if (!ok)
                    return null;

                var curve = new AnimationCurve(sorted.Select(entry => entry.key).ToArray())
                {
                    preWrapMode = pre,
                    postWrapMode = post
                };
                for (int i = 0; i < sorted.Count; i++)
                {
                    AnimationUtility.SetKeyBroken(curve, i, sorted[i].broken);
                    AnimationUtility.SetKeyLeftTangentMode(curve, i, sorted[i].left);
                    AnimationUtility.SetKeyRightTangentMode(curve, i, sorted[i].right);
                }
                return curve;
            }

            private static bool ReadKeyframe(JToken token, string path, Problems problems, FieldSpec field, out Keyframe key,
                out AnimationUtility.TangentMode left, out AnimationUtility.TangentMode right, out bool broken)
            {
                key = default;
                left = AnimationUtility.TangentMode.Free;
                right = AnimationUtility.TangentMode.Free;
                broken = false;
                var obj = ExpectObject(token, path, problems, "a keyframe object {time, value, ...}");
                if (obj == null || !CheckKeys(obj, path, problems, KeyframeKeys, new[] { "time", "value" }))
                    return false;

                bool ok = ReadFloat(obj["time"], Join(path, "time"), problems, out var time, field?.KeyTimeMin ?? 0f, field?.KeyTimeMax ?? 1f);
                ok &= ReadFloat(obj["value"], Join(path, "value"), problems, out var value);

                if (obj.Property("leftTangentMode") != null)
                    ok &= ReadTangentMode(obj["leftTangentMode"], Join(path, "leftTangentMode"), problems, out left);
                if (obj.Property("rightTangentMode") != null)
                    ok &= ReadTangentMode(obj["rightTangentMode"], Join(path, "rightTangentMode"), problems, out right);
                if (obj.Property("broken") != null)
                    ok &= ReadBool(obj["broken"], Join(path, "broken"), problems, out broken);

                float inTangent = 0f, outTangent = 0f;
                ok &= ReadTangent(obj, "inTangent", path, problems, left, "leftTangentMode", out inTangent);
                ok &= ReadTangent(obj, "outTangent", path, problems, right, "rightTangentMode", out outTangent);

                float inWeight = DefaultKeyWeight, outWeight = DefaultKeyWeight;
                if (obj.Property("inWeight") != null)
                    ok &= ReadFloat(obj["inWeight"], Join(path, "inWeight"), problems, out inWeight, 0, 1);
                if (obj.Property("outWeight") != null)
                    ok &= ReadFloat(obj["outWeight"], Join(path, "outWeight"), problems, out outWeight, 0, 1);

                var weightedMode = WeightedMode.None;
                if (obj.Property("weightedMode") != null)
                {
                    ok &= ReadEnum(obj["weightedMode"], Join(path, "weightedMode"), problems, typeof(WeightedMode), null, out var weighted);
                    if (weighted != null)
                        weightedMode = (WeightedMode)weighted;
                }

                key = new Keyframe(time, value, inTangent, outTangent, inWeight, outWeight) { weightedMode = weightedMode };
                return ok;
            }

            /// <summary>A Constant (stepped) side has an infinite tangent, which JSON cannot carry: the value must be omitted there.</summary>
            private static bool ReadTangent(JObject obj, string name, string path, Problems problems, AnimationUtility.TangentMode mode, string modeKey, out float tangent)
            {
                tangent = 0f;
                var property = obj.Property(name);
                if (property == null)
                    return true;
                if (mode == AnimationUtility.TangentMode.Constant)
                {
                    if (property.Value.Type == JTokenType.Null)
                        return true;
                    problems.Add(Join(path, name), $"{Join(path, name)} must be omitted when {modeKey} is Constant: a stepped side has an infinite tangent, which Unity sets itself.");
                    return false;
                }
                return ReadFloat(property.Value, Join(path, name), problems, out tangent);
            }

            private static bool ReadTangentMode(JToken token, string path, Problems problems, out AnimationUtility.TangentMode mode)
            {
                mode = AnimationUtility.TangentMode.Free;
                if (!ReadEnum(token, path, problems, typeof(AnimationUtility.TangentMode), null, out var parsed))
                    return false;
                mode = (AnimationUtility.TangentMode)parsed;
                return true;
            }

            private static readonly string[] WrapModeNames = { "Default", "Clamp", "Loop", "PingPong", "ClampForever" };

            private static bool ReadWrapMode(JToken token, string path, Problems problems, out WrapMode mode)
            {
                mode = WrapMode.ClampForever;
                if (token != null && token.Type == JTokenType.String &&
                    string.Equals(token.Value<string>().Trim(), "Once", StringComparison.OrdinalIgnoreCase))
                {
                    mode = WrapMode.Once;
                    return true;
                }
                if (!ReadEnum(token, path, problems, typeof(WrapMode), WrapModeNames, out var parsed))
                    return false;
                mode = (WrapMode)parsed;
                return true;
            }

            // ===================== MinMaxGradient =====================

            internal static readonly ParticleSystemGradientMode[] AllGradientModes =
            {
                ParticleSystemGradientMode.Color, ParticleSystemGradientMode.TwoColors, ParticleSystemGradientMode.Gradient,
                ParticleSystemGradientMode.TwoGradients, ParticleSystemGradientMode.RandomColor
            };

            /// <summary>
            /// A MinMaxGradient: {mode: Color, color} / {mode: TwoColors, minColor, maxColor} / {mode: Gradient, gradient} /
            /// {mode: TwoGradients, minGradient, maxGradient} / {mode: RandomColor, gradient}, or a bare colour object
            /// {r, g, b, a} as shorthand for mode Color.
            /// </summary>
            public static bool ReadGradient(FieldSpec field, JToken token, string path, Problems problems, out ParticleSystem.MinMaxGradient value)
            {
                value = default;
                var allowedModes = field.GradientModes ?? AllGradientModes;
                var obj = ExpectObject(token, path, problems, "a MinMaxGradient object ({\"mode\": \"Color\", \"color\": {...}})");
                if (obj == null)
                    return false;

                if (obj.Property("mode") == null && obj.Property("r") != null)
                {
                    if (!allowedModes.Contains(ParticleSystemGradientMode.Color))
                    {
                        problems.Add(path, $"{path} does not accept a single colour (a bare {{r,g,b,a}} means mode Color). Accepted modes: {string.Join(", ", allowedModes)}.");
                        return false;
                    }
                    if (!ReadColor(obj, path, problems, out var single))
                        return false;
                    value = new ParticleSystem.MinMaxGradient(single);
                    return true;
                }

                var modePath = Join(path, "mode");
                if (obj.Property("mode") == null)
                {
                    problems.Add(modePath, $"{modePath} is required: one of {string.Join(", ", allowedModes)}.", CodeMissingParam);
                    return false;
                }
                if (!ReadEnum(obj["mode"], modePath, problems, typeof(ParticleSystemGradientMode), allowedModes.Select(m => m.ToString()).ToArray(), out var modeObj))
                    return false;
                var mode = (ParticleSystemGradientMode)modeObj;

                switch (mode)
                {
                    case ParticleSystemGradientMode.Color:
                    {
                        if (!CheckKeys(obj, path, problems, new[] { "mode", "color" }, new[] { "color" }) ||
                            !ReadColor(obj["color"], Join(path, "color"), problems, out var color))
                            return false;
                        value = new ParticleSystem.MinMaxGradient(color);
                        return true;
                    }
                    case ParticleSystemGradientMode.TwoColors:
                    {
                        if (!CheckKeys(obj, path, problems, new[] { "mode", "minColor", "maxColor" }, new[] { "minColor", "maxColor" }))
                            return false;
                        bool ok = ReadColor(obj["minColor"], Join(path, "minColor"), problems, out var min);
                        ok &= ReadColor(obj["maxColor"], Join(path, "maxColor"), problems, out var max);
                        if (!ok)
                            return false;
                        value = new ParticleSystem.MinMaxGradient(min, max);
                        return true;
                    }
                    case ParticleSystemGradientMode.TwoGradients:
                    {
                        if (!CheckKeys(obj, path, problems, new[] { "mode", "minGradient", "maxGradient" }, new[] { "minGradient", "maxGradient" }))
                            return false;
                        var min = ReadGradientObject(obj["minGradient"], Join(path, "minGradient"), problems);
                        var max = ReadGradientObject(obj["maxGradient"], Join(path, "maxGradient"), problems);
                        if (min == null || max == null)
                            return false;
                        value = new ParticleSystem.MinMaxGradient(min, max);
                        return true;
                    }
                    default:
                    {
                        if (!CheckKeys(obj, path, problems, new[] { "mode", "gradient" }, new[] { "gradient" }))
                            return false;
                        var gradient = ReadGradientObject(obj["gradient"], Join(path, "gradient"), problems);
                        if (gradient == null)
                            return false;
                        value = new ParticleSystem.MinMaxGradient(gradient);
                        if (mode == ParticleSystemGradientMode.RandomColor)
                            value.mode = ParticleSystemGradientMode.RandomColor;
                        return true;
                    }
                }
            }

            private static readonly string[] GradientKeys = { "mode", "colorSpace", "colorKeys", "alphaKeys" };
            private static readonly string[] ColorKeyKeys = { "time", "color" };
            private static readonly string[] AlphaKeyKeys = { "time", "alpha" };

            /// <summary>
            /// {mode?, colorSpace?, colorKeys:[{time, color:{r,g,b}}], alphaKeys:[{time, alpha}]}: 1 to 8 keys of each kind,
            /// times 0-1 without repeats, colours HDR (no implicit gamma/linear conversion). Unity stores key times in
            /// steps of 1/65535, so a time reads back within 0.00002 of what was sent.
            /// </summary>
            public static Gradient ReadGradientObject(JToken token, string path, Problems problems)
            {
                var obj = ExpectObject(token, path, problems, "a Gradient object {colorKeys, alphaKeys}");
                if (obj == null || !CheckKeys(obj, path, problems, GradientKeys, new[] { "colorKeys", "alphaKeys" }))
                    return null;

                bool ok = true;
                var mode = GradientMode.Blend;
                if (obj.Property("mode") != null)
                {
                    ok &= ReadEnum(obj["mode"], Join(path, "mode"), problems, typeof(GradientMode), null, out var parsedMode);
                    if (parsedMode != null)
                        mode = (GradientMode)parsedMode;
                }

                ColorSpace? colorSpace = null;
                if (obj.Property("colorSpace") != null)
                {
                    ok &= ReadEnum(obj["colorSpace"], Join(path, "colorSpace"), problems, typeof(ColorSpace), null, out var parsedSpace);
                    if (parsedSpace != null)
                        colorSpace = (ColorSpace)parsedSpace;
                }

                var colorKeys = ReadGradientKeyList(obj["colorKeys"], Join(path, "colorKeys"), problems, ColorKeyKeys, (item, itemPath) =>
                {
                    bool keyOk = ReadFloat(item["time"], Join(itemPath, "time"), problems, out var time, 0, 1);
                    keyOk &= ReadColor(item["color"], Join(itemPath, "color"), problems, out var color, allowAlpha: false);
                    return (keyOk, time, (object)new GradientColorKey(color, time));
                });
                var alphaKeys = ReadGradientKeyList(obj["alphaKeys"], Join(path, "alphaKeys"), problems, AlphaKeyKeys, (item, itemPath) =>
                {
                    bool keyOk = ReadFloat(item["time"], Join(itemPath, "time"), problems, out var time, 0, 1);
                    keyOk &= ReadFloat(item["alpha"], Join(itemPath, "alpha"), problems, out var alpha, 0, 1);
                    return (keyOk, time, (object)new GradientAlphaKey(alpha, time));
                });
                if (!ok || colorKeys == null || alphaKeys == null)
                    return null;

                var gradient = new Gradient();
                gradient.SetKeys(colorKeys.Cast<GradientColorKey>().ToArray(), alphaKeys.Cast<GradientAlphaKey>().ToArray());
                gradient.mode = mode;
                if (colorSpace.HasValue)
                    gradient.colorSpace = colorSpace.Value;
                return gradient;
            }

            private static List<object> ReadGradientKeyList(JToken token, string path, Problems problems, string[] keyNames,
                Func<JObject, string, (bool ok, float time, object key)> readItem)
            {
                if (!(token is JArray array))
                {
                    problems.Add(path, $"{path} must be an array, got {Describe(token)}.", CodeTypeMismatch);
                    return null;
                }
                if (array.Count < 1 || array.Count > MaxGradientKeys)
                {
                    problems.Add(path, $"{path} must hold 1 to {MaxGradientKeys} keys (Unity's limit), got {array.Count}.");
                    return null;
                }

                bool ok = true;
                var items = new List<(int index, float time, object key)>();
                for (int i = 0; i < array.Count; i++)
                {
                    var itemPath = Index(path, i);
                    var item = ExpectObject(array[i], itemPath, problems, "a key object");
                    if (item == null || !CheckKeys(item, itemPath, problems, keyNames, keyNames))
                    {
                        ok = false;
                        continue;
                    }
                    var (itemOk, time, key) = readItem(item, itemPath);
                    if (itemOk)
                        items.Add((i, time, key));
                    else
                        ok = false;
                }
                if (!ok)
                    return null;

                var sorted = items.OrderBy(item => item.time).ToList();
                for (int i = 1; i < sorted.Count; i++)
                {
                    if (sorted[i].time == sorted[i - 1].time)
                    {
                        problems.Add(Index(path, sorted[i].index),
                            $"{Index(path, sorted[i].index)}.time {FormatNumber(sorted[i].time)} repeats the time of {Index(path, sorted[i - 1].index)}; two keys at one time are not accepted.");
                        ok = false;
                    }
                }
                return ok ? sorted.Select(item => item.key).ToList() : null;
            }

            // ===================== Asset references =====================

            internal const string BuiltinExtraPath = "Resources/unity_builtin_extra";
            internal const string BuiltinDefaultPath = "Library/unity default resources";
            /// <summary>"type", "builtin" and "persistent" are readback keys, accepted back so a read reference can be resent as is.</summary>
            private static readonly string[] AssetRefKeys = { "assetPath", "guid", "name", "type", "builtin", "persistent" };

            /// <summary>
            /// An asset reference: "Assets/.../X.mat" (shorthand), {assetPath}, {guid}, or either plus "name" to pick one of
            /// several sub-assets of the requested type. Unity's built-in assets use their container path
            /// ("Resources/unity_builtin_extra", "Library/unity default resources") plus the asset name, exactly as
            /// readback reports them. null clears a field documented as clearable.
            /// </summary>
            public static bool ReadAsset(FieldSpec field, JToken token, string path, Problems problems, out UnityEngine.Object asset)
            {
                asset = null;
                if (token == null || token.Type == JTokenType.Null)
                {
                    if (field.Nullable)
                        return true;
                    problems.Add(path, $"{path} cannot be null.");
                    return false;
                }

                string assetPath = null, guid = null, name = null;
                if (token.Type == JTokenType.String)
                {
                    assetPath = token.Value<string>();
                }
                else
                {
                    var obj = ExpectObject(token, path, problems, "an asset reference (\"Assets/...\" or {assetPath | guid, name?})");
                    if (obj == null || !CheckKeys(obj, path, problems, AssetRefKeys, null))
                        return false;
                    bool ok = true;
                    ok &= ReadOptionalString(obj, "assetPath", path, problems, out assetPath);
                    ok &= ReadOptionalString(obj, "guid", path, problems, out guid);
                    ok &= ReadOptionalString(obj, "name", path, problems, out name);
                    ok &= ReadOptionalString(obj, "type", path, problems, out var declaredType);
                    if (obj.Property("builtin") != null)
                        ok &= ReadBool(obj["builtin"], Join(path, "builtin"), problems, out _);
                    if (obj.Property("persistent") != null)
                        ok &= ReadBool(obj["persistent"], Join(path, "persistent"), problems, out _);
                    if (!ok)
                        return false;
                    if (declaredType != null && !string.Equals(declaredType, field.AssetType.Name, StringComparison.Ordinal) &&
                        !IsTypeNameOf(declaredType, field.AssetType))
                    {
                        problems.Add(Join(path, "type"), $"{path} must reference a {field.AssetType.Name}, not a {declaredType}.");
                        return false;
                    }
                    if (string.IsNullOrWhiteSpace(assetPath) && string.IsNullOrWhiteSpace(guid))
                    {
                        problems.Add(path, $"{path} needs assetPath or guid.", CodeMissingParam);
                        return false;
                    }
                }

                var typeName = field.AssetType.Name;
                if (!string.IsNullOrWhiteSpace(guid))
                {
                    var fromGuid = AssetDatabase.GUIDToAssetPath(guid.Trim());
                    bool builtinPath = IsBuiltinContainer(Normalize(assetPath));
                    if (string.IsNullOrEmpty(fromGuid) && !builtinPath)
                    {
                        problems.Add(Join(path, "guid"), $"No asset has guid '{guid.Trim()}'.", CodeTargetNotFound);
                        return false;
                    }
                    if (!string.IsNullOrEmpty(fromGuid) && !string.IsNullOrWhiteSpace(assetPath) &&
                        !string.Equals(Normalize(assetPath), fromGuid, StringComparison.Ordinal))
                    {
                        problems.Add(path, $"{path} names two different assets: guid '{guid.Trim()}' is '{fromGuid}', not '{assetPath}'. Send one of them.");
                        return false;
                    }
                    if (!string.IsNullOrEmpty(fromGuid))
                        assetPath = fromGuid;
                }

                assetPath = Normalize(assetPath);
                bool builtin = IsBuiltinContainer(assetPath);
                if (!builtin)
                {
                    if (Validate.SafePath(assetPath, Join(path, "assetPath")) is object pathError)
                    {
                        SkillResultHelper.TryGetError(pathError, out var pathMessage);
                        problems.Add(Join(path, "assetPath"), pathMessage ?? $"{path} is not a project asset path.");
                        return false;
                    }
                }
                else if (string.IsNullOrWhiteSpace(name))
                {
                    problems.Add(Join(path, "name"), $"{Join(path, "name")} is required for Unity's built-in asset container '{assetPath}'.", CodeMissingParam);
                    return false;
                }

                var candidates = AssetDatabase.LoadAllAssetsAtPath(assetPath)
                    .Where(candidate => candidate != null && field.AssetType.IsInstanceOfType(candidate))
                    .ToList();
                if (!builtin && candidates.Count == 0)
                {
                    var main = AssetDatabase.LoadMainAssetAtPath(assetPath);
                    if (main == null)
                        problems.Add(Join(path, "assetPath"), $"Asset not found: '{assetPath}'.", CodeTargetNotFound);
                    else
                        problems.Add(Join(path, "assetPath"), $"'{assetPath}' is a {main.GetType().Name}, not a {typeName}.");
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(name))
                {
                    var named = candidates.Where(candidate => string.Equals(candidate.name, name.Trim(), StringComparison.Ordinal)).ToList();
                    if (named.Count == 0)
                    {
                        var names = candidates.Select(candidate => candidate.name).Distinct().Take(20).ToArray();
                        problems.Add(Join(path, "name"), $"'{assetPath}' holds no {typeName} named '{name.Trim()}'." +
                            (names.Length > 0 ? $" {typeName} assets there: {string.Join(", ", names)}." : ""), CodeTargetNotFound);
                        return false;
                    }
                    asset = named[0];
                    return true;
                }

                var mainAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (mainAsset != null && field.AssetType.IsInstanceOfType(mainAsset))
                {
                    asset = mainAsset;
                    return true;
                }
                if (candidates.Count == 1)
                {
                    asset = candidates[0];
                    return true;
                }

                problems.Add(Join(path, "name"), $"'{assetPath}' holds {candidates.Count} {typeName} assets; add \"name\" to pick one: " +
                    string.Join(", ", candidates.Select(candidate => candidate.name).Distinct().Take(20)) + ".", CodeMissingParam);
                return false;
            }

            private static string Normalize(string assetPath) => assetPath?.Trim().Replace('\\', '/');

            private static bool IsBuiltinContainer(string assetPath) => assetPath == BuiltinExtraPath || assetPath == BuiltinDefaultPath;

            /// <summary>Whether <paramref name="typeName"/> is a subclass of <paramref name="baseType"/> (readback names the concrete type).</summary>
            private static bool IsTypeNameOf(string typeName, Type baseType)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var type = assembly.GetType("UnityEngine." + typeName, false);
                    if (type != null)
                        return baseType.IsAssignableFrom(type);
                }
                return false;
            }

            private static bool ReadOptionalString(JObject obj, string key, string path, Problems problems, out string value)
            {
                value = null;
                var property = obj.Property(key);
                if (property == null)
                    return true;
                if (property.Value.Type != JTokenType.String)
                {
                    problems.Add(Join(path, key), $"{Join(path, key)} must be a string, got {Describe(property.Value)}.", CodeTypeMismatch);
                    return false;
                }
                value = property.Value.Value<string>();
                return true;
            }

            public static JToken AssetToJson(UnityEngine.Object asset)
            {
                if (asset == null)
                    return JValue.CreateNull();
                var assetPath = AssetDatabase.GetAssetPath(asset);
                var result = new JObject();
                if (!string.IsNullOrEmpty(assetPath))
                    result["assetPath"] = assetPath;
                var guid = string.IsNullOrEmpty(assetPath) ? null : AssetDatabase.AssetPathToGUID(assetPath);
                if (!string.IsNullOrEmpty(guid))
                    result["guid"] = guid;
                result["name"] = asset.name;
                result["type"] = asset.GetType().Name;
                if (string.IsNullOrEmpty(assetPath))
                    result["persistent"] = false;
                else if (assetPath == BuiltinExtraPath || assetPath == BuiltinDefaultPath)
                    result["builtin"] = true;
                return result;
            }

            // ===================== Bursts =====================

            private static readonly string[] BurstKeys = { "time", "count", "cycleCount", "repeatInterval", "probability" };

            /// <summary>
            /// {time, count, cycleCount?, repeatInterval?, probability?}: time >= 0 seconds, count a MinMaxCurve of
            /// particles (>= 0), cycleCount >= 0 where 0 repeats forever (default 1), repeatInterval > 0 seconds (default
            /// 0.01), probability 0-1 (default 1).
            /// </summary>
            public static bool ReadBurst(JToken token, string path, Problems problems, out ParticleSystem.Burst burst)
            {
                burst = default;
                var obj = ExpectObject(token, path, problems, "a burst object {time, count, ...}");
                if (obj == null || !CheckKeys(obj, path, problems, BurstKeys, new[] { "time", "count" }))
                    return false;

                bool ok = ReadFloat(obj["time"], Join(path, "time"), problems, out var time, 0, null);
                ok &= ReadCurve(BurstCountField, obj["count"], Join(path, "count"), problems, out var count);
                int cycleCount = 1;
                float repeatInterval = DefaultBurstRepeatInterval, probability = 1f;
                if (obj.Property("cycleCount") != null)
                    ok &= ReadInt(obj["cycleCount"], Join(path, "cycleCount"), problems, out cycleCount, 0, null);
                if (obj.Property("repeatInterval") != null)
                    ok &= ReadFloat(obj["repeatInterval"], Join(path, "repeatInterval"), problems, out repeatInterval, 0, null, minExclusive: true);
                if (obj.Property("probability") != null)
                    ok &= ReadFloat(obj["probability"], Join(path, "probability"), problems, out probability, 0, 1);
                if (!ok)
                    return false;

                burst = new ParticleSystem.Burst(time, count, cycleCount, repeatInterval) { probability = probability };
                return true;
            }

            /// <summary>The value contract of a burst's count, shared by parsing and capabilities.</summary>
            internal static readonly FieldSpec BurstCountField = new FieldSpec
            {
                Name = "count",
                Kind = FieldKind.Curve,
                Unit = "particles",
                Min = 0,
                KeyTimeMeaning = "normalized position in the burst's repeat cycles",
                Description = "Particles per burst cycle."
            };

            public static JToken BurstToJson(ParticleSystem.Burst burst) => new JObject
            {
                ["time"] = burst.time,
                ["count"] = CurveToJson(burst.count),
                ["cycleCount"] = burst.cycleCount,
                ["repeatInterval"] = burst.repeatInterval,
                ["probability"] = burst.probability
            };

            // ===================== Canonical output =====================

            public static JToken CurveToJson(ParticleSystem.MinMaxCurve curve)
            {
                switch (curve.mode)
                {
                    case ParticleSystemCurveMode.Constant:
                        return new JObject { ["mode"] = "Constant", ["constant"] = curve.constant };
                    case ParticleSystemCurveMode.TwoConstants:
                        return new JObject { ["mode"] = "TwoConstants", ["min"] = curve.constantMin, ["max"] = curve.constantMax };
                    case ParticleSystemCurveMode.Curve:
                        return new JObject { ["mode"] = "Curve", ["multiplier"] = curve.curveMultiplier, ["curve"] = AnimationCurveToJson(curve.curveMax) };
                    default:
                        return new JObject
                        {
                            ["mode"] = "TwoCurves",
                            ["multiplier"] = curve.curveMultiplier,
                            ["minCurve"] = AnimationCurveToJson(curve.curveMin),
                            ["maxCurve"] = AnimationCurveToJson(curve.curveMax)
                        };
                }
            }

            public static JToken AnimationCurveToJson(AnimationCurve curve)
            {
                if (curve == null)
                    return JValue.CreateNull();
                var keys = new JArray();
                var frames = curve.keys;
                for (int i = 0; i < frames.Length; i++)
                {
                    var frame = frames[i];
                    var left = AnimationUtility.GetKeyLeftTangentMode(curve, i);
                    var right = AnimationUtility.GetKeyRightTangentMode(curve, i);
                    // A non-finite tangent is a step whatever the stored mode says; JSON carries it as mode Constant.
                    if (!IsFinite(frame.inTangent)) left = AnimationUtility.TangentMode.Constant;
                    if (!IsFinite(frame.outTangent)) right = AnimationUtility.TangentMode.Constant;

                    var key = new JObject { ["time"] = frame.time, ["value"] = frame.value };
                    if (left != AnimationUtility.TangentMode.Constant)
                        key["inTangent"] = frame.inTangent;
                    if (right != AnimationUtility.TangentMode.Constant)
                        key["outTangent"] = frame.outTangent;
                    key["inWeight"] = frame.inWeight;
                    key["outWeight"] = frame.outWeight;
                    key["weightedMode"] = frame.weightedMode.ToString();
                    key["leftTangentMode"] = left.ToString();
                    key["rightTangentMode"] = right.ToString();
                    key["broken"] = AnimationUtility.GetKeyBroken(curve, i);
                    keys.Add(key);
                }

                return new JObject
                {
                    ["preWrapMode"] = WrapModeName(curve.preWrapMode),
                    ["postWrapMode"] = WrapModeName(curve.postWrapMode),
                    ["keys"] = keys
                };
            }

            private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

            private static string WrapModeName(WrapMode mode)
            {
                switch (mode)
                {
                    case WrapMode.Clamp: return "Clamp";
                    case WrapMode.Loop: return "Loop";
                    case WrapMode.PingPong: return "PingPong";
                    case WrapMode.ClampForever: return "ClampForever";
                    default: return "Default";
                }
            }

            public static JToken GradientToJson(ParticleSystem.MinMaxGradient gradient)
            {
                switch (gradient.mode)
                {
                    case ParticleSystemGradientMode.Color:
                        return new JObject { ["mode"] = "Color", ["color"] = ColorToJson(gradient.color) };
                    case ParticleSystemGradientMode.TwoColors:
                        return new JObject { ["mode"] = "TwoColors", ["minColor"] = ColorToJson(gradient.colorMin), ["maxColor"] = ColorToJson(gradient.colorMax) };
                    case ParticleSystemGradientMode.Gradient:
                        return new JObject { ["mode"] = "Gradient", ["gradient"] = GradientObjectToJson(gradient.gradient) };
                    case ParticleSystemGradientMode.TwoGradients:
                        return new JObject { ["mode"] = "TwoGradients", ["minGradient"] = GradientObjectToJson(gradient.gradientMin), ["maxGradient"] = GradientObjectToJson(gradient.gradientMax) };
                    default:
                        return new JObject { ["mode"] = "RandomColor", ["gradient"] = GradientObjectToJson(gradient.gradient) };
                }
            }

            public static JToken GradientObjectToJson(Gradient gradient)
            {
                if (gradient == null)
                    return JValue.CreateNull();
                var colorKeys = new JArray();
                foreach (var key in gradient.colorKeys)
                    colorKeys.Add(new JObject { ["time"] = key.time, ["color"] = ColorToJson(key.color, withAlpha: false) });
                var alphaKeys = new JArray();
                foreach (var key in gradient.alphaKeys)
                    alphaKeys.Add(new JObject { ["time"] = key.time, ["alpha"] = key.alpha });
                return new JObject
                {
                    ["mode"] = gradient.mode.ToString(),
                    ["colorSpace"] = gradient.colorSpace.ToString(),
                    ["colorKeys"] = colorKeys,
                    ["alphaKeys"] = alphaKeys
                };
            }

            public static JToken ColorToJson(Color color, bool withAlpha = true)
            {
                var result = new JObject { ["r"] = color.r, ["g"] = color.g, ["b"] = color.b };
                if (withAlpha)
                    result["a"] = color.a;
                return result;
            }

            public static JToken Vector3ToJson(Vector3 value) => new JObject { ["x"] = value.x, ["y"] = value.y, ["z"] = value.z };

            public static JToken Vector2ToJson(Vector2 value) => new JObject { ["x"] = value.x, ["y"] = value.y };

            // ===================== Field dispatch =====================

            /// <summary>Parses one field value. Returns false (with problems) when the value is unusable.</summary>
            public static bool ReadField(FieldSpec field, JToken token, string path, Problems problems, out object value)
            {
                value = null;
                if (token == null || token.Type == JTokenType.Null)
                {
                    if (field.Nullable && field.Kind == FieldKind.Asset)
                        return true;
                    problems.Add(path, $"{path} cannot be null" + (field.Nullable ? "." : "; omit the key to keep the current value."));
                    return false;
                }

                switch (field.Kind)
                {
                    case FieldKind.Bool:
                    {
                        if (!ReadBool(token, path, problems, out var b)) return false;
                        value = b;
                        return true;
                    }
                    case FieldKind.Int:
                    {
                        if (!ReadInt(token, path, problems, out var i, field.Min, field.Max)) return false;
                        value = i;
                        return true;
                    }
                    case FieldKind.UInt:
                    {
                        if (!ReadUInt(token, path, problems, out var u)) return false;
                        value = u;
                        return true;
                    }
                    case FieldKind.Float:
                    {
                        if (!ReadFloat(token, path, problems, out var f, field.Min, field.Max, field.MinExclusive)) return false;
                        value = f;
                        return true;
                    }
                    case FieldKind.Enum:
                        return ReadEnum(token, path, problems, field.EnumType, field.WritableEnumValues, out value);
                    case FieldKind.Vector2:
                    {
                        if (!ReadVector2(token, path, problems, out var v, field.Min, field.Max)) return false;
                        value = v;
                        return true;
                    }
                    case FieldKind.Vector3:
                    {
                        if (!ReadVector3(token, path, problems, out var v, field.Min, field.Max)) return false;
                        value = v;
                        return true;
                    }
                    case FieldKind.Color:
                    {
                        if (!ReadColor(token, path, problems, out var c)) return false;
                        value = c;
                        return true;
                    }
                    case FieldKind.Curve:
                    {
                        if (!ReadCurve(field, token, path, problems, out var curve)) return false;
                        value = curve;
                        return true;
                    }
                    case FieldKind.Gradient:
                    {
                        if (!ReadGradient(field, token, path, problems, out var gradient)) return false;
                        value = gradient;
                        return true;
                    }
                    case FieldKind.Asset:
                    {
                        if (!ReadAsset(field, token, path, problems, out var asset)) return false;
                        value = asset;
                        return true;
                    }
                    case FieldKind.SortingLayer:
                        return ReadSortingLayer(token, path, problems, out value);
                    default:
                        problems.Add(path, $"{path} has an unsupported value kind {field.Kind}.");
                        return false;
                }
            }

            private static bool ReadSortingLayer(JToken token, string path, Problems problems, out object value)
            {
                value = null;
                var names = SortingLayer.layers.Select(layer => layer.name).ToArray();
                if (token.Type != JTokenType.String)
                {
                    problems.Add(path, $"{path} must be a sorting layer name, got {Describe(token)}. Defined layers: {string.Join(", ", names)}.", CodeTypeMismatch);
                    return false;
                }
                var text = token.Value<string>();
                var match = names.FirstOrDefault(name => string.Equals(name, text, StringComparison.Ordinal));
                if (match == null)
                {
                    var closest = SkillsCommon.ClosestMatch(text, names);
                    problems.Add(path, $"Sorting layer '{text}' is not defined" + (closest != null ? $" (did you mean '{closest}'?)" : "") +
                        $". Defined layers: {string.Join(", ", names)}. Add layers in Project Settings > Tags and Layers.");
                    return false;
                }
                value = match;
                return true;
            }

            /// <summary>The canonical JSON of a field value read from Unity.</summary>
            public static JToken WriteField(FieldSpec field, object value)
            {
                switch (field.Kind)
                {
                    case FieldKind.Bool: return new JValue((bool)value);
                    case FieldKind.Int: return new JValue((int)value);
                    case FieldKind.UInt: return new JValue((long)(uint)value);
                    case FieldKind.Float: return new JValue((float)value);
                    case FieldKind.Enum: return new JValue(value.ToString());
                    case FieldKind.Vector2: return Vector2ToJson((Vector2)value);
                    case FieldKind.Vector3: return Vector3ToJson((Vector3)value);
                    case FieldKind.Color: return ColorToJson((Color)value);
                    case FieldKind.Curve: return CurveToJson((ParticleSystem.MinMaxCurve)value);
                    case FieldKind.Gradient: return GradientToJson((ParticleSystem.MinMaxGradient)value);
                    case FieldKind.Asset: return AssetToJson(value as UnityEngine.Object);
                    case FieldKind.SortingLayer: return new JValue(value as string ?? "");
                    default: return JValue.CreateNull();
                }
            }

            /// <summary>
            /// Whether Unity kept a written value: the canonical JSON of what was requested against what reads back, with a
            /// float tolerance. A difference means Unity clamped or otherwise adjusted the value.
            /// </summary>
            public static bool Equivalent(JToken requested, JToken actual)
            {
                if (requested == null || actual == null)
                    return requested == actual;
                if (IsNumber(requested) && IsNumber(actual))
                {
                    // Both sides went through the same float storage, so only float rounding is forgiven: a clamp of
                    // a small value (0.00001 -> 0.0001) must still count as a change.
                    var a = requested.Value<double>();
                    var b = actual.Value<double>();
                    return Math.Abs(a - b) <= 1e-6 + 1e-5 * Math.Max(Math.Abs(a), Math.Abs(b));
                }
                if (requested.Type != actual.Type)
                    return false;
                if (requested is JObject ro && actual is JObject ao)
                {
                    if (ro.Count != ao.Count)
                        return false;
                    foreach (var property in ro.Properties())
                    {
                        if (!ao.TryGetValue(property.Name, out var other) || !Equivalent(property.Value, other))
                            return false;
                    }
                    return true;
                }
                if (requested is JArray ra && actual is JArray aa)
                {
                    if (ra.Count != aa.Count)
                        return false;
                    for (int i = 0; i < ra.Count; i++)
                    {
                        if (!Equivalent(ra[i], aa[i]))
                            return false;
                    }
                    return true;
                }
                return JToken.DeepEquals(requested, actual);
            }

            private static bool IsNumber(JToken token) => token.Type == JTokenType.Integer || token.Type == JTokenType.Float;
        }
    }
}
#endif

// Producer:Betsy
