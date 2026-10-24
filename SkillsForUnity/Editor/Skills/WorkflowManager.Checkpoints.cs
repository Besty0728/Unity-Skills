using System;
using System.Collections.Generic;
using System.Globalization;
using UnitySkills.Internal;

namespace UnitySkills
{
    public static partial class WorkflowManager
    {
        /// <summary>
        /// Records a checkpoint at the position the next step gets, so the checkpoint takes no seq. A position holds one
        /// checkpoint: the record merges into an existing one through <see cref="WorkflowStore.MergeCheckpoint"/>, so an auto
        /// checkpoint never replaces one and an explicit call upgrades it. Returns the checkpoint's id.
        /// </summary>
        internal static string AddCheckpoint(string agent, string kind, string label)
        {
            long position = CheckpointPosition();
            var checkpoint = new WorkflowCheckpoint
            {
                id = CheckpointIdFor(position),
                seq = position,
                timestampMs = NowUnixMs(),
                agent = string.IsNullOrEmpty(agent) ? ResolveCallerAgent() : agent,
                kind = kind,
                label = string.IsNullOrEmpty(label) ? null : label,
                cursor = EditorChangeTrackerService.CurrentCursor
            };

            var batch = new LogBatch();
            AddCheckpointRecord(batch, checkpoint);
            Commit(batch);
            return checkpoint.id;
        }

        /// <summary>Gives an existing checkpoint a new label (an empty one clears it). False when no checkpoint has the id.</summary>
        internal static bool RenameCheckpoint(string id, string label)
        {
            var checkpoint = History.checkpoints.Find(existing => existing.id == id);
            if (checkpoint == null)
                return false;

            checkpoint.label = string.IsNullOrEmpty(label) ? null : label;
            var batch = new LogBatch();
            batch.Lines.Add(WorkflowStore.BuildLabelLine(checkpoint.id, checkpoint.label));
            Commit(batch);
            return true;
        }

        public static IReadOnlyList<WorkflowCheckpoint> GetCheckpoints() => History.checkpoints;

        /// <summary>
        /// The checkpoint with this id, else the newest one whose label equals the text (ignoring case); null when neither exists.
        /// </summary>
        internal static WorkflowCheckpoint FindCheckpoint(string idOrLabel)
        {
            if (string.IsNullOrEmpty(idOrLabel))
                return null;

            var checkpoints = History.checkpoints;
            var byId = checkpoints.Find(checkpoint => checkpoint.id == idOrLabel);
            if (byId != null)
                return byId;

            WorkflowCheckpoint newest = null;
            foreach (var checkpoint in checkpoints)
            {
                if (string.Equals(checkpoint.label, idOrLabel, StringComparison.OrdinalIgnoreCase) &&
                    (newest == null || checkpoint.seq > newest.seq))
                    newest = checkpoint;
            }
            return newest;
        }

        internal static string CheckpointIdFor(long position) => "c" + position.ToString(CultureInfo.InvariantCulture);

        /// <summary>The position a checkpoint added now takes: the seq the next step gets. Peeks, so no seq is spent.</summary>
        private static long CheckpointPosition()
        {
            if (_history != null)
                return _history.nextSeq;
            // Loads the history only when the log's tail cannot answer, which is the same case that makes the first write load it.
            return WorkflowStore.TryPeekSeqWithoutReplay(out long seq) ? seq : History.nextSeq;
        }
    }
}

// Producer:Betsy
