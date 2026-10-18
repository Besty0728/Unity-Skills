using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class RuntimeInvocationTests
    {
        private readonly List<string> _jobs = new List<string>();
        private GameObject _target;
        private IDisposable _logScope;
        private int _frame;

        [SetUp]
        public void SetUp()
        {
            ComponentInvocationService.PlayingOverrideForTests = false;
            ServerAvailabilityHelper.CompilationInProgressOverrideForTests = false;
            InvocationFixture.Calls = 0;
            _frame = 100;
            ComponentInvocationService.FrameForTests = () => _frame;
            _logScope = ConsoleLogHistoryService.BeginTestScope(Path.Combine(Path.GetTempPath(), "unity-invocation-" + Guid.NewGuid().ToString("N") + ".jsonl"));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var id in _jobs)
            {
                var job = AsyncJobService.Get(id);
                if (job != null) ComponentInvocationService.NotifyCancelled(job);
                BatchPersistence.RemoveJob(id);
            }
            _jobs.Clear();
            if (_target != null) UnityEngine.Object.DestroyImmediate(_target);
            ComponentInvocationService.PlayingOverrideForTests = null;
            ComponentInvocationService.FrameForTests = null;
            _logScope?.Dispose();
            _logScope = null;
            ServerAvailabilityHelper.CompilationInProgressOverrideForTests = null;
            BatchPersistence.FlushIfDirty();
        }

        private static ComponentInvocationService.Request Request(string method, string arguments = null, string parameterTypes = null) =>
            new ComponentInvocationService.Request { componentType = typeof(InvocationFixture).AssemblyQualifiedName,
                methodName = method, isStatic = true, executionMode = "edit", arguments = arguments, parameterTypes = parameterTypes };

        private BatchJobRecord Start(ComponentInvocationService.Request request)
        {
            var response = JObject.FromObject(ComponentInvocationService.Start(request));
            Assert.That(response["error"], Is.Null, response.ToString());
            var id = response.Value<string>("jobId");
            Assert.That(id, Is.Not.Empty);
            _jobs.Add(id);
            return AsyncJobService.Get(id);
        }

        [Test]
        public void PreviewBindsWithoutInvocationOrJobCreation()
        {
            int count = BatchPersistence.ListJobs(100).Length;
            var result = JObject.FromObject(ComponentInvocationService.Preview(Request("Optional")));
            Assert.That(result.Value<bool>("valid"), Is.True);
            Assert.That(InvocationFixture.Calls, Is.Zero);
            Assert.That(BatchPersistence.ListJobs(100).Length, Is.EqualTo(count));
            var wrongMode = Request("Optional"); wrongMode.executionMode = "play";
            Assert.That(((JObject)ComponentInvocationService.Preview(wrongMode))["error"], Is.Not.Null);
            Assert.That(((JObject)ComponentInvocationService.Preview(Request("Overloaded", "[1]")))["error"].Value<string>(), Does.Contain("ambiguous"));
            Assert.That(InvocationFixture.Calls, Is.Zero);
        }

        [Test]
        public void AmbiguousPreviewAndStartReturnStructuredCandidatesWithoutInvocation()
        {
            foreach (var response in new[] { ComponentInvocationService.Preview(Request("Overloaded", "[3]")),
                ComponentInvocationService.Start(Request("Overloaded", "[3]")) })
            {
                var error = (JObject)response;
                var candidates = (JArray)error["details"]["candidates"];
                Assert.That(candidates.Count, Is.EqualTo(2));
                Assert.That(candidates[0]["signature"], Is.Not.Null);
                Assert.That(candidates[0]["declaringType"], Is.Not.Null);
                Assert.That(candidates[0]["parameterTypes"], Is.TypeOf<JArray>());
            }
            Assert.That(InvocationFixture.Calls, Is.Zero);
        }

        [Test]
        public void CompletedInvocationIncludesPersistedFramesUtcAndItsLogWindow()
        {
            ConsoleLogHistoryService.RecordForTests("before acceptance");
            var job = Start(Request("Routine"));
            Assert.That(Convert.ToInt64(job.metadata["acceptedLogSeq"]), Is.EqualTo(1));
            ConsoleLogHistoryService.RecordForTests("queued");
            _frame = 102;
            ComponentInvocationService.Process(job);
            Assert.That(Convert.ToInt64(job.metadata["startLogSeq"]), Is.EqualTo(2));
            Assert.That(Convert.ToInt64(job.metadata["startFrame"]), Is.EqualTo(102));
            ConsoleLogHistoryService.RecordForTests("during execution", "fixture stack", LogType.Error);
            _frame = 104;
            ComponentInvocationService.Process(job);
            Assert.That(job.status, Is.EqualTo("completed"), "Associated error logs do not change the invocation result.");
            Assert.That(job.resultData["startFrame"], Is.EqualTo(102L));
            Assert.That(job.resultData["finishFrame"], Is.EqualTo(104));
            Assert.That(((JValue)job.resultData["acceptedUtc"]).Value<string>(), Does.EndWith("Z"));
            Assert.That(((JValue)job.resultData["executionStartedUtc"]).Value<string>(), Does.EndWith("Z"));
            var logs = (JObject)job.resultData["logWindow"];
            Assert.That(logs["logs"].Count(), Is.EqualTo(2));
            Assert.That(logs["logs"][0].Value<string>("message"), Is.EqualTo("queued"));
            Assert.That(logs["logs"][1].Value<string>("type"), Is.EqualTo("Error"));
        }

        [TestCase("cancel")]
        [TestCase("timeout")]
        [TestCase("interrupted")]
        public void NonSuccessTerminationAlsoCarriesEvidence(string termination)
        {
            var job = Start(Request("Pending"));
            ConsoleLogHistoryService.RecordForTests("accepted window");
            if (termination == "cancel") ComponentInvocationService.NotifyCancelled(job);
            else
            {
                if (termination == "timeout") job.metadata["deadlineTicks"] = DateTime.UtcNow.AddSeconds(-1).Ticks;
                else job.metadata["invocationStarted"] = true;
                ComponentInvocationService.Process(job);
            }
            Assert.That(job.resultData.ContainsKey("finishFrame"), Is.True);
            Assert.That(job.resultData["startFrame"], Is.Null);
            Assert.That(((JObject)job.resultData["logWindow"])["logs"].Count(), Is.EqualTo(1));
        }

        [Test]
        public void UnsupportedResultExplicitlyReportsMissingSafeSerialization()
        {
            var result = ComponentInvocationService.BoundedValue(new UnsafeResultFixture());
            Assert.That(result.Value<bool>("$unsupported"), Is.True);
            Assert.That(result.Value<bool>("$truncated"), Is.True);
            Assert.That(result["$unsupportedProperties"][0].Value<string>(), Is.EqualTo("Dangerous"));
        }

        [Test]
        public void MoreThanSixtyFourPublicMembersAreMarkedTruncated()
        {
            var result = ComponentInvocationService.BoundedValue(new ManyMembersFixture());
            Assert.That(result.Value<bool>("$truncated"), Is.True);
            Assert.That(((JObject)result).Properties().Count(p => p.Name.StartsWith("Value", StringComparison.Ordinal)), Is.EqualTo(64));
        }

        [Test]
        public void OverloadsRequireExactSignatureEvenWhenOneArgumentLooksConvertible()
        {
            Assert.Throws<ComponentInvocationService.AmbiguousCallException>(() => ComponentInvocationService.Bind(Request("Overloaded", "[3]")));
            var call = ComponentInvocationService.Bind(Request("Overloaded", "[3]", "[\"System.Int32\"]"));
            Assert.That(call.Method.Invoke(call.Target, call.Arguments), Is.EqualTo(4));
        }

        [Test]
        public void PrivateStaticMethodUsesSharedVectorConversion()
        {
            var call = ComponentInvocationService.Bind(Request("VectorLength", "[\"3,4,0\"]"));
            Assert.That(call.Method.Invoke(null, call.Arguments), Is.EqualTo(5f));
        }

        [Test]
        public void InheritedProtectedInstanceMethodResolves()
        {
            _target = new GameObject("InvocationFixture_" + Guid.NewGuid().ToString("N"));
            _target.AddComponent<InvocationComponentFixture>();
            GameObjectFinder.InvalidateCache();
            var request = new ComponentInvocationService.Request { componentType = typeof(InvocationComponentFixture).FullName,
                methodName = "ProtectedDouble", path = _target.name, arguments = "[7]", executionMode = "edit" };
            var call = ComponentInvocationService.Bind(request);
            Assert.That(call.Method.Invoke(call.Target, call.Arguments), Is.EqualTo(14));
        }

        [Test]
        public void InstanceCallsReadASharedShortNameAsTheComponentType()
        {
            _target = new GameObject("InvocationFixture_" + Guid.NewGuid().ToString("N"));
            _target.AddComponent<ShortNameShared>();
            GameObjectFinder.InvalidateCache();
            var request = new ComponentInvocationService.Request { componentType = nameof(ShortNameShared),
                methodName = "Answer", path = _target.name, executionMode = "edit" };
            var call = ComponentInvocationService.Bind(request);
            Assert.That(call.Method.Invoke(call.Target, call.Arguments), Is.EqualTo(42));
            Assert.Throws<ComponentInvocationService.AmbiguousCallException>(
                () => ComponentInvocationService.ResolveType(nameof(ShortNameShared)),
                "without an instance target the short name stays ambiguous");
        }

        [Test]
        public void GenericMethodRequiresAndBindsClosedTypeArguments()
        {
            var request = Request("Echo", "[17]");
            Assert.Throws<ArgumentException>(() => ComponentInvocationService.Bind(request));
            request.genericTypeArguments = "[\"System.Int32\"]";
            var call = ComponentInvocationService.Bind(request);
            Assert.That(call.Method.ContainsGenericParameters, Is.False);
            Assert.That(call.Method.Invoke(null, call.Arguments), Is.EqualTo(17));
        }

        [Test]
        public void OptionalAndParamsArgumentsAreBoundWithoutOverloadGuessing()
        {
            var optional = ComponentInvocationService.Bind(Request("Optional"));
            Assert.That(optional.Method.Invoke(null, optional.Arguments), Is.EqualTo(23));
            var packed = ComponentInvocationService.Bind(Request("Sum", "[[1,2,3]]"));
            var spread = ComponentInvocationService.Bind(Request("Sum", "[1,2,3]"));
            Assert.That(packed.Method.Invoke(null, packed.Arguments), Is.EqualTo(6));
            Assert.That(spread.Method.Invoke(null, spread.Arguments), Is.EqualTo(6));
        }

        [Test]
        public void RefAndOutValuesAreReadBackFromInvocation()
        {
            var job = Start(Request("RefOut", "[3,null]", "[\"System.Int32&\",\"System.String&\"]"));
            ComponentInvocationService.Process(job);
            Assert.That(job.status, Is.EqualTo("completed"));
            var output = (JObject)job.resultData["refOut"];
            Assert.That(output.Value<int>("number"), Is.EqualTo(4));
            Assert.That(output.Value<string>("text"), Is.EqualTo("done"));
        }

