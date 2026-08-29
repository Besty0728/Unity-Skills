using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// <see cref="SkillParamUtil"/>: the enum rejection contract and its round-trippable formatters.
    ///
    /// <para>The regression this file guards against is "silent success". The old code pattern was
    /// <c>if (Enum.TryParse(v, true, out var e)) target = e;</c> — with no else branch — so a misspelled
    /// value was simply dropped while the skill still returned <c>success:true</c>, and the call's *other*
    /// parameters had already been written. The caller has no way to notice: the response says the call
    /// succeeded, and the one property it quietly failed to set is exactly the one the caller actually cared about.</para>
    ///
    /// <para>So the three independent properties each get their own assertion, because each can fail on
    /// its own: the call is genuinely rejected (returns <c>false</c> + an error object), the error is
    /// classified as <c>SEMANTIC_INVALID</c> rather than some type that would send the agent off hunting
    /// for a "nonexistent object", and nothing at all gets written. Only the last one catches the original
    /// bug — an implementation that rejects the call but still writes sibling parameters is the same data
    /// loss with a nicer error message.</para>
    ///
    /// <para>Assertions land on structure (<c>errorCode</c>, <c>parameter</c>, <c>validValues</c>), plus the
    /// one substring the routing classifier actually depends on ("Invalid value" must lead). The exact wording is not asserted.</para>
    /// </summary>
    [TestFixture]
    public class SkillParamRejectionTests
    {
        private SkillsOperatingMode _savedMode;
        private SurfaceProfileKind _savedProfile;

        [SetUp]
        public void SetUp()
        {
            _savedMode = SkillsModeManager.CurrentMode;
            _savedProfile = SkillsSurfaceProfile.Current;
            // End-to-end probes call write-category skills. A clean CI project defaults to Auto mode, and
            // the Optimization/Light categories get revoked under a non-full profile, so both are pinned
            // explicitly rather than assumed.
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

        private static JObject ToJObject(object result) => JObject.Parse(JsonConvert.SerializeObject(result));

        /// <summary>
        /// The set of names a rejected plain enum should advertise: declared members minus those marked
        /// <c>[Obsolete]</c>, in declaration order.
        ///
        /// <para>Always derived, never hardcoded, because Unity deprecates enum members across versions —
        /// <c>LightType.Area</c> is an obsolete spelling of <c>Rectangle</c> on 6000.x — so using
        /// <see cref="Enum.GetNames"/> directly as the expectation would turn this into a test that's
        /// "correct in behavior, yet turns red on a newer editor." The reason obsolete members are excluded
        /// here is the same reason the parser rejects them: they are unrepresentable values, and
        /// advertising one would just send the agent toward a name that gets rejected again on retry.</para>
        ///
        /// <para>Plain enums only. A <c>[Flags]</c> enum's obsolete names stay in the vocabulary, because
        /// they still parse as bits — see the StaticEditorFlags case below.</para>
        /// </summary>
        private static string[] LiveEnumNames(Type enumType) =>
            enumType.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => !f.IsDefined(typeof(ObsoleteAttribute), false))
                .Select(f => f.Name)
                .ToArray();

        // ---------- TryParseEnumParam ----------

        [Test]
        public void TryParseEnumParam_ValidValue_IsCaseInsensitive()
        {
            Assert.That(SkillParamUtil.TryParseEnumParam<LightShadows>("soft", "shadows", out var parsed, out var error),
                Is.True);
            Assert.That(error, Is.Null);
            Assert.That(parsed, Is.EqualTo(LightShadows.Soft));

            Assert.That(SkillParamUtil.TryParseEnumParam<LightShadows>("  HARD  ", "shadows", out var padded, out _),
                Is.True, "Values arrive from JSON bodies with incidental whitespace; trimming is part of the contract.");
            Assert.That(padded, Is.EqualTo(LightShadows.Hard));
        }

        [Test]
        public void TryParseEnumParam_BlankValue_IsTreatedAsNotSupplied()
        {
            foreach (var blank in new[] { null, "", "   " })
            {
                Assert.That(SkillParamUtil.TryParseEnumParam<LightShadows>(blank, "shadows", out _, out var error),
                    Is.True, $"A blank value ({blank ?? "null"}) means 'not supplied', not 'invalid'.");
                Assert.That(error, Is.Null);
            }
        }

        [Test]
        public void TryParseEnumParam_UnknownValue_IsRejectedWithSemanticInvalid()
        {
            Assert.That(SkillParamUtil.TryParseEnumParam<LightShadows>("NoSuchShadow", "shadows", out _, out var error),
                Is.False);
            Assert.That(error, Is.Not.Null, "A present-but-unparseable value must produce an error object, not a silent skip.");

            var json = ToJObject(error);
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"));
            Assert.That(json["parameter"]?.ToString(), Is.EqualTo("shadows"));
            Assert.That(json["validValues"]?.ToObject<string[]>(),
                Is.EqualTo(LiveEnumNames(typeof(LightShadows))),
                "validValues is what lets an agent fix the call in one retry instead of guessing.");
        }

        /// <summary>
        /// The "verdict must lead" rule. .NET's own enum-parse failure text is "Requested value 'X' was
        /// not found.", and the router classifies undeclared errors by message pattern, so the not-found
        /// signature in that phrase would grab the classification first, sending the caller off to call
        /// gameobject_find for an object that was never the problem. The message must start with "Invalid
        /// value" for the semantic-category verdict to win instead.
        /// </summary>
        [Test]
        public void RejectionMessage_LeadsWithInvalidValue_SoItIsNotClassifiedAsNotFound()
        {
            SkillParamUtil.TryParseEnumParam<LightShadows>("NoSuchShadow", "shadows", out _, out var error);
            var message = ToJObject(error)["error"]?.ToString();

            Assert.That(message, Does.StartWith("Invalid value"),
                "The classifier reads the leading verdict. Anything else here lets .NET's " +
                "\"Requested value ... was not found\" phrasing be bucketed as TARGET_NOT_FOUND.");
            Assert.That(message, Does.Contain("shadows"), "The offending parameter must be nameable from the message alone.");
        }

        /// <summary>
        /// <c>Enum.TryParse</c> will also accept any integer literal, including ones with no member behind
        /// them: "99" passed to an enum with only 3 members yields <c>(TEnum)99</c>, which then gets written
        /// to a Unity property and becomes a garbage value no inspector can display. A plain enum must
        /// reject input like this.
        /// </summary>
        [Test]
        public void TryParseEnumParam_IntegerLiteralWithNoMember_IsRejected()
        {
            Assert.That(SkillParamUtil.TryParseEnumParam<LightShadows>("99", "shadows", out _, out var error),
                Is.False, "(LightShadows)99 is not a member — Enum.TryParse accepts it, we must not.");
            Assert.That(ToJObject(error)["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"));
        }

        [Test]
        public void TryParseEnumParam_IntegerLiteralNamingARealMember_IsStillAccepted()
        {
            // The rejection above is about "representability", not "whether it's a number". An integer that
            // lands on a declared member is still valid, so a caller sending a real value in numeric form
            // is not stopped by this guard.
            Assert.That(SkillParamUtil.TryParseEnumParam<LightShadows>(
                    ((int)LightShadows.Hard).ToString(CultureInfo.InvariantCulture), "shadows", out var parsed, out var error),
                Is.True);
            Assert.That(error, Is.Null);
            Assert.That(parsed, Is.EqualTo(LightShadows.Hard));
        }

        [Test]
        public void TryParseEnumParam_FlagsEnum_AcceptsUndeclaredCombination()
        {
            // A [Flags] enum can naturally hold combination values that aren't declared members, so the
            // representability guard must not apply to them — otherwise a perfectly legitimate
            // BatchingStatic|OccluderStatic would get rejected as "not a member".
            int combo = (int)(StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic);
            Assert.That(SkillParamUtil.TryParseEnumParam<StaticEditorFlags>(
                    combo.ToString(CultureInfo.InvariantCulture), "flags", out var parsed, out var error),
                Is.True, "Flags combinations are not declared members but are entirely valid values.");
            Assert.That(error, Is.Null);
            Assert.That(parsed, Is.EqualTo(StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic));
        }

        // ---------- TryParseOptionalEnum / TryParseRequiredEnum ----------

        [Test]
        public void TryParseOptionalEnum_BlankValue_YieldsNull_NotDefaultMember()
        {
            // default(LightShadows) is LightShadows.None — a real member, a real write. A setter meant to
            // express "leave the current value alone" needs to distinguish these two cases, which is the
            // entire reason this overload returns a nullable type.
            Assert.That(SkillParamUtil.TryParseOptionalEnum<LightShadows>(null, "shadows", out var result, out var error),
                Is.True);
            Assert.That(error, Is.Null);
            Assert.That(result.HasValue, Is.False,
                "An omitted optional enum must be distinguishable from an explicit default(TEnum).");
        }

        [Test]
        public void TryParseOptionalEnum_SuppliedValue_YieldsThatValue()
        {
            Assert.That(SkillParamUtil.TryParseOptionalEnum<LightShadows>("Hard", "shadows", out var result, out _),
                Is.True);
            Assert.That(result, Is.EqualTo(LightShadows.Hard));
        }

        [Test]
        public void TryParseRequiredEnum_BlankValue_IsMissingParam_NotSemanticInvalid()
        {
            // Two distinct caller mistakes call for two distinct fixes: "you left it out" versus "you
            // misspelled it". Conflating them costs the agent one extra retry.
            Assert.That(SkillParamUtil.TryParseRequiredEnum<LightType>(null, "lightType", out _, out var error),
                Is.False, "A create-style skill's blank enum is a caller mistake, not 'leave it alone'.");

            var json = ToJObject(error);
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("MISSING_PARAM"));
            Assert.That(json["parameter"]?.ToString(), Is.EqualTo("lightType"));
            Assert.That(json["validValues"]?.ToObject<string[]>(), Is.EqualTo(LiveEnumNames(typeof(LightType))),
                "LightType is where this matters most: Area is an obsolete alias of Rectangle on " +
                "6000.x, and advertising it would hand the agent a value the parser then refuses.");
        }

        [Test]
        public void TryParseRequiredEnum_UnknownValue_IsSemanticInvalid()
        {
            Assert.That(SkillParamUtil.TryParseRequiredEnum<LightType>("Sunshine", "lightType", out _, out var error),
                Is.False);
            Assert.That(ToJObject(error)["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"));
        }

        // ---------- TryParseFlagsParam ----------

        /// <summary>
        /// "Everything" means every member the caller is still allowed to set — i.e. the bitwise OR of
        /// every non-<c>[Obsolete]</c> member, not the OR of every declared member.
        ///
        /// <para>This distinction is the entire reason the alias exists. A deprecated member might carry a
        /// bit that no live member occupies, and folding it in would make the skill's own documented
        /// default write a flag the caller can't name, can't request, and can't clear by name afterward.
        /// The expected value here is taken via reflection rather than hardcoded, because which members
        /// Unity has deprecated varies by editor version.</para>
        /// </summary>
        [Test]
        public void TryParseFlagsParam_EverythingAlias_IsOrOfEveryLiveMember()
        {
            // StaticEditorFlags declares neither Everything nor Nothing, so plain enum parsing would reject
            // optimize_set_static_flags's own documented default.
            Assert.That(SkillParamUtil.TryParseFlagsParam<StaticEditorFlags>("Everything", "flags", out var all, out var error),
                Is.True, "'Everything' is the skill's documented default — it has to parse.");
            Assert.That(error, Is.Null);

            var fields = typeof(StaticEditorFlags).GetFields(BindingFlags.Public | BindingFlags.Static);
            long liveMask = fields
                .Where(f => !f.IsDefined(typeof(ObsoleteAttribute), false))
                .Aggregate(0L, (acc, f) => acc | Convert.ToInt64(f.GetRawConstantValue()));
            long declaredMask = fields
                .Aggregate(0L, (acc, f) => acc | Convert.ToInt64(f.GetRawConstantValue()));

            Assume.That(liveMask, Is.Not.EqualTo(0L),
                "Every StaticEditorFlags member is deprecated on this editor; the alias falls back to " +
                "the full mask and there is nothing to distinguish.");
            Assert.That(Convert.ToInt64(all), Is.EqualTo(liveMask),
                $"'Everything' resolved to 0x{Convert.ToInt64(all):X} but the live members OR to " +
                $"0x{liveMask:X} (all declared members OR to 0x{declaredMask:X}). Folding a deprecated " +
                "member's bit into the default writes a flag no caller can name.");
        }

        [Test]
        public void TryParseFlagsParam_NothingAlias_IsZero()
        {
            Assert.That(SkillParamUtil.TryParseFlagsParam<StaticEditorFlags>("Nothing", "flags", out var none, out _),
                Is.True);
            Assert.That(Convert.ToInt64(none), Is.EqualTo(0L));
        }

        [Test]
        public void TryParseFlagsParam_CommaList_AccumulatesEveryPart()
        {
            Assert.That(SkillParamUtil.TryParseFlagsParam<StaticEditorFlags>(
                    "BatchingStatic,OccluderStatic", "flags", out var parsed, out _),
                Is.True);
            Assert.That(parsed, Is.EqualTo(StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic));
        }

        [Test]
        public void TryParseFlagsParam_OneBadNameInList_FailsTheWholeValue()
        {
            // Silently shrinking the set is the flags-shaped version of the same bug: the caller asked for
            // three flags, got two, and was still told it succeeded.
            Assert.That(SkillParamUtil.TryParseFlagsParam<StaticEditorFlags>(
                    "BatchingStatic,NoSuchFlag", "flags", out _, out var error),
                Is.False, "One unresolvable part must fail the call, not quietly drop that part.");
            Assert.That(ToJObject(error)["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"));
        }

        [Test]
        public void TryParseFlagsParam_BlankValue_IsMissingParam()
        {
            Assert.That(SkillParamUtil.TryParseFlagsParam<StaticEditorFlags>("", "flags", out _, out var error),
                Is.False);

            var json = ToJObject(error);
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("MISSING_PARAM"));
            Assert.That(json["validValues"]?.ToObject<string[]>(), Does.Contain("Everything").And.Contain("Nothing"),
                "The advertised aliases must appear in the vocabulary the error hands back.");
        }

        // ---------- End-to-end: the whole call is rejected, and nothing gets written ----------

        /// <summary>
        /// The only assertion that catches the original bug. An implementation that rejects the call but
        /// still writes sibling parameters is the same silent partial data loss with a nicer error message,
        /// so this checks the live object for the actual write result rather than trusting what the response claims.
        /// </summary>
        [Test]
        public void CameraSetProperties_BadEnum_AppliesNothing_IncludingSiblingParameters()
        {
            var go = new GameObject("__rej_cam__", typeof(Camera));
            try
            {
                GameObjectFinder.InvalidateCache();
                var cam = go.GetComponent<Camera>();
                cam.fieldOfView = 60f;
                float fovBefore = cam.fieldOfView;
                var clearBefore = cam.clearFlags;

                var response = JObject.Parse(SkillRouter.Execute("camera_set_properties",
                    "{\"name\":\"__rej_cam__\",\"fieldOfView\":33,\"clearFlags\":\"NoSuchFlag\"}"));

                Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"),
                    $"Expected the bad clearFlags to fail the call: {response.ToString(Formatting.None)}");
                Assert.That(cam.fieldOfView, Is.EqualTo(fovBefore).Within(0.001f),
                    "fieldOfView was applied even though the call was rejected — this is the silent " +
                    "partial write the rejection exists to prevent.");
                Assert.That(cam.clearFlags, Is.EqualTo(clearBefore));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }

        [Test]
        public void CameraSetProperties_ValidEnum_IsApplied()
        {
            // The positive-path case above only matters because this positive counterpart actually writes.
            var go = new GameObject("__acc_cam__", typeof(Camera));
            try
            {
                GameObjectFinder.InvalidateCache();
                var cam = go.GetComponent<Camera>();
                cam.clearFlags = CameraClearFlags.Skybox;

                var response = JObject.Parse(SkillRouter.Execute("camera_set_properties",
                    "{\"name\":\"__acc_cam__\",\"fieldOfView\":33,\"clearFlags\":\"Depth\"}"));

                Assert.That(response["errorCode"], Is.Null, response.ToString(Formatting.None));
                Assert.That(cam.clearFlags, Is.EqualTo(CameraClearFlags.Depth));
                Assert.That(cam.fieldOfView, Is.EqualTo(33f).Within(0.001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }

        /// <summary>
        /// Create-category skills: rejection must happen before the GameObject is created, otherwise a bad
        /// value leaves behind a half-configured object for the caller to clean up.
        /// </summary>
        [Test]
        public void LightCreate_BadEnum_CreatesNoObject()
        {
            const string probe = "__rej_light__";
            var response = JObject.Parse(SkillRouter.Execute("light_create",
                "{\"name\":\"" + probe + "\",\"lightType\":\"Sunshine\"}"));

            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"),
                response.ToString(Formatting.None));

            GameObjectFinder.InvalidateCache();
            Assert.That(GameObject.Find(probe), Is.Null,
                "A rejected light_create must not leave a half-configured GameObject in the scene.");
        }

        [Test]
        public void LightCreate_ValidEnum_CreatesTheLight()
        {
            const string probe = "__acc_light__";
            try
            {
                var response = JObject.Parse(SkillRouter.Execute("light_create",
                    "{\"name\":\"" + probe + "\",\"lightType\":\"Spot\",\"shadows\":\"Hard\"}"));

                Assert.That(response["errorCode"], Is.Null, response.ToString(Formatting.None));
                GameObjectFinder.InvalidateCache();

                var created = GameObject.Find(probe);
                Assert.That(created, Is.Not.Null);
                Assert.That(created.GetComponent<Light>().type, Is.EqualTo(LightType.Spot));
                Assert.That(created.GetComponent<Light>().shadows, Is.EqualTo(LightShadows.Hard));
            }
            finally
            {
                var created = GameObject.Find(probe);
                if (created != null) UnityEngine.Object.DestroyImmediate(created);
                GameObjectFinder.InvalidateCache();
            }
        }

        /// <summary>
        /// An end-to-end case for the [Flags] scenario, using exactly the value that used to be outright
        /// rejected: this skill's own documented default, "Everything".
        /// </summary>
        [Test]
        public void OptimizeSetStaticFlags_EverythingDefault_IsAcceptedAndWritten()
        {
            var go = new GameObject("__flags_probe__");
            try
            {
                GameObjectFinder.InvalidateCache();
                GameObjectUtility.SetStaticEditorFlags(go, 0);

                var response = JObject.Parse(SkillRouter.Execute("optimize_set_static_flags",
                    "{\"name\":\"__flags_probe__\",\"flags\":\"Everything\"}"));

                Assert.That(response["errorCode"], Is.Null, response.ToString(Formatting.None));
                Assert.That((int)GameObjectUtility.GetStaticEditorFlags(go), Is.Not.EqualTo(0),
                    "'Everything' is the skill's own documented default; it has to actually write.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }

        [Test]
        public void OptimizeSetStaticFlags_BadFlagName_WritesNothing()
        {
            var go = new GameObject("__flags_reject__");
            try
            {
                GameObjectFinder.InvalidateCache();
                GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
                var before = GameObjectUtility.GetStaticEditorFlags(go);

                var response = JObject.Parse(SkillRouter.Execute("optimize_set_static_flags",
                    "{\"name\":\"__flags_reject__\",\"flags\":\"BatchingStatic,NoSuchFlag\"}"));

                Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"),
                    response.ToString(Formatting.None));
                Assert.That(GameObjectUtility.GetStaticEditorFlags(go), Is.EqualTo(before),
                    "A partially-resolvable flags list must not write the parts that did resolve.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }

        // ---------- round-trip formatting ----------

        /// <summary>
        /// <c>ToString()</c> on a float both truncates (0.192156866 → "0.1921569") and follows the editor's
        /// locale. Both produce an echo the caller can't feed back: the former loses precision, the latter
        /// emits "0,5" where the caller's parser expects "0.5".
        /// </summary>
        [Test]
        public void FormatFloatR_RoundTripsExactly()
        {
            var probes = new[]
            {
                0f, 1f, -1f, 0.1f, 0.5f, 0.192156866f, 1f / 3f, 60f, 0.0001f,
                float.MaxValue, float.MinValue, float.Epsilon, -0.000123456f,
            };

            foreach (var value in probes)
            {
                var text = SkillParamUtil.FormatFloatR(value);
                Assert.That(float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed),
                    Is.True, $"'{text}' (from {value}) does not parse back as an invariant float.");
                Assert.That(parsed, Is.EqualTo(value),
                    $"{value} formatted to '{text}' which parses back as {parsed} — the echo is lossy.");
            }
        }

        [Test]
        public void FormatDoubleR_RoundTripsExactly()
        {
            foreach (var value in new[] { 0d, 0.1d, 1d / 3d, 1e-300, 1e300, -2.2250738585072014e-308 })
            {
                var text = SkillParamUtil.FormatDoubleR(value);
                Assert.That(double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed),
                    Is.True, $"'{text}' (from {value}) does not parse back as an invariant double.");
                Assert.That(parsed, Is.EqualTo(value));
            }
        }

        /// <summary>
        /// The locale half of the story. An editor locale that uses a comma as the decimal separator isn't
        /// rare — most of Europe defaults to it — and under that setting, an uncontrolled ToString() would
        /// emit "0,5", which any JSON consumer on the other end would either fail to parse or read as a
        /// two-element list.
        /// </summary>
        [Test]
        public void Formatters_AreCultureInvariant_UnderACommaDecimalLocale()
        {
            var saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

                Assert.That(SkillParamUtil.FormatFloatR(0.5f), Is.EqualTo("0.5"),
                    "A comma-decimal locale must not leak into the wire format.");
                Assert.That(SkillParamUtil.FormatDoubleR(0.5d), Is.EqualTo("0.5"));
                Assert.That(SkillParamUtil.FormatVector3(new Vector3(0.5f, 1.5f, -2.5f)),
                    Is.EqualTo("(0.5, 1.5, -2.5)"));
                Assert.That(SkillParamUtil.FormatScalarR(0.5f), Is.EqualTo("0.5"));
                Assert.That(SkillParamUtil.FormatScalarR(0.5d), Is.EqualTo("0.5"));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Test]
        public void FormatColor_AlwaysCarriesFourComponents()
        {
            // An echo without alpha is exactly where "alpha got dropped" hides: the response looks fine,
            // because the field the caller should have checked simply isn't in it.
            var text = SkillParamUtil.FormatColor(new Color(1f, 0f, 0f, 0.25f));

            Assert.That(text.Split(',').Length, Is.EqualTo(4),
                $"'{text}' is not RGBA — a three-component colour echo cannot report a dropped alpha.");
            Assert.That(text, Does.Contain("0.25"));
        }

        [Test]
        public void FormatScalarR_BooleansAreLowercaseJsonLiterals()
        {
            // .NET's Boolean.ToString() gives "True"/"False", which is neither valid JSON
            // nor something the caller's parser can read back when it's fed this echo.
            Assert.That(SkillParamUtil.FormatScalarR(true), Is.EqualTo("true"));
            Assert.That(SkillParamUtil.FormatScalarR(false), Is.EqualTo("false"));
            Assert.That(SkillParamUtil.FormatScalarR(null), Is.EqualTo("null"));
        }

        [Test]
        public void LooksLikeJsonObject_DistinguishesObjectFormFromCommaForm()
        {
            Assert.That(SkillParamUtil.LooksLikeJsonObject("{\"x\":1,\"y\":2}"), Is.True);
            Assert.That(SkillParamUtil.LooksLikeJsonObject("  {\"r\":1}"), Is.True, "Leading whitespace is incidental.");
            Assert.That(SkillParamUtil.LooksLikeJsonObject("1,2,3"), Is.False);
            Assert.That(SkillParamUtil.LooksLikeJsonObject("#FF0000"), Is.False);
            Assert.That(SkillParamUtil.LooksLikeJsonObject("red"), Is.False);
            Assert.That(SkillParamUtil.LooksLikeJsonObject(null), Is.False);
            Assert.That(SkillParamUtil.LooksLikeJsonObject("{}"), Is.False,
                "No colon means no members — let it fail as a comma form rather than an empty object.");
        }

        // ---------- component_set_property: value as a JSON object form ----------

        [Test]
        public void ComponentSetProperty_AcceptsVectorJsonObjectForm()
        {
            var go = new GameObject("__prop_vec__");
            try
            {
                GameObjectFinder.InvalidateCache();

                var response = JObject.Parse(SkillRouter.Execute("component_set_property",
                    "{\"name\":\"__prop_vec__\",\"componentType\":\"Transform\"," +
                    "\"propertyName\":\"localPosition\",\"value\":\"{\\\"x\\\":1.5,\\\"y\\\":2.5,\\\"z\\\":-3.5}\"}"));

                Assert.That(response["errorCode"], Is.Null,
                    "The {x,y,z} object form is documented in the module docs: " + response.ToString(Formatting.None));
                Assert.That(go.transform.localPosition, Is.EqualTo(new Vector3(1.5f, 2.5f, -3.5f)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }

        [Test]
        public void ComponentSetProperty_AcceptsCommaFormToo()
        {
            // The object form is additive, not a replacement — the comma form is what every existing caller is sending.
            var go = new GameObject("__prop_csv__");
            try
            {
                GameObjectFinder.InvalidateCache();

                var response = JObject.Parse(SkillRouter.Execute("component_set_property",
                    "{\"name\":\"__prop_csv__\",\"componentType\":\"Transform\"," +
                    "\"propertyName\":\"localPosition\",\"value\":\"1.5,2.5,-3.5\"}"));

                Assert.That(response["errorCode"], Is.Null, response.ToString(Formatting.None));
                Assert.That(go.transform.localPosition, Is.EqualTo(new Vector3(1.5f, 2.5f, -3.5f)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }

        /// <summary>
        /// The object form of a color, specifically targeting alpha. A <c>{r,g,b}</c> without alpha
        /// defaults to opaque, not fully transparent — if the default were 0, every three-channel object-form
        /// color would turn invisible.
        /// </summary>
        [Test]
        public void ComponentSetProperty_AcceptsColorJsonObjectForm_AlphaDefaultsToOpaque()
        {
            var go = new GameObject("__prop_col__", typeof(Light));
            try
            {
                GameObjectFinder.InvalidateCache();

                var withAlpha = JObject.Parse(SkillRouter.Execute("component_set_property",
                    "{\"name\":\"__prop_col__\",\"componentType\":\"Light\",\"propertyName\":\"color\"," +
                    "\"value\":\"{\\\"r\\\":1,\\\"g\\\":0,\\\"b\\\":0,\\\"a\\\":0.25}\"}"));
                Assert.That(withAlpha["errorCode"], Is.Null, withAlpha.ToString(Formatting.None));
                Assert.That(go.GetComponent<Light>().color.a, Is.EqualTo(0.25f).Within(0.001f));

                var withoutAlpha = JObject.Parse(SkillRouter.Execute("component_set_property",
                    "{\"name\":\"__prop_col__\",\"componentType\":\"Light\",\"propertyName\":\"color\"," +
                    "\"value\":\"{\\\"r\\\":0,\\\"g\\\":1,\\\"b\\\":0}\"}"));
                Assert.That(withoutAlpha["errorCode"], Is.Null, withoutAlpha.ToString(Formatting.None));
                Assert.That(go.GetComponent<Light>().color.a, Is.EqualTo(1f).Within(0.001f),
                    "An omitted alpha must default to opaque — defaulting to 0 makes the colour invisible.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }

        /// <summary>
        /// An echo must be feedable back in. This test asserts that property by construction: it takes the
        /// previous call's <c>valueSet</c>, sends it as the <c>value</c> of the next call, and requires the
        /// stored value to stay unchanged.
        /// </summary>
        [Test]
        public void ComponentSetProperty_ValueSetEcho_IsItselfAcceptedAsInput()
        {
            var go = new GameObject("__prop_round__");
            try
            {
                GameObjectFinder.InvalidateCache();

                var first = JObject.Parse(SkillRouter.Execute("component_set_property",
                    "{\"name\":\"__prop_round__\",\"componentType\":\"Transform\"," +
                    "\"propertyName\":\"localPosition\",\"value\":\"0.192156866,0.1,-0.3333333\"}"));
                Assert.That(first["errorCode"], Is.Null, first.ToString(Formatting.None));

                var echo = first["result"]?["valueSet"]?.ToString();
                Assert.That(echo, Is.Not.Null.And.Not.Empty,
                    "valueSet is the documented output; without it the round trip has no input.");

                var stored = go.transform.localPosition;
                go.transform.localPosition = Vector3.zero;

                // The echo carries parentheses — that's the documented display form, and the parser
                // accepting it fed back in is the entire point of the round-trip guarantee.
                var replay = JObject.Parse(SkillRouter.Execute("component_set_property",
                    "{\"name\":\"__prop_round__\",\"componentType\":\"Transform\"," +
                    "\"propertyName\":\"localPosition\",\"value\":" + JsonConvert.ToString(echo) + "}"));
                Assert.That(replay["errorCode"], Is.Null,
                    $"valueSet echo '{echo}' was not accepted back as input: {replay.ToString(Formatting.None)}");

                Assert.That(go.transform.localPosition, Is.EqualTo(stored),
                    $"Replaying the echo '{echo}' produced a different value — the echo is lossy.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }

        [Test]
        public void ComponentSetProperty_MalformedJsonObjectValue_IsRejected()
        {
            var go = new GameObject("__prop_bad__");
            try
            {
                GameObjectFinder.InvalidateCache();
                var before = go.transform.localPosition;

                var response = JObject.Parse(SkillRouter.Execute("component_set_property",
                    "{\"name\":\"__prop_bad__\",\"componentType\":\"Transform\"," +
                    "\"propertyName\":\"localPosition\",\"value\":\"{\\\"x\\\":oops}\"}"));

                Assert.That(response["errorCode"], Is.Not.Null,
                    "A malformed object form must fail rather than be silently retried as a comma list.");
                Assert.That(go.transform.localPosition, Is.EqualTo(before));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }
    }
}

// Producer:Betsy
