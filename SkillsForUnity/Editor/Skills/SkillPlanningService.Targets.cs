using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    internal static partial class SkillPlanningService
    {
        private static void TryValidateComponentAssignment(
            JObject args,
            string propertyName,
            Type targetType,
            SkillRouter.ParameterValidationResult validation)
        {
            var assetPath = GetStringArg(args, "assetPath");
            var referencePath = GetStringArg(args, "referencePath");
            var referenceName = GetStringArg(args, "referenceName");
            var referenceEntityId = GetStringArg(args, "referenceEntityId");
            var value = GetStringArg(args, "value");

            try
            {
                if (!string.IsNullOrEmpty(assetPath))
                {
                    var asset = AssetDatabase.LoadAssetAtPath(assetPath, targetType);
                    if (asset == null)
                        AddSemanticError(validation, propertyName, $"Asset not found or type mismatch: '{assetPath}' (expected {targetType.Name})");
                    return;
                }

                if (!string.IsNullOrEmpty(referencePath) || !string.IsNullOrEmpty(referenceName) || UnityObjectIdUtility.IsGivenEntityId(referenceEntityId))
                {
                    var reference = ResolveLocator(referenceName, 0, referencePath, referenceEntityId);
                    if (reference.IsPending)
                    {
                        WarnIfPending(validation, "reference", reference);
                        return;
                    }

                    // Reported under the parameter, so the generic entityId check does not report the same miss again.
                    if (reference.Error != null && UnityObjectIdUtility.IsGivenEntityId(referenceEntityId))
                    {
                        AddSemanticError(validation, "referenceEntityId", ExtractError(reference.Error));
                        return;
                    }

                    var resolved = reference.LiveObject != null ? ResolveSceneReference(targetType, reference.LiveObject) : null;
                    if (resolved == null)
                        AddSemanticError(validation, propertyName, $"Could not resolve reference for {propertyName}. Target: path='{referencePath}', name='{referenceName}'");
                    return;
                }

                ComponentSkills.ConvertValue(value, targetType);
            }
            catch (Exception ex)
            {
                AddSemanticError(validation, propertyName, ex.Message);
            }
        }

        // ===================== Scene target resolution & pending objects =====================

        /// <summary>
        /// Where a planner's locator points: a live scene object, an object that is only predicted (by an earlier item
        /// of the same batch call, or an earlier step of a /skills/batch dry run), or nowhere. Planners run their
        /// target-dependent checks (components, renderer, prefab link) only against a live object.
        /// </summary>
        private readonly struct TargetResolution
        {
            public readonly GameObject LiveObject;
            public readonly string PendingPath;
            // True when an earlier item of the same batch call predicted it: that is the call's own intent, so no warning.
            public readonly bool FromBatch;
            public readonly object Error;

            private TargetResolution(GameObject liveObject, string pendingPath, bool fromBatch, object error)
            {
                LiveObject = liveObject;
                PendingPath = pendingPath;
                FromBatch = fromBatch;
                Error = error;
            }

            public static TargetResolution Live(GameObject go) => new TargetResolution(go, null, false, null);
            public static TargetResolution Pending(string path, bool fromBatch) => new TargetResolution(null, path, fromBatch, null);
            public static TargetResolution Failed(object error) => new TargetResolution(null, null, false, error);

            public bool IsPending => PendingPath != null;
            public string Path => LiveObject != null ? GameObjectFinder.GetPath(LiveObject) : PendingPath;
            public string Name => LiveObject != null ? LiveObject.name : PendingPath?.Substring(PendingPath.LastIndexOf('/') + 1);
        }

        private static TargetResolution ResolveTarget(
            JObject args,
            string nameKey = "name",
            string instanceIdKey = "instanceId",
            string pathKey = "path",
            string entityIdKey = "entityId")
        {
            var (_, name, instanceId, path, entityId) = ReadObjectLocator(args, nameKey, instanceIdKey, pathKey, entityIdKey);
            return ResolveLocator(name, instanceId, path, entityId);
        }

        /// <summary>
        /// The one scene lookup every planner goes through. An id always means a live object -- nothing predicted has one.
        /// A name or path is first matched against <paramref name="batchCreated"/> (earlier items of the same call, which the
        /// executor also resolves first), then, inside <see cref="BeginPendingObjectsScope"/>, against what earlier steps
        /// would create, in the order execution will find it: exact live path, pending path, exact live name, pending name,
        /// then a name containing '/' read as a path.
        /// Anything else falls through to the finder unchanged, fuzzy name match included -- outside a scope and without a
        /// batch map, this is exactly <see cref="GameObjectFinder.FindOrError"/>.
        /// </summary>
        private static TargetResolution ResolveLocator(string name, int instanceId, string path, string entityId, PendingObjectSet batchCreated = null)
        {
            if (instanceId == 0 && string.IsNullOrEmpty(entityId))
            {
                if (batchCreated != null && batchCreated.TryMatch(name, path, liveFirst: false, out var batchPath))
                    return TargetResolution.Pending(batchPath, fromBatch: true);

                if (_pendingObjects != null && _pendingObjects.TryMatch(name, path, liveFirst: true, out var pendingPath))
                    return TargetResolution.Pending(pendingPath, fromBatch: false);
            }

            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path, entityId: entityId);
            return error != null ? TargetResolution.Failed(error) : TargetResolution.Live(go);
        }

        private static void WarnIfPending(SkillRouter.ParameterValidationResult validation, string role, TargetResolution target, int itemIndex = -1)
        {
            if (!target.IsPending || target.FromBatch)
                return;

            var note = $"{role} '{target.PendingPath}' will be created by an earlier step; checks that need the live object were skipped.";
            AddWarning(validation, itemIndex >= 0 ? $"items[{itemIndex}]: {note}" : note);
        }

        /// <summary>
        /// Inside a pending-objects scope, the components earlier steps add to <paramref name="go"/> that execution's
        /// <c>GetComponent(type)</c> would find (<paramref name="type"/> or a subclass), in step order; empty outside one.
        /// </summary>
        private static List<Type> PendingComponentsOf(GameObject go, Type type) =>
            _pendingObjects?.ComponentsOn(GameObjectFinder.GetPath(go)).Where(t => type.IsAssignableFrom(t)).ToList() ?? new List<Type>();

        /// <summary>
        /// <see cref="PendingComponentsOf"/>'s first match, else a concrete RequireComponent type of a pending component that
        /// <paramref name="type"/> would find: the object lacks <paramref name="type"/>, so Unity adds it with that component
        /// (a HingeJoint brings a Rigidbody). Null when neither. Not for removal, which that component's requirement blocks.
        /// </summary>
        private static Type PendingOrRequiredComponentOf(GameObject go, Type type)
        {
            var direct = PendingComponentsOf(go, type);
            if (direct.Count > 0)
                return direct[0];

            return PendingComponentsOf(go, typeof(Component))
                .SelectMany(pendingType => RequiredComponentTypes(pendingType))
                .FirstOrDefault(required => !required.IsAbstract && type.IsAssignableFrom(required));
        }

        private static Type[] RequiredComponentTypes(Type componentType)
        {
            try
            {
                return componentType.GetCustomAttributes(typeof(RequireComponent), true)
                    .OfType<RequireComponent>()
                    .SelectMany(requirement => new[] { requirement.m_Type0, requirement.m_Type1, requirement.m_Type2 })
                    .Where(required => required != null)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }

        private static void WarnPendingComponent(SkillRouter.ParameterValidationResult validation, Type component, string path, int itemIndex = -1)
        {
            var note = $"component '{component.Name}' on '{path}' will be added by an earlier step; checks that need the live component were skipped.";
            AddWarning(validation, itemIndex >= 0 ? $"items[{itemIndex}]: {note}" : note);
        }

        // The ambient set of a /skills/batch dry run (BeginPendingObjectsScope); null outside one. Main thread only, like every planner.
        [ThreadStatic] private static PendingObjectSet _pendingObjects;

        /// <summary>
        /// Opens the pending-objects scope a multi-step dry run wraps its loop in. While it is open, every
        /// <c>SkillRouter.DryRun</c> / <c>Plan</c> registers the GameObjects and components its valid plan would create
        /// (<see cref="RegisterPendingCreates"/>), and planners accept a later locator naming one of them, with a warning,
        /// instead of reporting it as not found. A nested call reuses the outer set and its disposal leaves that set open.
        /// </summary>
        internal static IDisposable BeginPendingObjectsScope()
        {
            if (_pendingObjects != null)
                return NestedPendingObjectsScope.Instance;

            _pendingObjects = new PendingObjectSet();
            return new PendingObjectsScope();
        }

        private sealed class PendingObjectsScope : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                _pendingObjects = null;
            }
        }

        private sealed class NestedPendingObjectsScope : IDisposable
        {
            public static readonly NestedPendingObjectsScope Instance = new NestedPendingObjectsScope();

            public void Dispose() { }
        }

        /// <summary>
        /// Inside a pending-objects scope, records what a call would create: its planner's <c>changes.create[]</c> entries
        /// that carry a <c>predictedPath</c> (GameObjects) or a <c>target</c> and <c>fullTypeName</c> (components), and,
        /// when no entry predicts the object, the one a <see cref="CreatesNamedSceneObject"/> skill makes. An invalid call
        /// registers nothing -- execution rejects it before anything exists. No-op outside a scope.
        /// </summary>
        internal static void RegisterPendingCreates(SkillRouter.SkillInfo skill, SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var pending = _pendingObjects;
            if (pending == null || validation == null || !validation.Valid)
                return;

            bool predictedObject = false;
            if (plan != null && plan.TryGetValue("changes", out var changesObj) && changesObj is IDictionary<string, object> changes &&
                changes.TryGetValue("create", out var createObj) && createObj is IEnumerable<object> creates)
            {
                foreach (var entry in creates.OfType<IDictionary<string, object>>())
                {
                    if (entry.TryGetValue("predictedPath", out var predictedPathObj) && entry.TryGetValue("name", out var name))
                    {
                        // Under a parent known by name only, the predicted path is a guess: keep the name alone.
                        var predictedPath = predictedPathObj as string;
                        int slash = predictedPath?.LastIndexOf('/') ?? -1;
                        if (slash > 0 && pending.IsNameOnly(predictedPath.Substring(0, slash)))
                            pending.RegisterName(name as string, predictedPath);
                        else
                            pending.Register(name as string, predictedPath);
                        predictedObject = true;
                    }
                    else if (entry.TryGetValue("target", out var target) && entry.TryGetValue("fullTypeName", out var fullTypeName))
                    {
                        pending.RegisterComponent(target as string, SkillsCommon.FindTypeByName(fullTypeName as string));
                    }
                }
            }

            if (!predictedObject && CreatesNamedSceneObject(skill))
                RegisterUnplannedCreate(pending, skill, validation.Args);
        }

        /// <summary>
        /// A scene create whose <c>name</c> names the GameObject it makes (light_create, camera_create, ui_create_*, ...):
        /// Create and MutatesScene, a <c>name</c> parameter, and none of the instanceId / path / entityId locators a skill
        /// acting on an existing object declares beside its <c>name</c> (component_add). Asset creators do not mutate the scene.
        /// </summary>
        private static bool CreatesNamedSceneObject(SkillRouter.SkillInfo skill)
        {
            var parameters = skill?.ParameterNames;
            return parameters != null && skill.Operation.HasFlag(SkillOperation.Create) && skill.MutatesScene &&
                parameters.Contains("name") &&
                !parameters.Contains("instanceId") && !parameters.Contains("path") && !parameters.Contains("entityId");
        }

        /// <summary>
        /// Registers the object such a skill makes, from its arguments alone. A skill without parent parameters puts it at
        /// the scene root; a given parent that resolves (live or pending) puts it under that parent. Otherwise where it lands
        /// is the skill's own choice (a UI element without a parent goes under a Canvas), so only its name is registered.
        /// </summary>
        private static void RegisterUnplannedCreate(PendingObjectSet pending, SkillRouter.SkillInfo skill, JObject args)
        {
            var name = GetStringArg(args, "name");
            if (string.IsNullOrWhiteSpace(name))
                return;

            if (!skill.ParameterNames.Any(parameter => parameter.StartsWith("parent", StringComparison.Ordinal)))
            {
                pending.Register(name, name);
                return;
            }

            // The parent* locator family, or the single "parent" name of ui_create_* / probuilder_create_shape / xr_add_teleport_anchor.
            var parentName = GetStringArg(args, "parentName", "parent");
            var parentPath = GetStringArg(args, "parentPath");
            int parentInstanceId = GetIntArg(args, "parentInstanceId");
            var parentEntityId = GetStringArg(args, "parentEntityId");
            if (!string.IsNullOrEmpty(parentName) || !string.IsNullOrEmpty(parentPath) || parentInstanceId != 0 || !string.IsNullOrEmpty(parentEntityId))
            {
                var parent = ResolveLocator(parentName, parentInstanceId, parentPath, parentEntityId);
                // A parent known by name only has no path to put the child under.
                if (parent.Error == null && !(parent.IsPending && pending.IsNameOnly(parent.PendingPath)))
                {
                    pending.Register(name, parent.Path + "/" + name);
                    return;
                }
            }
            pending.RegisterName(name);
        }

        /// <summary>
        /// Objects that are predicted but do not exist yet, and the components predicted on an object (live or predicted),
        /// keyed by its hierarchy path. Names and hierarchy paths compare case-insensitively, the finder's exact-match
        /// rules; a later registration of the same name or path wins, as in the batch executor.
        /// </summary>
        private sealed class PendingObjectSet
        {
            private readonly Dictionary<string, string> _pathsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, string> _paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, List<Type>> _componentsByPath = new Dictionary<string, List<Type>>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _nameOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public void Register(string name, string predictedPath)
            {
                var path = GameObjectFinder.NormalizePathKey(predictedPath);
                if (path == null || string.IsNullOrWhiteSpace(name))
                    return;

                _paths[path] = path;
                _pathsByName[name] = path;
                _nameOnly.Remove(name);
                _nameOnly.Remove(path);
            }

            /// <summary>
            /// An object whose place is unknown: matched by name only, and reported under its name. A planner's
            /// <paramref name="guessedPath"/> for it is remembered too, so the objects predicted under it stay name-only.
            /// </summary>
            public void RegisterName(string name, string guessedPath = null)
            {
                if (string.IsNullOrWhiteSpace(name))
                    return;

                _pathsByName[name] = name;
                _nameOnly.Add(name);
                if (!string.IsNullOrEmpty(guessedPath))
                    _nameOnly.Add(guessedPath);
            }

            /// <summary>Whether a match's <paramref name="pendingPath"/> is a <see cref="RegisterName"/> object's name (or guessed path) rather than a path.</summary>
            public bool IsNameOnly(string pendingPath) => pendingPath != null && _nameOnly.Contains(pendingPath);

            public void RegisterComponent(string targetPath, Type componentType)
            {
                var path = GameObjectFinder.NormalizePathKey(targetPath);
                if (path == null || componentType == null)
                    return;

                if (!_componentsByPath.TryGetValue(path, out var types))
                    _componentsByPath[path] = types = new List<Type>();
                types.Add(componentType);
            }

            /// <summary>The component types earlier steps add to the object at <paramref name="path"/>, one entry per add, in step order.</summary>
            public IReadOnlyList<Type> ComponentsOn(string path)
            {
                var key = GameObjectFinder.NormalizePathKey(path);
                return key != null && _componentsByPath.TryGetValue(key, out var types) ? types : (IReadOnlyList<Type>)Array.Empty<Type>();
            }

            /// <summary>
            /// Follows the precedence execution will apply once the prediction exists. <paramref name="liveFirst"/> false
            /// (earlier items of the same call): predicted path, live path, predicted name -- the executor's order.
            /// True (earlier steps): an exact live match of the same kind wins over a prediction, as it would when both exist.
            /// Last, a name containing '/' that names nothing is read as a path, as the finder does.
            /// </summary>
            public bool TryMatch(string name, string path, bool liveFirst, out string pendingPath)
            {
                pendingPath = null;
                // Every registration names its object; a name-only one has no path.
                if (_pathsByName.Count == 0)
                    return false;

                if (!string.IsNullOrEmpty(path))
                {
                    if (liveFirst && GameObjectFinder.FindByPath(path) != null)
                        return false;
                    if (TryMatchPath(path, out pendingPath))
                        return true;
                    if (!liveFirst && GameObjectFinder.FindByPath(path) != null)
                        return false;
                }

                if (!string.IsNullOrEmpty(name))
                {
                    if (liveFirst && GameObjectFinder.FindByNameCaseInsensitive(name) != null)
                        return false;
                    if (_pathsByName.TryGetValue(name, out pendingPath))
                        return true;

                    // Execution reads a '/'-name that names no object as a hierarchy path. Live and created objects
                    // both hold paths by then and a live one is indexed first, so it wins a tie; liveFirst already ruled out a live name.
                    if (name.IndexOf('/') >= 0 &&
                        (liveFirst || GameObjectFinder.FindByNameCaseInsensitive(name) == null) &&
                        GameObjectFinder.FindByPath(name) == null)
                        return TryMatchPath(name, out pendingPath);
                }

                return false;
            }

            private bool TryMatchPath(string path, out string pendingPath)
            {
                pendingPath = null;
                var key = GameObjectFinder.NormalizePathKey(path);
                if (key == null)
                    return false;
                if (_paths.TryGetValue(key, out pendingPath))
                    return true;

                // The finder also accepts "SceneName/Root/Child"; a prediction never carries the scene segment.
                int slash = key.IndexOf('/');
                if (slash <= 0)
                    return false;
                var firstSegment = key.Substring(0, slash);
                for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                    if (scene.IsValid() && scene.isLoaded && string.Equals(scene.name, firstSegment, StringComparison.OrdinalIgnoreCase))
                        return _paths.TryGetValue(key.Substring(slash + 1), out pendingPath);
                }
                return false;
            }
        }

        private static (bool hasLocator, string name, int instanceId, string path, string entityId) ReadObjectLocator(
            JObject args,
            string nameKey,
            string instanceIdKey,
            string pathKey,
            string entityIdKey = "entityId")
        {
            var name = GetStringArg(args, nameKey);
            var path = GetStringArg(args, pathKey);
            int instanceId = GetIntArg(args, instanceIdKey);
            var entityId = GetStringArg(args, entityIdKey);
            bool hasLocator = !string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(path) || instanceId != 0 || !string.IsNullOrEmpty(entityId);
            return (hasLocator, name, instanceId, path, entityId);
        }
    }
}

// Producer:Betsy
