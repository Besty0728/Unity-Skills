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
    /// The camera / light / selectable setters: what they actually write, what they claim to have written, and
    /// what the dryRun layer says before any of that happens.
    ///
    /// <para>Covers two opposite failure modes. A setter can write less than it claims — a silently dropped
    /// alpha, an enum value that fell through a switch; or it can claim less than it writes, which is exactly
    /// why the <c>applied</c>/<c>skipped</c> report exists: a caller sets <c>range</c> on a Directional light,
    /// and that light type has no range — without a <c>skipped</c> entry in the response, it's completely
    /// indistinguishable from a response saying "set successfully".</para>
    ///
    /// <para>Every write is verified against the live object, never inferred back from the response. The
    /// response is the thing under test, so trusting it would turn the assertion into circular reasoning.</para>
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
            // Camera/Light/UI writes get retracted by a non-full profile, and blocked in any mode other than
            // Bypass. Both are global EditorPrefs state shared across projects, so pin them explicitly here and
            // restore in teardown — never assume.
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

        /// <summary>In the success envelope, the skill payload lives under <c>result</c>, not at the top level.</summary>
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
        /// The alpha channel originally had no corresponding parameter at all, so background transparency was
        /// completely unsettable through this skill — and because the other three channels are writable, a
        /// caller setting bgR/bgG/bgB would get back a color whose alpha silently kept its previous value.
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

                // `applied` names the parameter, not the property: "backgroundColor" is a Camera property and
                // was never a valid input parameter, so echoing it back gives the caller nothing actionable.
                // What must appear is the actual parameter name that was sent.
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

                // The declared Outputs are the contract an agent plans against; if the response is missing any
                // of them, the caller gets pushed into calling a second skill just to get a value it should
                // have already had.
                Assert.That(SkillRouter.TryGetSkill("camera_set_properties", out var info), Is.True);
                var missing = info.Outputs.Where(key => payload[key] == null).ToArray();
                Assert.That(missing, Is.Empty,
                    $"Response is missing declared outputs: {string.Join(", ", missing)}");

                var applied = StringArray(payload["applied"]);
                Assert.That(applied, Is.EquivalentTo(new[] { "fieldOfView", "clearFlags" }),
                    "'applied' must name exactly the parameters written — listing an untouched " +
                    "property is how a caller comes to believe a write happened.");
                // Verify using a color parameter this call didn't send: "backgroundColor" is no longer a name
                // that could appear in `applied`, so asserting its absence wouldn't prove anything.
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
                // Same parameter-name contract as camera_set_properties: the caller sends `a`, so what comes
                // back must be `a` — "color" is a Light property, not an input parameter it can send again.
                Assert.That(StringArray(payload["applied"]), Does.Contain("a"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }

        /// <summary>
        /// The <c>skipped</c> half of the story. A Directional light has no range, so a value the caller sent
        /// has nowhere to go — and a response that doesn't say so is indistinguishable from a "write succeeded"
        /// response, so the caller ends up calling a light that ignored its own setting.
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
            // The above skipped report is only meaningful if the same parameter is actually applied on a light
            // type that genuinely has that property.
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
        /// The setter is called <c>light_set_properties</c>, so a caller would naturally reach for
        /// <c>light_get_properties</c> as the read side. This alias must be a true alias — matching metadata,
        /// matching payload. A drifted alias is worse than no alias, because both names show up in the
        /// manifest with nothing indicating which one is authoritative.
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
        /// A skill that declares RequiresInput "gameObject" but accepts several differently-named locator
        /// parameters (name / path / instanceId / entityId) has no single "this one parameter is required" rule
        /// that can enforce it — each locator parameter is individually optional, so an empty request body
        /// passes validation and the agent is told this call is ready to go. The whole point of group
        /// validation is to make "you didn't name a target" sayable.
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
            // Without this, the assertion above could easily be satisfied by an implementation that just rejects every request body.
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
            // Agents often send {"instanceId": 0} verbatim from a template. The locator layer treats 0 as "not
            // supplied", and group validation must do the same, or this defense gets bypassed by exactly the
            // most common placeholder value.
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
        /// Every skill that declares a gameObject-shaped target token must give the same answer for an empty
        /// request body. The candidate set is derived from the registry rather than a hand-written list, so a
        /// skill added later is already covered without touching this test — and if a given skill's locator
        /// parameters ever stop intersecting the token word list, that surfaces here instead of silently losing coverage.
        /// </summary>
        [Test]
        public void DryRun_EveryGameObjectTargetSkill_RejectsAnEmptyBodyUnderSomeField()
        {
            var candidates = SkillRouter.GetAllSkillsSnapshotUnfiltered()
                .Where(s => s.RequiresInput != null &&
                            s.RequiresInput.Any(t => string.Equals(t, "gameObject", StringComparison.OrdinalIgnoreCase)))
                .Where(s => s.SupportsDryRun)
                // Only include skills accepting `items` (all *_batch) or skills that use a custom locator
                // parameter name that this group word list can't satisfy; the planner deliberately skips those
                // rather than making them uncallable, so they're likewise out of scope here.
                .Where(s => s.AllowedParameterSet != null &&
                            new[] { "name", "path", "instanceId", "entityId" }.Any(p => s.AllowedParameterSet.Contains(p)))
                .ToArray();

            // A lower bound rather than an exact count: the registry shifts with installed optional packages,
            // and asserting equality here would go red for the wrong reason. The lower bound catches the case
            // where the sweep's scope has quietly shrunk to a handful of skills — once the locator-parameter
            // word list stops intersecting these skills' parameters, they'd all fall out of `candidates`, and
            // the assertion below would sail through on a near-empty set.
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
        /// For a request body that was already correct, this analyzer must be completely invisible. It's
        /// deliberately not supposed to alter the plan: attaching steps/changes to every dryRun for these
        /// skills would change the answer callers get for calls that did nothing wrong — a check that's only
        /// meant to validate ends up altering a legal response, which is a breaking change wearing a bugfix's clothes.
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
        // Selectable/Button comes from com.unity.ugui, not a hard dependency of this package. When the package
        // is missing, this whole block of test cases compiles out, instead of dragging down the entire test
        // assembly — including every camera/light/dryRun case above — just because a type can't be resolved.
        // Uses the same versionDefines + #if pattern as the Cinemachine test cases.
#if UGUI

        /// <summary>
        /// The earlier write guard only checked the four R channels, so a call passing only <c>normalG</c>
        /// would drop the entire color block, yet still report success.
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

            // Selectable needs a RectTransform, and Button itself provides Selectable. Nothing is rendered
            // here, so no Canvas parent is needed.
            var go = new GameObject(name, typeof(RectTransform), typeof(Button));
            GameObjectFinder.InvalidateCache();
            return go;
        }
#endif
    }
}

// Producer:Betsy
