using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    internal static class ComponentInvocationService
    {
        internal sealed class Request
        {
            public string name, path, componentType, assembly, methodName, parameterTypes, genericTypeArguments, arguments;
            public int instanceId, componentIndex;
            public bool isStatic;
            public string executionMode = "play";
            public int timeoutSeconds = 30;
        }

        internal sealed class AmbiguousCallException : ArgumentException
        {
            internal readonly JArray Candidates;
            internal AmbiguousCallException(string message, JArray candidates) : base(message) { Candidates = candidates; }
        }

        internal sealed class BoundCall
        {
            internal MethodInfo Method;
            internal object Target;
            internal object[] Arguments;
        }

        private sealed class RunningCall
        {
            internal BoundCall Call;
            internal object Result;
            internal Task Task;
            internal readonly Stack<IEnumerator> Enumerators = new Stack<IEnumerator>();
            internal object Yielded;
            internal double ResumeAt;
            internal int ResumeFrame = -1;
        }

        private static readonly Dictionary<string, RunningCall> Running = new Dictionary<string, RunningCall>();
        internal static bool? PlayingOverrideForTests;
        internal static Func<int> FrameForTests;
        private static int Frame => FrameForTests?.Invoke() ?? Time.frameCount;
        internal static bool IsPlaying => PlayingOverrideForTests ?? EditorApplication.isPlaying;

        internal static object Error(string message, SkillErrorCode code = SkillErrorCode.SemanticInvalid) =>
            JObject.Parse(SkillErrorResponse.Build(code, message, retryStrategy: SkillErrorResponse.Abort));
        private static object Error(Exception exception) => JObject.Parse(SkillErrorResponse.Build(
            SkillErrorCode.SemanticInvalid, exception.Message,
            details: exception is AmbiguousCallException ambiguous ? new JObject { ["candidates"] = ambiguous.Candidates } : null,
            retryStrategy: SkillErrorResponse.Abort));

        internal static Type ResolveType(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("componentType/type name is required.");
            var exact = Type.GetType(name, false);
            if (exact != null) return exact;
            var types = new List<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var full = assembly.GetType(name, false);
                if (full != null) types.Add(full);
            }
            if (types.Count == 0)
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] all;
                    try { all = assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { all = ex.Types; }
                    catch { continue; }
                    types.AddRange(all.Where(t => t != null && t.Name == name));
                }
            types = types.Distinct().ToList();
            if (types.Count == 0) throw new ArgumentException("Type not found: " + name);
            if (types.Count > 1) throw new AmbiguousCallException("Ambiguous type; use an assembly-qualified name: " + name,
                new JArray(types.Take(50).Select(t => new JObject { ["type"] = TypeName(t), ["assembly"] = t.Assembly.GetName().Name })));
            return types[0];
        }

        internal static IEnumerable<MethodInfo> Methods(Type type, bool isStatic, bool includePrivate)
        {
            var seen = new HashSet<string>();
            for (var current = type; current != null; current = current.BaseType)
                foreach (var method in current.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Public |
                    (includePrivate ? BindingFlags.NonPublic : 0) | (isStatic ? BindingFlags.Static : BindingFlags.Instance)))
                    if (seen.Add(Signature(method))) yield return method;
        }

        internal static string TypeName(Type type) => type.FullName ?? type.ToString();
        internal static string Signature(MethodInfo method) => method.Name +
            (method.IsGenericMethod ? "`" + method.GetGenericArguments().Length : "") + "(" +
            string.Join(",", method.GetParameters().Select(p => TypeName(p.ParameterType))) + ")";

        internal static object GetMethods(string componentType, bool isStatic, bool includePrivate, string methodName, string assembly = null)
        {
            try
            {
                var type = ResolveType(string.IsNullOrWhiteSpace(assembly) ? componentType : componentType + ", " + assembly);
                var methods = Methods(type, isStatic, includePrivate).Where(m => string.IsNullOrEmpty(methodName) || m.Name == methodName).Take(500)
                    .Select(m => new { name = m.Name, signature = Signature(m), declaringType = TypeName(m.DeclaringType),
                        isStatic = m.IsStatic, visibility = m.IsPublic ? "public" : m.IsFamily ? "protected" : "private",
                        returnType = TypeName(m.ReturnType), genericArity = m.IsGenericMethod ? m.GetGenericArguments().Length : 0,
                        parameters = m.GetParameters().Select(p => new { name = p.Name, type = TypeName(p.ParameterType),
                            optional = p.IsOptional, isOut = p.IsOut, isRef = p.ParameterType.IsByRef,
                            isParams = p.IsDefined(typeof(ParamArrayAttribute), false) }).ToArray() }).ToArray();
                return new { componentType = TypeName(type), methods, limit = 500, truncated = methods.Length == 500 };
            }
            catch (Exception ex) { return Error(ex); }
        }

        internal static BoundCall Bind(Request request)
        {
            var type = ResolveType(string.IsNullOrWhiteSpace(request.assembly) ? request.componentType : request.componentType + ", " + request.assembly);
            if (string.IsNullOrWhiteSpace(request.methodName)) throw new ArgumentException("methodName is required.");
            var generics = ParseTypes(request.genericTypeArguments);
            var requestedTypes = request.parameterTypes == null ? null : ParseTypes(request.parameterTypes);
            var candidates = new List<MethodInfo>();
            foreach (var raw in Methods(type, request.isStatic, true).Where(m => m.Name == request.methodName))
            {
                MethodInfo method = raw;
                if (method.IsGenericMethodDefinition)
                {
                    if (generics.Length != method.GetGenericArguments().Length) continue;
                    try { method = method.MakeGenericMethod(generics); } catch (ArgumentException) { continue; }
                }
                else if (generics.Length != 0) continue;
                if (method.ContainsGenericParameters) continue;
                if (requestedTypes != null && !method.GetParameters().Select(p => p.ParameterType).SequenceEqual(requestedTypes)) continue;
                candidates.Add(method);
            }
            if (candidates.Count == 0) throw new ArgumentException("No matching closed method; provide exact parameterTypes and genericTypeArguments.");
            if (candidates.Count > 1) throw new AmbiguousCallException(
                "Overload is ambiguous; provide exact parameterTypes from candidates (including & for ref/out).",
                new JArray(candidates.Take(50).Select(m => new JObject {
                    ["signature"] = Signature(m), ["declaringType"] = TypeName(m.DeclaringType),
                    ["parameterTypes"] = new JArray(m.GetParameters().Select(parameter => TypeName(parameter.ParameterType))) })));
            var selected = candidates[0];
            if (selected.ReturnType.IsByRef || selected.ReturnType.IsPointer || selected.GetParameters().Any(p => p.ParameterType.IsPointer))
                throw new ArgumentException("Pointer and ref-return methods are unsupported.");
            object target = null;
            if (!request.isStatic)
            {
                if (!typeof(Component).IsAssignableFrom(type)) throw new ArgumentException("Instance methods require a Unity Component; ordinary C# types support static calls.");
                var (go, error) = GameObjectFinder.FindOrError(request.name, request.instanceId, request.path);
                if (error != null) throw new ArgumentException("Target GameObject could not be uniquely resolved.");
                var components = go.GetComponents(type);
                if (request.componentIndex < 0 || request.componentIndex >= components.Length) throw new ArgumentException("componentIndex is out of range.");
                target = components[request.componentIndex];
            }
            var args = string.IsNullOrWhiteSpace(request.arguments) ? new JArray() : JArray.Parse(request.arguments);
            return new BoundCall { Target = target, Method = selected, Arguments = BindArguments(selected, args) };
        }

        private static Type[] ParseTypes(string json) => string.IsNullOrWhiteSpace(json) ? Type.EmptyTypes : JArray.Parse(json).Select(t => ResolveType((string)t)).ToArray();

        internal static object[] BindArguments(MethodInfo method, JArray supplied)
        {
            var parameters = method.GetParameters();
            var result = new object[parameters.Length];
            int index = 0;
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                var type = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
                if (p.IsDefined(typeof(ParamArrayAttribute), false))
                {
                    var rest = supplied.Count - index == 1 && supplied[index] is JArray packed ? packed : new JArray(supplied.Skip(index));
                    result[i] = ConvertArgument(rest, type); index = supplied.Count;
                }
                else if (p.IsOut)
                {
                    result[i] = type.IsValueType ? Activator.CreateInstance(type) : null;
                    // Out parameters retain their positional slot (null), preventing later argument shifts.
                    if (index < supplied.Count) index++;
                }
                else if (index < supplied.Count) result[i] = ConvertArgument(supplied[index++], type);
                else if (p.IsOptional) result[i] = p.DefaultValue;
                else throw new ArgumentException("Missing argument: " + p.Name);
            }
            if (index < supplied.Count) throw new ArgumentException("Too many arguments.");
            return result;
        }

        internal static object ConvertArgument(JToken value, Type type)
        {
            if (value == null || value.Type == JTokenType.Null)
            {
                if (type.IsValueType && Nullable.GetUnderlyingType(type) == null) throw new ArgumentException("Null is not valid for " + TypeName(type));
                return null;
            }
            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable != null) return ConvertArgument(value, nullable);
            if (typeof(UnityEngine.Object).IsAssignableFrom(type))
            {
                if (!(value is JObject reference)) throw new ArgumentException("Object arguments require an assetPath or referenceName/referencePath/referenceInstanceId object.");
                UnityEngine.Object resolved;
                if (reference.Value<string>("assetPath") is string assetPath) resolved = AssetDatabase.LoadAssetAtPath(assetPath, type);
                else
                {
                    var (go, error) = GameObjectFinder.FindOrError(reference.Value<string>("referenceName"), reference.Value<int?>("referenceInstanceId") ?? 0, reference.Value<string>("referencePath"));
                    if (error != null) throw new ArgumentException("Object argument reference could not be resolved.");
                    resolved = type == typeof(GameObject) || type == typeof(UnityEngine.Object) ? (UnityEngine.Object)go : go.GetComponent(type);
                }
                if (resolved == null || !type.IsInstanceOfType(resolved)) throw new ArgumentException("Object reference does not match " + TypeName(type));
                return resolved;
            }
            if (type.IsArray)
            {
                if (!(value is JArray array)) throw new ArgumentException("Array argument requires a JSON array.");
                var result = Array.CreateInstance(type.GetElementType(), array.Count);
                for (int i = 0; i < array.Count; i++) result.SetValue(ConvertArgument(array[i], type.GetElementType()), i);
                return result;
            }
            if (type == typeof(string)) return value.Type == JTokenType.String ? (string)value : value.ToString(Formatting.None);
            if (type == typeof(object)) return value.ToObject<object>(JsonSerializer.Create(SkillsCommon.JsonSettings));
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) && value is JArray listValues)
            {
                var list = (IList)Activator.CreateInstance(type);
                foreach (var item in listValues) list.Add(ConvertArgument(item, type.GetGenericArguments()[0]));
                return list;
            }
            // Do not deserialize arbitrary user DTO constructors/setters: preview uses this exact converter too.
            return ComponentSkills.ConvertValue(value.Type == JTokenType.String ? (string)value : value.ToString(Formatting.None), type);
        }

        internal static object Preview(Request request)
        {
            try
            {
                if (request.executionMode != "play" && request.executionMode != "edit") return Error("executionMode must be play or edit.");
                if (request.timeoutSeconds < 1 || request.timeoutSeconds > 600) return Error("timeoutSeconds must be between 1 and 600.");
                if ((request.executionMode == "play") != IsPlaying) return Error("executionMode does not match the current Editor mode.");
                var call = Bind(request);
                return new { valid = true, signature = Signature(call.Method), returnType = TypeName(call.Method.ReturnType),
                    executionMode = request.executionMode, undoSupported = false, atomic = false };
            }
            catch (Exception ex) { return Error(ex); }
        }

        internal static object Start(Request request)
        {
            try
            {
                if (request.executionMode != "play" && request.executionMode != "edit") return Error("executionMode must be play or edit.");
                if (request.timeoutSeconds < 1 || request.timeoutSeconds > 600) return Error("timeoutSeconds must be between 1 and 600.");
                if ((request.executionMode == "play") != IsPlaying) return Error("executionMode does not match the current Editor mode; enter Play first or explicitly choose edit.");
                if (ServerAvailabilityHelper.IsCompilationInProgress()) return Error("Cannot invoke while compilation or asset update is in progress.", SkillErrorCode.Compiling);
                Bind(request); // Resolve all arguments before accepting; bind again at execution to avoid stale objects.
                var job = AsyncJobService.CreateJob("component_invoke", "queued", "Method invocation accepted. No Undo or atomicity is promised.", true,
                    new Dictionary<string, object> { ["request"] = JsonConvert.SerializeObject(request, SkillsCommon.JsonSettings),
                        ["deadlineTicks"] = DateTime.UtcNow.AddSeconds(request.timeoutSeconds).Ticks,
                        ["acceptedUtcTicks"] = DateTime.UtcNow.Ticks, ["acceptedFrame"] = Frame,
                        ["acceptedLogSeq"] = ConsoleLogHistoryService.GetCurrentSeq() });
                return new { success = true, status = "accepted", jobId = job.jobId, kind = job.kind, waitUrl = AsyncJobService.BuildWaitUrl(job.jobId) };
            }
            catch (Exception ex) { return Error(ex); }
        }

        internal static void Process(BatchJobRecord job)
        {
            if (job == null || job.status == "completed" || job.status == "failed" || job.status == "cancelled") return;
            try
            {
                if (DateTime.UtcNow.Ticks > Long(job, "deadlineTicks")) { Fail(job, "Invocation timed out. User code may continue; it is never replayed.", "timeout"); return; }
                if (!Running.TryGetValue(job.jobId, out var context))
                {
                    if (Bool(job, "invocationStarted")) { Fail(job, "Invocation interrupted by domain reload; execution is not replayed.", "interrupted"); return; }
                    var request = JsonConvert.DeserializeObject<Request>((string)job.metadata["request"], SkillsCommon.JsonSettings);
                    if ((request.executionMode == "play") != IsPlaying) { Fail(job, "Editor mode changed before invocation."); return; }
                    var bound = Bind(request);
                    job.metadata["invocationStarted"] = true;
                    job.metadata["executionStartedUtcTicks"] = DateTime.UtcNow.Ticks;
                    job.metadata["startFrame"] = Frame;
                    job.metadata["startLogSeq"] = ConsoleLogHistoryService.GetCurrentSeq();
                    job.status = "running";
                    job.currentStage = "invoking";
                    Persist(job); // Persist before invoking arbitrary code, including code that reloads assemblies.
                    context = new RunningCall { Call = bound };
                    Running[job.jobId] = context;
                    context.Result = bound.Method.Invoke(bound.Target, bound.Arguments);
                    if (context.Result is Task task) context.Task = task;
                    else if (context.Result != null && (context.Result.GetType().FullName == "System.Threading.Tasks.ValueTask" || context.Result.GetType().FullName.StartsWith("System.Threading.Tasks.ValueTask`", StringComparison.Ordinal)))
                        context.Task = (Task)context.Result.GetType().GetMethod("AsTask", Type.EmptyTypes).Invoke(context.Result, null);
                    else if (context.Result is IEnumerator enumerator) context.Enumerators.Push(enumerator);
                }
                if (context.Task != null)
                {
                    if (!context.Task.IsCompleted) return;
                    context.Task.GetAwaiter().GetResult();
                    var resultProperty = context.Task.GetType().GetProperty("Result");
                    context.Result = resultProperty?.PropertyType.FullName == "System.Threading.Tasks.VoidTaskResult" ? null : resultProperty?.GetValue(context.Task);
                }
                if (context.Enumerators.Count > 0)
                {
                    if (!TickEnumerator(context)) return;
                    context.Result = null;
                }
                if (DateTime.UtcNow.Ticks > Long(job, "deadlineTicks"))
                { Fail(job, "Invocation exceeded its deadline. Synchronous user code cannot be preempted; it is not replayed.", "timeout"); return; }
                var output = new JObject();
                var parameters = context.Call.Method.GetParameters();
                for (int i = 0; i < parameters.Length; i++) if (parameters[i].ParameterType.IsByRef) output[parameters[i].Name] = BoundedValue(context.Call.Arguments[i]);
                job.resultData["returnValue"] = BoundedValue(context.Result);
                job.resultData["refOut"] = output;
                job.resultData["signature"] = Signature(context.Call.Method);
                Running.Remove(job.jobId);
                CaptureEvidence(job);
                AsyncJobService.CompleteJob(job.jobId, "Method completed.", job.resultData);
            }
            catch (Exception ex)
            {
                var cause = ex is TargetInvocationException invocation ? invocation.InnerException ?? ex : ex;
                string message = cause.Message ?? string.Empty;
                string stack = cause.StackTrace ?? string.Empty;
                job.resultData["exception"] = new JObject { ["type"] = Limit(TypeName(cause.GetType()), 1024),
                    ["message"] = Limit(message, 4096), ["stackTrace"] = Limit(stack, 8192),
                    ["truncated"] = message.Length > 4096 || stack.Length > 8192 };
                Fail(job, TypeName(cause.GetType()) + ": " + message);
            }
        }

        private static bool TickEnumerator(RunningCall context)
        {
            if (context.ResumeAt > Time.timeAsDouble) return false;
            if (IsPlaying && context.ResumeFrame >= Time.frameCount) return false;
            if (context.Yielded is AsyncOperation operation && !operation.isDone) return false;
            context.Yielded = null;
            for (int budget = 0; budget < 32 && context.Enumerators.Count > 0; budget++)
            {
                var current = context.Enumerators.Peek();
                if (!current.MoveNext()) { (current as IDisposable)?.Dispose(); context.Enumerators.Pop(); continue; }
                var yielded = current.Current;
                if (yielded is IEnumerator nested) { context.Enumerators.Push(nested); continue; }
                if (yielded is WaitForSeconds seconds)
                {
                    var duration = (float)typeof(WaitForSeconds).GetField("m_Seconds", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(seconds);
                    if (!IsPlaying) throw new NotSupportedException("WaitForSeconds requires Play Mode.");
                    context.ResumeAt = Time.timeAsDouble + duration;
                }
                else if (yielded != null && !(yielded is AsyncOperation)) throw new NotSupportedException("Unsupported coroutine yield: " + yielded.GetType().FullName);
                context.Yielded = yielded;
                context.ResumeFrame = Time.frameCount;
                return false;
            }
            return context.Enumerators.Count == 0;
        }

        internal static JToken BoundedValue(object value)
        {
            int budget = 512;
            return Serialize(value, 0, new HashSet<object>(ReferenceComparer.Instance), ref budget);
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        private static JToken Serialize(object value, int depth, HashSet<object> seen, ref int budget)
        {
            if (--budget < 0 || depth > 6) return new JObject { ["truncated"] = true };
            if (value == null) return JValue.CreateNull();
            if (value is UnityEngine.Object obj) return obj == null ? JValue.CreateNull() : new JObject { ["entityId"] = UnityObjectIdUtility.GetEntityId(obj), ["instanceId"] = UnityObjectIdUtility.GetObjectId(obj), ["name"] = obj.name, ["type"] = obj.GetType().FullName };
            if (value is string text) return new JValue(text.Length > 4096 ? text.Substring(0, 4096) + "[truncated]" : text);
            var type = value.GetType();
            if (type.IsEnum) return new JValue(value.ToString());
            if (type.IsPrimitive || value is decimal || value is DateTime || value is Guid) return new JValue(value);
            if (!type.IsValueType && !seen.Add(value)) return new JObject { ["cycle"] = true };
            if (value is IDictionary dictionary)
            {
                var result = new JObject(); int count = 0;
                foreach (DictionaryEntry entry in dictionary) { if (++count > 64 || budget <= 0) { result["$truncated"] = true; break; } result[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)] = Serialize(entry.Value, depth + 1, seen, ref budget); }
                return result;
            }
            if (value is IList list)
            {
                var result = new JArray();
                for (int i = 0; i < Math.Min(list.Count, 64) && budget > 0; i++) result.Add(Serialize(list[i], depth + 1, seen, ref budget));
                if (list.Count > result.Count) result.Add(new JObject { ["truncated"] = true });
                return result;
            }
            // Backing fields preserve DTO/anonymous results without executing property getters or lazy enumerables.
            var fields = new JObject { ["$type"] = TypeName(type) };
            var publicFields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
            var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
            bool omitted = publicFields.Length > 64 || properties.Length > 64;
            int serializedMembers = 0;
            var unsupportedProperties = new JArray();
            foreach (var field in publicFields.Take(64))
            {
                if (budget <= 0) { omitted = true; break; }
                fields[field.Name] = Serialize(field.GetValue(value), depth + 1, seen, ref budget);
                serializedMembers++;
            }
            foreach (var property in properties.Take(64))
            {
                if (budget <= 0) { omitted = true; break; }
                var declaring = property.DeclaringType ?? type;
                var backing = declaring.GetField("<" + property.Name + ">k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? declaring.GetField("<" + property.Name + ">i__Field", BindingFlags.Instance | BindingFlags.NonPublic);
                if (backing != null)
                {
                    fields[property.Name] = Serialize(backing.GetValue(value), depth + 1, seen, ref budget);
                    serializedMembers++;
                }
                else { omitted = true; unsupportedProperties.Add(property.Name); }
            }
            if (omitted) fields["$truncated"] = true;
            if (unsupportedProperties.Count > 0) fields["$unsupportedProperties"] = unsupportedProperties;
            if (serializedMembers == 0)
            {
                fields["$unsupported"] = true;
                fields["$reason"] = "No safely readable public fields or auto-property backing fields; custom getters and lazy enumerables were not executed.";
            }
            return fields;
        }

        internal static bool Bool(BatchJobRecord job, string key) => job.metadata.TryGetValue(key, out var value) && Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        internal static long Long(BatchJobRecord job, string key) => job.metadata.TryGetValue(key, out var value) ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : 0;
        internal static void Persist(BatchJobRecord job) { BatchPersistence.UpsertJob(job); BatchPersistence.FlushIfDirty(); }
        private static string Limit(string value, int limit) => value.Length > limit ? value.Substring(0, limit) : value;
        private static JToken Utc(BatchJobRecord job, string key) => Long(job, key) > 0
            ? new JValue(new DateTime(Long(job, key), DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture)) : JValue.CreateNull();

        internal static void CaptureEvidence(BatchJobRecord job)
        {
            long finishSeq = ConsoleLogHistoryService.GetCurrentSeq();
            var window = ConsoleLogHistoryService.ReadWindow(Long(job, "acceptedLogSeq"), finishSeq);
            finishSeq = window.snapshotSeq;
            bool started = Bool(job, "invocationStarted");
            job.resultData["acceptedUtc"] = Utc(job, "acceptedUtcTicks");
            job.resultData["executionStartedUtc"] = Utc(job, "executionStartedUtcTicks");
            job.resultData["finishedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            job.resultData["acceptedFrame"] = Long(job, "acceptedFrame");
            job.resultData["startFrame"] = started && job.metadata.ContainsKey("startFrame") ? (object)Long(job, "startFrame") : null;
            job.resultData["finishFrame"] = Frame;
            job.resultData["acceptedLogSeq"] = Long(job, "acceptedLogSeq");
            job.resultData["startLogSeq"] = started && job.metadata.ContainsKey("startLogSeq") ? (object)Long(job, "startLogSeq") : null;
            job.resultData["finishLogSeq"] = finishSeq;
            job.resultData["logWindow"] = JObject.FromObject(window, JsonSerializer.Create(SkillsCommon.JsonSettings));
        }

        private static void Fail(BatchJobRecord job, string message, string stage = "failed")
        { NotifyCancelled(job); AsyncJobService.FailJob(job.jobId, message.Length > 8192 ? message.Substring(0, 8192) : message, stage, job.resultData); }
        internal static void NotifyCancelled(BatchJobRecord job)
        {
            if (job == null || job.kind != "component_invoke" || job.status == "completed" || job.status == "failed" || job.status == "cancelled") return;
            if (Running.TryGetValue(job.jobId, out var context))
            {
                foreach (var enumerator in context.Enumerators) try { (enumerator as IDisposable)?.Dispose(); } catch { }
                Running.Remove(job.jobId);
            }
            job.resultData["notReplayed"] = true;
            job.resultData["userCodeMayContinue"] = Bool(job, "invocationStarted");
            CaptureEvidence(job);
        }
    }
}

// Producer:Betsy
