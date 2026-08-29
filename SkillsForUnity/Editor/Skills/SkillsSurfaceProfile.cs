using System;
using System.Collections.Generic;
using UnityEditor;

namespace UnitySkills
{
    /// <summary>Which slice of the skill surface the user has chosen to expose.</summary>
    public enum SurfaceProfileKind
    {
        /// <summary>Every registered skill is offered; the default profile.</summary>
        Full = 0,
        /// <summary>
        /// Hides the write skills of the modules a user is most likely to want to do by hand
        /// (GameObject / Component / Material / Scene, plus Sample's primitive creation — which
        /// is just relabeled GameObject authoring), so the AI walks through editor steps instead
        /// of authoring on the user's behalf. Read-only skills in these modules remain available.
        /// </summary>
        Guide,
        /// <summary>
        /// Hides scene-authoring write operations across every visual/creative module.
        /// Non-authoring work (assets, project, tests, diagnostics, scripts, …) is unaffected.
        /// </summary>
        NoSceneAuthoring
    }

    /// <summary>
    /// The user's presentation policy for the skill surface: which skills get offered at all.
    /// Persisted as a wire string in EditorPrefs, reported on <c>/health</c> as
    /// <c>surfaceProfile</c>, and enforced by <see cref="SkillRouter"/> at both discovery and execution.
    ///
    /// This is not a permission mode. <see cref="SkillsModeManager"/> answers "can this skill
    /// run"; this class answers "is this skill even on the menu". So exclusion takes priority
    /// over Bypass mode and the allowlist — those two grant permissions the user has already
    /// delegated, whereas a profile is the user saying "I don't want these operations attempted
    /// at all". The only way to lift it is for the user to switch back to
    /// <see cref="SurfaceProfileKind.Full"/> in the UnitySkills panel.
    ///
    /// Exclusion decisions are derived from skill metadata (category + ReadOnly) and never rely
    /// on a hardcoded skill name list, so a newly added skill is automatically covered as long as
    /// it carries a category. Two kinds of entry points have write operations determined by their
    /// payload rather than their own metadata (<c>batch_execute</c>, the workflow undo/redo
    /// skills); they enforce the same policy against themselves at execute time, and their
    /// previews declare it ahead of time via <see cref="CarriedWritePreviewGate"/> — see the
    /// "carried writes" section below.
    /// </summary>
    public static class SkillsSurfaceProfile
    {
        public const string WireFull = "full";
        public const string WireGuide = "guide";
        public const string WireNoSceneAuthoring = "noSceneAuthoring";

        private const string PrefKeyProfile = "UnitySkills_SurfaceProfile";
        // The pre-2.7 boolean guide toggle, read only for the one-way migration (see Load).
        private const string PrefKeyLegacyGuideMode = "UnitySkills_GuideMode";

        /// <summary>
        /// Fires after the profile has changed and been persisted. Subscribers must assume the
        /// visible skill set has already changed: <see cref="SkillRouter"/> drops its cached
        /// output strings, and <see cref="SkillsHttpServer"/> refreshes the /health snapshot.
        /// </summary>
        public static event Action OnChanged;

        // EditorPrefs is only accessible on the main thread, and building the manifest string
        // has to check visibility filtering for every skill, so the resolved result is memoized
        // here. Both the setter and the first read write it, and both happen on the main thread.
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

        /// <summary>The wire value reported on /health and /skills/meta.</summary>
        public static string CurrentWire => ToWire(Current);

        /// <summary>
        /// True when nothing is hidden — this is the hot path. Callers use it to skip
        /// per-skill filtering entirely, so the default profile only costs one comparison per
        /// surface instead of one per skill.
        /// </summary>
        public static bool IsFull => Current == SurfaceProfileKind.Full;

        /// <summary>
        /// The set of categories whose write skills a given profile hides.
        /// <see cref="SurfaceProfileKind.Full"/> returns null.
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
        /// The category-only exclusion check, kept for callers who only have the category and
        /// ReadOnly flag on hand.
        ///
        /// This "under-reports" in some deliberately designed cases: it can't see backdoor lists
        /// like <see cref="_alwaysHiddenSkillNames"/>, nor the NoSceneAuthoring rule that "hides
        /// every write operation declaring <c>MutatesScene</c>, regardless of category". Whenever
        /// a SkillInfo is available, prefer <see cref="IsExcluded(SkillRouter.SkillInfo)"/>
        /// instead — every gate in the router does. Anywhere that needs to count or display "how
        /// many skills are hidden" must also use the SkillInfo overload, or the number won't
        /// match what the router actually blocks.
        /// </summary>
        public static bool IsExcluded(SkillCategory category, bool readOnly)
        {
            return IsExcludedCore(Current, null, category, readOnly, mutatesScene: false);
        }

