using System;
using System.Collections.Generic;
using UnityEditor;

namespace UnitySkills
{
    /// <summary>用户选择对外暴露的技能面切片。</summary>
    public enum SurfaceProfileKind
    {
        /// <summary>提供全部已注册技能，默认档。</summary>
        Full = 0,
        /// <summary>
        /// 隐藏用户最可能想自己动手的模块的写技能（GameObject / Component / Material / Scene，
        /// 外加 Sample 的基元创建——那不过是换了名字的 GameObject 创作），使 AI 去讲编辑器步骤
        /// 而不是替用户创作。这些模块的只读技能仍然可用。
        /// </summary>
        Guide,
        /// <summary>
        /// 隐藏所有视觉/创作类模块的场景创作写操作。非创作类工作（资产、项目、测试、诊断、脚本……）不受影响。
        /// </summary>
        NoSceneAuthoring
    }

    /// <summary>
    /// 用户对技能面的呈现策略：哪些技能会被提供出来。
    /// 以 wire 字符串持久化在 EditorPrefs 中，在 <c>/health</c> 上以 <c>surfaceProfile</c> 上报，
    /// 由 <see cref="SkillRouter"/> 在发现与执行两处强制。
    ///
    /// 这不是权限模式。<see cref="SkillsModeManager"/> 回答"这个技能能不能跑"，本类回答"这个技能在不在菜单上"。
    /// 因此排除的优先级高于 Bypass 模式和白名单——后两者授予的是用户已经委派出去的权限，
    /// 而档位是用户在说"我不希望这些操作被尝试"。唯一的解除方式是用户在 UnitySkills 面板里切回
    /// <see cref="SurfaceProfileKind.Full"/>。
    ///
    /// 排除结论由技能元数据（category + ReadOnly）推导，绝不依赖硬编码技能名单，所以新加的技能
    /// 只要带上分类就自动被覆盖。有两类入口的写操作由载荷而非自身元数据决定
    /// （<c>batch_execute</c>、工作流 undo/redo 技能），它们在执行时对自己强制同一策略，
    /// 其预览也会通过 <see cref="CarriedWritePreviewGate"/> 提前声明——见下文"carried writes"一节。
    /// </summary>
    public static class SkillsSurfaceProfile
    {
        public const string WireFull = "full";
        public const string WireGuide = "guide";
        public const string WireNoSceneAuthoring = "noSceneAuthoring";

        private const string PrefKeyProfile = "UnitySkills_SurfaceProfile";
        // 2.7 之前的布尔 guide 开关，仅用于单向迁移时读取（见 Load）。
        private const string PrefKeyLegacyGuideMode = "UnitySkills_GuideMode";

        /// <summary>
        /// 档位变更并已持久化之后触发。订阅方必须假定可见技能集已经不同了：
        /// <see cref="SkillRouter"/> 会丢掉缓存的输出字符串，<see cref="SkillsHttpServer"/> 会刷新 /health 快照。
        /// </summary>
        public static event Action OnChanged;

        // EditorPrefs 只能在主线程访问，而构建清单字符串时每个技能都要查一次可见性过滤，
        // 所以解析结果在此记忆化。setter 与首次读取都会写它，两者均在主线程。
        private static SurfaceProfileKind? _current;

        public static SurfaceProfileKind Current
        {
            get
            {
                if (!_current.HasValue)
                    _current = Load();
                return _current.Value;
            }
            set
            {
                if (Current == value) return;
                _current = value;
                EditorPrefs.SetString(PrefKeyProfile, ToWire(value));
                RaiseChanged();
            }
        }

        /// <summary>在 /health 与 /skills/meta 上上报的 wire 值。</summary>
        public static string CurrentWire => ToWire(Current);

        /// <summary>
        /// 什么都没隐藏时为 true——这是热路径。调用方据此彻底跳过逐技能过滤，
        /// 使默认档的代价是每个面一次比较，而不是每个技能一次。
        /// </summary>
        public static bool IsFull => Current == SurfaceProfileKind.Full;

        /// <summary>
        /// 给定档位会隐藏其写技能的分类集合。<see cref="SurfaceProfileKind.Full"/> 返回 null。
        /// </summary>
        public static HashSet<SkillCategory> HiddenCategories(SurfaceProfileKind profile)
        {
            switch (profile)
            {
                case SurfaceProfileKind.Guide: return _guideHidden;
                case SurfaceProfileKind.NoSceneAuthoring: return _noSceneAuthoringHidden;
                default: return null;
            }
        }

