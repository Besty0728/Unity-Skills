using UnityEngine;
using UnityEditor;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// XR Interaction Toolkit skills — setup, interactors, interactables, locomotion, and UI.
    /// Requires com.unity.xr.interaction.toolkit (2.x or 3.x).
    /// All XRI API calls use reflection for cross-version compatibility.
    /// </summary>
    public static class XRSkills
    {
#if !XRI
        private static object NoXRI() =>
            new { error = "XR Interaction Toolkit package (com.unity.xr.interaction.toolkit) is not installed. Install via: Window > Package Manager > Unity Registry > XR Interaction Toolkit" };
#endif

        // ==================================================================================
        // Setup & Validation (5 skills)
        // ==================================================================================

        [UnitySkill("xr_check_setup", "Comprehensive XR project setup validation: checks XRI package, XR Origin, InteractionManager, EventSystem, InputSystem, controllers",
            Category = SkillCategory.XR, Operation = SkillOperation.Analyze,
            Tags = new[] { "xr", "setup", "validation", "diagnostic" },
            Outputs = new[] { "xriInstalled", "interactionManagerCount", "xrOriginCount", "issueCount", "issues" },
            ReadOnly = true)]
        public static object XRCheckSetup(bool verbose = false)
        {
#if !XRI
            return NoXRI();
#else
            var issues = new List<string>();
            var info = new Dictionary<string, object>();

            // 1. XRI version
            info["xriInstalled"] = XRReflectionHelper.IsXRIInstalled;
            info["xriMajorVersion"] = XRReflectionHelper.XRIMajorVersion;

            // 2. XRInteractionManager
            var managers = XRReflectionHelper.FindComponentsOfXRType("XRInteractionManager");
            info["interactionManagerCount"] = managers.Length;
            if (managers.Length == 0)
                issues.Add("No XRInteractionManager found in scene. Add one via xr_setup_interaction_manager.");
            if (managers.Length > 1)
                issues.Add($"Multiple XRInteractionManagers found ({managers.Length}). Typically only one is needed.");

            // 3. XR Origin
            var origins = XRReflectionHelper.FindComponentsOfXRType("XROrigin");
            info["xrOriginCount"] = origins.Length;
            if (origins.Length == 0)
                issues.Add("No XR Origin found in scene. Create one via xr_setup_rig.");

            // 4. Camera
            var mainCam = Camera.main;
            info["mainCamera"] = mainCam != null ? mainCam.gameObject.name : null;
            if (mainCam == null)
                issues.Add("No Main Camera found. XR Origin rig should include a tagged MainCamera.");

            // 5. EventSystem
            var eventSystems = FindHelper.FindAll<UnityEngine.EventSystems.EventSystem>();
            info["eventSystemCount"] = eventSystems.Length;
            if (eventSystems.Length == 0)
                issues.Add("No EventSystem found. Create one via xr_setup_event_system.");
            else
            {
                // Check for XRUIInputModule
                bool hasXRInput = false;
                foreach (var es in eventSystems)
                {
                    if (XRReflectionHelper.GetXRComponent(es.gameObject, "XRUIInputModule") != null)
                    {
                        hasXRInput = true;
                        break;
                    }
                }
                info["hasXRUIInputModule"] = hasXRInput;
                if (!hasXRInput)
                    issues.Add("EventSystem exists but lacks XRUIInputModule. Fix via xr_setup_event_system.");
            }

            // 6. Interactors & Interactables
            var interactors = XRReflectionHelper.FindComponentsOfXRType("XRBaseInteractor");
            var interactables = XRReflectionHelper.FindComponentsOfXRType("XRBaseInteractable");
            info["interactorCount"] = interactors.Length;
            info["interactableCount"] = interactables.Length;

            // 7. Locomotion
            var teleportProvider = XRReflectionHelper.FindFirstOfXRType("TeleportationProvider");
            var moveProvider = XRReflectionHelper.FindFirstOfXRType("ActionBasedContinuousMoveProvider")
                               ?? XRReflectionHelper.FindFirstOfXRType("ContinuousMoveProvider");
            var turnProvider = XRReflectionHelper.FindFirstOfXRType("ActionBasedSnapTurnProvider")
                               ?? XRReflectionHelper.FindFirstOfXRType("SnapTurnProvider")
                               ?? XRReflectionHelper.FindFirstOfXRType("ActionBasedContinuousTurnProvider")
                               ?? XRReflectionHelper.FindFirstOfXRType("ContinuousTurnProvider");
            info["hasTeleportation"] = teleportProvider != null;
            info["hasContinuousMove"] = moveProvider != null;
            info["hasTurnProvider"] = turnProvider != null;

            // 8. Collider validation — most common XR setup error
            var colliderIssues = new List<string>();
            foreach (var interactor in interactors)
            {
                var typeName = interactor.GetType().Name;
                if (typeName.Contains("Direct") || typeName.Contains("Socket"))
                {
                    var col = interactor.GetComponent<Collider>();
                    if (col == null)
                        colliderIssues.Add($"{interactor.gameObject.name} ({typeName}): missing trigger Collider — will not detect targets");
                    else if (!col.isTrigger)
                        colliderIssues.Add($"{interactor.gameObject.name} ({typeName}): Collider.isTrigger must be TRUE for interactors");
                }
            }
            foreach (var interactable in interactables)
            {
                var typeName = interactable.GetType().Name;
                if (typeName.Contains("Grab"))
                {
                    var rb = interactable.GetComponent<Rigidbody>();
                    if (rb == null)
                        colliderIssues.Add($"{interactable.gameObject.name} ({typeName}): missing Rigidbody — grab will not work");
                    var col = interactable.GetComponent<Collider>();
                    if (col == null)
                        colliderIssues.Add($"{interactable.gameObject.name} ({typeName}): missing Collider — cannot be detected by interactors");
                    else if (col.isTrigger)
                        colliderIssues.Add($"{interactable.gameObject.name} ({typeName}): Collider.isTrigger should be FALSE for interactables");
                }
            }
            if (colliderIssues.Count > 0)
            {
                issues.AddRange(colliderIssues);
                info["colliderIssues"] = colliderIssues;
            }

            // 9. TrackedPoseDriver check
            var tpdType = FindTrackedPoseDriverType();
            if (tpdType != null && origins.Length > 0)
            {
                var originGo = origins[0].gameObject;
                var controllers = new[] { "Left Controller", "Right Controller" };
                foreach (var ctrlName in controllers)
                {
                    var ctrlTransform = originGo.transform.Find(ctrlName);
                    if (ctrlTransform != null && ctrlTransform.GetComponent(tpdType) == null)
                        issues.Add($"'{ctrlName}' lacks TrackedPoseDriver — controller position will not update");
                }
            }

            info["issues"] = issues;
            info["issueCount"] = issues.Count;
            info["success"] = true;

            if (verbose)
            {
                // Add detailed component listing
                info["interactorDetails"] = interactors.Select(c => new {
                    name = c.gameObject.name, type = c.GetType().Name,
                    instanceId = c.gameObject.GetInstanceID()
                }).ToArray();
                info["interactableDetails"] = interactables.Select(c => new {
                    name = c.gameObject.name, type = c.GetType().Name,
                    instanceId = c.gameObject.GetInstanceID()
                }).ToArray();
            }

            return info;
#endif
        }

        [UnitySkill("xr_setup_rig", "Create a complete XR Origin rig with Camera, Left/Right Controllers", TracksWorkflow = true,
            Category = SkillCategory.XR, Operation = SkillOperation.Create,
            Tags = new[] { "xr", "rig", "origin", "camera", "controllers" },
            Outputs = new[] { "name", "instanceId", "xriVersion", "hierarchy", "position" },
            MutatesScene = true, RiskLevel = "medium", RequiresPackages = new[] { "com.unity.xr.interaction.toolkit" })]
        public static object XRSetupRig(
            string name = "XR Origin",
            float x = 0, float y = 0, float z = 0,
            float cameraYOffset = 1.36144f)
        {
#if !XRI
            return NoXRI();
#else
            // Check XROrigin type availability
            var xrOriginType = XRReflectionHelper.ResolveXRType("XROrigin");
            if (xrOriginType == null)
                return new { error = "XROrigin type not found. Ensure com.unity.xr.core-utils is installed." };

            // Root: XR Origin
            var root = new GameObject(name);
            root.transform.position = new Vector3(x, y, z);

            // Add XROrigin component
            var originComp = root.AddComponent(xrOriginType);
            if (originComp == null)
            {
                UnityEngine.Object.DestroyImmediate(root);
                return new { error = "Failed to add XROrigin component." };
            }

            // Camera Offset child
            var cameraOffset = new GameObject("Camera Offset");
            cameraOffset.transform.SetParent(root.transform, false);
            cameraOffset.transform.localPosition = new Vector3(0, cameraYOffset, 0);

            // Set CameraFloorOffsetObject via reflection
            XRReflectionHelper.SetProperty(originComp, "CameraFloorOffsetObject", cameraOffset);

            // Main Camera
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            camGo.transform.SetParent(cameraOffset.transform, false);
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.01f;
            camGo.AddComponent<AudioListener>();

            // Add TrackedPoseDriver to camera
            var tpdType = FindTrackedPoseDriverType();
            if (tpdType != null)
                camGo.AddComponent(tpdType);

            // Set Camera on XROrigin
            XRReflectionHelper.SetProperty(originComp, "Camera", cam);

            // Left Controller
            var leftCtrl = new GameObject("Left Controller");
            leftCtrl.transform.SetParent(root.transform, false);
            if (tpdType != null)
                leftCtrl.AddComponent(tpdType);

            // Right Controller
            var rightCtrl = new GameObject("Right Controller");
            rightCtrl.transform.SetParent(root.transform, false);
            if (tpdType != null)
                rightCtrl.AddComponent(tpdType);

            // Add XRInteractionManager if none exists
            var managerComp = XRReflectionHelper.FindFirstOfXRType("XRInteractionManager");
            if (managerComp == null)
            {
                var managerType = XRReflectionHelper.ResolveXRType("XRInteractionManager");
                if (managerType != null)
                    root.AddComponent(managerType);
            }

            Undo.RegisterCreatedObjectUndo(root, "Create XR Origin Rig");
            WorkflowManager.SnapshotObject(root, SnapshotType.Created);

            return new
            {
                success = true,
                name = root.name,
                instanceId = root.GetInstanceID(),
                xriVersion = XRReflectionHelper.XRIMajorVersion,
                hierarchy = new
                {
                    cameraOffset = cameraOffset.name,
                    mainCamera = camGo.name,
                    leftController = leftCtrl.name,
                    rightController = rightCtrl.name
                },
                position = new { x, y, z },
                cameraYOffset,
                note = "Add interactors to controllers via xr_add_ray_interactor or xr_add_direct_interactor."
            };
#endif
        }

        [UnitySkill("xr_setup_interaction_manager", "Add or get XRInteractionManager in the scene", TracksWorkflow = true,
            Category = SkillCategory.XR, Operation = SkillOperation.Create,
            Tags = new[] { "xr", "interaction", "manager", "setup" },
            Outputs = new[] { "alreadyExists", "name", "instanceId" },
            MutatesScene = true, RiskLevel = "medium", RequiresPackages = new[] { "com.unity.xr.interaction.toolkit" })]
        public static object XRSetupInteractionManager(string name = null)
        {
#if !XRI
            return NoXRI();
#else
            var managerType = XRReflectionHelper.ResolveXRType("XRInteractionManager");
            if (managerType == null)
                return new { error = "XRInteractionManager type not found." };

            // Check if one already exists
            var existing = XRReflectionHelper.FindFirstOfXRType("XRInteractionManager");
            if (existing != null)
                return new
                {
                    success = true,
                    alreadyExists = true,
                    name = existing.gameObject.name,
                    instanceId = existing.gameObject.GetInstanceID()
                };

            var go = new GameObject(name ?? "XR Interaction Manager");
            go.AddComponent(managerType);

            Undo.RegisterCreatedObjectUndo(go, "Create XRInteractionManager");
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

            return new
            {
                success = true,
                alreadyExists = false,
                name = go.name,
                instanceId = go.GetInstanceID()
            };
#endif
        }

        [UnitySkill("xr_setup_event_system", "Set up XR-compatible EventSystem (replace StandaloneInputModule with XRUIInputModule)", TracksWorkflow = true,
            Category = SkillCategory.XR, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "xr", "eventsystem", "input", "ui" },
            Outputs = new[] { "name", "instanceId", "created", "removedStandaloneInputModule", "addedXRUIInputModule" },
            MutatesScene = true, RiskLevel = "medium", RequiresPackages = new[] { "com.unity.xr.interaction.toolkit" })]
        public static object XRSetupEventSystem()
        {
#if !XRI
            return NoXRI();
#else
            var xrInputType = XRReflectionHelper.ResolveXRType("XRUIInputModule");
            if (xrInputType == null)
                return new { error = "XRUIInputModule type not found in current XRI version." };

            // Find or create EventSystem
            var eventSystems = FindHelper.FindAll<UnityEngine.EventSystems.EventSystem>();
            GameObject esGo;
            bool created = false;

            if (eventSystems.Length > 0)
            {
                esGo = eventSystems[0].gameObject;
            }
            else
            {
                esGo = new GameObject("EventSystem");
                esGo.AddComponent<UnityEngine.EventSystems.EventSystem>();
                created = true;
            }

            Undo.RecordObject(esGo, "Setup XR EventSystem");

            // Remove StandaloneInputModule if present
            var standalone = esGo.GetComponent<UnityEngine.EventSystems.StandaloneInputModule>();
            bool removedStandalone = false;
            if (standalone != null)
            {
                Undo.DestroyObjectImmediate(standalone);
                removedStandalone = true;
            }

            // Add XRUIInputModule if not present
            bool addedXRInput = false;
            var xrInput = esGo.GetComponent(xrInputType);
            if (xrInput == null)
            {
                xrInput = esGo.AddComponent(xrInputType);
                addedXRInput = true;
            }

            if (created)
                Undo.RegisterCreatedObjectUndo(esGo, "Create XR EventSystem");

            WorkflowManager.SnapshotObject(esGo, created ? SnapshotType.Created : SnapshotType.Modified);

            return new
            {
                success = true,
                name = esGo.name,
                instanceId = esGo.GetInstanceID(),
                created,
                removedStandaloneInputModule = removedStandalone,
                addedXRUIInputModule = addedXRInput
            };
#endif
        }

        [UnitySkill("xr_get_scene_report", "Generate comprehensive XR scene diagnostic report: all XR components, configuration, and issues",
            Category = SkillCategory.XR, Operation = SkillOperation.Query | SkillOperation.Analyze,
            Tags = new[] { "xr", "report", "scene", "diagnostic" },
            Outputs = new[] { "xriVersion", "totalXRComponents", "components", "summary" },
            ReadOnly = true)]
        public static object XRGetSceneReport(bool verbose = false)
        {
#if !XRI
            return NoXRI();
#else
            var report = new Dictionary<string, object>();

            report["xriVersion"] = XRReflectionHelper.XRIMajorVersion;

            // Collect all XR component types
            var componentTypes = new[]
            {
                "XRInteractionManager", "XROrigin",
                "XRRayInteractor", "XRDirectInteractor", "XRSocketInteractor", "NearFarInteractor",
                "XRGrabInteractable", "XRSimpleInteractable",
                "TeleportationProvider", "TeleportationArea", "TeleportationAnchor",
                "ActionBasedContinuousMoveProvider", "ContinuousMoveProvider",
                "ActionBasedSnapTurnProvider", "SnapTurnProvider",
                "ActionBasedContinuousTurnProvider", "ContinuousTurnProvider",
                "TrackedDeviceGraphicRaycaster", "XRUIInputModule",
                "ActionBasedController", "XRController"
            };

            var components = new List<object>();
            int totalCount = 0;

            foreach (var typeName in componentTypes)
            {
                var found = XRReflectionHelper.FindComponentsOfXRType(typeName);
                if (found.Length > 0)
                {
                    totalCount += found.Length;
                    foreach (var comp in found)
                    {
                        var entry = new Dictionary<string, object>
                        {
                            ["type"] = comp.GetType().Name,
                            ["gameObject"] = comp.gameObject.name,
                            ["instanceId"] = comp.gameObject.GetInstanceID(),
                            ["path"] = GameObjectFinder.GetPath(comp.gameObject),
                            ["enabled"] = comp is Behaviour b ? b.enabled : true
                        };

                        if (verbose)
                            entry["properties"] = XRReflectionHelper.GetComponentInfo(comp);

                        components.Add(entry);
                    }
                }
            }

            report["totalXRComponents"] = totalCount;
            report["components"] = components;

            // Summary counts
            report["summary"] = new
            {
                interactionManagers = XRReflectionHelper.FindComponentsOfXRType("XRInteractionManager").Length,
                origins = XRReflectionHelper.FindComponentsOfXRType("XROrigin").Length,
                interactors = XRReflectionHelper.FindComponentsOfXRType("XRBaseInteractor").Length,
                interactables = XRReflectionHelper.FindComponentsOfXRType("XRBaseInteractable").Length,
                teleportTargets =
                    XRReflectionHelper.FindComponentsOfXRType("TeleportationArea").Length +
                    XRReflectionHelper.FindComponentsOfXRType("TeleportationAnchor").Length
            };

            report["success"] = true;
            return report;
#endif
        }

        // ==================================================================================
        // Interactor Skills (4 skills)
        // ==================================================================================

        [UnitySkill("xr_add_ray_interactor", "Add XRRayInteractor to a controller GameObject (with LineRenderer and line visual)", TracksWorkflow = true,
            Category = SkillCategory.XR, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "xr", "ray", "interactor", "controller" },
            Outputs = new[] { "name", "instanceId", "interactorType", "maxRaycastDistance", "hasLineVisual" },
            RequiresInput = new[] { "gameObject" })]
        public static object XRAddRayInteractor(
            string name = null, int instanceId = 0, string path = null,
            float maxDistance = 30f,
            string lineType = "StraightLine",
            bool addLineVisual = true)
        {
#if !XRI
            return NoXRI();
#else
            var (go, findErr) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (findErr != null) return findErr;

            Undo.RecordObject(go, "Add XRRayInteractor");

            // Add XRRayInteractor
            var comp = XRReflectionHelper.AddXRComponent(go, "XRRayInteractor");
            if (comp == null)
                return new { error = "Failed to add XRRayInteractor. Type not found in current XRI version." };

            Undo.RegisterCreatedObjectUndo(comp, "Add XRRayInteractor");

            // Configure properties
            XRReflectionHelper.SetProperty(comp, "maxRaycastDistance", maxDistance);
            if (!string.IsNullOrEmpty(lineType))
                XRReflectionHelper.SetEnumProperty(comp, "lineType", lineType);

            // Add LineRenderer if not present
            var lr = go.GetComponent<LineRenderer>();
            if (lr == null)
            {
                lr = go.AddComponent<LineRenderer>();
                lr.startWidth = 0.01f;
                lr.endWidth = 0.01f;
                lr.material = new Material(Shader.Find("Sprites/Default"));
                lr.startColor = Color.white;
                lr.endColor = new Color(1, 1, 1, 0.5f);
            }

            // Add XRInteractorLineVisual if requested
            Component lineVisual = null;
            if (addLineVisual)
            {
                lineVisual = XRReflectionHelper.AddXRComponent(go, "XRInteractorLineVisual");
            }

            WorkflowManager.SnapshotObject(go);

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                interactorType = comp.GetType().Name,
                maxRaycastDistance = maxDistance,
                lineType,
                hasLineVisual = lineVisual != null,
                lineTypeOptions = XRReflectionHelper.GetEnumValues(comp, "lineType")
            };
