using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// The workflow history on disk: an append-only JSONL log in segments, with snapshot payloads and asset bytes as
    /// content-addressed blobs in <see cref="WorkflowFileStore"/>. A record states one task's whole current state in one
    /// stack ("t"), drops it, clears a stack or rebinds an agent, so replaying the log in order rebuilds the tasks. Records
    /// of the retired "u" (undone) stack are ignored. The log is the only source of truth; memory keeps task headers and
    /// snapshot stubs, and a payload is read back when its snapshot is restored. Blobs are written before the record naming
    /// them, so a crash leaves at most an orphan blob. Main thread only.
    /// </summary>
    internal static class WorkflowStore
    {
        internal const string TasksStack = "t";
        internal const string UndoneStack = "u";

        private const string ActiveLogName = "log.jsonl";
        private const string HeadName = "head.json";
        private const string LegacyHistoryName = "workflow_history.json";
        private const string LegacyFilesName = "workflow_files";
        private const string LegacyDirectoryName = "workflow_legacy";
        private const long SegmentMaxBytes = 4L * 1024 * 1024;
        private const int SegmentMaxLines = 5000;
        private const int TailWindowBytes = 64 * 1024;
        internal const int LegacyBackupKeepDays = 14;

        private static readonly JsonSerializerSettings RecordSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore,
            StringEscapeHandling = StringEscapeHandling.Default
        };

        private static string _defaultRoot;
        private static string _rootOverride;
        private static bool _initialized;
        private static Head _head = new Head();
        private static bool _headReadable;
        private static long _activeBytes;
        private static int _activeLines = -1;
        private static long _nextSeqWithoutReplay;
        private static long _bytesSinceSizeCheck;

        internal static long SegmentMaxBytesForTests;
        internal static int SegmentMaxLinesForTests;
        internal static Func<string, bool> SegmentReadFailsForTests;
        internal static int AppendsForTests;
        internal static int DirectoryEnumerationsForTests;
        internal static int FullReplaysForTests;

        /// <summary>Torn or unparsable lines the last full replay skipped.</summary>
        internal static int LastReplaySkippedLines { get; private set; }

        internal static string Root => _rootOverride ?? (_defaultRoot ??=
            Path.GetFullPath(Path.Combine(Application.dataPath, "../Library/UnitySkills/workflow")));

        internal static string BlobsRoot
        {
            get
            {
                EnsureInitialized();
                return Path.Combine(Root, "blobs");
            }
        }

        internal static string LegacyRoot => Path.Combine(ParentDirectory, LegacyDirectoryName);

        private static string ParentDirectory => Path.GetDirectoryName(Root) ?? string.Empty;
        private static string ActiveLogPath => Path.Combine(Root, ActiveLogName);
        private static string HeadPath => Path.Combine(Root, HeadName);
        private static string SegmentPath(int n) => Path.Combine(Root, "log." + n.ToString("D6", CultureInfo.InvariantCulture) + ".jsonl");
        private static string RefsPath(int n) => Path.Combine(Root, "log." + n.ToString("D6", CultureInfo.InvariantCulture) + ".refs");

        private static long SegmentByteCap => SegmentMaxBytesForTests > 0 ? SegmentMaxBytesForTests : SegmentMaxBytes;
        private static int SegmentLineCap => SegmentMaxLinesForTests > 0 ? SegmentMaxLinesForTests : SegmentMaxLines;

        /// <summary>The active segment has reached the size or line cap; sealing waits for the deferred maintenance pass.</summary>
        internal static bool NeedsSeal => _activeBytes >= SegmentByteCap || (_activeLines >= 0 && _activeLines >= SegmentLineCap);

        #region Records

        private sealed class Record
        {
            [JsonProperty("r")] public string Kind;
            [JsonProperty("st")] public string Stack;
            [JsonProperty("new")] public int IsNew;
            [JsonProperty("id")] public string Id;
            [JsonProperty("s")] public long Seq;
            [JsonProperty("k")] public string Tag;
            [JsonProperty("d")] public string Description;
            [JsonProperty("ts")] public long Timestamp;
            [JsonProperty("ss")] public string SessionId;
            [JsonProperty("a")] public string Agent;
            [JsonProperty("g")] public string Group;
            [JsonProperty("tg")] public string Target;
            [JsonProperty("is")] public string IdSession;
            [JsonProperty("c")] public List<Change> Changes;
            [JsonProperty("kd")] public string CheckpointKind;
            [JsonProperty("l")] public string Label;
            [JsonProperty("cu")] public long Cursor;
            [JsonProperty("cs")] public long CursorStart;
            [JsonProperty("ce")] public long CursorEnd;
            [JsonProperty("to")] public string To;
            [JsonProperty("gd")] public string Guard;
            [JsonProperty("sp")] public List<long> Steps;
            [JsonProperty("al")] public List<string[]> Aliases;
            [JsonProperty("sc")] public string Scope;
        }

        /// <summary>
        /// A snapshot's header: what listing, classification, restore planning, rollback and GC read without its payload. Restoring the
        /// header after a payload is read back keeps it authoritative over the payload's copy of the same fields.
        /// </summary>
        internal sealed class Change
        {
            [JsonProperty("t")] public int Type;
            [JsonProperty("g")] public string GlobalObjectId;
            [JsonProperty("e")] public string EntityId;
            [JsonProperty("i")] public int InstanceId;
            [JsonProperty("n")] public string Name;
            [JsonProperty("tn")] public string TypeName;
            [JsonProperty("a")] public string AssetPath;
            [JsonProperty("pa")] public string PreviousAssetPath;
            [JsonProperty("ah")] public string AssetFingerprint;
            [JsonProperty("sh")] public string AfterStateHash;
            [JsonProperty("ct")] public string ComponentTypeName;
            [JsonProperty("sk")] public string SettingKey;
            [JsonProperty("pg")] public string ParentGlobalObjectId;
            [JsonProperty("pe")] public string ParentEntityId;
            [JsonProperty("pi")] public int ParentInstanceId;
            [JsonProperty("is")] public string IdSession;
            [JsonProperty("p")] public string Payload;
            [JsonProperty("fh")] public string FileHash;
            [JsonProperty("mh")] public string MetaFileHash;
            [JsonProperty("f")] public List<string> Files;
        }

        private sealed class Head
        {
            [JsonProperty("nextSeq")] public long NextSeq = 1;
            [JsonProperty("lastSegment")] public int LastSegment;
            [JsonProperty("legacyMovedAtMs")] public long LegacyMovedAtMs;
        }

        /// <summary>
        /// The record of <paramref name="task"/>'s current state in <paramref name="stack"/>, storing the payload of every
        /// snapshot that has none yet; null when a payload could not be stored. <paramref name="isNew"/> marks a newly
        /// recorded task.
        /// </summary>
        internal static string BuildTaskLine(string stack, WorkflowTask task, bool isNew)
        {
            var changes = new List<Change>(task.snapshots?.Count ?? 0);
            if (task.snapshots != null)
            {
                foreach (var snapshot in task.snapshots)
                {
                    if (snapshot == null)
                        continue;
                    if (!EnsurePayloadStored(snapshot))
                        return null;
                    changes.Add(CaptureHeader(snapshot));
                }
            }

            return Serialize(new Record
            {
                Kind = "task", Stack = stack, IsNew = isNew ? 1 : 0, Id = task.id, Seq = task.seq, Tag = task.tag,
                Description = task.description, Timestamp = task.timestamp, SessionId = task.sessionId, Agent = task.agent,
                Group = task.group, Target = task.target, IdSession = task.idSession, Changes = changes,
                CursorStart = task.cursorStart, CursorEnd = task.cursorEnd
            });
        }

        internal static string BuildCheckpointLine(WorkflowCheckpoint checkpoint) => Serialize(new Record
        {
            Kind = "cp", Id = checkpoint.id, Seq = checkpoint.seq, Timestamp = checkpoint.timestampMs, Agent = checkpoint.agent,
            CheckpointKind = checkpoint.kind, Label = checkpoint.label, Cursor = checkpoint.cursor
        });

        internal static string BuildLabelLine(string id, string label) => Serialize(new Record { Kind = "label", Id = id, Label = label });

        internal static string BuildRestoreLine(WorkflowRestoreRecord restore) => Serialize(new Record
        {
            Kind = "restore", Seq = restore.seq, To = restore.to, Guard = restore.guard, Agent = restore.agent,
            Steps = restore.steps, Aliases = restore.aliases, Scope = restore.scope
        });

        internal static string BuildDropLine(string stack, string id) => Serialize(new Record { Kind = "drop", Stack = stack, Id = id });

        internal static string BuildClearLine(string stack) => Serialize(new Record { Kind = "clear", Stack = stack });

        internal static string BuildAgentLine(string stack, string id, string agent) =>
            Serialize(new Record { Kind = "agent", Stack = stack, Id = id, Agent = agent });

        private static string Serialize(Record record) => JsonConvert.SerializeObject(record, Formatting.None, RecordSettings);

        /// <summary>
        /// Reads one log line by hand: Newtonsoft's reflection-based deserializer is several times slower on Unity's Mono
        /// runtime, and parsing dominated replay. Unknown properties are skipped, so newer records still load; a line that
        /// is not one JSON object, is truncated or has trailing content is rejected, as is one without a kind.
        /// </summary>
        private static Record Parse(string line)
        {
            try
            {
                using (var reader = new JsonTextReader(new StringReader(line)) { DateParseHandling = DateParseHandling.None })
                {
                    if (!reader.Read() || reader.TokenType != JsonToken.StartObject)
                        return null;
                    var record = ReadRecord(reader);
                    if (reader.Read() && reader.TokenType != JsonToken.Comment)
                        return null;
                    return string.IsNullOrEmpty(record.Kind) ? null : record;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Reads a record object; the reader sits on its StartObject and leaves on its EndObject.</summary>
        private static Record ReadRecord(JsonTextReader reader)
        {
            var record = new Record();
            while (true)
            {
                ReadRequired(reader);
                if (reader.TokenType == JsonToken.EndObject)
                    return record;
                switch (ReadPropertyName(reader))
                {
                    case "r": record.Kind = reader.ReadAsString(); break;
                    case "st": record.Stack = reader.ReadAsString(); break;
                    case "new": record.IsNew = reader.ReadAsInt32() ?? 0; break;
                    case "id": record.Id = reader.ReadAsString(); break;
                    case "s": record.Seq = ReadLong(reader); break;
                    case "k": record.Tag = reader.ReadAsString(); break;
                    case "d": record.Description = reader.ReadAsString(); break;
                    case "ts": record.Timestamp = ReadLong(reader); break;
                    case "ss": record.SessionId = reader.ReadAsString(); break;
                    case "a": record.Agent = reader.ReadAsString(); break;
                    case "g": record.Group = reader.ReadAsString(); break;
                    case "tg": record.Target = reader.ReadAsString(); break;
                    case "is": record.IdSession = reader.ReadAsString(); break;
                    case "c": record.Changes = ReadChanges(reader); break;
                    case "cs": record.CursorStart = ReadLong(reader); break;
                    case "ce": record.CursorEnd = ReadLong(reader); break;
                    case "kd": record.CheckpointKind = reader.ReadAsString(); break;
                    case "l": record.Label = reader.ReadAsString(); break;
                    case "cu": record.Cursor = ReadLong(reader); break;
                    case "to": record.To = reader.ReadAsString(); break;
                    case "gd": record.Guard = reader.ReadAsString(); break;
                    case "sp": record.Steps = ReadSeqs(reader); break;
                    case "al": record.Aliases = ReadAliases(reader); break;
                    case "sc": record.Scope = reader.ReadAsString(); break;
                    default: reader.Skip(); break;
                }
            }
        }

        /// <summary>Reads the "c" array (or null) of a record; the reader sits on the property name.</summary>
        private static List<Change> ReadChanges(JsonTextReader reader)
        {
            ReadRequired(reader);
            if (reader.TokenType == JsonToken.Null)
                return null;
            if (reader.TokenType != JsonToken.StartArray)
                throw new JsonReaderException("A workflow record's changes must be an array.");

            var changes = new List<Change>();
            while (true)
            {
                ReadRequired(reader);
                if (reader.TokenType == JsonToken.EndArray)
                    return changes;
                changes.Add(reader.TokenType == JsonToken.Null ? null : ReadChange(reader));
            }
        }

        /// <summary>Reads one change object; the reader sits on its StartObject and leaves on its EndObject.</summary>
        private static Change ReadChange(JsonTextReader reader)
        {
            if (reader.TokenType != JsonToken.StartObject)
                throw new JsonReaderException("A workflow change must be an object.");

            var change = new Change();
            while (true)
            {
                ReadRequired(reader);
                if (reader.TokenType == JsonToken.EndObject)
                    return change;
                switch (ReadPropertyName(reader))
                {
                    case "t": change.Type = reader.ReadAsInt32() ?? 0; break;
                    case "g": change.GlobalObjectId = reader.ReadAsString(); break;
                    case "e": change.EntityId = reader.ReadAsString(); break;
                    case "i": change.InstanceId = reader.ReadAsInt32() ?? 0; break;
                    case "n": change.Name = reader.ReadAsString(); break;
                    case "tn": change.TypeName = reader.ReadAsString(); break;
                    case "a": change.AssetPath = reader.ReadAsString(); break;
                    case "pa": change.PreviousAssetPath = reader.ReadAsString(); break;
                    case "ah": change.AssetFingerprint = reader.ReadAsString(); break;
                    case "sh": change.AfterStateHash = reader.ReadAsString(); break;
                    case "ct": change.ComponentTypeName = reader.ReadAsString(); break;
                    case "sk": change.SettingKey = reader.ReadAsString(); break;
                    case "pg": change.ParentGlobalObjectId = reader.ReadAsString(); break;
                    case "pe": change.ParentEntityId = reader.ReadAsString(); break;
                    case "pi": change.ParentInstanceId = reader.ReadAsInt32() ?? 0; break;
                    case "is": change.IdSession = reader.ReadAsString(); break;
                    case "p": change.Payload = reader.ReadAsString(); break;
                    case "fh": change.FileHash = reader.ReadAsString(); break;
                    case "mh": change.MetaFileHash = reader.ReadAsString(); break;
                    case "f": change.Files = ReadHashes(reader); break;
                    default: reader.Skip(); break;
                }
            }
        }

        /// <summary>Reads the "f" array (or null) of a change; the reader sits on the property name.</summary>
        private static List<string> ReadHashes(JsonTextReader reader)
        {
            ReadRequired(reader);
            if (reader.TokenType == JsonToken.Null)
                return null;
            if (reader.TokenType != JsonToken.StartArray)
                throw new JsonReaderException("A workflow change's files must be an array.");

            var hashes = new List<string>();
            while (true)
            {
                ReadRequired(reader);
                if (reader.TokenType == JsonToken.EndArray)
                    return hashes;
                if (reader.TokenType == JsonToken.Null)
                    hashes.Add(null);
                else if (reader.TokenType == JsonToken.String)
                    hashes.Add((string)reader.Value);
                else if (reader.TokenType is JsonToken.StartObject or JsonToken.StartArray)
                    throw new JsonReaderException("A workflow file hash must be a string.");
                else
                    hashes.Add(Convert.ToString(reader.Value, CultureInfo.InvariantCulture));
            }
        }

        /// <summary>Reads the "sp" array (or null) of a restore record, its steps' seqs; the reader sits on the property name.</summary>
        private static List<long> ReadSeqs(JsonTextReader reader)
        {
            ReadRequired(reader);
            if (reader.TokenType == JsonToken.Null)
                return null;
            if (reader.TokenType != JsonToken.StartArray)
                throw new JsonReaderException("A workflow restore's steps must be an array.");

            var seqs = new List<long>();
            while (true)
            {
                ReadRequired(reader);
                if (reader.TokenType == JsonToken.EndArray)
                    return seqs;
                if (reader.TokenType != JsonToken.Integer)
                    throw new JsonReaderException("A workflow step seq must be an integer.");
                seqs.Add(Convert.ToInt64(reader.Value, CultureInfo.InvariantCulture));
            }
        }

        /// <summary>Reads the "al" array (or null) of a restore record, its [old, new] alias pairs; the reader sits on the property name.</summary>
        private static List<string[]> ReadAliases(JsonTextReader reader)
        {
            ReadRequired(reader);
            if (reader.TokenType == JsonToken.Null)
                return null;
            if (reader.TokenType != JsonToken.StartArray)
                throw new JsonReaderException("A workflow restore's aliases must be an array.");

            var pairs = new List<string[]>();
            while (true)
            {
                ReadRequired(reader);
                if (reader.TokenType == JsonToken.EndArray)
                    return pairs;
                pairs.Add(reader.TokenType == JsonToken.Null ? null : ReadStrings(reader));
            }
        }

        /// <summary>Reads one array of strings; the reader sits on its StartArray and leaves on its EndArray.</summary>
        private static string[] ReadStrings(JsonTextReader reader)
        {
            if (reader.TokenType != JsonToken.StartArray)
                throw new JsonReaderException("A workflow alias pair must be an array.");

            var values = new List<string>();
            while (true)
            {
                ReadRequired(reader);
                if (reader.TokenType == JsonToken.EndArray)
                    return values.ToArray();
                if (reader.TokenType == JsonToken.Null)
                    values.Add(null);
                else if (reader.TokenType == JsonToken.String)
                    values.Add((string)reader.Value);
                else
                    throw new JsonReaderException("A workflow alias must be a string.");
            }
        }

        /// <summary>Reads an integral value as a long; decimal holds every integer a record writes exactly.</summary>
        private static long ReadLong(JsonTextReader reader) => (long)(reader.ReadAsDecimal() ?? 0);

        private static string ReadPropertyName(JsonTextReader reader)
        {
            if (reader.TokenType != JsonToken.PropertyName)
                throw new JsonReaderException("A workflow record property name was expected.");
            return (string)reader.Value;
        }

        /// <summary>Reads the next token, which a truncated line does not have.</summary>
        private static void ReadRequired(JsonTextReader reader)
        {
            if (!reader.Read())
                throw new JsonReaderException("A workflow record ended before its closing brace.");
        }

        private static bool EnsurePayloadStored(ObjectSnapshot snapshot)
        {
            if (!string.IsNullOrEmpty(snapshot.payloadHash))
                return true;
            string hash = WorkflowFileStore.StoreBytes(SkillsCommon.Utf8NoBom.GetBytes(JsonUtility.ToJson(snapshot)));
            if (string.IsNullOrEmpty(hash))
                return false;
            snapshot.payloadHash = hash;
            return true;
        }

        internal static Change CaptureHeader(ObjectSnapshot snapshot) => new Change
        {
            Type = (int)snapshot.type,
            GlobalObjectId = snapshot.globalObjectId,
            EntityId = snapshot.entityId,
            InstanceId = snapshot.objectInstanceId,
            Name = snapshot.objectName,
            TypeName = snapshot.typeName,
            AssetPath = snapshot.assetPath,
            PreviousAssetPath = snapshot.previousAssetPath,
            AssetFingerprint = snapshot.assetFingerprint,
            AfterStateHash = snapshot.afterStateHash,
            ComponentTypeName = snapshot.componentTypeName,
            SettingKey = snapshot.settingKey,
            ParentGlobalObjectId = snapshot.parentGameObjectId,
            ParentEntityId = snapshot.parentGameObjectEntityId,
            ParentInstanceId = snapshot.parentGameObjectInstanceId,
            IdSession = snapshot.idSession,
            Payload = snapshot.payloadHash,
            FileHash = snapshot.fileHash,
            MetaFileHash = snapshot.metaFileHash,
            Files = DirectoryHashes(snapshot)
        };

        internal static void ApplyHeader(Change change, ObjectSnapshot snapshot)
        {
            snapshot.type = (SnapshotType)change.Type;
            snapshot.globalObjectId = change.GlobalObjectId;
            snapshot.entityId = change.EntityId;
            snapshot.objectInstanceId = change.InstanceId;
            snapshot.objectName = change.Name;
            snapshot.typeName = change.TypeName;
            snapshot.assetPath = change.AssetPath;
            snapshot.previousAssetPath = change.PreviousAssetPath;
            snapshot.assetFingerprint = change.AssetFingerprint;
            snapshot.afterStateHash = change.AfterStateHash;
            snapshot.componentTypeName = change.ComponentTypeName;
            snapshot.settingKey = change.SettingKey;
            snapshot.parentGameObjectId = change.ParentGlobalObjectId;
            snapshot.parentGameObjectEntityId = change.ParentEntityId;
            snapshot.parentGameObjectInstanceId = change.ParentInstanceId;
            snapshot.idSession = change.IdSession;
            snapshot.payloadHash = change.Payload;
            snapshot.fileHash = change.FileHash;
            snapshot.metaFileHash = change.MetaFileHash;
            snapshot.fileRefs = change.Files;
        }

        /// <summary>The blob hashes of a deleted folder's entries; a stub keeps them from its header.</summary>
        private static List<string> DirectoryHashes(ObjectSnapshot snapshot)
        {
            if (snapshot.payloadReleased)
                return snapshot.fileRefs;
            if (snapshot.directoryEntries == null || snapshot.directoryEntries.Count == 0)
                return null;

            var hashes = new List<string>();
            foreach (var entry in snapshot.directoryEntries)
            {
                if (entry == null) continue;
                if (!string.IsNullOrEmpty(entry.fileHash)) hashes.Add(entry.fileHash);
                if (!string.IsNullOrEmpty(entry.metaFileHash)) hashes.Add(entry.metaFileHash);
            }
            return hashes.Count > 0 ? hashes : null;
        }

        private static ObjectSnapshot ToStub(Change change)
        {
            var snapshot = new ObjectSnapshot();
            ApplyHeader(change, snapshot);
            if (!string.IsNullOrEmpty(change.Payload))
                ClearPayloadFields(snapshot);
            return snapshot;
        }

        /// <summary>Drops a stored snapshot's payload from memory; <see cref="TryReadPayload"/> brings it back.</summary>
        internal static void ReleasePayload(ObjectSnapshot snapshot)
        {
            if (snapshot == null || snapshot.payloadReleased || string.IsNullOrEmpty(snapshot.payloadHash))
                return;
            snapshot.fileRefs = DirectoryHashes(snapshot);
            ClearPayloadFields(snapshot);
        }

        private static void ClearPayloadFields(ObjectSnapshot snapshot)
        {
            snapshot.originalJson = null;
            snapshot.objectReferencesCaptured = false;
            snapshot.objectReferences = null;
            snapshot.isDirectory = false;
            snapshot.deleteRecursively = false;
            snapshot.directoryEntries = null;
            snapshot.settingOldValueJson = null;
            snapshot.afterJson = null;
            snapshot.afterObjectReferencesCaptured = false;
            snapshot.afterObjectReferences = null;
            snapshot.settingAfterValueJson = null;
            snapshot.primitiveType = null;
            snapshot.posX = snapshot.posY = snapshot.posZ = 0;
            snapshot.rotX = snapshot.rotY = snapshot.rotZ = snapshot.rotW = 0;
            snapshot.scaleX = snapshot.scaleY = snapshot.scaleZ = 1;
            snapshot.components = null;
            snapshot.gameObjectHierarchy = null;
            snapshot.payloadReleased = true;
        }

        /// <summary>The payload JSON a stub's hash names, verified against the hash; a damaged blob is quarantined.</summary>
        internal static bool TryReadPayload(string hash, out string json, out string error)
        {
            json = null;
            if (!WorkflowFileStore.TryReadBlob(hash, out byte[] bytes, out error))
                return false;
            json = SkillsCommon.Utf8NoBom.GetString(bytes);
            return true;
        }

        private static void AddRecordRefs(Record record, HashSet<string> refs)
        {
            if (record.Changes == null)
                return;
            foreach (var change in record.Changes)
            {
                if (change == null) continue;
                if (!string.IsNullOrEmpty(change.Payload)) refs.Add(change.Payload);
                if (!string.IsNullOrEmpty(change.FileHash)) refs.Add(change.FileHash);
                if (!string.IsNullOrEmpty(change.MetaFileHash)) refs.Add(change.MetaFileHash);
                if (change.Files == null) continue;
                foreach (string hash in change.Files)
                    if (!string.IsNullOrEmpty(hash)) refs.Add(hash);
            }
        }

        #endregion

        #region Initialization and legacy history

        /// <summary>
        /// Runs once per domain, on the first store access: moves a pre-3.0 history aside (it is not migrated), creates the
        /// store and reads head.json. Costs a few stats and no directory enumeration once the store exists.
        /// </summary>
        internal static void EnsureInitialized()
        {
            if (_initialized)
                return;
            _initialized = true;
            _head = new Head();
            _headReadable = false;
            _nextSeqWithoutReplay = 0;
            _bytesSinceSizeCheck = 0;

            bool rootExisted = Directory.Exists(Root);
            if (rootExisted)
                _headReadable = TryReadHead(out _head);
            _head ??= new Head();

            long movedAtMs = MoveLegacyHistory(scanCorruptCopies: !rootExisted);
            if (movedAtMs > 0 && _head.LegacyMovedAtMs == 0)
                _head.LegacyMovedAtMs = movedAtMs;
            if (!rootExisted || (movedAtMs > 0 && _headReadable))
            {
                try
                {
                    Directory.CreateDirectory(Root);
                    WriteHead();
                    _headReadable = true;
                }
                catch (Exception ex)
                {
                    SkillsLogger.LogError($"Failed to create the workflow history store: {ex.Message}");
                }
            }

            _activeBytes = 0;
            _activeLines = 0;
            if (File.Exists(ActiveLogPath))
            {
                _activeBytes = new FileInfo(ActiveLogPath).Length;
                _activeLines = -1;
            }
        }

        /// <summary>
        /// Moves workflow_history.json (with its .bak, .tmp and quarantined copies) and workflow_files/ into
        /// workflow_legacy/. Returns the move time, or 0 when nothing was moved; an item that cannot be moved (a Windows
        /// lock) stays and is retried at the next initialization.
        /// </summary>
        private static long MoveLegacyHistory(bool scanCorruptCopies)
        {
            string parent = ParentDirectory;
            string history = Path.Combine(parent, LegacyHistoryName);
            var files = new List<string> { history, history + ".bak", history + LibraryStateFile.TempSuffix }
                .Where(File.Exists).ToList();
            string blobs = Path.Combine(parent, LegacyFilesName);
            bool hasBlobs = Directory.Exists(blobs);
            if ((scanCorruptCopies || files.Count > 0 || hasBlobs) && Directory.Exists(parent))
            {
                DirectoryEnumerationsForTests++;
                files.AddRange(Directory.GetFiles(parent, "workflow_history.corrupt.*.json"));
            }
            if (files.Count == 0 && !hasBlobs)
                return 0;

            int moved = 0;
            var failures = new List<string>();
            try
            {
                Directory.CreateDirectory(LegacyRoot);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"Workflow history: could not create {LegacyDirectoryName}/: {ex.Message}");
                return 0;
            }

            foreach (string file in files)
            {
                try
                {
                    File.Move(file, FreeLegacyPath(Path.GetFileName(file)));
                    moved++;
                }
                catch (Exception ex)
                {
                    failures.Add(Path.GetFileName(file) + ": " + ex.Message);
                }
            }
            if (hasBlobs)
            {
                try
                {
                    Directory.Move(blobs, FreeLegacyPath(LegacyFilesName));
                    moved++;
                }
                catch (Exception ex)
                {
                    failures.Add(LegacyFilesName + ": " + ex.Message);
                }
            }

            SkillsLogger.LogVerbose(
                $"Workflow history: moved {moved} pre-3.0 item(s) to {LegacyDirectoryName}/ (old tasks are not migrated)" +
                (failures.Count > 0 ? "; retrying later: " + string.Join("; ", failures) : "."));
            return moved > 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 0;
        }

        private static string FreeLegacyPath(string name)
        {
            string path = Path.Combine(LegacyRoot, name);
            if (!File.Exists(path) && !Directory.Exists(path))
                return path;
            return Path.Combine(LegacyRoot, name + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture));
        }

        internal static WorkflowLegacyBackupInfo GetLegacyBackupInfo()
        {
            EnsureInitialized();
            string root = LegacyRoot;
            if (!Directory.Exists(root))
                return null;

            long total = 0;
            DirectoryEnumerationsForTests++;
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch (Exception) { /* A file that vanished or is locked does not count. */ }
            }

            DateTime movedAtUtc = _head.LegacyMovedAtMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(_head.LegacyMovedAtMs).UtcDateTime
                : Directory.GetCreationTimeUtc(root);
            return new WorkflowLegacyBackupInfo(total, movedAtUtc);
        }

        internal static bool DeleteLegacyBackup()
        {
            EnsureInitialized();
            try
            {
                if (Directory.Exists(LegacyRoot))
                    Directory.Delete(LegacyRoot, true);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Failed to delete the old workflow history backup {LegacyRoot}: {ex.Message}");
                return false;
            }

            // An unreadable head is rewritten by the next replay; writing this one now would reset its seq.
            if (_head.LegacyMovedAtMs != 0 && _headReadable)
            {
                _head.LegacyMovedAtMs = 0;
                TryWriteHead();
            }
            return true;
        }

        /// <summary>Deletes workflow_legacy/ once it is <see cref="LegacyBackupKeepDays"/> days old.</summary>
        internal static void CleanUpLegacyIfDue()
        {
            EnsureInitialized();
            if (_head.LegacyMovedAtMs <= 0)
                return;
            long ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _head.LegacyMovedAtMs;
            if (ageMs >= LegacyBackupKeepDays * 24L * 3600 * 1000)
                DeleteLegacyBackup();
        }

        private static bool TryReadHead(out Head head)
        {
            head = null;
            try
            {
                if (!File.Exists(HeadPath))
                    return false;
                head = JsonConvert.DeserializeObject<Head>(File.ReadAllText(HeadPath, SkillsCommon.Utf8NoBom));
                return head != null;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"Workflow history head is unreadable, rebuilding it from the log: {ex.Message}");
                head = null;
                return false;
            }
        }

        private static void WriteHead() =>
            LibraryStateFile.WriteAllText(HeadPath, JsonConvert.SerializeObject(_head, Formatting.None));

        private static void TryWriteHead()
        {
            try
            {
                WriteHead();
                _headReadable = true;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Failed to write the workflow history head: {ex.Message}");
            }
        }

        #endregion

        #region Append and seq

        /// <summary>Appends <paramref name="lines"/> to the active segment in one write. False (logged) when it failed.</summary>
        internal static bool Append(IReadOnlyCollection<string> lines)
        {
            if (lines == null || lines.Count == 0)
                return true;
            EnsureInitialized();

            var text = new StringBuilder();
            foreach (string line in lines)
                text.Append(line).Append('\n');
            byte[] bytes = SkillsCommon.Utf8NoBom.GetBytes(text.ToString());
            try
            {
                LibraryStateFile.AppendLines(ActiveLogPath, bytes);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogError($"Failed to save workflow history: {ex.Message}");
                return false;
            }

            AppendsForTests++;
            _activeBytes += bytes.Length;
            if (_activeLines >= 0)
                _activeLines += lines.Count;
            _bytesSinceSizeCheck += bytes.Length;
            return true;
        }

        /// <summary>
        /// Whether the log and the blobs grew by a tenth of <paramref name="capacityBytes"/> since the last size check, so
        /// the next maintenance pass should measure the store.
        /// </summary>
        internal static bool GrewPastSizeCheck(long capacityBytes)
        {
            _bytesSinceSizeCheck += WorkflowFileStore.TakeNewBlobBytes();
            return capacityBytes > 0 && _bytesSinceSizeCheck * 10 >= capacityBytes;
        }

        /// <summary>
        /// The next seq without replaying the log, for the first write after a domain reload: the head's nextSeq, or past
        /// the last newly recorded task in the active segment's tail. False when the head is missing or unreadable, or the
        /// active segment cannot be read; the caller then loads the history.
        /// </summary>
        internal static bool TryAllocateSeqWithoutReplay(out long seq)
        {
            if (!TryPeekSeqWithoutReplay(out seq))
                return false;
            _nextSeqWithoutReplay++;
            return true;
        }

        /// <summary>The seq <see cref="TryAllocateSeqWithoutReplay"/> would return next, without taking it; false as there.</summary>
        internal static bool TryPeekSeqWithoutReplay(out long seq)
        {
            seq = 0;
            EnsureInitialized();
            if (_nextSeqWithoutReplay <= 0)
            {
                if (!_headReadable || !TryReadLastNewSeq(out long lastNew))
                    return false;
                _nextSeqWithoutReplay = Math.Max(_head.NextSeq, lastNew + 1);
            }
            seq = _nextSeqWithoutReplay;
            return true;
        }

        /// <summary>
        /// Seq of the newest "new" task or restore record in the active segment, read backwards from its end in growing
        /// windows; 0 when it holds none (the head then has the answer).
        /// </summary>
        private static bool TryReadLastNewSeq(out long seq)
        {
            seq = 0;
            if (!File.Exists(ActiveLogPath))
                return true;
            try
            {
                using (var stream = new FileStream(ActiveLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long length = stream.Length;
                    for (long window = TailWindowBytes; ; window *= 4)
                    {
                        long start = Math.Max(0, length - window);
                        var buffer = new byte[length - start];
                        stream.Seek(start, SeekOrigin.Begin);
                        int read = 0;
                        while (read < buffer.Length)
                        {
                            int n = stream.Read(buffer, read, buffer.Length - read);
                            if (n <= 0) break;
                            read += n;
                        }

                        string[] lines = SkillsCommon.Utf8NoBom.GetString(buffer, 0, read).Split('\n');
                        // A window that starts mid-file starts mid-line; that partial line is read whole by the next window.
                        int first = start > 0 ? 1 : 0;
                        for (int i = lines.Length - 1; i >= first; i--)
                        {
                            if (string.IsNullOrWhiteSpace(lines[i])) continue;
                            var record = Parse(lines[i]);
                            if (record != null && ((record.Kind == "task" && record.IsNew != 0) || record.Kind == "restore"))
                            {
                                seq = record.Seq;
                                return true;
                            }
                        }
                        if (start == 0)
                            return true;
                    }
                }
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"Workflow history tail could not be read: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Replay

        /// <summary>Everything one replay builds: the tasks, the checkpoints by id and the restore records.</summary>
        private sealed class ReplayState
        {
            public readonly StackBuilder Tasks = new StackBuilder();
            public readonly List<WorkflowCheckpoint> Checkpoints = new List<WorkflowCheckpoint>();
            public readonly List<WorkflowRestoreRecord> Restores = new List<WorkflowRestoreRecord>();
            public readonly Dictionary<string, int> CheckpointIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        /// <summary>One stack under construction: records address tasks by id, in O(1), keeping first-seen order.</summary>
        private sealed class StackBuilder
        {
            private readonly List<WorkflowTask> _items = new List<WorkflowTask>();
            private readonly Dictionary<string, int> _index = new Dictionary<string, int>(StringComparer.Ordinal);

            public void Put(WorkflowTask task)
            {
                if (_index.TryGetValue(task.id, out int i))
                {
                    _items[i] = task;
                    return;
                }
                _index[task.id] = _items.Count;
                _items.Add(task);
            }

            public void Remove(string id)
            {
                if (id == null || !_index.TryGetValue(id, out int i))
                    return;
                _items[i] = null;
                _index.Remove(id);
            }

            public WorkflowTask Find(string id) => id != null && _index.TryGetValue(id, out int i) ? _items[i] : null;

            public void Clear()
            {
                _items.Clear();
                _index.Clear();
            }

            public List<WorkflowTask> Build() => _items.Where(task => task != null).ToList();
        }

        /// <summary>
        /// Rebuilds the history from every segment, oldest first. A sealed segment missing its .refs gets them rebuilt.
        /// <paramref name="unreadable"/> is set when a segment could not be read or its .refs could not be written: what
        /// was rebuilt then references fewer blobs than the store holds.
        /// </summary>
        internal static WorkflowHistoryData Replay(out bool unreadable)
        {
            EnsureInitialized();
            FullReplaysForTests++;
            unreadable = false;
            LastReplaySkippedLines = 0;

            var state = new ReplayState();
            long maxSeq = 0;
            var segments = ListSealedSegments();
            foreach (int n in segments)
            {
                var refs = File.Exists(RefsPath(n)) ? null : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!ReplayFile(SegmentPath(n), state, refs, ref maxSeq, out _))
                {
                    unreadable = true;
                    continue;
                }
                if (refs != null && !TryWriteRefs(n, refs))
                    unreadable = true;
            }

            _activeLines = 0;
            if (File.Exists(ActiveLogPath))
            {
                if (ReplayFile(ActiveLogPath, state, null, ref maxSeq, out int lines))
                    _activeLines = lines;
                else
                {
                    unreadable = true;
                    _activeLines = -1;
                }
            }

            var history = new WorkflowHistoryData
            {
                tasks = state.Tasks.Build(),
                checkpoints = state.Checkpoints,
                restores = state.Restores,
                nextSeq = Math.Max(_head.NextSeq, maxSeq + 1)
            };

            int lastSegment = segments.Count > 0 ? segments[segments.Count - 1] : 0;
            if (!_headReadable || _head.LastSegment < lastSegment)
            {
                _head.LastSegment = Math.Max(_head.LastSegment, lastSegment);
                _head.NextSeq = history.nextSeq;
                TryWriteHead();
            }
            _nextSeqWithoutReplay = 0;
            return history;
        }

        private static bool ReplayFile(string path, ReplayState state, HashSet<string> refs, ref long maxSeq, out int lineCount)
        {
            lineCount = 0;
            try
            {
                if (SegmentReadFailsForTests != null && SegmentReadFailsForTests(path))
                    throw new IOException("simulated read failure");

                foreach (string line in ReadLinesShared(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    lineCount++;
                    var record = Parse(line);
                    if (record == null || !Apply(record, state))
                    {
                        LastReplaySkippedLines++;
                        continue;
                    }
                    if (record.Kind != "task" && record.Kind != "restore")
                        continue;
                    maxSeq = Math.Max(maxSeq, record.Seq);
                    if (refs != null)
                        AddRecordRefs(record, refs);
                }
                return true;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogError(
                    $"Failed to read workflow history segment {Path.GetFileName(path)}: {ex.Message}. Workflow store cleanup " +
                    "is paused for this session so the backups of the unread tasks are kept; clear the history to resume it.");
                return false;
            }
        }

        private static bool Apply(Record record, ReplayState state)
        {
            // The undone stack is retired: its records from earlier versions are read and dropped.
            if (record.Stack == UndoneStack)
                return true;
            var stack = record.Stack == TasksStack ? state.Tasks : null;
            switch (record.Kind)
            {
                case "task":
                    if (stack == null || string.IsNullOrEmpty(record.Id))
                        return false;
                    stack.Put(ToTask(record));
                    return true;
                case "cp":
                    if (string.IsNullOrEmpty(record.Id))
                        return false;
                    PutCheckpoint(state, ToCheckpoint(record));
                    return true;
                case "label":
                    if (string.IsNullOrEmpty(record.Id))
                        return false;
                    if (state.CheckpointIndex.TryGetValue(record.Id, out int labelled))
                        state.Checkpoints[labelled].label = record.Label;
                    return true;
                case "restore":
                    if (record.Seq <= 0)
                        return false;
                    state.Restores.Add(ToRestore(record));
                    return true;
                case "drop":
                    if (stack == null)
                        return false;
                    stack.Remove(record.Id);
                    return true;
                case "clear":
                    if (stack == null)
                        return false;
                    stack.Clear();
                    return true;
                case "agent":
                    if (stack == null)
                        return false;
                    var task = stack.Find(record.Id);
                    if (task != null)
                        task.agent = record.Agent;
                    return true;
                default:
                    // A record kind from a newer version: nothing in these two stacks depends on it.
                    return true;
            }
        }

        /// <summary>
        /// Adds a checkpoint, or merges it into the one with its id (see <see cref="MergeCheckpoint"/>), keeping the list order
        /// of first appearance.
        /// </summary>
        private static void PutCheckpoint(ReplayState state, WorkflowCheckpoint checkpoint)
        {
            if (state.CheckpointIndex.TryGetValue(checkpoint.id, out int i))
            {
                state.Checkpoints[i] = MergeCheckpoint(state.Checkpoints[i], checkpoint);
                return;
            }
            state.CheckpointIndex[checkpoint.id] = state.Checkpoints.Count;
            state.Checkpoints.Add(checkpoint);
        }

        /// <summary>
        /// The checkpoint an id holds after <paramref name="incoming"/> is recorded at it. An auto or guard checkpoint never replaces
        /// an existing one, so a position holds one checkpoint and an explicit one is never downgraded or relabelled. Any other kind
        /// replaces it, keeping the existing label unless the new one has a label.
        /// </summary>
        internal static WorkflowCheckpoint MergeCheckpoint(WorkflowCheckpoint existing, WorkflowCheckpoint incoming)
        {
            if (existing == null)
                return incoming;
            if (incoming.kind == WorkflowCheckpoint.KindAuto || incoming.kind == WorkflowCheckpoint.KindGuard)
                return existing;
            if (string.IsNullOrEmpty(incoming.label))
                incoming.label = existing.label;
            return incoming;
        }

        private static WorkflowCheckpoint ToCheckpoint(Record record) => new WorkflowCheckpoint
        {
            id = record.Id,
            seq = record.Seq,
            timestampMs = record.Timestamp,
            agent = record.Agent,
            kind = record.CheckpointKind,
            label = record.Label,
            cursor = record.Cursor
        };

        private static WorkflowRestoreRecord ToRestore(Record record) => new WorkflowRestoreRecord
        {
            seq = record.Seq,
            to = record.To,
            guard = record.Guard,
            agent = record.Agent,
            steps = record.Steps ?? new List<long>(),
            aliases = record.Aliases ?? new List<string[]>(),
            scope = record.Scope
        };

        private static WorkflowTask ToTask(Record record)
        {
            var task = new WorkflowTask
            {
                id = record.Id,
                seq = record.Seq,
                tag = record.Tag,
                description = record.Description,
                timestamp = record.Timestamp,
                sessionId = record.SessionId,
                agent = record.Agent,
                group = record.Group,
                target = record.Target,
                idSession = record.IdSession,
                cursorStart = record.CursorStart,
                cursorEnd = record.CursorEnd,
                snapshots = new List<ObjectSnapshot>(record.Changes?.Count ?? 0)
            };
            if (record.Changes != null)
            {
                foreach (var change in record.Changes)
                    if (change != null)
                        task.snapshots.Add(ToStub(change));
            }
            return task;
        }

        /// <summary>Every blob hash a segment's records name; false when the segment could not be read.</summary>
        private static bool TryScanRefs(string path, HashSet<string> refs, ref long maxSeq)
        {
            try
            {
                if (SegmentReadFailsForTests != null && SegmentReadFailsForTests(path))
                    throw new IOException("simulated read failure");

                foreach (string line in ReadLinesShared(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    var record = Parse(line);
                    if (record == null || (record.Kind != "task" && record.Kind != "restore"))
                        continue;
                    maxSeq = Math.Max(maxSeq, record.Seq);
                    AddRecordRefs(record, refs);
                }
                return true;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogError($"Failed to read workflow history segment {Path.GetFileName(path)}: {ex.Message}. " +
                                      "Workflow store cleanup is paused for this session; clear the history to resume it.");
                return false;
            }
        }

        private static IEnumerable<string> ReadLinesShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, SkillsCommon.Utf8NoBom))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                    yield return line;
            }
        }

        private static List<int> ListSealedSegments()
        {
            var segments = new List<int>();
            if (!Directory.Exists(Root))
                return segments;

            DirectoryEnumerationsForTests++;
            foreach (string path in Directory.EnumerateFiles(Root, "log.*"))
            {
                string name = Path.GetFileName(path);
                if (!name.EndsWith(".jsonl", StringComparison.Ordinal) || name.Length <= "log..jsonl".Length)
                    continue;
                string number = name.Substring(4, name.Length - 4 - ".jsonl".Length);
                if (int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0)
                    segments.Add(n);
            }
            segments.Sort();
            return segments;
        }

        private static bool TryWriteRefs(int n, HashSet<string> refs)
        {
            try
            {
                var text = new StringBuilder();
                foreach (string hash in refs)
                    text.Append(hash).Append('\n');
                LibraryStateFile.WriteAllText(RefsPath(n), text.ToString());
                return true;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogError($"Failed to write the reference list of workflow history segment {n}: {ex.Message}");
                return false;
            }
        }

        /// <summary>A sealed segment's references, rebuilt from the segment when its .refs is missing.</summary>
        private static HashSet<string> ReadOrRebuildRefs(int n)
        {
            var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string refsPath = RefsPath(n);
            if (File.Exists(refsPath))
            {
                try
                {
                    foreach (string line in ReadLinesShared(refsPath))
                        if (!string.IsNullOrWhiteSpace(line))
                            refs.Add(line.Trim());
                    return refs;
                }
                catch (Exception ex)
                {
                    SkillsLogger.LogVerbose($"Workflow segment {n} reference list unreadable, rebuilding it: {ex.Message}");
                    refs.Clear();
                }
            }

            long ignored = 0;
            return TryScanRefs(SegmentPath(n), refs, ref ignored) && TryWriteRefs(n, refs) ? refs : null;
        }

        #endregion

        #region Seal, eviction, GC, clear

        /// <summary>
        /// Seals the active segment once it reached its cap: renames it to log.&lt;n&gt;.jsonl, writes its .refs and the
        /// head. A failed rename (a Windows reader) leaves it growing until the next pass; <paramref name="unreadable"/> is
        /// set when the segment could not be read for its references.
        /// </summary>
        internal static bool SealIfNeeded(long knownNextSeq, out bool unreadable)
        {
            unreadable = false;
            EnsureInitialized();
            if (!NeedsSeal || !_headReadable || !File.Exists(ActiveLogPath))
                return false;

            var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long maxSeq = 0;
            if (!TryScanRefs(ActiveLogPath, refs, ref maxSeq))
            {
                unreadable = true;
                return false;
            }

            int n = _head.LastSegment + 1;
            while (File.Exists(SegmentPath(n)) || File.Exists(RefsPath(n)))
                n++;
            try
            {
                File.Move(ActiveLogPath, SegmentPath(n));
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"Workflow history segment could not be sealed yet: {ex.Message}");
                return false;
            }

            _activeBytes = 0;
            _activeLines = 0;
            _head.LastSegment = n;
            _head.NextSeq = Math.Max(Math.Max(_head.NextSeq, maxSeq + 1), Math.Max(knownNextSeq, _nextSeqWithoutReplay));
            // A crash before the .refs lands is repaired on load, which rebuilds a missing list from its segment.
            TryWriteRefs(n, refs);
            TryWriteHead();
            return true;
        }

        internal sealed class EvictionResult
        {
            internal bool Unreadable;
            internal bool NeedsGc;
            internal int RemovedSegments;
            internal long RemovedSegmentBytes;
        }

        /// <summary>
        /// Deletes whole sealed segments, oldest first, while the oldest one's newest record (its write time) is older than
        /// <paramref name="maxAgeDays"/> or the log plus the blobs would still exceed <paramref name="capacityBytes"/> after
        /// the GC that follows. The active segment is never evicted. A blob is freed by deleting segments up to the last one
        /// naming it, unless <paramref name="liveHashes"/> or the 10-minute write grace keeps it. 0 disables a limit.
        /// </summary>
        internal static EvictionResult EvictSegments(long capacityBytes, int maxAgeDays, HashSet<string> liveHashes, bool force)
        {
            EnsureInitialized();
            _bytesSinceSizeCheck = 0;
            WorkflowFileStore.TakeNewBlobBytes();
            var result = new EvictionResult();

            var segments = ListSealedSegments();
            var segmentRefs = new List<HashSet<string>>(segments.Count);
            var segmentBytes = new List<long>(segments.Count);
            var segmentTimes = new List<DateTime>(segments.Count);
            foreach (int n in segments)
            {
                var refs = ReadOrRebuildRefs(n);
                if (refs == null)
                {
                    result.Unreadable = true;
                    return result;
                }
                segmentRefs.Add(refs);
                segmentBytes.Add(FileLength(SegmentPath(n)) + FileLength(RefsPath(n)));
                segmentTimes.Add(File.GetLastWriteTimeUtc(SegmentPath(n)));
            }

            var activeRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long ignored = 0;
            if (File.Exists(ActiveLogPath) && !TryScanRefs(ActiveLogPath, activeRefs, ref ignored))
            {
                result.Unreadable = true;
                return result;
            }

            var lastRef = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < segmentRefs.Count; i++)
                foreach (string hash in segmentRefs[i])
                    lastRef[hash] = i;
            foreach (string hash in activeRefs)
                lastRef[hash] = int.MaxValue;
            if (liveHashes != null)
                foreach (string hash in liveHashes)
                    lastRef[hash] = int.MaxValue;

            long logBytes = segmentBytes.Sum() + FileLength(ActiveLogPath) + FileLength(HeadPath);
            long blobBytes = 0;
            long orphanBytes = 0;
            var freedWithSegment = new long[segments.Count];
            DateTime graceCutoff = DateTime.UtcNow - WorkflowFileStore.RecentWriteGrace;
            foreach (var entry in WorkflowFileStore.ListEntries())
            {
                blobBytes += entry.bytes;
                if (entry.lastWrite > graceCutoff)
                    continue;
                if (!lastRef.TryGetValue(entry.hash, out int last))
                    orphanBytes += entry.bytes;
                else if (last < segments.Count)
                    freedWithSegment[last] += entry.bytes;
            }

            int evict = 0;
            if (maxAgeDays > 0)
            {
                DateTime ageCutoff = DateTime.UtcNow.AddDays(-maxAgeDays);
                while (evict < segments.Count && segmentTimes[evict] < ageCutoff)
                    evict++;
            }
            bool overCapacity = capacityBytes > 0 && logBytes + blobBytes > capacityBytes;
            if (overCapacity)
            {
                long remaining = logBytes + blobBytes - orphanBytes;
                for (int i = 0; i < evict; i++)
                    remaining -= segmentBytes[i] + freedWithSegment[i];
                while (evict < segments.Count && remaining > capacityBytes)
                {
                    remaining -= segmentBytes[evict] + freedWithSegment[evict];
                    evict++;
                }
            }

            for (int i = 0; i < evict; i++)
            {
                try
                {
                    File.Delete(SegmentPath(segments[i]));
                }
                catch (Exception ex)
                {
                    SkillsLogger.LogWarning($"Failed to delete workflow history segment {segments[i]}: {ex.Message}");
                    break;
                }
                try { File.Delete(RefsPath(segments[i])); }
                catch (Exception) { /* An orphan .refs names no segment and is removed by the next clear. */ }
                result.RemovedSegments++;
                result.RemovedSegmentBytes += segmentBytes[i];
            }

            result.NeedsGc = force || overCapacity || result.RemovedSegments > 0;
            if (result.RemovedSegments > 0)
                SkillsLogger.LogWorkflow($"Workflow cleanup removed {result.RemovedSegments} history segment(s)");
            return result;
        }

        /// <summary>
        /// Deletes every blob no segment names, unless <paramref name="liveHashes"/> or the 10-minute write grace keeps it.
        /// False (nothing deleted) when a segment could not be read.
        /// </summary>
        internal static bool CollectGarbage(HashSet<string> liveHashes, out int removedCount, out long removedBytes)
        {
            removedCount = 0;
            removedBytes = 0;
            EnsureInitialized();

            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (int n in ListSealedSegments())
            {
                var refs = ReadOrRebuildRefs(n);
                if (refs == null)
                    return false;
                referenced.UnionWith(refs);
            }
            long ignored = 0;
            if (File.Exists(ActiveLogPath) && !TryScanRefs(ActiveLogPath, referenced, ref ignored))
                return false;
            if (liveHashes != null)
                referenced.UnionWith(liveHashes);

            WorkflowFileStore.CollectGarbage(referenced, out removedCount, out removedBytes);
            return true;
        }

        /// <summary>
        /// Deletes the whole log and every blob except <paramref name="keepHashes"/> (the open task's), whatever their age;
        /// seqs keep counting from <paramref name="nextSeq"/>.
        /// </summary>
        internal static void Clear(long nextSeq, HashSet<string> keepHashes)
        {
            EnsureInitialized();
            if (Directory.Exists(Root))
            {
                DirectoryEnumerationsForTests++;
                foreach (string path in Directory.GetFiles(Root, "log.*"))
                {
                    try { File.Delete(path); }
                    catch (Exception ex) { SkillsLogger.LogError($"Failed to delete {Path.GetFileName(path)} while clearing workflow history: {ex.Message}"); }
                }
            }
            WorkflowFileStore.CollectGarbage(keepHashes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                out _, out _, includeRecentWrites: true);

            _head.NextSeq = nextSeq;
            TryWriteHead();
            _activeBytes = 0;
            _activeLines = 0;
            _nextSeqWithoutReplay = 0;
            _bytesSinceSizeCheck = 0;
        }

        /// <summary>Bytes of the log: every segment, its .refs and the head.</summary>
        internal static long GetLogBytes()
        {
            if (!Directory.Exists(Root))
                return 0;

            long total = 0;
            DirectoryEnumerationsForTests++;
            foreach (string path in Directory.EnumerateFiles(Root))
            {
                string name = Path.GetFileName(path);
                if (name == HeadName || name.StartsWith("log.", StringComparison.Ordinal))
                    total += FileLength(path);
            }
            return total;
        }

        private static long FileLength(string path)
        {
            try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch (Exception) { return 0; }
        }

        #endregion

        /// <summary>Points the store at <paramref name="root"/> (null = the project's) with fresh state and default caps.</summary>
        internal static void SetRootOverrideForTests(string root)
        {
            _rootOverride = string.IsNullOrEmpty(root) ? null : Path.GetFullPath(root);
            SegmentMaxBytesForTests = 0;
            SegmentMaxLinesForTests = 0;
            SegmentReadFailsForTests = null;
            ResetState();
        }

        /// <summary>Forgets everything read from disk, as a domain reload does.</summary>
        internal static void ResetState()
        {
            _initialized = false;
            _head = new Head();
            _headReadable = false;
            _activeBytes = 0;
            _activeLines = -1;
            _nextSeqWithoutReplay = 0;
            _bytesSinceSizeCheck = 0;
            LastReplaySkippedLines = 0;
        }
    }
}

// Producer:Betsy
