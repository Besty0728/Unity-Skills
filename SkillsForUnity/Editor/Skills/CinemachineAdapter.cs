using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using Object = UnityEngine.Object;
using UnitySkills.Internal;

#if CINEMACHINE_3
using Unity.Cinemachine;
#elif CINEMACHINE_2
using Cinemachine;
#endif

namespace UnitySkills
{
#if CINEMACHINE_2 || CINEMACHINE_3
    /// <summary>
    /// Adapter layer that abstracts Cinemachine 2.x vs 3.x API differences.
    /// All version-specific #if blocks are concentrated here so that CinemachineSkills
    /// methods can be written without conditional compilation.
    /// </summary>
    internal static class CinemachineAdapter
    {
        // ===================== VCam Type =====================

#if CINEMACHINE_3
        public const string VCamTypeName = "CinemachineCamera";
#else
        public const string VCamTypeName = "CinemachineVirtualCamera";
#endif

        public static MonoBehaviour GetVCam(GameObject go)
        {
#if CINEMACHINE_3
            return go.GetComponent<CinemachineCamera>();
#else
            return go.GetComponent<CinemachineVirtualCamera>();
#endif
        }

        /// <summary>Returns null if vcam found, or an error object if not.</summary>
        public static object VCamOrError(MonoBehaviour vcam)
        {
            return vcam != null ? null : new { error = $"Not a {VCamTypeName}" };
        }

        // ===================== Follow / LookAt =====================

        public static Transform GetFollow(MonoBehaviour vcam)
        {
#if CINEMACHINE_3
            return ((CinemachineCamera)vcam).Follow;
#else
            return ((CinemachineVirtualCamera)vcam).m_Follow;
#endif
        }

        public static void SetFollow(MonoBehaviour vcam, Transform target)
        {
#if CINEMACHINE_3
            ((CinemachineCamera)vcam).Follow = target;
#else
            ((CinemachineVirtualCamera)vcam).m_Follow = target;
#endif
        }

        public static Transform GetLookAt(MonoBehaviour vcam)
        {
#if CINEMACHINE_3
            return ((CinemachineCamera)vcam).LookAt;
#else
            return ((CinemachineVirtualCamera)vcam).m_LookAt;
#endif
        }

        public static void SetLookAt(MonoBehaviour vcam, Transform target)
        {
#if CINEMACHINE_3
            ((CinemachineCamera)vcam).LookAt = target;
#else
            ((CinemachineVirtualCamera)vcam).m_LookAt = target;
#endif
        }

        // ===================== Priority =====================

        public static int GetPriority(MonoBehaviour vcam)
        {
#if CINEMACHINE_3
            return ((CinemachineCamera)vcam).Priority.Value;
#else
            return ((CinemachineVirtualCamera)vcam).m_Priority;
#endif
        }

        public static void SetPriority(MonoBehaviour vcam, int value)
        {
#if CINEMACHINE_3
            ((CinemachineCamera)vcam).Priority.Value = value;
#else
            ((CinemachineVirtualCamera)vcam).m_Priority = value;
#endif
        }

        // ===================== Lens =====================

        public static LensSettings GetLens(MonoBehaviour vcam)
        {
#if CINEMACHINE_3
            return ((CinemachineCamera)vcam).Lens;
#else
            return ((CinemachineVirtualCamera)vcam).m_Lens;
#endif
        }

        public static void SetLens(MonoBehaviour vcam, LensSettings lens)
        {
#if CINEMACHINE_3
            ((CinemachineCamera)vcam).Lens = lens;
#else
            ((CinemachineVirtualCamera)vcam).m_Lens = lens;
#endif
        }

        // ===================== Noise =====================

        public static void SetNoiseGains(CinemachineBasicMultiChannelPerlin perlin, float amplitude, float frequency)
        {
#if CINEMACHINE_3
            perlin.AmplitudeGain = amplitude;
            perlin.FrequencyGain = frequency;
#else
            perlin.m_AmplitudeGain = amplitude;
            perlin.m_FrequencyGain = frequency;
#endif
        }

        // ===================== Brain =====================

        public static string GetBrainUpdateMethod(CinemachineBrain brain)
        {
#if CINEMACHINE_3
            return brain.UpdateMethod.ToString();
#else
            return brain.m_UpdateMethod.ToString();
#endif
        }

