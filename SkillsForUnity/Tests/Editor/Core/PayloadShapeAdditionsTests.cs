using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The response fields added to make a "truncated / incomplete answer" *detectable*.
    ///
    /// <para>They guard against the same class of failure: a payload that looks complete but isn't.
    /// A node with no <c>children</c> array because the walk hit the depth cap reads identically to
    /// a true leaf. A <c>tests</c> array cut short by <c>limit</c> reads identically to the full set.
    /// A polling job whose result was omitted for size reasons reads identically to one that produced
    /// nothing. In all three the caller's next move is wrong and nothing hints at it — so here every field is asserted together with the value it disambiguates, never alone.</para>
    ///
    /// <para>Counts are never hardcoded: registry size varies with installed optional packages, and
    /// discovered test counts vary with the project — every number is derived at run time, or synthesized by the test itself.</para>
    /// </summary>
    [TestFixture]
    public class PayloadShapeAdditionsTests
    {
        private const string TestDiscoveryJobKind = "test_discovery";

        private SkillsOperatingMode _savedMode;
        private SurfaceProfileKind _savedProfile;
        private readonly List<string> _createdJobs = new List<string>();
        private readonly List<string> _createdAssets = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _savedMode = SkillsModeManager.CurrentMode;
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var jobId in _createdJobs)
                BatchPersistence.RemoveJob(jobId);
            _createdJobs.Clear();

            foreach (var asset in _createdAssets)
                AssetDatabase.DeleteAsset(asset);
            _createdAssets.Clear();

            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
            GameObjectFinder.InvalidateCache();
        }

        private static JObject Payload(string skill, string body)
        {
            var response = JObject.Parse(SkillRouter.Execute(skill, body));
            Assert.That(response["errorCode"], Is.Null,
                $"{skill} failed: {response.ToString(Formatting.None)}");

            var result = response["result"] as JObject;
            Assert.That(result, Is.Not.Null,
                "Expected the skill payload under 'result'. Top-level keys: " +
                string.Join(", ", response.Properties().Select(p => p.Name)));
            return result;
        }

        // ---------- job_status / job_wait: omit result by default ----------

        /// <summary>
        /// These two endpoints are for repeated polling, and a completed test or compile job's result
        /// payload is far larger than the status envelope wrapping it. Inlining it every poll is an
        /// expensive default, but dropping it outright is worse: the caller can't distinguish "there is no result" from "the result was withheld." <c>resultAvailable</c> distinguishes the two, <c>resultHint</c> makes the answer actionable.
        /// </summary>
        [Test]
        public void JobStatus_ByDefault_OmitsTheResultButSaysItExists()
        {
            var jobId = CreateCompletedJobWithResult();

            var payload = Payload("job_status", "{\"jobId\":\"" + jobId + "\"}");

            Assert.That(payload["resultAvailable"]?.Value<bool>(), Is.True,
                "The job has a result payload; a caller must be able to learn that without receiving it.");
            Assert.That(payload["resultHint"]?.ToString(), Is.Not.Null.And.Not.Empty,
                "Knowing a result exists is only useful alongside how to fetch it.");

            // Key stays present, value is null. If the key vanished entirely it would be indistinguishable
            // from "this key never existed in an old version," and the client couldn't tell "withheld" from "unsupported."
            Assert.That(payload.Property("details"), Is.Not.Null,
                "'details' must remain present as an explicit null, not vanish from the payload.");
            Assert.That(payload["details"].Type, Is.EqualTo(JTokenType.Null),
                "includeDetails defaults to false, so the result must not be inlined.");
        }

        [Test]
        public void JobStatus_WithIncludeDetails_InlinesTheResult()
        {
            var jobId = CreateCompletedJobWithResult();

            var payload = Payload("job_status", "{\"jobId\":\"" + jobId + "\",\"includeDetails\":true}");

            Assert.That(payload["details"]?.Type, Is.EqualTo(JTokenType.Object),
                "includeDetails=true is the documented escape hatch back to the pre-2.7 shape.");
            Assert.That(payload["details"]?["totalTests"]?.Value<int>(), Is.EqualTo(7));
            Assert.That(payload["resultAvailable"]?.Value<bool>(), Is.True);
            Assert.That(payload["resultHint"]?.Type ?? JTokenType.Null, Is.EqualTo(JTokenType.Null),
                "With the result inlined there is nothing left to hint at; a hint here would tell " +
                "the caller to go fetch what it is already holding.");
        }

        [Test]
        public void JobStatus_JobWithNoResult_ReportsResultUnavailable()
        {
            // The negative case is what makes resultAvailable meaningful -- an always-true impl would also pass the assertion above.
            var jobId = CreateJob("running", withResult: false);

            var payload = Payload("job_status", "{\"jobId\":\"" + jobId + "\"}");

            Assert.That(payload["resultAvailable"]?.Value<bool>(), Is.False);
            Assert.That(payload["details"]?.Type ?? JTokenType.Null, Is.EqualTo(JTokenType.Null));
        }

        [Test]
        public void JobWait_FollowsTheSameOmissionContract()
        {
            // Same two fields, same defaults. This one drifted once before; a caller that polls with
            // job_wait then switches to job_status shouldn't be forced to relearn the response shape.
            var jobId = CreateCompletedJobWithResult();

            var withheld = Payload("job_wait", "{\"jobId\":\"" + jobId + "\",\"timeoutMs\":100}");
            Assert.That(withheld["resultAvailable"]?.Value<bool>(), Is.True);
            Assert.That(withheld["resultHint"]?.ToString(), Is.Not.Null.And.Not.Empty);
            Assert.That(withheld.Property("details"), Is.Not.Null);
            Assert.That(withheld["details"].Type, Is.EqualTo(JTokenType.Null));

            var inlined = Payload("job_wait",
                "{\"jobId\":\"" + jobId + "\",\"timeoutMs\":100,\"includeDetails\":true}");
            Assert.That(inlined["details"]?.Type, Is.EqualTo(JTokenType.Object));
        }

        /// <summary>
        /// A test job's hint must name <c>test_get_result</c> specifically. The generic fallback hint
        /// ("call again with includeDetails=true") isn't wrong, just wasteful, and actively bad advice
        /// for test runs: the dedicated skill returns parsed totals and failure detail, not a raw blob.
        /// </summary>
        [Test]
        public void JobStatus_ResultHint_NamesTheDedicatedResultSkillForTestJobs()
        {
            var jobId = CreateJob("completed", withResult: true, kind: "test");

            var payload = Payload("job_status", "{\"jobId\":\"" + jobId + "\"}");

            Assert.That(payload["resultHint"]?.ToString(), Does.Contain("test_get_result"),
                $"A test job should be pointed at its own result skill: {payload["resultHint"]}");
        }

        // ---------- test discovery: count / returned / truncated ----------

        /// <summary>
        /// The two skills split the same three numbers differently, and both splits are load-bearing
        /// because each field keeps its pre-2.7 meaning: <c>test_discover_get_result.count</c> has always
        /// meant "the discovered total," and <c>test_list.count</c> has always meant "the number returned
        /// this call." Unifying them would be tidier but would silently change what every existing caller reads, so the fix is to add the missing field on whichever side lacks it.
        /// </summary>
        [Test]
        public void TestDiscoverGetResult_UnderLimit_ReportsCountAsTotalAndReturnedAsThisPage()
        {
            var jobId = CreateDiscoveryJob(testCount: 5, testMode: "PlayMode");

            var payload = Payload("test_discover_get_result",
                "{\"jobId\":\"" + jobId + "\",\"limit\":2}");

            Assert.That(payload["count"]?.Value<int>(), Is.EqualTo(5),
                "count keeps its v1 meaning here: the pre-truncation total.");
            Assert.That(payload["returned"]?.Value<int>(), Is.EqualTo(2),
                "returned is the length of this response's array.");
            Assert.That((payload["tests"] as JArray)?.Count, Is.EqualTo(2),
                "returned must match the array it describes.");
            Assert.That(payload["truncated"]?.Value<bool>(), Is.True,
                "Without this flag a cut page is indistinguishable from the complete set.");
        }

        [Test]
        public void TestDiscoverGetResult_LimitAboveTotal_IsNotTruncated()
        {
            var jobId = CreateDiscoveryJob(testCount: 3, testMode: "PlayMode");

            var payload = Payload("test_discover_get_result",
                "{\"jobId\":\"" + jobId + "\",\"limit\":100}");

            Assert.That(payload["count"]?.Value<int>(), Is.EqualTo(3));
            Assert.That(payload["returned"]?.Value<int>(), Is.EqualTo(3));
            Assert.That(payload["truncated"]?.Value<bool>(), Is.False,
                "A page that holds everything must not claim to be cut.");
        }

        [Test]
        public void TestDiscoverGetResult_CountIsInvariantToLimit()
        {
            // The property that lets `count` serve as a "total": it must not move when the caller changes how much it asks for.
            var jobId = CreateDiscoveryJob(testCount: 6, testMode: "PlayMode");

            var narrow = Payload("test_discover_get_result", "{\"jobId\":\"" + jobId + "\",\"limit\":1}");
            var wide = Payload("test_discover_get_result", "{\"jobId\":\"" + jobId + "\",\"limit\":50}");

            Assert.That(narrow["count"]?.Value<int>(), Is.EqualTo(wide["count"]?.Value<int>()),
                "count is the discovered total, so limit must not change it.");
            Assert.That(narrow["returned"]?.Value<int>(), Is.LessThan(wide["returned"]?.Value<int>()),
                "returned is the page size, so limit must change it.");
        }

        [Test]
        public void TestList_ReportsCountAsThisPageAndTotalAsTheDiscoveredSet()
        {
            var jobId = CreateDiscoveryJob(testCount: 5, testMode: "PlayMode");

            var payload = Payload("test_list", "{\"testMode\":\"PlayMode\",\"limit\":2}");

            // If a completed PlayMode discovery job produced elsewhere with a newer timestamp exists,
            // it would win the lookup and this test would read someone else's data. Skip in that case rather than asserting on it.
            Assume.That(payload["total"]?.Value<int>(), Is.EqualTo(5),
                $"A different PlayMode discovery job won the lookup (job {jobId} not selected).");

            Assert.That(payload["count"]?.Value<int>(), Is.EqualTo(2),
                "count keeps its v1 meaning here: the number returned.");
            Assert.That((payload["tests"] as JArray)?.Count, Is.EqualTo(2));
            Assert.That(payload["truncated"]?.Value<bool>(), Is.True);
        }

        [Test]
        public void TestList_LimitAboveTotal_IsNotTruncated()
        {
            var jobId = CreateDiscoveryJob(testCount: 4, testMode: "PlayMode");

            var payload = Payload("test_list", "{\"testMode\":\"PlayMode\",\"limit\":100}");

            Assume.That(payload["total"]?.Value<int>(), Is.EqualTo(4),
                $"A different PlayMode discovery job won the lookup (job {jobId} not selected).");
            Assert.That(payload["count"]?.Value<int>(), Is.EqualTo(4));
            Assert.That(payload["truncated"]?.Value<bool>(), Is.False);
        }

        // ---------- scene_get_hierarchy: childCount ----------

        /// <summary>
        /// Without <c>childCount</c>, a node clipped by <c>maxDepth</c> produces the same JSON as a true
        /// leaf node: neither has <c>children</c>. An agent reading it concludes the subtree is empty
        /// and stops walking, silently reporting a deep hierarchy as flat.
        /// </summary>
        [Test]
        public void SceneGetHierarchy_ClippedNode_IsDistinguishableFromALeaf()
        {
            var root = new GameObject("__hier_root__");
            var child = new GameObject("__hier_child__");
            var grandchild = new GameObject("__hier_grandchild__");
            try
            {
                child.transform.SetParent(root.transform);
                grandchild.transform.SetParent(child.transform);
                GameObjectFinder.InvalidateCache();

                // maxDepth 1: the root's children are walked, but the children's children are not.
                var payload = Payload("scene_get_hierarchy", "{\"maxDepth\":1}");
                var rootNode = FindNode(payload["hierarchy"] as JArray, "__hier_root__");
                Assert.That(rootNode, Is.Not.Null, "Probe root missing from the hierarchy.");

                var childNode = FindNode(rootNode["children"] as JArray, "__hier_child__");
                Assert.That(childNode, Is.Not.Null, "The root's own children must be walked at maxDepth 1.");

                Assert.That(childNode["childCount"]?.Value<int>(), Is.EqualTo(1),
                    "childCount is the real child count regardless of how deep the walk went.");
                Assert.That(childNode["children"]?.Type ?? JTokenType.Null, Is.EqualTo(JTokenType.Null),
                    "At maxDepth 1 this node's children are not walked.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                GameObjectFinder.InvalidateCache();
            }
        }

        [Test]
        public void SceneGetHierarchy_TrueLeaf_ReportsZeroChildCount()
        {
            // The signal for "clipped" is `children==null && childCount>0`, so a true leaf must report 0 --
            // otherwise the two cases are still indistinguishable, just with the direction flipped.
            var leaf = new GameObject("__hier_leaf__");
            try
            {
                GameObjectFinder.InvalidateCache();

                var payload = Payload("scene_get_hierarchy", "{\"maxDepth\":3}");
                var node = FindNode(payload["hierarchy"] as JArray, "__hier_leaf__");

                Assert.That(node, Is.Not.Null);
                Assert.That(node["childCount"]?.Value<int>(), Is.EqualTo(0));
                Assert.That(node["children"]?.Type ?? JTokenType.Null, Is.EqualTo(JTokenType.Null),
                    "A childless node has no children array to emit.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(leaf);
                GameObjectFinder.InvalidateCache();
            }
        }

        [Test]
        public void SceneGetHierarchy_WalkedNode_ChildCountMatchesTheEmittedArray()
        {
            var root = new GameObject("__hier_two__");
            try
            {
                var a = new GameObject("__hier_a__");
                var b = new GameObject("__hier_b__");
                a.transform.SetParent(root.transform);
                b.transform.SetParent(root.transform);
                GameObjectFinder.InvalidateCache();

                var payload = Payload("scene_get_hierarchy", "{\"maxDepth\":5}");
                var node = FindNode(payload["hierarchy"] as JArray, "__hier_two__");

                Assert.That(node, Is.Not.Null);
                Assert.That(node["childCount"]?.Value<int>(), Is.EqualTo(2));
                Assert.That((node["children"] as JArray)?.Count, Is.EqualTo(2),
                    "When the walk does descend, childCount and the array must agree — a mismatch " +
                    "would make the clipping signal fire on a fully-walked node.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                GameObjectFinder.InvalidateCache();
            }
        }

        private static JObject FindNode(JArray nodes, string name)
        {
            if (nodes == null) return null;
            foreach (var node in nodes.OfType<JObject>())
            {
                if (node["name"]?.ToString() == name) return node;
                var found = FindNode(node["children"] as JArray, name);
                if (found != null) return found;
            }
            return null;
        }

        // ---------- material read skills: materialPath ----------

        /// <summary>
        /// A lookup by GameObject name resolves, via the renderer, to whichever material is actually
        /// attached to it -- not necessarily what the caller had in mind, and if shared, the same
        /// asset multiple objects point to. Echo the resolved path so the caller can confirm which <c>.mat</c> this describes before acting on it.
        /// </summary>
        [TestCase("material_get_properties")]
        [TestCase("material_get_keywords")]
        public void MaterialGetters_LookupByGameObject_EchoTheResolvedAssetPath(string skill)
        {
            Assume.That(SkillRouter.HasSkill(skill), Is.True);

            var assetPath = CreateMaterialAsset("__mat_echo__");
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                go.name = "__mat_owner__";
                go.GetComponent<MeshRenderer>().sharedMaterial =
                    AssetDatabase.LoadAssetAtPath<Material>(assetPath);
                GameObjectFinder.InvalidateCache();

                var payload = Payload(skill, "{\"name\":\"__mat_owner__\"}");

                Assert.That(payload["materialPath"]?.ToString(), Is.EqualTo(assetPath),
                    $"{skill} must report which .mat it inspected when reached through a GameObject.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }

        [TestCase("material_get_properties")]
        [TestCase("material_get_keywords")]
        public void MaterialGetters_LookupByPath_EchoTheSamePathBack(string skill)
        {
            Assume.That(SkillRouter.HasSkill(skill), Is.True);
            var assetPath = CreateMaterialAsset("__mat_direct__");

            var payload = Payload(skill, "{\"path\":" + JsonConvert.ToString(assetPath) + "}");

            Assert.That(payload["materialPath"]?.ToString(), Is.EqualTo(assetPath),
                "The echo has to hold for the direct lookup too, or callers cannot rely on it.");
        }

        [TestCase("material_get_properties")]
        [TestCase("material_get_keywords")]
        public void MaterialGetters_DeclareMaterialPathAmongTheirOutputs(string skill)
        {
            // agent plans off of Outputs; a field that only exists in the payload but isn't declared won't be looked for by anyone.
            Assume.That(SkillRouter.TryGetSkill(skill, out var info), Is.True);
            Assert.That(info.Outputs, Does.Contain("materialPath"));
        }

        // ---------- unknown values passed to ?category= / ?operation= ----------

        /// <summary>
        /// An unknown filter value used to come back as an "empty but successful" manifest, reading
        /// identically to "this category truly has zero skills at the current profile." So an agent
        /// that typos <c>?category=GameObjects</c> would conclude this module doesn't exist and stop looking.
        /// </summary>
        [TestCase("category", "validCategories")]
        [TestCase("operation", "validOperations")]
        public void UnknownNarrowingFilterValue_IsRejectedWithTheLegalVocabulary(string key, string vocabularyField)
        {
            var response = JObject.Parse(SkillRouter.GetFilteredManifest($"?{key}=NoSuchValue"));

            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"),
                $"A typo'd {key} must not come back as an empty success: {response.ToString(Formatting.None)}");
            Assert.That(response["retryStrategy"]?.ToString(), Is.EqualTo(SkillErrorResponse.RetryFixAndRetry));

            var vocabulary = response["details"]?[vocabularyField] as JArray;
            Assert.That(vocabulary, Is.Not.Null.And.Not.Empty,
                $"The rejection must hand back {vocabularyField} so the caller can fix it in one retry.");
            Assert.That(response["details"]?["parameter"]?.ToString(), Is.EqualTo(key));
            Assert.That(response["details"]?["value"]?.ToString(), Is.EqualTo("NoSuchValue"),
                "Echoing the offending value is what makes the error readable in a log.");
        }

        [Test]
        public void ValidCategoryVocabulary_MatchesTheEnumItDescribes()
        {
            var advertised = (JObject.Parse(SkillRouter.GetFilteredManifest("?category=NoSuchValue"))
                ["details"]?["validCategories"] as JArray)?.Select(t => t.ToString()).ToArray();

            Assert.That(advertised, Is.EqualTo(Enum.GetNames(typeof(SkillCategory))),
                "Advertising a vocabulary that does not match the enum sends the caller to a value " +
                "that will be rejected on the next attempt too.");
        }

        /// <summary>
        /// category/operation are narrowing query keys, so an unvalidated garbage value reaches the
        /// keyed cache layer and mints -- and then permanently occupies -- a manifest-sized cache
        /// entry keyed on the typo. An agent that keeps retrying the same misspelling is effectively a memory leak.
        /// </summary>
        [TestCase("?category=NoSuchCategoryForTests")]
        [TestCase("?operation=NoSuchOperationForTests")]
        public void RejectedFilterValue_MintsNoCacheEntry(string query)
        {
            SkillRouter.GetFilteredManifest(query);

            Assert.That(SkillRouter.TryGetCachedGetResponse("/skills", query, out _, out _), Is.False,
                $"'{query}' left a cache entry behind; a typo must not buy permanent residency.");
        }

        [Test]
        public void LegalFilterValues_AreStillAccepted_AndStillCached()
        {
            // This rejection logic must not narrow what counts as legal. Still case-insensitive, and
            // still cacheable -- the guard runs before the cache, so a mistake there would strip caching from every scoped request.
            var category = SkillRouter.GetAllSkillsSnapshot()
                .Where(s => s.Category != SkillCategory.Uncategorized)
                .GroupBy(s => s.Category)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key.ToString())
                .FirstOrDefault();
            Assume.That(category, Is.Not.Null, "No categorized skills in the registry.");

            var response = JObject.Parse(SkillRouter.GetFilteredManifest($"?category={category}"));
            Assert.That(response["errorCode"], Is.Null, response.ToString(Formatting.None));

            Assert.That(SkillRouter.GetFilteredManifest($"?category={category.ToLowerInvariant()}"),
                Is.Not.Null.And.Not.Empty);
            Assert.That(JObject.Parse(SkillRouter.GetFilteredManifest($"?category={category.ToLowerInvariant()}"))["errorCode"],
                Is.Null, "Filter values have always been case-insensitive; the guard must not change that.");

            Assert.That(SkillRouter.TryGetCachedGetResponse("/skills", $"?category={category}", out _, out _),
                Is.True, "A legal scoped request must still be cached.");
        }

        // ---------- rejection responses must not carry an ETag ----------

        /// <summary>
        /// Pushes <see cref="RejectedFilterValue_MintsNoCacheEntry"/> out one more layer. It's not enough
        /// for SkillRouter to keep a typo out of its own output cache: the rejection body is assembled by
        /// the HTTP handler itself, and once handed to the ETag helper, the rejection gets a content hash.
        /// The client stores it, sends <c>If-None-Match</c> next time, and gets a bodyless 304 back -- the
        /// error text is gone, the typo'd query looks accepted, and the typo squats the ETag cache permanently, so a client that keeps retrying the same misspelling crowds out entries that actually matter.
        ///
        /// <para>Drives the real handler via reflection rather than restating its decision branch in the
        /// test -- that branch is the thing under test, and a copy would still be green after it's deleted.</para>
        /// </summary>
        [TestCase("/skills", "?category=NoSuchCategoryForEtagTests")]
        [TestCase("/skills", "?operation=NoSuchOperationForEtagTests")]
        [TestCase("/skills/schema", "?category=NoSuchCategoryForEtagTests")]
        [TestCase("/skills/schema", "?operation=NoSuchOperationForEtagTests")]
        public void ServerHandler_RejectedFilterValue_Answers400WithNoETag(string path, string query)
        {
            var keysBefore = EtagCacheKeys();

            var (statusCode, etag, body) = ProcessGetOnMainThread(path, query);

            Assert.That(statusCode, Is.EqualTo(400),
                $"GET {path}{query} must be a rejection, not a manifest: {body}");
            Assert.That(JObject.Parse(body)["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), body);
            Assert.That(etag, Is.Null,
                "An error body was given an ETag. The client caches it, the next If-None-Match " +
                "matches, and the rejection comes back as an empty 304 — the query starts looking " +
                $"accepted. ETag={etag}");
            Assert.That(EtagCacheKeys(), Is.EquivalentTo(keysBefore),
                "The rejected query minted an ETag cache entry keyed on the typo, which is permanent " +
                "residency for a misspelling: " +
                string.Join(", ", EtagCacheKeys().Except(keysBefore)));
        }

        [TestCase("/skills")]
        [TestCase("/skills/schema")]
        public void ServerHandler_AcceptedRequest_IsStillETagged(string path)
        {
            // Without this, the assertion above would be satisfied by a handler that simply stopped
            // ETagging altogether -- which would turn every conditional GET into a full transfer.
            var (statusCode, etag, body) = ProcessGetOnMainThread(path, "");

            Assert.That(statusCode, Is.EqualTo(200), body);
            Assert.That(etag, Is.Not.Null.And.Not.Empty,
                $"GET {path} carries no ETag, so no client can ever get a 304 for it.");
        }

        // ---------- the same URL can only have one answer, warm or cold ----------

        /// <summary>
        /// <c>?brief=1</c> selects a path that never consults the keyed cache -- it hands back a
        /// pre-built brief directly -- so unless the HTTP-thread fast path also reruns the narrowing
        /// filter check, <c>?brief=1&amp;category=Bogus</c> returns a 200 brief when the cache is warm
        /// and a rejection only when cold. One URL with two answers is worse than either alone: an agent retrying the same typo eventually hits "accepted," and which answer it gets depends on something it can't observe.
        /// </summary>
        [Test]
        public void BriefSurface_WithABogusNarrowingFilter_IsRejectedWarmAndCold()
        {
            // Warm it up first, or the fast path bails for the mundane reason that "nothing has been
            // built yet," and the assertion below would prove nothing about filter validation.
            SkillRouter.GetFilteredManifest("?brief=1");
            Assume.That(SkillRouter.TryGetCachedGetResponse("/skills", "?brief=1", out _, out _), Is.True,
                "The brief cache did not warm, so the fast path is not being exercised.");

            Assert.That(
                SkillRouter.TryGetCachedGetResponse("/skills", "?brief=1&category=NoSuchCategoryForTests", out _, out _),
                Is.False,
                "The fast path served a bogus ?category from the brief cache. That surface does not go " +
                "through the keyed cache, so it is only the fast path's own filter check standing " +
                "between a typo and a 200 catalogue.");

            var (statusCode, etag, body) = ProcessGetOnMainThread("/skills", "?brief=1&category=NoSuchCategoryForTests");
            Assert.That(statusCode, Is.EqualTo(400),
                $"The slow path must reject the same URL the fast path declined: {body}");
            Assert.That(etag, Is.Null, "A rejection must not be ETagged.");
        }

        [Test]
        public void BriefSurface_AnswersTheSameBytesWarmAndCold()
        {
            // The other half: both paths must also agree in the "accepted" case, or a client that
            // happens to hit a cold editor gets different bytes -- and a different ETag -- for a URL it already has cached.
            var slowPath = SkillRouter.GetFilteredManifest("?brief=1");

            Assume.That(SkillRouter.TryGetCachedGetResponse("/skills", "?brief=1", out var fastPath, out _), Is.True);
            Assert.That(fastPath, Is.EqualTo(slowPath),
                "The brief catalogue differs between the HTTP-thread fast path and the main-thread " +
                "build, so the same URL answers differently depending on cache state.");
        }

        // ---------- helpers ----------

        /// <summary>
        /// Drives a GET through the real main-thread handler <c>SkillsHttpServer.ProcessJob</c> and
        /// returns its verdict. Reflection is the only way in: both the job type and the method are
        /// private, and the alternative -- asserting against a reimplementation of the handler's branch -- would not actually be testing the handler.
        /// </summary>
        private static (int StatusCode, string ETag, string ResponseJson) ProcessGetOnMainThread(
            string path, string query)
        {
            var jobType = typeof(SkillsHttpServer).GetNestedType("RequestJob", BindingFlags.NonPublic);
            Assert.That(jobType, Is.Not.Null,
                "SkillsHttpServer.RequestJob was renamed; this test drives the real handler and needs it.");

            var job = Activator.CreateInstance(jobType, nonPublic: true);
            SetJobField(jobType, job, "HttpMethod", "GET");
            SetJobField(jobType, job, "Path", path);
            SetJobField(jobType, job, "QueryString", query);
            SetJobField(jobType, job, "StatusCode", 200);

            var processJob = typeof(SkillsHttpServer).GetMethod(
                "ProcessJob", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(processJob, Is.Not.Null, "SkillsHttpServer.ProcessJob was renamed.");
            processJob.Invoke(null, new[] { job });

            return (
                (int)GetJobField(jobType, job, "StatusCode"),
                (string)GetJobField(jobType, job, "ETag"),
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

        /// <summary>Keys currently resident in SkillRouter's ETag cache.</summary>
        private static string[] EtagCacheKeys()
        {
            var field = typeof(SkillRouter).GetField("_etagCache", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null, "SkillRouter._etagCache was renamed.");

            var cache = field.GetValue(null) as IDictionary;
            Assert.That(cache, Is.Not.Null, "SkillRouter._etagCache is no longer enumerable as a dictionary.");

            return cache.Keys.Cast<object>().Select(k => k.ToString())
                .OrderBy(k => k, StringComparer.Ordinal).ToArray();
        }

        private string CreateJob(string status, bool withResult, string kind = "test")
        {
            var job = new BatchJobRecord
            {
                jobId = "test_shape_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                kind = kind,
                status = status,
                currentStage = status,
                startedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                totalItems = 1,
                processedItems = status == "completed" ? 1 : 0,
                progress = status == "completed" ? 100 : 0,
            };

            if (withResult)
            {
                job.resultData = new Dictionary<string, object>
                {
                    ["totalTests"] = 7,
                    ["passedTests"] = 6,
                    ["failedTests"] = 1,
                };
            }
            else
            {
                job.resultData = new Dictionary<string, object>();
            }

            BatchPersistence.UpsertJob(job);
            _createdJobs.Add(job.jobId);
            return job.jobId;
        }

        private string CreateCompletedJobWithResult() => CreateJob("completed", withResult: true);

        /// <summary>
        /// Builds a completed discovery job holding <paramref name="testCount"/> synthetic test cases,
        /// written straight to the persistence layer to skip triggering real (asynchronous) Unity Test Runner discovery, which would make these assertions depend on the host project.
        /// </summary>
        private string CreateDiscoveryJob(int testCount, string testMode)
        {
            var tests = new List<object>();
            for (int i = 0; i < testCount; i++)
            {
                tests.Add(new JObject
                {
                    ["name"] = $"Probe{i:D3}",
                    ["fullName"] = $"UnitySkills.Synthetic.Probe{i:D3}",
                    ["runState"] = "Runnable",
                    ["categories"] = new JArray(),
                });
            }

            var job = new BatchJobRecord
            {
                jobId = "test_discovery_probe_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                kind = TestDiscoveryJobKind,
                status = "completed",
                currentStage = "completed",
                progress = 100,
                startedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                totalItems = testCount,
                processedItems = testCount,
                metadata = new Dictionary<string, object> { ["testMode"] = testMode },
                resultData = new Dictionary<string, object> { ["tests"] = tests },
            };

            BatchPersistence.UpsertJob(job);
            _createdJobs.Add(job.jobId);
            return job.jobId;
        }

        private string CreateMaterialAsset(string name)
        {
            // The CI fixture project has Assets/Temp; a fresh project doesn't, so create it.
            if (!AssetDatabase.IsValidFolder("Assets/Temp"))
                AssetDatabase.CreateFolder("Assets", "Temp");

            var path = AssetDatabase.GenerateUniqueAssetPath($"Assets/Temp/{name}.mat");
            var shader = Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit");
            Assume.That(shader, Is.Not.Null, "No usable built-in shader found for the probe material.");

            AssetDatabase.CreateAsset(new Material(shader), path);
            AssetDatabase.SaveAssets();
            _createdAssets.Add(path);
            return path;
        }
    }
}

// Producer:Betsy