#if !UNITY_6000_4_OR_NEWER
        // The legacy instance ID reads 0 on Unity 6000.4+, so this variant only exists below it.
        [Test]
        public void SceneObjectArgumentsResolveByInstanceId()
        {
            _target = new GameObject("InvocationReference");
            var token = new JObject { ["referenceInstanceId"] = UnityObjectIdUtility.GetObjectId(_target) };
            Assert.That(ComponentInvocationService.ConvertArgument(token, typeof(Transform)), Is.SameAs(_target.transform));
            Assert.Throws<ArgumentException>(() => ComponentInvocationService.ConvertArgument(token, typeof(BoxCollider)));
        }
#endif

        [Test]
        public void SceneObjectArgumentsResolveByPathAndName()
        {
            _target = new GameObject("InvocationReference_" + Guid.NewGuid().ToString("N"));
            GameObjectFinder.InvalidateCache();
            foreach (var key in new[] { "referencePath", "referenceName" })
            {
                var token = new JObject { [key] = _target.name };
                Assert.That(ComponentInvocationService.ConvertArgument(token, typeof(Transform)), Is.SameAs(_target.transform), key);
                Assert.Throws<ArgumentException>(() => ComponentInvocationService.ConvertArgument(token, typeof(BoxCollider)), key);
            }
        }

        [Test]
        public void UnityObjectResultsCarryAnEntityIdThatResolvesBack()
        {
            _target = new GameObject("InvocationResult");
            var result = (JObject)ComponentInvocationService.BoundedValue(_target);
            Assert.That(result.Value<string>("entityId"), Is.EqualTo(UnityObjectIdUtility.GetEntityId(_target)));
            Assert.That(result.Value<int>("instanceId"), Is.EqualTo(UnityObjectIdUtility.GetObjectId(_target)));
            Assert.That(result.Value<string>("name"), Is.EqualTo("InvocationResult"));
            Assert.That(result.Value<string>("type"), Is.EqualTo(typeof(GameObject).FullName));
            Assert.That(UnityObjectIdUtility.EntityIdToObject(result.Value<string>("entityId")), Is.SameAs(_target));
        }

        [Test]
        public void LiteralNullStringRemainsStringAndNullValueTypeIsRejected()
        {
            Assert.That(ComponentInvocationService.ConvertArgument(new JValue("null"), typeof(string)), Is.EqualTo("null"));
            Assert.Throws<ArgumentException>(() => ComponentInvocationService.ConvertArgument(JValue.CreateNull(), typeof(int)));
        }

        [TestCase("TaskResult", 41)]
        [TestCase("ValueTaskResult", 42)]
        public void CompletedAsyncResultsAreUnwrapped(string method, int expected)
        {
            var job = Start(Request(method));
            ComponentInvocationService.Process(job);
            Assert.That(job.status, Is.EqualTo("completed"));
            Assert.That(((JToken)job.resultData["returnValue"]).Value<int>(), Is.EqualTo(expected));
        }

        [Test]
        public void EnumeratorAdvancesAcrossTicksAndIsDisposed()
        {
            var job = Start(Request("Routine"));
            ComponentInvocationService.Process(job);
            Assert.That(InvocationFixture.Calls, Is.EqualTo(1));
            ComponentInvocationService.Process(job);
            Assert.That(job.status, Is.EqualTo("completed"));
            Assert.That(InvocationFixture.Calls, Is.EqualTo(3));
        }

        [Test]
        public void MethodExceptionIsReportedAndNeverReplayed()
        {
            var job = Start(Request("ThrowOnce"));
            ComponentInvocationService.Process(job);
            Assert.That(job.status, Is.EqualTo("failed"));
            Assert.That(InvocationFixture.Calls, Is.EqualTo(1));
            Assert.That(job.error, Does.Contain("fixture failure"));
            var exception = (JObject)job.resultData["exception"];
            Assert.That(exception.Value<string>("type"), Is.EqualTo(typeof(InvalidOperationException).FullName));
            Assert.That(exception.Value<string>("message"), Is.EqualTo("fixture failure"));
            Assert.That(exception.Value<string>("stackTrace").Length, Is.LessThanOrEqualTo(8192));
            Assert.That(job.resultData.ContainsKey("logWindow"), Is.True);
        }

        [Test]
        public void PersistedStartedInvocationWithoutContextBecomesInterrupted()
        {
            var job = Start(Request("Optional"));
            job.metadata["invocationStarted"] = true;
            ComponentInvocationService.Process(job);
            Assert.That(job.status, Is.EqualTo("failed"));
            Assert.That(job.currentStage, Is.EqualTo("interrupted"));
            Assert.That(InvocationFixture.Calls, Is.Zero);
        }

        [Test]
        public void ExpiredInvocationNeverExecutes()
        {
            var job = Start(Request("Optional"));
            job.metadata["deadlineTicks"] = DateTime.UtcNow.AddSeconds(-1).Ticks;
            ComponentInvocationService.Process(job);
            Assert.That(job.currentStage, Is.EqualTo("timeout"));
            Assert.That(InvocationFixture.Calls, Is.Zero);
        }

        [Test]
        public void PendingTaskCancellationRemovesContextWithoutReplaying()
        {
            var job = Start(Request("Pending"));
            ComponentInvocationService.Process(job);
            ComponentInvocationService.NotifyCancelled(job);
            Assert.That(job.resultData["notReplayed"], Is.True);
            Assert.That(InvocationFixture.Calls, Is.EqualTo(1));
            ComponentInvocationService.Process(job);
            Assert.That(job.currentStage, Is.EqualTo("interrupted"));
            Assert.That(InvocationFixture.Calls, Is.EqualTo(1));
        }

        [Test]
        public void BoundedSerializationHandlesCyclesAndDoesNotInvokeCustomGetters()
        {
            var value = new ResultFixture { Value = 8 };
            value.Self = value;
            var result = ComponentInvocationService.BoundedValue(value);
            Assert.That(result["Self"]["cycle"].Value<bool>(), Is.True);
            Assert.That(result["Value"].Value<int>(), Is.EqualTo(8));
            Assert.That(result["Dangerous"], Is.Null);
            Assert.That(ComponentInvocationService.BoundedValue(new string('x', 5000)).Value<string>().Length, Is.LessThan(4200));
        }

        [Test]
        public void PlayDefaultRefusesEditModeThroughPureStateSeam()
        {
            var request = Request("Optional"); request.executionMode = "play";
            var response = (JObject)ComponentInvocationService.Start(request);
            Assert.That(response["error"], Is.Not.Null);
            Assert.That(InvocationFixture.Calls, Is.Zero);
        }

        [Test]
        public void HighRiskMetadataIsExplicitWithoutExecutingGuards()
        {
            foreach (var name in new[] { "component_invoke_method", "component_invoke_method_batch", "editor_run_scenario" })
            {
                Assert.That(SkillRouter.TryGetSkill(name, out var skill), Is.True);
                Assert.That(skill.RiskLevel, Is.EqualTo("high"));
                Assert.That(skill.ReadOnly, Is.False);
            }
        }

        internal sealed class UnsafeResultFixture
        {
            public int Dangerous => throw new InvalidOperationException("must not read");
        }
        internal sealed class ManyMembersFixture
        {
            public int Value0 { get; set; }
            public int Value1 { get; set; }
            public int Value2 { get; set; }
            public int Value3 { get; set; }
            public int Value4 { get; set; }
            public int Value5 { get; set; }
            public int Value6 { get; set; }
            public int Value7 { get; set; }
            public int Value8 { get; set; }
            public int Value9 { get; set; }
            public int Value10 { get; set; }
            public int Value11 { get; set; }
            public int Value12 { get; set; }
            public int Value13 { get; set; }
            public int Value14 { get; set; }
            public int Value15 { get; set; }
            public int Value16 { get; set; }
            public int Value17 { get; set; }
            public int Value18 { get; set; }
            public int Value19 { get; set; }
            public int Value20 { get; set; }
            public int Value21 { get; set; }
            public int Value22 { get; set; }
            public int Value23 { get; set; }
            public int Value24 { get; set; }
            public int Value25 { get; set; }
            public int Value26 { get; set; }
            public int Value27 { get; set; }
            public int Value28 { get; set; }
            public int Value29 { get; set; }
            public int Value30 { get; set; }
            public int Value31 { get; set; }
            public int Value32 { get; set; }
            public int Value33 { get; set; }
            public int Value34 { get; set; }
            public int Value35 { get; set; }
            public int Value36 { get; set; }
            public int Value37 { get; set; }
            public int Value38 { get; set; }
            public int Value39 { get; set; }
            public int Value40 { get; set; }
            public int Value41 { get; set; }
            public int Value42 { get; set; }
            public int Value43 { get; set; }
            public int Value44 { get; set; }
            public int Value45 { get; set; }
            public int Value46 { get; set; }
            public int Value47 { get; set; }
            public int Value48 { get; set; }
            public int Value49 { get; set; }
            public int Value50 { get; set; }
            public int Value51 { get; set; }
            public int Value52 { get; set; }
            public int Value53 { get; set; }
            public int Value54 { get; set; }
            public int Value55 { get; set; }
            public int Value56 { get; set; }
            public int Value57 { get; set; }
            public int Value58 { get; set; }
            public int Value59 { get; set; }
            public int Value60 { get; set; }
            public int Value61 { get; set; }
            public int Value62 { get; set; }
            public int Value63 { get; set; }
            public int Value64 { get; set; }
        }

        internal sealed class ResultFixture
        {
            public ResultFixture Self;
            public int Value { get; set; }
            public int Dangerous => throw new InvalidOperationException("getter must not execute");
        }

        internal static class InvocationFixture
        {
            internal static int Calls;
            public static int Overloaded(int value) => value + 1;
            public static string Overloaded(string value) => value;
            private static float VectorLength(Vector3 value) => value.magnitude;
            public static T Echo<T>(T value) => value;
            public static int Optional(int value = 23) { Calls++; return value; }
            public static int Sum(params int[] values) { int sum = 0; foreach (int value in values) sum += value; return sum; }
            public static void RefOut(ref int number, out string text) { number++; text = "done"; }
            public static Task<int> TaskResult() => Task.FromResult(41);
            public static ValueTask<int> ValueTaskResult() => new ValueTask<int>(42);
            public static Task Pending() { Calls++; return new TaskCompletionSource<int>().Task; }
            public static IEnumerator Routine() { try { Calls++; yield return null; Calls++; } finally { Calls++; } }
            public static void ThrowOnce() { Calls++; throw new InvalidOperationException("fixture failure"); }
        }
    }

    public class InvocationComponentBase : MonoBehaviour
    {
        protected int ProtectedDouble(int value) => value * 2;
    }
    public sealed class InvocationComponentFixture : InvocationComponentBase { }

    /// <summary>Shares its short name with a plain class, like UnityEngine.UI.Button and UnityEngine.UIElements.Button.</summary>
    public sealed class ShortNameShared : MonoBehaviour
    {
        public int Answer() => 42;
    }
}

namespace UnitySkills.Tests.Core.ShortNameTwin
{
    public sealed class ShortNameShared
    {
        public int Answer() => 7;
    }
}

// Producer:Betsy
