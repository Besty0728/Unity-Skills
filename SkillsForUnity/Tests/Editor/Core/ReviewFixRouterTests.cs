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
    /// Scenarios on the discovery / preview interfaces where "the answer is wrong, and the caller
    /// can't tell" — four categories of failure, all silent:
    ///
    /// <para><b>Silent no-op.</b> A query key dropped by the parser (<c>?full</c> written without
    /// a value), or a value that gets past the guard but matches nothing. The caller reads a
    /// well-formed 200 and draws a conclusion about the project that only holds because of its
    /// own typo.</para>
    ///
    /// <para><b>Silent override.</b> Two request keys are parsed by the same flag, so the loser
    /// disappears: when the request body <c>{"dryRun":true}</c> and <c>?mode=transactional</c>
    /// both show up, the operation the caller wanted to preview actually gets executed, and
    /// nothing in the response says which mode won.</para>
    ///
    /// <para><b>Silent leak.</b> A payload is built from the raw registry instead of the visible
    /// skill set, so a name the user withdrew via a profile still gets sent out anyway — in
    /// <c>/skills/meta</c>, in the v1 envelope, and among the spelling-correction candidates.</para>
    ///
    /// <para><b>Silent optimism.</b> For entry points where "whether it's rejected is decided by
    /// the payload, and the preview never got that payload" (batch_execute and the workflow
    /// undo/redo family), the preview answered <c>allowed:true</c> based on metadata alone.</para>
    ///
    /// No skill count is hardcoded: the registry shifts with whichever optional packages are
    /// installed. Both the probes and the expectations are derived from the live registry.
    /// </summary>
    [TestFixture]
    public class ReviewFixRouterTests
    {
        private SurfaceProfileKind _savedProfile;
        private SkillsOperatingMode _savedMode;

        /// <summary>
        /// UnitySkills_SurfaceProfile is an EditorPrefs key, meaning it's "shared machine-wide
        /// per Unity version" rather than per-project: leaving a profile set here would silently
        /// change the visible scope for every other fixture in this run (and the developer's next
        /// editor session). Hence it's backed up and restored here.
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

        // ---------- ?operation=: the guard must not be narrower than the filter ----------

        /// <summary>
        /// SkillOperation is a [Flags] enum, and the filter parses it with
        /// <c>Enum.TryParse(value, ignoreCase: true)</c>, so it accepts a comma list. But the
        /// guard standing in front of it instead compares against Enum.GetNames, so
        /// <c>?operation=Query,Modify</c> — a value the filter would happily recognize — got a
        /// 400. A guard that rejects legal input is worse than no guard at all: it turns a query
        /// that would otherwise work into a permanent error.
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
            // Enum.TryParse also accepts the underlying numeric value, so the filter recognizes
            // "?operation=4". The guard must be consistent with that — see OperationFilter_AcceptsAFlagsCommaList.
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
            // The relaxation above must not amount to turning the guard off: a genuine typo must
            // still be a 400 with a vocabulary, not a 200 with an empty skills array.
            var response = JObject.Parse(SkillRouter.GetFilteredManifest("?operation=Modifyy,Query"));

            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"),
                response.ToString(Formatting.None));
            Assert.That(response["details"]?["validOperations"] as JArray, Is.Not.Null.And.Not.Empty);
        }

        [TestCase("/skills")]
        [TestCase("/skills/schema")]
        public void OperationCommaList_IsAcceptedOnBothManifestPaths(string path)
        {
            // Both endpoints go through the same guard, and the fast path on the HTTP thread asks
            // the same question — so a value legal on one endpoint must be legal on the other, and on both paths.
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

        // ---------- Bare flags and blank values ----------

        /// <summary>
        /// <c>?full</c> without <c>=1</c> is the conventional URL idiom for "this flag is set",
        /// and it happens to be the one flag whose whole job is to override the "give the brief
        /// by default" behavior. Once dropped by the parser, it returns a ~19KB directory while
        /// the caller is still waiting for the full manifest — and because the directory itself
        /// is a perfectly legal payload, nothing looks wrong.
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
        /// A key written with no value at all. Dropping it is equivalent to "no filtering", so a
        /// scope restriction that only got half-written returns the entire directory, and it
        /// still looks like it was honored. A rejection must carry the vocabulary, exactly like a
        /// mistyped value would.
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
            // Each of these has a "default interpretation", and that is exactly the problem:
            // answering based on the default would make the caller believe this key took effect.
            // ?tags= would match nothing at all, ?readonly= would quietly be treated as
            // readonly=false, and ?full= would hand back the very directory the caller was trying to escape.
            var response = JObject.Parse(SkillRouter.GetFilteredManifest($"?{key}="));

            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"),
                $"?{key}= was answered instead of refused: {response.ToString(Formatting.None)}");
            Assert.That(response["details"]?["parameter"]?.ToString(), Is.EqualTo(key));
        }

        [Test]
        public void BlankValueRejection_MintsNoCacheEntry()
        {
            // Same reasoning as a mistyped value: a rejected query must not earn a manifest-sized
            // cache entry, nor an ETag that would turn the error into a 304.
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
            // The parser now additionally preserves two forms it used to drop. The forms it
            // already preserved must not change at all: those are the queries real callers are actually sending.
            var category = FirstPopulatedCategory();

            Assert.That(JObject.Parse(SkillRouter.GetFilteredManifest($"?category={category}"))["errorCode"], Is.Null);
            Assert.That(JObject.Parse(SkillRouter.GetFilteredManifest($"?category={category}&wire=v2"))["wire"]?.ToString(),
                Is.EqualTo("v2"));
            Assert.That(SkillRouter.GetFilteredManifest("?nonce=abc"), Is.EqualTo(SkillRouter.GetBrief()),
                "An unrecognized key with a value is still stripped, so the request still lands on brief.");
            Assert.That(SkillRouter.GetFilteredManifest(null), Is.EqualTo(SkillRouter.GetBrief()));
            Assert.That(SkillRouter.GetFilteredManifest("?"), Is.EqualTo(SkillRouter.GetBrief()));
        }

        // ---------- POST /skills/batch: mode and dryRun are two independent keys ----------

        /// <summary>
        /// The failure this test group targets: the URL carries <c>?mode=transactional</c> while
        /// the request body carries <c>{"dryRun":true}</c>. Both keys used to be gated by the
        /// same "has the query already been decided" flag, so the body's dryRun got discarded and
        /// the batch of operations was actually executed — the caller wanted a preview and got a
        /// real change, and the response never mentioned which mode had won.
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
            // When the same key appears in both places, the URL wins. Without this, the fix above
            // would just become "the request body always wins" — the same bug, pointed the other way.
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
            // The reverse counterpart of the transactional=false forcing above: it must only trigger when someone genuinely asked for a preview.
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
            // This per-key split touched the mode parser, which sits right next to the unknown-parameter gate. That gate's behavior must stay unchanged.
            string body = JsonConvert.SerializeObject(new
            {
                steps = new[] { new { skill = ParameterlessReadOnlySkill(), args = new { } } },
            });

            var (statusCode, responseJson) = ProcessRequest("POST", "/skills/batch", "?nonce=1", body);

            Assert.That(statusCode, Is.EqualTo(400), responseJson);
            Assert.That(JObject.Parse(responseJson)["errorCode"]?.ToString(), Is.EqualTo("UNKNOWN_PARAM"));
        }

        // ---------- Profiles: no payload may name a skill that's already been withdrawn ----------

        /// <summary>
        /// A profile is the user's statement of "what can be offered to the AI". Any payload that
        /// enumerates skill names must answer from the visible set — and
        /// <c>workflowTrackedSkills</c> is the place least able to afford forgetting this, because
        /// a "tracked" skill is by definition a write-type skill, exactly the half a profile is meant to withdraw.
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
            // The other half: filtering must not overreach. Under the default profile, this block
            // is exactly equal to the registry's TracksWorkflow set, and that's precisely what
            // guarantees every pre-2.7 v1 payload stays byte-for-byte unchanged.
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
        /// Spelling correction reads the registry to find similar-looking names, and that turns
        /// it into an enumeration channel: asking with a slight typo of a hidden skill gets the
        /// real name handed back in the error response, wrapped in a "did you mean" that an agent
        /// naturally follows.
        /// </summary>
        [Test]
        public void SkillNotFound_NeverSuggestsAHiddenSkill()
        {
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;

            var hidden = FirstHiddenSkill();
            Assume.That(hidden, Is.Not.Null, "The guide profile hides nothing in this project.");

            // Off by only one character, so as long as the Levenshtein search can see the real name, it will rank it first.
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
            // Without this, the assertion above would also be satisfied by a version that simply
            // stops offering any suggestion at all — which would strip the agent's only path to self-correction.
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

        // ---------- ?mode=plan: exclusion signal ----------

        /// <summary>
        /// <c>?mode=dryRun</c> reported SURFACE_EXCLUDED, while <c>?mode=plan</c> didn't, at the
        /// time. So an agent that plans ahead first — exactly the behavior this endpoint exists
        /// to encourage — would get a complete, confident plan for a skill that can't actually
        /// run at all, and only hit the wall on the first execution attempt.
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
            // Only attach it when this block actually says something: plan is already the
            // largest of the three preview payload types, and hanging an "always allowed"
            // authorization block on every single plan is pure overhead.
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

        // ---------- dryRun: longRunning must appear on the default response surface ----------

        /// <summary>
        /// LongRunning used to live only in the sparse flags array under <c>?wire=v2</c>, so the
        /// one surface an agent actually reads before calling — the dry-run preview — never
        /// warned it that the call it was about to send would block the main thread, and the
        /// entire HTTP queue with it, for several seconds.
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
            // Guards against this field being wired to something else that just happens to have
            // the same value on a single probe (e.g. mayTriggerReload). Any skill whose dry run
            // simply didn't come back as a preview is skipped — that's a different kind of
            // defect, and not skipping it would misreport as this one.
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

        // ---------- dryRun / plan: entry points where the write operation is carried by the payload ----------

        /// <summary>
        /// There are six entry points that apply what their own payload carries — the batch type
        /// a given confirmToken corresponds to, the snapshot of an already-recorded task —
        /// rather than what their own metadata declares. Their SURFACE_EXCLUDED rejection is only
        /// decided at execute time, so a preview that answers based on metadata alone would say
        /// <c>allowed:true</c> for a call the gate will actually reject.
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

        // Written out one by one here rather than driven from CarriedWriteSkills: if the two
        // lists ever diverge, that in itself deserves to turn this test red, because the
        // "ordinary skill" test case relies on this exact array for its exclusion.
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

            // "Absent" rather than "false": the full profile withdraws nothing, so its preview
            // bytes must be exactly identical to before the fix.
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
        /// Under the noSceneAuthoring profile, these six are directly hidden (they all declare
        /// MutatesScene), and the preview would already answer SURFACE_EXCLUDED on its own.
        /// Appending the "payload-related" notice on top of that here would just be describing one wall as two.
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

        /// <summary>Each flag name included in a skill's Operation declaration, in enum order.</summary>
        private static string[] OperationFlagsOf(SkillRouter.SkillInfo skill)
        {
            return Enum.GetValues(typeof(SkillOperation))
                .Cast<SkillOperation>()
                .Where(flag => flag != 0 && skill.Operation.HasFlag(flag))
                .Select(flag => flag.ToString())
                .ToArray();
        }

        /// <summary>
        /// A read-only skill with every parameter optional, so a given batch step can "really
        /// execute" (for the transactional control case) without touching the project. Prefers
        /// editor_get_layers — parameterless, no dependency on optional packages, a read-only
        /// LayerMask — and if it ever gets renamed, falls back to any qualifying skill in the
        /// registry, so the fixture keeps working.
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

        /// <summary>The first skill hidden by the currently active profile — the profile must be set before calling this.</summary>
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
        /// Routes a request through the actual main-thread handler (<c>SkillsHttpServer.ProcessJob</c>).
        /// The only way in is reflection — the job type and method are both private — and
        /// re-describing the handler's routing logic here instead would mean this isn't testing the handler at all.
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
