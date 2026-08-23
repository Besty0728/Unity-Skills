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
    /// 发现类 / 预览类接口上那些"答案是错的、而调用方看不出来"的场景。四类故障，全都是静默的：
    ///
    /// <para><b>静默空操作。</b>被解析器丢掉的查询键（<c>?full</c> 不带值地写）、或者被守卫放过却
    /// 一个都匹配不上的值。调用方读到一个格式良好的 200，就此对工程下了一个只对它自己的拼写错误
    /// 成立的结论。</para>
    ///
    /// <para><b>静默覆盖。</b>两个请求键被同一个标志位解析，于是输的那个消失了：请求体
    /// <c>{"dryRun":true}</c> 与 <c>?mode=transactional</c> 同时出现时，那批调用方本想预览的操作被真的
    /// 执行了，而响应里没有任何地方说明是哪个模式胜出。</para>
    ///
    /// <para><b>静默泄漏。</b>载荷是拿原始注册表而不是可见技能集合构建的，于是用户经档位撤下的名字
    /// 照样发了出去——在 <c>/skills/meta</c> 里、在 v1 信封里、以及在拼写纠正候选里。</para>
    ///
    /// <para><b>静默乐观。</b>对那些"是否拒绝由载荷决定、而预览从未拿到该载荷"的入口
    /// （batch_execute 与 workflow 撤销/重做一族），预览仅凭元数据就回了 <c>allowed:true</c>。</para>
    ///
    /// 不硬编码任何技能数量：注册表随已安装的可选包变动。探针与预期都从实时注册表推导。
    /// </summary>
    [TestFixture]
    public class ReviewFixRouterTests
    {
        private SurfaceProfileKind _savedProfile;
        private SkillsOperatingMode _savedMode;

        /// <summary>
        /// UnitySkills_SurfaceProfile 是 EditorPrefs 键，也就是"按 Unity 版本全机器共享"而非按工程独立：
        /// 在这里留下一个档位，会静默改变本轮其它所有夹具（以及开发者下一次编辑器会话）的可见范围。
        /// 故此处备份并还原。
        /// </summary>
        [SetUp]
        public void SetUp()
        {
            _savedProfile = SkillsSurfaceProfile.Current;
            _savedMode = SkillsModeManager.CurrentMode;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
        }

        [TearDown]
        public void TearDown()
        {
            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
        }

        // ---------- ?operation=：守卫不得比过滤器更窄 ----------

        /// <summary>
        /// SkillOperation 是 [Flags] 枚举，过滤器用 <c>Enum.TryParse(value, ignoreCase: true)</c> 解析它，
        /// 因此接受逗号列表。而挡在它前面的守卫却是拿 Enum.GetNames 比对，于是
        /// <c>?operation=Query,Modify</c>——一个过滤器本会认的值——回了 400。
        /// 一道会拒绝合法输入的守卫比没有守卫更糟：它把一个本来能用的查询变成了永久错误。
        /// </summary>
        [Test]
        public void OperationFilter_AcceptsAFlagsCommaList()
        {
            var probe = SkillRouter.GetAllSkillsSnapshot()
                .Where(s => OperationFlagsOf(s).Length >= 2)
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .FirstOrDefault();
            Assume.That(probe, Is.Not.Null, "No skill declares two operation flags, so the comma list has nothing to match.");

            var flags = OperationFlagsOf(probe);
            string list = $"{flags[0]},{flags[1]}";

            var response = JObject.Parse(SkillRouter.GetFilteredManifest($"?operation={list}"));

            Assert.That(response["errorCode"], Is.Null,
                $"?operation={list} was rejected, but the filter parses exactly this: {response.ToString(Formatting.None)}");
            var names = ((JArray)response["skills"]).Select(s => s["name"].ToString()).ToArray();
            Assert.That(names, Does.Contain(probe.Name),
                $"{probe.Name} declares {list}; a comma list means 'declares all of these', so it must be in the result.");
        }

        [Test]
        public void OperationFilter_AcceptsANumericLiteral()
        {
            // Enum.TryParse 同样接受底层数值，所以过滤器认 "?operation=4"。守卫必须与之一致——
            // 参见 OperationFilter_AcceptsAFlagsCommaList。
            var probe = SkillRouter.GetAllSkillsSnapshot()
                .Where(s => OperationFlagsOf(s).Length == 1)
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .FirstOrDefault();
            Assume.That(probe, Is.Not.Null, "No skill declares exactly one operation flag.");

            int numeric = (int)probe.Operation;
            var response = JObject.Parse(SkillRouter.GetFilteredManifest($"?operation={numeric}"));

            Assert.That(response["errorCode"], Is.Null,
                $"?operation={numeric} was rejected: {response.ToString(Formatting.None)}");
            Assert.That(((JArray)response["skills"]).Select(s => s["name"].ToString()), Does.Contain(probe.Name));
        }

        [Test]
        public void OperationFilter_StillRejectsAValueTheFilterCannotUse()
        {
            // 上面的放宽不得等于把守卫关掉：真正的拼写错误仍必须是带词表的 400，
            // 而不是一个 skills 数组为空的 200。
            var response = JObject.Parse(SkillRouter.GetFilteredManifest("?operation=Modifyy,Query"));

            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"),
                response.ToString(Formatting.None));
            Assert.That(response["details"]?["validOperations"] as JArray, Is.Not.Null.And.Not.Empty);
        }

        [TestCase("/skills")]
        [TestCase("/skills/schema")]
        public void OperationCommaList_IsAcceptedOnBothManifestPaths(string path)
        {
            // 两个端点走同一道守卫，HTTP 线程上的快路径问的也是同一个问题——所以在一个端点合法的值，
            // 在另一个端点、以及在两条路径上都必须合法。
            var probe = SkillRouter.GetAllSkillsSnapshot()
                .Where(s => OperationFlagsOf(s).Length >= 2)
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .FirstOrDefault();
            Assume.That(probe, Is.Not.Null, "No skill declares two operation flags.");

            var flags = OperationFlagsOf(probe);
            string query = $"?operation={flags[0]},{flags[1]}";

            var (statusCode, body) = ProcessRequest("GET", path, query, null);

            Assert.That(statusCode, Is.EqualTo(200), $"GET {path}{query} → {body}");
            Assert.That(JObject.Parse(body)["errorCode"], Is.Null);
        }

        // ---------- 裸标志位与空值 ----------

        /// <summary>
        /// 不带 <c>=1</c> 的 <c>?full</c> 是"标志位已置起"在 URL 里的惯用写法，而它恰恰是唯一一个
        /// 专门用来推翻"默认给简表"行为的标志。被解析器丢掉之后，它回的是约 19KB 的目录，
        /// 而调用方还在等完整 manifest——又因为目录本身是完全合法的载荷，看起来一点问题都没有。
        /// </summary>
        [Test]
        public void BareFullFlag_ServesTheSameFullManifestAsFullEqualsOne()
        {
            string bare = SkillRouter.GetFilteredManifest("?full");

            Assert.That(JObject.Parse(bare)["manifestType"]?.ToString(), Is.EqualTo("manifest"),
                "?full must reach the full manifest, not the brief directory.");
            Assert.That(bare, Is.EqualTo(SkillRouter.GetFilteredManifest("?full=1")),
                "?full and ?full=1 are the same request and must answer with the same bytes.");
            Assert.That(bare, Is.EqualTo(SkillRouter.GetManifest()));

            Assert.That(SkillRouter.GetEtagForCachedGet("/skills", "?full", bare),
                Is.EqualTo(SkillRouter.GetEtagForCachedGet("/skills", "?full=1", bare)),
                "Two spellings of one request must share a cache entry, or they get different ETags " +
                "and a client alternating between them never sees a 304.");
        }

        [Test]
        public void BareBriefFlag_ServesTheDirectory()
        {
            Assert.That(SkillRouter.GetFilteredManifest("?brief"), Is.EqualTo(SkillRouter.GetBrief()));
        }

        [Test]
        public void BareSummaryFlag_ServesTheLiteManifest()
        {
            var bare = SkillRouter.GetFilteredManifest("?summary");

            Assert.That(JObject.Parse(bare)["summary"]?.Value<bool>(), Is.True,
                "?summary must select the lite manifest, not fall through as an unset flag.");
            Assert.That(bare, Is.EqualTo(SkillRouter.GetFilteredManifest("?summary=1")));
        }

        /// <summary>
        /// 一个完全没写值的键。把它丢掉就等于"不过滤"，于是一个只打了一半的范围限定回了整份目录，
        /// 看起来还像是被采纳了。拒绝时必须带上词表，与拼错值时完全一致。
        /// </summary>
        [TestCase("category", "validCategories")]
        [TestCase("operation", "validOperations")]
        public void BlankNarrowingFilterValue_IsRejectedWithTheLegalVocabulary(string key, string vocabularyField)
        {
            var body = SkillRouter.GetFilteredManifest($"?{key}=", out bool isError);
            var response = JObject.Parse(body);

            Assert.That(isError, Is.True, $"?{key}= must be reported to the HTTP layer as a rejection: {body}");
            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), body);
            Assert.That(response["details"]?["parameter"]?.ToString(), Is.EqualTo(key));
            Assert.That(response["details"]?[vocabularyField] as JArray, Is.Not.Null.And.Not.Empty,
                $"The rejection must hand back {vocabularyField} so the caller can fix it in one retry.");
        }

        [TestCase("tags")]
        [TestCase("q")]
        [TestCase("readonly")]
        [TestCase("summary")]
        [TestCase("brief")]
        [TestCase("wire")]
        [TestCase("full")]
        public void BlankValueOnAnyRecognizedKey_IsRejected(string key)
        {
            // 这里每一个都存在一种"默认解读"，而那正是问题所在：按默认作答会让调用方相信这个键生效了。
            // ?tags= 会一个都匹配不上，?readonly= 会悄悄当成 readonly=false，
            // ?full= 会把调用方本想摆脱的那份目录再递回来。
            var response = JObject.Parse(SkillRouter.GetFilteredManifest($"?{key}="));

            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"),
                $"?{key}= was answered instead of refused: {response.ToString(Formatting.None)}");
            Assert.That(response["details"]?["parameter"]?.ToString(), Is.EqualTo(key));
        }

        [Test]
        public void BlankValueRejection_MintsNoCacheEntry()
        {
            // 与拼错值同一套道理：被拒的查询不得换来一条 manifest 大小的缓存条目，
            // 也不得换来一个会把错误变成 304 的 ETag。
            const string query = "?category=";
            SkillRouter.GetFilteredManifest(query);

            Assert.That(SkillRouter.TryGetCachedGetResponse("/skills", query, out _, out _), Is.False,
                "The blank-value rejection left a cache entry behind.");

            var (statusCode, body) = ProcessRequest("GET", "/skills", query, null);
            Assert.That(statusCode, Is.EqualTo(400), body);
        }

        [Test]
        public void LegalQueriesAreUnaffectedByTheParserChange()
        {
            // 解析器现在多保留了两种它过去会丢掉的写法。它原本就保留的那些写法不得有任何变化：
            // 这些才是真实调用方在发的查询。
            var category = FirstPopulatedCategory();

            Assert.That(JObject.Parse(SkillRouter.GetFilteredManifest($"?category={category}"))["errorCode"], Is.Null);
            Assert.That(JObject.Parse(SkillRouter.GetFilteredManifest($"?category={category}&wire=v2"))["wire"]?.ToString(),
                Is.EqualTo("v2"));
            Assert.That(SkillRouter.GetFilteredManifest("?nonce=abc"), Is.EqualTo(SkillRouter.GetBrief()),
                "An unrecognized key with a value is still stripped, so the request still lands on brief.");
            Assert.That(SkillRouter.GetFilteredManifest(null), Is.EqualTo(SkillRouter.GetBrief()));
            Assert.That(SkillRouter.GetFilteredManifest("?"), Is.EqualTo(SkillRouter.GetBrief()));
        }

        // ---------- POST /skills/batch：mode 与 dryRun 是两个独立的键 ----------

        /// <summary>
        /// 本组测试针对的故障：URL 里带 <c>?mode=transactional</c>，同时请求体里带 <c>{"dryRun":true}</c>。
        /// 当时用同一个"查询是否已决定"的标志位管着这两个键，于是请求体里的 dryRun 被丢弃，
        /// 那批操作被真的执行了——调用方要的是预览，得到的是真实改动，而响应从未提及它选了哪个模式。
        /// </summary>
        [Test]
        public void Batch_QueryMode_DoesNotSwallowBodyDryRun()
        {
            string probe = ParameterlessReadOnlySkill();
            string body = JsonConvert.SerializeObject(new
            {
                steps = new[] { new { skill = probe, args = new { } } },
                dryRun = true,
            });

            var (statusCode, responseJson) = ProcessRequest("POST", "/skills/batch", "?mode=transactional", body);
            var response = JObject.Parse(responseJson);

            Assert.That(statusCode, Is.EqualTo(200), responseJson);
            Assert.That(response["dryRun"]?.Value<bool>(), Is.True,
                "The body asked for a preview and the URL said nothing about dryRun, so this must be a preview.");
            Assert.That(response["mode"]?.ToString(), Is.EqualTo("dryRun"),
                "The envelope must echo the mode that actually ran — it is the only way the caller " +
                "can tell a preview from an execution when the two keys came from different places.");

            var step = (JObject)((JArray)response["results"])[0];
            var payload = (step["result"] ?? step["error"]) as JObject;
            Assert.That(payload?["status"]?.ToString(), Is.EqualTo("dryRun"),
                $"The step was executed instead of previewed: {step.ToString(Formatting.None)}");

            Assert.That(response.Property("transactional"), Is.Null,
                "A preview executes nothing, so there is no transaction to report (or to roll back).");
        }

        [Test]
        public void Batch_QueryDryRun_StillWinsOverTheBodyForItsOwnKey()
        {
            // 同一个键出现在两处时以 URL 为准。没有这条，上面那个修复就会变成"请求体永远胜出"，
            // 那是同一个 bug 换了个方向。
            string probe = ParameterlessReadOnlySkill();
            string body = JsonConvert.SerializeObject(new
            {
                steps = new[] { new { skill = probe, args = new { } } },
                dryRun = false,
            });

            var (statusCode, responseJson) = ProcessRequest("POST", "/skills/batch", "?dryRun=true", body);
            var response = JObject.Parse(responseJson);

            Assert.That(statusCode, Is.EqualTo(200), responseJson);
            Assert.That(response["dryRun"]?.Value<bool>(), Is.True);
            Assert.That(response["mode"]?.ToString(), Is.EqualTo("dryRun"));
        }

        [Test]
        public void Batch_TransactionalWithoutADryRunKey_IsStillTransactional()
        {
            // 上面那个强制 transactional=false 的反向对照：它只能在真的有人要求预览时才触发。
            string probe = ParameterlessReadOnlySkill();
            string body = JsonConvert.SerializeObject(new
            {
                steps = new[] { new { skill = probe, args = new { } } },
            });

            var (statusCode, responseJson) = ProcessRequest("POST", "/skills/batch", "?mode=transactional", body);
            var response = JObject.Parse(responseJson);

            Assert.That(statusCode, Is.EqualTo(200), responseJson);
            Assert.That(response["dryRun"]?.Value<bool>(), Is.False);
            Assert.That(response["transactional"]?.Value<bool>(), Is.True);
            Assert.That(response["mode"]?.ToString(), Is.EqualTo("transactional"));
        }

        [Test]
        public void Batch_PlainExecution_EchoesExecuteMode()
        {
            string probe = ParameterlessReadOnlySkill();
            string body = JsonConvert.SerializeObject(new
            {
                steps = new[] { new { skill = probe, args = new { } } },
            });

            var (_, responseJson) = ProcessRequest("POST", "/skills/batch", "", body);

            Assert.That(JObject.Parse(responseJson)["mode"]?.ToString(), Is.EqualTo("execute"),
                "The echo must be present on every batch response, not only on the interesting ones — " +
                "a field that appears conditionally cannot be relied on to mean anything.");
        }

        [Test]
        public void Batch_UnknownQueryKey_IsStillRejected()
        {
            // 这次按键拆分的改造动到了模式解析器，而它紧邻未知参数闸门。那道闸门的行为必须保持不变。
            string body = JsonConvert.SerializeObject(new
            {
                steps = new[] { new { skill = ParameterlessReadOnlySkill(), args = new { } } },
            });

            var (statusCode, responseJson) = ProcessRequest("POST", "/skills/batch", "?nonce=1", body);

            Assert.That(statusCode, Is.EqualTo(400), responseJson);
            Assert.That(JObject.Parse(responseJson)["errorCode"]?.ToString(), Is.EqualTo("UNKNOWN_PARAM"));
        }

        // ---------- 档位：任何载荷都不得点名已被撤下的技能 ----------

        /// <summary>
        /// 档位是用户对"可以给 AI 提供什么"的表态。任何会枚举技能名的载荷都必须从可见集合作答——
        /// 而 <c>workflowTrackedSkills</c> 是最不能忘掉这一点的地方，因为"被跟踪"的技能按定义就是写类技能，
        /// 恰恰就是档位要撤掉的那一半。
        /// </summary>
        [Test]
        public void MetaAndV1Envelope_NeverNameASkillTheProfileWithdrew()
        {
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            var fullTracked = TrackedSkillsFromMeta();

            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;
            var visible = new HashSet<string>(
                SkillRouter.GetAllSkillsSnapshot().Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
            var withdrawn = fullTracked.Where(name => !visible.Contains(name)).ToArray();
            Assume.That(withdrawn, Is.Not.Empty,
                "The guide profile hides no workflow-tracked skill, so this test cannot observe a leak.");

            var guideTracked = TrackedSkillsFromMeta();
            Assert.That(guideTracked.Intersect(withdrawn, StringComparer.OrdinalIgnoreCase), Is.Empty,
                "/skills/meta named skills the guide profile hides: " +
                string.Join(", ", guideTracked.Intersect(withdrawn, StringComparer.OrdinalIgnoreCase).Take(10)));

            var envelopeTracked = ((JArray)JObject.Parse(SkillRouter.GetFilteredManifest("?full=1"))["workflowTrackedSkills"])
                .Select(t => t.ToString()).ToArray();
            Assert.That(envelopeTracked.Intersect(withdrawn, StringComparer.OrdinalIgnoreCase), Is.Empty,
                "The v1 manifest envelope leaks the same names /skills/meta was fixed not to leak — " +
                "both blocks come from one helper, so a divergence here means one call site was missed.");
        }

        [Test]
        public void FullProfile_WorkflowTrackedSkills_IsTheWholeRegistrySet()
        {
            // 另一半：过滤不得过度。在默认档位下这个块恰好等于注册表的 TracksWorkflow 集合，
            // 正是这一点保证了所有 2.7 之前的 v1 载荷逐字节不变。
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;

            var expected = SkillRouter.GetAllSkillsSnapshotUnfiltered()
                .Where(s => s.TracksWorkflow)
                .Select(s => s.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.That(TrackedSkillsFromMeta().OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                Is.EqualTo(expected),
                "Under 'full' the tracked list must still be the complete set — a narrower list here " +
                "would be a silent content change to every v1 envelope.");
        }

        /// <summary>
        /// 拼写纠正会读注册表来找相近名字，这就让它变成了一条枚举通道：拿一个被隐藏技能的
        /// 轻微错拼去问，错误响应会把真名递回来，还包在一句 agent 天生就会照做的"你是不是想找"里。
        /// </summary>
        [Test]
        public void SkillNotFound_NeverSuggestsAHiddenSkill()
        {
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;

            var hidden = FirstHiddenSkill();
            Assume.That(hidden, Is.Not.Null, "The guide profile hides nothing in this project.");

            // 只差一个字符，所以只要 Levenshtein 搜索能看见真名，就会把它排在第一位。
            string typo = hidden.Name + "x";
            var response = JObject.Parse(SkillRouter.Execute(typo, "{}"));

            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SKILL_NOT_FOUND"),
                response.ToString(Formatting.None));

            var related = (response["relatedSkills"] as JArray)?.Select(t => t.ToString()).ToArray()
                          ?? Array.Empty<string>();
            var suggested = (response["suggestedFixes"] as JArray)?
                .Select(f => f["skill"]?.ToString()).Where(s => s != null).ToArray()
                          ?? Array.Empty<string>();

            Assert.That(related, Does.Not.Contain(hidden.Name),
                $"relatedSkills handed back '{hidden.Name}', which the profile withdrew.");
            Assert.That(suggested, Does.Not.Contain(hidden.Name),
                $"suggestedFixes handed back '{hidden.Name}', which the profile withdrew.");
        }

        [Test]
        public void SkillNotFound_StillSuggestsAVisibleSkill()
        {
            // 没有这条，上面那个断言会被一个"干脆不再给任何建议"的版本满足，
            // 而那会剥掉 agent 唯一的自我纠正途径。
            var visible = SkillRouter.GetAllSkillsSnapshot()
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .First();

            var response = JObject.Parse(SkillRouter.Execute(visible.Name + "x", "{}"));
            var related = (response["relatedSkills"] as JArray)?.Select(t => t.ToString()).ToArray()
                          ?? Array.Empty<string>();

            Assert.That(related, Does.Contain(visible.Name),
                "A one-character typo on a visible skill must still be corrected — otherwise the " +
                "assertion above is satisfied by a build that suggests nothing at all.");
        }

        // ---------- ?mode=plan：排除信号 ----------

        /// <summary>
        /// <c>?mode=dryRun</c> 会汇报 SURFACE_EXCLUDED，而 <c>?mode=plan</c> 当时不会。于是一个先做规划的
        /// agent——恰恰是该端点存在所要鼓励的行为——会为一个根本跑不了的技能拿到一份完整而自信的计划，
        /// 直到第一次执行才撞上墙。
        /// </summary>
        [Test]
        public void Plan_OnHiddenSkill_CarriesTheSurfaceExcludedAuthorizationBlock()
        {
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;

            var hidden = FirstHiddenSkill();
            Assume.That(hidden, Is.Not.Null, "The guide profile hides nothing in this project.");

            var plan = JObject.Parse(SkillRouter.Plan(hidden.Name, "{}"));
            Assert.That(plan["status"]?.ToString(), Is.EqualTo("plan"),
                $"plan failed outright: {plan.ToString(Formatting.None)}");

            var auth = plan["authorization"] as JObject;
            Assert.That(auth, Is.Not.Null, $"{hidden.Name}'s plan carries no authorization block.");
            Assert.That(auth["allowed"]?.Value<bool>(), Is.False);
            Assert.That(auth["blockedBy"]?.ToString(), Is.EqualTo("SURFACE_EXCLUDED"),
                "Same verdict, same wire string as the dry-run preview — one contract for the caller.");
            Assert.That(auth["surfaceProfile"]?.ToString(), Is.EqualTo(SkillsSurfaceProfile.WireGuide),
                "The block has to name the profile responsible, or the agent cannot say what the user must change.");
            Assert.That(auth["hint"]?.ToString(), Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void Plan_OnAVisibleSkill_IsUnchanged()
        {
            // 只有在这个块确实说明了什么时才附上它：plan 本就是三种预览载荷里最大的一个，
            // 给每份计划都挂一个"永远允许"的授权块纯属额外开销。
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;

            var probe = SkillRouter.GetAllSkillsSnapshot()
                .Where(s => s.ReadOnly)
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .First();

            var plan = JObject.Parse(SkillRouter.Plan(probe.Name, "{}"));

            Assert.That(plan["status"]?.ToString(), Is.EqualTo("plan"), plan.ToString(Formatting.None));
            Assert.That(plan.Property("authorization"), Is.Null,
                "A visible skill's plan must keep its pre-fix bytes.");
        }

        // ---------- dryRun：longRunning 要出现在默认响应面上 ----------

        /// <summary>
        /// LongRunning 当时只存在于 <c>?wire=v2</c> 那个稀疏 flags 数组里，于是 agent 在调用前真正会读的
        /// 那个面——dry-run 预览——从不提醒它：即将发出的这次调用会把主线程、连同整个 HTTP 队列，
        /// 阻塞数秒。
        /// </summary>
        [Test]
        public void DryRun_ReportsLongRunning_ForBothValues()
        {
            var slow = SkillRouter.GetAllSkillsSnapshot()
                .Where(s => s.LongRunning)
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .FirstOrDefault();
            Assume.That(slow, Is.Not.Null, "No skill is annotated LongRunning; the annotations may have been lost.");

            var slowBlock = DryRunSkillBlock(slow.Name);
            Assert.That(slowBlock["longRunning"]?.Value<bool>(), Is.True,
                $"{slow.Name} declares LongRunning but its preview does not say so.");

            var fast = SkillRouter.GetAllSkillsSnapshot()
                .Where(s => !s.LongRunning && s.ReadOnly)
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .First();

            var fastBlock = DryRunSkillBlock(fast.Name);
            Assert.That(fastBlock.Property("longRunning"), Is.Not.Null,
                "The field must be present with 'false', not omitted: an absent key is indistinguishable " +
                "from an older build that never had it, so a caller could not tell 'fast' from 'unknown'.");
            Assert.That(fastBlock["longRunning"].Value<bool>(), Is.False);
        }

        [Test]
        public void DryRun_LongRunningSet_IsSourcedFromTheRegistry()
        {
            // 防的是这个字段被接到了另一个恰好在单个探针上取值相同的东西上（比如 mayTriggerReload）。
            // 凡是 dry run 根本没回成预览的技能都跳过——那是另一种缺陷，若不跳过会被误报成本条。
            var mismatches = new List<string>();
            int previewed = 0;

            foreach (var skill in SkillRouter.GetAllSkillsSnapshot()
                         .Where(s => s.ReadOnly)
                         .OrderBy(s => s.Name, StringComparer.Ordinal)
                         .Take(40))
            {
                var dry = JObject.Parse(SkillRouter.DryRun(skill.Name, "{}"));
                if (dry["status"]?.ToString() != "dryRun")
                    continue;

                previewed++;
                if ((dry["skill"]?["longRunning"] as JValue)?.Value<bool>() != skill.LongRunning)
                    mismatches.Add(skill.Name);
            }

            Assume.That(previewed, Is.GreaterThan(10),
                $"Only {previewed} previews came back; the sweep is not covering anything.");
            Assert.That(mismatches, Is.Empty,
                "dryRun's longRunning disagrees with the registry for: " + string.Join(", ", mismatches));
        }

        // ---------- dryRun / plan：载荷携带写操作的那些入口 ----------

        /// <summary>
        /// 有六个入口，它们施加的是自己载荷里携带的东西——某个 confirmToken 对应的批处理类型、
        /// 某个已记录任务的快照——而不是自身元数据声明的东西。它们的 SURFACE_EXCLUDED 拒绝是在执行时
        /// 才决定的，所以一个只凭元数据作答的预览，会对闸门实际会拒的调用回 <c>allowed:true</c>。
        /// </summary>
        private static readonly string[] CarriedWriteSkills =
        {
            "batch_execute",
            "batch_retry_failed",
            "workflow_undo_task",
            "workflow_redo_task",
            "workflow_revert_task",
            "workflow_session_undo",
        };

        // 这里逐个写出来而不是从 CarriedWriteSkills 驱动：两份清单一旦不一致，本身就值得让测试变红，
        // 因为"普通技能"那个用例正是靠这个数组来做排除的。
        [TestCase("batch_execute")]
        [TestCase("batch_retry_failed")]
        [TestCase("workflow_undo_task")]
        [TestCase("workflow_redo_task")]
        [TestCase("workflow_revert_task")]
        [TestCase("workflow_session_undo")]
        public void DryRun_CarriedWriteSkill_UnderGuide_CarriesThePayloadGate(string skillName)
        {
            Assert.That(CarriedWriteSkills, Has.Some.EqualTo(skillName),
                "The fixture's carried-write list and this test's cases drifted apart.");
            Assume.That(SkillRouter.HasSkill(skillName), Is.True, $"{skillName} is not registered.");
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;

            var auth = DryRunAuthorizationBlock(skillName);

            Assert.That(auth["payloadGated"]?.Value<bool>(), Is.True,
                $"{skillName}'s preview says nothing about the payload gate: an agent reads allowed:true " +
                $"and walks into SURFACE_EXCLUDED on execute. Block: {auth.ToString(Formatting.None)}");
            Assert.That(auth["allowed"]?.Value<bool>(), Is.True,
                "The verdict must stay as the mode ladder decided it — the preview holds no payload, so " +
                "guessing allowed:false would be wrong for every batch kind and undo this profile permits.");
            Assert.That(auth["payloadGateHint"]?.ToString(), Does.Contain("SURFACE_EXCLUDED"),
                "The caveat has to name the error code the agent will actually receive.");

            var categories = auth["payloadGatedCategories"] as JArray;
            Assert.That(categories, Is.Not.Null.And.Not.Empty,
                "The caveat must name the withdrawn categories, or the agent cannot tell which payloads are gated.");
            foreach (var category in categories)
            {
                Assert.That(Enum.TryParse(category.ToString(), out SkillCategory parsed), Is.True,
                    $"'{category}' is not a SkillCategory name.");
                Assert.That(SkillsSurfaceProfile.IsExcluded(parsed, readOnly: false), Is.True,
                    $"{skillName} reports '{category}' as gated but the active profile does not withdraw it — " +
                    "the list must be derived from the profile, not hardcoded.");
            }
        }

        [TestCase("batch_execute")]
        [TestCase("batch_retry_failed")]
        [TestCase("workflow_undo_task")]
        [TestCase("workflow_redo_task")]
        [TestCase("workflow_revert_task")]
        [TestCase("workflow_session_undo")]
        public void DryRun_CarriedWriteSkill_UnderFullProfile_IsUnchanged(string skillName)
        {
            Assume.That(SkillRouter.HasSkill(skillName), Is.True, $"{skillName} is not registered.");
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;

            var auth = DryRunAuthorizationBlock(skillName);

            // 是"不存在"而不是"为 false"：full 档位什么都不撤，所以它的预览字节必须与修复前完全一致。
            foreach (var field in new[] { "payloadGated", "payloadGatedCategories", "payloadGateHint" })
                Assert.That(auth.Property(field), Is.Null,
                    $"The full profile grew a '{field}' field on {skillName}'s authorization block.");
        }

        [Test]
        public void DryRun_OrdinarySkill_UnderGuide_CarriesNoPayloadGate()
        {
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;

            var probe = SkillRouter.GetAllSkillsSnapshot()
                .Where(s => s.ReadOnly && !CarriedWriteSkills.Contains(s.Name))
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .First();

            var auth = DryRunAuthorizationBlock(probe.Name);
            Assert.That(auth.Property("payloadGated"), Is.Null,
                $"{probe.Name} decides its write from its own metadata — the caveat belongs only to the " +
                "entry points that do not.");
        }

        /// <summary>
        /// 在 noSceneAuthoring 档位下这六个被直接隐藏（它们都声明了 MutatesScene），预览本就会回
        /// SURFACE_EXCLUDED。此时再追加那条"载荷相关"的告知，等于把一道墙说成两道。
        /// </summary>
        [Test]
        public void DryRun_CarriedWriteSkill_UnderNoSceneAuthoring_ReportsTheSkillLevelExclusionAlone()
        {
            Assume.That(SkillRouter.HasSkill("batch_execute"), Is.True, "batch_execute is not registered.");
            SkillsSurfaceProfile.Current = SurfaceProfileKind.NoSceneAuthoring;

            var auth = DryRunAuthorizationBlock("batch_execute");

            Assert.That(auth["allowed"]?.Value<bool>(), Is.False);
            Assert.That(auth["blockedBy"]?.ToString(), Is.EqualTo("SURFACE_EXCLUDED"));
            Assert.That(auth.Property("payloadGated"), Is.Null,
                "The skill-level exclusion is the whole answer here; the payload caveat must not double it.");
        }

        [Test]
        public void Plan_OnCarriedWriteSkill_UnderGuide_CarriesThePayloadGate()
        {
            Assume.That(SkillRouter.HasSkill("batch_execute"), Is.True, "batch_execute is not registered.");
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;

            var plan = JObject.Parse(SkillRouter.Plan("batch_execute", "{}"));
            Assert.That(plan["status"]?.ToString(), Is.EqualTo("plan"), plan.ToString(Formatting.None));

            var auth = plan["authorization"] as JObject;
            Assert.That(auth, Is.Not.Null,
                "?mode=plan is the surface an agent reads before sequencing several calls — the same " +
                "caveat has to reach it.");
            Assert.That(auth["payloadGated"]?.Value<bool>(), Is.True);
        }

        [Test]
        public void Plan_OnCarriedWriteSkill_UnderFullProfile_HasNoAuthorizationBlock()
        {
            Assume.That(SkillRouter.HasSkill("batch_execute"), Is.True, "batch_execute is not registered.");
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;

            var plan = JObject.Parse(SkillRouter.Plan("batch_execute", "{}"));
            Assert.That(plan.Property("authorization"), Is.Null,
                "Nothing is withdrawn under the full profile, so the plan must keep its pre-fix bytes.");
        }

        // ---------- helpers ----------

        private static JObject DryRunAuthorizationBlock(string skillName)
        {
            var dry = JObject.Parse(SkillRouter.DryRun(skillName, "{}"));
            Assert.That(dry["status"]?.ToString(), Is.EqualTo("dryRun"),
                $"{skillName}'s dry run failed: {dry.ToString(Formatting.None)}");

            var auth = dry["authorization"] as JObject;
            Assert.That(auth, Is.Not.Null, $"{skillName}'s dry run carries no authorization block.");
            return auth;
        }

        private static string[] TrackedSkillsFromMeta()
        {
            var tracked = JObject.Parse(SkillRouter.GetMeta())["workflowTrackedSkills"] as JArray;
            Assert.That(tracked, Is.Not.Null, "/skills/meta lost its workflowTrackedSkills block.");
            return tracked.Select(t => t.ToString()).ToArray();
        }

        private static JObject DryRunSkillBlock(string skillName)
        {
            var dry = JObject.Parse(SkillRouter.DryRun(skillName, "{}"));
            Assert.That(dry["status"]?.ToString(), Is.EqualTo("dryRun"),
                $"{skillName}'s dry run failed: {dry.ToString(Formatting.None)}");

            var block = dry["skill"] as JObject;
            Assert.That(block, Is.Not.Null, $"{skillName}'s dry run carries no 'skill' block.");
            return block;
        }

        /// <summary>技能 Operation 声明中包含的各个标志名，按枚举顺序。</summary>
        private static string[] OperationFlagsOf(SkillRouter.SkillInfo skill)
        {
            return Enum.GetValues(typeof(SkillOperation))
                .Cast<SkillOperation>()
                .Where(flag => flag != 0 && skill.Operation.HasFlag(flag))
                .Select(flag => flag.ToString())
                .ToArray();
        }

        /// <summary>
        /// 一个所有参数都可选的只读技能，好让某个批处理步骤能"真的执行"（用于 transactional 对照用例）
        /// 而不动工程。优先取 editor_get_layers——无参数、不依赖可选包、只读 LayerMask——若它被改名，
        /// 则退回注册表里任何符合条件的技能，使夹具仍然可用。
        /// </summary>
        private static string ParameterlessReadOnlySkill()
        {
            const string preferred = "editor_get_layers";
            if (SkillRouter.HasSkill(preferred))
                return preferred;

            var name = SkillRouter.GetAllSkillsSnapshot()
                .Where(s => s.ReadOnly &&
                            (s.Parameters == null || s.Parameters.All(p => p.HasDefaultValue)))
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .Select(s => s.Name)
                .FirstOrDefault();

            Assert.That(name, Is.Not.Null, "No read-only, parameterless skill to use as a batch probe.");
            return name;
        }

        /// <summary>当前生效档位所隐藏的第一个技能——调用前必须先设好档位。</summary>
        private static SkillRouter.SkillInfo FirstHiddenSkill()
        {
            return SkillRouter.GetAllSkillsSnapshotUnfiltered()
                .Where(s => SkillsSurfaceProfile.IsExcluded(s))
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        private static string FirstPopulatedCategory()
        {
            var category = SkillRouter.GetAllSkillsSnapshot()
                .Where(s => s.Category != SkillCategory.Uncategorized)
                .GroupBy(s => s.Category)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key.ToString())
                .FirstOrDefault();

            Assert.That(category, Is.Not.Null, "No categorized skills in the registry.");
            return category;
        }

        /// <summary>
        /// 让一次请求走真正的主线程处理器（<c>SkillsHttpServer.ProcessJob</c>）。只能靠反射进去——
        /// job 类型与方法都是 private——而在这里复述一遍处理器的路由逻辑，就等于没在测处理器。
        /// </summary>
        private static (int StatusCode, string ResponseJson) ProcessRequest(
            string httpMethod, string path, string query, string body)
        {
            var jobType = typeof(SkillsHttpServer).GetNestedType("RequestJob", BindingFlags.NonPublic);
            Assert.That(jobType, Is.Not.Null,
                "SkillsHttpServer.RequestJob was renamed; this test drives the real handler and needs it.");

            var job = Activator.CreateInstance(jobType, nonPublic: true);
            SetJobField(jobType, job, "HttpMethod", httpMethod);
            SetJobField(jobType, job, "Path", path);
            SetJobField(jobType, job, "QueryString", query);
            SetJobField(jobType, job, "Body", body);
            SetJobField(jobType, job, "StatusCode", 200);

            var processJob = typeof(SkillsHttpServer).GetMethod(
                "ProcessJob", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(processJob, Is.Not.Null, "SkillsHttpServer.ProcessJob was renamed.");
            processJob.Invoke(null, new[] { job });

            return (
                (int)GetJobField(jobType, job, "StatusCode"),
                (string)GetJobField(jobType, job, "ResponseJson"));
        }

        private static void SetJobField(Type jobType, object job, string name, object value)
        {
            var field = jobType.GetField(name);
            Assert.That(field, Is.Not.Null, $"RequestJob.{name} was renamed.");
            field.SetValue(job, value);
        }

        private static object GetJobField(Type jobType, object job, string name)
        {
            var field = jobType.GetField(name);
            Assert.That(field, Is.Not.Null, $"RequestJob.{name} was renamed.");
            return field.GetValue(job);
        }
    }
}

// Producer:Betsy