        /// <summary>
        /// The authoritative exclusion check: every rule the profile enforces, all derived from
        /// a single skill's own metadata. Every discovery surface and both gates call this one.
        /// </summary>
        internal static bool IsExcluded(SkillRouter.SkillInfo skill)
        {
            if (skill == null) return false;
            return IsExcludedCore(Current, skill.Name, skill.Category, skill.ReadOnly, skill.MutatesScene);
        }

        /// <summary>
        /// Rule order, and why each one exists:
        /// <list type="number">
        /// <item><b>Read-only skills are never hidden.</b> A profile withdraws authoring
        /// capability, not observation capability — an AI that can't see the scene can't walk
        /// the user through manual steps either.</item>
        /// <item><b>Names on the backdoor list are always hidden</b> (see <see cref="_alwaysHiddenSkillNames"/>).</item>
        /// <item><b>The category is in this profile's hidden set.</b> This is the
        /// metadata-driven default rule.</item>
        /// <item><b>NoSceneAuthoring additionally hides every write operation declaring
        /// <c>MutatesScene</c>.</b> If a skill claims it changes the scene, it's self-declared
        /// scene authoring no matter which module it lives in — a profile called "no scene
        /// authoring" letting it through would be self-contradictory. This is exactly the rule
        /// that shuts off the Netcode and Behavior modules: they aren't on the category list, but
        /// they do declare that they change the scene.</item>
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
        /// Returns true for backdoor skills in <see cref="_alwaysHiddenSkillNames"/> — i.e. every
        /// skill hidden by name in any non-full profile. This lets a rejection payload explain
        /// the exclusion in terms of "what this skill can reach" instead of category — in these
        /// cases category can't say anything useful.
        /// </summary>
        internal static bool IsAlwaysHiddenSkill(string skillName) =>
            skillName != null && _alwaysHiddenSkillNames.Contains(skillName);

        /// <summary>
        /// The <c>manual-*</c> doc that teaches how to do this category's operation by hand;
        /// returns null if the category has none. Only categories touched by Guide have this
        /// doc, which is exactly why Guide's rejection is "actionable" ("read this, then walk the
        /// user through it") while NoSceneAuthoring's rejection can only point the user back to the panel.
        /// </summary>
        public static string ManualDocFor(SkillCategory category)
        {
            switch (category)
            {
                case SkillCategory.GameObject: return "skills/manual-gameobject/SKILL.md";
                case SkillCategory.Component:  return "skills/manual-component/SKILL.md";
                case SkillCategory.Material:   return "skills/manual-material/SKILL.md";
                case SkillCategory.Scene:      return "skills/manual-scene/SKILL.md";
                // The Sample module's write operations are just spawning primitives and calling
                // transform — the GameObject manual doc teaches exactly these editor steps, so
                // point there rather than leave the agent with nothing to read.
                case SkillCategory.Sample:     return "skills/manual-gameobject/SKILL.md";
                default: return null;
            }
        }

        // ---- Carried writes (payload-carried write operations) --------------------------------
        //
        // Rules 1–4 read a skill's own metadata, which is correct as long as the declaration
        // faithfully describes what this call will write. Two entry points break that premise:
        // batch_execute executes whatever operation a confirmToken was originally minted for
        // (and the previews that mint tokens are ReadOnly, so rule 1 lets them through — as it
        // should, since a preview is exactly how the AI describes the change it's about to
        // explain); the workflow undo/redo skills replay everything a recorded task touched.
        // Both fall under the Workflow category, which no profile hides; under NoSceneAuthoring,
        // rule 4 shuts them off because they declare MutatesScene, but under Guide they used to
        // be a usable shortcut around the four categories Guide withdraws.
        //
        // Simply hiding them under Guide would be a smaller change, but it would be wrong. Guide
        // only withdraws five out of fifty categories, so these two entry points also carry
        // operations Guide allows — batch kinds that only touch assets, undo of tasks that only
        // touched assets — and hiding them would strip the undo safety net from exactly the write
        // operations Guide deliberately leaves available to the AI. It would also stuff the
        // Workflow category into Guide's hidden set, even though it has no manual-* doc behind
        // it, and that doc is exactly what makes Guide's rejection actionable.
        // So instead, the payload is classified at execute time, and the whole call is rejected
        // based on that classification.