#endif
        }

        [UnitySkill("xr_add_direct_interactor", "Add XRDirectInteractor for close-range grab (with SphereCollider trigger)", TracksWorkflow = true,
            Category = SkillCategory.XR, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "xr", "direct", "interactor", "grab" },
            Outputs = new[] { "name", "instanceId", "interactorType", "triggerRadius" },
            RequiresInput = new[] { "gameObject" })]
        public static object XRAddDirectInteractor(
            string name = null, int instanceId = 0, string path = null,
            float radius = 0.1f)
        {
#if !XRI
            return NoXRI();
#else
            var (go, findErr) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (findErr != null) return findErr;

            Undo.RecordObject(go, "Add XRDirectInteractor");

            // Add XRDirectInteractor
            var comp = XRReflectionHelper.AddXRComponent(go, "XRDirectInteractor");
            if (comp == null)
                return new { error = "Failed to add XRDirectInteractor. Type not found in current XRI version." };

            Undo.RegisterCreatedObjectUndo(comp, "Add XRDirectInteractor");

            // Add SphereCollider trigger if no collider exists
            var collider = go.GetComponent<Collider>();
            if (collider == null)
            {
                var sphere = go.AddComponent<SphereCollider>();
                sphere.isTrigger = true;
                sphere.radius = radius;
            }

            WorkflowManager.SnapshotObject(go);

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                interactorType = comp.GetType().Name,
                triggerRadius = radius
            };
