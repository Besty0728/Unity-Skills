using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    /// <summary>
    /// 随错误响应一并给出的具体恢复建议，使 AI agent 无需回头询问人类即可自行恢复。
    /// </summary>
    public sealed class SuggestedFix
    {
        /// <summary>动作动词："retry"、"fix_param"、"find_target"、"install_package"、"wait"、"confirm"。</summary>
        public string action;

        /// <summary>可选：建议调用方考虑的替代 skill。</summary>
        public string skill;

        /// <summary>可选：建议调用方据此形状重试的参数。</summary>
        public object args;

        /// <summary>一句话说明该建议的理由。</summary>
        public string reason;
    }

    /// <summary>
    /// REST 错误载荷的统一构造器。所有路由/校验/运行时失败都返回同一形状：
    /// <code>
    /// {
    ///   "status": "error",
    ///   "errorCode": "MISSING_PARAM",
    ///   "error": "...",
    ///   "skill": "...",
    ///   "details": { ... },
    ///   "suggestedFixes": [ ... ],
    ///   "relatedSkills": [ ... ],
    ///   "retryStrategy": "fix_and_retry",
    ///   "retryAfterSeconds": 5
    /// }
    /// </code>
    /// </summary>
    public static class SkillErrorResponse
    {
        // retryStrategy 的稳定线上取值。
        public const string RetryFixAndRetry     = "fix_and_retry";
        public const string RetryWaitAndRetry    = "wait_and_retry";
        public const string RetryFindAndRetry    = "find_target_and_retry";
        public const string RetryInstallAndRetry = "install_and_retry";
        public const string RetryConfirmAndRetry = "confirm_and_retry";
        public const string RetryAskUserAndGrant = "ask_user_and_grant";
        public const string Abort                = "abort";

        private static readonly JsonSerializerSettings _jsonSettings = SkillsCommon.JsonSettings;
        private static JsonSerializer Serializer => JsonSerializer.Create(_jsonSettings);

        public static string Build(
            SkillErrorCode code,
            string message,
            string skill = null,
            object details = null,
            IList<SuggestedFix> suggestedFixes = null,
            IList<string> relatedSkills = null,
            string retryStrategy = null,
            int? retryAfterSeconds = null,
            IDictionary<string, object> extra = null)
        {
            var payload = new JObject
            {
                ["status"] = "error",
                ["errorCode"] = code.ToWireString(),
                ["error"] = message ?? string.Empty,
            };

            if (!string.IsNullOrEmpty(skill))
                payload["skill"] = skill;

            if (details != null)
                payload["details"] = JToken.FromObject(details, Serializer);

            if (suggestedFixes != null && suggestedFixes.Count > 0)
                payload["suggestedFixes"] = JToken.FromObject(suggestedFixes, Serializer);

            if (relatedSkills != null && relatedSkills.Count > 0)
                payload["relatedSkills"] = JArray.FromObject(relatedSkills);

            if (!string.IsNullOrEmpty(retryStrategy))
                payload["retryStrategy"] = retryStrategy;

            if (retryAfterSeconds.HasValue)
                payload["retryAfterSeconds"] = retryAfterSeconds.Value;

            if (extra != null)
            {
                foreach (var kv in extra)
                {
                    if (payload.ContainsKey(kv.Key))
                        continue;
                    payload[kv.Key] = kv.Value == null
                        ? JValue.CreateNull()
                        : JToken.FromObject(kv.Value, Serializer);
                }
            }

            return JsonConvert.SerializeObject(payload, _jsonSettings);
        }

        /// <summary>skill 名查找未命中，可附带模糊匹配给出的候选建议。</summary>
        public static string SkillNotFound(string skillName, IList<string> nearestSkills = null)
        {
            var fixes = new List<SuggestedFix>();
            if (nearestSkills != null)
            {
                foreach (var s in nearestSkills)
                {
                    fixes.Add(new SuggestedFix
                    {
                        action = "retry",
                        skill = s,
                        reason = "Closest registered skill name"
                    });
                }
            }
            fixes.Add(new SuggestedFix
            {
                action = "retry",
                skill = "GET /skills/recommend?intent=...",
                reason = "Discover skills by natural-language intent"
            });

            return Build(
                SkillErrorCode.SkillNotFound,
                $"Skill '{skillName}' not found",
                skill: skillName,
                relatedSkills: nearestSkills,
                suggestedFixes: fixes.Count > 0 ? fixes : null,
                retryStrategy: RetryFixAndRetry);
        }

        /// <summary>
        /// 调用方把 Python 客户端的辅助函数名（如 <c>get_skill_schema</c>）当成 REST skill 发了过来。
        /// 与其他未命中一样报 SKILL_NOT_FOUND，但给的是对应的具体 REST 用法而非模糊名候选：
        /// 这些辅助函数与任何已注册 skill 都没有共同 token，<see cref="SkillNotFound"/> 的
        /// 最近名搜索会返回空，调用方将无从自我纠正。
        /// </summary>
        public static string ClientHelperNotASkill(string helperName, string restEquivalent)
        {
            return Build(
                SkillErrorCode.SkillNotFound,
                $"'{helperName}' is a Python client helper function (unity_skills.py), not a REST skill — " +
                $"POST /skill/{helperName} can never succeed. Use {restEquivalent} instead.",
                skill: helperName,
                suggestedFixes: new List<SuggestedFix>
                {
                    new SuggestedFix
                    {
                        action = "retry",
                        skill = restEquivalent,
                        reason = "REST equivalent of the client-side helper",
                    },
                },
                retryStrategy: RetryFixAndRetry);
        }

        /// <summary>通用内部错误包装，便于调用方处理。</summary>
        public static string Internal(string message, string skill = null) =>
            Build(SkillErrorCode.Internal, message, skill: skill, retryStrategy: Abort);
    }

    /// <summary>
    /// 对一个业务错误得出的分类结论：报哪个错误码、调用方该如何反应、下一步试什么。
    /// </summary>
    public sealed class SkillErrorClassification
    {
        public SkillErrorCode Code;
        public string RetryStrategy;
        public List<SuggestedFix> SuggestedFixes;
        public List<string> RelatedSkills;
    }

    /// <summary>
    /// skill 业务错误的消息模式分类器——router 错误契约的第二层。
    ///
    /// <para>第一层是可选的直通：skill 若在自己的错误对象上声明了 <c>errorCode</c> /
    /// <c>suggestedFixes</c> / <c>retryStrategy</c> / <c>relatedSkills</c>，则原样沿用。
    /// 第二层之所以存在，是因为绝大多数 skill 只返回 <c>new { error = "..." }</c>；
    /// 没有它，这些错误会一律塌缩为 <c>SKILL_ERROR</c> + <c>abort</c>，
    /// 而这对 agent 判断"这次调用值不值得重试"毫无帮助。</para>
    ///
    /// <para>下面的规则是把 <c>*Skills.cs</c> 中实际存在的约 950 条错误字面量分桶归纳出来的，
    /// 不是凭第一性原理推的，覆盖其中约 82%。顺序有意义——第一条命中的规则胜出，
    /// 兜底桶保持既有的 <c>SKILL_ERROR</c> + <c>abort</c> 行为。任何规则都不得产出
    /// <c>wait_and_retry</c>：Python 客户端遇到该策略会自动重试，而需要调用方修正的业务错误
    /// 会因此空转。</para>
    /// </summary>
    public static class SkillErrorClassifier
    {
        // 规则 1 —— 缺少可选的包 / Asset Store 依赖。
        private static readonly string[] DependencyMarkers =
        {
            "not installed", "not imported", "requires com.", "requires the",
            "package manager", "install via", "from the asset store", "未安装",
        };

        // 规则 1b —— "Package not found: com.x" / "Package 'x' does not exist" 指明缺失的正是*包*本身。
        // package 这个词必须紧贴 not-found 短语（两者之间最多允许一个带引号、带括号或带点的包 id）：
        // 错误消息会内插调用方给的标识符，若只用 Contains("package")，任何 jobId
        //（"DefaultPackage_validation_1"）或 "Packages/..." 资产路径都能把一次普通的查找未命中
        // 改判为 MISSING_PACKAGE，把 agent 引去 package_install 而不是去改那个 id 或路径。
        // \bpackage\b 这两者都匹配不上；后顾断言则把 "Group 'g' not found in package 'p'"
        //（在一个已存在的包内部查找）排除在外。
        private static readonly Regex PackageAbsentPattern = new Regex(
            @"(?<!\bin )\bpackage\b(?:\s+(?:'[^']*'|""[^""]*""|\([\w.@/~-]+\)|[\w-]+(?:\.[\w-]+)+))?\s*:?\s*(?:is |was )?(?:not found|does not exist)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // 规则 2 —— 调用方想创建的东西已经存在。
        private static readonly string[] ConflictMarkers =
        {
            "already exists", "already has", "already in use", "already registered", "已存在",
        };

        // 规则 3 —— 目标定位不到。
        private static readonly string[] NotFoundMarkers =
        {
            "not found", "was not found", "no gameobject", "could not find", "could not locate",
            "cannot be found", "does not exist", "doesn't exist", "no such", "not present",
            "找不到", "不存在",
        };

        // 规则 3a —— 目标*已经*定位到了，缺的是调用方指名的属性或字段。必须排在规则 3 之前，
        // 因为后者占据了裸的 "not found" 文本："Property not found: _Cull" 会在那里命中并返回
        // TARGET_NOT_FOUND，于是建议修复把 agent 引去 gameobject_find 找一个从来不是问题所在的对象，
        // 也从未指出它真正需要的属性读取 skill。同样必须排在规则 5 之前——后者的 ^no [a-z] 分支
        // 会认领 "No color property found on material"。
        //
        // 每个分支都锚定在 property/field/enum-value 这类名词上，因此真正的
        // "GameObject not found" / "Material asset not found: <path>" 不受影响——它们都不带这类名词。
        // 这五种形状取自实际存在的错误字面量："<名词> ... not found"（"Property not found: X"、
        // "Property '_x' not found on Rigidbody"、"Property/field not found: X"、
        // "Shader Graph property 'x' was not found"）、只读拒绝、倒装的
        // "No color property found on material"、"<thing> does not have a color property"，
        // 以及下面的枚举值形式。
        //
        // "Enum value 'x' not found for 'm_Foo'" 是同一类缺陷换了说法——对象和属性都已解析成功，
        // 不存在的是那个*值*——但它需要单独一个分支，因为此处 "not found" 位于名词之前而非之后，
        // 第一个分支覆盖不到。
        private static readonly Regex PropertyNotOnTargetPattern = new Regex(
            @"\b(?:propert(?:y|ies)|field)\b[^.;]{0,40}?\b(?:not found|is read-?only)\b" +
            @"|\bno\b[^.;]{0,30}?\bpropert(?:y|ies)\b[^.;]{0,20}?\bfound\b" +
            @"|\bdoes not have\b[^.;]{0,40}?\bpropert(?:y|ies)\b" +
            @"|\benum value\b[^.;]{0,60}?\bnot found\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // 裸的 "shader" 还会匹配 "shaders"（GraphicsSettings 的复数 Always Included Shaders 列表）
        // 和 "shader graph property type not found"（内部类型名查找失败，并非某个材质/shader 实例上
        // 真实存在的属性）——这两者都不该被引向 material_get_properties。因此锚定单数词让复数落空，
        // 并排除 "property type" 让类型查找失败也落空；而 "Shader Graph property 'x' was not found"
        //（真正的具名属性未命中）仍然命中并保持既有路由。
        private static readonly Regex ShaderPropertyPhrase = new Regex(
            @"\bshader\b[^.;]{0,30}?\bpropert(?:y|ies)\b(?!\s+type\b)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // 规则 6 —— 调用方本该给的参数缺失了。"provide " 带一个尾随空格，以免匹配到 "provided"；
        // "no X provided" 那类形式已由规则 4 认领。
        private static readonly string[] MissingParamMarkers =
        {
            "is required", "are required", "required when", "must be provided", "must be specified",
            "provide ", "missing", "必填", "必须提供",
        };

        // 规则 7 —— 参数给了，但不可用。
        private static readonly string[] SemanticMarkers =
        {
            "invalid", "must be", "must not", "must start", "unknown ", "unsupported",
            "out of range", "not allowed", "not a valid", "cannot be", "expected ",
            "非法", "无效",
        };

        // 规则 4 —— "No faces selected" / "No items provided"：调用方压根什么都没传。
        private static readonly Regex NotSuppliedPattern = new Regex(
            @"\bno \S+ (provided|selected|specified|supplied|given)\b|\bno objects selected\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // 规则 5 —— "GameObject has no RectTransform" / "No Light component on X" / "No mesh found"：
        // 对象定位到了，但它身上没有该 skill 需要的东西。
        private static readonly Regex MissingOnTargetPattern = new Regex(
            @"\bhas no \b|\bno \S+ (component|found)\b|\bno \S+ on |^no [a-z]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // 规则 7b —— "Not a texture: X" / "Child is not a Cinemachine Virtual Camera"。
        // 按词锚定，使 "cannot allocate" 和 "not allowed" 无法命中。
        private static readonly Regex WrongKindPattern = new Regex(
            @"\bnot an?\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // 规则 2b —— 消息*开头*就点明了失败种类（"Invalid bindingMode 'X': ..."、
        // "Unknown step 'y'."）。这类消息往往在后半段引用内层异常，而 .NET 自身的枚举解析失败写作
        // "Requested value 'X' was not found"——若不这样处理，它会先命中 not-found 标记，
        // 把一个非法枚举值报成场景对象缺失，把调用方引去 gameobject_find。
        // 锚定在开头，只让消息自己的判定词生效，绝不采信被引用内层文本里埋着的短语。
        private static readonly Regex LeadingSemanticPattern = new Regex(
            @"^\s*(invalid|unknown|unsupported|malformed)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// 把原始 skill 错误消息映射为错误码、重试策略与具体的下一步动作。
        /// 大小写不敏感；绝不返回 null，也绝不抛异常。
        /// </summary>
        public static SkillErrorClassification Classify(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return Unclassified();

            var text = message.ToLowerInvariant();

            if (PackageAbsentPattern.IsMatch(text) || ContainsAny(text, DependencyMarkers))
                return Dependency();

            if (ContainsAny(text, ConflictMarkers))
                return AlreadyExists();

            if (LeadingSemanticPattern.IsMatch(text))
                return SemanticInvalid();

            if (PropertyNotOnTargetPattern.IsMatch(text))
                return PropertyNotOnTarget(text);

            if (ContainsAny(text, NotFoundMarkers))
                return TargetNotFound(text);

            if (NotSuppliedPattern.IsMatch(text))
                return MissingParam();

            if (MissingOnTargetPattern.IsMatch(text))
                return TargetNotFound(text);

            if (ContainsAny(text, MissingParamMarkers))
                return MissingParam();

            if (ContainsAny(text, SemanticMarkers) || WrongKindPattern.IsMatch(text))
                return SemanticInvalid();

            return Unclassified();
        }

        /// <summary>
        /// 为 skill 自己在错误对象上声明的错误码给出配套建议。这让*部分*声明也保持自洽：
        /// 只写了 <c>errorCode</c> 而没写 <c>retryStrategy</c>/<c>suggestedFixes</c> 的 skill，
        /// 拿到的是属于该错误码的建议，而不是碰巧由其消息文本推出来的东西。
        /// 不在本分类器词表内的错误码回退到消息分类——这是故意的：
        /// 声明一个瞬时性错误码（COMPILING、RATE_LIMIT 等）绝不能让 router 推断出
        /// <c>wait_and_retry</c>；需要它的 skill 必须显式声明。
        /// </summary>
        public static SkillErrorClassification ForCode(SkillErrorCode code, string message)
        {
            switch (code)
            {
                case SkillErrorCode.TargetNotFound:
                    return TargetNotFound((message ?? string.Empty).ToLowerInvariant());
                case SkillErrorCode.MissingPackage:
                    return Dependency();
                case SkillErrorCode.MissingParam:
                    return MissingParam();
                case SkillErrorCode.SemanticInvalid:
                    return SemanticInvalid();
                default:
                    return Classify(message);
            }
        }

        private static bool ContainsAny(string text, string[] markers)
        {
            for (int i = 0; i < markers.Length; i++)
            {
                if (text.Contains(markers[i]))
                    return true;
            }
            return false;
        }

        private static SkillErrorClassification Dependency() => new SkillErrorClassification
        {
            Code = SkillErrorCode.MissingPackage,
            RetryStrategy = SkillErrorResponse.RetryInstallAndRetry,
            RelatedSkills = new List<string> { "package_install", "package_list" },
            SuggestedFixes = new List<SuggestedFix>
            {
                new SuggestedFix
                {
                    action = "install_package",
                    skill = "package_install",
                    reason = "The error names the missing package — install it, wait for the domain reload, then retry."
                },
                new SuggestedFix
                {
                    action = "retry",
                    skill = "package_list",
                    reason = "Confirm what is actually installed before assuming the package id."
                },
            },
        };

        private static SkillErrorClassification AlreadyExists() => new SkillErrorClassification
        {
            Code = SkillErrorCode.SemanticInvalid,
            RetryStrategy = SkillErrorResponse.RetryFixAndRetry,
            SuggestedFixes = new List<SuggestedFix>
            {
                new SuggestedFix
                {
                    action = "fix_param",
                    reason = "The target already exists. Retry with a different name/path, or pass the skill's overwrite/force parameter if it has one."
                },
            },
        };

        private static SkillErrorClassification TargetNotFound(string text)
        {
            var classification = new SkillErrorClassification
            {
                Code = SkillErrorCode.TargetNotFound,
                RetryStrategy = SkillErrorResponse.RetryFindAndRetry,
            };

            if (text.Contains("component"))
            {
                classification.RelatedSkills = new List<string> { "component_list", "gameobject_get_info" };
                classification.SuggestedFixes = new List<SuggestedFix>
                {
                    new SuggestedFix
                    {
                        action = "find_target",
                        skill = "component_list",
                        reason = "List the components actually present on the object, then retry with a name from that list."
                    },
                };
                return classification;
            }

            if (ContainsAny(text, AssetMarkers))
            {
                classification.RelatedSkills = new List<string> { "asset_find", "asset_get_info" };
                classification.SuggestedFixes = new List<SuggestedFix>
                {
                    new SuggestedFix
                    {
                        action = "find_target",
                        skill = "asset_find",
                        reason = "Resolve the real project path first — asset paths are case-sensitive and must start with Assets/ or Packages/."
                    },
                };
                return classification;
            }

            // job id 不是场景对象：此处若把调用方引向 gameobject_find，
            // 它会在层级里翻找一个只存在于 job 表中的东西。
            if (text.Contains("job"))
            {
                classification.RelatedSkills = new List<string> { "job_list", "job_status" };
                classification.SuggestedFixes = new List<SuggestedFix>
                {
                    new SuggestedFix
                    {
                        action = "find_target",
                        skill = "job_list",
                        reason = "List the jobs this session still knows about — ids do not survive a domain reload."
                    },
                };
                return classification;
            }

            classification.RelatedSkills = new List<string> { "gameobject_find", "scene_get_hierarchy" };
            classification.SuggestedFixes = new List<SuggestedFix>
            {
                new SuggestedFix
                {
                    action = "find_target",
                    skill = "gameobject_find",
                    reason = "Confirm the target exists in an open scene, then retry with the entityId it returns rather than a name."
                },
                new SuggestedFix
                {
                    action = "find_target",
                    skill = "scene_get_hierarchy",
                    reason = "If the name is a guess, list the hierarchy and pick the exact path."
                },
            };
            return classification;
        }

        /// <summary>
        /// 对象在，属性不在。给 SEMANTIC_INVALID + fix_and_retry：调用方给的名字目标身上没有——
        /// 不需要找什么，只需要改一个参数。
        ///
        /// <para>推荐哪个读取 skill 取决于属性的种类，这正是该建议的全部价值所在：
        /// 对 shader 属性推荐 component_get_properties，和本规则所取代的 gameobject_find 一样没用。
        /// 消息给不出线索时——"Property not found: _Cull" 分不出是材质还是组件——
        /// 两个读取 skill 都给出，而不是猜一个。</para>
        /// </summary>
        private static SkillErrorClassification PropertyNotOnTarget(string text)
        {
            var classification = new SkillErrorClassification
            {
                Code = SkillErrorCode.SemanticInvalid,
                RetryStrategy = SkillErrorResponse.RetryFixAndRetry,
            };

            // 必须排在下面各项检查之前：它引用的 propertyPath 本身可能含有 "shader"/"material"
            //（如 "Enum value 'x' not found for 'm_Shader'"），否则一次序列化枚举失败会被
            // 路由到材质属性读取 skill。
            if (text.Contains("enum value"))
            {
                classification.RelatedSkills = new List<string> { "component_get_serialized_properties" };
                classification.SuggestedFixes = new List<SuggestedFix>
                {
                    new SuggestedFix
                    {
                        action = "fix_param",
                        reason = "The property resolved; the value does not exist on it. The message lists the accepted names — retry with one of those, or a comma-separated set / raw bitmask for a [Flags] enum."
                    },
                    new SuggestedFix
                    {
                        action = "fix_param",
                        skill = "component_get_serialized_properties",
                        reason = "If the accepted names are not enough to tell which property was addressed, list the serialized properties and confirm the propertyPath."
                    },
                };
                return classification;
            }

            // 排除 "GraphicsSettings serialized property not found"——那个 SerializedObject 属于
            // 工程设置资产而非组件，推荐 component_get_serialized_properties 会让调用方
            // 去检视一个从未涉及的对象。
            if (text.Contains("serialized") && !text.Contains("graphicssettings"))
            {
                classification.RelatedSkills = new List<string> { "component_get_serialized_properties" };
                classification.SuggestedFixes = new List<SuggestedFix>
                {
                    new SuggestedFix
                    {
                        action = "fix_param",
                        skill = "component_get_serialized_properties",
                        reason = "Serialized paths are not the C# member names — list them and retry with an exact propertyPath."
                    },
                };
                return classification;
            }

            if (text.Contains("material") || ShaderPropertyPhrase.IsMatch(text))
            {
                classification.RelatedSkills = new List<string> { "material_get_properties" };
                classification.SuggestedFixes = new List<SuggestedFix>
                {
                    new SuggestedFix
                    {
                        action = "fix_param",
                        skill = "material_get_properties",
                        reason = "Shader property names vary by render pipeline (_Color vs _BaseColor). List what this material's shader exposes, then retry with a name from that list."
                    },
                };
                return classification;
            }

            classification.RelatedSkills = new List<string> { "component_get_properties", "material_get_properties" };
            classification.SuggestedFixes = new List<SuggestedFix>
            {
                new SuggestedFix
                {
                    action = "fix_param",
                    skill = "component_get_properties",
                    reason = "The target exists but carries no such property. List the properties it does expose, then retry with one of them — use material_get_properties instead if the target is a material."
                },
            };
            return classification;
        }

        private static readonly string[] AssetMarkers =
        {
            "asset", "path", "file", "folder", "directory", "prefab", "material", "shader", "texture",
        };

        private static SkillErrorClassification MissingParam() => new SkillErrorClassification
        {
            Code = SkillErrorCode.MissingParam,
            RetryStrategy = SkillErrorResponse.RetryFixAndRetry,
            SuggestedFixes = new List<SuggestedFix>
            {
                new SuggestedFix
                {
                    action = "fix_param",
                    skill = "POST /skill/<name>?mode=dryRun",
                    reason = "Supply the parameter named in the message; dryRun returns the full parameter schema without executing."
                },
            },
        };

        private static SkillErrorClassification SemanticInvalid() => new SkillErrorClassification
        {
            Code = SkillErrorCode.SemanticInvalid,
            RetryStrategy = SkillErrorResponse.RetryFixAndRetry,
            SuggestedFixes = new List<SuggestedFix>
            {
                new SuggestedFix
                {
                    action = "fix_param",
                    skill = "POST /skill/<name>?mode=dryRun",
                    reason = "The value is rejected, not the parameter name. Read the accepted range/enum in the message, then dryRun the corrected args."
                },
            },
        };

        // 兜底桶：真正的运行时失败（"Failed to ..."、编辑器卡死状态）。
        // 错误码与策略与本分类器出现之前保持一致。
        private static SkillErrorClassification Unclassified() => new SkillErrorClassification
        {
            Code = SkillErrorCode.SkillError,
            RetryStrategy = SkillErrorResponse.Abort,
        };
    }
}

// Producer:Betsy
