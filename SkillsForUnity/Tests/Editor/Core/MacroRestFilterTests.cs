using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// REST 来源过滤（UnitySkills_MacroIgnoreRestChanges）单测 —— property 事件源部分。
    ///
    /// 探针对一个**录制开始前就存在**的对象做 REST 属性修改（gameobject_set_active →
    /// m_IsActive）：Undo.postprocessModifications 同步派发，且对象已在初始 catalog 里，
    /// 导出不依赖帧末结构事件的顺序 —— 因此可在 EditMode 同步稳定验证过滤开关的 on/off
    /// 决策。结构事件源（帧末批量派发）与"标记延迟复位后手工操作不被误伤"由真实 REST
    /// 环境实测 + 人工验收覆盖（EditMode 的 yield return null 不推进真实 editor loop，
    /// 无法在测试内驱动帧末派发时序）。
    ///
    /// 判定完全依赖显式 Assert（导出步骤内容），因此 SetUp 里关闭"意外日志判失败"：
    /// 编辑器偶发的引擎内部错误（场景切换后的 Material MissingReferenceException、
    /// domain reload 时 UIElements 的 InvalidOperationException 等，堆栈无本项目帧）
    /// 与被测单元无关，不应把本 fixture 打成假阴性。
    /// </summary>
    [TestFixture]
    public class MacroRestFilterTests
    {
        private bool _savedToggle;
        private bool _savedIgnoreFailing;

        [SetUp]
        public void SetUp()
        {
            _savedIgnoreFailing = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
            _savedToggle = MacroRecorderService.IgnoreRestChanges;
            if (MacroRecorderService.IsRecording)
                MacroRecorderService.Stop();
        }

        [TearDown]
        public void TearDown()
        {
            if (MacroRecorderService.IsRecording)
                MacroRecorderService.Stop();
            MacroRecorderService.IgnoreRestChanges = _savedToggle;
            LogAssert.ignoreFailingMessages = _savedIgnoreFailing;
        }

        private static string[] ExportSkillNames()
        {
            var export = JObject.FromObject(MacroRecorderService.Export("batch"));
            Assert.IsNull(export["error"], "export failed: " + export["error"]);
            return ((JArray)export["steps"]).Select(s => s["skill"].ToString()).ToArray();
        }

        /// <summary>录制前建好探针对象 → 初始 catalog 收录（CreatedDuringRecording=false）。</summary>
        private static void SeedProbeAndStart()
        {
            new GameObject("RestFilterProbe");
            GameObjectFinder.InvalidateCache();
            MacroRecorderService.Start(null);
        }

        /// <summary>REST property 源探针：改预存在对象的 m_IsActive（postprocessModifications，同步）。</summary>
        private static void RunRestPropertyOperation()
        {
            var setActive = JObject.Parse(SkillRouter.Execute("gameobject_set_active", @"{""name"":""RestFilterProbe"",""active"":false}"));
            Assert.AreEqual("success", setActive["status"]?.ToString(), setActive.ToString());
        }

        [Test]
        public void ToggleOn_RestPropertyChange_IsFiltered()
        {
            MacroRecorderService.IgnoreRestChanges = true;
            SeedProbeAndStart();
            RunRestPropertyOperation();
            MacroRecorderService.Stop();

            var skills = ExportSkillNames();
            Assert.IsEmpty(skills,
                "REST-driven steps must be filtered when the toggle is on, got: [" + string.Join(",", skills) + "]");
        }

        [Test]
        public void ToggleOff_RestPropertyChange_IsRecorded()
        {
            MacroRecorderService.IgnoreRestChanges = false;
            SeedProbeAndStart();
            RunRestPropertyOperation();
            MacroRecorderService.Stop();

            var skills = ExportSkillNames();
            CollectionAssert.Contains(skills, "gameobject_set_active",
                "property source (m_IsActive) missing, got: [" + string.Join(",", skills) + "]");
        }
    }
}