#endif
        }

        [UnitySkill("xr_add_socket_interactor", "Add XRSocketInteractor for snap-to-slot object placement", TracksWorkflow = true,
            Category = SkillCategory.XR, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "xr", "socket", "interactor", "snap" },
            Outputs = new[] { "name", "instanceId", "interactorType", "showHoverMesh", "recycleDelay" },
            RequiresInput = new[] { "gameObject" })]
        public static object XRAddSocketInteractor(
            string name = null, int instanceId = 0, string path = null,
            bool showHoverMesh = true,
            float recycleDelay = 1f)
        {
#if !XRI
            return NoXRI();
#else
            var (go, findErr) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (findErr != null) return findErr;

            Undo.RecordObject(go, "Add XRSocketInteractor");

            var comp = XRReflectionHelper.AddXRComponent(go, "XRSocketInteractor");
            if (comp == null)
                return new { error = "Failed to add XRSocketInteractor. Type not found in current XRI version." };

            Undo.RegisterCreatedObjectUndo(comp, "Add XRSocketInteractor");

            XRReflectionHelper.SetProperty(comp, "showInteractableHoverMeshes", showHoverMesh);
            XRReflectionHelper.SetProperty(comp, "recycleDelayTime", recycleDelay);

            // Add SphereCollider trigger if no collider exists
            if (go.GetComponent<Collider>() == null)
            {
                var sphere = go.AddComponent<SphereCollider>();
                sphere.isTrigger = true;
                sphere.radius = 0.15f;
            }

            WorkflowManager.SnapshotObject(go);

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                interactorType = comp.GetType().Name,
                showHoverMesh,
                recycleDelay
            };