        /// <summary>
        /// Whether the current profile withdraws write operations under
        /// <paramref name="category"/> — this asks about one operation, not one skill.
        /// The "under-reports" warning on <see cref="IsExcluded(SkillCategory, bool)"/> doesn't
        /// apply here: a carried operation has no skill name of its own to check against the
        /// backdoor list, and rule 4 has already been answered by the carrying skill's own
        /// <c>MutatesScene</c> declaration.
        /// </summary>
        internal static bool WithdrawsWriteIn(SkillCategory category) =>
            IsExcludedCore(Current, null, category, readOnly: false, mutatesScene: false);

        /// <summary>
        /// The rejection object returned when a call's about-to-be-applied write operation is
        /// determined by its payload rather than its own metadata, and falls into a withdrawn
        /// category. The error code, field names, and abort strategy all match the router's own gates.
        ///
        /// These fields sit at the top level rather than nested under <c>details</c>, because the
        /// router's skill-error pass-through forwards a skill's unrecognized members as-is but
        /// drops any <c>details</c> the skill wrote itself — nesting it there would be a silent loss.
        ///
        /// <paramref name="subject"/> completes the noun phrase "&lt;subject&gt; writes the X
        /// category", and must avoid the keywords <see cref="SkillErrorClassifier"/> uses to
        /// classify errors ("missing", "not found", "invalid", etc.). This is also why
        /// <paramref name="operation"/> is passed as a field rather than interpolated into the
        /// message text: a batch kind named <c>fix_missing_scripts</c> appearing in the text would
        /// get this rejection classified as "missing parameter", leading the agent to a
        /// "supply more parameters and retry" suggested fix.
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
        /// The same verdict, but attached to a preview that still succeeds. A preview is
        /// read-only, and under the Guide profile it's exactly what the AI needs to hand-describe
        /// a change, so it still returns a diff and token as normal. The one thing it must not do
        /// is stay silent: an agent that only got <c>confirmToken: ab12</c> would read an
        /// execute-time rejection as a bug and go look for another module. Callers attach this
        /// block only when the profile actually withdraws this operation, so the payload bytes
        /// under the full profile are unchanged.
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
        /// The carried-write entry points, mapped to the full set of categories their payload
        /// could fall into. Used by dry-run / plan-authorization preview queries — a preview has
        /// no payload to classify, and without this it would answer <c>allowed:true</c> for a
        /// call the execute-time gate is bound to reject.
        ///
        /// The category list here mirrors the two classifiers that actually make the call:
        /// <c>BatchSkills.SurfaceCategoryForKind</c> (maps a kind to GameObject / Component /
        /// Material, with its default branch conservatively falling to GameObject) and
        /// <c>WorkflowSkills.TryClassifySnapshot</c> (a scene object → GameObject, <c>.unity</c>
        /// → Scene, <c>.mat</c> → Material). It's listed here rather than queried directly because
        /// both classifiers need a payload, and "could this call get rejected" is exactly asking
        /// for their union. Widening either classifier requires widening the matching entry here
        /// too; <c>ReviewFixRouterTests</c> pins this down by asserting all six names trigger the
        /// gate under the guide profile. This list can't be derived anywhere: a seventh entry
        /// point that classifies its own payload at execute time would also need to be added
        /// here, or its preview would start lying too.
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
        /// The payload-level warning a carried-write entry point's preview must attach: returns
        /// null when the skill isn't one of these, or the current profile doesn't withdraw any
        /// category its payload could fall into (so it's always null under the full profile, and
        /// that preview's bytes are unchanged).
        ///
        /// Deliberately not a verdict. A preview has no payload on hand — no confirmToken
        /// obtained, no snapshot of some task id read — so it has no way to know whether this
        /// specific call would be rejected; answering <c>allowed:false</c> for every batch kind
        /// and undo the profile allows would be wrong. What it can say, and what a skill-level
        /// preview has never said before, is that this <c>allowed:true</c> was reached without
        /// looking at the payload.
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
        /// Tells the agent what to do instead. Consistent with the router's own branching: with a
        /// manual doc available, the agent finishes the job in a narrator role; without one, only
        /// the user can lift the restriction. Both branches must explicitly cut off the reflex to
        /// "try a different route" — unlike a hidden skill, this entry point is visible, which
        /// naturally invites trying a fresh token again.
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
        /// Parses a wire value case-insensitively. Anything unrecognized always returns false —
        /// the caller then falls back to <see cref="SurfaceProfileKind.Full"/> instead of
        /// guessing, so a typo or a pref written by a newer version can never silently hide skills.
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
        /// Reads the persisted profile, and on first run migrates the pre-2.7 boolean guide
        /// toggle: a user who had guide mode on but never chose a profile lands on
        /// <see cref="SurfaceProfileKind.Guide"/>. The migration is read-only — the old key is
        /// left as-is, and once a profile has been written it no longer has any effect, so
        /// downgrading to an older version of the plugin still finds its own toggle intact.
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
        /// Notifies subscribers one at a time, isolated from each other, so a subscriber that
        /// throws can't block the rest. Wrapping <c>OnChanged?.Invoke()</c> in a single
        /// try/catch would let the first exception abandon the whole call chain, and one of the
        /// subscribers is <see cref="SkillRouter.InvalidateOutputCaches"/> — meaning an earlier-
        /// registered UI handler throwing would leave the manifest cache still holding skills the
        /// user just withdrew, with the only clue being a console warning. Cache invalidation is a
        /// security invariant here, and can't depend on unrelated subscribers behaving.
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
        /// Skills hidden by name under every non-full profile — because nothing in their
        /// metadata can express "why they're a problem".
        ///
        /// <c>editor_execute_menu</c> can execute any Unity menu path. That single parameter
        /// reaches GameObject/Create, Edit/Delete, Component/Add — exactly the full set of write
        /// operations every profile exists to withdraw. Its category (Editor) isn't in any hidden
        /// set, and never should be, because the rest of that module is legitimate tooling; so the
        /// category rule has no way to express "this one is a skeleton key" — only the name can.
        /// Leaving it callable would turn every other exclusion into decoration: an agent blocked
        /// by gameobject_create would just execute "GameObject/Create Empty" and carry on anyway.
        ///
        /// Deliberately kept as a closed, minimal list. It's not meant to hide skills the category
        /// rules can already cover — every extra name here is extra maintenance burden that
        /// metadata alone can't carry.
        /// </summary>
        private static readonly HashSet<string> _alwaysHiddenSkillNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "editor_execute_menu",
        };

