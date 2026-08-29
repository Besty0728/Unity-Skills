using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// Parameter parsing and value formatting shared by the various setter skills.
    ///
    /// <para>It has to guard against two problems. First, enums silently dropped: the old-style
    /// <c>if (Enum.TryParse(v, true, out var e)) target = e;</c> has no else branch, so a
    /// misspelled value is dropped while the skill still answers <c>success:true</c>, and the
    /// call's other parameters get written anyway. Every such spot now goes through
    /// <see cref="TryParseEnumParam{TEnum}"/> and rejects the whole call instead. Second,
    /// distorted echoes: interpolating a float with <c>ToString()</c> both truncates it
    /// (0.192156866 → 0.1921569) and follows the editor's culture, so a comma-decimal locale
    /// would emit a value the caller can't parse back. <see cref="FormatFloatR"/> and its
    /// siblings guarantee lossless, culture-independent round-tripping.</para>
    ///
    /// <para>Two more concerns were added later, both about "what counts as a valid value".
    /// Unity's enums are full of <c>[Obsolete]</c> members that <c>Enum.IsDefined</c> still lets
    /// through — <c>TextureImporterType.Image</c>'s value is <c>int.MinValue</c>, which used to
    /// flow straight into the importer as-is — so representability is judged only against
    /// non-obsolete members, and the <c>validValues</c> list is built the same way. Also, CLR
    /// member names are often not the words people actually type: the alias-table overload
    /// accepts the Inspector's vocabulary alongside the CLR names
    /// (<c>TextureImporterCompression</c>'s <c>None</c>/<c>LowQuality</c>/…).</para>
    ///
    /// <para>Error objects follow the router's first-layer pass-through contract
    /// (<c>SkillResultHelper.TryGetErrorContext</c>): <c>error</c> carries the message,
    /// <c>errorCode</c> is passed through as-is, and <c>parameter</c>/<c>validValues</c> aren't
    /// reserved words, so they get forwarded unchanged to the top of the response.</para>
    /// </summary>
    internal static class SkillParamUtil
    {
        /// <summary>
        /// The wire value of <c>errorCode</c> when a parameter value is rejected. Deliberately a
        /// literal rather than <c>SkillErrorCode.SemanticInvalid.ToWireString()</c>, so skills that
        /// never reference that enum can still construct an anonymous error object normally.
        /// </summary>
        internal const string SemanticInvalidCode = "SEMANTIC_INVALID";

        #region Enum parameters

        /// <summary>
        /// Parses an enum-typed skill parameter, case-insensitively.
        ///
        /// <para>When <paramref name="value"/> is null/blank, returns true with
        /// <paramref name="error"/> null, meaning "not provided, skip me". In that case
        /// <paramref name="result"/> is <c>default(TEnum)</c>, which for most Unity enums is a
        /// real member; so a caller that treats "parameter omitted" as "keep the current value"
        /// still must check the original string itself (or switch to
        /// <see cref="TryParseOptionalEnum{TEnum}"/>, which returns a nullable value instead).</para>
        ///
        /// <para>When a value is given but can't be parsed, returns false with a response-shaped
        /// <paramref name="error"/>. The caller must return that object as-is and must not write
        /// anything.</para>
        /// </summary>
        public static bool TryParseEnumParam<TEnum>(string value, string paramName, out TEnum result, out object error)
            where TEnum : struct
        {
            return TryParseEnumParam<TEnum>(value, paramName, null, out result, out error);
        }

        /// <summary>
        /// The alias-table variant of <see cref="TryParseEnumParam{TEnum}(string,string,out TEnum,out object)"/>,
        /// for enums whose CLR member names aren't the words people actually use.
        ///
        /// <para>This overload was forced into existence by <c>TextureImporterCompression</c>: it
        /// declares <c>Uncompressed/Compressed/CompressedHQ/CompressedLQ</c>, while every skill
        /// description, module doc, and Unity Inspector label writes None / Low Quality / Normal
        /// Quality / High Quality. Before the alias table existed, the words given in the docs
        /// were rejected 100% of the time. <paramref name="aliases"/> is an alias → CLR member
        /// name mapping, looked up case-insensitively; the CLR names still work too, and both
        /// spellings show up in the rejection message's <c>validValues</c>, so one wrong guess is
        /// enough to get it right.</para>
        ///
        /// <para>Internal spaces are stripped before both the alias lookup and the parse. CLR
        /// enum member names can never contain spaces, so this is lossless, and it's exactly what
        /// lets the Inspector's original labels ("Editor GUI", "Low Quality", "Normal Map") be
        /// pasted in as-is.</para>
        /// </summary>
        public static bool TryParseEnumParam<TEnum>(string value, string paramName,
            IDictionary<string, string> aliases, out TEnum result, out object error)
            where TEnum : struct
        {
            result = default(TEnum);
            error = null;

            if (string.IsNullOrWhiteSpace(value))
                return true;

            var candidate = value.Trim();
            if (aliases != null)
                candidate = ResolveAlias(aliases, candidate.Replace(" ", ""));

            if (Enum.TryParse<TEnum>(candidate, true, out var parsed) && IsRepresentable(parsed))
            {
                result = parsed;
                return true;
            }

            error = InvalidValueError(value, paramName, Vocabulary<TEnum>(aliases));
            return false;
        }

        /// <summary>
        /// Alias → CLR name, case-insensitive. The tables below are all built with
        /// <see cref="StringComparer.OrdinalIgnoreCase"/>, so the linear rescan only happens on a
        /// lookup miss — i.e. for every value already written as the CLR name, and the table has
        /// at most a handful of entries anyway. A caller's own ordinal dictionary works fine too.
        /// </summary>
        private static string ResolveAlias(IDictionary<string, string> aliases, string value)
        {
            if (aliases.TryGetValue(value, out var canonical))
                return canonical;

            foreach (var pair in aliases)
            {
                if (string.Equals(pair.Key, value, StringComparison.OrdinalIgnoreCase))
                    return pair.Value;
            }
            return value;
        }

        /// <summary>
        /// The <see cref="TryParseEnumParam{TEnum}"/> variant for setters where "parameter
        /// omitted" must mean "keep the object's current value": when nothing is passed,
        /// <paramref name="result"/> is null, so the write site needs only a <c>HasValue</c>
        /// check, and <c>default(TEnum)</c> can never slip through disguised as a real write.
        /// </summary>
        public static bool TryParseOptionalEnum<TEnum>(string value, string paramName, out TEnum? result, out object error)
            where TEnum : struct
        {
            return TryParseOptionalEnum<TEnum>(value, paramName, null, out result, out error);
        }

        /// <summary>The alias-table version of <see cref="TryParseOptionalEnum{TEnum}(string,string,out TEnum?,out object)"/>.</summary>
        public static bool TryParseOptionalEnum<TEnum>(string value, string paramName,
            IDictionary<string, string> aliases, out TEnum? result, out object error)
            where TEnum : struct
        {
            result = null;
            if (!TryParseEnumParam<TEnum>(value, paramName, aliases, out var parsed, out error))
                return false;

            if (!string.IsNullOrWhiteSpace(value))
                result = parsed;
            return true;
        }

        /// <summary>
        /// An enum parameter that must resolve to a member — used by creation-type skills whose
        /// own documented default is already a valid name ("Point", "Soft"). There, an empty
        /// value is the caller's mistake rather than "leave it alone"; letting it through would
        /// silently write <c>default(TEnum)</c> (i.e. LightType.Spot, not the "Point" the docs claim).
        /// </summary>
        public static bool TryParseRequiredEnum<TEnum>(string value, string paramName, out TEnum result, out object error)
            where TEnum : struct
        {
            return TryParseRequiredEnum<TEnum>(value, paramName, null, out result, out error);
        }

        /// <summary>The alias-table version of <see cref="TryParseRequiredEnum{TEnum}(string,string,out TEnum,out object)"/>.</summary>
        public static bool TryParseRequiredEnum<TEnum>(string value, string paramName,
            IDictionary<string, string> aliases, out TEnum result, out object error)
            where TEnum : struct
        {
            result = default(TEnum);
            error = null;

            if (string.IsNullOrWhiteSpace(value))
            {
                var names = Vocabulary<TEnum>(aliases);
                error = new
                {
                    error = $"Missing value for parameter '{paramName}'. Valid values: {string.Join(", ", names)}.",
                    errorCode = "MISSING_PARAM",
                    parameter = paramName,
                    validValues = names,
                };
                return false;
            }

            return TryParseEnumParam<TEnum>(value, paramName, aliases, out result, out error);
        }

        /// <summary>
        /// A comma-separated [Flags] parameter, with the "Everything"/"Nothing" aliases added
        /// that skill docs claim but the enum itself doesn't declare (Unity's StaticEditorFlags
        /// has neither, which used to make <c>optimize_set_static_flags</c> reject its own
        /// documented default value, "Everything"). "Nothing" is 0; "Everything" is the bitwise
        /// OR of all non-<c>[Obsolete]</c> members — ORing together all of StaticEditorFlags
        /// gives 127, and removing the two bits Unity's own Static dropdown no longer exposes
        /// (NavigationStatic 8, OffMeshLinkGeneration 32) gives 87. There are actually three
        /// deprecated members, not two: the third is LightmapStatic, which shares bit 1 with the
        /// still-live ContributeGI, so it doesn't affect the result. Every item listed must parse
        /// successfully — one wrong name fails the whole call rather than silently shrinking the set.
        /// </summary>
        public static bool TryParseFlagsParam<TEnum>(string value, string paramName, out TEnum result, out object error)
            where TEnum : struct
        {
            result = default(TEnum);
            error = null;

            var facts = GetFacts(typeof(TEnum));
            var names = facts.PublicNames;
            var vocabulary = names.Concat(new[] { "Everything", "Nothing" })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (string.IsNullOrWhiteSpace(value))
            {
                error = new
                {
                    error = $"Missing value for parameter '{paramName}'. Valid values: {string.Join(", ", vocabulary)}.",
                    errorCode = "MISSING_PARAM",
                    parameter = paramName,
                    validValues = vocabulary,
                };
                return false;
            }

            var trimmed = value.Trim();

            if (!ContainsName(names, "Everything") &&
                string.Equals(trimmed, "Everything", StringComparison.OrdinalIgnoreCase))
            {
                result = (TEnum)Enum.ToObject(typeof(TEnum), facts.LiveMask);
                return true;
            }

            if (!ContainsName(names, "Nothing") &&
                string.Equals(trimmed, "Nothing", StringComparison.OrdinalIgnoreCase))
            {
                result = (TEnum)Enum.ToObject(typeof(TEnum), 0L);
                return true;
            }

            long accumulated = 0;
            foreach (var part in trimmed.Split(','))
            {
                if (!TryParseEnumParam<TEnum>(part, paramName, out var flag, out _) ||
                    string.IsNullOrWhiteSpace(part))
                {
                    // A per-item error only lists declared members, so the two aliases this
                    // method adds itself would end up missing from the "here's what you can
                    // pass" message.
                    error = InvalidValueError(part, paramName, vocabulary);
                    return false;
                }
                accumulated |= ToInt64(flag);
            }

            result = (TEnum)Enum.ToObject(typeof(TEnum), accumulated);
            return true;
        }

        private static bool ContainsName(string[] names, string candidate)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], candidate, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Produces only the rejection payload for an enum parameter, without parsing. Used
        /// where values are mapped by hand (a switch over a batch of strings that don't match any
        /// CLR enum member name) but a uniform message and complete valid-values list are still wanted.
        /// </summary>
        public static object InvalidEnumError<TEnum>(string value, string paramName) where TEnum : struct
        {
            return InvalidValueError(value, paramName, Vocabulary<TEnum>(null));
        }


        /// <summary>
        /// The rejection payload for values with no CLR enum behind them, drawn instead from a
        /// hand-written vocabulary ("low"/"medium"/"high", "sprite"/"texture"/…).
        ///
        /// <para>The message must start with "Invalid value", with nothing before it. The router
        /// classifies undeclared errors by message pattern, and its leading-word rule
        /// (<c>SkillErrorResponse.LeadingSemanticPattern</c>) is exactly what keeps an invalid
        /// parameter value out of the TARGET_NOT_FOUND bucket — .NET's own enum-failure wording is
        /// "Requested value 'X' was not found.", which would otherwise get claimed by the
        /// not-found marker first and send the caller off to gameobject_find. This also explicitly
        /// declares <c>errorCode</c>, so the error code stays correct even if the wording changes later.</para>
        /// </summary>
        public static object InvalidValueError(string value, string paramName, IEnumerable<string> validValues)
        {
            var names = validValues?.ToArray() ?? Array.Empty<string>();
            return new
            {
                error = $"Invalid value '{value}' for parameter '{paramName}'. Valid values: {string.Join(", ", names)}.",
                errorCode = SemanticInvalidCode,
                parameter = paramName,
                validValues = names,
            };
        }

        /// <summary>
        /// The same rejection payload, but tagged with which batch entry it came from, so the
        /// failing item in a <c>*_batch</c> call can be located without the caller diffing the input array.
        /// </summary>
        public static object InvalidValueError(string value, string paramName, IEnumerable<string> validValues, string target)
        {
            var names = validValues?.ToArray() ?? Array.Empty<string>();
            return new
            {
                error = $"Invalid value '{value}' for parameter '{paramName}'. Valid values: {string.Join(", ", names)}.",
                errorCode = SemanticInvalidCode,
                parameter = paramName,
                validValues = names,
                target,
            };
        }

        /// <summary>
        /// The batch-entry version of <see cref="InvalidEnumError{TEnum}(string,string)"/>.
        /// </summary>
        public static object InvalidEnumError<TEnum>(string value, string paramName, string target) where TEnum : struct
        {
            return InvalidValueError(value, paramName, Vocabulary<TEnum>(null), target);
        }

        /// <summary>
        /// The alias-table batch-entry version, so the vocabulary a rejected entry lists exactly
        /// matches what the single-item setter would list.
        /// </summary>
        public static object InvalidEnumError<TEnum>(string value, string paramName,
            IDictionary<string, string> aliases, string target) where TEnum : struct
        {
            return InvalidValueError(value, paramName, Vocabulary<TEnum>(aliases), target);
        }

        /// <summary>
        /// Everything the parser needs to know about a given enum type, reflected once and
        /// cached: which members are actually usable, which names are worth echoing back, and —
        /// for [Flags] — which bits are valid.
        /// </summary>
        private sealed class EnumFacts
        {
            public bool IsFlags;
            /// <summary>All declared member names, in declaration order.</summary>
            public string[] AllNames;
            /// <summary>Declared member names that are not <c>[Obsolete]</c>.</summary>
            public string[] LiveNames;
            /// <summary>Values of the non-obsolete members — the valid-value set for a plain enum.</summary>
            public HashSet<long> LiveValues;
            /// <summary>Bitwise OR of all declared members (including obsolete): the valid bits for a [Flags] value.</summary>
            public long DeclaredMask;
            /// <summary>Bitwise OR of the non-obsolete members, i.e. what the "Everything" alias means.</summary>
            public long LiveMask;

            /// <summary>
            /// The vocabulary exposed externally. A plain enum strips out obsolete members (they
            /// get rejected there); [Flags] keeps them (they still parse there) — StaticEditorFlags's
            /// deprecated <c>NavigationStatic</c> is right there in this repo's own documented
            /// default list.
            /// </summary>
            public string[] PublicNames => IsFlags ? AllNames : LiveNames;
        }

        private static readonly Dictionary<Type, EnumFacts> FactsCache = new Dictionary<Type, EnumFacts>();

        private static EnumFacts GetFacts(Type type)
        {
            lock (FactsCache)
            {
                if (FactsCache.TryGetValue(type, out var cached))
                    return cached;

                var allNames = new List<string>();
                var liveNames = new List<string>();
                var liveValues = new HashSet<long>();
                long declaredMask = 0;
                long liveMask = 0;

                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    var raw = ToInt64(field.GetRawConstantValue());
                    allNames.Add(field.Name);
                    declaredMask |= raw;

                    if (field.IsDefined(typeof(ObsoleteAttribute), false))
                        continue;

                    liveNames.Add(field.Name);
                    liveValues.Add(raw);
                    liveMask |= raw;
                }

                // Degenerate case: if every member of an enum is deprecated, without this every
                // value would get rejected. Fall back to the full set here instead of becoming
                // completely unusable.
                if (liveNames.Count == 0)
                {
                    foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                    {
                        liveNames.Add(field.Name);
                        liveValues.Add(ToInt64(field.GetRawConstantValue()));
                    }
                    liveMask = declaredMask;
                }

                var facts = new EnumFacts
                {
                    IsFlags = type.IsDefined(typeof(FlagsAttribute), false),
                    AllNames = allNames.ToArray(),
                    LiveNames = liveNames.ToArray(),
                    LiveValues = liveValues,
                    DeclaredMask = declaredMask,
                    LiveMask = liveMask,
                };
                FactsCache[type] = facts;
                return facts;
            }
        }

        /// <summary>
        /// Gets the underlying integer value of an enum member (or its boxed primitive). An enum
        /// based on ulong with the top bit set would overflow <c>Convert.ToInt64</c>, so this
        /// reinterprets bitwise instead — the value is only used for set membership checks and bitmasks.
        /// </summary>
        private static long ToInt64(object raw)
        {
            if (raw is ulong u)
                return unchecked((long)u);
            if (raw is Enum e && Enum.GetUnderlyingType(e.GetType()) == typeof(ulong))
                return unchecked((long)Convert.ToUInt64(e, CultureInfo.InvariantCulture));
            return Convert.ToInt64(raw, CultureInfo.InvariantCulture);
        }

        /// <summary>The valid-value list for an enum parameter: CLR member names first, then any aliases.</summary>
        private static string[] Vocabulary<TEnum>(IDictionary<string, string> aliases) where TEnum : struct
        {
            var names = GetFacts(typeof(TEnum)).PublicNames;
            if (aliases == null || aliases.Count == 0)
                return names;

            return names.Concat(aliases.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>
        /// Determines whether a parsed value is actually representable by this enum.
        ///
        /// <para>There are two ways this can fail. First, <c>Enum.TryParse</c> accepts any
        /// integer literal, including ones with no member behind them: a three-member enum given
        /// "99" produces <c>(TEnum)99</c>, and a [Flags] enum given "999" used to be written
        /// straight through as-is, so a mask check is needed to reject any bit no declared member
        /// claims. Second, Unity marks large numbers of members <c>[Obsolete]</c> while
        /// <c>Enum.IsDefined</c> still accepts them: <c>TextureImporterType.Image</c>'s value is
        /// <c>int.MinValue</c>, which used to flow straight into the importer as-is, and no
        /// Inspector can even display it. Membership is judged by value rather than by name, so
        /// deprecated spellings of a still-live member keep working (<c>LightType.Area</c> is
        /// <c>Rectangle</c>; <c>TextureImporterFormat.AutomaticCompressed</c> is <c>Automatic</c>).</para>
        /// </summary>
        private static bool IsRepresentable<TEnum>(TEnum value) where TEnum : struct
        {
            var facts = GetFacts(typeof(TEnum));
            var raw = ToInt64(value);

            if (facts.IsFlags)
                return (raw & ~facts.DeclaredMask) == 0;

            return facts.LiveValues.Contains(raw);
        }

        #endregion

        #region Importer vocabulary aliases

        /// <summary>
        /// <c>TextureImporterCompression</c> declares
        /// <c>Uncompressed/Compressed/CompressedHQ/CompressedLQ</c>, but the Inspector, the
        /// module docs, and this repo's own skill descriptions all write None / Low Quality /
        /// Normal Quality / High Quality — those four words used to be rejected 100% of the time.
        /// Both spellings now parse.
        /// </summary>
        public static readonly IDictionary<string, string> TextureCompressionAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "None", "Uncompressed" },
                { "Normal", "Compressed" },
                { "NormalQuality", "Compressed" },
                { "LowQuality", "CompressedLQ" },
                { "HighQuality", "CompressedHQ" },
            };

        /// <summary>
        /// The Inspector's "Editor GUI and Legacy GUI" texture type is <c>TextureImporterType.GUI</c>
        /// in CLR. The alias-aware parse strips spaces, so "Editor GUI" written as-is is covered too.
        /// </summary>
        public static readonly IDictionary<string, string> TextureTypeAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "EditorGUI", "GUI" },
            };

        /// <summary>
        /// The Rig dropdown writes "Humanoid", while <c>ModelImporterAnimationType</c> spells it <c>Human</c>.
        /// </summary>
        public static readonly IDictionary<string, string> ModelAnimationTypeAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Humanoid", "Human" },
            };

        #endregion

        #region Round-trip formatting

        /// <summary>
        /// Produces the shortest, culture-independent representation of <paramref name="value"/>
        /// that parses back to the original value. Tries "R" first (the shortest round-trip form
        /// on modern runtimes: 0.1f is still "0.1") and validates it by reparsing; falls back to
        /// "G9" on runtimes where "R" is still the old lossy implementation — by significant-digit
        /// count, that guarantees a lossless round trip for float.
        /// </summary>
        public static string FormatFloatR(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return value.ToString(CultureInfo.InvariantCulture);

            var text = value.ToString("R", CultureInfo.InvariantCulture);
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                parsed.Equals(value))
                return text;

            return value.ToString("G9", CultureInfo.InvariantCulture);
        }

        /// <summary>The double version of <see cref="FormatFloatR"/>; "G17" is the safe fallback.</summary>
        public static string FormatDoubleR(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return value.ToString(CultureInfo.InvariantCulture);

            var text = value.ToString("R", CultureInfo.InvariantCulture);
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                parsed.Equals(value))
                return text;

            return value.ToString("G17", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Culture-independent round-trip form of an arbitrary boxed value, for skills that need
        /// to interpolate a reflected property value into a string. Numeric types go through the
        /// round-trip formatters; other types fall back to their own <c>ToString</c>, using an
        /// invariant overload if the type provides one.
        /// </summary>
        public static string FormatScalarR(object value)
        {
            switch (value)
            {
                case null: return "null";
                case float f: return FormatFloatR(f);
                case double d: return FormatDoubleR(d);
                case decimal m: return m.ToString(CultureInfo.InvariantCulture);
                case bool b: return b ? "true" : "false";
                case IFormattable formattable: return formattable.ToString(null, CultureInfo.InvariantCulture);
                default: return value.ToString();
            }
        }

        public static string FormatVector2(Vector2 v) =>
            $"({FormatFloatR(v.x)}, {FormatFloatR(v.y)})";

        public static string FormatVector3(Vector3 v) =>
            $"({FormatFloatR(v.x)}, {FormatFloatR(v.y)}, {FormatFloatR(v.z)})";

        public static string FormatVector4(Vector4 v) =>
            $"({FormatFloatR(v.x)}, {FormatFloatR(v.y)}, {FormatFloatR(v.z)}, {FormatFloatR(v.w)})";

        /// <summary>RGBA, always emitting all four components — a missing alpha in the echo is exactly where a dropped alpha would hide.</summary>
        public static string FormatColor(Color c) =>
            $"({FormatFloatR(c.r)}, {FormatFloatR(c.g)}, {FormatFloatR(c.b)}, {FormatFloatR(c.a)})";

        #endregion

        #region JSON-object parameter forms

        /// <summary>
        /// Determines whether a string parameter looks like a JSON object rather than a
        /// scalar/CSV form. Deliberately shallow (starts with '{' and has a ':' somewhere): the
        /// caller only needs to decide which parser to hand the text to, and a malformed object
        /// should fail with a JSON error inside the JSON parser, not get silently retried as a
        /// comma-separated list.
        /// </summary>
        public static bool LooksLikeJsonObject(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            var trimmed = value.TrimStart();
            return trimmed.Length > 0 && trimmed[0] == '{' && trimmed.IndexOf(':') >= 0;
        }

        #endregion
    }
}

// Producer:Betsy