#endif
        }

        [UnitySkill("xr_list_interactors", "List all XR interactors in the scene with type and configuration",
            Category = SkillCategory.XR, Operation = SkillOperation.Query,
            Tags = new[] { "xr", "interactors", "list", "scene" },
            Outputs = new[] { "count", "interactors", "xriVersion" },
            ReadOnly = true)]
        public static object XRListInteractors(bool verbose = false)
        {
#if !XRI
            return NoXRI();
#else
            var interactorTypes = new[] { "XRRayInteractor", "XRDirectInteractor", "XRSocketInteractor", "NearFarInteractor" };
            var results = new List<object>();

            foreach (var typeName in interactorTypes)
            {
                var found = XRReflectionHelper.FindComponentsOfXRType(typeName);
                foreach (var comp in found)
                {
                    var entry = new Dictionary<string, object>
                    {
                        ["type"] = comp.GetType().Name,
                        ["gameObject"] = comp.gameObject.name,
                        ["instanceId"] = comp.gameObject.GetInstanceID(),
                        ["path"] = GameObjectFinder.GetPath(comp.gameObject),
                        ["enabled"] = comp is Behaviour b ? b.enabled : true
                    };

                    if (verbose)
                        entry["properties"] = XRReflectionHelper.GetComponentInfo(comp);

                    results.Add(entry);
                }
            }

            return new
            {
                success = true,
                count = results.Count,
                interactors = results,
                xriVersion = XRReflectionHelper.XRIMajorVersion
            };
#endif
        }

        // ==================================================================================
        // Interactable Skills (4 skills)
        // ==================================================================================

        [UnitySkill("xr_add_grab_interactable", "Make an object grabbable (adds XRGrabInteractable + Rigidbody + Collider if needed)", TracksWorkflow = true,
            Category = SkillCategory.XR, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "xr", "grab", "interactable", "physics" },
            Outputs = new[] { "name", "instanceId", "movementType", "throwOnDetach" },
            RequiresInput = new[] { "gameObject" })]
        public static object XRAddGrabInteractable(
            string name = null, int instanceId = 0, string path = null,
            string movementType = "VelocityTracking",
            bool throwOnDetach = true,
            bool smoothPosition = true,
            bool smoothRotation = true,
            float smoothPositionAmount = 5f,
            float smoothRotationAmount = 5f,
            bool useGravity = true,
            bool isKinematic = false,
            string attachTransformOffset = null)
        {
#if !XRI
            return NoXRI();
#else
            var (go, findErr) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (findErr != null) return findErr;

            Undo.RecordObject(go, "Add XRGrabInteractable");

            // Ensure Rigidbody
            var rb = go.GetComponent<Rigidbody>();
            if (rb == null)
            {
                rb = go.AddComponent<Rigidbody>();
                rb.useGravity = useGravity;
                rb.isKinematic = isKinematic;
            }

            // Ensure Collider
            if (go.GetComponent<Collider>() == null)
            {
                // Auto-detect best collider based on mesh
                var meshFilter = go.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null)
                    go.AddComponent<MeshCollider>().convex = true;
                else
                    go.AddComponent<BoxCollider>();
            }

            // Add XRGrabInteractable
            var comp = XRReflectionHelper.AddXRComponent(go, "XRGrabInteractable");
            if (comp == null)
                return new { error = "Failed to add XRGrabInteractable. Type not found in current XRI version." };

            Undo.RegisterCreatedObjectUndo(comp, "Add XRGrabInteractable");

            // Configure via reflection
            XRReflectionHelper.SetEnumProperty(comp, "movementType", movementType);
            XRReflectionHelper.SetProperty(comp, "throwOnDetach", throwOnDetach);
            XRReflectionHelper.SetProperty(comp, "smoothPosition", smoothPosition);
            XRReflectionHelper.SetProperty(comp, "smoothRotation", smoothRotation);
            XRReflectionHelper.SetProperty(comp, "smoothPositionAmount", smoothPositionAmount);
            XRReflectionHelper.SetProperty(comp, "smoothRotationAmount", smoothRotationAmount);

            // Create and set custom attach transform if offset specified
            if (!string.IsNullOrEmpty(attachTransformOffset))
            {
                var offsets = ParseVector3(attachTransformOffset);
                if (offsets.HasValue)
                {
                    var attachGo = new GameObject("Attach Point");
                    attachGo.transform.SetParent(go.transform, false);
                    attachGo.transform.localPosition = offsets.Value;
                    XRReflectionHelper.SetProperty(comp, "attachTransform", attachGo.transform);
                }
            }

            WorkflowManager.SnapshotObject(go);

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                movementType,
                throwOnDetach,
                smoothPosition,
                smoothRotation,
                movementTypeOptions = XRReflectionHelper.GetEnumValues(comp, "movementType")
            };
#endif
        }

        [UnitySkill("xr_add_simple_interactable", "Add XRSimpleInteractable for hover/select event triggers (no grab physics)", TracksWorkflow = true,
            Category = SkillCategory.XR, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "xr", "simple", "interactable", "hover" },
            Outputs = new[] { "name", "instanceId", "interactableType" },
            RequiresInput = new[] { "gameObject" })]
        public static object XRAddSimpleInteractable(
            string name = null, int instanceId = 0, string path = null)
        {
#if !XRI
            return NoXRI();
#else
            var (go, findErr) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (findErr != null) return findErr;

            Undo.RecordObject(go, "Add XRSimpleInteractable");

            var comp = XRReflectionHelper.AddXRComponent(go, "XRSimpleInteractable");
            if (comp == null)
                return new { error = "Failed to add XRSimpleInteractable. Type not found in current XRI version." };

            Undo.RegisterCreatedObjectUndo(comp, "Add XRSimpleInteractable");

            // Ensure collider exists for interaction detection
            if (go.GetComponent<Collider>() == null)
                go.AddComponent<BoxCollider>();

            WorkflowManager.SnapshotObject(go);

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                interactableType = comp.GetType().Name,
                note = "Use xr_add_interaction_event to wire up hover/select callbacks."
            };
