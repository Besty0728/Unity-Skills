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
    /// 2026-08-22 复审修复批次的回归覆盖：
    /// <list type="bullet">
    /// <item>#7 —— SkillErrorClassifier.PropertyNotOnTarget 里裸的 "material"/"shader" 锚点</item>
    /// <item>#12 —— 五个批量技能的 Outputs 声明里漏了 failCount</item>
    /// <item>#13 —— SmartReferenceBind 里硬编码的 "SEMANTIC_INVALID" 字面量</item>
    /// <item>#14 —— PrimeTweenSkills.Stringify 没有展平 struct 类型的配置值</item>
    /// </list>
    /// #11（ModelSkills 可写检查的顺序）与 #17（UnitySkillsWindow 的 EditorUiScheduler 路由）在这里
    /// 没有覆盖，原因见修复报告：#11 需要一个真实的 .fbx 资源（本仓库没有）外加一个 VCS provider
    /// mock，才能观察到它调整顺序所围绕的 MakeEditable 副作用；#17 需要一个活的 UI Toolkit panel
    /// （已 attach 的 VisualElement.panel），EditorUiScheduler.RepeatSafe 的守卫才会真的执行回调，
    /// 而那意味着要在测试里立起一个真正的 EditorWindow。
    ///
    /// <para>另加 2026-08-23 的 8090 真机批次：
    /// <list type="bullet">
    /// <item>L3 —— Addressables 的 group/profile 查找失败被错误路由到 gameobject_find / asset_find</item>
    /// <item>L4 —— YooAsset 运行时校验作业被路由到根本看不见它们的 job_list</item>
    /// <item>L5 —— prefab_set_property 没有 Quaternion 分支（所有 localRotation 写入都失败）</item>
    /// <item>L7 —— 冒烟探针的夹具处理：会抛异常的 lightmap getter + 放宽后的白名单</item>
    /// </list>
    /// L1/L2（Addressables 组改名回显、group_create 的必填 groupName）放在它们所属的端点旁：
    /// AddressablesSkillsTests 需要装了包才能观察到改名，而 schema 必填断言并入了
    /// WorkflowPersistenceTests 里已有的那份清单。L6（十二个技能缺 RequiresInput）是全注册表扫描，
    /// 因此归 SkillMetadataGuardTests。</para>
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

        // ---------- #7：收紧 SkillErrorClassifier.PropertyNotOnTarget 的锚点 ----------

        /// <summary>
        /// 这三条字面量过去会命中 material_get_properties（前两条，经裸的 "shader"/"material" 子串检查）
        /// 或 component_get_serialized_properties（第三条，经裸的 "serialized" 检查），尽管它们指的都不是
        /// 任何 material/shader/component 实例上真实存在的属性——它们是 GraphicsSettings/ShaderGraph
        /// 的内部查找失败。现在三条都会落到通用的"属性未找到"兜底分支，其唯一 SuggestedFix 以
        /// component_get_properties 打头。
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
        /// #7 的修复不得回退的部分：真正的 material/shader 属性查找失败，依旧照原样路由到
        /// material_get_properties。
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

        /// <summary>真正的组件序列化属性查找失败，必须保留它修复前的那条路由。</summary>
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

        // ---------- #12：批量技能必须声明 failCount，因为 BatchExecutor 一定会返回它 ----------

        /// <summary>
        /// 这五个都走 BatchExecutor.Execute，其信封无条件携带 totalItems/successCount/failCount/results
        /// （见 BatchExecutor.cs）——Outputs 就该照实声明。
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

        // ---------- #13：SmartReferenceBind 的 SEMANTIC_INVALID 现在取自 SkillParamUtil ----------

        private const string BindTargetName = "__review_fix_bind_target__";
        private const string BindSourceName = "__review_fix_bind_source__";

        /// <summary>
        /// sharedMaterials 是 Material[]，而 sourceTag/sourceName 只可能解析到 GameObject，
        /// 所以这个元素类型永远填不满，应当一开始就以 SEMANTIC_INVALID 拒掉，而不是静默清空该字段。
        /// 这条测试经 SkillRouter 端到端地检验 SmartSkills.cs 现在从
        /// SkillParamUtil.SemanticInvalidCode 取用的那个字面量，而不是孤立地再断言一遍字符串常量。
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

        // ---------- #14：PrimeTweenSkills.Stringify 必须展平 struct 值，而不只是枚举 ----------

        private enum ProbeEnum { First, Second }

        private struct ProbeStruct
        {
            public int Value;
            public override string ToString() => $"probe:{Value}";
        }

        /// <summary>
        /// PrimeTween.UpdateType 的形状:enum-like struct,状态在私有枚举字段里,不覆写
        /// ToString()——默认 ValueType.ToString() 只给类型名,值不可见。
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
        /// 真正的 bug：PrimeTween 较新版本的 UpdateType 是 struct 而不是 enum，于是它从原先只处理枚举的
        /// 分支漏了下去，配置的匿名对象序列化器为它输出了 "{}"。
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
        /// 字面上"非空字符串"不够:8090 实测曾输出常量 "PrimeTween.UpdateType"(类型全名),
        /// 调用方拿不到真实配置值。enum-like struct 必须解包到枚举字段的名字。
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
        // 2026-08-23 8090 真机批次
        // ================================================================================

        // ---------- L3：Addressables 的 group/profile 查找失败指向了错误的读取端 ----------

        /// <summary>
        /// 一个在 AddressableAssetSettings 资源里并不存在的 group/profile 名，当时由分类器的通用分支作答：
        /// "Group not found: X" 里没有任何资源类名词，于是落到 gameobject_find / scene_get_hierarchy；
        /// 而 "Profile not found: X" 之所以命中"资源标记"那条分支，纯粹因为 "profile" 里含子串 "file"，
        /// 于是落到 asset_find。这两个都解析不了一个只存在于 settings 资源里的名字。
        ///
        /// <para>第三条断言才是让这个测试保持诚实的那条：它重新推导"仅凭分类器"对同一条消息还会怎么说，
        /// 从而使本测试不会因为错误路由出于某个无关原因悄悄消失而通过——真正该起作用的是声明。</para>
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

        // ---------- L4：YooAsset 运行时校验作业不是 AsyncJobService 的作业 ----------

        /// <summary>
        /// 未知 jobId 的回答过去只是一句干巴巴的 "…not found"，分类器的作业分支把它变成了 job_list
        /// 外加一句"id 撑不过一次 domain reload"。两者都错了：这些作业活在 YooAssetSkills 自己的字典里，
        /// job_list/job_status 根本看不见；而它们会被持久化到 EditorPrefs 并在 reload 之后恢复——
        /// 于是调用方被指向了一张永远不可能装着这个 id 的表，理由还是一条并不适用的生命周期规则。
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

            // Canary:裸分类器必须仍然给不出正确指引(具体错到哪个技能会随分类器演进漂移,
            // 不钉死——曾钉 job_list,分类器基线一变就误报)。一旦这条失败,说明分类器自己
            // 学会了正确路由,层-1 覆盖可能已冗余,应重新评估而不是直接删。
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

        // ---------- L5：prefab_set_property 写不了 Quaternion ----------

        private const string PrefabProbeFolder = "Assets/Temp";
        private const string PrefabProbeName = "__review_fix_prefab_probe__";
        private const string PrefabProbePath = PrefabProbeFolder + "/" + PrefabProbeName + ".prefab";

        /// <summary>
        /// m_LocalRotation 是 Quaternion，而 SetSerializedPropertyValue 当时没有 Quaternion 分支——
        /// 于是最常见的那种 prefab 写入落到了 default 分支，回来一句
        /// "Failed to set value … (type: Quaternion)"。这里通过重新加载资源来断言而不看响应，
        /// 因为缺的从来不是那个成功信封。
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

                // 而且是真的落到了磁盘上，不只是在已加载的资源里：绕 Y 轴 90° 会序列化成
                // (0, 0.707…, 0, 0.707…)，所以这里对数字的断言留了足够余量，以容忍 Unity 的浮点取舍。
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
        /// 同一个修复的另一半：本技能确实无法从文本写入的属性类型，必须把这件事说清楚。
        /// "Failed to set value 'x'" 把责任推给了值，换来一个未分类的 SKILL_ERROR + 中止，
        /// 于是 agent 无从区分"把你的值改个格式"和"这个字段得改用 assetReferencePath"。
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

        // ---------- L7：冒烟探针的夹具处理 ----------

        /// <summary>
        /// 场景没有 Lighting Settings 资源时，Lightmapping.lightingSettings 是抛异常而不是返回 null，
        /// 于是那个本该用 Unity 内置默认值作答的判空分支根本没跑到，这条只读查询在任何默认工程上都算
        /// 一次冒烟失败。两种情况下本测试都通过——挂了资源就走另一条分支——因为钉住的是
        /// "永远不报错"，而不是由哪条分支作答。
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
        /// 冒烟探针的夹具白名单。必须命中的那几行，是干净工程会产生的那些确切拒绝
        /// （没有 NetworkManager、处于 EditMode 而非 PlayMode）；必须不命中的那几行，
        /// 则是防止白名单退化成"一律跳过"的关键——即在册技能出现的另一种失败，
        /// 以及不在册技能报出的在册措辞。
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
        // 2026-08-23 DOTween Pro 1.0.381 真机批次（B1–B7）
        //
        // 这里没有任何用例需要装 DOTween。真正需要它的那两样东西（一个真实的 DOTweenAnimation，
        // 以及那些错误引用的 Ease/LoopType 枚举），改为针对技能实际经过的那些接缝、
        // 用形状相同的替身类型来断言——每条测试都注明了它替代的是哪个真机症状。
        // ================================================================================

        // ---------- B1：全场景列举给出的索引，其消费方并不按它取用 ----------

        private const string IndexProbeA = "__dotween_index_probe_a__";
        private const string IndexProbeB = "__dotween_index_probe_b__";

        /// <summary>
        /// <c>dotween_pro_list_animations</c> 的全场景分支把 <c>FindHelper.FindAll</c> 的结果
        /// （文档明确说无序）分组后给出一个递增计数，而所有 setter 都是按
        /// <c>gameObject.GetComponents(type)</c> 索引的。当一个 GameObject 上挂了多个 DOTweenAnimation 时，
        /// 两者就不一致了——真实工程里某个 GameObject 被列举成 [Fade 0.3, Scale 0.6, Fade 0.4]，
        /// 而它的 GetComponents 顺序是 [Scale 0.6, Fade 0.3, Fade 0.4]——于是"先列举再设置"的 agent
        /// 改到的是另一个组件，而不是它读到的那个，且两次调用都报成功。
        ///
        /// <para>这里拿 BoxCollider 而不是 DOTweenAnimation 来断言：被测性质是"索引等于该组件在
        /// GetComponents 中的位置"，与类型无关；输入还故意做了反序，好让残留的递增计数无法通过。</para>
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
        /// 全场景形态：来自多个 GameObject 的组件交错到达。索引必须按 GameObject 重新从头计数，
        /// 因为那才是 setter 里那个索引的含义。
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

        // ---------- B2：数值类 setter 什么都收，且什么都不回显 ----------

        private const string DOTweenTargetProbe = "__dotween_setter_probe__";

        /// <summary>
        /// duration 传 -1 会被原样写入并回 <c>{"success":true}</c>。这里经路由（而非直接调方法）断言拒绝，
        /// 以便钉住调用方真正收到的 errorCode/retryStrategy；而且故意不给一个可解析的目标：
        /// 本技能永远不可能接受的值，应当在查场景之前就被拒掉——这也正是它在没装 DOTween Pro 的
        /// 工程上依然可观测的原因。
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
        /// loops 传 0 会比其它情况早一层被拒：RequiresInput 分组 "loops|loopType" 把数值 0 读作"根本没给值"，
        /// 于是校验层在技能执行前就回 "Provide one of: loops, loopType"。它依然是一次拒绝、错误码与
        /// 重试策略都相同——只是措辞来自分组而非循环取值域，这也是它要与上面那些用例分开断言的原因
        /// （那些用例钉的是 validValues）。
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

        /// <summary>-1 是 DOTween 的"无限循环"标记，必须继续被接受；守卫把它拒掉，
        /// 就是同一个缺陷换了个符号。</summary>
        [TestCase("{\"target\":\"" + DOTweenTargetProbe + "\",\"loops\":-1}")]
        [TestCase("{\"target\":\"" + DOTweenTargetProbe + "\",\"loops\":3}")]
        [TestCase("{\"target\":\"" + DOTweenTargetProbe + "\",\"loopType\":\"Yoyo\"}")]
        public void DOTweenProSetLoops_InDomainValue_IsNotRejectedAsSemanticInvalid(string body)
        {
            var response = JObject.Parse(SkillRouter.Execute("dotween_pro_set_loops", body));

            // 没装 DOTween Pro 时这会落到 MISSING_PACKAGE；装了则落到组件缺失 / GameObject 缺失。
            // 无论哪种，被归咎的都不能是那个*值*。
            Assert.That(response["errorCode"]?.ToString(), Is.Not.EqualTo("SEMANTIC_INVALID"),
                response.ToString(Formatting.None));
        }

        /// <summary>
        /// "缺省"陷阱。<c>float duration = 1f</c> 分不清"传了 1"和"什么都没传"，于是一次只想指向某个动画的
        /// 调用会静默把它重置成 1 秒；<c>fieldValue = null</c> 会清掉被点名的字段；
        /// <c>int loops = 1</c> 把一次只设 loopType 的调用变成了"顺便别再循环了"。这三者现在都会拒绝——
        /// 而且在 dryRun/schema 层就拒，好让调用方在任何东西被执行之前就知道。
        /// </summary>
        [TestCase("dotween_pro_set_duration", "{\"target\":\"" + DOTweenTargetProbe + "\"}", "MISSING_PARAM")]
        [TestCase("dotween_pro_set_animation_field",
            "{\"target\":\"" + DOTweenTargetProbe + "\",\"fieldName\":\"id\"}", "MISSING_PARAM")]
        // set_loops 的两半各自单看都是可选的，所以"两个都没给"是校验层给出的分组判定
        // （SEMANTIC_INVALID），而不是逐参数的 MISSING_PARAM。
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
        /// stagger 的守卫，在辅助函数层面验：<c>dotween_pro_stagger_animations</c> 先查包再查参数
        /// （没有 Pro 它反正什么都加不了），所以端到端的拒绝在这里观察不到——这里钉的是技能所调用的
        /// 那道守卫本身。
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
        /// 完整的循环取值域，含 0——上面那个端到端用例观察不到 0，因为分组校验会先把数值 0 截走。
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

        // ---------- B3：对外宣称的 capacity 参数，在已安装的 DOTween 上并没有对应字段 ----------

        private enum ProbeEaseEnum { Linear, OutQuad, Unset, INTERNAL_Custom }
        private enum ProbeLoopEnum { Restart, Yoyo, Incremental }
        private enum ProbeLogEnum { Default, Verbose, ErrorsOnly }

        /// <summary>DOTween Pro 1.0.381 的 DOTweenSettings 实际具备的形状：没有 capacity 字段。</summary>
        // 这里每个字段都是靠反射写入的，编译器看不见（CS0649）。
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
        /// <c>dotween_settings_configure</c> 对外宣称支持 tweenersCapacity / sequencesCapacity，
        /// 而在 DOTween Pro 1.0.381 上那个资源两个都没有。两处写入都只被一个裸的
        /// <c>if (SetFieldByName(...))</c> 包着，false 分支什么都不做，于是调用回的是
        /// <c>success:true, modified:[]</c>——与"已接受、本来就是对的"完全无法区分。
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

        /// <summary>另一半：在确实声明了这些字段的版本上，它们依然会被写入。</summary>
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

        /// <summary>非法枚举值必须列出可接受的取值，且必须什么都没写进去。</summary>
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
        /// dotween_settings_validate 本来就把 capacity &lt;= 0 当作一个问题上报，
        /// 所以真写进去会让本包在下次读取时把自己刚做的修改判为非法。
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

        // ---------- B4：枚举拒绝没带词表，而词表必须是真的 ----------

        // 只被反射读取——词表/可设置名辅助函数从不写它们（CS0649）。
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
        /// ease/loopType 拒绝响应里的 <c>validValues</c> 列表，是从已安装 DOTween 所声明的枚举上反射来的，
        /// 因此不会随资源版本漂移。其中有两个成员被故意扣下：<c>Unset</c> 的含义是"继承工程默认值"，
        /// 而这个 setter 表达不了；<c>INTERNAL_Custom</c> 则是 easeCurveJson 那条路径植入的标记——
        /// 不带曲线地点名它，只会得到一个背后没有曲线的自定义 ease。
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
        /// 未知 fieldName 的拒绝响应会列出哪些字段可设，其中必须排除那些由专用技能负责的字段——
        /// 在这里提供 `duration` 只会把调用方引向一次拒绝。
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

        // ---------- B5 / B6：生成出来的脚本 ----------

        /// <summary>
        /// 每份生成的 Sequence 都声明了 <c>[SerializeField] private float duration</c>，随后又把每一步的
        /// duration 以字面量烘死进去，于是这个字段无人引用——本包刚写出来的文件上就报 CS0414。
        /// 现在与顶层值不同的步骤仍然用自己的字面量，而与之相同的步骤读该字段，
        /// 这样默认配方下 Inspector 上那个旋钮仍然有效。
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
        /// CanvasGroup 属于 <c>UnityEngine.CanvasGroup</c>（UIModule，始终存在）——它当时和 Graphic/Image
        /// 共用了那句硬编码的 <c>using UnityEngine.UI;</c>，于是在没装 com.unity.ugui 的工程里，
        /// 本包生成的文件会因为一个它自己根本没引用的命名空间而编译失败于 CS0246。
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

        // ---------- B7：HasProperty 不是颜色守卫 ----------

        /// <summary>
        /// <c>optimize_find_duplicate_materials</c> 用 <c>HasProperty</c> 来守它的 <c>GetColor</c>，
        /// 而前者对*任意*类型的属性都返回 true——于是在 URP 工程里满地都是的 hidden/decal 着色器上，
        /// 读取照样执行，引擎为每个材质打出一条原生的 "doesn't have a color property" 错误。
        /// 那是原生日志而非抛出的异常，所以外面的 try/catch 什么都没捕到，一次只读分析把控制台刷红了。
        ///
        /// <para>这里钉的是新守卫的判别性质，在内置着色器上验：float 类型的属性它答 false，
        /// 而 <c>HasProperty</c> 答 true；真正的颜色它仍然答 true。控制台本身不做断言——
        /// 要复现旧错误需要一个专门制作的着色器资源，而本程序集没有引用 UnityEngine.TestRunner 的
        /// LogAssert 来限定预期。</para>
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
