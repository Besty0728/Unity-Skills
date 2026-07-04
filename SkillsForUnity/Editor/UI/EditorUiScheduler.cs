using System;
using UnityEditor;
using UnityEngine.UIElements;

namespace UnitySkills
{
    /// <summary>
    /// Safe wrapper around <c>VisualElement.schedule.Execute(...).Every(...)</c> for periodic UI ticks.
    ///
    /// UI Toolkit runs scheduled callbacks during the panel's update pass, which in an
    /// EditorWindow can overlap layout/repaint (<c>generateVisualContent</c>). Mutating the
    /// visual tree directly from such a callback (label.text=, AddToClassList/RemoveFromClassList,
    /// etc.) throws <c>InvalidOperationException</c> — and because the tick repeats, one hit turns
    /// into a Console spam loop (issue #44).
    ///
    /// <see cref="RepeatSafe"/> keeps the same <c>element.schedule</c> lifecycle binding (auto-pause
    /// on detach, auto-resume on attach) but defers the actual mutation to
    /// <see cref="EditorApplication.delayCall"/>, which runs outside repaint. Multiple ticks that
    /// land before the deferred call drains are coalesced into a single invocation.
    /// </summary>
    internal static class EditorUiScheduler
    {
        public static IVisualElementScheduledItem RepeatSafe(VisualElement element, long intervalMs, Action body)
        {
            bool queued = false;

            void OnTick()
            {
                if (element?.panel == null || queued) return;
                queued = true;
                EditorApplication.delayCall += () =>
                {
                    queued = false;
                    if (element?.panel == null) return;
                    body();
                };
            }

            return element.schedule.Execute(OnTick).Every(intervalMs);
        }
    }
}
