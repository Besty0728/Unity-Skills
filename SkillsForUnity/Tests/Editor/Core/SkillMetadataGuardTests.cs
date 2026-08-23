using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// 全注册表范围的元数据守卫，钉在"零违规"。
    ///
    /// <para>这里的声明式元数据不是文档，而是运行时真正据以行动的东西。<c>ReadOnly</c> 决定档位是否
    /// 撤下某个技能，所以一个被错标成 <c>ReadOnly=true</c> 的写操作，在专门为撤掉它而设的档位下
    /// 依然可调。<c>MutatesScene</c>/<c>MutatesAssets</c> 决定档位撤掉什么，<c>TracksWorkflow</c> 决定
    /// 一次调用能否撤销，<c>RiskLevel</c> 则是 agent 在决定是否找用户确认之前会读的东西。
    /// 这些每一项都是一道闸门，而错误的声明能笔直穿过去。</para>
    ///
    /// <para>违规数目前是零。把它钉在零正是本文件的意义：这类自相矛盾没人会故意引入，
    /// 所以抓住它的有用时机是引入它的那次提交，而不是某次发布后才发现某个档位其实什么都没藏住。</para>
    ///
    /// <para>这里不硬编码任何技能数量。注册表规模随已安装的可选包变动，因此一律运行期推导；
    /// 断言的对象是"违规集合为空"。</para>
    /// </summary>
    [TestFixture]
    public class SkillMetadataGuardTests
    {
        private SurfaceProfileKind _savedProfile;

        [SetUp]
        public void SetUp()
        {
            // ValidateMetadata 审计整个注册表，而下面那些独立推导的检查所用的快照辅助函数是遵守档位的。
            // 钉成 full 让两边看到同一个技能集合；用完还原，因为这个 pref 是全局的。
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
        }

        [TearDown]
        public void TearDown()
        {
            SkillsSurfaceProfile.Current = _savedProfile;
        }

        /// <summary>
        /// 审计自身的 ERROR 档，钉在零。
        ///
        /// <para>只管 ERROR。WARN 档属于理想追求——"Tags 为空"、"没写 Operation"——把它也钉在零，
        /// 会让每加一个技能都变成负担，最后整条断言被删掉。ERROR 只留给自相矛盾的声明，
        /// 那是另一类东西：不存在任何合理的代码库状态能让它成立。</para>
        /// </summary>
        [Test]
        public void ValidateMetadata_ReportsNoErrors()
        {
            var errors = SkillRouter.ValidateMetadata()
                .Where(issue => issue.StartsWith("[ERROR]", StringComparison.Ordinal))
                .OrderBy(issue => issue, StringComparer.Ordinal)
                .ToArray();

            Assert.That(errors, Is.Empty,
                $"{errors.Length} metadata contradiction(s). Each one defeats a runtime gate rather " +
                "than merely reading oddly — see SkillRouter.ValidateMetadata for what each implies:\n" +
                string.Join("\n", errors));
        }

        /// <summary>
        /// <c>X_batch</c> 声明的影响面必须不低于 <c>X</c>。
        ///
        /// <para>这里故意重新推导一遍，而不是从 <c>ValidateMetadata</c> 里读结论。若那条规则被从审计中
        /// 删掉，两边都会通过——预期与实际会一起归零，断言依旧是绿的。这份副本的代价是规则变更时要
        /// 同步一次，而那次报警正是我们想要的。</para>
        ///
        /// <para>一个声明不足的批量技能，就是那种能穿过它单体孪生版被拦住的每一道闸门、
        /// 而且一次动 N 个对象而非一个的变体。</para>
        /// </summary>
        [Test]
        public void EveryBatchSkill_DeclaresAtLeastTheImpactOfItsSingularTwin()
        {
            const string suffix = "_batch";
            var registry = SkillRouter.GetAllSkillsSnapshotUnfiltered();
            var byName = registry.ToDictionary(s => s.Name, s => s, StringComparer.Ordinal);

            var violations = new List<string>();
            int pairsChecked = 0;

            foreach (var batch in registry
                         .Where(s => s.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                var singularName = batch.Name.Substring(0, batch.Name.Length - suffix.Length);
                // 只认严格的 X / X_batch 配对。孪生名拼法不同的批量技能
                // （material_set_colors_batch 对 material_set_color）选择跳过，而不是靠猜。
                if (!byName.TryGetValue(singularName, out var single))
                    continue;

                pairsChecked++;

                if (single.MutatesScene && !batch.MutatesScene)
                    violations.Add($"{batch.Name}: MutatesScene=false but {singularName} declares true");
                if (single.MutatesAssets && !batch.MutatesAssets)
                    violations.Add($"{batch.Name}: MutatesAssets=false but {singularName} declares true");
                if (single.TracksWorkflow && !batch.TracksWorkflow)
                    violations.Add($"{batch.Name}: TracksWorkflow=false but {singularName} declares true");
                if (RiskRank(batch.RiskLevel) < RiskRank(single.RiskLevel))
                    violations.Add($"{batch.Name}: RiskLevel='{batch.RiskLevel}' below {singularName}'s '{single.RiskLevel}'");
                if (single.ReadOnly != batch.ReadOnly)
                    violations.Add($"{batch.Name}: ReadOnly={batch.ReadOnly} but {singularName} declares {single.ReadOnly}");
            }

            Assert.That(pairsChecked, Is.GreaterThan(0),
                "No X / X_batch pairs found — the mirror check would be vacuous. Did the naming convention change?");
            Assert.That(violations, Is.Empty,
                $"{violations.Count} batch skill(s) declare less impact than the singular skill they " +
                "repeat N times over:\n" + string.Join("\n", violations));
        }

        /// <summary>low &lt; medium &lt; high；其它一律排最低，与特性默认值一致。</summary>
        private static int RiskRank(string riskLevel)
        {
            if (string.Equals(riskLevel, "high", StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(riskLevel, "medium", StringComparison.OrdinalIgnoreCase)) return 1;
            return 0;
        }

        /// <summary>
        /// <c>ReadOnly</c> 写错后最承重的那一条后果，独立于审计再陈述一遍。档位从不隐藏只读技能，
        /// 所以一个挂着 <c>ReadOnly=true</c> 的写操作，恰恰就是那个能在"为撤掉它而设"的档位下存活的
        /// 技能——而且是无声地存活，因为从外面看档位依然像在过滤。
        /// </summary>
        [Test]
        public void NoReadOnlySkill_AlsoDeclaresItMutatesSomething()
        {
            var contradictory = SkillRouter.GetAllSkillsSnapshotUnfiltered()
                .Where(s => s.ReadOnly && (s.MutatesScene || s.MutatesAssets))
                .Select(s => $"{s.Name} (MutatesScene={s.MutatesScene}, MutatesAssets={s.MutatesAssets})")
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.That(contradictory, Is.Empty,
                "These skills claim to be read-only while declaring that they mutate. The surface " +
                "profile never hides a read-only skill, so each of these stays callable under a " +
                $"profile that exists to withdraw it:\n{string.Join("\n", contradictory)}");
        }

        [Test]
        public void NoReadOnlySkill_DeclaresAWriteOperation()
        {
            const SkillOperation writeOps = SkillOperation.Create | SkillOperation.Modify | SkillOperation.Delete;

            var contradictory = SkillRouter.GetAllSkillsSnapshotUnfiltered()
                .Where(s => s.ReadOnly && (s.Operation & writeOps) != 0)
                .Select(s => $"{s.Name} (Operation={s.Operation})")
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.That(contradictory, Is.Empty,
                "Read-only skills declaring a Create/Modify/Delete operation:\n" +
                string.Join("\n", contradictory));
        }

        [Test]
        public void NoReadOnlySkill_AlsoTracksWorkflow()
        {
            // TracksWorkflow 的含义是"这次调用会被快照下来以便撤销"。只读技能没有可撤销的东西，
            // 所以两者同时成立就说明其中一个声明是错的——而如果错的是 ReadOnly，
            // 这个技能还同时在逃避档位过滤。
            var contradictory = SkillRouter.GetAllSkillsSnapshotUnfiltered()
                .Where(s => s.ReadOnly && s.TracksWorkflow)
                .Select(s => s.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.That(contradictory, Is.Empty,
                "Read-only skills that also track workflow (nothing to roll back): " +
                string.Join(", ", contradictory));
        }

        /// <summary>
        /// 两个曾被声明成 <c>ReadOnly=true</c> 却在往磁盘写东西的技能。
        /// <c>scene_dependency_analyze</c> 会写一份 markdown 报告（它自己的 <c>savedTo</c> 输出就点了文件名），
        /// <c>scriptableobject_export_json</c> 会写一个 JSON 文件。因此这两个在任何档位下都藏不住。
        ///
        /// <para>这里逐个点名而不交给上面那些全注册表扫描，因为这两个都没声明 MutatesAssets、
        /// 也没声明写类 Operation——任何推导出来的检查都抓不到它们被改回去。这条主张就是针对
        /// 这两个具体技能的。</para>
        /// </summary>
        [TestCase("scene_dependency_analyze")]
        [TestCase("scriptableobject_export_json")]
        public void FileWritingAnalysisSkills_AreNotDeclaredReadOnly(string skill)
        {
            Assume.That(SkillRouter.TryGetSkill(skill, out var info), Is.True, $"{skill} is not registered.");

            Assert.That(info.ReadOnly, Is.False,
                $"{skill} writes a file to the project, so ReadOnly=true makes it unhideable by " +
                "every surface profile — the one property no profile can withdraw.");
        }

        /// <summary>
        /// <c>gameobject_get_info</c> 是 agent 用来一次性了解某个对象全部信息的技能，而 <c>Outputs</c>
        /// 正是告诉它"哪些后续调用不必发"的东西。Outputs 声明不足，就会为响应本已携带的值
        /// 每缺一个键多付一次往返。
        ///
        /// <para>数量与名字一起断言，好让"替换"过不了关：把一个键换成另一个，总数仍是 15。</para>
        /// </summary>
        [Test]
        public void GameObjectGetInfo_DeclaresAllFifteenOutputs()
        {
            Assume.That(SkillRouter.TryGetSkill("gameobject_get_info", out var info), Is.True);

            var expected = new[]
            {
                "name", "entityId", "instanceId", "path", "tag", "layer", "isActive",
                "position", "rotation", "scale", "parent", "parentPath", "childCount",
                "children", "components",
            };

            Assert.That(info.Outputs, Is.EquivalentTo(expected),
                "Outputs drifted from the response this skill actually returns:\n" +
                $"declared: {string.Join(", ", info.Outputs ?? Array.Empty<string>())}");
            Assert.That(info.Outputs.Length, Is.EqualTo(15));
            Assert.That(info.Outputs.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(info.Outputs.Length),
                "Duplicate output keys.");
        }

        /// <summary>
        /// 声明的 outputs 必须处处唯一。重复项在运行期无害、在评审时也看不见，所以才能活下来——
        /// 但它会撑大每一份带这条记录的 manifest，并让"数量"作为完整性信号失去意义。
        /// </summary>
        [Test]
        public void NoSkill_DeclaresDuplicateOutputsOrTags()
        {
            var offenders = new List<string>();

            foreach (var skill in SkillRouter.GetAllSkillsSnapshotUnfiltered()
                         .OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                if (skill.Outputs != null &&
                    skill.Outputs.Distinct(StringComparer.Ordinal).Count() != skill.Outputs.Length)
                {
                    offenders.Add($"{skill.Name}: duplicate Outputs [{string.Join(", ", skill.Outputs)}]");
                }

                if (skill.Tags != null &&
                    skill.Tags.Distinct(StringComparer.OrdinalIgnoreCase).Count() != skill.Tags.Length)
                {
                    offenders.Add($"{skill.Name}: duplicate Tags [{string.Join(", ", skill.Tags)}]");
                }
            }

            Assert.That(offenders, Is.Empty, string.Join("\n", offenders));
        }

        /// <summary>
        /// 别名技能必须镜像其目标的影响面声明。别名是同一份代码经第二个名字被调用，所以声明一旦分岔，
        /// 就意味着两个名字里有一个受了错误闸门的管辖——而 agent 无从知道是哪一个。
        /// </summary>
        [TestCase("light_get_properties", "light_get_info")]
        public void AliasSkill_MirrorsItsTargetsImpactDeclarations(string alias, string target)
        {
            Assume.That(SkillRouter.TryGetSkill(alias, out var aliasInfo), Is.True, $"{alias} is not registered.");
            Assume.That(SkillRouter.TryGetSkill(target, out var targetInfo), Is.True, $"{target} is not registered.");

            Assert.That(aliasInfo.ReadOnly, Is.EqualTo(targetInfo.ReadOnly));
            Assert.That(aliasInfo.MutatesScene, Is.EqualTo(targetInfo.MutatesScene));
            Assert.That(aliasInfo.MutatesAssets, Is.EqualTo(targetInfo.MutatesAssets));
            Assert.That(aliasInfo.TracksWorkflow, Is.EqualTo(targetInfo.TracksWorkflow));
            Assert.That(aliasInfo.RiskLevel, Is.EqualTo(targetInfo.RiskLevel));
            Assert.That(aliasInfo.Category, Is.EqualTo(targetInfo.Category));
            Assert.That(aliasInfo.Outputs, Is.EqualTo(targetInfo.Outputs));
        }

        /// <summary>
        /// <c>SkillPlanningService._requiredInputGroups</c> 里的每个候选名，都必须是"至少有一个声明了
        /// 该 token 的技能真的接受"的参数名。
        ///
        /// <para>分组校验会把候选名与技能自身的参数集合求交，所以一个没有任何技能接受的名字会被静默丢弃
        /// ——它永不失败、永不触发，读起来却像有覆盖，实则没有。曾经就发布过两个这样的名字：
        /// "materialPath"（声明 material token 的 16 个技能里 0 个接受它，它们收的是双用途的 <c>path</c>），
        /// 以及 assetPath token 下的 "path"。这条测试同时也是 "componentName" 在同一轮清理中得以保留的
        /// 理由：恰好有一个技能（smart_reference_bind）接受它，删掉就会静默丢掉那个技能的目标校验。</para>
        /// </summary>
        [Test]
        public void RequiredInputGroups_NameOnlyRealParameters()
        {
            var groups = RequiredInputGroups();
            Assume.That(groups, Is.Not.Null.And.Not.Empty,
                "SkillPlanningService._requiredInputGroups was renamed or emptied.");

            var registry = SkillRouter.GetAllSkillsSnapshotUnfiltered().ToArray();
            var offenders = new List<string>();
            foreach (var group in groups)
            {
                var declaring = registry
                    .Where(s => s.RequiresInput != null &&
                                s.RequiresInput.Any(t => string.Equals(t, group.Key, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                if (declaring.Length == 0)
                    continue;

                foreach (var candidate in group.Value)
                {
                    if (!declaring.Any(s => SkillAcceptsParameter(s, candidate)))
                    {
                        offenders.Add($"token '{group.Key}' offers '{candidate}', accepted by none of its " +
                                      $"{declaring.Length} declaring skill(s)");
                    }
                }
            }

            Assert.That(offenders, Is.Empty, string.Join("\n", offenders));
        }

        /// <summary>
        /// "A 或 B" 形式的 RequiresInput token，其后一半必须是调用方真能发出的键。
        /// <c>gameObject</c> 豁免，因为它是词表里唯一纯语义的 token（代表 name/path/instanceId/entityId）；
        /// 唯一定位载体是 <c>items</c> 的技能也豁免，因为所有 <c>*_batch</c> 的定位参数都装在数组里面。
        ///
        /// <para>它抓的是这种情况：<c>material_set_color</c> 对外宣称 "gameObject|materialPath"，
        /// 却把 <c>materialPath</c> 当未知参数拒掉——而这个名字在同模块的 <c>material_assign</c> 上确实存在，
        /// 于是 agent 把它推广过来，反而因为正确阅读了元数据而吃了一个拒绝。</para>
        /// </summary>
        [Test]
        public void CompoundRequiredInputTokens_NameAKeyTheSkillAccepts()
        {
            var offenders = new List<string>();

            foreach (var skill in SkillRouter.GetAllSkillsSnapshotUnfiltered()
                         .Where(s => s.RequiresInput != null)
                         .OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                if (SkillAcceptsParameter(skill, "items"))
                    continue;

                foreach (var token in skill.RequiresInput)
                {
                    if (token == null || token.IndexOf('|') < 0)
                        continue;

                    foreach (var part in token.Split('|'))
                    {
                        if (string.Equals(part, "gameObject", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!SkillAcceptsParameter(skill, part))
                            offenders.Add($"{skill.Name}: token '{token}' names '{part}', which it does not accept");
                    }
                }
            }

            Assert.That(offenders, Is.Empty, string.Join("\n", offenders));
        }

        /// <summary>
        /// 2026-08-23 真机冒烟扫描抓到的十二个技能：它们会照空请求体执行下去，然后在自己实现内部失败。
        /// 每一个都需要参数却没声明 <c>RequiresInput</c>，而它们的参数既不是值类型也没有 CLR 默认值，
        /// 于是 <c>IsParameterRequired</c> 把它们全判成可选。schema 说"什么都不必填"，dryRun 说
        /// <c>valid:true</c>，失败只在执行之后才出现。
        ///
        /// <para>两条断言，因为任一条单独都可能被一个错误的修法满足。token 检查抓的是 B3 陷阱——
        /// 一个点名了技能并不接受的键的 token 什么也强制不了，读起来却像有覆盖（裸参数名必须被接受；
        /// "A|B" 或语义 token 必须经 <c>_requiredInputGroups</c> 与技能参数求交非空）。dryRun 检查抓的是
        /// 相反的错误：元数据看着对但实际不可达，空请求体照样合法。</para>
        ///
        /// <para>注册与否是断言出来的，不是假设的。这十二个都位于"装不装可选包都能编译"的模块里
        /// （包检测在方法体内部），所以这里少了某个名字意味着技能被改名或删了，而不是缺包。</para>
        /// </summary>
        [TestCase("batch_replace_material")]
        [TestCase("batch_set_render_layer")]
        [TestCase("behavior_blackboard_list")]
        [TestCase("decal_get_info")]
        [TestCase("find_objects_by_name")]
        [TestCase("netcode_get_network_object_info")]
        [TestCase("netcode_list_network_prefabs")]
        [TestCase("script_find_in_file")]
        [TestCase("shader_find")]
        [TestCase("smart_scene_query")]
        [TestCase("yooasset_get_build_settings")]
        [TestCase("yooasset_runtime_get_validation_result")]
        public void SkillsNeedingAnArgument_DeclareItAndRefuseAnEmptyBodyBeforeExecuting(string skillName)
        {
            Assert.That(SkillRouter.TryGetSkill(skillName, out var skill), Is.True,
                $"{skillName} is not registered.");

            Assert.That(skill.RequiresInput, Is.Not.Null.And.Not.Empty,
                $"{skillName} cannot do anything without an argument, so it must declare RequiresInput — " +
                "without it the schema advertises every parameter as optional and an empty body " +
                "executes into a failure the caller was told would not happen.");

            var groups = RequiredInputGroups();
            foreach (var token in skill.RequiresInput)
            {
                bool namesAnAcceptedKey = token.Split('|').Any(part => SkillAcceptsParameter(skill, part));
                bool resolvesThroughAGroup = groups.TryGetValue(token, out var candidates) &&
                                             candidates.Any(candidate => SkillAcceptsParameter(skill, candidate));

                Assert.That(namesAnAcceptedKey || resolvesThroughAGroup, Is.True,
                    $"{skillName}: RequiresInput token '{token}' neither names a parameter it accepts nor " +
                    "maps to a group whose candidates it accepts, so it enforces nothing and an agent " +
                    "reading it literally gets UNKNOWN_PARAM.");
            }

            var dry = JObject.Parse(SkillRouter.DryRun(skillName, "{}"));
            Assert.That(dry["valid"]?.Value<bool>(), Is.False,
                $"An empty body still dry-runs as valid for {skillName}: {dry["validation"]?.ToString(Formatting.None)}");
        }

        private static bool SkillAcceptsParameter(SkillRouter.SkillInfo skill, string parameterName)
        {
            if (skill.AllowedParameterSet != null)
                return skill.AllowedParameterSet.Contains(parameterName);

            return skill.ParameterNames != null &&
                   skill.ParameterNames.Any(name => string.Equals(name, parameterName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 那份私有的分组映射表，用反射读取：它本身就是被测对象，在这里复述一遍等于在测副本。
        /// </summary>
        private static Dictionary<string, string[]> RequiredInputGroups()
        {
            var field = typeof(SkillPlanningService).GetField(
                "_requiredInputGroups", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null, "SkillPlanningService._requiredInputGroups was renamed.");
            return field.GetValue(null) as Dictionary<string, string[]>;
        }
    }
}

// Producer:Betsy