        /// <summary>
        /// 只看分类的排除判定，保留给手上只有 category 与 ReadOnly 标志的调用方。
        ///
        /// 对某些特意设计的情形会"少报"：它看不到 <see cref="_alwaysHiddenSkillNames"/> 这类后门名单，
        /// 也看不到 NoSceneAuthoring 那条"隐藏所有声明 <c>MutatesScene</c> 的写操作（不论分类是否在列）"的规则。
        /// 只要手上有 SkillInfo，就优先用 <see cref="IsExcluded(SkillRouter.SkillInfo)"/>——router 的各道闸门都这么做。
        /// 任何要统计或展示"被隐藏技能数"的地方也必须用 SkillInfo 重载，否则数字会与 router 实际拦掉的对不上。
        /// </summary>
        public static bool IsExcluded(SkillCategory category, bool readOnly)
        {
            return IsExcludedCore(Current, null, category, readOnly, mutatesScene: false);
        }

        /// <summary>
        /// 权威的排除判定：档位强制的全部规则，全部依据单个技能自身的元数据得出。
        /// 各发现面与两道闸门调用的都是它。
        /// </summary>
        internal static bool IsExcluded(SkillRouter.SkillInfo skill)
        {
            if (skill == null) return false;
            return IsExcludedCore(Current, skill.Name, skill.Category, skill.ReadOnly, skill.MutatesScene);
        }

        /// <summary>
        /// 规则顺序及各条存在的理由：
        /// <list type="number">
        /// <item><b>只读技能永不隐藏。</b>档位收掉的是创作能力，不是观察能力——
        /// 一个看不到场景的 AI 也没法讲清手工步骤。</item>
        /// <item><b>后门名单里的名字一律隐藏</b>（见 <see cref="_alwaysHiddenSkillNames"/>）。</item>
        /// <item><b>该分类在本档位的隐藏集里。</b>这是元数据驱动的默认规则。</item>
        /// <item><b>NoSceneAuthoring 额外隐藏所有声明 <c>MutatesScene</c> 的写操作。</b>
        /// 一个技能自称会改场景，那它无论住在哪个模块都是自认的场景创作——
        /// 一个叫"不做场景创作"的档位却放它过去，就是自相矛盾。正是这条规则关掉了 Netcode 与 Behavior
        /// 两个模块：它们不在分类名单里，但确实声明了会改场景。</item>
        /// </list>
        /// </summary>
        private static bool IsExcludedCore(
            SurfaceProfileKind profile, string skillName, SkillCategory category, bool readOnly, bool mutatesScene)
        {
            if (profile == SurfaceProfileKind.Full) return false;
            if (readOnly) return false;

            if (skillName != null && _alwaysHiddenSkillNames.Contains(skillName))
                return true;

            var hidden = HiddenCategories(profile);
            if (hidden != null && hidden.Contains(category))
                return true;

            return profile == SurfaceProfileKind.NoSceneAuthoring && mutatesScene;
        }

        /// <summary>
        /// 对 <see cref="_alwaysHiddenSkillNames"/> 中的后门技能返回 true——即所有非 full 档下按名字隐藏的那些。
        /// 有了它，拒绝载荷可以按"这个技能能触达什么"来解释排除原因，而不是按分类——
        /// 在这些情形下分类说不出任何有用的东西。
        /// </summary>
        internal static bool IsAlwaysHiddenSkill(string skillName) =>
            skillName != null && _alwaysHiddenSkillNames.Contains(skillName);

        /// <summary>
        /// 教人手工完成该分类操作的 <c>manual-*</c> 文档；该分类没有则返回 null。
        /// 只有 Guide 涉及的分类附带此文档，这也正是 Guide 的拒绝能"可行动"（"读这个，然后指导用户"）
        /// 而 NoSceneAuthoring 的拒绝只能把用户指回面板的原因。
        /// </summary>
        public static string ManualDocFor(SkillCategory category)
        {
            switch (category)
            {
                case SkillCategory.GameObject: return "skills/manual-gameobject/SKILL.md";
                case SkillCategory.Component:  return "skills/manual-component/SKILL.md";
                case SkillCategory.Material:   return "skills/manual-material/SKILL.md";
                case SkillCategory.Scene:      return "skills/manual-scene/SKILL.md";
                // Sample 模块的写操作就是生成基元和调 transform——GameObject 的手工文档教的正是这些编辑器步骤，
                // 所以指向那里，而不是让 agent 无文可读。
                case SkillCategory.Sample:     return "skills/manual-gameobject/SKILL.md";
                default: return null;
            }
        }

