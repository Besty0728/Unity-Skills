using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Payload contract and size red lines. Every assertion calls <see cref="SkillRouter"/> directly to
    /// build the string in-process, so the byte count measured is exactly what the HTTP layer actually
    /// sends out (both paths share the same cached string).
    ///
    /// This file deliberately never writes a skill-count literal: the registered count varies with which
    /// optional packages are installed (Cinemachine / Addressables / HybridCLR ...), and is never the same
    /// number on a clean CI project versus a local dev project. Any assertion involving a count is always
    /// derived from the same runtime source.
    /// </summary>
    [TestFixture]
    public class SkillsPayloadContractTests
    {
        // The four session-constant blocks a v2 entry omits — none of these should appear in a scoped v2 payload.
        private static readonly string[] SessionConstantKeys =
        {
            "categories", "operationTypes", "reservedBodyParameters", "workflowTrackedSkills"
        };

        private SurfaceProfileKind _savedProfile;

        /// <summary>
        /// The profile is shared globally per Unity version (EditorPrefs, not per project), so this suite's
        /// size/shape assertions must first pin the profile to full — otherwise a leftover guide profile on
        /// this machine would build a comparison like "v2 full &lt; v1 full" on top of two different skill
        /// sets. Teardown restores the original value.
        /// </summary>
        [SetUp]
        public void SetUp()
        {
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
        }

        [TearDown]
        public void TearDown()
        {
            SkillsSurfaceProfile.Current = _savedProfile;
        }

        private static int Utf8Bytes(string json) => Encoding.UTF8.GetByteCount(json);

        private static JObject Manifest(string query) => JObject.Parse(SkillRouter.GetFilteredManifest(query));

        // ---------- Bare surface verdict ----------

        [Test]
        public void BareSkillsRequest_ServesBriefDirectory()
        {
            var bare = Manifest(null);

            Assert.That(bare["manifestType"]?.ToString(), Is.EqualTo("brief"),
                "裸 GET /skills 必须回目录层 brief —— v2.7 的默认翻转就是这一条。");
            Assert.That(bare["modules"], Is.Not.Null, "brief 必须带 modules 分组。");
            Assert.That(bare["skills"], Is.Null, "brief 不该带 skills 数组（那是全量/scoped 的形状）。");
        }

        [Test]
        public void FullFlag_RestoresV1Manifest()
        {
            var full = Manifest("?full=1");

            Assert.That(full["manifestType"]?.ToString(), Is.EqualTo("manifest"),
                "?full=1 是全量清单的逃生口。");
            Assert.That(full["skills"], Is.Not.Null.And.Not.Empty);
            Assert.That(full["filtered"]?.Value<bool>(), Is.False,
                "?full=1 本身不缩小技能集，filtered 必须仍是 false。");
            Assert.That(SkillRouter.GetFilteredManifest("?full=1"), Is.EqualTo(SkillRouter.GetManifest()),
                "?full=1 必须与 GetManifest() 逐字节一致，否则两条路径给同一个 URL 两份字节。");
        }

        [Test]
        public void UnrecognizedQueryKeyAlone_StillServesBrief()
        {
            // A cache-busting nonce, client-side tracking parameters, and the like must not push the
            // request away from brief, nor mint their own permanent full-manifest cache entry.
            Assert.That(SkillRouter.GetFilteredManifest("?nonce=abc"),
                Is.EqualTo(SkillRouter.GetBrief()),
                "只带未识别参数的请求必须仍落在 brief 缓存串上。");
        }

        [Test]
        public void WireV2Alone_StillServesBrief_NotASlimFullManifest()
        {
            // The trap here: ?wire=v2 is a shape selector, it doesn't narrow the skill set, so on its own a
            // bare /skills request still lands on brief (brief has no per-skill flags to slim down, so both
            // wires share one cache entry and ETag).
            // Getting the slimmed-down full manifest requires ?full=1&wire=v2 — which is exactly what briefHint says.
            Assert.That(SkillRouter.GetFilteredManifest("?wire=v2"), Is.EqualTo(SkillRouter.GetBrief()),
                "?wire=v2 单独出现不该把裸 /skills 变回全量。");
            Assert.That(JObject.Parse(SkillRouter.GetFilteredManifest("?wire=v2"))["manifestType"]?.ToString(),
                Is.EqualTo("brief"));

            var slimFull = Manifest("?full=1&wire=v2");
            Assert.That(slimFull["manifestType"]?.ToString(), Is.EqualTo("manifest"));
            Assert.That(slimFull["wire"]?.ToString(), Is.EqualTo("v2"),
                "?full=1&wire=v2 才是瘦身版全量清单。");
        }

        [Test]
        public void BareSchemaRequest_StaysFullV1()
        {
            var schema = JObject.Parse(SkillRouter.GetFilteredSchema(null));

            Assert.That(schema["manifestType"]?.ToString(), Is.EqualTo("schema"),
                "/skills/schema 的默认没有翻转 —— 裸请求仍是全量 schema。");
            Assert.That(schema["skills"], Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void BriefTotalSkills_EqualsNamesActuallyListed()
        {
            var brief = Manifest(null);
            var modules = (JObject)brief["modules"];
            Assert.That(modules, Is.Not.Null);

            int listed = modules.Properties().Sum(p => ((JArray)p.Value).Count);

            Assert.That(brief["totalSkills"]?.Value<int>(), Is.EqualTo(listed),
                "brief 的 totalSkills 报的必须是这份载荷真的列出来的名字数 —— 报注册表总数会让 " +
                "agent 去找目录里不存在的名字。");
        }

        [Test]
        public void BriefModuleNames_AreSortedAndUnique()
        {
            var modules = (JObject)Manifest(null)["modules"];
            var moduleNames = modules.Properties().Select(p => p.Name).ToArray();

            Assert.That(moduleNames,
                Is.EqualTo(moduleNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray()),
                "模块键必须有序 —— 字节稳定性（以及 ETag 稳定性）依赖它。");

            foreach (var module in modules.Properties())
            {
                var names = ((JArray)module.Value).Select(n => n.ToString()).ToArray();
                Assert.That(names,
                    Is.EqualTo(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray()),
                    $"{module.Name} 的技能名必须有序。");
                Assert.That(names.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(names.Length),
                    $"{module.Name} 里出现了重复技能名。");
            }
        }

        // ---------- ?wire=v2 contract ----------

        [Test]
        public void ScopedV2_DropsSessionConstants_AndPointsAtMeta()
        {
            var category = FirstPopulatedCategory();
            var v2 = Manifest($"?category={category}&wire=v2");

            Assert.That(v2["wire"]?.ToString(), Is.EqualTo("v2"),
                "v2 必须自报 wire，否则静默回落 v1 时调用方会把缺失的 flags 读成「没有副作用」。");
            foreach (var key in SessionConstantKeys)
                Assert.That(v2[key], Is.Null, $"v2 载荷不该再带会话常量块 '{key}'。");

            Assert.That(v2["metaUrl"]?.ToString(), Is.EqualTo("/skills/meta"),
                "省掉常量块的代价是必须指出去哪儿取。");
            Assert.That(v2["defaults"], Is.Not.Null,
                "defaults 是 v2 省略语义唯一的可复原依据。");
            Assert.That(v2["defaults"]?["riskLevel"]?.ToString(), Is.EqualTo("low"));
            Assert.That(v2["defaults"]?["supportsDryRun"]?.Value<bool>(), Is.True);
        }

        [Test]
        public void V1Scoped_StillCarriesSessionConstants()
        {
            // v2's gains must come from v2 itself; v1's shape must not change by a single byte.
            var category = FirstPopulatedCategory();
            var v1 = Manifest($"?category={category}");

            foreach (var key in SessionConstantKeys)
                Assert.That(v1[key], Is.Not.Null, $"v1 载荷必须保留 '{key}'（老客户端在读）。");
            Assert.That(v1["metaUrl"], Is.Null, "v1 不该长出 v2 才有的字段。");
        }

        [Test]
        public void UnrecognizedWireValue_FallsBackToV1()
        {
            Assert.That(SkillRouter.GetFilteredManifest("?full=1&wire=v9"),
                Is.EqualTo(SkillRouter.GetFilteredManifest("?full=1")),
                "无法识别的 wire 值必须回落 v1 而不是报错 —— 打错一个字母不能换来一个客户端解析不了的形状。");

            var category = FirstPopulatedCategory();
            Assert.That(SkillRouter.GetFilteredManifest($"?category={category}&wire=v9"),
                Is.EqualTo(SkillRouter.GetFilteredManifest($"?category={category}")),
                "scoped 请求上的未知 wire 同样回落 v1。");
        }

        /// <summary>
        /// <c>?full=1</c> is a shape selector, it doesn't narrow the skill set, so attaching it after an
        /// already-scoped query should change nothing: same bytes, same ETag. A client swapping between
        /// these two URLs with If-None-Match must get a 304 either way.
        /// </summary>
        [Test]
        public void RedundantFullFlagOnScopedQuery_ChangesNothingObservable()
        {
            var category = FirstPopulatedCategory();
            string scoped = SkillRouter.GetFilteredManifest($"?category={category}");
            string scopedWithFull = SkillRouter.GetFilteredManifest($"?category={category}&full=1");

            Assert.That(scopedWithFull, Is.EqualTo(scoped),
                "?full=1 挂在 scoped 查询上不该改变载荷 —— 它选形状，不选技能子集。");

            Assert.That(SkillRouter.GetEtagForCachedGet("/skills", $"?category={category}&full=1", scopedWithFull),
                Is.EqualTo(SkillRouter.GetEtagForCachedGet("/skills", $"?category={category}", scoped)),
                "两个 URL 的 ETag 必须相同，否则客户端在它们之间切换时 If-None-Match 永远命中不了。");
        }

        /// <summary>
        /// <summary>
        /// A v2 summary entry must report the same flags / supportsDryRun as the full v2 entry for the same skill.
        ///
        /// This is a v2-only constraint: v1's summary carries neither field, while every v2 payload carries
        /// defaults, and defaults says "absent from flags means false." So a v2 summary entry with no flags
        /// doesn't read as "effects unknown" — it reads as "this skill changes nothing, dryRun is safe too" —
        /// which for a write skill is exactly the opposite conclusion.
        /// </summary>
        [Test]
        public void SummaryV2Entries_AgreeWithFullV2Entries_OnFlagsAndDryRun()
        {
            var category = FirstPopulatedCategory();
            var full = ((JArray)Manifest($"?category={category}&wire=v2")["skills"])
                .Cast<JObject>().ToDictionary(s => s["name"].ToString(), s => s, StringComparer.Ordinal);
            var summary = ((JArray)Manifest($"?category={category}&wire=v2&summary=1")["skills"])
                .Cast<JObject>().ToArray();

            Assert.That(summary, Is.Not.Empty, $"category={category} 的 v2 summary 是空的。");

            var issues = new List<string>();
            foreach (var entry in summary)
            {
                var name = entry["name"].ToString();
                if (!full.TryGetValue(name, out var fullEntry))
                {
                    issues.Add($"{name}: 出现在 summary 但不在 full v2 里");
                    continue;
                }

                if (!JToken.DeepEquals(entry["flags"] ?? JValue.CreateNull(),
                        fullEntry["flags"] ?? JValue.CreateNull()))
                {
                    issues.Add($"{name}.flags: summary={entry["flags"]} full={fullEntry["flags"]}");
                }

                if (!JToken.DeepEquals(entry["supportsDryRun"] ?? JValue.CreateNull(),
                        fullEntry["supportsDryRun"] ?? JValue.CreateNull()))
                {
                    issues.Add($"{name}.supportsDryRun: summary={entry["supportsDryRun"]} " +
                               $"full={fullEntry["supportsDryRun"]}");
                }

                if (!JToken.DeepEquals(entry["riskLevel"] ?? JValue.CreateNull(),
                        fullEntry["riskLevel"] ?? JValue.CreateNull()))
                {
                    issues.Add($"{name}.riskLevel: summary={entry["riskLevel"]} full={fullEntry["riskLevel"]}");
                }
            }

            Assert.That(issues, Is.Empty,
                "v2 summary 与 full 条目对同一技能的影响面报得不一致:\n" + string.Join("\n", issues.Take(30)));
        }

        [Test]
        public void SummaryV2_CarriesDefaultsBlock_SoOmissionsAreReadable()
        {
            var summary = Manifest($"?category={FirstPopulatedCategory()}&wire=v2&summary=1");

            Assert.That(summary["defaults"], Is.Not.Null,
                "summary 条目省略 flags/supportsDryRun 的语义全靠 defaults 说明，它必须在。");
            Assert.That(summary["summary"]?.Value<bool>(), Is.True);
        }

        [Test]
        public void V2Entries_ReconstructV1Entries_Losslessly()
        {
            var category = FirstPopulatedCategory();
            var v1Skills = ((JArray)Manifest($"?category={category}")["skills"]).Cast<JObject>().ToArray();
            var v2Skills = ((JArray)Manifest($"?category={category}&wire=v2")["skills"]).Cast<JObject>().ToArray();

            Assert.That(v2Skills.Length, Is.EqualTo(v1Skills.Length),
                "两个 wire 必须描述同一批技能。");
            Assert.That(v1Skills.Length, Is.GreaterThan(0), $"category={category} 没有任何技能，抽查是空的。");

            for (int i = 0; i < v1Skills.Length; i++)
            {
                var expected = v1Skills[i];
                var rebuilt = ReconstructV1Entry(v2Skills[i]);
                var name = expected["name"]?.ToString();

                Assert.That(rebuilt["name"]?.ToString(), Is.EqualTo(name),
                    "两个 wire 的条目顺序必须一致（都按名字排序）。");

                foreach (var property in expected.Properties())
                {
                    Assert.That(JToken.DeepEquals(rebuilt[property.Name] ?? JValue.CreateNull(),
                            property.Value),
                        Is.True,
                        $"v2→v1 复原丢失了 `{name}`.{property.Name}：" +
                        $"v1={property.Value.ToString(Newtonsoft.Json.Formatting.None)}，" +
                        $"复原={(rebuilt[property.Name] ?? JValue.CreateNull()).ToString(Newtonsoft.Json.Formatting.None)}");
                }
            }
        }

        [Test]
        public void V2Flags_AgreeWithV1Booleans_AcrossWholeRegistry()
        {
            // The test above spot-checks by category; this one covers everything: flags are v2's only
            // suspect for data loss, running it in-process is cheap, and there's no reason to only sample.
            var v1Skills = ((JArray)Manifest("?full=1")["skills"]).Cast<JObject>()
                .ToDictionary(s => s["name"].ToString(), s => s, StringComparer.Ordinal);
            var v2Skills = ((JArray)Manifest("?full=1&wire=v2")["skills"]).Cast<JObject>();

            var issues = new List<string>();
            foreach (var v2 in v2Skills)
            {
                var name = v2["name"].ToString();
                if (!v1Skills.TryGetValue(name, out var v1))
                {
                    issues.Add($"v2 有而 v1 没有的技能: {name}");
                    continue;
                }

                var flags = new HashSet<string>(
                    (v2["flags"] as JArray)?.Select(f => f.ToString()) ?? Enumerable.Empty<string>(),
                    StringComparer.Ordinal);

                foreach (var flag in new[] { "readOnly", "tracksWorkflow", "mutatesScene",
                                             "mutatesAssets", "mayTriggerReload", "mayEnterPlayMode" })
                {
                    bool v1Value = v1[flag]?.Value<bool>() ?? false;
                    if (flags.Contains(flag) != v1Value)
                        issues.Add($"{name}.{flag}: v1={v1Value}，v2 flags {(flags.Contains(flag) ? "含" : "不含")}");
                }
            }

            Assert.That(issues, Is.Empty, "v2 flags 与 v1 布尔不一致:\n" + string.Join("\n", issues.Take(40)));
        }

        [Test]
        public void LongRunningFlag_IsCarriedByV2Only()
        {
            var v2Skills = ((JArray)Manifest("?full=1&wire=v2")["skills"]).Cast<JObject>().ToArray();
            var longRunning = v2Skills
                .Where(s => (s["flags"] as JArray)?.Any(f => f.ToString() == "longRunning") == true)
                .Select(s => s["name"].ToString())
                .ToArray();

            // The count isn't hardcoded (it varies with optional packages), but there has to be at least
            // one — the whole reason this flag exists is to flag synchronous skills that freeze the main
            // thread, and the HTTP queue along with it, for seconds at a time.
            Assert.That(longRunning, Is.Not.Empty,
                "没有任何技能带 longRunning，标注很可能整批丢了。");

            var expected = SkillRouter.GetAllSkillsSnapshot()
                .Where(s => s.LongRunning)
                .Select(s => s.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            Assert.That(longRunning.OrderBy(n => n, StringComparer.Ordinal).ToArray(), Is.EqualTo(expected),
                "v2 的 longRunning 集合必须与注册表元数据同源。");
        }

        // ---------- /skills/meta ----------

        [Test]
        public void MetaEndpoint_MatchesV1EnvelopeConstants_ValueByValue()
        {
            var meta = JObject.Parse(SkillRouter.GetMeta());
            var v1 = Manifest("?full=1");

            Assert.That(meta["manifestType"]?.ToString(), Is.EqualTo("meta"));
            foreach (var key in SessionConstantKeys)
            {
                Assert.That(meta[key], Is.Not.Null, $"/skills/meta 缺少常量块 '{key}'。");
                Assert.That(JToken.DeepEquals(meta[key], v1[key]), Is.True,
                    $"/skills/meta 的 '{key}' 与 v1 envelope 不一致 —— v2 客户端会读到一份和 v1 " +
                    $"客户端不同的枚举。\nmeta={meta[key]}\nv1={v1[key]}");
            }
        }

        [Test]
        public void MetaEndpoint_CarriesDefaults()
        {
            var meta = JObject.Parse(SkillRouter.GetMeta());

            Assert.That(meta["defaults"], Is.Not.Null);
            Assert.That(JToken.DeepEquals(meta["defaults"],
                    Manifest($"?category={FirstPopulatedCategory()}&wire=v2")["defaults"]),
                Is.True, "meta 与 v2 envelope 的 defaults 必须逐值一致，否则省略语义有两份说法。");
        }

        /// <summary>
        /// meta's stability contract. This used to assert "byte-identical across all three profiles", and
        /// that can no longer be required — workflowTrackedSkills must be filtered per profile (tracked
        /// skills are by definition write skills, exactly the half a profile revokes, so not filtering them
        /// would mean handing out names the user explicitly revoked), so the whole payload can never be
        /// identical across profiles.
        ///
        /// What's left is what an agent actually depends on, pinned down here point by point:
        /// <list type="number">
        /// <item><b>Byte-stable within a profile</b>: rebuilding after a cache clear yields the same bytes,
        /// otherwise the ETag would drift for no reason.</item>
        /// <item><b>workflowTrackedSkills is a subset of the current profile's visible skill set</b>:
        /// leaking a name is the only reason this filtering exists.</item>
        /// <item><b>Every block other than workflowTrackedSkills is value-identical across profiles</b>:
        /// this is the part of the original assertion that still guards against drift —
        /// blocking anyone from smuggling surfaceProfile (or any other live value) back into meta.</item>
        /// </list>
        /// The profile itself remains solely authoritative via <c>/health</c>; this field is not placed in meta.
        /// </summary>
        [Test]
        public void MetaEndpoint_IsStablePerProfile_AndCarriesNoOtherLiveValue()
        {
            var byProfile = new Dictionary<SurfaceProfileKind, string>();
            foreach (SurfaceProfileKind profile in Enum.GetValues(typeof(SurfaceProfileKind)))
            {
                SkillsSurfaceProfile.Current = profile;

                var built = SkillRouter.GetMeta();
                SkillRouter.InvalidateOutputCaches();
                Assert.That(SkillRouter.GetMeta(), Is.EqualTo(built),
                    $"{profile} 档重建出的 meta 与上一次不同字节 —— 同一档位内它必须是确定的，" +
                    "否则每次缓存失效都换一个 ETag。");
                byProfile[profile] = built;

                var visible = new HashSet<string>(
                    SkillRouter.GetAllSkillsSnapshot().Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
                var tracked = ((JArray)JObject.Parse(built)["workflowTrackedSkills"])
                    .Select(t => t.ToString()).ToArray();
                var leaked = tracked.Where(name => !visible.Contains(name)).ToArray();
                Assert.That(leaked, Is.Empty,
                    $"{profile} 档的 meta 列出了该档位隐藏的技能名: " + string.Join(", ", leaked.Take(10)));
            }

            var baseline = JObject.Parse(byProfile[SurfaceProfileKind.Full]);
            foreach (var kv in byProfile)
            {
                var current = JObject.Parse(kv.Value);
                Assert.That(current.Properties().Select(p => p.Name).ToArray(),
                    Is.EqualTo(baseline.Properties().Select(p => p.Name).ToArray()),
                    $"{kv.Key} 档的 meta 字段集与 full 档不同 —— 形状不该随档位变。");

                foreach (var property in baseline.Properties())
                {
                    if (property.Name == "workflowTrackedSkills")
                        continue;
                    Assert.That(JToken.DeepEquals(current[property.Name], property.Value), Is.True,
                        $"meta 的 '{property.Name}' 随档位变了 —— 只有 workflowTrackedSkills 允许随档位变。" +
                        $"\nfull={property.Value}\n{kv.Key}={current[property.Name]}");
                }
            }

            Assert.That(baseline.Property("surfaceProfile"), Is.Null,
                "meta 不该带 surfaceProfile：档位是用户可随时切换的活值，唯一权威是 GET /health。" +
                "把它放进一份「取一次、整个会话复用」的文档里，就是让 agent 缓存一个会过期的值。");
        }

        // ---------- Size red lines ----------

        [Test]
        public void V2FullManifest_IsSmallerThanV1()
        {
            int v1 = Utf8Bytes(SkillRouter.GetFilteredManifest("?full=1"));
            int v2 = Utf8Bytes(SkillRouter.GetFilteredManifest("?full=1&wire=v2"));

            Assert.That(v2, Is.LessThan(v1),
                $"v2 的每一处差异都该是减法，实测 v1={v1} v2={v2}。");
        }

        [Test]
        public void BriefPayload_IsUnderTenPercentOfV1Full()
        {
            int v1 = Utf8Bytes(SkillRouter.GetFilteredManifest("?full=1"));
            int brief = Utf8Bytes(SkillRouter.GetBrief());

            Assert.That(brief, Is.LessThan(v1 / 10),
                $"目录层要担得起「默认答案」这个位置，必须远小于全量：v1={v1} brief={brief} " +
                $"({100.0 * brief / v1:F1}%)。");
        }

        // ---------- dryRun authorization preview shape ----------

        [Test]
        public void DryRun_CarriesAuthorizationBlockWithFullShape()
        {
            var probe = FirstReadOnlySkillName();
            var dry = JObject.Parse(SkillRouter.DryRun(probe, "{}"));
            var auth = dry["authorization"] as JObject;

            Assert.That(auth, Is.Not.Null, $"{probe} 的 dryRun 没有 authorization 块。");
            foreach (var field in new[] { "allowed", "blockedBy", "currentMode", "allowlisted", "hint" })
                Assert.That(auth.Property(field), Is.Not.Null,
                    $"authorization 缺字段 '{field}' —— agent 靠这几个字段判断该不该发 execute。");

            Assert.That(auth["currentMode"]?.ToString(),
                Is.EqualTo(SkillsModeManager.ModeToWire(SkillsModeManager.CurrentMode)));
            Assert.That(auth["allowlisted"]?.Type, Is.EqualTo(JTokenType.Boolean));
            Assert.That(auth["allowed"]?.Type, Is.EqualTo(JTokenType.Boolean));
        }

        [Test]
        public void DryRun_UnderBypass_ReportsOrdinarySkillAsAllowed()
        {
            var savedMode = SkillsModeManager.CurrentMode;
            try
            {
                SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
                var auth = JObject.Parse(SkillRouter.DryRun(FirstReadOnlySkillName(), "{}"))["authorization"];

                Assert.That(auth["allowed"]?.Value<bool>(), Is.True, "Bypass 下普通技能必须 allowed。");
                Assert.That(auth["blockedBy"]?.Type, Is.EqualTo(JTokenType.Null),
                    "allowed 为真时 blockedBy 必须是 null，不是空串。");
            }
            finally
            {
                SkillsModeManager.CurrentMode = savedMode;
            }
        }

        [Test]
        public void DryRun_DoesNotConsumeOneShotGrantToken()
        {
            // A preview must be pure read: CheckAccess consumes the thread's one-shot grant token, so
            // BuildAuthorizationPreview deliberately routes around it. This test guards that "deliberately".
            var savedMode = SkillsModeManager.CurrentMode;
            try
            {
                SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
                SkillsModeManager.ClearOneShotBypass();

                int pendingBefore = SkillsModeManager.PendingGrantRequests.Count;
                SkillRouter.DryRun(FirstReadOnlySkillName(), "{}");

                Assert.That(SkillsModeManager.PendingGrantRequests.Count, Is.EqualTo(pendingBefore),
                    "dryRun 不该发起授权请求，也不该消耗待批队列。");
            }
            finally
            {
                SkillsModeManager.CurrentMode = savedMode;
                SkillsModeManager.ClearOneShotBypass();
            }
        }

        // ---------- helpers ----------

        /// <summary>
        /// Picks any category that has skills under the current profile — the name is read live from the
        /// registry, so this holds whether or not any optional package is installed.
        /// </summary>
        private static string FirstPopulatedCategory()
        {
            var category = SkillRouter.GetAllSkillsSnapshot()
                .Where(s => s.Category != SkillCategory.Uncategorized)
                .GroupBy(s => s.Category)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key.ToString())
                .FirstOrDefault();

            Assert.That(category, Is.Not.Null, "注册表里没有任何带分类的技能。");
            return category;
        }

        private static string FirstReadOnlySkillName()
        {
            var name = SkillRouter.GetAllSkillsSnapshot()
                .Where(s => s.ReadOnly)
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .Select(s => s.Name)
                .FirstOrDefault();

            Assert.That(name, Is.Not.Null, "注册表里没有任何只读技能。");
            return name;
        }

        /// <summary>
        /// Reconstructs a v2 entry into v1-entry shape following v2's own declared omission rules: flags is
        /// expanded back into six booleans, riskLevel defaults to "low" when absent, supportsDryRun defaults
        /// to true when absent, and members swallowed by NullValueHandling are restored as explicit null.
        /// longRunning doesn't participate — v1 never carries this field, it's a pure v2 addition.
        /// </summary>
        private static JObject ReconstructV1Entry(JObject v2)
        {
            var flags = new HashSet<string>(
                (v2["flags"] as JArray)?.Select(f => f.ToString()) ?? Enumerable.Empty<string>(),
                StringComparer.Ordinal);

            var rebuilt = new JObject
            {
                ["name"] = v2["name"],
                ["description"] = v2["description"],
                ["category"] = v2["category"] ?? JValue.CreateNull(),
                ["operation"] = v2["operation"] ?? JValue.CreateNull(),
                ["tags"] = v2["tags"] ?? JValue.CreateNull(),
                ["outputs"] = v2["outputs"] ?? JValue.CreateNull(),
                ["requiresInput"] = v2["requiresInput"] ?? JValue.CreateNull(),
                ["readOnly"] = flags.Contains("readOnly"),
                ["tracksWorkflow"] = flags.Contains("tracksWorkflow"),
                ["mutatesScene"] = flags.Contains("mutatesScene"),
                ["mutatesAssets"] = flags.Contains("mutatesAssets"),
                ["mayTriggerReload"] = flags.Contains("mayTriggerReload"),
                ["mayEnterPlayMode"] = flags.Contains("mayEnterPlayMode"),
                // Absence means default: supportsDryRun only appears when false, riskLevel only appears when it isn't low.
                ["supportsDryRun"] = v2["supportsDryRun"]?.Value<bool>() ?? true,
                ["riskLevel"] = v2["riskLevel"]?.ToString() ?? "low",
                ["requiresPackages"] = v2["requiresPackages"] ?? JValue.CreateNull(),
                ["mode"] = v2["mode"],
                ["approvalBehavior"] = v2["approvalBehavior"],
                ["parameters"] = ReconstructParameters(v2["parameters"] as JArray),
            };

            return rebuilt;
        }

        private static JArray ReconstructParameters(JArray v2Parameters)
        {
            var rebuilt = new JArray();
            if (v2Parameters == null) return rebuilt;

            foreach (var parameter in v2Parameters.Cast<JObject>())
            {
                rebuilt.Add(new JObject
                {
                    ["name"] = parameter["name"],
                    ["type"] = parameter["type"],
                    ["required"] = parameter["required"],
                    // v2 serializes with NullValueHandling.Ignore, so a parameter whose defaultValue is null
                    // has the whole key missing; v1 writes an explicit null.
                    ["defaultValue"] = parameter["defaultValue"] ?? JValue.CreateNull(),
                });
            }
            return rebuilt;
        }
    }
}

// Producer:Betsy
