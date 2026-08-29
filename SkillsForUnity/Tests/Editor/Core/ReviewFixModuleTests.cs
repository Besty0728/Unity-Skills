using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Regression coverage for the 2026-08-22 review-fix batch:
    /// <list type="bullet">
    /// <item>#7 — the bare "material"/"shader" anchors in SkillErrorClassifier.PropertyNotOnTarget</item>
    /// <item>#12 — five batch skills' Outputs declarations missing failCount</item>
    /// <item>#13 — the hardcoded "SEMANTIC_INVALID" literal in SmartReferenceBind</item>
    /// <item>#14 — PrimeTweenSkills.Stringify not flattening struct-typed configuration values</item>
    /// </list>
    /// #11 (the ordering of ModelSkills' writability check) and #17 (UnitySkillsWindow's EditorUiScheduler
    /// routing) are not covered here — see the fix report for why: #11 needs a real .fbx asset (this repo has
    /// none) plus a VCS provider mock, to observe the MakeEditable side effect the reordering revolves around;
    /// #17 needs a live UI Toolkit panel (an already-attached VisualElement.panel) before
    /// EditorUiScheduler.RepeatSafe's guard will actually run the callback, which means standing up a real
    /// EditorWindow inside the test.
    ///
    /// <para>Also the 2026-08-23 live-8090 batch:
    /// <list type="bullet">
    /// <item>L3 — Addressables group/profile lookup failures were misrouted to gameobject_find / asset_find</item>
    /// <item>L4 — YooAsset runtime validation jobs were routed to job_list, which can't see them at all</item>
    /// <item>L5 — prefab_set_property had no Quaternion branch (every localRotation write failed)</item>
    /// <item>L7 — smoke-probe fixture handling: a lightmap getter that throws + a relaxed whitelist</item>
    /// </list>
    /// L1/L2 (Addressables group-rename echo, group_create's required groupName) live next to the endpoints
    /// they belong to: AddressablesSkillsTests needs the package installed to observe the rename, and the
    /// schema-required assertion was folded into the existing list in WorkflowPersistenceTests. L6 (twelve
    /// skills missing RequiresInput) is a registry-wide scan, so it belongs to SkillMetadataGuardTests.</para>
    /// </summary>
    [TestFixture]
    public class ReviewFixModuleTests
    {
        private SkillsOperatingMode _savedMode;
        private SurfaceProfileKind _savedProfile;

        [SetUp]
        public void SetUp()
        {
            _savedMode = SkillsModeManager.CurrentMode;
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
            GameObjectFinder.InvalidateCache();
        }

        // ---------- #7: tightening the anchors in SkillErrorClassifier.PropertyNotOnTarget ----------

        /// <summary>
        /// These three literals used to hit material_get_properties (the first two, via a bare "shader"/"material"
        /// substring check) or component_get_serialized_properties (the third, via a bare "serialized" check),
        /// even though none of them refer to a property that actually exists on any material/shader/component
        /// instance — they are internal GraphicsSettings/ShaderGraph lookup failures. Now all three fall
        /// through to the generic "property not found" fallback branch, whose only SuggestedFix leads with
        /// component_get_properties.
        /// </summary>
        [TestCase("Always Included Shaders property not found in GraphicsSettings")]
        [TestCase("Shader Graph property type not found: Vector4")]
        [TestCase("GraphicsSettings serialized property not found")]
        public void Classify_MisroutedGraphicsLiterals_NoLongerSuggestMaterialOrSerializedReader(string message)
        {
            var classification = SkillErrorClassifier.Classify(message);
            var suggestedSkills = SuggestedSkillNames(classification);

            Assert.That(suggestedSkills, Has.None.EqualTo("material_get_properties"),
                $"'{message}' still suggests material_get_properties — the anchor tightening regressed.");
            Assert.That(suggestedSkills, Has.None.EqualTo("component_get_serialized_properties"),
                $"'{message}' still suggests component_get_serialized_properties — the anchor tightening regressed.");
            Assert.That(suggestedSkills, Has.Some.EqualTo("component_get_properties"),
                $"'{message}' did not land in the generic property-not-found fallback as designed.");
        }

        /// <summary>
        /// The part #7's fix must not regress: a genuine material/shader property lookup failure still routes
        /// to material_get_properties, unchanged.
        /// </summary>
        [TestCase("Material does not have a color property. Tried: _Color, _BaseColor")]
        [TestCase("No color property found on material")]
        [TestCase("Shader Graph property 'x' was not found")]
        public void Classify_GenuineMaterialShaderPropertyMisses_StillRouteToMaterialGetProperties(string message)
        {
            var classification = SkillErrorClassifier.Classify(message);
            var suggestedSkills = SuggestedSkillNames(classification);

            Assert.That(suggestedSkills, Has.Some.EqualTo("material_get_properties"),
                $"'{message}' should still route to material_get_properties.");
        }

        /// <summary>A genuine component serialized-property lookup failure must keep the routing it had before the fix.</summary>
        [Test]
        public void Classify_ComponentSerializedPropertyMiss_StillRoutesToSerializedPropertiesReader()
        {
            var classification = SkillErrorClassifier.Classify("Serialized property not found: m_Foo");
            var suggestedSkills = SuggestedSkillNames(classification);

            Assert.That(suggestedSkills, Has.Some.EqualTo("component_get_serialized_properties"),
                "A genuine component serialized-property miss must keep routing to " +
                "component_get_serialized_properties.");
        }

        private static string[] SuggestedSkillNames(SkillErrorClassification classification) =>
            (classification.SuggestedFixes ?? new List<SuggestedFix>())
                .Select(fix => fix.skill)
                .ToArray();

        // ---------- #12: batch skills must declare failCount, since BatchExecutor always returns it ----------

        /// <summary>
        /// All five of these go through BatchExecutor.Execute, whose envelope unconditionally carries
        /// totalItems/successCount/failCount/results (see BatchExecutor.cs) — Outputs should declare that faithfully.
        /// </summary>
        [TestCase("material_create_batch")]
        [TestCase("material_assign_batch")]
        [TestCase("material_set_colors_batch")]
        [TestCase("material_set_emission_batch")]
        [TestCase("script_create_batch")]
        public void BatchSkill_DeclaresFailCountOutput(string skillName)
        {
            Assume.That(SkillRouter.TryGetSkill(skillName, out var info), Is.True, $"{skillName} is not registered.");
            Assert.That(info.Outputs, Has.Some.EqualTo("failCount"),
                $"{skillName}'s Outputs omits failCount even though its BatchExecutor envelope carries it.");
        }

        // ---------- #13: SmartReferenceBind's SEMANTIC_INVALID now comes from SkillParamUtil ----------

        private const string BindTargetName = "__review_fix_bind_target__";
        private const string BindSourceName = "__review_fix_bind_source__";

        /// <summary>
        /// sharedMaterials is Material[], and sourceTag/sourceName can only ever resolve to a GameObject, so
        /// this element type can never be satisfied — it should be rejected up front with SEMANTIC_INVALID,
        /// not have the field silently cleared. This test checks end-to-end through SkillRouter that
        /// SmartSkills.cs now pulls that literal from SkillParamUtil.SemanticInvalidCode, rather than asserting
        /// the string constant a second time in isolation.
        /// </summary>
        [Test]
        public void SmartReferenceBind_UnsupportedElementType_StillReportsSemanticInvalid()
        {
            var target = new GameObject(BindTargetName, typeof(MeshRenderer));
            var source = new GameObject(BindSourceName);
            try
            {
                GameObjectFinder.InvalidateCache();

                var body = "{\"targetName\":\"" + BindTargetName + "\",\"componentName\":\"MeshRenderer\"," +
                           "\"fieldName\":\"sharedMaterials\",\"sourceName\":\"" + BindSourceName + "\"}";
                var response = JObject.Parse(SkillRouter.Execute("smart_reference_bind", body));

                Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"),
                    $"Response: {response.ToString(Formatting.None)}");
            }
            finally
            {
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(source);
                GameObjectFinder.InvalidateCache();
            }
        }

        // ---------- #14: PrimeTweenSkills.Stringify must flatten struct values, not just enums ----------

        private enum ProbeEnum { First, Second }

        private struct ProbeStruct
        {
            public int Value;
            public override string ToString() => $"probe:{Value}";
        }

        /// <summary>
        /// PrimeTween.UpdateType's shape: an enum-like struct whose state lives in a private enum field, without
        /// overriding ToString() — the default ValueType.ToString() only gives the type name, the value stays invisible.
        /// </summary>
        private struct ProbeEnumLikeStruct
        {
#pragma warning disable 0414
            private ProbeEnum _enumValue;
#pragma warning restore 0414

            public static ProbeEnumLikeStruct Of(ProbeEnum value) =>
                new ProbeEnumLikeStruct { _enumValue = value };
        }

        private static object InvokeStringify(object value)
        {
            var method = typeof(PrimeTweenSkills).GetMethod("Stringify", BindingFlags.NonPublic | BindingFlags.Static);
            Assume.That(method, Is.Not.Null, "PrimeTweenSkills.Stringify signature changed or was removed.");
            return method.Invoke(null, new[] { value });
        }

        /// <summary>
        /// The real bug: in newer PrimeTween versions, UpdateType is a struct rather than an enum, so it fell
        /// through the branch that originally only handled enums, and the configured anonymous-object
        /// serializer output "{}" for it.
        /// </summary>
        [Test]
        public void Stringify_NonPrimitiveValueType_FlattensToItsToString()
        {
            var result = InvokeStringify(new ProbeStruct { Value = 42 });
            Assert.That(result, Is.EqualTo("probe:42"));
        }

        [Test]
        public void Stringify_Enum_StillFlattensToItsName()
        {
            var result = InvokeStringify(ProbeEnum.Second);
            Assert.That(result, Is.EqualTo("Second"));
        }

        /// <summary>
        /// Literally "non-empty string" isn't enough: a live-8090 run once output the constant
        /// "PrimeTween.UpdateType" (the full type name), giving the caller no access to the real configured
        /// value. An enum-like struct must be unwrapped to its enum field's name.
        /// </summary>
        [Test]
        public void Stringify_EnumLikeStruct_UnwrapsToTheEnumFieldName()
        {
            var result = InvokeStringify(ProbeEnumLikeStruct.Of(ProbeEnum.Second));
            Assert.That(result, Is.EqualTo("Second"));
        }

        [Test]
        public void Stringify_Primitive_PassesThroughUnchanged()
        {
            Assert.That(InvokeStringify(7), Is.EqualTo(7));
            Assert.That(InvokeStringify(true), Is.EqualTo(true));
        }

        [Test]
        public void Stringify_ReferenceTypeAndNull_PassThroughUnchanged()
        {
            var obj = new object();
            Assert.That(InvokeStringify(obj), Is.SameAs(obj));
            Assert.That(InvokeStringify(null), Is.Null);
        }

        // ================================================================================
        // 2026-08-23 live-8090 batch
        // ================================================================================

        // ---------- L3: Addressables group/profile lookup failures pointed at the wrong reader ----------

        /// <summary>
        /// A group/profile name that doesn't exist in the AddressableAssetSettings asset used to be answered
        /// by the classifier's generic branch: "Group not found: X" has no asset-class noun in it, so it fell
        /// through to gameobject_find / scene_get_hierarchy; and "Profile not found: X" hit the "asset marker"
        /// branch purely because "profile" contains the substring "file", so it fell through to asset_find.
        /// Neither can resolve a name that only exists inside the settings asset.
        ///
        /// <para>The third assertion is what keeps this test honest: it re-derives what the classifier alone
        /// would still say about the same message, so this test can't silently pass just because the
        /// misrouting disappeared for some unrelated reason — what should be doing the work here is the declaration.</para>
        /// </summary>
        [TestCase("GroupNotFound", "MissingGroup", "addressables_group_list", "gameobject_find")]
        [TestCase("ProfileNotFound", "MissingProfile", "addressables_profile_get", "asset_find")]
        public void AddressablesLookupMiss_PointsAtTheAddressablesReader(
            string helper, string argument, string expectedSkill, string misroutedSkill)
        {
            var method = typeof(AddressablesSkills).GetMethod(helper, BindingFlags.NonPublic | BindingFlags.Static);
            Assume.That(method, Is.Not.Null, $"AddressablesSkills.{helper} was renamed or removed.");

            var payload = method.Invoke(null, new object[] { argument });
            Assert.That(SkillResultHelper.TryGetErrorContext(payload, out var context), Is.True,
                "The helper must still read as an error object, or the router will treat it as success.");

            var declared = DeclaredSkillNames(context);
            Assert.That(declared, Has.Some.EqualTo(expectedSkill),
                $"{helper} does not point at {expectedSkill}: {string.Join(", ", declared)}");
            Assert.That(declared, Has.None.EqualTo(misroutedSkill),
                $"{helper} still offers {misroutedSkill}, which cannot resolve an Addressables settings name.");

            var classifierOnly = SkillErrorClassifier.Classify(context.Message)
                .SuggestedFixes?.Select(fix => fix.skill).ToArray() ?? new string[0];
            Assert.That(classifierOnly, Has.Some.EqualTo(misroutedSkill),
                $"The classifier no longer misroutes '{context.Message}', so this test is now asserting " +
                "nothing — re-derive what it does say before deleting the declaration.");
        }

        // ---------- L4: YooAsset runtime validation jobs are not AsyncJobService jobs ----------

        /// <summary>
        /// The answer for an unknown jobId used to be a flat "…not found", which the classifier's job branch
        /// turned into job_list plus a line about "the id can't survive a domain reload". Both were wrong:
        /// these jobs live in YooAssetSkills' own dictionary, invisible to job_list/job_status entirely; and
        /// they're persisted to EditorPrefs and restored after a reload — so the caller was pointed at a table
        /// that could never hold this id, for a reason that doesn't even apply to it.
        /// </summary>
        [Test]
        public void YooAssetUnknownRuntimeJob_DoesNotSendTheCallerToTheGenericJobTable()
        {
            var method = typeof(YooAssetSkills).GetMethod(
                "UnknownRuntimeValidationJob", BindingFlags.NonPublic | BindingFlags.Static);
            Assume.That(method, Is.Not.Null, "YooAssetSkills.UnknownRuntimeValidationJob was renamed or removed.");

            var payload = method.Invoke(null, new object[] { "deadbeef" });
            Assert.That(SkillResultHelper.TryGetErrorContext(payload, out var context), Is.True);

            var declared = DeclaredSkillNames(context);
            Assert.That(declared, Has.None.EqualTo("job_list"),
                $"Runtime validation jobs are invisible to job_list: {string.Join(", ", declared)}");
            Assert.That(declared, Has.None.EqualTo("job_status"));
            Assert.That(declared, Has.Some.EqualTo("yooasset_runtime_validate_package"));
            Assert.That(context.Message, Does.Not.Contain("do not survive"),
                "These ids DO survive a domain reload — that claim came from the generic job arm.");
            Assert.That(context.Extra != null && context.Extra.ContainsKey("knownJobIds"), Is.True,
                "The live ids must travel with the error; there is no listing endpoint for this store.");

            // Canary: the bare classifier must still fail to give correct guidance (which exact skill it gets
            // wrong drifts as the classifier evolves, so it's not pinned down — it used to be pinned to
            // job_list, and a classifier baseline change caused a false failure). If this ever fails, it means
            // the classifier has learned to route correctly on its own, and the layer-1 override may be
            // redundant — re-evaluate instead of just deleting it.
            var classifierOnly = SkillErrorClassifier.Classify(context.Message)
                .SuggestedFixes?.Select(fix => fix.skill).ToArray() ?? new string[0];
            Assert.That(classifierOnly, Has.None.EqualTo("yooasset_runtime_validate_package"),
                "The bare classifier now produces the correct YooAsset guidance on its own — the " +
                "layer-1 override may be redundant; re-evaluate before trusting this test.");
        }

        private static string[] DeclaredSkillNames(SkillErrorContext context) =>
            (context.RelatedSkills ?? new List<string>())
                .Concat((context.SuggestedFixes ?? new List<SuggestedFix>()).Select(fix => fix.skill))
                .Where(name => !string.IsNullOrEmpty(name))
                .ToArray();

        // ---------- L5: prefab_set_property couldn't write a Quaternion ----------

        private const string PrefabProbeFolder = "Assets/Temp";
        private const string PrefabProbeName = "__review_fix_prefab_probe__";
        private const string PrefabProbePath = PrefabProbeFolder + "/" + PrefabProbeName + ".prefab";

        /// <summary>
        /// m_LocalRotation is a Quaternion, and SetSerializedPropertyValue had no Quaternion branch at the
        /// time — so the most common kind of prefab write fell into the default branch and came back with
        /// "Failed to set value … (type: Quaternion)". This asserts by reloading the asset rather than looking
        /// at the response, because what was missing was never the success envelope.
        /// </summary>
        [Test]
        public void PrefabSetProperty_Quaternion_LandsOnTheAsset()
        {
            var probe = new GameObject(PrefabProbeName);
            try
            {
                EnsureProbeFolder();
                var asset = UnityEditor.PrefabUtility.SaveAsPrefabAsset(probe, PrefabProbePath);
                Assume.That(asset, Is.Not.Null, "Could not create the prefab fixture.");

                var response = JObject.Parse(SkillRouter.Execute("prefab_set_property",
                    "{\"prefabPath\":\"" + PrefabProbePath + "\",\"componentType\":\"Transform\"," +
                    "\"propertyName\":\"localRotation\",\"value\":\"0,90,0\"}"));
                Assert.That(response["status"]?.ToString(), Is.EqualTo("success"),
                    response.ToString(Formatting.None));

                var reloaded = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(PrefabProbePath);
                Assert.That(reloaded, Is.Not.Null, "The prefab asset disappeared.");
                Assert.That(Quaternion.Angle(reloaded.transform.localRotation, Quaternion.Euler(0f, 90f, 0f)),
                    Is.LessThan(0.5f),
                    $"localRotation reads back as {reloaded.transform.localRotation.eulerAngles}, not (0, 90, 0).");

                // And that it actually landed on disk, not just in the loaded asset: rotating 90 degrees around
                // Y serializes to (0, 0.707…, 0, 0.707…), so the numeric assertion here leaves enough margin to
                // tolerate Unity's floating-point rounding.
                var rotationLine = System.IO.File.ReadAllText(PrefabProbePath)
                    .Split('\n')
                    .FirstOrDefault(line => line.Contains("m_LocalRotation"));
                Assert.That(rotationLine, Is.Not.Null, "The prefab YAML has no m_LocalRotation entry.");
                Assert.That(rotationLine, Does.Contain("0.7"),
                    $"m_LocalRotation on disk is '{rotationLine?.Trim()}' — the write never reached the file.");
            }
            finally
            {
                Object.DestroyImmediate(probe);
                UnityEditor.AssetDatabase.DeleteAsset(PrefabProbePath);
            }
        }

        /// <summary>
        /// The other half of the same fix: for a property type this skill genuinely cannot write from text, it
        /// must say so clearly. "Failed to set value 'x'" blames the value, leaving an uncategorized
        /// SKILL_ERROR + abort, so the agent has no way to tell "reformat your value" apart from "this field
        /// needs assetReferencePath instead".
        /// </summary>
        [Test]
        public void PrefabSetProperty_UnsupportedSerializedType_BlamesTheTypeNotTheValue()
        {
            var probe = new GameObject(PrefabProbeName, typeof(MeshFilter));
            try
            {
                EnsureProbeFolder();
                var asset = UnityEditor.PrefabUtility.SaveAsPrefabAsset(probe, PrefabProbePath);
                Assume.That(asset, Is.Not.Null, "Could not create the prefab fixture.");

                var response = JObject.Parse(SkillRouter.Execute("prefab_set_property",
                    "{\"prefabPath\":\"" + PrefabProbePath + "\",\"componentType\":\"MeshFilter\"," +
                    "\"propertyName\":\"m_Mesh\",\"value\":\"Cube\"}"));

                Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"),
                    response.ToString(Formatting.None));
                Assert.That(response["error"]?.ToString(), Does.Contain("Unsupported serialized property type"));
                Assert.That(response["error"]?.ToString(), Does.Contain("assetReferencePath"),
                    "The message must name the parameter that can actually set an object reference.");
            }
            finally
            {
                Object.DestroyImmediate(probe);
                UnityEditor.AssetDatabase.DeleteAsset(PrefabProbePath);
            }
        }

        private static void EnsureProbeFolder()
        {
            if (!UnityEditor.AssetDatabase.IsValidFolder(PrefabProbeFolder))
                UnityEditor.AssetDatabase.CreateFolder("Assets", "Temp");
        }

        // ---------- L7: smoke-probe fixture handling ----------

        /// <summary>
        /// When the scene has no Lighting Settings asset, Lightmapping.lightingSettings throws instead of
        /// returning null, so the null-check branch meant to answer with Unity's built-in defaults never runs
        /// at all — this read-only query would count as a smoke failure on any default project. This test
        /// passes in both cases — if the asset happens to exist, a different branch runs — because what's
        /// pinned down is "never errors", not which branch answers.
        /// </summary>
        [Test]
        public void LightGetLightmapSettings_AnswersWithoutALightingSettingsAsset()
        {
            var response = JObject.Parse(SkillRouter.Execute("light_get_lightmap_settings", "{}"));

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"),
                response.ToString(Formatting.None));
            Assert.That(response["result"]?["lightmapSize"], Is.Not.Null,
                "The answer must carry the settings fields in both branches.");
        }

        /// <summary>
        /// The smoke probe's fixture whitelist. The lines that must match are the exact rejections a clean
        /// project produces (no NetworkManager, in EditMode rather than PlayMode); the lines that must not
        /// match are what keeps the whitelist from degenerating into "skip everything" — namely a different
        /// failure from a listed skill, and listed wording coming from an unlisted skill.
        /// </summary>
        [TestCase("netcode_get_manager_info", "NetworkManager not found (name=<any>).", true)]
        [TestCase("netcode_get_status", "NetworkManager not found.", true)]
        [TestCase("netcode_get_transport_info", "NetworkTransport not assigned.", true)]
        [TestCase("netcode_get_spawn_manager_info", "SpawnManager only accessible in PlayMode.", true)]
        [TestCase("netcode_get_scene_manager_info", "SceneManager info only available in PlayMode.", true)]
        [TestCase("cinemachine_get_brain_info", "No CinemachineBrain found in the scene.", true)]
        [TestCase("netcode_get_manager_info", "Object reference not set to an instance of an object", false)]
        [TestCase("netcode_get_status", "NetworkConfig is corrupt", false)]
        [TestCase("light_get_lightmap_settings", "Lightmapping.lightingSettings is null", false)]
        [TestCase("gameobject_find", "NetworkManager not found.", false)]
        public void SmokeFixtureWhitelist_MatchesTheFixtureAbsenceShapesOnly(
            string skillName, string error, bool expected)
        {
            var method = typeof(TestSkills).GetMethod(
                "IsExpectedMissingSceneFixture", BindingFlags.NonPublic | BindingFlags.Static);
            Assume.That(method, Is.Not.Null, "TestSkills.IsExpectedMissingSceneFixture was renamed or removed.");

            var matched = (bool)method.Invoke(null, new object[] { skillName, error });
            Assert.That(matched, Is.EqualTo(expected),
                $"IsExpectedMissingSceneFixture(\"{skillName}\", \"{error}\") should be {expected}. " +
                "A false positive hides a real regression; a false negative keeps the smoke sweep red " +
                "on a clean project.");
        }

        // ================================================================================
        // 2026-08-23 DOTween Pro 1.0.381 live-machine batch (B1-B7)
        //
        // None of the test cases here need DOTween installed. The two things that would genuinely require it
        // (a real DOTweenAnimation, and the Ease/LoopType enums that were wrongly referenced) are instead
        // asserted against the actual seams the skill goes through, using shape-matched stand-in types — each
        // test notes which live-machine symptom it replaces.
        // ================================================================================

        // ---------- B1: the index full-scene listing hands out isn't the one consumers use to look things up ----------

        private const string IndexProbeA = "__dotween_index_probe_a__";
        private const string IndexProbeB = "__dotween_index_probe_b__";

        /// <summary>
        /// <c>dotween_pro_list_animations</c>'s full-scene branch groups <c>FindHelper.FindAll</c>'s results
        /// (explicitly documented as unordered) and hands out an incrementing counter, while every setter
        /// indexes by <c>gameObject.GetComponents(type)</c>. When a GameObject carries multiple
        /// DOTweenAnimation components, the two disagree — in a real project, one GameObject was listed as
        /// [Fade 0.3, Scale 0.6, Fade 0.4] while its GetComponents order was [Scale 0.6, Fade 0.3, Fade 0.4] —
        /// so an agent that lists then sets ends up modifying a different component than the one it read, with
        /// both calls reporting success.
        ///
        /// <para>This asserts using BoxCollider rather than DOTweenAnimation: the property under test is
        /// "index equals the component's position in GetComponents", which has nothing to do with type; and the
        /// input is deliberately reversed, so a leftover running counter can't pass.</para>
        /// </summary>
        [Test]
        public void AuthoritativeIndices_FollowGetComponentsOrder_WhateverTheInputOrder()
        {
            var probe = new GameObject(IndexProbeA);
            try
            {
                probe.AddComponent<BoxCollider>();
                probe.AddComponent<BoxCollider>();
                probe.AddComponent<BoxCollider>();
                var authoritative = probe.GetComponents(typeof(BoxCollider));
                Assume.That(authoritative.Length, Is.EqualTo(3), "Could not stack three colliders.");

                var shuffled = authoritative.Reverse().ToList();
                var pairs = DOTweenSkills.ResolveAuthoritativeIndices(shuffled, typeof(BoxCollider));

                Assert.That(pairs.Count, Is.EqualTo(3), "A row was dropped.");
                for (int i = 0; i < authoritative.Length; i++)
                {
                    Assert.That(pairs[i].Value, Is.EqualTo(i), $"Row {i} reports index {pairs[i].Value}.");
                    Assert.That(pairs[i].Key, Is.SameAs(authoritative[i]),
                        $"Row {i} is not the component GetComponents()[{i}] returns — this is exactly the " +
                        "mismatch that made list-then-set edit the wrong component.");
                }
            }
            finally
            {
                Object.DestroyImmediate(probe);
            }
        }

        /// <summary>
        /// Full-scene shape: components from multiple GameObjects arrive interleaved. Indices must restart
        /// from zero per GameObject, because that's exactly what the setter's index means.
        /// </summary>
        [Test]
        public void AuthoritativeIndices_RestartPerGameObject_ForInterleavedInput()
        {
            var first = new GameObject(IndexProbeA);
            var second = new GameObject(IndexProbeB);
            try
            {
                foreach (var go in new[] { first, second })
                {
                    go.AddComponent<BoxCollider>();
                    go.AddComponent<BoxCollider>();
                }
                var firstComps = first.GetComponents(typeof(BoxCollider));
                var secondComps = second.GetComponents(typeof(BoxCollider));

                var interleaved = new List<Component>
                {
                    secondComps[1], firstComps[1], secondComps[0], firstComps[0]
                };
                var pairs = DOTweenSkills.ResolveAuthoritativeIndices(interleaved, typeof(BoxCollider));

                Assert.That(pairs.Count, Is.EqualTo(4));
                Assert.That(pairs.Select(p => p.Value), Is.EquivalentTo(new[] { 0, 1, 0, 1 }),
                    "Indices must be per-GameObject component positions, not a running counter.");
                foreach (var pair in pairs)
                {
                    var owner = pair.Key.gameObject.GetComponents(typeof(BoxCollider));
                    Assert.That(pair.Key, Is.SameAs(owner[pair.Value]));
                }
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

        // ---------- B2: numeric setters accept anything and echo back nothing ----------

        private const string DOTweenTargetProbe = "__dotween_setter_probe__";

        /// <summary>
        /// duration=-1 used to be written as-is with <c>{"success":true}</c> as the response. This asserts the
        /// rejection through the router (rather than calling the method directly), so it pins down the actual
        /// errorCode/retryStrategy the caller gets; and deliberately gives no resolvable target — a value this
        /// skill could never accept should be rejected before the scene is even queried, which is also why
        /// this is observable even on a project without DOTween Pro installed.
        /// </summary>
        [TestCase("dotween_pro_set_duration", "{\"target\":\"" + DOTweenTargetProbe + "\",\"duration\":-1}")]
        [TestCase("dotween_pro_set_duration", "{\"target\":\"" + DOTweenTargetProbe + "\",\"duration\":0}")]
        [TestCase("dotween_pro_set_loops", "{\"target\":\"" + DOTweenTargetProbe + "\",\"loops\":-7}")]
        [TestCase("dotween_pro_set_loops", "{\"target\":\"" + DOTweenTargetProbe + "\",\"loops\":-2}")]
        [TestCase("dotween_pro_set_ease", "{\"target\":\"" + DOTweenTargetProbe + "\",\"easeCurveJson\":\"not json\"}")]
        public void DOTweenProSetter_OutOfDomainValue_IsRejectedWithSemanticInvalid(string skillName, string body)
        {
            var response = JObject.Parse(SkillRouter.Execute(skillName, body));

            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"),
                response.ToString(Formatting.None));
            Assert.That(response["retryStrategy"]?.ToString(), Is.EqualTo("fix_and_retry"));
            Assert.That(response["validValues"], Is.Not.Null,
                "The rejection must say what the accepted values are — otherwise the caller can only guess.");
        }

        /// <summary>
        /// loops=0 gets rejected one layer earlier than the other cases: the RequiresInput group
        /// "loops|loopType" reads the numeric value 0 as "no value given at all", so the validation layer
        /// replies "Provide one of: loops, loopType" before the skill even executes. It's still a rejection
        /// with the same error code and retry strategy — just worded by the group rather than the loops value
        /// domain, which is why it's asserted separately from the cases above (which pin down validValues).
        /// </summary>
        [Test]
        public void DOTweenProSetLoops_Zero_IsRefusedBeforeExecuting()
        {
            var body = "{\"target\":\"" + DOTweenTargetProbe + "\",\"loops\":0}";

            Assert.That(JObject.Parse(SkillRouter.DryRun("dotween_pro_set_loops", body))["valid"]?.Value<bool>(),
                Is.False);
            var response = JObject.Parse(SkillRouter.Execute("dotween_pro_set_loops", body));
            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"),
                response.ToString(Formatting.None));
        }

        /// <summary>-1 is DOTween's "infinite loop" marker and must keep being accepted; a guard that rejects
        /// it is the same defect wearing a different sign.</summary>
        [TestCase("{\"target\":\"" + DOTweenTargetProbe + "\",\"loops\":-1}")]
        [TestCase("{\"target\":\"" + DOTweenTargetProbe + "\",\"loops\":3}")]
        [TestCase("{\"target\":\"" + DOTweenTargetProbe + "\",\"loopType\":\"Yoyo\"}")]
        public void DOTweenProSetLoops_InDomainValue_IsNotRejectedAsSemanticInvalid(string body)
        {
            var response = JObject.Parse(SkillRouter.Execute("dotween_pro_set_loops", body));

            // Without DOTween Pro installed this falls into MISSING_PACKAGE; with it installed, it falls into
            // component-missing / GameObject-missing. Either way, the *value* must never be the one blamed.
            Assert.That(response["errorCode"]?.ToString(), Is.Not.EqualTo("SEMANTIC_INVALID"),
                response.ToString(Formatting.None));
        }

        /// <summary>
        /// The "default value" trap. <c>float duration = 1f</c> can't tell "1 was passed" apart from "nothing
        /// was passed", so a call meaning only to target some animation would silently reset it to 1 second;
        /// <c>fieldValue = null</c> would clear the named field; <c>int loops = 1</c> turned a call that only
        /// meant to set loopType into "and also stop looping". All three now reject — and reject at the
        /// dryRun/schema layer, so the caller knows before anything gets executed.
        /// </summary>
        [TestCase("dotween_pro_set_duration", "{\"target\":\"" + DOTweenTargetProbe + "\"}", "MISSING_PARAM")]
        [TestCase("dotween_pro_set_animation_field",
            "{\"target\":\"" + DOTweenTargetProbe + "\",\"fieldName\":\"id\"}", "MISSING_PARAM")]
        // set_loops's two halves are each individually optional, so "neither given" is the group judgment
        // validation returns (SEMANTIC_INVALID), not a per-parameter MISSING_PARAM.
        [TestCase("dotween_pro_set_loops", "{\"target\":\"" + DOTweenTargetProbe + "\"}", "SEMANTIC_INVALID")]
        public void DOTweenProSetter_OmittedPayload_IsRefusedInsteadOfDefaulted(
            string skillName, string body, string expectedCode)
        {
            var dry = JObject.Parse(SkillRouter.DryRun(skillName, body));
            Assert.That(dry["valid"]?.Value<bool>(), Is.False,
                $"{skillName} still dry-runs an omitted payload as valid: {dry.ToString(Formatting.None)}");

            var response = JObject.Parse(SkillRouter.Execute(skillName, body));
            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo(expectedCode),
                response.ToString(Formatting.None));
            Assert.That(response["retryStrategy"]?.ToString(), Is.EqualTo("fix_and_retry"));
        }

        /// <summary>
        /// The stagger guard, checked at the helper-function level: <c>dotween_pro_stagger_animations</c>
        /// checks the package before the parameters (with no Pro installed it can't add anything anyway), so
        /// the end-to-end rejection can't be observed here — what's pinned down is the guard function the
        /// skill itself calls.
        /// </summary>
        [TestCase("InvalidNonNegativeError", -0.1f, false)]
        [TestCase("InvalidNonNegativeError", 0f, true)]
        [TestCase("InvalidNonNegativeError", 0.1f, true)]
        [TestCase("InvalidPositiveError", 0f, false)]
        [TestCase("InvalidPositiveError", -1f, false)]
        [TestCase("InvalidPositiveError", 0.001f, true)]
        public void DOTweenNumericGuard_AcceptsOnlyItsDomain(string helper, float value, bool accepted)
        {
            var method = typeof(DOTweenSkills).GetMethod(helper, BindingFlags.NonPublic | BindingFlags.Static);
            Assume.That(method, Is.Not.Null, $"DOTweenSkills.{helper} was renamed or removed.");

            var payload = method.Invoke(null, new object[] { value, "baseDelay" });
            if (accepted)
            {
                Assert.That(payload, Is.Null, $"{helper}({value}) must accept.");
                return;
            }

            Assert.That(payload, Is.Not.Null, $"{helper}({value}) must reject.");
            Assert.That(SkillResultHelper.TryGetErrorContext(payload, out var context), Is.True);
            Assert.That(context.Code, Is.EqualTo(SkillErrorCode.SemanticInvalid), context.Message);
        }

        /// <summary>
        /// The full loops value domain, including 0 — the end-to-end case above can't observe 0, since group
        /// validation intercepts the numeric value first.
        /// </summary>
        [TestCase(-1, true)]
        [TestCase(1, true)]
        [TestCase(3, true)]
        [TestCase(0, false)]
        [TestCase(-2, false)]
        [TestCase(-7, false)]
        public void DOTweenLoopsGuard_AcceptsOnlyMinusOneAndPositiveCounts(int loops, bool accepted)
        {
            var method = typeof(DOTweenSkills).GetMethod("InvalidLoopsError", BindingFlags.NonPublic | BindingFlags.Static);
            Assume.That(method, Is.Not.Null, "DOTweenSkills.InvalidLoopsError was renamed or removed.");

            var payload = method.Invoke(null, new object[] { loops });
            if (accepted)
            {
                Assert.That(payload, Is.Null, $"loops {loops} must be accepted (-1 is DOTween's infinite marker).");
                return;
            }

            Assert.That(payload, Is.Not.Null, $"loops {loops} must be rejected.");
            Assert.That(SkillResultHelper.TryGetErrorContext(payload, out var context), Is.True);
            Assert.That(context.Code, Is.EqualTo(SkillErrorCode.SemanticInvalid), context.Message);
        }

        // ---------- B3: capacity parameters advertised externally have no corresponding field on installed DOTween ----------

        private enum ProbeEaseEnum { Linear, OutQuad, Unset, INTERNAL_Custom }
        private enum ProbeLoopEnum { Restart, Yoyo, Incremental }
        private enum ProbeLogEnum { Default, Verbose, ErrorsOnly }

        /// <summary>The actual shape of DOTween Pro 1.0.381's DOTweenSettings: no capacity fields.</summary>
        // Every field here is written via reflection, invisible to the compiler (CS0649).
