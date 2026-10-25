using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// Pure state hashes of a snapshotted object: the EditorJsonUtility JSON with object-reference leaves blanked, plus the
    /// object references keyed by property path. Instance ids are session-local, so they never reach the hash.
    /// </summary>
    internal static class WorkflowStateHash
    {
        private const string ReferencesSeparator = "\n--refs--\n";
        private static readonly string[] ReferenceLeafKeys = { "instanceID", "fileID", "guid", "type" };

        /// <summary>The JSON in compact form with every object-reference leaf emptied to {}; the input unchanged when it does not parse.</summary>
        internal static string Normalize(string editorJson)
        {
            if (string.IsNullOrEmpty(editorJson))
                return editorJson;

            JToken root;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(editorJson)) { DateParseHandling = DateParseHandling.None })
                    root = JToken.Load(reader);
            }
            catch (JsonException)
            {
                return editorJson;
            }

            BlankReferenceLeaves(root);
            return root.ToString(Formatting.None);
        }

        /// <summary>One "propertyPath=globalObjectId" line per reference, ordinally sorted and joined by newlines.</summary>
        internal static string ReferencesKey(IEnumerable<ObjectReferenceData> references)
        {
            if (references == null)
                return string.Empty;

            var lines = references
                .Where(reference => reference != null)
                .Select(reference => reference.propertyPath + "=" + ReferenceIdentity(reference))
                .ToList();
            lines.Sort(StringComparer.Ordinal);
            return string.Join("\n", lines);
        }

        /// <summary>The first 16 lowercase hex digits of SHA1 over the normalized JSON, the separator and the references key.</summary>
        internal static string Hash(string normalizedJson, string referencesKey)
        {
            using (var sha = SHA1.Create())
            {
                var bytes = Encoding.UTF8.GetBytes((normalizedJson ?? string.Empty) + ReferencesSeparator + (referencesKey ?? string.Empty));
                var hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++)
                    sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        internal static string StateHash(string editorJson, IEnumerable<ObjectReferenceData> references) =>
            Hash(Normalize(editorJson), ReferencesKey(references));

        // An object of a never-saved scene has no usable GlobalObjectId, so its session-local entity id names it instead.
        internal static string ReferenceIdentity(ObjectReferenceData reference)
        {
            if (string.IsNullOrEmpty(reference.globalObjectId) || WorkflowManager.IsUnsavedSceneGlobalObjectId(reference.globalObjectId))
                return "entity:" + reference.entityId;
            return reference.globalObjectId;
        }

        internal static void BlankReferenceLeaves(JToken token)
        {
            if (token is JObject obj)
            {
                if (IsReferenceLeaf(obj))
                {
                    obj.RemoveAll();
                    return;
                }
                foreach (var value in obj.PropertyValues())
                    BlankReferenceLeaves(value);
            }
            else if (token is JArray array)
            {
                foreach (var item in array)
                    BlankReferenceLeaves(item);
            }
        }

        // EditorJsonUtility writes an object reference as {"instanceID": n}, or as {"fileID", "guid", "type"} for an asset.
        private static bool IsReferenceLeaf(JObject obj) =>
            obj["instanceID"] != null && obj.Properties().All(property => ReferenceLeafKeys.Contains(property.Name));
    }
}

// Producer:Betsy
