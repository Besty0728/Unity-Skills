using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;

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
            { "Transposer", "CinemachineTransposer" },
            { "Composer", "CinemachineComposer" },
            { "PanTilt", "CinemachinePanTilt" },
            { "SameAsFollow", "CinemachineSameAsFollowTarget" },
            { "HardLockToTarget", "CinemachineHardLockToTarget" },
            { "Perlin", "CinemachineBasicMultiChannelPerlin" },
            { "Impulse", "CinemachineImpulseSource" }
#else
            { "Transposer", "CinemachineTransposer" },
            { "Composer", "CinemachineComposer" },
            { "FramingTransposer", "CinemachineFramingTransposer" },
            { "HardLockToTarget", "CinemachineHardLockToTarget" },
            { "Perlin", "CinemachineBasicMultiChannelPerlin" },
            { "Impulse", "CinemachineImpulseSource" },
            { "POV", "CinemachinePOV" },
            { "OrbitalTransposer", "CinemachineOrbitalTransposer" }
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
    }
#endif
}
