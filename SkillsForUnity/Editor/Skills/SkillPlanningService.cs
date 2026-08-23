using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    internal static class SkillPlanningService
    {
        public static void ApplySemanticValidation(SkillRouter.SkillInfo skill, SkillRouter.ParameterValidationResult validation)
        {
            if (skill == null || validation == null)
                return;

            ApplySemanticPlanner(skill, validation, null);
        }

        /// <summary>
        /// 返回可嵌入 DryRun 响应的 steps/changes 数据；该 skill 没有语义 planner 时返回 null。
        /// </summary>
        public static IDictionary<string, object> BuildPlanData(SkillRouter.SkillInfo skill, SkillRouter.ParameterValidationResult validation)
        {
            if (skill == null || validation == null)
                return null;

            var plan = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            ApplySemanticPlanner(skill, validation, plan);

            // planLevel 未被置为 "semantic"，说明没有任何 planner 命中
            if (!plan.ContainsKey("planLevel") || !"semantic".Equals(plan["planLevel"]?.ToString(), StringComparison.OrdinalIgnoreCase))
                return null;

            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (plan.ContainsKey("steps"))
                result["steps"] = plan["steps"];
            if (plan.ContainsKey("changes"))
                result["changes"] = plan["changes"];
            return result.Count > 0 ? result : null;
        }

        /// <summary>
        /// <c>?mode=plan</c> 的载荷。返回具体字典而非 <c>object</c>，
        /// 以便调用方无需反射即可追加属于自己的信封级区块（surface profile 的
        /// <c>authorization</c> 预览）；键的插入顺序即序列化顺序，故追加的区块落在 "note" 之后。
        /// </summary>
        public static Dictionary<string, object> BuildPlan(SkillRouter.SkillInfo skill, SkillRouter.ParameterValidationResult validation)
        {
            var plan = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["status"] = "plan",
                ["valid"] = validation?.Valid ?? false,
                ["planLevel"] = "generic",
                ["skill"] = BuildSkillDescriptor(skill),
                ["parameters"] = validation?.ParameterDetails?.ToArray() ?? Array.Empty<object>(),
                ["validation"] = BuildValidation(validation),
                ["summary"] = BuildSummary(skill, validation, null),
                ["steps"] = BuildGenericSteps(skill, validation?.Args).ToArray(),
                ["changes"] = BuildGenericChanges(skill, validation?.Args),
                ["workflow"] = BuildWorkflow(skill),
                ["note"] = "No execution performed"
            };

            var serverAvailability = BuildServerAvailability(validation?.Args);
            if (serverAvailability != null)
                plan["serverAvailability"] = serverAvailability;

            ApplySemanticPlanner(skill, validation, plan);

            plan["valid"] = validation?.Valid ?? false;
            plan["validation"] = BuildValidation(validation);
            plan["summary"] = BuildSummary(skill, validation, plan);
            return plan;
        }

        private static Dictionary<string, object> BuildSkillDescriptor(SkillRouter.SkillInfo skill)
        {
            return new Dictionary<string, object>
            {
                ["name"] = skill.Name,
                ["description"] = skill.Description,
                ["category"] = skill.Category != SkillCategory.Uncategorized ? skill.Category.ToString() : null,
                ["operation"] = SkillRouter.FormatOperationForPlanning(skill.Operation),
                ["tags"] = skill.Tags,
                ["outputs"] = skill.Outputs,
                ["requiresInput"] = skill.RequiresInput,
                ["readOnly"] = skill.ReadOnly,
                ["tracksWorkflow"] = skill.TracksWorkflow,
                ["mutatesScene"] = skill.MutatesScene,
                ["mutatesAssets"] = skill.MutatesAssets,
                ["mayTriggerReload"] = skill.MayTriggerReload,
                ["mayEnterPlayMode"] = skill.MayEnterPlayMode,
                ["supportsDryRun"] = skill.SupportsDryRun,
                ["riskLevel"] = skill.RiskLevel,
                ["requiresPackages"] = skill.RequiresPackages
            };
        }

        private static Dictionary<string, object> BuildValidation(SkillRouter.ParameterValidationResult validation)
        {
            return new Dictionary<string, object>
            {
                ["missingParams"] = validation?.MissingParams.Count > 0 ? validation.MissingParams.ToArray() : null,
                ["unknownParams"] = validation?.UnknownParams.Count > 0 ? validation.UnknownParams.ToArray() : null,
                ["typeErrors"] = validation?.TypeErrors.Count > 0 ? validation.TypeErrors.ToArray() : null,
                ["semanticErrors"] = validation?.SemanticErrors.Count > 0 ? validation.SemanticErrors.ToArray() : null,
                ["warnings"] = validation?.Warnings.Count > 0 ? validation.Warnings.ToArray() : null
            };
        }

        private static Dictionary<string, object> BuildSummary(
            SkillRouter.SkillInfo skill,
            SkillRouter.ParameterValidationResult validation,
            IDictionary<string, object> plan)
        {
            var summary = new Dictionary<string, object>
            {
                ["canExecute"] = validation?.Valid ?? false,
                ["requiresConfirmation"] = RequiresConfirmation(skill),
                ["message"] = BuildSummaryMessage(skill, validation)
            };

            if (plan != null && plan.TryGetValue("changes", out var changesObj) && changesObj is IDictionary<string, object> changes)
            {
                summary["changeCounts"] = new Dictionary<string, object>
                {
                    ["create"] = CountEntries(changes, "create"),
                    ["modify"] = CountEntries(changes, "modify"),
                    ["delete"] = CountEntries(changes, "delete")
                };
            }

            return summary;
        }

        private static int CountEntries(IDictionary<string, object> changes, string key)
        {
            if (!changes.TryGetValue(key, out var value) || value == null)
                return 0;

            if (value is Array arr)
                return arr.Length;

            if (value is IEnumerable<object> enumerable)
                return enumerable.Count();

            return 0;
        }

        private static string BuildSummaryMessage(SkillRouter.SkillInfo skill, SkillRouter.ParameterValidationResult validation)
        {
            if (validation == null)
                return $"Plan generated for {skill.Name}.";

            if (!validation.Valid)
                return $"Plan found blocking issues for {skill.Name}. Fix validation errors before executing.";

            if (skill.ReadOnly)
                return $"{skill.Name} is read-only. Execution is safe and does not require workflow rollback.";

            if (skill.TracksWorkflow)
                return $"{skill.Name} can execute with workflow tracking and task-level rollback support.";

            return $"{skill.Name} is ready to execute. Review predicted changes before applying.";
        }

        private static bool RequiresConfirmation(SkillRouter.SkillInfo skill)
        {
            return skill.Operation.HasFlag(SkillOperation.Delete)
                || (skill.Tags?.Any(t => string.Equals(t, "batch", StringComparison.OrdinalIgnoreCase)) ?? false)
                || skill.Category == SkillCategory.Asset
                || skill.Category == SkillCategory.Cleaner;
        }

        private static List<object> BuildGenericSteps(SkillRouter.SkillInfo skill, JObject args)
        {
            return new List<object>
            {
                new Dictionary<string, object>
                {
                    ["index"] = 1,
                    ["skill"] = skill.Name,
                    ["action"] = DescribeAction(skill.Operation),
                    ["target"] = InferPrimaryTarget(args),
                    ["note"] = "Generic plan derived from skill metadata."
                }
            };
        }

        private static Dictionary<string, object> BuildGenericChanges(SkillRouter.SkillInfo skill, JObject args)
        {
            var create = new List<object>();
            var modify = new List<object>();
            var delete = new List<object>();
            var target = InferPrimaryTarget(args);

            var entry = new Dictionary<string, object>
            {
                ["target"] = target,
                ["category"] = skill.Category.ToString(),
                ["operation"] = DescribeAction(skill.Operation)
            };

            if (skill.Operation.HasFlag(SkillOperation.Create))
                create.Add(entry);
            if (skill.Operation.HasFlag(SkillOperation.Modify))
                modify.Add(entry);
            if (skill.Operation.HasFlag(SkillOperation.Delete))
                delete.Add(entry);

            return new Dictionary<string, object>
            {
                ["create"] = create.ToArray(),
                ["modify"] = modify.ToArray(),
                ["delete"] = delete.ToArray()
            };
        }

        private static Dictionary<string, object> BuildWorkflow(SkillRouter.SkillInfo skill)
        {
            return new Dictionary<string, object>
            {
                ["tracksWorkflow"] = skill.TracksWorkflow,
                ["rollbackScope"] = skill.TracksWorkflow ? "task" : "none",
                ["predictedSnapshots"] = skill.TracksWorkflow
                    ? new[]
                    {
                        skill.Operation.HasFlag(SkillOperation.Create) ? "createdObjects" : null,
                        skill.Operation.HasFlag(SkillOperation.Modify) ? "modifiedTargets" : null,
                        skill.Operation.HasFlag(SkillOperation.Delete) ? "deletedTargets" : null
                    }.Where(x => x != null).ToArray()
                    : Array.Empty<string>()
            };
        }

        private static Dictionary<string, object> BuildServerAvailability(JObject args)
        {
            if (args == null)
                return null;

            foreach (var path in GetCandidatePaths(args))
            {
                if (ServerAvailabilityHelper.AffectsScriptDomain(path))
                {
                    return ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                        $"Plan detected a script-domain affecting path: {path}. Execution may briefly disconnect the REST server.",
                        alwaysInclude: true,
                        retryAfterSeconds: 5);
                }
            }

            return null;
        }

        private static IEnumerable<string> GetCandidatePaths(JObject args)
        {
            var keys = new[] { "assetPath", "destinationPath", "sourcePath", "savePath", "materialPath" };
            foreach (var key in keys)
            {
                if (args.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) && token.Type != JTokenType.Null)
                {
                    var value = token.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                        yield return value;
                }
            }

            var itemsJson = GetStringArg(args, "items");
            if (string.IsNullOrWhiteSpace(itemsJson))
                yield break;

            JArray items;
            try
            {
                items = JArray.Parse(itemsJson);
            }
            catch
            {
                yield break;
            }

            foreach (var token in items.OfType<JObject>())
            {
                foreach (var itemKey in new[] { "assetPath", "destinationPath", "sourcePath", "path", "materialPath" })
                {
                    if (token.TryGetValue(itemKey, StringComparison.OrdinalIgnoreCase, out var itemValue) && itemValue.Type != JTokenType.Null)
                    {
                        var value = itemValue.ToString();
                        if (!string.IsNullOrWhiteSpace(value))
                            yield return value;
                    }
                }
            }
        }

        private static string DescribeAction(SkillOperation operation)
        {
            var actions = new List<string>();
            if (operation.HasFlag(SkillOperation.Create)) actions.Add("Create");
            if (operation.HasFlag(SkillOperation.Modify)) actions.Add("Modify");
            if (operation.HasFlag(SkillOperation.Delete)) actions.Add("Delete");
            if (operation.HasFlag(SkillOperation.Query)) actions.Add("Query");
            if (operation.HasFlag(SkillOperation.Analyze)) actions.Add("Analyze");
            if (operation.HasFlag(SkillOperation.Execute)) actions.Add("Execute");
            return actions.Count > 0 ? string.Join("/", actions) : "Execute";
        }

        private static string InferPrimaryTarget(JObject args)
        {
            if (args == null)
                return "(unspecified)";

            var target = GetStringArg(args, "newName", "name", "path", "assetPath", "destinationPath", "sourcePath", "materialPath", "componentType");
            if (!string.IsNullOrWhiteSpace(target))
                return target;

            if (args.TryGetValue("instanceId", StringComparison.OrdinalIgnoreCase, out var idToken) &&
                idToken.Type != JTokenType.Null &&
                idToken.ToObject<int>() != 0)
            {
                return $"instanceId:{idToken.ToObject<int>()}";
            }

            var entityId = GetStringArg(args, "entityId");
            if (!string.IsNullOrWhiteSpace(entityId))
                return $"entityId:{entityId}";

            return "(unspecified)";
        }

        // RequiresInput 里装的是*语义*记号（"gameObject"、"assetPath"、"component"）而非参数名——
        // 所以 SkillRouter.IsParameterRequired 拿它做名字比较几乎从不命中；在本检查出现之前，
        // 那 136 个没有目标就什么都做不了的 skill 对空请求体一律 dryRun 成 valid:true。
        // 每个记号映射到能满足它的参数名集合；广泛使用的记号只在有五个以上 skill 声明时才映射，
        // 其余保持旧行为而不去猜。例外是那种为某个具体 skill 与其记号*一同*编写的映射
        //（最后两条）——那不是对既有声明的猜测，而是新声明自带的强制。
        // 每个候选都必须是某个声明了该记号的 skill 真正接受的参数名。下面的求交会静默丢掉其余的，
        // 因此一个没有任何 skill 接受的名字不是无害的备份：它读起来像一层并不存在的覆盖。
        // 已因此移除两个名字——material 记号下的 "materialPath"（声明它的 16 个 skill 无一接受该键，
        // 材质资产路径是经双用途的 `path` 传入的），以及 assetPath 下的 "path"。
        // "componentName" 保留，因为 smart_reference_bind 确实接受它——这正是肉眼扫查最容易漏掉的
        // "仅一个真实使用者"的情形。
        // 规则由 SkillMetadataGuardTests.RequiredInputGroups_NameOnlyRealParameters 钉住。
        private static readonly Dictionary<string, string[]> _requiredInputGroups =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["gameObject"] = new[] { "name", "path", "instanceId", "entityId" },
            // "A 或 B" 记号：材质 setter 既可作用于场景对象也可作用于材质资产，
            // 两者都经同一个 `path` 参数传入——这正是记号写 `path`
            // 而不写那些 skill 压根不接受的 "materialPath" 的原因。
            ["gameObject|path"] = new[] { "name", "path", "instanceId", "entityId" },
            ["assetPath"] = new[] { "assetPath" },
            ["component"] = new[] { "componentType", "componentName" },
            // 下面两条各自只有一个 skill 声明，仍然做了映射：它们是与该 skill 的记号一同编写的
            //（2026-08-23），所以并没有猜测的成分——不映射的话，记号读起来像契约却什么都不强制，
            // 而这正是这两个 skill 曾把空请求体 dryRun 成 valid 的原因。
            // find_objects_by_name：`name` 是 `nameContains` 的文档化别名。
            ["nameContains|name"] = new[] { "nameContains", "name" },
            // behavior_blackboard_list：接受图资产路径*或*一个 agent 定位符。entityId 应当在此
            //（该 skill 接受 instanceId + name + path，故合成定位符对它可用）——漏掉它会在一个仅仅是
            // 解析失败的 entityId 之上再报一句 "provide one of …"。上面的 find_objects_by_name
            // 出于镜像原因不带 entityId：它没有 instanceId，合成定位符不可用，
            // 而一个没有任何声明方接受的候选读起来就是一层并不存在的覆盖
            //（SkillMetadataGuardTests.RequiredInputGroups_NameOnlyRealParameters）。
            ["gameObject|graphAssetPath"] = new[] { "name", "path", "instanceId", "entityId", "graphAssetPath" },
            // dotween_pro_set_loops：两个互相独立的半边，任一单独就构成完整请求。
            // 与上面两条同样的理由（2026-08-23 随该 skill 的记号一同编写）——不映射则记号什么都不强制，
            // 而"两个都没传"恰恰是过去会成功、并把 loops 静默重置为 CLR 默认值 1 的那种调用。
            // 注意 HasUsableArgument 把数值 0 视为不可用，这在此处是正确的：
            // 0 也不是 DOTween 接受的循环次数（执行时该 skill 会给出更详细说明）。
            ["loops|loopType"] = new[] { "loops", "loopType" },
        };

        /// <summary>
        /// 强制执行 RequiresInput 中"必须指明目标"的那一半——此前无人强制：
        /// 做一次组级检查，确认能满足某语义记号的参数中至少有一个带了可用值。
        ///
        /// <para>只追加"整组缺失"这一条判定。逐参数的必填性仍是 MissingParams 的职责且原样保留——
        /// 两者重叠时（记号的键本身就是必填 CLR 参数），此处保持沉默，
        /// 而不是把同一个空请求体用两种措辞报两遍。</para>
        ///
        /// <para>候选列表会与该 skill 实际接受的参数求交。没有这一步，对那 35 个声明了 RequiresInput
        /// "gameObject" 却只接受 <c>items</c>（所有 <c>*_batch</c>）或使用不同名定位符
        /// （component_copy 的 sourceName/targetName）的 skill 来说，本检查永远无法满足——
        /// 那会把一处元数据不一致变成一个谁都调不动的 skill。这也是记号词表就此为止的原因：
        /// cinemachine 的 <c>vcam</c> 记号求交后只剩 {instanceId, path}，
        /// 会拒掉完全合理的 <c>{vcamName: "CM vcam1"}</c>。</para>
        /// </summary>
        private static void ApplyRequiredInputGroups(
            SkillRouter.SkillInfo skill,
            SkillRouter.ParameterValidationResult validation)
        {
            // BuildPlan 容许 validation 为 null，而与下面逐 skill 的分析器不同，本检查对每个 skill
            // 都会运行——因此这是唯一必须显式处理该情况的地方。
            if (skill == null || validation?.Args == null)
                return;

            var tokens = skill.RequiresInput;
            if (tokens == null || tokens.Length == 0)
                return;

            foreach (var token in tokens)
            {
                if (token == null || !_requiredInputGroups.TryGetValue(token, out var candidates))
                    continue;

                var accepted = candidates.Where(candidate => SkillAcceptsParameter(skill, candidate)).ToArray();
                if (accepted.Length == 0)
                    continue;

                if (accepted.Any(candidate => validation.MissingParams.Any(missing =>
                        string.Equals(missing, candidate, StringComparison.OrdinalIgnoreCase))))
                {
                    continue;
                }

                if (accepted.Any(candidate => HasUsableArgument(validation.Args, candidate)))
                    continue;

                if (TokenAlreadyReported(validation, token, accepted))
                    continue;

                AddSemanticError(validation, RequiredInputGroupField(token),
                    $"Provide one of: {string.Join(", ", accepted)}.");
            }
        }

        /// <summary>
        /// 判断是否已有分析器就该目标发过话——若有，则通用的 "Provide one of: …" 只是同一抱怨的
        /// 第二种措辞。空的 gameobject_delete 请求体过去会同时收到本处的 <c>field: "target"</c>
        /// 和 AnalyzeGameObjectDelete 的 <c>field: "gameObject"</c>，让 agent 去猜自己是有一个问题
        /// 还是两个。
        ///
        /// <para>匹配时除了已接受的参数，也匹配记号自身的各部分：对象定位分析器报在 "gameObject" 名下，
        /// 而那是记号名而非任何 skill 参数；双边记号 "gameObject|path" 则需要认出其中任一半。</para>
        /// </summary>
        private static bool TokenAlreadyReported(
            SkillRouter.ParameterValidationResult validation, string token, string[] accepted)
        {
            if (validation.SemanticErrors.Count == 0)
                return false;

            var tokenParts = token.Split('|');

            foreach (var entry in validation.SemanticErrors)
            {
                if (!SkillResultHelper.TryGetMemberValue(entry, "field", out var fieldValue))
                    continue;

                var field = fieldValue?.ToString();
                if (string.IsNullOrEmpty(field))
                    continue;

                if (tokenParts.Any(part => string.Equals(field, part, StringComparison.OrdinalIgnoreCase)) ||
                    accepted.Any(candidate => string.Equals(field, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 整组缺失错误报在哪个 <c>field</c> 名下。两个对象定位记号用 "target"，
        /// 因为没有哪一个参数该被归咎——调用方一个都没给。
        /// </summary>
        private static string RequiredInputGroupField(string token)
        {
            if (token.StartsWith("gameObject", StringComparison.OrdinalIgnoreCase))
                return "target";
            return token;
        }

        /// <summary>
        /// 判断该 skill 是否接受这个键：其声明的参数，加上 router 为接受 instanceId 的 skill
        /// 注入的合成 entityId 定位符。AllowedParameterSet 正是 router 对该问题的答案，
        /// 读它可保证两边一致。
        /// </summary>
        private static bool SkillAcceptsParameter(SkillRouter.SkillInfo skill, string parameterName)
        {
            if (skill.AllowedParameterSet != null)
                return skill.AllowedParameterSet.Contains(parameterName);

            return skill.ParameterNames != null &&
                skill.ParameterNames.Any(name => string.Equals(name, parameterName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 判断请求体是否给出了定位层真会据以行动的值。与 <see cref="ReadObjectLocator"/> 保持一致：
        /// 空白字符串和 instanceId 0 都表示"没传"——若在此把它们算作已提供，
        /// agent 例行原样发来的 <c>{"name": ""}</c> 或 <c>{"instanceId": 0}</c> 就能绕过本检查。
        ///
        /// <para>数值键里的带引号数字按数字判定，而非按非空字符串判定：
        /// <see cref="GetIntArg"/> 会把 <c>{"instanceId": "0"}</c> 转成 0，定位层随即找不到目标，
        /// 所以把它算作已提供，正是让一个绝无可能解析成功的请求体通过 dryRun、
        /// 只在执行时才失败的原因。字符串定位符保持字符串语义——
        /// <c>{"name": "0"}</c> 指的是名为 "0" 的 GameObject，执行器接受它。</para>
        /// </summary>
        private static bool HasUsableArgument(JObject args, string key)
        {
            if (!args.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) ||
                token == null ||
                token.Type == JTokenType.Null)
            {
                return false;
            }

            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                try { return token.ToObject<double>() != 0d; }
                catch { return false; }
            }

            if (token.Type == JTokenType.String && IsNumericLocatorKey(key))
            {
                // 与 GetIntArg 做同样的转换，因此给同样的判定：任何最终落到 0 的输入
                //（包括它压根解析不了的文本）都会让定位层没有目标。
                try { return token.ToObject<int>() != 0; }
                catch { return false; }
            }

            return !string.IsNullOrWhiteSpace(token.ToString());
        }

        /// <summary>
        /// 定位层经 <see cref="GetIntArg"/> 读取的键。本包中所有 instanceId 形态的 skill 参数
        /// 都声明为 <c>int</c>（instanceId、parentInstanceId、sourceInstanceId、vcamInstanceId …），
        /// 因此靠后缀即可识别，无需维护一份要跟二十来个调用点同步的清单。
        /// </summary>
        private static bool IsNumericLocatorKey(string key) =>
            key != null && key.EndsWith("instanceId", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 只做校验的 planner，用于那种唯一值得规划的风险就是某个枚举参数的 setter。
        /// 在 dryRun 层拒掉非法值——那正是 agent 在提交前会看的地方——除此之外什么都不做。
        ///
        /// <para>它补充而非取代 skill 自身的拒绝：skill 体负责阻止非法值被写入，
        /// 而这里负责阻止 agent 被告知"这次调用已就绪"。复用
        /// <see cref="SkillParamUtil.TryParseEnumParam{TEnum}"/> 意味着同样的大小写不敏感、
        /// 拒绝整数字面量的解析，以及与真正执行时一致的 "Invalid value 'X' for parameter 'Y'.
        /// Valid values: …" 文案。</para>
        ///
        /// <para>刻意不调用 <see cref="MarkSemantic"/>：那会给这些 skill 的每一次 dryRun 都挂上
        /// steps/changes，也就是改变了本来正确的请求体所得到的答复。此处的目的仅是不再对不正确的
        /// 请求体说 valid:true。</para>
        /// </summary>
        private static void AnalyzeEnumSetterParameter<TEnum>(
            SkillRouter.ParameterValidationResult validation,
            string parameterName) where TEnum : struct
        {
            var raw = GetStringArg(validation?.Args, parameterName);
            if (string.IsNullOrWhiteSpace(raw))
                return;

            if (!SkillParamUtil.TryParseEnumParam<TEnum>(raw, parameterName, out _, out var error))
                AddSemanticError(validation, parameterName, ExtractError(error));
        }

        /// <summary>
        /// 只做校验的 planner，用于 skill 以 <see cref="SkillParamUtil.TryParseRequiredEnum{TEnum}"/>
        /// 读取的创建类枚举参数——即自身声明的默认值本就是成员名（"Point"、"soft"）的那种。
        ///
        /// <para>与 <see cref="AnalyzeEnumSetterParameter{TEnum}"/> 只有一处不同，且必须不同：空值。
        /// 不传该键是合法的，因为绑定的是 CLR 默认值而它有效。显式传空串或 null 则不合法，
        /// 因为 ValidateParameters 绑定的是所给的值而不会代入默认值，于是 skill 的
        /// TryParseRequiredEnum 会拒绝它——而 setter 分析器那条"空值意味着别动它，放行"的规则
        /// 会对唯一跑不起来的请求体答 valid:true。</para>
        /// </summary>
        private static void AnalyzeRequiredEnumParameter<TEnum>(
            SkillRouter.ParameterValidationResult validation,
            string parameterName) where TEnum : struct
        {
            var args = validation?.Args;
            if (args == null ||
                !args.TryGetValue(parameterName, StringComparison.OrdinalIgnoreCase, out var token))
            {
                return;
            }

            var raw = token == null || token.Type == JTokenType.Null ? null : token.ToString();
            if (!SkillParamUtil.TryParseRequiredEnum<TEnum>(raw, parameterName, out _, out var error))
                AddSemanticError(validation, parameterName, ExtractError(error));
        }

        private static void ApplySemanticPlanner(
            SkillRouter.SkillInfo skill,
            SkillRouter.ParameterValidationResult validation,
            IDictionary<string, object> plan)
        {
            switch (skill.Name)
            {
                case "camera_set_properties":
                    AnalyzeEnumSetterParameter<CameraClearFlags>(validation, "clearFlags");
                    break;
                case "light_set_properties":
                    AnalyzeEnumSetterParameter<LightShadows>(validation, "shadows");
                    break;
                case "light_create":
                    AnalyzeRequiredEnumParameter<LightType>(validation, "lightType");
                    AnalyzeRequiredEnumParameter<LightShadows>(validation, "shadows");
                    break;
                case "gameobject_create":
                    AnalyzeGameObjectCreate(validation, plan);
                    break;
                case "gameobject_create_batch":
                    AnalyzeGameObjectCreateBatch(validation, plan);
                    break;
                case "gameobject_rename":
                    AnalyzeGameObjectRename(validation, plan);
                    break;
                case "gameobject_rename_batch":
                    AnalyzeGameObjectRenameBatch(validation, plan);
                    break;
                case "gameobject_delete":
                    AnalyzeGameObjectDelete(validation, plan);
                    break;
                case "gameobject_delete_batch":
                    AnalyzeGameObjectDeleteBatch(validation, plan);
                    break;
                case "gameobject_set_parent":
                    AnalyzeGameObjectSetParent(validation, plan);
                    break;
                case "gameobject_set_parent_batch":
                    AnalyzeGameObjectSetParentBatch(validation, plan);
                    break;
                case "component_add":
                    AnalyzeComponentAdd(validation, plan);
                    break;
                case "component_add_batch":
                    AnalyzeComponentAddBatch(validation, plan);
                    break;
                case "component_remove":
                    AnalyzeComponentRemove(validation, plan);
                    break;
                case "component_remove_batch":
                    AnalyzeComponentRemoveBatch(validation, plan);
                    break;
                case "component_set_property":
                    AnalyzeComponentSetProperty(validation, plan);
                    break;
                case "component_set_property_batch":
                    AnalyzeComponentSetPropertyBatch(validation, plan);
                    break;
                case "material_create":
                    AnalyzeMaterialCreate(validation, plan);
                    break;
                case "material_create_batch":
                    AnalyzeMaterialCreateBatch(validation, plan);
                    break;
                case "material_assign":
                    AnalyzeMaterialAssign(validation, plan);
                    break;
                case "material_assign_batch":
                    AnalyzeMaterialAssignBatch(validation, plan);
                    break;
                case "asset_import":
                    AnalyzeAssetImport(validation, plan);
                    break;
                case "asset_import_batch":
                    AnalyzeAssetImportBatch(validation, plan);
                    break;
                case "asset_delete":
                    AnalyzeAssetDelete(validation, plan);
                    break;
                case "asset_delete_batch":
                    AnalyzeAssetDeleteBatch(validation, plan);
                    break;
                case "asset_move":
                    AnalyzeAssetMove(validation, plan);
                    break;
                case "asset_move_batch":
                    AnalyzeAssetMoveBatch(validation, plan);
                    break;
                case "scene_create":
                    AnalyzeSceneCreate(validation, plan);
                    break;
                case "scene_save":
                    AnalyzeSceneSave(validation, plan);
                    break;
                case "scene_load":
                    AnalyzeSceneLoad(validation, plan);
                    break;
                case "prefab_create":
                    AnalyzePrefabCreate(validation, plan);
                    break;
                case "prefab_apply":
                    AnalyzePrefabApply(validation, plan);
                    break;
                case "script_create":
                    AnalyzeScriptCreate(validation, plan);
                    break;
                case "timeline_add_audio_track":
                case "timeline_add_animation_track":
                case "timeline_add_activation_track":
                case "timeline_add_control_track":
                case "timeline_add_signal_track":
                case "timeline_remove_track":
                case "timeline_list_tracks":
                case "timeline_add_clip":
                case "timeline_set_duration":
                case "timeline_play":
                case "timeline_set_binding":
                    AnalyzeTimelineSceneLocatorSkill(skill.Name, validation);
                    break;
            }

            // 必须放最后而非最前：上面逐 skill 的分析器会指名那个真正解析不了的参数，
            // 而本通用检查在其中之一已覆盖同一目标时会主动让位（见 TokenAlreadyReported）。
            // 若放在最前，空请求体会同时拿到两条判定——因为那时还没有任何已上报的内容可供让位。
            ApplyRequiredInputGroups(skill, validation);
        }

        private static void AnalyzeTimelineSceneLocatorSkill(string skillName, SkillRouter.ParameterValidationResult validation)
        {
            var args = validation?.Args;
            if (args == null)
                return;

            var path = GetStringArg(args, "path");
            if (!string.IsNullOrWhiteSpace(path) && path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                AddSemanticError(
                    validation,
                    "path",
                    $"{skillName} 的 path 参数是场景层级路径，不是 Assets 资源路径。请改用场景中的 Timeline GameObject 名称、instanceId 或层级路径。");
            }

            var name = GetStringArg(args, "name");
            if (!string.IsNullOrWhiteSpace(name) && name.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                AddSemanticError(
                    validation,
                    "name",
                    $"{skillName} 面向场景中的 PlayableDirector GameObject，不接受 Assets 资源路径作为 name。");
            }
        }

        private static void AnalyzeGameObjectCreate(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var name = GetStringArg(args, "name");
            AddErrorFromValidation(validation, Validate.Required(name, "name"), "name");

            var primitiveType = GetStringArg(args, "primitiveType");
            if (!string.IsNullOrWhiteSpace(primitiveType) &&
                !primitiveType.Equals("Empty", StringComparison.OrdinalIgnoreCase) &&
                !primitiveType.Equals("None", StringComparison.OrdinalIgnoreCase) &&
                !Enum.TryParse<PrimitiveType>(primitiveType, true, out _))
            {
                AddSemanticError(validation, "primitiveType", $"Unknown primitive type: {primitiveType}");
            }

            GameObject parentGo = null;
            var (hasParent, parentName, parentInstanceId, parentPath, parentEntityId) = ReadObjectLocator(args, "parentName", "parentInstanceId", "parentPath", "parentEntityId");
            if (hasParent)
            {
                var (found, parentErr) = GameObjectFinder.FindOrError(parentName, parentInstanceId, parentPath, entityId: parentEntityId);
                if (parentErr != null)
                    AddSemanticError(validation, "parent", ExtractError(parentErr));
                else
                    parentGo = found;
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                var predictedPath = string.IsNullOrWhiteSpace(name)
                    ? "(unresolved)"
                    : parentGo != null ? GameObjectFinder.GetPath(parentGo) + "/" + name : name;
                SetPlanDetails(
                    plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Create GameObject",
                            ["target"] = name,
                            ["primitiveType"] = string.IsNullOrWhiteSpace(primitiveType) ? "Empty" : primitiveType,
                            ["parent"] = parentGo != null ? GameObjectFinder.GetPath(parentGo) : "(root)"
                        }
                    },
                    create: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["name"] = name,
                            ["predictedPath"] = predictedPath,
                            ["primitiveType"] = string.IsNullOrWhiteSpace(primitiveType) ? "Empty" : primitiveType,
                            ["position"] = new Dictionary<string, object>
                            {
                                ["x"] = GetFloatArg(args, "x"),
                                ["y"] = GetFloatArg(args, "y"),
                                ["z"] = GetFloatArg(args, "z")
                            }
                        }
                    });
            }
        }

        private static void AnalyzeGameObjectCreateBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var ctx = TryBeginBatchAnalyze(validation, plan);
            if (ctx == null) return;

            var creates = new List<object>();
            for (int i = 0; i < ctx.Items.Count; i++)
            {
                var item = ctx.GetItem(i);
                var errors = new List<string>();
                var name = GetStringArg(item, "name");
                if (Validate.Required(name, "name") is object nameErr)
                    errors.Add(ExtractError(nameErr));

                var primitiveType = GetStringArg(item, "primitiveType");
                if (!string.IsNullOrWhiteSpace(primitiveType) &&
                    !primitiveType.Equals("Empty", StringComparison.OrdinalIgnoreCase) &&
                    !primitiveType.Equals("None", StringComparison.OrdinalIgnoreCase) &&
                    !Enum.TryParse<PrimitiveType>(primitiveType, true, out _))
                {
                    errors.Add($"Unknown primitive type: {primitiveType}");
                }

                string parentPath = "(root)";
                var (hasParent, parentName, parentInstanceId, parentLocatorPath, parentEntityId) = ReadObjectLocator(item, "parentName", "parentInstanceId", "parentPath", "parentEntityId");
                if (hasParent)
                {
                    var (parentGo, parentErr) = GameObjectFinder.FindOrError(parentName, parentInstanceId, parentLocatorPath, entityId: parentEntityId);
                    if (parentErr != null)
                        errors.Add(ExtractError(parentErr));
                    else
                        parentPath = GameObjectFinder.GetPath(parentGo);
                }

                ctx.ReportItemErrors(i, errors);

                var predictedPath = string.IsNullOrWhiteSpace(name)
                    ? "(unresolved)"
                    : parentPath == "(root)" ? name : parentPath + "/" + name;
                ctx.AddItemPlan(i,
                    name,
                    errors.Count == 0,
                    errors.ToArray(),
                    new Dictionary<string, object> { ["predictedPath"] = predictedPath });

                if (errors.Count == 0)
                {
                    creates.Add(new Dictionary<string, object>
                    {
                        ["name"] = name,
                        ["predictedPath"] = predictedPath,
                        ["primitiveType"] = string.IsNullOrWhiteSpace(primitiveType) ? "Empty" : primitiveType
                    });
                }
            }
            ctx.EmitPlan("Create GameObjects (batch)", create: creates);
        }

        private static void AnalyzeGameObjectRename(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var newName = GetStringArg(args, "newName");
            AddErrorFromValidation(validation, Validate.Required(newName, "newName"), "newName");

            var (go, error) = ResolveGameObject(args);
            if (error != null)
            {
                AddSemanticError(validation, "gameObject", ExtractError(error));
                return;
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(
                    plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Rename GameObject",
                            ["target"] = GameObjectFinder.GetPath(go),
                            ["newName"] = newName
                        }
                    },
                    modify: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["target"] = GameObjectFinder.GetPath(go),
                            ["oldName"] = go.name,
                            ["newName"] = newName
                        }
                    });
            }
        }

        private static void AnalyzeGameObjectRenameBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var ctx = TryBeginBatchAnalyze(validation, plan);
            if (ctx == null) return;

            var modifies = new List<object>();
            for (int i = 0; i < ctx.Items.Count; i++)
            {
                var item = ctx.GetItem(i);
                var errors = new List<string>();
                var newName = GetStringArg(item, "newName");
                if (Validate.Required(newName, "newName") is object nameErr)
                    errors.Add(ExtractError(nameErr));

                var (go, error) = ResolveGameObject(item);
                if (error != null)
                    errors.Add(ExtractError(error));

                ctx.ReportItemErrors(i, errors);

                ctx.AddItemPlan(i,
                    go != null ? GameObjectFinder.GetPath(go) : InferPrimaryTarget(item),
                    errors.Count == 0,
                    errors.ToArray(),
                    new Dictionary<string, object> { ["newName"] = newName });

                if (errors.Count == 0 && go != null)
                {
                    modifies.Add(new Dictionary<string, object>
                    {
                        ["target"] = GameObjectFinder.GetPath(go),
                        ["oldName"] = go.name,
                        ["newName"] = newName
                    });
                }
            }
            ctx.EmitPlan("Rename GameObjects (batch)", modify: modifies);
        }

        private static void AnalyzeGameObjectSetParent(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;

            var (child, childError) = ResolveGameObject(args, "childName", "childInstanceId", "childPath", "childEntityId");
            if (childError != null)
            {
                AddSemanticError(validation, "child", ExtractError(childError));
                return;
            }

            GameObject parentGo = null;
            var (hasParent, parentName, parentInstanceId, parentPath, parentEntityId) = ReadObjectLocator(args, "parentName", "parentInstanceId", "parentPath", "parentEntityId");
            if (hasParent)
            {
                var (found, parentErr) = GameObjectFinder.FindOrError(parentName, parentInstanceId, parentPath, entityId: parentEntityId);
                if (parentErr != null)
                    AddSemanticError(validation, "parent", ExtractError(parentErr));
                else
                    parentGo = found;
            }

            if (plan != null)
            {
                var oldPath = GameObjectFinder.GetPath(child);
                var newParentPath = parentGo != null ? GameObjectFinder.GetPath(parentGo) : "(root)";
                var predictedPath = parentGo != null ? newParentPath + "/" + child.name : child.name;

                MarkSemantic(plan);
                SetPlanDetails(
                    plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Set Parent",
                            ["target"] = oldPath,
                            ["newParent"] = newParentPath
                        }
                    },
                    modify: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["target"] = oldPath,
                            ["oldPath"] = oldPath,
                            ["newPath"] = predictedPath,
                            ["newParent"] = newParentPath
                        }
                    });
            }
        }

        private static void AnalyzeGameObjectSetParentBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var ctx = TryBeginBatchAnalyze(validation, plan);
            if (ctx == null) return;

            if (plan != null)
            {
                foreach (var token in ctx.Items)
                {
                    var itemObj = token as JObject;
                    var childName = itemObj != null ? GetStringArg(itemObj, "childName", "childPath") : null;
                    var parentName = itemObj != null ? GetStringArg(itemObj, "parentName", "parentPath") : null;
                    ctx.ItemPlans.Add(new Dictionary<string, object>
                    {
                        ["child"] = childName ?? "(unspecified)",
                        ["newParent"] = parentName ?? "(root)"
                    });
                }

                ctx.EmitPlan("Batch Set Parent", modify: new List<object>());
            }
        }

        private static void AnalyzeGameObjectDelete(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var (go, error) = ResolveGameObject(validation.Args);
            if (error != null)
            {
                AddSemanticError(validation, "gameObject", ExtractError(error));
                return;
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(
                    plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Delete GameObject",
                            ["target"] = GameObjectFinder.GetPath(go)
                        }
                    },
                    delete: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["target"] = GameObjectFinder.GetPath(go),
                            ["name"] = go.name
                        }
                    });
            }
        }

        private static void AnalyzeGameObjectDeleteBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var ctx = TryBeginBatchAnalyze(validation, plan);
            if (ctx == null) return;

            var deletes = new List<object>();
            for (int i = 0; i < ctx.Items.Count; i++)
            {
                var token = ctx.Items[i];
                var item = token as JObject;
                if (item == null && token.Type == JTokenType.String)
                    item = new JObject { ["name"] = token.ToString() };
                item ??= new JObject();

                var errors = new List<string>();
                var (go, error) = ResolveGameObject(item);
                if (error != null)
                    errors.Add(ExtractError(error));
                ctx.ReportItemErrors(i, errors);

                ctx.AddItemPlan(i,
                    go != null ? GameObjectFinder.GetPath(go) : InferPrimaryTarget(item),
                    errors.Count == 0,
                    errors.ToArray());

                if (errors.Count == 0 && go != null)
                {
                    deletes.Add(new Dictionary<string, object>
                    {
                        ["target"] = GameObjectFinder.GetPath(go),
                        ["name"] = go.name
                    });
                }
            }
            ctx.EmitPlan("Delete GameObjects (batch)", delete: deletes);
        }

        private static void AnalyzeComponentAdd(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var componentType = GetStringArg(args, "componentType");
            AddErrorFromValidation(validation, Validate.Required(componentType, "componentType"), "componentType");

            var (go, error) = ResolveGameObject(args);
            if (error != null)
            {
                AddSemanticError(validation, "gameObject", ExtractError(error));
                return;
            }

            var type = ComponentSkills.FindComponentType(componentType);
            if (type == null)
            {
                AddSemanticError(validation, "componentType", $"Component type not found: {componentType}");
                return;
            }

            bool alreadyExists = go.GetComponent(type) != null && !AllowsMultiple(type);
            if (alreadyExists)
                AddWarning(validation, $"Component {type.Name} already exists on {go.name}; execution will be a no-op warning.");

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(
                    plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Add Component",
                            ["target"] = GameObjectFinder.GetPath(go),
                            ["componentType"] = type.FullName
                        }
                    },
                    create: alreadyExists
                        ? new List<object>()
                        : new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["target"] = GameObjectFinder.GetPath(go),
                                ["component"] = type.Name,
                                ["fullTypeName"] = type.FullName
                            }
                        });
            }
        }

        private static void AnalyzeComponentAddBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var ctx = TryBeginBatchAnalyze(validation, plan);
            if (ctx == null) return;

            var creates = new List<object>();
            for (int i = 0; i < ctx.Items.Count; i++)
            {
                var item = ctx.GetItem(i);
                var errors = new List<string>();
                var warnings = new List<string>();

                var componentType = GetStringArg(item, "componentType");
                if (Validate.Required(componentType, "componentType") is object typeErr)
                    errors.Add(ExtractError(typeErr));

                var (go, error) = ResolveGameObject(item);
                if (error != null)
                    errors.Add(ExtractError(error));

                Type type = null;
                if (errors.Count == 0)
                {
                    type = ComponentSkills.FindComponentType(componentType);
                    if (type == null)
                        errors.Add($"Component type not found: {componentType}");
                    else if (go.GetComponent(type) != null && !AllowsMultiple(type))
                        warnings.Add($"Component {type.Name} already exists on {go.name}");
                }

                ctx.ReportItemErrors(i, errors);
                foreach (var warning in warnings)
                    AddWarning(validation, $"items[{i}]: {warning}");

                ctx.AddItemPlan(i,
                    go != null ? GameObjectFinder.GetPath(go) : InferPrimaryTarget(item),
                    errors.Count == 0,
                    errors.ToArray());

                if (errors.Count == 0 && type != null && warnings.Count == 0)
                {
                    creates.Add(new Dictionary<string, object>
                    {
                        ["target"] = GameObjectFinder.GetPath(go),
                        ["component"] = type.Name,
                        ["fullTypeName"] = type.FullName
                    });
                }
            }
            ctx.EmitPlan("Add Components (batch)", create: creates);
        }

        private static void AnalyzeComponentRemove(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var componentType = GetStringArg(args, "componentType");
            AddErrorFromValidation(validation, Validate.Required(componentType, "componentType"), "componentType");

            var (go, error) = ResolveGameObject(args);
            if (error != null)
            {
                AddSemanticError(validation, "gameObject", ExtractError(error));
                return;
            }

            var type = ComponentSkills.FindComponentType(componentType);
            if (type == null)
            {
                AddSemanticError(validation, "componentType", $"Component type not found: {componentType}");
                return;
            }

            var components = go.GetComponents(type);
            if (components.Length == 0)
            {
                AddSemanticError(validation, "component", $"Component not found on {go.name}: {componentType}");
                return;
            }

            int componentIndex = GetIntArg(args, "componentIndex");
            if (componentIndex >= components.Length)
            {
                AddSemanticError(validation, "componentIndex", $"Component index {componentIndex} out of range. Found {components.Length} components of type {componentType}");
                return;
            }

            var requiredBy = GetRequiredByComponents(go, type);
            if (requiredBy.Any())
            {
                AddSemanticError(validation, "component", $"Cannot remove {componentType} - required by: {string.Join(", ", requiredBy)}");
                return;
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(
                    plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Remove Component",
                            ["target"] = GameObjectFinder.GetPath(go),
                            ["componentType"] = type.FullName,
                            ["componentIndex"] = componentIndex
                        }
                    },
                    delete: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["target"] = GameObjectFinder.GetPath(go),
                            ["component"] = type.Name,
                            ["componentIndex"] = componentIndex
                        }
                    });
            }
        }

        private static void AnalyzeComponentRemoveBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var ctx = TryBeginBatchAnalyze(validation, plan);
            if (ctx == null) return;

            var deletes = new List<object>();
            for (int i = 0; i < ctx.Items.Count; i++)
            {
                var item = ctx.GetItem(i);
                var errors = new List<string>();

                var componentType = GetStringArg(item, "componentType");
                if (Validate.Required(componentType, "componentType") is object typeErr)
                    errors.Add(ExtractError(typeErr));

                var (go, error) = ResolveGameObject(item);
                if (error != null)
                    errors.Add(ExtractError(error));

                Type type = null;
                int count = 0;
                if (errors.Count == 0)
                {
                    type = ComponentSkills.FindComponentType(componentType);
                    if (type == null)
                        errors.Add($"Component type not found: {componentType}");
                    else
                    {
                        var components = go.GetComponents(type);
                        count = components.Length;
                        if (count == 0)
                            errors.Add($"Component not found: {componentType}");
                        else if (GetRequiredByComponents(go, type).Any())
                            errors.Add($"Cannot remove {componentType} because another component requires it");
                    }
                }

                ctx.ReportItemErrors(i, errors);

                ctx.AddItemPlan(i,
                    go != null ? GameObjectFinder.GetPath(go) : InferPrimaryTarget(item),
                    errors.Count == 0,
                    errors.ToArray());

                if (errors.Count == 0 && type != null)
                {
                    deletes.Add(new Dictionary<string, object>
                    {
                        ["target"] = GameObjectFinder.GetPath(go),
                        ["component"] = type.Name,
                        ["count"] = count
                    });
                }
            }
            ctx.EmitPlan("Remove Components (batch)", delete: deletes);
        }

        private static void AnalyzeComponentSetProperty(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var componentType = GetStringArg(args, "componentType");
            var propertyName = GetStringArg(args, "propertyName");
            AddErrorFromValidation(validation, Validate.Required(componentType, "componentType"), "componentType");
            AddErrorFromValidation(validation, Validate.Required(propertyName, "propertyName"), "propertyName");

            var (go, error) = ResolveGameObject(args);
            if (error != null)
            {
                AddSemanticError(validation, "gameObject", ExtractError(error));
                return;
            }

            var type = ComponentSkills.FindComponentType(componentType);
            if (type == null)
            {
                AddSemanticError(validation, "componentType", $"Component type not found: {componentType}");
                return;
            }

            var comp = go.GetComponent(type);
            if (comp == null)
            {
                AddSemanticError(validation, "component", $"Component not found: {componentType}");
                return;
            }

            var (prop, field) = FindMember(type, propertyName);
            if (prop == null && field == null)
            {
                AddSemanticError(validation, "propertyName", $"Property/field not found: {propertyName}");
                return;
            }

            var targetType = prop?.PropertyType ?? field?.FieldType;
            if (targetType == null)
            {
                AddSemanticError(validation, "propertyName", $"Property/field not found: {propertyName}");
                return;
            }

            if (prop != null && !prop.CanWrite)
            {
                AddSemanticError(validation, "propertyName", $"Property {propertyName} is read-only");
                return;
            }

            TryValidateComponentAssignment(args, propertyName, targetType, validation);

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(
                    plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Set Component Property",
                            ["target"] = GameObjectFinder.GetPath(go),
                            ["componentType"] = type.FullName,
                            ["propertyName"] = propertyName,
                            ["valueType"] = targetType.Name
                        }
                    },
                    modify: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["target"] = GameObjectFinder.GetPath(go),
                            ["component"] = type.Name,
                            ["property"] = propertyName,
                            ["valueType"] = targetType.Name
                        }
                    });
            }
        }

        private static void AnalyzeComponentSetPropertyBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var ctx = TryBeginBatchAnalyze(validation, plan);
            if (ctx == null) return;

            var modifies = new List<object>();
            for (int i = 0; i < ctx.Items.Count; i++)
            {
                var item = ctx.GetItem(i);
                var iv = new SkillRouter.ParameterValidationResult { Args = item };
                AnalyzeComponentSetProperty(iv, null);
                ctx.ReportDelegatedErrors(i, iv);
                foreach (var warning in iv.Warnings)
                    AddWarning(validation, $"items[{i}]: {warning}");

                var (go, _) = ResolveGameObject(item);
                ctx.AddItemPlan(i,
                    go != null ? GameObjectFinder.GetPath(go) : InferPrimaryTarget(item),
                    iv.SemanticErrors.Count == 0,
                    iv.SemanticErrors.Select(ExtractSemanticMessage).ToArray());

                if (iv.SemanticErrors.Count == 0 && go != null)
                    modifies.Add(new Dictionary<string, object>
                    {
                        ["target"] = GameObjectFinder.GetPath(go),
                        ["component"] = GetStringArg(item, "componentType"),
                        ["property"] = GetStringArg(item, "propertyName")
                    });
            }
            ctx.EmitPlan("Set Component Properties (batch)", modify: modifies);
        }

        private static void AnalyzeMaterialCreate(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var name = GetStringArg(args, "name");
            AddErrorFromValidation(validation, Validate.Required(name, "name"), "name");

            var savePath = GetStringArg(args, "savePath");
            if (!string.IsNullOrEmpty(savePath) && Validate.SafePath(savePath, "savePath") is object saveErr)
                AddSemanticError(validation, "savePath", ExtractError(saveErr));

            var resolvedShaderName = ResolveShaderName(GetStringArg(args, "shaderName"), validation);
            string resolvedPath = null;
            if (!string.IsNullOrEmpty(savePath) && !string.IsNullOrEmpty(name))
                resolvedPath = ResolveMaterialSavePath(savePath, name);
            else if (string.IsNullOrEmpty(savePath))
                AddWarning(validation, "Material will be created in memory only because savePath is omitted.");

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(
                    plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Create Material",
                            ["target"] = name,
                            ["shader"] = resolvedShaderName,
                            ["savePath"] = resolvedPath
                        }
                    },
                    create: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["name"] = name,
                            ["shader"] = resolvedShaderName,
                            ["path"] = resolvedPath,
                            ["renderPipeline"] = ProjectSkills.DetectRenderPipeline().ToString(),
                            ["persistent"] = !string.IsNullOrEmpty(resolvedPath)
                        }
                    });
            }
        }

        private static void AnalyzeMaterialCreateBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var ctx = TryBeginBatchAnalyze(validation, plan);
            if (ctx == null) return;

            var creates = new List<object>();
            for (int i = 0; i < ctx.Items.Count; i++)
            {
                var item = ctx.GetItem(i);
                var iv = new SkillRouter.ParameterValidationResult { Args = item };
                AnalyzeMaterialCreate(iv, null);

                var name = GetStringArg(item, "name");
                var savePath = GetStringArg(item, "savePath");
                var resolvedShader = ResolveShaderName(GetStringArg(item, "shaderName"), iv);

                ctx.ReportDelegatedErrors(i, iv);
                foreach (var warning in iv.Warnings)
                    AddWarning(validation, $"items[{i}]: {warning}");

                ctx.AddItemPlan(i,
                    name,
                    iv.SemanticErrors.Count == 0,
                    iv.SemanticErrors.Select(ExtractSemanticMessage).ToArray());

                if (iv.SemanticErrors.Count == 0)
                {
                    creates.Add(new Dictionary<string, object>
                    {
                        ["name"] = name,
                        ["shader"] = resolvedShader,
                        ["path"] = !string.IsNullOrEmpty(savePath) && !string.IsNullOrEmpty(name) ? ResolveMaterialSavePath(savePath, name) : null
                    });
                }
            }
            ctx.EmitPlan("Create Materials (batch)", create: creates);
        }

        private static void AnalyzeMaterialAssign(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var materialPath = GetStringArg(args, "materialPath");
            AddErrorFromValidation(validation, Validate.Required(materialPath, "materialPath"), "materialPath");

            var (go, error) = ResolveGameObject(args);
            if (error != null)
            {
                AddSemanticError(validation, "gameObject", ExtractError(error));
                return;
            }

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
            {
                AddSemanticError(validation, "renderer", "No Renderer component found");
                return;
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                AddSemanticError(validation, "materialPath", $"Material not found: {materialPath}");
                return;
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Assign Material",
                            ["target"] = GameObjectFinder.GetPath(go),
                            ["materialPath"] = materialPath
                        }
                    },
                    modify: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["target"] = GameObjectFinder.GetPath(go),
                            ["material"] = materialPath,
                            ["rendererType"] = renderer.GetType().Name
                        }
                    });
            }
        }

        private static void AnalyzeMaterialAssignBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var ctx = TryBeginBatchAnalyze(validation, plan);
            if (ctx == null) return;

            var modifies = new List<object>();
            for (int i = 0; i < ctx.Items.Count; i++)
            {
                var item = ctx.GetItem(i);
                var iv = new SkillRouter.ParameterValidationResult { Args = item };
                AnalyzeMaterialAssign(iv, null);
                ctx.ReportDelegatedErrors(i, iv);

                var (go, _) = ResolveGameObject(item);
                ctx.AddItemPlan(i,
                    go != null ? GameObjectFinder.GetPath(go) : InferPrimaryTarget(item),
                    iv.SemanticErrors.Count == 0,
                    iv.SemanticErrors.Select(ExtractSemanticMessage).ToArray());

                if (iv.SemanticErrors.Count == 0 && go != null)
                    modifies.Add(new Dictionary<string, object>
                    {
                        ["target"] = GameObjectFinder.GetPath(go),
                        ["material"] = GetStringArg(item, "materialPath")
                    });
            }
            ctx.EmitPlan("Assign Materials (batch)", modify: modifies);
        }

        private static void AnalyzeAssetImport(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var sourcePath = GetStringArg(args, "sourcePath");
            var destinationPath = GetStringArg(args, "destinationPath");
            AddErrorFromValidation(validation, Validate.Required(sourcePath, "sourcePath"), "sourcePath");
            AddErrorFromValidation(validation, Validate.Required(destinationPath, "destinationPath"), "destinationPath");

            if (!string.IsNullOrEmpty(sourcePath))
            {
                bool isDir = Directory.Exists(sourcePath);
                if (!File.Exists(sourcePath) && !isDir)
                    AddSemanticError(validation, "sourcePath", $"Source not found: {sourcePath}");
                else if (isDir)
                    AddSemanticError(validation, "sourcePath", $"Source path must be a file, not a directory: {sourcePath}");
            }
            if (!string.IsNullOrEmpty(destinationPath) && Validate.SafePath(destinationPath, "destinationPath") is object dstErr)
                AddSemanticError(validation, "destinationPath", ExtractError(dstErr));

            if (plan != null)
            {
                MarkSemantic(plan);
                var dir = string.IsNullOrEmpty(destinationPath) ? null : Path.GetDirectoryName(destinationPath)?.Replace("\\", "/");
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Import Asset",
                            ["target"] = destinationPath,
                            ["sourcePath"] = sourcePath
                        }
                    },
                    create: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["sourcePath"] = sourcePath,
                            ["destinationPath"] = destinationPath,
                            ["createsDirectory"] = !string.IsNullOrEmpty(dir) && !Directory.Exists(dir)
                        }
                    });
            }
        }

        private static void AnalyzeAssetImportBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var ctx = TryBeginBatchAnalyze(validation, plan);
            if (ctx == null) return;

            var creates = new List<object>();
            for (int i = 0; i < ctx.Items.Count; i++)
            {
                var item = ctx.GetItem(i);
                var iv = new SkillRouter.ParameterValidationResult { Args = item };
                AnalyzeAssetImport(iv, null);
                ctx.ReportDelegatedErrors(i, iv);

                ctx.AddItemPlan(i,
                    GetStringArg(item, "destinationPath") ?? GetStringArg(item, "sourcePath"),
                    iv.SemanticErrors.Count == 0,
                    iv.SemanticErrors.Select(ExtractSemanticMessage).ToArray());

                if (iv.SemanticErrors.Count == 0)
                {
                    creates.Add(new Dictionary<string, object>
                    {
                        ["sourcePath"] = GetStringArg(item, "sourcePath"),
                        ["destinationPath"] = GetStringArg(item, "destinationPath")
                    });
                }
            }
            ctx.EmitPlan("Import Assets (batch)", create: creates);
        }

        private static void AnalyzeAssetDelete(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var assetPath = GetStringArg(validation.Args, "assetPath");
            AddErrorFromValidation(validation, Validate.Required(assetPath, "assetPath"), "assetPath");
            if (!string.IsNullOrEmpty(assetPath) && Validate.SafePath(assetPath, "assetPath", isDelete: true) is object pathErr)
                AddSemanticError(validation, "assetPath", ExtractError(pathErr));
            if (!string.IsNullOrEmpty(assetPath) && !SkillsCommon.PathExists(assetPath))
                AddSemanticError(validation, "assetPath", $"Asset not found: {assetPath}");

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Delete Asset",
                            ["target"] = assetPath
                        }
                    },
                    delete: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["target"] = assetPath
                        }
                    });
            }
        }

        private static void AnalyzeAssetDeleteBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var ctx = TryBeginBatchAnalyze(validation, plan);
            if (ctx == null) return;

            var deletes = new List<object>();
            for (int i = 0; i < ctx.Items.Count; i++)
            {
                var item = ctx.GetItem(i);
                var path = GetStringArg(item, "path");
                var errors = new List<string>();
                if (Validate.Required(path, "path") is object pathRequired)
                    errors.Add(ExtractError(pathRequired));
                else
                {
                    if (Validate.SafePath(path, "path", isDelete: true) is object pathErr)
                        errors.Add(ExtractError(pathErr));
                    if (!SkillsCommon.PathExists(path))
                        errors.Add($"Asset not found: {path}");
                }

                ctx.ReportItemErrors(i, errors);

                ctx.AddItemPlan(i,
                    path,
                    errors.Count == 0,
                    errors.ToArray());

                if (errors.Count == 0)
                    deletes.Add(new Dictionary<string, object> { ["target"] = path });
            }
            ctx.EmitPlan("Delete Assets (batch)", delete: deletes);
        }

        private static void AnalyzeAssetMove(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var sourcePath = GetStringArg(args, "sourcePath");
            var destinationPath = GetStringArg(args, "destinationPath");
            AddErrorFromValidation(validation, Validate.Required(sourcePath, "sourcePath"), "sourcePath");
            AddErrorFromValidation(validation, Validate.Required(destinationPath, "destinationPath"), "destinationPath");

            if (!string.IsNullOrEmpty(sourcePath) && Validate.SafePath(sourcePath, "sourcePath") is object srcErr)
                AddSemanticError(validation, "sourcePath", ExtractError(srcErr));
            if (!string.IsNullOrEmpty(destinationPath) && Validate.SafePath(destinationPath, "destinationPath") is object dstErr)
                AddSemanticError(validation, "destinationPath", ExtractError(dstErr));
            if (!string.IsNullOrEmpty(sourcePath) && !SkillsCommon.PathExists(sourcePath))
                AddSemanticError(validation, "sourcePath", $"Asset not found: {sourcePath}");
            if (!string.IsNullOrEmpty(destinationPath) && AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(destinationPath) != null)
                AddWarning(validation, $"Destination already exists and may cause move failure: {destinationPath}");

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Move Asset",
                            ["target"] = sourcePath,
                            ["destinationPath"] = destinationPath
                        }
                    },
                    modify: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["from"] = sourcePath,
                            ["to"] = destinationPath
                        }
                    });
            }
        }

        private static void AnalyzeAssetMoveBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var ctx = TryBeginBatchAnalyze(validation, plan);
            if (ctx == null) return;

            var modifies = new List<object>();
            for (int i = 0; i < ctx.Items.Count; i++)
            {
                var item = ctx.GetItem(i);
                var iv = new SkillRouter.ParameterValidationResult { Args = item };
                AnalyzeAssetMove(iv, null);
                ctx.ReportDelegatedErrors(i, iv);
                foreach (var warning in iv.Warnings)
                    AddWarning(validation, $"items[{i}]: {warning}");

                ctx.AddItemPlan(i,
                    GetStringArg(item, "sourcePath"),
                    iv.SemanticErrors.Count == 0,
                    iv.SemanticErrors.Select(ExtractSemanticMessage).ToArray());

                if (iv.SemanticErrors.Count == 0)
                {
                    modifies.Add(new Dictionary<string, object>
                    {
                        ["from"] = GetStringArg(item, "sourcePath"),
                        ["to"] = GetStringArg(item, "destinationPath")
                    });
                }
            }
            ctx.EmitPlan("Move Assets (batch)", modify: modifies);
        }

        // ==================================================================================
        // 场景语义 planner
        // ==================================================================================

        private static void AnalyzeSceneCreate(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var scenePath = GetStringArg(args, "scenePath");
            AddErrorFromValidation(validation, Validate.Required(scenePath, "scenePath"), "scenePath");
            AddErrorFromValidation(validation, Validate.SafePath(scenePath, "scenePath"), "scenePath");

            if (!string.IsNullOrWhiteSpace(scenePath))
            {
                if (!scenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    scenePath += ".unity";

                if (File.Exists(scenePath))
                    AddWarning(validation, $"Scene already exists at '{scenePath}' and will be overwritten.");
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Create Scene",
                            ["target"] = scenePath ?? "(unresolved)"
                        }
                    },
                    create: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "Scene",
                            ["path"] = scenePath
                        }
                    });

                plan["serverAvailability"] = ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                    "Creating a new scene may briefly interrupt the connection.",
                    alwaysInclude: false, retryAfterSeconds: 3);
            }
        }

        private static void AnalyzeSceneSave(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var scenePath = GetStringArg(args, "scenePath");

            if (!string.IsNullOrWhiteSpace(scenePath))
                AddErrorFromValidation(validation, Validate.SafePath(scenePath, "scenePath"), "scenePath");

            var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var targetPath = !string.IsNullOrWhiteSpace(scenePath) ? scenePath : activeScene.path;

            if (string.IsNullOrWhiteSpace(targetPath))
                AddWarning(validation, "Scene has no path yet. A Save As dialog may appear or a default path will be used.");

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Save Scene",
                            ["target"] = targetPath ?? activeScene.name
                        }
                    },
                    modify: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "Scene",
                            ["path"] = targetPath,
                            ["sceneName"] = activeScene.name,
                            ["isDirty"] = activeScene.isDirty
                        }
                    });
            }
        }

        private static void AnalyzeSceneLoad(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var scenePath = GetStringArg(args, "scenePath");
            AddErrorFromValidation(validation, Validate.Required(scenePath, "scenePath"), "scenePath");

            if (!string.IsNullOrWhiteSpace(scenePath) && !File.Exists(scenePath))
                AddSemanticError(validation, "scenePath", $"Scene file not found: {scenePath}");

            var additive = args?["additive"]?.Value<bool>() ?? false;
            var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();

            if (!additive && activeScene.isDirty)
                AddWarning(validation, $"Active scene '{activeScene.name}' has unsaved changes that will be lost.");

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = additive ? "Load Scene (Additive)" : "Load Scene",
                            ["target"] = scenePath ?? "(unresolved)",
                            ["additive"] = additive
                        }
                    },
                    modify: additive ? null : new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "ActiveScene",
                            ["from"] = activeScene.path,
                            ["to"] = scenePath
                        }
                    });

                if (!additive)
                {
                    plan["serverAvailability"] = ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                        "Loading a scene (non-additive) replaces the current scene and may briefly interrupt the connection.",
                        alwaysInclude: false, retryAfterSeconds: 3);
                }
            }
        }

        // ==================================================================================
        // Prefab 语义 planner
        // ==================================================================================

        private static void AnalyzePrefabCreate(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var savePath = GetStringArg(args, "savePath");
            AddErrorFromValidation(validation, Validate.Required(savePath, "savePath"), "savePath");
            AddErrorFromValidation(validation, Validate.SafePath(savePath, "savePath"), "savePath");

            var (go, goErr) = ResolveGameObject(args);
            if (goErr != null)
                AddSemanticError(validation, "gameObject", ExtractError(goErr));

            if (!string.IsNullOrWhiteSpace(savePath))
            {
                if (!savePath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    savePath += ".prefab";
                if (File.Exists(savePath))
                    AddWarning(validation, $"Prefab already exists at '{savePath}' and will be overwritten.");
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                var goName = go != null ? go.name : GetStringArg(args, "name", "path");
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Create Prefab",
                            ["source"] = goName ?? "(unresolved)",
                            ["target"] = savePath ?? "(unresolved)"
                        }
                    },
                    create: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "Prefab",
                            ["path"] = savePath,
                            ["sourceName"] = goName
                        }
                    });
            }
        }

        private static void AnalyzePrefabApply(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var (go, goErr) = ResolveGameObject(args);
            if (goErr != null)
            {
                AddSemanticError(validation, "gameObject", ExtractError(goErr));
            }
            else if (go != null)
            {
                if (!UnityEditor.PrefabUtility.IsPartOfPrefabInstance(go))
                    AddSemanticError(validation, "gameObject", $"'{go.name}' is not a prefab instance.");
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                var goName = go != null ? go.name : GetStringArg(args, "name", "path");
                string prefabPath = null;
                if (go != null && UnityEditor.PrefabUtility.IsPartOfPrefabInstance(go))
                {
                    var prefab = UnityEditor.PrefabUtility.GetCorrespondingObjectFromSource(go);
                    if (prefab != null)
                        prefabPath = AssetDatabase.GetAssetPath(prefab);
                }

                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Apply Prefab Overrides",
                            ["source"] = goName ?? "(unresolved)",
                            ["target"] = prefabPath ?? "(unresolved)"
                        }
                    },
                    modify: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "Prefab",
                            ["instanceName"] = goName,
                            ["prefabPath"] = prefabPath
                        }
                    });
            }
        }

        // ==================================================================================
        // 脚本语义 planner
        // ==================================================================================

        private static void AnalyzeScriptCreate(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var scriptName = GetStringArg(args, "scriptName", "name");
            var folder = GetStringArg(args, "folder") ?? "Assets/Scripts";

            if (string.IsNullOrWhiteSpace(scriptName))
                AddSemanticError(validation, "scriptName", "scriptName or name is required.");

            if (!string.IsNullOrWhiteSpace(scriptName))
            {
                // 校验 C# 类名
                if (!System.Text.RegularExpressions.Regex.IsMatch(scriptName, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                    AddSemanticError(validation, "scriptName", $"'{scriptName}' is not a valid C# class name.");

                var predictedPath = Path.Combine(folder, scriptName + ".cs").Replace('\\', '/');
                if (File.Exists(predictedPath))
                    AddWarning(validation, $"Script already exists at '{predictedPath}' and will be overwritten.");
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                var predictedPath = !string.IsNullOrWhiteSpace(scriptName)
                    ? Path.Combine(folder, scriptName + ".cs").Replace('\\', '/')
                    : "(unresolved)";

                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Create C# Script",
                            ["target"] = predictedPath
                        }
                    },
                    create: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "Script",
                            ["path"] = predictedPath,
                            ["className"] = scriptName
                        }
                    });

                plan["serverAvailability"] = ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                    "Creating a C# script triggers compilation and Domain Reload. The REST server will be briefly unavailable.",
                    alwaysInclude: true, retryAfterSeconds: 10);
            }
        }

        private static void TryValidateComponentAssignment(
            JObject args,
            string propertyName,
            Type targetType,
            SkillRouter.ParameterValidationResult validation)
        {
            var assetPath = GetStringArg(args, "assetPath");
            var referencePath = GetStringArg(args, "referencePath");
            var referenceName = GetStringArg(args, "referenceName");
            var value = GetStringArg(args, "value");

            try
            {
                if (!string.IsNullOrEmpty(assetPath))
                {
                    var asset = AssetDatabase.LoadAssetAtPath(assetPath, targetType);
                    if (asset == null)
                        AddSemanticError(validation, propertyName, $"Asset not found or type mismatch: '{assetPath}' (expected {targetType.Name})");
                    return;
                }

                if (!string.IsNullOrEmpty(referencePath) || !string.IsNullOrEmpty(referenceName))
                {
                    var resolved = ResolveSceneReference(targetType, referencePath, referenceName);
                    if (resolved == null)
                        AddSemanticError(validation, propertyName, $"Could not resolve reference for {propertyName}. Target: path='{referencePath}', name='{referenceName}'");
                    return;
                }

                ComponentSkills.ConvertValue(value, targetType);
            }
            catch (Exception ex)
            {
                AddSemanticError(validation, propertyName, ex.Message);
            }
        }

        private static (GameObject go, object error) ResolveGameObject(
            JObject args,
            string nameKey = "name",
            string instanceIdKey = "instanceId",
            string pathKey = "path",
            string entityIdKey = "entityId")
        {
            var (_, name, instanceId, path, entityId) = ReadObjectLocator(args, nameKey, instanceIdKey, pathKey, entityIdKey);
            return GameObjectFinder.FindOrError(name, instanceId, path, entityId: entityId);
        }

        private static (bool hasLocator, string name, int instanceId, string path, string entityId) ReadObjectLocator(
            JObject args,
            string nameKey,
            string instanceIdKey,
            string pathKey,
            string entityIdKey = "entityId")
        {
            var name = GetStringArg(args, nameKey);
            var path = GetStringArg(args, pathKey);
            int instanceId = GetIntArg(args, instanceIdKey);
            var entityId = GetStringArg(args, entityIdKey);
            bool hasLocator = !string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(path) || instanceId != 0 || !string.IsNullOrEmpty(entityId);
            return (hasLocator, name, instanceId, path, entityId);
        }

        private static string ResolveShaderName(string shaderName, SkillRouter.ParameterValidationResult validation)
        {
            if (string.IsNullOrWhiteSpace(shaderName))
                shaderName = ProjectSkills.GetDefaultShaderName();

            var shader = Shader.Find(shaderName);
            if (shader != null)
                return shaderName;

            var pipeline = ProjectSkills.DetectRenderPipeline();
            var fallbackShaders = pipeline switch
            {
                ProjectSkills.RenderPipelineType.URP => new[] { "Universal Render Pipeline/Lit", "Universal Render Pipeline/Simple Lit", "Standard" },
                ProjectSkills.RenderPipelineType.HDRP => new[] { "HDRP/Lit", "Standard" },
                _ => new[] { "Standard", "Mobile/Diffuse", "Unlit/Color" }
            };

            foreach (var fallback in fallbackShaders)
            {
                shader = Shader.Find(fallback);
                if (shader != null)
                {
                    AddWarning(validation, $"Shader '{shaderName}' not found. Planner fell back to '{fallback}'.");
                    return fallback;
                }
            }

            AddSemanticError(validation, "shaderName", $"Shader not found: {shaderName}");
            return shaderName;
        }

        private static string ResolveMaterialSavePath(string savePath, string materialName)
        {
            if (string.IsNullOrEmpty(savePath))
                return null;

            if (!savePath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                savePath = "Assets/" + savePath;

            if (Directory.Exists(savePath) || !Path.HasExtension(savePath))
            {
                string fileName = string.IsNullOrEmpty(materialName) ? "NewMaterial" : materialName;
                savePath = Path.Combine(savePath, fileName + ".mat").Replace("\\", "/");
            }
            else if (!savePath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
            {
                savePath = savePath + ".mat";
            }

            return savePath;
        }

        private static bool TryParseBatchItems(SkillRouter.ParameterValidationResult validation, out JArray items)
        {
            items = null;
            if (validation?.Args == null)
                return false;

            var itemsJson = GetStringArg(validation.Args, "items");
            if (Validate.RequiredJsonArray(itemsJson, "items") is object requiredErr)
            {
                AddSemanticError(validation, "items", ExtractError(requiredErr));
                return false;
            }

            try
            {
                items = JArray.Parse(itemsJson);
                if (items.Count == 0)
                {
                    AddSemanticError(validation, "items", "items must be a non-empty array");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                AddSemanticError(validation, "items", $"Failed to parse items JSON: {ex.Message}");
                return false;
            }
        }

        private static bool AllowsMultiple(Type type)
        {
            try
            {
                return type.GetCustomAttributes(typeof(DisallowMultipleComponent), true).Length == 0;
            }
            catch
            {
                return true;
            }
        }

        private static string[] GetRequiredByComponents(GameObject go, Type targetType)
        {
            try
            {
                return go.GetComponents<Component>()
                    .Where(c => c != null && c.GetType() != targetType)
                    .Where(c => c.GetType().GetCustomAttributes(typeof(RequireComponent), true)
                        .OfType<RequireComponent>()
                        .Any(r => r.m_Type0 == targetType || r.m_Type1 == targetType || r.m_Type2 == targetType))
                    .Select(c => c.GetType().Name)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static (PropertyInfo prop, FieldInfo field) FindMember(Type type, string memberName)
        {
            if (type == null || string.IsNullOrEmpty(memberName))
                return (null, null);

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
            var prop = type.GetProperty(memberName, flags);
            var field = type.GetField(memberName, flags);
            return (prop, field);
        }

        private static object ResolveSceneReference(Type targetType, string referencePath, string referenceName)
        {
            var (go, error) = GameObjectFinder.FindOrError(referenceName, 0, referencePath);
            if (error != null || go == null)
                return null;

            if (targetType == typeof(GameObject))
                return go;
            if (targetType == typeof(Transform))
                return go.transform;
            if (typeof(Component).IsAssignableFrom(targetType))
                return go.GetComponent(targetType);
            return null;
        }

        private static void SetPlanDetails(
            IDictionary<string, object> plan,
            List<object> steps,
            List<object> create = null,
            List<object> modify = null,
            List<object> delete = null,
            IDictionary<string, object> extra = null)
        {
            if (plan == null)
                return;

            plan["steps"] = steps?.ToArray() ?? Array.Empty<object>();
            plan["changes"] = new Dictionary<string, object>
            {
                ["create"] = (create ?? new List<object>()).ToArray(),
                ["modify"] = (modify ?? new List<object>()).ToArray(),
                ["delete"] = (delete ?? new List<object>()).ToArray()
            };

            if (extra != null)
            {
                foreach (var pair in extra)
                    plan[pair.Key] = pair.Value;
            }
        }

        private static void MarkSemantic(IDictionary<string, object> plan)
        {
            if (plan != null)
                plan["planLevel"] = "semantic";
        }

        private static string GetStringArg(JObject args, params string[] keys)
        {
            if (args == null)
                return null;

            foreach (var key in keys)
            {
                if (args.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) && token.Type != JTokenType.Null)
                    return token.ToString();
            }
            return null;
        }

        private static int GetIntArg(JObject args, string key)
        {
            if (args != null && args.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) && token.Type != JTokenType.Null)
            {
                try { return token.ToObject<int>(); } catch { }
            }
            return 0;
        }

        private static float GetFloatArg(JObject args, string key)
        {
            if (args != null && args.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) && token.Type != JTokenType.Null)
            {
                try { return token.ToObject<float>(); } catch { }
            }
            return 0f;
        }

        private static void AddErrorFromValidation(SkillRouter.ParameterValidationResult validation, object result, string field)
        {
            if (result == null)
                return;
            AddSemanticError(validation, field, ExtractError(result));
        }

        private static void AddSemanticError(SkillRouter.ParameterValidationResult validation, string field, string message)
        {
            if (validation == null || string.IsNullOrWhiteSpace(message))
                return;

            bool exists = validation.SemanticErrors.Any(entry =>
                SkillResultHelper.TryGetMemberValue(entry, "field", out var existingField) &&
                SkillResultHelper.TryGetMemberValue(entry, "error", out var existingError) &&
                string.Equals(existingField?.ToString(), field, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existingError?.ToString(), message, StringComparison.Ordinal));

            if (exists)
                return;

            validation.SemanticErrors.Add(new Dictionary<string, object>
            {
                ["field"] = field,
                ["error"] = message
            });
        }

        private static void AddWarning(SkillRouter.ParameterValidationResult validation, string message)
        {
            if (validation == null || string.IsNullOrWhiteSpace(message))
                return;

            if (validation.Warnings.Any(w => string.Equals(w, message, StringComparison.Ordinal)))
                return;

            validation.Warnings.Add(message);
        }

        private static string ExtractError(object result)
        {
            return SkillResultHelper.TryGetError(result, out var errorText) ? errorText : result?.ToString() ?? "Unknown error";
        }

        private static string ExtractSemanticMessage(object semanticError)
        {
            if (SkillResultHelper.TryGetMemberValue(semanticError, "error", out var value) && value != null)
                return value.ToString();
            return semanticError?.ToString() ?? "Unknown semantic error";
        }

        // ===================== 批处理分析辅助 =====================

        private class BatchAnalyzeContext
        {
            public readonly SkillRouter.ParameterValidationResult Validation;
            public readonly IDictionary<string, object> Plan;
            public readonly JArray Items;
            public readonly List<object> ItemPlans = new List<object>();

            public BatchAnalyzeContext(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan, JArray items)
            {
                Validation = validation;
                Plan = plan;
                Items = items;
            }

            public JObject GetItem(int i) => Items[i] as JObject ?? new JObject();

            public void ReportItemErrors(int index, List<string> errors)
            {
                if (errors.Count > 0)
                    AddSemanticError(Validation, $"items[{index}]", string.Join("; ", errors));
            }

            public void ReportDelegatedErrors(int index, SkillRouter.ParameterValidationResult itemValidation)
            {
                foreach (var se in itemValidation.SemanticErrors)
                    AddSemanticError(Validation, $"items[{index}]", ExtractSemanticMessage(se));
            }

            public void AddItemPlan(int index, string target, bool valid, string[] errors,
                IDictionary<string, object> extra = null)
            {
                var entry = new Dictionary<string, object>
                {
                    ["index"] = index,
                    ["target"] = target,
                    ["valid"] = valid,
                    ["errors"] = errors
                };
                if (extra != null)
                    foreach (var kv in extra) entry[kv.Key] = kv.Value;
                ItemPlans.Add(entry);
            }

            public void EmitPlan(string actionLabel,
                List<object> create = null, List<object> modify = null, List<object> delete = null)
            {
                if (Plan == null) return;
                MarkSemantic(Plan);
                SetPlanDetails(Plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = actionLabel,
                            ["target"] = $"{Items.Count} items"
                        }
                    },
                    create: create, modify: modify, delete: delete,
                    extra: new Dictionary<string, object>
                    {
                        ["batchPreview"] = new Dictionary<string, object>
                        {
                            ["totalItems"] = Items.Count,
                            ["items"] = ItemPlans.ToArray()
                        }
                    });
            }
        }

        private static BatchAnalyzeContext TryBeginBatchAnalyze(
            SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            if (!TryParseBatchItems(validation, out var items))
                return null;
            return new BatchAnalyzeContext(validation, plan, items);
        }
    }
}

// Producer:Betsy
