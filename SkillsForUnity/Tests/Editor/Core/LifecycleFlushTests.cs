using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Telemetry and audit records sit in a 200 ms delayed queue; a domain reload or editor quit inside that window used to
    /// lose them. FlushSync is what the lifecycle hooks call, so it must write the queued record on the spot, and the hooks
    /// themselves (including the journal's debounced property flush) must be subscribed.
    /// </summary>
    [TestFixture]
    public class LifecycleFlushTests
    {
        [Test]
        public void Telemetry_FlushSync_WritesTheQueuedRecordWithoutWaitingForTheDelayedFlush()
        {
            bool previousEnabled = SkillTelemetryService.Enabled;
            SkillTelemetryService.Enabled = true;
            SkillTelemetryService.ResetForTests();
            try
            {
                string skill = "lifecycle_flush_probe_" + Guid.NewGuid().ToString("N");
                SkillTelemetryService.Record(skill, "tests", "execute", true, null, 1);

                SkillTelemetryService.FlushSync();

                StringAssert.Contains(skill, File.ReadAllText(SkillTelemetryService.GetLogPath()));
            }
            finally
            {
                SkillTelemetryService.ResetForTests();
                SkillTelemetryService.Enabled = previousEnabled;
            }
        }

        [Test]
        public void Audit_FlushSync_WritesTheQueuedEventWithoutWaitingForTheDelayedFlush()
        {
            SkillsAuditLog.ResetForTests();
            try
            {
                string marker = "lifecycle_flush_probe_" + Guid.NewGuid().ToString("N");
                SkillsAuditLog.Append(marker, new { note = "queued" });

                SkillsAuditLog.FlushSync();

                StringAssert.Contains(marker, File.ReadAllText(SkillsAuditLog.GetLogPath()));
            }
            finally
            {
                SkillsAuditLog.ResetForTests();
            }
        }

        [Test]
        public void FlushHandlers_AreRegisteredForAssemblyReloadAndEditorQuit()
        {
            var beforeReload = Subscribers(typeof(AssemblyReloadEvents), "beforeAssemblyReload");
            var quitting = Subscribers(typeof(EditorApplication), "quitting");

            // Subscribers that have always been there prove the reflection can see handlers at all; without them a Unity
            // version that hides the delegates would turn this into a test that passes or fails for the wrong reason.
            if (!IsMethod(beforeReload, typeof(EventChannelService), "OnBeforeAssemblyReload") ||
                !IsMethod(quitting, typeof(RegistryService), "Unregister"))
                Assert.Ignore("This Unity version does not expose its reload/quit event delegates to reflection.");

            Assert.IsTrue(IsMethod(beforeReload, typeof(SkillTelemetryService), "FlushSync"), "telemetry flush before domain reload");
            Assert.IsTrue(IsMethod(quitting, typeof(SkillTelemetryService), "FlushSync"), "telemetry flush on quit");
            Assert.IsTrue(IsMethod(beforeReload, typeof(SkillsAuditLog), "FlushSync"), "audit flush before domain reload");
            Assert.IsTrue(IsMethod(quitting, typeof(SkillsAuditLog), "FlushSync"), "audit flush on quit");
            Assert.IsTrue(IsMethod(beforeReload, typeof(EditorChangeTrackerService), "FlushPendingProperties"), "journal property flush before domain reload");
            Assert.IsTrue(IsMethod(quitting, typeof(EditorChangeTrackerService), "FlushPendingProperties"), "journal property flush on quit");
            Assert.IsTrue(IsMethod(beforeReload, typeof(ConsoleLogHistoryService), "FinalFlush"), "console history final flush before domain reload");
            Assert.IsTrue(IsMethod(quitting, typeof(ConsoleLogHistoryService), "FinalFlush"), "console history final flush on quit");
        }

        /// <summary>
        /// Every handler reachable from the owner's static field(s) named after the event. A plain Unity event keeps them in one
        /// delegate; newer editors wrap it (performance-tracker entries), so the walk follows fields and collections.
        /// </summary>
        private static Delegate[] Subscribers(Type owner, string eventName)
        {
            var found = new List<Delegate>();
            foreach (var field in owner.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.Name.IndexOf(eventName, StringComparison.OrdinalIgnoreCase) >= 0)
                    Collect(field.GetValue(null), found, 0);
            }
            return found.ToArray();
        }

        private static void Collect(object node, List<Delegate> found, int depth)
        {
            if (node == null || depth > 6)
                return;
            if (node is Delegate handlers)
            {
                found.AddRange(handlers.GetInvocationList());
                return;
            }
            var type = node.GetType();
            if (node is string || type.IsPrimitive || type.IsEnum)
                return;
            if (node is IEnumerable items)
            {
                foreach (var item in items)
                    Collect(item, found, depth + 1);
                return;
            }
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                Collect(field.GetValue(node), found, depth + 1);
        }

        private static bool IsMethod(IEnumerable<Delegate> handlers, Type declaringType, string methodName) =>
            handlers.Any(h => h.Method.DeclaringType == declaringType && h.Method.Name == methodName);
    }
}

// Producer:Betsy
