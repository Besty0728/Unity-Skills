using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UnitySkills.Tests.OptionalPackages
{
    /// <summary>
    /// Timeline asset + PlayableDirector skills against a throwaway scene GameObject and an asset under
    /// Assets/R6OptProbe. Timeline is a hard dependency of UnitySkills itself (always present), but this
    /// test assembly has no compile-time reference to Unity.Timeline, so TimelineAsset/PlayableDirector are
    /// read back only through the skills' own JSON and, via the base class's Member()/ComponentNamed(),
    /// reflection — never a direct UnityEngine.Timeline type reference.
    /// </summary>
    [TestFixture]
    public class TimelineSkillsLiveTests : OptionalPackageTestBase
    {
        protected override string ProbeSkill => "timeline_list_tracks";

        [Test]
        public void Create_CreatesDirectorWithTimelineAsset()
        {
            EnsureProbeFolder();
            var result = Ok(Run("timeline_create", new JObject { ["name"] = "R6Timeline", ["folder"] = ProbeFolder }));
            Assert.That(result["gameObjectName"]?.ToString(), Is.EqualTo("R6Timeline"), result.ToString());
            var assetPath = result["assetPath"]?.ToString();
            Assert.That(assetPath, Does.StartWith(ProbeFolder), result.ToString());

            var go = Find("R6Timeline");
            var director = ComponentNamed(go, "PlayableDirector");
            Assert.That(director, Is.Not.Null, "timeline_create must attach a PlayableDirector.");

            var asset = (UnityEngine.Object)Member(director, "playableAsset");
            Assert.That(asset, Is.Not.Null);
            Assert.That(asset.GetType().Name, Is.EqualTo("TimelineAsset"));
            Assert.That(AssetDatabase.GetAssetPath(asset), Is.EqualTo(assetPath));
        }

        [Test]
        public void AddAudioTrackAndClip_ListedByListTracksAndAssetOutputCount()
        {
            EnsureProbeFolder();
            var create = Ok(Run("timeline_create", new JObject { ["name"] = "R6Timeline2", ["folder"] = ProbeFolder }));
            var goName = create["gameObjectName"]?.ToString();

            var track = Ok(Run("timeline_add_audio_track", new JObject { ["name"] = goName, ["trackName"] = "R6Audio" }));
            Assert.That(track["trackName"]?.ToString(), Is.EqualTo("R6Audio"), track.ToString());

            var clip = Ok(Run("timeline_add_clip", new JObject { ["name"] = goName, ["trackName"] = "R6Audio", ["start"] = 1.5, ["duration"] = 2.5 }));
            Assert.That(clip["clipStart"]?.Value<double>(), Is.EqualTo(1.5).Within(1e-6), clip.ToString());
            Assert.That(clip["clipDuration"]?.Value<double>(), Is.EqualTo(2.5).Within(1e-6), clip.ToString());

            var listed = Ok(Run("timeline_list_tracks", new JObject { ["name"] = goName }));
            Assert.That(listed["count"]?.Value<int>(), Is.EqualTo(1), listed.ToString());
            var trackJson = listed["tracks"]?[0];
            Assert.That(trackJson?["name"]?.ToString(), Is.EqualTo("R6Audio"));
            Assert.That(trackJson?["type"]?.ToString(), Is.EqualTo("AudioTrack"));
            Assert.That(trackJson?["clipCount"]?.Value<int>(), Is.EqualTo(1));

            // Cross-check against the asset itself, independent of the skill's own JSON.
            var director = ComponentNamed(Find(goName), "PlayableDirector");
            var asset = (UnityEngine.Object)Member(director, "playableAsset");
            Assert.That(Member(asset, "outputTrackCount"), Is.EqualTo(1));
        }

        [Test]
        public void SetDuration_WritesFixedDurationAndWrapMode_ReadsBackOnDirectorAndAsset()
        {
            EnsureProbeFolder();
            var create = Ok(Run("timeline_create", new JObject { ["name"] = "R6Timeline3", ["folder"] = ProbeFolder }));
            var goName = create["gameObjectName"]?.ToString();

            var result = Ok(Run("timeline_set_duration", new JObject { ["name"] = goName, ["duration"] = 12.5, ["wrapMode"] = "Loop" }));
            Assert.That(result["duration"]?.Value<double>(), Is.EqualTo(12.5).Within(1e-9), result.ToString());
            Assert.That(result["wrapMode"]?.ToString(), Is.EqualTo("Loop"), result.ToString());

            var director = ComponentNamed(Find(goName), "PlayableDirector");
            // TimelineAsset.duration (not .fixedDuration, which subtracts one discrete tick) returns
            // m_FixedDuration verbatim whenever durationMode is FixedLength, which timeline_set_duration
            // always sets — so this is an exact, framerate-independent round trip of what was written.
            var asset = (UnityEngine.Object)Member(director, "playableAsset");
            Assert.That((double)Member(asset, "duration"), Is.EqualTo(12.5).Within(1e-9));
            Assert.That(Member(director, "extrapolationMode").ToString(), Is.EqualTo("Loop"));
        }

        [Test]
        public void SetDuration_InvalidWrapMode_IsRejectedBeforeAnyWrite()
        {
            EnsureProbeFolder();
            var create = Ok(Run("timeline_create", new JObject { ["name"] = "R6Timeline4", ["folder"] = ProbeFolder }));
            var goName = create["gameObjectName"]?.ToString();

            var director = ComponentNamed(Find(goName), "PlayableDirector");
            var asset = (UnityEngine.Object)Member(director, "playableAsset");
            var durationBefore = (double)Member(asset, "duration");
            var wrapModeBefore = Member(director, "extrapolationMode").ToString();

            AssertSemanticInvalid(Run("timeline_set_duration", new JObject { ["name"] = goName, ["duration"] = 42, ["wrapMode"] = "Bogus" }), "wrapMode");

            Assert.That((double)Member(asset, "duration"), Is.EqualTo(durationBefore).Within(1e-9));
            Assert.That(Member(director, "extrapolationMode").ToString(), Is.EqualTo(wrapModeBefore));
        }

        [Test]
        public void RemoveTrack_ItIsGoneFromListTracksAndAssetOutputCount()
        {
            EnsureProbeFolder();
            var create = Ok(Run("timeline_create", new JObject { ["name"] = "R6Timeline5", ["folder"] = ProbeFolder }));
            var goName = create["gameObjectName"]?.ToString();
            Ok(Run("timeline_add_audio_track", new JObject { ["name"] = goName, ["trackName"] = "R6Audio" }));

            var removed = Ok(Run("timeline_remove_track", new JObject { ["name"] = goName, ["trackName"] = "R6Audio" }));
            Assert.That(removed["removed"]?.ToString(), Is.EqualTo("R6Audio"), removed.ToString());

            var listed = Ok(Run("timeline_list_tracks", new JObject { ["name"] = goName }));
            Assert.That(listed["count"]?.Value<int>(), Is.EqualTo(0), listed.ToString());

            var director = ComponentNamed(Find(goName), "PlayableDirector");
            var asset = (UnityEngine.Object)Member(director, "playableAsset");
            Assert.That(Member(asset, "outputTrackCount"), Is.EqualTo(0));
        }
    }
}

// Producer:Betsy
