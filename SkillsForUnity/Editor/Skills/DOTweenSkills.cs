using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// DOTween Pro DOTweenAnimation editor-time configuration skills.
    /// All DOTween / DOTweenAnimation access is via reflection — the assembly
    /// compiles even without DOTween installed. DOTWEEN / DOTWEEN_PRO scripting
    /// defines are maintained automatically by DOTweenPresenceDetector; they act
    /// as fast-path signals (detector-less short-circuit), not compile gates.
    /// </summary>
    public static class DOTweenSkills
    {
        private static object NoDOTween() => DOTweenReflectionHelper.NoDOTween();
        private static object NoDOTweenPro() => DOTweenReflectionHelper.NoDOTweenPro();

        // ==================================================================================
        // Free runtime / project diagnostics
        // ==================================================================================

        [UnitySkill("dotween_get_status",
            "Get DOTween installation status, Pro availability, DOTweenSettings presence, and visible module count. Works with DOTween Free or Pro.",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Query,
            Tags = new[] { "dotween", "free", "status", "installed", "modules" },
            Outputs = new[] { "isDOTweenInstalled", "isDOTweenProInstalled", "settingsFound", "moduleCount" })]
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
            Outputs = new[] { "success", "path", "fields" })]
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
            Outputs = new[] { "count", "paths", "resourcesLoadPath" })]
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
            Outputs = new[] { "success", "isValid", "issues", "warnings" })]
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
            Outputs = new[] { "count", "types" })]
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
            Outputs = new[] { "count", "methods" })]
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
            "ease: one of 38 Ease enum names (OutQuad default). loopType: Yoyo/Restart/Incremental.",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Create,
            Tags = new[] { "dotween", "animation", "tween", "ui", "pro", "add" },
            Outputs = new[] { "success", "component", "animationIndex" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true, MutatesScene = true, RiskLevel = "low")]
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
            "Each target i gets delay = baseDelay + i * staggerDelay.",
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
        // B. Tuning — 3 dedicated + 2 generic
        // ==================================================================================

        [UnitySkill("dotween_pro_set_duration",
            "Set the duration (seconds) of an existing DOTweenAnimation. " +
            "Use animationIndex when a GameObject has multiple DOTweenAnimation components (default 0).",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Modify,
            Tags = new[] { "dotween", "duration", "tweak", "animation", "pro" },
            Outputs = new[] { "success" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true, MutatesScene = true, RiskLevel = "low")]
        public static object DOTweenProSetDuration(
            string target = null, int targetInstanceId = 0, string targetPath = null,
            int animationIndex = 0, float duration = 1f)
        {
            var (comp, err) = ResolveAnimationComponent(target, targetInstanceId, targetPath, animationIndex);
            if (err != null) return err;

            Undo.RecordObject(comp, "DOTween set duration");
            if (!DOTweenReflectionHelper.SetFieldByCandidates(comp, DOTweenReflectionHelper.DurationFieldCandidates, duration))
                return new { error = "Failed to set duration on DOTweenAnimation" };
            WorkflowManager.SnapshotObject(comp);
            EditorUtility.SetDirty(comp);
            return new { success = true };
        }

        [UnitySkill("dotween_pro_set_ease",
            "Set the ease of an existing DOTweenAnimation (Ease enum name, or easeCurveJson for a custom AnimationCurve).",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Modify,
            Tags = new[] { "dotween", "ease", "curve", "animation", "pro" },
            Outputs = new[] { "success" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true, MutatesScene = true, RiskLevel = "low")]
        public static object DOTweenProSetEase(
            string target = null, int targetInstanceId = 0, string targetPath = null,
            int animationIndex = 0, string ease = "OutQuad", string easeCurveJson = null)
        {
            var (comp, err) = ResolveAnimationComponent(target, targetInstanceId, targetPath, animationIndex);
            if (err != null) return err;

            Undo.RecordObject(comp, "DOTween set ease");
            if (!DOTweenReflectionHelper.TrySetEase(comp, ease, easeCurveJson))
                return new { error = $"Failed to set ease '{ease}'. Check the Ease enum name (e.g. OutQuad/InOutElastic) or easeCurveJson format." };
            WorkflowManager.SnapshotObject(comp);
            EditorUtility.SetDirty(comp);
            return new { success = true };
        }

        [UnitySkill("dotween_pro_set_loops",
            "Set loops count and (optional) loopType for an existing DOTweenAnimation. loops=-1 means infinite.",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Modify,
            Tags = new[] { "dotween", "loops", "loop", "animation", "pro" },
            Outputs = new[] { "success" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true, MutatesScene = true, RiskLevel = "low")]
        public static object DOTweenProSetLoops(
            string target = null, int targetInstanceId = 0, string targetPath = null,
            int animationIndex = 0, int loops = 1, string loopType = null)
        {
            var (comp, err) = ResolveAnimationComponent(target, targetInstanceId, targetPath, animationIndex);
            if (err != null) return err;

            Undo.RecordObject(comp, "DOTween set loops");
            if (!DOTweenReflectionHelper.SetFieldByCandidates(comp, DOTweenReflectionHelper.LoopsFieldCandidates, loops))
                return new { error = "Failed to set loops field" };
            if (!string.IsNullOrEmpty(loopType) && !DOTweenReflectionHelper.TrySetLoopType(comp, loopType))
                return new { error = $"Failed to set loopType '{loopType}' (valid: Restart/Yoyo/Incremental)" };
            WorkflowManager.SnapshotObject(comp);
            EditorUtility.SetDirty(comp);
            return new { success = true };
        }

        [UnitySkill("dotween_pro_set_animation_field",
            "Generic field setter for a DOTweenAnimation component. " +
            "Use the dedicated skills (dotween_pro_set_duration / _set_ease / _set_loops) for those common fields — this skill rejects duration/ease/easeType/easeCurve/loops/loopType. " +
            "Valid targets: delay / isRelative / isFrom / autoPlay / autoKill / id / endValueV3 / endValueFloat / endValueColor / optionalFloat0 / etc. " +
            "fieldValue is a string (vec/color parsed automatically).",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Modify,
            Tags = new[] { "dotween", "field", "reflection", "animation", "pro" },
            Outputs = new[] { "success" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true, MutatesScene = true, RiskLevel = "low")]
        public static object DOTweenProSetAnimationField(
            string target = null, int targetInstanceId = 0, string targetPath = null,
            int animationIndex = 0, string fieldName = null, string fieldValue = null)
        {
            if (string.IsNullOrEmpty(fieldName))
                return new { error = "fieldName is required" };
            if (DOTweenReflectionHelper.ReservedByDedicatedSkills.Contains(fieldName))
                return new
                {
                    error = $"Field '{fieldName}' must be modified via the dedicated skill " +
                            "(dotween_pro_set_duration / dotween_pro_set_ease / dotween_pro_set_loops). " +
                            "This keeps intent explicit and avoids accidental ease/loop type mismatches."
                };

            var (comp, err) = ResolveAnimationComponent(target, targetInstanceId, targetPath, animationIndex);
            if (err != null) return err;

            Undo.RecordObject(comp, $"DOTween set {fieldName}");
            if (!DOTweenReflectionHelper.SetFieldByName(comp, fieldName, fieldValue))
                return new { error = $"Failed to set '{fieldName}' on DOTweenAnimation. Run dotween_pro_get_animation to inspect available fields." };
            WorkflowManager.SnapshotObject(comp);
            EditorUtility.SetDirty(comp);
            return new { success = true };
        }

        [UnitySkill("dotween_pro_get_animation",
            "Read all serialized fields of a single DOTweenAnimation component (use animationIndex to pick one).",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Query,
            Tags = new[] { "dotween", "inspect", "animation", "pro" },
            Outputs = new[] { "fields" },
            RequiresInput = new[] { "gameObject" },
            ReadOnly = true)]
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
            "List all DOTweenAnimation components under a target (set recursive=true for the whole hierarchy).",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Query,
            Tags = new[] { "dotween", "list", "animation", "pro" },
            Outputs = new[] { "animations" },
            ReadOnly = true)]
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
#if UNITY_6000_0_OR_NEWER
                comps = UnityEngine.Object.FindObjectsByType(type, FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .OfType<Component>().ToArray();
#else
                comps = UnityEngine.Object.FindObjectsOfType(type).OfType<Component>().ToArray();
#endif
            }

            var list = new List<object>();
            var grouped = comps.GroupBy(c => c.gameObject);
            foreach (var g in grouped)
            {
                int idx = 0;
                foreach (var c in g)
                {
                    list.Add(new
                    {
                        gameObject = g.Key.name,
                        instanceId = g.Key.GetInstanceID(),
                        animationIndex = idx++,
                        animationType = DOTweenReflectionHelper.GetFieldByCandidates(c, DOTweenReflectionHelper.AnimationTypeFieldCandidates)?.ToString(),
                        duration = DOTweenReflectionHelper.GetFieldByCandidates(c, DOTweenReflectionHelper.DurationFieldCandidates),
                        ease = DOTweenReflectionHelper.GetFieldByCandidates(c, DOTweenReflectionHelper.EaseFieldCandidates)?.ToString(),
                        loops = DOTweenReflectionHelper.GetFieldByCandidates(c, DOTweenReflectionHelper.LoopsFieldCandidates),
                        id = DOTweenReflectionHelper.GetFieldByCandidates(c, DOTweenReflectionHelper.IdFieldCandidates)?.ToString()
                    });
                }
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

            WorkflowManager.SnapshotObject(comp.gameObject);
            Undo.DestroyObjectImmediate(comp);
            return new { success = true };
        }

        // ==================================================================================
        // D. Settings
        // ==================================================================================

        [UnitySkill("dotween_settings_configure",
            "Configure Resources/DOTweenSettings.asset (defaultEaseType/defaultAutoKill/defaultLoopType/safeMode/logBehaviour/tweenersCapacity/sequencesCapacity). " +
            "Any parameter left null is not modified.",
            Category = SkillCategory.DOTween, Operation = SkillOperation.Modify,
            Tags = new[] { "dotween", "settings", "configure", "capacity", "safemode" },
            Outputs = new[] { "success", "modified" },
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

            var modified = new List<string>();
            if (!string.IsNullOrEmpty(defaultEaseType))
            {
                var f = DOTweenReflectionHelper.ResolveField(settings.GetType(), "defaultEaseType");
                if (f != null && f.FieldType.IsEnum)
                {
                    try { f.SetValue(settings, Enum.Parse(f.FieldType, defaultEaseType, ignoreCase: true)); modified.Add("defaultEaseType"); }
                    catch { return new { error = $"Invalid defaultEaseType '{defaultEaseType}'" }; }
                }
            }
            if (defaultAutoKill.HasValue && DOTweenReflectionHelper.SetFieldByName(settings, "defaultAutoKill", defaultAutoKill.Value))
                modified.Add("defaultAutoKill");
            if (!string.IsNullOrEmpty(defaultLoopType))
            {
                var f = DOTweenReflectionHelper.ResolveField(settings.GetType(), "defaultLoopType");
                if (f != null && f.FieldType.IsEnum)
                {
                    try { f.SetValue(settings, Enum.Parse(f.FieldType, defaultLoopType, ignoreCase: true)); modified.Add("defaultLoopType"); }
                    catch { return new { error = $"Invalid defaultLoopType '{defaultLoopType}'" }; }
                }
            }
            if (safeMode.HasValue && DOTweenReflectionHelper.SetFieldByName(settings, "useSafeMode", safeMode.Value))
                modified.Add("useSafeMode");
            if (!string.IsNullOrEmpty(logBehaviour))
            {
                var f = DOTweenReflectionHelper.ResolveField(settings.GetType(), "logBehaviour");
                if (f != null && f.FieldType.IsEnum)
                {
                    try { f.SetValue(settings, Enum.Parse(f.FieldType, logBehaviour, ignoreCase: true)); modified.Add("logBehaviour"); }
                    catch { return new { error = $"Invalid logBehaviour '{logBehaviour}'" }; }
                }
            }
            if (tweenersCapacity.HasValue && DOTweenReflectionHelper.SetFieldByName(settings, "defaultTweensCapacity", tweenersCapacity.Value))
                modified.Add("defaultTweensCapacity");
            if (sequencesCapacity.HasValue && DOTweenReflectionHelper.SetFieldByName(settings, "defaultSequencesCapacity", sequencesCapacity.Value))
                modified.Add("defaultSequencesCapacity");

            if (modified.Count == 0) return new { success = true, modified = new string[0], note = "No fields changed" };

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            return new { success = true, modified };
        }

        // ==================================================================================
        // Private core
        // ==================================================================================

        private class RuntimeTweenSpec
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

        private static RuntimeTweenSpec ResolveRuntimeTweenSpec(string targetKind, string tweenKind)
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
            valueType = valueType, valueField = valueField, defaultValue = defaultValue, methodCall = call, extraUsing = "using UnityEngine.UI;"
        };

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
            var sb = new StringBuilder();
            sb.AppendLine(string.Join("\n", usings));
            sb.AppendLine();
            sb.AppendLine($"public class {className} : MonoBehaviour");
            sb.AppendLine("{");
            foreach (var spec in fieldSpecs) sb.AppendLine($"    [SerializeField] private {spec.fieldType} {spec.fieldName};");
            foreach (var spec in valueSpecs) sb.AppendLine($"    [SerializeField] private {spec.valueType} {spec.valueField} = {spec.defaultValue};");
            sb.AppendLine($"    [SerializeField] private float duration = {FloatLiteral(duration)};");
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
            foreach (var item in specs)
            {
                if (item.op == "AppendInterval") sb.AppendLine($"        sequence.AppendInterval({FloatLiteral(item.duration)});");
                else sb.AppendLine($"        sequence.{item.op}({item.spec.methodCall.Replace("duration", FloatLiteral(item.duration))});");
            }
            sb.AppendLine("        sequence.SetEase(ease).SetLoops(loops);");
            if (useSetLink) sb.AppendLine("        sequence.SetLink(gameObject);");
            sb.AppendLine("    }");
            AppendKillMethods(sb, "sequence");
            sb.AppendLine("}");
            return WrapGeneratedNamespace(namespaceName, sb.ToString());
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
            if (!string.IsNullOrEmpty(loopType))
                DOTweenReflectionHelper.TrySetLoopType(comp, loopType);
            if (!string.IsNullOrEmpty(ease))
                DOTweenReflectionHelper.TrySetEase(comp, ease, null);
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
