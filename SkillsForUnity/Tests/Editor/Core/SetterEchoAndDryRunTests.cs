using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
#if UGUI
using UnityEngine.UI;
#endif

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// camera / light / selectable 这几个 setter：真的写了什么、声称写了什么，以及在这一切发生之前
    /// dryRun 层怎么说。
    ///
    /// <para>覆盖两类相反的故障。setter 可能写得比它声称的少——被静默丢弃的 alpha、从 switch 漏下去的
    /// 枚举值；也可能声称得比它写的少，这正是 <c>applied</c>/<c>skipped</c> 汇报存在的意义：调用方对
    /// Directional 灯设了 <c>range</c>，而这种灯没有 range，若响应里没有 <c>skipped</c> 条目，
    /// 它就和"设成功了"的响应完全无法区分。</para>
    ///
    /// <para>每一次写入都拿活对象来核，绝不从响应反推。响应本身就是被测对象，信它会让断言变成循环论证。</para>
    /// </summary>
    [TestFixture]
    public class SetterEchoAndDryRunTests
    {
        private SkillsOperatingMode _savedMode;
        private SurfaceProfileKind _savedProfile;

        [SetUp]
        public void SetUp()
        {
            _savedMode = SkillsModeManager.CurrentMode;
            _savedProfile = SkillsSurfaceProfile.Current;
            // Camera/Light/UI 的写操作会被非 full 档位撤掉，也会在非 Bypass 模式下被拦。两者都是
            // 跨工程共享的全局 EditorPrefs 状态，所以在这里显式钉住、teardown 里还原，绝不假设。
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
            GameObjectFinder.InvalidateCache();
        }

        /// <summary>成功信封里技能载荷在 <c>result</c> 之下，不在顶层。</summary>
        private static JObject Payload(string skill, string body)
        {
            var response = JObject.Parse(SkillRouter.Execute(skill, body));
            Assert.That(response["errorCode"], Is.Null,
                $"{skill} failed: {response.ToString(Formatting.None)}");

            var result = response["result"] as JObject;
            Assert.That(result, Is.Not.Null,
                "Success envelope shape changed — expected the skill payload under 'result'. Top-level keys: " +
                string.Join(", ", response.Properties().Select(p => p.Name)));
            return result;
        }

        private static string[] StringArray(JToken token) =>
            (token as JArray)?.Select(t => t.ToString()).ToArray() ?? Array.Empty<string>();

        // ---------- camera_set_properties ----------

        /// <summary>
        /// alpha 通道当初根本没有对应参数，于是背景透明度经这个技能完全无法设置——而又因为另外三个
        /// 通道可写，调用方设 bgR/bgG/bgB 时拿到的颜色，其 alpha 会静默沿用原先的值。
        /// </summary>
        [Test]
        public void CameraSetProperties_BgA_WritesTheAlphaChannel()
        {
            var go = new GameObject("__cam_bga__", typeof(Camera));
            try
            {
                GameObjectFinder.InvalidateCache();
                var cam = go.GetComponent<Camera>();
                cam.backgroundColor = new Color(0.1f, 0.2f, 0.3f, 1f);

                var payload = Payload("camera_set_properties",
                    "{\"name\":\"__cam_bga__\",\"bgA\":0.25}");

                Assert.That(cam.backgroundColor.a, Is.EqualTo(0.25f).Within(0.001f),
                    "bgA must reach the camera — an alpha-less setter cannot express a transparent clear colour.");
                Assert.That(cam.backgroundColor.r, Is.EqualTo(0.1f).Within(0.001f),
                    "Channels the caller did not name must keep their current value.");
                Assert.That(cam.backgroundColor.g, Is.EqualTo(0.2f).Within(0.001f));
                Assert.That(cam.backgroundColor.b, Is.EqualTo(0.3f).Within(0.001f));

                // `applied` 列的是参数名而非属性名："backgroundColor" 是 Camera 的属性、从来不是合法
                // 入参，回显它对调用方毫无可操作价值。必须出现的是它实际发出的那个参数。
                Assert.That(StringArray(payload["applied"]), Does.Contain("bgA"),
                    "An alpha-only call still writes the colour, so it must be reported as applied.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }

        [Test]
        public void CameraSetProperties_EchoesEveryPropertyAndOnlyTheAppliedOnes()
        {
            var go = new GameObject("__cam_echo__", typeof(Camera));
            try
            {
                GameObjectFinder.InvalidateCache();

                var payload = Payload("camera_set_properties",
                    "{\"name\":\"__cam_echo__\",\"fieldOfView\":42,\"clearFlags\":\"Depth\"}");

                // 声明的 Outputs 就是 agent 据以做计划的契约；响应缺了其中任何一个，
                // 都会把调用方推去调第二个技能，只为拿一个它本该已经拿到的值。
                Assert.That(SkillRouter.TryGetSkill("camera_set_properties", out var info), Is.True);
                var missing = info.Outputs.Where(key => payload[key] == null).ToArray();
                Assert.That(missing, Is.Empty,
                    $"Response is missing declared outputs: {string.Join(", ", missing)}");

                var applied = StringArray(payload["applied"]);
                Assert.That(applied, Is.EquivalentTo(new[] { "fieldOfView", "clearFlags" }),
                    "'applied' must name exactly the parameters written — listing an untouched " +
                    "property is how a caller comes to believe a write happened.");
                // 改用本次调用没发的某个颜色参数来验："backgroundColor" 已不再是 `applied` 可能出现的
                // 名字，断言它不存在证明不了任何事。
                Assert.That(applied, Does.Not.Contain("bgA"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }

        [Test]
        public void CameraSetProperties_NoParameters_AppliesNothingAndStillEchoes()
        {
            var go = new GameObject("__cam_noop__", typeof(Camera));
            try
            {
                GameObjectFinder.InvalidateCache();

                var payload = Payload("camera_set_properties", "{\"name\":\"__cam_noop__\"}");

                Assert.That(StringArray(payload["applied"]), Is.Empty,
                    "A call naming no properties applied none of them.");
                Assert.That(payload["fieldOfView"], Is.Not.Null,
                    "The echo is unconditional — it is how a caller reads current state after a no-op.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }

        // ---------- light_set_properties ----------

        [Test]
        public void LightSetProperties_A_WritesTheAlphaChannel()
        {
            var go = new GameObject("__light_a__", typeof(Light));
            try
            {
                GameObjectFinder.InvalidateCache();
                var light = go.GetComponent<Light>();
                light.type = LightType.Point;
                light.color = new Color(1f, 1f, 1f, 1f);

                var payload = Payload("light_set_properties",
                    "{\"name\":\"__light_a__\",\"a\":0.5}");

                Assert.That(light.color.a, Is.EqualTo(0.5f).Within(0.001f));
                Assert.That(light.color.r, Is.EqualTo(1f).Within(0.001f),
                    "Each channel defaults to the light's current value, so an alpha-only call " +
                    "must not reset r/g/b to zero.");
                // 与 camera_set_properties 同一套参数名契约：调用方发的是 `a`，回来的就得是 `a`——
                // "color" 是 Light 的属性，不是它能再发一次的入参。
                Assert.That(StringArray(payload["applied"]), Does.Contain("a"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }

        /// <summary>
        /// <c>skipped</c> 的那一半。Directional 灯没有 range，调用方发来的值就无处可去——而一个不说明
        /// 这件事的响应，与"写成功了"的响应完全一样，调用方于是会去调一盏"无视了设置"的灯。
        /// </summary>
        [Test]
        public void LightSetProperties_RangeOnDirectionalLight_IsReportedSkipped_NotSilentlyDropped()
        {
            var go = new GameObject("__light_dir__", typeof(Light));
            try
            {
                GameObjectFinder.InvalidateCache();
                var light = go.GetComponent<Light>();
                light.type = LightType.Directional;

                var payload = Payload("light_set_properties",
                    "{\"name\":\"__light_dir__\",\"range\":50,\"intensity\":2}");

                var applied = StringArray(payload["applied"]);
                var skipped = StringArray(payload["skipped"]);

                Assert.That(applied, Does.Contain("intensity"),
                    "The parameters the light does carry must still be applied.");
                Assert.That(applied, Does.Not.Contain("range"));
                Assert.That(skipped.Any(s => s.StartsWith("range", StringComparison.Ordinal)), Is.True,
                    $"A range sent to a Directional light must be reported as skipped. skipped=[{string.Join(" | ", skipped)}]");
                Assert.That(light.intensity, Is.EqualTo(2f).Within(0.001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }

        [Test]
        public void LightSetProperties_RangeOnPointLight_IsApplied_NotSkipped()
        {
            // 只有当同一个参数在"确实有该属性"的灯型上被真正应用，上面那条 skipped 汇报才有意义。
            var go = new GameObject("__light_point__", typeof(Light));
            try
            {
                GameObjectFinder.InvalidateCache();
                var light = go.GetComponent<Light>();
                light.type = LightType.Point;

                var payload = Payload("light_set_properties",
                    "{\"name\":\"__light_point__\",\"range\":50}");

                Assert.That(StringArray(payload["applied"]), Does.Contain("range"));
                Assert.That(StringArray(payload["skipped"]), Is.Empty);
                Assert.That(light.range, Is.EqualTo(50f).Within(0.001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }

        [Test]
        public void LightSetProperties_SpotAngleOnPointLight_IsReportedSkipped()
        {
            var go = new GameObject("__light_spotless__", typeof(Light));
            try
            {
                GameObjectFinder.InvalidateCache();
                go.GetComponent<Light>().type = LightType.Point;

                var payload = Payload("light_set_properties",
                    "{\"name\":\"__light_spotless__\",\"spotAngle\":45}");

                Assert.That(StringArray(payload["skipped"]).Any(s => s.StartsWith("spotAngle", StringComparison.Ordinal)),
                    Is.True, "Only Spot lights have a cone angle; sending one elsewhere must be reported.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }

        // ---------- light_get_properties alias ----------

        /// <summary>
        /// setter 叫 <c>light_set_properties</c>，调用方自然会去找 <c>light_get_properties</c> 当读取端。
        /// 这个别名必须是真别名：元数据一致、载荷一致。一个跑偏的别名比没有别名更糟，
        /// 因为两个名字都会出现在 manifest 里，而没有任何东西说明哪个才权威。
        /// </summary>
        [Test]
        public void LightGetProperties_IsATrueAliasOfLightGetInfo()
        {
            Assume.That(SkillRouter.HasSkill("light_get_info"), Is.True);
            Assert.That(SkillRouter.HasSkill("light_get_properties"), Is.True,
                "light_get_properties is the name callers reach for once the setter is light_set_properties.");

            Assert.That(SkillRouter.TryGetSkill("light_get_info", out var info), Is.True);
            Assert.That(SkillRouter.TryGetSkill("light_get_properties", out var alias), Is.True);

            Assert.That(alias.Outputs, Is.EqualTo(info.Outputs),
                "An alias reporting different outputs is a different skill wearing the same description.");
            Assert.That(alias.ReadOnly, Is.EqualTo(info.ReadOnly));
            Assert.That(alias.Category, Is.EqualTo(info.Category));
            Assert.That(alias.RequiresInput, Is.EqualTo(info.RequiresInput));

            var go = new GameObject("__light_alias__", typeof(Light));
            try
            {
                GameObjectFinder.InvalidateCache();
                go.GetComponent<Light>().intensity = 3.5f;

                var viaInfo = Payload("light_get_info", "{\"name\":\"__light_alias__\"}");
                var viaAlias = Payload("light_get_properties", "{\"name\":\"__light_alias__\"}");

                Assert.That(JToken.DeepEquals(viaAlias, viaInfo), Is.True,
                    $"The alias returned a different payload.\ninfo ={viaInfo.ToString(Formatting.None)}" +
                    $"\nalias={viaAlias.ToString(Formatting.None)}");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }

        // ---------- dryRun: target group ----------

        /// <summary>
        /// 一个声明了 RequiresInput "gameObject"、但接受多个不同名定位参数（name / path / instanceId /
        /// entityId）的技能，没有任何"单参数必填"可以强制——每个定位参数单看都是可选的，于是空请求体
        /// 通过了校验，agent 被告知这次调用已就绪。分组校验的意义就在于让"你没指定目标"这句话说得出口。
        /// </summary>
        [Test]
        public void DryRun_EmptyBodyOnGameObjectTargetSkill_ReportsSemanticErrorOnTarget()
        {
            var dry = JObject.Parse(SkillRouter.DryRun("camera_set_properties", "{}"));

            Assert.That(dry["valid"]?.Value<bool>(), Is.False,
                "An empty body names no camera; saying valid:true sends the agent into an execute that cannot work.");

            var semantic = dry["validation"]?["semanticErrors"] as JArray;
            Assert.That(semantic, Is.Not.Null.And.Not.Empty,
                $"Expected a semantic error for the missing target: {dry.ToString(Formatting.None)}");

            var targetError = semantic.FirstOrDefault(e => e["field"]?.ToString() == "target");
            Assert.That(targetError, Is.Not.Null,
                "The error belongs to no single parameter — the caller named none of them — so it is " +
                $"reported under field 'target'. Got: {semantic.ToString(Formatting.None)}");
            Assert.That(targetError["error"]?.ToString(), Does.Contain("name"),
                "The message must enumerate the locators that would satisfy the group.");
        }

        [Test]
        public void DryRun_BodyNamingATarget_IsValid()
        {
            // 没有这条，上面那个断言可以被一个"拒绝一切请求体"的实现轻易满足。
            var dry = JObject.Parse(SkillRouter.DryRun("camera_set_properties",
                "{\"name\":\"__anything__\",\"fieldOfView\":42}"));

            Assert.That(dry["valid"]?.Value<bool>(), Is.True,
                $"A body naming a target must validate — the group check is about absence, not existence: " +
                $"{dry.ToString(Formatting.None)}");
            Assert.That(dry["validation"]?["semanticErrors"]?.Type ?? JTokenType.Null,
                Is.EqualTo(JTokenType.Null));
        }

        [Test]
        public void DryRun_InstanceIdZero_CountsAsNoTarget()
        {
            // agent 经常照模板原样发 {"instanceId": 0}。定位层把 0 当作"未提供"，分组校验也必须如此，
            // 否则这道防线会被最常见的那个占位值直接绕过。
            var dry = JObject.Parse(SkillRouter.DryRun("camera_set_properties", "{\"instanceId\":0}"));

            var semantic = dry["validation"]?["semanticErrors"] as JArray;
            Assert.That(semantic?.Any(e => e["field"]?.ToString() == "target"), Is.True,
                $"instanceId 0 is the locator layer's 'not supplied' value: {dry.ToString(Formatting.None)}");
        }

        [Test]
        public void DryRun_BlankName_CountsAsNoTarget()
        {
            var dry = JObject.Parse(SkillRouter.DryRun("camera_set_properties", "{\"name\":\"\"}"));

            var semantic = dry["validation"]?["semanticErrors"] as JArray;
            Assert.That(semantic?.Any(e => e["field"]?.ToString() == "target"), Is.True,
                $"An empty string is not a target: {dry.ToString(Formatting.None)}");
        }

        /// <summary>
        /// 所有声明了 gameObject 形态目标 token 的技能，对空请求体都必须给出同样的回答。候选集从注册表
        /// 推导而非手写清单，这样以后新增的技能不必改本测试就已被覆盖——而某个技能的定位参数一旦不再与
        /// token 词表相交，会在这里暴露出来，而不是悄悄失去防护。
        /// </summary>
        [Test]
        public void DryRun_EveryGameObjectTargetSkill_RejectsAnEmptyBodyUnderSomeField()
        {
            var candidates = SkillRouter.GetAllSkillsSnapshotUnfiltered()
                .Where(s => s.RequiresInput != null &&
                            s.RequiresInput.Any(t => string.Equals(t, "gameObject", StringComparison.OrdinalIgnoreCase)))
                .Where(s => s.SupportsDryRun)
                // 只收 `items` 的技能（所有 *_batch）或用自定义定位参数名的技能满足不了这套分组词表；
                // 规划器有意跳过它们而不是让它们变得不可调用，所以这里同样不在范围内。
                .Where(s => s.AllowedParameterSet != null &&
                            new[] { "name", "path", "instanceId", "entityId" }.Any(p => s.AllowedParameterSet.Contains(p)))
                .ToArray();

            // 用下界而非精确条数：注册表会随已安装的可选包变动，在这里断言相等只会以错误的理由变红。
            // 下界的作用是抓住"扫描范围悄悄缩到几个"的情况——一旦定位参数词表不再与这些技能的参数相交，
            // 它们会全部从 `candidates` 里掉出去，下面那条断言就会在一个近乎空集上轻松通过。
            Assume.That(candidates, Is.Not.Empty, "No gameObject-target skills found; the sweep would be empty.");
            Assert.That(candidates.Length, Is.GreaterThanOrEqualTo(20),
                $"Only {candidates.Length} skills qualified for the sweep. Around 90 declare " +
                "RequiresInput \"gameObject\", so a set this small means the locator-parameter " +
                "intersection broke and the check below is no longer covering anything.");

            var permissive = candidates.Where(s =>
            {
                var dry = JObject.Parse(SkillRouter.DryRun(s.Name, "{}"));
                return dry["valid"]?.Value<bool>() == true;
            }).Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

            Assert.That(permissive, Is.Empty,
                "These skills need a target but call an empty body valid, so an agent is told to " +
                $"execute a call that cannot resolve anything: {string.Join(", ", permissive.Take(20))}");
        }

        // ---------- dryRun: enum analyzers ----------

        [TestCase("camera_set_properties", "clearFlags", "NoSuchFlag")]
        [TestCase("camera_set_properties", "clearFlags", "99")]
        [TestCase("light_set_properties", "shadows", "NoSuchShadow")]
        [TestCase("light_set_properties", "shadows", "99")]
        public void DryRun_IllegalEnumValue_IsInvalidBeforeExecution(string skill, string parameter, string value)
        {
            Assume.That(SkillRouter.HasSkill(skill), Is.True);

            var dry = JObject.Parse(SkillRouter.DryRun(skill,
                "{\"name\":\"__probe__\",\"" + parameter + "\":\"" + value + "\"}"));

            Assert.That(dry["valid"]?.Value<bool>(), Is.False,
                $"dryRun is where an agent looks before committing; {parameter}='{value}' must fail there too, " +
                $"not only in the executed call: {dry.ToString(Formatting.None)}");

            var semantic = dry["validation"]?["semanticErrors"] as JArray;
            Assert.That(semantic?.Any(e => e["field"]?.ToString() == parameter), Is.True,
                $"The error must be attributed to '{parameter}': {dry.ToString(Formatting.None)}");
        }

        [TestCase("camera_set_properties", "clearFlags", "Depth")]
        [TestCase("camera_set_properties", "clearFlags", "skybox")]
        [TestCase("light_set_properties", "shadows", "Soft")]
        public void DryRun_LegalEnumValue_IsValid(string skill, string parameter, string value)
        {
            Assume.That(SkillRouter.HasSkill(skill), Is.True);

            var dry = JObject.Parse(SkillRouter.DryRun(skill,
                "{\"name\":\"__probe__\",\"" + parameter + "\":\"" + value + "\"}"));

            Assert.That(dry["valid"]?.Value<bool>(), Is.True,
                $"{parameter}='{value}' is a legal value (case-insensitively): {dry.ToString(Formatting.None)}");
        }

        /// <summary>
        /// 对本来就正确的请求体，这个分析器必须完全隐形。它有意不去改动 plan：给这些技能的每次 dryRun
        /// 都挂上 steps/changes，会改变那些什么都没做错的调用方拿到的回答——一个只该管校验的检查却动了
        /// 合法响应，那是披着 bugfix 外衣的破坏性变更。
        /// </summary>
        [Test]
        public void DryRun_AddingALegalEnum_LeavesValidationAndPlanBlocksUnchanged()
        {
            var withoutEnum = JObject.Parse(SkillRouter.DryRun("camera_set_properties",
                "{\"name\":\"__probe__\",\"fieldOfView\":42}"));
            var withLegalEnum = JObject.Parse(SkillRouter.DryRun("camera_set_properties",
                "{\"name\":\"__probe__\",\"fieldOfView\":42,\"clearFlags\":\"Depth\"}"));

            foreach (var block in new[] { "validation", "steps", "changes" })
            {
                Assert.That(JToken.DeepEquals(withLegalEnum[block] ?? JValue.CreateNull(),
                        withoutEnum[block] ?? JValue.CreateNull()),
                    Is.True,
                    $"The '{block}' block changed when a legal enum was added. The analyzer must only " +
                    $"stop saying valid:true to bad values, not alter the answer for good ones.\n" +
                    $"without={(withoutEnum[block] ?? JValue.CreateNull()).ToString(Formatting.None)}\n" +
                    $"with   ={(withLegalEnum[block] ?? JValue.CreateNull()).ToString(Formatting.None)}");
            }
        }

        [Test]
        public void DryRun_IsDeterministic_ForTheSameBody()
        {
            const string body = "{\"name\":\"__probe__\",\"clearFlags\":\"Depth\",\"fieldOfView\":42}";
            Assert.That(SkillRouter.DryRun("camera_set_properties", body),
                Is.EqualTo(SkillRouter.DryRun("camera_set_properties", body)),
                "Two identical previews must be byte-identical, or the agent cannot cache one.");
        }

        // ---------- ui_configure_selectable ----------
        //
        // Selectable/Button 来自 com.unity.ugui，不是本包的硬依赖。缺包时这些用例整段编译掉，
        // 而不是因为类型解析不到就把整个程序集——连同上面所有 camera/light/dryRun 用例——一起拖垮。
        // 与 Cinemachine 用例采用同一套 versionDefines + #if 形态。
#if UGUI

        /// <summary>
        /// 早先的写入守卫只检查四个 R 通道，于是只传 <c>normalG</c> 的调用会把整个颜色块丢掉，
        /// 却仍然回成功。
        /// </summary>
        [TestCase("normalG")]
        [TestCase("normalB")]
        [TestCase("normalA")]
        public void UIConfigureSelectable_SingleNonRedChannel_StillWritesTheColorBlock(string parameter)
        {
            var go = NewButton("__sel_channel__");
            try
            {
                var button = go.GetComponent<Button>();
                var colors = button.colors;
                colors.normalColor = new Color(0.5f, 0.5f, 0.5f, 1f);
                button.colors = colors;

                var response = JObject.Parse(SkillRouter.Execute("ui_configure_selectable",
                    "{\"name\":\"__sel_channel__\",\"" + parameter + "\":0.25}"));
                Assert.That(response["errorCode"], Is.Null, response.ToString(Formatting.None));

                var after = button.colors.normalColor;
                float written = parameter == "normalG" ? after.g : parameter == "normalB" ? after.b : after.a;
                Assert.That(written, Is.EqualTo(0.25f).Within(0.001f),
                    $"{parameter} alone did not reach the colour block — the guard is still red-only.");
                Assert.That(after.r, Is.EqualTo(0.5f).Within(0.001f),
                    "Channels the caller did not name keep their current value.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }

        [TestCase("normalA")]
        [TestCase("highlightedA")]
        [TestCase("pressedA")]
        [TestCase("disabledA")]
        public void UIConfigureSelectable_EveryAlphaParameter_Exists_AndWrites(string parameter)
        {
            var go = NewButton("__sel_alpha__");
            try
            {
                var button = go.GetComponent<Button>();

                var response = JObject.Parse(SkillRouter.Execute("ui_configure_selectable",
                    "{\"name\":\"__sel_alpha__\",\"" + parameter + "\":0.4}"));

                Assert.That(response["errorCode"], Is.Null,
                    $"{parameter} must be an accepted parameter — a ColorBlock with three writable " +
                    $"channels cannot express a fade: {response.ToString(Formatting.None)}");

                var colors = button.colors;
                float alpha =
                    parameter == "normalA" ? colors.normalColor.a :
                    parameter == "highlightedA" ? colors.highlightedColor.a :
                    parameter == "pressedA" ? colors.pressedColor.a : colors.disabledColor.a;
                Assert.That(alpha, Is.EqualTo(0.4f).Within(0.001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }

        [Test]
        public void UIConfigureSelectable_UnnamedBlocks_AreLeftAlone()
        {
            var go = NewButton("__sel_preserve__");
            try
            {
                var button = go.GetComponent<Button>();
                var colors = button.colors;
                colors.pressedColor = new Color(0.1f, 0.2f, 0.3f, 0.4f);
                button.colors = colors;
                var pressedBefore = button.colors.pressedColor;

                var response = JObject.Parse(SkillRouter.Execute("ui_configure_selectable",
                    "{\"name\":\"__sel_preserve__\",\"normalA\":0.9}"));
                Assert.That(response["errorCode"], Is.Null, response.ToString(Formatting.None));

                Assert.That(button.colors.pressedColor, Is.EqualTo(pressedBefore),
                    "Naming one block must not rewrite the other three.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }

        [Test]
        public void UIConfigureSelectable_BadTransitionEnum_WritesNothing()
        {
            var go = NewButton("__sel_reject__");
            try
            {
                var button = go.GetComponent<Button>();
                var colors = button.colors;
                colors.normalColor = new Color(0.5f, 0.5f, 0.5f, 1f);
                button.colors = colors;
                bool interactableBefore = button.interactable;

                var response = JObject.Parse(SkillRouter.Execute("ui_configure_selectable",
                    "{\"name\":\"__sel_reject__\",\"transition\":\"NoSuchTransition\"," +
                    "\"interactable\":false,\"normalA\":0.1}"));

                Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"),
                    response.ToString(Formatting.None));
                Assert.That(button.interactable, Is.EqualTo(interactableBefore),
                    "The interactable flag from the same call was committed despite the rejection.");
                Assert.That(button.colors.normalColor.a, Is.EqualTo(1f).Within(0.001f),
                    "The colour block from the same call was committed despite the rejection.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }

        private static GameObject NewButton(string name)
        {
            Assume.That(SkillRouter.HasSkill("ui_configure_selectable"), Is.True);

            // Selectable 需要 RectTransform，而 Button 本身就提供 Selectable。这里不渲染任何东西，
            // 所以不需要 Canvas 父节点。
            var go = new GameObject(name, typeof(RectTransform), typeof(Button));
            GameObjectFinder.InvalidateCache();
            return go;
        }
#endif
    }
}

// Producer:Betsy
