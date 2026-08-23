using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// <see cref="SkillMetadataGuardTests"/> 的行为侧另一半。
    ///
    /// <para>那个文件里的断言全是拿一个特性比另一个特性——ReadOnly 比 MutatesScene、批量版声明比
    /// 它的单体孪生版。这只能抓出"自相矛盾"，别的一概抓不到：声明与*代码*矛盾时它全盲。
    /// <c>Outputs</c> 可以写着响应从不携带的键，<c>ReadOnly</c> 可以扣在一个会写的技能头上，
    /// 而两个文件照样全绿。补上这个缺口，必须真的执行技能并读回答。</para>
    ///
    /// <para>取代表性样本而非全量扫描：每个在运行期真正起闸门作用的声明各挑一个技能
    /// （ReadOnly、MutatesScene、MutatesAssets）、一个信封形态的批量技能，以及本轮重新规定了
    /// <c>applied</c> 回显的枚举 setter 之一。全注册表版本需要为几百个技能各自准备夹具数据，
    /// 最后只会被禁用而不是被维护，那比一个真的在跑的小样本更糟。</para>
    ///
    /// <para>这里主张两件事。<c>Outputs</c> 必须是响应实际携带键的子集，因为 agent 是照 Outputs
    /// 做计划的——声明了却从不到达的键，会让调用方为一个被承诺过的值多跑一次往返。以及 ReadOnly
    /// 技能必须什么都不留下，这正是档位机制赖以成立的前提：没有任何档位会撤下只读技能，
    /// 所以一个挂着该标记的写操作，在所有档位下都藏不住。</para>
    /// </summary>
    [TestFixture]
    public class SkillMetadataBehaviorTests
    {
        private const string ProbeParent = "__behavior_probe_parent__";
        private const string ProbeChild = "__behavior_probe_child__";
        private const string ProbeFolder = "Assets/__UnitySkillsBehaviorProbe__";
        private const string ProbeMaterialPath = ProbeFolder + "/probe.mat";

        /// <summary>只读样本，请求体里点名夹具建好的那几个探针对象。</summary>
        private static readonly (string Skill, string Body)[] ReadOnlyProbes =
        {
            ("gameobject_get_info", "{\"name\":\"" + ProbeChild + "\"}"),
            ("component_list", "{\"name\":\"" + ProbeChild + "\"}"),
            ("material_get_properties", "{\"path\":\"" + ProbeMaterialPath + "\"}"),
        };

        private SkillsOperatingMode _savedMode;
        private SurfaceProfileKind _savedProfile;
        private readonly List<string> _createdAssetPaths = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _savedMode = SkillsModeManager.CurrentMode;
            _savedProfile = SkillsSurfaceProfile.Current;
            // 非 Bypass 模式下写操作会被拒，非 full 档位又会撤掉写类目。两者都存在全局 EditorPrefs 里，
            // 所以都显式钉住再恢复，绝不假设。
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var path in _createdAssetPaths)
                AssetDatabase.DeleteAsset(path);
            _createdAssetPaths.Clear();

            if (AssetDatabase.IsValidFolder(ProbeFolder))
                AssetDatabase.DeleteAsset(ProbeFolder);

            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
            GameObjectFinder.InvalidateCache();
        }

        // ---------- Outputs 必须只写响应真的携带的键 ----------

        /// <summary>
        /// ReadOnly 的代表，也是 <c>SkillMetadataGuardTests.GameObjectGetInfo_DeclaresAllFifteenOutputs</c>
        /// 逐个点名断言 Outputs 的那个技能。那条测试钉住"特性怎么说"，这条钉住"响应与之一致"。
        /// </summary>
        [Test]
        public void GameObjectGetInfo_ResponseCarriesEveryDeclaredOutput()
        {
            CreateProbeHierarchy();

            AssertResponseCoversDeclaredOutputs("gameobject_get_info", "{\"name\":\"" + ProbeChild + "\"}");
        }

        [Test]
        public void ComponentList_ResponseCarriesEveryDeclaredOutput()
        {
            CreateProbeHierarchy();

            AssertResponseCoversDeclaredOutputs("component_list", "{\"name\":\"" + ProbeChild + "\"}");
        }

        /// <summary>MutatesScene 的代表。</summary>
        [Test]
        public void GameObjectCreate_ResponseCarriesEveryDeclaredOutput()
        {
            Assume.That(SkillRouter.TryGetSkill("gameobject_create", out var declared), Is.True);
            Assume.That(declared.MutatesScene, Is.True,
                "Chosen as the MutatesScene representative; if it stops declaring that, pick another skill.");

            AssertResponseCoversDeclaredOutputs("gameobject_create",
                "{\"name\":\"" + ProbeChild + "\",\"primitiveType\":\"Cube\"}");

            Assert.That(FindProbe(ProbeChild), Is.Not.Null,
                "gameobject_create answered success without putting anything in the scene.");
        }

        /// <summary>MutatesAssets 的代表。</summary>
        [Test]
        public void MaterialCreate_ResponseCarriesEveryDeclaredOutput()
        {
            Assume.That(SkillRouter.TryGetSkill("material_create", out var declared), Is.True);
            Assume.That(declared.MutatesAssets, Is.True,
                "Chosen as the MutatesAssets representative; if it stops declaring that, pick another skill.");
            EnsureProbeFolder();

            var payload = AssertResponseCoversDeclaredOutputs("material_create",
                "{\"name\":\"behavior_probe_mat\",\"savePath\":\"" + ProbeFolder + "\"}");

            // `path` 是调用方拿到刚创建资源的唯一把手，所以它必须是一个真能加载的路径——
            // 一个解析不到东西的回显，会让调用方连自己的新材质都寻址不到。
            var createdPath = payload["path"]?.ToString();
            Assert.That(createdPath, Is.Not.Null.And.Not.Empty);
            _createdAssetPaths.Add(createdPath);
            Assert.That(AssetDatabase.LoadAssetAtPath<Material>(createdPath), Is.Not.Null,
                $"material_create reported path '{createdPath}' but nothing loads from it.");
        }

        /// <summary>
        /// 本轮重新规定过的枚举 setter 之一：<c>shadows</c> 现在是被拒而不是静默丢弃，
        /// <c>applied</c>/<c>skipped</c> 按参数逐项汇报。这两个都是声明过的 Outputs，
        /// 本测试抓的就是响应哪天不再携带它们。
        /// </summary>
        [Test]
        public void LightSetProperties_ResponseCarriesEveryDeclaredOutput()
        {
            var go = new GameObject(ProbeChild, typeof(Light));
            try
            {
                go.GetComponent<Light>().type = LightType.Spot;
                GameObjectFinder.InvalidateCache();

                var payload = AssertResponseCoversDeclaredOutputs("light_set_properties",
                    "{\"name\":\"" + ProbeChild + "\",\"intensity\":2,\"shadows\":\"Soft\"}");

                Assert.That(payload["applied"]?.Type, Is.EqualTo(JTokenType.Array),
                    "`applied` is declared, and it has to be a list the caller can read parameter names out of.");
                Assert.That(go.GetComponent<Light>().shadows, Is.EqualTo(LightShadows.Soft),
                    "The enum reached the response but not the light.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }

        /// <summary>
        /// 信封形态的批量技能。逐项结果放在 <c>results</c> 下，所以这里要紧的是信封自己的那四个键——
        /// 批量响应一旦丢了 <c>failCount</c>，调用方除了遍历每一项，就没别的办法区分"部分失败"和
        /// "全部成功"。
        /// </summary>
        [Test]
        public void LightSetEnabledBatch_ResponseCarriesTheEnvelopeKeys()
        {
            var go = new GameObject(ProbeChild, typeof(Light));
            try
            {
                GameObjectFinder.InvalidateCache();

                var payload = AssertResponseCoversDeclaredOutputs("light_set_enabled_batch",
                    "{\"items\":[{\"name\":\"" + ProbeChild + "\",\"enabled\":false}]}");

                foreach (var key in new[] { "totalItems", "successCount", "failCount", "results" })
                {
                    Assert.That(payload[key], Is.Not.Null,
                        $"The batch envelope is missing '{key}'. Without the counts a caller cannot " +
                        "detect a partially-failed batch without walking every item: " +
                        payload.ToString(Formatting.None));
                }

                Assert.That(payload["totalItems"]?.Value<int>(), Is.EqualTo(1));
                Assert.That(go.GetComponent<Light>().enabled, Is.False,
                    "The batch envelope reported on a write that did not happen.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }

        // ---------- ReadOnly 必须名副其实 ----------

        /// <summary>
        /// <c>ReadOnly</c> 是唯一没有档位能覆盖的声明，所以一个挂着它却动了工程的技能，
        /// 按设计就是藏不住的。这里拿工程本身来核，而不是拿另一个特性来核：场景里的对象、
        /// 磁盘上的资源路径，以及经 <c>SaveAssets</c> 落盘后探针材质的字节——最后这项专门抓
        /// "只改了内存、还没落盘"的修改，因为任何人下一次保存都会把它提交。
        /// </summary>
        [Test]
        public void EveryReadOnlyProbe_LeavesTheSceneAndAssetDatabaseAsItFoundThem()
        {
            CreateProbeHierarchy();
            CreateProbeMaterial();

            foreach (var (skill, body) in ReadOnlyProbes)
            {
                Assume.That(SkillRouter.TryGetSkill(skill, out var declared), Is.True, $"{skill} is not registered.");
                Assume.That(declared.ReadOnly, Is.True,
                    $"{skill} is in the read-only sample but no longer declares ReadOnly.");

                var objectsBefore = SceneObjectNames();
                // 用 HashSet 而非 NUnit 的 EquivalentTo：算上本包，工程里有几千条资源路径，
                // 而 EquivalentTo 是两两比对。
                var assetsBefore = new HashSet<string>(AssetDatabase.GetAllAssetPaths(), StringComparer.Ordinal);
                var materialBefore = ReadProbeMaterialBytes();

                var response = JObject.Parse(SkillRouter.Execute(skill, body));
                Assert.That(response["errorCode"], Is.Null,
                    $"{skill} failed, so it never got far enough to prove anything: {response.ToString(Formatting.None)}");

                Assert.That(SceneObjectNames(), Is.EquivalentTo(objectsBefore),
                    $"{skill} declares ReadOnly but the scene's object set changed. No surface profile " +
                    "withdraws a read-only skill, so this write stays reachable under every one of them.");

                var assetsAfter = AssetDatabase.GetAllAssetPaths();
                var appeared = assetsAfter.Where(p => !assetsBefore.Contains(p)).ToArray();
                Assert.That(appeared, Is.Empty,
                    $"{skill} declares ReadOnly but created asset(s): {string.Join(", ", appeared.Take(10))}");
                Assert.That(assetsAfter.Length, Is.EqualTo(assetsBefore.Count),
                    $"{skill} declares ReadOnly but the asset count changed, so it deleted something.");

                Assert.That(ReadProbeMaterialBytes(), Is.EqualTo(materialBefore),
                    $"{skill} declares ReadOnly but modified the probe material it was asked to read.");
            }
        }

        // ---------- helpers ----------

        /// <summary>
        /// 执行 <paramref name="skill"/> 并断言每个声明的 output 都对应载荷里真实存在的键，
        /// 同时把载荷交回给调用方做技能专属的后续检查。
        /// </summary>
        private static JObject AssertResponseCoversDeclaredOutputs(string skill, string body)
        {
            Assume.That(SkillRouter.TryGetSkill(skill, out var declared), Is.True, $"{skill} is not registered.");
            Assume.That(declared.Outputs, Is.Not.Null.And.Not.Empty, $"{skill} declares no Outputs.");

            var response = JObject.Parse(SkillRouter.Execute(skill, body));
            Assert.That(response["errorCode"], Is.Null,
                $"{skill} failed: {response.ToString(Formatting.None)}");

            var payload = response["result"] as JObject;
            Assert.That(payload, Is.Not.Null,
                "Success envelope shape changed — expected the skill payload under 'result'. Top-level keys: " +
                string.Join(", ", response.Properties().Select(p => p.Name)));

            var missing = declared.Outputs.Where(key => payload[key] == null).ToArray();
            Assert.That(missing, Is.Empty,
                $"{skill} declares outputs its response does not carry: {string.Join(", ", missing)}. " +
                "Outputs is what an agent plans against, so every missing key is a follow-up call for a " +
                "value the caller was told to expect.\nPayload keys: " +
                string.Join(", ", payload.Properties().Select(p => p.Name)));

            return payload;
        }

        private static void CreateProbeHierarchy()
        {
            var parent = new GameObject(ProbeParent);
            // 故意挂了父节点：`parent` 与 `parentPath` 都是声明过的 output，而在根对象上两者都返回
            // null——一旦调用方开始判空，这与"载荷根本没带这两个键"就无法区分了。
            var child = new GameObject(ProbeChild, typeof(BoxCollider));
            child.transform.SetParent(parent.transform, false);
            GameObjectFinder.InvalidateCache();
        }

        private void CreateProbeMaterial()
        {
            EnsureProbeFolder();

            var shaderName = ProjectSkills.GetDefaultShaderName();
            var shader = Shader.Find(shaderName);
            Assume.That(shader, Is.Not.Null, $"The project's default shader '{shaderName}' did not resolve.");

            AssetDatabase.CreateAsset(new Material(shader), ProbeMaterialPath);
            AssetDatabase.SaveAssets();
            _createdAssetPaths.Add(ProbeMaterialPath);

            Assume.That(File.Exists(ProbeMaterialPath), Is.True, "Probe material was not written to disk.");
        }

        /// <summary>
        /// 先把待写内容刷盘，再读探针材质的磁盘字节。走 <c>SaveAssets</c> 正是关键：只把内存对象
        /// 标脏的修改也会在这里显形，而那恰恰是"只读"技能本可以一直隐形、直到别处一次保存
        /// 才暴露的那种写。
        /// </summary>
        private static byte[] ReadProbeMaterialBytes()
        {
            AssetDatabase.SaveAssets();
            return File.ReadAllBytes(ProbeMaterialPath);
        }

        private static void EnsureProbeFolder()
        {
            if (!AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.CreateFolder("Assets", ProbeFolder.Substring("Assets/".Length));
                AssetDatabase.Refresh();
            }
        }

        /// <summary>
        /// 活动场景里所有对象的名字，含嵌套与未激活的。走场景根节点遍历而不调 <c>FindObjectsOfType</c>：
        /// 后者在本套件同样要编译通过的新版编辑器上已是 error 级 obsolete。
        /// </summary>
        private static string[] SceneObjectNames() =>
            SceneManager.GetActiveScene().GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                .Select(t => t.gameObject.name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

        private static GameObject FindProbe(string name) =>
            SceneManager.GetActiveScene().GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                .Select(t => t.gameObject)
                .FirstOrDefault(go => string.Equals(go.name, name, StringComparison.Ordinal));
    }
}

// Producer:Betsy