#pragma warning disable 0649
        private class SettingsProbeWithoutCapacities
        {
            public ProbeEaseEnum defaultEaseType;
            public bool defaultAutoKill;
            public ProbeLoopEnum defaultLoopType;
            public bool useSafeMode;
            public ProbeLogEnum logBehaviour;
        }
#pragma warning restore 0649

        private class SettingsProbeWithCapacities : SettingsProbeWithoutCapacities
        {
            public int defaultTweensCapacity = 200;
            public int defaultSequencesCapacity = 50;
        }

        /// <summary>
        /// <c>dotween_settings_configure</c> advertises support for tweenersCapacity / sequencesCapacity, and
        /// on DOTween Pro 1.0.381 that asset has neither. Both writes were only guarded by a bare
        /// <c>if (SetFieldByName(...))</c> that does nothing on the false branch, so the call came back with
        /// <c>success:true, modified:[]</c> — indistinguishable from "accepted, and it was already correct".
        /// </summary>
        [Test]
        public void SettingsConfigure_AbsentCapacityField_IsReportedUnsupportedNotSwallowed()
        {
            var probe = new SettingsProbeWithoutCapacities();
            var result = DOTweenSkills.ApplySettingsFields(probe, null, null, null, null, null, 500, 60);

            Assert.That(result.Error, Is.Null);
            Assert.That(result.Modified, Is.Empty);
            Assert.That(result.Unsupported.Select(u => u.parameter),
                Is.EquivalentTo(new[] { "tweenersCapacity", "sequencesCapacity" }),
                "Both parameters must be named back to the caller.");
            Assert.That(result.Unsupported.Select(u => u.field),
                Is.EquivalentTo(new[] { "defaultTweensCapacity", "defaultSequencesCapacity" }));
            Assert.That(result.Unsupported.All(u => !string.IsNullOrEmpty(u.reason)), Is.True,
                "An unsupported entry without a reason is as unactionable as the silent no-op was.");
        }

        /// <summary>The other half: on a version that does declare these fields, they still get written.</summary>
        [Test]
        public void SettingsConfigure_PresentCapacityField_IsStillWritten()
        {
            var probe = new SettingsProbeWithCapacities();
            var result = DOTweenSkills.ApplySettingsFields(probe, "Linear", true, "Yoyo", false, "Verbose", 500, 60);

            Assert.That(result.Error, Is.Null);
            Assert.That(result.Unsupported, Is.Empty);
            Assert.That(result.Modified, Is.EquivalentTo(new[]
            {
                "defaultEaseType", "defaultLoopType", "logBehaviour",
                "defaultAutoKill", "useSafeMode",
                "defaultTweensCapacity", "defaultSequencesCapacity"
            }));
            Assert.That(probe.defaultTweensCapacity, Is.EqualTo(500));
            Assert.That(probe.defaultSequencesCapacity, Is.EqualTo(60));
            Assert.That(probe.defaultEaseType, Is.EqualTo(ProbeEaseEnum.Linear));
            Assert.That(probe.logBehaviour, Is.EqualTo(ProbeLogEnum.Verbose));
            Assert.That(probe.defaultAutoKill, Is.True);
            Assert.That(probe.useSafeMode, Is.False);
        }

        /// <summary>An invalid enum value must list the accepted values, and must write nothing at all.</summary>
        [Test]
        public void SettingsConfigure_InvalidEnumValue_IsRejectedWithTheRealVocabulary()
        {
            var probe = new SettingsProbeWithCapacities();
            var result = DOTweenSkills.ApplySettingsFields(probe, "NotAnEase", null, null, null, null, null, null);

            Assert.That(result.Error, Is.Not.Null);
            Assert.That(result.Modified, Is.Empty);
            Assert.That(SkillResultHelper.TryGetErrorContext(result.Error, out var context), Is.True);
            Assert.That(context.Code, Is.EqualTo(SkillErrorCode.SemanticInvalid));
            Assert.That(context.Extra != null && context.Extra.ContainsKey("validValues"), Is.True,
                $"No validValues on the rejection: {context.Message}");
        }

        /// <summary>
        /// dotween_settings_validate already reports capacity &lt;= 0 as an issue, so actually writing it in
        /// would make this package judge its own just-made change as invalid the next time it reads it.
        /// </summary>
        [TestCase(0)]
        [TestCase(-5)]
        public void SettingsConfigure_NonPositiveCapacity_IsRejected(int capacity)
        {
            var probe = new SettingsProbeWithCapacities();
            var result = DOTweenSkills.ApplySettingsFields(probe, null, null, null, null, null, capacity, null);

            Assert.That(result.Error, Is.Not.Null, $"tweenersCapacity {capacity} was accepted.");
            Assert.That(probe.defaultTweensCapacity, Is.EqualTo(200), "The value was written before rejecting.");
        }

        // ---------- B4: enum rejections came without a vocabulary, and the vocabulary must be the real one ----------

        // Only ever read via reflection — the vocabulary/settable-name helpers never write these (CS0649).
