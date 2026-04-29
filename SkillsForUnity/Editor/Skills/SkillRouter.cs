using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Routes REST API requests to skill methods.
    /// </summary>
    public static class SkillRouter
    {
        internal const int SkillSchemaVersion = 2;

        internal enum RequestMode
        {
            Execute,
            DryRun,
            Plan
        }

        internal sealed class ParameterValidationResult
        {
            public JObject Args { get; set; }
            public object[] InvokeArgs { get; set; }
            public List<string> MissingParams { get; } = new List<string>();
            public List<object> UnknownParams { get; } = new List<object>();
            public List<object> TypeErrors { get; } = new List<object>();
            public List<object> SemanticErrors { get; } = new List<object>();
            public List<string> Warnings { get; } = new List<string>();
            public List<object> ParameterDetails { get; } = new List<object>();
            public bool Valid => MissingParams.Count == 0 && UnknownParams.Count == 0 && TypeErrors.Count == 0 && SemanticErrors.Count == 0;
        }

        internal sealed class SkillInfo
        {
            public string Name;
            public string Description;
            public MethodInfo Method;
            public ParameterInfo[] Parameters;
            public bool TracksWorkflow;
            // Intent-level metadata (v1.7)
            public SkillCategory Category;
            public SkillOperation Operation;
            public string[] Tags;
            public string[] Outputs;
            public string[] RequiresInput;
            public bool ReadOnly;
            // Risk & impact metadata
            public bool MutatesScene;
            public bool MutatesAssets;
            public bool MayTriggerReload;
            public bool MayEnterPlayMode;
            public bool SupportsDryRun;
            public string RiskLevel;
            public string[] RequiresPackages;
            // Cached to avoid repeated allocations per Execute/DryRun call
            public string[] ParameterNames;
            public HashSet<string> AllowedParameterSet;
            // Pre-computed lowercase for filtering/search (avoids per-query ToLowerInvariant)
            public string NameLower;
            public string DescriptionLower;
            public string[] TagsLower;
        }

        private static volatile Dictionary<string, SkillInfo> _skills;
        private static volatile bool _initialized;
        private static string _cachedManifest;
        private static string _cachedSchema;
        private static Dictionary<string, List<SkillInfo>> _outputIndex;

        /// <summary>Number of registered skills. Avoids parsing manifest just for a count.</summary>
        public static int SkillCount
        {
            get
            {
                Initialize();
                return _skills.Count;
            }
        }
        private static readonly object _initLock = new object();

        private static HashSet<string> _workflowTrackedSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _reservedBodyParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "verbose",
            "_confirm"
        };

        private static readonly HashSet<string> _transactionlessSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "editor_undo",
            "editor_redo",
            "gameobject_create",
            "history_undo",
            "history_redo",
            "workflow_undo_task",
            "workflow_redo_task",
            "workflow_revert_task",
            "workflow_session_undo"
        };

        private static readonly Dictionary<string, Dictionary<string, string[]>> _commonParameterSuggestions =
            new Dictionary<string, Dictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase)
        {
            ["gameobject_set_transform"] = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["x"] = new[] { "posX" },
                ["y"] = new[] { "posY" },
                ["z"] = new[] { "posZ" }
            },
            ["shader_find"] = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["shaderName"] = new[] { "searchName" }
            },
            ["shader_check_errors"] = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["shaderName"] = new[] { "shaderNameOrPath" }
            },
            ["shader_get_keywords"] = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["shaderName"] = new[] { "shaderNameOrPath" }
            },
            ["camera_look_at"] = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["targetName"] = new[] { "x", "y", "z" }
            },
            ["cinemachine_set_vcam_property"] = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = new[] { "vcamName" }
            }
        };

        private static readonly Dictionary<string, Dictionary<string, string>> _commonParameterHints =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["camera_look_at"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["targetName"] = "camera_look_at 只接受世界坐标 x/y/z，不支持对象名。"
            },
            ["timeline_list_tracks"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["path"] = "timeline_list_tracks 的 path 是场景层级路径，不是 Assets 资源路径。"
            }
        };

        // ========== Intent Synonym Maps ==========

        private static readonly Dictionary<string, string[]> _synonymMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // Chinese → English
            {"创建", new[]{"create"}}, {"新建", new[]{"create"}}, {"添加", new[]{"add","create"}},
            {"删除", new[]{"delete"}}, {"移除", new[]{"delete","remove"}},
            {"移动", new[]{"move","position"}}, {"位置", new[]{"position","transform"}},
            {"旋转", new[]{"rotate","rotation"}}, {"缩放", new[]{"scale"}},
            {"修改", new[]{"modify","set"}}, {"设置", new[]{"set","modify"}},
            {"获取", new[]{"get","query"}}, {"查询", new[]{"query","get","list","find"}},
            {"查找", new[]{"find","search"}}, {"搜索", new[]{"search","find"}},
            {"复制", new[]{"duplicate","copy"}}, {"克隆", new[]{"duplicate","clone"}},
            {"重命名", new[]{"rename"}}, {"命名", new[]{"name","rename"}},
            {"颜色", new[]{"color","material"}}, {"上色", new[]{"color","material","set_color"}},
            {"材质", new[]{"material"}}, {"贴图", new[]{"texture"}}, {"纹理", new[]{"texture"}},
            {"灯光", new[]{"light"}}, {"光照", new[]{"light","lighting"}},
            {"摄像机", new[]{"camera"}}, {"相机", new[]{"camera"}},
            {"物理", new[]{"physics","rigidbody","collider"}},
            {"碰撞", new[]{"collider","collision","physics"}},
            {"刚体", new[]{"rigidbody","physics"}},
            {"动画", new[]{"animation","animator"}}, {"动画控制器", new[]{"animator","controller"}},
            {"预制体", new[]{"prefab"}}, {"预制件", new[]{"prefab"}},
            {"实例化", new[]{"instantiate","prefab"}}, {"生成", new[]{"instantiate","create","spawn"}},
            {"场景", new[]{"scene"}}, {"层级", new[]{"hierarchy","parent"}},
            {"父物体", new[]{"parent","set_parent"}}, {"子物体", new[]{"child","parent"}},
            {"组件", new[]{"component"}}, {"脚本", new[]{"script"}},
            {"方块", new[]{"cube"}}, {"球体", new[]{"sphere"}}, {"圆柱", new[]{"cylinder"}},
            {"平面", new[]{"plane"}}, {"胶囊", new[]{"capsule"}},
            {"地形", new[]{"terrain"}}, {"导航", new[]{"navmesh","navigation"}},
            {"音频", new[]{"audio"}}, {"声音", new[]{"audio","sound"}},
            {"UI", new[]{"ui","canvas"}}, {"界面", new[]{"ui","canvas"}},
            {"着色器", new[]{"shader"}}, {"模型", new[]{"model","mesh"}},
            {"截图", new[]{"screenshot","capture"}}, {"截屏", new[]{"screenshot","capture"}},
            {"撤销", new[]{"undo"}}, {"重做", new[]{"redo"}},
            {"保存", new[]{"save"}}, {"加载", new[]{"load"}},
            {"清理", new[]{"clean","cleanup"}}, {"优化", new[]{"optimize","optimization"}},
            {"调试", new[]{"debug"}}, {"日志", new[]{"console","log"}},
            {"测试", new[]{"test"}}, {"验证", new[]{"validate","validation"}},
            {"工作流", new[]{"workflow"}}, {"批量", new[]{"batch"}},
            {"包", new[]{"package"}}, {"资源", new[]{"asset"}}, {"导入", new[]{"import"}},
            // English aliases
            {"spawn", new[]{"instantiate","create"}}, {"remove", new[]{"delete"}},
            {"color", new[]{"material","set_color"}}, {"colour", new[]{"material","set_color"}},
            {"transform", new[]{"position","rotation","scale"}},
            {"pos", new[]{"position"}}, {"rot", new[]{"rotation"}},
            {"hierarchy", new[]{"parent","child","gameobject"}},
            {"mesh", new[]{"model"}}, {"tex", new[]{"texture"}}, {"mat", new[]{"material"}},
            {"anim", new[]{"animation","animator"}}, {"nav", new[]{"navmesh","navigation"}},
            {"rb", new[]{"rigidbody"}}, {"col", new[]{"collider"}},
            {"cam", new[]{"camera"}}, {"img", new[]{"texture","image"}},
            {"fx", new[]{"particle","effect"}}, {"vfx", new[]{"particle","effect"}},
        };

        private static readonly Dictionary<string, SkillOperation> _operationKeywords = new Dictionary<string, SkillOperation>(StringComparer.OrdinalIgnoreCase)
        {
            {"create", SkillOperation.Create}, {"创建", SkillOperation.Create}, {"新建", SkillOperation.Create},
            {"add", SkillOperation.Create}, {"添加", SkillOperation.Create},
            {"delete", SkillOperation.Delete}, {"删除", SkillOperation.Delete}, {"remove", SkillOperation.Delete}, {"移除", SkillOperation.Delete},
            {"query", SkillOperation.Query}, {"get", SkillOperation.Query}, {"list", SkillOperation.Query}, {"find", SkillOperation.Query},
            {"查询", SkillOperation.Query}, {"获取", SkillOperation.Query}, {"查找", SkillOperation.Query},
            {"modify", SkillOperation.Modify}, {"set", SkillOperation.Modify}, {"update", SkillOperation.Modify},
            {"修改", SkillOperation.Modify}, {"设置", SkillOperation.Modify},
            {"execute", SkillOperation.Execute}, {"run", SkillOperation.Execute}, {"执行", SkillOperation.Execute},
            {"analyze", SkillOperation.Analyze}, {"check", SkillOperation.Analyze}, {"分析", SkillOperation.Analyze}, {"检查", SkillOperation.Analyze},
        };

        private static readonly Dictionary<string, SkillCategory> _categoryKeywords = new Dictionary<string, SkillCategory>(StringComparer.OrdinalIgnoreCase)
        {
            {"gameobject", SkillCategory.GameObject}, {"物体", SkillCategory.GameObject}, {"对象", SkillCategory.GameObject},
            {"component", SkillCategory.Component}, {"组件", SkillCategory.Component},
            {"scene", SkillCategory.Scene}, {"场景", SkillCategory.Scene},
            {"material", SkillCategory.Material}, {"材质", SkillCategory.Material},
            {"light", SkillCategory.Light}, {"灯光", SkillCategory.Light}, {"光照", SkillCategory.Light},
            {"camera", SkillCategory.Camera}, {"摄像机", SkillCategory.Camera}, {"相机", SkillCategory.Camera},
            {"physics", SkillCategory.Physics}, {"物理", SkillCategory.Physics},
            {"prefab", SkillCategory.Prefab}, {"预制体", SkillCategory.Prefab},
            {"script", SkillCategory.Script}, {"脚本", SkillCategory.Script},
            {"ui", SkillCategory.UI}, {"界面", SkillCategory.UI},
            {"uitoolkit", SkillCategory.UIToolkit},
            {"animator", SkillCategory.Animator}, {"animation", SkillCategory.Animator}, {"动画", SkillCategory.Animator},
            {"audio", SkillCategory.Audio}, {"音频", SkillCategory.Audio}, {"声音", SkillCategory.Audio},
            {"texture", SkillCategory.Texture}, {"贴图", SkillCategory.Texture},
            {"shader", SkillCategory.Shader}, {"着色器", SkillCategory.Shader},
            {"shadergraph", SkillCategory.ShaderGraph}, {"subgraph", SkillCategory.ShaderGraph}, {"着色图", SkillCategory.ShaderGraph}, {"子图", SkillCategory.ShaderGraph},
            {"terrain", SkillCategory.Terrain}, {"地形", SkillCategory.Terrain},
            {"navmesh", SkillCategory.NavMesh}, {"导航", SkillCategory.NavMesh},
            {"model", SkillCategory.Model}, {"模型", SkillCategory.Model},
            {"asset", SkillCategory.Asset}, {"资源", SkillCategory.Asset},
            {"editor", SkillCategory.Editor}, {"编辑器", SkillCategory.Editor},
            {"package", SkillCategory.Package}, {"包", SkillCategory.Package},
            {"workflow", SkillCategory.Workflow}, {"工作流", SkillCategory.Workflow},
            {"debug", SkillCategory.Debug}, {"调试", SkillCategory.Debug},
            {"console", SkillCategory.Console}, {"控制台", SkillCategory.Console},
            {"test", SkillCategory.Test}, {"测试", SkillCategory.Test},
            {"validation", SkillCategory.Validation}, {"验证", SkillCategory.Validation},
            {"optimization", SkillCategory.Optimization}, {"优化", SkillCategory.Optimization},
            {"profiler", SkillCategory.Profiler}, {"性能", SkillCategory.Profiler},
            {"timeline", SkillCategory.Timeline}, {"时间线", SkillCategory.Timeline},
            {"cinemachine", SkillCategory.Cinemachine},
            {"probuilder", SkillCategory.ProBuilder},
            {"xr", SkillCategory.XR},
        };

        /// <summary>
        /// Matches keywords against a dictionary using exact + substring matching (for unsegmented Chinese).
        /// </summary>
        private static HashSet<TValue> MatchKeywords<TValue>(string[] keywords, Dictionary<string, TValue> map)
        {
            var results = new HashSet<TValue>();
            foreach (var kw in keywords)
            {
                if (map.TryGetValue(kw, out var val)) results.Add(val);
                foreach (var entry in map)
                {
                    if (entry.Key.Length >= 2 && kw.IndexOf(entry.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                        results.Add(entry.Value);
                }
            }
            return results;
        }

        private static string[] ExpandIntent(string[] keywords)
        {
            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kw in keywords) expanded.Add(kw);
            foreach (var synonyms in MatchKeywords(keywords, _synonymMap))
            {
                foreach (var s in synonyms) expanded.Add(s);
            }
            return expanded.ToArray();
        }

        private static HashSet<SkillOperation> ExtractOperations(string[] keywords)
            => MatchKeywords(keywords, _operationKeywords);

        private static HashSet<SkillCategory> ExtractCategories(string[] keywords)
            => MatchKeywords(keywords, _categoryKeywords);
        // Shared JSON settings from SkillsCommon (single definition, no duplication)
        private static readonly JsonSerializerSettings _jsonSettings = SkillsCommon.JsonSettings;

        private static string ErrorJson(string error) =>
            SkillErrorResponse.Build(SkillErrorCode.Internal, error);

        private static string ErrorJson(SkillErrorCode code, string error, string skill = null, string retryStrategy = null, object details = null) =>
            SkillErrorResponse.Build(code, error, skill: skill, details: details, retryStrategy: retryStrategy);

        public static void Initialize()
        {
            if (_initialized) return;
            lock (_initLock)
            {
                if (_initialized) return;

                var skills = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);
                var trackedSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var allTypes = SkillsCommon.GetAllLoadedTypes();

                foreach (var type in allTypes)
                {
                    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        UnitySkillAttribute attr;
                        try { attr = method.GetCustomAttribute<UnitySkillAttribute>(); }
                        catch { continue; }
                        if (attr != null)
                        {
                            var name = attr.Name ?? ToSnakeCase(method.Name);
                            var parameters = method.GetParameters();
                            var parameterNames = parameters.Select(p => p.Name).ToArray();
                            var allowedSet = new HashSet<string>(parameterNames, StringComparer.OrdinalIgnoreCase);
                            allowedSet.UnionWith(_reservedBodyParameters);
                            skills[name] = new SkillInfo
                            {
                                Name = name,
                                Description = attr.Description ?? "",
                                Method = method,
                                Parameters = parameters,
                                TracksWorkflow = attr.TracksWorkflow,
                                Category = attr.Category,
                                Operation = attr.Operation,
                                Tags = attr.Tags,
                                Outputs = attr.Outputs,
                                RequiresInput = attr.RequiresInput,
                                ReadOnly = attr.ReadOnly,
                                MutatesScene = attr.MutatesScene,
                                MutatesAssets = attr.MutatesAssets,
                                MayTriggerReload = attr.MayTriggerReload,
                                MayEnterPlayMode = attr.MayEnterPlayMode,
                                SupportsDryRun = attr.SupportsDryRun,
                                RiskLevel = attr.RiskLevel ?? "low",
                                RequiresPackages = attr.RequiresPackages,
                                ParameterNames = parameterNames,
                                AllowedParameterSet = allowedSet,
                                NameLower = name.ToLowerInvariant(),
                                DescriptionLower = (attr.Description ?? "").ToLowerInvariant(),
                                TagsLower = attr.Tags?.Select(t => t.ToLowerInvariant()).ToArray()
                            };
                            if (attr.TracksWorkflow)
                                trackedSkills.Add(name);
                        }
                    }
                }

                _skills = skills; // Atomic assignment of fully-built dictionary
                _workflowTrackedSkills = trackedSkills;

                // Build reverse index: output field → producing skills
                var outputIdx = new Dictionary<string, List<SkillInfo>>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in skills.Values)
                {
                    if (s.Outputs == null) continue;
                    foreach (var output in s.Outputs)
                    {
                        if (!outputIdx.TryGetValue(output, out var list))
                        {
                            list = new List<SkillInfo>();
                            outputIdx[output] = list;
                        }
                        list.Add(s);
                    }
                }
                _outputIndex = outputIdx;

                _initialized = true;
                SkillsLogger.Log($"Discovered {_skills.Count} skills");
            }
        }

        public static string GetManifest()
        {
            Initialize();
            var cached = _cachedManifest;
            if (cached != null) return cached;

            lock (_initLock)
            {
                if (_cachedManifest != null) return _cachedManifest;

                var manifest = BuildManifest(_skills.Values, filtered: false, filters: null, manifestType: "manifest");
                _cachedManifest = JsonConvert.SerializeObject(manifest, _jsonSettings);
                return _cachedManifest;
            }
        }

        public static string GetSchema()
        {
            Initialize();
            var cached = _cachedSchema;
            if (cached != null) return cached;

            lock (_initLock)
            {
                if (_cachedSchema != null) return _cachedSchema;

                var schema = BuildManifest(_skills.Values, filtered: false, filters: null, manifestType: "schema");
                _cachedSchema = JsonConvert.SerializeObject(schema, _jsonSettings);
                return _cachedSchema;
            }
        }

        /// <summary>Returns true if a skill with the given name is registered.</summary>
        public static bool HasSkill(string name)
        {
            Initialize();
            return !string.IsNullOrEmpty(name) && _skills.ContainsKey(name);
        }

        public static string Execute(string name, string json)
        {
            Initialize();
            if (!_skills.TryGetValue(name, out var skill))
            {
                return ResolveSkillNotFound(name);
            }

            bool autoStartedWorkflow = false;
            var wrapWithUndoTransaction = !skill.ReadOnly && !_transactionlessSkills.Contains(name);
            int undoGroup = -1;
            try
            {
                var validation = ValidateParameters(skill, json);
                if (validation.UnknownParams.Count > 0)
                {
                    var fixes = BuildUnknownParamFixes(name, validation.UnknownParams);
                    return SkillErrorResponse.Build(
                        SkillErrorCode.UnknownParam,
                        $"Unknown parameters: {string.Join(", ", ExtractValidationParameterNames(validation.UnknownParams))}",
                        skill: name,
                        details: new { unknownParams = validation.UnknownParams.ToArray(), allowedParams = skill.ParameterNames },
                        suggestedFixes: fixes,
                        retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                }

                if (validation.MissingParams.Count > 0)
                {
                    return SkillErrorResponse.Build(
                        SkillErrorCode.MissingParam,
                        $"Missing required parameter: {validation.MissingParams[0]}",
                        skill: name,
                        details: new { missingParams = validation.MissingParams.ToArray(), allowedParams = skill.ParameterNames },
                        retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                }

                if (validation.TypeErrors.Count > 0)
                {
                    var firstTypeError = validation.TypeErrors[0];
                    var message = SkillResultHelper.TryGetMemberValue(firstTypeError, "error", out var errorValue) && errorValue != null
                        ? errorValue.ToString()
                        : "Parameter type mismatch";
                    return SkillErrorResponse.Build(
                        SkillErrorCode.TypeMismatch,
                        message,
                        skill: name,
                        details: new { typeErrors = validation.TypeErrors.ToArray() },
                        retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                }

                if (validation.SemanticErrors.Count > 0)
                {
                    return SkillErrorResponse.Build(
                        SkillErrorCode.SemanticInvalid,
                        ExtractValidationMessage(validation.SemanticErrors[0], "Semantic validation failed"),
                        skill: name,
                        details: new
                        {
                            semanticErrors = validation.SemanticErrors.ToArray(),
                            warnings = validation.Warnings.Count > 0 ? validation.Warnings.ToArray() : null
                        },
                        retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                }

                // Confirmation gate: high-risk skills require an explicit one-shot token
                // when ConfirmationTokenService.RequireConfirmation is enabled.
                // Disabled by default — flip in Window > UnitySkills > Server > Settings.
                if (ConfirmationTokenService.RequireConfirmation && ConfirmationTokenService.IsHighRisk(skill))
                {
                    var gateResult = ApplyConfirmationGate(skill, name, json, validation);
                    if (gateResult != null)
                        return gateResult;
                }

                var args = validation.Args;
                var invoke = validation.InvokeArgs;

                if (wrapWithUndoTransaction)
                {
                    UnityEditor.Undo.IncrementCurrentGroup();
                    UnityEditor.Undo.SetCurrentGroupName($"Skill: {name}");
                    undoGroup = UnityEditor.Undo.GetCurrentGroup();
                }

                // ========== AUTO WORKFLOW RECORDING ==========
                if (skill.TracksWorkflow && !WorkflowManager.IsRecording)
                {
                    var desc = $"{name} - {(json?.Length > 80 ? json.Substring(0, 80) + "..." : json ?? "")}";
                    WorkflowManager.BeginTask(name, desc);
                    autoStartedWorkflow = true;
                }

                // Auto-snapshot target objects BEFORE skill execution for rollback support
                if (WorkflowManager.IsRecording)
                {
                    TrySnapshotTargetsFromArgs(args);
                }
                // ==============================================

                // Verbose control
                bool verbose = true; // Default to true if not specified to maintain backward compatibility for direct calls
                if (args.TryGetValue("verbose", StringComparison.OrdinalIgnoreCase, out var verboseToken))
                {
                    verbose = verboseToken.ToObject<bool>();
                    args.Remove("verbose");
                }

                var result = skill.Method.Invoke(null, invoke);

                if (!skill.ReadOnly)
                    UnityEditor.Undo.FlushUndoRecordObjects();

                // ========== AUTO WORKFLOW END ==========
                if (autoStartedWorkflow)
                {
                    WorkflowManager.EndTask();
                    WorkflowManager.SaveHistory();
                }
                else if (WorkflowManager.IsRecording)
                {
                    WorkflowManager.SaveHistory();
                }
                // ========================================

                if (wrapWithUndoTransaction)
                {
                    // Commit transaction
                    UnityEditor.Undo.CollapseUndoOperations(undoGroup);

                    // REST-invoked skills do not run through the usual menu/mouse event
                    // boundaries that advance Unity's undo stack. Move to the next group
                    // explicitly so editor_undo/editor_redo target the completed mutation.
                    if (!skill.ReadOnly)
                        UnityEditor.Undo.IncrementCurrentGroup();
                }

                // Return a normalized error payload when a skill reports a logical failure.
                if (SkillResultHelper.TryGetError(result, out string errorText))
                {
                    return SkillErrorResponse.Build(
                        SkillErrorCode.SkillError,
                        errorText,
                        skill: name,
                        retryStrategy: SkillErrorResponse.Abort);
                }

                if (!verbose && result != null)
                {
                    // "Summary Mode" Logic
                    // 1. Convert result to JToken to inspect it
                    var jsonResult = JToken.FromObject(result);

                    // 2. Check if it's a large Array (> 10 items)
                    if (jsonResult is JArray arr && arr.Count > 10)
                    {
                        var truncatedItems = new JArray();
                        for (int i = 0; i < 5; i++) truncatedItems.Add(arr[i]);

                        // Return a wrapper object instead of the list
                        // This keeps 'items' clean (same type) while providing meta info
                        var wrapper = new JObject
                        {
                            ["isTruncated"] = true,
                            ["totalCount"] = arr.Count,
                            ["showing"] = 5,
                            ["items"] = truncatedItems,
                            ["hint"] = "Result is truncated. To see all items, pass 'verbose=true' parameter."
                        };

                        return SerializeSuccessResponse(wrapper);
                    }
                }

                // Full Mode (verbose=true OR small result) - Return original result as is
                return SerializeSuccessResponse(result);
            }
            catch (TargetInvocationException ex)
            {
                // Clean up auto-started workflow on error
                if (autoStartedWorkflow && WorkflowManager.IsRecording)
                    WorkflowManager.EndTask();

                if (undoGroup >= 0)
                {
                    // Revert transaction
                    UnityEditor.Undo.RevertAllInCurrentGroup();
                }

                var inner = ex.InnerException ?? ex;
                return SkillErrorResponse.Build(
                    SkillErrorCode.Internal,
                    $"[Transactional Revert] {inner.Message}",
                    skill: name,
                    details: new { exceptionType = inner.GetType().Name },
                    retryStrategy: SkillErrorResponse.RetryWaitAndRetry);
            }
            catch (Exception ex)
            {
                // Clean up auto-started workflow on error
                if (autoStartedWorkflow && WorkflowManager.IsRecording)
                    WorkflowManager.EndTask();

                if (undoGroup >= 0)
                {
                    // Revert transaction
                    UnityEditor.Undo.RevertAllInCurrentGroup();
                }

                return SkillErrorResponse.Build(
                    SkillErrorCode.Internal,
                    $"[Transactional Revert] {ex.Message}",
                    skill: name,
                    details: new { exceptionType = ex.GetType().Name },
                    retryStrategy: SkillErrorResponse.RetryWaitAndRetry);
            }
        }

        public static string DryRun(string name, string json)
        {
            Initialize();
            if (!_skills.TryGetValue(name, out var skill))
                return ResolveSkillNotFound(name);

            try
            {
                var validation = ValidateParameters(skill, json);
                var planData = SkillPlanningService.BuildPlanData(skill, validation);
                return JsonConvert.SerializeObject(new
                {
                    status = "dryRun",
                    valid = validation.Valid,
                    skill = new
                    {
                        name = skill.Name,
                        description = skill.Description,
                        category = skill.Category != SkillCategory.Uncategorized ? skill.Category.ToString() : null,
                        operation = FormatOperation(skill.Operation),
                        tags = skill.Tags,
                        outputs = skill.Outputs,
                        requiresInput = skill.RequiresInput,
                        readOnly = skill.ReadOnly,
                        tracksWorkflow = skill.TracksWorkflow,
                        mutatesScene = skill.MutatesScene,
                        mutatesAssets = skill.MutatesAssets,
                        mayTriggerReload = skill.MayTriggerReload,
                        mayEnterPlayMode = skill.MayEnterPlayMode,
                        supportsDryRun = skill.SupportsDryRun,
                        riskLevel = skill.RiskLevel,
                        requiresPackages = skill.RequiresPackages
                    },
                    parameters = validation.ParameterDetails,
                    validation = new
                    {
                        missingParams = validation.MissingParams.Count > 0 ? validation.MissingParams.ToArray() : null,
                        unknownParams = validation.UnknownParams.Count > 0 ? validation.UnknownParams.ToArray() : null,
                        typeErrors = validation.TypeErrors.Count > 0 ? validation.TypeErrors.ToArray() : null,
                        semanticErrors = validation.SemanticErrors.Count > 0 ? validation.SemanticErrors.ToArray() : null,
                        warnings = validation.Warnings.Count > 0 ? validation.Warnings.ToArray() : null
                    },
                    impact = new
                    {
                        readOnly = skill.ReadOnly,
                        tracksWorkflow = skill.TracksWorkflow,
                        operation = FormatOperation(skill.Operation),
                        mutatesScene = skill.MutatesScene,
                        mutatesAssets = skill.MutatesAssets,
                        mayTriggerReload = skill.MayTriggerReload,
                        mayEnterPlayMode = skill.MayEnterPlayMode,
                        riskLevel = skill.RiskLevel
                    },
                    steps = planData?["steps"],
                    changes = planData?["changes"],
                    note = "No execution performed"
                }, _jsonSettings);
            }
            catch (Exception ex)
            {
                return SkillErrorResponse.Build(
                    SkillErrorCode.InvalidJson,
                    $"Invalid JSON: {ex.Message}",
                    skill: name,
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
            }
        }

        private static string SerializeSuccessResponse(object result)
        {
            if (ServerAvailabilityHelper.IsCompilationInProgress())
            {
                try
                {
                    var jsonResult = JToken.FromObject(result ?? new object());
                    if (jsonResult is JObject obj && !obj.ContainsKey("serverAvailability"))
                    {
                        var notice = ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                            "A skill execution may have triggered compilation or asset refresh.",
                            alwaysInclude: true);
                        if (notice != null)
                        {
                            obj["serverAvailability"] = JToken.FromObject(notice);
                            return JsonConvert.SerializeObject(new { status = "success", result = obj }, _jsonSettings);
                        }
                    }
                }
                catch { }
            }
            return JsonConvert.SerializeObject(new { status = "success", result }, _jsonSettings);
        }

        public static void Refresh()
        {
            lock (_initLock)
            {
                _initialized = false;
                _skills = null;
                _cachedManifest = null;
                _cachedSchema = null;
                _outputIndex = null;
                _workflowTrackedSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            Initialize();
        }

        private static string ToSnakeCase(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s, "([a-z])([A-Z])", "$1_$2").ToLower();

        private static string GetJsonType(Type t)
        {
            var underlying = Nullable.GetUnderlyingType(t) ?? t;
            if (underlying == typeof(string)) return "string";
            if (underlying == typeof(int) || underlying == typeof(long)) return "integer";
            if (underlying == typeof(float) || underlying == typeof(double)) return "number";
            if (underlying == typeof(bool)) return "boolean";
            if (underlying.IsArray) return "array";
            return "object";
        }

        /// <summary>
        /// A parameter is truly required only if it has no default value and cannot accept null
        /// (non-nullable value type). Reference types silently receive null when omitted.
        /// </summary>
        private static bool IsParameterRequired(ParameterInfo p)
        {
            if (p.HasDefaultValue) return false;
            return p.ParameterType.IsValueType && Nullable.GetUnderlyingType(p.ParameterType) == null;
        }

        private static string[] FormatOperation(SkillOperation op)
        {
            if (op == 0) return null;
            var list = new List<string>();
            foreach (SkillOperation flag in Enum.GetValues(typeof(SkillOperation)))
            {
                if (flag != 0 && op.HasFlag(flag))
                    list.Add(flag.ToString());
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        // ========== Filtered Manifest ==========

        /// <summary>
        /// Returns a filtered skills manifest based on query string parameters.
        /// Supported: category, operation, tags, readOnly, q (text search).
        /// </summary>
        public static string GetFilteredManifest(string queryString)
        {
            Initialize();
            var filters = ParseQueryString(queryString);
            if (filters.Count == 0) return GetManifest();

            IEnumerable<SkillInfo> filtered = _skills.Values;

            if (filters.TryGetValue("category", out var cat))
                filtered = filtered.Where(s => s.Category.ToString().Equals(cat, StringComparison.OrdinalIgnoreCase));

            if (filters.TryGetValue("operation", out var op))
                filtered = filtered.Where(s => s.Operation != 0 &&
                    Enum.TryParse<SkillOperation>(op, true, out var flag) && s.Operation.HasFlag(flag));

            if (filters.TryGetValue("tags", out var tag))
                filtered = filtered.Where(s => s.Tags != null &&
                    s.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)));

            if (filters.TryGetValue("readonly", out var ro))
                filtered = filtered.Where(s => s.ReadOnly == (ro.Equals("true", StringComparison.OrdinalIgnoreCase)));

            if (filters.TryGetValue("q", out var q))
            {
                var keywords = q.ToLowerInvariant().Split(new[] { ' ', '+' }, StringSplitOptions.RemoveEmptyEntries);
                filtered = filtered.Where(s => keywords.Any(kw =>
                    s.NameLower.Contains(kw) ||
                    s.DescriptionLower.Contains(kw) ||
                    (s.TagsLower != null && s.TagsLower.Any(t => t.Contains(kw)))));
            }

            var results = filtered.ToList();
            var manifest = BuildManifest(results, filtered: true, filters, manifestType: "manifest");
            return JsonConvert.SerializeObject(manifest, _jsonSettings);
        }

        private static object BuildManifest(IEnumerable<SkillInfo> skills, bool filtered, Dictionary<string, string> filters, string manifestType)
        {
            var skillArray = skills
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new
            {
                manifestType,
                schemaVersion = SkillSchemaVersion,
                version = SkillsLogger.Version,
                unityVersion = Application.unityVersion,
                totalSkills = skillArray.Length,
                filtered,
                filters,
                categories = Enum.GetNames(typeof(SkillCategory)).Where(c => c != "Uncategorized").ToArray(),
                operationTypes = Enum.GetNames(typeof(SkillOperation)),
                reservedBodyParameters = _reservedBodyParameters.OrderBy(x => x).ToArray(),
                workflowTrackedSkills = _workflowTrackedSkills.OrderBy(name => name).ToArray(),
                skills = skillArray.Select(s => new
                {
                    name = s.Name,
                    description = s.Description,
                    category = s.Category != SkillCategory.Uncategorized ? s.Category.ToString() : null,
                    operation = FormatOperation(s.Operation),
                    tags = s.Tags,
                    outputs = s.Outputs,
                    requiresInput = s.RequiresInput,
                    readOnly = s.ReadOnly,
                    tracksWorkflow = s.TracksWorkflow,
                    mutatesScene = s.MutatesScene,
                    mutatesAssets = s.MutatesAssets,
                    mayTriggerReload = s.MayTriggerReload,
                    mayEnterPlayMode = s.MayEnterPlayMode,
                    supportsDryRun = s.SupportsDryRun,
                    riskLevel = s.RiskLevel,
                    requiresPackages = s.RequiresPackages,
                    parameters = s.Parameters.Select(p => new
                    {
                        name = p.Name,
                        type = GetJsonType(p.ParameterType),
                        required = IsParameterRequired(p),
                        defaultValue = p.HasDefaultValue ? p.DefaultValue?.ToString() : null
                    })
                })
            };
        }

        // ========== Skill Recommendations ==========

        /// <summary>
        /// Intent-based skill recommendation. Scores skills by keyword matching against
        /// name (3pts), tags (2pts), and description (1pt). Returns top-N ranked results.
        /// </summary>
        public static string GetRecommendations(string queryString)
        {
            Initialize();
            var filters = ParseQueryString(queryString);
            var intent = "";
            int topN = 10;
            bool includeSchema = false;
            if (filters.TryGetValue("intent", out var i)) intent = i;
            if (filters.TryGetValue("topn", out var n) && int.TryParse(n, out var parsed)) topN = Mathf.Clamp(parsed, 1, 50);
            if (filters.TryGetValue("includeschema", out var inc))
                includeSchema = inc.Equals("true", StringComparison.OrdinalIgnoreCase) || inc == "1";

            if (string.IsNullOrWhiteSpace(intent))
            {
                return SkillErrorResponse.Build(
                    SkillErrorCode.MissingParam,
                    "Missing required parameter: intent",
                    details: new { example = "/skills/recommend?intent=create+cube&topN=10&includeSchema=true" },
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
            }

            var rawKeywords = intent.ToLowerInvariant().Split(new[] { ' ', '+', '_' }, StringSplitOptions.RemoveEmptyEntries);
            var keywords = ExpandIntent(rawKeywords);
            var scored = new List<(SkillInfo skill, int score, List<string> matchedOn)>();

            // Pre-compute operation and category matches (with Chinese substring support)
            var matchedOps = ExtractOperations(rawKeywords);
            var matchedCats = ExtractCategories(rawKeywords);

            foreach (var s in _skills.Values)
            {
                int score = 0;
                var matchedOn = new List<string>();
                var nameLower = s.NameLower;
                var descLower = s.DescriptionLower;

                foreach (var kw in keywords)
                {
                    if (nameLower.Contains(kw))
                    {
                        score += 3;
                        matchedOn.Add($"name:{kw}");
                    }
                    if (s.TagsLower != null && s.TagsLower.Any(t => t.Contains(kw)))
                    {
                        score += 2;
                        matchedOn.Add($"tag:{kw}");
                    }
                    if (descLower.Contains(kw))
                    {
                        score += 1;
                        matchedOn.Add($"desc:{kw}");
                    }
                }

                // Category bonus
                if (matchedCats.Count > 0 && s.Category != SkillCategory.Uncategorized && matchedCats.Contains(s.Category))
                {
                    score += 2;
                    matchedOn.Add($"category:{s.Category}");
                }

                // Operation bonus
                if (matchedOps.Count > 0 && s.Operation != 0)
                {
                    foreach (var op in matchedOps)
                    {
                        if (s.Operation.HasFlag(op))
                        {
                            score += 2;
                            matchedOn.Add($"operation:{op}");
                            break;
                        }
                    }
                }

                if (score > 0)
                    scored.Add((s, score, matchedOn));
            }

            var results = scored.OrderByDescending(x => x.score).Take(topN).ToList();
            var response = new
            {
                intent,
                expandedKeywords = keywords.Length > rawKeywords.Length ? keywords : null,
                topN,
                includeSchema,
                totalMatches = scored.Count,
                results = results.Select(x => new
                {
                    name = x.skill.Name,
                    description = x.skill.Description,
                    category = x.skill.Category != SkillCategory.Uncategorized ? x.skill.Category.ToString() : null,
                    score = x.score,
                    confidence = ScoreToConfidence(x.score),
                    matchedOn = x.matchedOn.Distinct().ToArray(),
                    schema = includeSchema ? BuildSkillSchemaForRecommend(x.skill) : null
                })
            };
            return JsonConvert.SerializeObject(response, _jsonSettings);
        }

        private static string ScoreToConfidence(int score)
        {
            if (score >= 10) return "high";
            if (score >= 5) return "medium";
            return "low";
        }

        private static object BuildSkillSchemaForRecommend(SkillInfo s) => new
        {
            parameters = s.Parameters.Select(p => new
            {
                name = p.Name,
                type = GetJsonType(p.ParameterType),
                required = IsParameterRequired(p),
                defaultValue = p.HasDefaultValue ? p.DefaultValue?.ToString() : null
            }).ToArray(),
            outputs = s.Outputs,
            requiresInput = s.RequiresInput,
            tags = s.Tags,
            operation = FormatOperation(s.Operation),
            riskLevel = s.RiskLevel,
            readOnly = s.ReadOnly,
            mutatesScene = s.MutatesScene,
            mutatesAssets = s.MutatesAssets,
            requiresPackages = s.RequiresPackages,
        };

        // ========== Skill Dependency Chain ==========

        /// <summary>
        /// Traces Outputs→RequiresInput relationships via BFS to build operation chains.
        /// Given a target output field, finds all skills that produce it and their dependencies.
        /// </summary>
        public static string GetSkillChain(string queryString)
        {
            Initialize();
            var filters = ParseQueryString(queryString);
            string targetOutput = "";
            int maxDepth = 3;
            if (filters.TryGetValue("output", out var o)) targetOutput = o;
            if (filters.TryGetValue("maxdepth", out var d) && int.TryParse(d, out var dp))
                maxDepth = Mathf.Clamp(dp, 1, 10);

            if (string.IsNullOrWhiteSpace(targetOutput))
            {
                return SkillErrorResponse.Build(
                    SkillErrorCode.MissingParam,
                    "Missing required parameter: output",
                    details: new { example = "/skills/chain?output=instanceId&maxDepth=3" },
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
            }

            // BFS: find skills producing the target, then trace their RequiresInput
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(string field, int depth)>();
            queue.Enqueue((targetOutput, 0));
            visited.Add(targetOutput);

            var producers = new List<object>();

            while (queue.Count > 0)
            {
                var (field, depth) = queue.Dequeue();

                if (!_outputIndex.TryGetValue(field, out var fieldProducers))
                    continue;

                foreach (var s in fieldProducers)
                {

                    producers.Add(new
                    {
                        skill = s.Name,
                        description = s.Description,
                        category = s.Category != SkillCategory.Uncategorized ? s.Category.ToString() : null,
                        depth,
                        producesField = field,
                        outputs = s.Outputs,
                        requiresInput = s.RequiresInput
                    });

                    // Enqueue RequiresInput fields for next depth level
                    if (depth < maxDepth && s.RequiresInput != null)
                    {
                        foreach (var req in s.RequiresInput)
                        {
                            if (!visited.Contains(req))
                            {
                                visited.Add(req);
                                queue.Enqueue((req, depth + 1));
                            }
                        }
                    }
                }
            }

            return JsonConvert.SerializeObject(new
            {
                targetOutput,
                maxDepth,
                totalProducers = producers.Count,
                producers
            }, _jsonSettings);
        }

        internal static string[] FormatOperationForPlanning(SkillOperation op)
        {
            return FormatOperation(op);
        }

        internal static string ResolveSkillNotFound(string name)
        {
            // Surface up to 5 nearest registered skill names so AI agents can self-correct typos.
            var nearest = _skills.Keys
                .Select(k => new { Name = k, Distance = ComputeLevenshteinDistance(name ?? string.Empty, k) })
                .Where(x => x.Distance <= 5 ||
                            (!string.IsNullOrEmpty(name) && k_ContainsCi(x.Name, name)))
                .OrderBy(x => x.Distance)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .Select(x => x.Name)
                .ToList();

            return SkillErrorResponse.SkillNotFound(name, nearest);
        }

        private static bool k_ContainsCi(string haystack, string needle) =>
            !string.IsNullOrEmpty(haystack) && !string.IsNullOrEmpty(needle) &&
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        internal static bool TryGetSkill(string name, out SkillInfo skill)
        {
            Initialize();
            return _skills.TryGetValue(name, out skill);
        }

        internal static SkillInfo[] GetAllSkillsSnapshot()
        {
            Initialize();
            return _skills.Values
                .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        internal static ParameterValidationResult ValidateParameters(SkillInfo skill, string json)
        {
            var validation = new ParameterValidationResult
            {
                Args = string.IsNullOrEmpty(json) ? new JObject() : JObject.Parse(json)
            };

            var ps = skill.Parameters;
            CollectUnknownParameters(skill, validation);
            var invoke = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                var p = ps[i];
                bool provided = validation.Args.TryGetValue(p.Name, StringComparison.OrdinalIgnoreCase, out var token);

                if (provided)
                {
                    try
                    {
                        invoke[i] = token.ToObject(p.ParameterType);
                    }
                    catch (Exception ex)
                    {
                        validation.TypeErrors.Add(new { parameter = p.Name, expectedType = GetJsonType(p.ParameterType), error = ex.Message });
                    }
                }
                else if (p.HasDefaultValue)
                {
                    invoke[i] = p.DefaultValue;
                }
                else if (!IsParameterRequired(p))
                {
                    invoke[i] = null;
                }
                else
                {
                    validation.MissingParams.Add(p.Name);
                }

                validation.ParameterDetails.Add(new
                {
                    name = p.Name,
                    type = GetJsonType(p.ParameterType),
                    required = IsParameterRequired(p),
                    provided,
                    defaultValue = p.HasDefaultValue ? p.DefaultValue?.ToString() : null
                });
            }

            validation.InvokeArgs = invoke;
            SkillPlanningService.ApplySemanticValidation(skill, validation);
            return validation;
        }

        private static void CollectUnknownParameters(SkillInfo skill, ParameterValidationResult validation)
        {
            if (validation?.Args == null)
                return;

            var allowed = skill.AllowedParameterSet;
            var parameterNames = skill.ParameterNames;

            foreach (var property in validation.Args.Properties())
            {
                if (allowed.Contains(property.Name))
                    continue;

                var suggestions = SuggestParameters(skill.Name, property.Name, parameterNames);
                var entry = new Dictionary<string, object>
                {
                    ["parameter"] = property.Name
                };

                if (suggestions.Length > 0)
                    entry["suggestions"] = suggestions;

                var hint = GetParameterHint(skill.Name, property.Name);
                if (!string.IsNullOrWhiteSpace(hint))
                    entry["hint"] = hint;

                validation.UnknownParams.Add(entry);
            }
        }

        /// <summary>
        /// Returns null when the skill is allowed to execute (token consumed); otherwise returns
        /// a serialized error payload (CONFIRMATION_REQUIRED or INVALID_TOKEN) the caller should
        /// surface back to the client unchanged.
        /// </summary>
        private static string ApplyConfirmationGate(
            SkillInfo skill,
            string name,
            string rawJson,
            ParameterValidationResult validation)
        {
            string token = null;
            if (validation.Args.TryGetValue("_confirm", StringComparison.OrdinalIgnoreCase, out var ct) && ct.Type != JTokenType.Null)
            {
                token = ct.ToString();
            }

            // argsHash excludes _confirm so the same args produce the same hash on both calls.
            var argsForHash = (JObject)validation.Args.DeepClone();
            argsForHash.Remove("_confirm");
            var argsForHashJson = argsForHash.ToString(Formatting.None);

            if (string.IsNullOrEmpty(token))
            {
                var (newToken, ttl) = ConfirmationTokenService.IssueToken(name, argsForHashJson);
                JObject dryRunPreview = null;
                try
                {
                    var dryRunJson = DryRun(name, rawJson);
                    if (!string.IsNullOrEmpty(dryRunJson))
                        dryRunPreview = JObject.Parse(dryRunJson);
                }
                catch
                {
                    // Dry-run is best-effort; if it fails the token is still valid.
                }

                return SkillErrorResponse.Build(
                    SkillErrorCode.ConfirmationRequired,
                    "This skill is high-risk and requires confirmation. Re-call with the same args plus '_confirm':'<token>' to execute.",
                    skill: name,
                    details: new
                    {
                        _confirm = newToken,
                        ttlSeconds = ttl,
                        why = $"riskLevel={skill.RiskLevel}, operation={string.Join("|", FormatOperation(skill.Operation) ?? new[] { "?" })}",
                        dryRun = dryRunPreview
                    },
                    retryStrategy: SkillErrorResponse.RetryConfirmAndRetry,
                    retryAfterSeconds: 0);
            }

            if (!ConfirmationTokenService.TryConsume(token, name, argsForHashJson))
            {
                return SkillErrorResponse.Build(
                    SkillErrorCode.InvalidToken,
                    "_confirm token is invalid, expired, or args differ from when the token was issued.",
                    skill: name,
                    details: new { suggestion = "Re-call without '_confirm' to receive a fresh token bound to your current args." },
                    retryStrategy: SkillErrorResponse.RetryConfirmAndRetry);
            }

            return null;
        }

        private static List<SuggestedFix> BuildUnknownParamFixes(string skillName, List<object> unknownParams)
        {
            var fixes = new List<SuggestedFix>();
            if (unknownParams == null || unknownParams.Count == 0)
                return fixes;

            foreach (var entry in unknownParams)
            {
                if (entry is not IDictionary<string, object> dict)
                    continue;

                string param = dict.TryGetValue("parameter", out var pv) ? pv?.ToString() : null;
                string hint = dict.TryGetValue("hint", out var hv) ? hv?.ToString() : null;

                if (dict.TryGetValue("suggestions", out var sObj) && sObj is IEnumerable<string> sugs)
                {
                    foreach (var s in sugs)
                    {
                        fixes.Add(new SuggestedFix
                        {
                            action = "fix_param",
                            skill = skillName,
                            args = new Dictionary<string, string> { [s] = "<value>" },
                            reason = !string.IsNullOrEmpty(hint)
                                ? $"Did you mean '{s}'? {hint}"
                                : (!string.IsNullOrEmpty(param)
                                    ? $"Replace unknown parameter '{param}' with '{s}'"
                                    : $"Use '{s}'")
                        });
                    }
                }
                else if (!string.IsNullOrEmpty(hint))
                {
                    fixes.Add(new SuggestedFix
                    {
                        action = "fix_param",
                        skill = skillName,
                        reason = hint
                    });
                }
            }
            return fixes.Count > 0 ? fixes : null;
        }

        private static string[] SuggestParameters(string skillName, string unknownParameter, string[] allowedParameterNames)
        {
            if (_commonParameterSuggestions.TryGetValue(skillName, out var skillSuggestions) &&
                skillSuggestions.TryGetValue(unknownParameter, out var directSuggestions) &&
                directSuggestions?.Length > 0)
            {
                return directSuggestions;
            }

            return allowedParameterNames
                .Select(name => new
                {
                    Name = name,
                    Distance = ComputeLevenshteinDistance(unknownParameter, name)
                })
                .Where(x =>
                    x.Distance <= 3 ||
                    x.Name.IndexOf(unknownParameter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    unknownParameter.IndexOf(x.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(x => x.Distance)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .Select(x => x.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string GetParameterHint(string skillName, string parameterName)
        {
            if (_commonParameterHints.TryGetValue(skillName, out var hints) &&
                hints.TryGetValue(parameterName, out var hint))
            {
                return hint;
            }

            return null;
        }

        private static int ComputeLevenshteinDistance(string left, string right)
        {
            if (string.IsNullOrEmpty(left))
                return string.IsNullOrEmpty(right) ? 0 : right.Length;
            if (string.IsNullOrEmpty(right))
                return left.Length;

            var matrix = new int[left.Length + 1, right.Length + 1];
            for (int i = 0; i <= left.Length; i++)
                matrix[i, 0] = i;
            for (int j = 0; j <= right.Length; j++)
                matrix[0, j] = j;

            for (int i = 1; i <= left.Length; i++)
            {
                for (int j = 1; j <= right.Length; j++)
                {
                    int cost = char.ToUpperInvariant(left[i - 1]) == char.ToUpperInvariant(right[j - 1]) ? 0 : 1;
                    matrix[i, j] = Math.Min(
                        Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                        matrix[i - 1, j - 1] + cost);
                }
            }

            return matrix[left.Length, right.Length];
        }

        private static string[] ExtractValidationParameterNames(IEnumerable<object> validationEntries)
        {
            if (validationEntries == null)
                return Array.Empty<string>();

            return validationEntries
                .Select(entry => TryGetValidationEntryField(entry, "parameter"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string ExtractValidationMessage(object validationEntry, string fallback)
        {
            return SkillResultHelper.TryGetMemberValue(validationEntry, "error", out var errorValue) && errorValue != null
                ? errorValue.ToString()
                : fallback;
        }

        private static string TryGetValidationEntryField(object validationEntry, string fieldName)
        {
            return SkillResultHelper.TryGetMemberValue(validationEntry, fieldName, out var value) && value != null
                ? value.ToString()
                : null;
        }

        public static string Plan(string name, string json)
        {
            Initialize();
            if (!_skills.TryGetValue(name, out var skill))
                return ResolveSkillNotFound(name);

            try
            {
                var validation = ValidateParameters(skill, json);
                var plan = SkillPlanningService.BuildPlan(skill, validation);
                return JsonConvert.SerializeObject(plan, _jsonSettings);
            }
            catch (Exception ex)
            {
                return SkillErrorResponse.Build(
                    SkillErrorCode.InvalidJson,
                    $"Invalid JSON: {ex.Message}",
                    skill: name,
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
            }
        }



        /// <summary>
        /// Validates metadata completeness and consistency across all discovered skills.
        /// Returns a list of diagnostic messages (WARN/ERROR prefix).
        /// </summary>
        public static List<string> ValidateMetadata()
        {
            Initialize();
            var issues = new List<string>();

            foreach (var s in _skills.Values)
            {
                if (s.Category == SkillCategory.Uncategorized)
                    issues.Add($"[WARN] {s.Name}: Category is Uncategorized");

                if (s.Operation == 0)
                    issues.Add($"[WARN] {s.Name}: Operation not specified");

                if (s.ReadOnly && s.TracksWorkflow)
                    issues.Add($"[ERROR] {s.Name}: ReadOnly=true conflicts with TracksWorkflow=true");

                if (s.Tags == null || s.Tags.Length == 0)
                    issues.Add($"[WARN] {s.Name}: Tags is empty");

                if (s.Outputs == null || s.Outputs.Length == 0)
                    issues.Add($"[WARN] {s.Name}: Outputs is empty");

                if (s.Operation.HasFlag(SkillOperation.Delete) || s.Operation.HasFlag(SkillOperation.Modify))
                {
                    if (s.RequiresInput == null || s.RequiresInput.Length == 0)
                        issues.Add($"[WARN] {s.Name}: Delete/Modify operation but RequiresInput is empty");
                }

                if (s.MayEnterPlayMode && s.ReadOnly)
                    issues.Add($"[WARN] {s.Name}: MayEnterPlayMode=true but ReadOnly=true seems inconsistent");

                if (!s.SupportsDryRun && s.ReadOnly)
                    issues.Add($"[WARN] {s.Name}: SupportsDryRun=false but ReadOnly=true — read-only skills should support dry run");
            }

            return issues;
        }

        // ========== Query String Parser ==========

        internal static Dictionary<string, string> ParseQueryString(string qs)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(qs)) return result;

            // Remove leading '?'
            var raw = qs.StartsWith("?") ? qs.Substring(1) : qs;
            if (string.IsNullOrEmpty(raw)) return result;

            foreach (var pair in raw.Split('&'))
            {
                var eqIdx = pair.IndexOf('=');
                if (eqIdx <= 0) continue;
                var key = Uri.UnescapeDataString(pair.Substring(0, eqIdx)).Trim();
                var val = Uri.UnescapeDataString(pair.Substring(eqIdx + 1)).Trim();
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(val))
                    result[key] = val;
            }
            return result;
        }

        /// <summary>
        /// Auto-snapshot target objects from skill arguments for universal rollback support.
        /// Identifies common target parameters (name, instanceId, path, materialPath, etc.) and snapshots them.
        /// </summary>
        private static void TrySnapshotTargetsFromArgs(JObject args)
        {
            try
            {
                // Try to find target GameObject by common parameter names
                string targetName = null;
                int targetInstanceId = 0;
                string targetPath = null;

                if (args.TryGetValue("name", StringComparison.OrdinalIgnoreCase, out var nameToken))
                    targetName = nameToken.ToString();
                if (args.TryGetValue("instanceId", StringComparison.OrdinalIgnoreCase, out var idToken))
                    targetInstanceId = idToken.ToObject<int>();
                if (args.TryGetValue("path", StringComparison.OrdinalIgnoreCase, out var pathToken))
                    targetPath = pathToken.ToString();

                // Snapshot GameObject if identifiable
                if (!string.IsNullOrEmpty(targetName) || targetInstanceId != 0 || !string.IsNullOrEmpty(targetPath))
                {
                    var (go, _) = GameObjectFinder.FindOrError(targetName, targetInstanceId, targetPath);
                    if (go != null)
                    {
                        WorkflowManager.SnapshotObject(go);
                        // Also snapshot Transform which is commonly modified
                        WorkflowManager.SnapshotObject(go.transform);
                        // Snapshot Renderer's material if present
                        var renderer = go.GetComponent<UnityEngine.Renderer>();
                        if (renderer != null && renderer.sharedMaterial != null)
                            WorkflowManager.SnapshotObject(renderer.sharedMaterial);
                    }
                }

                // Snapshot Material asset if materialPath is provided
                if (args.TryGetValue("materialPath", StringComparison.OrdinalIgnoreCase, out var matPathToken))
                {
                    var matPath = matPathToken.ToString();
                    if (!string.IsNullOrEmpty(matPath))
                    {
                        var mat = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(matPath);
                        if (mat != null)
                            WorkflowManager.SnapshotObject(mat);
                    }
                }

                // Snapshot asset if assetPath is provided
                if (args.TryGetValue("assetPath", StringComparison.OrdinalIgnoreCase, out var assetPathToken))
                {
                    var assetPath = assetPathToken.ToString();
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                        if (asset != null)
                            WorkflowManager.SnapshotObject(asset);
                    }
                }

                // Handle child/parent operations
                if (args.TryGetValue("childName", StringComparison.OrdinalIgnoreCase, out var childNameToken))
                {
                    var (childGo, _) = GameObjectFinder.FindOrError(childNameToken.ToString(), 0, null);
                    if (childGo != null)
                        WorkflowManager.SnapshotObject(childGo.transform);
                }

                // Handle batch items - snapshot each target in the batch
                if (args.TryGetValue("items", StringComparison.OrdinalIgnoreCase, out var itemsToken))
                {
                    try
                    {
                        var items = itemsToken.ToObject<List<Dictionary<string, object>>>();
                        if (items != null)
                        {
                            foreach (var item in items.Take(50)) // Limit to avoid performance issues
                            {
                                string itemName = item.ContainsKey("name") ? item["name"]?.ToString() : null;
                                int itemId = item.ContainsKey("instanceId") ? Convert.ToInt32(item["instanceId"]) : 0;
                                string itemPath = item.ContainsKey("path") ? item["path"]?.ToString() : null;

                                if (!string.IsNullOrEmpty(itemName) || itemId != 0 || !string.IsNullOrEmpty(itemPath))
                                {
                                    var (itemGo, _) = GameObjectFinder.FindOrError(itemName, itemId, itemPath);
                                    if (itemGo != null)
                                    {
                                        WorkflowManager.SnapshotObject(itemGo);
                                        WorkflowManager.SnapshotObject(itemGo.transform);
                                    }
                                }
                            }
                        }
                    }
                    catch { /* Ignore batch parsing errors */ }
                }
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Workflow snapshot failed: {ex.Message}");
            }
        }
    }

    internal static class SkillResultHelper
    {
        public static bool TryGetError(object result, out string errorText)
        {
            errorText = null;
            if (result == null)
                return false;

            if (!TryGetMemberValue(result, "error", out object errorValue) || errorValue == null)
                return false;

            if (TryGetMemberValue(result, "success", out object successValue) && successValue is bool successBool && successBool)
                return false;

            errorText = errorValue.ToString();
            return !string.IsNullOrWhiteSpace(errorText);
        }

        public static bool TryGetMemberValue(object result, string memberName, out object value)
        {
            value = null;
            if (result == null || string.IsNullOrEmpty(memberName))
                return false;

            if (result is JObject jsonObject &&
                jsonObject.TryGetValue(memberName, StringComparison.OrdinalIgnoreCase, out JToken token))
            {
                value = token.Type == JTokenType.Null ? null : token.ToObject<object>();
                return true;
            }

            if (result is IDictionary<string, object> dictionary)
            {
                foreach (var pair in dictionary)
                {
                    if (string.Equals(pair.Key, memberName, StringComparison.OrdinalIgnoreCase))
                    {
                        value = pair.Value;
                        return true;
                    }
                }
            }

            var resultType = result.GetType();
            var property = resultType.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property != null)
            {
                value = property.GetValue(result);
                return true;
            }

            var field = resultType.GetField(memberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (field != null)
            {
                value = field.GetValue(result);
                return true;
            }

            return false;
        }
    }
}
