using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// 各 setter skill 共用的参数解析与值格式化。
    ///
    /// <para>它要挡住两个问题。其一，枚举被静默丢弃：历史写法
    /// <c>if (Enum.TryParse(v, true, out var e)) target = e;</c> 没有 else 分支，拼错的值被丢掉，
    /// skill 仍答 <c>success:true</c>，而同一次调用里的其他参数照样写入。现在所有此类位置一律走
    /// <see cref="TryParseEnumParam{TEnum}"/> 并拒绝整次调用。其二，回显失真：用 <c>ToString()</c>
    /// 内插 float 既会截断（0.192156866 → 0.1921569），又跟随编辑器的 culture，
    /// 逗号小数点的 locale 会输出调用方解析不回来的值。<see cref="FormatFloatR"/> 及其同族
    /// 保证往返无损且与 culture 无关。</para>
    ///
    /// <para>后来又加了两点，都关于"什么才算合法值"。Unity 的枚举里充斥着 <c>Enum.IsDefined</c>
    /// 照样放行的 <c>[Obsolete]</c> 成员——<c>TextureImporterType.Image</c> 的值是
    /// <c>int.MinValue</c>，过去就是原样落进 importer 的——因此可表示性只按非 obsolete 成员判定，
    /// <c>validValues</c> 列表也照此构建。另外 CLR 成员名常常不是人们实际会写的词：
    /// 带别名的重载在 CLR 名之外同时接受 Inspector 的用词
    /// （<c>TextureImporterCompression</c> 的 <c>None</c>/<c>LowQuality</c>/…）。</para>
    ///
    /// <para>错误对象遵循 router 的第一层直通契约（<c>SkillResultHelper.TryGetErrorContext</c>）：
    /// <c>error</c> 承载消息，<c>errorCode</c> 原样沿用，<c>parameter</c>/<c>validValues</c>
    /// 不属保留字，因此会被原封不动转发到响应顶层。</para>
    /// </summary>
    internal static class SkillParamUtil
    {
        /// <summary>
        /// 参数值被拒时 <c>errorCode</c> 的线上取值。刻意写成字面量而非
        /// <c>SkillErrorCode.SemanticInvalid.ToWireString()</c>，使那些从不引用该枚举的 skill
        /// 也能照常构造匿名错误对象。
        /// </summary>
        internal const string SemanticInvalidCode = "SEMANTIC_INVALID";

        #region Enum parameters

        /// <summary>
        /// 解析枚举型 skill 参数，大小写不敏感。
        ///
        /// <para><paramref name="value"/> 为 null/空白时返回 true 且 <paramref name="error"/> 为 null，
        /// 表示"没传，跳过我"。此时 <paramref name="result"/> 是 <c>default(TEnum)</c>，
        /// 而这对多数 Unity 枚举都是一个真实成员；因此把"参数缺省"视为"保持原值"的调用方
        /// 仍须自行判断原始字符串（或改用返回可空值的 <see cref="TryParseOptionalEnum{TEnum}"/>）。</para>
        ///
        /// <para>值给了但解析不出来时返回 false，并给出响应形状的 <paramref name="error"/>。
        /// 调用方必须原样返回该对象，且不得写入任何内容。</para>
        /// </summary>
        public static bool TryParseEnumParam<TEnum>(string value, string paramName, out TEnum result, out object error)
            where TEnum : struct
        {
            return TryParseEnumParam<TEnum>(value, paramName, null, out result, out error);
        }

        /// <summary>
        /// 带别名表的 <see cref="TryParseEnumParam{TEnum}(string,string,out TEnum,out object)"/>，
        /// 用于那些 CLR 成员名并非人们实际用词的枚举。
        ///
        /// <para>逼出这个重载的正是 <c>TextureImporterCompression</c>：它声明为
        /// <c>Uncompressed/Compressed/CompressedHQ/CompressedLQ</c>，而所有 skill 描述、模块文档
        /// 和 Unity Inspector 标签写的都是 None / Low Quality / Normal Quality / High Quality。
        /// 没有别名表之前，文档里给出的词 100% 被拒。<paramref name="aliases"/> 是
        /// 别名 → CLR 成员名 的映射，查表时大小写不敏感；CLR 名依旧可用，
        /// 且两种写法都会出现在拒绝消息的 <c>validValues</c> 里，猜错一次即可改对。</para>
        ///
        /// <para>别名查表与解析之前都会先去掉内部空格。CLR 枚举成员名不可能含空格，所以这是无损的，
        /// 也正是它让 Inspector 的原版标签（"Editor GUI"、"Low Quality"、"Normal Map"）能被直接粘贴使用。</para>
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
        /// 别名 → CLR 名，大小写不敏感。下面的表都以
        /// <see cref="StringComparer.OrdinalIgnoreCase"/> 构建，因此线性重扫只在查表未命中时发生——
        /// 也就是所有按 CLR 名书写的值，而表最多也只有几条。调用方自带的 ordinal 字典同样能正常工作。
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
        /// 面向"参数缺省须保持对象原值"的 setter 的 <see cref="TryParseEnumParam{TEnum}"/> 变体：
        /// 什么都没传时 <paramref name="result"/> 为 null，写入点只需一个 <c>HasValue</c> 判断，
        /// <c>default(TEnum)</c> 绝无可能作为真实写入漏进去。
        /// </summary>
        public static bool TryParseOptionalEnum<TEnum>(string value, string paramName, out TEnum? result, out object error)
            where TEnum : struct
        {
            return TryParseOptionalEnum<TEnum>(value, paramName, null, out result, out error);
        }

        /// <summary><see cref="TryParseOptionalEnum{TEnum}(string,string,out TEnum?,out object)"/> 的别名表版本。</summary>
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
        /// 必须解析出成员的枚举参数——用于自身声明的默认值本就是合法名字（"Point"、"Soft"）的
        /// 创建类 skill。在那里，空值是调用方的错误而非"别动它"；放过去会静默写入
        /// <c>default(TEnum)</c>（即 LightType.Spot，而不是文档所说的 "Point"）。
        /// </summary>
        public static bool TryParseRequiredEnum<TEnum>(string value, string paramName, out TEnum result, out object error)
            where TEnum : struct
        {
            return TryParseRequiredEnum<TEnum>(value, paramName, null, out result, out error);
        }

        /// <summary><see cref="TryParseRequiredEnum{TEnum}(string,string,out TEnum,out object)"/> 的别名表版本。</summary>
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
        /// 逗号分隔的 [Flags] 参数，并补上 skill 文档宣称、而枚举本身并未声明的
        /// "Everything"/"Nothing" 别名（Unity 的 StaticEditorFlags 两个都没有，
        /// 以致 <c>optimize_set_static_flags</c> 会拒掉自己文档里的默认值 "Everything"）。
        /// "Nothing" 为 0；"Everything" 是所有非 <c>[Obsolete]</c> 成员的按位或——
        /// StaticEditorFlags 全体或起来是 127，去掉 Unity 自家 Static 下拉框已不再提供的两位
        /// （NavigationStatic 8、OffMeshLinkGeneration 32）后为 87。被弃用的成员其实是三个而不是两个：
        /// 第三个是 LightmapStatic，它与仍在用的 ContributeGI 共用第 1 位，因此不影响结果。
        /// 列出的每一项都必须解析成功——有一个名字错就让整次调用失败，而不是静默缩小集合。
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
                    // 分项级错误只会列出已声明的成员，于是本方法自己补的那两个别名，
                    // 恰恰会从"告诉调用方可以传什么"的那条消息里缺席。
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
        /// 只产出枚举参数的拒绝载荷，不做解析。用于自行手写值映射（对一批不匹配任何 CLR 枚举成员名的
        /// 字符串做 switch）但仍想要统一消息与完整合法值列表的场景。
        /// </summary>
        public static object InvalidEnumError<TEnum>(string value, string paramName) where TEnum : struct
        {
            return InvalidValueError(value, paramName, Vocabulary<TEnum>(null));
        }


        /// <summary>
        /// 背后没有 CLR 枚举、取值来自手写词表（"low"/"medium"/"high"、"sprite"/"texture"/…）时的
        /// 拒绝载荷。
        ///
        /// <para>消息必须以 "Invalid value" 开头，前面不能有别的内容。router 对未声明的错误按消息模式
        /// 分类，其首词判定规则（<c>SkillErrorResponse.LeadingSemanticPattern</c>）正是把非法参数值
        /// 挡在 TARGET_NOT_FOUND 桶之外的关键——.NET 自身的枚举失败文案是
        /// "Requested value 'X' was not found."，否则会先被 not-found 标记认领，
        /// 把调用方引去 gameobject_find。此处同时显式声明 <c>errorCode</c>，
        /// 这样即便日后改了措辞，错误码依然正确。</para>
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
        /// 同样的拒绝载荷，但标注了它来自哪个批处理条目，使 <c>*_batch</c> 调用中失败的那一项
        /// 无需调用方比对输入数组即可定位。
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
        /// <see cref="InvalidEnumError{TEnum}(string,string)"/> 的批处理条目版本。
        /// </summary>
        public static object InvalidEnumError<TEnum>(string value, string paramName, string target) where TEnum : struct
        {
            return InvalidValueError(value, paramName, Vocabulary<TEnum>(null), target);
        }

        /// <summary>
        /// 带别名表的批处理条目版本，使被拒条目列出的词表与单体 setter 列出的完全一致。
        /// </summary>
        public static object InvalidEnumError<TEnum>(string value, string paramName,
            IDictionary<string, string> aliases, string target) where TEnum : struct
        {
            return InvalidValueError(value, paramName, Vocabulary<TEnum>(aliases), target);
        }

        /// <summary>
        /// 解析器需要知道的关于某个枚举类型的全部信息，反射一次后缓存：哪些成员是真实可用的、
        /// 哪些名字值得回传，以及——对 [Flags] 而言——合法的位。
        /// </summary>
        private sealed class EnumFacts
        {
            public bool IsFlags;
            /// <summary>所有已声明的成员名，按声明顺序。</summary>
            public string[] AllNames;
            /// <summary>非 <c>[Obsolete]</c> 的已声明成员名。</summary>
            public string[] LiveNames;
            /// <summary>非 obsolete 成员的取值——普通枚举的合法值集合。</summary>
            public HashSet<long> LiveValues;
            /// <summary>所有已声明成员（含 obsolete）的按位或：[Flags] 值的合法位。</summary>
            public long DeclaredMask;
            /// <summary>非 obsolete 成员的按位或，即 "Everything" 别名的含义。</summary>
            public long LiveMask;

            /// <summary>
            /// 对外公布的词表。普通枚举会剔除 obsolete 成员（那里它们会被拒），
            /// [Flags] 则保留（那里它们仍能解析）——StaticEditorFlags 已弃用的
            /// <c>NavigationStatic</c> 就在本仓库自己文档化的默认列表里。
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

                // 退化情形：若某枚举的每个成员都已弃用，否则会导致任何取值都被拒。
                // 此时回退到全集，而不是变得完全不可用。
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
        /// 取枚举成员（或其背后已装箱基元）的底层整数值。以 ulong 为底且最高位置 1 的枚举会让
        /// <c>Convert.ToInt64</c> 溢出，故改为按位重解释——该值只用于集合成员判定与位掩码。
        /// </summary>
        private static long ToInt64(object raw)
        {
            if (raw is ulong u)
                return unchecked((long)u);
            if (raw is Enum e && Enum.GetUnderlyingType(e.GetType()) == typeof(ulong))
                return unchecked((long)Convert.ToUInt64(e, CultureInfo.InvariantCulture));
            return Convert.ToInt64(raw, CultureInfo.InvariantCulture);
        }

        /// <summary>枚举参数的合法值列表：先 CLR 成员名，再列各别名。</summary>
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
        /// 判断解析出的值是否真是该枚举能表示的值。
        ///
        /// <para>有两种失败方式。其一，<c>Enum.TryParse</c> 接受任意整数字面量，包括背后没有成员的：
        /// 三成员枚举传 "99" 会得到 <c>(TEnum)99</c>，而 [Flags] 枚举的 "999" 过去是原样写进去的，
        /// 因此需要掩码检查来拒绝任何已声明成员都不认领的位。其二，Unity 把大量成员标了
        /// <c>[Obsolete]</c> 而 <c>Enum.IsDefined</c> 照样接受：<c>TextureImporterType.Image</c> 的值是
        /// <c>int.MinValue</c>，过去就是原样落进 importer 的，而任何 Inspector 都显示不出它。
        /// 成员判定按值而非按名，因此活成员的弃用写法仍然可用
        /// （<c>LightType.Area</c> 即 <c>Rectangle</c>，<c>TextureImporterFormat.AutomaticCompressed</c>
        /// 即 <c>Automatic</c>）。</para>
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
        /// <c>TextureImporterCompression</c> 声明为
        /// <c>Uncompressed/Compressed/CompressedHQ/CompressedLQ</c>，但 Inspector、模块文档
        /// 以及本仓库自己的 skill 描述写的都是 None / Low Quality / Normal Quality / High Quality——
        /// 这四个词过去 100% 被拒。现在两种写法都能解析。
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
        /// Inspector 里的 "Editor GUI and Legacy GUI" 贴图类型在 CLR 中是
        /// <c>TextureImporterType.GUI</c>。带别名的解析会去空格，因此原样写的 "Editor GUI" 也能覆盖。
        /// </summary>
        public static readonly IDictionary<string, string> TextureTypeAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "EditorGUI", "GUI" },
            };

        /// <summary>
        /// Rig 下拉框写 "Humanoid"，而 <c>ModelImporterAnimationType</c> 拼作 <c>Human</c>。
        /// </summary>
        public static readonly IDictionary<string, string> ModelAnimationTypeAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Humanoid", "Human" },
            };

        #endregion

        #region Round-trip formatting

        /// <summary>
        /// 给出 <paramref name="value"/> 能原值解析回来的最短、与 culture 无关的表示。
        /// 先试 "R"（现代运行时上最短的往返形式：0.1f 仍是 "0.1"）并通过重新解析验证；
        /// 在 "R" 仍是旧有失真实现的运行时上回退到 "G9"——按有效位数计，它对 float 保证往返无损。
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

        /// <summary><see cref="FormatFloatR"/> 的 double 版本；"G17" 是安全的回退。</summary>
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
        /// 任意装箱值的、与 culture 无关的往返形式，供需要把反射得到的属性值内插进字符串的 skill 使用。
        /// 数值类型走往返格式化器；其余类型沿用自身的 <c>ToString</c>，
        /// 若该类型提供了 invariant 重载则用它。
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

        /// <summary>RGBA，恒输出四个分量——少了 alpha 的回显正是 alpha 被丢弃时的藏身之处。</summary>
        public static string FormatColor(Color c) =>
            $"({FormatFloatR(c.r)}, {FormatFloatR(c.g)}, {FormatFloatR(c.b)}, {FormatFloatR(c.a)})";

        #endregion

        #region JSON-object parameter forms

        /// <summary>
        /// 判断字符串参数看起来像 JSON 对象而非标量/CSV 形式。刻意做得很浅
        /// （以 '{' 开头，且某处有 ':'）：调用方只需决定把文本交给哪个解析器，
        /// 而格式错误的对象应当在 JSON 解析器里以 JSON 错误失败，
        /// 不应被静默地改按逗号列表重试。
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
