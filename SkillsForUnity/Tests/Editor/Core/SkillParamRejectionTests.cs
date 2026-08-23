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
    /// <see cref="SkillParamUtil"/>：枚举拒绝契约与可往返的格式化函数。
    ///
    /// <para>本文件要拦的回归是"静默成功"。历史写法是 <c>if (Enum.TryParse(v, true, out var e)) target = e;</c>
    /// ——没有 else 分支——于是拼错的值被直接丢弃，而 skill 依然回 <c>success:true</c>，同一次调用里的
    /// *其它*参数还已经写进去了。调用方无从察觉：响应说调用成功了，而它悄悄没设的那个属性，
    /// 恰恰是调用方真正在意的那个。</para>
    ///
    /// <para>所以三条互相独立的性质各有自己的断言，因为它们会各自单独失效：调用确实被拒
    /// （返回 <c>false</c> + 一个 error 对象）、错误被归类为 <c>SEMANTIC_INVALID</c> 而不是某种会把 agent
    /// 引去找"不存在的对象"的类型、以及什么都没写进去。只有最后一条能抓住当初那个 bug——
    /// 一个拒绝了却仍然把同伴参数写进去的实现，只是换了更好的错误文案的同一种数据丢失。</para>
    ///
    /// <para>断言落在结构上（<c>errorCode</c>、<c>parameter</c>、<c>validValues</c>），外加路由分类器真正
    /// 依赖的那一个子串（"Invalid value" 必须打头）。完整措辞不做断言。</para>
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
            // 端到端探针会调写类 skill。干净 CI 工程默认是 Auto 模式，而 Optimization/Light 类目
            // 又会被非 full 档位撤掉，所以两者都显式钉住而非假设。
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
        /// 普通枚举被拒时应当告知的名字集合：声明的成员减去带 <c>[Obsolete]</c> 的，保持声明顺序。
        ///
        /// <para>一律推导、绝不硬编码，因为 Unity 会在版本之间弃用枚举成员——<c>LightType.Area</c> 在
        /// 6000.x 上是 <c>Rectangle</c> 的过时写法——所以直接用 <see cref="Enum.GetNames"/> 当预期，
        /// 会变成一条"产品行为正确、却在新版编辑器上变红"的测试。这里排除过时成员的理由与解析器
        /// 拒绝它们的理由相同：它们是无法表示的值，把其中一个告知出去，等于把 agent 引向一个重试时
        /// 照样会被拒的名字。</para>
        ///
        /// <para>仅限普通枚举。<c>[Flags]</c> 枚举的过时名字仍留在词表里，因为它们作为位仍能解析——
        /// 见下面的 StaticEditorFlags 用例。</para>
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
        /// "结论必须打头"规则。.NET 自带的枚举失败文案是 "Requested value 'X' was not found."，
        /// 而路由对未声明错误是按消息模式分类的，于是这句话里的 not-found 特征词会先抢到判定，
        /// 把调用方引去调 gameobject_find 找一个从来不是问题所在的对象。消息必须以 "Invalid value"
        /// 开头，语义类判定才能胜出。
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
        /// <c>Enum.TryParse</c>还会接受任意整数字面量，包括背后没有成员的那些："99" 传给一个只有
        /// 3 个成员的枚举会得到 <c>(TEnum)99</c>，随后被写进 Unity 属性，成为任何 inspector 都显示
        /// 不出来的垃圾值。普通枚举必须拒掉这类输入。
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
            // 上面那条拒绝管的是"可表示性"，不是"有没有数字"。落在已声明成员上的整数仍然合法，
            // 所以用数字形式传真实值的调用方不会被这道守卫打断。
            Assert.That(SkillParamUtil.TryParseEnumParam<LightShadows>(
                    ((int)LightShadows.Hard).ToString(CultureInfo.InvariantCulture), "shadows", out var parsed, out var error),
                Is.True);
            Assert.That(error, Is.Null);
            Assert.That(parsed, Is.EqualTo(LightShadows.Hard));
        }

        [Test]
        public void TryParseEnumParam_FlagsEnum_AcceptsUndeclaredCombination()
        {
            // [Flags] 枚举天然可以持有并非已声明成员的组合值，所以可表示性守卫不能作用于它们——
            // 否则一个完全正当的 BatchingStatic|OccluderStatic 会被当成"不是成员"而拒掉。
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
            // default(LightShadows) 就是 LightShadows.None——一个真实成员、一次真实写入。想表达
            // "保持当前值"的 setter 必须能区分这两者，这正是本重载返回可空类型的全部理由。
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
            // 两种不同的调用方错误对应两种不同的修法："你漏了" 与 "你拼错了"。把它们混为一谈，
            // 就要多花 agent 一次重试。
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
        /// "Everything" 指的是调用方仍被允许设置的全部成员——即所有非 <c>[Obsolete]</c> 成员按位或，
        /// 而不是所有已声明成员的或。
        ///
        /// <para>这个区分正是该别名存在的意义。一个被弃用的成员可能携带某个现存成员都不占的位，
        /// 把它折进去，就会让 skill 自己文档化的默认值写入一个调用方无法命名、无法请求、
        /// 事后也无法按名清除的标记。这里的预期值用反射取而不是写死，因为 Unity 弃用了哪些成员
        /// 随编辑器版本而异。</para>
        /// </summary>
        [Test]
        public void TryParseFlagsParam_EverythingAlias_IsOrOfEveryLiveMember()
        {
            // StaticEditorFlags 既没声明 Everything 也没声明 Nothing，于是普通枚举解析会拒掉
            // optimize_set_static_flags 自己文档化的默认值。
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
            // 静默缩小集合就是同一个 bug 的 flags 版本：调用方要三个标记，拿到两个，却被告知成功。
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

        // ---------- 端到端：整次调用被拒，且什么都没写 ----------

        /// <summary>
        /// 唯一能抓住当初那个 bug 的断言。一个拒绝了却仍然把同伴参数写进去的实现，只是换了更好错误
        /// 文案的同一种静默数据丢失，所以这里拿活对象来核写入结果，而不是相信响应的说法。
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
            // 只有正面用例真的写入了，上面那条反面用例才有意义。
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
        /// 创建类 skill：拒绝必须发生在 GameObject 被创建之前，否则一个坏值会留下一个半配置好的
        /// 对象等着调用方去清理。
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
        /// [Flags] 场景的端到端用例，用的正是过去会被直接拒掉的那个值：本 skill 文档化的默认值
        /// "Everything"。
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
        /// float 上的 <c>ToString()</c> 既会截断（0.192156866 → "0.1921569"），又会跟随编辑器的区域设置。
        /// 两者都会产出调用方喂不回来的回显：前者丢精度，后者在调用方解析器要 "0.5" 的地方吐出 "0,5"。
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
        /// 区域设置那一半。用逗号作小数点的编辑器区域并不罕见——欧洲大部分地区都是默认如此——
        /// 在那种设置下不受控的 ToString() 会吐出 "0,5"，而对端任何 JSON 消费者读到它，
        /// 要么解析失败，要么当成两个元素的列表。
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
            // 不带 alpha 的回显正是"alpha 被丢掉"的藏身之处：响应看起来没问题，
            // 因为调用方本该去核对的那个字段压根不在里面。
            var text = SkillParamUtil.FormatColor(new Color(1f, 0f, 0f, 0.25f));

            Assert.That(text.Split(',').Length, Is.EqualTo(4),
                $"'{text}' is not RGBA — a three-component colour echo cannot report a dropped alpha.");
            Assert.That(text, Does.Contain("0.25"));
        }

        [Test]
        public void FormatScalarR_BooleansAreLowercaseJsonLiterals()
        {
            // .NET 的 Boolean.ToString() 给的是 "True"/"False"，既不是合法 JSON，
            // 也不是调用方回喂这段回显时能解析的东西。
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

        // ---------- component_set_property：JSON 对象形式的 value ----------

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
            // 对象形式是新增而非替代——逗号形式才是所有既有调用方在发的东西。
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
        /// 颜色的对象形式，专门盯 alpha。不带 alpha 的 <c>{r,g,b}</c> 默认是不透明而非全透明——
        /// 若默认取 0，每一个三通道对象形式的颜色都会变成看不见的。
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
        /// 回显必须能被喂回去。这条测试按构造方式断言该性质：取上一次调用的 <c>valueSet</c>，
        /// 作为下一次调用的 <c>value</c> 发出，并要求存下来的值保持不变。
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

                // 回显带括号——那是文档化的展示形式，而解析器接受它回传，这正是往返保证的全部意义。
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