        // ===================== Assembly / Type Lookup =====================

        public static System.Reflection.Assembly CmAssembly =>
#if CINEMACHINE_3
            typeof(CinemachineCamera).Assembly;
#else
            typeof(CinemachineVirtualCamera).Assembly;
#endif

        private static readonly Dictionary<string, string> AliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
#if CINEMACHINE_3
            { "OrbitalFollow", "CinemachineOrbitalFollow" },
            { "Follow", "CinemachineFollow" },
            { "Transposer", "CinemachineFollow" },
            { "Composer", "CinemachineRotationComposer" },
            { "RotationComposer", "CinemachineRotationComposer" },
            { "PositionComposer", "CinemachinePositionComposer" },
            { "FramingTransposer", "CinemachinePositionComposer" },
            { "PanTilt", "CinemachinePanTilt" },
            { "POV", "CinemachinePanTilt" },
            { "SameAsFollow", "CinemachineSameAsFollowTarget" },
            { "RotateWithFollow", "CinemachineRotateWithFollowTarget" },
            { "HardLockToTarget", "CinemachineHardLockToTarget" },
            { "HardLookAt", "CinemachineHardLookAt" },
            { "Perlin", "CinemachineBasicMultiChannelPerlin" },
            { "Noise", "CinemachineBasicMultiChannelPerlin" },
            { "Impulse", "CinemachineImpulseSource" },
            { "ImpulseListener", "CinemachineImpulseListener" },
            { "ThirdPersonFollow", "CinemachineThirdPersonFollow" },
            { "3rdPersonFollow", "CinemachineThirdPersonFollow" },
            { "SplineDolly", "CinemachineSplineDolly" },
            { "TrackedDolly", "CinemachineSplineDolly" },
            { "Confiner", "CinemachineConfiner3D" },
            { "Confiner2D", "CinemachineConfiner2D" },
            { "Confiner3D", "CinemachineConfiner3D" },
            { "Deoccluder", "CinemachineDeoccluder" },
            { "Collider", "CinemachineDeoccluder" },
            { "Decollider", "CinemachineDecollider" },
            { "FollowZoom", "CinemachineFollowZoom" },
            { "GroupFraming", "CinemachineGroupFraming" },
            { "GroupComposer", "CinemachineGroupFraming" },
            { "FreeLookModifier", "CinemachineFreeLookModifier" },
            { "Recomposer", "CinemachineRecomposer" },
            { "Storyboard", "CinemachineStoryboard" },
            { "ThirdPersonAim", "CinemachineThirdPersonAim" },
            { "AutoFocus", "CinemachineAutoFocus" },
            { "Sequencer", "CinemachineSequencerCamera" },
            { "BlendList", "CinemachineSequencerCamera" }
#else
            { "Transposer", "CinemachineTransposer" },
            { "Follow", "CinemachineTransposer" },
            { "Composer", "CinemachineComposer" },
            { "RotationComposer", "CinemachineComposer" },
            { "FramingTransposer", "CinemachineFramingTransposer" },
            { "PositionComposer", "CinemachineFramingTransposer" },
            { "HardLockToTarget", "CinemachineHardLockToTarget" },
            { "HardLookAt", "CinemachineHardLookAt" },
            { "Perlin", "CinemachineBasicMultiChannelPerlin" },
            { "Noise", "CinemachineBasicMultiChannelPerlin" },
            { "Impulse", "CinemachineImpulseSource" },
            { "ImpulseListener", "CinemachineImpulseListener" },
            { "POV", "CinemachinePOV" },
            { "PanTilt", "CinemachinePOV" },
            { "OrbitalTransposer", "CinemachineOrbitalTransposer" },
            { "OrbitalFollow", "CinemachineOrbitalTransposer" },
            { "3rdPersonFollow", "Cinemachine3rdPersonFollow" },
            { "ThirdPersonFollow", "Cinemachine3rdPersonFollow" },
            { "TrackedDolly", "CinemachineTrackedDolly" },
            { "SplineDolly", "CinemachineTrackedDolly" },
            { "SameAsFollow", "CinemachineSameAsFollowTarget" },
            { "RotateWithFollow", "CinemachineSameAsFollowTarget" },
            { "Confiner", "CinemachineConfiner" },
            { "Confiner2D", "CinemachineConfiner2D" },
            { "Confiner3D", "CinemachineConfiner" },
            { "Collider", "CinemachineCollider" },
            { "Deoccluder", "CinemachineCollider" },
            { "FollowZoom", "CinemachineFollowZoom" },
            { "GroupComposer", "CinemachineGroupComposer" },
            { "Recomposer", "CinemachineRecomposer" },
            { "Storyboard", "CinemachineStoryboard" },
            { "FreeLook", "CinemachineFreeLook" },
            { "BlendList", "CinemachineBlendListCamera" },
            { "Sequencer", "CinemachineBlendListCamera" }
#endif
        };