#endif
        }

        [UnitySkill("xr_configure_interactable", "Configure properties of an existing XR interactable (selectMode, movementType, throwOnDetach, etc.)", TracksWorkflow = true,
            Category = SkillCategory.XR, Operation = SkillOperation.Modify,
            Tags = new[] { "xr", "interactable", "configure", "properties" },
            Outputs = new[] { "name", "instanceId", "interactableType", "changedProperties" },
            RequiresInput = new[] { "gameObject" })]
        public static object XRConfigureInteractable(
            string name = null, int instanceId = 0, string path = null,
            string selectMode = null,
            string movementType = null,
            bool? throwOnDetach = null,
            bool? smoothPosition = null,
            bool? smoothRotation = null,
            float? smoothPositionAmount = null,
            float? smoothRotationAmount = null,
            bool? trackPosition = null,
            bool? trackRotation = null)
        {
#if !XRI
            return NoXRI();
#else
            var (go, findErr) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (findErr != null) return findErr;

            // Find any interactable component
            var comp = XRReflectionHelper.GetXRComponent(go, "XRGrabInteractable")
                    ?? XRReflectionHelper.GetXRComponent(go, "XRSimpleInteractable")
                    ?? XRReflectionHelper.GetXRComponent(go, "XRBaseInteractable");

            if (comp == null)
                return new { error = $"No XR interactable found on '{go.name}'. Add one via xr_add_grab_interactable or xr_add_simple_interactable." };

            Undo.RecordObject(comp, "Configure XR Interactable");
            WorkflowManager.SnapshotObject(comp);

            var changed = new List<string>();

            if (!string.IsNullOrEmpty(selectMode) && XRReflectionHelper.SetEnumProperty(comp, "selectMode", selectMode))
                changed.Add("selectMode");
            if (!string.IsNullOrEmpty(movementType) && XRReflectionHelper.SetEnumProperty(comp, "movementType", movementType))
                changed.Add("movementType");
            if (throwOnDetach.HasValue && XRReflectionHelper.SetProperty(comp, "throwOnDetach", throwOnDetach.Value))
                changed.Add("throwOnDetach");
            if (smoothPosition.HasValue && XRReflectionHelper.SetProperty(comp, "smoothPosition", smoothPosition.Value))
                changed.Add("smoothPosition");
            if (smoothRotation.HasValue && XRReflectionHelper.SetProperty(comp, "smoothRotation", smoothRotation.Value))
                changed.Add("smoothRotation");
            if (smoothPositionAmount.HasValue && XRReflectionHelper.SetProperty(comp, "smoothPositionAmount", smoothPositionAmount.Value))
                changed.Add("smoothPositionAmount");
            if (smoothRotationAmount.HasValue && XRReflectionHelper.SetProperty(comp, "smoothRotationAmount", smoothRotationAmount.Value))
                changed.Add("smoothRotationAmount");
            if (trackPosition.HasValue && XRReflectionHelper.SetProperty(comp, "trackPosition", trackPosition.Value))
                changed.Add("trackPosition");
            if (trackRotation.HasValue && XRReflectionHelper.SetProperty(comp, "trackRotation", trackRotation.Value))
                changed.Add("trackRotation");

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                interactableType = comp.GetType().Name,
                changedProperties = changed,
                selectModeOptions = XRReflectionHelper.GetEnumValues(comp, "selectMode"),
                movementTypeOptions = XRReflectionHelper.GetEnumValues(comp, "movementType")
            };
#endif
        }

        [UnitySkill("xr_list_interactables", "List all XR interactables in the scene with type and status",
            Category = SkillCategory.XR, Operation = SkillOperation.Query,
            Tags = new[] { "xr", "interactables", "list", "scene" },
            Outputs = new[] { "count", "interactables", "xriVersion" },
            ReadOnly = true)]
        public static object XRListInteractables(bool verbose = false)
        {
#if !XRI
            return NoXRI();
#else
            var interactableTypes = new[] { "XRGrabInteractable", "XRSimpleInteractable" };
            var results = new List<object>();

            foreach (var typeName in interactableTypes)
            {
                var found = XRReflectionHelper.FindComponentsOfXRType(typeName);
                foreach (var comp in found)
                {
                    var entry = new Dictionary<string, object>
                    {
                        ["type"] = comp.GetType().Name,
                        ["gameObject"] = comp.gameObject.name,
                        ["instanceId"] = comp.gameObject.GetInstanceID(),
                        ["path"] = GameObjectFinder.GetPath(comp.gameObject),
                        ["enabled"] = comp is Behaviour b ? b.enabled : true,
                        ["isSelected"] = (bool)(XRReflectionHelper.GetProperty(comp, "isSelected") ?? false),
                        ["isHovered"] = (bool)(XRReflectionHelper.GetProperty(comp, "isHovered") ?? false)
                    };

                    if (verbose)
                        entry["properties"] = XRReflectionHelper.GetComponentInfo(comp);

                    results.Add(entry);
                }
            }

            // Also find TeleportationArea/Anchor since they are interactables
            foreach (var typeName in new[] { "TeleportationArea", "TeleportationAnchor" })
            {
                var found = XRReflectionHelper.FindComponentsOfXRType(typeName);
                foreach (var comp in found)
                {
                    results.Add(new Dictionary<string, object>
                    {
                        ["type"] = comp.GetType().Name,
                        ["gameObject"] = comp.gameObject.name,
                        ["instanceId"] = comp.gameObject.GetInstanceID(),
                        ["path"] = GameObjectFinder.GetPath(comp.gameObject),
                        ["enabled"] = comp is Behaviour b2 ? b2.enabled : true
                    });
                }
            }

            return new
            {
                success = true,
                count = results.Count,
                interactables = results,
                xriVersion = XRReflectionHelper.XRIMajorVersion
            };
#endif
        }

        // ==================================================================================
        // Locomotion Skills (5 skills)
        // ==================================================================================

        [UnitySkill("xr_setup_teleportation", "Set up TeleportationProvider on XR Origin for teleport locomotion", TracksWorkflow = true,
            Category = SkillCategory.XR, Operation = SkillOperation.Create,
            Tags = new[] { "xr", "teleportation", "locomotion", "provider" },
            Outputs = new[] { "name", "instanceId", "providerType" })]
        public static object XRSetupTeleportation(
            string name = null, int instanceId = 0, string path = null)
        {
#if !XRI
            return NoXRI();
#else
            // Find XR Origin
            GameObject go;
            if (string.IsNullOrEmpty(name) && instanceId == 0 && string.IsNullOrEmpty(path))
            {
                var origin = XRReflectionHelper.FindFirstOfXRType("XROrigin");
                if (origin == null)
                    return new { error = "No XR Origin found in scene. Create one via xr_setup_rig, or specify the target object." };
                go = origin.gameObject;
            }
            else
            {
                var (found, findErr) = GameObjectFinder.FindOrError(name, instanceId, path);
                if (findErr != null) return findErr;
                go = found;
            }

            Undo.RecordObject(go, "Setup Teleportation");

            var comp = XRReflectionHelper.AddXRComponent(go, "TeleportationProvider");
            if (comp == null)
                return new { error = "Failed to add TeleportationProvider. Type not found in current XRI version." };

            Undo.RegisterCreatedObjectUndo(comp, "Add TeleportationProvider");
            WorkflowManager.SnapshotObject(go);

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                providerType = comp.GetType().Name,
                note = "Now create teleport targets via xr_add_teleport_area or xr_add_teleport_anchor."
            };
