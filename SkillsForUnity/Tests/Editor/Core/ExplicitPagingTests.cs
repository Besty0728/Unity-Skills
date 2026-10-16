using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Explicit paging keys used to take effect only with verbose=false, while a body without verbose (the router's
    /// default, true) silently got every item back. They now page whatever verbose says, as
    /// references/protocol-operating-mode.md promises; verbose still governs the automatic truncation alone.
    /// find_objects_by_name declares no offset/limit/verbose, so every one of those keys reaches the envelope.
    /// </summary>
    [TestFixture]
    public class ExplicitPagingTests
    {
        private const string Probe = "ExplicitPagingProbe";
        private const int ProbeCount = 5;

        private SurfaceProfileKind _savedProfile;
        private bool _savedAutoTruncate;
        private int _savedPageSize;

        [SetUp]
        public void SetUp()
        {
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            _savedAutoTruncate = SkillRouter.SummaryAutoTruncate;
            _savedPageSize = SkillRouter.SummaryPageSize;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            for (var i = 0; i < ProbeCount; i++)
                new GameObject($"{Probe}_{i}");
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            SkillRouter.SummaryAutoTruncate = _savedAutoTruncate;
            SkillRouter.SummaryPageSize = _savedPageSize;
            SkillsSurfaceProfile.Current = _savedProfile;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [TestCase(null)]
        [TestCase(true)]
        [TestCase(false)]
        public void PagingKeys_PageTheResponseWhateverVerboseSays(bool? verbose)
        {
            SkillRouter.SummaryAutoTruncate = false;
            var everything = Names(FindProbes(new JObject { ["verbose"] = true })["objects"]);
            Assert.That(everything, Has.Length.EqualTo(ProbeCount));

            var args = new JObject { ["pageOffset"] = 1, ["pageLimit"] = 2 };
            if (verbose.HasValue)
                args["verbose"] = verbose.Value;
            var page = FindProbes(args);

            Assert.That(page["isTruncated"]?.Value<bool>(), Is.True, page.ToString(Formatting.None));
            Assert.That(page["totalCount"]?.Value<int>(), Is.EqualTo(ProbeCount));
            Assert.That(page["offset"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(page["limit"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(page["showing"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(page["nextOffset"]?.Value<int>(), Is.EqualTo(3));
            Assert.That(page["count"]?.Value<int>(), Is.EqualTo(ProbeCount), "The skill's own fields are kept beside the page.");
            Assert.That(Names(page["objects"]), Is.EqualTo(everything.Skip(1).Take(2)));
            // verbose=true with the same paging keys returns this same page, so the hint must not offer it alone.
            Assert.That(page["hint"]?.ToString(), Does.Contain("pageOffset=3").And.Contain("drop the paging keys"));
        }

        [Test]
        public void OffsetAndLimit_PageWhenTheSkillDeclaresNoSuchParameter()
        {
            var page = FindProbes(new JObject { ["offset"] = 3, ["limit"] = 2 });

            Assert.That(page["isTruncated"]?.Value<bool>(), Is.True, page.ToString(Formatting.None));
            Assert.That(page["offset"]?.Value<int>(), Is.EqualTo(3));
            Assert.That(page["showing"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(page["nextOffset"], Is.Null);
            Assert.That(page["hint"]?.ToString(), Does.Contain("(last page)"));
        }

        [Test]
        public void PageOffsetPastTheEnd_ReturnsAnEmptyPageWithVerboseOmitted()
        {
            var page = FindProbes(new JObject { ["pageOffset"] = ProbeCount });

            Assert.That(page["isTruncated"]?.Value<bool>(), Is.True, page.ToString(Formatting.None));
            Assert.That(page["showing"]?.Value<int>(), Is.EqualTo(0));
            Assert.That(page["objects"], Has.Count.Zero);
        }

        [Test]
        public void VerboseTrueWithoutPagingKeys_StillReturnsEverything()
        {
            SkillRouter.SummaryAutoTruncate = true;
            SkillRouter.SummaryPageSize = 2;

            var result = FindProbes(new JObject { ["verbose"] = true });

            Assert.That(result["objects"], Has.Count.EqualTo(ProbeCount));
            Assert.That(result["isTruncated"], Is.Null);
        }

        [Test]
        public void AutomaticTruncation_KeepsItsHint()
        {
            SkillRouter.SummaryAutoTruncate = true;
            SkillRouter.SummaryPageSize = 2;

            var page = FindProbes(new JObject { ["verbose"] = false });

            Assert.That(page["isTruncated"]?.Value<bool>(), Is.True, page.ToString(Formatting.None));
            Assert.That(page["showing"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(page["hint"]?.ToString(), Does.Contain("pageOffset=2' (or 'verbose=true' for all items)"));
        }

        [Test]
        public void SkillDeclaredLimit_StaysTheSkillsOwnParameter()
        {
            var response = JObject.Parse(SkillRouter.Execute("asset_find", "{\"searchFilter\":\"\",\"limit\":3}"));

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));
            Assert.That(response["result"]?["assets"], Has.Count.EqualTo(3));
            Assert.That(response["result"]?["isTruncated"], Is.Null);
        }

        private static JToken FindProbes(JObject extraArgs)
        {
            var args = new JObject { ["nameContains"] = Probe };
            args.Merge(extraArgs);
            var response = JObject.Parse(SkillRouter.Execute("find_objects_by_name", args.ToString(Formatting.None)));
            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));
            return response["result"];
        }

        private static string[] Names(JToken objects) =>
            objects?.Select(item => item["name"]?.ToString()).ToArray();
    }
}

// Producer:Betsy
