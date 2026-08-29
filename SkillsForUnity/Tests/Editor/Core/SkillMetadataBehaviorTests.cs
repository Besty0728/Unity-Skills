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
    /// The behavioral half of <see cref="SkillMetadataGuardTests"/>.
    ///
    /// <para>Every assertion in that file compares one attribute against another -- ReadOnly against MutatesScene, a batch variant's declaration against its singular twin. That can
    /// only catch "self-contradiction," and nothing else: it's completely blind when a declaration contradicts the *code*. <c>Outputs</c> can list keys the response never carries,
    /// <c>ReadOnly</c> can sit on a skill that writes, and both files stay green regardless. Closing
    /// that gap requires actually executing the skill and reading the answer.</para>
    ///
    /// <para>Uses a representative sample rather than a full sweep: one skill is picked for each declaration that actually acts as a gate at runtime (ReadOnly, MutatesScene, MutatesAssets),
    /// one envelope-shaped batch skill, and one of the enum setters this round re-specified for <c>applied</c> echo-back. A full-registry version would need fixture data prepared for
    /// hundreds of skills individually, and would eventually just get disabled rather than maintained,
    /// which is worse than a small sample that's actually run.</para>
    ///
    /// <para>Two things are asserted here. <c>Outputs</c> must be a subset of the keys the response actually carries, because an agent plans against Outputs -- a key that's declared
    /// but never arrives forces the caller into an extra round trip for a value it was promised. And a ReadOnly skill must leave nothing behind, which is exactly the premise the surface-
    /// tier mechanism depends on: no tier ever withdraws a read-only skill, so a write hiding
    /// under that label can't stay hidden under any tier.</para>
    /// </summary>
    [TestFixture]
    public class SkillMetadataBehaviorTests
    {
        private const string ProbeParent = "__behavior_probe_parent__";
        private const string ProbeChild = "__behavior_probe_child__";
        private const string ProbeFolder = "Assets/__UnitySkillsBehaviorProbe__";
        private const string ProbeMaterialPath = ProbeFolder + "/probe.mat";

        /// <summary>Read-only samples; the request body names the specific probe objects the fixture built.</summary>
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
            // Writes are rejected outside Bypass mode, and non-full tiers withdraw the write category too. Both live in global EditorPrefs, so both are explicitly pinned and
            // restored, never assumed.
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

        // ---------- Outputs must only list keys the response actually carries ----------

        /// <summary>
        /// The ReadOnly representative, and also the skill that <c>SkillMetadataGuardTests.GameObjectGetInfo_DeclaresAllFifteenOutputs</c> asserts
        /// Outputs against name by name. That test pins "what the attribute says"; this one pins "the response matches it."
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

        /// <summary>The MutatesScene representative.</summary>
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

        /// <summary>The MutatesAssets representative.</summary>
        [Test]
        public void MaterialCreate_ResponseCarriesEveryDeclaredOutput()
        {
            Assume.That(SkillRouter.TryGetSkill("material_create", out var declared), Is.True);
            Assume.That(declared.MutatesAssets, Is.True,
                "Chosen as the MutatesAssets representative; if it stops declaring that, pick another skill.");
            EnsureProbeFolder();

            var payload = AssertResponseCoversDeclaredOutputs("material_create",
                "{\"name\":\"behavior_probe_mat\",\"savePath\":\"" + ProbeFolder + "\"}");

            // `path` is the caller's only handle on the asset it just created, so it must be a path that actually loads -- an echo that resolves to nothing would leave the caller
            // unable to address its own new material.
            var createdPath = payload["path"]?.ToString();
            Assert.That(createdPath, Is.Not.Null.And.Not.Empty);
            _createdAssetPaths.Add(createdPath);
            Assert.That(AssetDatabase.LoadAssetAtPath<Material>(createdPath), Is.Not.Null,
                $"material_create reported path '{createdPath}' but nothing loads from it.");
        }

        /// <summary>
        /// One of the enum setters re-specified this round: <c>shadows</c> is now rejected rather
        /// than silently dropped, with <c>applied</c>/<c>skipped</c> reporting per-parameter. Both of those are declared Outputs; this test is exactly what catches the day the
        /// response stops carrying them.
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
        /// An envelope-shaped batch skill. Per-item results live under <c>results</c>, so what
        /// matters here is the envelope's own four keys -- if a batch response loses <c>failCount</c>, the caller has no way to distinguish "partial failure" from "total
        /// success" short of walking every item.
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

        // ---------- ReadOnly must mean what it says ----------

        /// <summary>
        /// <c>ReadOnly</c> is the one declaration no tier can override, so a skill carrying it while still touching the project can't be hidden by design. This checks against the
        /// project itself rather than another attribute: objects in the scene, asset paths on disk, and the probe material's bytes after landing on disk via <c>SaveAssets</c> --
        /// that last one specifically catches a modification that only dirtied memory and never
        /// hit disk, since anyone's next save would commit it.
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
                // Uses a HashSet rather than NUnit's EquivalentTo: counting this package, the
                // project has thousands of asset paths, and EquivalentTo compares pairwise.
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
        /// Executes <paramref name="skill"/> and asserts that every declared output corresponds to a key actually present in the payload, while also handing the payload back to the
        /// caller for skill-specific follow-up checks.
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
            // Deliberately gives it a parent: `parent` and `parentPath` are both declared outputs, and both return null on a root object -- once the caller starts checking
            // for null, that becomes indistinguishable from "the payload doesn't carry these two keys at all."
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
        /// Flushes pending writes to disk first, then reads the probe material's bytes on disk.
        /// Going through <c>SaveAssets</c> is exactly the point: a modification that only dirtied an in-memory object shows up here too, and that's precisely the kind of write a
        /// "read-only" skill could otherwise keep hidden until some unrelated save exposed it.
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
        /// The names of every object in the active scene, including nested and inactive ones. Walks the scene's root nodes rather than calling <c>FindObjectsOfType</c>: the latter
        /// is already error-level obsolete on the newer editor this suite must also compile against.
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