        // ---- 载荷携带的写操作（carried writes） ----------------------------------------------
        //
        // 规则 1–4 读的是技能自身的元数据，只要声明如实描述了这次调用会写什么，判定就是对的。
        // 有两类入口破坏了这个前提：batch_execute 会执行 confirmToken 当初为之铸造的任意操作
        // （而铸造 token 的那些预览是 ReadOnly，故规则 1 会放它们过去——这本该如此，预览正是 AI
        // 用来描述它即将讲解的改动的方式）；工作流 undo/redo 技能则会重放某条已记录任务碰过的一切。
        // 两者都归在 Workflow 分类下，而没有任何档位隐藏该分类；NoSceneAuthoring 下规则 4 因它们声明了
        // MutatesScene 而关掉了它们，但 Guide 下它们曾是绕开 Guide 所收四个分类的一条可用小路。
        //
        // 在 Guide 下直接隐藏它们改动更小，但那是错的。Guide 从五十个分类里只收掉五个，所以这两个入口
        // 同样承载着 Guide 允许的操作——只碰资产的 batch kind、只碰过资产的任务的撤销——
        // 隐藏就等于把撤销这张安全网从 Guide 特意留给 AI 的那些写操作上抽走。
        // 那还会把 Workflow 分类塞进 Guide 隐藏集，而它背后没有 manual-* 文档，
        // 而正是那份文档才让 Guide 的拒绝成为可行动的。
        // 因此改为在执行时对载荷分类，并整体拒绝该次调用。

        /// <summary>
        /// 当前档位是否收掉 <paramref name="category"/> 下的写操作——问的是一次操作，而不是一个技能。
        /// <see cref="IsExcluded(SkillCategory, bool)"/> 上"会少报"的警示在这里不成问题：
        /// 被携带的操作没有自己的技能名可以去比后门名单，而规则 4 已由承载它的那个技能自身的
        /// <c>MutatesScene</c> 声明回答过了。
        /// </summary>
        internal static bool WithdrawsWriteIn(SkillCategory category) =>
            IsExcludedCore(Current, null, category, readOnly: false, mutatesScene: false);

        /// <summary>
        /// 当一次调用即将施加的写操作由载荷而非自身元数据决定、且落在被收掉的分类里时，返回的拒绝对象。
        /// 错误码、字段名与中止策略都与 router 自己的闸门保持一致。
        ///
        /// 这些字段放在顶层而不是嵌在 <c>details</c> 下，因为 router 的技能错误透传会把技能未识别的成员
        /// 原样转发，却会丢掉技能自己写的 <c>details</c>——嵌进去就等于静默丢失。
        ///
        /// <paramref name="subject"/> 是补全"&lt;subject&gt; writes the X category"的名词短语，
        /// 且必须避开 <see cref="SkillErrorClassifier"/> 用来归类的关键词（"missing"、"not found"、"invalid" 等）。
        /// 这也是 <paramref name="operation"/> 以字段而非插值进文案的方式传递的原因：
        /// 文本里出现一个叫 <c>fix_missing_scripts</c> 的 batch kind，会让这条拒绝被归类成"缺参数"，
        /// 于是 agent 收到一条"补更多参数再试"的建议修法。
        /// </summary>
        internal static object CarriedWriteRejection(
            string skillName, SkillCategory category, string subject, string operation)
        {
            var manualDoc = ManualDocFor(category);
            return new
            {
                success = false,
                error = $"Skill '{skillName}' is withdrawn by the current surface profile " +
                        $"'{CurrentWire}': {subject} writes the {category} category, which this profile hides.",
                errorCode = SkillErrorCode.SurfaceExcluded.ToWireString(),
                retryStrategy = SkillErrorResponse.Abort,
                surfaceProfile = CurrentWire,
                category = category.ToString(),
                operation,
                manualDoc,
                userControlled = true,
                hint = CarriedWriteHint(manualDoc),
            };
        }

