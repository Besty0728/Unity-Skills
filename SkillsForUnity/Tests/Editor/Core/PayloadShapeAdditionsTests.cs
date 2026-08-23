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
    /// 为了让"被截断/不完整的回答"变得*可察觉*而新增的那些响应字段。
    ///
    /// <para>它们防的是同一类故障：一份看起来完整、实则不完整的载荷。某个节点因为遍历触到深度上限而
    /// 没有 <c>children</c> 数组，读起来和叶子节点一模一样。被 <c>limit</c> 截断的 <c>tests</c> 数组，
    /// 读起来和完整集合一模一样。因体积过大而省略了 result 的轮询作业，读起来和"什么都没产出"的作业
    /// 一模一样。这三种情况下调用方的下一步动作都是错的，而响应里没有任何东西提示它——所以这里每个
    /// 字段都与它所消歧的那个值一起断言，绝不单独断言。</para>
    ///
    /// <para>数量一律不硬编码：注册表规模随已安装的可选包变动，发现到的测试数随工程变动。
    /// 所有数字都在运行期推导，或由测试自己合成。</para>
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

        // ---------- job_status / job_wait：默认省略 result ----------

        /// <summary>
        /// 这两个端点是给反复轮询用的，而一个已完成的测试或编译作业，其 result 载荷远大于包裹它的
        /// 状态信封。每次轮询都内联它是昂贵的默认行为，但直接丢掉更糟：调用方无法区分"没有 result"
        /// 与"result 被扣下了"。<c>resultAvailable</c> 负责区分这两者，<c>resultHint</c> 负责让回答可执行。
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

            // 键保留、值为 null。整个键消失就与"旧版本从来没有这个键"无法区分，
            // 客户端于是分不清"被扣下"和"不支持"。
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
            // 反例才让 resultAvailable 有意义——一个恒为 true 的实现也能满足上面那条断言。
            var jobId = CreateJob("running", withResult: false);

            var payload = Payload("job_status", "{\"jobId\":\"" + jobId + "\"}");

            Assert.That(payload["resultAvailable"]?.Value<bool>(), Is.False);
            Assert.That(payload["details"]?.Type ?? JTokenType.Null, Is.EqualTo(JTokenType.Null));
        }

        [Test]
        public void JobWait_FollowsTheSameOmissionContract()
        {
            // 同样两个字段、同样的默认值。这两者曾经跑偏过一次；用 job_wait 轮询后又切到 job_status 的
            // 调用方，不该被迫重新学一遍响应形状。
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
        /// 测试类作业的 hint 必须点名 <c>test_get_result</c>。通用兜底提示（"带 includeDetails=true
        /// 再调一次"）虽然没错但浪费，而且对测试运行来说是错的建议：专用技能返回的是解析好的总数与
        /// 失败详情，而不是原始大块数据。
        /// </summary>
        [Test]
        public void JobStatus_ResultHint_NamesTheDedicatedResultSkillForTestJobs()
        {
            var jobId = CreateJob("completed", withResult: true, kind: "test");

            var payload = Payload("job_status", "{\"jobId\":\"" + jobId + "\"}");

            Assert.That(payload["resultHint"]?.ToString(), Does.Contain("test_get_result"),
                $"A test job should be pointed at its own result skill: {payload["resultHint"]}");
        }

        // ---------- 测试发现：count / returned / truncated ----------

        /// <summary>
        /// 两个技能把同样三个数字拆分得不一样，而两种拆法都是承重的，因为每个字段都保持了它 2.7 之前的
        /// 含义：<c>test_discover_get_result.count</c> 一直是"发现到的总数"，<c>test_list.count</c> 一直是
        /// "本次返回的条数"。把它们统一固然更整洁，但也会悄无声息地改变每一个既有调用方读到的东西，
        /// 所以做法是在缺字段的那一侧补上新字段。
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
            // 让 `count` 能当"总数"用的那条性质：调用方改变索取的数量时，它不得跟着变。
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

            // 若存在时间戳更新的、别处产生的已完成 PlayMode 发现作业，它会在查找中胜出，
            // 让本测试读到别人的数据。这种情况选择跳过，而不是对它做断言。
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

        // ---------- scene_get_hierarchy：childCount ----------

        /// <summary>
        /// 没有 <c>childCount</c> 时，被 <c>maxDepth</c> 剪掉的节点与真正的叶子节点产出同样的 JSON：
        /// 都没有 <c>children</c>。agent 读到就会断定子树为空并停止遍历，一棵很深的层级于是被
        /// 无声无息地汇报成扁平的。
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

                // maxDepth 1：根的子节点会被遍历，而子节点的子节点不会。
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
            // 判定"被剪断"的信号是 `children==null && childCount>0`，所以真叶子必须报 0——
            // 否则两种情况依然无法区分，只是反了个方向。
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

        // ---------- material 读取类技能：materialPath ----------

        /// <summary>
        /// 按 GameObject 名查找，会经渲染器解析到它身上实际挂着的那个材质——那未必是调用方心里想的
        /// 那一个，而若是共享材质，则是多个对象共同指向的同一份资源。把解析出的路径回显出来，
        /// 才能让调用方在动手改之前确认这个回答描述的是哪个 <c>.mat</c>。
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
            // agent 是照 Outputs 做计划的；一个只存在于载荷、却没在声明里出现的字段，没人会去找它。
            Assume.That(SkillRouter.TryGetSkill(skill, out var info), Is.True);
            Assert.That(info.Outputs, Does.Contain("materialPath"));
        }

        // ---------- ?category= / ?operation= 传入未知值 ----------

        /// <summary>
        /// 未知的过滤值过去会返回一个"空但成功"的 manifest，而这与"当前档位下该类目确实一个技能都没有"
        /// 读起来完全一样。于是打错成 <c>?category=GameObjects</c> 的 agent 会断定这个模块在本工程里
        /// 不存在，就不再找了。
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
        /// category/operation 属于收窄类查询键，因此一个未经校验的乱值会一路到达带键缓存层，
        /// 按这个拼写错误铸出——并从此长期占住——一条 manifest 级大小的缓存条目。
        /// 一个 agent 反复重试同一个错拼，就等于一处内存泄漏。
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
            // 这道拒绝逻辑不得把"什么算合法"收窄了。大小写仍不敏感，且仍然可缓存——守卫跑在缓存之前，
            // 那里一旦写错，就会让所有带范围的请求全部失去缓存。
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

        // ---------- 拒绝响应不得带 ETag ----------

        /// <summary>
        /// 把 <see cref="RejectedFilterValue_MintsNoCacheEntry"/> 往外推一层。SkillRouter 把错拼挡在自己的
        /// 输出缓存之外还不够：拒绝响应的 body 是 HTTP 处理器自己拼的，一旦它把这个 body 交给 ETag 辅助
        /// 函数，这条拒绝就获得了内容哈希。客户端存下它，下次发同样请求时带上 <c>If-None-Match</c>，
        /// 拿回一个无 body 的 304——此时错误文本已经消失，那个乱值查询看起来像是被接受了。
        /// 而且这个错拼还会长期占住 ETag 缓存，反复重试错拼的客户端会把真正有用的条目挤掉。
        ///
        /// <para>这里用反射驱动真实处理器，而不是在测试里复述它的判断分支——因为那个分支正是被测对象，
        /// 条件的副本在处理器把它删掉之后依然会是绿的。</para>
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
            // 没有这条，上面那个断言会被一个"干脆什么都不再打 ETag"的处理器满足，
            // 而那会把每一次条件 GET 都变成一次全量传输。
            var (statusCode, etag, body) = ProcessGetOnMainThread(path, "");

            Assert.That(statusCode, Is.EqualTo(200), body);
            Assert.That(etag, Is.Not.Null.And.Not.Empty,
                $"GET {path} carries no ETag, so no client can ever get a 304 for it.");
        }

        // ---------- 同一个 URL，冷热都只能有一个答案 ----------

        /// <summary>
        /// <c>?brief=1</c> 选中的是一条从不查带键缓存的路径——它直接交回预先构建好的简表——所以除非
        /// HTTP 线程上的快路径自己也重跑一遍收窄过滤校验，<c>?brief=1&amp;category=Bogus</c> 在缓存热时
        /// 会回 200 简表、缓存冷时才回拒绝。一个 URL 两个答案比其中任何一个单独存在都更糟：
        /// 反复重试错拼的 agent 迟早会撞上"被接受"，而它拿到哪个答案取决于它观察不到的东西。
        /// </summary>
        [Test]
        public void BriefSurface_WithABogusNarrowingFilter_IsRejectedWarmAndCold()
        {
            // 先预热，否则快路径只是因为"还什么都没构建"这种平庸理由而放弃，
            // 下面的断言就证明不了任何与过滤校验有关的事。
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
            // 另一半：两条路径在"被接受"的情形下也必须一致，否则恰好撞上冷编辑器的客户端，
            // 会为一个它已缓存的 URL 拿到不同的字节——以及不同的 ETag。
            var slowPath = SkillRouter.GetFilteredManifest("?brief=1");

            Assume.That(SkillRouter.TryGetCachedGetResponse("/skills", "?brief=1", out var fastPath, out _), Is.True);
            Assert.That(fastPath, Is.EqualTo(slowPath),
                "The brief catalogue differs between the HTTP-thread fast path and the main-thread " +
                "build, so the same URL answers differently depending on cache state.");
        }

        // ---------- helpers ----------

        /// <summary>
        /// 让一次 GET 走真正的主线程处理器 <c>SkillsHttpServer.ProcessJob</c>，并返回它的判定结果。
        /// 只能靠反射进去：job 类型与方法都是 private，而另一条路（对着处理器分支的复刻实现做断言）
        /// 根本没在测处理器。
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

        /// <summary>当前驻留在 SkillRouter ETag 缓存里的键。</summary>
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
        /// 造一个已完成的发现作业，内含 <paramref name="testCount"/> 个合成用例。直接写进持久层，
        /// 免得触发真正的 Unity Test Runner 发现——那是异步的，会让这些断言依赖宿主工程。
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
            // CI 夹具工程里有 Assets/Temp；若是全新工程则现建一个。
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
