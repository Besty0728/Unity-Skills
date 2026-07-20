using NUnit.Framework;
using System.IO;
using System.Linq;
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
#if CINEMACHINE_2
        private string _workflowTempRoot;
#endif

        [SetUp]
        public void SetUp()
        {
            _vcamGo = new GameObject("GameplayCam");
#if CINEMACHINE_3
            _vcamGo.AddComponent<CinemachineCamera>();
#elif CINEMACHINE_2
            _vcamGo.AddComponent<CinemachineVirtualCamera>();
            _workflowTempRoot = Path.Combine(
                Path.GetTempPath(), "UnitySkillsCinemachineWorkflowTests_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workflowTempRoot);
            WorkflowManager.OverrideHistoryFilePathForTests = Path.Combine(_workflowTempRoot, "workflow_history.json");
            WorkflowFileStore.OverrideStoreRootForTests = Path.Combine(_workflowTempRoot, "workflow_files");
            WorkflowManager.ResetStateForTests();
#endif
        }

        [TearDown]
        public void TearDown()
        {
#if CINEMACHINE_2
            WorkflowManager.AbortTask();
            WorkflowManager.ResetStateForTests();
            WorkflowManager.OverrideHistoryFilePathForTests = null;
            WorkflowFileStore.OverrideStoreRootForTests = null;
#endif
            if (_vcamGo != null)
            {
                Object.DestroyImmediate(_vcamGo);
            }
#if CINEMACHINE_2
            try
            {
                if (Directory.Exists(_workflowTempRoot)) Directory.Delete(_workflowTempRoot, true);
            }
            catch
            {
                // Best-effort cleanup for test-only files.
            }
#endif
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

        [Test]
        public void MixingWeight_RejectsCameraOutsideMixerHierarchy()
        {
            var mixerGo = new GameObject("GameplayMixer");
            mixerGo.AddComponent<CinemachineMixingCamera>();

            var result = CinemachineSkills.CinemachineMixingCameraSetWeight(
                mixerName: mixerGo.name, childName: _vcamGo.name, weight: 0.75f);

            StringAssert.Contains("must be an immediate child", GetError(result));
            Object.DestroyImmediate(mixerGo);
        }

        [Test]
        public void MixingWeight_UpdatesImmediateChild()
        {
            var mixerGo = new GameObject("GameplayMixer");
            var mixer = mixerGo.AddComponent<CinemachineMixingCamera>();
            _vcamGo.transform.SetParent(mixerGo.transform);

            var result = CinemachineSkills.CinemachineMixingCameraSetWeight(
                mixerName: mixerGo.name, childName: _vcamGo.name, weight: 0.75f);

            Assert.That(GetError(result), Is.Null);
            Assert.That(mixer.GetWeight(_vcamGo.GetComponent<CinemachineVirtualCameraBase>()),
                Is.EqualTo(0.75f).Within(0.001f));
            _vcamGo.transform.SetParent(null);
            Object.DestroyImmediate(mixerGo);
        }

        [Test]
        public void GameObjectFinder_DestroyedCachedObject_RebuildsForReplacement()
        {
            Assert.That(GameObjectFinder.FindByNameCaseInsensitive(_vcamGo.name), Is.SameAs(_vcamGo));
            var original = _vcamGo;
            Object.DestroyImmediate(original);

            _vcamGo = new GameObject("GameplayCam");
#if CINEMACHINE_3
            _vcamGo.AddComponent<CinemachineCamera>();
#elif CINEMACHINE_2
            _vcamGo.AddComponent<CinemachineVirtualCamera>();
#endif

            Assert.That(GameObjectFinder.FindByNameCaseInsensitive(_vcamGo.name), Is.SameAs(_vcamGo));
        }

#if CINEMACHINE_2
        [Test]
        public void AddComponent_Transposer_AddsBodyToPipelineOwner()
        {
            var result = CinemachineSkills.CinemachineAddComponent(
                vcamName: _vcamGo.name, componentType: "Transposer");

            Assert.That(GetError(result), Is.Null);
            var vcam = GetVirtualCamera();
            Assert.That(vcam.GetCinemachineComponent(CinemachineCore.Stage.Body),
                Is.TypeOf<CinemachineTransposer>());
            Assert.That(_vcamGo.GetComponent<CinemachineTransposer>(), Is.Null,
                "CM2 pipeline components must not be attached to the VCam root.");
        }

        [Test]
        public void AddComponent_Composer_AddsAimToPipelineOwner()
        {
            var result = CinemachineSkills.CinemachineAddComponent(
                vcamName: _vcamGo.name, componentType: "Composer");

            Assert.That(GetError(result), Is.Null);
            var vcam = GetVirtualCamera();
            Assert.That(vcam.GetCinemachineComponent(CinemachineCore.Stage.Aim),
                Is.TypeOf<CinemachineComposer>());
            Assert.That(_vcamGo.GetComponent<CinemachineComposer>(), Is.Null,
                "CM2 pipeline components must not be attached to the VCam root.");
        }

        [Test]
        public void SetNoise_AddsAndConfiguresPerlinInNoisePipeline()
        {
            var result = CinemachineSkills.CinemachineSetNoise(
                vcamName: _vcamGo.name, amplitudeGain: 1.75f, frequencyGain: 2.25f);

            Assert.That(GetError(result), Is.Null);
            var vcam = GetVirtualCamera();
            var perlin = vcam.GetCinemachineComponent(CinemachineCore.Stage.Noise)
                as CinemachineBasicMultiChannelPerlin;
            Assert.That(perlin, Is.Not.Null);
            Assert.That(_vcamGo.GetComponent<CinemachineBasicMultiChannelPerlin>(), Is.Null,
                "CM2 noise belongs to the hidden pipeline owner, not the VCam root.");
            Assert.That(perlin.m_AmplitudeGain, Is.EqualTo(1.75f).Within(0.001f));
            Assert.That(perlin.m_FrequencyGain, Is.EqualTo(2.25f).Within(0.001f));
        }

        [Test]
        public void ConfigureBody_UpdatesTransposerInPipeline()
        {
            var transposer = GetVirtualCamera().AddCinemachineComponent<CinemachineTransposer>();

            var result = CinemachineSkills.CinemachineConfigureBody(
                vcamName: _vcamGo.name,
                offsetX: 1.25f, offsetY: 2.5f, offsetZ: -3.75f,
                bindingMode: "WorldSpace",
                dampingX: 0.2f, dampingY: 0.4f, dampingZ: 0.6f);

            Assert.That(GetError(result), Is.Null);
            Assert.That(transposer.m_FollowOffset, Is.EqualTo(new Vector3(1.25f, 2.5f, -3.75f)));
            Assert.That(transposer.m_BindingMode, Is.EqualTo(CinemachineTransposer.BindingMode.WorldSpace));
            Assert.That(transposer.m_XDamping, Is.EqualTo(0.2f).Within(0.001f));
            Assert.That(transposer.m_YDamping, Is.EqualTo(0.4f).Within(0.001f));
            Assert.That(transposer.m_ZDamping, Is.EqualTo(0.6f).Within(0.001f));
        }

        [Test]
        public void ConfigureAim_UpdatesComposerInPipeline()
        {
            var composer = GetVirtualCamera().AddCinemachineComponent<CinemachineComposer>();

            var result = CinemachineSkills.CinemachineConfigureAim(
                vcamName: _vcamGo.name,
                screenX: 0.35f, screenY: 0.65f,
                deadZoneWidth: 0.15f, deadZoneHeight: 0.25f,
                softZoneWidth: 0.7f, softZoneHeight: 0.75f,
                horizontalDamping: 0.3f, verticalDamping: 0.45f,
                lookaheadTime: 0.5f, lookaheadSmoothing: 0.8f,
                centerOnActivate: false);

            Assert.That(GetError(result), Is.Null);
            Assert.That(composer.m_ScreenX, Is.EqualTo(0.35f).Within(0.001f));
            Assert.That(composer.m_ScreenY, Is.EqualTo(0.65f).Within(0.001f));
            Assert.That(composer.m_DeadZoneWidth, Is.EqualTo(0.15f).Within(0.001f));
            Assert.That(composer.m_DeadZoneHeight, Is.EqualTo(0.25f).Within(0.001f));
            Assert.That(composer.m_SoftZoneWidth, Is.EqualTo(0.7f).Within(0.001f));
            Assert.That(composer.m_SoftZoneHeight, Is.EqualTo(0.75f).Within(0.001f));
            Assert.That(composer.m_HorizontalDamping, Is.EqualTo(0.3f).Within(0.001f));
            Assert.That(composer.m_VerticalDamping, Is.EqualTo(0.45f).Within(0.001f));
            Assert.That(composer.m_LookaheadTime, Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(composer.m_LookaheadSmoothing, Is.EqualTo(0.8f).Within(0.001f));
            Assert.That(composer.m_CenterOnActivate, Is.False);
        }

        [Test]
        public void SetComponent_ReplacesExistingBodyWithoutDuplicates()
        {
            var vcam = GetVirtualCamera();
            vcam.AddCinemachineComponent<CinemachineTransposer>();

            var result = CinemachineSkills.CinemachineSetComponent(
                vcamName: _vcamGo.name, stage: "Body", componentType: "FramingTransposer");

            Assert.That(GetError(result), Is.Null);
            Assert.That(vcam.GetCinemachineComponent(CinemachineCore.Stage.Body),
                Is.TypeOf<CinemachineFramingTransposer>());
            Assert.That(CountPipelineComponents(vcam, CinemachineCore.Stage.Body), Is.EqualTo(1));
            Assert.That(_vcamGo.GetComponent<CinemachineFramingTransposer>(), Is.Null,
                "The replacement must remain on the CM2 pipeline owner.");
        }

        [Test]
        public void SetComponent_WorkflowUndoRedo_RefreshesPipelineCache()
        {
            var vcam = GetVirtualCamera();
            vcam.AddCinemachineComponent<CinemachineTransposer>();

            WorkflowManager.BeginTask("cm2-pipeline-replace", "test");
            var result = CinemachineSkills.CinemachineSetComponent(
                vcamName: _vcamGo.name, stage: "Body", componentType: "FramingTransposer");
            WorkflowManager.EndTask();
            var taskId = WorkflowManager.History.tasks.Last().id;

            Assert.That(GetError(result), Is.Null);
            Assert.That(vcam.GetCinemachineComponent(CinemachineCore.Stage.Body),
                Is.TypeOf<CinemachineFramingTransposer>());

            Assert.That(WorkflowManager.UndoTask(taskId).success, Is.True);
            Assert.That(vcam.GetCinemachineComponent(CinemachineCore.Stage.Body),
                Is.TypeOf<CinemachineTransposer>());

            Assert.That(WorkflowManager.RedoTask(taskId).success, Is.True);
            Assert.That(vcam.GetCinemachineComponent(CinemachineCore.Stage.Body),
                Is.TypeOf<CinemachineFramingTransposer>());
        }

        private CinemachineVirtualCamera GetVirtualCamera()
        {
            return _vcamGo.GetComponent<CinemachineVirtualCamera>();
        }

        private static int CountPipelineComponents(
            CinemachineVirtualCamera vcam, CinemachineCore.Stage stage)
        {
            var count = 0;
            foreach (var component in vcam.GetComponentsInChildren<CinemachineComponentBase>(true))
            {
                if (component.Stage == stage)
                {
                    count++;
                }
            }
            return count;
        }
#endif

#if CINEMACHINE_3
        [Test]
        public void ConfigureImpulseSource_SetsModernAndLegacyDurationFields()
        {
            var source = _vcamGo.AddComponent<CinemachineImpulseSource>();

            var result = CinemachineSkills.CinemachineConfigureImpulseSource(
                sourceName: _vcamGo.name, duration: 0.35f);

            Assert.That(GetError(result), Is.Null);
            Assert.That(source.ImpulseDefinition.ImpulseDuration, Is.EqualTo(0.35f).Within(0.001f));
            Assert.That(source.ImpulseDefinition.TimeEnvelope.SustainTime, Is.EqualTo(0.35f).Within(0.001f));
        }

        [Test]
        public void ConfigureExtension_WithUnsupportedParameters_ReturnsError()
        {
            _vcamGo.AddComponent<CinemachineCameraOffset>();

            var result = CinemachineSkills.CinemachineConfigureExtension(
                vcamName: _vcamGo.name,
                extensionName: nameof(CinemachineCameraOffset),
                damping: 0.25f);

            StringAssert.Contains("No compatible properties", GetError(result));
        }

        [Test]
        public void SetBrain_WithoutSettings_ReturnsValidationError()
        {
            var result = CinemachineSkills.CinemachineSetBrain();

            Assert.That(GetError(result), Is.EqualTo("No Brain settings were provided to update."));
        }
#endif

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

// Producer:Betsy