#endif
        }

        [UnitySkill("xr_add_teleport_area", "Add TeleportationArea to a surface for teleport destination", TracksWorkflow = true,
            Category = SkillCategory.XR, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "xr", "teleport", "area", "destination" },
            Outputs = new[] { "name", "instanceId", "teleportType", "matchOrientation" },
            RequiresInput = new[] { "gameObject" })]
        public static object XRAddTeleportArea(
            string name = null, int instanceId = 0, string path = null,
            string matchOrientation = "WorldSpaceUp")
        {
#if !XRI
            return NoXRI();
#else
            var (go, findErr) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (findErr != null) return findErr;

            Undo.RecordObject(go, "Add TeleportationArea");

            var comp = XRReflectionHelper.AddXRComponent(go, "TeleportationArea");
            if (comp == null)
                return new { error = "Failed to add TeleportationArea. Type not found in current XRI version." };

            Undo.RegisterCreatedObjectUndo(comp, "Add TeleportationArea");

            if (!string.IsNullOrEmpty(matchOrientation))
                XRReflectionHelper.SetEnumProperty(comp, "matchOrientation", matchOrientation);

            // Ensure collider for raycast detection
            if (go.GetComponent<Collider>() == null)
            {
                var meshFilter = go.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null)
                    go.AddComponent<MeshCollider>();
                else
                    go.AddComponent<BoxCollider>();
            }

            WorkflowManager.SnapshotObject(go);

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                teleportType = "TeleportationArea",
                matchOrientation,
                matchOrientationOptions = XRReflectionHelper.GetEnumValues(comp, "matchOrientation")
            };
#endif
        }

        [UnitySkill("xr_add_teleport_anchor", "Create a teleport anchor at a specific position and rotation", TracksWorkflow = true,
            Category = SkillCategory.XR, Operation = SkillOperation.Create,
            Tags = new[] { "xr", "teleport", "anchor", "waypoint" },
            Outputs = new[] { "name", "instanceId", "teleportType", "position", "rotationY" })]
        public static object XRAddTeleportAnchor(
            string name = "Teleport Anchor",
            float x = 0, float y = 0, float z = 0,
            float rotY = 0,
            string matchOrientation = "TargetUpAndForward",
            string parent = null)
        {
#if !XRI
            return NoXRI();
#else
            var go = new GameObject(name);
            go.transform.position = new Vector3(x, y, z);
            go.transform.rotation = Quaternion.Euler(0, rotY, 0);

            if (!string.IsNullOrEmpty(parent))
            {
                var parentGo = GameObjectFinder.Find(parent);
                if (parentGo != null)
                    go.transform.SetParent(parentGo.transform, true);
            }

            var comp = XRReflectionHelper.AddXRComponent(go, "TeleportationAnchor");
            if (comp == null)
            {
                UnityEngine.Object.DestroyImmediate(go);
                return new { error = "Failed to add TeleportationAnchor. Type not found in current XRI version." };
            }

            if (!string.IsNullOrEmpty(matchOrientation))
                XRReflectionHelper.SetEnumProperty(comp, "matchOrientation", matchOrientation);

            // Add a small collider for raycast detection
            var collider = go.AddComponent<BoxCollider>();
            collider.size = new Vector3(1, 0.01f, 1);

            // Add visual indicator
            var visual = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            visual.name = "Anchor Visual";
            visual.transform.SetParent(go.transform, false);
            visual.transform.localScale = new Vector3(1, 0.02f, 1);
            var renderer = visual.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = new Material(Shader.Find("Sprites/Default"));
                renderer.sharedMaterial.color = new Color(0, 0.8f, 1, 0.5f);
            }
            // Remove auto-generated collider from visual primitive
            var visualCollider = visual.GetComponent<Collider>();
            if (visualCollider != null)
                UnityEngine.Object.DestroyImmediate(visualCollider);

            Undo.RegisterCreatedObjectUndo(go, "Create Teleport Anchor");
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                teleportType = "TeleportationAnchor",
                position = new { x, y, z },
                rotationY = rotY,
                matchOrientation
            };
#endif
        }

        [UnitySkill("xr_setup_continuous_move", "Add continuous movement provider to XR Origin (joystick-based locomotion)", TracksWorkflow = true,
            Category = SkillCategory.XR, Operation = SkillOperation.Create,
            Tags = new[] { "xr", "continuous", "move", "locomotion" },
            Outputs = new[] { "name", "instanceId", "providerType", "moveSpeed", "enableStrafe", "enableFly" })]
        public static object XRSetupContinuousMove(
            string name = null, int instanceId = 0, string path = null,
            float moveSpeed = 2f,
            bool enableStrafe = true,
            bool enableFly = false)
        {
#if !XRI
            return NoXRI();
#else
            // Find XR Origin
            GameObject go;
            if (string.IsNullOrEmpty(name) && instanceId == 0 && string.IsNullOrEmpty(path))
            {
                var origin = XRReflectionHelper.FindFirstOfXRType("XROrigin");
                if (origin == null)
                    return new { error = "No XR Origin found in scene. Create one via xr_setup_rig, or specify the target object." };
                go = origin.gameObject;
            }
            else
            {
                var (found, findErr) = GameObjectFinder.FindOrError(name, instanceId, path);
                if (findErr != null) return findErr;
                go = found;
            }

            Undo.RecordObject(go, "Setup Continuous Move");

            // Try ActionBased first, then generic
            var comp = XRReflectionHelper.AddXRComponent(go, "ActionBasedContinuousMoveProvider")
                    ?? XRReflectionHelper.AddXRComponent(go, "ContinuousMoveProvider");

            if (comp == null)
                return new { error = "Failed to add ContinuousMoveProvider. Type not found in current XRI version." };

            Undo.RegisterCreatedObjectUndo(comp, "Add ContinuousMoveProvider");

            XRReflectionHelper.SetProperty(comp, "moveSpeed", moveSpeed);
            XRReflectionHelper.SetProperty(comp, "enableStrafe", enableStrafe);
            XRReflectionHelper.SetProperty(comp, "enableFly", enableFly);

            WorkflowManager.SnapshotObject(go);

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                providerType = comp.GetType().Name,
                moveSpeed,
                enableStrafe,
                enableFly
            };
