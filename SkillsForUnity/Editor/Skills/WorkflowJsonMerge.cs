using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    /// <summary>
    /// The outcome of a three-way merge: the JSON to apply, the paths that changed both since the restore's state and now (with the
    /// current value of each), and how many leaves the merge reverted to the before state.
    /// </summary>
    internal sealed class JsonMergeResult
    {
        public string MergedJson;
        public List<string> ConflictPaths = new List<string>();
        public Dictionary<string, string> ConflictValues = new Dictionary<string, string>(StringComparer.Ordinal);
        public int RevertedLeaves;
    }

    /// <summary>
    /// Property-level three-way merge of a snapshotted object (design section 6.4): the state before a step, the state the step left,
    /// and the state now. A leaf the step did not change keeps its current value; one the step changed and nothing changed since goes
    /// back to its before value; one already at the before value stays; any other leaf is a conflict. Pure, so it runs without Unity.
    /// </summary>
    internal static class WorkflowJsonMerge
    {
        internal const string ReferenceConflictPrefix = "ref:";
        private static readonly JToken NullValue = JValue.CreateNull();

        private sealed class MergeRun
        {
            public JToken BeforeSource;
            public JObject Target;
            public ISet<string> Overwrite;
            public JsonMergeResult Result;
        }

        /// <summary>
        /// Merges the current state into a copy of itself. Object-reference leaves are compared blanked, as <see cref="WorkflowStateHash"/>
        /// blanks them, and are never written: their objects come back from the merged references.
        /// </summary>
        internal static JsonMergeResult Merge(string beforeJson, string afterJson, string currentJson, ISet<string> overwritePaths)
        {
            JObject before = ParseObject(beforeJson);
            var result = new JsonMergeResult();
            var run = new MergeRun
            {
                BeforeSource = before,
                Target = ParseObject(currentJson),
                Overwrite = overwritePaths,
                Result = result
            };
            MergeObject(Normalized(before), Normalized(ParseObject(afterJson)), Normalized(run.Target), new List<object>(), run);
            result.MergedJson = run.Target.ToString(Formatting.None);
            return result;
        }

        /// <summary>
        /// Merges object references keyed by property path, with the same rules as <see cref="Merge"/>. Returns the references to apply: the
        /// before entry for a reverted path, the current entry for the rest. A conflict path is added as "ref:&lt;path&gt;".
        /// </summary>
        internal static List<ObjectReferenceData> MergeReferences(List<ObjectReferenceData> before, List<ObjectReferenceData> after,
            List<ObjectReferenceData> current, ISet<string> overwritePaths, List<string> conflictPaths, out int reverted)
        {
            reverted = 0;
            var beforeByPath = ByPath(before);
            var afterByPath = ByPath(after);
            var currentByPath = ByPath(current);
            var paths = currentByPath.Keys.Concat(beforeByPath.Keys).Concat(afterByPath.Keys).Distinct(StringComparer.Ordinal);

            var merged = new List<ObjectReferenceData>();
            foreach (string path in paths)
            {
                beforeByPath.TryGetValue(path, out var beforeRef);
                afterByPath.TryGetValue(path, out var afterRef);
                currentByPath.TryGetValue(path, out var currentRef);

                ObjectReferenceData chosen;
                if (SameReference(beforeRef, afterRef))
                {
                    chosen = currentRef;
                }
                else if (SameReference(currentRef, afterRef))
                {
                    chosen = beforeRef;
                    reverted++;
                }
                else if (SameReference(currentRef, beforeRef))
                {
                    chosen = currentRef;
                }
                else
                {
                    string conflictPath = ReferenceConflictPrefix + path;
                    conflictPaths.Add(conflictPath);
                    chosen = overwritePaths.Contains(conflictPath) ? beforeRef : currentRef;
                }

                if (chosen != null)
                    merged.Add(chosen);
                else if (currentRef != null)
                    merged.Add(new ObjectReferenceData { propertyPath = path, globalObjectId = string.Empty });
            }
            return merged;
        }

        private static void MergeObject(JObject before, JObject after, JObject current, List<object> segments, MergeRun run)
        {
            var keys = current.Properties().Select(property => property.Name)
                .Union(before.Properties().Select(property => property.Name), StringComparer.Ordinal)
                .Union(after.Properties().Select(property => property.Name), StringComparer.Ordinal)
                .ToList();
            foreach (string key in keys)
            {
                segments.Add(key);
                MergeValue(before[key], after[key], current[key], segments, run);
                segments.RemoveAt(segments.Count - 1);
            }
        }

        private static void MergeValue(JToken before, JToken after, JToken current, List<object> segments, MergeRun run)
        {
            if (before is JObject beforeObject && after is JObject afterObject && current is JObject currentObject)
            {
                MergeObject(beforeObject, afterObject, currentObject, segments, run);
                return;
            }

            if (before is JArray beforeArray && after is JArray afterArray && current is JArray currentArray &&
                beforeArray.Count == afterArray.Count && afterArray.Count == currentArray.Count)
            {
                for (int i = 0; i < currentArray.Count; i++)
                {
                    segments.Add(i);
                    MergeValue(beforeArray[i], afterArray[i], currentArray[i], segments, run);
                    segments.RemoveAt(segments.Count - 1);
                }
                return;
            }

            MergeLeaf(before, after, current, segments, run);
        }

        private static void MergeLeaf(JToken before, JToken after, JToken current, List<object> segments, MergeRun run)
        {
            if (Same(before, after))
                return;

            if (Same(current, after))
            {
                Revert(segments, run);
                run.Result.RevertedLeaves++;
                return;
            }

            if (Same(current, before))
                return;

            string path = PathOf(segments);
            run.Result.ConflictPaths.Add(path);
            run.Result.ConflictValues[path] = current == null ? "null" : current.ToString(Formatting.None);
            if (run.Overwrite.Contains(path))
                Revert(segments, run);
        }

        // Writes the before value of a leaf into the target. A reference leaf is never written: the object it names comes back from the merged references.
        private static void Revert(List<object> segments, MergeRun run)
        {
            JToken value = ValueAt(run.BeforeSource, segments);
            if (IsReferenceLeaf(value))
                return;
            SetAt(run.Target, segments, value?.DeepClone());
        }

        private static bool IsReferenceLeaf(JToken token) =>
            token is JObject obj && obj["instanceID"] != null;

        private static JObject Normalized(JObject raw)
        {
            var copy = (JObject)raw.DeepClone();
            WorkflowStateHash.BlankReferenceLeaves(copy);
            return copy;
        }

        private static JObject ParseObject(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
                return JObject.Load(reader);
        }

        // A missing key or index counts as null, so a leaf one side lacks compares equal to an explicit null.
        private static bool Same(JToken x, JToken y) => JToken.DeepEquals(x ?? NullValue, y ?? NullValue);

        private static bool SameReference(ObjectReferenceData x, ObjectReferenceData y)
        {
            if (x == null || y == null)
                return x == null && y == null;
            return WorkflowStateHash.ReferenceIdentity(x) == WorkflowStateHash.ReferenceIdentity(y);
        }

        private static Dictionary<string, ObjectReferenceData> ByPath(List<ObjectReferenceData> references)
        {
            var byPath = new Dictionary<string, ObjectReferenceData>(StringComparer.Ordinal);
            foreach (var reference in references ?? new List<ObjectReferenceData>())
            {
                if (reference != null && !string.IsNullOrEmpty(reference.propertyPath) && !byPath.ContainsKey(reference.propertyPath))
                    byPath.Add(reference.propertyPath, reference);
            }
            return byPath;
        }

        private static JToken ValueAt(JToken root, IEnumerable<object> segments)
        {
            JToken node = root;
            foreach (object segment in segments)
            {
                if (node == null)
                    return null;
                node = segment is string key ? (node as JObject)?[key] : (node as JArray)?[(int)segment];
            }
            return node;
        }

        // The parent of a leaf exists in the target: a walk only enters an object or an array the target also has.
        private static void SetAt(JToken root, List<object> segments, JToken value)
        {
            JToken parent = ValueAt(root, segments.Take(segments.Count - 1));
            object last = segments[segments.Count - 1];
            if (last is string key)
            {
                if (value == null)
                    ((JObject)parent).Remove(key);
                else
                    ((JObject)parent)[key] = value;
            }
            else
            {
                ((JArray)parent)[(int)last] = value;
            }
        }

        // Leaf paths: dotted keys, with [i] for an array index, e.g. Light.m_Color.r.
        private static string PathOf(IEnumerable<object> segments)
        {
            var path = new StringBuilder();
            foreach (object segment in segments)
            {
                if (segment is string key)
                {
                    if (path.Length > 0)
                        path.Append('.');
                    path.Append(key);
                }
                else
                {
                    path.Append('[').Append(((int)segment).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(']');
                }
            }
            return path.ToString();
        }
    }
}

// Producer:Betsy
