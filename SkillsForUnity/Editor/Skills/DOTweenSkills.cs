using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// DOTween Pro 的 DOTweenAnimation 编辑期配置技能。
    /// 对 DOTween / DOTweenAnimation 的访问一律走反射，未安装 DOTween 时程序集照样编译。
    /// DOTWEEN / DOTWEEN_PRO 两个 scripting define 由 DOTweenPresenceDetector 自动维护，
    /// 它们只是快速判定信号（省去探测的短路），不是编译开关。
    /// </summary>
    public static class DOTweenSkills
    {
        private static object NoDOTween() => DOTweenReflectionHelper.NoDOTween();
        private static object NoDOTweenPro() => DOTweenReflectionHelper.NoDOTweenPro();

        // ==================================================================================
        // 免费版运行时 / 工程诊断
        // ==================================================================================

        [UnitySkill("dotween_get_status",
            "Get DOTween installation status, Pro availability, DOTweenSettings presence, and visible module count. Works with DOTween Free or Pro.",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Query,
            Tags = new[] { "dotween", "free", "status", "installed", "modules" },
            Outputs = new[] { "isDOTweenInstalled", "isDOTweenProInstalled", "settingsFound", "moduleCount" },
            Mode = SkillMode.SemiAuto)]
        public static object DOTweenGetStatus()
        {
            var dotweenType = DOTweenReflectionHelper.FindTypeInAssemblies(DOTweenReflectionHelper.DOTweenTypeName);
            var proType = DOTweenReflectionHelper.FindTypeInAssemblies(DOTweenReflectionHelper.DOTweenAnimationTypeName);
            var settings = Resources.Load("DOTweenSettings");
            var moduleTypes = FindDOTweenTypes(t => IsDOTweenModuleType(t)).ToList();

            return new
            {
                isDOTweenInstalled = dotweenType != null,
                isDOTweenProInstalled = proType != null,
                dotweenType = dotweenType?.AssemblyQualifiedName,
                dotweenAnimationType = proType?.AssemblyQualifiedName,
                settingsFound = settings != null,
                settingsPath = settings != null ? AssetDatabase.GetAssetPath(settings) : null,
                moduleCount = moduleTypes.Count,
                modules = moduleTypes.Select(t => t.FullName).OrderBy(n => n).ToArray()
            };
        }

        [UnitySkill("dotween_settings_get",
            "Read common fields from Resources/DOTweenSettings.asset. Works with DOTween Free or Pro.",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Query,
            Tags = new[] { "dotween", "free", "settings", "read", "query" },
            Outputs = new[] { "success", "path", "fields" },
            Mode = SkillMode.SemiAuto)]
        public static object DOTweenSettingsGet()
        {
            if (!DOTweenReflectionHelper.IsDOTweenInstalled) return NoDOTween();

            var settings = Resources.Load("DOTweenSettings");
            if (settings == null) return DOTweenSettingsMissing();

            return new
            {
                success = true,
                path = AssetDatabase.GetAssetPath(settings),
                fields = ReadDOTweenSettingsFields(settings)
            };
        }

        [UnitySkill("dotween_settings_find",
            "Find DOTweenSettings assets in the project. Works with DOTween Free or Pro.",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Query,
            Tags = new[] { "dotween", "free", "settings", "find", "asset" },
            Outputs = new[] { "count", "paths", "resourcesLoadPath" },
            Mode = SkillMode.SemiAuto)]
        public static object DOTweenSettingsFind()
        {
            var paths = FindDOTweenSettingsPaths();
            var settings = Resources.Load("DOTweenSettings");
            return new
            {
                count = paths.Count,
                paths,
                resourcesLoadFound = settings != null,
                resourcesLoadPath = settings != null ? AssetDatabase.GetAssetPath(settings) : null
            };
        }

        [UnitySkill("dotween_settings_validate",
            "Validate basic DOTweenSettings health: missing asset, invalid capacities, SafeMode/logBehaviour visibility. Works with DOTween Free or Pro.",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Query,
            Tags = new[] { "dotween", "free", "settings", "validate", "diagnostic" },
            Outputs = new[] { "success", "isValid", "issues", "warnings" },
            Mode = SkillMode.SemiAuto)]
        public static object DOTweenSettingsValidate()
        {
            if (!DOTweenReflectionHelper.IsDOTweenInstalled) return NoDOTween();

            var settings = Resources.Load("DOTweenSettings");
            var issues = new List<string>();
            var warnings = new List<string>();
            var paths = FindDOTweenSettingsPaths();

            if (settings == null)
            {
                issues.Add("DOTweenSettings.asset was not found via Resources.Load(\"DOTweenSettings\"). Run Tools > Demigiant > DOTween Utility Panel > Setup DOTween.");
            }
            if (paths.Count > 1)
            {
                warnings.Add($"Found {paths.Count} DOTweenSettings assets. DOTween loads by Resources path, so duplicate settings can be confusing.");
            }

            Dictionary<string, object> fields = null;
            if (settings != null)
            {
                fields = ReadDOTweenSettingsFields(settings);
                ValidateCapacity(fields, "defaultTweensCapacity", issues);
                ValidateCapacity(fields, "defaultSequencesCapacity", issues);
                if (fields.TryGetValue("useSafeMode", out var safeMode) && safeMode is bool b && !b)
                    warnings.Add("useSafeMode is disabled. This is valid, but destroyed/missing targets will be less forgiving.");
            }

            return new
            {
                success = true,
                isValid = issues.Count == 0,
                issues,
                warnings,
                paths,
                fields
            };
        }

        [UnitySkill("dotween_list_modules",
            "List visible DOTween module and extension types loaded in the current Unity domain. Works with DOTween Free or Pro.",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Query,
            Tags = new[] { "dotween", "free", "modules", "extensions", "reflection" },
            Outputs = new[] { "count", "types" },
            Mode = SkillMode.SemiAuto)]
        public static object DOTweenListModules(bool includeMethods = false, int methodLimit = 20)
        {
            if (!DOTweenReflectionHelper.IsDOTweenInstalled) return NoDOTween();

            var types = FindDOTweenTypes(t => IsDOTweenModuleType(t) || IsDOTweenExtensionContainer(t))
                .OrderBy(t => t.FullName)
                .Select(t => new
                {
                    name = t.Name,
                    fullName = t.FullName,
                    assembly = t.Assembly.GetName().Name,
                    publicStaticMethodCount = t.GetMethods(BindingFlags.Public | BindingFlags.Static).Length,
                    methods = includeMethods
                        ? t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                            .Select(m => m.Name)
                            .Distinct()
                            .OrderBy(n => n)
                            .Take(Mathf.Max(methodLimit, 1))
                            .ToArray()
                        : null
                })
                .ToArray();

            return new { count = types.Length, types };
        }

        [UnitySkill("dotween_list_shortcuts",
            "List public DOTween shortcut/extension methods, optionally filtered by target type and method prefix. Works with DOTween Free or Pro.",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Query,
            Tags = new[] { "dotween", "free", "shortcut", "extension", "methods" },
            Outputs = new[] { "count", "methods" },
            Mode = SkillMode.SemiAuto)]
        public static object DOTweenListShortcuts(string targetType = null, string methodPrefix = null, int limit = 100)
        {
            if (!DOTweenReflectionHelper.IsDOTweenInstalled) return NoDOTween();

            var methods = FindDOTweenTypes(IsDOTweenExtensionContainer)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Where(IsExtensionMethod)
                .Select(ToShortcutInfo)
                .Where(m => string.IsNullOrEmpty(targetType) ||
                            (m.targetType != null && m.targetType.IndexOf(targetType, StringComparison.OrdinalIgnoreCase) >= 0))
                .Where(m => string.IsNullOrEmpty(methodPrefix) ||
                            m.name.StartsWith(methodPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.targetType)
                .ThenBy(m => m.name)
                .Take(Mathf.Max(limit, 1))
                .ToArray();

            return new { count = methods.Length, methods };
        }

        [UnitySkill("dotween_generate_tween_script",
            "Generate a minimal runtime DOTween MonoBehaviour script for DOTween Free/Pro. Does not attach it to scene objects.",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Create,
            Tags = new[] { "dotween", "free", "generate", "script", "runtime", "tween" },
            Outputs = new[] { "success", "path", "className" },
            RequiresInput = new[] { "className" },
            TracksWorkflow = true, MutatesAssets = true, MayTriggerReload = true, RiskLevel = "high")]
        public static object DOTweenGenerateTweenScript(
            string className,
            string folder = "Assets/Scripts/DOTween",
            string namespaceName = null,
            string targetKind = "Transform",
            string tweenKind = "DOMove",
            float duration = 1f,
            string ease = "OutQuad",
            int loops = 1,
            bool autoPlay = true,
            bool useSetLink = true)
        {
            if (!DOTweenReflectionHelper.IsDOTweenInstalled) return NoDOTween();
            var spec = ResolveRuntimeTweenSpec(targetKind, tweenKind);
            if (spec == null) return UnsupportedTween(targetKind, tweenKind);

            var content = BuildTweenScript(className, namespaceName, spec, duration, ease, loops, autoPlay, useSetLink);
            return WriteGeneratedScript(className, folder, content);
        }

        [UnitySkill("dotween_generate_sequence_script",
            "Generate a minimal runtime DOTween Sequence MonoBehaviour script. stepsJson optionally accepts [{op,tweenKind,duration}]. Does not attach it to scene objects.",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Create,
            Tags = new[] { "dotween", "free", "generate", "script", "runtime", "sequence" },
            Outputs = new[] { "success", "path", "className" },
            RequiresInput = new[] { "className" },
            TracksWorkflow = true, MutatesAssets = true, MayTriggerReload = true, RiskLevel = "high")]
        public static object DOTweenGenerateSequenceScript(
            string className,
            string folder = "Assets/Scripts/DOTween",
            string namespaceName = null,
            string targetKind = "Transform",
            string tweenKind = "DOMove",
            float duration = 1f,
            string ease = "OutQuad",
            int loops = 1,
            bool autoPlay = true,
            bool useSetLink = true,
            string stepsJson = null)
        {
            if (!DOTweenReflectionHelper.IsDOTweenInstalled) return NoDOTween();
            var steps = ParseSequenceSteps(stepsJson, tweenKind, duration);
            if (steps == null) return new { error = "stepsJson must be a JSON array of { op: Append|Join|AppendInterval, tweenKind, duration }." };

            var specs = new List<(string op, RuntimeTweenSpec spec, float duration)>();
            foreach (var step in steps)
            {
                if (string.Equals(step.op, "AppendInterval", StringComparison.OrdinalIgnoreCase))
                {
                    specs.Add(("AppendInterval", null, Mathf.Max(step.duration, 0f)));
                    continue;
                }
                var op = string.Equals(step.op, "Join", StringComparison.OrdinalIgnoreCase) ? "Join" : "Append";
                var spec = ResolveRuntimeTweenSpec(targetKind, step.tweenKind ?? tweenKind);
                if (spec == null) return UnsupportedTween(targetKind, step.tweenKind ?? tweenKind);
                specs.Add((op, spec, step.duration > 0f ? step.duration : duration));
            }

            var content = BuildSequenceScript(className, namespaceName, targetKind, specs, duration, ease, loops, autoPlay, useSetLink);
            return WriteGeneratedScript(className, folder, content);
        }

        [UnitySkill("dotween_generate_lifetime_script",
            "Generate a DOTween lifetime-safe MonoBehaviour wrapper that uses SetLink by default and kills owned tweens on disable/destroy.",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Create,
            Tags = new[] { "dotween", "free", "generate", "script", "lifetime", "safe" },
            Outputs = new[] { "success", "path", "className" },
            RequiresInput = new[] { "className" },
            TracksWorkflow = true, MutatesAssets = true, MayTriggerReload = true, RiskLevel = "high")]
        public static object DOTweenGenerateLifetimeScript(
            string className,
            string folder = "Assets/Scripts/DOTween",
            string namespaceName = null,
            string targetKind = "Transform",
            string tweenKind = "DOScale",
            float duration = 1f,
            string ease = "OutQuad",
            int loops = 1,
            bool autoPlay = true,
            bool useSetLink = true)
        {
            if (!DOTweenReflectionHelper.IsDOTweenInstalled) return NoDOTween();
            var spec = ResolveRuntimeTweenSpec(targetKind, tweenKind);
            if (spec == null) return UnsupportedTween(targetKind, tweenKind);

            var content = BuildLifetimeScript(className, namespaceName, spec, duration, ease, loops, autoPlay, useSetLink);
            return WriteGeneratedScript(className, folder, content);
        }

        // ==================================================================================
        // A. 生成
        // ==================================================================================

        [UnitySkill("dotween_pro_add_animation",
            "Add a DOTweenAnimation component to a GameObject and configure it (DOTween Pro only). " +
            "animationType: Move/LocalMove/Rotate/LocalRotate/Scale/Punch*/Shake*/AnchorPos3D/AnchorPos/UIWidthHeight/Fade/FillAmount/CameraOrthoSize/CameraFieldOfView/Value/Color/CameraBackgroundColor/Text/UIRect. " +
            "Supply the matching endValue* param for the type (V3/V2/Float/Color/String/Rect). " +
            "ease: one of 38 Ease enum names (OutQuad default). loopType: Yoyo/Restart/Incremental. " +
            "An unknown animationType/ease/loopType, a duration <= 0, a loops value other than -1 or >= 1, and a negative delay are all rejected before anything is added to the scene.",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Create,
            Tags = new[] { "dotween", "animation", "tween", "ui", "pro", "add" },
            Outputs = new[] { "success", "component", "animationIndex" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesScene = true, RiskLevel = "low")]
        public static object DOTweenProAddAnimation(
            string target = null, int targetInstanceId = 0, string targetPath = null,
            string animationType = "Move",
            string endValueV3 = null,
            float? endValueFloat = null,
            string endValueColor = null,
            string endValueV2 = null,
            string endValueString = null,
            string endValueRect = null,
            float duration = 1f,
            string ease = "OutQuad",
            int loops = 1,
            string loopType = "Yoyo",
            float delay = 0f,
            bool isRelative = false,
            bool isFrom = false,
            bool autoPlay = true,
            bool autoKill = true,
            string id = null)
        {
            if (!DOTweenReflectionHelper.IsDOTweenInstalled) return NoDOTween();
            if (!DOTweenReflectionHelper.IsDOTweenProInstalled) return NoDOTweenPro();

            if (ValidateAnimationSpec(animationType, ease, loopType, duration, loops, delay) is object specErr)
                return specErr;

            var (go, err) = GameObjectFinder.FindOrError(name: target, instanceId: targetInstanceId, path: targetPath);
            if (err != null) return err;

            var result = AddAnimationCore(go, animationType, endValueV3, endValueFloat, endValueColor,
                endValueV2, endValueString, endValueRect,
                duration, ease, loops, loopType, delay, isRelative, isFrom, autoPlay, autoKill, id);
            return result;
        }

        [UnitySkill("dotween_pro_batch_add_animation",
            "Add the same DOTweenAnimation to multiple GameObjects. targetsJson is a JSON array of names or paths.",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Create,
            Tags = new[] { "dotween", "animation", "batch", "ui", "pro" },
            Outputs = new[] { "success", "added", "failed" },
            RequiresInput = new[] { "gameObjects" },
            TracksWorkflow = true, MutatesScene = true, RiskLevel = "low")]
        public static object DOTweenProBatchAddAnimation(
            string targetsJson,
            string animationType = "Move",
            string endValueV3 = null,
            float? endValueFloat = null,
            string endValueColor = null,
            string endValueV2 = null,
            string endValueString = null,
            string endValueRect = null,
            float duration = 1f,
            string ease = "OutQuad",
            int loops = 1,
            string loopType = "Yoyo",
            float delay = 0f,
            bool isRelative = false,
            bool isFrom = false,
            bool autoPlay = true,
            bool autoKill = true,
            string id = null)
        {
            if (!DOTweenReflectionHelper.IsDOTweenInstalled) return NoDOTween();
            if (!DOTweenReflectionHelper.IsDOTweenProInstalled) return NoDOTweenPro();

            var targets = ParseTargetList(targetsJson);
            if (targets == null) return new { error = "targetsJson must be a JSON array of strings" };

            // 在入口一次性拒绝，而不是逐项报错：这些参数为所有目标共用，逐项失败只会把
            // 同一个调用方错误复读 N 遍，而且等调用方看到时前面的目标早就加上去了。
            if (ValidateAnimationSpec(animationType, ease, loopType, duration, loops, delay) is object specErr)
                return specErr;

            var added = new List<object>();
            var failed = new List<object>();
            foreach (var t in targets)
            {
                var (go, err) = GameObjectFinder.FindOrError(name: t);
                if (err != null) { failed.Add(new { target = t, error = err }); continue; }

                var r = AddAnimationCore(go, animationType, endValueV3, endValueFloat, endValueColor,
                    endValueV2, endValueString, endValueRect,
                    duration, ease, loops, loopType, delay, isRelative, isFrom, autoPlay, autoKill, id);
                if (IsSuccess(r)) added.Add(new { target = t, result = r });
                else failed.Add(new { target = t, error = r });
            }
            return new { success = failed.Count == 0, added, failed };
        }

        [UnitySkill("dotween_pro_stagger_animations",
            "Batch-add DOTweenAnimation with incrementing delay (UI cascade entrance). " +
            "Each target i gets delay = baseDelay + i * staggerDelay; both must be >= 0 (DOTween clamps a negative delay away silently, so it is rejected instead).",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Create,
            Tags = new[] { "dotween", "animation", "stagger", "cascade", "ui", "pro" },
            Outputs = new[] { "success", "added" },
            RequiresInput = new[] { "gameObjects" },
            TracksWorkflow = true, MutatesScene = true, RiskLevel = "low")]
        public static object DOTweenProStaggerAnimations(
            string targetsJson,
            string animationType = "Move",
            string endValueV3 = null,
            float? endValueFloat = null,
            string endValueColor = null,
            string endValueV2 = null,
            float duration = 0.5f,
            string ease = "OutBack",
            int loops = 1,
            string loopType = "Yoyo",
            float baseDelay = 0f,
            float staggerDelay = 0.1f,
            bool isFrom = true,
            bool autoPlay = true,
            bool autoKill = true)
        {
            if (!DOTweenReflectionHelper.IsDOTweenInstalled) return NoDOTween();
            if (!DOTweenReflectionHelper.IsDOTweenProInstalled) return NoDOTweenPro();

            var targets = ParseTargetList(targetsJson);
            if (targets == null) return new { error = "targetsJson must be a JSON array of strings" };

            // baseDelay / staggerDelay 为负必须拒绝：DOTween 会一声不吭地把负延迟夹掉，
            // 于是回报给调用方的错峰效果（以及逐项回显的 delay）根本不存在。
            if (InvalidNonNegativeError(baseDelay, "baseDelay") is object baseErr) return baseErr;
            if (InvalidNonNegativeError(staggerDelay, "staggerDelay") is object staggerErr) return staggerErr;
            if (ValidateAnimationSpec(animationType, ease, loopType, duration, loops, baseDelay) is object specErr)
                return specErr;

            var added = new List<object>();
            var failed = new List<object>();
            for (int i = 0; i < targets.Count; i++)
            {
                var (go, err) = GameObjectFinder.FindOrError(name: targets[i]);
                if (err != null) { failed.Add(new { target = targets[i], error = err }); continue; }
                float delay = baseDelay + i * staggerDelay;
                var r = AddAnimationCore(go, animationType, endValueV3, endValueFloat, endValueColor,
                    endValueV2, null, null,
                    duration, ease, loops, loopType, delay, false, isFrom, autoPlay, autoKill, null);
                if (IsSuccess(r)) added.Add(new { target = targets[i], delay, result = r });
                else failed.Add(new { target = targets[i], error = r });
            }
            return new { success = failed.Count == 0, added, failed };
        }

        // ==================================================================================
        // B. 调参 —— 3 个专用 + 2 个通用
        // ==================================================================================

        [UnitySkill("dotween_pro_set_duration",
            "Set the duration (seconds) of an existing DOTweenAnimation. duration is required and must be > 0. " +
            "Use animationIndex when a GameObject has multiple DOTweenAnimation components (default 0) — take the index from dotween_pro_list_animations, which numbers per GameObject in component order. " +
            "The response echoes 'applied' plus the value read back off the component.",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Modify,
            Tags = new[] { "dotween", "duration", "tweak", "animation", "pro" },
            Outputs = new[] { "success", "applied", "duration", "gameObject", "animationIndex" },
            RequiresInput = new[] { "gameObject", "duration" },
            TracksWorkflow = true, MutatesScene = true, RiskLevel = "low")]
        public static object DOTweenProSetDuration(
            string target = null, int targetInstanceId = 0, string targetPath = null,
            int animationIndex = 0, float? duration = null)
        {
            // 先校参数域，再解析目标：值非法与是否装了 Pro 无关，放在这里校验才能在没有
            // Asset Store 包的环境下也观察到这条拒绝。duration 既是可空的又列进了
            // RequiresInput：若写成 float duration = 1f，省略它就会把动画静默重置为 1s
            // 并报成功——CLR 默认值与显式传 1 无法区分。
            if (Validate.Required(duration, "duration") is object missing) return missing;
            if (InvalidPositiveError(duration.Value, "duration") is object invalid) return invalid;

            var (comp, err) = ResolveAnimationComponent(target, targetInstanceId, targetPath, animationIndex);
            if (err != null) return err;

            Undo.RecordObject(comp, "DOTween set duration");
            if (!DOTweenReflectionHelper.SetFieldByCandidates(comp, DOTweenReflectionHelper.DurationFieldCandidates, duration.Value))
                return new { error = "Failed to set duration on DOTweenAnimation" };
            WorkflowManager.SnapshotObject(comp);
            EditorUtility.SetDirty(comp);
            return new
            {
                success = true,
                gameObject = comp.gameObject.name,
                animationIndex,
                applied = new[] { "duration" },
                duration = DOTweenReflectionHelper.GetFieldByCandidates(comp, DOTweenReflectionHelper.DurationFieldCandidates)
            };
        }

        [UnitySkill("dotween_pro_set_ease",
            "Set the ease of an existing DOTweenAnimation (Ease enum name, or easeCurveJson for a custom AnimationCurve — easeCurveJson wins when both are sent). " +
            "An unknown ease name or an unparseable easeCurveJson is rejected with the accepted values; the response echoes 'applied' plus the ease read back off the component.",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Modify,
            Tags = new[] { "dotween", "ease", "curve", "animation", "pro" },
            Outputs = new[] { "success", "applied", "ease", "gameObject", "animationIndex" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true, MutatesScene = true, RiskLevel = "low")]
        public static object DOTweenProSetEase(
            string target = null, int targetInstanceId = 0, string targetPath = null,
            int animationIndex = 0, string ease = "OutQuad", string easeCurveJson = null)
        {
            // 传了但解析不了的 easeCurveJson 必须在动组件之前就拒绝：否则会掉进按名设缓动的
            // 分支，装上 OutQuad 还报 success:true——那是静默的错误缓动，而不是拒绝。
            AnimationCurve curve = null;
            if (!string.IsNullOrEmpty(easeCurveJson) &&
                !DOTweenReflectionHelper.TryParseEaseCurve(easeCurveJson, out curve))
            {
                return SkillParamUtil.InvalidValueError(easeCurveJson, "easeCurveJson", new[]
                {
                    "[{\"time\":0,\"value\":0},{\"time\":1,\"value\":1}]",
                    "JsonUtility-serialized AnimationCurve JSON",
                });
            }

            var (comp, err) = ResolveAnimationComponent(target, targetInstanceId, targetPath, animationIndex);
            if (err != null) return err;

            // 缓动名要对着当前 DOTween 版本真正声明的 Ease 枚举校验，且在第一次写入之前完成，
            // 这样被拒的名字不会在组件上留下任何改动。
            if (curve == null &&
                !DOTweenReflectionHelper.EnumFieldAccepts(comp.GetType(), DOTweenReflectionHelper.EaseFieldCandidates, ease))
            {
                return SkillParamUtil.InvalidValueError(ease, "ease",
                    DOTweenReflectionHelper.EnumNamesForField(comp.GetType(), DOTweenReflectionHelper.EaseFieldCandidates));
            }

            Undo.RecordObject(comp, "DOTween set ease");
            if (curve != null)
            {
                if (!DOTweenReflectionHelper.TrySetEaseCurve(comp, curve))
                    return new { error = "Failed to install the custom ease curve: this DOTweenAnimation has no easeCurve field or no INTERNAL_Custom Ease member, so the curve would be ignored at runtime." };
            }
            else if (!DOTweenReflectionHelper.TrySetEase(comp, ease))
            {
                return new { error = $"Failed to set ease '{ease}' on DOTweenAnimation" };
            }
            WorkflowManager.SnapshotObject(comp);
            EditorUtility.SetDirty(comp);
            return new
            {
                success = true,
                gameObject = comp.gameObject.name,
                animationIndex,
                applied = new[] { curve != null ? "easeCurveJson" : "ease" },
                ease = DOTweenReflectionHelper.GetFieldByCandidates(comp, DOTweenReflectionHelper.EaseFieldCandidates)?.ToString()
            };
        }

        [UnitySkill("dotween_pro_set_loops",
            "Set loops count and/or loopType for an existing DOTweenAnimation. loops=-1 means infinite; DOTween has no other negative loop count, so anything below -1 (and 0) is rejected. " +
            "Send loops, loopType, or both — omitting both is refused rather than silently resetting loops to 1. The response echoes 'applied' plus the values read back off the component.",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Modify,
            Tags = new[] { "dotween", "loops", "loop", "animation", "pro" },
            Outputs = new[] { "success", "applied", "loops", "loopType", "gameObject", "animationIndex" },
            RequiresInput = new[] { "gameObject", "loops|loopType" },
            TracksWorkflow = true, MutatesScene = true, RiskLevel = "low")]
        public static object DOTweenProSetLoops(
            string target = null, int targetInstanceId = 0, string targetPath = null,
            int animationIndex = 0, int? loops = null, string loopType = null)
        {
            // 两个参数都必须可空可选：本 setter 是两个互不相干的半边，写成 int loops = 1 时，
            // 只传 loopType 的调用会把无限循环静默改成播一次。两个都不传属于调用方错误，
            // 不是空操作。
            if (!loops.HasValue && string.IsNullOrEmpty(loopType))
                return MissingEitherError("loops", "loopType");
            if (loops.HasValue && InvalidLoopsError(loops.Value) is object invalidLoops) return invalidLoops;

            var (comp, err) = ResolveAnimationComponent(target, targetInstanceId, targetPath, animationIndex);
            if (err != null) return err;

            // 必须在第一次写入前校验：先写 loops 再拒绝 loopType，
            // 会在一个错误响应下留下半边已经生效的改动。
            if (!string.IsNullOrEmpty(loopType) &&
                !DOTweenReflectionHelper.EnumFieldAccepts(comp.GetType(), DOTweenReflectionHelper.LoopTypeFieldCandidates, loopType))
            {
                return SkillParamUtil.InvalidValueError(loopType, "loopType",
                    DOTweenReflectionHelper.EnumNamesForField(comp.GetType(), DOTweenReflectionHelper.LoopTypeFieldCandidates));
            }

            Undo.RecordObject(comp, "DOTween set loops");
            var applied = new List<string>();
            if (loops.HasValue)
            {
                if (!DOTweenReflectionHelper.SetFieldByCandidates(comp, DOTweenReflectionHelper.LoopsFieldCandidates, loops.Value))
                    return new { error = "Failed to set loops field" };
                applied.Add("loops");
            }
            if (!string.IsNullOrEmpty(loopType))
            {
                if (!DOTweenReflectionHelper.TrySetLoopType(comp, loopType))
                    return new { error = $"Failed to set loopType '{loopType}' on DOTweenAnimation" };
                applied.Add("loopType");
            }
            WorkflowManager.SnapshotObject(comp);
            EditorUtility.SetDirty(comp);
            return new
            {
                success = true,
                gameObject = comp.gameObject.name,
                animationIndex,
                applied = applied.ToArray(),
                loops = DOTweenReflectionHelper.GetFieldByCandidates(comp, DOTweenReflectionHelper.LoopsFieldCandidates),
                loopType = DOTweenReflectionHelper.GetFieldByCandidates(comp, DOTweenReflectionHelper.LoopTypeFieldCandidates)?.ToString()
            };
        }

        [UnitySkill("dotween_pro_set_animation_field",
            "Generic field setter for a DOTweenAnimation component. " +
            "Use the dedicated skills (dotween_pro_set_duration / _set_ease / _set_loops) for those common fields — this skill rejects duration/ease/easeType/easeCurve/loops/loopType. " +
            "Valid targets: delay / isRelative / isFrom / autoPlay / autoKill / id / endValueV3 / endValueFloat / endValueColor / optionalFloat0 / etc. " +
            "fieldValue is required (vec/color parsed automatically) — send \"\" to deliberately clear a string field. " +
            "An unknown fieldName is rejected with the settable field list; the response echoes 'applied' plus the value read back off the component.",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Modify,
            Tags = new[] { "dotween", "field", "reflection", "animation", "pro" },
            Outputs = new[] { "success", "applied", "fieldName", "fieldValue", "gameObject", "animationIndex" },
            RequiresInput = new[] { "gameObject", "fieldName", "fieldValue" },
            TracksWorkflow = true, MutatesScene = true, RiskLevel = "low")]
        public static object DOTweenProSetAnimationField(
            string target = null, int targetInstanceId = 0, string targetPath = null,
            int animationIndex = 0, string fieldName = null, string fieldValue = null)
        {
            if (Validate.Required(fieldName, "fieldName") is object missingName) return missingName;
            if (DOTweenReflectionHelper.ReservedByDedicatedSkills.Contains(fieldName))
                return new
                {
                    error = $"Field '{fieldName}' must be modified via the dedicated skill " +
                            "(dotween_pro_set_duration / dotween_pro_set_ease / dotween_pro_set_loops). " +
                            "This keeps intent explicit and avoids accidental ease/loop type mismatches.",
                    errorCode = SkillParamUtil.SemanticInvalidCode,
                    parameter = "fieldName",
                };
            // 省略 fieldValue 时不能放行：它会以 null 走到反射层把字段清空，响应却报成功。
            // 只有显式传空串 "" 才表示清空——路由把两者分得很清（显式空串原样绑定，
            // 缺键才绑 CLR 默认值），所以这里不去猜意图。
            if (fieldValue == null)
                return MissingFieldValueError(fieldName);

            var (comp, err) = ResolveAnimationComponent(target, targetInstanceId, targetPath, animationIndex);
            if (err != null) return err;

            // "字段不存在"与"值转换不了"要分开报：合成一个 bool 会让后者也去怪 fieldName，
            // 而它其实是 fieldValue 的问题。
            var field = DOTweenReflectionHelper.ResolveField(comp.GetType(), fieldName);
            if (field == null)
                return SkillParamUtil.InvalidValueError(fieldName, "fieldName",
                    DOTweenReflectionHelper.SettableFieldNames(comp.GetType()));

            Undo.RecordObject(comp, $"DOTween set {fieldName}");
            if (!DOTweenReflectionHelper.SetFieldByName(comp, fieldName, fieldValue))
                return SkillParamUtil.InvalidValueError(fieldValue, "fieldValue", AcceptedFieldValues(field.FieldType));
            WorkflowManager.SnapshotObject(comp);
            EditorUtility.SetDirty(comp);
            return new
            {
                success = true,
                gameObject = comp.gameObject.name,
                animationIndex,
                applied = new[] { fieldName },
                fieldName,
                fieldValue = DOTweenReflectionHelper.DumpFieldValue(comp, fieldName)
            };
        }

        [UnitySkill("dotween_pro_get_animation",
            "Read all serialized fields of a single DOTweenAnimation component (use animationIndex to pick one).",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Query,
            Tags = new[] { "dotween", "inspect", "animation", "pro" },
            Outputs = new[] { "fields" },
            RequiresInput = new[] { "gameObject" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object DOTweenProGetAnimation(
            string target = null, int targetInstanceId = 0, string targetPath = null,
            int animationIndex = 0)
        {
            var (comp, err) = ResolveAnimationComponent(target, targetInstanceId, targetPath, animationIndex);
            if (err != null) return err;

            var fields = DOTweenReflectionHelper.DumpAllFields(comp);
            return new { success = true, fields, componentName = comp.GetType().Name, gameObject = comp.gameObject.name };
        }

        // ==================================================================================
        // C. 辅助 —— 列举 / 复制 / 移除
        // ==================================================================================

        [UnitySkill("dotween_pro_list_animations",
            "List all DOTweenAnimation components under a target (set recursive=true for the whole hierarchy). " +
            "animationIndex is the component order on its own GameObject — the same index every dotween_pro_* setter/remover addresses.",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Query,
            Tags = new[] { "dotween", "list", "animation", "pro" },
            Outputs = new[] { "success", "count", "animations" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object DOTweenProListAnimations(
            string target = null, int targetInstanceId = 0, string targetPath = null,
            bool recursive = false)
        {
            if (!DOTweenReflectionHelper.IsDOTweenProInstalled) return NoDOTweenPro();

            var type = DOTweenReflectionHelper.FindTypeInAssemblies(DOTweenReflectionHelper.DOTweenAnimationTypeName);
            if (type == null) return NoDOTweenPro();

            Component[] comps;
            if (!string.IsNullOrEmpty(target) || targetInstanceId != 0 || !string.IsNullOrEmpty(targetPath))
            {
                var (go, err) = GameObjectFinder.FindOrError(name: target, instanceId: targetInstanceId, path: targetPath);
                if (err != null) return err;
                comps = recursive
                    ? go.GetComponentsInChildren(type, includeInactive: true)
                    : go.GetComponents(type);
            }
            else
            {
                comps = FindHelper.FindAll(type, includeInactive: true).OfType<Component>().ToArray();
            }

            var list = new List<object>();
            foreach (var pair in ResolveAuthoritativeIndices(comps, type))
            {
                var c = pair.Key;
                var go = c.gameObject;
                list.Add(new
                {
                    gameObject = go.name,
                    entityId = UnityObjectIdUtility.GetEntityId(go),
                    instanceId = UnityObjectIdUtility.GetObjectId(go),
                    animationIndex = pair.Value,
                    animationType = DOTweenReflectionHelper.GetFieldByCandidates(c, DOTweenReflectionHelper.AnimationTypeFieldCandidates)?.ToString(),
                    duration = DOTweenReflectionHelper.GetFieldByCandidates(c, DOTweenReflectionHelper.DurationFieldCandidates),
                    ease = DOTweenReflectionHelper.GetFieldByCandidates(c, DOTweenReflectionHelper.EaseFieldCandidates)?.ToString(),
                    loops = DOTweenReflectionHelper.GetFieldByCandidates(c, DOTweenReflectionHelper.LoopsFieldCandidates),
                    id = DOTweenReflectionHelper.GetFieldByCandidates(c, DOTweenReflectionHelper.IdFieldCandidates)?.ToString()
                });
            }
            return new { success = true, count = list.Count, animations = list };
        }

        [UnitySkill("dotween_pro_copy_animation",
            "Copy all fields of a DOTweenAnimation from sourceTarget[sourceIndex] to destTarget (adds a new component).",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Create,
            Tags = new[] { "dotween", "copy", "duplicate", "animation", "pro" },
            Outputs = new[] { "success" },
            RequiresInput = new[] { "gameObjects" },
            TracksWorkflow = true, MutatesScene = true, RiskLevel = "low")]
        public static object DOTweenProCopyAnimation(
            string sourceTarget, string destTarget, int sourceIndex = 0)
        {
            if (!DOTweenReflectionHelper.IsDOTweenProInstalled) return NoDOTweenPro();

            var (srcComp, srcErr) = ResolveAnimationComponent(sourceTarget, 0, null, sourceIndex);
            if (srcErr != null) return srcErr;

            var (destGo, destErr) = GameObjectFinder.FindOrError(name: destTarget);
            if (destErr != null) return destErr;

            var type = DOTweenReflectionHelper.FindTypeInAssemblies(DOTweenReflectionHelper.DOTweenAnimationTypeName);
            var dst = Undo.AddComponent(destGo, type);
            if (dst == null) return new { error = "Failed to add DOTweenAnimation to destination" };

            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (f.IsInitOnly) continue;
                try { f.SetValue(dst, f.GetValue(srcComp)); }
                catch { /* skip unassignable fields */ }
            }
            WorkflowManager.SnapshotCreatedComponent(dst);
            EditorUtility.SetDirty(dst);
            return new { success = true, sourceGameObject = srcComp.gameObject.name, destGameObject = destGo.name };
        }

        [UnitySkill("dotween_pro_remove_animation",
            "Remove a single DOTweenAnimation component by animationIndex (default 0).",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Delete,
            Tags = new[] { "dotween", "remove", "delete", "animation", "pro" },
            Outputs = new[] { "success" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true, MutatesScene = true, RiskLevel = "low")]
        public static object DOTweenProRemoveAnimation(
            string target = null, int targetInstanceId = 0, string targetPath = null,
            int animationIndex = 0)
        {
            var (comp, err) = ResolveAnimationComponent(target, targetInstanceId, targetPath, animationIndex);
            if (err != null) return err;

            if (!WorkflowManager.DeleteSceneObject(comp))
                return new { error = "Failed to capture and remove DOTweenAnimation" };
            return new { success = true };
        }

        // ==================================================================================
        // D. 设置
        // ==================================================================================

        [UnitySkill("dotween_settings_configure",
            "Configure Resources/DOTweenSettings.asset (defaultEaseType/defaultAutoKill/defaultLoopType/safeMode/logBehaviour/tweenersCapacity/sequencesCapacity). " +
            "Any parameter left null is not modified. Fields this DOTween version's DOTweenSettings does not declare are reported in 'unsupported' instead of being silently swallowed as success.",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Modify,
            Tags = new[] { "dotween", "settings", "configure", "capacity", "safemode" },
            Outputs = new[] { "success", "modified", "unsupported" },
            MutatesAssets = true, RiskLevel = "low")]
        public static object DOTweenSettingsConfigure(
            string defaultEaseType = null,
            bool? defaultAutoKill = null,
            string defaultLoopType = null,
            bool? safeMode = null,
            string logBehaviour = null,
            int? tweenersCapacity = null,
            int? sequencesCapacity = null)
        {
            if (!DOTweenReflectionHelper.IsDOTweenInstalled) return NoDOTween();

            var settings = Resources.Load("DOTweenSettings");
            if (settings == null)
            {
                return new
                {
                    error = "DOTweenSettings.asset not found in any Resources folder. " +
                            "Open Tools > Demigiant > DOTween Utility Panel and click 'Setup DOTween...' once to generate it."
                };
            }

            var write = ApplySettingsFields(settings, defaultEaseType, defaultAutoKill, defaultLoopType,
                safeMode, logBehaviour, tweenersCapacity, sequencesCapacity);
            if (write.Error != null) return write.Error;

            if (write.Modified.Count == 0)
            {
                return new
                {
                    success = true,
                    modified = new string[0],
                    unsupported = write.Unsupported.ToArray(),
                    note = write.Unsupported.Count > 0
                        ? "No fields changed: every supplied parameter maps to a DOTweenSettings field this DOTween version does not declare (see unsupported)."
                        : "No fields changed"
                };
            }

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            return new
            {
                success = true,
                modified = write.Modified.ToArray(),
                unsupported = write.Unsupported.ToArray()
            };
        }

        // ==================================================================================
        // 内部核心
        // ==================================================================================

        /// <summary>
        /// 一份生成脚本的配方。定为 internal 而非 private，是为了能直接对生成契约下断言
        /// （某类目标需要哪个 <c>using</c>、某一步是否把 duration 烘成字面量）——
        /// 生成器本身在未装 DOTween 时会拒绝运行，所以在干净工程上无法端到端测试产出的文本。
        /// </summary>
        internal class RuntimeTweenSpec
        {
            public string targetKind;
            public string tweenKind;
            public string fieldType;
            public string fieldName;
            public string fieldInitializer;
            public string valueField;
            public string valueType;
            public string defaultValue;
            public string methodCall;
            public string extraUsing;
            public bool genericDOTweenTo;
        }

        private class SequenceStepSpec
        {
            public string op { get; set; }
            public string tweenKind { get; set; }
            public float duration { get; set; }
        }

        private class ShortcutInfo
        {
            public string name { get; set; }
            public string declaringType { get; set; }
            public string targetType { get; set; }
            public string returnType { get; set; }
            public string signature { get; set; }
        }

        // ==================================================================================
        // 数值域与必填性守卫
        //
        // dotween_pro_* 的数字若原样透传，duration=-1、loops=-7 都会落到组件上并报
        // success:true，调用方毫无察觉。DOTween 自身的取值域足够窄，可以精确声明。
        // ==================================================================================

        /// <summary>duration 非正就不是补间：DOTween 会当成瞬移，并在播放时才打日志，
        /// 那时距离技能声称"已配置好动画"早就过去了。</summary>
        private static object InvalidPositiveError(float value, string paramName) =>
            value > 0f ? null : SkillParamUtil.InvalidValueError(SkillParamUtil.FormatFloatR(value), paramName, new[] { "> 0" });

        /// <summary>负的 delay / 错峰步长等于让级联倒着走时间；DOTween 会静默夹掉，
        /// 于是回报给调用方的那个动画根本不存在。</summary>
        private static object InvalidNonNegativeError(float value, string paramName) =>
            value >= 0f ? null : SkillParamUtil.InvalidValueError(SkillParamUtil.FormatFloatR(value), paramName, new[] { ">= 0" });

        /// <summary>-1 是 DOTween 唯一的无限循环标记。0 以及小于 -1 的值都没有意义，
        /// DOTween 自己既不夹取也不报错。</summary>
        private static object InvalidLoopsError(int value) =>
            value == -1 || value >= 1
                ? null
                : SkillParamUtil.InvalidValueError(value.ToString(CultureInfo.InvariantCulture), "loops",
                    new[] { "-1 (infinite)", ">= 1" });

        /// <summary>
        /// 表达"这两个至少给一个"。载荷形状与 <see cref="Validate"/> 的缺参响应一致
        /// （errorCode 会被路由层 1 原样透传），因为两半单看都不是必填，
        /// 逐参数检查说不出这个约束。
        /// </summary>
        private static object MissingEitherError(string first, string second) => new
        {
            error = $"Provide {first} and/or {second} — neither was sent, so there is nothing to change. " +
                    $"Sending only {second} keeps the current {first}, and vice versa.",
            errorCode = SkillErrorCode.MissingParam.ToWireString(),
            retryStrategy = SkillErrorResponse.RetryFixAndRetry,
            parameter = $"{first}|{second}",
        };

        private static object MissingFieldValueError(string fieldName) => new
        {
            error = $"fieldValue is required. It was omitted for fieldName '{fieldName}', which used to " +
                    "clear the field and still report success. Send the value you want, or an explicit " +
                    "empty string (\"\") to clear a string field on purpose.",
            errorCode = SkillErrorCode.MissingParam.ToWireString(),
            retryStrategy = SkillErrorResponse.RetryFixAndRetry,
            parameter = "fieldValue",
        };

        /// <summary>
        /// 给定字段类型下 <c>fieldValue</c> 可以长成什么样，用于拒绝响应里的 <c>validValues</c>。
        /// 内容与 <c>DOTweenReflectionHelper.ConvertValue</c> 接受的字符串形式保持一致——
        /// 列出转换器解析不了的形式，比不列还糟。
        /// </summary>
        private static string[] AcceptedFieldValues(Type fieldType)
        {
            if (fieldType == null) return Array.Empty<string>();
            if (fieldType.IsEnum) return DOTweenReflectionHelper.EnumNames(fieldType);
            if (fieldType == typeof(bool)) return new[] { "true", "false" };
            if (fieldType == typeof(Vector3)) return new[] { "x,y,z", "[x,y,z]" };
            if (fieldType == typeof(Vector2)) return new[] { "x,y", "[x,y]" };
            if (fieldType == typeof(Color)) return new[] { "#RRGGBB", "#RRGGBBAA", "r,g,b", "r,g,b,a" };
            if (fieldType == typeof(Rect)) return new[] { "x,y,width,height" };
            if (fieldType == typeof(float) || fieldType == typeof(double)) return new[] { "a decimal number (invariant '.' separator)" };
            if (fieldType == typeof(int) || fieldType == typeof(long)) return new[] { "a whole number" };
            if (fieldType == typeof(string)) return new[] { "any string (\"\" clears it)" };
            return new[] { $"a value convertible to {fieldType.Name} — this field is not settable from text" };
        }

        // ==================================================================================
        // 组件下标的权威来源
        // ==================================================================================

        /// <summary>
        /// 为每个组件配上使用方真正会用来寻址的下标，也就是它在
        /// <c>gameObject.GetComponents(type)</c> 中的位置——所有
        /// <c>ResolveAnimationComponent</c> 调用索引的都是这个数组。
        ///
        /// <para>之所以需要它，是因为全场景列举路径原本不按这个顺序：它把
        /// <c>FindHelper.FindAll</c>（文档明确说无序）的结果按 GameObject 分组后发一个自增计数，
        /// 于是同一个 GameObject 上挂多个 DOTweenAnimation 时，列举报出的 animationIndex
        /// 与 setter 使用的下标对不上。实际工程中出现过：列举报
        /// [Fade 0.3, Scale 0.6, Fade 0.4]，而该对象的 GetComponents 顺序是
        /// [Scale 0.6, Fade 0.3, Fade 0.4]——agent 先列举再设置，改的其实是另一个组件，
        /// 而且两次调用都成功，完全无感。</para>
        ///
        /// <para>输出按每个 GameObject 的权威顺序。类型匹配的查询理论上不会出现权威数组里
        /// 没有的组件，但真出现时保留并置下标为 -1 而非丢弃：列表悄悄少一行是更糟的失败，
        /// 而负下标会被 ResolveAnimationComponent 拒绝，不会误指到别的组件上。</para>
        /// </summary>
        internal static List<KeyValuePair<Component, int>> ResolveAuthoritativeIndices(
            IEnumerable<Component> comps, Type componentType)
        {
            var result = new List<KeyValuePair<Component, int>>();
            if (comps == null || componentType == null) return result;

            foreach (var group in comps.Where(c => c != null).GroupBy(c => c.gameObject))
            {
                var authoritative = group.Key.GetComponents(componentType);
                var indexed = group
                    .Select(c => new KeyValuePair<Component, int>(c, Array.IndexOf(authoritative, c)))
                    .OrderBy(pair => pair.Value < 0 ? int.MaxValue : pair.Value);
                result.AddRange(indexed);
            }
            return result;
        }

        // ==================================================================================
        // DOTweenSettings 写入
        // ==================================================================================

        /// <summary>因对应字段不存在而无法应用的单个参数。</summary>
        internal sealed class UnsupportedSettingsField
        {
            public string parameter;
            public string field;
            public string reason;
        }

        internal sealed class SettingsWriteResult
        {
            public readonly List<string> Modified = new List<string>();
            public readonly List<UnsupportedSettingsField> Unsupported = new List<UnsupportedSettingsField>();
            public object Error;
        }

        /// <summary>
        /// 把配置参数应用到传入的对象上，并逐参数汇报结果。之所以从技能里拆出来，
        /// 是为了能拿替身设置对象来测——它修的缺陷只在缺字段的 DOTweenSettings 上才显形。
        ///
        /// <para>DOTween Pro 1.0.381 的设置资源根本没有 <c>defaultTweensCapacity</c> /
        /// <c>defaultSequencesCapacity</c>。若两次写入只用裸的 <c>if (SetFieldByName(...))</c>
        /// 守卫、false 分支什么都不做，响应就会是 <c>success:true, modified:[]</c>，
        /// 读起来像"已接受，没什么要改"，而不是"这个 DOTween 版本没地方放你的值"。
        /// 那四个 enum/bool 参数的 <c>f != null &amp;&amp; f.FieldType.IsEnum</c> 守卫同样会静默。
        /// 因此每个参数都必须恰好落到 modified / unsupported / Error 三者之一。</para>
        /// </summary>
        internal static SettingsWriteResult ApplySettingsFields(
            object settings,
            string defaultEaseType,
            bool? defaultAutoKill,
            string defaultLoopType,
            bool? safeMode,
            string logBehaviour,
            int? tweenersCapacity,
            int? sequencesCapacity)
        {
            var result = new SettingsWriteResult();
            if (settings == null)
            {
                result.Error = new { error = "DOTweenSettings instance is null" };
                return result;
            }

            if (!ApplyEnumSetting(settings, "defaultEaseType", defaultEaseType, result)) return result;
            if (!ApplyEnumSetting(settings, "defaultLoopType", defaultLoopType, result)) return result;
            if (!ApplyEnumSetting(settings, "logBehaviour", logBehaviour, result)) return result;

            ApplyBoolSetting(settings, "defaultAutoKill", "defaultAutoKill", defaultAutoKill, result);
            ApplyBoolSetting(settings, "useSafeMode", "safeMode", safeMode, result);

            if (!ApplyCapacitySetting(settings, "defaultTweensCapacity", "tweenersCapacity", tweenersCapacity, result)) return result;
            if (!ApplyCapacitySetting(settings, "defaultSequencesCapacity", "sequencesCapacity", sequencesCapacity, result)) return result;

            return result;
        }

        /// <summary>返回 false 表示整个调用都该被拒（给的值该字段根本表达不了）。</summary>
        private static bool ApplyEnumSetting(object settings, string fieldName, string value, SettingsWriteResult result)
        {
            if (string.IsNullOrEmpty(value)) return true;

            var field = DOTweenReflectionHelper.ResolveField(settings.GetType(), fieldName);
            if (field == null || !field.FieldType.IsEnum)
            {
                result.Unsupported.Add(Unsupported(fieldName, fieldName,
                    field == null
                        ? "no such field on this DOTween version's DOTweenSettings"
                        : $"field exists but is {field.FieldType.Name}, not an enum"));
                return true;
            }

            var names = DOTweenReflectionHelper.EnumNames(field.FieldType);
            if (!DOTweenReflectionHelper.EnumFieldAccepts(settings.GetType(), new[] { fieldName }, value))
            {
                result.Error = SkillParamUtil.InvalidValueError(value, fieldName, names);
                return false;
            }

            field.SetValue(settings, Enum.Parse(field.FieldType, value.Trim(), ignoreCase: true));
            result.Modified.Add(fieldName);
            return true;
        }

        private static void ApplyBoolSetting(object settings, string fieldName, string parameterName,
            bool? value, SettingsWriteResult result)
        {
            if (!value.HasValue) return;

            var field = DOTweenReflectionHelper.ResolveField(settings.GetType(), fieldName);
            if (field == null || field.FieldType != typeof(bool))
            {
                result.Unsupported.Add(Unsupported(parameterName, fieldName,
                    field == null
                        ? "no such field on this DOTween version's DOTweenSettings"
                        : $"field exists but is {field.FieldType.Name}, not bool"));
                return;
            }

            field.SetValue(settings, value.Value);
            result.Modified.Add(fieldName);
        }

        /// <summary>返回 false 表示整个调用都该被拒。</summary>
        private static bool ApplyCapacitySetting(object settings, string fieldName, string parameterName,
            int? value, SettingsWriteResult result)
        {
            if (!value.HasValue) return true;

            // dotween_settings_validate 已经把 capacity <= 0 当成问题上报，
            // 真写进去会让本插件下次读取时把自己刚做的改动判为非法。
            if (value.Value <= 0)
            {
                result.Error = SkillParamUtil.InvalidValueError(
                    value.Value.ToString(CultureInfo.InvariantCulture), parameterName, new[] { ">= 1" });
                return false;
            }

            var field = DOTweenReflectionHelper.ResolveField(settings.GetType(), fieldName);
            if (field == null || (field.FieldType != typeof(int) && !field.FieldType.IsEnum))
            {
                result.Unsupported.Add(Unsupported(parameterName, fieldName,
                    field == null
                        ? "no such field on this DOTween version's DOTweenSettings — DOTween Pro 1.0.381 has none, its capacities are set at runtime via DOTween.SetTweensCapacity(tweenersCapacity, sequencesCapacity)"
                        : $"field exists but is {field.FieldType.Name}, not int"));
                return true;
            }

            field.SetValue(settings, value.Value);
            result.Modified.Add(fieldName);
            return true;
        }

        private static UnsupportedSettingsField Unsupported(string parameter, string field, string reason) =>
            new UnsupportedSettingsField { parameter = parameter, field = field, reason = reason };

        /// <summary>
        /// add / batch / stagger 三个技能共用的数值与枚举契约，在往场景里添加任何东西之前检查。
        /// 枚举名对着当前 DOTween 版本真正声明的枚举比对，因此拒绝响应里的 validValues
        /// 是真实词表，而不是会随资源包版本漂移的硬编码列表。
        /// </summary>
        private static object ValidateAnimationSpec(
            string animationType, string ease, string loopType, float duration, int loops, float delay)
        {
            if (InvalidPositiveError(duration, "duration") is object durationErr) return durationErr;
            if (InvalidLoopsError(loops) is object loopsErr) return loopsErr;
            if (InvalidNonNegativeError(delay, "delay") is object delayErr) return delayErr;

            var type = DOTweenReflectionHelper.FindTypeInAssemblies(DOTweenReflectionHelper.DOTweenAnimationTypeName);
            if (type == null) return NoDOTweenPro();

            if (InvalidEnumFieldError(type, DOTweenReflectionHelper.AnimationTypeFieldCandidates, "animationType", animationType) is object typeErr)
                return typeErr;
            if (InvalidEnumFieldError(type, DOTweenReflectionHelper.EaseFieldCandidates, "ease", ease) is object easeErr)
                return easeErr;
            if (InvalidEnumFieldError(type, DOTweenReflectionHelper.LoopTypeFieldCandidates, "loopType", loopType) is object loopTypeErr)
                return loopTypeErr;

            return null;
        }

        private static object InvalidEnumFieldError(Type owner, string[] candidates, string paramName, string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            if (DOTweenReflectionHelper.EnumFieldAccepts(owner, candidates, value)) return null;
            return SkillParamUtil.InvalidValueError(value, paramName,
                DOTweenReflectionHelper.EnumNamesForField(owner, candidates));
        }

        private static IEnumerable<Type> FindDOTweenTypes(Func<Type, bool> predicate)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
                    catch { return Array.Empty<Type>(); }
                })
                .Where(t => t != null && !string.IsNullOrEmpty(t.FullName) && t.FullName.StartsWith("DG.Tweening", StringComparison.Ordinal))
                .Where(predicate);
        }

        private static bool IsDOTweenModuleType(Type t)
        {
            return t.IsClass && t.IsAbstract && t.IsSealed && t.Name.StartsWith("DOTweenModule", StringComparison.Ordinal);
        }

        private static bool IsDOTweenExtensionContainer(Type t)
        {
            return t.IsClass && t.IsAbstract && t.IsSealed &&
                   (t.Name.IndexOf("ShortcutExtensions", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.Name.IndexOf("TweenExtensions", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.Name.IndexOf("TweenSettingsExtensions", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.Name.StartsWith("DOTweenModule", StringComparison.Ordinal));
        }

        private static bool IsExtensionMethod(MethodInfo method)
        {
            return method.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false) &&
                   method.GetParameters().Length > 0;
        }

        private static ShortcutInfo ToShortcutInfo(MethodInfo method)
        {
            var parameters = method.GetParameters();
            return new ShortcutInfo
            {
                name = method.Name,
                declaringType = method.DeclaringType?.FullName,
                targetType = parameters.Length > 0 ? FriendlyTypeName(parameters[0].ParameterType) : null,
                returnType = FriendlyTypeName(method.ReturnType),
                signature = $"{FriendlyTypeName(method.ReturnType)} {method.Name}({string.Join(", ", parameters.Select(p => FriendlyTypeName(p.ParameterType) + " " + p.Name))})"
            };
        }

        private static string FriendlyTypeName(Type type)
        {
            if (type == null) return null;
            if (!type.IsGenericType) return type.FullName ?? type.Name;
            var name = type.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0) name = name.Substring(0, tick);
            return $"{type.Namespace}.{name}<{string.Join(",", type.GetGenericArguments().Select(FriendlyTypeName))}>";
        }

        private static List<string> FindDOTweenSettingsPaths()
        {
            return AssetDatabase.FindAssets("DOTweenSettings t:ScriptableObject")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !string.IsNullOrEmpty(p) && string.Equals(Path.GetFileNameWithoutExtension(p), "DOTweenSettings", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p)
                .ToList();
        }

        private static object DOTweenSettingsMissing() => new
        {
            error = "DOTweenSettings.asset not found in any Resources folder. Open Tools > Demigiant > DOTween Utility Panel and click 'Setup DOTween...' once to generate it."
        };

        private static Dictionary<string, object> ReadDOTweenSettingsFields(object settings)
        {
            var names = new[]
            {
                "useSafeMode", "safeModeOptions", "timeScale", "useSmoothDeltaTime", "maxSmoothUnscaledTime",
                "rewindCallbackMode", "showUnityEditorReport", "logBehaviour", "drawGizmos",
                "defaultRecyclable", "defaultAutoPlay", "defaultUpdateType", "defaultTimeScaleIndependent",
                "defaultEaseType", "defaultEaseOvershootOrAmplitude", "defaultEasePeriod", "defaultAutoKill",
                "defaultLoopType", "defaultTweensCapacity", "defaultSequencesCapacity"
            };
            var fields = new Dictionary<string, object>();
            foreach (var name in names)
            {
                var field = DOTweenReflectionHelper.ResolveField(settings.GetType(), name);
                if (field != null) fields[name] = StringifySettingsValue(field.GetValue(settings));
            }
            return fields;
        }

        private static object StringifySettingsValue(object value)
        {
            if (value == null) return null;
            if (value is Enum e) return e.ToString();
            if (value is UnityEngine.Object o) return o != null ? AssetDatabase.GetAssetPath(o) : null;
            var type = value.GetType();
            if (type.IsPrimitive || value is string || value is decimal) return value;
            return value.ToString();
        }

        private static void ValidateCapacity(Dictionary<string, object> fields, string fieldName, List<string> issues)
        {
            if (!fields.TryGetValue(fieldName, out var value)) return;
            if (value is int i && i <= 0) issues.Add($"{fieldName} should be greater than 0.");
        }

        internal static RuntimeTweenSpec ResolveRuntimeTweenSpec(string targetKind, string tweenKind)
        {
            targetKind = string.IsNullOrWhiteSpace(targetKind) ? "Transform" : targetKind.Trim();
            tweenKind = string.IsNullOrWhiteSpace(tweenKind) ? "DOMove" : tweenKind.Trim();
            var key = $"{targetKind}:{tweenKind}".ToLowerInvariant();

            RuntimeTweenSpec TransformSpec(string method, string valueType, string defaultValue, string fieldName, string call) => new RuntimeTweenSpec
            {
                targetKind = "Transform", tweenKind = method, fieldType = "Transform", fieldName = "targetTransform",
                fieldInitializer = "targetTransform = transform;", valueType = valueType, valueField = fieldName,
                defaultValue = defaultValue, methodCall = call
            };

            switch (key)
            {
                case "transform:domove": return TransformSpec("DOMove", "Vector3", "new Vector3(0f, 1f, 0f)", "endPosition", "targetTransform.DOMove(endPosition, duration)");
                case "transform:dolocalmove": return TransformSpec("DOLocalMove", "Vector3", "new Vector3(0f, 1f, 0f)", "endLocalPosition", "targetTransform.DOLocalMove(endLocalPosition, duration)");
                case "transform:dorotate": return TransformSpec("DORotate", "Vector3", "new Vector3(0f, 180f, 0f)", "endRotation", "targetTransform.DORotate(endRotation, duration)");
                case "transform:dolocalrotate": return TransformSpec("DOLocalRotate", "Vector3", "new Vector3(0f, 180f, 0f)", "endLocalRotation", "targetTransform.DOLocalRotate(endLocalRotation, duration)");
                case "transform:doscale": return TransformSpec("DOScale", "Vector3", "Vector3.one * 1.2f", "endScale", "targetTransform.DOScale(endScale, duration)");
                case "transform:dopunchposition": return TransformSpec("DOPunchPosition", "Vector3", "new Vector3(0f, 0.25f, 0f)", "punch", "targetTransform.DOPunchPosition(punch, duration)");
                case "transform:doshakeposition": return TransformSpec("DOShakePosition", "Vector3", "new Vector3(0.25f, 0.25f, 0f)", "strength", "targetTransform.DOShakePosition(duration, strength)");
                case "recttransform:doanchorpos": return RectSpec("DOAnchorPos", "Vector2", "new Vector2(0f, 100f)", "endAnchorPosition", "targetRectTransform.DOAnchorPos(endAnchorPosition, duration)");
                case "recttransform:dosizedelta": return RectSpec("DOSizeDelta", "Vector2", "new Vector2(200f, 80f)", "endSizeDelta", "targetRectTransform.DOSizeDelta(endSizeDelta, duration)");
                case "canvasgroup:dofade": return UiSpec("CanvasGroup", "targetCanvasGroup", "targetCanvasGroup = GetComponent<CanvasGroup>();", "DOFade", "float", "0f", "endAlpha", "targetCanvasGroup.DOFade(endAlpha, duration)");
                case "graphic:docolor": return UiSpec("Graphic", "targetGraphic", "targetGraphic = GetComponent<Graphic>();", "DOColor", "Color", "Color.white", "endColor", "targetGraphic.DOColor(endColor, duration)");
                case "graphic:dofade": return UiSpec("Graphic", "targetGraphic", "targetGraphic = GetComponent<Graphic>();", "DOFade", "float", "0f", "endAlpha", "targetGraphic.DOFade(endAlpha, duration)");
                case "image:docolor": return UiSpec("Image", "targetImage", "targetImage = GetComponent<Image>();", "DOColor", "Color", "Color.white", "endColor", "targetImage.DOColor(endColor, duration)");
                case "image:dofade": return UiSpec("Image", "targetImage", "targetImage = GetComponent<Image>();", "DOFade", "float", "0f", "endAlpha", "targetImage.DOFade(endAlpha, duration)");
                case "generic:dotween.to": return new RuntimeTweenSpec
                {
                    targetKind = "Generic", tweenKind = "DOTween.To", fieldType = null, fieldName = null,
                    valueType = "float", valueField = "endValue", defaultValue = "1f", genericDOTweenTo = true,
                    methodCall = "DOTween.To(() => currentValue, value => currentValue = value, endValue, duration)"
                };
                default: return null;
            }
        }

        private static RuntimeTweenSpec RectSpec(string method, string valueType, string defaultValue, string fieldName, string call) => new RuntimeTweenSpec
        {
            targetKind = "RectTransform", tweenKind = method, fieldType = "RectTransform", fieldName = "targetRectTransform",
            fieldInitializer = "targetRectTransform = transform as RectTransform;", valueType = valueType, valueField = fieldName,
            defaultValue = defaultValue, methodCall = call
        };

        private static RuntimeTweenSpec UiSpec(string type, string field, string initializer, string method, string valueType, string defaultValue, string valueField, string call) => new RuntimeTweenSpec
        {
            targetKind = type, tweenKind = method, fieldType = type, fieldName = field, fieldInitializer = initializer,
            valueType = valueType, valueField = valueField, defaultValue = defaultValue, methodCall = call,
            extraUsing = ExtraUsingForTargetKind(type)
        };

        /// <summary>
        /// 生成脚本为其目标类型额外需要的 <c>using</c>——只有当该类型确实位于那个命名空间时才输出。
        ///
        /// <para>几个"看起来像 UI"的目标不能共用一句硬编码的 <c>using UnityEngine.UI;</c>：
        /// <c>CanvasGroup</c> 属于 <c>UnityEngine</c>（UIModule，永远存在），而
        /// <c>Graphic</c> / <c>Image</c> 属于 <c>UnityEngine.UI</c>，随 com.unity.ugui 提供。
        /// 在没装该包的工程里，生成的 CanvasGroup 文件会因为一个自身根本没引用的命名空间
        /// 报 CS0246 编译失败。生成过程是纯字符串拼接，唯一能决定这件事的只有目标类型
        /// 自己所在的命名空间。</para>
        /// </summary>
        private static string ExtraUsingForTargetKind(string targetKind)
        {
            switch (targetKind)
            {
                case "Graphic":
                case "Image":
                case "Text":
                    return "using UnityEngine.UI;";
                default:
                    // Transform / RectTransform / CanvasGroup / Generic 都在 UnityEngine 命名空间下。
                    return null;
            }
        }

        private static object UnsupportedTween(string targetKind, string tweenKind) => new
        {
            error = $"Unsupported DOTween Free runtime tween targetKind='{targetKind}', tweenKind='{tweenKind}'. Supported targetKind/tweenKind pairs: Transform DOMove/DOLocalMove/DORotate/DOLocalRotate/DOScale/DOPunchPosition/DOShakePosition; RectTransform DOAnchorPos/DOSizeDelta; CanvasGroup DOFade; Graphic/Image DOColor/DOFade; Generic DOTween.To."
        };

        private static List<SequenceStepSpec> ParseSequenceSteps(string stepsJson, string tweenKind, float duration)
        {
            if (string.IsNullOrWhiteSpace(stepsJson))
            {
                return new List<SequenceStepSpec>
                {
                    new SequenceStepSpec { op = "Append", tweenKind = tweenKind, duration = duration },
                    new SequenceStepSpec { op = "AppendInterval", duration = 0.1f },
                    new SequenceStepSpec { op = "Append", tweenKind = tweenKind, duration = duration }
                };
            }
            try { return JsonConvert.DeserializeObject<List<SequenceStepSpec>>(stepsJson); }
            catch { return null; }
        }

        private static object WriteGeneratedScript(string className, string folder, string content)
        {
            if (string.IsNullOrWhiteSpace(className)) return new { error = "className is required" };
            if (!IsValidClassName(className)) return new { error = "className must be a valid C# identifier and must not contain path separators" };
            if (Validate.SafePath(folder, "folder") is object folderErr) return folderErr;
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

            var path = Path.Combine(folder, className + ".cs").Replace("\\", "/");
            if (File.Exists(path)) return new { error = $"Script already exists: {path}" };

            File.WriteAllText(path, content, SkillsCommon.Utf8NoBom);
            AssetDatabase.ImportAsset(path);
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset != null) WorkflowManager.SnapshotCreatedAsset(asset);
            return new { success = true, path, className, nextAction = "Unity may start compiling. After compilation finishes, call script_get_compile_feedback if needed." };
        }

        private static bool IsValidClassName(string className)
        {
            if (string.IsNullOrWhiteSpace(className)) return false;
            if (className.Contains("/") || className.Contains("\\") || className.Contains("..")) return false;
            if (!(char.IsLetter(className[0]) || className[0] == '_')) return false;
            return className.All(c => char.IsLetterOrDigit(c) || c == '_');
        }

        private static string BuildTweenScript(string className, string namespaceName, RuntimeTweenSpec spec, float duration, string ease, int loops, bool autoPlay, bool useSetLink)
        {
            var body = BuildScriptBody(className, spec, duration, ease, loops, autoPlay, useSetLink, "Tween");
            return WrapGeneratedNamespace(namespaceName, body);
        }

        private static string BuildLifetimeScript(string className, string namespaceName, RuntimeTweenSpec spec, float duration, string ease, int loops, bool autoPlay, bool useSetLink)
        {
            var body = BuildScriptBody(className, spec, duration, ease, loops, autoPlay, useSetLink, "Tween", includeRestart: true);
            return WrapGeneratedNamespace(namespaceName, body);
        }

        private static string BuildSequenceScript(string className, string namespaceName, string targetKind, List<(string op, RuntimeTweenSpec spec, float duration)> specs, float duration, string ease, int loops, bool autoPlay, bool useSetLink)
        {
            var usings = new SortedSet<string> { "using DG.Tweening;", "using UnityEngine;" };
            foreach (var item in specs.Where(i => i.spec != null && !string.IsNullOrEmpty(i.spec.extraUsing))) usings.Add(item.spec.extraUsing);
            var fieldSpecs = specs.Where(i => i.spec != null && !i.spec.genericDOTweenTo).Select(i => i.spec).GroupBy(s => s.fieldName).Select(g => g.First()).ToList();
            var valueSpecs = specs.Where(i => i.spec != null).Select(i => i.spec).GroupBy(s => s.valueField).Select(g => g.First()).ToList();

            // 必须先构造 Play() 方法体，因为它决定 duration 字段到底声不声明。
            // 若每一步都把自己的时长烘成字面量（methodCall.Replace("duration", …)），
            // [SerializeField] float duration 就没人引用，每个生成的序列脚本都报 CS0414。
            // 现在时长等于顶层值的步骤改为读该字段：常见情形下 Inspector 上的旋钮仍然可用，
            // 没人会用到时该字段才消失。
            var playLines = BuildSequenceSteps(specs, duration, out bool usesDurationField);

            var sb = new StringBuilder();
            sb.AppendLine(string.Join("\n", usings));
            sb.AppendLine();
            sb.AppendLine($"public class {className} : MonoBehaviour");
            sb.AppendLine("{");
            foreach (var spec in fieldSpecs) sb.AppendLine($"    [SerializeField] private {spec.fieldType} {spec.fieldName};");
            foreach (var spec in valueSpecs) sb.AppendLine($"    [SerializeField] private {spec.valueType} {spec.valueField} = {spec.defaultValue};");
            if (usesDurationField) sb.AppendLine($"    [SerializeField] private float duration = {FloatLiteral(duration)};");
            sb.AppendLine($"    [SerializeField] private Ease ease = Ease.{SanitizeEnumName(ease, "OutQuad")};");
            sb.AppendLine($"    [SerializeField] private int loops = {loops};");
            sb.AppendLine($"    [SerializeField] private bool autoPlay = {BoolLiteral(autoPlay)};");
            sb.AppendLine("    private Sequence sequence;");
            if (specs.Any(i => i.spec != null && i.spec.genericDOTweenTo)) sb.AppendLine("    private float currentValue;");
            sb.AppendLine();
            sb.AppendLine("    private void Awake()");
            sb.AppendLine("    {");
            foreach (var spec in fieldSpecs) sb.AppendLine($"        if ({spec.fieldName} == null) {spec.fieldInitializer}");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    private void OnEnable()");
            sb.AppendLine("    {");
            sb.AppendLine("        if (autoPlay) Play();");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    public void Play()");
            sb.AppendLine("    {");
            sb.AppendLine("        KillTween();");
            sb.AppendLine("        sequence = DOTween.Sequence();");
            foreach (var line in playLines) sb.AppendLine(line);
            sb.AppendLine("        sequence.SetEase(ease).SetLoops(loops);");
            if (useSetLink) sb.AppendLine("        sequence.SetLink(gameObject);");
            sb.AppendLine("    }");
            AppendKillMethods(sb, "sequence");
            sb.AppendLine("}");
            return WrapGeneratedNamespace(namespaceName, sb.ToString());
        }

        /// <summary>
        /// 生成 Sequence 的 Play() 方法体，并回报其中是否有任何一行读了 <c>duration</c> 字段。
        /// 时长与顶层值一致的步骤按字段生成，只有确实不同的步骤才烘成字面量。
        /// </summary>
        internal static List<string> BuildSequenceSteps(
            List<(string op, RuntimeTweenSpec spec, float duration)> specs, float duration, out bool usesDurationField)
        {
            var lines = new List<string>();
            usesDurationField = false;
            if (specs == null) return lines;

            foreach (var item in specs)
            {
                bool matchesTopLevel = Mathf.Approximately(item.duration, duration);
                if (item.op == "AppendInterval")
                {
                    lines.Add($"        sequence.AppendInterval({(matchesTopLevel ? "duration" : FloatLiteral(item.duration))});");
                    usesDurationField |= matchesTopLevel;
                    continue;
                }

                var call = item.spec.methodCall;
                if (matchesTopLevel && call.Contains("duration"))
                    usesDurationField = true;
                else
                    call = call.Replace("duration", FloatLiteral(item.duration));
                lines.Add($"        sequence.{item.op}({call});");
            }
            return lines;
        }

        private static string BuildScriptBody(string className, RuntimeTweenSpec spec, float duration, string ease, int loops, bool autoPlay, bool useSetLink, string tweenType, bool includeRestart = false)
        {
            var usings = new SortedSet<string> { "using DG.Tweening;", "using UnityEngine;" };
            if (!string.IsNullOrEmpty(spec.extraUsing)) usings.Add(spec.extraUsing);
            var sb = new StringBuilder();
            sb.AppendLine(string.Join("\n", usings));
            sb.AppendLine();
            sb.AppendLine($"public class {className} : MonoBehaviour");
            sb.AppendLine("{");
            if (!spec.genericDOTweenTo) sb.AppendLine($"    [SerializeField] private {spec.fieldType} {spec.fieldName};");
            sb.AppendLine($"    [SerializeField] private {spec.valueType} {spec.valueField} = {spec.defaultValue};");
            sb.AppendLine($"    [SerializeField] private float duration = {FloatLiteral(duration)};");
            sb.AppendLine($"    [SerializeField] private Ease ease = Ease.{SanitizeEnumName(ease, "OutQuad")};");
            sb.AppendLine($"    [SerializeField] private int loops = {loops};");
            sb.AppendLine($"    [SerializeField] private bool autoPlay = {BoolLiteral(autoPlay)};");
            sb.AppendLine($"    private {tweenType} tween;");
            if (spec.genericDOTweenTo) sb.AppendLine("    private float currentValue;");
            sb.AppendLine();
            if (!spec.genericDOTweenTo)
            {
                sb.AppendLine("    private void Awake()");
                sb.AppendLine("    {");
                sb.AppendLine($"        if ({spec.fieldName} == null) {spec.fieldInitializer}");
                sb.AppendLine("    }");
                sb.AppendLine();
            }
            sb.AppendLine("    private void OnEnable()");
            sb.AppendLine("    {");
            sb.AppendLine("        if (autoPlay) Play();");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    public void Play()");
            sb.AppendLine("    {");
            sb.AppendLine("        KillTween();");
            sb.AppendLine($"        tween = {spec.methodCall}.SetEase(ease).SetLoops(loops);");
            if (useSetLink) sb.AppendLine("        tween.SetLink(gameObject);");
            sb.AppendLine("    }");
            if (includeRestart)
            {
                sb.AppendLine();
                sb.AppendLine("    public void RestartTween()");
                sb.AppendLine("    {");
                sb.AppendLine("        if (tween != null && tween.IsActive()) tween.Restart();");
                sb.AppendLine("        else Play();");
                sb.AppendLine("    }");
            }
            AppendKillMethods(sb, "tween");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static void AppendKillMethods(StringBuilder sb, string fieldName)
        {
            sb.AppendLine();
            sb.AppendLine("    public void KillTween()");
            sb.AppendLine("    {");
            sb.AppendLine($"        if ({fieldName} != null && {fieldName}.IsActive()) {fieldName}.Kill();");
            sb.AppendLine($"        {fieldName} = null;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    private void OnDisable()");
            sb.AppendLine("    {");
            sb.AppendLine("        KillTween();");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    private void OnDestroy()");
            sb.AppendLine("    {");
            sb.AppendLine("        KillTween();");
            sb.AppendLine("    }");
        }

        private static string WrapGeneratedNamespace(string namespaceName, string content)
        {
            if (string.IsNullOrWhiteSpace(namespaceName)) return content;
            var indented = string.Join("\n", content.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Select(line => string.IsNullOrEmpty(line) ? string.Empty : "    " + line));
            return $"namespace {namespaceName}\n{{\n{indented}\n}}\n";
        }

        private static string FloatLiteral(float value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "f";
        private static string BoolLiteral(bool value) => value ? "true" : "false";
        private static string SanitizeEnumName(string value, string fallback) => string.IsNullOrWhiteSpace(value) || !value.All(c => char.IsLetterOrDigit(c) || c == '_') ? fallback : value.Trim();

        private static object AddAnimationCore(
            GameObject go,
            string animationType,
            string endValueV3, float? endValueFloat, string endValueColor,
            string endValueV2, string endValueString, string endValueRect,
            float duration, string ease, int loops, string loopType,
            float delay, bool isRelative, bool isFrom, bool autoPlay, bool autoKill,
            string id)
        {
            var type = DOTweenReflectionHelper.FindTypeInAssemblies(DOTweenReflectionHelper.DOTweenAnimationTypeName);
            if (type == null) return NoDOTweenPro();

            WorkflowManager.SnapshotObject(go);
            var comp = Undo.AddComponent(go, type);
            if (comp == null) return new { error = "Failed to add DOTweenAnimation" };

            if (!DOTweenReflectionHelper.TrySetAnimationType(comp, animationType))
            {
                Undo.DestroyObjectImmediate(comp);
                return new { error = $"Unknown animationType '{animationType}' — check spelling (Move/LocalMove/Rotate/Scale/Fade/Color/...)" };
            }

            var (ok, evErr) = DOTweenReflectionHelper.ApplyEndValue(
                comp, animationType, endValueV3, endValueFloat, endValueColor, endValueV2, endValueString, endValueRect);
            if (!ok)
            {
                Undo.DestroyObjectImmediate(comp);
                return new { error = evErr };
            }

            DOTweenReflectionHelper.SetFieldByCandidates(comp, DOTweenReflectionHelper.DurationFieldCandidates, duration);
            DOTweenReflectionHelper.SetFieldByCandidates(comp, DOTweenReflectionHelper.DelayFieldCandidates, delay);
            DOTweenReflectionHelper.SetFieldByCandidates(comp, DOTweenReflectionHelper.LoopsFieldCandidates, loops);
            // ease / loopType 的写入结果必须检查：写飞了会让组件停在默认值上，
            // 技能却报 success:true 且不回显任何请求值。拼写错误已由 ValidateAnimationSpec
            // 在添加组件之前拦下，所以走到这里说明是当前版本上字段缺失或类型不同。
            if (!string.IsNullOrEmpty(loopType) && !DOTweenReflectionHelper.TrySetLoopType(comp, loopType))
            {
                Undo.DestroyObjectImmediate(comp);
                return SkillParamUtil.InvalidValueError(loopType, "loopType",
                    DOTweenReflectionHelper.EnumNamesForField(type, DOTweenReflectionHelper.LoopTypeFieldCandidates));
            }
            if (!string.IsNullOrEmpty(ease) && !DOTweenReflectionHelper.TrySetEase(comp, ease))
            {
                Undo.DestroyObjectImmediate(comp);
                return SkillParamUtil.InvalidValueError(ease, "ease",
                    DOTweenReflectionHelper.EnumNamesForField(type, DOTweenReflectionHelper.EaseFieldCandidates));
            }
            DOTweenReflectionHelper.SetFieldByCandidates(comp, DOTweenReflectionHelper.IsRelativeFieldCandidates, isRelative);
            DOTweenReflectionHelper.SetFieldByCandidates(comp, DOTweenReflectionHelper.IsFromFieldCandidates, isFrom);
            DOTweenReflectionHelper.SetFieldByCandidates(comp, DOTweenReflectionHelper.AutoPlayFieldCandidates, autoPlay);
            DOTweenReflectionHelper.SetFieldByCandidates(comp, DOTweenReflectionHelper.AutoKillFieldCandidates, autoKill);
            if (!string.IsNullOrEmpty(id))
                DOTweenReflectionHelper.SetFieldByCandidates(comp, DOTweenReflectionHelper.IdFieldCandidates, id);

            WorkflowManager.SnapshotCreatedComponent(comp);
            EditorUtility.SetDirty(comp);

            var indexOnGo = go.GetComponents(type).ToList().IndexOf(comp);
            return new
            {
                success = true,
                component = type.Name,
                gameObject = go.name,
                animationIndex = indexOnGo
            };
        }

        private static (Component comp, object error) ResolveAnimationComponent(
            string target, int targetInstanceId, string targetPath, int animationIndex)
        {
            if (!DOTweenReflectionHelper.IsDOTweenProInstalled) return (null, NoDOTweenPro());

            var (go, err) = GameObjectFinder.FindOrError(name: target, instanceId: targetInstanceId, path: targetPath);
            if (err != null) return (null, err);

            var type = DOTweenReflectionHelper.FindTypeInAssemblies(DOTweenReflectionHelper.DOTweenAnimationTypeName);
            if (type == null) return (null, NoDOTweenPro());

            var comps = go.GetComponents(type);
            if (comps == null || comps.Length == 0)
                return (null, new { error = $"'{go.name}' has no DOTweenAnimation component. Add one with dotween_pro_add_animation first." });
            if (animationIndex < 0 || animationIndex >= comps.Length)
                return (null, new { error = $"animationIndex {animationIndex} out of range (found {comps.Length} DOTweenAnimation components)" });

            return (comps[animationIndex], null);
        }

        private static List<string> ParseTargetList(string targetsJson)
        {
            if (string.IsNullOrEmpty(targetsJson)) return null;
            try { return JsonConvert.DeserializeObject<List<string>>(targetsJson); }
            catch { return null; }
        }

        private static bool IsSuccess(object result)
        {
            if (result == null) return false;
            var p = result.GetType().GetProperty("success");
            return p != null && p.GetValue(result) is bool b && b;
        }
    }
}

// Producer:Betsy
