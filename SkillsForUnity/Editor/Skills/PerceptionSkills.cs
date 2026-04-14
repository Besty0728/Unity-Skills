using UnityEngine;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditorInternal;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace UnitySkills
{
    /// <summary>
    /// Scene Understanding Skills - Help AI quickly perceive project state.
    /// </summary>
    public static class PerceptionSkills
    {
        private sealed class CodeDependencyScriptInfo
        {
            public string Path;
            public string ClassName;
        }

        private sealed class CodeDependencyCache
        {
            public readonly HashSet<string> UserClassNames = new HashSet<string>();
            public readonly List<CodeDependencyScriptInfo> Scripts = new List<CodeDependencyScriptInfo>();
            public List<DependencyEdge> AllEdges;
        }

        private static CodeDependencyCache _codeDependencyCache;
        private static bool _codeDependencyCacheDirty = true;

        private static readonly HashSet<string> UnityCallbacks = new HashSet<string>
        {
            "Awake", "Start", "Update", "FixedUpdate", "LateUpdate",
            "OnEnable", "OnDisable", "OnDestroy",
            "OnCollisionEnter", "OnCollisionExit", "OnCollisionStay",
            "OnCollisionEnter2D", "OnCollisionExit2D", "OnCollisionStay2D",
            "OnTriggerEnter", "OnTriggerExit", "OnTriggerStay",
            "OnTriggerEnter2D", "OnTriggerExit2D", "OnTriggerStay2D",
            "OnMouseDown", "OnMouseUp", "OnMouseEnter", "OnMouseExit",
            "OnGUI", "OnDrawGizmos", "OnValidate",
            "OnBecameVisible", "OnBecameInvisible",
            "OnApplicationPause", "OnApplicationQuit", "OnApplicationFocus",
            "OnAnimatorIK", "OnAnimatorMove",
            "OnParticleCollision", "OnParticleTrigger",
            "OnRenderObject", "OnPreRender", "OnPostRender",
            "OnWillRenderObject", "OnRenderImage"
        };

        static PerceptionSkills()
        {
            EditorApplication.projectChanged += InvalidateCodeDependencyCache;
            CompilationPipeline.compilationFinished += _ => InvalidateCodeDependencyCache();
        }

        private static void InvalidateCodeDependencyCache()
        {
            _codeDependencyCache = null;
            _codeDependencyCacheDirty = true;
        }

        private sealed class SceneMetricsSnapshot
        {
            public Scene Scene;
            public IReadOnlyList<GameObject> Objects;
            public Dictionary<string, int> ComponentCounts = new Dictionary<string, int>();
            public int TotalObjects;
            public int ActiveObjects;
            public int DisabledObjects;
            public int RootObjects;
            public int MaxHierarchyDepth;
            public int Cameras;
            public int MainCameraCount;
            public int Lights;
            public int Canvases;
            public int EventSystems;
            public int AudioListeners;
            public int PrefabInstances;
            public bool HasUiGraphic;
            public bool HasUiToolkitDocument;
            public int EmptyLeafCount;
        }

        private sealed class SceneHotspot
        {
            public string Type;
            public string Severity;
            public string Name;
            public string Path;
            public int Count;
            public int Depth;
            public string Message;
        }

        private static readonly string[] DefaultContractRoots = { "Systems", "Managers", "Gameplay", "UIRoot" };

        private static SceneMetricsSnapshot CollectSceneMetrics(bool includeComponentStats = true)
        {
            var scene = SceneManager.GetActiveScene();
            var allObjects = GameObjectFinder.GetSceneObjects();
            var snapshot = new SceneMetricsSnapshot
            {
                Scene = scene,
                Objects = allObjects,
                TotalObjects = allObjects.Count,
                RootObjects = scene.rootCount
            };

            var componentBuffer = new List<Component>(8);
            var uiDocumentType = FindTypeInAssemblies("UnityEngine.UIElements.UIDocument");

            foreach (var go in allObjects)
            {
                if (go.activeInHierarchy) snapshot.ActiveObjects++;
                else snapshot.DisabledObjects++;

                var depth = GameObjectFinder.GetDepth(go);
                if (depth > snapshot.MaxHierarchyDepth)
                    snapshot.MaxHierarchyDepth = depth;

                if (PrefabUtility.IsPartOfPrefabInstance(go) && !PrefabUtility.IsPartOfPrefabAsset(go))
                    snapshot.PrefabInstances++;

                componentBuffer.Clear();
                go.GetComponents(componentBuffer);

                if (componentBuffer.Count == 1 && go.transform.childCount == 0)
                    snapshot.EmptyLeafCount++;

                foreach (var component in componentBuffer)
                {
                    if (component == null)
                        continue;

                    var typeName = component.GetType().Name;
                    if (includeComponentStats)
                    {
                        snapshot.ComponentCounts[typeName] = snapshot.ComponentCounts.TryGetValue(typeName, out var count)
                            ? count + 1
                            : 1;
                    }

                    if (component is Camera)
                    {
                        snapshot.Cameras++;
                        if (go.CompareTag("MainCamera"))
                            snapshot.MainCameraCount++;
                    }
                    else if (component is Light)
                    {
                        snapshot.Lights++;
                    }
                    else if (component is Canvas)
                    {
                        snapshot.Canvases++;
                    }
                    else if (component is EventSystem)
                    {
                        snapshot.EventSystems++;
                    }
                    else if (component is AudioListener)
                    {
                        snapshot.AudioListeners++;
                    }
                    else if (component is Graphic)
                    {
                        snapshot.HasUiGraphic = true;
                    }
                }

                if (uiDocumentType != null && go.GetComponent(uiDocumentType) != null)
                    snapshot.HasUiToolkitDocument = true;
            }

            return snapshot;
        }

        private static Type FindTypeInAssemblies(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = null;
                try { type = assembly.GetType(fullName, false); }
                catch { }

                if (type != null)
                    return type;
            }

            return null;
        }

        private static object GetPropertyValue(object target, string name)
        {
            if (target == null)
                return null;

            var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            return property?.GetValue(target);
        }

        private static T GetPropertyValue<T>(object target, string name, T fallback = default)
        {
            var value = GetPropertyValue(target, name);
            if (value == null)
                return fallback;

            if (value is T typed)
                return typed;

            try
            {
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return fallback;
            }
        }

        private static IEnumerable<object> GetEnumerableProperty(object target, string name)
        {
            var value = GetPropertyValue(target, name);
            if (value is IEnumerable enumerable && !(value is string))
                return enumerable.Cast<object>();

            return Enumerable.Empty<object>();
        }

        private static object[] BuildTopComponents(SceneMetricsSnapshot snapshot, int topComponentsLimit)
        {
            return snapshot.ComponentCounts
                .Where(kv => kv.Key != "Transform")
                .OrderByDescending(kv => kv.Value)
                .Take(topComponentsLimit)
                .Select(kv => (object)new { component = kv.Key, count = kv.Value })
                .ToArray();
        }

        private static string[] ParseOptionalStringArray(string rawJson, string[] defaults)
        {
            if (string.IsNullOrWhiteSpace(rawJson))
                return defaults?.ToArray() ?? Array.Empty<string>();

            return JArray.Parse(rawJson)
                .Values<string>()
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static List<SceneHotspot> CollectHotspots(IReadOnlyList<GameObject> allObjects, int deepHierarchyThreshold, int largeChildCountThreshold, int maxResults)
        {
            var hotspots = new List<SceneHotspot>();

            foreach (var go in allObjects)
            {
                var depth = GameObjectFinder.GetDepth(go);
                if (depth >= deepHierarchyThreshold)
                {
                    hotspots.Add(new SceneHotspot
                    {
                        Type = "DeepHierarchy",
                        Severity = depth >= deepHierarchyThreshold + 3 ? "Warning" : "Info",
                        Name = go.name,
                        Path = GameObjectFinder.GetCachedPath(go),
                        Depth = depth,
                        Count = depth,
                        Message = $"Hierarchy depth {depth} exceeds threshold {deepHierarchyThreshold}."
                    });
                }

                if (go.transform.childCount >= largeChildCountThreshold)
                {
                    hotspots.Add(new SceneHotspot
                    {
                        Type = "LargeChildSet",
                        Severity = go.transform.childCount >= largeChildCountThreshold * 2 ? "Warning" : "Info",
                        Name = go.name,
                        Path = GameObjectFinder.GetCachedPath(go),
                        Count = go.transform.childCount,
                        Message = $"{go.transform.childCount} direct children under one node."
                    });
                }
            }

            foreach (var group in allObjects.GroupBy(go => go.name).Where(g => g.Count() > 1))
            {
                hotspots.Add(new SceneHotspot
                {
                    Type = "DuplicateNameCluster",
                    Severity = group.Count() >= 5 ? "Warning" : "Info",
                    Name = group.Key,
                    Count = group.Count(),
                    Message = $"{group.Count()} objects share the name '{group.Key}'."
                });
            }

            var emptyLeafGroups = allObjects
                .Where(go => go.transform.childCount == 0 && go.GetComponents<Component>().Length == 1)
                .GroupBy(go => go.transform.parent != null ? GameObjectFinder.GetCachedPath(go.transform.parent.gameObject) : "<root>")
                .Where(g => g.Count() >= 3);

            foreach (var group in emptyLeafGroups)
            {
                hotspots.Add(new SceneHotspot
                {
                    Type = "EmptyLeafCluster",
                    Severity = "Info",
                    Path = group.Key,
                    Count = group.Count(),
                    Message = $"{group.Count()} empty leaf objects are grouped under '{group.Key}'."
                });
            }

            return hotspots
                .OrderBy(h => GetSeverityRank(h.Severity))
                .ThenByDescending(h => h.Count)
                .ThenByDescending(h => h.Depth)
                .Take(maxResults)
                .ToList();
        }

        private static int GetSeverityRank(string severity)
        {
            switch (severity)
            {
                case "Error":
                    return 0;
                case "Warning":
                    return 1;
                default:
                    return 2;
            }
        }

        private static HashSet<string> ReadInstalledPackageIds()
        {
            var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var manifestPath = Path.Combine("Packages", "manifest.json");
            if (!File.Exists(manifestPath))
                return packageIds;

            try
            {
                var manifest = JObject.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
                if (manifest["dependencies"] is JObject dependencies)
                {
                    foreach (var dependency in dependencies.Properties())
                        packageIds.Add(dependency.Name);
                }
            }
            catch
            {
                // Ignore malformed manifest and fall back to empty detection.
            }

            return packageIds;
        }

        private static string DetectInputHandling(HashSet<string> packageIds)
        {
            var property = typeof(PlayerSettings).GetProperty("activeInputHandler", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?? typeof(PlayerSettings).GetProperty("activeInputHandling", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            if (property != null)
            {
                try
                {
                    var value = property.GetValue(null);
                    if (value != null)
                        return value.ToString();
                }
                catch
                {
                    // Ignore reflection failure and fall back to package-based inference.
                }
            }

            return packageIds.Contains("com.unity.inputsystem")
                ? "InputSystemPackageInstalled"
                : "LegacyInputManager";
        }

        private static string DetermineUiRoute(SceneMetricsSnapshot metrics, bool hasUiToolkitAssets)
        {
            var usesUgui = metrics.Canvases > 0 || metrics.HasUiGraphic;
            var usesUiToolkit = metrics.HasUiToolkitDocument || hasUiToolkitAssets;

            if (usesUgui && usesUiToolkit) return "Both";
            if (usesUiToolkit) return "UIToolkit";
            if (usesUgui) return "UGUI";
            return "Unknown";
        }

        private static string DetermineProjectProfile(SceneMetricsSnapshot metrics, bool xrDetected, string uiRoute)
        {
            if (xrDetected)
                return "XR";

            var spriteRendererCount = metrics.ComponentCounts.TryGetValue(nameof(SpriteRenderer), out var spriteCount) ? spriteCount : 0;
            var meshRendererCount = metrics.ComponentCounts.TryGetValue(nameof(MeshRenderer), out var meshCount) ? meshCount : 0;

            if (uiRoute != "Unknown" && metrics.Canvases >= Math.Max(1, metrics.Cameras))
                return "UI";

            if (spriteRendererCount > meshRendererCount && spriteRendererCount > 0)
                return "2D";

            return "3D";
        }

        private static List<object> BuildSuggestedNextSkills(IEnumerable<object> findings)
        {
            var findingTypes = findings
                .Select(f => GetPropertyValue<string>(f, "type", string.Empty))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var suggestions = new List<object>
            {
                new { skill = "scene_context", reason = "Export a deeper scene snapshot for follow-up coding or review.", priority = 3 }
            };

            if (findingTypes.Contains("MissingScript") || findingTypes.Contains("MissingReference"))
                suggestions.Add(new { skill = "batch_fix_missing_scripts", reason = "Use the validation output as input to preview missing-script cleanup.", priority = 1 });

            if (findingTypes.Contains("DuplicateName") || findingTypes.Contains("DuplicateNameCluster"))
                suggestions.Add(new { skill = "batch_standardize_naming", reason = "Normalize duplicate or inconsistent object names before more automation.", priority = 2 });

            if (findingTypes.Contains("EmptyGameObject") || findingTypes.Contains("EmptyLeafCluster"))
                suggestions.Add(new { skill = "batch_cleanup_temp_objects", reason = "Preview cleanup for empty or temporary helper objects.", priority = 2 });

            if (findingTypes.Contains("MissingEventSystem") || findingTypes.Contains("MissingCanvas"))
                suggestions.Add(new { skill = "ui_create_canvas", reason = "Repair missing UI infrastructure before adding more UI automation.", priority = 1 });

            if (findingTypes.Contains("MissingRoot") || findingTypes.Contains("MissingTagDefinition") || findingTypes.Contains("MissingLayerDefinition"))
                suggestions.Add(new { skill = "scene_contract_validate", reason = "Re-run contract validation after aligning scene conventions.", priority = 2 });

            return suggestions
                .GroupBy(s => GetPropertyValue<string>(s, "skill", string.Empty), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(s => GetPropertyValue<int>(s, "priority", 99)).First())
                .OrderBy(s => GetPropertyValue<int>(s, "priority", 99))
                .Cast<object>()
                .ToList();
        }

        private static List<object> DeduplicateFindings(IEnumerable<object> findings)
        {
            var result = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var finding in findings)
            {
                var key = string.Join("|",
                    GetPropertyValue<string>(finding, "type", string.Empty),
                    GetPropertyValue<string>(finding, "path", string.Empty),
                    GetPropertyValue<string>(finding, "message", string.Empty));

                if (seen.Add(key))
                    result.Add(finding);
            }

            return result;
        }

        [UnitySkill("scene_component_stats", "Get detailed scene component statistics and key infrastructure counts.",
            Category = SkillCategory.Perception, Operation = SkillOperation.Analyze,
            Tags = new[] { "scene", "components", "statistics", "overview" },
            Outputs = new[] { "sceneName", "stats", "keyFacilities", "topComponents" },
            ReadOnly = true)]
        public static object SceneComponentStats(int topComponentsLimit = 15)
        {
            var metrics = CollectSceneMetrics(includeComponentStats: true);
            var totalObjects = Math.Max(metrics.TotalObjects, 1);

            return new
            {
                success = true,
                sceneName = metrics.Scene.name,
                stats = new
                {
                    totalObjects = metrics.TotalObjects,
                    activeObjects = metrics.ActiveObjects,
                    inactiveObjects = metrics.TotalObjects - metrics.ActiveObjects,
                    rootObjects = metrics.RootObjects,
                    maxHierarchyDepth = metrics.MaxHierarchyDepth,
                    prefabInstances = metrics.PrefabInstances,
                    disabledObjects = metrics.DisabledObjects,
                    disabledRatio = (float)Math.Round(metrics.DisabledObjects / (double)totalObjects, 3),
                    emptyLeafObjects = metrics.EmptyLeafCount,
                    cameras = metrics.Cameras,
                    mainCameras = metrics.MainCameraCount,
                    lights = metrics.Lights,
                    canvases = metrics.Canvases,
                    eventSystems = metrics.EventSystems,
                    audioListeners = metrics.AudioListeners
                },
                keyFacilities = new
                {
                    hasMainCamera = metrics.MainCameraCount > 0,
                    hasLight = metrics.Lights > 0,
                    hasCanvas = metrics.Canvases > 0,
                    hasEventSystem = metrics.EventSystems > 0,
                    hasAudioListener = metrics.AudioListeners > 0,
                    hasUgui = metrics.Canvases > 0 || metrics.HasUiGraphic,
                    hasUiToolkit = metrics.HasUiToolkitDocument
                },
                topComponents = BuildTopComponents(metrics, topComponentsLimit)
            };
        }

        [UnitySkill("scene_find_hotspots", "Find deep hierarchies, large child groups, duplicate-name clusters, and empty-node hotspots in the current scene.",
            Category = SkillCategory.Perception, Operation = SkillOperation.Analyze,
            Tags = new[] { "scene", "hotspots", "hierarchy", "diagnostics" },
            Outputs = new[] { "sceneName", "hotspotCount", "hotspots" },
            ReadOnly = true)]
        public static object SceneFindHotspots(int deepHierarchyThreshold = 8, int largeChildCountThreshold = 25, int maxResults = 20)
        {
            var metrics = CollectSceneMetrics(includeComponentStats: false);
            var hotspots = CollectHotspots(metrics.Objects, deepHierarchyThreshold, largeChildCountThreshold, maxResults);

            return new
            {
                success = true,
                sceneName = metrics.Scene.name,
                thresholds = new
                {
                    deepHierarchyThreshold,
                    largeChildCountThreshold
                },
                hotspotCount = hotspots.Count,
                hotspots = hotspots.Select(h => new
                {
                    type = h.Type,
                    severity = h.Severity,
                    name = h.Name,
                    path = h.Path,
                    count = h.Count,
                    depth = h.Depth,
                    message = h.Message
                }).ToArray()
            };
        }

        [UnitySkill("scene_health_check", "Run a read-only scene health report: missing scripts, missing references, duplicate names, empty nodes, deep hierarchy, and missing infrastructure.",
            Category = SkillCategory.Perception, Operation = SkillOperation.Analyze,
            Tags = new[] { "scene", "health", "validation", "diagnostics" },
            Outputs = new[] { "sceneName", "summary", "findings", "hotspots", "suggestedNextSkills" },
            ReadOnly = true)]
        public static object SceneHealthCheck(int issueLimit = 100, int deepHierarchyThreshold = 8, int largeChildCountThreshold = 25)
        {
            var metrics = CollectSceneMetrics(includeComponentStats: false);
            var findings = new List<object>();

            var sceneValidation = ValidationSkills.ValidateScene(checkEmptyGameObjects: true);
            foreach (var issue in GetEnumerableProperty(sceneValidation, "issues"))
            {
                findings.Add(new
                {
                    type = GetPropertyValue<string>(issue, "type", "Unknown"),
                    severity = GetPropertyValue<string>(issue, "severity", "Info"),
                    gameObject = GetPropertyValue<string>(issue, "gameObject", null),
                    path = GetPropertyValue<string>(issue, "path", null),
                    message = GetPropertyValue<string>(issue, "message", null),
                    count = GetPropertyValue<int>(issue, "count", 0),
                    source = "validate_scene"
                });
            }

            var missingReferences = ValidationSkills.ValidateMissingReferences(issueLimit);
            foreach (var issue in GetEnumerableProperty(missingReferences, "issues"))
            {
                findings.Add(new
                {
                    type = "MissingReference",
                    severity = "Error",
                    gameObject = GetPropertyValue<string>(issue, "gameObject", null),
                    path = GetPropertyValue<string>(issue, "path", null),
                    message = $"{GetPropertyValue<string>(issue, "component", "Component")}.{GetPropertyValue<string>(issue, "property", "property")} is missing a reference.",
                    source = "validate_missing_references"
                });
            }

            if (metrics.MainCameraCount == 0)
            {
                findings.Add(new
                {
                    type = "MissingMainCamera",
                    severity = "Error",
                    message = "No MainCamera-tagged camera was found in the active scene.",
                    source = "scene_health"
                });
            }

            if (metrics.Lights == 0)
            {
                findings.Add(new
                {
                    type = "MissingLight",
                    severity = "Warning",
                    message = "No Light component was found in the active scene.",
                    source = "scene_health"
                });
            }

            if ((metrics.Canvases > 0 || metrics.HasUiGraphic) && metrics.EventSystems == 0)
            {
                findings.Add(new
                {
                    type = "MissingEventSystem",
                    severity = "Error",
                    message = "UGUI objects exist but no EventSystem was found.",
                    source = "scene_health"
                });
            }

            if (metrics.HasUiGraphic && metrics.Canvases == 0)
            {
                findings.Add(new
                {
                    type = "MissingCanvas",
                    severity = "Error",
                    message = "UI graphics exist but no Canvas was found.",
                    source = "scene_health"
                });
            }

            if (metrics.Cameras > 0 && metrics.AudioListeners == 0)
            {
                findings.Add(new
                {
                    type = "MissingAudioListener",
                    severity = "Warning",
                    message = "Scene contains cameras but no AudioListener component was found.",
                    source = "scene_health"
                });
            }

            var hotspots = CollectHotspots(metrics.Objects, deepHierarchyThreshold, largeChildCountThreshold, issueLimit);
            foreach (var hotspot in hotspots.Where(h => h.Type != "DuplicateNameCluster"))
            {
                findings.Add(new
                {
                    type = hotspot.Type,
                    severity = hotspot.Severity,
                    path = hotspot.Path,
                    message = hotspot.Message,
                    count = hotspot.Count,
                    depth = hotspot.Depth,
                    source = "scene_hotspots"
                });
            }

            var uniqueFindings = DeduplicateFindings(findings);
            var visibleFindings = uniqueFindings.Take(issueLimit).ToArray();
            var suggestedNextSkills = BuildSuggestedNextSkills(visibleFindings);

            return new
            {
                success = true,
                sceneName = metrics.Scene.name,
                summary = new
                {
                    totalFindings = uniqueFindings.Count,
                    shownFindings = visibleFindings.Length,
                    errors = visibleFindings.Count(f => GetPropertyValue<string>(f, "severity", "Info") == "Error"),
                    warnings = visibleFindings.Count(f => GetPropertyValue<string>(f, "severity", "Info") == "Warning"),
                    info = visibleFindings.Count(f => GetPropertyValue<string>(f, "severity", "Info") == "Info"),
                    truncated = uniqueFindings.Count > visibleFindings.Length
                },
                findings = visibleFindings,
                hotspots = hotspots.Select(h => new
                {
                    type = h.Type,
                    severity = h.Severity,
                    name = h.Name,
                    path = h.Path,
                    count = h.Count,
                    depth = h.Depth,
                    message = h.Message
                }).ToArray(),
                suggestedNextSkills = suggestedNextSkills.ToArray()
            };
        }

        [UnitySkill("scene_contract_validate", "Validate default scene conventions (Systems/Managers/UIRoot/Gameplay, UI infrastructure, tags, and layers).",
            Category = SkillCategory.Perception, Operation = SkillOperation.Analyze,
            Tags = new[] { "scene", "contract", "convention", "validation" },
            Outputs = new[] { "sceneName", "summary", "findings", "checkedRoots" },
            ReadOnly = true)]
        public static object SceneContractValidate(
            string requiredRootsJson = null,
            string requiredTagsJson = null,
            string requiredLayersJson = null,
            bool requireEventSystemForUi = true)
        {
            string[] requiredRoots;
            string[] requiredTags;
            string[] requiredLayers;

            try
            {
                requiredRoots = ParseOptionalStringArray(requiredRootsJson, DefaultContractRoots);
                requiredTags = ParseOptionalStringArray(requiredTagsJson, Array.Empty<string>());
                requiredLayers = ParseOptionalStringArray(requiredLayersJson, Array.Empty<string>());
            }
            catch (Exception ex)
            {
                return new { success = false, error = $"Invalid contract JSON array: {ex.Message}" };
            }

            var metrics = CollectSceneMetrics(includeComponentStats: false);
            var findings = new List<object>();
            var rootNames = metrics.Scene.GetRootGameObjects()
                .Select(go => go.name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var requiredRoot in requiredRoots)
            {
                if (!rootNames.Contains(requiredRoot))
                {
                    findings.Add(new
                    {
                        type = "MissingRoot",
                        severity = "Warning",
                        name = requiredRoot,
                        message = $"Required root '{requiredRoot}' is missing."
                    });
                }
            }

            if (metrics.MainCameraCount == 0)
            {
                findings.Add(new
                {
                    type = "MissingMainCamera",
                    severity = "Error",
                    message = "Convention requires a MainCamera-tagged camera."
                });
            }

            if (metrics.Lights == 0)
            {
                findings.Add(new
                {
                    type = "MissingLight",
                    severity = "Warning",
                    message = "Convention expects at least one Light in the scene."
                });
            }

            if (metrics.HasUiGraphic && metrics.Canvases == 0)
            {
                findings.Add(new
                {
                    type = "MissingCanvas",
                    severity = "Error",
                    message = "UGUI graphics were found but no Canvas exists."
                });
            }

            if (requireEventSystemForUi && (metrics.Canvases > 0 || metrics.HasUiGraphic) && metrics.EventSystems == 0)
            {
                findings.Add(new
                {
                    type = "MissingEventSystem",
                    severity = "Error",
                    message = "UGUI infrastructure exists but EventSystem is missing."
                });
            }

            var definedTags = new HashSet<string>(InternalEditorUtility.tags, StringComparer.OrdinalIgnoreCase);
            foreach (var requiredTag in requiredTags)
            {
                if (!definedTags.Contains(requiredTag))
                {
                    findings.Add(new
                    {
                        type = "MissingTagDefinition",
                        severity = "Warning",
                        name = requiredTag,
                        message = $"Required tag '{requiredTag}' is not defined in TagManager."
                    });
                }
            }

            var definedLayers = new HashSet<string>(InternalEditorUtility.layers, StringComparer.OrdinalIgnoreCase);
            foreach (var requiredLayer in requiredLayers)
            {
                if (!definedLayers.Contains(requiredLayer))
                {
                    findings.Add(new
                    {
                        type = "MissingLayerDefinition",
                        severity = "Warning",
                        name = requiredLayer,
                        message = $"Required layer '{requiredLayer}' is not defined in TagManager."
                    });
                }
            }

            var uniqueFindings = DeduplicateFindings(findings);
            return new
            {
                success = true,
                sceneName = metrics.Scene.name,
                checkedRoots = requiredRoots,
                checkedTags = requiredTags,
                checkedLayers = requiredLayers,
                summary = new
                {
                    passed = uniqueFindings.Count == 0,
                    errors = uniqueFindings.Count(f => GetPropertyValue<string>(f, "severity", "Info") == "Error"),
                    warnings = uniqueFindings.Count(f => GetPropertyValue<string>(f, "severity", "Info") == "Warning"),
                    info = uniqueFindings.Count(f => GetPropertyValue<string>(f, "severity", "Info") == "Info")
                },
                findings = uniqueFindings.ToArray()
            };
        }

        [UnitySkill("project_stack_detect", "Detect the current project's render pipeline, UI route, input system, major packages, and common folder conventions.",
            Category = SkillCategory.Perception, Operation = SkillOperation.Analyze,
            Tags = new[] { "project", "stack", "detect", "pipeline", "packages" },
            Outputs = new[] { "unityVersion", "renderPipeline", "ui", "input", "packages", "projectProfile" },
            ReadOnly = true)]
        public static object ProjectStackDetect()
        {
            var metrics = CollectSceneMetrics(includeComponentStats: true);
            var packageIds = ReadInstalledPackageIds();
            var hasUiToolkitAssets = AssetDatabase.FindAssets("t:VisualTreeAsset", new[] { "Assets" }).Length > 0
                || AssetDatabase.FindAssets("t:PanelSettings", new[] { "Assets" }).Length > 0;

            var cinemachineDetected = packageIds.Contains("com.unity.cinemachine")
                || FindTypeInAssemblies("Cinemachine.CinemachineBrain") != null
                || FindTypeInAssemblies("Unity.Cinemachine.CinemachineBrain") != null;
            var timelineDetected = packageIds.Contains("com.unity.timeline")
                || AssetDatabase.FindAssets("t:TimelineAsset", new[] { "Assets" }).Length > 0;
            var navMeshDetected = packageIds.Contains("com.unity.ai.navigation")
                || FindTypeInAssemblies("Unity.AI.Navigation.NavMeshSurface") != null;
            var xrDetected = packageIds.Contains("com.unity.xr.interaction.toolkit")
                || packageIds.Contains("com.unity.xr.management")
                || FindTypeInAssemblies("UnityEngine.XR.Interaction.Toolkit.XRInteractionManager") != null;
            var proBuilderDetected = packageIds.Contains("com.unity.probuilder")
                || FindTypeInAssemblies("UnityEngine.ProBuilder.ProBuilderMesh") != null;
            var inputSystemDetected = packageIds.Contains("com.unity.inputsystem");
            var uiRoute = DetermineUiRoute(metrics, hasUiToolkitAssets);
            var inputHandling = DetectInputHandling(packageIds);
            var testAsmdefs = AssetDatabase.FindAssets("t:AssemblyDefinitionAsset", new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrEmpty(path))
                .ToArray();

            return new
            {
                success = true,
                unityVersion = Application.unityVersion,
                renderPipeline = new
                {
                    type = ProjectSkills.DetectRenderPipeline().ToString(),
                    defaultShader = ProjectSkills.GetDefaultShaderName(),
                    unlitShader = ProjectSkills.GetUnlitShaderName()
                },
                input = new
                {
                    mode = inputHandling,
                    inputSystemInstalled = inputSystemDetected,
                    legacyInputManagerAvailable = !inputSystemDetected || inputHandling.IndexOf("Both", StringComparison.OrdinalIgnoreCase) >= 0
                },
                ui = new
                {
                    route = uiRoute,
                    uguiDetected = metrics.Canvases > 0 || metrics.HasUiGraphic || packageIds.Contains("com.unity.ugui"),
                    uiToolkitDetected = metrics.HasUiToolkitDocument || hasUiToolkitAssets
                },
                packages = new
                {
                    cinemachine = cinemachineDetected,
                    timeline = timelineDetected,
                    navMesh = navMeshDetected,
                    xr = xrDetected,
                    proBuilder = proBuilderDetected,
                    inputSystem = inputSystemDetected
                },
                tests = new
                {
                    detected = Directory.Exists("Assets/Tests") || testAsmdefs.Any(path => Path.GetFileNameWithoutExtension(path).IndexOf("Test", StringComparison.OrdinalIgnoreCase) >= 0),
                    nunitLoaded = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name.IndexOf("nunit", StringComparison.OrdinalIgnoreCase) >= 0)
                },
                projectFolders = new
                {
                    scripts = Directory.Exists("Assets/Scripts"),
                    scenes = Directory.Exists("Assets/Scenes"),
                    prefabs = Directory.Exists("Assets/Prefabs"),
                    materials = Directory.Exists("Assets/Materials"),
                    tests = Directory.Exists("Assets/Tests")
                },
                projectProfile = DetermineProjectProfile(metrics, xrDetected, uiRoute)
            };
        }

        [UnitySkill("scene_analyze", "Analyze the active scene and project context in one pass. Returns summary, health findings, stack detection, recommendations, and suggested next skills.",
            Category = SkillCategory.Perception, Operation = SkillOperation.Analyze,
            Tags = new[] { "scene", "analyze", "overview", "context", "recommendations" },
            Outputs = new[] { "summary", "stats", "findings", "recommendations", "suggestedNextSkills" },
            ReadOnly = true)]
        public static object SceneAnalyze(int topComponentsLimit = 10, int issueLimit = 100, int deepHierarchyThreshold = 8, int largeChildCountThreshold = 25)
        {
            var metrics = CollectSceneMetrics(includeComponentStats: true);
            var componentStats = SceneComponentStats(topComponentsLimit);
            var health = SceneHealthCheck(issueLimit, deepHierarchyThreshold, largeChildCountThreshold);
            var contract = SceneContractValidate();
            var stack = ProjectStackDetect();

            var allFindings = new List<object>();
            allFindings.AddRange(GetEnumerableProperty(health, "findings"));
            allFindings.AddRange(GetEnumerableProperty(contract, "findings"));
            var dedupedFindings = DeduplicateFindings(allFindings);
            var warnings = dedupedFindings
                .Where(f => GetPropertyValue<string>(f, "severity", "Info") != "Error")
                .Take(Math.Min(20, dedupedFindings.Count))
                .ToArray();

            var recommendations = BuildSuggestedNextSkills(dedupedFindings);
            var projectProfile = GetPropertyValue<string>(stack, "projectProfile", "Unknown");
            var errorCount = dedupedFindings.Count(f => GetPropertyValue<string>(f, "severity", "Info") == "Error");
            var warningCount = dedupedFindings.Count(f => GetPropertyValue<string>(f, "severity", "Info") == "Warning");

            return new
            {
                success = true,
                sceneName = metrics.Scene.name,
                summary = new
                {
                    headline = $"{projectProfile} scene with {metrics.TotalObjects} objects, {errorCount} errors, and {warningCount} warnings detected.",
                    projectProfile,
                    totalObjects = metrics.TotalObjects,
                    activeObjects = metrics.ActiveObjects,
                    errors = errorCount,
                    warnings = warningCount
                },
                stats = GetPropertyValue(componentStats, "stats"),
                findings = dedupedFindings.ToArray(),
                warnings,
                recommendations = recommendations.ToArray(),
                suggestedNextSkills = recommendations.Select(r => new
                {
                    skill = GetPropertyValue<string>(r, "skill", null),
                    reason = GetPropertyValue<string>(r, "reason", null)
                }).ToArray(),
                componentStats,
                health,
                contract,
                stack
            };
        }

        [UnitySkill("scene_summarize", "Get a structured summary of the current scene (object counts, component stats, hierarchy depth)",
            Category = SkillCategory.Perception, Operation = SkillOperation.Analyze,
            Tags = new[] { "scene", "summary", "statistics", "overview" },
            Outputs = new[] { "sceneName", "scenePath", "stats", "topComponents" },
            ReadOnly = true)]
        public static object SceneSummarize(bool includeComponentStats = true, int topComponentsLimit = 10)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var allObjects = GameObjectFinder.GetSceneObjects();
            var rootObjects = scene.GetRootGameObjects();
            var componentBuffer = new List<Component>(8);

            int totalObjects = allObjects.Count;
            int activeObjects = 0;
            int maxDepth = 0;
            int lightCount = 0, cameraCount = 0, canvasCount = 0;
            var componentCounts = new Dictionary<string, int>();

            foreach (var go in allObjects)
            {
                if (go.activeInHierarchy) activeObjects++;

                // Get depth from the request-level hierarchy cache.
                int depth = GameObjectFinder.GetDepth(go);
                if (depth > maxDepth) maxDepth = depth;

                // Count components in single pass
                componentBuffer.Clear();
                go.GetComponents(componentBuffer);
                foreach (var comp in componentBuffer)
                {
                    if (comp == null) continue;
                    var typeName = comp.GetType().Name;

                    // Count key types inline
                    if (comp is Light) lightCount++;
                    else if (comp is Camera) cameraCount++;
                    else if (comp is Canvas) canvasCount++;

                    if (includeComponentStats)
                    {
                        if (!componentCounts.ContainsKey(typeName))
                            componentCounts[typeName] = 0;
                        componentCounts[typeName]++;
                    }
                }
            }

            componentCounts.Remove("Transform");
            var topComponents = componentCounts
                .OrderByDescending(kv => kv.Value)
                .Take(topComponentsLimit)
                .Select(kv => (object)new { component = kv.Key, count = kv.Value })
                .ToArray();

            return new
            {
                success = true,
                sceneName = scene.name,
                scenePath = scene.path,
                isDirty = scene.isDirty,
                stats = new
                {
                    totalObjects,
                    activeObjects,
                    inactiveObjects = totalObjects - activeObjects,
                    rootObjects = rootObjects.Length,
                    maxHierarchyDepth = maxDepth,
                    lights = lightCount,
                    cameras = cameraCount,
                    canvases = canvasCount
                },
                topComponents
            };
        }

        [UnitySkill("hierarchy_describe", "Get a text tree of the scene hierarchy (like 'tree' command). Returns human-readable text. For JSON structure use scene_get_hierarchy.",
            Category = SkillCategory.Perception, Operation = SkillOperation.Query,
            Tags = new[] { "hierarchy", "tree", "scene", "structure" },
            Outputs = new[] { "sceneName", "hierarchy", "totalObjectsShown" },
            ReadOnly = true)]
        public static object HierarchyDescribe(int maxDepth = 5, bool includeInactive = false, int maxItemsPerLevel = 20)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var rootObjects = scene.GetRootGameObjects()
                .Where(g => includeInactive || g.activeInHierarchy)
                .OrderBy(g => g.transform.GetSiblingIndex())
                .Take(maxItemsPerLevel)
                .ToArray();

            var sb = new StringBuilder();
            sb.AppendLine($"Scene: {scene.name}");
            sb.AppendLine("─".PadRight(40, '─'));

            int totalShown = 0;
            var componentBuffer = new List<Component>(8);
            foreach (var root in rootObjects)
            {
                BuildHierarchyTree(sb, root.transform, 0, maxDepth, includeInactive, maxItemsPerLevel, ref totalShown, componentBuffer);
            }

            var allRoots = scene.GetRootGameObjects();
            if (allRoots.Length > maxItemsPerLevel)
            {
                sb.AppendLine($"... and {allRoots.Length - maxItemsPerLevel} more root objects");
            }

            return new
            {
                success = true,
                sceneName = scene.name,
                hierarchy = sb.ToString(),
                totalObjectsShown = totalShown
            };
        }

        private static void BuildHierarchyTree(StringBuilder sb, Transform t, int depth, int maxDepth, bool includeInactive, int maxItems, ref int total, List<Component> componentBuffer)
        {
            if (depth > maxDepth) return;
            if (!includeInactive && !t.gameObject.activeInHierarchy) return;

            total++;
            string indent = new string(' ', depth * 2);
            string prefix = depth == 0 ? "► " : "├─";
            string activeMarker = t.gameObject.activeSelf ? "" : " [inactive]";
            string componentHint = GetComponentHint(t.gameObject, componentBuffer);

            sb.AppendLine($"{indent}{prefix} {t.name}{componentHint}{activeMarker}");

            int childrenShown = 0;
            foreach (Transform child in t)
            {
                if (childrenShown >= maxItems)
                {
                    sb.AppendLine($"{indent}  ... and {t.childCount - childrenShown} more children");
                    break;
                }
                BuildHierarchyTree(sb, child, depth + 1, maxDepth, includeInactive, maxItems, ref total, componentBuffer);
                childrenShown++;
            }
        }

        private static string GetComponentHint(GameObject go, List<Component> componentBuffer)
        {
            componentBuffer.Clear();
            go.GetComponents(componentBuffer);

            bool hasCamera = false;
            bool hasLight = false;
            bool hasCanvas = false;
            bool hasButton = false;
            bool hasAnimator = false;
            bool hasAudioSource = false;
            bool hasParticleSystem = false;
            bool hasCollider = false;
            bool hasRigidbody = false;
            bool hasSkinnedMeshRenderer = false;
            bool hasMeshRenderer = false;
            bool hasSpriteRenderer = false;
            bool hasUiGraphic = false;

            foreach (var component in componentBuffer)
            {
                if (component == null)
                    continue;

                if (component is Camera) hasCamera = true;
                else if (component is Light) hasLight = true;
                else if (component is Canvas) hasCanvas = true;
                else if (component is UnityEngine.UI.Button) hasButton = true;
                else if (component is Animator) hasAnimator = true;
                else if (component is AudioSource) hasAudioSource = true;
                else if (component is ParticleSystem) hasParticleSystem = true;
                else if (component is Collider || component is Collider2D) hasCollider = true;
                else if (component is Rigidbody || component is Rigidbody2D) hasRigidbody = true;
                else if (component is SkinnedMeshRenderer) hasSkinnedMeshRenderer = true;
                else if (component is MeshRenderer) hasMeshRenderer = true;
                else if (component is SpriteRenderer) hasSpriteRenderer = true;
                else if (component is UnityEngine.UI.Text || component is UnityEngine.UI.Image) hasUiGraphic = true;
            }

            if (hasCamera) return " [Camera]";
            if (hasLight) return " [Light]";
            if (hasCanvas) return " [Canvas]";
            if (hasButton) return " [Button]";
            if (hasAnimator) return " [Animator]";
            if (hasAudioSource) return " [AudioSource]";
            if (hasParticleSystem) return " [ParticleSystem]";
            if (hasCollider) return " [Collider]";
            if (hasRigidbody) return " [Rigidbody]";
            if (hasSkinnedMeshRenderer) return " [SkinnedMeshRenderer]";
            if (hasMeshRenderer) return " [MeshRenderer]";
            if (hasSpriteRenderer) return " [SpriteRenderer]";
            if (hasUiGraphic) return " [UI]";
            return "";
        }

        private static string GetComponentHintLegacy(Transform t)
        {
            if (t.GetComponent<Camera>()) return " 📷";
            if (t.GetComponent<Light>()) return " 💡";
            if (t.GetComponent<Canvas>()) return " 🖼";
            if (t.GetComponent<UnityEngine.UI.Button>()) return " 🔘";
            if (t.GetComponent<Animator>()) return " 🎬";
            if (t.GetComponent<AudioSource>()) return " 🔊";
            if (t.GetComponent<ParticleSystem>()) return " ✨";
            if (t.GetComponent<Collider>() || t.GetComponent<Collider2D>()) return " 🧱";
            if (t.GetComponent<Rigidbody>() || t.GetComponent<Rigidbody2D>()) return " ⚙";
            if (t.GetComponent<SkinnedMeshRenderer>()) return " 🦴";
            if (t.GetComponent<MeshRenderer>()) return " ▣";
            if (t.GetComponent<SpriteRenderer>()) return " 🖾";
            if (t.GetComponent<UnityEngine.UI.Text>() || t.GetComponent<UnityEngine.UI.Image>()) return " 🎨";
            return "";
        }

        [UnitySkill("script_analyze", "Analyze a script's public API (MonoBehaviour, ScriptableObject, or plain class)",
            Category = SkillCategory.Perception, Operation = SkillOperation.Analyze,
            Tags = new[] { "script", "analyze", "api", "reflection" },
            Outputs = new[] { "script", "fullName", "kind", "fields", "properties", "methods", "unityCallbacks" },
            RequiresInput = new[] { "scriptName" },
            ReadOnly = true)]
        public static object ScriptAnalyze(string scriptName, bool includePrivate = false)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                .FirstOrDefault(t => t.Name.Equals(scriptName, StringComparison.OrdinalIgnoreCase) &&
                                     (typeof(MonoBehaviour).IsAssignableFrom(t) ||
                                      typeof(ScriptableObject).IsAssignableFrom(t) ||
                                      (t.IsClass && !t.IsAbstract && t.Namespace != null &&
                                       !t.Namespace.StartsWith("Unity") && !t.Namespace.StartsWith("System"))));

            if (type == null)
            {
                return new { success = false, error = $"Script '{scriptName}' not found (searched MonoBehaviour, ScriptableObject, and user classes)" };
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
            if (includePrivate) flags |= BindingFlags.NonPublic;

            var fields = type.GetFields(flags)
                .Where(f => !f.Name.StartsWith("<"))
                .Select(f => new
                {
                    name = f.Name,
                    type = GetFriendlyTypeName(f.FieldType),
                    isSerializable = f.IsPublic || f.GetCustomAttribute<SerializeField>() != null
                })
                .ToList();

            var properties = type.GetProperties(flags)
                .Where(p => p.CanRead)
                .Select(p => new
                {
                    name = p.Name,
                    type = GetFriendlyTypeName(p.PropertyType),
                    canWrite = p.CanWrite
                })
                .ToList();

            var methods = type.GetMethods(flags)
                .Where(m => !m.IsSpecialName)
                .Select(m => new
                {
                    name = m.Name,
                    returnType = GetFriendlyTypeName(m.ReturnType),
                    parameters = string.Join(", ", m.GetParameters().Select(p => $"{GetFriendlyTypeName(p.ParameterType)} {p.Name}"))
                })
                .ToList();

            // Unity callbacks only for MonoBehaviour
            List<string> unityEvents = null;
            if (typeof(MonoBehaviour).IsAssignableFrom(type))
            {
                unityEvents = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(m => UnityCallbacks.Contains(m.Name))
                    .Select(m => m.Name)
                    .ToList();
            }

            string scriptKind = typeof(MonoBehaviour).IsAssignableFrom(type) ? "MonoBehaviour"
                : typeof(ScriptableObject).IsAssignableFrom(type) ? "ScriptableObject"
                : "Class";

            return new
            {
                success = true,
                script = scriptName,
                fullName = type.FullName,
                kind = scriptKind,
                baseClass = type.BaseType?.Name,
                fields,
                properties,
                methods,
                unityCallbacks = unityEvents
            };
        }

        [UnitySkill("scene_spatial_query", "Find objects within a radius of a point, or near another object",
            Category = SkillCategory.Perception, Operation = SkillOperation.Query,
            Tags = new[] { "spatial", "radius", "proximity", "search" },
            Outputs = new[] { "center", "radius", "totalFound", "results" },
            ReadOnly = true)]
        public static object SceneSpatialQuery(
            float x = 0, float y = 0, float z = 0,
            float radius = 10f,
            string nearObject = null,
            string componentFilter = null,
            int maxResults = 50)
        {
            Vector3 center;

            if (!string.IsNullOrEmpty(nearObject))
            {
                var go = GameObjectFinder.Find(nearObject);
                if (go == null)
                    return new { success = false, error = $"Object '{nearObject}' not found" };
                center = go.transform.position;
            }
            else
            {
                center = new Vector3(x, y, z);
            }

            var allObjects = GameObjectFinder.GetSceneObjects();
            float radiusSq = radius * radius;

            Type filterType = string.IsNullOrEmpty(componentFilter)
                ? null
                : ComponentSkills.FindComponentType(componentFilter);

            var found = new List<(float dist, object info)>();
            foreach (var go in allObjects)
            {
                if (filterType != null && go.GetComponent(filterType) == null) continue;

                var pos = go.transform.position;
                float distSq = (pos - center).sqrMagnitude;
                if (distSq <= radiusSq)
                {
                    float dist = Mathf.Sqrt(distSq);
                    found.Add((dist, new
                    {
                        name = go.name,
                        path = GameObjectFinder.GetCachedPath(go),
                        distance = dist,
                        position = new { x = pos.x, y = pos.y, z = pos.z }
                    }));
                }
            }

            var results = found.Count <= maxResults
                ? found.Select(f => f.info).ToList()
                : found.OrderBy(f => f.dist).Take(maxResults).Select(f => f.info).ToList();

            return new
            {
                success = true,
                center = new { x = center.x, y = center.y, z = center.z },
                radius,
                totalFound = found.Count,
                results
            };
        }

        [UnitySkill("scene_materials", "Get an overview of all materials and shaders used in the current scene",
            Category = SkillCategory.Perception, Operation = SkillOperation.Query,
            Tags = new[] { "material", "shader", "scene", "overview" },
            Outputs = new[] { "totalMaterials", "totalShaders", "shaders" },
            ReadOnly = true)]
        public static object SceneMaterials(bool includeProperties = false)
        {
            var renderers = FindHelper.FindAll<Renderer>();
            var materialMap = new Dictionary<string, MaterialInfo>();

            foreach (var renderer in renderers)
            {
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null) continue;
                    var key = mat.GetInstanceID().ToString();
                    if (!materialMap.ContainsKey(key))
                    {
                        materialMap[key] = new MaterialInfo
                        {
                            name = mat.name,
                            shader = mat.shader != null ? mat.shader.name : "null",
                            renderQueue = mat.renderQueue,
                            path = AssetDatabase.GetAssetPath(mat),
                            users = new List<string>()
                        };
                        if (includeProperties && mat.shader != null)
                        {
                            var props = new List<object>();
                            int count = ShaderUtil.GetPropertyCount(mat.shader);
                            for (int i = 0; i < count; i++)
                            {
                                props.Add(new
                                {
                                    name = ShaderUtil.GetPropertyName(mat.shader, i),
                                    type = ShaderUtil.GetPropertyType(mat.shader, i).ToString()
                                });
                            }
                            materialMap[key].properties = props;
                        }
                    }
                    materialMap[key].users.Add(renderer.gameObject.name);
                }
            }

            // Group by shader
            var shaderGroups = materialMap.Values
                .GroupBy(m => m.shader)
                .Select(g => new
                {
                    shader = g.Key,
                    materialCount = g.Count(),
                    materials = g.Select(m => new
                    {
                        m.name, m.path, m.renderQueue,
                        userCount = m.users.Count,
                        users = m.users.Take(5).ToList(),
                        properties = includeProperties ? m.properties : null
                    }).ToList()
                })
                .OrderByDescending(g => g.materialCount)
                .ToList();

            return new
            {
                success = true,
                totalMaterials = materialMap.Count,
                totalShaders = shaderGroups.Count,
                shaders = shaderGroups
            };
        }

        private class MaterialInfo
        {
            public string name, shader, path;
            public int renderQueue;
            public List<string> users;
            public List<object> properties;
        }

        [UnitySkill("scene_context", "Generate a comprehensive scene snapshot for AI coding assistance (hierarchy, components, script fields, references, UI layout). Best for initial context gathering before editing code or complex scene work.",
            Category = SkillCategory.Perception, Operation = SkillOperation.Analyze,
            Tags = new[] { "scene", "context", "snapshot", "comprehensive", "ai" },
            Outputs = new[] { "sceneName", "totalObjects", "exportedObjects", "objects", "references", "codeDependencies" },
            ReadOnly = true)]
        public static object SceneContext(
            int maxDepth = 10,
            int maxObjects = 200,
            string rootPath = null,
            bool includeValues = false,
            bool includeReferences = true,
            bool includeCodeDeps = false)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var totalObjects = GameObjectFinder.GetSceneObjects().Count;
            int scopeObjects;

            // Determine roots
            Transform[] roots;
            if (!string.IsNullOrEmpty(rootPath))
            {
                var rootGo = GameObjectFinder.FindByPath(rootPath);
                if (rootGo == null)
                    return new { success = false, error = $"Root path '{rootPath}' not found" };
                roots = new[] { rootGo.transform };
                scopeObjects = CountSubtreeObjects(rootGo.transform);
            }
            else
            {
                roots = scene.GetRootGameObjects().Select(g => g.transform).ToArray();
                scopeObjects = totalObjects;
            }

            // BFS traversal
            var objects = new List<object>();
            var references = new List<object>();
            var queue = new Queue<(Transform t, int depth)>();
            var componentBuffer = new List<Component>(8);
            var relevantUserScripts = includeCodeDeps ? new HashSet<string>() : null;
            foreach (var r in roots) queue.Enqueue((r, 0));

            while (queue.Count > 0 && objects.Count < maxObjects)
            {
                var (t, depth) = queue.Dequeue();
                objects.Add(BuildObjectInfo(t.gameObject, includeValues, includeReferences, references, componentBuffer, relevantUserScripts));

                if (depth + 1 <= maxDepth)
                {
                    foreach (Transform child in t)
                        queue.Enqueue((child, depth + 1));
                }
            }

            // Optional: code-level dependencies
            List<object> codeDeps = null;
            if (includeCodeDeps)
            {
                codeDeps = CollectCodeDependencies(relevantUserScripts).Select(e => (object)new
                {
                    from = e.fromScript,
                    to = e.toObject,
                    type = e.fieldType,
                    detail = e.fieldName
                }).ToList();
            }

            var result = new
            {
                success = true,
                sceneName = scene.name,
                totalObjects,
                scopeObjects,
                exportedObjects = objects.Count,
                truncated = objects.Count < scopeObjects || queue.Count > 0,
                objects,
                references = includeReferences ? references : null,
                codeDependencies = codeDeps
            };
            return result;
        }

        private static object BuildObjectInfo(GameObject go, bool includeValues, bool includeReferences, List<object> refs, List<Component> componentBuffer, HashSet<string> relevantUserScripts = null)
        {
            var path = GameObjectFinder.GetCachedPath(go);
            var components = new List<object>();

            componentBuffer.Clear();
            go.GetComponents(componentBuffer);
            foreach (var comp in componentBuffer)
            {
                if (comp == null) continue;
                if (relevantUserScripts != null && comp is MonoBehaviour mono && mono != null && IsUserScript(mono.GetType()))
                    relevantUserScripts.Add(mono.GetType().Name);
                components.Add(BuildComponentInfo(comp, path, includeValues, includeReferences, refs));
            }

            var children = new List<string>();
            foreach (Transform child in go.transform)
                children.Add(GameObjectFinder.GetCachedPath(child.gameObject));

            return new
            {
                path,
                name = go.name,
                active = go.activeInHierarchy,
                tag = go.tag,
                layer = LayerMask.LayerToName(go.layer),
                components,
                children
            };
        }

        private static object BuildComponentInfo(Component comp, string objPath, bool includeValues, bool includeReferences, List<object> refs)
        {
            var type = comp.GetType();
            var typeName = type.Name;

            // MonoBehaviour → serialized fields
            if (comp is MonoBehaviour)
            {
                var fields = ExtractSerializedFields(comp, objPath, includeValues, includeReferences, refs);
                return new { type = typeName, kind = "MonoBehaviour", fields };
            }

            // Built-in components: only output props when includeValues is true
            if (includeValues)
            {
                var props = GetBuiltinComponentProps(comp);
                return new { type = typeName, props };
            }
            return new { type = typeName };
        }

        private static readonly HashSet<string> SkipFields = new HashSet<string>
        {
            "m_Script", "m_ObjectHideFlags", "m_CorrespondingSourceObject",
            "m_PrefabInstance", "m_PrefabAsset", "m_GameObject", "m_Enabled"
        };

        private static Dictionary<string, object> ExtractSerializedFields(Component comp, string objPath, bool includeValues, bool includeReferences, List<object> refs)
        {
            var fields = new Dictionary<string, object>();
            var so = new SerializedObject(comp);
            var prop = so.GetIterator();
            bool enterChildren = true;

            while (prop.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (SkipFields.Contains(prop.name)) continue;
                if (prop.propertyType == SerializedPropertyType.ArraySize) continue;

                var fieldType = prop.propertyType.ToString();

                // Always extract ObjectReference refs (independent of includeValues)
                if (prop.propertyType == SerializedPropertyType.ObjectReference && prop.objectReferenceValue != null)
                {
                    var refObj = prop.objectReferenceValue;
                    fieldType = refObj.GetType().Name;
                    string refPath = GetObjectReferencePath(refObj);

                    if (includeReferences && refPath != null)
                        refs.Add(new { from = $"{objPath}:{comp.GetType().Name}.{prop.name}", to = refPath });

                    if (includeValues)
                        fields[prop.name] = new { type = fieldType, value = (object)(refPath ?? refObj.name) };
                    else
                        fields[prop.name] = fieldType;
                    continue;
                }

                if (!includeValues)
                {
                    fields[prop.name] = fieldType;
                    continue;
                }

                // includeValues=true: extract actual values
                object value;
                switch (prop.propertyType)
                {
                    case SerializedPropertyType.Integer: value = prop.intValue; break;
                    case SerializedPropertyType.Float: value = prop.floatValue; break;
                    case SerializedPropertyType.Boolean: value = prop.boolValue; break;
                    case SerializedPropertyType.String:
                        var sv = prop.stringValue;
                        value = sv != null && sv.Length > 100 ? sv.Substring(0, 100) + "..." : sv;
                        break;
                    case SerializedPropertyType.Enum: value = prop.enumDisplayNames != null && prop.enumValueIndex >= 0 && prop.enumValueIndex < prop.enumDisplayNames.Length ? prop.enumDisplayNames[prop.enumValueIndex] : prop.enumValueIndex; break;
                    case SerializedPropertyType.Vector2: value = FormatVec(prop.vector2Value); break;
                    case SerializedPropertyType.Vector3: value = FormatVec(prop.vector3Value); break;
                    case SerializedPropertyType.Vector4: value = FormatVec(prop.vector4Value); break;
                    case SerializedPropertyType.Color: var c = prop.colorValue; value = $"({c.r:F2}, {c.g:F2}, {c.b:F2}, {c.a:F2})"; break;
                    case SerializedPropertyType.ObjectReference: value = "null"; break; // ref is null (non-null handled above)
                    default:
                        value = prop.isArray ? $"{prop.arrayElementType}[{prop.arraySize}]" : fieldType;
                        break;
                }
                fields[prop.name] = new { type = fieldType, value };
            }
            return fields;
        }

        private static string FormatVec(Vector2 v) => $"({v.x}, {v.y})";
        private static string FormatVec(Vector3 v) => $"({v.x}, {v.y}, {v.z})";
        private static string FormatVec(Vector4 v) => $"({v.x}, {v.y}, {v.z}, {v.w})";

        private static Dictionary<string, object> GetBuiltinComponentProps(Component comp)
        {
            var props = new Dictionary<string, object>();

            switch (comp)
            {
                case RectTransform rt:
                    props["anchoredPosition"] = FormatVec(rt.anchoredPosition);
                    props["sizeDelta"] = FormatVec(rt.sizeDelta);
                    props["anchorMin"] = FormatVec(rt.anchorMin);
                    props["anchorMax"] = FormatVec(rt.anchorMax);
                    props["pivot"] = FormatVec(rt.pivot);
                    break;
                case Transform t:
                    props["position"] = FormatVec(t.position);
                    props["rotation"] = FormatVec(t.eulerAngles);
                    props["scale"] = FormatVec(t.localScale);
                    break;
                case Camera cam:
                    props["fieldOfView"] = cam.fieldOfView;
                    props["orthographic"] = cam.orthographic;
                    props["clearFlags"] = cam.clearFlags.ToString();
                    props["cullingMask"] = cam.cullingMask;
                    break;
                case Light light:
                    props["type"] = light.type.ToString();
                    props["color"] = $"({light.color.r:F2}, {light.color.g:F2}, {light.color.b:F2})";
                    props["intensity"] = light.intensity;
                    props["range"] = light.range;
                    break;
                case Renderer rend:
                    props["material"] = rend.sharedMaterial != null ? rend.sharedMaterial.name : "null";
                    props["enabled"] = rend.enabled;
                    break;
                case Canvas canvas:
                    props["renderMode"] = canvas.renderMode.ToString();
                    props["sortingOrder"] = canvas.sortingOrder;
                    break;
                case CanvasGroup cg:
                    props["alpha"] = cg.alpha;
                    props["interactable"] = cg.interactable;
                    props["blocksRaycasts"] = cg.blocksRaycasts;
                    break;
                case UnityEngine.UI.Button btn:
                    props["interactable"] = btn.interactable;
                    props["transition"] = btn.transition.ToString();
                    break;
                case UnityEngine.UI.Text txt:
                    var textVal = txt.text;
                    props["text"] = textVal != null && textVal.Length > 50 ? textVal.Substring(0, 50) + "..." : textVal;
                    props["fontSize"] = txt.fontSize;
                    props["color"] = $"({txt.color.r:F2}, {txt.color.g:F2}, {txt.color.b:F2}, {txt.color.a:F2})";
                    break;
                case UnityEngine.UI.Image img:
                    props["sprite"] = img.sprite != null ? img.sprite.name : "null";
                    props["color"] = $"({img.color.r:F2}, {img.color.g:F2}, {img.color.b:F2}, {img.color.a:F2})";
                    props["raycastTarget"] = img.raycastTarget;
                    break;
                case Animator anim:
                    props["controller"] = anim.runtimeAnimatorController != null ? anim.runtimeAnimatorController.name : "null";
                    props["enabled"] = anim.enabled;
                    break;
                case AudioSource audio:
                    props["clip"] = audio.clip != null ? audio.clip.name : "null";
                    props["playOnAwake"] = audio.playOnAwake;
                    props["loop"] = audio.loop;
                    props["volume"] = audio.volume;
                    break;
                case Collider col:
                    props["isTrigger"] = col.isTrigger;
                    props["enabled"] = col.enabled;
                    break;
                case Collider2D col2d:
                    props["isTrigger"] = col2d.isTrigger;
                    props["enabled"] = col2d.enabled;
                    break;
                case Rigidbody rb:
                    props["mass"] = rb.mass;
                    props["useGravity"] = rb.useGravity;
                    props["isKinematic"] = rb.isKinematic;
                    break;
                default:
                    props["enabled"] = IsComponentEnabled(comp);
                    break;
            }
            return props;
        }

        private static object IsComponentEnabled(Component comp)
        {
            if (comp is Behaviour b) return b.enabled;
            if (comp is Renderer r) return r.enabled;
            if (comp is Collider c) return c.enabled;
            return null;
        }

        [UnitySkill("scene_export_report", "Export complete scene structure and script dependency report as markdown file. Use when user asks to: export scene report, generate scene document, save scene overview, create scene context file",
            Category = SkillCategory.Perception, Operation = SkillOperation.Analyze | SkillOperation.Execute,
            Tags = new[] { "scene", "report", "export", "markdown", "documentation" },
            Outputs = new[] { "savedTo", "objectCount", "userScriptCount", "referenceCount" })]
        public static object SceneExportReport(
            string savePath = "Assets/Docs/SceneReport.md",
            int maxDepth = 10,
            int maxObjects = 500)
        {
            if (Validate.SafePath(savePath, "savePath") is object pathErr0) return pathErr0;

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects().Select(g => g.transform).ToArray();

            // BFS collect objects
            var objList = new List<(GameObject go, int depth)>();
            var queue = new Queue<(Transform t, int depth)>();
            foreach (var r in roots) queue.Enqueue((r, 0));
            while (queue.Count > 0 && objList.Count < maxObjects)
            {
                var (t, depth) = queue.Dequeue();
                objList.Add((t.gameObject, depth));
                if (depth + 1 <= maxDepth)
                    foreach (Transform child in t) queue.Enqueue((child, depth + 1));
            }

            // Collect serialized reference edges
            var allObjects = new GameObject[objList.Count];
            for (int i = 0; i < objList.Count; i++)
                allObjects[i] = objList[i].go;
            var edges = CollectDependencyEdges(allObjects);

            // Collect C# code-level dependencies
            var codeEdges = CollectCodeDependencies();

            // Merge all edges
            var allEdges = new List<DependencyEdge>(edges);
            allEdges.AddRange(codeEdges);
            var reverseIndex = allEdges.GroupBy(e => e.toObject).ToDictionary(g => g.Key, g => g.ToList());

            // Build markdown
            var sb = new StringBuilder();
            sb.AppendLine($"# Scene Report: {scene.name}");
            int userScriptCount = 0;
            var userMonos = new List<(string objPath, MonoBehaviour mb)>();
            var componentBuffer = new List<Component>(8);
            var componentNamesBuilder = new StringBuilder(64);
            foreach (var (go, _) in objList)
            {
                componentBuffer.Clear();
                go.GetComponents(componentBuffer);
                foreach (var component in componentBuffer)
                {
                    if (component is MonoBehaviour mono && mono != null && IsUserScript(mono.GetType()))
                    {
                        userScriptCount++;
                        userMonos.Add((GameObjectFinder.GetCachedPath(go), mono));
                    }
                }
            }
            sb.AppendLine($"> Generated: {DateTime.Now:yyyy-MM-dd HH:mm} | Objects: {objList.Count} | User Scripts: {userScriptCount} | References: {allEdges.Count}");
            sb.AppendLine();

            // Hierarchy section — built-in components: name only; user scripts: name*
            sb.AppendLine("## Hierarchy");
            sb.AppendLine();
            foreach (var (go, depth) in objList)
            {
                var indent = new string(' ', depth * 2);
                componentBuffer.Clear();
                go.GetComponents(componentBuffer);
                componentNamesBuilder.Clear();
                bool isFirstComponent = true;
                foreach (var component in componentBuffer)
                {
                    if (component == null || component is Transform)
                        continue;

                    var typeName = component.GetType().Name;
                    if (component is MonoBehaviour mono && mono != null && IsUserScript(mono.GetType()))
                        typeName += "*";

                    if (!isFirstComponent)
                        componentNamesBuilder.Append(", ");
                    componentNamesBuilder.Append(typeName);
                    isFirstComponent = false;
                }

                var compStr = componentNamesBuilder.ToString();
                sb.AppendLine($"{indent}{go.name}{(compStr.Length > 0 ? $" [{compStr}]" : "")}");
            }
            sb.AppendLine();

            // Script Fields section — only user scripts, with values
            if (userMonos.Count > 0)
            {
                sb.AppendLine("## Script Fields");
                sb.AppendLine();
                foreach (var (objPath, mb) in userMonos)
                {
                    sb.AppendLine($"### {mb.GetType().Name} (on: {objPath})");
                    sb.AppendLine();
                    sb.AppendLine("| Field | Type | Value |");
                    sb.AppendLine("|-------|------|-------|");
                    var so = new SerializedObject(mb);
                    var prop = so.GetIterator();
                    bool enter = true;
                    while (prop.NextVisible(enter))
                    {
                        enter = false;
                        if (SkipFields.Contains(prop.name) || prop.propertyType == SerializedPropertyType.ArraySize) continue;
                        string ft = prop.propertyType.ToString();
                        string val = "";
                        if (prop.propertyType == SerializedPropertyType.ObjectReference)
                        {
                            if (prop.objectReferenceValue != null)
                            {
                                var refObj = prop.objectReferenceValue;
                                ft = refObj.GetType().Name;
                                val = GetObjectReferencePath(refObj) ?? refObj.name;
                            }
                            else val = "null";
                        }
                        else val = GetSerializedValue(prop);
                        sb.AppendLine($"| {prop.name} | {ft} | {val} |");
                    }
                    sb.AppendLine();
                }
            }

            // Code Dependencies section
            if (codeEdges.Count > 0)
            {
                sb.AppendLine("## Code Dependencies (C# source analysis)");
                sb.AppendLine();
                var byFrom = codeEdges.GroupBy(e => e.fromScript);
                foreach (var g in byFrom.OrderBy(g => g.Key))
                {
                    sb.AppendLine($"### {g.Key}");
                    foreach (var e in g)
                        sb.AppendLine($"- → **{e.toObject}** via `{e.fieldName}` ({e.fieldType})");
                    sb.AppendLine();
                }
            }

            // Dependency Graph section (merged: serialized + code)
            if (allEdges.Count > 0)
            {
                sb.AppendLine("## Dependency Graph");
                sb.AppendLine();
                sb.AppendLine("| From | To | Type | Source | Detail |");
                sb.AppendLine("|------|----|------|--------|--------|");
                foreach (var e in allEdges.OrderBy(e => e.fromObject).ThenBy(e => e.toObject))
                    sb.AppendLine($"| {e.fromScript} | {e.toObject} | {e.fieldType} | {e.source} | {e.fieldName} |");
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine($"*Generated: {DateTime.Now:yyyy-MM-dd HH:mm}*");

            // Save
            var dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(savePath, sb.ToString(), new UTF8Encoding(false));
            AssetDatabase.ImportAsset(savePath);

            return new
            {
                success = true,
                savedTo = savePath,
                objectCount = objList.Count,
                userScriptCount,
                referenceCount = allEdges.Count,
                codeReferenceCount = codeEdges.Count
            };
        }

        private static bool IsUserScript(Type type)
        {
            if (type == null) return false;
            var ns = type.Namespace;
            if (string.IsNullOrEmpty(ns)) return true; // no namespace = user script
            return !ns.StartsWith("UnityEngine") && !ns.StartsWith("Unity.") &&
                   !ns.StartsWith("TMPro") && !ns.StartsWith("UnityEditor");
        }

        private static string GetSerializedValue(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer: return prop.intValue.ToString();
                case SerializedPropertyType.Float: return prop.floatValue.ToString("G4");
                case SerializedPropertyType.Boolean: return prop.boolValue.ToString();
                case SerializedPropertyType.String:
                    var s = prop.stringValue;
                    return s != null && s.Length > 60 ? s.Substring(0, 57) + "..." : (s ?? "");
                case SerializedPropertyType.Enum:
                    return prop.enumDisplayNames != null && prop.enumValueIndex >= 0 && prop.enumValueIndex < prop.enumDisplayNames.Length
                        ? prop.enumDisplayNames[prop.enumValueIndex] : prop.enumValueIndex.ToString();
                case SerializedPropertyType.Vector2: return FormatVec(prop.vector2Value);
                case SerializedPropertyType.Vector3: return FormatVec(prop.vector3Value);
                case SerializedPropertyType.Vector4: return FormatVec(prop.vector4Value);
                case SerializedPropertyType.Color: var c = prop.colorValue; return $"({c.r:F2},{c.g:F2},{c.b:F2},{c.a:F2})";
                case SerializedPropertyType.LayerMask: return prop.intValue.ToString();
                default:
                    return prop.isArray ? $"{prop.arrayElementType}[{prop.arraySize}]" : prop.propertyType.ToString();
            }
        }

        private static string GetObjectReferencePath(UnityEngine.Object referenceObject)
        {
            if (referenceObject is GameObject referenceGameObject)
                return GameObjectFinder.GetCachedPath(referenceGameObject);
            if (referenceObject is Component referenceComponent)
                return GameObjectFinder.GetCachedPath(referenceComponent.gameObject);
            return null;
        }

        private static int CountSubtreeObjects(Transform root)
        {
            int count = 0;
            var stack = new Stack<Transform>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                count++;
                foreach (Transform child in current)
                    stack.Push(child);
            }

            return count;
        }

        // Regex patterns for C# code-level dependency detection
        private static readonly System.Text.RegularExpressions.Regex RxGetComponent =
            new System.Text.RegularExpressions.Regex(@"(?:Get|Add)Component(?:InChildren|InParent|s)?<(\w+)>", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxFindObject =
            new System.Text.RegularExpressions.Regex(@"FindObject(?:OfType|sOfType|sByType)?<(\w+)>", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxSendMessage =
            new System.Text.RegularExpressions.Regex(@"(?:SendMessage|BroadcastMessage)\s*\(\s*""(\w+)""", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxFieldRef =
            new System.Text.RegularExpressions.Regex(@"(?:public|private|protected|\[SerializeField\])\s+(\w+)\s+\w+\s*[;=]", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxSingleton =
            new System.Text.RegularExpressions.Regex(@"([A-Z]\w+)\s*\.\s*[Ii]nstance\b", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxStaticAccess =
            new System.Text.RegularExpressions.Regex(@"([A-Z]\w+)\s*\.\s*[A-Z]\w*\s*[\(;,\)]", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxNewInstance =
            new System.Text.RegularExpressions.Regex(@"new\s+(\w+)\s*\(", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxGenericArg =
            new System.Text.RegularExpressions.Regex(@"<(\w+)>\s*\(", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxInheritance =
            new System.Text.RegularExpressions.Regex(@"class\s+(\w+)\s*:\s*([\w\s,]+?)\s*\{", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxTypeCheck =
            new System.Text.RegularExpressions.Regex(@"(?:typeof|is|as)\s*[\(<]\s*(\w+)\s*[\)>]?", System.Text.RegularExpressions.RegexOptions.Compiled);
        // Matches strings (group 1) OR comments (group 2/3). Strings are kept, comments replaced.
        private static readonly System.Text.RegularExpressions.Regex RxComment =
            new System.Text.RegularExpressions.Regex(@"(""(?:[^""\\]|\\.)*"")|(/\*[\s\S]*?\*/)|(//.*?$)",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Multiline);
        private static readonly System.Text.RegularExpressions.Regex RxMethodDecl =
            new System.Text.RegularExpressions.Regex(@"(?:(?:public|private|protected|internal|static|virtual|override|abstract|async)\s+)*(?:void|bool|int|float|string|IEnumerator|object|[A-Z]\w*)\s+([A-Z]\w*)\s*\(", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static List<DependencyEdge> CollectCodeDependenciesLegacy()
        {
            var edges = new List<DependencyEdge>();
            var scriptGuids = AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets" });

            // Collect all user class names (MonoBehaviour, ScriptableObject, plain classes, etc.)
            var userClassNames = new HashSet<string>();
            var userScriptPaths = new List<(string path, string className)>();
            foreach (var guid in scriptGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (script == null) continue;
                var scriptClass = script.GetClass();
                if (scriptClass != null && IsUserScript(scriptClass))
                {
                    userClassNames.Add(scriptClass.Name);
                    userScriptPaths.Add((path, scriptClass.Name));
                }
            }
            if (userClassNames.Count == 0) return edges;

            foreach (var (path, className) in userScriptPaths)
            {
                string rawSource;
                try { rawSource = File.ReadAllText(path, System.Text.Encoding.UTF8); } catch { continue; }

                // Strip comments to avoid false positives (preserve string literals, keep char offsets)
                var source = RxComment.Replace(rawSource, m => m.Groups[1].Success ? m.Value : new string(' ', m.Length));

                // Build line→method index for method-level granularity
                var methodAtLine = BuildMethodIndex(source);

                void AddEdge(string target, string pattern, string edgeType, int charIndex)
                {
                    if (target != className && userClassNames.Contains(target))
                    {
                        var method = GetMethodAtPos(source, charIndex, methodAtLine);
                        var location = method != null ? $"{className}.{method}" : className;
                        edges.Add(new DependencyEdge { fromObject = className, fromScript = location, fieldName = pattern, fieldType = edgeType, toObject = target, source = "code" });
                    }
                }

                // GetComponent<T> / GetComponentInChildren<T>
                foreach (System.Text.RegularExpressions.Match m in RxGetComponent.Matches(source))
                    AddEdge(m.Groups[1].Value, m.Value, "GetComponent", m.Index);

                // FindObjectOfType<T>
                foreach (System.Text.RegularExpressions.Match m in RxFindObject.Matches(source))
                    AddEdge(m.Groups[1].Value, m.Value, "FindObject", m.Index);

                // SendMessage / BroadcastMessage
                foreach (System.Text.RegularExpressions.Match m in RxSendMessage.Matches(source))
                    AddEdge(m.Groups[1].Value, m.Value, "Message", m.Index);

                // Field referencing other user classes
                foreach (System.Text.RegularExpressions.Match m in RxFieldRef.Matches(source))
                    AddEdge(m.Groups[1].Value, $"field:{m.Groups[1].Value}", "FieldReference", m.Index);

                // Singleton access: ClassName.Instance (PascalCase only)
                foreach (System.Text.RegularExpressions.Match m in RxSingleton.Matches(source))
                    AddEdge(m.Groups[1].Value, $"{m.Groups[1].Value}.Instance", "Singleton", m.Index);

                // Static member access: PascalCase.PascalCase (both sides must start uppercase)
                foreach (System.Text.RegularExpressions.Match m in RxStaticAccess.Matches(source))
                    AddEdge(m.Groups[1].Value, m.Value.TrimEnd('(', ';', ',', ')').Trim(), "StaticAccess", m.Index);

                // new ClassName()
                foreach (System.Text.RegularExpressions.Match m in RxNewInstance.Matches(source))
                    AddEdge(m.Groups[1].Value, $"new {m.Groups[1].Value}()", "Instantiation", m.Index);

                // Generic type argument: SomeMethod<ClassName>()
                foreach (System.Text.RegularExpressions.Match m in RxGenericArg.Matches(source))
                    AddEdge(m.Groups[1].Value, m.Value.TrimEnd('('), "GenericArg", m.Index);

                // Inheritance: class X : BaseClass, IInterface (Matches for multi-class files)
                foreach (System.Text.RegularExpressions.Match inhMatch in RxInheritance.Matches(source))
                {
                    var declaredClass = inhMatch.Groups[1].Value;
                    foreach (var baseType in inhMatch.Groups[2].Value.Split(','))
                    {
                        var trimmed = baseType.Trim();
                        if (trimmed != declaredClass && userClassNames.Contains(trimmed))
                            edges.Add(new DependencyEdge { fromObject = declaredClass, fromScript = declaredClass, fieldName = $"extends:{trimmed}", fieldType = "Inheritance", toObject = trimmed, source = "code" });
                    }
                }

                // typeof(T) / is T / as T
                foreach (System.Text.RegularExpressions.Match m in RxTypeCheck.Matches(source))
                    AddEdge(m.Groups[1].Value, m.Value.Trim(), "TypeCheck", m.Index);
            }

            // Deduplicate
            return edges.GroupBy(e => $"{e.fromScript}→{e.toObject}:{e.fieldName}")
                .Select(g => g.First()).ToList();
        }

        private static CodeDependencyCache GetOrBuildCodeDependencyInventory()
        {
            if (_codeDependencyCache != null && !_codeDependencyCacheDirty)
                return _codeDependencyCache;

            var cache = new CodeDependencyCache();
            var scriptGuids = AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets" });
            foreach (var guid in scriptGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (script == null) continue;

                var scriptClass = script.GetClass();
                if (scriptClass == null || !IsUserScript(scriptClass))
                    continue;

                cache.UserClassNames.Add(scriptClass.Name);
                cache.Scripts.Add(new CodeDependencyScriptInfo
                {
                    Path = path,
                    ClassName = scriptClass.Name
                });
            }

            _codeDependencyCache = cache;
            _codeDependencyCacheDirty = false;
            return cache;
        }

        private static List<DependencyEdge> CollectCodeDependencies()
        {
            var cache = GetOrBuildCodeDependencyInventory();
            if (cache.UserClassNames.Count == 0)
                return new List<DependencyEdge>();

            if (cache.AllEdges == null)
                cache.AllEdges = ParseCodeDependencies(cache.Scripts, cache.UserClassNames);

            return cache.AllEdges;
        }

        private static List<DependencyEdge> CollectCodeDependencies(HashSet<string> relevantScriptNames)
        {
            if (relevantScriptNames == null || relevantScriptNames.Count == 0)
                return new List<DependencyEdge>();

            var cache = GetOrBuildCodeDependencyInventory();
            if (cache.UserClassNames.Count == 0)
                return new List<DependencyEdge>();

            if (cache.AllEdges != null)
                return FilterCodeDependencies(cache.AllEdges, relevantScriptNames);

            var scopedScripts = cache.Scripts
                .Where(script => relevantScriptNames.Contains(script.ClassName))
                .ToList();

            if (scopedScripts.Count == 0)
                return new List<DependencyEdge>();

            return ParseCodeDependencies(scopedScripts, cache.UserClassNames);
        }

        private static List<DependencyEdge> ParseCodeDependencies(
            IReadOnlyList<CodeDependencyScriptInfo> scriptInfos,
            HashSet<string> userClassNames)
        {
            var edges = new List<DependencyEdge>();
            foreach (var scriptInfo in scriptInfos)
            {
                var path = scriptInfo.Path;
                var className = scriptInfo.ClassName;
                string rawSource;
                try { rawSource = File.ReadAllText(path, System.Text.Encoding.UTF8); } catch { continue; }

                var source = RxComment.Replace(rawSource, m => m.Groups[1].Success ? m.Value : new string(' ', m.Length));
                var methodAtLine = BuildMethodIndex(source);

                void AddEdge(string target, string pattern, string edgeType, int charIndex)
                {
                    if (target != className && userClassNames.Contains(target))
                    {
                        var method = GetMethodAtPos(source, charIndex, methodAtLine);
                        var location = method != null ? $"{className}.{method}" : className;
                        edges.Add(new DependencyEdge
                        {
                            fromObject = className,
                            fromScript = location,
                            fieldName = pattern,
                            fieldType = edgeType,
                            toObject = target,
                            source = "code"
                        });
                    }
                }

                foreach (System.Text.RegularExpressions.Match m in RxGetComponent.Matches(source))
                    AddEdge(m.Groups[1].Value, m.Value, "GetComponent", m.Index);

                foreach (System.Text.RegularExpressions.Match m in RxFindObject.Matches(source))
                    AddEdge(m.Groups[1].Value, m.Value, "FindObject", m.Index);

                foreach (System.Text.RegularExpressions.Match m in RxSendMessage.Matches(source))
                    AddEdge(m.Groups[1].Value, m.Value, "Message", m.Index);

                foreach (System.Text.RegularExpressions.Match m in RxFieldRef.Matches(source))
                    AddEdge(m.Groups[1].Value, $"field:{m.Groups[1].Value}", "FieldReference", m.Index);

                foreach (System.Text.RegularExpressions.Match m in RxSingleton.Matches(source))
                    AddEdge(m.Groups[1].Value, $"{m.Groups[1].Value}.Instance", "Singleton", m.Index);

                foreach (System.Text.RegularExpressions.Match m in RxStaticAccess.Matches(source))
                    AddEdge(m.Groups[1].Value, m.Value.TrimEnd('(', ';', ',', ')').Trim(), "StaticAccess", m.Index);

                foreach (System.Text.RegularExpressions.Match m in RxNewInstance.Matches(source))
                    AddEdge(m.Groups[1].Value, $"new {m.Groups[1].Value}()", "Instantiation", m.Index);

                foreach (System.Text.RegularExpressions.Match m in RxGenericArg.Matches(source))
                    AddEdge(m.Groups[1].Value, m.Value.TrimEnd('('), "GenericArg", m.Index);

                foreach (System.Text.RegularExpressions.Match inhMatch in RxInheritance.Matches(source))
                {
                    var declaredClass = inhMatch.Groups[1].Value;
                    foreach (var baseType in inhMatch.Groups[2].Value.Split(','))
                    {
                        var trimmed = baseType.Trim();
                        if (trimmed != declaredClass && userClassNames.Contains(trimmed))
                        {
                            edges.Add(new DependencyEdge
                            {
                                fromObject = declaredClass,
                                fromScript = declaredClass,
                                fieldName = $"extends:{trimmed}",
                                fieldType = "Inheritance",
                                toObject = trimmed,
                                source = "code"
                            });
                        }
                    }
                }

                foreach (System.Text.RegularExpressions.Match m in RxTypeCheck.Matches(source))
                    AddEdge(m.Groups[1].Value, m.Value.Trim(), "TypeCheck", m.Index);
            }

            return DeduplicateDependencyEdges(edges);
        }

        private static List<DependencyEdge> FilterCodeDependencies(
            IReadOnlyList<DependencyEdge> edges,
            HashSet<string> relevantScriptNames)
        {
            return DeduplicateDependencyEdges(edges.Where(edge =>
                relevantScriptNames.Contains(edge.fromObject) ||
                relevantScriptNames.Contains(edge.toObject)));
        }

        private static List<DependencyEdge> DeduplicateDependencyEdges(IEnumerable<DependencyEdge> edges)
        {
            return edges
                .GroupBy(e => $"{e.fromScript}->{e.toObject}:{e.fieldName}:{e.fieldType}")
                .Select(g => g.First())
                .ToList();
        }

        private static List<(int lineStart, string methodName)> BuildMethodIndex(string source)
        {
            var result = new List<(int lineStart, string methodName)>();
            foreach (System.Text.RegularExpressions.Match m in RxMethodDecl.Matches(source))
                result.Add((m.Index, m.Groups[1].Value));
            result.Sort((a, b) => a.lineStart.CompareTo(b.lineStart));
            return result;
        }

        private static string GetMethodAtPos(string source, int charIndex, List<(int lineStart, string methodName)> methods)
        {
            string best = null;
            foreach (var (pos, name) in methods)
            {
                if (pos <= charIndex) best = name;
                else break;
            }
            return best;
        }

        private static List<DependencyEdge> CollectDependencyEdges(IReadOnlyList<GameObject> allObjects)
        {
            var edges = new List<DependencyEdge>(allObjects.Count);
            var componentBuffer = new List<Component>(8);
            foreach (var go in allObjects)
            {
                var objPath = GameObjectFinder.GetCachedPath(go);
                componentBuffer.Clear();
                go.GetComponents(componentBuffer);
                foreach (var comp in componentBuffer)
                {
                    if (comp == null) continue;
                    var so = new SerializedObject(comp);
                    var prop = so.GetIterator();
                    bool enter = true;
                    while (prop.NextVisible(enter))
                    {
                        enter = false;
                        if (prop.propertyType != SerializedPropertyType.ObjectReference) continue;
                        if (prop.objectReferenceValue == null) continue;

                        string refTarget = null;
                        var refObj = prop.objectReferenceValue;
                        refTarget = GetObjectReferencePath(refObj);
                        if (refTarget == null || refTarget == objPath) continue;

                        edges.Add(new DependencyEdge
                        {
                            fromObject = objPath,
                            fromScript = comp.GetType().Name,
                            fieldName = prop.name,
                            fieldType = refObj.GetType().Name,
                            toObject = refTarget,
                            source = "scene"
                        });
                    }
                }
            }
            return edges;
        }

        [UnitySkill("scene_dependency_analyze", "Analyze object dependency graph and impact of changes. Use ONLY when user explicitly asks about: dependency analysis, impact analysis, what depends on, what references, safe to delete/disable/remove, refactoring impact, reference check",
            Category = SkillCategory.Perception, Operation = SkillOperation.Analyze,
            Tags = new[] { "dependency", "impact", "reference", "analysis", "graph" },
            Outputs = new[] { "sceneName", "totalReferences", "objectsAnalyzed", "analysis", "markdown" },
            ReadOnly = true)]
        public static object SceneDependencyAnalyze(
            string targetPath = null,
            string savePath = null)
        {
            if (!string.IsNullOrEmpty(savePath) && Validate.SafePath(savePath, "savePath") is object pathErr) return pathErr;

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var allObjects = GameObjectFinder.GetSceneObjects();

            var edges = CollectDependencyEdges(allObjects);

            // Build reverse index: who depends on each object
            var reverseIndex = edges.GroupBy(e => e.toObject)
                .ToDictionary(g => g.Key, g => g.ToList());

            // If targetPath specified, only analyze that subtree
            List<object> analysis;
            if (!string.IsNullOrEmpty(targetPath))
            {
                var targetGo = GameObjectFinder.FindByPath(targetPath);
                if (targetGo == null)
                    return new { success = false, error = $"Target '{targetPath}' not found" };

                // Collect target + all descendants
                var targetPaths = new HashSet<string>();
                var stack = new Stack<Transform>();
                stack.Push(targetGo.transform);
                while (stack.Count > 0)
                {
                    var t = stack.Pop();
                    targetPaths.Add(GameObjectFinder.GetCachedPath(t.gameObject));
                    foreach (Transform child in t) stack.Push(child);
                }

                analysis = BuildAnalysis(targetPaths, reverseIndex, edges);
            }
            else
            {
                // All objects that are depended upon
                var allTargets = new HashSet<string>(reverseIndex.Keys);
                analysis = BuildAnalysis(allTargets, reverseIndex, edges);
            }

            // Build markdown
            var md = BuildDependencyMarkdown(scene.name, targetPath, analysis, edges);

            // Save if requested
            string savedPath = null;
            if (!string.IsNullOrEmpty(savePath))
            {
                var dir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(savePath, md, new UTF8Encoding(false));
                AssetDatabase.ImportAsset(savePath);
                savedPath = savePath;
            }

            return new
            {
                success = true,
                sceneName = scene.name,
                target = targetPath,
                totalReferences = edges.Count,
                objectsAnalyzed = analysis.Count,
                analysis,
                savedTo = savedPath,
                markdown = savedPath == null ? md : null
            };
        }

        [UnitySkill("script_dependency_graph",
            "Given an entry script, return its N-hop dependency closure as structured JSON. "
            + "Shows which scripts to read to understand or safely modify a feature.",
            Category = SkillCategory.Perception, Operation = SkillOperation.Analyze,
            Tags = new[] { "script", "dependency", "graph", "closure", "analysis" },
            Outputs = new[] { "entryScript", "totalScriptsReached", "scripts", "edges", "suggestedReadOrder" },
            RequiresInput = new[] { "scriptName" },
            ReadOnly = true)]
        public static object ScriptDependencyGraph(
            string scriptName,
            int maxHops = 2,
            bool includeDetails = true)
        {
            if (string.IsNullOrEmpty(scriptName))
                return new { success = false, error = "scriptName is required" };

            // Find the entry script type
            var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                .Where(t => t.IsClass && IsUserScript(t))
                .ToList();

            var entryType = allTypes.FirstOrDefault(t => t.Name.Equals(scriptName, StringComparison.OrdinalIgnoreCase));
            if (entryType == null)
                return new { success = false, error = $"Script '{scriptName}' not found among user scripts" };

            var entryName = entryType.Name;

            // Collect all code-level dependency edges
            var codeEdges = CollectCodeDependencies();

            // Build bidirectional adjacency: outgoing (A depends on B) and incoming (B is depended by A)
            var outgoing = new Dictionary<string, HashSet<string>>();
            var incoming = new Dictionary<string, HashSet<string>>();

            foreach (var e in codeEdges)
            {
                if (!outgoing.ContainsKey(e.fromObject)) outgoing[e.fromObject] = new HashSet<string>();
                outgoing[e.fromObject].Add(e.toObject);

                if (!incoming.ContainsKey(e.toObject)) incoming[e.toObject] = new HashSet<string>();
                incoming[e.toObject].Add(e.fromObject);
            }

            // BFS from entry, expanding both directions, up to maxHops
            var visited = new Dictionary<string, int>(); // scriptName → hop
            var queue = new Queue<(string name, int hop)>();
            visited[entryName] = 0;
            queue.Enqueue((entryName, 0));

            while (queue.Count > 0)
            {
                var (current, hop) = queue.Dequeue();
                if (hop >= maxHops) continue;

                // Expand outgoing
                if (outgoing.TryGetValue(current, out var outs))
                {
                    foreach (var neighbor in outs)
                    {
                        if (!visited.ContainsKey(neighbor))
                        {
                            visited[neighbor] = hop + 1;
                            queue.Enqueue((neighbor, hop + 1));
                        }
                    }
                }

                // Expand incoming
                if (incoming.TryGetValue(current, out var ins))
                {
                    foreach (var neighbor in ins)
                    {
                        if (!visited.ContainsKey(neighbor))
                        {
                            visited[neighbor] = hop + 1;
                            queue.Enqueue((neighbor, hop + 1));
                        }
                    }
                }
            }

            // Build file path lookup via MonoScript assets
            var filePathMap = new Dictionary<string, string>();
            var scriptGuids = AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets" });
            foreach (var guid in scriptGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var ms = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (ms == null) continue;
                var cls = ms.GetClass();
                if (cls != null && visited.ContainsKey(cls.Name))
                    filePathMap[cls.Name] = path;
            }

            // Build type lookup for reached scripts
            var typeMap = new Dictionary<string, Type>();
            foreach (var t in allTypes)
            {
                if (visited.ContainsKey(t.Name) && !typeMap.ContainsKey(t.Name))
                    typeMap[t.Name] = t;
            }

            // Build script info list
            var scripts = new List<object>();
            foreach (var kv in visited.OrderBy(k => k.Value).ThenBy(k => k.Key))
            {
                var sName = kv.Key;
                var hop = kv.Value;
                var type = typeMap.ContainsKey(sName) ? typeMap[sName] : null;

                var dependsOn = outgoing.ContainsKey(sName)
                    ? outgoing[sName].Where(visited.ContainsKey).OrderBy(n => n).ToList()
                    : new List<string>();
                var dependedBy = incoming.ContainsKey(sName)
                    ? incoming[sName].Where(visited.ContainsKey).OrderBy(n => n).ToList()
                    : new List<string>();

                string kind = null, baseClass = null;
                List<object> fields = null;
                List<string> callbacks = null;

                if (type != null)
                {
                    kind = typeof(MonoBehaviour).IsAssignableFrom(type) ? "MonoBehaviour"
                        : typeof(ScriptableObject).IsAssignableFrom(type) ? "ScriptableObject"
                        : "Class";
                    baseClass = type.BaseType?.Name;

                    if (includeDetails)
                    {
                        fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                            .Where(f => !f.Name.StartsWith("<"))
                            .Select(f => (object)new
                            {
                                name = f.Name,
                                type = GetFriendlyTypeName(f.FieldType),
                                serializable = f.IsPublic || f.GetCustomAttribute<SerializeField>() != null
                            }).ToList();

                        if (typeof(MonoBehaviour).IsAssignableFrom(type))
                        {
                            callbacks = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                                .Where(m => UnityCallbacks.Contains(m.Name))
                                .Select(m => m.Name).ToList();
                        }
                    }
                }

                scripts.Add(new
                {
                    name = sName,
                    hop,
                    kind,
                    baseClass,
                    filePath = filePathMap.ContainsKey(sName) ? filePathMap[sName] : null,
                    dependsOn,
                    dependedBy,
                    fields,
                    unityCallbacks = callbacks
                });
            }

            // Filter edges to only those between reached scripts
            var reachedEdges = codeEdges
                .Where(e => visited.ContainsKey(e.fromObject) && visited.ContainsKey(e.toObject))
                .Select(e => (object)new { from = e.fromObject, to = e.toObject, type = e.fieldType, detail = e.fieldName })
                .ToList();

            // Topological sort for suggestedReadOrder (Kahn's algorithm)
            var readOrder = TopologicalSort(visited.Keys.ToList(), codeEdges.Where(e => visited.ContainsKey(e.fromObject) && visited.ContainsKey(e.toObject)).ToList(), entryName);

            return new
            {
                success = true,
                entryScript = entryName,
                totalScriptsReached = visited.Count,
                maxHops,
                scripts,
                edges = reachedEdges,
                suggestedReadOrder = readOrder
            };
        }

        /// <summary>
        /// Kahn's topological sort on dependency subgraph. Leaves with no outgoing edges come first.
        /// Entry script is placed last. Cycle members appended alphabetically.
        /// </summary>
        private static List<string> TopologicalSort(List<string> nodes, List<DependencyEdge> edges, string entryScript)
        {
            var inDegree = nodes.ToDictionary(n => n, n => 0);
            var adj = nodes.ToDictionary(n => n, n => new List<string>());

            foreach (var e in edges)
            {
                if (!adj.ContainsKey(e.toObject) || !inDegree.ContainsKey(e.fromObject)) continue;
                adj[e.toObject].Add(e.fromObject); // dependency flows: if A depends on B, B should be read first → edge B→A
                inDegree[e.fromObject] = inDegree.TryGetValue(e.fromObject, out var d) ? d + 1 : 1;
            }

            var queue = new Queue<string>(nodes.Where(n => inDegree[n] == 0).OrderBy(n => n));
            var result = new List<string>();

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                result.Add(node);
                foreach (var neighbor in adj[node].OrderBy(n => n))
                {
                    inDegree[neighbor]--;
                    if (inDegree[neighbor] == 0) queue.Enqueue(neighbor);
                }
            }

            // Remaining nodes are in cycles — append alphabetically
            var remaining = nodes.Where(n => !result.Contains(n)).OrderBy(n => n).ToList();
            result.AddRange(remaining);

            // Move entry script to end (read dependencies first, entry last)
            if (result.Remove(entryScript))
                result.Add(entryScript);

            return result;
        }

        private class DependencyEdge
        {
            public string fromObject, fromScript, fieldName, fieldType, toObject, source;
        }

        private static List<object> BuildAnalysis(HashSet<string> targets, Dictionary<string, List<DependencyEdge>> reverseIndex, List<DependencyEdge> allEdges)
        {
            var result = new List<object>();
            foreach (var path in targets.OrderBy(p => p))
            {
                var dependedBy = reverseIndex.ContainsKey(path)
                    ? reverseIndex[path].Select(e => new { source = e.fromObject, script = e.fromScript, field = e.fieldName, fieldType = e.fieldType }).ToList()
                    : null;
                var dependsOn = allEdges.Where(e => e.fromObject == path)
                    .Select(e => new { target = e.toObject, script = e.fromScript, field = e.fieldName, fieldType = e.fieldType }).ToList();

                int incomingCount = dependedBy?.Count ?? 0;
                string risk = incomingCount == 0 ? "safe" : incomingCount <= 2 ? "low" : incomingCount <= 5 ? "medium" : "high";

                result.Add(new
                {
                    path,
                    risk,
                    dependedByCount = incomingCount,
                    dependedBy,
                    dependsOnCount = dependsOn.Count,
                    dependsOn = dependsOn.Count > 0 ? dependsOn : null
                });
            }
            return result;
        }

        private static string BuildDependencyMarkdown(string sceneName, string targetPath, List<object> analysis, List<DependencyEdge> edges)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Dependency Analysis: {sceneName}");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(targetPath))
                sb.AppendLine($"> Target: `{targetPath}`");
            sb.AppendLine($"> Total references: {edges.Count} | Objects analyzed: {analysis.Count}");
            sb.AppendLine();

            // High risk objects first
            sb.AppendLine("## Risk Summary");
            sb.AppendLine();
            sb.AppendLine("| Object | Risk | Depended By | Depends On |");
            sb.AppendLine("|--------|------|-------------|------------|");
            foreach (dynamic item in analysis)
            {
                sb.AppendLine($"| `{item.path}` | {item.risk} | {item.dependedByCount} | {item.dependsOnCount} |");
            }
            sb.AppendLine();

            // Detail for objects with incoming dependencies
            var withDeps = analysis.Where(a => ((dynamic)a).dependedByCount > 0).ToList();
            if (withDeps.Count > 0)
            {
                sb.AppendLine("## Dependency Details");
                sb.AppendLine();
                sb.AppendLine("Objects below are referenced by other scripts. **Disabling/deleting them may cause errors.**");
                sb.AppendLine();
                foreach (dynamic item in withDeps)
                {
                    sb.AppendLine($"### `{item.path}` (risk: {item.risk})");
                    sb.AppendLine();
                    sb.AppendLine("**Referenced by:**");
                    if (item.dependedBy != null)
                    {
                        foreach (dynamic dep in item.dependedBy)
                            sb.AppendLine($"- `{dep.source}` → `{dep.script}.{dep.field}` ({dep.fieldType})");
                    }
                    sb.AppendLine();
                }
            }

            sb.AppendLine("---");
            sb.AppendLine($"*Generated: {DateTime.Now:yyyy-MM-dd HH:mm}*");
            return sb.ToString();
        }

        private static string GetFriendlyTypeName(Type type)
        {
            if (type == typeof(void)) return "void";
            if (type == typeof(int)) return "int";
            if (type == typeof(float)) return "float";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(string)) return "string";
            if (type.IsGenericType)
            {
                var baseName = type.Name.Split('`')[0];
                var args = string.Join(", ", type.GetGenericArguments().Select(GetFriendlyTypeName));
                return $"{baseName}<{args}>";
            }
            if (type.IsArray)
            {
                return GetFriendlyTypeName(type.GetElementType()) + "[]";
            }
            return type.Name;
        }

        [UnitySkill("scene_tag_layer_stats", "Get Tag/Layer usage stats and find potential issues (untagged objects, unused layers)",
            Category = SkillCategory.Perception, Operation = SkillOperation.Analyze,
            Tags = new[] { "tag", "layer", "statistics", "usage" },
            Outputs = new[] { "totalObjects", "untaggedCount", "tags", "layers", "emptyDefinedLayers" },
            ReadOnly = true)]
        public static object SceneTagLayerStats()
        {
            var allObjects = GameObjectFinder.GetSceneObjects();
            var tagCounts = new Dictionary<string, int>();
            var layerCounts = new Dictionary<string, int>();
            var usedLayers = new HashSet<int>();
            int untaggedCount = 0;

            foreach (var go in allObjects)
            {
                var tag = go.tag ?? "Untagged";
                if (tag == "Untagged") untaggedCount++;
                tagCounts[tag] = tagCounts.TryGetValue(tag, out var tc) ? tc + 1 : 1;
                var layerName = LayerMask.LayerToName(go.layer);
                if (string.IsNullOrEmpty(layerName)) layerName = $"Layer {go.layer}";
                layerCounts[layerName] = layerCounts.TryGetValue(layerName, out var lc) ? lc + 1 : 1;
                usedLayers.Add(go.layer);
            }

            // Find layers with physics interactions that have no objects
            var emptyLayers = new List<string>();
            for (int i = 0; i < 32; i++)
            {
                var layerName = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(layerName) && !usedLayers.Contains(i))
                    emptyLayers.Add(layerName);
            }

            return new { success = true, totalObjects = allObjects.Count, untaggedCount,
                tags = tagCounts.OrderByDescending(kv => kv.Value).Select(kv => new { tag = kv.Key, count = kv.Value }).ToArray(),
                layers = layerCounts.OrderByDescending(kv => kv.Value).Select(kv => new { layer = kv.Key, count = kv.Value }).ToArray(),
                emptyDefinedLayers = emptyLayers.ToArray() };
        }

        [UnitySkill("scene_performance_hints", "Diagnose scene performance issues with prioritized actionable suggestions",
            Category = SkillCategory.Perception, Operation = SkillOperation.Analyze,
            Tags = new[] { "performance", "optimization", "diagnostics", "hints" },
            Outputs = new[] { "hintCount", "hints" },
            ReadOnly = true)]
        public static object ScenePerformanceHints()
        {
            var hints = new List<object>();

            // 1. Realtime shadow lights
            var lights = FindHelper.FindAll<Light>();
            var shadowLights = lights.Where(l => l.shadows != LightShadows.None).ToArray();
            if (shadowLights.Length > 4)
                hints.Add(new { priority = 1, category = "Lighting", issue = $"{shadowLights.Length} shadow-casting lights",
                    suggestion = "Reduce to ≤4 or use baked lighting", fixSkill = "light_set_properties" });

            // 2. Non-static renderers
            var renderers = FindHelper.FindAll<Renderer>();
            int nonStaticCount = renderers.Count(r => !r.gameObject.isStatic);
            if (nonStaticCount > 100)
                hints.Add(new { priority = 2, category = "Batching", issue = $"{nonStaticCount} non-static renderers",
                    suggestion = "Mark static objects with optimize_set_static_flags", fixSkill = "optimize_set_static_flags" });

            // 3. High-poly meshes without LOD
            var meshFilters = FindHelper.FindAll<MeshFilter>();
            var highPoly = meshFilters.Where(mf => mf.sharedMesh != null && mf.sharedMesh.triangles.Length / 3 > 10000
                && mf.GetComponent<LODGroup>() == null).ToArray();
            if (highPoly.Length > 0)
                hints.Add(new { priority = 2, category = "Geometry", issue = $"{highPoly.Length} high-poly meshes (>10k tris) without LOD",
                    suggestion = "Add LOD groups", fixSkill = "optimize_set_lod_group" });

            // 4. Duplicate materials
            var mats = renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null).ToArray();
            var uniqueShaders = mats.Select(m => m.shader?.name).Distinct().Count();
            var duplicateCount = mats.Length - mats.Select(m => m.GetInstanceID()).Distinct().Count();
            if (duplicateCount > 10)
                hints.Add(new { priority = 3, category = "Materials", issue = $"{duplicateCount} duplicate material references",
                    suggestion = "Consolidate materials", fixSkill = "optimize_find_duplicate_materials" });

            // 5. Particle systems
            var particles = FindHelper.FindAll<ParticleSystem>();
            if (particles.Length > 20)
                hints.Add(new { priority = 3, category = "Particles", issue = $"{particles.Length} particle systems",
                    suggestion = "Consider reducing or pooling particle systems", fixSkill = (string)null });

            if (hints.Count == 0)
                hints.Add(new { priority = 0, category = "OK", issue = "No obvious performance issues",
                    suggestion = "Scene looks good", fixSkill = (string)null });

            return new { success = true, hintCount = hints.Count, hints };
        }
    }
}