        /// <summary>
        /// 同一结论，但附在一次仍然成功的预览上。预览是只读的，而在 Guide 档下它恰恰是 AI 手工描述改动所需的东西，
        /// 所以它照常返回 diff 与 token。它唯一不能做的是保持沉默：一个只拿到 <c>confirmToken: ab12</c> 的 agent
        /// 撞上执行期拒绝时会把它读成 bug，转头去找别的模块。调用方只在档位确实收掉该操作时附加此块，
        /// 因此 full 档下的载荷字节不变。
        /// </summary>
        internal static object CarriedWriteNotice(string blockedSkill, SkillCategory category)
        {
            var manualDoc = ManualDocFor(category);
            return new
            {
                blockedSkill,
                blockedBy = SkillErrorCode.SurfaceExcluded.ToWireString(),
                surfaceProfile = CurrentWire,
                category = category.ToString(),
                manualDoc,
                hint = CarriedWriteHint(manualDoc),
            };
        }

        /// <summary>
        /// 载荷携带写操作的那些入口，映射到其载荷可能被归入的全部分类。
        /// 供 dry-run / 计划授权预览查询——预览手上没有载荷可分类，否则会对一个执行闸门必然拒绝的调用
        /// 回答 <c>allowed:true</c>。
        ///
        /// 这里的分类清单是对真正做判定的两个分类器的镜像：
        /// <c>BatchSkills.SurfaceCategoryForKind</c>（kind 映射到 GameObject / Component / Material，
        /// 其 default 分支保守地落到 GameObject）与
        /// <c>WorkflowSkills.TryClassifySnapshot</c>（场景对象 → GameObject，<c>.unity</c> → Scene，
        /// <c>.mat</c> → Material）。之所以在此列出而非直接查询，是因为两个分类器都需要载荷；
        /// 而"这次调用有可能被拒"问的正是它们的并集。加宽任一分类器就必须同步加宽这里对应的条目，
        /// <c>ReviewFixRouterTests</c> 通过断言 guide 档下六个名字都会触发闸门把这一点钉住。
        /// 本清单无处可推导：第七个在执行时自行分类载荷的入口也必须加进来，否则它的预览又会开始说谎。
        /// </summary>
        private static readonly Dictionary<string, SkillCategory[]> _carriedWriteSkills =
            new Dictionary<string, SkillCategory[]>(StringComparer.Ordinal)
            {
                ["batch_execute"] = new[] { SkillCategory.GameObject, SkillCategory.Component, SkillCategory.Material },
                ["batch_retry_failed"] = new[] { SkillCategory.GameObject, SkillCategory.Component, SkillCategory.Material },
                ["workflow_undo_task"] = new[] { SkillCategory.GameObject, SkillCategory.Scene, SkillCategory.Material },
                ["workflow_redo_task"] = new[] { SkillCategory.GameObject, SkillCategory.Scene, SkillCategory.Material },
                ["workflow_revert_task"] = new[] { SkillCategory.GameObject, SkillCategory.Scene, SkillCategory.Material },
                ["workflow_session_undo"] = new[] { SkillCategory.GameObject, SkillCategory.Scene, SkillCategory.Material },
            };

        /// <summary>
        /// carried-write 入口的预览必须带上的载荷级警示：该技能不属于此类，或当前档位没有收掉其载荷
        /// 可能落入的任何分类时返回 null（因此 full 档恒为 null，其预览字节不变）。
        ///
        /// 刻意不是一个判定结论。预览手上没有载荷——没拿到 confirmToken，也没读过某个 task id 的快照——
        /// 所以它无从知道这一次具体调用是否会被拒；而对档位允许的每一种 batch kind 和撤销回答
        /// <c>allowed:false</c> 都是错的。它能说、也是技能级预览此前从未说过的，是这句
        /// <c>allowed:true</c> 是在没看载荷的情况下得出的。
        /// </summary>
        internal static object CarriedWritePreviewGate(string skillName)
        {
            if (IsFull || skillName == null) return null;
            if (!_carriedWriteSkills.TryGetValue(skillName, out var candidates)) return null;

            var withdrawn = new List<string>();
            foreach (var category in candidates)
            {
                if (WithdrawsWriteIn(category))
                    withdrawn.Add(category.ToString());
            }
            if (withdrawn.Count == 0) return null;

            var categoryList = string.Join(" / ", withdrawn.ToArray());
            return new
            {
                payloadGated = true,
                payloadGatedCategories = withdrawn.ToArray(),
                payloadGateHint =
                    $"Skill-level verdict only — this entry point applies whatever its payload carries, so the " +
                    $"\"{CurrentWire}\" profile is enforced at execute time against the classified payload, not against " +
                    $"this skill's metadata. A payload writing {categoryList} is refused with " +
                    $"{SkillErrorCode.SurfaceExcluded.ToWireString()} even though allowed is true here. Check the payload " +
                    $"before executing: a batch preview carries a \"surfaceExclusion\" block when the kind its token was " +
                    $"minted for is withdrawn, and for the workflow undo/redo skills the verdict comes from the recorded " +
                    $"task's snapshots. If it is withdrawn, teach the change by hand rather than retrying.",
            };
        }

