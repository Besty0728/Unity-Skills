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

        // ===================== Body / Aim Component Detection =====================

        private static readonly HashSet<string> BodyTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CinemachineFollow", "CinemachineOrbitalFollow", "CinemachineThirdPersonFollow",
            "CinemachinePositionComposer", "CinemachineSplineDolly", "CinemachineHardLockToTarget",
            // CM2
            "CinemachineTransposer", "CinemachineFramingTransposer", "CinemachineOrbitalTransposer",
            "Cinemachine3rdPersonFollow", "CinemachineTrackedDolly"
        };

        private static readonly HashSet<string> AimTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CinemachineRotationComposer", "CinemachinePanTilt", "CinemachineHardLookAt",
            "CinemachineRotateWithFollowTarget", "CinemachineSplineDollyLookAtTargets",
            // CM2
            "CinemachineComposer", "CinemachineGroupComposer", "CinemachinePOV",
            "CinemachineSameAsFollowTarget"
        };

        public static MonoBehaviour GetPipelineComponent(GameObject go, string stage)
        {
            var comps = go.GetComponents<MonoBehaviour>();
            var set = stage == "Body" ? BodyTypes : AimTypes;
            return comps.FirstOrDefault(c => c != null && set.Contains(c.GetType().Name));
        }
    }
#endif
}
