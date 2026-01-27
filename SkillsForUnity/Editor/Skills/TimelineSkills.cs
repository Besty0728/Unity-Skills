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
        [UnitySkill("timeline_create", "Create a new Timeline asset and Director instance")]
        public static object TimelineCreate(string name, string folder = "Assets/Timelines")
        {
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
                directorInstanceId = director.GetInstanceID() 
            };
        }

        [UnitySkill("timeline_add_audio_track", "Add an Audio track to a Timeline")]
        public static object TimelineAddAudioTrack(string directorObjectName, string trackName = "Audio Track")
        {
            var go = GameObject.Find(directorObjectName);
            if (go == null) return new { error = $"GameObject not found: {directorObjectName}" };
            
            var director = go.GetComponent<PlayableDirector>();
            if (director == null) return new { error = "PlayableDirector component not found" };
            
            var timeline = director.playableAsset as TimelineAsset;
            if (timeline == null) return new { error = "No TimelineAsset assigned to Director" };

            var track = timeline.CreateTrack<AudioTrack>(null, trackName);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            return new { success = true, trackName = track.name };
        }
        
        [UnitySkill("timeline_add_animation_track", "Add an Animation track to a Timeline, optionally binding an object")]
        public static object TimelineAddAnimationTrack(string directorObjectName, string trackName = "Animation Track", string bindingObjectName = null)
        {
            var go = GameObject.Find(directorObjectName);
            if (go == null) return new { error = $"GameObject not found: {directorObjectName}" };
            
            var director = go.GetComponent<PlayableDirector>();
            if (director == null) return new { error = "PlayableDirector component not found" };
            
            var timeline = director.playableAsset as TimelineAsset;
            if (timeline == null) return new { error = "No TimelineAsset assigned to Director" };

            var track = timeline.CreateTrack<AnimationTrack>(null, trackName);
            
            if (!string.IsNullOrEmpty(bindingObjectName))
            {
                var bindingGo = GameObject.Find(bindingObjectName);
                if (bindingGo != null)
                {
                    var animator = bindingGo.GetComponent<Animator>();
                    if (animator == null) animator = bindingGo.AddComponent<Animator>();
                    
                    director.SetGenericBinding(track, animator);
                }
            }
            
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            return new { success = true, trackName = track.name, boundObject = bindingObjectName ?? "None" };
        }
    }
}