        /// <summary>
        /// 告诉 agent 该改做什么。与 router 的分岔一致：有手工文档时 agent 以讲解者身份把活干完；
        /// 没有时只有用户能解除。两条分支都必须显式掐掉"换条路再试"的条件反射——
        /// 与被隐藏的技能不同，这里的入口是可见的，天然在邀请对方换个新 token 再来一次。
        /// </summary>
        private static string CarriedWriteHint(string manualDoc)
        {
            return manualDoc != null
                ? $"Do not retry and do not look for another route — previewing and planning stay open, applying does not. Read {manualDoc} and walk the user through the change in the Editor yourself, or ask them to switch the surface profile back to \"full\" in the UnitySkills panel if they want it automated."
                : $"Do not retry and do not look for another route. The \"{CurrentWire}\" profile excludes scene-authoring writes; tell the user this step needs one and let them switch the surface profile back to \"full\" in the UnitySkills panel.";
        }

        public static string ToWire(SurfaceProfileKind profile)
        {
            switch (profile)
            {
                case SurfaceProfileKind.Guide: return WireGuide;
                case SurfaceProfileKind.NoSceneAuthoring: return WireNoSceneAuthoring;
                default: return WireFull;
            }
        }

        /// <summary>
        /// 忽略大小写地解析 wire 值。无法识别的一律返回 false——调用方随后落到
        /// <see cref="SurfaceProfileKind.Full"/> 而不是猜，使拼写错误或更新版本写下的 pref
        /// 永不会静默隐藏技能。
        /// </summary>
        public static bool TryParseWire(string value, out SurfaceProfileKind profile)
        {
            profile = SurfaceProfileKind.Full;
            if (string.IsNullOrWhiteSpace(value)) return false;

            var trimmed = value.Trim();
            if (trimmed.Equals(WireFull, StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals(nameof(SurfaceProfileKind.Full), StringComparison.OrdinalIgnoreCase))
                return true;
            if (trimmed.Equals(WireGuide, StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals(nameof(SurfaceProfileKind.Guide), StringComparison.OrdinalIgnoreCase))
            {
                profile = SurfaceProfileKind.Guide;
                return true;
            }
            if (trimmed.Equals(WireNoSceneAuthoring, StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals(nameof(SurfaceProfileKind.NoSceneAuthoring), StringComparison.OrdinalIgnoreCase))
            {
                profile = SurfaceProfileKind.NoSceneAuthoring;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 读取持久化的档位，并在首次运行时迁移 2.7 之前的布尔 guide 开关：
        /// 曾开着 guide 模式、又从未选过档位的用户会落到 <see cref="SurfaceProfileKind.Guide"/>。
        /// 迁移是只读的——旧键原样保留，一旦写入过档位它就不再起作用，
        /// 因此降级回旧版插件时仍能找到自己那个开关完好如初。
        /// </summary>
        private static SurfaceProfileKind Load()
        {
            try
            {
                if (EditorPrefs.HasKey(PrefKeyProfile) &&
                    TryParseWire(EditorPrefs.GetString(PrefKeyProfile, null), out var stored))
                    return stored;

                return EditorPrefs.GetBool(PrefKeyLegacyGuideMode, false)
                    ? SurfaceProfileKind.Guide
                    : SurfaceProfileKind.Full;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"SurfaceProfile load failed, defaulting to full: {ex.Message}");
                return SurfaceProfileKind.Full;
            }
        }

        /// <summary>
        /// 逐个通知订阅者并彼此隔离，使某个抛异常的订阅者无法阻断其余订阅者。
        /// 若用单个 try/catch 包住 <c>OnChanged?.Invoke()</c>，第一个异常就会放弃整条调用链，
        /// 而其中一个订阅者是 <see cref="SkillRouter.InvalidateOutputCaches"/>——
        /// 也就是说，一个注册更早的 UI 处理器抛异常，就会让清单缓存继续持有用户刚刚收掉的技能，
        /// 而线索只有控制台里一条警告。缓存失效在这里是安全性不变量，不能依赖无关订阅者是否守规矩。
        /// </summary>
        private static void RaiseChanged()
        {
            var handlers = OnChanged;
            if (handlers == null) return;

            foreach (var handler in handlers.GetInvocationList())
            {
                try { ((Action)handler)?.Invoke(); }
                catch (Exception ex)
                {
                    SkillsLogger.LogWarning(
                        $"SurfaceProfile OnChanged handler '{handler.Method?.DeclaringType?.Name}.{handler.Method?.Name}' threw " +
                        $"(remaining handlers still ran): {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 在所有非 full 档下按名字隐藏的技能——因为它们身上任何元数据都表达不出"它们为什么是个问题"。
        ///
        /// <c>editor_execute_menu</c> 能执行任意 Unity 菜单路径。就那一个参数便可触达 GameObject/Create、
        /// Edit/Delete、Component/Add——正是每个档位存在的意义所要收掉的全部写操作。
        /// 它的分类（Editor）不在任何隐藏集里，也永远不该在，因为该模块其余部分都是正当工具；
        /// 于是分类规则表达不了"这一个是万能钥匙"，只能靠名字。放它可调用会让其他所有排除沦为装饰：
        /// 一个被 gameobject_create 拦住的 agent 执行一句 "GameObject/Create Empty" 就照样干下去了。
        ///
        /// 刻意保持为一份封闭的、极小的名单。它不是用来藏那些分类规则已能覆盖的技能的——
        /// 这里每多一个名字，就多一份元数据承载不了的维护负担。
        /// </summary>
        private static readonly HashSet<string> _alwaysHiddenSkillNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "editor_execute_menu",
        };

        // ---- 隐藏分类集 --------------------------------------------------------------------
        //
        // 只列分类。某个技能是否算写操作由它自己的 ReadOnly 元数据决定，
        // 所以新增技能时这些集合无需改动。

        private static readonly HashSet<SkillCategory> _guideHidden = new HashSet<SkillCategory>
        {
            SkillCategory.GameObject,
            SkillCategory.Component,
            SkillCategory.Material,
            SkillCategory.Scene,
            // Sample 入集是看它的技能"做了什么"，不是看名字给人的印象：
            // create_cube / delete_object / set_object_position 就是换了个标签的 GameObject 创作。
            // 漏掉它会让 guide 边界留下一条可用小路——被 gameobject_create 拦住的 agent
            // 照样能用 create_cube 生成一个立方体。
            SkillCategory.Sample,
        };

        // 凡写操作会产出 Scene/Game 视图里所见内容的模块，都在此列。刻意比 Guide 的四个宽：
        // 这个档位面向的用户是希望 AI 干资产、代码和诊断，而把场景本身留在自己手里。
        //
        // 如今它已不是全部：IsExcludedCore 的规则 4 还会隐藏任何声明 MutatesScene 的写操作，不论分类。
        // 本集合仍然保留，因为它能抓住那些元数据恰好没置该标志的场景创作写操作；
        // 而那个标志能抓住谁也没想到要列在这里的模块。两者单独都不够。
        private static readonly HashSet<SkillCategory> _noSceneAuthoringHidden = new HashSet<SkillCategory>
        {
            SkillCategory.Cinemachine,
            // Smart 入集也是看它的写操作做了什么，不是看名字：该模块写的那一半是场景摆放
            // （对齐网格、对齐、分布、贴地），作用于当前选中的任何东西。名字读起来像个分析辅助，
            // 这正是它当初被漏掉的原因——在这个档位下 smart_snap_to_grid 真的在挪对象。
            SkillCategory.Smart,
            SkillCategory.UI,
            SkillCategory.UIToolkit,
            SkillCategory.ProBuilder,
            SkillCategory.DOTween,
            SkillCategory.Material,
            SkillCategory.XR,
            SkillCategory.GameObject,
            SkillCategory.ShaderGraph,
            SkillCategory.Component,
            SkillCategory.Timeline,
            SkillCategory.Prefab,
            SkillCategory.Camera,
            SkillCategory.PostProcess,
            SkillCategory.Terrain,
            SkillCategory.Light,
            SkillCategory.Animator,
            SkillCategory.Volume,
            SkillCategory.Decal,
            SkillCategory.URP,
            SkillCategory.Shader,
            SkillCategory.Physics,
            SkillCategory.Model,
            SkillCategory.Texture,
            SkillCategory.Graphics,
            SkillCategory.Scene,
            SkillCategory.NavMesh,
            SkillCategory.Audio,
            SkillCategory.PrimeTween,
            // 理由同 _guideHidden：Sample 的写操作产出的是场景内容。
            SkillCategory.Sample,
        };
    }
}

// Producer:Betsy
