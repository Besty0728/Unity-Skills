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
    /// 把 REST API 请求路由到各 skill 方法。
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
            // 为 true 表示该 skill 自行捕获 workflow 快照；跳过 TrySnapshotTargetsFromArgs 里的
            // 通用执行前快照，避免重复备份。
            public bool SkipAutoPresnapshot;
            // 意图层元数据
            public SkillCategory Category;
            public SkillOperation Operation;
            public string[] Tags;
            public string[] Outputs;
            public string[] RequiresInput;
            public bool ReadOnly;
            // 风险与影响元数据
            public bool MutatesScene;
            public bool MutatesAssets;
            public bool MayTriggerReload;
            public bool MayEnterPlayMode;
            public bool SupportsDryRun;
            // 为 true 表示该 skill 会阻塞主线程数秒以上；有异步 job 路径时 agent 应优先走那条。
            // 见 UnitySkillAttribute.LongRunning。
            public bool LongRunning;
            public string RiskLevel;
            public string[] RequiresPackages;
            // 权限档位。默认 FullAuto，使未标注的 skill 都要过 Approval 闸门；
            // SemiAuto 必须经 [UnitySkill(Mode=...)] 显式声明才生效。
            public SkillMode Mode;
            // 缓存下来，避免每次 Execute/DryRun 重复分配
            public string[] ParameterNames;
            public HashSet<string> AllowedParameterSet;
            // 预先算好的小写形式，供过滤/搜索用（省掉每次查询的 ToLowerInvariant）
            public string NameLower;
            public string DescriptionLower;
            public string[] TagsLower;
        }

        private static volatile Dictionary<string, SkillInfo> _skills;
        private static volatile bool _initialized;
        // 对 SkillsSurfaceProfile.OnChanged 的一次性订阅，在 Initialize() 中接上。
        private static bool _surfaceHookInstalled;

        // 手动录制会话（workflow_begin_task）的脏标记：上次 SaveHistory 时记下的
        //（taskId, snapshotCount）。使被跟踪的 skill 在上次保存之后没有新快照时可跳过一次多余的保存。
        private static string _lastSavedTaskId;
        private static int _lastSavedSnapshotCount = -1;
        // 这四个都必须是 volatile：它们构成 GET 快路径双重检查锁的读侧——
        // HTTP 线程在 _initLock 之外读它们（TryGetCachedGetResponse），主线程在锁内发布它们。
        // 没有 volatile，读侧可能持有被提升出循环的副本，把主线程在切换 profile 时已失效的载荷发出去。
        private static volatile string _cachedManifest;
        private static volatile string _cachedSchema;
        // 裸 GET /skills（目录层）与 GET /skills/meta（会话常量）。两者与上面两个一样是整载荷单例
        // 而非按 query 建键的条目，因此 HTTP 线程快路径无需查 _filteredOutputCache 即可直接返回。
        private static volatile string _cachedBrief;
        private static volatile string _cachedMeta;
        private static Dictionary<string, List<SkillInfo>> _outputIndex;

        // 过滤（限定范围）后的 schema/manifest 缓存，以 query 字符串的规范形式为键。
        // 完整 schema/manifest 已有缓存（_cachedSchema/_cachedManifest），但过滤变体
        //（?category=… 等）过去每次请求都要重建并重新序列化——而那恰恰是 agent 用来省 token 的路径
        //（限定范围约 24KB，完整约 618KB）。在 skill 集合不变的前提下，同一 query 的内容逐字节确定，
        // 因此缓存是安全的；Refresh()（域重载 / skill 增删）时清空。只有已识别的过滤键会进入缓存键
        //（见 StripUnrecognizedFilterKeys），使得取值无界的 query 参数（如用于破坏缓存的 ?nonce=N）
        // 无法每次请求都造出一条几百 KB 的新条目；条目数另由 MaxCacheEntries 硬性封顶作为第二道防线。
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _filteredOutputCache =
            new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

        // _filteredOutputCache 与 _etagCache 共用的硬上限。两者都由 HTTP 线程读、主线程写；
        // 容量检查加 Clear() 无需额外加锁（ConcurrentDictionary.Clear() 本身线程安全），
        // 也把淘汰策略保持为最简单的"整个缓存重置"——真实调用方只会在一小组封闭的
        // category/tag/summary 组合间轮转，所以这条只是防住病态的 query 变化。
        private const int MaxCacheEntries = 256;

        /// <summary>已注册 skill 数量。免去只为取个数而解析 manifest。</summary>
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
        private static bool? _summaryAutoTruncate;

        /// <summary>
        /// Summary 模式自动截断的开关，需显式开启。默认关闭：除调用方用 pageOffset/pageLimit
        /// 显式分页外，非 verbose 结果原样透传。开启后，超过 10 项的非 verbose 数组会被截到第一页，
        /// 并附带 isTruncated 元数据。
        /// </summary>
        public static bool SummaryAutoTruncate
        {
            get
            {
                if (!_summaryAutoTruncate.HasValue)
                    _summaryAutoTruncate = EditorPrefs.GetBool(PrefKeySummaryAutoTruncate, false);
                return _summaryAutoTruncate.Value;
            }
            set
            {
                _summaryAutoTruncate = value;
                EditorPrefs.SetBool(PrefKeySummaryAutoTruncate, value);
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
                ["targetName"] = "camera_look_at 只接受世界坐标 x/y/z，不支持对象名。"
            },
            ["timeline_list_tracks"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["path"] = "timeline_list_tracks 的 path 是场景层级路径，不是 Assets 资源路径。"
            }
        };

        // ========== 意图同义词映射表 ==========

        private static readonly Dictionary<string, string[]> _synonymMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // 中文 → 英文
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
            // 英文别名
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
        /// 按精确匹配 + 子串匹配把关键词对到字典上（子串匹配是为未分词的中文准备的）。
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
        // 复用 SkillsCommon 里的 JSON 设置（单一定义，不重复）
        private static readonly JsonSerializerSettings _jsonSettings = SkillsCommon.JsonSettings;

        // 仅用于 ?wire=v2 载荷。丢弃 null 才让 v2 的省略语义（"riskLevel 缺席即为 low"）真正省下字节；
        // 所有 v1 路径继续用 _jsonSettings，以保证输出与 v2 出现之前逐字节一致。
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

                // 装在这里而不是静态构造函数里：所有能产出缓存输出字符串的路径都先经过 Initialize()，
                // 因此等到出现"切换 profile 会使之失效"的缓存时，这个钩子必定已在监听。
                // 域重载时随其余静态字段一起重置。
                if (!_surfaceHookInstalled)
                {
                    SkillsSurfaceProfile.OnChanged += InvalidateOutputCaches;
                    _surfaceHookInstalled = true;
                }

                var skills = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);
                var trackedSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 使用 Unity 编辑器索引直接查询 Skill 方法，避免在 Domain Reload 后枚举全部程序集和类型。
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

                _skills = skills; // 整体构建完毕后原子赋值
                _workflowTrackedSkills = trackedSkills;

                // 反向索引：输出字段 → 产出它的 skill
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
        /// 当前 surface profile 对外暴露的 skill 集合——所有对外的发现面
        /// （manifest、schema、过滤后的 manifest/schema、brief、recommend、snapshot）
        /// 都必须枚举它而不是 <c>_skills.Values</c>。唯一刻意的例外是 <see cref="ValidateMetadata"/>，
        /// 它审计注册表本身，必须看到全部。
        ///
        /// 仅限主线程（要读 profile，首次调用可能命中 EditorPrefs）。对每个调用方都成立：
        /// HTTP 线程快路径读到的永远只是本方法参与构建出来的字符串。
        /// </summary>
        private static IEnumerable<SkillInfo> VisibleSkills()
        {
            // 默认 profile 下返回与之前同一个实例——不分配，也不逐 skill 判定。
            if (SkillsSurfaceProfile.IsFull)
                return _skills.Values;
            return _skills.Values.Where(s => !SkillsSurfaceProfile.IsExcluded(s));
        }

        /// <summary>
        /// 真正对外提供的、被 workflow 跟踪的 skill 名，顺序与原始集合一致。
        /// 携带该区块的载荷都是对外的，因此必须与 <see cref="VisibleSkills"/> 出自同一权威——
        /// 在此列出一个被隐藏的名字，正是 profile 要防的泄漏，而且泄漏的是注册表里最要紧的那一半
        ///（被跟踪的 skill 按定义都是写操作，而写操作恰恰是 profile 要撤下的）。
        ///
        /// 默认的完整 profile 下过滤不到任何东西，因此该数组——以及由它构建的每个 v1 信封的字节——
        /// 与未过滤集合完全相同。仅限主线程，理由同 VisibleSkills。
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
        /// 丢弃所有缓存的输出字符串，但不重跑 skill 发现。挂在
        /// <see cref="SkillsSurfaceProfile.OnChanged"/> 上：切换 profile 不改变 skill 注册表，
        /// 但由它构建的每个载荷都会变，所以字符串必须重建，反射则不必重做。
        /// ETag 顺带自动跟上——<c>_etagCache</c> 的条目只在其源字符串与当前缓存引用相同时才有效，
        /// 而重建出的字符串内容不同，哈希本来就不一样。
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
        /// 目录层——裸 <c>GET /skills</c>（以及 <c>?brief=1</c>）现在返回的内容。
        /// 与完整 manifest 一样缓存为单个字符串：同一 skill 集合下载荷字节稳定，
        /// 因此 HTTP 线程快路径可以带一个稳定的 ETag 直接返回。
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
        /// <c>GET /skills/meta</c>——manifest 信封中会话恒定的那一半（category 与 operation 枚举、
        /// 保留的请求体参数名、被 workflow 跟踪的 skill 列表），外加 <c>?wire=v2</c> 条目省略掉的
        /// 字段默认值。v2 载荷丢掉这些区块并指向此处，使 agent 每会话只付一次代价，
        /// 而不是每次限定范围拉取都付。
        ///
        /// 除 <c>workflowTrackedSkills</c> 之外，此处一切都满足"会话恒定"：该字段会被 surface profile
        /// 过滤（见 <see cref="VisibleWorkflowTrackedSkills"/>），因此用户切换 profile 时会变——
        /// 切换时缓存与其 ETag 都会被丢弃，<c>metaHint</c> 也说明了这一点。
        /// 为了恢复字面上的恒定而撤掉过滤，就等于把用户选择隐藏的名字发出去。
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
                    // 此处刻意不放 surfaceProfile 字段：profile 随时可被用户切换，把一个实时值混进
                    // 一份"告诉 agent 每会话只拉一次"的载荷里，只会让人从这里读到过期值。
                    // /health 是它唯一的权威，且每条拒绝响应也都带着它。这与 workflowTrackedSkills
                    // 被 profile 过滤是两回事——用户撤下的名字绝不能发出去，
                    // 下面的 hint 说明其后果（这一个区块可能在会话中途变化）而不是隐瞒它。
                    metaHint = "SESSION CONSTANTS — fetch once, reuse for the whole session. The enums, reserved parameters and defaults change only with the plugin version; 'workflowTrackedSkills' lists only what the active surface profile offers, so it moves (and the ETag changes) if the user switches profile mid-session. 'defaults' states the values ?wire=v2 omits from skill entries: a missing riskLevel is \"low\", a missing supportsDryRun is true, and a flag absent from 'flags' is false. For the live surface profile read 'surfaceProfile' on GET /health — it is user-switchable and deliberately not mirrored here."
                }, _jsonSettingsV2);
                return _cachedMeta;
            }
        }

        /// <summary>给定名字的 skill 是否已注册。</summary>
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
        /// 执行一个 skill。<paramref name="captureDiff"/> 为 true 时（POST /skill/{name}?diff=1），
        /// 以纯旁路观察者的身份捕获一份语义场景 diff，作为顶层 "sceneDiff" 字段附在成功响应上——
        /// 告诉调用方本次操作实际改了什么。diff 绝不影响执行：undo/workflow/错误分支一概不动，
        /// 任何 diff 失败只让 sceneDiff 降级为 {error:...}，不影响 skill 结果。
        /// captureDiff:false 时输出与原来逐字节一致。
        /// </summary>
        public static string Execute(string name, string json, bool captureDiff)
        {
            Initialize();
            if (!_skills.TryGetValue(name, out var skill))
            {
                return ResolveSkillNotFound(name);
            }

            bool autoStartedWorkflow = false;
            // 自动 workflow 路径下 EndTask() 的持久化开销，以 workflowEndMs 附在成功信封上。
            // 其他路径一律为 null，以保证输出逐字节不变。
            long? workflowEndMs = null;
            var wrapWithUndoTransaction = !skill.ReadOnly && !_transactionlessSkills.Contains(name);
            int undoGroup = -1;
            int workflowSnapshotCountBefore = WorkflowManager.CurrentTask?.snapshots?.Count ?? 0;
            // 在持久化的编辑器变更日志里，把本次调用造成的改动（含帧末的 ObjectChangeEvent）
            // 归因到 REST。
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

                // surface profile 闸门。必须在权限闸门*之前*运行，这个顺序本身就是契约：
                // 权限档位回答"这个 skill 可以跑吗"，profile 回答"用户到底有没有把它放进菜单"。
                // Bypass 模式与白名单是授权，因此不能解除一项排除——只有用户把 profile 切回完整才能。
                // 若把 profile 放在第二位，Bypass 就能发出面板上标为隐藏的 skill。
                var surfaceGate = ApplySurfaceGate(skill, name);
                if (surfaceGate != null)
                    return surfaceGate;

                // 权限档位闸门。放在高风险确认闸门之前，使既是 FullAuto 又属高风险的 skill
                // 先报 MODE_RESTRICTED；只有当该 skill 本来就允许运行时，ConfirmationToken 那步才有意义。
                var modeGate = ApplyModeGate(skill, name, validation);
                if (modeGate != null)
                    return modeGate;

                // 确认闸门：启用 ConfirmationTokenService.RequireConfirmation 后，
                // 高风险 skill 需要一个显式的一次性 token。
                // 默认关闭——在 Window > UnitySkills > Server > Settings 中开启。
                if (ConfirmationTokenService.RequireConfirmation && ConfirmationTokenService.IsHighRisk(skill))
                {
                    var gateResult = ApplyConfirmationGate(skill, name, json, validation);
                    if (gateResult != null)
                        return gateResult;
                }

                var args = validation.Args;
                var invoke = validation.InvokeArgs;

                // 语义 diff 的前捕获（?diff=1）。纯旁路观察者，位置在权限闸门之后、invoke 之前；
                // 只读 skill 跳过（没有执行可 diff）。CaptureBefore 内部已完全隔离异常。
                SkillSceneDiff.DiffCapture diffCapture = null;
                if (captureDiff && !skill.ReadOnly)
                    diffCapture = SkillSceneDiff.CaptureBefore(args);

                if (wrapWithUndoTransaction)
                {
                    UnityEditor.Undo.IncrementCurrentGroup();
                    UnityEditor.Undo.SetCurrentGroupName($"Skill: {name}");
                    undoGroup = UnityEditor.Undo.GetCurrentGroup();
                }

                // ========== 自动 workflow 录制 ==========
                if (skill.TracksWorkflow && !WorkflowManager.IsRecording)
                {
                    var desc = $"{name} - {(json?.Length > 80 ? json.Substring(0, 80) + "..." : json ?? "")}";
                    WorkflowManager.BeginTask(name, desc);
                    autoStartedWorkflow = true;
                }

                // 在 skill 执行*之前*自动快照目标对象，以支持回滚。
                // 自行管理专用快照的 skill 通过 SkipAutoPresnapshot 退出，避免产生多余的通用备份。
                if (WorkflowManager.IsRecording && !skill.SkipAutoPresnapshot)
                {
                    TrySnapshotTargetsFromArgs(args);
                }
                // ==============================================

                // verbose 控制
                bool verbose = true; // 未指定时默认 true，以保持直接调用的向后兼容
                if (args.TryGetValue("verbose", StringComparison.OrdinalIgnoreCase, out var verboseToken))
                {
                    try
                    {
                        verbose = verboseToken.ToObject<bool>();
                    }
                    catch (Exception)
                    {
                        // ToObject<bool> 接受 true/false/"true"/1 但拒绝 "1"/"yes" 之类。
                        // 先试常见的字符串形式；其余都属客户端错误，必须以
                        // TYPE_MISMATCH + fix_and_retry 呈现——下面那个通用 catch 会把它误标为
                        // INTERNAL "[Transactional Revert]" + wait_and_retry，
                        // 让 agent 在一个只有它自己能修的请求体上陷入重试循环。
                        var raw = verboseToken.Type == JTokenType.String
                            ? verboseToken.Value<string>()?.Trim().ToLowerInvariant()
                            : null;
                        if (raw == "true" || raw == "1" || raw == "yes")
                            verbose = true;
                        else if (raw == "false" || raw == "0" || raw == "no")
                            verbose = false;
                        else
                        {
                            // 此时还没有 invoke 任何东西；把上面开启的记账回退掉，
                            // 与下面的 catch 处理保持一致。
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

                // Summary 模式的分页控制。
                // 若 skill 自己声明了同名参数则跳过：'limit' 属于 asset_find/light_find_all/… 自己，
                // 必须作为它们的参数送达，而不能被信封层当作分页吃掉（那还会把小结果也包一层）。
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
                        // 此时还没有 invoke 任何东西；把上面开启的记账回退掉。
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

                    // 全部 skill 的业务错误都汇入此处。skill 自己声明了什么，就逐字段优先采用；
                    // 分类器只补空缺，使那约 700 个只返回 { error = "..." } 的 skill 也能拿到错误码
                    // 与重试策略，而不是统一的 SKILL_ERROR + abort。声明了 errorCode 还会带动其余字段，
                    // 让部分声明保持自洽。
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

                // ========== 自动 workflow 收尾 ==========
                if (autoStartedWorkflow)
                {
                    // 自动 workflow 路径下持久化职责全在 EndTask（它内部会调 SaveHistory）。
                    // 此处测量该开销用于观测。
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    WorkflowManager.EndTask();
                    sw.Stop();
                    workflowEndMs = sw.ElapsedMilliseconds;
                }
                else if (WorkflowManager.IsRecording)
                {
                    // 手动会话（workflow_begin_task）：否则每个被跟踪的 skill 每次调用都要存一遍。
                    // 当前任务自上次保存以来没有新快照时跳过保存。
                    if (ManualSessionIsDirty(WorkflowManager.CurrentTask))
                        WorkflowManager.SaveHistory();
                }
                // ========================================

                if (wrapWithUndoTransaction)
                {
                    // 提交事务
                    UnityEditor.Undo.CollapseUndoOperations(undoGroup);

                    // 经 REST 调用的 skill 不会经过推进 Unity undo 栈的那些常规菜单/鼠标事件边界。
                    // 因此显式切到下一组，使 editor_undo/editor_redo 作用于刚完成的这次改动。
                    if (!skill.ReadOnly)
                        UnityEditor.Undo.IncrementCurrentGroup();
                }

                // 语义 diff 的后捕获与对比（?diff=1）。以顶层 "sceneDiff" 附在成功信封上；
                // 默认路径下为 null，以保证输出逐字节不变。BuildSceneDiff 已隔离异常——
                // diff 绝不会弄坏响应，而上面报了错的 skill 根本到不了这里。
                JToken sceneDiff = BuildSceneDiff(captureDiff, skill, diffCapture, result);

                if (!verbose && result != null)
                {
                    // 带分页的 "Summary Mode" 逻辑
                    var jsonResult = JToken.FromObject(result);

                    var arr = FindPageArray(jsonResult, out var arrayProperty);
                    if (arr != null && ((SummaryAutoTruncate && arr.Count > 10) || offset.HasValue || limit.HasValue))
                    {
                        int startIndex = offset ?? 0;
                        int pageSize = limit ?? 5;

                        // 夹到合法范围
                        if (startIndex >= arr.Count)
                        {
                            // offset 越过数组边界，返回空页
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

                        // 返回带分页元数据的包装对象
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

                // 完整模式（verbose=true 或结果本身很小）：原样返回
                return SerializeSuccessResponse(result, sceneDiff, workflowEndMs);
            }
            catch (TargetInvocationException ex)
            {
                // 出错时清理自动开启的 workflow
                if (autoStartedWorkflow && WorkflowManager.IsRecording)
                    WorkflowManager.AbortTask();
                else if (WorkflowManager.IsRecording)
                    WorkflowManager.TruncateCurrentTask(workflowSnapshotCountBefore);

                if (undoGroup >= 0)
                {
                    // 回滚事务
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
                // 请求体格式错误——ValidateParameters 内部的 JObject.Parse 在任何改动或 undo 组
                // 开启之前就抛了。这是客户端错误，不是服务端或事务失败：返回
                // InvalidJson + fix_and_retry，让 agent 去改请求体而不是在 wait_and_retry 上打转
                //（下面那个通用 catch 会把它误标为 "[Transactional Revert]"）。与 DryRun 保持一致。
                return SkillErrorResponse.Build(
                    SkillErrorCode.InvalidJson,
                    $"Invalid JSON: {ex.Message}",
                    skill: name,
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
            }
            catch (Exception ex)
            {
                // 出错时清理自动开启的 workflow
                if (autoStartedWorkflow && WorkflowManager.IsRecording)
                    WorkflowManager.AbortTask();
                else if (WorkflowManager.IsRecording)
                    WorkflowManager.TruncateCurrentTask(workflowSnapshotCountBefore);

                if (undoGroup >= 0)
                {
                    // 回滚事务
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
                        // 恒定输出，两种取值都发。此前该标志只存在于 ?wire=v2 的稀疏 "flags" 数组里，
                        // 于是默认面（v1 载荷与本预览）从不提及"即将发起的这次调用会把主线程
                        //（连同整个 HTTP 队列）阻塞数秒"。预览正是该说这件事的地方：
                        // 只在 true 时输出会让"缺席"在"很快"与"旧版本"之间产生歧义，故两种取值都发。
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
                // JSON 合法也仍可能让 plan/语义校验崩掉（例如 NRE）。把这种情况报成 INVALID_JSON
                // 会让 agent 反复重写一个本来没问题的请求体；因此照 Execute 的 catch 分法，
                // 如实上报真正的失败。
                return SkillErrorResponse.Build(
                    SkillErrorCode.Internal,
                    $"Dry-run failed: {ex.Message}",
                    skill: name,
                    details: new { exceptionType = ex.GetType().Name },
                    retryStrategy: SkillErrorResponse.Abort);
            }
        }

        /// <summary>
        /// 对 <see cref="ApplyModeGate"/> 将给出的判定做只读预览——使一次 dry run 能回答
        /// "这个调用到底允不允许跑"，而不是让 agent 到 execute 时才撞上
        /// MODE_FORBIDDEN / MODE_RESTRICTED 这堵墙。
        ///
        /// 刻意从 <see cref="SkillsModeManager.CurrentMode"/>、白名单和
        /// <see cref="SkillsModeManager.IsForbiddenInSemi"/> 重新推导结论，而不是直接调
        /// <c>CheckAccess</c>：CheckAccess 会消耗本线程的一次性授权 token，而包着它的闸门还会
        /// 发起授权请求、写审计条目。预览一件都不该做。下面的顺序与 CheckAccess 完全一致，
        /// 只少了一次性检查——待用的一次性放行属于紧随授权之后的那一次 execute 调用，不属于预览，
        /// 在此上报它等于宣传一个下一个调用方可能拿不到的许可。
        ///
        /// 应当读作预测而非预留：mode 或白名单可能在本次 dry run 与 execute 之间发生变化，
        /// 因此 <c>allowed:true</c> 不是保证。
        ///
        /// 判定依据是 skill 自身的元数据；对除"携带写入"入口
        ///（batch_execute / batch_retry_failed，以及 workflow 的 undo/redo/revert 系列）之外的
        /// 所有 skill，这就是全部依据。那些入口在执行时是按一份本预览拿不到的载荷的分类结果被拒的，
        /// 因此此处附加一条说明而非给出判定——见
        /// <see cref="SkillsSurfaceProfile.CarriedWritePreviewGate"/>。
        /// </summary>
        private static object BuildAuthorizationPreview(SkillInfo skill)
        {
            var verdict = BuildModeAuthorizationPreview(skill);

            // 已在 skill 层被拒：SURFACE_EXCLUDED 区块已把载荷说明要讲的都讲了，
            // 说两遍会被读成两堵不同的墙。
            if (SkillsSurfaceProfile.IsExcluded(skill))
                return verdict;

            var payloadGate = SkillsSurfaceProfile.CarriedWritePreviewGate(skill.Name);
            if (payloadGate == null)
                return verdict;

            // 只追加、绝不替换，使原有字段的名称、取值与顺序都不变，唯一新增的是那条说明。
            var annotated = JObject.FromObject(verdict);
            foreach (var property in JObject.FromObject(payloadGate).Properties())
                annotated[property.Name] = property.Value;
            return annotated;
        }

        /// <summary>
        /// <see cref="BuildAuthorizationPreview"/> 中只看元数据的那一半：先判 surface 排除，
        /// 再按 CheckAccess 的顺序走 mode/白名单的判定阶梯。
        /// </summary>
        private static object BuildModeAuthorizationPreview(SkillInfo skill)
        {
            var mode = SkillsModeManager.CurrentMode;
            var modeWire = SkillsModeManager.ModeToWire(mode);
            bool allowlisted = SkillsModeManager.IsInAllowlist(skill.Name);

            // 首先，与 execute 路径一致：排除的优先级高于 Bypass 和白名单，
            // 因此若对一个"在白名单但被隐藏"的 skill 在此报 allowed:true，
            // agent 会径直撞上一个刚被告知不会遇到的 SURFACE_EXCLUDED。
            // dry run 本身永不被拦——预览一个被排除的 skill，正是 agent 得知"用户需要改什么"的途径。
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

        // 序列化成功信封。sceneDiff（?diff=1）与 workflowEndMs（自动 workflow 的 EndTask 持久化
        // 耗时，毫秒）都只在存在时作为顶层字段追加；两者都不存在时，输出与引入 diff 之前逐字节一致。
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

        // 为一次成功的 ?diff=1 执行构建 sceneDiff 载荷。只读 skill 只给一条 note（没什么可 diff）；
        // 其余委托给 SkillSceneDiff.Build。完全隔离——任何失败都降级为 {error:...}，绝不扰动响应信封。
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
        /// 同名的显式 RequiresInput 元数据会覆盖 CLR 层面的"可选（有默认值）"判定。
        /// 否则，只有既无默认值又不接受 null 的参数才算必填。
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

        // ========== 过滤后的 manifest ==========

        /// <summary>
        /// 按 query 参数返回过滤后的 skill manifest。
        /// 支持 category、operation、tags、readOnly、q（文本搜索）。
        /// </summary>
        public static string GetFilteredManifest(string queryString) => BuildFilteredOutput(queryString, "manifest", out _);

        /// <summary>
        /// 过滤条件与 GetFilteredManifest 相同（category/operation/tags/readOnly/q），
        /// 但把载荷的 manifestType 标为 "schema"——支撑 GET /skills/schema?category=…
        ///（限定范围的 schema，只需一个 category 时免去拉取整份约 618KB 的 schema）。
        /// </summary>
        public static string GetFilteredSchema(string queryString) => BuildFilteredOutput(queryString, "schema", out _);

        /// <summary>
        /// 在 <see cref="GetFilteredManifest(string)"/> 之外，额外告知返回的字符串是拒绝响应
        /// 还是 manifest。HTTP 层需要这一区分，且不嗅探文本就无法从载荷里还原出来，原因有两点：
        /// 错误必须答 400 而不是 200；并且不能给它 ETag——被缓存的 400 响应体会让客户端下一次
        /// If-None-Match 得到一个完全没有 body 的 304，读起来就像"你的查询没问题，且什么都没变"。
        /// </summary>
        public static string GetFilteredManifest(string queryString, out bool isError) =>
            BuildFilteredOutput(queryString, "manifest", out isError);

        /// <summary><see cref="GetFilteredManifest(string, out bool)"/> 的 schema 对应版本。</summary>
        public static string GetFilteredSchema(string queryString, out bool isError) =>
            BuildFilteredOutput(queryString, "schema", out isError);

        // BuildFilteredOutput 真正用来过滤或分支的 query 键。其余一切（拼写错误、用于破坏缓存的
        // nonce、客户端埋点参数…）在进入缓存键之前就被剔除——否则每个不同的未识别取值都会造出
        // 一条永久的约 618KB 缓存条目（见 _filteredOutputCache 上方 MaxCacheEntries 的注释）。
        // 在此新增一个键，必须同时加进 _blankRejectingFilterKeys，否则 "?newKey=" 又会变成静默空操作。
        private static readonly HashSet<string> _recognizedFilterKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "category", "operation", "tags", "readonly", "q", "summary", "includeSchema", "brief",
            // 面/格式选择器——列在此处以免被剔除，但它们从不缩小 skill 集合（见 _surfaceSelectionKeys）。
            "wire", "full"
        };

        // 这些已识别的键选的是载荷*形状*而非 skill 子集。它们既不得作为 "filters" 回显，
        // 也不得把 "filtered" 置为 true：单独的 ?wire=v2 仍是完整未过滤的 manifest，
        // 说成过滤过的会误报 totalSkills 的含义。
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
        /// 剔除 <see cref="_surfaceSelectionKeys"/>，只留真正缩小 skill 集合的键。
        /// 无需剔除任何东西时原样返回入参实例——正是这一点让所有 v2 之前的 query
        /// 回显出的 <c>filters</c> 对象逐字节不变。
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

        // 线上格式版本。v1 是历史载荷，且永远保持为默认：未识别的 ?wire 取值解析为 v1 而不报错，
        // 因此拼写错误绝不会静默地把一个调用方解析不了的形状发给它。
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

        /// <summary>manifest 家族的 GET 由哪一份缓存字符串应答。</summary>
        private enum GetSurface
        {
            /// <summary>_cachedManifest / _cachedSchema —— 原封不动的 v1 完整载荷。</summary>
            FullV1,
            /// <summary>_cachedBrief —— 裸 GET /skills，以及两条路径上的 ?brief=1。</summary>
            Brief,
            /// <summary>_cachedMeta —— GET /skills/meta。</summary>
            Meta,
            /// <summary>_filteredOutputCache —— 所有限定范围、summary 或 wire=v2 的变体。</summary>
            Keyed
        }

        private const string BriefCacheKey = "manifest|__brief__";
        private const string MetaCacheKey = "meta|__full__";

        /// <summary>
        /// "这个 query 选的是哪个面、用哪个缓存键"的唯一事实来源。主线程构建器
        /// （<see cref="BuildFilteredOutput"/>）与 HTTP 线程快路径（<see cref="BuildGetCacheKey"/>）
        /// 都调用它——两边一旦不一致，快路径就会用另一个面的字节来应答本面的请求。
        ///
        /// 在 <paramref name="filters"/> 已剔除无关键的前提下，判定顺序为：
        /// <list type="number">
        /// <item>meta 路径 → <see cref="GetSurface.Meta"/>。</item>
        /// <item>?brief 为真，或裸 /skills 请求（无缩小范围的过滤且无 ?full）→
        /// <see cref="GetSurface.Brief"/>。这是 v2.7 的默认值翻转：裸 GET /skills 过去返回约 618KB
        /// 的 manifest，现在返回目录；?full=1 可恢复旧行为。</item>
        /// <item>无任何缩小范围的键且 wire 为 v1 → <see cref="GetSurface.FullV1"/>
        /// （裸 /skills/schema，以及 /skills?full=1）。</item>
        /// <item>其余一切 → <see cref="GetSurface.Keyed"/>。</item>
        /// </list>
        /// Brief 与 wire 无关（它不携带任何可精简的逐 skill 标志），因此两种 wire 共用一条缓存条目，
        /// 也因此共用一个 ETag。
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
            // ?full 从键里剔除，?wire 不剔除。请求一旦走到 Keyed，?full 唯一的作用
            //（击败上面分支里的 brief 默认值）已经完成，不可能再影响字节——留着它只会把同一份载荷
            // 拆成两条内容相同的几百 KB 条目（/skills/schema?wire=v2 与 ?full=1&wire=v2）。
            // ?wire 则确实会选出不同的字节，必须留在标识里。
            return BuildFilteredOutputCacheKey(StripFullFlagKey(filters), manifestType);
        }

        /// <summary>
        /// 只剔除 <c>full</c> 键，其余保持插入顺序。与
        /// <see cref="StripSurfaceSelectionKeys"/> 一样，无需改动时直接返回入参实例。
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

        // ?category= / ?operation= 的合法取值。存下来而不是每次重算，因为 Enum.GetNames 每次调用
        // 都要新分配一个数组，而未命中缓存的 manifest GET 每次都会读它们；
        // 它们同时也是拒绝响应里回给调用方的那份列表。
        private static readonly string[] _validCategoryNames = Enum.GetNames(typeof(SkillCategory));
        private static readonly string[] _validOperationNames = Enum.GetNames(typeof(SkillOperation));

        /// <summary>
        /// 拒绝未知的 <c>?category=</c> / <c>?operation=</c> 取值，以及留空的缩小范围键，
        /// 而不是拿它悄悄去过滤。
        ///
        /// <para>这两个过滤器过去都会静默地"失败即关闭"：未识别的 category 不可能等于任何
        /// <c>Category.ToString()</c>，解析不出的 operation 会让下面每个 <c>Enum.TryParse</c> 都失败——
        /// 于是答复是 200 加 <c>skills: []</c>，与"当前 surface profile 下该 category 确实空无一物"
        /// 逐字节相同。agent 读到这个会断定本工程里没有该模块并停止查找，
        /// 而它其实只是把 <c>?category=GameObjects</c> 拼错了。</para>
        ///
        /// <para>必须在 <see cref="ResolveGetSurface"/> 之前运行。category/operation 属缩小范围键，
        /// 因此一个错误取值会走到 <see cref="GetSurface.Keyed"/>，并以那个拼写错误为键铸出——
        /// 而后永久持有——一条 manifest 大小的缓存条目。</para>
        ///
        /// <para>所有出现的取值都可接受时返回 null，包括压根没有缩小范围键的情形。
        /// 绝不拒绝下面的过滤器本会匹配的取值，因此任何合法 query 的字节都不会改变。</para>
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
        /// 返回下面的过滤器无法使用其取值的那个缩小范围键；所有取值都可接受时返回 null。
        /// 从 <see cref="ValidateNarrowingFilterValues"/> 中拆出来，是为了让 HTTP 线程快路径也能问
        /// 同一个问题：不碰 Unity API、不写日志、不调 Initialize()——这正是快路径区域的跨线程契约所要求的。
        /// 快路径只需要知道"这个 query 是否该被拒"，从不需要错误体，故构建载荷仍留在主线程。
        ///
        /// 每一项检查都必须与 <see cref="BuildFilteredOutput"/> 中对应过滤器所做的检查*完全相同*，
        /// 绝不能更严：本处拒掉而过滤器本会匹配的取值，会把一个正常的 200 变成 400。
        /// </summary>
        private static string FindInvalidNarrowingFilterKey(Dictionary<string, string> filters)
        {
            if (filters.Count == 0)
                return null;

            if (filters.TryGetValue("category", out var category) &&
                !_validCategoryNames.Contains(category, StringComparer.OrdinalIgnoreCase))
                return "category";

            // 用 Enum.TryParse 而非"名字是否在列表中"：SkillOperation 是 [Flags]，
            // 过滤器接受逗号列表（"Query,Modify"——匹配同时声明两者的 skill）和数字字面量，
            // 而按名字列表检查恰恰会拒掉这两种。
            if (filters.TryGetValue("operation", out var operation) &&
                !Enum.TryParse<SkillOperation>(operation, true, out _))
                return "operation";

            // 写了键但没写值（"?tags="、"?summary="）现在会被 ParseQueryString 保留而非丢弃，
            // 而这些键都没有有意义的"空值"读法：缩小范围键会变成什么都匹配不上的过滤条件，
            // 形状类键则退回到调用方本想覆盖的那个默认值。两种答法都会让调用方误以为该键生效了，
            // 所以直接拒绝。category/operation 无需列入——空串不属于这两个词表的任何成员，
            // 上面两项检查已经拦住它们。
            foreach (var key in _blankRejectingFilterKeys)
            {
                if (filters.TryGetValue(key, out var value) && string.IsNullOrWhiteSpace(value))
                    return key;
            }

            return null;
        }

        // 所有已识别的 query 键，顺序*固定*，使带多个空值的 query 每次拒绝时都指名同一个键——
        // 错误体和其他缓存答复一样，必须对同一 query 字节稳定。需与 _recognizedFilterKeys 保持同步。
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

            // 放在 ResolveGetSurface 之前，使未知取值绝无可能成为缓存键；也放在 brief/meta 分支之前，
            // 否则一个本该被拒的 query 会拿到一份完全合法的目录。HTTP 快路径经
            // FindInvalidNarrowingFilterKey 问同一个问题并主动让位，
            // 因此它不会为本处会返回错误的 query 发出 _cachedBrief。
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
                // ?brief=1（或 ?brief=true），以及现在的裸 GET /skills → 目录层：按 category 分组的
                // skill 名，不含描述与参数 schema（约 19KB，对比 summary 约 139KB / 完整约 618KB）。
                // 优先级高于 summary/category 等过滤（它们被忽略），以保持语义最小化：
                // 先定位模块，再经 GET /skills/schema?category=<Category> 拉取精确签名。
                case GetSurface.Brief:
                    return GetBrief();
                case GetSurface.FullV1:
                    return manifestType == "schema" ? GetSchema() : GetManifest();
            }

            // 在 Refresh() 之前，过滤输出对同一 query 逐字节确定；缓存它，
            // 使重复的限定范围拉取（?category=…）不必每次都重建并重新序列化全部 skill。
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

            // ?summary=1（或 ?includeSchema=false，与 /skills/recommend 的约定一致）
            // → 轻量认知 manifest：省略参数 schema，截断描述。
            bool summary = filters.TryGetValue("summary", out var sumVal) &&
                (sumVal == "1" || sumVal.Equals("true", StringComparison.OrdinalIgnoreCase));
            if (!summary && filters.TryGetValue("includeSchema", out var incVal) &&
                (incVal == "0" || incVal.Equals("false", StringComparison.OrdinalIgnoreCase)))
                summary = true;

            // 只有真正缩小范围的键才作为 `filters` 回显、才被 `filtered` 计入；
            // 一个什么都没缩小的 ?wire=v2 或 ?full=1 请求报 filtered:false。
            // 对所有 v2 之前的 query，这里是与从前同一个字典实例，因此字节相同。
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
            // 规范化并统一大小写的键。BuildFilteredOutput 里每项过滤比较都是大小写不敏感的
            //（category/tags/readonly 用 OrdinalIgnoreCase，operation 的 TryParse 传 ignoreCase=true，
            // q 走 ToLowerInvariant），因此把键与值一起转小写可把等价 query
            //（?category=GameObject 与 ?Category=gameobject）收敛到同一条缓存条目。
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
        /// skill 自己是否声明了同名参数。这类名字必须作为它自己的参数送达，
        /// 不得被信封层当作分页参数吃掉。
        /// </summary>
        private static bool SkillDeclaresParameter(SkillInfo skill, string parameterName) =>
            skill != null && ContainsParameter(skill.ParameterNames, parameterName);

        /// <summary>
        /// 把信封层的分页参数（'offset'/'limit'）读为不小于 minValue 的整数。
        /// 同时接受 JSON 数字及其字符串形式（"10"），使经 query string 调用的一方也能工作。
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
        /// 当某个信封层参数被判定非法、且尚未执行任何东西时，回退 <c>Method.Invoke</c> 之前开启的
        /// workflow/undo 记账。与 Execute 中各 catch 处理的清理动作一致。
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

        // internal：/skills/batch 的 dry-run 用它按被引用 skill 声明的 outputs
        //（含合成的 entityId）对 $ref 路径做结构性校验。
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
                // 按 profile 过滤，而不按 query 过滤：本区块属信封常量，因此限定范围的 ?category=
                // 拉取仍须列出所有对外提供的被跟踪 skill（把它收窄到当前页会改变每个限定范围 query
                // 的 v1 字节）。见 VisibleWorkflowTrackedSkills——默认 profile 下它就是全集。
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

        // v1 与 v2 信封共用，使两者永不走偏。
        private const string SummaryHintText = "AWARENESS ONLY — parameter schemas are omitted and descriptions are informal (human-written; some omit parameter hints entirely), not a formal signature. Before executing any skill listed here, validate its parameters with ?mode=dryRun (the server returns unknownParam suggestions + the full parameter schema) or fetch its scoped schema GET /skills/schema?category=<Category>. Do NOT guess parameters from descriptions alone.";

        internal const string MetaEndpointPath = "/skills/meta";

        // v2 条目唯一会省略的 riskLevel 取值。比较用 Ordinal（而非 IgnoreCase），
        // 使其他任何拼法都原样透传，而不是被静默归一化掉。
        private const string DefaultRiskLevel = "low";

        /// <summary>
        /// <c>?wire=v2</c> 在条目中省略掉的那些逐 skill 取值，在此统一声明一次。
        /// v2 信封与 <see cref="GetMeta"/> 都会输出它，且由构造方式保证两处完全相同——
        /// 唯有这个区块让那些省略变得可还原。
        /// </summary>
        private static object BuildWireDefaults() => new
        {
            riskLevel = DefaultRiskLevel,
            supportsDryRun = true
        };

        /// <summary>
        /// <c>?wire=v2</c> 信封。与 v1 的每一处差异都是"减法"：
        /// <list type="bullet">
        /// <item>四个会话恒定区块（categories / operationTypes / reservedBodyParameters /
        /// workflowTrackedSkills）让位给 <c>metaUrl</c>——拉一次
        /// <see cref="MetaEndpointPath"/> 即可，无需在每次限定范围拉取时都为它们付费；</item>
        /// <item>六个影响布尔量加 longRunning 折叠为 <c>flags</c>，只列其中为真的；</item>
        /// <item><c>riskLevel</c> 仅在非默认值时出现，<c>supportsDryRun</c> 仅在为 false 时出现，
        /// 而 <c>defaults</c> 说明这些省略各自意味着什么；</item>
        /// <item>为 null 的成员彻底消失（用 <c>_jsonSettingsV2</c> 序列化）。</item>
        /// </list>
        /// <c>approvalBehavior</c> 刻意保留在每个条目上：它是 agent 在判断"这次调用到底会不会被允许"
        /// 之前必须知道的唯一字段，而从 mode + flags 反推它，正是本载荷要消除的那种猜测。
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
                        // 尽管 v1 的 summary 两者都不带，flags 与 supportsDryRun 在此仍要带上。
                        // `defaults` 会出现在每个 v2 载荷里，而它规定"标志缺席即为 false"——
                        // 所以缺了它们的 summary 条目不会被读成"影响未知"，而会被读成
                        //"该 skill 什么都不改，且 dry-run 正常"。在此省略它们，
                        // 等于让每个 summary 条目对 784 个 skill 断言了恰恰相反的事实。
                        // 所有 v2 面共用一份契约。
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
        /// v2 中取代六个影响布尔量加 longRunning 的形式：只列已置位的标志，顺序固定以保持载荷字节稳定。
        /// 一个都没置位时为 null（因而被省略）；标志未出现在数组中即表示 false。
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
        /// 目录层 manifest——裸 <c>GET /skills</c>（以及 <c>?brief=1</c>）返回的内容：
        /// 按 category 分组的 skill 名，别无其他。模块键与名字都排序，使同一 skill 集合下载荷字节稳定，
        /// 因此缓存字符串（及其快路径 ETag）在 Refresh() 之前一直有效。
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
                // 这里报的是本载荷实际列出的数量。在非完整 surfaceProfile 下它小于注册表持有的数量——
                // 若在此报注册表总数，会让 agent 去找目录里并不存在的名字。
                totalSkills = visibleCount,
                briefHint = "DIRECTORY ONLY — names + categories, no descriptions or parameters. This is the default answer for GET /skills. Locate the module(s) you need, then fetch exact signatures via GET /skills/schema?category=<Category>, and always dryRun before first execution. If a name is ambiguous, fall back to GET /skills?summary=1 (full descriptions) or GET /skills/recommend?intent=... The complete manifest is still available at GET /skills?full=1 (~618KB — add &wire=v2 to cut it down), and session constants live at GET /skills/meta.",
                modules
            };
        }

        // ========== skill 推荐 ==========

        /// <summary>
        /// 基于意图的 skill 推荐。按关键词与 name（3 分）、tags（2 分）、description（1 分）的匹配打分，
        /// 返回排名前 N 的结果。
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

            // 预先算好 operation 与 category 的匹配（支持中文子串）
            var matchedOps = ExtractOperations(rawKeywords);
            var matchedCats = ExtractCategories(rawKeywords);

            // 意图对齐的输入（见 ApplyIntentAlignment）。取自原始意图词而非同义词扩展集：
            // 扩展的目的是放宽关键词匹配，若让它来决定"调用方是想观察还是想改动"，
            // 就会把调用方从未写过的动词也算进去（材质 → material，hierarchy → parent/child/gameobject）。
            bool readIntent = rawKeywords.Any(_readIntentVerbs.Contains);
            bool writeIntent = rawKeywords.Any(_writeIntentVerbs.Contains);
            bool sampleIntent = rawKeywords.Any(_sampleIntentWords.Contains);
            // 包列表仍在异步刷新期间为 null——为什么这意味着"跳过检查"而不是"去查一下"，
            // 见 HasUninstalledPackage。
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

                // category 加分
                if (matchedCats.Count > 0 && s.Category != SkillCategory.Uncategorized && matchedCats.Contains(s.Category))
                {
                    score += 2;
                    matchedOn.Add($"category:{s.Category}");
                }

                // operation 加分
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
                    // 只调整那些本就命中过东西的 skill。对零分 skill 施加读意图加分，
                    // 会仅凭意图就把注册表里所有只读 skill 拽进结果里。
                    score = ApplyIntentAlignment(s, score, readIntent, writeIntent, sampleIntent, packageCache, matchedOn);
                    healthBySkill.TryGetValue(s.Name, out var health);
                    var adjustedScore = Math.Max(1, score - (health?.Penalty ?? 0));
                    scored.Add((s, adjustedScore, score, matchedOn, health));
                }
            }

            var results = scored.OrderByDescending(x => x.score)
                .ThenByDescending(x => x.semanticScore)
                // 稳定的同分排序。没有它，同分 skill 会按反射发现顺序输出，
                // 而该顺序在不同工程之间、不同域重载之间都不一样——
                // 同一个意图会毫无理由地给同一批候选排出不同的名次。
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
                // v2 的 recommend 保持同一信封，只重塑逐 skill 的 schema，因此它与 manifest 受
                // 同一份 `flags` / `defaults` 契约描述。此处显式声明而不留作隐含：
                // 一个请求了 v2 却静默拿到 v1 的调用方，会把缺失的 `flags` 数组读成"没有置位标志"——
                // 即把一个会改动的 skill 当成无害——而这条回显正是为了让该误读不可能发生。
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
                    // `full` 下为 null，而 v2 会丢弃 null——因此默认 profile 下它不花任何代价。
                    // 排名类端点为何必须说明这一点，见 SurfaceProfilePrunedHint。
                    surfaceProfile = SkillsSurfaceProfile.IsFull ? null : SkillsSurfaceProfile.CurrentWire,
                    surfaceProfileHint = SkillsSurfaceProfile.IsFull ? null : SurfaceProfilePrunedHint,
                    response.results
                }, _jsonSettingsV2);
            }

            // 打分阶段已跳过被隐藏的 skill，因此非完整 profile 会静默缩短这份排名。
            // 理由与 chain 信封相同，字节稳定性分支也相同：v1 序列化会写出 null，
            // 所以 `full` 绝不能碰这些附加字段。
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

        // 用于判别调用方是想观察还是想改动的动词。只与原始意图词匹配（GetRecommendations），
        // 绝不与同义词扩展集匹配。
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
        /// 在关键词得分之上施加的三项修正，每一项都对应一个实测到的错误排名：
        ///
        /// <list type="bullet">
        /// <item><b>读/写意图对齐。</b>"read current camera properties inspect fov" 曾把
        /// camera_set_properties 排在 camera_get_properties 之前——setter 的描述必然会提到读取方
        /// 返回的那些属性，而且它恰好提得更多。现在，形态上明确属"读"的意图会偏向只读 skill，
        /// 明确属"写"的则把它们往下压。混合或无动词的意图不做处理：那里猜比不猜更糟。</item>
        /// <item><b>压制 Sample。</b><see cref="SkillCategory.Sample"/> 下的 skill
        ///（create_cube、set_object_position …）是真正的 gameobject_* / camera_* 的教学复制品，
        /// 而它们的短名字会稳赢名字子串加分——agent 想移动一个对象时，
        /// set_object_position 会排在 gameobject_set_transform 之前。它们仍然可达，
        /// 但只有在意图里真的出现 sample/demo/example 时才进入排名。</item>
        /// <item><b>未安装的可选包。</b>为一次普通材质编辑推荐 yooasset_* / probuilder_*
        /// 比推荐不到更糟：skill 是注册着的，所以在调用因缺包而失败之前，没有任何东西提醒 agent。</item>
        /// </list>
        ///
        /// <para>刻意不做重写。关键词权重（name 3 / tag 2 / desc 1）、category 与 operation 加分、
        /// 观测惩罚以及排序键都原封不动。每项调整都追加到 <c>matchedOn</c>，
        /// 使一个意外的名次仅凭响应即可审计；结果下限为 1，
        /// 因此任何调整都不能把一个真实的关键词命中从 <c>totalMatches</c> 里剔除——只能把它压到最后。</para>
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

            // readIntent != writeIntent 即"恰好只有一个成立"，也就是无歧义的那些情形。
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
        /// 该 skill 是否指名了一个尚未安装的可选包。机制与冒烟测试的跳过闸门
        ///（<c>TestSkills.EvaluateSmokeSkill</c>）相同，包括它的空缓存守卫：
        /// <paramref name="packageCache"/> 为 null 表示
        /// <see cref="PackageManagerHelper.InstalledPackages"/> 的异步刷新还没完成，
        /// 此时打分器宁可*一个候选都不降权*，也不依据一份还不存在的包列表作答——
        /// 把"暂时不知道"读成"没安装"，会在会话的最初几秒把所有可选包 skill 全压下去。
        ///
        /// 这道守卫是为了正确性而非省开销。<c>IsPackageInstalled</c> 的未命中路径是
        /// <c>ResolveDirectly</c> → <c>PackageInfo.FindForAssetPath("Packages/&lt;id&gt;")</c>，
        /// 属内存注册表查找而非 Package Manager 客户端请求——所以单个 id 足够便宜，
        /// 而 <paramref name="packageCache"/> 会在*本次请求*余下的过程中记忆结果，
        /// 使被二十个 skill 共用的包只解析一次。缓存刻意做成每请求一份：
        /// 生命周期更长的缓存会在用户装好包之后仍然一直回答"缺失"。
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
        /// <see cref="BuildSkillSchemaForRecommend"/> 的 <c>?wire=v2</c> 形式：与 v2 manifest 条目
        /// 使用同一个 <c>flags</c> 数组和同一套"省略默认值"规则，使 agent 在所有端点上只需解析一种形状。
        /// 注意它输出全部七个标志，而 v1 只带三个布尔量（readOnly / mutatesScene / mutatesAssets）——
        /// 用更少的字节给出严格更多的信息；v1 报告过的一项都没丢。
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

        // ========== skill 依赖链 ==========

        /// <summary>
        /// 用 BFS 沿 Outputs→RequiresInput 关系构建操作链。
        /// 给定一个目标输出字段，找出所有产出它的 skill 及其依赖。
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

            // BFS：先找产出目标字段的 skill，再顺着它们的 RequiresInput 追溯
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
                    // _outputIndex 是对整个注册表的完整索引，因此在此处过滤而非在构建时过滤：
                    // 指名一个被当前 profile 隐藏的 skill，会让 agent 走上一条第一步就答
                    // SURFACE_EXCLUDED 的链。被排除的产出方整体跳过——它们的 RequiresInput 字段
                    // 也不入队，因为一个跑不了的步骤不可能成为计划的一部分。
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

                    // 把 RequiresInput 字段入队，供下一层深度使用
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

            // `full` 下什么都没被裁剪，载荷与 v1 逐字节一致。非完整 profile 下，上面的产出方列表
            // 已静默丢掉了一些步骤，而本信封是唯一能说明这件事的地方：否则一条变短的链会被读成
            //"Unity 没有任何办法产出该字段"，agent 会去汇报一件不可能的事，而实际上只是 skill 被隐藏了。
            // 注意本信封用 _jsonSettings 序列化，它会写出 null——故此处用分支而不是一个值为 null 的字段。
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
        /// 附加在那些"非完整 surface profile 下会悄悄少返回 skill"的发现类信封上
        ///（<c>/skills/recommend</c>、<c>/skills/chain</c>）。这两者是排名与遍历而非枚举，
        /// 因此被裁剪的结果与空结果无从区分——没有这条提示，agent 会断定该操作不可能并如此告知用户，
        /// 而事实是用户把它隐藏了，也可以取消隐藏。
        /// </summary>
        private const string SurfaceProfilePrunedHint = "Results were pruned by the user's surface profile — a skill missing here may exist but be hidden, so do not conclude Unity cannot do it. GET /health for the active profile; only the user can switch it back to \"full\" in the UnitySkills panel.";

        internal static string[] FormatOperationForPlanning(SkillOperation op)
        {
            return FormatOperation(op);
        }

        /// <summary>
        /// 被 agent 误当成 REST skill 名的 Python 客户端辅助函数名，映射到真正能干这件事的 REST 调用。
        /// 需与 <c>unity-skills~/scripts/unity_skills.py</c> 中模块级的 def 保持同步。
        ///
        /// 之所以需要一张精确表，是因为 <see cref="ResolveSkillNotFound"/> 里的模糊回退在结构上
        /// 触及不到它们：辅助函数名与任何已注册 skill 都没有共同 token，
        /// 既不在编辑距离 5 之内，也不是任何 skill 名的子串——调用方会拿到空建议列表，
        /// 无从自我纠正。此处只列 agent 在会话开始时会遇到的发现/认知类辅助函数；
        /// 其余仍照旧走模糊路径。
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
            // 客户端辅助函数名不可能模糊匹配到任何 skill——在落到最近名搜索之前，
            // 先用对应的 REST 用法作答。
            if (!string.IsNullOrEmpty(name) &&
                k_ClientHelperRestEquivalents.TryGetValue(name, out var restEquivalent))
            {
                return SkillErrorResponse.ClientHelperNotASkill(name, restEquivalent);
            }

            // 给出最多 5 个最接近的*对外提供*的 skill 名，让 AI agent 能自行纠正拼写错误。
            // 取自 VisibleSkills 而非注册表：对一个被隐藏 skill 的近似命中，
            // 会把 surface profile 刚撤下的那个名字原样交回去，
            // 使拼写纠错变成用户所选不暴露内容的枚举通道。
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
        /// 遵循当前 surface profile 的对外 skill 集合。凡是要把 skill 提供给调用方的场合
        ///（白名单选择器、skill 浏览器、冒烟探测）都该用这个：提供一个被 profile 隐藏的 skill，
        /// 只会在之后换来一个 SURFACE_EXCLUDED。需要面向整个注册表记账时用
        /// <see cref="GetAllSkillsSnapshotUnfiltered"/>。
        /// </summary>
        internal static SkillInfo[] GetAllSkillsSnapshot()
        {
            Initialize();
            return VisibleSkills()
                .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>
        /// 所有已注册 skill，忽略 surface profile——供那些必须就注册表本身（而非"对外提供了什么"）
        /// 作推断的调用方使用：解析一个已持久化的 skill 名（白名单条目跨 profile 切换仍然有效，
        /// 因此仅因当前 profile 隐藏了它就渲染成 "(Unknown)" 是在说谎），
        /// 以及与 <see cref="ValidateMetadata"/> 同类的全注册表审计。
        ///
        /// 仅限本地编辑器 UI 与诊断。绝不可把它接到任何 HTTP 面上：profile 是用户对
        ///"可以把什么提供给 AI"的表态，任何从此处枚举的端点都会交回用户选择撤下的 skill 名——
        /// 而这正是 <see cref="VisibleSkills"/> 存在所要防的泄漏。
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
                        // 批处理类 skill 把 JSON 载荷声明为 string 参数，而 agent 经常直接发原生
                        // 数组/对象。此处序列化回字符串，而不是以 TYPE_MISMATCH 失败——
                        // skill 内部会重新解析该 JSON，所以往返是无损的。
                        // 只有目标类型为 string 时才有此宽容，其他类型仍严格。
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
        /// 当前 surface profile 暴露该 skill 时返回 null；否则返回一份序列化好的 SURFACE_EXCLUDED
        /// 载荷，由调用方原样呈现。
        ///
        /// 这条消息只有一个任务：阻止 agent 绕开该排除。agent 撞墙后会本能地重试，
        /// 接着去找一个能做同样写入的邻近模块——两者都会让 profile 失去意义。
        /// 因此载荷会点明是哪个 profile 在隐藏它、说明该设置归用户所有，
        /// 并（在 guide 下）交出 manual-* 文档，让 agent 转而以指导者的身份把事情做完。
        /// </summary>
        /// <summary>
        /// 对一个被排除的 skill，应交给 agent 的 manual-* 文档；没有则返回 null。
        /// 该问题由 category 回答，唯独按名字隐藏的那些"逃生口"例外：它们之所以被隐藏，
        /// 恰恰是因为其 category 说明不了它们能触及什么，因此由 category 推出的文档会是错误的指引。
        /// </summary>
        private static string SurfaceExclusionManualDoc(SkillInfo skill) =>
            SkillsSurfaceProfile.IsAlwaysHiddenSkill(skill.Name)
                ? null
                : SkillsSurfaceProfile.ManualDocFor(skill.Category);

        /// <summary>
        /// 两条拒绝路径——dry-run 预览（<paramref name="forPreview"/>）与 execute 闸门——共用一份文案，
        /// 使两者绝不会就同一堵墙对 agent 说出不同的话。预览说明"会发生什么"，
        /// 闸门则告诉 agent"该改做什么"。按重要性排列的三种情形：
        /// <list type="bullet">
        /// <item><b>按名字隐藏的逃生口：</b>没有适用的手工文档，原因在于该 skill 的触及范围而非其模块。
        /// 指向 Editor 菜单——那正是这个 skill 本来要驱动的东西，用户可以亲手做 AI 不被允许替他们做的事。</item>
        /// <item><b>guide 且有手工文档：</b>交出该文档；agent 以指导者身份把事情做完。</item>
        /// <item><b>其余一切：</b>只有用户能解除。</item>
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
                    // category 在消息里和 details.category 里都已点明，所以 hint 只说"这次改动"
                    // 而不把它内插进去——"walk the user through the Sample change"
                    // 对这里唯一重要的那个受众来说读起来毫无意义。
                    : $"Do not retry and do not substitute another module — the write is off the menu, not failing. Read {manualDoc} and walk the user through the change in the Editor yourself, or ask them to switch the surface profile back to \"full\" in the UnitySkills panel if they want it automated.";
            }

            // 目前只有 noSceneAuthoring 会走到这里，因此把 "excludes scene-authoring writes"
            // 硬编码是安全的：guide 到不了这里，因为 guide 隐藏的每个 category 都随附一份 manual-* 文档，
            // 会被上面的分支接住。该不变式由
            // SkillsSurfaceProfileTests.EveryGuideHiddenCategory_ShipsAManualDoc 保障——
            // 新增的 guide category 必须先备好它的 manual-* 文档，否则本分支会开始告诉 guide 用户
            // 他们的写入是以"场景编排"为由被拦下的。
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
                // 逃生口用它自己的措辞："a write skill in the Editor category" 既不对
                //（它的 category 并没有被隐藏），又没用（它说明不了这个 skill 为什么被隐藏）。
                // 其余所有排除确实都是 category + 写入。
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
                // 最接近的可用策略：这次调用不得原样重复。与 ask_user_and_grant 不同，
                // 这里没有 token 可拿——要么用户改面板设置，要么这件事由人工完成。
                retryStrategy: SkillErrorResponse.Abort);
        }

        /// <summary>
        /// 权限档位允许该 skill 时返回 null；否则返回一份序列化好的错误载荷
        ///（MODE_RESTRICTED 或 MODE_FORBIDDEN），由调用方原样呈现。
        /// 判定为 Allowed 时总会写一条审计 "call" 条目，使 Auto 模式下的静默执行仍可追溯。
        /// </summary>
        private static string ApplyModeGate(SkillInfo skill, string name, ParameterValidationResult validation)
        {
            var argsForHash = validation?.Args == null ? new JObject() : (JObject)validation.Args.DeepClone();
            argsForHash.Remove("_confirm");
            var argsJson = argsForHash.ToString(Formatting.None);

            // 关键：必须先于 CheckAccess 读取 allowlist 状态——CheckAccess 内部会消费 one-shot 标记，
            // 之后 IsInAllowlist 仍可重复查询。先记下 allowlist 命中，便于审计区分 allowlist vs oneShot vs auto。
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
                    // grantSource：allowlist 命中最高优先；否则若是 Bypass 模式视作 bypass；
                    // 其余非 Allowlist/非 Bypass 的 Allowed 都归类为 auto（CheckAccess 在调用前已消费了
                    // 任何 one-shot 令牌，无法事后区分；这是当前可观察到的最佳近似）。
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
                                ? "Ask the user; on consent POST /permission/grant {skill, token}. v1.9 方案 B: grant 调用本身会一步执行该 skill 并返回结果（response.result）——无需再 re-call 原 skill。"
                                : "Tell the user to click Approve on the Unity panel; then POST /permission/grant {skill, token} once. That grant call executes the skill in-line and returns the result. Do not poll grant; do not re-call the original skill.",
                        },
                        retryStrategy: SkillErrorResponse.RetryAskUserAndGrant);
            }
            return null;
        }

        /// <summary>
        /// 允许该 skill 执行（token 已消耗）时返回 null；否则返回一份序列化好的错误载荷
        ///（CONFIRMATION_REQUIRED 或 INVALID_TOKEN），调用方应原样回传给客户端。
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

            // argsHash 不含 _confirm，使同样的参数在两次调用中算出同样的哈希。
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
                    // dry-run 属尽力而为；即便失败，token 依然有效。
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

                // schema 的 supportsDryRun 标志宣告的是 router 层的预演传输方式
                //（POST /skill/<name>?mode=dryRun），不是一个请求体参数——但读到该标志的 agent
                // 总会真的传一个过来，而 Levenshtein 对 "dryRun" 找不到任何有用的邻居。
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

            // 最后的兜底：别名表和编辑距离都抓不到 assetPath→savePath 这类改名
            //（距离 4，且无子串重叠），但整个 skill 库的参数名会复用同一批 camelCase 词元
            //（path/name/id/target/source/…），因此"共享任一词元"是很强的线索。
            // 仅在更严格的层级什么都没找到时才启用，以免给本已有良好匹配的建议里添噪。
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

                // 为一个被 profile 隐藏的 skill 所做的计划是执行不了的计划，而 ?mode=plan 曾是唯一
                // 从不说明这件事的预览——agent 规划好整个序列，第一次 execute 就撞上 SURFACE_EXCLUDED，
                // 而计划里没有任何东西提示过。此处与 dry-run 分支用同一区块、同一形状
                //（BuildAuthorizationPreview 在这里返回 SURFACE_EXCLUDED 判定），使调用方只读一份契约。
                // 只在确有内容可说时才附加：对 profile 直接允许的每个 skill，计划字节保持不变，
                // 而计划输出本已是三种预览载荷中最大的一份。第二个分支覆盖"携带写入"入口，
                // 它们的拒绝由任何预览都拿不到的载荷决定——为一个 profile 将会拒绝的 batch_execute
                // 做规划，是同一个陷阱在上一层的翻版。
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
                // JSON 合法也仍可能让 plan/语义校验崩掉（例如 NRE）。把这种情况报成 INVALID_JSON
                // 会让 agent 反复重写一个本来没问题的请求体；因此照 Execute 的 catch 分法，
                // 如实上报真正的失败。
                return SkillErrorResponse.Build(
                    SkillErrorCode.Internal,
                    $"Plan failed: {ex.Message}",
                    skill: name,
                    details: new { exceptionType = ex.GetType().Name },
                    retryStrategy: SkillErrorResponse.Abort);
            }
        }



        /// <summary>
        /// 校验所有已发现 skill 的元数据完整性与一致性。
        /// 返回一组诊断消息（以 WARN/ERROR 为前缀）。
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

                // ReadOnly 不只是文档，它是承重的：surface profile 从不隐藏只读 skill，
                // 因此一个被误标为 ReadOnly=true 的写操作，在一个专为撤下此类写入而存在的 profile 下
                // 仍然可调用。下面这三条正是误标会造成的自相矛盾；它们是 ERROR 而不是 WARN，
                // 因为每一条都会静默地击穿一项面向用户的保证。
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

                // RiskLevel 是自由格式字符串，而 RiskRank 会把它不认识的任何取值静默排为 "low"。
                // 所以拼写错误（"hgih"）不会显式失败——它把该 skill 降级为风险最低，
                // 而那恰恰是 agent 在决定要不要向用户确认时所读的字段，
                // 也是 AppendBatchMirrorIssues 用来比较 batch 与单体的字段。
                // 目前随包发布的每条声明都合法；本检查是为了让下一条不会朝着"隐藏风险"的方向写错。
                if (!IsKnownRiskLevel(s.RiskLevel))
                    issues.Add($"[WARN] {s.Name}: RiskLevel='{s.RiskLevel}' is not one of low/medium/high — it ranks as 'low'");
            }

            AppendBatchMirrorIssues(issues);

            return issues;
        }

        private const string BatchSkillSuffix = "_batch";

        /// <summary>
        /// 跨 skill 规则：<c>X_batch</c> 声明的影响面必须不低于 <c>X</c>。
        ///
        /// <para>批处理 skill 把单体 skill 的活干 N 遍，因此它不可能改得更少、跟踪得更少或风险更低。
        /// 元数据若相反，那就是批处理条目写错了，而后果并非无关痛痒：
        /// MutatesScene/MutatesAssets 决定 surface profile 撤下什么，TracksWorkflow 决定这次调用
        /// 能不能撤销，RiskLevel 是 agent 在决定是否向用户确认前所读的字段。
        /// 于是一个声明过轻的批处理，就成了那个能穿过所有拦住其单体孪生兄弟的闸门的变体——
        /// 而且它碰的是 N 个对象而不是一个。</para>
        ///
        /// <para>只检查严格的 <c>X</c>/<c>X_batch</c> 名字配对。孪生单体拼法不同
        ///（material_set_colors_batch ↔ material_set_color）或根本没有孪生体的批处理 skill
        /// 一律跳过，不做猜测。</para>
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
        /// low &lt; medium &lt; high。其余一切都与 "low" 同级——这不算兜底，而是事实：
        /// <see cref="UnitySkillAttribute.RiskLevel"/> 的默认值就是 "low"，
        /// 所以一个未识别或缺失的等级，确实就是可得的最低风险声明。
        /// </summary>
        private static int RiskRank(string riskLevel)
        {
            if (string.Equals(riskLevel, "high", StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(riskLevel, "medium", StringComparison.OrdinalIgnoreCase)) return 1;
            return 0;
        }

        /// <summary>
        /// <paramref name="riskLevel"/> 是否是 <see cref="RiskRank"/> 真正认识的等级。
        /// 把未知字符串排为 "low" 是正确的运行时行为，但对此保持沉默则不对，
        /// 所以 <see cref="ValidateMetadata"/> 会就此发出 WARN。
        /// </summary>
        private static bool IsKnownRiskLevel(string riskLevel) =>
            string.Equals(riskLevel, "low", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(riskLevel, "medium", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(riskLevel, "high", StringComparison.OrdinalIgnoreCase);

        // ========== query string 解析 ==========

        /// <summary>
        /// 把 query string 解析为大小写不敏感的 键→值 映射。
        ///
        /// 过去有两种写法被直接丢弃，而调用方写它们都是有意的：
        /// <list type="bullet">
        /// <item><b>裸键</b>（<c>?full</c>、<c>?brief</c>）——URL 中"出现即为真"的惯用写法。
        /// 丢弃它会让 <c>GET /skills?full</c> 变成静默空操作，在调用方等着 618KB manifest 时
        /// 返回 19KB 的目录。现在它被收集为值 <c>"1"</c>，与 <c>?full=1</c> 得到的值相同，
        /// 因此两种写法共用一条缓存条目和一个 ETag。</item>
        /// <item><b>值为空的键</b>（<c>?category=</c>）——收集为空串而不是丢弃，
        /// 以便缩小范围过滤的守卫能连合法词表一起拒绝它。丢弃它会把一个写了一半的过滤条件
        /// 变成"没有过滤"，于是一个限定范围的请求拿到了整份目录，看起来还像成功了。</item>
        /// </list>
        /// 完全没有键的对（<c>?=v</c>，或 <c>?a&amp;&amp;b</c> 里的空段）仍然跳过——没有键可供索引。
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
        /// 从 skill 参数中自动快照目标对象，为通用回滚提供支持。
        /// 识别常见的目标参数（name、instanceId、path、materialPath 等）并对它们做快照。
        /// 目标定位委托给 <see cref="CollectTargetsFromArgs"/>，
        /// 使语义 diff 的前捕获复用完全相同的对象集合、顺序与尽力而为语义。
        /// </summary>
        /// <summary>
        /// 当前手动录制会话（workflow_begin_task）自上次 SaveHistory 以来是否有新内容需要持久化——
        /// 即换了另一个任务在活动，或活动任务新增了快照。每次返回 true 时都会推进已保存标记，
        /// 使下次调用以本次保存点为基准比较。尽力而为：遇到任何异常（任务为 null）时默认保存，
        /// 以确保绝不静默丢弃历史。
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
        /// 定位由 skill 参数所指向的 UnityEngine.Object——它是自动 workflow 快照
        ///（<see cref="TrySnapshotTargetsFromArgs"/>）与语义 diff 前捕获
        ///（<see cref="SkillSceneDiff.CaptureBefore"/>）共用的底层原语。
        ///
        /// 对象按与历史快照序列一致的固定顺序返回，以保证快照行为不变：
        /// 目标 GameObject + 其 Transform + Renderer.sharedMaterial，然后是 materialPath / assetPath
        /// 指向的资产，然后是子 Transform，最后是 items[] 中的各个目标
        ///（GameObject + Transform，取前 50 个）。定位属尽力而为，解析不出的目标跳过。
        /// items[] 那一段自带 try/catch，使格式错误的批次绝不会中断其余部分——与原先的内联行为一致。
        /// </summary>
        internal static List<UnityEngine.Object> CollectTargetsFromArgs(JObject args)
        {
            var targets = new List<UnityEngine.Object>();

            // 按常见参数名尝试定位目标 GameObject
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

            // 能识别出 GameObject 时快照它
            if (!string.IsNullOrEmpty(targetEntityId) || !string.IsNullOrEmpty(targetName) || targetInstanceId != 0 || !string.IsNullOrEmpty(targetPath))
            {
                var (go, _) = GameObjectFinder.FindOrError(targetName, targetInstanceId, targetPath, entityId: targetEntityId);
                if (go != null)
                {
                    targets.Add(go);
                    // Transform 是最常被改的，一并快照
                    targets.Add(go.transform);
                    // 若有 Renderer，快照其材质
                    var renderer = go.GetComponent<UnityEngine.Renderer>();
                    if (renderer != null && renderer.sharedMaterial != null)
                        targets.Add(renderer.sharedMaterial);
                }
            }

            // 给了 materialPath 时快照该材质资产
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

            // 给了 assetPath 时快照该资产
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

            // 处理 child/parent 类操作（带 entityId 兜底的快照）
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

            // 处理批次条目：逐个快照批次里的目标
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
                catch { /* 批次解析出错时忽略 */ }
            }

            return targets;
        }

        #region HTTP-thread cached GET fast path (v2.1)
        // ⚠ 跨线程契约：本 region 会被 SkillsHttpServer 的 HTTP 监听线程直接调用，必须保持
        // 零 Unity API（UnityEngine.*/UnityEditor.*）、零 SkillsLogger（内部走 Debug.Log 且
        // Level getter 首次会读 EditorPrefs）。只允许读取已由主线程构建好的字符串缓存
        // （_cachedManifest / _cachedSchema / _filteredOutputCache，均为不可变 string 或
        // ConcurrentDictionary）以及本 region 自有的 _etagCache。缓存未建立时必须返回 false，
        // 交回主线程慢路径（主线程构建缓存后下一次请求即可命中）。
        // 本 region 内代码不得调用 Initialize()/GetManifest()/GetSchema()/BuildFilteredOutput()
        // ——它们会触发反射扫描与 SkillsLogger 日志，只能在主线程运行。

        // ETag 缓存：键 = 输出缓存键，值 = (来源 json 引用, etag)。
        // SkillRouter 非 [InitializeOnLoad]、无静态持久化，域重载即整体重置，天然失效；
        // Refresh()（skill 增删）重建后旧 entry 的 json 引用与新缓存串不再相等，下方
        // ReferenceEquals 不匹配即自动重算并覆盖同 key——正确性本不依赖清空。但 Refresh() 仍
        // 主动 Clear()，避免旧 entry（及其引用的大字符串）在多次 Refresh 间累积；同时用
        // MaxCacheEntries 兜底防止任意路径下的无界膨胀。
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Json, string Etag)> _etagCache =
            new System.Collections.Concurrent.ConcurrentDictionary<string, (string Json, string Etag)>();

        /// <summary>
        /// HTTP 线程快速通道：GET /skills、GET /skills/schema（含 query 变体）与 GET /skills/meta
        /// 在字符串缓存已由主线程构建时直接返回缓存 json + ETag（SHA256 前 16 hex），绕过主线程队列。
        /// 未命中（缓存尚未构建 / 路径不属于这三个端点）返回 false。
        /// /skills/recommend、/skills/chain、/skills/batch 等路径精确匹配不上，一律走慢路径。
        /// 分流完全交给 <see cref="ResolveGetSurface"/>——与主线程 BuildFilteredOutput 同一把逻辑，
        /// 所以裸 /skills 在两条路径上都落到 brief 缓存串，不会一条给 brief 一条给全量。
        /// 同理，非法 ?category=/?operation= 值也必须在这里退回慢路径：分流一致还不够，两条路径
        /// 对「这个 query 该不该被拒」的判断也得一致。
        /// </summary>
        internal static bool TryGetCachedGetResponse(string path, string query, out string json, out string etag)
        {
            json = null;
            etag = null;

            string manifestType = ResolveManifestTypeForPath(path);
            if (manifestType == null)
                return false;

            var filters = StripUnrecognizedFilterKeys(ParseQueryString(query));

            // 与主线程同一把判定（FindInvalidNarrowingFilterKey，纯字符串比较、不触 Unity API）：
            // 非法 ?category=/?operation= 值一律退回慢路径去铸错误体。少了这一步，Brief/Meta 两个
            // surface 会绕过校验——它们不查 _filteredOutputCache，而是直接返回主线程早已建好的
            // _cachedBrief/_cachedMeta，于是 ?brief=1&category=Bogus 在缓存热时得到 200 目录、
            // 冷时得到错误，同一个 URL 两种答案。
            if (FindInvalidNarrowingFilterKey(filters) != null)
                return false;

            // ResolveGetSurface 直调而非经 BuildGetCacheKey：分流逻辑仍是同一把，只是 filters
            // 已在上面解析过，再走一遍 BuildGetCacheKey 等于每个快路径请求白解析一次 query。
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
        /// 主线程慢路径专用：为刚构建好的 /skills、/skills/schema 或 /skills/meta 输出取 ETag。与
        /// <see cref="TryGetCachedGetResponse"/> 共用 <see cref="BuildGetCacheKey"/> 与
        /// <see cref="GetOrComputeEtag"/>，所以同一份内容在慢路径与 HTTP 线程快路径上得到的
        /// etag 完全一致——否则客户端会在两条路径间来回抖动，If-None-Match 永远命中不了 304。
        /// json 为空（错误响应等）时返回 null，调用方不应发 ETag 头。
        /// </summary>
        internal static string GetEtagForCachedGet(string path, string query, string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            return GetOrComputeEtag(BuildGetCacheKey(path, query, out _), json);
        }

        /// <summary>
        /// manifest 家族路径 → manifestType；其他路径返回 null。纯字符串匹配，可安全在 HTTP 线程调用。
        /// </summary>
        private static string ResolveManifestTypeForPath(string path)
        {
            if (string.Equals(path, "/skills", StringComparison.OrdinalIgnoreCase)) return "manifest";
            if (string.Equals(path, "/skills/schema", StringComparison.OrdinalIgnoreCase)) return "schema";
            if (string.Equals(path, MetaEndpointPath, StringComparison.OrdinalIgnoreCase)) return "meta";
            return null;
        }

        /// <summary>
        /// 与 BuildFilteredOutput 的分流保持一致：同一个 <see cref="ResolveGetSurface"/> 决定
        /// surface 与缓存键（未知路径按 manifest 处理，仅 <see cref="GetEtagForCachedGet"/> 的
        /// 防御性回退会走到）。
        /// </summary>
        private static string BuildGetCacheKey(string path, string query, out GetSurface surface)
        {
            string manifestType = ResolveManifestTypeForPath(path) ?? "manifest";
            var filters = StripUnrecognizedFilterKeys(ParseQueryString(query));
            return ResolveGetSurface(manifestType, filters, out surface);
        }

        /// <summary>
        /// 按 (缓存键, json 引用) 记忆化的 ETag 获取：条目存在且 Json 引用与当前缓存串一致
        /// 才复用，否则重算并覆盖——保证 Refresh() 重建缓存后不会拿旧 etag 误判 304。
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
    /// router 能从 skill 错误对象上取到的全部内容。对于旧式的 <c>new { error = "..." }</c> 形状，
    /// 只有 <see cref="Message"/> 会被填充；其余是 skill 可选声明、用于覆盖
    /// <see cref="SkillErrorClassifier"/> 猜测结果的契约。
    /// </summary>
    internal sealed class SkillErrorContext
    {
        public string Message;
        public SkillErrorCode? Code;
        public string RetryStrategy;
        public List<SuggestedFix> SuggestedFixes;
        public List<string> RelatedSkills;

        /// <summary>
        /// skill 放在错误对象上的其余所有字段（合法值列表、文档 URL、包 id、提示等）。
        /// 没有它，分类器只会拿消息作答，并静默丢掉 skill 特意算出来的那些诊断信息。
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
        /// router 错误契约的第一层：取出消息，以及 skill 选择声明的任何结构化字段
        ///（<c>errorCode</c>、<c>suggestedFixes</c>、<c>retryStrategy</c>、<c>relatedSkills</c>）。
        /// 判定"这是不是错误"的条件与 <see cref="TryGetError(object, out string)"/> 完全相同，
        /// 因此没有额外声明的 skill 行为与从前一致。字段提取已隔离异常——
        /// 声明格式错误时降级为仅取消息，而不是让整个响应失败。
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
        /// skill 错误对象上已被响应信封建模的字段。其余一律原样转发，
        /// 使 skill 自己写的诊断信息能在分类过程中存活下来。
        /// </summary>
        private static readonly HashSet<string> ReservedErrorFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "error", "errorCode", "retryStrategy", "relatedSkills", "suggestedFixes",
            "status", "skill", "details", "retryAfterSeconds", "success"
        };

        /// <summary>
        /// 收集 skill 错误对象上的非保留成员。匿名类型、字典和 JObject 都支持，
        /// 因为 skill 这三种形状都会返回。已隔离异常：读不出来的成员跳过，而不是让整个响应失败。
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

        /// <summary>接受 string、string[]、JArray 或任意序列；为空时返回 null。</summary>
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
        /// 接受单个修复建议或它们的序列，形状可以是完整的
        ///（<c>{ action, skill, args, reason }</c>）或一个裸提示字符串。
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