        private static readonly string CmNamespace =
#if CINEMACHINE_3
            "Unity.Cinemachine.";
#else
            "Cinemachine.";
#endif

        public static Type FindCinemachineType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            if (AliasMap.TryGetValue(name, out var fullName)) name = fullName;
            if (!name.StartsWith("Cinemachine")) name = "Cinemachine" + name;

            var type = CmAssembly.GetType(CmNamespace + name, false, true);
            if (type == null) type = CmAssembly.GetType(name, false, true);
            return type;
        }

        // ===================== Find All VCams =====================

        public static MonoBehaviour[] FindAllVCams()
        {
#if CINEMACHINE_3
            return FindHelper.FindAll<CinemachineCamera>().Cast<MonoBehaviour>().ToArray();
#else
            return FindHelper.FindAll<CinemachineVirtualCamera>().Cast<MonoBehaviour>().ToArray();
#endif
        }

        public static int GetMaxPriority()
        {
            var all = FindAllVCams();
            int max = 0;
            foreach (var v in all) { int p = GetPriority(v); if (p > max) max = p; }
            return max;
        }

        // ===================== Brain Write =====================

        public static CinemachineBrain FindBrain()
        {
            var mainCam = Camera.main;
            if (mainCam != null)
            {
                var brain = mainCam.GetComponent<CinemachineBrain>();
                if (brain != null) return brain;
            }
            return Object.FindAnyObjectByType<CinemachineBrain>();
        }

        public static void SetBrainUpdateMethod(CinemachineBrain brain, string method)
        {
#if CINEMACHINE_3
            if (System.Enum.TryParse<CinemachineBrain.UpdateMethods>(method, true, out var v))
                brain.UpdateMethod = v;
#else
            if (System.Enum.TryParse<CinemachineBrain.UpdateMethod>(method, true, out var v))
                brain.m_UpdateMethod = v;
#endif
        }

        public static void SetBrainBlendUpdateMethod(CinemachineBrain brain, string method)
        {
#if CINEMACHINE_3
            if (System.Enum.TryParse<CinemachineBrain.BrainUpdateMethods>(method, true, out var v))
                brain.BlendUpdateMethod = v;
#else
            if (System.Enum.TryParse<CinemachineBrain.BrainUpdateMethod>(method, true, out var v))
                brain.m_BlendUpdateMethod = v;
#endif
        }

        public static string GetBrainBlendUpdateMethod(CinemachineBrain brain)
        {
#if CINEMACHINE_3
            return brain.BlendUpdateMethod.ToString();
#else
            return brain.m_BlendUpdateMethod.ToString();
#endif
        }

        public static bool GetBrainBool(CinemachineBrain brain, string propName)
        {
#if CINEMACHINE_3
            switch (propName)
            {
                case "ShowDebugText": return brain.ShowDebugText;
                case "ShowCameraFrustum": return brain.ShowCameraFrustum;
                case "IgnoreTimeScale": return brain.IgnoreTimeScale;
            }
#else
            switch (propName)
            {
                case "ShowDebugText": return brain.m_ShowDebugText;
                case "ShowCameraFrustum": return brain.m_ShowCameraFrustum;
                case "IgnoreTimeScale": return brain.m_IgnoreTimeScale;
            }
#endif
            return false;
        }

        public static void SetBrainBool(CinemachineBrain brain, string propName, bool value)
        {
#if CINEMACHINE_3
            switch (propName)
            {
                case "ShowDebugText": brain.ShowDebugText = value; break;
                case "ShowCameraFrustum": brain.ShowCameraFrustum = value; break;
                case "IgnoreTimeScale": brain.IgnoreTimeScale = value; break;
            }
#else
            switch (propName)
            {
                case "ShowDebugText": brain.m_ShowDebugText = value; break;
                case "ShowCameraFrustum": brain.m_ShowCameraFrustum = value; break;
                case "IgnoreTimeScale": brain.m_IgnoreTimeScale = value; break;
            }
#endif
        }

        // ===================== Blend Definition =====================

        public static CinemachineBlendDefinition GetBrainDefaultBlend(CinemachineBrain brain)
        {
#if CINEMACHINE_3
            return brain.DefaultBlend;
#else
            return brain.m_DefaultBlend;
#endif
        }

        public static void SetBrainDefaultBlend(CinemachineBrain brain, CinemachineBlendDefinition blend)
        {
#if CINEMACHINE_3
            brain.DefaultBlend = blend;
#else
            brain.m_DefaultBlend = blend;
#endif
        }

        public static string GetBlendStyle(CinemachineBlendDefinition blend)
        {
#if CINEMACHINE_3
            return blend.Style.ToString();
#else
            return blend.m_Style.ToString();
#endif
        }

        public static float GetBlendTime(CinemachineBlendDefinition blend)
        {
#if CINEMACHINE_3
            return blend.Time;
#else
            return blend.m_Time;
#endif
        }

        public static CinemachineBlendDefinition CreateBlendDefinition(string style, float time)
        {
            var blend = new CinemachineBlendDefinition();
#if CINEMACHINE_3
            if (System.Enum.TryParse<CinemachineBlendDefinition.Styles>(style, true, out var s))
                blend.Style = s;
            blend.Time = time;
#else
            if (System.Enum.TryParse<CinemachineBlendDefinition.Style>(style, true, out var s))
                blend.m_Style = s;
            blend.m_Time = time;
#endif
            return blend;
        }

        // ===================== StateDriven Instruction =====================

        public static void AddStateDrivenInstruction(
            CinemachineStateDrivenCamera stateCam,
            int stateHash,
            CinemachineVirtualCameraBase childVcam,
            float minDuration,
            float activateAfter)
        {
            var list = new List<CinemachineStateDrivenCamera.Instruction>();
#if CINEMACHINE_3
            if (stateCam.Instructions != null) list.AddRange(stateCam.Instructions);
            list.Add(new CinemachineStateDrivenCamera.Instruction
            {
                FullHash = stateHash,
                Camera = childVcam,
                MinDuration = minDuration,
                ActivateAfter = activateAfter
            });
            stateCam.Instructions = list.ToArray();
#else
            if (stateCam.m_Instructions != null) list.AddRange(stateCam.m_Instructions);
            list.Add(new CinemachineStateDrivenCamera.Instruction
            {
                m_FullHash = stateHash,
                m_VirtualCamera = childVcam,
                m_MinDuration = minDuration,
                m_ActivateAfter = activateAfter
            });
            stateCam.m_Instructions = list.ToArray();
#endif
        }

        // ===================== Sequencer =====================

