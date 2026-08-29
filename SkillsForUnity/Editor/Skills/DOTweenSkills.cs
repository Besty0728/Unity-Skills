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
    /// DOTween Pro's DOTweenAnimation editor-time configuration skills.
    /// All access to DOTween / DOTweenAnimation goes through reflection, so the assembly still compiles when DOTween isn't installed.
    /// The two scripting defines DOTWEEN / DOTWEEN_PRO are maintained automatically by DOTweenPresenceDetector;
    /// they're just a fast-path detection signal (skipping detection), not a compile switch.
    /// </summary>
    public static class DOTweenSkills
    {
        private static object NoDOTween() => DOTweenReflectionHelper.NoDOTween();
        private static object NoDOTweenPro() => DOTweenReflectionHelper.NoDOTweenPro();

        // ==================================================================================
        // Free version runtime / project diagnostics
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
        // A. Generation
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

            // Reject once up front rather than per-item: these parameters are shared by every target, so a
            // per-item failure would just echo the same caller error N times, and by the time the caller sees it
            // the earlier targets have already been added.
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

            // A negative baseDelay / staggerDelay must be rejected: DOTween silently clamps a negative delay
            // away, so the staggered cascade effect reported back to the caller (and the per-item echoed delay) wouldn't actually exist.
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
        // B. Tuning — 3 dedicated setters + 2 generic ones
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
            // Validate the parameter domain before resolving the target: an invalid value is unrelated to
            // whether Pro is installed, so checking here lets this rejection be observed even without the
            // Asset Store package. duration is both nullable and listed in RequiresInput: if it were declared as
            // float duration = 1f, omitting it would silently reset the animation to 1s and still report
            // success — the CLR default value is indistinguishable from an explicit 1.
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
            // An easeCurveJson that is sent but fails to parse must be rejected before touching the component:
            // otherwise it would fall into the branch that sets ease by name, install OutQuad, and still report
            // success:true — that's a silently wrong ease, not a rejection.
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

            // The ease name must be validated against the enum this DOTween version actually declares, and this
            // must complete before the first write — so a rejected name leaves no changes on the component.
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
            // Both parameters must be nullable/optional: this setter covers two unrelated halves — if it were
            // declared as int loops = 1, a call sending only loopType would silently turn infinite looping into
            // playing once. Sending neither is a caller error, not a no-op.
            if (!loops.HasValue && string.IsNullOrEmpty(loopType))
                return MissingEitherError("loops", "loopType");
            if (loops.HasValue && InvalidLoopsError(loops.Value) is object invalidLoops) return invalidLoops;

            var (comp, err) = ResolveAnimationComponent(target, targetInstanceId, targetPath, animationIndex);
            if (err != null) return err;

            // Must validate before the first write: writing loops first and then rejecting loopType would leave
            // half the change applied under a single error response.
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
            // Omitting fieldValue must not be allowed through: it would flow through as null down to the
            // reflection layer, clearing the field while the response still reports success.
            // Only an explicit empty string "" means "clear it" — the router keeps the two clearly separate
            // (an explicit empty string binds as-is, a missing key binds the CLR default), so intent isn't guessed here.
            if (fieldValue == null)
                return MissingFieldValueError(fieldName);

            var (comp, err) = ResolveAnimationComponent(target, targetInstanceId, targetPath, animationIndex);
            if (err != null) return err;

            // "field doesn't exist" and "value doesn't convert" must be reported separately: merging them into
            // one bool would make the latter case also blame fieldName, when the real problem is fieldValue.
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
        // C. Helpers — list / copy / remove
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
        // D. Settings
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
        // Internal core
        // ==================================================================================

        /// <summary>
        /// A recipe for a generated script. Declared internal rather than private so assertions can be made
        /// directly against the generation contract (which <c>using</c> a given target kind needs, whether a
        /// given step bakes duration into a literal) — the generator itself refuses to run when DOTween isn't
        /// installed, so the produced text can't be tested end-to-end on a clean project.
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
        // Numeric domain and required-ness guards
        //
        // If the dotween_pro_* numbers were passed through as-is, duration=-1 or loops=-7 would land on the
        // component and report success:true with the caller none the wiser. DOTween's own value domains are
        // narrow enough to declare precisely.
        // ==================================================================================

        /// <summary>duration <= 0 isn't a tween: DOTween treats it as an instant jump and only logs it at play
        /// time, long after the skill has already claimed "the animation is configured".</summary>
        private static object InvalidPositiveError(float value, string paramName) =>
            value > 0f ? null : SkillParamUtil.InvalidValueError(SkillParamUtil.FormatFloatR(value), paramName, new[] { "> 0" });

        /// <summary>A negative delay / stagger step is equivalent to running the cascade backward in time;
        /// DOTween silently clamps it, so the animation reported back to the caller doesn't actually exist.</summary>
        private static object InvalidNonNegativeError(float value, string paramName) =>
            value >= 0f ? null : SkillParamUtil.InvalidValueError(SkillParamUtil.FormatFloatR(value), paramName, new[] { ">= 0" });

        /// <summary>-1 is DOTween's only marker for infinite looping. 0 and anything below -1 are meaningless,
        /// and DOTween neither clamps them nor errors on its own.</summary>
        private static object InvalidLoopsError(int value) =>
            value == -1 || value >= 1
                ? null
                : SkillParamUtil.InvalidValueError(value.ToString(CultureInfo.InvariantCulture), "loops",
                    new[] { "-1 (infinite)", ">= 1" });

        /// <summary>
        /// Expresses "at least one of these two must be given". The payload shape matches <see cref="Validate"/>'s
        /// missing-param response (errorCode is passed through as-is by routing layer 1), because neither half is
        /// individually required — a per-parameter check can't express this constraint.
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
        /// What <c>fieldValue</c> can look like for a given field type, for the <c>validValues</c> in a rejection
        /// response. Content is kept consistent with the string forms <c>DOTweenReflectionHelper.ConvertValue</c>
        /// accepts — listing forms the converter can't parse would be worse than not listing anything.
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
        // Authoritative source for component indices
        // ==================================================================================

        /// <summary>
        /// Assigns each component the index that callers will actually use to address it — namely its position
        /// in <c>gameObject.GetComponents(type)</c>. Every <c>ResolveAnimationComponent</c> call indexes into this array.
        ///
        /// <para>This exists because the whole-scene listing path doesn't originally follow that order: it groups
        /// <c>FindHelper.FindAll</c>'s results (explicitly documented as unordered) by GameObject and hands out a
        /// running counter, so when the same GameObject carries multiple DOTweenAnimation components, the
        /// animationIndex reported by listing doesn't line up with the index the setter uses. This has happened in
        /// a real project: listing reported [Fade 0.3, Scale 0.6, Fade 0.4], while that object's GetComponents order
        /// was [Scale 0.6, Fade 0.3, Fade 0.4] — an agent listed then set, and ended up modifying a different
        /// component than intended, with both calls succeeding and no indication anything was wrong.</para>
        ///
        /// <para>Output follows each GameObject's authoritative order. A type-matched query in theory should never
        /// produce a component absent from the authoritative array, but if it ever does, it's kept with index -1
        /// rather than dropped: a list silently missing a row is a worse failure, and a negative index is rejected
        /// by ResolveAnimationComponent rather than mistakenly pointing at a different component.</para>
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
        // DOTweenSettings write
        // ==================================================================================

        /// <summary>A single parameter that couldn't be applied because the corresponding field doesn't exist.</summary>
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
        /// Applies configuration parameters to the given object and reports the outcome per parameter. Pulled out
        /// of the skill so a stand-in settings object can be used for testing — the bug it fixes only shows up on
        /// a DOTweenSettings missing fields.
        ///
        /// <para>DOTween Pro 1.0.381's settings asset has no <c>defaultTweensCapacity</c> /
        /// <c>defaultSequencesCapacity</c> at all. If both writes only used a bare <c>if (SetFieldByName(...))</c>
        /// guard that does nothing on the false branch, the response would be <c>success:true, modified:[]</c>,
        /// which reads as "accepted, nothing to change" rather than "this DOTween version has nowhere to put your
        /// value." The <c>f != null &amp;&amp; f.FieldType.IsEnum</c> guard on those four enum/bool parameters would
        /// go silent the same way. So every parameter must land in exactly one of modified / unsupported / Error.</para>
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

        /// <summary>Returns false to mean the whole call should be rejected (the field can't express the given value at all).</summary>
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

        /// <summary>Returns false to mean the whole call should be rejected.</summary>
        private static bool ApplyCapacitySetting(object settings, string fieldName, string parameterName,
            int? value, SettingsWriteResult result)
        {
            if (!value.HasValue) return true;

            // dotween_settings_validate already reports capacity <= 0 as an issue; actually writing it in would
            // make this plugin's next read flag its own just-made change as invalid.
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
        /// The numeric and enum contract shared by the add / batch / stagger skills, checked before anything is
        /// added to the scene. Enum names are compared against what the current DOTween version actually
        /// declares, so the validValues in a rejection response are the real word list rather than a hardcoded
        /// one that can drift with the asset package version.
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
        /// The extra <c>using</c> a generated script needs for its target type — only emitted when that type actually lives in that namespace.
        ///
        /// <para>A few "looks like UI" targets can't share one hardcoded <c>using UnityEngine.UI;</c>:
        /// <c>CanvasGroup</c> belongs to <c>UnityEngine</c> (UIModule, always present), while
        /// <c>Graphic</c> / <c>Image</c> belong to <c>UnityEngine.UI</c>, shipped with com.unity.ugui.
        /// In a project without that package installed, a generated CanvasGroup file would fail to compile with
        /// CS0246 over a namespace it never actually references. Generation is pure string concatenation, so the
        /// only thing that can decide this is the namespace the target type itself lives in.</para>
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
                    // Transform / RectTransform / CanvasGroup / Generic are all in the UnityEngine namespace.
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

            // Must build the Play() method body first, since it determines whether the duration field gets
            // declared at all. If every step baked its own duration into a literal (methodCall.Replace("duration", …)),
            // [SerializeField] float duration would go unreferenced and every generated sequence script would
            // report CS0414. Steps whose duration equals the top-level value now read that field instead: the
            // Inspector knob remains usable in the common case, and the field only disappears when nothing uses it.
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
        /// Generates the Play() method body for a Sequence, and reports back whether any line reads the
        /// <c>duration</c> field. Steps whose duration matches the top-level value are generated referencing the
        /// field; only steps that actually differ get baked into a literal.
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
            // The result of writing ease / loopType must be checked: a failed write leaves the component on its
            // default value while the skill still reports success:true without echoing any requested value.
            // Typos are already caught by ValidateAnimationSpec before the component was added, so reaching here
            // means the field is missing or a different type on this version.
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
