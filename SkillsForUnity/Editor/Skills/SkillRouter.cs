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
    public static partial class SkillRouter
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
            // Declared RequiresPackages that are not installed. Stays empty for a ReadOnly skill (reported as a warning,
            // so status probes keep answering) and while the package list is still loading (unknown is not absent).
            public List<string> MissingPackages { get; } = new List<string>();
            public List<string> Warnings { get; } = new List<string>();
            public List<object> ParameterDetails { get; } = new List<object>();
            public bool Valid => MissingParams.Count == 0 && UnknownParams.Count == 0 && TypeErrors.Count == 0 && SemanticErrors.Count == 0 && MissingPackages.Count == 0;
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
            // Parameter names required despite a CLR default; see UnitySkillAttribute.RequiredParams.
            public string[] RequiredParams;
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
            // SkillParamAttribute text, index-aligned with Parameters; null when no parameter carries one.
            public string[] ParameterDescriptions;
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
        private static volatile string _cachedMetaV2;   // ?wire=v2: same constants minus workflowTrackedSkills (flags already carry it)
        private static Dictionary<string, List<SkillInfo>> _outputIndex;

        // Cache of filtered (scoped) schema/manifest output, keyed by the canonical form of the
        // query string. The full schema/manifest already has a cache (_cachedSchema/_cachedManifest),
        // but the filtered variants (?category=... etc.) used to be rebuilt and re-serialized on
        // every request -- and that's exactly the path an agent uses to save tokens (scoped is
        // roughly 24KB, full roughly 707KB). As long as the skill set doesn't change, a given query's
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

        /// <summary>Envelope-level body keys every skill accepts (paging, verbose, _confirm), case-insensitive.</summary>
        internal static bool IsReservedBodyParameter(string name) =>
            !string.IsNullOrEmpty(name) && _reservedBodyParameters.Contains(name);

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
            {"duplicate", SkillOperation.Create}, {"copy", SkillOperation.Create}, {"clone", SkillOperation.Create},
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

        // Words that carry no routing information; they never earn name/tag/description credit. Operation verbs are not
        // listed here -- they still count as whole-token name matches (gameobject_duplicate for "duplicate") and drive the
        // operation bonus, but no longer as substrings of unrelated names or descriptions (ui_set_rect used to win "set box
        // collider center and size" on `name:set`).
        private static readonly HashSet<string> _intentStopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "the", "and", "or", "to", "of", "on", "in", "with", "for", "its", "it", "is", "be", "by", "from", "into",
            "at", "as", "that", "this", "my", "me", "then", "please", "value", "values", "new", "using", "use", "via", "under",
            "onto", "all", "one", "some", "any", "existing", "current", "given", "specific",
            "的", "了", "把", "给", "并", "和", "与", "将", "请", "一个", "一下",
        };

        // Unity component / concept words -> the modules that own them. A hit adds the category bonus to those modules and, when
        // the intent has no explicit module word of its own (see GetRecommendations), unlocks the generic component_* rule so that
        // "set box collider center" finds component_set_property although neither "box" nor "collider" appears in its name.
        private static readonly Dictionary<string, SkillCategory[]> _componentTypeHints = new Dictionary<string, SkillCategory[]>(StringComparer.OrdinalIgnoreCase)
        {
            {"collider", new[]{SkillCategory.Component, SkillCategory.Physics}},
            {"boxcollider", new[]{SkillCategory.Component, SkillCategory.Physics}},
            {"spherecollider", new[]{SkillCategory.Component, SkillCategory.Physics}},
            {"capsulecollider", new[]{SkillCategory.Component, SkillCategory.Physics}},
            {"meshcollider", new[]{SkillCategory.Component, SkillCategory.Physics}},
            {"rigidbody", new[]{SkillCategory.Component, SkillCategory.Physics}},
            {"joint", new[]{SkillCategory.Component, SkillCategory.Physics}},
            {"renderer", new[]{SkillCategory.Component, SkillCategory.Material}},
            {"meshrenderer", new[]{SkillCategory.Component, SkillCategory.Material}},
            {"meshfilter", new[]{SkillCategory.Component}},
            {"skinnedmeshrenderer", new[]{SkillCategory.Component}},
            {"audiosource", new[]{SkillCategory.Component, SkillCategory.Audio}},
            {"light", new[]{SkillCategory.Light, SkillCategory.Component}},
            {"camera", new[]{SkillCategory.Camera, SkillCategory.Component}},
            {"animator", new[]{SkillCategory.Animator, SkillCategory.Component}},
            {"transform", new[]{SkillCategory.GameObject}},
            {"position", new[]{SkillCategory.GameObject}},
            {"rotation", new[]{SkillCategory.GameObject}},
            {"scale", new[]{SkillCategory.GameObject}},
            {"field", new[]{SkillCategory.Component}},
            {"property", new[]{SkillCategory.Component}},
            {"properties", new[]{SkillCategory.Component}},
        };

        // Categories that name a whole module in _categoryKeywords; when the intent names one of these explicitly, the generic
        // component_* rule stays off (an intent that says "script" or "light" has already chosen its module).
        private static readonly HashSet<SkillCategory> _genericRuleSuppressors = new HashSet<SkillCategory>
        {
            SkillCategory.Script, SkillCategory.Light, SkillCategory.Camera, SkillCategory.Material, SkillCategory.Animator,
            SkillCategory.Audio, SkillCategory.Scene, SkillCategory.Prefab, SkillCategory.Asset, SkillCategory.UI,
            SkillCategory.UIToolkit, SkillCategory.Package, SkillCategory.Test, SkillCategory.Shader, SkillCategory.Texture,
        };

        private static readonly string[] _genericModifySkills = { "component_set_property", "component_set_property_batch", "component_set_serialized_property" };
        private static readonly string[] _genericQuerySkills = { "component_get_properties", "component_list", "component_get_serialized_properties" };
        private static readonly string[] _genericCreateSkills = { "component_add", "component_add_batch" };
        private static readonly string[] _scriptEditSkills = { "script_append", "script_replace", "script_find_in_file" };
        private static readonly HashSet<string> _scriptContentWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "field", "fields", "method", "methods", "function", "variable", "line", "lines", "code", "using", "property", "member", "attribute"
        };
        // Reuses the JSON settings from SkillsCommon (single definition, no duplication)
        private static readonly JsonSerializerSettings _jsonSettings = SkillsCommon.JsonSettings;

        // Only for the ?wire=v2 payload. Dropping nulls is what actually makes v2's omission semantics ("riskLevel absent means low") save bytes;
        // every v1 path still uses _jsonSettings, to keep output byte-for-byte identical to before v2 existed.
        private static readonly JsonSerializerSettings _jsonSettingsV2 = SkillsCommon.JsonSettingsOmitNull;

        // The query keys BuildFilteredOutput actually uses to filter or branch. Everything else (typos, cache-busting
        // nonces, client telemetry parameters...) gets stripped before entering the cache key -- otherwise every distinct unrecognized value would create
        // a permanent roughly 707KB cache entry (see the MaxCacheEntries comment above _filteredOutputCache).
        // Adding a new key here must also add it to _blankRejectingFilterKeys, or "?newKey=" silently becomes a no-op again.
        private static readonly HashSet<string> _recognizedFilterKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "category", "operation", "tags", "readonly", "q", "names", "summary", "includeSchema", "brief",
            // Surface/wire-format selectors -- listed here so they aren't stripped, but they never narrow the skill set (see _surfaceSelectionKeys).
            "wire", "full"
        };

        // Keys a caller writes when it means "this skill" but guessed the parameter name. Silently stripping these used to hand back the
        // unfiltered ~700KB schema; they are rejected with UNKNOWN_PARAM instead and pointed at names= / q=. Any other unrecognized key
        // (cache-busting nonces, client telemetry) is still stripped silently -- see StripUnrecognizedFilterKeys.
        private static readonly HashSet<string> _skillSelectorLikeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "skill", "skills", "name", "skillname", "skill_name", "id", "ids"
        };

        // These recognized keys select the payload's *shape*, not a subset of skills. They must never be echoed back as "filters,"
        // nor set "filtered" to true: a bare ?wire=v2 is still the complete, unfiltered manifest,
        // and calling it filtered would misrepresent the meaning of totalSkills.
        private static readonly HashSet<string> _surfaceSelectionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "wire", "full"
        };

        // Valid values for ?category= / ?operation=. Stored rather than recomputed each time, because Enum.GetNames allocates a new array
        // on every call, and a manifest GET that misses the cache reads them every time;
        // they're also the list handed back to the caller in a rejection response.
        private static readonly string[] _validCategoryNames = Enum.GetNames(typeof(SkillCategory));
        private static readonly string[] _validOperationNames = Enum.GetNames(typeof(SkillOperation));

        // All recognized query keys, in a *fixed* order, so a query with multiple blank values always names the same key on rejection --
        // the error body, like any other cached response, must be byte-stable for the same query. Keep in sync with _recognizedFilterKeys.
        private static readonly string[] _blankRejectingFilterKeys =
        {
            "category", "operation", "tags", "readonly", "q", "names", "summary", "includeSchema", "brief",
            "wire", "full"
        };

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

            var rawFilters = ParseQueryString(query);
            if (FindSkillSelectorLikeKey(rawFilters) != null)
                return false;
            var filters = StripUnrecognizedFilterKeys(rawFilters);

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
                    json = ResolveWireVersion(filters) == WireV2 ? _cachedMetaV2 : _cachedMeta;
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
