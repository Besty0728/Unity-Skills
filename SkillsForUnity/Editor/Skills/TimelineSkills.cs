using UnityEngine;
using UnityEditor;
using UnityEngine.Timeline;
using UnityEngine.Playables;
using System.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Timeline skills — creates assets, tracks, and clips.
    /// </summary>
    public static class TimelineSkills
    {
        private const string BindingEntityIdNote = "entityId of the GameObject to bind; outranks bindingObjectName.";

        [UnitySkill("timeline_create", "Create a new Timeline asset and Director instance",
            Category = SkillCategory.Timeline, Operation = SkillOperation.Create,
            Tags = new[] { "timeline", "director", "playable", "asset" },
            Outputs = new[] { "assetPath", "gameObjectName", "directorInstanceId" },
            RequiresInput = new[] { "name" },
            TracksWorkflow = true, MutatesScene = true, MutatesAssets = true)]
        public static object TimelineCreate(string name, string folder = "Assets/Timelines")
        {
            if (Validate.Required(name, "name") is object nameErr) return nameErr;
            if (name.Contains("/") || name.Contains("\\") || name.Contains(".."))
                return new { error = "name must not contain path separators" };
            if (Validate.SafePath(folder, "folder") is object folderErr) return folderErr;

            if (!System.IO.Directory.Exists(folder))
                System.IO.Directory.CreateDirectory(folder);

            string assetPath = System.IO.Path.Combine(folder, name + ".playable");
            assetPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);

            var timelineAsset = ScriptableObject.CreateInstance<TimelineAsset>();
            AssetDatabase.CreateAsset(timelineAsset, assetPath);

            var go = new GameObject(name);
            var director = go.AddComponent<PlayableDirector>();
            director.playableAsset = timelineAsset;

            AssetDatabase.SaveAssets();
            
            return new
            {
                success = true,
                assetPath,
                gameObjectName = go.name,
                directorEntityId = UnityObjectIdUtility.GetEntityId(director),
                directorInstanceId = UnityObjectIdUtility.GetObjectId(director)
            };
        }

        [UnitySkill("timeline_add_audio_track", "Add an Audio track to a Timeline",
            Category = SkillCategory.Timeline, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "timeline", "audio", "track", "sound" },
            Outputs = new[] { "trackName" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object TimelineAddAudioTrack(string name = null, int instanceId = 0, string path = null, string trackName = "Audio Track", string entityId = null)
        {
            var (go, findErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path, entityId: entityId);
            if (findErr != null) return findErr;

            var director = go.GetComponent<PlayableDirector>();
            if (director == null) return new { error = "PlayableDirector component not found" };

            var timeline = director.playableAsset as TimelineAsset;
            if (timeline == null) return new { error = "No TimelineAsset assigned to Director" };

            var track = timeline.CreateTrack<AudioTrack>(null, trackName);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            return new { success = true, trackName = track.name };
        }

        [UnitySkill("timeline_add_animation_track", "Add an Animation track to a Timeline, optionally binding an object",
            Category = SkillCategory.Timeline, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "timeline", "animation", "track", "binding" },
            Outputs = new[] { "trackName", "boundObject" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true, MutatesScene = true, MutatesAssets = true)]
        public static object TimelineAddAnimationTrack(string name = null, int instanceId = 0, string path = null, string trackName = "Animation Track", string bindingObjectName = null, string entityId = null,
            [SkillParam(BindingEntityIdNote)] string bindingEntityId = null)
        {
            var (go, findErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path, entityId: entityId);
            if (findErr != null) return findErr;

            var director = go.GetComponent<PlayableDirector>();
            if (director == null) return new { error = "PlayableDirector component not found" };

            var timeline = director.playableAsset as TimelineAsset;
            if (timeline == null) return new { error = "No TimelineAsset assigned to Director" };

            // Resolved before the track exists, so a binding that names nothing leaves the timeline unchanged.
            GameObject bindingGo = null;
            if (!string.IsNullOrEmpty(bindingObjectName) || UnityObjectIdUtility.IsGivenEntityId(bindingEntityId))
            {
                var (found, bindErr) = GameObjectFinder.FindOrError(name: bindingObjectName, entityId: bindingEntityId);
                if (bindErr != null) return bindErr;
                bindingGo = found;
            }

            var track = timeline.CreateTrack<AnimationTrack>(null, trackName);

            if (bindingGo != null)
            {
                var animator = bindingGo.GetComponent<Animator>();
                if (animator == null) animator = bindingGo.AddComponent<Animator>();

                director.SetGenericBinding(track, animator);
            }
            
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            return new { success = true, trackName = track.name, boundObject = bindingGo != null ? bindingGo.name : "None" };
        }

        [UnitySkill("timeline_add_activation_track", "Add an Activation track to control object visibility",
            Category = SkillCategory.Timeline, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "timeline", "activation", "track", "visibility" },
            Outputs = new[] { "trackName" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object TimelineAddActivationTrack(string name = null, int instanceId = 0, string path = null, string trackName = "Activation Track", string entityId = null)
        {
            var (timeline, director, err) = GetTimeline(name, instanceId, path, entityId);
            if (err != null) return err;
            var track = timeline.CreateTrack<ActivationTrack>(null, trackName);
            AssetDatabase.SaveAssets();
            return new { success = true, trackName = track.name };
        }

        [UnitySkill("timeline_add_control_track", "Add a Control track for nested Timelines or prefab spawning",
            Category = SkillCategory.Timeline, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "timeline", "control", "track", "nested", "prefab" },
            Outputs = new[] { "trackName" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object TimelineAddControlTrack(string name = null, int instanceId = 0, string path = null, string trackName = "Control Track", string entityId = null)
        {
            var (timeline, director, err) = GetTimeline(name, instanceId, path, entityId);
            if (err != null) return err;
            var track = timeline.CreateTrack<ControlTrack>(null, trackName);
            AssetDatabase.SaveAssets();
            return new { success = true, trackName = track.name };
        }

        [UnitySkill("timeline_add_signal_track", "Add a Signal track for event markers",
            Category = SkillCategory.Timeline, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "timeline", "signal", "track", "event", "marker" },
            Outputs = new[] { "trackName" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object TimelineAddSignalTrack(string name = null, int instanceId = 0, string path = null, string trackName = "Signal Track", string entityId = null)
        {
            var (timeline, director, err) = GetTimeline(name, instanceId, path, entityId);
            if (err != null) return err;
            var track = timeline.CreateTrack<SignalTrack>(null, trackName);
            AssetDatabase.SaveAssets();
            return new { success = true, trackName = track.name };
        }

        [UnitySkill("timeline_remove_track", "Remove a track by name from a Timeline",
            Category = SkillCategory.Timeline, Operation = SkillOperation.Delete,
            Tags = new[] { "timeline", "track", "remove", "delete" },
            Outputs = new[] { "success", "removed" },
            RequiresInput = new[] { "gameObject", "trackName" },
            TracksWorkflow = true,
            RiskLevel = "medium", MutatesAssets = true)]
        public static object TimelineRemoveTrack(string name = null, int instanceId = 0, string path = null, string trackName = null, string entityId = null)
        {
            var (timeline, director, err) = GetTimeline(name, instanceId, path, entityId);
            if (err != null) return err;
            var track = timeline.GetOutputTracks().FirstOrDefault(t => t.name == trackName);
            if (track == null) return new { error = $"Track not found: {trackName}" };
            timeline.DeleteTrack(track);
            AssetDatabase.SaveAssets();
            return new { success = true, removed = trackName };
        }

        [UnitySkill("timeline_list_tracks", "List all tracks in a Timeline",
            Category = SkillCategory.Timeline, Operation = SkillOperation.Query,
            Tags = new[] { "timeline", "track", "list", "inspect" },
            Outputs = new[] { "count", "tracks" },
            RequiresInput = new[] { "gameObject" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object TimelineListTracks(string name = null, int instanceId = 0, string path = null, string entityId = null)
        {
            var (timeline, director, err) = GetTimeline(name, instanceId, path, entityId);
            if (err != null) return err;
            var tracks = timeline.GetOutputTracks().Select(t => new
            {
                name = t.name, type = t.GetType().Name,
                muted = t.muted, clipCount = t.GetClips().Count()
            }).ToArray();
            return new { count = tracks.Length, tracks };
        }

        [UnitySkill("timeline_add_clip", "Add a clip to a track by track name",
            Category = SkillCategory.Timeline, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "timeline", "clip", "track", "add" },
            Outputs = new[] { "trackName", "clipStart", "clipDuration" },
            RequiresInput = new[] { "gameObject", "trackName" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object TimelineAddClip(string name = null, int instanceId = 0, string path = null, string trackName = null, double start = 0, double duration = 1, string entityId = null)
        {
            var (timeline, director, err) = GetTimeline(name, instanceId, path, entityId);
            if (err != null) return err;
            var track = timeline.GetOutputTracks().FirstOrDefault(t => t.name == trackName);
            if (track == null) return new { error = $"Track not found: {trackName}" };

            // TrackAsset.CreateDefaultClip() only creates a clip when the track's own [TrackClipType] attribute
            // specifies a PlayableAsset type; a track without that attribute (SignalTrack marks itself with SignalEmitter
            // rather than clips, and has no such attribute at all) makes it log a warning and return null. We check this explicitly
            // so the caller knows which track type rejected the call and why, instead of an unexplained NullReferenceException.
            var clip = track.CreateDefaultClip();
            if (clip == null)
            {
                return new
                {
                    error = track is SignalTrack
                        ? $"Track '{trackName}' is a Signal Track. Signal tracks hold SignalEmitter markers, not generic TimelineClips - timeline_add_clip does not support this track type."
                        : $"Track '{trackName}' ({track.GetType().Name}) has no default clip type registered and cannot accept a generic clip via timeline_add_clip.",
                    errorCode = SkillErrorCode.SemanticInvalid.ToWireString(),
                    parameter = "trackName",
                    trackType = track.GetType().Name
                };
            }

            clip.start = start;
            clip.duration = duration;
            AssetDatabase.SaveAssets();
            return new { success = true, trackName, clipStart = clip.start, clipDuration = clip.duration };
        }

        [UnitySkill("timeline_set_duration", "Set Timeline duration and wrap mode",
            Category = SkillCategory.Timeline, Operation = SkillOperation.Modify,
            Tags = new[] { "timeline", "duration", "wrapMode", "length" },
            Outputs = new[] { "success", "duration", "wrapMode" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object TimelineSetDuration(string name = null, int instanceId = 0, string path = null, double duration = 0, string wrapMode = null, string entityId = null)
        {
            var (timeline, director, err) = GetTimeline(name, instanceId, path, entityId);
            if (err != null) return err;

            // Must be parsed before writing duration: otherwise an invalid wrapMode gets discarded while
            // the timeline still gets clipped to FixedLength at the requested duration.
            if (!SkillParamUtil.TryParseOptionalEnum<DirectorWrapMode>(wrapMode, "wrapMode", out var wm, out var wrapModeError))
                return wrapModeError;

            timeline.fixedDuration = duration;
            timeline.durationMode = TimelineAsset.DurationMode.FixedLength;
            if (wm.HasValue) director.extrapolationMode = wm.Value;
            AssetDatabase.SaveAssets();
            return new { success = true, duration, wrapMode = director.extrapolationMode.ToString() };
        }

        [UnitySkill("timeline_play", "Play, pause, or stop a Timeline (Editor preview)",
            Category = SkillCategory.Timeline, Operation = SkillOperation.Execute,
            Tags = new[] { "timeline", "play", "pause", "stop", "preview" },
            Outputs = new[] { "success", "action", "time" },
            RequiresInput = new[] { "gameObject" })]
        public static object TimelinePlay(string name = null, int instanceId = 0, string path = null, string action = "play", string entityId = null)
        {
            var (go, findErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path, entityId: entityId);
            if (findErr != null) return findErr;
            var director = go.GetComponent<PlayableDirector>();
            if (director == null) return new { error = "PlayableDirector not found" };
            switch (action.ToLower())
            {
                case "play": director.Play(); break;
                case "pause": director.Pause(); break;
                case "stop": director.Stop(); break;
                default: return new { error = $"Unknown action: {action}. Use play/pause/stop" };
            }
            return new { success = true, action, time = director.time };
        }

        [UnitySkill("timeline_set_binding", "Set the binding object for a track",
            Category = SkillCategory.Timeline, Operation = SkillOperation.Modify,
            Tags = new[] { "timeline", "binding", "track", "assign" },
            Outputs = new[] { "success", "trackName", "boundTo" },
            // "director" dropped as redundant: "gameObject" already covers the same name/entityId/instanceId/path locator.
            // The binding target is genuinely required (the body resolves it via GameObjectFinder with no fallback), by
            // either bindingObjectName or bindingEntityId; the compound token lets either one satisfy it.
            RequiresInput = new[] { "gameObject", "trackName", "bindingObjectName|bindingEntityId" },
            TracksWorkflow = true)]
        public static object TimelineSetBinding(string name = null, int instanceId = 0, string path = null, string trackName = null, string bindingObjectName = null, string entityId = null,
            [SkillParam(BindingEntityIdNote)] string bindingEntityId = null)
        {
            if (string.IsNullOrEmpty(bindingObjectName) && !UnityObjectIdUtility.IsGivenEntityId(bindingEntityId))
                return Validate.Required(bindingObjectName, "bindingObjectName");
            var (timeline, director, err) = GetTimeline(name, instanceId, path, entityId);
            if (err != null) return err;
            var track = timeline.GetOutputTracks().FirstOrDefault(t => t.name == trackName);
            if (track == null) return new { error = $"Track not found: {trackName}" };
            var (bindGo, bindErr) = GameObjectFinder.FindOrError(name: bindingObjectName, entityId: bindingEntityId);
            if (bindErr != null) return bindErr;
            director.SetGenericBinding(track, bindGo);
            return new { success = true, trackName, boundTo = bindGo.name };
        }

        private static (TimelineAsset, PlayableDirector, object) GetTimeline(string name, int instanceId, string path, string entityId)
        {
            var (go, findErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path, entityId: entityId);
            if (findErr != null) return (null, null, findErr);
            var director = go.GetComponent<PlayableDirector>();
            if (director == null) return (null, null, new { error = "PlayableDirector not found" });
            var timeline = director.playableAsset as TimelineAsset;
            if (timeline == null) return (null, null, new { error = "No TimelineAsset assigned" });
            return (timeline, director, null);
        }
    }
}

// Producer:Betsy
