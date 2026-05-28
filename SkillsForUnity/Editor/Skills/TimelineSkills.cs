using UnityEngine;
using UnityEditor;
using UnityEngine.Timeline;
using UnityEngine.Playables;
using System.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Timeline skills - Create assets, tracks, clips.
    /// </summary>
    public static class TimelineSkills
    {
        [UnitySkill("timeline_create", "Create a new Timeline asset and Director instance",
            Category = SkillCategory.Timeline, Operation = SkillOperation.Create,
            Tags = new[] { "timeline", "director", "playable", "asset" },
            Outputs = new[] { "assetPath", "gameObjectName", "directorInstanceId" },
            TracksWorkflow = true)]
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

            // Create Asset
            var timelineAsset = ScriptableObject.CreateInstance<TimelineAsset>();
            AssetDatabase.CreateAsset(timelineAsset, assetPath);
            
            // Create GameObject
            var go = new GameObject(name);
            var director = go.AddComponent<PlayableDirector>();
            director.playableAsset = timelineAsset;

            AssetDatabase.SaveAssets();
            
            return new 
            { 
                success = true, 
                assetPath, 
                gameObjectName = go.name, 
                directorInstanceId = (int)director.GetEntityId() 
            };
        }

        [UnitySkill("timeline_add_audio_track", "Add an Audio track to a Timeline",
            Category = SkillCategory.Timeline, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "timeline", "audio", "track", "sound" },
            Outputs = new[] { "trackName" },
            RequiresInput = new[] { "director" },
            TracksWorkflow = true)]
        public static object TimelineAddAudioTrack(string name = null, int instanceId = 0, string path = null, string trackName = "Audio Track")
        {
            var (go, findErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path);
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
            RequiresInput = new[] { "director" },
            TracksWorkflow = true)]
        public static object TimelineAddAnimationTrack(string name = null, int instanceId = 0, string path = null, string trackName = "Animation Track", string bindingObjectName = null)
        {
            var (go, findErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path);
            if (findErr != null) return findErr;

            var director = go.GetComponent<PlayableDirector>();
            if (director == null) return new { error = "PlayableDirector component not found" };

            var timeline = director.playableAsset as TimelineAsset;
            if (timeline == null) return new { error = "No TimelineAsset assigned to Director" };

            var track = timeline.CreateTrack<AnimationTrack>(null, trackName);

            if (!string.IsNullOrEmpty(bindingObjectName))
            {
                var (bindingGo, bindErr) = GameObjectFinder.FindOrError(name: bindingObjectName);
                if (bindErr != null) return bindErr;

                var animator = bindingGo.GetComponent<Animator>();
                if (animator == null) animator = bindingGo.AddComponent<Animator>();

                director.SetGenericBinding(track, animator);
            }
            
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            return new { success = true, trackName = track.name, boundObject = bindingObjectName ?? "None" };
        }

        [UnitySkill("timeline_add_activation_track", "Add an Activation track to control object visibility",
            Category = SkillCategory.Timeline, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "timeline", "activation", "track", "visibility" },
            Outputs = new[] { "trackName" },
            RequiresInput = new[] { "director" },
            TracksWorkflow = true)]
        public static object TimelineAddActivationTrack(string name = null, int instanceId = 0, string path = null, string trackName = "Activation Track")
        {
            var (timeline, director, err) = GetTimeline(name, instanceId, path);
            if (err != null) return err;
            var track = timeline.CreateTrack<ActivationTrack>(null, trackName);
            AssetDatabase.SaveAssets();
            return new { success = true, trackName = track.name };
        }

        [UnitySkill("timeline_add_control_track", "Add a Control track for nested Timelines or prefab spawning",
            Category = SkillCategory.Timeline, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "timeline", "control", "track", "nested", "prefab" },
            Outputs = new[] { "trackName" },
            RequiresInput = new[] { "director" },
            TracksWorkflow = true)]
        public static object TimelineAddControlTrack(string name = null, int instanceId = 0, string path = null, string trackName = "Control Track")
        {
            var (timeline, director, err) = GetTimeline(name, instanceId, path);
            if (err != null) return err;
            var track = timeline.CreateTrack<ControlTrack>(null, trackName);
            AssetDatabase.SaveAssets();
            return new { success = true, trackName = track.name };
        }

        [UnitySkill("timeline_add_signal_track", "Add a Signal track for event markers",
            Category = SkillCategory.Timeline, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "timeline", "signal", "track", "event", "marker" },
            Outputs = new[] { "trackName" },
            RequiresInput = new[] { "director" },
            TracksWorkflow = true)]
        public static object TimelineAddSignalTrack(string name = null, int instanceId = 0, string path = null, string trackName = "Signal Track")
        {
            var (timeline, director, err) = GetTimeline(name, instanceId, path);
            if (err != null) return err;
            var track = timeline.CreateTrack<SignalTrack>(null, trackName);
            AssetDatabase.SaveAssets();
            return new { success = true, trackName = track.name };
        }

        [UnitySkill("timeline_remove_track", "Remove a track by name from a Timeline",
            Category = SkillCategory.Timeline, Operation = SkillOperation.Delete,
            Tags = new[] { "timeline", "track", "remove", "delete" },
            Outputs = new[] { "success", "removed" },
            RequiresInput = new[] { "director", "track" },
            TracksWorkflow = true)]
        public static object TimelineRemoveTrack(string name = null, int instanceId = 0, string path = null, string trackName = null)
        {
            var (timeline, director, err) = GetTimeline(name, instanceId, path);
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
            RequiresInput = new[] { "director" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object TimelineListTracks(string name = null, int instanceId = 0, string path = null)
        {
            var (timeline, director, err) = GetTimeline(name, instanceId, path);
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
            RequiresInput = new[] { "director", "track" },
            TracksWorkflow = true)]
        public static object TimelineAddClip(string name = null, int instanceId = 0, string path = null, string trackName = null, double start = 0, double duration = 1)
        {
            var (timeline, director, err) = GetTimeline(name, instanceId, path);
            if (err != null) return err;
            var track = timeline.GetOutputTracks().FirstOrDefault(t => t.name == trackName);
            if (track == null) return new { error = $"Track not found: {trackName}" };
            var clip = track.CreateDefaultClip();
            clip.start = start;
            clip.duration = duration;
            AssetDatabase.SaveAssets();
            return new { success = true, trackName, clipStart = clip.start, clipDuration = clip.duration };
        }

        [UnitySkill("timeline_set_duration", "Set Timeline duration and wrap mode",
            Category = SkillCategory.Timeline, Operation = SkillOperation.Modify,
            Tags = new[] { "timeline", "duration", "wrapMode", "length" },
            Outputs = new[] { "success", "duration", "wrapMode" },
            RequiresInput = new[] { "director" },
            TracksWorkflow = true)]
        public static object TimelineSetDuration(string name = null, int instanceId = 0, string path = null, double duration = 0, string wrapMode = null)
        {
            var (timeline, director, err) = GetTimeline(name, instanceId, path);
            if (err != null) return err;
            timeline.fixedDuration = duration;
            timeline.durationMode = TimelineAsset.DurationMode.FixedLength;
            if (!string.IsNullOrEmpty(wrapMode))
            {
                if (System.Enum.TryParse<DirectorWrapMode>(wrapMode, true, out var wm))
                    director.extrapolationMode = wm;
            }
            AssetDatabase.SaveAssets();
            return new { success = true, duration, wrapMode = director.extrapolationMode.ToString() };
        }

        [UnitySkill("timeline_play", "Play, pause, or stop a Timeline (Editor preview)",
            Category = SkillCategory.Timeline, Operation = SkillOperation.Execute,
            Tags = new[] { "timeline", "play", "pause", "stop", "preview" },
            Outputs = new[] { "success", "action", "time" },
            RequiresInput = new[] { "director" })]
        public static object TimelinePlay(string name = null, int instanceId = 0, string path = null, string action = "play")
        {
            var (go, findErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path);
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
            RequiresInput = new[] { "director", "track", "gameObject" },
            TracksWorkflow = true)]
        public static object TimelineSetBinding(string name = null, int instanceId = 0, string path = null, string trackName = null, string bindingObjectName = null)
        {
            var (timeline, director, err) = GetTimeline(name, instanceId, path);
            if (err != null) return err;
            var track = timeline.GetOutputTracks().FirstOrDefault(t => t.name == trackName);
            if (track == null) return new { error = $"Track not found: {trackName}" };
            var (bindGo, bindErr) = GameObjectFinder.FindOrError(name: bindingObjectName);
            if (bindErr != null) return bindErr;
            director.SetGenericBinding(track, bindGo);
            return new { success = true, trackName, boundTo = bindingObjectName };
        }

        private static (TimelineAsset, PlayableDirector, object) GetTimeline(string name, int instanceId, string path)
        {
            var (go, findErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path);
            if (findErr != null) return (null, null, findErr);
            var director = go.GetComponent<PlayableDirector>();
            if (director == null) return (null, null, new { error = "PlayableDirector not found" });
            var timeline = director.playableAsset as TimelineAsset;
            if (timeline == null) return (null, null, new { error = "No TimelineAsset assigned" });
            return (timeline, director, null);
        }
    }
}
