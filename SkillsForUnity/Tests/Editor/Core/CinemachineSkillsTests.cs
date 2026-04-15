using NUnit.Framework;
using UnityEngine;

#if CINEMACHINE_3
using Unity.Cinemachine;
#elif CINEMACHINE_2
using Cinemachine;
#endif

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class CinemachineSkillsTests
    {
#if CINEMACHINE_2 || CINEMACHINE_3
        private GameObject _vcamGo;

        [SetUp]
        public void SetUp()
        {
            _vcamGo = new GameObject("GameplayCam");
#if CINEMACHINE_3
            _vcamGo.AddComponent<CinemachineCamera>();
#elif CINEMACHINE_2
            _vcamGo.AddComponent<CinemachineVirtualCamera>();
#endif
        }

        [TearDown]
        public void TearDown()
        {
            if (_vcamGo != null)
            {
                Object.DestroyImmediate(_vcamGo);
            }
        }

        [Test]
        public void SetVCamProperty_WithLensShortcut_UpdatesFov()
        {
            var result = CinemachineSkills.CinemachineSetVCamProperty(vcamName: _vcamGo.name, fov: 50f);

            Assert.That(GetError(result), Is.Null);

#if CINEMACHINE_3
            var vcam = _vcamGo.GetComponent<CinemachineCamera>();
            Assert.That(vcam.Lens.FieldOfView, Is.EqualTo(50f).Within(0.001f));
#elif CINEMACHINE_2
            var vcam = _vcamGo.GetComponent<CinemachineVirtualCamera>();
            Assert.That(vcam.m_Lens.FieldOfView, Is.EqualTo(50f).Within(0.001f));
#endif
        }

        [Test]
        public void SetVCamProperty_WithoutPropertyName_ReturnsValidationErrorInsteadOfThrowing()
        {
            var result = CinemachineSkills.CinemachineSetVCamProperty(vcamName: _vcamGo.name);

            Assert.That(GetError(result), Is.EqualTo("propertyName is required unless using shorthand lens parameters (fov/nearClip/farClip/orthoSize)."));
        }

        private static string GetError(object result)
        {
            if (result == null) return null;
            var prop = result.GetType().GetProperty("error");
            return prop?.GetValue(result)?.ToString();
        }
#else
        [Test]
        public void CinemachineNotInstalled_Skip()
        {
            Assert.Pass("Cinemachine 未安装，跳过相关测试。");
        }
#endif
    }
}
