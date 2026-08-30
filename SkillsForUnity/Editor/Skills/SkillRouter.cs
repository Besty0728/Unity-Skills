using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Routes REST API requests to each skill method.
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
            // True means the skill captures its own workflow snapshot; skips the generic
            // pre-execution snapshot in TrySnapshotTargetsFromArgs, to avoid backing up twice.
            public bool SkipAutoPresnapshot;
            // Intent-layer metadata
            public SkillCategory Category;
            public SkillOperation Operation;
            public string[] Tags;
            public string[] Outputs;
            public string[] RequiresInput;
            public bool ReadOnly;
            // Risk and impact metadata
            public bool MutatesScene;
            public bool MutatesAssets;
            public bool MayTriggerReload;
            public bool MayEnterPlayMode;
            public bool SupportsDryRun;
            // True means this skill blocks the main thread for seconds or more; the agent should
            // prefer the async job path when one exists. See UnitySkillAttribute.LongRunning.
            public bool LongRunning;
            public string RiskLevel;
            public string[] RequiresPackages;
            // Permission tier. Defaults to FullAuto, so an unannotated skill goes through the
            // Approval gate; SemiAuto only takes effect when explicitly declared via [UnitySkill(Mode=...)].
            public SkillMode Mode;
            // Cached to avoid re-allocating on every Execute/DryRun
            public string[] ParameterNames;
            public HashSet<string> AllowedParameterSet;
            // Precomputed lowercase form, for filtering/search (skips ToLowerInvariant on every query)
            public string NameLower;
            public string DescriptionLower;
            public string[] TagsLower;
        }

        private static volatile Dictionary<string, SkillInfo> _skills;
        private static volatile bool _initialized;
        // One-time subscription to SkillsSurfaceProfile.OnChanged, wired up in Initialize().
        private static bool _surfaceHookInstalled;

        // Dirty marker for the manually-recorded session (workflow_begin_task): the (taskId, snapshotCount) recorded at the last SaveHistory. Lets a tracked skill skip a redundant save
        // when there are no new snapshots since the last save.
        private static string _lastSavedTaskId;
        private static int _lastSavedSnapshotCount = -1;
        // These four all have to be volatile: they form the read side of the GET fast-path double-
        // checked lock -- the HTTP thread reads them outside _initLock (TryGetCachedGetResponse)
        // while the main thread publishes inside the lock. Without volatile, the read side could hold a stale copy hoisted out of a loop after a profile switch invalidated it.
        private static volatile string _cachedManifest;
        private static volatile string _cachedSchema;
        // Bare GET /skills (catalog layer) and GET /skills/meta (session constants). Like the two
        // above, both are whole-payload singletons rather than query-keyed entries, so the HTTP thread's fast path can return them directly without consulting _filteredOutputCache.
        private static volatile string _cachedBrief;
        private static volatile string _cachedMeta;
        private static Dictionary<string, List<SkillInfo>> _outputIndex;

        // Cache of filtered (scoped) schema/manifest output, keyed by the canonical form of the
        // query string. The full schema/manifest already has a cache (_cachedSchema/_cachedManifest),
        // but the filtered variants (?category=... etc.) used to be rebuilt and re-serialized on
        // every request -- and that's exactly the path an agent uses to save tokens (scoped is
        // roughly 24KB, full roughly 618KB). As long as the skill set doesn't change, a given query's
        // content is byte-for-byte deterministic, so caching is safe; cleared on Refresh() (domain
        // reload / skill add-remove). Only recognized filter keys enter the cache key (see StripUnrecognizedFilterKeys), so an unbounded query parameter (e.g. a cache-busting ?nonce=N) can't manufacture a fresh several-hundred-KB entry per request; entry count is also hard-capped by MaxCacheEntries as a second line of defense.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _filteredOutputCache =
            new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

        // Shared hard cap between _filteredOutputCache and _etagCache. Both are read by the HTTP
        // thread and written by the main thread; the capacity check plus Clear() needs no extra lock
        // (ConcurrentDictionary.Clear() is itself thread-safe), keeping eviction as simple as "reset
        // the whole cache" -- real callers only cycle through a small, closed set of category/tag/summary combinations, so this only guards against pathological query variation.
        private const int MaxCacheEntries = 256;

        /// <summary>Number of registered skills. Avoids parsing the manifest just to get a count.</summary>
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
            "offset",
            "limit",
            "pageOffset",
            "pageLimit",
            "_confirm"
        };

        private const string EntityIdParameterName = "entityId";

        private const string PrefKeySummaryAutoTruncate = "UnitySkills_SummaryAutoTruncate";
        private const string PrefKeySummaryPageSize = "UnitySkills_SummaryPageSize";
        public const int DefaultSummaryPageSize = 10;
        private static bool? _summaryAutoTruncate;
        private static int? _summaryPageSize;

        /// <summary>
        /// Raised after either summary preference is changed through this class. EditorPrefs and
        /// all subscribers are expected to run on Unity's main thread; HTTP worker threads only
        /// enqueue requests and never access these properties directly.
        /// </summary>
        public static event Action SummarySettingsChanged;

        /// <summary>
        /// Toggle for automatic truncation in Summary mode. The first read performs the one-shot
        /// upgrade default: existing installations retain the historic disabled behavior, while
        /// fresh installations opt into truncation. Once read, the value is persisted and no
        /// future package update can silently change a user's choice.
        /// </summary>
        public static bool SummaryAutoTruncate
        {
            get
            {
                if (!_summaryAutoTruncate.HasValue)
                {
                    if (EditorPrefs.HasKey(PrefKeySummaryAutoTruncate))
                        _summaryAutoTruncate = EditorPrefs.GetBool(PrefKeySummaryAutoTruncate, false);
                    else
                    {
                        // Keep this list in lockstep with PermissionUiHelpers.IsExistingInstall
                        // and SkillsModeManager.IsExistingInstall. The internal helper also lets
                        // EditMode tests simulate an upgrade without creating machine prefs.
                        _summaryAutoTruncate = !SkillsModeManager.IsExistingInstallForDefaults();
                        EditorPrefs.SetBool(PrefKeySummaryAutoTruncate, _summaryAutoTruncate.Value);
                    }
                }
                return _summaryAutoTruncate.Value;
            }
            set
            {
                bool changed = !_summaryAutoTruncate.HasValue || _summaryAutoTruncate.Value != value;
                _summaryAutoTruncate = value;
                EditorPrefs.SetBool(PrefKeySummaryAutoTruncate, value);
                if (changed) RaiseSummarySettingsChanged();
            }
        }

        /// <summary>
        /// Number of items returned for an automatic Summary page. Explicit pageLimit arguments
        /// continue to override this value, and explicit paging remains available even when
        /// automatic truncation is disabled. Values below one are treated as a malformed pref and
        /// read as the safe default; the malformed value is left untouched for rollback safety.
        /// </summary>
        public static int SummaryPageSize
        {
            get
            {
                if (!_summaryPageSize.HasValue)
                {
                    if (!EditorPrefs.HasKey(PrefKeySummaryPageSize))
                    {
                        _summaryPageSize = DefaultSummaryPageSize;
                        EditorPrefs.SetInt(PrefKeySummaryPageSize, DefaultSummaryPageSize);
                    }
                    else
                    {
                        int stored = EditorPrefs.GetInt(PrefKeySummaryPageSize, DefaultSummaryPageSize);
                        _summaryPageSize = stored > 0 ? stored : DefaultSummaryPageSize;
                    }
                }
                return _summaryPageSize.Value;
            }
            set
            {
                int normalized = value > 0 ? value : DefaultSummaryPageSize;
                bool changed = !_summaryPageSize.HasValue || _summaryPageSize.Value != normalized;
                _summaryPageSize = normalized;
                EditorPrefs.SetInt(PrefKeySummaryPageSize, normalized);
                if (changed) RaiseSummarySettingsChanged();
            }
        }

        /// <summary>Test-only cache reset. Preference values themselves are intentionally preserved.</summary>
        internal static void ResetSummaryPreferencesForTests()
        {
            _summaryAutoTruncate = null;
            _summaryPageSize = null;
        }

        private static void RaiseSummarySettingsChanged()
        {
            var handlers = SummarySettingsChanged;
            if (handlers == null) return;
            foreach (var handler in handlers.GetInvocationList())
            {
                try { ((Action)handler)?.Invoke(); }
                catch (Exception ex)
                {
                    SkillsLogger.LogWarning(
                        $"SummarySettingsChanged handler '{handler.Method?.DeclaringType?.Name}.{handler.Method?.Name}' threw: {ex.Message}");
                }
            }
        }

        private static readonly string[] _entityIdPathFallbackParameters =
        {
            "path",
            "targetPath",
            "cameraPath",
            "vcamPath",
            "sequencerPath"
        };

        private static readonly string[] _entityIdNameFallbackParameters =
        {
            "name",
            "target",
            "targetName",
            "cameraName",
            "vcamName",
            "sequencerName",
            "objectName",
            "gameObjectName"
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
                ["targetName"] = "camera_look_at only accepts world coordinates x/y/z; object names are not supported."
            },
            ["timeline_list_tracks"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["path"] = "The path of timeline_list_tracks is a scene hierarchy path, not an Assets resource path."
            }
        };

        // ========== Intent synonym map ==========

        private static readonly Dictionary<string, string[]> _synonymMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // Chinese -> English
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
        /// Maps keywords onto the dictionary via exact match plus substring match (substring matching is there for unsegmented Chinese).
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
        // Reuses the JSON settings from SkillsCommon (single definition, no duplication)
        private static readonly JsonSerializerSettings _jsonSettings = SkillsCommon.JsonSettings;

        // Only for the ?wire=v2 payload. Dropping nulls is what actually makes v2's omission semantics ("riskLevel absent means low") save bytes;
        // every v1 path still uses _jsonSettings, to keep output byte-for-byte identical to before v2 existed.
        private static readonly JsonSerializerSettings _jsonSettingsV2 = SkillsCommon.JsonSettingsOmitNull;

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

                // Installed here rather than in a static constructor: every path that can produce a
                // cached output string first goes through Initialize(), so by the time a cache exists whose validity depends on the profile, this hook is guaranteed to already be listening.
                // Reset along with the other static fields on a domain reload.
                if (!_surfaceHookInstalled)
                {
                    SkillsSurfaceProfile.OnChanged += InvalidateOutputCaches;
                    _surfaceHookInstalled = true;
                }

                var skills = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);
                var trackedSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Uses the Unity editor's index to query skill methods directly, avoiding enumerating every assembly and type after a Domain Reload.
                var methods = TypeCache.GetMethodsWithAttribute<UnitySkillAttribute>();
                foreach (var method in methods)
                {
                    if (!method.IsPublic || !method.IsStatic)
                        continue;

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
                        if (!allowedSet.Contains(EntityIdParameterName) && SupportsSyntheticEntityId(parameterNames))
                            allowedSet.Add(EntityIdParameterName);
                        skills[name] = new SkillInfo
                        {
                            Name = name,
                            Description = attr.Description ?? "",
                            Method = method,
                            Parameters = parameters,
                            TracksWorkflow = attr.TracksWorkflow,
                            SkipAutoPresnapshot = attr.SkipAutoPresnapshot,
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
                            LongRunning = attr.LongRunning,
                            RiskLevel = attr.RiskLevel ?? "low",
                            RequiresPackages = attr.RequiresPackages,
                            Mode = attr.Mode,
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

                _skills = skills; // Atomic assignment once the whole thing is built
                _workflowTrackedSkills = trackedSkills;

                // Reverse index: output field -> the skill that produces it
                var outputIdx = new Dictionary<string, List<SkillInfo>>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in skills.Values)
                {
                    var effectiveOutputs = GetEffectiveOutputs(s);
                    if (effectiveOutputs == null) continue;
                    foreach (var output in effectiveOutputs)
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

        /// <summary>
        /// The set of skills the current surface profile exposes externally -- every external discovery
        /// surface (manifest, schema, filtered manifest/schema, brief, recommend, snapshot)
        /// must enumerate this rather than <c>_skills.Values</c>. The one deliberate exception is <see cref="ValidateMetadata"/>,
        /// which audits the registry itself and must see everything.
        ///
        /// Main-thread only (needs to read the profile; the first call may hit EditorPrefs). True for every caller:
        /// what the HTTP thread's fast path reads is always just a string this method helped build.
        /// </summary>
        private static IEnumerable<SkillInfo> VisibleSkills()
        {
            // Under the default profile, returns the same instance as before -- no allocation, no per-skill check.
            if (SkillsSurfaceProfile.IsFull)
                return _skills.Values;
            return _skills.Values.Where(s => !SkillsSurfaceProfile.IsExcluded(s));
        }

        /// <summary>
        /// The workflow-tracked skill names actually offered externally, in the same order as the
        /// original collection. Every payload carrying this block is external-facing, so it must draw from the same authority as <see cref="VisibleSkills"/> --
        /// listing a hidden name here is exactly the leak the profile is meant to prevent, and what it would leak is the most consequential half of the registry
        /// (tracked skills are by definition write operations, and write operations are exactly what a profile withdraws).
        ///
        /// Under the default full profile nothing gets filtered, so this array -- and every byte of the v1 envelope built from it --
        /// is identical to the unfiltered set. Main-thread only, for the same reason as VisibleSkills.
        /// </summary>
        private static string[] VisibleWorkflowTrackedSkills()
        {
            if (SkillsSurfaceProfile.IsFull)
                return _workflowTrackedSkills.OrderBy(name => name).ToArray();

            return _workflowTrackedSkills
                .Where(name => _skills.TryGetValue(name, out var skill) && !SkillsSurfaceProfile.IsExcluded(skill))
                .OrderBy(name => name)
                .ToArray();
        }

        /// <summary>
        /// Drops every cached output string, but doesn't rerun skill discovery. Hooked onto
        /// <see cref="SkillsSurfaceProfile.OnChanged"/>: switching profiles doesn't change the skill registry,
        /// but every payload built from it changes, so the strings must be rebuilt, though reflection doesn't need to be redone.
        /// ETag follows along automatically as a side effect -- an entry in <c>_etagCache</c> is only valid when its source string is reference-equal to the current cache,
        /// and a rebuilt string's content differs, so its hash is naturally different too.
        /// </summary>
        internal static void InvalidateOutputCaches()
        {
            lock (_initLock)
            {
                _cachedManifest = null;
                _cachedSchema = null;
                _cachedBrief = null;
                _cachedMeta = null;
                _filteredOutputCache.Clear();
                _etagCache.Clear();
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

                var manifest = BuildManifest(VisibleSkills(), filtered: false, filters: null, manifestType: "manifest");
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

                var schema = BuildManifest(VisibleSkills(), filtered: false, filters: null, manifestType: "schema");
                _cachedSchema = JsonConvert.SerializeObject(schema, _jsonSettings);
                return _cachedSchema;
            }
        }

        /// <summary>
        /// The catalog layer -- what bare <c>GET /skills</c> (and <c>?brief=1</c>) now returns.
        /// Cached as a single string just like the full manifest: under the same skill set the payload bytes are stable,
        /// so the HTTP thread's fast path can return it directly with a stable ETag.
        /// </summary>
        public static string GetBrief()
        {
            Initialize();
            var cached = _cachedBrief;
            if (cached != null) return cached;

            lock (_initLock)
            {
                if (_cachedBrief != null) return _cachedBrief;

                _cachedBrief = JsonConvert.SerializeObject(BuildBriefManifest(), _jsonSettings);
                return _cachedBrief;
            }
        }

        /// <summary>
        /// <c>GET /skills/meta</c> -- the session-constant half of the manifest envelope (the category and operation enums,
        /// reserved request-body parameter names, the workflow-tracked skill list), plus the field defaults
        /// that <c>?wire=v2</c> entries omit. The v2 payload drops these blocks and points here, so an agent pays this cost once per session,
        /// rather than on every scoped fetch.
        ///
        /// Except for <c>workflowTrackedSkills</c>, everything here satisfies "session constant": that field is filtered by the surface profile
        /// (see <see cref="VisibleWorkflowTrackedSkills"/>), so it changes when the user switches profiles --
        /// the cache and its ETag are both dropped on a switch, and <c>metaHint</c> says as much.
        /// Removing the filtering to restore literal constancy would mean sending out names the user chose to hide.
        /// </summary>
        public static string GetMeta()
        {
            Initialize();
            var cached = _cachedMeta;
            if (cached != null) return cached;

            lock (_initLock)
            {
                if (_cachedMeta != null) return _cachedMeta;

                _cachedMeta = JsonConvert.SerializeObject(new
                {
                    manifestType = "meta",
                    schemaVersion = SkillSchemaVersion,
                    version = SkillsLogger.Version,
                    defaults = BuildWireDefaults(),
                    categories = Enum.GetNames(typeof(SkillCategory)).Where(c => c != "Uncategorized").ToArray(),
                    operationTypes = Enum.GetNames(typeof(SkillOperation)),
                    reservedBodyParameters = _reservedBodyParameters.OrderBy(x => x).ToArray(),
                    workflowTrackedSkills = VisibleWorkflowTrackedSkills(),
                    // Deliberately no surfaceProfile field here: the profile can be switched by the user at any time, and mixing a live value into
                    // a payload that says "fetch once per session" would only let someone read a stale value from here.
                    // /health is its sole authority, and every rejection response carries it too. This is different from workflowTrackedSkills
                    // being filtered by profile -- a name the user withdrew must never be sent out;
                    // the hint below states the consequence (this one block may change mid-session) rather than concealing it.
                    metaHint = "SESSION CONSTANTS — fetch once, reuse for the whole session. The enums, reserved parameters and defaults change only with the plugin version; 'workflowTrackedSkills' lists only what the active surface profile offers, so it moves (and the ETag changes) if the user switches profile mid-session. 'defaults' states the values ?wire=v2 omits from skill entries: a missing riskLevel is \"low\", a missing supportsDryRun is true, and a flag absent from 'flags' is false. For the live surface profile read 'surfaceProfile' on GET /health — it is user-switchable and deliberately not mirrored here."
                }, _jsonSettingsV2);
                return _cachedMeta;
            }
        }

        /// <summary>Whether a skill with the given name is registered.</summary>
        public static bool HasSkill(string name)
        {
            Initialize();
            return !string.IsNullOrEmpty(name) && _skills.ContainsKey(name);
        }

        public static string Execute(string name, string json)
        {
            return Execute(name, json, captureDiff: false);
        }

        /// <summary>
        /// Executes a skill. When <paramref name="captureDiff"/> is true (POST /skill/{name}?diff=1),
        /// captures a semantic scene diff as a pure side-channel observer, attached to a successful response as a top-level "sceneDiff" field --
        /// telling the caller what this operation actually changed. The diff never affects execution: the undo/workflow/error branches are left entirely untouched,
        /// and any diff failure only degrades sceneDiff to {error:...}, without affecting the skill's result.
        /// When captureDiff is false, output is byte-for-byte identical to before.
        /// </summary>
        public static string Execute(string name, string json, bool captureDiff)
        {
            Initialize();
            if (!_skills.TryGetValue(name, out var skill))
            {
                return ResolveSkillNotFound(name);
            }

            bool autoStartedWorkflow = false;
            // The persistence cost of EndTask() on the auto-workflow path, attached to the success envelope as workflowEndMs.
            // Always null on every other path, to keep output byte-for-byte unchanged.
            long? workflowEndMs = null;
            var wrapWithUndoTransaction = !skill.ReadOnly && !_transactionlessSkills.Contains(name);
            int undoGroup = -1;
            int workflowSnapshotCountBefore = WorkflowManager.CurrentTask?.snapshots?.Count ?? 0;
            // In the persisted editor change log, attributes the changes this call caused (including the
            // end-of-frame ObjectChangeEvent) to REST.
            EditorChangeTrackerService.BeginRestExecution();
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
                        details: new { unknownParams = validation.UnknownParams.ToArray(), allowedParams = GetEffectiveParameterNames(skill) },
                        suggestedFixes: fixes,
                        retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                }

                if (validation.MissingParams.Count > 0)
                {
                    return SkillErrorResponse.Build(
                        SkillErrorCode.MissingParam,
                        $"Missing required parameter: {validation.MissingParams[0]}",
                        skill: name,
                        details: new { missingParams = validation.MissingParams.ToArray(), allowedParams = GetEffectiveParameterNames(skill) },
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

                // The surface profile gate. Must run *before* the permission gate -- this ordering is itself a contract:
                // the permission tier answers "can this skill run," the profile answers "did the user even put it on the menu."
                // Bypass mode and the allowlist are authorization, so they cannot lift an exclusion -- only the user switching the profile back to full can.
                // If the profile gate ran second, Bypass could hand out a skill the panel marks hidden.
                var surfaceGate = ApplySurfaceGate(skill, name);
                if (surfaceGate != null)
                    return surfaceGate;

                // The permission tier gate. Placed before the high-risk confirmation gate, so a skill that is both FullAuto and high-risk
                // reports MODE_RESTRICTED first; the ConfirmationToken step only matters once the skill is already allowed to run.
                var modeGate = ApplyModeGate(skill, name, validation);
                if (modeGate != null)
                    return modeGate;

                // Confirmation gate: once ConfirmationTokenService.RequireConfirmation is enabled,
                // a high-risk skill requires an explicit one-time token.
                // Off by default -- enable it in Window > UnitySkills > Server > Settings.
                if (ConfirmationTokenService.RequireConfirmation && ConfirmationTokenService.IsHighRisk(skill))
                {
                    var gateResult = ApplyConfirmationGate(skill, name, json, validation);
                    if (gateResult != null)
                        return gateResult;
                }

                var args = validation.Args;
                var invoke = validation.InvokeArgs;

                // Pre-capture for the semantic diff (?diff=1). A pure side-channel observer, positioned after the permission gates and before invoke;
                // skipped for read-only skills (nothing to diff against). CaptureBefore fully isolates its own exceptions internally.
                SkillSceneDiff.DiffCapture diffCapture = null;
                if (captureDiff && !skill.ReadOnly)
                    diffCapture = SkillSceneDiff.CaptureBefore(args);

                if (wrapWithUndoTransaction)
                {
                    UnityEditor.Undo.IncrementCurrentGroup();
                    UnityEditor.Undo.SetCurrentGroupName($"Skill: {name}");
                    undoGroup = UnityEditor.Undo.GetCurrentGroup();
                }

                // ========== Automatic workflow recording ==========
                if (skill.TracksWorkflow && !WorkflowManager.IsRecording)
                {
                    var desc = $"{name} - {(json?.Length > 80 ? json.Substring(0, 80) + "..." : json ?? "")}";
                    WorkflowManager.BeginTask(name, desc);
                    autoStartedWorkflow = true;
                }

                // Automatically snapshots the target objects *before* the skill executes, to support rollback.
                // A skill that manages its own dedicated snapshot opts out via SkipAutoPresnapshot, to avoid a redundant generic backup.
                if (WorkflowManager.IsRecording && !skill.SkipAutoPresnapshot)
                {
                    TrySnapshotTargetsFromArgs(args);
                }
                // ==============================================

                // verbose control
                bool verbose = true; // Defaults to true when unspecified, for backward compatibility with direct calls
                if (args.TryGetValue("verbose", StringComparison.OrdinalIgnoreCase, out var verboseToken))
                {
                    try
                    {
                        verbose = verboseToken.ToObject<bool>();
                    }
                    catch (Exception)
                    {
                        // ToObject<bool> accepts true/false/"true"/1 but rejects things like "1"/"yes".
                        // Try the common string forms first; everything else is a client error and must be presented as
                        // TYPE_MISMATCH + fix_and_retry -- the generic catch below would mislabel it as
                        // INTERNAL "[Transactional Revert]" + wait_and_retry,
                        // trapping the agent in a retry loop on a request body only it can fix.
                        var raw = verboseToken.Type == JTokenType.String
                            ? verboseToken.Value<string>()?.Trim().ToLowerInvariant()
                            : null;
                        if (raw == "true" || raw == "1" || raw == "yes")
                            verbose = true;
                        else if (raw == "false" || raw == "0" || raw == "no")
                            verbose = false;
                        else
                        {
                            // Nothing has been invoked yet at this point; roll back the bookkeeping started above,
                            // consistent with the catch handling below.
                            if (autoStartedWorkflow && WorkflowManager.IsRecording)
                                WorkflowManager.AbortTask();
                            else if (WorkflowManager.IsRecording)
                                WorkflowManager.TruncateCurrentTask(workflowSnapshotCountBefore);
                            if (undoGroup >= 0)
                                UnityEditor.Undo.RevertAllInCurrentGroup();

                            return SkillErrorResponse.Build(
                                SkillErrorCode.TypeMismatch,
                                $"Parameter 'verbose' must be a boolean (true/false), got: {verboseToken.ToString(Formatting.None)}",
                                skill: name,
                                details: new { typeErrors = new object[] { new { parameter = "verbose", expectedType = "boolean", error = $"Cannot convert {verboseToken.Type} to Boolean" } } },
                                retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                        }
                    }
                    args.Remove("verbose");
                }

                // Pagination control for Summary mode.
                // Skipped if the skill itself declares a parameter with the same name: 'limit' belongs to asset_find/light_find_all/etc. themselves,
                // and must reach them as their own parameter rather than being swallowed by the envelope layer as pagination (which would also wrap small results in a page).
                int? offset = null;
                int? limit = null;

                if (args.TryGetValue("pageOffset", StringComparison.OrdinalIgnoreCase, out var pageOffsetToken))
                {
                    if (!TryReadPagingArg(pageOffsetToken, "pageOffset", 0, out var value, out var error))
                    {
                        UnwindBeforeInvoke(autoStartedWorkflow, workflowSnapshotCountBefore, undoGroup);
                        return SkillErrorResponse.Build(SkillErrorCode.TypeMismatch, error, skill: name,
                            retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                    }
                    offset = value;
                    args.Remove("pageOffset");
                }

                if (args.TryGetValue("pageLimit", StringComparison.OrdinalIgnoreCase, out var pageLimitToken))
                {
                    if (!TryReadPagingArg(pageLimitToken, "pageLimit", 1, out var value, out var error))
                    {
                        UnwindBeforeInvoke(autoStartedWorkflow, workflowSnapshotCountBefore, undoGroup);
                        return SkillErrorResponse.Build(SkillErrorCode.TypeMismatch, error, skill: name,
                            retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                    }
                    limit = value;
                    args.Remove("pageLimit");
                }

                if (!offset.HasValue && !SkillDeclaresParameter(skill, "offset") &&
                    args.TryGetValue("offset", StringComparison.OrdinalIgnoreCase, out var offsetToken))
                {
                    if (!TryReadPagingArg(offsetToken, "offset", minValue: 0, out var offsetValue, out var offsetError))
                    {
                        // Nothing has been invoked yet at this point; roll back the bookkeeping started above.
                        UnwindBeforeInvoke(autoStartedWorkflow, workflowSnapshotCountBefore, undoGroup);
                        return SkillErrorResponse.Build(
                            SkillErrorCode.TypeMismatch,
                            offsetError,
                            skill: name,
                            details: new { typeErrors = new object[] { new { parameter = "offset", expectedType = "integer", error = offsetError } } },
                            retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                    }
                    offset = offsetValue;
                    args.Remove("offset");
                }

                if (!limit.HasValue && !SkillDeclaresParameter(skill, "limit") &&
                    args.TryGetValue("limit", StringComparison.OrdinalIgnoreCase, out var limitToken))
                {
                    if (!TryReadPagingArg(limitToken, "limit", minValue: 1, out var limitValue, out var limitError))
                    {
                        UnwindBeforeInvoke(autoStartedWorkflow, workflowSnapshotCountBefore, undoGroup);
                        return SkillErrorResponse.Build(
                            SkillErrorCode.TypeMismatch,
                            limitError,
                            skill: name,
                            details: new { typeErrors = new object[] { new { parameter = "limit", expectedType = "integer", error = limitError } } },
                            retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                    }
                    limit = limitValue;
                    args.Remove("limit");
                }

                var result = skill.Method.Invoke(null, invoke);

                if (!skill.ReadOnly)
                    UnityEditor.Undo.FlushUndoRecordObjects();

                if (SkillResultHelper.TryGetErrorContext(result, out var errorContext))
                {
                    if (autoStartedWorkflow && WorkflowManager.IsRecording)
                        WorkflowManager.AbortTask();
                    else if (WorkflowManager.IsRecording)
                        WorkflowManager.TruncateCurrentTask(workflowSnapshotCountBefore);

                    if (undoGroup >= 0)
                        UnityEditor.Undo.RevertAllInCurrentGroup();

                    // Every skill's business error funnels in here. Whatever the skill itself declared takes priority field by field;
                    // the classifier only fills gaps, so the roughly 700 skills that just return { error = "..." } still get an error code
                    // and retry strategy, instead of a uniform SKILL_ERROR + abort. Declaring errorCode also pulls the rest of the fields along,
                    // keeping a partial declaration self-consistent.
                    var classified = errorContext.Code.HasValue
                        ? SkillErrorClassifier.ForCode(errorContext.Code.Value, errorContext.Message)
                        : SkillErrorClassifier.Classify(errorContext.Message);

                    return SkillErrorResponse.Build(
                        errorContext.Code ?? classified.Code,
                        errorContext.Message,
                        skill: name,
                        suggestedFixes: errorContext.SuggestedFixes ?? classified.SuggestedFixes,
                        relatedSkills: errorContext.RelatedSkills ?? classified.RelatedSkills,
                        retryStrategy: errorContext.RetryStrategy ?? classified.RetryStrategy,
                        extra: errorContext.Extra);
                }

                // ========== Automatic workflow wrap-up ==========
                if (autoStartedWorkflow)
                {
                    // On the auto-workflow path, persistence is entirely EndTask's responsibility (it calls SaveHistory internally).
                    // The cost is measured here for observability.
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    WorkflowManager.EndTask();
                    sw.Stop();
                    workflowEndMs = sw.ElapsedMilliseconds;
                }
                else if (WorkflowManager.IsRecording)
                {
                    // Manual session (workflow_begin_task): otherwise every tracked skill would save on every single call.
                    // Skips the save when the current task has had no new snapshots since the last save.
                    if (ManualSessionIsDirty(WorkflowManager.CurrentTask))
                        WorkflowManager.SaveHistory();
                }
                // ========================================

                if (wrapWithUndoTransaction)
                {
                    // Commit the transaction
                    UnityEditor.Undo.CollapseUndoOperations(undoGroup);

                    // A skill invoked over REST never passes through the usual menu/mouse event boundaries that advance Unity's undo stack.
                    // So explicitly move to the next group, so editor_undo/editor_redo act on the change that was just completed.
                    if (!skill.ReadOnly)
                        UnityEditor.Undo.IncrementCurrentGroup();
                }

                // Post-capture and comparison for the semantic diff (?diff=1). Attached to the success envelope as a top-level "sceneDiff";
                // null on the default path, to keep output byte-for-byte unchanged. BuildSceneDiff already isolates its own exceptions --
                // the diff can never break the response, and a skill that reported an error above never reaches this point anyway.
                JToken sceneDiff = BuildSceneDiff(captureDiff, skill, diffCapture, result);

                if (!verbose && result != null)
                {
                    // "Summary Mode" logic with pagination
                    var jsonResult = JToken.FromObject(result);

                    var arr = FindPageArray(jsonResult, out var arrayProperty);
                    if (arr != null && ((SummaryAutoTruncate && arr.Count > 10) || offset.HasValue || limit.HasValue))
                    {
                        int startIndex = offset ?? 0;
                        int pageSize = limit ?? SummaryPageSize;

                        // Clamp to a valid range
                        if (startIndex >= arr.Count)
                        {
                            // offset is beyond the array bounds, return an empty page
                            var emptyWrapper = new JObject
                            {
                                ["isTruncated"] = true,
                                ["totalCount"] = arr.Count,
                                ["offset"] = startIndex,
                                ["limit"] = pageSize,
                                ["showing"] = 0,
                                ["items"] = new JArray(),
                                ["hint"] = $"Offset {startIndex} is beyond array bounds (totalCount: {arr.Count}). To see items, pass a lower 'pageOffset' value."
                            };
                            if (arrayProperty != null)
                            {
                                var preserved = (JObject)jsonResult.DeepClone();
                                preserved[arrayProperty] = new JArray();
                                foreach (var property in emptyWrapper.Properties().Where(property => property.Name != "items"))
                                    preserved[property.Name] = property.Value;
                                return SerializeSuccessResponse(preserved, sceneDiff, workflowEndMs);
                            }
                            return SerializeSuccessResponse(emptyWrapper, sceneDiff, workflowEndMs);
                        }

                        int endIndex = (int)Math.Min((long)startIndex + pageSize, arr.Count);
                        int actualCount = endIndex - startIndex;

                        var paginatedItems = new JArray();
                        for (int i = startIndex; i < endIndex; i++)
                            paginatedItems.Add(arr[i]);

                        bool hasMore = endIndex < arr.Count;
                        int? nextOffset = hasMore ? (int?)endIndex : null;

                        // Return a wrapper object carrying pagination metadata
                        var wrapper = new JObject
                        {
                            ["isTruncated"] = true,
                            ["totalCount"] = arr.Count,
                            ["offset"] = startIndex,
                            ["limit"] = pageSize,
                            ["showing"] = actualCount,
                            ["items"] = paginatedItems
                        };

                        if (hasMore)
                        {
                            wrapper["nextOffset"] = nextOffset;
                            wrapper["hint"] = $"Showing items {startIndex}-{endIndex - 1} of {arr.Count}. To see more, pass 'pageOffset={nextOffset}' (or 'verbose=true' for all items).";
                        }
                        else
                        {
                            wrapper["hint"] = $"Showing items {startIndex}-{endIndex - 1} of {arr.Count} (last page).";
                        }

                        if (arrayProperty != null)
                        {
                            var preserved = (JObject)jsonResult.DeepClone();
                            preserved[arrayProperty] = paginatedItems;
                            foreach (var property in wrapper.Properties().Where(property => property.Name != "items"))
                                preserved[property.Name] = property.Value;
                            return SerializeSuccessResponse(preserved, sceneDiff, workflowEndMs);
                        }

                        return SerializeSuccessResponse(wrapper, sceneDiff, workflowEndMs);
                    }
                }

                // Full mode (verbose=true, or the result is already small): return as-is
                return SerializeSuccessResponse(result, sceneDiff, workflowEndMs);
            }
            catch (TargetInvocationException ex)
            {
                // Clean up an auto-started workflow on error
                if (autoStartedWorkflow && WorkflowManager.IsRecording)
                    WorkflowManager.AbortTask();
                else if (WorkflowManager.IsRecording)
                    WorkflowManager.TruncateCurrentTask(workflowSnapshotCountBefore);

                if (undoGroup >= 0)
                {
                    // Roll back the transaction
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
            catch (Newtonsoft.Json.JsonException ex)
            {
                // Malformed request body -- JObject.Parse inside ValidateParameters throws before any change or undo group
                // has been opened. This is a client error, not a server or transaction failure: return
                // InvalidJson + fix_and_retry, so the agent edits the request body instead of spinning on wait_and_retry
                // (the generic catch below would mislabel it as "[Transactional Revert]"). Consistent with DryRun.
                return SkillErrorResponse.Build(
                    SkillErrorCode.InvalidJson,
                    $"Invalid JSON: {ex.Message}",
                    skill: name,
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
            }
            catch (Exception ex)
            {
                // Clean up an auto-started workflow on error
                if (autoStartedWorkflow && WorkflowManager.IsRecording)
                    WorkflowManager.AbortTask();
                else if (WorkflowManager.IsRecording)
                    WorkflowManager.TruncateCurrentTask(workflowSnapshotCountBefore);

                if (undoGroup >= 0)
                {
                    // Roll back the transaction
                    UnityEditor.Undo.RevertAllInCurrentGroup();
                }

                return SkillErrorResponse.Build(
                    SkillErrorCode.Internal,
                    $"[Transactional Revert] {ex.Message}",
                    skill: name,
                    details: new { exceptionType = ex.GetType().Name },
                    retryStrategy: SkillErrorResponse.RetryWaitAndRetry);
            }
            finally
            {
                EditorChangeTrackerService.EndRestExecution();
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
                        description = GetEffectiveDescription(skill),
                        category = skill.Category != SkillCategory.Uncategorized ? skill.Category.ToString() : null,
                        operation = FormatOperation(skill.Operation),
                        tags = skill.Tags,
                        outputs = GetEffectiveOutputs(skill),
                        requiresInput = skill.RequiresInput,
                        readOnly = skill.ReadOnly,
                        tracksWorkflow = skill.TracksWorkflow,
                        mutatesScene = skill.MutatesScene,
                        mutatesAssets = skill.MutatesAssets,
                        mayTriggerReload = skill.MayTriggerReload,
                        mayEnterPlayMode = skill.MayEnterPlayMode,
                        supportsDryRun = skill.SupportsDryRun,
                        // Always output, regardless of value. This flag used to exist only in ?wire=v2's sparse "flags" array,
                        // so the default surface (the v1 payload and this preview) never mentioned that the call about to be made
                        // would block the main thread (and the whole HTTP queue) for seconds. The preview is exactly the place that should say this:
                        // outputting it only when true would make "absent" ambiguous between "fast" and "old version," so both values are emitted.
                        longRunning = skill.LongRunning,
                        riskLevel = skill.RiskLevel,
                        requiresPackages = skill.RequiresPackages,
                        mode = SkillsModeManager.SkillModeToWire(skill.Mode),
                        approvalBehavior = SkillsModeManager.ApprovalBehaviorForSkill(skill)
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
                    authorization = BuildAuthorizationPreview(skill),
                    steps = planData?["steps"],
                    changes = planData?["changes"],
                    note = "No execution performed"
                }, _jsonSettings);
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                return SkillErrorResponse.Build(
                    SkillErrorCode.InvalidJson,
                    $"Invalid JSON: {ex.Message}",
                    skill: name,
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
            }
            catch (Exception ex)
            {
                // Even valid JSON can still crash plan/semantic validation (e.g. an NRE). Reporting this case as INVALID_JSON
                // would send the agent into repeatedly rewriting a request body that was never the problem; so, following Execute's catch split,
                // the real failure is reported honestly.
                return SkillErrorResponse.Build(
                    SkillErrorCode.Internal,
                    $"Dry-run failed: {ex.Message}",
                    skill: name,
                    details: new { exceptionType = ex.GetType().Name },
                    retryStrategy: SkillErrorResponse.Abort);
            }
        }

        /// <summary>
        /// A read-only preview of the verdict <see cref="ApplyModeGate"/> would give -- so a dry run can answer
        /// "is this call actually allowed to run," rather than making the agent hit the
        /// MODE_FORBIDDEN / MODE_RESTRICTED wall only once it reaches execute.
        ///
        /// Deliberately re-derives the conclusion from <see cref="SkillsModeManager.CurrentMode"/>, the allowlist, and
        /// <see cref="SkillsModeManager.IsForbiddenInSemi"/>, rather than calling
        /// <c>CheckAccess</c> directly: CheckAccess consumes this thread's one-shot grant token, and the gate wrapping it would also
        /// issue a grant request and write an audit entry -- none of which a preview should do. The order below matches CheckAccess exactly,
        /// minus the one-shot check -- a pending one-shot bypass belongs to the one execute call right after the grant, not to a preview,
        /// and reporting it here would be advertising a permission the next caller might not actually get.
        ///
        /// Should be read as a prediction, not a reservation: the mode or allowlist may change between this dry run and the execute call,
        /// so <c>allowed:true</c> is not a guarantee.
        ///
        /// The verdict is based on the skill's own metadata; for every skill except the "carried-write" entry points
        /// (batch_execute / batch_retry_failed, and the workflow undo/redo/revert family),
        /// that's the entire basis. Those entry points are rejected at execution time based on a classification of a payload this preview has no access to,
        /// so an additional note is attached here instead of a verdict -- see
        /// <see cref="SkillsSurfaceProfile.CarriedWritePreviewGate"/>.
        /// </summary>
        private static object BuildAuthorizationPreview(SkillInfo skill)
        {
            var verdict = BuildModeAuthorizationPreview(skill);

            // Already rejected at the skill layer: the SURFACE_EXCLUDED block has already said everything the payload needs to say;
            // saying it twice would read as two different walls.
            if (SkillsSurfaceProfile.IsExcluded(skill))
                return verdict;

            var payloadGate = SkillsSurfaceProfile.CarriedWritePreviewGate(skill.Name);
            if (payloadGate == null)
                return verdict;

            // Only appends, never replaces, so the original fields' names, values, and order are unchanged; the only addition is that note.
            var annotated = JObject.FromObject(verdict);
            foreach (var property in JObject.FromObject(payloadGate).Properties())
                annotated[property.Name] = property.Value;
            return annotated;
        }

        /// <summary>
        /// The metadata-only half of <see cref="BuildAuthorizationPreview"/>: first checks surface exclusion,
        /// then walks the mode/allowlist decision ladder in the same order as CheckAccess.
        /// </summary>
        private static object BuildModeAuthorizationPreview(SkillInfo skill)
        {
            var mode = SkillsModeManager.CurrentMode;
            var modeWire = SkillsModeManager.ModeToWire(mode);
            bool allowlisted = SkillsModeManager.IsInAllowlist(skill.Name);

            // First, consistent with the execute path: exclusion takes priority over Bypass and the allowlist,
            // so reporting allowed:true here for a skill that is "allowlisted but hidden"
            // would send the agent straight into a SURFACE_EXCLUDED it was just told it wouldn't hit.
            // The dry run itself is never blocked -- previewing an excluded skill is exactly how the agent learns "what the user needs to change."
            if (SkillsSurfaceProfile.IsExcluded(skill))
            {
                return new
                {
                    allowed = false,
                    blockedBy = SkillErrorCode.SurfaceExcluded.ToWireString(),
                    currentMode = modeWire,
                    allowlisted,
                    hint = BuildSurfaceExclusionHint(skill, forPreview: true),
                    surfaceProfile = SkillsSurfaceProfile.CurrentWire,
                };
            }

            if (mode == SkillsOperatingMode.Bypass || allowlisted)
            {
                return new
                {
                    allowed = true,
                    blockedBy = (string)null,
                    currentMode = modeWire,
                    allowlisted,
                    hint = allowlisted
                        ? "Allowlisted — runs without approval in any mode."
                        : "Bypass mode — every skill runs without approval."
                };
            }

            if (SkillsModeManager.IsForbiddenInSemi(skill))
            {
                return new
                {
                    allowed = false,
                    blockedBy = SkillErrorCode.ModeForbidden.ToWireString(),
                    currentMode = modeWire,
                    allowlisted,
                    hint = "Classified as never-in-semi (delete / play mode / domain reload / high risk). Executing needs Bypass mode, or the user adding this skill to the allowlist."
                };
            }

            if (mode == SkillsOperatingMode.Auto || skill.Mode == SkillMode.SemiAuto)
            {
                return new
                {
                    allowed = true,
                    blockedBy = (string)null,
                    currentMode = modeWire,
                    allowlisted,
                    hint = "Executes directly under the current mode — no approval step."
                };
            }

            return new
            {
                allowed = false,
                blockedBy = SkillErrorCode.ModeRestricted.ToWireString(),
                currentMode = modeWire,
                allowlisted,
                hint = "FullAuto skill in Approval mode: the execute call will answer MODE_RESTRICTED with a grant token. Ask the user, then POST /permission/grant {skill, token} — that grant call runs the skill and returns its result."
            };
        }

        private static string SerializeSuccessResponse(object result, JToken sceneDiff = null, long? workflowEndMs = null)
        {
            var jsonResult = NormalizeSuccessResult(result);

            if (ServerAvailabilityHelper.IsCompilationInProgress())
            {
                try
                {
                    if (jsonResult is JObject obj && !obj.ContainsKey("serverAvailability"))
                    {
                        var notice = ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                            "A skill execution may have triggered compilation or asset refresh.",
                            alwaysInclude: true);
                        if (notice != null)
                        {
                            obj["serverAvailability"] = JToken.FromObject(notice);
                            return BuildSuccessEnvelope(obj, sceneDiff, workflowEndMs);
                        }
                    }
                }
                catch { }
            }

            return BuildSuccessEnvelope(jsonResult, sceneDiff, workflowEndMs);
        }

        // Serializes the success envelope. sceneDiff (?diff=1) and workflowEndMs (the auto-workflow EndTask persistence
        // time, in milliseconds) are only appended as top-level fields when present; when neither exists, output is byte-for-byte identical to before diff was introduced.
        private static string BuildSuccessEnvelope(JToken result, JToken sceneDiff, long? workflowEndMs = null)
        {
            if (sceneDiff == null && workflowEndMs == null)
                return JsonConvert.SerializeObject(new { status = "success", result }, _jsonSettings);
            if (workflowEndMs == null)
                return JsonConvert.SerializeObject(new { status = "success", result, sceneDiff }, _jsonSettings);
            if (sceneDiff == null)
                return JsonConvert.SerializeObject(new { status = "success", result, workflowEndMs = workflowEndMs.Value }, _jsonSettings);
            return JsonConvert.SerializeObject(new { status = "success", result, sceneDiff, workflowEndMs = workflowEndMs.Value }, _jsonSettings);
        }

        // Builds the sceneDiff payload for a successful ?diff=1 execution. A read-only skill just gets a note (nothing to diff);
        // everything else is delegated to SkillSceneDiff.Build. Fully isolated -- any failure degrades to {error:...}, and never disturbs the response envelope.
        private static JToken BuildSceneDiff(bool captureDiff, SkillInfo skill, SkillSceneDiff.DiffCapture diffCapture, object result)
        {
            if (!captureDiff)
                return null;
            try
            {
                if (skill.ReadOnly)
                    return new JObject { ["note"] = "read-only skill, no diff captured" };
                return SkillSceneDiff.Build(diffCapture, result);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"[diff] build failed: {ex.Message}");
                return new JObject { ["error"] = $"diff failed: {ex.Message}" };
            }
        }

        private static JToken NormalizeSuccessResult(object result)
        {
            try
            {
                var token = result is JToken existingToken
                    ? existingToken.DeepClone()
                    : JToken.FromObject(result ?? new object(), JsonSerializer.Create(_jsonSettings));

                AddEntityIdsToResult(token);
                return token;
            }
            catch
            {
                return result is JToken fallbackToken
                    ? fallbackToken.DeepClone()
                    : JToken.FromObject(result ?? new object());
            }
        }

        private static void AddEntityIdsToResult(JToken token)
        {
            if (token == null)
                return;

            if (token is JObject obj)
            {
                TryAddEntityIdToResultObject(obj);
                foreach (var property in obj.Properties().ToArray())
                    AddEntityIdsToResult(property.Value);
                return;
            }

            if (token is JArray array)
            {
                foreach (var item in array)
                    AddEntityIdsToResult(item);
            }
        }

        private static void TryAddEntityIdToResultObject(JObject obj)
        {
            if (obj == null ||
                TryGetJsonValue(obj, EntityIdParameterName, out _) ||
                !TryGetJsonValue(obj, "instanceId", out var instanceIdToken))
            {
                return;
            }

            var unityObject = ResolveUnityObjectFromResultObject(obj, instanceIdToken);
            var entityId = UnityObjectIdUtility.GetEntityId(unityObject);
            if (!string.IsNullOrWhiteSpace(entityId))
                obj[EntityIdParameterName] = entityId;
        }

        private static UnityEngine.Object ResolveUnityObjectFromResultObject(JObject obj, JToken instanceIdToken)
        {
            if (TryReadInt(instanceIdToken, out var instanceId) && instanceId != 0)
            {
                var byInstanceId = UnityObjectIdUtility.ObjectIdToObject(instanceId);
                if (byInstanceId != null)
                    return byInstanceId;
            }

            foreach (var pathField in new[] { "assetPath", "materialPath", "profilePath", "prefabPath", "path" })
            {
                if (!TryGetJsonString(obj, pathField, out var candidatePath))
                    continue;

                var asset = TryResolveAssetPath(candidatePath);
                if (asset != null)
                    return asset;

                var sceneObject = TryResolveScenePath(candidatePath);
                if (sceneObject != null)
                    return sceneObject;
            }

            foreach (var nameField in new[] { "gameObject", "gameObjectName", "target", "targetName", "objectName", "cameraName", "vcamName", "sequencerName" })
            {
                if (!TryGetJsonString(obj, nameField, out var candidateName))
                    continue;

                var sceneObject = GameObjectFinder.Find(name: candidateName);
                if (sceneObject != null)
                    return sceneObject;
            }

            if (!LooksLikeAssetResult(obj) && TryGetJsonString(obj, "name", out var name))
                return GameObjectFinder.Find(name: name);

            return null;
        }

        private static UnityEngine.Object TryResolveAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var normalized = path.Replace('\\', '/');
            if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(normalized);
        }

        private static GameObject TryResolveScenePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var normalized = path.Replace('\\', '/');
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return GameObjectFinder.Find(path: normalized);
        }

        private static bool LooksLikeAssetResult(JObject obj)
        {
            return TryGetJsonValue(obj, "assetPath", out _) ||
                TryGetJsonValue(obj, "materialPath", out _) ||
                TryGetJsonValue(obj, "profilePath", out _) ||
                TryGetJsonValue(obj, "prefabPath", out _) ||
                TryGetJsonValue(obj, "shader", out _) ||
                TryGetJsonValue(obj, "texture", out _) ||
                TryGetJsonValue(obj, "renderPipeline", out _);
        }

        private static bool TryGetJsonString(JObject obj, string propertyName, out string value)
        {
            value = null;
            if (!TryGetJsonValue(obj, propertyName, out var token) ||
                token == null ||
                token.Type == JTokenType.Null)
            {
                return false;
            }

            value = token.ToString();
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool TryGetJsonValue(JObject obj, string propertyName, out JToken value)
        {
            value = null;
            if (obj == null || string.IsNullOrEmpty(propertyName))
                return false;

            foreach (var property in obj.Properties())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            return false;
        }

        private static bool TryReadInt(JToken token, out int value)
        {
            value = 0;
            if (token == null || token.Type == JTokenType.Null)
                return false;

            try
            {
                value = token.ToObject<int>();
                return true;
            }
            catch
            {
                return int.TryParse(token.ToString(), out value);
            }
        }

        public static void Refresh()
        {
            lock (_initLock)
            {
                _initialized = false;
                _skills = null;
                _outputIndex = null;
                InvalidateOutputCaches();
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
        /// Explicit RequiresInput metadata with the same name overrides the CLR-level "optional (has a default value)" determination.
        /// Otherwise, only a parameter with neither a default value nor null acceptance counts as required.
        /// </summary>
        private static bool IsParameterRequired(SkillInfo skill, ParameterInfo p)
        {
            if (skill?.RequiresInput?.Any(required =>
                    string.Equals(required, p.Name, StringComparison.OrdinalIgnoreCase)) == true)
                return true;
            if (p.HasDefaultValue) return false;
            if (p.ParameterType.IsValueType && Nullable.GetUnderlyingType(p.ParameterType) == null)
                return true;
            return false;
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

        // ========== Filtered manifest ==========

        /// <summary>
        /// Returns the skill manifest filtered by query parameters.
        /// Supports category, operation, tags, readOnly, q (text search).
        /// </summary>
        public static string GetFilteredManifest(string queryString) => BuildFilteredOutput(queryString, "manifest", out _);

        /// <summary>
        /// Same filter conditions as GetFilteredManifest (category/operation/tags/readOnly/q),
        /// but marks the payload's manifestType as "schema" -- backing GET /skills/schema?category=...
        /// (a scoped schema, so needing just one category doesn't require pulling the whole roughly 618KB schema).
        /// </summary>
        public static string GetFilteredSchema(string queryString) => BuildFilteredOutput(queryString, "schema", out _);

        /// <summary>
        /// On top of <see cref="GetFilteredManifest(string)"/>, additionally reports whether the returned string is a rejection response
        /// or a manifest. The HTTP layer needs this distinction, and can't recover it from the payload without sniffing the text, for two reasons:
        /// an error must answer 400 rather than 200; and it must not get an ETag -- a cached 400 response body would give the client's next
        /// If-None-Match a 304 with no body at all, which reads as "your query was fine, and nothing changed."
        /// </summary>
        public static string GetFilteredManifest(string queryString, out bool isError) =>
            BuildFilteredOutput(queryString, "manifest", out isError);

        /// <summary>The schema counterpart of <see cref="GetFilteredManifest(string, out bool)"/>.</summary>
        public static string GetFilteredSchema(string queryString, out bool isError) =>
            BuildFilteredOutput(queryString, "schema", out isError);

        // The query keys BuildFilteredOutput actually uses to filter or branch. Everything else (typos, cache-busting
        // nonces, client telemetry parameters...) gets stripped before entering the cache key -- otherwise every distinct unrecognized value would create
        // a permanent roughly 618KB cache entry (see the MaxCacheEntries comment above _filteredOutputCache).
        // Adding a new key here must also add it to _blankRejectingFilterKeys, or "?newKey=" silently becomes a no-op again.
        private static readonly HashSet<string> _recognizedFilterKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "category", "operation", "tags", "readonly", "q", "summary", "includeSchema", "brief",
            // Surface/wire-format selectors -- listed here so they aren't stripped, but they never narrow the skill set (see _surfaceSelectionKeys).
            "wire", "full"
        };

        // These recognized keys select the payload's *shape*, not a subset of skills. They must never be echoed back as "filters,"
        // nor set "filtered" to true: a bare ?wire=v2 is still the complete, unfiltered manifest,
        // and calling it filtered would misrepresent the meaning of totalSkills.
        private static readonly HashSet<string> _surfaceSelectionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "wire", "full"
        };

        private static Dictionary<string, string> StripUnrecognizedFilterKeys(Dictionary<string, string> filters)
        {
            if (filters.Count == 0 || filters.Keys.All(k => _recognizedFilterKeys.Contains(k)))
                return filters;

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in filters)
            {
                if (_recognizedFilterKeys.Contains(kv.Key))
                    result[kv.Key] = kv.Value;
            }
            return result;
        }

        /// <summary>
        /// Strips <see cref="_surfaceSelectionKeys"/>, leaving only keys that actually narrow the skill set.
        /// Returns the argument instance unchanged when there's nothing to strip -- this alone is what keeps the <c>filters</c> object
        /// echoed for every pre-v2 query byte-for-byte identical.
        /// </summary>
        private static Dictionary<string, string> StripSurfaceSelectionKeys(Dictionary<string, string> filters)
        {
            if (filters.Count == 0 || !filters.Keys.Any(k => _surfaceSelectionKeys.Contains(k)))
                return filters;

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in filters)
            {
                if (!_surfaceSelectionKeys.Contains(kv.Key))
                    result[kv.Key] = kv.Value;
            }
            return result;
        }

        private static bool IsQueryFlagSet(Dictionary<string, string> filters, string key)
        {
            return filters.TryGetValue(key, out var value) && value != null &&
                (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
        }

        // Wire format version. v1 is the legacy payload and stays the default forever: an unrecognized ?wire value resolves to v1 rather than erroring,
        // so a typo never silently sends a caller a shape it can't parse.
        private const int WireV1 = 1;
        private const int WireV2 = 2;

        private static int ResolveWireVersion(Dictionary<string, string> filters)
        {
            if (filters.TryGetValue("wire", out var raw) && raw != null)
            {
                var value = raw.Trim();
                if (value == "2" || value.Equals("v2", StringComparison.OrdinalIgnoreCase))
                    return WireV2;
            }
            return WireV1;
        }

        /// <summary>Which cached string a GET in the manifest family answers with.</summary>
        private enum GetSurface
        {
            /// <summary>_cachedManifest / _cachedSchema -- the untouched full v1 payload.</summary>
            FullV1,
            /// <summary>_cachedBrief -- bare GET /skills, and ?brief=1 on either path.</summary>
            Brief,
            /// <summary>_cachedMeta -- GET /skills/meta.</summary>
            Meta,
            /// <summary>_filteredOutputCache -- every scoped, summary, or wire=v2 variant.</summary>
            Keyed
        }

        private const string BriefCacheKey = "manifest|__brief__";
        private const string MetaCacheKey = "meta|__full__";

        /// <summary>
        /// The single source of truth for "which surface this query selects, and which cache key to use." The main-thread builder
        /// (<see cref="BuildFilteredOutput"/>) and the HTTP thread's fast path (<see cref="BuildGetCacheKey"/>)
        /// both call it -- the moment the two disagree, the fast path will answer a request for this surface with bytes from a different one.
        ///
        /// Given that <paramref name="filters"/> has already had irrelevant keys stripped, the determination order is:
        /// <list type="number">
        /// <item>meta path -> <see cref="GetSurface.Meta"/>.</item>
        /// <item>?brief is true, or a bare /skills request (no narrowing filter and no ?full) ->
        /// <see cref="GetSurface.Brief"/>. This is the v2.7 default flip: bare GET /skills used to return the roughly 618KB
        /// manifest, and now returns the catalog; ?full=1 restores the old behavior.</item>
        /// <item>No narrowing key at all and wire is v1 -> <see cref="GetSurface.FullV1"/>
        /// (bare /skills/schema, and /skills?full=1).</item>
        /// <item>Everything else -> <see cref="GetSurface.Keyed"/>.</item>
        /// </list>
        /// Brief is independent of wire (it carries no per-skill flags that could be trimmed), so both wire versions share one cache entry,
        /// and therefore share one ETag.
        /// </summary>
        private static string ResolveGetSurface(string manifestType, Dictionary<string, string> filters, out GetSurface surface)
        {
            if (manifestType == "meta")
            {
                surface = GetSurface.Meta;
                return MetaCacheKey;
            }

            bool hasNarrowingFilter = StripSurfaceSelectionKeys(filters).Count > 0;

            if (IsQueryFlagSet(filters, "brief") ||
                (!hasNarrowingFilter && manifestType != "schema" && !IsQueryFlagSet(filters, "full")))
            {
                surface = GetSurface.Brief;
                return BriefCacheKey;
            }

            if (!hasNarrowingFilter && ResolveWireVersion(filters) == WireV1)
            {
                surface = GetSurface.FullV1;
                return manifestType + "|__full__";
            }

            surface = GetSurface.Keyed;
            // ?full is stripped from the key, ?wire is not. Once a request reaches Keyed, ?full's one job
            // (defeating the brief default in the branch above) is already done, and it can no longer affect the bytes -- keeping it would only split the same payload
            // into two several-hundred-KB entries with identical content (/skills/schema?wire=v2 and ?full=1&wire=v2).
            // ?wire, on the other hand, really does select different bytes, and must stay in the identifier.
            return BuildFilteredOutputCacheKey(StripFullFlagKey(filters), manifestType);
        }

        /// <summary>
        /// Strips only the <c>full</c> key, keeping the rest in insertion order. Like
        /// <see cref="StripSurfaceSelectionKeys"/>, returns the argument instance directly when there's nothing to change.
        /// </summary>
        private static Dictionary<string, string> StripFullFlagKey(Dictionary<string, string> filters)
        {
            if (filters.Count == 0 || !filters.ContainsKey("full"))
                return filters;

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in filters)
            {
                if (!string.Equals(kv.Key, "full", StringComparison.OrdinalIgnoreCase))
                    result[kv.Key] = kv.Value;
            }
            return result;
        }

        // Valid values for ?category= / ?operation=. Stored rather than recomputed each time, because Enum.GetNames allocates a new array
        // on every call, and a manifest GET that misses the cache reads them every time;
        // they're also the list handed back to the caller in a rejection response.
        private static readonly string[] _validCategoryNames = Enum.GetNames(typeof(SkillCategory));
        private static readonly string[] _validOperationNames = Enum.GetNames(typeof(SkillOperation));

        /// <summary>
        /// Rejects unknown <c>?category=</c> / <c>?operation=</c> values, and a blank narrowing key,
        /// rather than silently filtering with it.
        ///
        /// <para>Both filters used to silently "fail closed": an unrecognized category can never equal any
        /// <c>Category.ToString()</c>, and an unparseable operation makes every <c>Enum.TryParse</c> below fail --
        /// so the answer is 200 plus <c>skills: []</c>, byte-for-byte identical to "this category is genuinely
        /// empty under the current surface profile." An agent reading that concludes the module doesn't exist in this project and stops looking,
        /// when it actually just typo'd <c>?category=GameObjects</c>.</para>
        ///
        /// <para>Must run before <see cref="ResolveGetSurface"/>. category/operation are narrowing keys,
        /// so a bad value would fall into <see cref="GetSurface.Keyed"/>, and mint -- then permanently hold --
        /// a manifest-sized cache entry keyed on that typo.</para>
        ///
        /// <para>Returns null when every value present is acceptable, including when there's no narrowing key at all.
        /// Never rejects a value the filters below would actually match, so no legitimate query's bytes ever change.</para>
        /// </summary>
        private static string ValidateNarrowingFilterValues(Dictionary<string, string> filters)
        {
            var invalidKey = FindInvalidNarrowingFilterKey(filters);
            if (invalidKey == null)
                return null;

            var value = filters[invalidKey];

            object details;
            if (string.Equals(invalidKey, "category", StringComparison.OrdinalIgnoreCase))
                details = new { parameter = invalidKey, value, validCategories = _validCategoryNames };
            else if (string.Equals(invalidKey, "operation", StringComparison.OrdinalIgnoreCase))
                details = new { parameter = invalidKey, value, validOperations = _validOperationNames };
            else
                details = new
                {
                    parameter = invalidKey,
                    value,
                    hint = $"'{invalidKey}' was written with no value. Give it one or drop the key entirely — a blank is neither an omission nor a usable value, and answering as if the key were absent is what let a mistyped query look like it worked.",
                };

            return SkillErrorResponse.Build(
                SkillErrorCode.SemanticInvalid,
                $"Invalid value '{value}' for parameter '{invalidKey}'.",
                details: details,
                retryStrategy: SkillErrorResponse.RetryFixAndRetry);
        }

        /// <summary>
        /// Returns the narrowing key whose value the filters below can't use; returns null when every value is acceptable.
        /// Split out of <see cref="ValidateNarrowingFilterValues"/> so the HTTP thread's fast path can ask
        /// the same question: it never touches the Unity API, never logs, never calls Initialize() -- exactly what the fast-path zone's cross-thread contract requires.
        /// The fast path only needs to know "should this query be rejected," never the error body, so building the payload still happens on the main thread.
        ///
        /// Every check here must be *exactly identical* to what the corresponding filter does in <see cref="BuildFilteredOutput"/>,
        /// and never stricter: rejecting a value here that the filter would actually match would turn a normal 200 into a 400.
        /// </summary>
        private static string FindInvalidNarrowingFilterKey(Dictionary<string, string> filters)
        {
            if (filters.Count == 0)
                return null;

            if (filters.TryGetValue("category", out var category) &&
                !_validCategoryNames.Contains(category, StringComparer.OrdinalIgnoreCase))
                return "category";

            // Uses Enum.TryParse rather than "is the name in the list": SkillOperation is [Flags],
            // the filter accepts a comma list ("Query,Modify" -- matching a skill that declares both) and numeric literals,
            // and checking against a name list would reject exactly those two forms.
            if (filters.TryGetValue("operation", out var operation) &&
                !Enum.TryParse<SkillOperation>(operation, true, out _))
                return "operation";

            // A key written with no value ("?tags=", "?summary=") is now preserved by ParseQueryString rather than dropped,
            // and none of these keys has a meaningful reading of "empty": a narrowing key would become a filter condition that matches nothing,
            // while a shape key would fall back to the very default the caller meant to override. Either answer would leave the caller believing the key took effect,
            // so it's rejected outright. category/operation don't need to be listed -- an empty string isn't a member of either word list,
            // and the two checks above already catch them.
            foreach (var key in _blankRejectingFilterKeys)
            {
                if (filters.TryGetValue(key, out var value) && string.IsNullOrWhiteSpace(value))
                    return key;
            }

            return null;
        }

        // All recognized query keys, in a *fixed* order, so a query with multiple blank values always names the same key on rejection --
        // the error body, like any other cached response, must be byte-stable for the same query. Keep in sync with _recognizedFilterKeys.
        private static readonly string[] _blankRejectingFilterKeys =
        {
            "category", "operation", "tags", "readonly", "q", "summary", "includeSchema", "brief",
            "wire", "full"
        };

        private static string BuildFilteredOutput(string queryString, string manifestType, out bool isError)
        {
            Initialize();
            isError = false;
            var filters = StripUnrecognizedFilterKeys(ParseQueryString(queryString));

            // Placed before ResolveGetSurface, so an unknown value can never become a cache key; also placed before the brief/meta branches,
            // otherwise a query that should be rejected would get a perfectly legitimate catalog. The HTTP fast path asks
            // the same question via FindInvalidNarrowingFilterKey and voluntarily steps aside,
            // so it never hands out _cachedBrief for a query that would return an error here.
            var filterValueError = ValidateNarrowingFilterValues(filters);
            if (filterValueError != null)
            {
                isError = true;
                return filterValueError;
            }

            string cacheKey = ResolveGetSurface(manifestType, filters, out var surface);

            switch (surface)
            {
                case GetSurface.Meta:
                    return GetMeta();
                // ?brief=1 (or ?brief=true), and now bare GET /skills too -> the catalog layer: skill names grouped by
                // category, without descriptions or parameter schemas (roughly 19KB, versus roughly 139KB for summary / roughly 618KB for full).
                // Takes priority over summary/category and other filters (which are ignored), to keep the semantics minimal:
                // locate the module first, then pull the exact signature via GET /skills/schema?category=<Category>.
                case GetSurface.Brief:
                    return GetBrief();
                case GetSurface.FullV1:
                    return manifestType == "schema" ? GetSchema() : GetManifest();
            }

            // Before Refresh(), filtered output is byte-for-byte deterministic for the same query; caching it means
            // a repeated scoped fetch (?category=...) doesn't have to rebuild and re-serialize every skill each time.
            if (_filteredOutputCache.TryGetValue(cacheKey, out var cachedOutput))
                return cachedOutput;

            IEnumerable<SkillInfo> filtered = VisibleSkills();

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

            // ?summary=1 (or ?includeSchema=false, consistent with the /skills/recommend convention)
            // -> a lightweight cognitive manifest: omits parameter schemas, truncates descriptions.
            bool summary = filters.TryGetValue("summary", out var sumVal) &&
                (sumVal == "1" || sumVal.Equals("true", StringComparison.OrdinalIgnoreCase));
            if (!summary && filters.TryGetValue("includeSchema", out var incVal) &&
                (incVal == "0" || incVal.Equals("false", StringComparison.OrdinalIgnoreCase)))
                summary = true;

            // Only keys that actually narrow the scope are echoed back as `filters` and counted toward `filtered`;
            // a ?wire=v2 or ?full=1 request that narrows nothing reports filtered:false.
            // For every pre-v2 query, this is the same dictionary instance as before, so the bytes match.
            var narrowingFilters = StripSurfaceSelectionKeys(filters);
            bool isFiltered = narrowingFilters.Count > 0;
            int wire = ResolveWireVersion(filters);

            var manifest = BuildManifest(results, isFiltered, isFiltered ? narrowingFilters : null, manifestType, summary, wire);
            var json = JsonConvert.SerializeObject(manifest, wire == WireV2 ? _jsonSettingsV2 : _jsonSettings);
            if (_filteredOutputCache.Count >= MaxCacheEntries) _filteredOutputCache.Clear();
            _filteredOutputCache[cacheKey] = json;
            return json;
        }

        private static string BuildFilteredOutputCacheKey(Dictionary<string, string> filters, string manifestType)
        {
            // Normalizes and lowercases the keys. Every filter comparison in BuildFilteredOutput is case-insensitive
            // (category/tags/readonly use OrdinalIgnoreCase, operation's TryParse passes ignoreCase=true,
            // q goes through ToLowerInvariant), so lowercasing both keys and values together converges equivalent queries
            // (?category=GameObject and ?Category=gameobject) onto the same cache entry.
            var parts = filters.Keys
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(k => $"{k.ToLowerInvariant()}={(filters[k] ?? string.Empty).ToLowerInvariant()}");
            return manifestType + "|" + string.Join("|", parts);
        }

        private static bool ContainsParameter(IEnumerable<string> parameterNames, string parameterName)
        {
            return parameterNames != null &&
                parameterNames.Any(name => string.Equals(name, parameterName, StringComparison.OrdinalIgnoreCase));
        }

        private static bool SupportsSyntheticEntityId(string[] parameterNames)
        {
            return !ContainsParameter(parameterNames, EntityIdParameterName) &&
                ContainsParameter(parameterNames, "instanceId") &&
                (_entityIdPathFallbackParameters.Any(name => ContainsParameter(parameterNames, name)) ||
                 _entityIdNameFallbackParameters.Any(name => ContainsParameter(parameterNames, name)));
        }

        private static bool ShouldExposeSyntheticEntityId(SkillInfo skill)
        {
            return skill != null &&
                !ContainsParameter(skill.ParameterNames, EntityIdParameterName) &&
                skill.AllowedParameterSet != null &&
                skill.AllowedParameterSet.Contains(EntityIdParameterName);
        }

        private static string[] GetEffectiveParameterNames(SkillInfo skill)
        {
            if (skill?.ParameterNames == null)
                return Array.Empty<string>();

            if (!ShouldExposeSyntheticEntityId(skill))
                return skill.ParameterNames;

            return skill.ParameterNames
                .Concat(new[] { EntityIdParameterName })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>
        /// Whether the skill itself declares a parameter with this name. Such a name must reach it as its own parameter,
        /// and must not be swallowed by the envelope layer as a pagination parameter.
        /// </summary>
        private static bool SkillDeclaresParameter(SkillInfo skill, string parameterName) =>
            skill != null && ContainsParameter(skill.ParameterNames, parameterName);

        /// <summary>
        /// Reads an envelope-layer pagination parameter ('offset'/'limit') as an integer no smaller than minValue.
        /// Also accepts both a JSON number and its string form ("10"), so a caller going through a query string also works.
        /// </summary>
        private static bool TryReadPagingArg(JToken token, string parameterName, int minValue, out int value, out string error)
        {
            value = 0;
            error = null;

            var raw = token.Type == JTokenType.Integer
                ? token.ToString(Formatting.None)
                : token.Type == JTokenType.String ? token.Value<string>()?.Trim() : null;
            if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                error = $"Parameter '{parameterName}' must be an integer, got: {token.ToString(Formatting.None)}";
                return false;
            }

            if (parsed < minValue)
            {
                error = minValue <= 0
                    ? $"Parameter '{parameterName}' must be a non-negative integer, got: {parsed}"
                    : $"Parameter '{parameterName}' must be a positive integer, got: {parsed}";
                return false;
            }

            value = parsed;
            return true;
        }

        private static JArray FindPageArray(JToken result, out string propertyName)
        {
            propertyName = null;
            if (result is JArray array)
                return array;
            if (!(result is JObject obj))
                return null;

            foreach (var name in new[] { "items", "assets", "objects", "groups", "entries" })
            {
                if (obj[name] is JArray nested)
                {
                    propertyName = name;
                    return nested;
                }
            }
            return null;
        }

        /// <summary>
        /// Rolls back the workflow/undo bookkeeping opened before <c>Method.Invoke</c>, for when an envelope-layer parameter
        /// is judged invalid before anything has executed. Matches the cleanup each catch does in Execute.
        /// </summary>
        private static void UnwindBeforeInvoke(bool autoStartedWorkflow, int workflowSnapshotCountBefore, int undoGroup)
        {
            if (autoStartedWorkflow && WorkflowManager.IsRecording)
                WorkflowManager.AbortTask();
            else if (WorkflowManager.IsRecording)
                WorkflowManager.TruncateCurrentTask(workflowSnapshotCountBefore);

            if (undoGroup >= 0)
                UnityEditor.Undo.RevertAllInCurrentGroup();
        }

        private static object[] BuildParameterSchema(SkillInfo skill)
        {
            if (skill == null)
                return Array.Empty<object>();

            var parameters = skill.Parameters.Select(p => (object)new
            {
                name = p.Name,
                type = GetJsonType(p.ParameterType),
                required = IsParameterRequired(skill, p),
                defaultValue = p.HasDefaultValue ? p.DefaultValue?.ToString() : null
            }).ToList();

            if (ShouldExposeSyntheticEntityId(skill))
            {
                parameters.Add(new
                {
                    name = EntityIdParameterName,
                    type = "string",
                    required = false,
                    defaultValue = (string)null
                });
            }

            return parameters.ToArray();
        }

        // internal: /skills/batch's dry-run uses this to structurally validate $ref paths against the referenced skill's declared outputs
        // (including the synthesized entityId).
        internal static string[] GetEffectiveOutputs(SkillInfo skill)
        {
            if (skill?.Outputs == null)
                return null;

            if (!skill.Outputs.Any(output => string.Equals(output, "instanceId", StringComparison.OrdinalIgnoreCase)) ||
                skill.Outputs.Any(output => string.Equals(output, EntityIdParameterName, StringComparison.OrdinalIgnoreCase)))
            {
                return skill.Outputs;
            }

            return skill.Outputs
                .Concat(new[] { EntityIdParameterName })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string GetEffectiveDescription(SkillInfo skill)
        {
            var description = skill?.Description ?? string.Empty;
            if (!ShouldExposeSyntheticEntityId(skill))
                return description;

            return description
                .Replace("name/instanceId/path", "name/entityId/instanceId/path")
                .Replace("name, instanceId, or path", "name, entityId, instanceId, or path")
                .Replace("name / instanceId / path", "name / entityId / instanceId / path");
        }

        private static object BuildManifest(IEnumerable<SkillInfo> skills, bool filtered, Dictionary<string, string> filters, string manifestType, bool summary = false, int wire = WireV1)
        {
            var skillArray = skills
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (wire == WireV2)
                return BuildManifestV2(skillArray, filtered, filters, manifestType, summary);

            return new
            {
                manifestType,
                schemaVersion = SkillSchemaVersion,
                version = SkillsLogger.Version,
                unityVersion = Application.unityVersion,
                totalSkills = skillArray.Length,
                filtered,
                filters,
                summary,
                summaryHint = summary
                    ? SummaryHintText
                    : null,
                categories = Enum.GetNames(typeof(SkillCategory)).Where(c => c != "Uncategorized").ToArray(),
                operationTypes = Enum.GetNames(typeof(SkillOperation)),
                reservedBodyParameters = _reservedBodyParameters.OrderBy(x => x).ToArray(),
                // Filtered by profile, not by query: this block is an envelope constant, so a scoped ?category=
                // fetch must still list every externally-offered tracked skill (narrowing it to the current page would change the v1 bytes
                // of every scoped query). See VisibleWorkflowTrackedSkills -- under the default profile it's the full set.
                workflowTrackedSkills = VisibleWorkflowTrackedSkills(),
                skills = summary
                    ? skillArray.Select(s => (object)new
                    {
                        name = s.Name,
                        description = GetEffectiveDescription(s),
                        category = s.Category != SkillCategory.Uncategorized ? s.Category.ToString() : null,
                        operation = FormatOperation(s.Operation),
                        riskLevel = s.RiskLevel
                    })
                    : skillArray.Select(s => (object)new
                    {
                        name = s.Name,
                        description = GetEffectiveDescription(s),
                        category = s.Category != SkillCategory.Uncategorized ? s.Category.ToString() : null,
                        operation = FormatOperation(s.Operation),
                        tags = s.Tags,
                        outputs = GetEffectiveOutputs(s),
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
                        mode = SkillsModeManager.SkillModeToWire(s.Mode),
                        approvalBehavior = SkillsModeManager.ApprovalBehaviorForSkill(s),
                        parameters = BuildParameterSchema(s)
                    })
            };
        }

        // Shared between the v1 and v2 envelopes, so the two can never drift apart.
        private const string SummaryHintText = "AWARENESS ONLY — parameter schemas are omitted and descriptions are informal (human-written; some omit parameter hints entirely), not a formal signature. Before executing any skill listed here, validate its parameters with ?mode=dryRun (the server returns unknownParam suggestions + the full parameter schema) or fetch its scoped schema GET /skills/schema?category=<Category>. Do NOT guess parameters from descriptions alone.";

        internal const string MetaEndpointPath = "/skills/meta";

        // The one riskLevel value a v2 entry omits. Compared with Ordinal (not IgnoreCase),
        // so any other spelling passes through unchanged rather than being silently normalized away.
        private const string DefaultRiskLevel = "low";

        /// <summary>
        /// The per-skill values that <c>?wire=v2</c> omits from an entry, declared here once, centrally.
        /// Both the v2 envelope and <see cref="GetMeta"/> output it, and the way it's constructed guarantees the two are always identical --
        /// this block alone is what makes those omissions reversible.
        /// </summary>
        private static object BuildWireDefaults() => new
        {
            riskLevel = DefaultRiskLevel,
            supportsDryRun = true
        };

        /// <summary>
        /// <c>?wire=v2</c> envelope. Every difference from v1 is a subtraction:
        /// <list type="bullet">
        /// <item>Four session-constant blocks (categories / operationTypes / reservedBodyParameters /
        /// workflowTrackedSkills) give way to <c>metaUrl</c> -- fetch
        /// <see cref="MetaEndpointPath"/> once, no need to pay for them on every scoped fetch;</item>
        /// <item>Six impact booleans plus longRunning collapse into <c>flags</c>, listing only the ones that are true;</item>
        /// <item><c>riskLevel</c> appears only at a non-default value, <c>supportsDryRun</c> appears only when false,
        /// and <c>defaults</c> states what each omission means;</item>
        /// <item>Null members disappear entirely (serialized with <c>_jsonSettingsV2</c>).</item>
        /// </list>
        /// <c>approvalBehavior</c> is deliberately kept on every entry: it's the one field an agent must know
        /// before judging "will this call actually be allowed," and reverse-deriving it from mode + flags is exactly the guessing this payload exists to eliminate.
        /// </summary>
        private static object BuildManifestV2(SkillInfo[] skillArray, bool filtered, Dictionary<string, string> filters, string manifestType, bool summary)
        {
            return new
            {
                manifestType,
                schemaVersion = SkillSchemaVersion,
                wire = "v2",
                version = SkillsLogger.Version,
                unityVersion = Application.unityVersion,
                totalSkills = skillArray.Length,
                filtered,
                filters,
                summary,
                summaryHint = summary
                    ? SummaryHintText
                    : null,
                metaUrl = MetaEndpointPath,
                defaults = BuildWireDefaults(),
                skills = summary
                    ? skillArray.Select(s => (object)new
                    {
                        name = s.Name,
                        description = GetEffectiveDescription(s),
                        category = s.Category != SkillCategory.Uncategorized ? s.Category.ToString() : null,
                        operation = FormatOperation(s.Operation),
                        // Even though v1's summary carries neither, flags and supportsDryRun still need to be carried here.
                        // `defaults` appears in every v2 payload, and it states that "a flag's absence means false" --
                        // so a summary entry missing them isn't read as "impact unknown", it's read as
                        // "this skill changes nothing, and dry-run works fine." Omitting them here
                        // would mean every summary entry asserts the exact opposite fact for 784 skills.
                        // All v2 surfaces share one contract.
                        flags = BuildSkillFlags(s),
                        riskLevel = NonDefaultRiskLevel(s),
                        supportsDryRun = s.SupportsDryRun ? (bool?)null : false
                    })
                    : skillArray.Select(s => (object)new
                    {
                        name = s.Name,
                        description = GetEffectiveDescription(s),
                        category = s.Category != SkillCategory.Uncategorized ? s.Category.ToString() : null,
                        operation = FormatOperation(s.Operation),
                        tags = s.Tags,
                        outputs = GetEffectiveOutputs(s),
                        requiresInput = s.RequiresInput,
                        flags = BuildSkillFlags(s),
                        riskLevel = NonDefaultRiskLevel(s),
                        supportsDryRun = s.SupportsDryRun ? (bool?)null : false,
                        requiresPackages = s.RequiresPackages,
                        mode = SkillsModeManager.SkillModeToWire(s.Mode),
                        approvalBehavior = SkillsModeManager.ApprovalBehaviorForSkill(s),
                        parameters = BuildParameterSchema(s)
                    })
            };
        }

        private static string NonDefaultRiskLevel(SkillInfo s) =>
            string.Equals(s.RiskLevel, DefaultRiskLevel, StringComparison.Ordinal) ? null : s.RiskLevel;

        /// <summary>
        /// Replaces the six impact booleans plus longRunning in v2: lists only the flags that are set, in a fixed order to keep payload bytes stable.
        /// Null when none are set (and therefore omitted); a flag's absence from the array means false.
        /// </summary>
        private static string[] BuildSkillFlags(SkillInfo s)
        {
            var flags = new List<string>(7);
            if (s.ReadOnly) flags.Add("readOnly");
            if (s.TracksWorkflow) flags.Add("tracksWorkflow");
            if (s.MutatesScene) flags.Add("mutatesScene");
            if (s.MutatesAssets) flags.Add("mutatesAssets");
            if (s.MayTriggerReload) flags.Add("mayTriggerReload");
            if (s.MayEnterPlayMode) flags.Add("mayEnterPlayMode");
            if (s.LongRunning) flags.Add("longRunning");
            return flags.Count > 0 ? flags.ToArray() : null;
        }

        /// <summary>
        /// The catalog-layer manifest -- what bare <c>GET /skills</c> (and <c>?brief=1</c>) returns:
        /// skill names grouped by category, nothing else. Both module keys and names are sorted, so the payload bytes are stable for the same skill set,
        /// which is why the cached string (and its fast-path ETag) stays valid until Refresh().
        /// </summary>
        private static object BuildBriefManifest()
        {
            var modules = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            int visibleCount = 0;
            foreach (var s in VisibleSkills())
            {
                var category = s.Category.ToString();
                if (!modules.TryGetValue(category, out var names))
                    modules[category] = names = new List<string>();
                names.Add(s.Name);
                visibleCount++;
            }
            foreach (var names in modules.Values)
                names.Sort(StringComparer.OrdinalIgnoreCase);

            return new
            {
                manifestType = "brief",
                schemaVersion = SkillSchemaVersion,
                version = SkillsLogger.Version,
                // Reports the count actually listed in this payload. Under a non-full surfaceProfile it's smaller than the registry's total --
                // reporting the registry's total count here would send the agent looking for names that don't exist in the catalog.
                totalSkills = visibleCount,
                briefHint = "DIRECTORY ONLY — names + categories, no descriptions or parameters. This is the default answer for GET /skills. Locate the module(s) you need, then fetch exact signatures via GET /skills/schema?category=<Category>, and always dryRun before first execution. If a name is ambiguous, fall back to GET /skills?summary=1 (full descriptions) or GET /skills/recommend?intent=... The complete manifest is still available at GET /skills?full=1 (~618KB — add &wire=v2 to cut it down), and session constants live at GET /skills/meta.",
                modules
            };
        }

        // ========== Skill recommendations ==========

        /// <summary>
        /// Intent-based skill recommendation. Scores by keyword matches against name (3 points), tags (2 points), and description (1 point),
        /// returning the top N results.
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
            int wire = ResolveWireVersion(filters);

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
            var healthBySkill = SkillTelemetryService.GetRecommendationHealth();
            var scored = new List<(SkillInfo skill, int score, int semanticScore, List<string> matchedOn, SkillTelemetryService.RecommendationHealth health)>();

            // Precomputes operation and category matches (supports Chinese substrings)
            var matchedOps = ExtractOperations(rawKeywords);
            var matchedCats = ExtractCategories(rawKeywords);

            // Input for intent alignment (see ApplyIntentAlignment). Drawn from the raw intent words rather than the synonym-expanded set:
            // expansion exists to loosen keyword matching, and letting it decide "does the caller want to observe or to change"
            // would count verbs the caller never wrote (材质 -> material, hierarchy -> parent/child/gameobject).
            bool readIntent = rawKeywords.Any(_readIntentVerbs.Contains);
            bool writeIntent = rawKeywords.Any(_writeIntentVerbs.Contains);
            bool sampleIntent = rawKeywords.Any(_sampleIntentWords.Contains);
            // null while the package list is still refreshing asynchronously -- for why that means "skip the check" rather than "go find out,"
            // see HasUninstalledPackage.
            var packageCache = PackageManagerHelper.InstalledPackages != null
                ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                : null;

            foreach (var s in VisibleSkills())
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

                // category bonus
                if (matchedCats.Count > 0 && s.Category != SkillCategory.Uncategorized && matchedCats.Contains(s.Category))
                {
                    score += 2;
                    matchedOn.Add($"category:{s.Category}");
                }

                // operation bonus
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
                {
                    // Only adjusts skills that already matched something. Applying the read-intent bonus to zero-score skills
                    // would pull every read-only skill in the registry into the results based on intent alone.
                    score = ApplyIntentAlignment(s, score, readIntent, writeIntent, sampleIntent, packageCache, matchedOn);
                    healthBySkill.TryGetValue(s.Name, out var health);
                    var adjustedScore = Math.Max(1, score - (health?.Penalty ?? 0));
                    scored.Add((s, adjustedScore, score, matchedOn, health));
                }
            }

            var results = scored.OrderByDescending(x => x.score)
                .ThenByDescending(x => x.semanticScore)
                // Stable tie-breaking. Without it, same-score skills would come out in reflection discovery order,
                // and that order differs across projects and across domain reloads --
                // the same intent would rank the same candidates differently for no reason.
                .ThenBy(x => x.skill.Name, StringComparer.Ordinal)
                .Take(topN).ToList();
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
                    description = GetEffectiveDescription(x.skill),
                    category = x.skill.Category != SkillCategory.Uncategorized ? x.skill.Category.ToString() : null,
                    score = x.score,
                    semanticScore = x.semanticScore,
                    confidence = ScoreToConfidence(x.score),
                    matchedOn = x.matchedOn.Distinct().ToArray(),
                    telemetry = x.health == null ? null : new
                    {
                        window = "7d",
                        calls = x.health.Calls,
                        errors = x.health.Errors,
                        errorRate = x.health.ErrorRate,
                        avgMs = x.health.AvgMs,
                    },
                    telemetryPenalty = x.health?.Penalty ?? 0,
                    warnings = x.health != null && x.health.Warnings.Length > 0 ? x.health.Warnings : null,
                    schema = includeSchema
                        ? (wire == WireV2 ? BuildSkillSchemaForRecommendV2(x.skill) : BuildSkillSchemaForRecommend(x.skill))
                        : null
                })
            };

            if (wire == WireV2)
            {
                // v2's recommend keeps the same envelope, only reshaping the per-skill schema, so it's described by the same
                // `flags` / `defaults` contract as the manifest. Declared explicitly here rather than left implicit:
                // a caller that requested v2 but silently got v1 would read a missing `flags` array as "no flags set" --
                // treating a skill that mutates something as harmless -- and this echo exists to make that misreading impossible.
                return JsonConvert.SerializeObject(new
                {
                    response.intent,
                    response.expandedKeywords,
                    response.topN,
                    response.includeSchema,
                    response.totalMatches,
                    wire = "v2",
                    metaUrl = MetaEndpointPath,
                    defaults = BuildWireDefaults(),
                    // Null under `full`, and v2 drops nulls -- so under the default profile it costs nothing.
                    // See SurfaceProfilePrunedHint for why a ranking-style endpoint must state this.
                    surfaceProfile = SkillsSurfaceProfile.IsFull ? null : SkillsSurfaceProfile.CurrentWire,
                    surfaceProfileHint = SkillsSurfaceProfile.IsFull ? null : SurfaceProfilePrunedHint,
                    response.results
                }, _jsonSettingsV2);
            }

            // The scoring stage already skipped hidden skills, so a non-full profile silently shortens this ranking.
            // Same rationale as the chain envelope, and the same byte-stability branch: v1 serialization writes out null,
            // so `full` must never touch these extra fields.
            if (!SkillsSurfaceProfile.IsFull)
            {
                return JsonConvert.SerializeObject(new
                {
                    response.intent,
                    response.expandedKeywords,
                    response.topN,
                    response.includeSchema,
                    response.totalMatches,
                    surfaceProfile = SkillsSurfaceProfile.CurrentWire,
                    surfaceProfileHint = SurfaceProfilePrunedHint,
                    response.results
                }, _jsonSettings);
            }

            return JsonConvert.SerializeObject(response, _jsonSettings);
        }

        // Verbs used to judge whether the caller wants to observe or to change something. Matched only against the raw intent words (GetRecommendations),
        // never against the synonym-expanded set.
        private static readonly HashSet<string> _readIntentVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "get", "read", "inspect", "list", "find", "query", "show", "what", "which"
        };

        private static readonly HashSet<string> _writeIntentVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "set", "create", "add", "delete", "remove", "assign", "apply",
            "build", "bake", "make", "change", "modify", "rename", "move"
        };

        private static readonly HashSet<string> _sampleIntentWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sample", "demo", "example"
        };

        /// <summary>
        /// Three corrections applied on top of the keyword score, each addressing an observed bad ranking:
        ///
        /// <list type="bullet">
        /// <item><b>Read/write intent alignment.</b> "read current camera properties inspect fov" once ranked
        /// camera_set_properties above camera_get_properties -- a setter's description inevitably mentions the properties a getter
        /// returns, and happens to mention them more. Now, an intent that's unambiguously "read" in shape favors read-only skills,
        /// and one that's unambiguously "write" pushes them down. Mixed or verb-less intents are left untouched: guessing there is worse than not.</item>
        /// <item><b>Demoting Sample.</b> Skills under <see cref="SkillCategory.Sample"/>
        /// (create_cube, set_object_position, ...) are teaching duplicates of the real gameobject_* / camera_* skills,
        /// and their short names reliably win the name-substring bonus -- when an agent wants to move an object,
        /// set_object_position would outrank gameobject_set_transform. They're still reachable,
        /// but only enter the ranking when sample/demo/example genuinely appears in the intent.</item>
        /// <item><b>Uninstalled optional packages.</b> Recommending yooasset_* / probuilder_* for an ordinary material edit
        /// is worse than not recommending it: the skill is registered, so nothing warns the agent before the call fails for a missing package.</item>
        /// </list>
        ///
        /// <para>Deliberately does not rewrite anything. Keyword weights (name 3 / tag 2 / desc 1), category and operation bonuses,
        /// the telemetry penalty, and the sort key are all left untouched. Every adjustment is appended to <c>matchedOn</c>,
        /// so an unexpected ranking can be audited from the response alone; the result floor is 1,
        /// so no adjustment can strike a genuine keyword hit out of <c>totalMatches</c> -- it can only push it to the bottom.</para>
        /// </summary>
        private static int ApplyIntentAlignment(
            SkillInfo skill,
            int score,
            bool readIntent,
            bool writeIntent,
            bool sampleIntent,
            Dictionary<string, bool> packageCache,
            List<string> matchedOn)
        {
            int delta = 0;

            // readIntent != writeIntent means "exactly one holds," i.e. the unambiguous cases.
            if (skill.ReadOnly && readIntent != writeIntent)
            {
                if (readIntent)
                {
                    delta += 3;
                    matchedOn.Add("intent:read+3");
                }
                else
                {
                    delta -= 1;
                    matchedOn.Add("intent:write-1");
                }
            }

            if (!sampleIntent && skill.Category == SkillCategory.Sample)
            {
                delta -= 3;
                matchedOn.Add("demoted:sample-3");
            }

            if (HasUninstalledPackage(skill, packageCache))
            {
                delta -= 5;
                matchedOn.Add("demoted:packageMissing-5");
            }

            return delta == 0 ? score : Math.Max(1, score + delta);
        }

        /// <summary>
        /// Whether this skill names an optional package that isn't installed yet. The mechanism matches the smoke test's skip gate
        /// (<c>TestSkills.EvaluateSmokeSkill</c>), including its empty-cache guard:
        /// <paramref name="packageCache"/> being null means
        /// <see cref="PackageManagerHelper.InstalledPackages"/>'s async refresh hasn't finished yet,
        /// and at that point the scorer would rather *demote no candidate at all* than answer based on a package list that doesn't exist yet --
        /// reading "don't know yet" as "not installed" would suppress every optional-package skill during the first few seconds of a session.
        ///
        /// This guard exists for correctness, not to save work. <c>IsPackageInstalled</c>'s miss path is
        /// <c>ResolveDirectly</c> -> <c>PackageInfo.FindForAssetPath("Packages/&lt;id&gt;")</c>,
        /// an in-memory registry lookup rather than a Package Manager client request -- so a single id is cheap enough,
        /// and <paramref name="packageCache"/> memoizes the result for the rest of *this request*,
        /// so a package shared by twenty skills is only resolved once. The cache is deliberately scoped per request:
        /// a longer-lived cache would keep answering "missing" even after the user installed the package.
        /// </summary>
        private static bool HasUninstalledPackage(SkillInfo skill, Dictionary<string, bool> packageCache)
        {
            if (packageCache == null || skill.RequiresPackages == null || skill.RequiresPackages.Length == 0)
                return false;

            foreach (var packageId in skill.RequiresPackages)
            {
                if (string.IsNullOrWhiteSpace(packageId))
                    continue;

                if (!packageCache.TryGetValue(packageId, out var installed))
                {
                    installed = PackageManagerHelper.IsPackageInstalled(packageId);
                    packageCache[packageId] = installed;
                }

                if (!installed)
                    return true;
            }

            return false;
        }

        private static string ScoreToConfidence(int score)
        {
            if (score >= 10) return "high";
            if (score >= 5) return "medium";
            return "low";
        }

        private static object BuildSkillSchemaForRecommend(SkillInfo s) => new
        {
            parameters = BuildParameterSchema(s),
            outputs = GetEffectiveOutputs(s),
            requiresInput = s.RequiresInput,
            tags = s.Tags,
            operation = FormatOperation(s.Operation),
            riskLevel = s.RiskLevel,
            readOnly = s.ReadOnly,
            mutatesScene = s.MutatesScene,
            mutatesAssets = s.MutatesAssets,
            requiresPackages = s.RequiresPackages,
            mode = SkillsModeManager.SkillModeToWire(s.Mode),
            approvalBehavior = SkillsModeManager.ApprovalBehaviorForSkill(s),
        };

        /// <summary>
        /// The <c>?wire=v2</c> form of <see cref="BuildSkillSchemaForRecommend"/>: uses the same <c>flags</c> array and the same
        /// "omit the default" rule as a v2 manifest entry, so an agent only ever has to parse one shape across every endpoint.
        /// Note it outputs all seven flags, while v1 only carries three booleans (readOnly / mutatesScene / mutatesAssets) --
        /// strictly more information in fewer bytes; nothing v1 reported is lost.
        /// </summary>
        private static object BuildSkillSchemaForRecommendV2(SkillInfo s) => new
        {
            parameters = BuildParameterSchema(s),
            outputs = GetEffectiveOutputs(s),
            requiresInput = s.RequiresInput,
            tags = s.Tags,
            operation = FormatOperation(s.Operation),
            flags = BuildSkillFlags(s),
            riskLevel = NonDefaultRiskLevel(s),
            requiresPackages = s.RequiresPackages,
            mode = SkillsModeManager.SkillModeToWire(s.Mode),
            approvalBehavior = SkillsModeManager.ApprovalBehaviorForSkill(s),
        };

        // ========== Skill dependency chain ==========

        /// <summary>
        /// Builds an operation chain via BFS along the Outputs -> RequiresInput relationship.
        /// Given a target output field, finds every skill that produces it and its dependencies.
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

            // BFS: first find the skills that produce the target field, then trace back through their RequiresInput
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
                    // _outputIndex is a complete index over the entire registry, so filtering happens here rather than at build time:
                    // naming a skill the current profile hides would send the agent down a chain whose first step answers
                    // SURFACE_EXCLUDED. An excluded producer is skipped entirely -- its RequiresInput fields
                    // are not enqueued either, because a step that can't run can't be part of the plan.
                    if (SkillsSurfaceProfile.IsExcluded(s))
                        continue;

                    producers.Add(new
                    {
                        skill = s.Name,
                        description = GetEffectiveDescription(s),
                        category = s.Category != SkillCategory.Uncategorized ? s.Category.ToString() : null,
                        depth,
                        producesField = field,
                        outputs = GetEffectiveOutputs(s),
                        requiresInput = s.RequiresInput
                    });

                    // Enqueues the RequiresInput fields, for use at the next depth level
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

            // Under `full`, nothing was trimmed and the payload is byte-for-byte identical to v1. Under a non-full profile, the producers list above
            // has already silently dropped some steps, and this envelope is the only place that can explain it: otherwise a shortened chain would be read as
            // "Unity has no way to produce this field," and the agent would report something impossible when the skill was actually just hidden.
            // Note this envelope serializes with _jsonSettings, which writes out null -- hence the branch here rather than a field that's simply null.
            if (SkillsSurfaceProfile.IsFull)
            {
                return JsonConvert.SerializeObject(new
                {
                    targetOutput,
                    maxDepth,
                    totalProducers = producers.Count,
                    producers
                }, _jsonSettings);
            }

            return JsonConvert.SerializeObject(new
            {
                targetOutput,
                maxDepth,
                totalProducers = producers.Count,
                surfaceProfile = SkillsSurfaceProfile.CurrentWire,
                surfaceProfileHint = SurfaceProfilePrunedHint,
                producers
            }, _jsonSettings);
        }

        /// <summary>
        /// Attached to discovery envelopes that "silently return fewer skills under a non-full surface profile"
        /// (<c>/skills/recommend</c>, <c>/skills/chain</c>). Both are ranking and traversal rather than enumeration,
        /// so a trimmed result is indistinguishable from an empty one -- without this hint, an agent would conclude the operation is impossible and tell the user so,
        /// when in fact the user hid it, and can also un-hide it.
        /// </summary>
        private const string SurfaceProfilePrunedHint = "Results were pruned by the user's surface profile — a skill missing here may exist but be hidden, so do not conclude Unity cannot do it. GET /health for the active profile; only the user can switch it back to \"full\" in the UnitySkills panel.";

        internal static string[] FormatOperationForPlanning(SkillOperation op)
        {
            return FormatOperation(op);
        }

        /// <summary>
        /// Python client helper function names that an agent could mistake for a REST skill name, mapped to the REST call that actually does the thing.
        /// Must stay in sync with the module-level defs in <c>unity-skills~/scripts/unity_skills.py</c>.
        ///
        /// An exact table is needed because the fuzzy fallback in <see cref="ResolveSkillNotFound"/> structurally
        /// can't reach them: a helper function's name shares no token with any registered skill,
        /// isn't within edit distance 5 of one, and isn't a substring of any skill name either -- the caller would get an empty suggestion list,
        /// with no way to self-correct. Only the discovery/cognition-oriented helpers an agent would hit at the start of a session are listed here;
        /// everything else still goes through the fuzzy path as before.
        /// </summary>
        private static readonly Dictionary<string, string> k_ClientHelperRestEquivalents =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "get_skill_schema",   "GET /skills/schema (add ?category=<Category> to scope it)" },
                { "get_skills_summary", "GET /skills?summary=1" },
                { "get_skills",         "GET /skills (brief directory: names by category; ?full=1 for full entries)" },
                { "search_skills",      "GET /skills/recommend?intent=... (search_skills greps a local cache; it has no REST counterpart)" },
                { "find_skills",        "GET /skills/recommend?intent=..." },
                { "get_skill_chain",    "GET /skills/chain?output=<field>&maxDepth=<n>" },
                { "health",             "GET /health" },
                { "get_server_status",  "GET /health" },
                { "is_unity_running",   "GET /health" },
                { "wait_for_health",    "GET /health (poll it)" },
                { "wait_for_unity",     "GET /health (poll it)" },
                { "call_skill",         "POST /skill/<real skill name> — call_skill is the client wrapper, not a skill" },
                { "dry_run_skill",      "POST /skill/<real skill name>?mode=dryRun" },
                { "plan_skill",         "POST /skill/<real skill name>?mode=plan" },
                { "plan_workflow",      "the 'workflow_plan' skill" },
                { "create_script",      "the 'script_create' skill (note the word order)" },
                { "diagnose",           "the 'unity_diagnose' skill" },
                { "get_audit_log",      "GET /permission/audit" },
            };

        internal static string ResolveSkillNotFound(string name)
        {
            // A client helper function's name can never fuzzy-match any skill -- before falling back to nearest-name search,
            // answer with its corresponding REST usage first.
            if (!string.IsNullOrEmpty(name) &&
                k_ClientHelperRestEquivalents.TryGetValue(name, out var restEquivalent))
            {
                return SkillErrorResponse.ClientHelperNotASkill(name, restEquivalent);
            }

            // Gives up to 5 closest *externally offered* skill names, letting the AI agent self-correct a typo.
            // Drawn from VisibleSkills rather than the registry: an approximate match against a hidden skill
            // would hand back, verbatim, the very name the surface profile just withdrew,
            // turning typo correction into an enumeration channel for what the user chose not to expose.
            var nearest = VisibleSkills().Select(s => s.Name)
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

        /// <summary>
        /// The externally-offered skill set, honoring the current surface profile. Anywhere skills get offered to a caller
        /// (allowlist picker, skill browser, smoke probing) should use this: offering a skill the profile hides
        /// would only earn a SURFACE_EXCLUDED later on. Use <see cref="GetAllSkillsSnapshotUnfiltered"/> when accounting
        /// needs to cover the entire registry.
        /// </summary>
        internal static SkillInfo[] GetAllSkillsSnapshot()
        {
            Initialize();
            return VisibleSkills()
                .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>
        /// Every registered skill, ignoring the surface profile -- for callers that must reason about the registry itself
        /// (rather than "what's offered externally"): resolving an already-persisted skill name (an allowlist entry stays valid
        /// across a profile switch, so rendering it as "(Unknown)" just because the current profile hides it would be a lie),
        /// and full-registry audits of the same kind as <see cref="ValidateMetadata"/>.
        ///
        /// Local editor UI and diagnostics only. Never wire this to any HTTP surface: the profile is the user's statement of
        /// "what can be offered to the AI," and any endpoint enumerating from here would hand back a skill name the user chose to withdraw --
        /// exactly the leak <see cref="VisibleSkills"/> exists to prevent.
        /// </summary>
        internal static SkillInfo[] GetAllSkillsSnapshotUnfiltered()
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
            NormalizeSyntheticEntityIdLocator(skill, validation);
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
                        // Batch-style skills declare a JSON payload as a string parameter, and an agent frequently sends a native
                        // array/object directly. Serializes it back to a string here, instead of failing with TYPE_MISMATCH --
                        // the skill re-parses that JSON internally, so the round trip is lossless.
                        // This leniency only applies when the target type is string; every other type stays strict.
                        if (p.ParameterType == typeof(string) && (token is JArray || token is JObject))
                            invoke[i] = token.ToString(Formatting.None);
                        else
                            invoke[i] = token.ToObject(p.ParameterType);
                    }
                    catch (Exception ex)
                    {
                        validation.TypeErrors.Add(new { parameter = p.Name, expectedType = GetJsonType(p.ParameterType), error = ex.Message });
                    }
                }
                else if (IsParameterRequired(skill, p))
                {
                    validation.MissingParams.Add(p.Name);
                }
                else if (p.HasDefaultValue)
                {
                    invoke[i] = p.DefaultValue;
                }
                else
                {
                    invoke[i] = null;
                }

                validation.ParameterDetails.Add(new
                {
                    name = p.Name,
                    type = GetJsonType(p.ParameterType),
                    required = IsParameterRequired(skill, p),
                    provided,
                    defaultValue = p.HasDefaultValue ? p.DefaultValue?.ToString() : null
                });
            }

            if (ShouldExposeSyntheticEntityId(skill))
            {
                validation.ParameterDetails.Add(new
                {
                    name = EntityIdParameterName,
                    type = "string",
                    required = false,
                    provided = validation.Args.TryGetValue(EntityIdParameterName, StringComparison.OrdinalIgnoreCase, out _),
                    defaultValue = (string)null,
                    synthetic = true
                });
            }

            validation.InvokeArgs = invoke;
            SkillPlanningService.ApplySemanticValidation(skill, validation);
            return validation;
        }

        private static void NormalizeSyntheticEntityIdLocator(SkillInfo skill, ParameterValidationResult validation)
        {
            if (!ShouldExposeSyntheticEntityId(skill) ||
                validation?.Args == null ||
                !validation.Args.TryGetValue(EntityIdParameterName, StringComparison.OrdinalIgnoreCase, out var token))
            {
                return;
            }

            var entityId = token.Type == JTokenType.Null ? null : token.ToString();
            if (string.IsNullOrWhiteSpace(entityId))
                return;

            var unityObject = UnityObjectIdUtility.EntityIdToObject(entityId);
            var gameObject = unityObject as GameObject ?? (unityObject as Component)?.gameObject;
            if (gameObject == null)
            {
                validation.SemanticErrors.Add(new
                {
                    parameter = EntityIdParameterName,
                    error = $"Object not found for entityId: {entityId}"
                });
                return;
            }

            if (TryInjectLocatorValue(validation.Args, skill.ParameterNames, _entityIdPathFallbackParameters, GameObjectFinder.GetCachedPath(gameObject)))
                return;

            TryInjectLocatorValue(validation.Args, skill.ParameterNames, _entityIdNameFallbackParameters, gameObject.name);
        }

        private static bool TryInjectLocatorValue(JObject args, string[] parameterNames, string[] candidates, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            foreach (var candidate in candidates)
            {
                if (!ContainsParameter(parameterNames, candidate))
                    continue;

                args[candidate] = value;
                return true;
            }

            return false;
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
        /// Returns null when the current surface profile exposes this skill; otherwise returns a serialized SURFACE_EXCLUDED
        /// payload, for the caller to present as-is.
        ///
        /// This message has exactly one job: stopping the agent from working around the exclusion. After hitting the wall, an
        /// agent instinctively retries, then goes looking for a neighboring module that can do the same write -- either would defeat the profile's purpose.
        /// So the payload names which profile is hiding it, states that the setting belongs to the user,
        /// and (under guide) hands over the manual-* doc, so the agent switches to acting as an instructor to get the task done instead.
        /// </summary>
        /// <summary>
        /// For an excluded skill, the manual-* doc that should be handed to the agent; returns null if there is none.
        /// That question is answered by category, with one exception for the "escape hatch" skills hidden by name: they're hidden
        /// precisely because their category can't describe what they can reach, so a doc derived from category would be the wrong guidance.
        /// </summary>
        private static string SurfaceExclusionManualDoc(SkillInfo skill) =>
            SkillsSurfaceProfile.IsAlwaysHiddenSkill(skill.Name)
                ? null
                : SkillsSurfaceProfile.ManualDocFor(skill.Category);

        /// <summary>
        /// The two rejection paths -- the dry-run preview (<paramref name="forPreview"/>) and the execute gate -- share one copy,
        /// so they never tell the agent something different about the same wall. The preview states "what would happen",
        /// the gate tells the agent "what to do instead." Three cases, in order of precedence:
        /// <list type="bullet">
        /// <item><b>An escape hatch hidden by name:</b> no manual doc applies, because the reason is this skill's reach, not its module.
        /// Points at the Editor menu -- exactly what this skill was driving in the first place, so the user can do by hand what the AI isn't allowed to do for them.</item>
        /// <item><b>guide, with a manual doc available:</b> hand over that doc; the agent finishes the task acting as an instructor.</item>
        /// <item><b>Everything else:</b> only the user can lift it.</item>
        /// </list>
        /// </summary>
        private static string BuildSurfaceExclusionHint(SkillInfo skill, bool forPreview)
        {
            var profile = SkillsSurfaceProfile.CurrentWire;

            if (SkillsSurfaceProfile.IsAlwaysHiddenSkill(skill.Name))
            {
                return forPreview
                    ? $"Hidden by the \"{profile}\" surface profile — it can execute any menu item, including the writes this profile withdraws, so it is off the menu in every mode, allowlist included. Tell the user which Editor menu path does the job and let them run it."
                    : $"Do not retry and do not look for another route — this skill drives arbitrary Editor menu items, which is why the profile withdraws it wholesale. Name the exact menu path (e.g. GameObject > Create Empty) and walk the user through clicking it, or ask them to switch the surface profile back to \"full\" in the UnitySkills panel.";
            }

            var manualDoc = SkillsSurfaceProfile.ManualDocFor(skill.Category);
            if (manualDoc != null)
            {
                return forPreview
                    ? $"Hidden by the \"{profile}\" surface profile — executing is impossible in any mode, allowlist included. Guide the user by hand ({manualDoc}), or they switch the profile back to \"full\" in the UnitySkills panel."
                    // category is already named in the message and in details.category, so the hint just says "this change"
                    // rather than interpolating it -- "walk the user through the Sample change"
                    // reads as meaningless to the one audience that matters here.
                    : $"Do not retry and do not substitute another module — the write is off the menu, not failing. Read {manualDoc} and walk the user through the change in the Editor yourself, or ask them to switch the surface profile back to \"full\" in the UnitySkills panel if they want it automated.";
            }

            // Only noSceneAuthoring reaches here right now, so hardcoding "excludes scene-authoring writes"
            // is safe: guide never gets here, because every category guide hides ships a manual-* doc,
            // which gets caught by the branch above. That invariant is guarded by
            // SkillsSurfaceProfileTests.EveryGuideHiddenCategory_ShipsAManualDoc --
            // a newly added guide category must have its manual-* doc ready first, or this branch would start telling guide users
            // their write was blocked for being "scene authoring" when it wasn't.
            return forPreview
                ? $"Hidden by the \"{profile}\" surface profile, which excludes scene-authoring writes — executing is impossible in any mode, allowlist included. Only the user can switch the profile back to \"full\" in the UnitySkills panel."
                : $"Do not retry and do not substitute another module. The \"{profile}\" profile excludes scene-authoring writes; tell the user this step needs one and let them switch the surface profile back to \"full\" in the UnitySkills panel.";
        }

        private static string ApplySurfaceGate(SkillInfo skill, string name)
        {
            if (!SkillsSurfaceProfile.IsExcluded(skill))
                return null;

            var profile = SkillsSurfaceProfile.CurrentWire;
            var category = skill.Category.ToString();
            var manualDoc = SurfaceExclusionManualDoc(skill);
            var hint = BuildSurfaceExclusionHint(skill, forPreview: false);

            SkillsAuditLog.Append("call", new
            {
                skill = name,
                result = "surfaceExcluded",
                surfaceProfile = profile,
                category,
            });

            return SkillErrorResponse.Build(
                SkillErrorCode.SurfaceExcluded,
                // The escape hatch uses its own wording: "a write skill in the Editor category" would be both wrong
                // (its category isn't what's hidden) and useless (it wouldn't explain why this skill is hidden).
                // Every other exclusion really is category + write.
                SkillsSurfaceProfile.IsAlwaysHiddenSkill(name)
                    ? $"Skill '{name}' is hidden by the current surface profile '{profile}': it can execute any Editor menu item, which would reach the writes this profile withdraws."
                    : $"Skill '{name}' is hidden by the current surface profile '{profile}': it is a write skill in the {category} category.",
                skill: name,
                details: new
                {
                    surfaceProfile = profile,
                    category,
                    manualDoc,
                    userControlled = true,
                    hint,
                },
                // The closest available strategy: this call must not be repeated as-is. Unlike ask_user_and_grant,
                // there's no token to obtain here -- either the user changes a panel setting, or the task gets done by hand.
                retryStrategy: SkillErrorResponse.Abort);
        }

        /// <summary>
        /// Returns null when the permission tier allows this skill; otherwise returns a serialized error payload
        /// (MODE_RESTRICTED or MODE_FORBIDDEN), for the caller to present as-is.
        /// Always writes a "call" audit entry when the verdict is Allowed, so silent execution under Auto mode is still traceable.
        /// </summary>
        private static string ApplyModeGate(SkillInfo skill, string name, ParameterValidationResult validation)
        {
            var argsForHash = validation?.Args == null ? new JObject() : (JObject)validation.Args.DeepClone();
            argsForHash.Remove("_confirm");
            var argsJson = argsForHash.ToString(Formatting.None);

            // Critical: allowlist status must be read before CheckAccess -- CheckAccess consumes the one-shot marker internally,
            // while IsInAllowlist can still be queried repeatedly afterward. Recording the allowlist hit first lets the audit distinguish allowlist vs oneShot vs auto.
            bool allowlistHit = SkillsModeManager.IsInAllowlist(skill.Name);
            var access = SkillsModeManager.CheckAccess(skill);
            var currentMode = SkillsModeManager.CurrentMode;
            var modeWire = SkillsModeManager.ModeToWire(currentMode);

            switch (access)
            {
                case SkillsModeManager.AccessResult.Allowed:
                    bool highImpact = currentMode == SkillsOperatingMode.Auto
                        && (skill.MutatesScene || skill.MutatesAssets
                            || skill.Operation.HasFlag(SkillOperation.Modify)
                            || skill.Operation.HasFlag(SkillOperation.Create));
                    // grantSource: an allowlist hit takes top priority; otherwise Bypass mode counts as bypass;
                    // every other Allowed that's neither Allowlist nor Bypass is classified as auto (CheckAccess already consumed
                    // any one-shot token before this call, so it can't be told apart afterward; this is the best approximation currently observable).
                    string grantSource;
                    if (allowlistHit) grantSource = "allowlist";
                    else if (currentMode == SkillsOperatingMode.Bypass) grantSource = "bypass";
                    else grantSource = "auto";
                    SkillsAuditLog.Append("call", new
                    {
                        skill = name,
                        mode = modeWire,
                        skillMode = SkillsModeManager.SkillModeToWire(skill.Mode),
                        result = "allowed",
                        highImpact,
                        allowlistHit,
                        grantSource,
                    });
                    return null;

                case SkillsModeManager.AccessResult.Forbidden:
                    SkillsAuditLog.Append("call", new
                    {
                        skill = name,
                        mode = modeWire,
                        skillMode = SkillsModeManager.SkillModeToWire(skill.Mode),
                        result = "forbidden",
                    });
                    return SkillErrorResponse.Build(
                        SkillErrorCode.ModeForbidden,
                        "This skill is classified as never-in-semi and is only allowed in Bypass mode.",
                        skill: name,
                        details: new
                        {
                            currentMode = modeWire,
                            riskLevel = skill.RiskLevel,
                            mayEnterPlayMode = skill.MayEnterPlayMode,
                            mayTriggerReload = skill.MayTriggerReload,
                            operation = FormatOperation(skill.Operation),
                            hint = "Switch the Unity panel to Bypass mode, or use a different skill.",
                        },
                        retryStrategy: SkillErrorResponse.Abort);

                case SkillsModeManager.AccessResult.NeedsGrant:
                    var (token, ttl, channel) = SkillsModeManager.IssueGrantRequest(name, argsJson);
                    var channelWire = SkillsModeManager.ChannelToWire(channel);
                    var pendingSummary = SkillsModeManager.PeekPending(token);
                    SkillsAuditLog.Append("call", new
                    {
                        skill = name,
                        mode = modeWire,
                        skillMode = SkillsModeManager.SkillModeToWire(skill.Mode),
                        result = "restricted",
                        grantToken = token,
                        channel = channelWire,
                    });
                    return SkillErrorResponse.Build(
                        SkillErrorCode.ModeRestricted,
                        "This skill is FullAuto and requires user approval under the current mode.",
                        skill: name,
                        details: new
                        {
                            currentMode = modeWire,
                            skillMode = SkillsModeManager.SkillModeToWire(skill.Mode),
                            approvalChannel = channelWire,
                            grantRequestToken = token,
                            tokenTtlSeconds = ttl,
                            argsSummary = pendingSummary?.ArgsSummary,
                            hint = channel == SkillsModeManager.ApprovalChannel.Dialog
                                ? "Ask the user; on consent POST /permission/grant {skill, token}. That grant call executes the skill in-line and returns the result (response.result). Do not re-call the original skill."
                                : "Tell the user to click Approve on the Unity panel; then POST /permission/grant {skill, token} once. That grant call executes the skill in-line and returns the result. Do not poll grant; do not re-call the original skill.",
                        },
                        retryStrategy: SkillErrorResponse.RetryAskUserAndGrant);
            }
            return null;
        }

        /// <summary>
        /// Returns null when this skill is allowed to execute (the token has been consumed); otherwise returns a serialized error payload
        /// (CONFIRMATION_REQUIRED or INVALID_TOKEN), which the caller should pass back to the client as-is.
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

            // argsHash excludes _confirm, so the same parameters hash identically across both calls.
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
                    // The dry-run is best-effort; the token is still valid even if it fails.
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

                // The schema's supportsDryRun flag advertises a router-level preview transport mode
                // (POST /skill/<name>?mode=dryRun), not a request-body parameter -- but an agent that reads that flag
                // invariably passes one anyway, and Levenshtein finds no useful neighbor for "dryRun".
                if (string.Equals(param, "dryRun", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(param, "dry_run", StringComparison.OrdinalIgnoreCase))
                {
                    fixes.Add(new SuggestedFix
                    {
                        action = "fix_param",
                        skill = skillName,
                        reason = $"'{param}' is not a parameter — dry run is a transport mode: " +
                                 $"POST /skill/{skillName}?mode=dryRun with the same JSON body, then execute without the query flag."
                    });
                    continue;
                }

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

            var fuzzyMatches = allowedParameterNames
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

            if (fuzzyMatches.Length > 0)
                return fuzzyMatches;

            // Last-resort fallback: neither the alias table nor edit distance catches a rename like assetPath->savePath
            // (distance 4, no substring overlap), but parameter names across the whole skill library reuse the same set of camelCase tokens
            // (path/name/id/target/source/...), so "sharing any token" is a strong signal.
            // Only enabled when the stricter tiers find nothing, to avoid adding noise to suggestions that already have a good match.
            var unknownTokens = SplitCamelCaseTokens(unknownParameter);
            if (unknownTokens.Count == 0)
                return fuzzyMatches;

            return allowedParameterNames
                .Where(name => SplitCamelCaseTokens(name).Overlaps(unknownTokens))
                .Select(name => new
                {
                    Name = name,
                    Distance = ComputeLevenshteinDistance(unknownParameter, name)
                })
                .OrderBy(x => x.Distance)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .Select(x => x.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static HashSet<string> SplitCamelCaseTokens(string name)
        {
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(name))
                return tokens;

            var current = new System.Text.StringBuilder();
            foreach (var c in name)
            {
                if (!char.IsLetter(c))
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                    continue;
                }

                if (char.IsUpper(c) && current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                current.Append(char.ToLowerInvariant(c));
            }

            if (current.Length > 0)
                tokens.Add(current.ToString());
            return tokens;
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

                // A plan made for a skill the profile hides is a plan that can never execute, and ?mode=plan used to be the one preview
                // that never said so -- an agent would plan out the whole sequence, hit SURFACE_EXCLUDED on the very first execute,
                // with nothing in the plan having hinted at it. This uses the same block, the same shape
                // as the dry-run branch (BuildAuthorizationPreview returns the SURFACE_EXCLUDED verdict here too), so the caller only ever has to read one contract.
                // Only appended when there's actually something to say: for every skill the profile directly allows, the plan bytes stay unchanged,
                // and the plan output is already the largest of the three preview payloads. The second branch covers the "carried-write" entry points,
                // whose rejection is decided by a payload no preview has access to -- planning for a batch_execute
                // a profile is going to reject is the same trap one level up.
                if (SkillsSurfaceProfile.IsExcluded(skill) ||
                    SkillsSurfaceProfile.CarriedWritePreviewGate(skill.Name) != null)
                    plan["authorization"] = BuildAuthorizationPreview(skill);

                return JsonConvert.SerializeObject(plan, _jsonSettings);
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                return SkillErrorResponse.Build(
                    SkillErrorCode.InvalidJson,
                    $"Invalid JSON: {ex.Message}",
                    skill: name,
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
            }
            catch (Exception ex)
            {
                // Even valid JSON can still crash plan/semantic validation (e.g. an NRE). Reporting this case as INVALID_JSON
                // would send the agent into repeatedly rewriting a request body that was never the problem; so, following Execute's catch split,
                // the real failure is reported honestly.
                return SkillErrorResponse.Build(
                    SkillErrorCode.Internal,
                    $"Plan failed: {ex.Message}",
                    skill: name,
                    details: new { exceptionType = ex.GetType().Name },
                    retryStrategy: SkillErrorResponse.Abort);
            }
        }



        /// <summary>
        /// Validates the metadata completeness and consistency of every discovered skill.
        /// Returns a set of diagnostic messages (prefixed with WARN/ERROR).
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

                // ReadOnly isn't just documentation, it's load-bearing: a surface profile never hides a read-only skill,
                // so a write operation mislabeled ReadOnly=true remains callable under a profile that exists specifically
                // to withdraw that kind of write. The three checks below are exactly the self-contradictions a mislabel would cause; they're ERROR rather than WARN,
                // because each one silently breaks a user-facing guarantee.
                if (s.ReadOnly)
                {
                    if (s.MutatesScene)
                        issues.Add($"[ERROR] {s.Name}: ReadOnly=true conflicts with MutatesScene=true (a read-only skill is never hidden by the surface profile)");

                    if (s.MutatesAssets)
                        issues.Add($"[ERROR] {s.Name}: ReadOnly=true conflicts with MutatesAssets=true (a read-only skill is never hidden by the surface profile)");

                    var writeOps = FormatOperation(s.Operation & (SkillOperation.Create | SkillOperation.Modify | SkillOperation.Delete));
                    if (writeOps != null)
                        issues.Add($"[ERROR] {s.Name}: ReadOnly=true conflicts with write Operation {string.Join("|", writeOps)}");
                }

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

                // RiskLevel is a free-form string, and RiskRank silently ranks any value it doesn't recognize as "low".
                // So a typo ("hgih") doesn't fail explicitly -- it demotes that skill to the lowest risk,
                // which is exactly the field an agent reads when deciding whether to confirm with the user,
                // and also the field AppendBatchMirrorIssues uses to compare a batch against its singular counterpart.
                // Every declaration shipped with the package today is valid; this check exists so the next one doesn't get miswritten in the direction of hiding risk.
                if (!IsKnownRiskLevel(s.RiskLevel))
                    issues.Add($"[WARN] {s.Name}: RiskLevel='{s.RiskLevel}' is not one of low/medium/high — it ranks as 'low'");
            }

            AppendBatchMirrorIssues(issues);

            return issues;
        }

        private const string BatchSkillSuffix = "_batch";

        /// <summary>
        /// Cross-skill rule: what <c>X_batch</c> declares must not have a smaller impact footprint than <c>X</c>.
        ///
        /// <para>A batch skill does the same work as the singular skill, N times over, so it can never mutate less, track less, or carry less risk.
        /// If the metadata says otherwise, the batch entry was written wrong, and the consequences aren't trivial:
        /// MutatesScene/MutatesAssets decide what a surface profile withdraws, TracksWorkflow decides whether this call
        /// can be undone, and RiskLevel is the field an agent reads before deciding whether to confirm with the user.
        /// An under-declared batch thus becomes the variant that slips through every gate meant to stop its singular twin --
        /// and it touches N objects instead of one.</para>
        ///
        /// <para>Only checks a strict <c>X</c>/<c>X_batch</c> name pairing. A singular twin spelled differently
        /// (material_set_colors_batch <-> material_set_color), or a batch skill with no twin at all,
        /// is skipped entirely, with no guessing.</para>
        /// </summary>
        private static void AppendBatchMirrorIssues(List<string> issues)
        {
            foreach (var batch in _skills.Values)
            {
                if (!batch.Name.EndsWith(BatchSkillSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var singularName = batch.Name.Substring(0, batch.Name.Length - BatchSkillSuffix.Length);
                if (!_skills.TryGetValue(singularName, out var single))
                    continue;

                if (single.MutatesScene && !batch.MutatesScene)
                    issues.Add($"[ERROR] {batch.Name}: MutatesScene=false but {singularName} declares MutatesScene=true");

                if (single.MutatesAssets && !batch.MutatesAssets)
                    issues.Add($"[ERROR] {batch.Name}: MutatesAssets=false but {singularName} declares MutatesAssets=true");

                if (single.TracksWorkflow && !batch.TracksWorkflow)
                    issues.Add($"[ERROR] {batch.Name}: TracksWorkflow=false but {singularName} declares TracksWorkflow=true");

                if (RiskRank(batch.RiskLevel) < RiskRank(single.RiskLevel))
                    issues.Add($"[ERROR] {batch.Name}: RiskLevel='{batch.RiskLevel}' is below {singularName}'s '{single.RiskLevel}'");
            }
        }

        /// <summary>
        /// low &lt; medium &lt; high. Everything else ranks the same as "low" -- that's not a fallback, it's a fact:
        /// <see cref="UnitySkillAttribute.RiskLevel"/>'s default value is "low",
        /// so an unrecognized or missing level genuinely is the lowest risk declaration available.
        /// </summary>
        private static int RiskRank(string riskLevel)
        {
            if (string.Equals(riskLevel, "high", StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(riskLevel, "medium", StringComparison.OrdinalIgnoreCase)) return 1;
            return 0;
        }

        /// <summary>
        /// Whether <paramref name="riskLevel"/> is a level <see cref="RiskRank"/> genuinely recognizes.
        /// Ranking an unknown string as "low" is correct runtime behavior, but staying silent about it isn't,
        /// so <see cref="ValidateMetadata"/> raises a WARN for it.
        /// </summary>
        private static bool IsKnownRiskLevel(string riskLevel) =>
            string.Equals(riskLevel, "low", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(riskLevel, "medium", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(riskLevel, "high", StringComparison.OrdinalIgnoreCase);

        // ========== Query string parsing ==========

        /// <summary>
        /// Parses a query string into a case-insensitive key -> value map.
        ///
        /// Two forms used to be dropped outright, even though a caller writes them deliberately:
        /// <list type="bullet">
        /// <item><b>A bare key</b> (<c>?full</c>, <c>?brief</c>) -- the URL idiom for "present means true."
        /// Dropping it would turn <c>GET /skills?full</c> into a silent no-op, returning the 19KB catalog while the caller
        /// waits on the 618KB manifest. It's now collected as the value <c>"1"</c>, the same value <c>?full=1</c> gets,
        /// so both spellings share one cache entry and one ETag.</item>
        /// <item><b>A key with an empty value</b> (<c>?category=</c>) -- collected as an empty string rather than dropped,
        /// so the narrowing-filter guard can reject it alongside the valid word list. Dropping it would turn a half-written filter
        /// condition into "no filter," so a scoped request would get the whole catalog while still looking like it succeeded.</item>
        /// </list>
        /// A pair with no key at all (<c>?=v</c>, or an empty segment in <c>?a&amp;&amp;b</c>) is still skipped -- there's no key to index by.
        /// </summary>
        internal static Dictionary<string, string> ParseQueryString(string qs)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(qs)) return result;

            var raw = qs.StartsWith("?") ? qs.Substring(1) : qs;
            if (string.IsNullOrEmpty(raw)) return result;

            foreach (var pair in raw.Split('&'))
            {
                var eqIdx = pair.IndexOf('=');
                if (eqIdx == 0) continue;

                string key, val;
                if (eqIdx < 0)
                {
                    key = Uri.UnescapeDataString(pair).Trim();
                    val = "1";
                }
                else
                {
                    key = Uri.UnescapeDataString(pair.Substring(0, eqIdx)).Trim();
                    val = Uri.UnescapeDataString(pair.Substring(eqIdx + 1)).Trim();
                }

                if (!string.IsNullOrEmpty(key))
                    result[key] = val;
            }
            return result;
        }

        /// <summary>
        /// Automatically snapshots target objects from skill parameters, to support generic rollback.
        /// Recognizes common target parameters (name, instanceId, path, materialPath, etc.) and snapshots them.
        /// Target location is delegated to <see cref="CollectTargetsFromArgs"/>,
        /// so the semantic diff's pre-capture reuses exactly the same object set, order, and best-effort semantics.
        /// </summary>
        /// <summary>
        /// Whether the current manually-recorded session (workflow_begin_task) has anything new to persist since the last SaveHistory --
        /// i.e. a different task is now active, or the active task gained new snapshots. Every time it returns true, it advances the saved marker,
        /// so the next call compares against this save point. Best-effort: defaults to saving on any anomaly (a null task),
        /// to guarantee history is never silently dropped.
        /// </summary>
        private static bool ManualSessionIsDirty(WorkflowTask currentTask)
        {
            if (currentTask == null)
                return true; // shouldn't happen while IsRecording; save defensively

            int count = currentTask.snapshots?.Count ?? 0;
            if (currentTask.id == _lastSavedTaskId && count == _lastSavedSnapshotCount)
                return false;

            _lastSavedTaskId = currentTask.id;
            _lastSavedSnapshotCount = count;
            return true;
        }

        private static void TrySnapshotTargetsFromArgs(JObject args)
        {
            try
            {
                foreach (var obj in CollectTargetsFromArgs(args))
                    WorkflowManager.SnapshotObject(obj);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Workflow snapshot failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Locates the UnityEngine.Object a skill parameter points to -- the shared low-level primitive behind both the
        /// automatic workflow snapshot (<see cref="TrySnapshotTargetsFromArgs"/>) and the semantic diff's
        /// pre-capture (<see cref="SkillSceneDiff.CaptureBefore"/>).
        ///
        /// Objects are returned in a fixed order consistent with the historical snapshot sequence, to keep snapshot behavior unchanged:
        /// the target GameObject + its Transform + Renderer.sharedMaterial, then the asset pointed to by materialPath / assetPath,
        /// then child Transforms, and finally each target in items[]
        /// (GameObject + Transform, capped at the first 50). Location is best-effort; a target that can't be resolved is skipped.
        /// The items[] section carries its own try/catch, so a malformed batch never interrupts the rest -- matching the original inline behavior.
        /// </summary>
        internal static List<UnityEngine.Object> CollectTargetsFromArgs(JObject args)
        {
            var targets = new List<UnityEngine.Object>();

            // Tries to locate the target GameObject by common parameter names
            string targetName = null;
            int targetInstanceId = 0;
            string targetPath = null;
            string targetEntityId = null;

            if (args.TryGetValue("name", StringComparison.OrdinalIgnoreCase, out var nameToken))
                targetName = nameToken.ToString();
            if (args.TryGetValue("instanceId", StringComparison.OrdinalIgnoreCase, out var idToken))
                targetInstanceId = idToken.ToObject<int>();
            if (args.TryGetValue("path", StringComparison.OrdinalIgnoreCase, out var pathToken))
                targetPath = pathToken.ToString();
            if (args.TryGetValue(EntityIdParameterName, StringComparison.OrdinalIgnoreCase, out var entityIdToken))
                targetEntityId = entityIdToken.ToString();

            // Snapshot the GameObject once it's identified
            if (!string.IsNullOrEmpty(targetEntityId) || !string.IsNullOrEmpty(targetName) || targetInstanceId != 0 || !string.IsNullOrEmpty(targetPath))
            {
                var (go, _) = GameObjectFinder.FindOrError(targetName, targetInstanceId, targetPath, entityId: targetEntityId);
                if (go != null)
                {
                    targets.Add(go);
                    // Transform is the most commonly modified, snapshot it too
                    targets.Add(go.transform);
                    // If there's a Renderer, snapshot its material
                    var renderer = go.GetComponent<UnityEngine.Renderer>();
                    if (renderer != null && renderer.sharedMaterial != null)
                        targets.Add(renderer.sharedMaterial);
                }
            }

            // Snapshot the material asset when materialPath is given
            if (args.TryGetValue("materialPath", StringComparison.OrdinalIgnoreCase, out var matPathToken))
            {
                var matPath = matPathToken.ToString();
                if (!string.IsNullOrEmpty(matPath))
                {
                    var mat = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(matPath);
                    if (mat != null)
                        targets.Add(mat);
                }
            }

            // Snapshot the asset when assetPath is given
            if (args.TryGetValue("assetPath", StringComparison.OrdinalIgnoreCase, out var assetPathToken))
            {
                var assetPath = assetPathToken.ToString();
                if (!string.IsNullOrEmpty(assetPath))
                {
                    var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                    if (asset != null)
                        targets.Add(asset);
                }
            }

            // Handles child/parent-style operations (snapshot with entityId fallback)
            {
                args.TryGetValue("childName", StringComparison.OrdinalIgnoreCase, out var childNameToken);
                args.TryGetValue("childEntityId", StringComparison.OrdinalIgnoreCase, out var childEntityIdToken);
                args.TryGetValue("childInstanceId", StringComparison.OrdinalIgnoreCase, out var childInstanceIdToken);
                args.TryGetValue("childPath", StringComparison.OrdinalIgnoreCase, out var childPathToken);
                var childEntityId = childEntityIdToken?.ToString();
                var childName = childNameToken?.ToString();
                int.TryParse(childInstanceIdToken?.ToString(), out int childInstanceId);
                var childPath = childPathToken?.ToString();
                if (!string.IsNullOrEmpty(childEntityId) || !string.IsNullOrEmpty(childName) || childInstanceId != 0 || !string.IsNullOrEmpty(childPath))
                {
                    var (childGo, _) = GameObjectFinder.FindOrError(childName, childInstanceId, childPath, entityId: childEntityId);
                    if (childGo != null)
                        targets.Add(childGo.transform);
                }
            }

            // Handles batch entries: snapshot each target in the batch individually
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
                            string itemEntityId = item.ContainsKey(EntityIdParameterName) ? item[EntityIdParameterName]?.ToString() : null;

                            if (!string.IsNullOrEmpty(itemEntityId) || !string.IsNullOrEmpty(itemName) || itemId != 0 || !string.IsNullOrEmpty(itemPath))
                            {
                                var (itemGo, _) = GameObjectFinder.FindOrError(itemName, itemId, itemPath, entityId: itemEntityId);
                                if (itemGo != null)
                                {
                                    targets.Add(itemGo);
                                    targets.Add(itemGo.transform);
                                }
                            }
                        }
                    }
                }
                catch { /* Ignored when batch parsing fails */ }
            }

            return targets;
        }

        #region HTTP-thread cached GET fast path (v2.1)
        // ⚠ Cross-thread contract: this region is called directly by SkillsHttpServer's HTTP listener thread, and must stay at
        // zero Unity API (UnityEngine.*/UnityEditor.*), zero SkillsLogger (internally routes through Debug.Log, and the
        // Level getter reads EditorPrefs on first access). Only reading string caches already built by the main thread is allowed
        // (_cachedManifest / _cachedSchema / _filteredOutputCache, all either immutable strings or
        // ConcurrentDictionary) plus this region's own _etagCache. Must return false when the cache hasn't been built yet,
        // handing back to the main thread's slow path (the main thread builds the cache, and the next request then hits it).
        // Code in this region must not call Initialize()/GetManifest()/GetSchema()/BuildFilteredOutput()
        // -- they trigger reflection scanning and SkillsLogger logging, and can only run on the main thread.

        // ETag cache: key = output cache key, value = (source json reference, etag).
        // SkillRouter is not [InitializeOnLoad] and has no static persistence, so a domain reload resets it wholesale, naturally invalidating it;
        // after Refresh() (skill add-remove) rebuilds, an old entry's json reference no longer equals the new cached string, and
        // a ReferenceEquals mismatch below automatically recomputes and overwrites the same key -- correctness never depended on clearing. But Refresh() still
        // actively Clear()s, to avoid old entries (and the large strings they reference) accumulating across repeated Refreshes; MaxCacheEntries additionally
        // guards against unbounded growth along any path.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Json, string Etag)> _etagCache =
            new System.Collections.Concurrent.ConcurrentDictionary<string, (string Json, string Etag)>();

        /// <summary>
        /// HTTP thread fast lane: GET /skills, GET /skills/schema (including query variants), and GET /skills/meta
        /// return the cached json + ETag (first 16 hex of SHA256) directly once the string cache has been built by the main thread, bypassing the main-thread queue.
        /// A miss (cache not yet built / path doesn't belong to these three endpoints) returns false.
        /// /skills/recommend, /skills/chain, /skills/batch, and other paths that don't match exactly always go through the slow path.
        /// Routing is delegated entirely to <see cref="ResolveGetSurface"/> -- the same logic as the main thread's BuildFilteredOutput,
        /// so bare /skills lands on the brief cache string on both paths, never brief on one and full on the other.
        /// Likewise, an invalid ?category=/?operation= value must also fall back to the slow path here: consistent routing alone isn't enough, the two paths'
        /// judgment of "should this query be rejected" must agree too.
        /// </summary>
        internal static bool TryGetCachedGetResponse(string path, string query, out string json, out string etag)
        {
            json = null;
            etag = null;

            string manifestType = ResolveManifestTypeForPath(path);
            if (manifestType == null)
                return false;

            var filters = StripUnrecognizedFilterKeys(ParseQueryString(query));

            // Same determination as the main thread (FindInvalidNarrowingFilterKey, pure string comparison, no Unity API touched):
            // an invalid ?category=/?operation= value always falls back to the slow path to mint the error body. Without this step, the Brief/Meta
            // surfaces would bypass validation entirely -- they don't consult _filteredOutputCache, they return _cachedBrief/_cachedMeta
            // (already built by the main thread) directly, so ?brief=1&category=Bogus would get a 200 catalog when the cache is warm,
            // and an error when it's cold -- two different answers for the same URL.
            if (FindInvalidNarrowingFilterKey(filters) != null)
                return false;

            // Calls ResolveGetSurface directly rather than through BuildGetCacheKey: the routing logic is still the same one, but filters
            // have already been parsed above, and going through BuildGetCacheKey again would parse the query a second time for nothing on every fast-path request.
            string cacheKey = ResolveGetSurface(manifestType, filters, out var surface);
            switch (surface)
            {
                case GetSurface.Meta:
                    json = _cachedMeta;
                    break;
                case GetSurface.Brief:
                    json = _cachedBrief;
                    break;
                case GetSurface.FullV1:
                    json = manifestType == "schema" ? _cachedSchema : _cachedManifest;
                    break;
                default:
                    _filteredOutputCache.TryGetValue(cacheKey, out json);
                    break;
            }

            if (json == null)
                return false;

            etag = GetOrComputeEtag(cacheKey, json);
            return true;
        }

        /// <summary>
        /// Main-thread slow path only: gets the ETag for output just built for /skills, /skills/schema, or /skills/meta. Shares
        /// <see cref="BuildGetCacheKey"/> and
        /// <see cref="GetOrComputeEtag"/> with <see cref="TryGetCachedGetResponse"/>, so the same content gets an identical etag
        /// whether it comes from the slow path or the HTTP thread's fast path -- otherwise the client would flip-flop between the two paths, and If-None-Match would never hit a 304.
        /// Returns null when json is empty (an error response, etc.); the caller should not send an ETag header in that case.
        /// </summary>
        internal static string GetEtagForCachedGet(string path, string query, string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            return GetOrComputeEtag(BuildGetCacheKey(path, query, out _), json);
        }

        /// <summary>
        /// Manifest-family paths -> manifestType; every other path returns null. Pure string matching, safe to call on the HTTP thread.
        /// </summary>
        private static string ResolveManifestTypeForPath(string path)
        {
            if (string.Equals(path, "/skills", StringComparison.OrdinalIgnoreCase)) return "manifest";
            if (string.Equals(path, "/skills/schema", StringComparison.OrdinalIgnoreCase)) return "schema";
            if (string.Equals(path, MetaEndpointPath, StringComparison.OrdinalIgnoreCase)) return "meta";
            return null;
        }

        /// <summary>
        /// Stays consistent with BuildFilteredOutput's routing: the same <see cref="ResolveGetSurface"/> decides
        /// the surface and the cache key (an unknown path is treated as manifest, reachable only through
        /// <see cref="GetEtagForCachedGet"/>'s defensive fallback).
        /// </summary>
        private static string BuildGetCacheKey(string path, string query, out GetSurface surface)
        {
            string manifestType = ResolveManifestTypeForPath(path) ?? "manifest";
            var filters = StripUnrecognizedFilterKeys(ParseQueryString(query));
            return ResolveGetSurface(manifestType, filters, out surface);
        }

        /// <summary>
        /// Gets an ETag memoized by (cache key, json reference): only reused when the entry exists and its Json reference
        /// matches the current cached string, otherwise recomputed and overwritten -- ensuring that after Refresh() rebuilds the cache, a stale etag never falsely triggers a 304.
        /// </summary>
        private static string GetOrComputeEtag(string cacheKey, string json)
        {
            if (_etagCache.TryGetValue(cacheKey, out var entry) && ReferenceEquals(entry.Json, json))
                return entry.Etag;

            string etag = ComputeEtag(json);
            if (_etagCache.Count >= MaxCacheEntries) _etagCache.Clear();
            _etagCache[cacheKey] = (json, etag);
            return etag;
        }

        private static string ComputeEtag(string json)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(json));
                var sb = new System.Text.StringBuilder(16);
                for (int i = 0; i < 8; i++)
                    sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        #endregion
    }

    /// <summary>
    /// Everything the router can pull off a skill's error object. For the legacy <c>new { error = "..." }</c> shape,
    /// only <see cref="Message"/> gets filled in; everything else is a contract a skill can optionally declare, to override
    /// <see cref="SkillErrorClassifier"/>'s guessed result.
    /// </summary>
    internal sealed class SkillErrorContext
    {
        public string Message;
        public SkillErrorCode? Code;
        public string RetryStrategy;
        public List<SuggestedFix> SuggestedFixes;
        public List<string> RelatedSkills;

        /// <summary>
        /// Every other field a skill puts on its error object (a list of valid values, doc URL, package id, hints, etc.).
        /// Without this, the classifier would only ever answer from the message, silently dropping the diagnostic information a skill deliberately computed.
        /// </summary>
        public Dictionary<string, object> Extra;
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

        /// <summary>
        /// The first layer of the router's error contract: extracts the message, plus any structured fields a skill chooses to declare
        /// (<c>errorCode</c>, <c>suggestedFixes</c>, <c>retryStrategy</c>, <c>relatedSkills</c>).
        /// The condition for "is this an error" is exactly the same as <see cref="TryGetError(object, out string)"/>,
        /// so a skill with no extra declarations behaves exactly as before. Field extraction isolates its own exceptions --
        /// a malformed declaration degrades to taking just the message, rather than failing the whole response.
        /// </summary>
        public static bool TryGetErrorContext(object result, out SkillErrorContext context)
        {
            context = null;
            if (!TryGetError(result, out string errorText))
                return false;

            context = new SkillErrorContext { Message = errorText };

            try
            {
                if (TryGetMemberValue(result, "errorCode", out var codeValue) && codeValue != null &&
                    SkillErrorCodeExtensions.TryParseWire(codeValue.ToString(), out var parsedCode))
                    context.Code = parsedCode;

                if (TryGetMemberValue(result, "retryStrategy", out var retryValue) && retryValue != null)
                {
                    var retry = retryValue.ToString().Trim();
                    if (retry.Length > 0)
                        context.RetryStrategy = retry;
                }

                if (TryGetMemberValue(result, "relatedSkills", out var relatedValue))
                    context.RelatedSkills = ToStringList(relatedValue);

                if (TryGetMemberValue(result, "suggestedFixes", out var fixesValue))
                    context.SuggestedFixes = ToSuggestedFixes(fixesValue);

                context.Extra = CollectExtraErrorFields(result);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"Skill error context extraction failed, falling back to message only: {ex.Message}");
            }

            return true;
        }

        /// <summary>
        /// Fields on a skill's error object that the response envelope already models. Everything else is forwarded as-is,
        /// so the diagnostic information a skill wrote itself survives the classification process.
        /// </summary>
        private static readonly HashSet<string> ReservedErrorFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "error", "errorCode", "retryStrategy", "relatedSkills", "suggestedFixes",
            "status", "skill", "details", "retryAfterSeconds", "success"
        };

        /// <summary>
        /// Collects the non-reserved members on a skill's error object. Anonymous types, dictionaries, and JObject are all supported,
        /// because skills return all three shapes. Isolates its own exceptions: a member that can't be read is skipped, rather than failing the whole response.
        /// </summary>
        private static Dictionary<string, object> CollectExtraErrorFields(object result)
        {
            if (result == null) return null;
            var extra = new Dictionary<string, object>();

            try
            {
                if (result is JObject jsonObject)
                {
                    foreach (var pair in jsonObject)
                    {
                        if (ReservedErrorFields.Contains(pair.Key)) continue;
                        extra[pair.Key] = pair.Value == null || pair.Value.Type == JTokenType.Null
                            ? null
                            : pair.Value.ToObject<object>();
                    }
                }
                else if (result is IDictionary<string, object> dictionary)
                {
                    foreach (var pair in dictionary)
                    {
                        if (ReservedErrorFields.Contains(pair.Key)) continue;
                        extra[pair.Key] = pair.Value;
                    }
                }
                else
                {
                    var resultType = result.GetType();
                    foreach (var property in resultType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (ReservedErrorFields.Contains(property.Name) ||
                            property.GetIndexParameters().Length > 0)
                            continue;
                        try { extra[property.Name] = property.GetValue(result); }
                        catch { }
                    }
                    foreach (var field in resultType.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (ReservedErrorFields.Contains(field.Name) || extra.ContainsKey(field.Name))
                            continue;
                        try { extra[field.Name] = field.GetValue(result); }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"Skill error extra-field extraction failed: {ex.Message}");
                return null;
            }

            return extra.Count > 0 ? extra : null;
        }

        /// <summary>Accepts a string, string[], JArray, or any sequence; returns null when empty.</summary>
        private static List<string> ToStringList(object value)
        {
            if (value == null || value is JObject)
                return null;

            var items = new List<string>();

            if (value is string single)
            {
                if (!string.IsNullOrWhiteSpace(single))
                    items.Add(single);
            }
            else if (value is System.Collections.IEnumerable sequence)
            {
                foreach (var entry in sequence)
                {
                    var text = entry?.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                        items.Add(text);
                }
            }

            return items.Count > 0 ? items : null;
        }

        /// <summary>
        /// Accepts a single suggested fix or a sequence of them, in either the full shape
        /// (<c>{ action, skill, args, reason }</c>) or as a bare hint string.
        /// </summary>
        private static List<SuggestedFix> ToSuggestedFixes(object value)
        {
            if (value == null)
                return null;

            var fixes = new List<SuggestedFix>();

            if (value is string || value is JObject || value is SuggestedFix)
            {
                var single = ToSuggestedFix(value);
                if (single != null)
                    fixes.Add(single);
            }
            else if (value is System.Collections.IEnumerable sequence)
            {
                foreach (var entry in sequence)
                {
                    var one = ToSuggestedFix(entry);
                    if (one != null)
                        fixes.Add(one);
                }
            }

            return fixes.Count > 0 ? fixes : null;
        }

        private static SuggestedFix ToSuggestedFix(object entry)
        {
            if (entry == null)
                return null;

            if (entry is SuggestedFix typed)
                return typed;

            if (entry is string hint)
                return string.IsNullOrWhiteSpace(hint) ? null : new SuggestedFix { action = "retry", reason = hint };

            var token = entry as JToken ?? JToken.FromObject(entry);

            if (token.Type == JTokenType.String)
            {
                var text = token.Value<string>();
                return string.IsNullOrWhiteSpace(text) ? null : new SuggestedFix { action = "retry", reason = text };
            }

            if (!(token is JObject obj))
                return null;

            var fix = new SuggestedFix
            {
                action = ReadString(obj, "action"),
                skill = ReadString(obj, "skill"),
                reason = ReadString(obj, "reason"),
            };

            var argsToken = obj.GetValue("args", StringComparison.OrdinalIgnoreCase);
            if (argsToken != null && argsToken.Type != JTokenType.Null)
                fix.args = argsToken;

            bool empty = string.IsNullOrEmpty(fix.action) && string.IsNullOrEmpty(fix.skill) &&
                         string.IsNullOrEmpty(fix.reason) && fix.args == null;
            return empty ? null : fix;
        }

        private static string ReadString(JObject obj, string name)
        {
            var token = obj.GetValue(name, StringComparison.OrdinalIgnoreCase);
            return token == null || token.Type == JTokenType.Null ? null : token.ToString();
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

// Producer:Betsy