#if CINEMACHINE_3
        public const string SequencerTypeName = "CinemachineSequencerCamera";
#else
        public const string SequencerTypeName = "CinemachineBlendListCamera";
#endif

        public static MonoBehaviour GetSequencer(GameObject go)
        {
#if CINEMACHINE_3
            return go.GetComponent<CinemachineSequencerCamera>();
#else
            return go.GetComponent<CinemachineBlendListCamera>();
#endif
        }

        public static void SetSequencerLoop(MonoBehaviour seq, bool loop)
        {
#if CINEMACHINE_3
            ((CinemachineSequencerCamera)seq).Loop = loop;
#else
            ((CinemachineBlendListCamera)seq).m_Loop = loop;
#endif
        }

        public static bool GetSequencerLoop(MonoBehaviour seq)
        {
#if CINEMACHINE_3
            return ((CinemachineSequencerCamera)seq).Loop;
#else
            return ((CinemachineBlendListCamera)seq).m_Loop;
#endif
        }

        public static void AddSequencerInstruction(
            MonoBehaviour seq,
            CinemachineVirtualCameraBase childVcam,
            float hold,
            CinemachineBlendDefinition blend)
        {
#if CINEMACHINE_3
            var seqCam = (CinemachineSequencerCamera)seq;
            if (seqCam.Instructions == null) seqCam.Instructions = new List<CinemachineSequencerCamera.Instruction>();
            seqCam.Instructions.Add(new CinemachineSequencerCamera.Instruction
            {
                Camera = childVcam,
                Hold = hold,
                Blend = blend
            });
#else
            var blendList = (CinemachineBlendListCamera)seq;
            var list = new List<CinemachineBlendListCamera.Instruction>();
            if (blendList.m_Instructions != null) list.AddRange(blendList.m_Instructions);
            list.Add(new CinemachineBlendListCamera.Instruction
            {
                m_VirtualCamera = childVcam,
                m_Hold = hold,
                m_Blend = blend
            });
            blendList.m_Instructions = list.ToArray();
#endif
        }

        public static int GetSequencerInstructionCount(MonoBehaviour seq)
        {
#if CINEMACHINE_3
            var s = ((CinemachineSequencerCamera)seq).Instructions;
            return s?.Count ?? 0;
#else
            var s = ((CinemachineBlendListCamera)seq).m_Instructions;
            return s?.Length ?? 0;
#endif
        }

        // ===================== FreeLook =====================

        public static GameObject CreateFreeLook(string name)
        {
            var go = new GameObject(name);
#if CINEMACHINE_3
            var cam = go.AddComponent<CinemachineCamera>();
            cam.Priority = new PrioritySettings { Enabled = true, Value = 10 };
            var orbital = go.AddComponent<CinemachineOrbitalFollow>();
            orbital.OrbitStyle = CinemachineOrbitalFollow.OrbitStyles.ThreeRing;
            go.AddComponent<CinemachineRotationComposer>();
#else
            go.AddComponent<CinemachineFreeLook>();
#endif
            return go;
        }

        // ===================== Pipeline Components =====================

        public static bool TryParsePipelineStage(string stageName, out CinemachineCore.Stage stage)
        {
            if (!string.IsNullOrWhiteSpace(stageName) &&
                !int.TryParse(stageName, out _) &&
                Enum.TryParse(stageName, true, out stage) &&
                stage >= CinemachineCore.Stage.Body && stage <= CinemachineCore.Stage.Noise)
            {
                return true;
            }

            stage = default;
            return false;
        }

        public static bool TryGetPipelineStage(Type componentType, out CinemachineCore.Stage stage)
        {
            stage = default;
            if (componentType == null || componentType.IsAbstract ||
                !typeof(CinemachineComponentBase).IsAssignableFrom(componentType))
            {
                return false;
            }

#if CINEMACHINE_3
            var attribute = componentType
                .GetCustomAttributes(typeof(CameraPipelineAttribute), true)
                .OfType<CameraPipelineAttribute>()
                .FirstOrDefault();
            if (attribute == null) return false;
            stage = attribute.Stage;
            return stage >= CinemachineCore.Stage.Body && stage <= CinemachineCore.Stage.Noise;
#else
            // CM2 has no pipeline-stage attribute. This is the same discovery mechanism used by
            // its editor package: instantiate the component briefly and read its Stage property.
            var probe = new GameObject("UnitySkills Cinemachine Stage Probe")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            try
            {
                var component = probe.AddComponent(componentType) as CinemachineComponentBase;
                if (component == null) return false;
                stage = component.Stage;
                return stage >= CinemachineCore.Stage.Body && stage <= CinemachineCore.Stage.Noise;
            }
            finally
            {
                Object.DestroyImmediate(probe);
            }
#endif
        }

        public static bool TryGetPipelineStage(MonoBehaviour component, out CinemachineCore.Stage stage)
        {
            if (component is CinemachineComponentBase pipelineComponent)
            {
                stage = pipelineComponent.Stage;
                return stage >= CinemachineCore.Stage.Body && stage <= CinemachineCore.Stage.Noise;
            }

            stage = default;
            return false;
        }

        public static MonoBehaviour[] GetPipelineComponents(GameObject go)
        {
            var vcam = GetVCam(go);
            if (vcam == null) return Array.Empty<MonoBehaviour>();

#if CINEMACHINE_3
            return go.GetComponents<CinemachineComponentBase>().Cast<MonoBehaviour>().ToArray();
#else
            var owner = FindExistingPipelineOwner((CinemachineVirtualCamera)vcam);
            return owner == null
                ? Array.Empty<MonoBehaviour>()
                : owner.GetComponents<CinemachineComponentBase>().Cast<MonoBehaviour>().ToArray();
#endif
        }

        public static MonoBehaviour GetPipelineComponent(GameObject go, string stage)
        {
            if (!TryParsePipelineStage(stage, out var stageValue)) return null;

            var vcam = GetVCam(go);
            if (vcam == null) return null;

#if CINEMACHINE_3
            return ((CinemachineCamera)vcam).GetCinemachineComponent(stageValue);
#else
            var owner = FindExistingPipelineOwner((CinemachineVirtualCamera)vcam);
            if (owner == null) return null;
            return owner.GetComponents<CinemachineComponentBase>()
                .FirstOrDefault(component => component != null && component.Stage == stageValue);
#endif
        }

        public static MonoBehaviour AddPipelineComponent(GameObject go, Type componentType, out string error)
        {
            error = null;
            var vcam = GetVCam(go);
            if (vcam == null)
            {
                error = $"Not a {VCamTypeName}";
                return null;
            }

            if (!TryGetPipelineStage(componentType, out _))
            {
                error = componentType == null
                    ? "Pipeline component type is required."
                    : componentType.Name + " is not a valid Cinemachine pipeline component.";
                return null;
            }

#if CINEMACHINE_3
            var owner = go;
#else
            var ownerTransform = ((CinemachineVirtualCamera)vcam).GetComponentOwner();
            if (ownerTransform == null)
            {
                error = "Cinemachine pipeline owner is unavailable. Prefab instances must be opened in Prefab Mode or unpacked.";
                return null;
            }
            var owner = ownerTransform.gameObject;
#endif

            var component = UnityEditor.Undo.AddComponent(owner, componentType) as MonoBehaviour;
            if (component == null)
            {
                error = "Failed to add pipeline component " + componentType.Name + ".";
                return null;
            }

            InvalidatePipeline(go);
            return component;
        }

        public static void InvalidatePipeline(GameObject go)
        {
#if CINEMACHINE_2
            var vcam = GetVCam(go) as CinemachineVirtualCamera;
            if (vcam != null) vcam.InvalidateComponentPipeline();
#endif
        }

