using System;
using UnityEditor;
using UnityEngine;
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
    ///
    /// delayCall is NOT unconditionally safe, though: in Play mode the editor can pump
    /// Internal_CallDelayFunctions inside a panel's render pass, and a panel whose renderer
    /// died mid-pass (e.g. the TextCore atlas-Material engine bug) stays locked in "rendering"
    /// so EVERY later mutation throws. Both surface as InvalidOperationException from
    /// IncrementVersion. Since every tick here is an idempotent status refresh, the deferred
    /// body swallows that exception (plus MissingReferenceException from dead text Materials)
    /// and simply skips the beat — the next tick repaints the same state, and one skipped
    /// refresh is invisible while an uncaught throw turns into a Console spam loop.
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
                    try
                    {
                        body();
                    }
                    catch (InvalidOperationException)
                    {
                        // Visual tree is mid-render (or its renderer is wedged) — skip this
                        // beat; the next interval repaints the same idempotent state.
                    }
                    catch (MissingReferenceException)
                    {
                        // A text Material died under us (TextCore atlas engine bug) — same
                        // deal: skip, let the font self-heal path rebuild before the next beat.
                    }
                };
            }

            return element.schedule.Execute(OnTick).Every(intervalMs);
        }
    }
}