#endif
        }

        [UnitySkill("xr_setup_turn_provider", "Add snap or continuous turn provider to XR Origin", TracksWorkflow = true,
            Category = SkillCategory.XR, Operation = SkillOperation.Create,
            Tags = new[] { "xr", "turn", "snap", "locomotion" },
            Outputs = new[] { "name", "instanceId", "providerType", "turnType", "turnAmount", "turnSpeed" })]
        public static object XRSetupTurnProvider(
            string name = null, int instanceId = 0, string path = null,
            string turnType = "Snap",
            float turnAmount = 45f,
            float turnSpeed = 90f)
        {
#if !XRI
            return NoXRI();
#else
            // Find XR Origin
            GameObject go;
            if (string.IsNullOrEmpty(name) && instanceId == 0 && string.IsNullOrEmpty(path))
            {
                var origin = XRReflectionHelper.FindFirstOfXRType("XROrigin");
                if (origin == null)
                    return new { error = "No XR Origin found in scene. Create one via xr_setup_rig, or specify the target object." };
                go = origin.gameObject;
            }
            else
            {
                var (found, findErr) = GameObjectFinder.FindOrError(name, instanceId, path);
                if (findErr != null) return findErr;
                go = found;
            }

            Undo.RecordObject(go, "Setup Turn Provider");

            bool isSnap = turnType.Equals("Snap", StringComparison.OrdinalIgnoreCase);
            Component comp;

            if (isSnap)
            {
                comp = XRReflectionHelper.AddXRComponent(go, "ActionBasedSnapTurnProvider")
                    ?? XRReflectionHelper.AddXRComponent(go, "SnapTurnProvider");
                if (comp != null)
                    XRReflectionHelper.SetProperty(comp, "turnAmount", turnAmount);
            }
            else
            {
                comp = XRReflectionHelper.AddXRComponent(go, "ActionBasedContinuousTurnProvider")
                    ?? XRReflectionHelper.AddXRComponent(go, "ContinuousTurnProvider");
                if (comp != null)
                    XRReflectionHelper.SetProperty(comp, "turnSpeed", turnSpeed);
            }

            if (comp == null)
                return new { error = $"Failed to add {turnType}TurnProvider. Type not found in current XRI version." };

            Undo.RegisterCreatedObjectUndo(comp, "Add Turn Provider");
            WorkflowManager.SnapshotObject(go);

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                providerType = comp.GetType().Name,
                turnType,
                turnAmount = isSnap ? turnAmount : 0f,
                turnSpeed = isSnap ? 0f : turnSpeed
            };
#endif
        }

        // ==================================================================================
        // Advanced Skills (4 skills)
        // ==================================================================================

        [UnitySkill("xr_setup_ui_canvas", "Make a Canvas XR-compatible by adding TrackedDeviceGraphicRaycaster", TracksWorkflow = true,
            Category = SkillCategory.XR, Operation = SkillOperation.Modify,
            Tags = new[] { "xr", "ui", "canvas", "raycaster" },
            Outputs = new[] { "name", "instanceId", "removedStandardRaycaster", "addedTrackedDeviceRaycaster", "renderMode" },
            RequiresInput = new[] { "gameObject" })]
        public static object XRSetupUICanvas(
            string name = null, int instanceId = 0, string path = null)
        {
#if !XRI
            return NoXRI();
#else
            var (go, findErr) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (findErr != null) return findErr;

            var canvas = go.GetComponent<Canvas>();
            if (canvas == null)
                return new { error = $"'{go.name}' does not have a Canvas component." };

            Undo.RecordObject(go, "Setup XR UI Canvas");

            // Set Canvas to WorldSpace for XR
            if (canvas.renderMode != RenderMode.WorldSpace)
            {
                canvas.renderMode = RenderMode.WorldSpace;
                // Set reasonable defaults for world-space XR canvas
                var rt = canvas.GetComponent<RectTransform>();
                if (rt != null)
                {
                    rt.sizeDelta = new Vector2(400, 300);
                    rt.localScale = new Vector3(0.001f, 0.001f, 0.001f);
                }
            }

            // Remove standard GraphicRaycaster
            var standardRaycaster = go.GetComponent<UnityEngine.UI.GraphicRaycaster>();
            bool removedStandard = false;
            if (standardRaycaster != null)
            {
                Undo.DestroyObjectImmediate(standardRaycaster);
                removedStandard = true;
            }

            // Add TrackedDeviceGraphicRaycaster
            var trackedRaycaster = XRReflectionHelper.AddXRComponent(go, "TrackedDeviceGraphicRaycaster");
            bool addedTracked = trackedRaycaster != null;

            WorkflowManager.SnapshotObject(go);

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                removedStandardRaycaster = removedStandard,
                addedTrackedDeviceRaycaster = addedTracked,
                renderMode = "WorldSpace",
                note = addedTracked ? null : "TrackedDeviceGraphicRaycaster type not found. Ensure XRI UI module is installed."
            };
#endif
        }

        [UnitySkill("xr_configure_haptics", "Configure haptic feedback parameters on an XR interactor", TracksWorkflow = true,
            Category = SkillCategory.XR, Operation = SkillOperation.Modify,
            Tags = new[] { "xr", "haptics", "feedback", "vibration" },
            Outputs = new[] { "name", "instanceId", "interactorType", "changedProperties" },
            RequiresInput = new[] { "gameObject" })]
        public static object XRConfigureHaptics(
            string name = null, int instanceId = 0, string path = null,
            float selectIntensity = 0.5f,
            float selectDuration = 0.1f,
            float hoverIntensity = 0.1f,
            float hoverDuration = 0.05f)
        {
#if !XRI
            return NoXRI();
#else
            var (go, findErr) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (findErr != null) return findErr;

            // Find any interactor component
            var comp = XRReflectionHelper.GetXRComponent(go, "XRRayInteractor")
                    ?? XRReflectionHelper.GetXRComponent(go, "XRDirectInteractor")
                    ?? XRReflectionHelper.GetXRComponent(go, "XRSocketInteractor")
                    ?? XRReflectionHelper.GetXRComponent(go, "XRBaseInteractor");

            if (comp == null)
                return new { error = $"No XR interactor found on '{go.name}'." };

            Undo.RecordObject(comp, "Configure Haptics");
            WorkflowManager.SnapshotObject(comp);

            var changed = new List<string>();

            // Haptic properties vary by version but try common names
            if (XRReflectionHelper.SetProperty(comp, "playHapticsOnSelectEntered", true))
                changed.Add("playHapticsOnSelectEntered");
            if (XRReflectionHelper.SetProperty(comp, "hapticSelectEnterIntensity", selectIntensity))
                changed.Add("hapticSelectEnterIntensity");
            if (XRReflectionHelper.SetProperty(comp, "hapticSelectEnterDuration", selectDuration))
                changed.Add("hapticSelectEnterDuration");
            if (XRReflectionHelper.SetProperty(comp, "playHapticsOnHoverEntered", hoverIntensity > 0))
                changed.Add("playHapticsOnHoverEntered");
            if (XRReflectionHelper.SetProperty(comp, "hapticHoverEnterIntensity", hoverIntensity))
                changed.Add("hapticHoverEnterIntensity");
            if (XRReflectionHelper.SetProperty(comp, "hapticHoverEnterDuration", hoverDuration))
                changed.Add("hapticHoverEnterDuration");

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                interactorType = comp.GetType().Name,
                changedProperties = changed,
                selectIntensity,
                selectDuration,
                hoverIntensity,
                hoverDuration,
                note = changed.Count == 0 ? "Haptic properties not found on this interactor type. Haptics API may differ in your XRI version." : null
            };