        // ---- Hidden category sets ---------------------------------------------------------
        //
        // Categories only. Whether a given skill counts as a write operation is decided by its
        // own ReadOnly metadata, so these sets need no changes when a new skill is added.

        private static readonly HashSet<SkillCategory> _guideHidden = new HashSet<SkillCategory>
        {
            SkillCategory.GameObject,
            SkillCategory.Component,
            SkillCategory.Material,
            SkillCategory.Scene,
            // Sample is included based on what its skills actually do, not the impression its
            // name gives: create_cube / delete_object / set_object_position are just relabeled
            // GameObject authoring. Missing it would leave a usable shortcut through the guide
            // boundary — an agent blocked by gameobject_create could still spawn a cube with create_cube.
            SkillCategory.Sample,
        };

        // Every module whose write operations produce something visible in the Scene/Game view is
        // listed here. Deliberately wider than Guide's four: the user this profile targets wants
        // the AI to handle assets, code, and diagnostics, while keeping the scene itself in their own hands.
        //
        // It's no longer the complete story: IsExcludedCore's rule 4 also hides any write
        // operation declaring MutatesScene, regardless of category. This set is still kept
        // because it catches scene-authoring write operations whose metadata happens not to set
        // that flag; and that flag catches modules nobody thought to list here. Neither alone is enough.
        private static readonly HashSet<SkillCategory> _noSceneAuthoringHidden = new HashSet<SkillCategory>
        {
            SkillCategory.Cinemachine,
            // Smart is included based on what its write operations actually do, not its name: half
            // of what this module writes is scene placement (snap to grid, align, distribute,
            // ground), acting on whatever is currently selected. The name reads like an analysis
            // aid, which is exactly why it was originally missed — under this profile,
            // smart_snap_to_grid really is moving objects.
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
            // Same reasoning as _guideHidden: Sample's write operations produce scene content.
            SkillCategory.Sample,
        };
    }
}

// Producer:Betsy
