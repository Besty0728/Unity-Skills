using System;
using System.Collections.Generic;
using System.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Decides when an agent's idle gap ends, which is when its router run starts over and an automatic checkpoint is due.
    /// Busy time (compiles and domain reloads, see <see cref="WorkflowLifecycle"/>) inside the gap is not idle time.
    /// </summary>
    internal static class WorkflowCheckpointPolicy
    {
        /// <summary>
        /// Whether the agent was idle for at least <paramref name="idleGapSeconds"/> between its last call and the step's start,
        /// leaving out the busy spans that overlap that window.
        /// </summary>
        internal static bool IsIdleGap(long lastCallMs, long stepStartMs, IReadOnlyList<(long start, long end)> busySpans,
            int idleGapSeconds)
        {
            long idleMs = stepStartMs - lastCallMs - BusyWithin(lastCallMs, stepStartMs, busySpans);
            return idleMs >= idleGapSeconds * 1000L;
        }

        /// <summary>Busy milliseconds inside [from, to]; overlapping spans count once.</summary>
        private static long BusyWithin(long from, long to, IReadOnlyList<(long start, long end)> busySpans)
        {
            if (busySpans == null || to <= from)
                return 0;

            long busy = 0;
            long countedUntil = long.MinValue;
            foreach (var span in busySpans.OrderBy(s => s.start))
            {
                long start = Math.Max(Math.Max(span.start, from), countedUntil);
                long end = Math.Min(span.end, to);
                if (end > start)
                {
                    busy += end - start;
                    countedUntil = end;
                }
            }
            return busy;
        }
    }
}

// Producer:Betsy
