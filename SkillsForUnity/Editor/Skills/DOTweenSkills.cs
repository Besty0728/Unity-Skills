using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
            for (int i = 0; i < targets.Count; i++)
            {
                var (go, err) = GameObjectFinder.FindOrError(name: targets[i]);
                if (err != null) { added.Add(new { target = targets[i], error = err }); continue; }
                float delay = baseDelay + i * staggerDelay;
                var r = AddAnimationCore(go, animationType, endValueV3, endValueFloat, endValueColor,
                    endValueV2, null, null,
                    duration, ease, loops, loopType, delay, false, isFrom, autoPlay, autoKill, null);
                added.Add(new { target = targets[i], delay, result = r });
            }
            return new { success = true, added };
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