#endif
        }

        [UnitySkill("xr_add_interaction_event", "Wire up an interaction event (selectEntered/selectExited/hoverEntered/hoverExited/activated) to a target method via UnityEvent", TracksWorkflow = true,
            Category = SkillCategory.XR, Operation = SkillOperation.Modify,
            Tags = new[] { "xr", "interaction", "event", "callback" },
            Outputs = new[] { "name", "instanceId", "eventType", "targetObject", "targetMethod" },
            RequiresInput = new[] { "gameObject", "targetName", "targetMethod" })]
        public static object XRAddInteractionEvent(
            string name = null, int instanceId = 0, string path = null,
            string eventType = "selectEntered",
            string targetName = null,
            string targetMethod = null)
        {
#if !XRI
            return NoXRI();
#else
            if (Validate.Required(targetName, "targetName") is object err1) return err1;
            if (Validate.Required(targetMethod, "targetMethod") is object err2) return err2;

            var (go, findErr) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (findErr != null) return findErr;

            // Find interactable
            var comp = XRReflectionHelper.GetXRComponent(go, "XRGrabInteractable")
                    ?? XRReflectionHelper.GetXRComponent(go, "XRSimpleInteractable")
                    ?? XRReflectionHelper.GetXRComponent(go, "XRBaseInteractable");

            if (comp == null)
                return new { error = $"No XR interactable found on '{go.name}'." };

            // Find target object
            var targetGo = GameObjectFinder.Find(targetName);
            if (targetGo == null)
                return new { error = $"Target GameObject '{targetName}' not found." };

            // Find the event property
            var eventProp = comp.GetType().GetProperty(eventType,
                BindingFlags.Public | BindingFlags.Instance);
            if (eventProp == null)
                return new
                {
                    error = $"Event '{eventType}' not found on {comp.GetType().Name}.",
                    availableEvents = new[] { "selectEntered", "selectExited", "hoverEntered", "hoverExited",
                        "firstSelectEntered", "lastSelectExited", "firstHoverEntered", "lastHoverExited",
                        "activated", "deactivated" }
                };

            Undo.RecordObject(comp, "Add XR Interaction Event");
            WorkflowManager.SnapshotObject(comp);

            // Get the UnityEvent and add persistent listener
            var eventObj = eventProp.GetValue(comp);
            if (eventObj == null)
                return new { error = $"Event '{eventType}' returned null." };

            // Find target method on any component of the target
            MonoBehaviour targetComponent = null;
            MethodInfo targetMethodInfo = null;
            foreach (var targetComp in targetGo.GetComponents<MonoBehaviour>())
            {
                var method = targetComp.GetType().GetMethod(targetMethod,
                    BindingFlags.Public | BindingFlags.Instance);
                if (method != null)
                {
                    targetComponent = targetComp;
                    targetMethodInfo = method;
                    break;
                }
            }

            if (targetComponent == null || targetMethodInfo == null)
                return new { error = $"Method '{targetMethod}' not found on any component of '{targetName}'." };

            // Use UnityEventTools to add persistent listener
            var addMethod = typeof(UnityEditor.Events.UnityEventTools).GetMethods()
                .FirstOrDefault(m => m.Name == "AddVoidPersistentListener" && m.GetParameters().Length == 2);

            if (addMethod != null)
            {
                var action = Delegate.CreateDelegate(typeof(UnityEngine.Events.UnityAction), targetComponent, targetMethodInfo, false);
                if (action != null)
                {
                    addMethod.Invoke(null, new object[] { eventObj, action });
                }
            }

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                eventType,
                targetObject = targetName,
                targetMethod,
                interactableType = comp.GetType().Name
            };
#endif
        }

        [UnitySkill("xr_configure_interaction_layers", "Configure interaction layer mask on an XR interactor or interactable", TracksWorkflow = true,
            Category = SkillCategory.XR, Operation = SkillOperation.Modify,
            Tags = new[] { "xr", "interaction", "layers", "mask" },
            Outputs = new[] { "name", "instanceId", "componentType", "layers" },
            RequiresInput = new[] { "gameObject" })]
        public static object XRConfigureInteractionLayers(
            string name = null, int instanceId = 0, string path = null,
            string layers = "Default",
            bool isInteractor = true)
        {
#if !XRI
            return NoXRI();
#else
            var (go, findErr) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (findErr != null) return findErr;

            // Find the XR component
            Component comp;
            if (isInteractor)
            {
                comp = XRReflectionHelper.GetXRComponent(go, "XRRayInteractor")
                    ?? XRReflectionHelper.GetXRComponent(go, "XRDirectInteractor")
                    ?? XRReflectionHelper.GetXRComponent(go, "XRSocketInteractor")
                    ?? XRReflectionHelper.GetXRComponent(go, "XRBaseInteractor");
            }
            else
            {
                comp = XRReflectionHelper.GetXRComponent(go, "XRGrabInteractable")
                    ?? XRReflectionHelper.GetXRComponent(go, "XRSimpleInteractable")
                    ?? XRReflectionHelper.GetXRComponent(go, "XRBaseInteractable");
            }

            if (comp == null)
                return new { error = $"No XR {(isInteractor ? "interactor" : "interactable")} found on '{go.name}'." };

            Undo.RecordObject(comp, "Configure Interaction Layers");
            WorkflowManager.SnapshotObject(comp);

            // Try to set interaction layers via InteractionLayerMask
            var ilmType = XRReflectionHelper.ResolveXRType("InteractionLayerMask");
            if (ilmType != null)
            {
                var getMethod = ilmType.GetMethod("GetMask", BindingFlags.Public | BindingFlags.Static);
                if (getMethod != null)
                {
                    try
                    {
                        var layerNames = layers.Split(',').Select(l => l.Trim()).ToArray();
                        var mask = getMethod.Invoke(null, new object[] { layerNames });
                        XRReflectionHelper.SetProperty(comp, "interactionLayers", mask);
                    }
                    catch
                    {
                        // Fallback: try setting by integer value
                        if (int.TryParse(layers, out int layerMask))
                            XRReflectionHelper.SetProperty(comp, "interactionLayers", layerMask);
                    }
                }
            }

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                componentType = comp.GetType().Name,
                layers,
                isInteractor
            };
#endif
        }

        // ==================================================================================
        // Helpers
        // ==================================================================================

        private static Vector3? ParseVector3(string csv)
        {
            if (string.IsNullOrEmpty(csv)) return null;
            var parts = csv.Split(',');
            if (parts.Length != 3) return null;
            if (float.TryParse(parts[0].Trim(), out var x) &&
                float.TryParse(parts[1].Trim(), out var y) &&
                float.TryParse(parts[2].Trim(), out var z))
                return new Vector3(x, y, z);
            return null;
        }

        private static Type FindTrackedPoseDriverType()
        {
            // Unity 2022+: UnityEngine.InputSystem.XR.TrackedPoseDriver (Input System package)
            var newType = XRReflectionHelper.FindTypeInAssemblies("UnityEngine.InputSystem.XR.TrackedPoseDriver");
            if (newType != null) return newType;

            // Legacy: UnityEngine.XR.TrackedPoseDriver (built-in)
            var legacyType = XRReflectionHelper.FindTypeInAssemblies("UnityEngine.XR.TrackedPoseDriver");
            if (legacyType != null) return legacyType;

            // Fallback: UnityEngine.SpatialTracking.TrackedPoseDriver
            return XRReflectionHelper.FindTypeInAssemblies("UnityEngine.SpatialTracking.TrackedPoseDriver");
        }
    }
}