#if CINEMACHINE_2
        [UnityEditor.InitializeOnLoad]
        private static class PipelineCacheInvalidator
        {
            private static bool _scheduled;

            static PipelineCacheInvalidator()
            {
                WorkflowManager.ComponentTopologyChanged += OnComponentTopologyChanged;
                UnityEditor.Undo.undoRedoPerformed += ScheduleInvalidation;
                UnityEditor.ObjectChangeEvents.changesPublished += OnChangesPublished;
            }

            private static void OnComponentTopologyChanged(GameObject owner, Type componentType)
            {
                if (owner == null || componentType == null ||
                    !typeof(CinemachineComponentBase).IsAssignableFrom(componentType))
                {
                    return;
                }

                var vcam = owner.GetComponent<CinemachineVirtualCamera>() ??
                           owner.GetComponentInParent<CinemachineVirtualCamera>();
                if (vcam != null) vcam.InvalidateComponentPipeline();
            }

            private static void OnChangesPublished(ref UnityEditor.ObjectChangeEventStream stream)
            {
                for (int i = 0; i < stream.length; i++)
                {
                    var kind = stream.GetEventType(i);
                    if (kind == UnityEditor.ObjectChangeKind.ChangeGameObjectStructure ||
                        kind == UnityEditor.ObjectChangeKind.ChangeGameObjectStructureHierarchy)
                    {
                        ScheduleInvalidation();
                        return;
                    }
                }
            }

            private static void ScheduleInvalidation()
            {
                if (_scheduled) return;
                _scheduled = true;
                UnityEditor.EditorApplication.delayCall += InvalidateAll;
            }

            private static void InvalidateAll()
            {
                _scheduled = false;
                foreach (var vcam in FindHelper.FindAll<CinemachineVirtualCamera>())
                {
                    if (vcam != null) vcam.InvalidateComponentPipeline();
                }
            }
        }

        private static GameObject FindExistingPipelineOwner(CinemachineVirtualCamera vcam)
        {
            if (vcam == null) return null;
            foreach (Transform child in vcam.transform)
            {
                if (child.GetComponent<CinemachinePipeline>() != null)
                    return child.gameObject;
            }
            return null;
        }
#endif
    }
#endif
}

// Producer:Betsy