#pragma warning disable 0649
        private class AnimationFieldProbe
        {
            public ProbeEaseEnum easeType;
            public ProbeLoopEnum loopType;
            public float duration;
            public int loops;
            public string id;
            public bool autoKill;
        }
#pragma warning restore 0649

        /// <summary>
        /// The <c>validValues</c> list in the ease/loopType rejection response is reflected off the enum the
        /// installed DOTween version actually declares, so it never drifts with the asset version. Two members
        /// are deliberately withheld: <c>Unset</c> means "inherit the project default", which this setter can't
        /// express; and <c>INTERNAL_Custom</c> is the marker the easeCurveJson path plants — naming it without
        /// a curve just produces a custom ease with no curve behind it.
        /// </summary>
        [Test]
        public void EnumVocabulary_ListsRealMembersAndWithholdsTheInternalOnes()
        {
            var names = DOTweenReflectionHelper.EnumNamesForField(
                typeof(AnimationFieldProbe), new[] { "easeType", "ease" });

            Assert.That(names, Is.EquivalentTo(new[] { "Linear", "OutQuad" }));
        }

        [TestCase("OutQuad", true)]
        [TestCase("outquad", true)]
        [TestCase("  OutQuad ", true)]
        [TestCase("INTERNAL_Custom", false)]
        [TestCase("Unset", false)]
        [TestCase("Bogus", false)]
        [TestCase("1", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void EnumFieldAccepts_MatchesExactlyWhatItAdvertises(string value, bool expected)
        {
            var accepted = DOTweenReflectionHelper.EnumFieldAccepts(
                typeof(AnimationFieldProbe), new[] { "easeType", "ease" }, value);

            Assert.That(accepted, Is.EqualTo(expected),
                $"'{value}' — accepted and advertised must be the same set, and a bare integer must " +
                "not slip through as an undefined enum member.");
        }

        /// <summary>
        /// The rejection response for an unknown fieldName lists which fields are settable, and it must
        /// exclude fields owned by dedicated skills — offering `duration` here would only lead the caller into
        /// a rejection.
        /// </summary>
        [Test]
        public void SettableFieldNames_OmitTheDedicatedSkillFields()
        {
            var names = DOTweenReflectionHelper.SettableFieldNames(typeof(AnimationFieldProbe));

            Assert.That(names, Is.EquivalentTo(new[] { "id", "autoKill" }));
            Assert.That(names, Has.None.EqualTo("duration"));
            Assert.That(names, Has.None.EqualTo("easeType"));
            Assert.That(names, Has.None.EqualTo("loops"));
            Assert.That(names, Has.None.EqualTo("loopType"));
        }

        // ---------- B5 / B6: generated scripts ----------

        /// <summary>
        /// Every generated Sequence declared <c>[SerializeField] private float duration</c>, then baked each
        /// step's duration into a literal anyway, so the field went unreferenced — the file this package had
        /// just written reported CS0414. Now a step whose duration differs from the top-level value still uses
        /// its own literal, while a matching step reads the field, so the Inspector knob still works under the default recipe.
        /// </summary>
        [Test]
        public void SequenceSteps_PerStepDurations_BakeLiteralsAndDropTheDeadField()
        {
            var scale = DOTweenSkills.ResolveRuntimeTweenSpec("Transform", "DOScale");
            Assume.That(scale, Is.Not.Null, "Transform/DOScale is no longer a supported recipe.");

            var steps = new List<(string, DOTweenSkills.RuntimeTweenSpec, float)>
            {
                ("Append", scale, 0.12f),
                ("AppendInterval", null, 0.05f),
            };
            var lines = DOTweenSkills.BuildSequenceSteps(steps, 1f, out var usesDurationField);

            Assert.That(usesDurationField, Is.False,
                "No step uses the top-level duration, so declaring the field would be CS0414.");
            Assert.That(lines, Has.Count.EqualTo(2));
            Assert.That(lines[0], Does.Contain("0.12f").And.Not.Contain("duration"));
            Assert.That(lines[1], Does.Contain("0.05f"));
        }

        [Test]
        public void SequenceSteps_DefaultRecipe_KeepsTheSerializedDurationField()
        {
            var move = DOTweenSkills.ResolveRuntimeTweenSpec("Transform", "DOMove");
            Assume.That(move, Is.Not.Null, "Transform/DOMove is no longer a supported recipe.");

            var steps = new List<(string, DOTweenSkills.RuntimeTweenSpec, float)>
            {
                ("Append", move, 1f),
                ("AppendInterval", null, 0.1f),
                ("Append", move, 1f),
            };
            var lines = DOTweenSkills.BuildSequenceSteps(steps, 1f, out var usesDurationField);

            Assert.That(usesDurationField, Is.True);
            Assert.That(lines[0], Does.Contain("duration)"),
                "A step at the top-level duration should read the field, not a baked copy of it.");
            Assert.That(lines[1], Does.Contain("0.1f"));
        }

        /// <summary>
        /// CanvasGroup belongs to <c>UnityEngine.CanvasGroup</c> (UIModule, always present) — it used to share
        /// the hardcoded <c>using UnityEngine.UI;</c> line with Graphic/Image, so in a project without
        /// com.unity.ugui installed, this package's generated file would fail to compile with CS0246 over a
        /// namespace it never actually referenced.
        /// </summary>
        [TestCase("CanvasGroup", "DOFade", null)]
        [TestCase("Transform", "DOMove", null)]
        [TestCase("RectTransform", "DOAnchorPos", null)]
        [TestCase("Image", "DOFade", "using UnityEngine.UI;")]
        [TestCase("Graphic", "DOColor", "using UnityEngine.UI;")]
        public void GeneratedScript_EmitsAUguiUsingOnlyForUguiTargets(
            string targetKind, string tweenKind, string expectedUsing)
        {
            var spec = DOTweenSkills.ResolveRuntimeTweenSpec(targetKind, tweenKind);
            Assume.That(spec, Is.Not.Null, $"{targetKind}/{tweenKind} is no longer a supported recipe.");

            Assert.That(spec.extraUsing, Is.EqualTo(expectedUsing),
                $"{targetKind} lives in {(expectedUsing == null ? "UnityEngine" : "UnityEngine.UI")}, " +
                "and generation is a pure string operation — the target's own namespace is the only " +
                "thing that may decide this.");
        }

        // ---------- B7: HasProperty is not a color guard ----------

        /// <summary>
        /// <c>optimize_find_duplicate_materials</c> uses <c>HasProperty</c> to guard its <c>GetColor</c>, and
        /// the former returns true for a property of *any* type — so on the hidden/decal shaders that litter a
        /// URP project, the read still executed, and the engine logged a native "doesn't have a color
        /// property" error for every material. That's a native log rather than a thrown exception, so the
        /// try/catch around it caught nothing, and a single read-only analysis pass flooded the console red.
        ///
        /// <para>What's pinned down here is the new guard's discriminating behavior, verified against a
        /// built-in shader: it answers false for a float-typed property, where <c>HasProperty</c> answers true;
        /// it still answers true for a genuine color. The console itself isn't asserted on — reproducing the
        /// old error would need a purpose-built shader asset, and this assembly doesn't reference
        /// UnityEngine.TestRunner's LogAssert to constrain expectations.</para>
        /// </summary>
        [Test]
        public void MaterialColorGuard_DiscriminatesByPropertyTypeNotJustName()
        {
            var probe = FindMaterialProbe();
            Assume.That(probe.material, Is.Not.Null,
                "No stock shader with both a Color and a float property was found in this project.");

            try
            {
                Assume.That(probe.material.HasProperty(probe.floatProperty), Is.True,
                    $"{probe.floatProperty} is not declared by {probe.material.shader.name} in this Unity version.");

                Assert.That(OptimizationSkills.HasReadableColor(probe.material, probe.floatProperty), Is.False,
                    $"'{probe.floatProperty}' is a float on {probe.material.shader.name}: the guard must " +
                    "refuse it. HasProperty says yes, which is precisely why it was the wrong guard.");
                Assert.That(OptimizationSkills.HasReadableColor(probe.material, probe.colorProperty), Is.True,
                    $"'{probe.colorProperty}' is a real colour — the guard must not over-reject, or every " +
                    "duplicate-material key collapses to \"none\".");
            }
            finally
            {
                Object.DestroyImmediate(probe.material);
            }
        }

        private static (Material material, string colorProperty, string floatProperty) FindMaterialProbe()
        {
            var candidates = new[]
            {
                ("Standard", "_Color", "_Glossiness"),
                ("Sprites/Default", "_Color", "_EnableExternalAlpha"),
                ("Legacy Shaders/Transparent/Diffuse", "_Color", "_Cutoff"),
            };

            foreach (var (shaderName, colorProperty, floatProperty) in candidates)
            {
                var shader = Shader.Find(shaderName);
                if (shader == null) continue;
                var material = new Material(shader);
                if (material.HasProperty(colorProperty) && material.HasProperty(floatProperty))
                    return (material, colorProperty, floatProperty);
                Object.DestroyImmediate(material);
            }
            return (null, null, null);
        }
    }
}

// Producer:Betsy
