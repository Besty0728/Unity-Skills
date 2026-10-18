#if COM_UNITY_MODULES_PARTICLESYSTEM
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnitySkills
{
    public static partial class ParticleSkills
    {
        /// <summary>Finds the GameObject; a missing locator is MISSING_PARAM, a miss is the finder's own TARGET_NOT_FOUND.</summary>
        internal static (GameObject go, object error) FindTarget(Locator locator)
        {
            if (locator.IsEmpty)
            {
                return (null, Error(CodeMissingParam, "Provide the target: one of entityId, name, path or instanceId.",
                    fixes: new[] { Fix("find_target", "particle_find_all", "List the particle systems in the open scenes and use the entityId it returns.") },
                    parameter: "target"));
            }
            return GameObjectFinder.FindOrError(locator.Name, locator.InstanceId, locator.Path, entityId: locator.EntityId);
        }

        /// <summary>The ParticleSystem (and its renderer) on the target; refuses an object without one.</summary>
        internal static object ResolveSystem(Locator locator, out ParticleBinding binding)
        {
            binding = null;
            var (go, error) = FindTarget(locator);
            if (error != null)
                return error;
            return BindExisting(go, out binding);
        }

        internal static object BindExisting(GameObject go, out ParticleBinding binding)
        {
            binding = null;
            var system = go.GetComponent<ParticleSystem>();
            if (system == null)
            {
                return Error(CodeTargetNotFound, $"'{GameObjectFinder.GetPath(go)}' has no ParticleSystem component.",
                    fixes: new[]
                    {
                        Fix("fix_param", "particle_add_system", "Add a ParticleSystem to this object first."),
                        Fix("find_target", "particle_find_all", "Or target an object that already has one.")
                    },
                    related: new[] { "particle_find_all", "particle_add_system" });
            }
            binding = new ParticleBinding { GameObject = go, System = system, Renderer = go.GetComponent<ParticleSystemRenderer>() };
            return null;
        }

        /// <summary>A renderer is optional on a ParticleSystem (it can be removed); renderer skills need one.</summary>
        internal static object RequireRenderer(ParticleBinding binding)
        {
            if (binding.Renderer != null)
                return null;
            return Error(CodeTargetNotFound, $"'{GameObjectFinder.GetPath(binding.GameObject)}' has a ParticleSystem but no ParticleSystemRenderer.",
                fixes: new[] { Fix("fix_param", "component_add", "Add a ParticleSystemRenderer (componentType 'ParticleSystemRenderer') to draw the particles.") });
        }

        /// <summary>
        /// The preconditions of every particle write: Edit Mode, an object in an open scene (not a prefab asset or
        /// Prefab Mode preview), and a stopped system. A playing or paused system is refused rather than stopped
        /// implicitly, so a write never clears the particles someone is looking at.
        /// </summary>
        internal static void CheckWritable(GameObject go, ParticleSystem system, string path, Problems problems)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                problems.Add(path, "Particle writes are Edit Mode only (Play Mode changes are lost on exit). Exit Play Mode first.");
                return;
            }

            if (go != null && !IsInOpenScene(go))
            {
                problems.Add(path, $"'{go.name}' is not in an open scene (a prefab asset or a Prefab Mode object). Particle skills edit scene objects and prefab instances; edit prefab assets with the prefab skills.");
                return;
            }

            if (system != null && (system.isPlaying || system.isPaused))
            {
                var state = system.isPaused ? "paused" : "playing";
                problems.Add(path, $"ParticleSystem '{GameObjectFinder.GetPath(system.gameObject)}' is {state}; particle writes need a stopped system and never stop it implicitly. " +
                    "Stop it first: press Stop in the Scene view's Particle Effect overlay (an edit-mode preview of a selected system plays it), or deselect it.");
            }
        }

        internal static bool IsInOpenScene(GameObject go)
        {
            if (go == null || EditorUtility.IsPersistent(go))
                return false;
            var scene = go.scene;
            if (!scene.IsValid() || !scene.isLoaded || EditorSceneManager.IsPreviewScene(scene))
                return false;
            return true;
        }

        /// <summary>The identity keys every particle response carries for its target, in the framework's order.</summary>
        internal static void AddIdentity(IDictionary<string, object> result, GameObject go)
        {
            result["name"] = go.name;
            result["entityId"] = UnityObjectIdUtility.GetEntityId(go);
            result["instanceId"] = UnityObjectIdUtility.GetObjectId(go);
            result["path"] = GameObjectFinder.GetPath(go);
        }

        internal static Dictionary<string, object> ComponentIdentity(Component component)
        {
            if (component == null)
                return null;
            return new Dictionary<string, object>
            {
                ["type"] = component.GetType().Name,
                ["entityId"] = UnityObjectIdUtility.GetEntityId(component),
                ["instanceId"] = UnityObjectIdUtility.GetObjectId(component)
            };
        }

        /// <summary>The material Unity's own GameObject > Effects > Particle System menu assigns: the pipeline's default particle material.</summary>
        internal static Material DefaultParticleMaterial()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline;
            if (pipeline != null)
                return pipeline.defaultParticleMaterial;
            return AssetDatabase.GetBuiltinExtraResource<Material>("Default-ParticleSystem.mat");
        }

        // ===================== Errors =====================

        internal static Dictionary<string, object> Fix(string action, string skill, string reason)
        {
            var fix = new Dictionary<string, object> { ["action"] = action };
            if (!string.IsNullOrEmpty(skill))
                fix["skill"] = skill;
            fix["reason"] = reason;
            return fix;
        }

        /// <summary>A business error in the router's pass-through shape: error / errorCode / retryStrategy / suggestedFixes (+ extras).</summary>
        internal static Dictionary<string, object> Error(string code, string message, object[] fixes = null, string[] related = null,
            string parameter = null, object problems = null)
        {
            var result = new Dictionary<string, object>
            {
                ["error"] = message,
                ["errorCode"] = code,
                ["retryStrategy"] = RetryFor(code)
            };
            if (parameter != null)
                result["parameter"] = parameter;
            if (problems != null)
                result["problems"] = problems;
            if (fixes != null && fixes.Length > 0)
                result["suggestedFixes"] = fixes;
            if (related != null && related.Length > 0)
                result["relatedSkills"] = related;
            return result;
        }

        private static string RetryFor(string code)
        {
            switch (code)
            {
                case CodeTargetNotFound: return SkillErrorResponse.RetryFindAndRetry;
                case CodeMissingPackage: return SkillErrorResponse.RetryInstallAndRetry;
                default: return SkillErrorResponse.RetryFixAndRetry;
            }
        }

        /// <summary>Every problem of a rejected call in one error: the first decides the code and message, all are listed.</summary>
        internal static Dictionary<string, object> ProblemsError(string action, Problems problems)
        {
            var first = problems.Items.FirstOrDefault();
            var count = problems.Count;
            var message = first == null
                ? $"{action} was rejected."
                : count == 1
                    ? $"{action} was rejected: {first.Message}"
                    : $"{action} was rejected with {count} problems; the first: {first.Message}";
            var code = problems.LeadingCode;
            var fixes = new List<object>
            {
                Fix("fix_param", null, "Fix every entry of 'problems' (each names its JSON path), then resend; nothing was changed."),
            };
            if (code == CodeTargetNotFound)
                fixes.Add(Fix("find_target", "particle_find_all", "List the particle systems in the open scenes and retry with an entityId it returns."));
            else
                fixes.Add(Fix("fix_param", "particle_get_capabilities", "Look up the accepted fields, value shapes, ranges and enum values."));
            return Error(code, message, fixes: fixes.ToArray(), problems: problems.ToJson());
        }

        /// <summary>A failure while applying an already validated write; everything this call did has been reverted.</summary>
        internal static Dictionary<string, object> ApplyFailed(string action, System.Exception exception)
        {
            SkillsLogger.LogVerbose($"{action} failed while applying and was reverted: {exception}");
            return new Dictionary<string, object>
            {
                ["error"] = $"{action} failed while applying and was rolled back, so nothing was changed: {exception.Message}",
                ["errorCode"] = "INTERNAL",
                ["retryStrategy"] = SkillErrorResponse.Abort,
                ["exceptionType"] = exception.GetType().Name
            };
        }
    }
}
#endif

// Producer:Betsy
