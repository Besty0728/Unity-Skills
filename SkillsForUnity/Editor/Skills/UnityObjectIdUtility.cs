using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    internal static class UnityObjectIdUtility
    {
        public static string GetEntityId(UnityEngine.Object obj)
        {
            if (obj == null)
                return null;

#if UNITY_6000_4_OR_NEWER
            return EntityId.ToULong(obj.GetEntityId()).ToString(CultureInfo.InvariantCulture);
#else
            return obj.GetInstanceID().ToString(CultureInfo.InvariantCulture);
#endif
        }

        public static int GetLegacyInstanceId(UnityEngine.Object obj)
        {
            if (obj == null)
                return 0;

#if UNITY_6000_4_OR_NEWER
            return 0;
#else
            return obj.GetInstanceID();
#endif
        }

        public static int GetObjectId(UnityEngine.Object obj)
        {
            return GetLegacyInstanceId(obj);
        }

        public static bool MatchesEntityId(UnityEngine.Object obj, string entityId)
        {
            return obj != null &&
                !string.IsNullOrWhiteSpace(entityId) &&
                string.Equals(GetEntityId(obj), entityId.Trim(), StringComparison.Ordinal);
        }

        public static bool MatchesObjectId(UnityEngine.Object obj, int objectId)
        {
            return objectId != 0 && GetLegacyInstanceId(obj) == objectId;
        }

        public static UnityEngine.Object EntityIdToObject(string entityId)
        {
            if (string.IsNullOrWhiteSpace(entityId))
                return null;

            var trimmed = entityId.Trim();

#if UNITY_6000_4_OR_NEWER
            if (!ulong.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var rawId))
                return null;

            try
            {
                return EditorUtility.EntityIdToObject(EntityId.FromULong(rawId));
            }
            catch
            {
                return null;
            }
#else
            if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var instanceId))
                return null;

            return EditorUtility.InstanceIDToObject(instanceId);
#endif
        }

        public static UnityEngine.Object ObjectIdToObject(int objectId)
        {
            if (objectId == 0)
                return null;

#if UNITY_6000_4_OR_NEWER
            return null;
#else
            return EditorUtility.InstanceIDToObject(objectId);
#endif
        }

        public static bool HasMissingObjectReference(SerializedProperty property)
        {
            if (property == null ||
                property.propertyType != SerializedPropertyType.ObjectReference ||
                property.objectReferenceValue != null)
                return false;

#if UNITY_6000_4_OR_NEWER
            return property.objectReferenceEntityIdValue.IsValid();
#else
            return property.objectReferenceInstanceIDValue != 0;
#endif
        }

        public static int[] GetSelectionObjectIds()
        {
#if UNITY_6000_4_OR_NEWER
            return Array.Empty<int>();
#else
            return Selection.instanceIDs ?? Array.Empty<int>();
#endif
        }

        public static string[] GetSelectionEntityIds()
        {
            return Selection.objects?
                .Where(obj => obj != null)
                .Select(GetEntityId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .ToArray() ?? Array.Empty<string>();
        }

        public static void SetSelectionObjectIds(IEnumerable<int> objectIds)
        {
            var ids = objectIds?
                .Where(id => id != 0)
                .Distinct()
                .ToArray() ?? Array.Empty<int>();

#if UNITY_6000_4_OR_NEWER
            Selection.objects = ids
                .Select(ObjectIdToObject)
                .Where(obj => obj != null)
                .ToArray();
#else
            Selection.instanceIDs = ids;
#endif
        }

        public static void SetSelectionEntityIds(IEnumerable<string> entityIds)
        {
            Selection.objects = entityIds?
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .Select(EntityIdToObject)
                .Where(obj => obj != null)
                .ToArray() ?? Array.Empty<UnityEngine.Object>();
        }
    }
}
